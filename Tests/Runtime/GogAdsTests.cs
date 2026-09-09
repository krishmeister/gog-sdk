using System;
using System.Collections;
using System.Text.RegularExpressions;
using Gog.Sdk;
using Gog.Sdk.Internal;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;

namespace Gog.Sdk.Tests
{
    /// <summary>
    /// The GogAds contract (docs/sdk-api-contract.md §2, §5, §6, I7, I8, I9) proven against a
    /// hermetic loopback server that serves both the directive and the creative it points at.
    ///
    /// <para>Tests drive <c>GogAds.ShowInternal</c> — the platform-independent seam — because the
    /// public entry point requires <c>RuntimePlatform.Android</c> and the Editor reports
    /// <c>WindowsEditor</c>. The two gates the seam skips are proven separately against the public
    /// entry (<see cref="PublicEntry_BeforeInitialize_ReturnsNotInitialized"/> and
    /// <see cref="PublicEntry_OffAndroid_ReturnsUnsupportedPlatform"/>).</para>
    /// </summary>
    public class GogAdsTests
    {
        private const string GameId = "5ce9b0d3-be97-4a25-bdb2-195d58f4a91f";

        private LoopbackServer _server;

        // Written on the test thread immediately after the server is constructed, read on the
        // listener thread when a request arrives. Volatile so the responder can never observe null.
        private volatile string _creativeUrl;

        [SetUp]
        public void SetUp()
        {
            GogSdk.Shutdown();                    // drop any live ad surface + pre-init start state
            GogAdImpressionIndex.ClearForTests(); // deterministic index per test
        }

        [TearDown]
        public void TearDown()
        {
            GogSdk.Shutdown();
            GogAdImpressionIndex.ClearForTests();
            _server?.Dispose();
            _server = null;
        }

        // ── 1. {"type":"none"} → NoAd, a SUCCESS (I7) ─────────────────────────────
        [UnityTest]
        public IEnumerator None_ResolvesNoAd_AsSuccess()
        {
            _server = LoopbackServer.Constant(200, "application/json", "{\"type\":\"none\"}");
            var run = Show();
            yield return run.Wait();

            Assert.IsTrue(run.Result.Ok, "\"no ad\" is a SUCCESS, never an error (I7)");
            Assert.AreEqual(GogAdOutcome.NoAd, run.Result.Value.Outcome);
            Assert.AreEqual(1, run.Calls, "callback must fire exactly once");
        }

        // ── 2. A house directive renders and resolves Shown ───────────────────────
        [UnityTest]
        public IEnumerator House_RendersCreative_AndResolvesShown()
        {
            _server = HouseServer(forcedViewSeconds: 1);
            var run = Show();

            yield return WaitUntil(() => GogAdOverlay.ActiveForTests != null, 10f,
                "the ad overlay was never created");

            GogAdOverlay overlay = GogAdOverlay.ActiveForTests;
            yield return WaitUntil(() => overlay.CloseControlReadyForTests, 10f,
                "the close control never became available — the forced-view timer did not elapse");

            Assert.IsTrue(overlay.CloseControlInteractableForTests,
                "the close control must be interactable once the forced-view window elapses");
            overlay.InvokeCloseForTests();

            yield return run.Wait();
            Assert.IsTrue(run.Result.Ok);
            Assert.AreEqual(GogAdOutcome.Shown, run.Result.Value.Outcome);
            Assert.AreEqual(1, run.Calls);
        }

        // ── 3. admob → NoAd + the loud warning (I7; NOT AdNotConfigured) ──────────
        [UnityTest]
        public IEnumerator Admob_ResolvesNoAd_WithLoudWarning()
        {
            LogAssert.Expect(LogType.Warning, new Regex("Google Mobile Ads is not integrated"));
            _server = LoopbackServer.Constant(200, "application/json",
                "{\"type\":\"admob\",\"impression_id\":\"i1\",\"ad_unit_id\":\"ca-app-pub-1/2\"," +
                "\"expires_at\":\"2026-01-01T00:00:00.000Z\",\"expires_in\":300}");

            var run = Show();
            yield return run.Wait();

            Assert.IsTrue(run.Result.Ok, "an unexecutable directive is NoAd, never an error (I7)");
            Assert.AreEqual(GogAdOutcome.NoAd, run.Result.Value.Outcome);
        }

        // ── 4. An unknown discriminator → NoAd, the module-safe default (I9) ──────
        [UnityTest]
        public IEnumerator UnknownType_ResolvesNoAd_ForwardTolerant()
        {
            _server = LoopbackServer.Constant(200, "application/json",
                "{\"type\":\"rewarded\",\"impression_id\":\"i1\",\"expires_in\":300}");

            var run = Show();
            yield return run.Wait();

            Assert.IsTrue(run.Result.Ok, "an unknown type must not become an error (I9)");
            Assert.AreEqual(GogAdOutcome.NoAd, run.Result.Value.Outcome);
        }

        // ── 5. An already-expired directive → NoAd, and nothing is rendered ───────
        [UnityTest]
        public IEnumerator ExpiredDirective_ResolvesNoAd_WithoutRendering()
        {
            _server = HouseServer(forcedViewSeconds: 1, expiresIn: 0);
            var run = Show();
            yield return run.Wait();

            Assert.IsTrue(run.Result.Ok, "an expired directive is NoAd, never an error (§6)");
            Assert.AreEqual(GogAdOutcome.NoAd, run.Result.Value.Outcome);
            Assert.IsNull(GogAdOverlay.ActiveForTests, "an expired directive must not render");
        }

        // ── 6. Malformed 200 → NetworkError (the silent-hang regression) ──────────
        [UnityTest]
        public IEnumerator MalformedTwoHundred_IsNetworkError()
        {
            _server = LoopbackServer.Constant(200, "application/json", "<html>not json</html>");
            var run = Show();
            yield return run.Wait();

            Assert.IsFalse(run.Result.Ok);
            Assert.AreEqual(GogErrorCode.NetworkError, run.Result.Error.Code);
            Assert.AreEqual(1, run.Calls, "exactly once, even on the failure path");
        }

        // ── 7. house with no "creative" → NetworkError NAMING the field ───────────
        [UnityTest]
        public IEnumerator HouseWithoutCreative_FailsNamingTheField()
        {
            _server = LoopbackServer.Constant(200, "application/json",
                "{\"type\":\"house\",\"impression_id\":\"i1\",\"expires_in\":300}");

            var run = Show();
            yield return run.Wait();

            Assert.IsFalse(run.Result.Ok, "a wrong-shape 200 must not be a default-filled success");
            Assert.AreEqual(GogErrorCode.NetworkError, run.Result.Error.Code);
            StringAssert.Contains("creative", run.Result.Error.Message);
        }

        // ── 8. A creative missing image_url → NetworkError naming it (Newtonsoft) ─
        [UnityTest]
        public IEnumerator CreativeMissingImageUrl_FailsNamingTheField()
        {
            _server = LoopbackServer.Constant(200, "application/json",
                "{\"type\":\"house\",\"impression_id\":\"i1\",\"creative\":{\"id\":\"c1\"},\"expires_in\":300}");

            var run = Show();
            yield return run.Wait();

            Assert.IsFalse(run.Result.Ok);
            Assert.AreEqual(GogErrorCode.NetworkError, run.Result.Error.Code);
            StringAssert.Contains("image_url", run.Result.Error.Message);
        }

        // ── 9. A creative that will not load → NoAd, never a hang, never an error ─
        [UnityTest]
        public IEnumerator CreativeFailsToLoad_ResolvesNoAd()
        {
            LogAssert.Expect(LogType.Warning, new Regex("could not be loaded"));
            int deadPort = LoopbackServer.ReserveClosedPort();
            _server = new LoopbackServer(_ => new LoopbackServer.Response(200, "application/json",
                Directive($"http://127.0.0.1:{deadPort}/gone.png", 1, 300)));

            var run = Show();
            yield return run.Wait();

            Assert.IsTrue(run.Result.Ok, "a broken creative is NoAd (I7), not an error");
            Assert.AreEqual(GogAdOutcome.NoAd, run.Result.Value.Outcome);
        }

        // ── 10. The impression index is monotonic across calls ────────────────────
        [UnityTest]
        public IEnumerator ImpressionIndex_IsMonotonicAcrossCalls()
        {
            _server = LoopbackServer.Constant(200, "application/json", "{\"type\":\"none\"}");

            for (int i = 0; i < 3; i++)
            {
                var run = Show();
                yield return run.Wait();
            }

            Assert.AreEqual(3, _server.RequestCount, "three opportunities, three requests");
            Assert.AreEqual(0, IndexOf(_server.Requests[0]), "the first opportunity is index 0");
            Assert.AreEqual(1, IndexOf(_server.Requests[1]));
            Assert.AreEqual(2, IndexOf(_server.Requests[2]));
        }

        // ── 11. It survives a "restart": persisted, and restarts at 0 only if cleared ──
        [UnityTest]
        public IEnumerator ImpressionIndex_PersistsAcrossRestart_AndOnlyRestartsWhenCleared()
        {
            _server = LoopbackServer.Constant(200, "application/json", "{\"type\":\"none\"}");

            GogAdImpressionIndex.SetForTests(41); // as if 41 impressions had already happened
            var run = Show();
            yield return run.Wait();
            Assert.AreEqual(41, IndexOf(_server.Requests[0]), "a persisted index must be resumed, not reset");
            Assert.AreEqual(42, GogAdImpressionIndex.PeekForTests(), "and advanced for next time");

            // The uninstall / clear-data case (also the accepted PlayerPrefs.DeleteAll hole).
            GogAdImpressionIndex.ClearForTests();
            var run2 = Show();
            yield return run2.Wait();
            Assert.AreEqual(0, IndexOf(_server.Requests[1]), "a cleared store restarts at 0");
        }

        // ── 12. A transport retry must NOT consume a second opportunity ───────────
        [UnityTest]
        public IEnumerator TransientRetry_ReusesTheSameIndex()
        {
            int hits = 0;
            _server = new LoopbackServer(_ =>
            {
                hits++;
                return hits == 1
                    ? new LoopbackServer.Response(503, "application/json", "{\"error\":\"internal error\"}")
                    : new LoopbackServer.Response(200, "application/json", "{\"type\":\"none\"}");
            });

            var run = Show();
            yield return run.Wait();

            Assert.GreaterOrEqual(_server.RequestCount, 2, "the 503 should have been retried once");
            Assert.AreEqual(IndexOf(_server.Requests[0]), IndexOf(_server.Requests[1]),
                "a RETRY must re-send the same index — a retry is not a new opportunity");
            Assert.AreEqual(1, GogAdImpressionIndex.PeekForTests(),
                "one opportunity consumed, not two");
        }

        // ── 13. A concurrent second call is NoAd, and never reaches the server ────
        [UnityTest]
        public IEnumerator ConcurrentCall_IsNoAd_AndDoesNotConsumeAnOpportunity()
        {
            LogAssert.Expect(LogType.Warning, new Regex("already in flight"));
            _server = HouseServer(forcedViewSeconds: 1);

            var first = Show();
            yield return WaitUntil(() => GogAdOverlay.ActiveForTests != null, 10f,
                "the first ad never rendered");

            var second = Show();                       // while the first is on screen
            yield return second.Wait();

            Assert.IsTrue(second.Result.Ok, "a double call is NoAd, not an error (I7)");
            Assert.AreEqual(GogAdOutcome.NoAd, second.Result.Value.Outcome);
            Assert.AreEqual(1, GogAdImpressionIndex.PeekForTests(),
                "the rejected call must NOT consume an impression opportunity");

            // Close only once the forced-view window has elapsed: before that the control is
            // deliberately inert, which is the whole point of a forced view.
            GogAdOverlay overlay = GogAdOverlay.ActiveForTests;
            yield return WaitUntil(() => overlay.CloseControlReadyForTests, 10f,
                "the close control never became available");
            overlay.InvokeCloseForTests();
            yield return first.Wait();
        }

        // ── 14. Shutdown mid-ad still resolves exactly once ───────────────────────
        [UnityTest]
        public IEnumerator ShutdownDuringAd_ResolvesExactlyOnce()
        {
            _server = HouseServer(forcedViewSeconds: 5);
            var run = Show();
            yield return WaitUntil(() => GogAdOverlay.ActiveForTests != null, 10f,
                "the ad never rendered");

            GogSdk.Shutdown();                          // tears the surface down mid-ad
            yield return run.Wait();

            Assert.IsTrue(run.Result.Ok);
            Assert.AreEqual(GogAdOutcome.NoAd, run.Result.Value.Outcome);
            Assert.AreEqual(1, run.Calls, "exactly once (I2), even when torn down mid-flight");
            Assert.IsNull(GogAdOverlay.ActiveForTests, "the surface must be gone after Shutdown()");
        }

        // ── 15a. The public entry: pre-init → NotInitialized on any platform ──────
        [UnityTest]
        public IEnumerator PublicEntry_BeforeInitialize_ReturnsNotInitialized()
        {
            Assert.IsFalse(GogSdk.IsInitialized, "precondition: not initialized");
            GogResult<GogAdResult> result = default;
            bool done = false;

            GogAds.ShowInterstitial(r => { result = r; done = true; });

            yield return WaitUntil(() => done, 2f, "callback never fired");
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(GogErrorCode.NotInitialized, result.Error.Code);
        }

        // ── 15b. The public entry: off Android → UnsupportedPlatform ──────────────
        [UnityTest]
        public IEnumerator PublicEntry_OffAndroid_ReturnsUnsupportedPlatform()
        {
            GogSdk.Initialize();
            Assert.AreNotEqual(RuntimePlatform.Android, Application.platform,
                "precondition: these tests run off-device");

            GogResult<GogAdResult> result = default;
            bool done = false;
            GogAds.ShowInterstitial(r => { result = r; done = true; });

            yield return WaitUntil(() => done, 2f, "callback never fired");
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(GogErrorCode.UnsupportedPlatform, result.Error.Code);
        }

        // ── 15c. A null callback is a programmer error and throws AT CALL TIME ────
        [Test]
        public void NullCallback_ThrowsImmediately()
        {
            Assert.Throws<ArgumentNullException>(() => GogAds.ShowInterstitial(null));
        }

        // ── RULING 6 — THE UNDISMISSABLE-AD CASE ──────────────────────────────────
        // A game with no EventSystem must still get a working close control. If this regresses,
        // the ad is permanently undismissable and the player must force-quit the game.
        //
        // Batchmode cannot synthesise a real touch, so this proves the two things that make the
        // pointer path work — an EventSystem exists to dispatch it, and the button is interactable
        // and wired — plus the failsafe that backstops both. Real pointer delivery is a device check.
        [UnityTest]
        public IEnumerator NoEventSystemInScene_CloseControlStillWorks()
        {
            foreach (EventSystem existing in UnityEngine.Object.FindObjectsByType<EventSystem>(
                         FindObjectsInactive.Include))
            {
                UnityEngine.Object.DestroyImmediate(existing.gameObject);
            }
            yield return null;
            Assert.IsNull(EventSystem.current, "precondition: no EventSystem in the scene");

            _server = HouseServer(forcedViewSeconds: 1);
            var run = Show();

            yield return WaitUntil(() => GogAdOverlay.ActiveForTests != null, 10f,
                "the ad overlay was never created");
            Assert.IsNotNull(EventSystem.current,
                "the SDK MUST create an EventSystem when the game has none — without one no " +
                "button fires and the ad is permanently undismissable");

            GogAdOverlay overlay = GogAdOverlay.ActiveForTests;
            yield return WaitUntil(() => overlay.CloseControlReadyForTests, 10f,
                "the close control never became available");
            Assert.IsTrue(overlay.CloseControlInteractableForTests,
                "the close control must be interactable, not just present");

            overlay.InvokeCloseForTests();
            yield return run.Wait();

            Assert.IsTrue(run.Result.Ok);
            Assert.AreEqual(GogAdOutcome.Shown, run.Result.Value.Outcome);
            Assert.IsNull(GogAdOverlay.ActiveForTests, "the overlay must be gone after dismissal");

            yield return null; // Destroy() is deferred to end of frame
            yield return null;
            Assert.IsNull(EventSystem.current,
                "the EventSystem the SDK created must be destroyed with the ad");
        }

        // The failsafe VALUE is a device-timescale concern (90s), but the invariant it encodes —
        // "a stuck ad is never permanent" — is asserted here at the level a unit test can reach.
        [Test]
        public void Failsafe_IsBoundedAndClearsTheLongestForcedView()
        {
            Assert.Greater(GogAdOverlay.FailsafeMaxLifetimeSeconds,
                GogAdDirective.MaxForcedViewSeconds,
                "the failsafe must fire AFTER the longest legal forced view, so it can only ever " +
                "catch a genuinely stuck ad — never cut a legitimate one short");
            Assert.LessOrEqual(GogAdOverlay.FailsafeMaxLifetimeSeconds, 120f,
                "a stuck ad must resolve well before a player concludes the game has frozen");
        }

        // ── pure-seam coverage (RULING 7): no platform, no network, no Unity loop ─
        [Test]
        public void BuildUrl_ShapesTheRequest_AndOmitsUserIdWhenAbsent()
        {
            string anon = GogAdDirective.BuildUrl("https://api.example.com", GameId, null, 7);
            Assert.AreEqual($"https://api.example.com/ads/next?game_id={GameId}&index=7", anon);

            string signedIn = GogAdDirective.BuildUrl("https://api.example.com/", GameId, "u-1", 0);
            StringAssert.Contains("&user_id=u-1", signedIn);
            StringAssert.DoesNotContain("//ads/next", signedIn, "a trailing slash must not double up");
        }

        [Test]
        public void ForcedView_DefaultsToFourSeconds_AndClampsAServerValue()
        {
            Assert.AreEqual(4f, GogAdDirective.DefaultImageForcedViewSeconds);

            Assert.AreEqual(4f, Interpret(null).ForcedViewSeconds, "absent → the compiled default");
            Assert.AreEqual(12f, Interpret(12).ForcedViewSeconds, "present → the server wins");
            Assert.AreEqual(GogAdDirective.MaxForcedViewSeconds, Interpret(6000).ForcedViewSeconds,
                "an absurd server value must be clamped, never trap the player");
            Assert.AreEqual(GogAdDirective.MinForcedViewSeconds, Interpret(0).ForcedViewSeconds);
        }

        [Test]
        public void VideoCreative_CarriesItsSourceAndKeepsThePosterAsImageUrl()
        {
            GogAdDirective d = InterpretMedia("video", "https://example.com/a.mp4", 15);

            Assert.AreEqual(GogAdDirectiveKind.House, d.Kind, "video is a house directive");
            Assert.AreEqual("https://example.com/a.mp4", d.VideoUrl);
            Assert.AreEqual(15, d.DurationSeconds);
            Assert.AreEqual("https://example.com/a.png", d.ImageUrl,
                "image_url stays the POSTER — it is what renders while the video prepares and " +
                "what remains if playback fails");
        }

        [Test]
        public void CreativeWithoutMediaType_IsAnImage()
        {
            // The field is additive: a server that predates video never sends it, and those
            // responses must keep behaving exactly as they did.
            GogAdDirective d = InterpretMedia(null, null, null);

            Assert.AreEqual(GogAdDirectiveKind.House, d.Kind);
            Assert.IsNull(d.VideoUrl, "no media_type means a plain image creative");
            Assert.AreEqual(0, d.DurationSeconds);
        }

        [Test]
        public void VideoCreativeMissingItsSource_DegradesToTheImage_NotToAFailure()
        {
            // A data fault on the server must not cost the player the ad: the poster is a
            // complete, serveable creative on its own (I7).
            GogAdDirective d = InterpretMedia("video", null, null);

            Assert.AreEqual(GogAdDirectiveKind.House, d.Kind,
                "a missing video source is NOT an Invalid directive");
            Assert.IsNull(d.VideoUrl);
            Assert.AreEqual("https://example.com/a.png", d.ImageUrl);
        }

        private static GogAdDirective InterpretMedia(string mediaType, string videoUrl, int? duration)
        {
            var dto = new AdsNextDto
            {
                Type = "house",
                ExpiresIn = 300,
                Creative = new AdsNextCreativeDto
                {
                    Id = "c1",
                    ImageUrl = "https://example.com/a.png",
                    MediaType = mediaType,
                    VideoUrl = videoUrl,
                    DurationSeconds = duration,
                },
            };
            return GogAdDirective.Interpret(dto);
        }

        private static GogAdDirective Interpret(int? forcedView)
        {
            var dto = new AdsNextDto
            {
                Type = "house",
                ExpiresIn = 300,
                Creative = new AdsNextCreativeDto
                {
                    Id = "c1",
                    ImageUrl = "https://example.com/a.png",
                    ForcedViewSeconds = forcedView,
                },
            };
            return GogAdDirective.Interpret(dto);
        }

        // ── helpers ───────────────────────────────────────────────────────────────

        /// <summary>A house directive pointing at this same loopback server for the creative.</summary>
        private LoopbackServer HouseServer(int forcedViewSeconds, int expiresIn = 300)
        {
            byte[] png = OnePixelPng();
            var server = new LoopbackServer(target =>
                target.StartsWith("/ads/next", StringComparison.Ordinal)
                    ? new LoopbackServer.Response(200, "application/json",
                        Directive(_creativeUrl, forcedViewSeconds, expiresIn))
                    : new LoopbackServer.Response(200, "image/png", png));
            _creativeUrl = server.Url("/creative.png");
            return server;
        }

        private static string Directive(string imageUrl, int forcedViewSeconds, int expiresIn) =>
            "{\"type\":\"house\",\"impression_id\":\"i1\",\"creative\":{\"id\":\"c1\"," +
            "\"image_url\":\"" + imageUrl + "\",\"click_url\":null," +
            "\"forced_view_seconds\":" + forcedViewSeconds + "}," +
            "\"expires_at\":\"2026-01-01T00:00:00.000Z\",\"expires_in\":" + expiresIn + "}";

        private static byte[] OnePixelPng()
        {
            var tex = new Texture2D(2, 2);
            tex.SetPixels(new[] { Color.red, Color.red, Color.red, Color.red });
            tex.Apply();
            byte[] png = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);
            return png;
        }

        /// <summary>Extract the <c>index</c> query value from a recorded request target.</summary>
        private static int IndexOf(string requestTarget)
        {
            Match m = Regex.Match(requestTarget ?? string.Empty, @"[?&]index=(\d+)");
            Assert.IsTrue(m.Success, $"no index in request target '{requestTarget}'");
            return int.Parse(m.Groups[1].Value);
        }

        private Run Show()
        {
            GogSdk.Initialize();
            var run = new Run();
            GogAds.ShowInternal(_server.Url(), GameId, null, r =>
            {
                run.Calls++;
                run.Result = r;
                run.Done = true;
            });
            return run;
        }

        private sealed class Run
        {
            internal GogResult<GogAdResult> Result;
            internal bool Done;
            internal int Calls;

            internal IEnumerator Wait() => WaitUntil(() => Done, 20f, "the ad callback never fired");
        }

        private static IEnumerator WaitUntil(Func<bool> condition, float seconds, string failMessage)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            while (!condition() && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(condition(), failMessage);
        }
    }
}
