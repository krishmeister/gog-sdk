using System;
using Gog.Sdk.Internal;
using UnityEngine;

namespace Gog.Sdk
{
    /// <summary>What happened on an interstitial opportunity. A class rather than a bare enum so
    /// v1.1 can add data (network, revenue) additively — changing the generic parameter of the
    /// callback would be a re-typing, forbidden until 2.0 (contract §2, §8).</summary>
    public sealed class GogAdResult
    {
        /// <summary>The outcome. Consumers should default-branch unknown values (§1.2).</summary>
        public GogAdOutcome Outcome { get; }

        internal GogAdResult(GogAdOutcome outcome) { Outcome = outcome; }
    }

    /// <summary>The outcome of an interstitial opportunity.</summary>
    public enum GogAdOutcome
    {
        /// <summary>An ad was displayed to the player.</summary>
        Shown,

        /// <summary>No ad was displayed. A NORMAL, frequent, successful outcome (I7) — the server
        /// declining to serve, an expired directive, or a creative that could not be shown. Game
        /// flow must never depend on an ad rendering.</summary>
        NoAd,
    }

    /// <summary>
    /// Ads surface — one call per interstitial opportunity.
    ///
    /// <para>The SDK reimplements NO decision logic: which creative, whether AdMob or house,
    /// rotation, cadence and city targeting are all resolved server-side and controlled by the
    /// operator from the GOG dashboard. By the time a response arrives the decision is made; the
    /// client's whole job is to fetch the directive and render it.</para>
    ///
    /// <para><b>I7 — ads never block the game.</b> <see cref="GogAdOutcome.NoAd"/> is a success,
    /// not an error, and it is common. The recommended integration is to treat
    /// <c>!result.Ok</c> exactly like <c>NoAd</c> and carry on:</para>
    /// <code>
    /// GogAds.ShowInterstitial(result => ContinueToNextLevel());
    /// </code>
    ///
    /// <para><b>AdMob</b> interstitials are shown by the bundled native core with the Google
    /// Mobile Ads Android SDK — no Unity plugin. The game needs two things: the External
    /// Dependency Manager for Unity (so <c>GogSdkDependencies.xml</c> pulls in
    /// play-services-ads) and <c>GogSettings.AdmobAppId</c>. Without them an AdMob directive
    /// resolves to <see cref="GogAdOutcome.NoAd"/> with a warning naming the fix.</para>
    ///
    /// <para><b>Video needs nothing from you.</b> Playback uses Unity's built-in
    /// UnityEngine.Video module — no package to import, no dependency to resolve, no settings to
    /// fill in. A video creative also carries a poster frame, and the poster is what renders if
    /// the video cannot be played on a given device, so <see cref="GogAdOutcome.Shown"/> means
    /// the same thing it always did.</para>
    /// </summary>
    public static class GogAds
    {
        private static AdSession _active;

        // The public entry points' platform gate. iOS qualifies because everything up to the
        // render is plain C# over UnityWebRequest; only the AdMob path is Android-native (JNI),
        // and that resolves NoAd off Android without touching Java (see ShowAdmob).
        private static bool IsSupportedPlatform =>
            Application.platform == RuntimePlatform.Android ||
            Application.platform == RuntimePlatform.IPhonePlayer;

        /// <summary>
        /// Fetch and render one interstitial. The callback fires exactly once, on the main
        /// thread, whatever happens.
        /// </summary>
        /// <param name="callback">Required. <c>null</c> throws <see cref="ArgumentNullException"/>
        /// at call time (§1.1) — there is no optional second delegate whose absence could
        /// silently swallow the result.</param>
        public static void ShowInterstitial(Action<GogResult<GogAdResult>> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));

            // NotInitialized is checked FIRST so a pre-init call reports that on any platform,
            // including the Editor, rather than UnsupportedPlatform (§4/I4).
            if (!GogSdk.IsInitialized)
            {
                callback(GogResult<GogAdResult>.Fail(GogErrorCode.NotInitialized,
                    "GogSdk.Initialize() was not called.", retryable: false));
                return;
            }
            if (!IsSupportedPlatform)
            {
                callback(GogResult<GogAdResult>.Fail(GogErrorCode.UnsupportedPlatform,
                    "GOG Ads runs on Android and iOS only.", retryable: false));
                return;
            }

            GogSettings settings = GogSdk.Settings;
            ShowInternal(settings.ApiBaseUrl, settings.GameId, ResolveUserId(), callback);
        }

        /// <summary>
        /// The platform-independent entry: everything except the two gates above. Config arrives
        /// resolved, so this is drivable from a test on any platform against a loopback server —
        /// the public entry is the only part that requires a device (Android or iOS).
        ///
        /// <para>Deliberately does NOT require a signed-in session. <c>/ads/next</c> is a public,
        /// unauthenticated route and <c>user_id</c> is optional; gating on sign-in would deny
        /// house ads to signed-out players and contradict the server's design.</para>
        /// </summary>
        internal static void ShowInternal(string baseUrl, string gameId, string userId,
            Action<GogResult<GogAdResult>> callback, string trigger = null)
        {
            // Concurrency guard, BEFORE the index increment: a rejected double-call must not
            // consume an impression opportunity. NoAd rather than an error keeps game flow
            // identical (I7); the warning stops it being silent.
            if (_active != null || GogAdOverlay.IsShowing)
            {
                Debug.LogWarning("[GOG SDK] ShowInterstitial was called while an interstitial was " +
                                 "already in flight — resolving the second call as NoAd. Wait for " +
                                 "the first callback before requesting another ad.");
                callback(GogResult<GogAdResult>.Success(new GogAdResult(GogAdOutcome.NoAd)));
                return;
            }

            var session = new AdSession(callback);
            _active = session;

            int index = GogAdImpressionIndex.Next();
            string url = GogAdDirective.BuildUrl(baseUrl, gameId, userId, index, trigger);

            // No bearer: the route is public. Fetching a token would put a refresh round trip on
            // the ad path for nothing.
            GogHttp.Get<AdsNextDto>(url, null, r => OnDirective(session, r));
        }

        /// <summary>
        /// The automatic game-open opportunity, fired by <c>GogSdk.Initialize()</c> — no game
        /// code involved. Tagged <c>trigger=open</c> so the SERVER decides via the game's
        /// show_on_open policy: games whose operator has not opted in get <c>none</c>, exactly
        /// as if this call never happened. Fire-and-forget with a logging callback — there is
        /// no partner callback to strand, and I7 (ads never block the game) holds throughout.
        /// </summary>
        internal static void AutoShowOnOpen()
        {
            if (!GogSdk.IsInitialized || !IsSupportedPlatform) return;

            GogSettings settings = GogSdk.Settings;
            ShowInternal(settings.ApiBaseUrl, settings.GameId, ResolveUserId(), result =>
            {
                if (!result.Ok)
                {
                    Debug.Log("[GOG SDK] Open interstitial not shown: " + result.Error.Message);
                }
            }, trigger: "open");
        }

        /// <summary>Drop any live ad surface and resolve anything in flight. Called from
        /// <c>GogSdk.Shutdown()</c> and the domain-reload static reset, so exactly-once (I2)
        /// still holds when the SDK is torn down mid-ad.</summary>
        internal static void ResetState()
        {
            GogAdOverlay.Teardown();     // resolves a live surface as NoAd through its session
            AdSession pending = _active; // still in flight (e.g. the fetch has not returned)
            _active = null;
            if (pending != null) pending.Abandon();
        }

        // ── pipeline ──────────────────────────────────────────────────────────────

        private static void OnDirective(AdSession session, GogResult<AdsNextDto> response)
        {
            if (!response.Ok)
            {
                session.Fail(response.Error);
                return;
            }

            GogAdDirective directive = GogAdDirective.Interpret(response.Value);
            switch (directive.Kind)
            {
                case GogAdDirectiveKind.Invalid:
                    session.Fail(new GogError(GogErrorCode.NetworkError,
                        "Malformed response: " + directive.InvalidReason, retryable: true,
                        rawReason: "invalid_shape"));
                    return;

                case GogAdDirectiveKind.NoAd:
                    if (directive.UnknownType != null)
                    {
                        // Designed forward-evolution (I9), not a fault — Log, not LogWarning.
                        Debug.Log("[GOG SDK] The server sent an ad directive of an unknown type ('" +
                                  directive.UnknownType + "'). This build resolves it as NoAd. " +
                                  "Update the GOG SDK to serve it.");
                    }
                    session.Resolve(GogAdOutcome.NoAd);
                    return;

                case GogAdDirectiveKind.Admob:
                    ShowAdmob(session, directive);
                    return;

                default:
                    FetchCreative(session, directive);
                    return;
            }
        }

        /// <summary>
        /// Hand an AdMob directive to the native core, which loads and shows the interstitial
        /// with the Google Mobile Ads Android SDK. The load is capped (never past the directive's
        /// TTL) so a slow fill is a NoAd, not a wait.
        ///
        /// <para>A no-fill is routine and logs quietly. A build that cannot serve AdMob at all —
        /// GMA not resolved, no app id — warns loudly and names the fix, because it will miss
        /// every AdMob opportunity until rebuilt.</para>
        ///
        /// <para>Android only. <see cref="AndroidAdmobBridge"/> answers
        /// <c>unsupported_platform</c> on any other platform BEFORE it touches JNI, so on iOS an
        /// AdMob directive resolves as NoAd with a quiet log line and no Java is ever reached.</para>
        /// </summary>
        private static void ShowAdmob(AdSession session, GogAdDirective directive)
        {
            AndroidAdmobBridge.ShowInterstitial(directive.AdUnitId, directive.ExpiresInSeconds,
                (shown, reason) =>
                {
                    if (!shown)
                    {
                        string hint = AdmobFixHint(reason);
                        string message = "[GOG SDK] AdMob interstitial not shown (" + reason + ")" +
                                         (hint != null ? " — " + hint : string.Empty) +
                                         " — resolving as NoAd.";
                        if (hint != null) Debug.LogWarning(message);
                        else Debug.Log(message);
                    }
                    session.Resolve(shown ? GogAdOutcome.Shown : GogAdOutcome.NoAd);
                });
        }

        /// <summary>The fix for a build that can never show AdMob, or null for per-request
        /// outcomes (no fill, timeout, off-Android) that need no action.</summary>
        private static string AdmobFixHint(string reason)
        {
            switch (reason)
            {
                case "gma_missing":
                    return "Google Mobile Ads (play-services-ads) is not in this build. Install the " +
                           "External Dependency Manager for Unity so the SDK's GogSdkDependencies.xml " +
                           "resolves, then rebuild. To serve house ads only, change this game's " +
                           "delivery mode in the GOG dashboard.";
                case "app_id_missing":
                    return "GogSettings.AdmobAppId is empty. Set it in Project Settings → GOG SDK " +
                           "(the ca-app-pub-…~… app id from the same AdMob account as the ad unit) " +
                           "and rebuild.";
                case "bridge_exception":
                    return "the bundled native core has no AdMob support. Reinstall the com.gog.sdk " +
                           "package cleanly.";
                default:
                    return null;
            }
        }

        /// <summary>
        /// Fetch the still frame and present it. For a VIDEO creative this same still is the
        /// poster, and the video URL is handed to the overlay to swap in once it prepares.
        ///
        /// <para>The poster is deliberately the gate for BOTH kinds: it is small, it is the
        /// fallback, and gating on it means a video creative can never be worse than the image
        /// ad it degrades to. Nothing here waits on the video — the ad is already on screen
        /// before playback is attempted.</para>
        /// </summary>
        private static void FetchCreative(AdSession session, GogAdDirective directive)
        {
            // Captured at RECEIPT and measured on the monotonic clock, never against expires_at
            // (a device-wall-clock value; device clocks lie — §6).
            float deadline = Time.realtimeSinceStartup + directive.ExpiresInSeconds;

            GogHttp.GetTexture(directive.ImageUrl, r =>
            {
                if (!r.Ok)
                {
                    // I7: a creative we cannot fetch is NoAd, never an error and never a hang.
                    // The diagnostic is not lost — it names the creative here. (Never the
                    // impression id: that stays inside the SDK per I8.)
                    Debug.LogWarning("[GOG SDK] Ad creative " + directive.CreativeId +
                                     " could not be loaded (" + r.Error.Message + ") — resolving as NoAd.");
                    session.Resolve(GogAdOutcome.NoAd);
                    return;
                }

                // Re-check freshness before PRESENTING. Not re-checked mid-render: yanking a
                // visible ad off the player's screen serves no one. TTL gates starting a render,
                // not continuing one.
                if (Time.realtimeSinceStartup >= deadline)
                {
                    Debug.Log("[GOG SDK] Ad directive expired before it could be shown — resolving as NoAd.");
                    DestroyTexture(r.Value);
                    session.Resolve(GogAdOutcome.NoAd);
                    return;
                }

                try
                {
                    GogAdOverlay.Show(r.Value, directive.ClickUrl, directive.ForcedViewSeconds,
                        outcome => session.Resolve(outcome), directive.VideoUrl);
                }
                catch (Exception e)
                {
                    // Building the surface must never strand the callback.
                    Debug.LogException(e);
                    DestroyTexture(r.Value);
                    GogAdOverlay.Teardown();
                    session.Resolve(GogAdOutcome.NoAd);
                }
            });
        }

        private static void DestroyTexture(Texture2D texture)
        {
            if (texture == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(texture);
            else UnityEngine.Object.DestroyImmediate(texture);
        }

        /// <summary>The signed-in user id, when there is one. Optional and additive per §6: it
        /// unlocks city-targeted creatives. Best-effort — never fail an ad over targeting.</summary>
        private static string ResolveUserId()
        {
            try
            {
                GogSession session = GogAuth.CurrentSession;
                if (session == null || !session.IsSignedIn || session.User == null) return null;
                return string.IsNullOrEmpty(session.User.Id) ? null : session.User.Id;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Holds the partner callback and guarantees exactly-once delivery (I2) across BOTH async
        /// stages. <c>GogHttp.Once</c> only guards the fetch; the render is a second stage after
        /// that callback returns, so the module needs its own guard or a close racing a failsafe
        /// could deliver twice.
        /// </summary>
        private sealed class AdSession
        {
            private readonly Action<GogResult<GogAdResult>> _callback;
            private bool _delivered;

            internal AdSession(Action<GogResult<GogAdResult>> callback) { _callback = callback; }

            internal void Resolve(GogAdOutcome outcome) =>
                Deliver(GogResult<GogAdResult>.Success(new GogAdResult(outcome)));

            internal void Fail(GogError error) =>
                Deliver(GogResult<GogAdResult>.Fail(error));

            /// <summary>Resolve as NoAd because the SDK is going away, not because the ad
            /// finished. A no-op if already delivered.</summary>
            internal void Abandon() => Resolve(GogAdOutcome.NoAd);

            private void Deliver(GogResult<GogAdResult> result)
            {
                if (_delivered) return;
                _delivered = true; // set BEFORE invoking: a throwing callback cannot re-deliver
                if (_active == this) _active = null;

                // Already on the main thread on every path — GogHttp delivers through the
                // dispatcher, and the render stages run in Unity callbacks.
                try { _callback(result); }
                catch (Exception e) { Debug.LogException(e); } // the game's bug, made visible
            }
        }
    }
}
