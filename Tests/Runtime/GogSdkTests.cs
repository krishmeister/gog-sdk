using System;
using System.Collections;
using System.Text.RegularExpressions;
using Gog.Sdk;
using Gog.Sdk.Internal;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Gog.Sdk.Tests
{
    /// <summary>
    /// The single init model (docs/sdk-api-contract.md §1.3). Proves the boot contract:
    /// pre-init calls are typed NotInitialized (not throws, not hangs); a missing/invalid
    /// GogSettings is a loud throw; a second Initialize() is a no-op; and Shutdown()+
    /// Initialize() leaves no stale event subscriptions (the domain-reload leak, in test form).
    ///
    /// <para>Runs inside gog-sdk-test, whose GogSettings asset holds a valid Game ID — so the
    /// real <see cref="GogSdk.Initialize"/> succeeds; the missing/blank cases use the internal
    /// seam with an injected asset.</para>
    /// </summary>
    public class GogSdkTests
    {
        [SetUp]
        public void SetUp() => GogSdk.Shutdown();   // guarantee a pre-init starting state

        [TearDown]
        public void TearDown() => GogSdk.Shutdown(); // don't leak init/subscriptions to the next test

        // (a) A GogCoins call before Initialize() → typed NotInitialized. Not a throw, not a hang.
        [UnityTest]
        public IEnumerator GogCoins_BeforeInitialize_ReturnsNotInitialized()
        {
            Assert.IsFalse(GogSdk.IsInitialized, "precondition: not initialized");
            GogResult<GogCoinBalance> result = default;
            bool done = false;

            GogCoins.GetBalance(r => { result = r; done = true; });

            yield return WaitFor(() => done, 2f, "callback never fired");
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(GogErrorCode.NotInitialized, result.Error.Code);
        }

        // (b) A missing or blank-GameId GogSettings is a programmer error → THROWS with an
        //     actionable message, and leaves the SDK uninitialized.
        [Test]
        public void Initialize_MissingOrBlankSettings_Throws()
        {
            Assert.IsFalse(GogSdk.IsInitialized);

            var missing = Assert.Throws<InvalidOperationException>(
                () => GogSdk.InitializeInternal(null), "a missing settings asset must throw");
            StringAssert.Contains("Project Settings", missing.Message, "message must tell the dev where to fix it");

            var blank = ScriptableObject.CreateInstance<GogSettings>(); // gameId defaults blank
            Assert.Throws<InvalidOperationException>(
                () => GogSdk.InitializeInternal(blank), "a blank/invalid Game ID must throw");
            UnityEngine.Object.DestroyImmediate(blank);

            Assert.IsFalse(GogSdk.IsInitialized, "a thrown Initialize must not leave the SDK initialized");
        }

        // (c) A second Initialize() is a logged no-op; state is unchanged. This is the root
        //     kill of the double-init divergence — reconfiguration does not exist.
        [UnityTest]
        public IEnumerator Initialize_Twice_SecondIsLoggedNoOp()
        {
            GogSdk.Initialize();
            Assert.IsTrue(GogSdk.IsInitialized, "first Initialize should succeed from the asset");

            LogAssert.Expect(LogType.Log, new Regex("called again"));
            GogSdk.Initialize(); // second — logged no-op, no throw
            Assert.IsTrue(GogSdk.IsInitialized, "still initialized after the no-op");
            yield return null;
        }

        // (d) Shutdown() then Initialize() must drop stale event subscriptions — the
        //     Enter-Play-Mode-without-Domain-Reload leak, expressed as a test.
        [UnityTest]
        public IEnumerator ShutdownThenInitialize_DropsStaleSubscriptions()
        {
            int oldHandlerCalls = 0;
            GogAuth.OnSignedOut += () => oldHandlerCalls++;

            GogSdk.Initialize();
            GogAuth.SignOut();          // raises OnSignedOut (local clear; no I/O in the Editor)
            yield return null;
            Assert.AreEqual(1, oldHandlerCalls, "handler should fire while subscribed");

            GogSdk.Shutdown();          // ResetEvents drops the subscription
            GogSdk.Initialize();
            GogAuth.SignOut();          // raise again — the stale handler must be gone
            yield return null;
            Assert.AreEqual(1, oldHandlerCalls, "stale handler must NOT fire after Shutdown()+Initialize()");
        }

        // The binding↔core version handshake logic (contract §1.5). The throw-at-boot
        // behaviour is proven on-device; here we lock the decision and the remediation message.
        [Test]
        public void VersionHandshake_MatchIsNull_MismatchNamesBothVersionsAndRemediation()
        {
            Assert.IsNull(AndroidAuthBridge.VersionHandshakeError("0.1.0", "0.1.0"),
                "equal versions must pass (null = no error)");

            string msg = AndroidAuthBridge.VersionHandshakeError("0.1.0", "9.9.9-stale");
            Assert.IsNotNull(msg, "a mismatch must produce an error message");
            StringAssert.Contains("0.1.0", msg);
            StringAssert.Contains("9.9.9-stale", msg);
            StringAssert.Contains("Reinstall the package", msg);
            StringAssert.Contains("never hand-copy", msg);
        }

        // COMPLETENESS PROOF (contract §5). Every reason string the native core (A1) and the hub
        // (A2) can emit maps EXPLICITLY — correct GogErrorCode, correct Retryable, RawReason
        // preserved, and NEVER the InternalError fallback. MapReason matches string literals
        // against Kotlin GogAuthErrorCode.name (PascalCase) and hub ERROR_* constants
        // (snake_case) with no compiler check; if a rename ever makes a KNOWN reason fall
        // through, this test goes red. Anyone adding a reason adds a row here.
        [Test]
        public void NoKnownReasonFallsThroughToInternalError()
        {
            var cases = new (string reason, bool exchange, GogErrorCode code, bool retryable)[]
            {
                // PascalCase — native core GogAuthErrorCode.name (via onError)
                ("InvalidOrExpiredCode", true,  GogErrorCode.InvalidOrExpiredCode,  false),
                ("InvalidRequest",       true,  GogErrorCode.InvalidRequest,        false), // the HTTP-400 hole, closed
                ("NetworkError",         true,  GogErrorCode.NetworkError,          false), // exchange: code likely burned
                ("NetworkError",         false, GogErrorCode.NetworkError,          true),  // token refresh: retryable
                // snake_case — core onFailed + hub/RN EXTRA_ERROR
                ("not_initialized",      true,  GogErrorCode.NotInitialized,        false),
                ("in_progress",          true,  GogErrorCode.SignInInProgress,      true),
                ("hub_untrusted",        true,  GogErrorCode.HubVerificationFailed, false),
                ("caller_mismatch",      true,  GogErrorCode.HubVerificationFailed, false),
                ("hub_unavailable",      true,  GogErrorCode.HubNotInstalled,       false),
                ("unsupported_os",       true,  GogErrorCode.UnsupportedOsVersion,  false), // SDK HubIdentity, API<28
                ("unsupported",          true,  GogErrorCode.UnsupportedOsVersion,  false), // hub broker, API<28
                ("unknown_game",         true,  GogErrorCode.UnknownGame,           false),
                ("invalid_request",      true,  GogErrorCode.InvalidRequest,        false),
                ("user_cancelled",       true,  GogErrorCode.SignInCancelled,       true),
                ("network",              true,  GogErrorCode.NetworkError,          true),  // RN refusal, no code minted
                ("editor_unsupported",   true,  GogErrorCode.UnsupportedPlatform,   false),
            };

            foreach (var c in cases)
            {
                GogError e = AndroidAuthBridge.MapReason(c.reason, null, c.exchange);
                Assert.AreNotEqual(GogErrorCode.InternalError, e.Code,
                    $"KNOWN reason '{c.reason}' fell through to InternalError — the hole is back");
                Assert.AreEqual(c.code, e.Code, $"reason '{c.reason}' code");
                Assert.AreEqual(c.retryable, e.Retryable, $"reason '{c.reason}' (exchange={c.exchange}) retryable");
                Assert.AreEqual(c.reason, e.RawReason, $"reason '{c.reason}' raw preserved");
                Assert.IsNotNull(e.Message, $"reason '{c.reason}' message");
            }
        }

        // Headless completions (a reverse handoff that outlived its process, I6) reach the
        // game through the unsolicited-transition events — there is no per-call callback to
        // receive them. These raisers are the last C# hop of that path; keep them honest.
        [UnityTest]
        public IEnumerator HeadlessRaisers_DeliverThroughTheEvents()
        {
            GogSdk.Initialize();

            GogUser gotUser = null;
            GogError gotError = default;
            bool sawError = false;
            GogAuth.OnSignedIn += u => gotUser = u;
            GogAuth.OnLaunchSignInFailed += e => { gotError = e; sawError = true; };

            GogAuth.RaiseSignedIn(new GogUser("u1", "u1@example.com"));
            GogAuth.RaiseLaunchSignInFailed(new GogError(
                GogErrorCode.InvalidOrExpiredCode, "rejected (401)", false, "InvalidOrExpiredCode"));
            yield return null;

            Assert.IsNotNull(gotUser, "OnSignedIn must deliver the headless completion");
            Assert.AreEqual("u1", gotUser.Id);
            Assert.IsTrue(sawError, "OnLaunchSignInFailed must deliver the headless failure");
            Assert.AreEqual(GogErrorCode.InvalidOrExpiredCode, gotError.Code);
            Assert.IsFalse(gotError.Retryable);
        }

        // The fallback still works for a genuinely NEW reason: InternalError, raw preserved, and
        // NOT retryable — we do not promise retryability for something we don't recognise.
        [Test]
        public void UnknownReason_IsInternalError_RawPreserved_NotRetryable()
        {
            GogError e = AndroidAuthBridge.MapReason("some_future_reason_x9", null, exchange: true);
            Assert.AreEqual(GogErrorCode.InternalError, e.Code);
            Assert.AreEqual("some_future_reason_x9", e.RawReason);
            Assert.IsFalse(e.Retryable, "an unrecognised reason must not claim to be retryable");
        }

        private static IEnumerator WaitFor(Func<bool> condition, float seconds, string failMessage)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            while (!condition() && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(condition(), failMessage);
        }
    }
}
