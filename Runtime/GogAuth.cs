using System;
using Gog.Sdk.Internal;
using UnityEngine;

namespace Gog.Sdk
{
    /// <summary>
    /// The GOG SDK auth surface (docs/sdk-api-contract.md §2). A thin facade over the
    /// native core (Kotlin on Android, Swift on iOS): no auth logic, no token storage here.
    /// Identity and short-lived access tokens flow up; the refresh token never crosses into
    /// managed code.
    ///
    /// <para>Explicit operations report through their single required callback
    /// (<see cref="GogResult{T}"/>). Events are only for transitions the game did not
    /// necessarily initiate (§1.1); when a transition results from an explicit call, the
    /// caller's callback fires first, then the event.</para>
    ///
    /// <para>All callbacks and events are delivered on Unity's main thread.</para>
    /// </summary>
    public static class GogAuth
    {
        /// <summary>Fired on any successful sign-in, whatever the path (explicit call,
        /// launch code, deep link).</summary>
        public static event Action<GogUser> OnSignedIn;

        /// <summary>Fired on sign-out — explicit <see cref="SignOut"/> or unsolicited
        /// (a refresh rejected with a real 401 → dead session). (The GogSignOutReason
        /// argument arrives in S7.2; kept parameterless this slice.)</summary>
        public static event Action OnSignedOut;

        /// <summary>Fired when an SDK-initiated launch/deep-link sign-in fails — there is no
        /// call site to receive a callback, so the failure travels here (§1.1). Explicit
        /// sign-in failures do NOT come through this event; they arrive on the caller's
        /// <see cref="GogResult{T}"/>. (One deliberate exception:
        /// <see cref="TrySignInFromInstallHandoff"/> reports through both, because the code it
        /// spends was SDK-delivered, exactly like a launch link's.)</summary>
        public static event Action<GogError> OnLaunchSignInFailed;

        /// <summary>Game-initiated "Sign in with GOG" — the reverse handoff. Opens the GOG
        /// hub, then signs this game in with the minted code. Completes through
        /// <paramref name="callback"/>; on success <see cref="OnSignedIn"/> also fires.
        ///
        /// <para>iOS: the hub app hands the code back through this game's universal link, so
        /// <see cref="GogDeepLink.Enable"/> must have been called for the sign-in to complete.
        /// When the hub is not installed the SDK opens its App Store page itself and completes
        /// with <see cref="GogErrorCode.HubNotInstalled"/>.</para></summary>
        public static void SignInWithGog(Action<GogResult<GogUser>> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            Action<GogResult<GogUser>> done = r =>
            {
                callback(r);                           // initiator first (§1.1 ordering)
                if (r.Ok) OnSignedIn?.Invoke(r.Value); // then the global event
            };
            if (IosAuthBridge.IsActivePlatform)
            {
                if (GogSdk.IsInitialized && !GogDeepLink.IsEnabled)
                    Debug.LogWarning("[GOG SDK] SignInWithGog on iOS returns through this game's universal " +
                                     "link, but GogDeepLink.Enable() has not been called, so the sign-in " +
                                     "cannot complete. Call GogDeepLink.Enable() right after GogSdk.Initialize().");
                IosAuthBridge.SignInWithGog(done);
            }
            else
            {
                AndroidAuthBridge.SignInWithGog(done);
            }
        }

        /// <summary>Exchange an SSO code for a session — the escape hatch for games that own
        /// their own link plumbing. Completes through <paramref name="callback"/>; on success
        /// <see cref="OnSignedIn"/> also fires.</summary>
        public static void SignInWithCode(string code, Action<GogResult<GogUser>> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            BridgeSignInWithCode(code, r =>
            {
                callback(r);
                if (r.Ok) OnSignedIn?.Invoke(r.Value);
            });
        }

        /// <summary>
        /// Sign in with the code the GOG hub left for this game before sending the player to the
        /// App Store — so a game installed from the hub opens already signed in, with no screen.
        /// Call it ONCE, right after <see cref="GogSdk.Initialize"/>; the SDK never runs it on
        /// its own.
        ///
        /// <para>The callback gets <c>Ok(true)</c> when the player was signed in (then
        /// <see cref="OnSignedIn"/> fires, after the callback), or <c>Ok(false)</c> when there
        /// was nothing to do — no code, an expired one, a player already signed in, or not iOS
        /// (Android and the Editor always answer <c>Ok(false)</c>). Carry on as a guest on
        /// <c>false</c>. A failed exchange completes with the error AND raises
        /// <see cref="OnLaunchSignInFailed"/>, as a launch link would. Called before
        /// <see cref="GogSdk.Initialize"/> it is <see cref="GogErrorCode.NotInitialized"/>, on
        /// every platform.</para>
        /// </summary>
        public static void TrySignInFromInstallHandoff(Action<GogResult<bool>> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            if (!GogSdk.IsInitialized)
            {
                callback(GogResult<bool>.Fail(GogErrorCode.NotInitialized,
                    "GogSdk.Initialize() was not called.", retryable: false, rawReason: "not_initialized"));
                return;
            }
            if (!IosAuthBridge.IsActivePlatform)
            {
                callback(GogResult<bool>.Success(false)); // the handoff is an iOS Keychain feature
                return;
            }
            IosAuthBridge.SignInFromInstallHandoff(r =>
            {
                if (!r.Ok)
                {
                    callback(GogResult<bool>.Fail(r.Error));
                    OnLaunchSignInFailed?.Invoke(r.Error);
                    return;
                }
                if (!r.Value.Handled)
                {
                    callback(GogResult<bool>.Success(false));
                    return;
                }
                if (r.Value.User == null)
                {
                    var error = new GogError(GogErrorCode.InternalError, "Sign-in returned no user.", false, "handoff_no_user");
                    callback(GogResult<bool>.Fail(error));
                    OnLaunchSignInFailed?.Invoke(error);
                    return;
                }
                callback(GogResult<bool>.Success(true)); // initiator first (§1.1 ordering)
                OnSignedIn?.Invoke(r.Value.User);
            });
        }

        /// <summary>Current session — identity only, never tokens.</summary>
        public static GogSession CurrentSession => IosAuthBridge.IsActivePlatform
            ? IosAuthBridge.CurrentSession()
            : AndroidAuthBridge.CurrentSession();

        /// <summary>Get a fresh access token, refreshing on demand. The only way to obtain a
        /// token. On failure the result carries the reason (a dead refresh token also raises
        /// <see cref="OnSignedOut"/>).</summary>
        public static void GetAccessToken(Action<GogResult<string>> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            if (IosAuthBridge.IsActivePlatform) IosAuthBridge.GetAccessToken(callback);
            else AndroidAuthBridge.GetAccessToken(callback);
        }

        /// <summary>Local sign-out (v1: no server revocation). Synchronous — a local token
        /// clear with no I/O, the one documented exception to the callback rule (contract
        /// §2, C5). Clears stored tokens and raises <see cref="OnSignedOut"/>.</summary>
        public static void SignOut()
        {
            if (IosAuthBridge.IsActivePlatform) IosAuthBridge.SignOut();
            else AndroidAuthBridge.SignOut();
            OnSignedOut?.Invoke();
        }

        // Launch/deep-link sign-in has no explicit caller: success → OnSignedIn, failure →
        // OnLaunchSignInFailed (§1.1). Driven by GogDeepLink; internal on purpose.
        internal static void SignInFromLaunch(string code)
        {
            BridgeSignInWithCode(code, r =>
            {
                if (r.Ok) OnSignedIn?.Invoke(r.Value);
                else OnLaunchSignInFailed?.Invoke(r.Error);
            });
        }

        // The iOS launch path: the native core validates the whole universal link (host, Game ID,
        // where the code rides) and exchanges it. Same events as SignInFromLaunch.
        //
        // While a SignInWithGog is pending, the link IS its return leg: the native side finishes
        // that pending call WITH the user, and this reply may or may not carry the user too. The
        // pending call's callback owns the outcome then, so OnSignedIn fires exactly once — from
        // there — and a failure belongs to that caller rather than to this event. Captured
        // BEFORE the round trip, because the pending call settles while this one is in flight.
        internal static void SignInFromLaunchUrl(string url)
        {
            bool hubSignInOwnsOutcome = IosAuthBridge.IsHubSignInPending;
            IosAuthBridge.HandleLaunchUrl(url, r =>
            {
                if (!r.Ok)
                {
                    if (hubSignInOwnsOutcome)
                        Debug.LogWarning("[GOG SDK] The Sign in with GOG return link failed: " + r.Error +
                                         " — the SignInWithGog callback reports the outcome.");
                    else OnLaunchSignInFailed?.Invoke(r.Error);
                    return;
                }
                if (!r.Value.Handled)
                {
                    Debug.LogWarning("[GOG SDK] The native core did not treat \"" + url +
                                     "\" as this game's launch link; nothing was signed in.");
                    return;
                }
                // A null user is legal (manual mode, a redelivered link, a pending hub sign-in
                // finishing the exchange) and is never reported as a sign-in.
                if (r.Value.User != null && !hubSignInOwnsOutcome) OnSignedIn?.Invoke(r.Value.User);
            });
        }

        private static void BridgeSignInWithCode(string code, Action<GogResult<GogUser>> callback)
        {
            if (IosAuthBridge.IsActivePlatform) IosAuthBridge.SignInWithCode(code, callback);
            else AndroidAuthBridge.SignInWithCode(code, callback);
        }

        // The native core's auto sign-out (dead refresh) is routed here — GogSdk.Initialize
        // hands this to the bridge as the sign-out listener so GogAuth stays the event owner.
        internal static void RaiseSignedOut() => OnSignedOut?.Invoke();

        // HEADLESS completions (contract I6): a reverse handoff that outlived its process is
        // finished by the native core with no live per-call callback — the caller's lambda
        // died with the process. Success/failure arrive from the native state listener and
        // surface through the unsolicited-transition events (§1.1).
        internal static void RaiseSignedIn(GogUser user) => OnSignedIn?.Invoke(user);
        internal static void RaiseLaunchSignInFailed(GogError error) => OnLaunchSignInFailed?.Invoke(error);

        // Drop every subscriber so Shutdown()/Enter-Play-Mode-without-Domain-Reload cannot
        // leak a stale handler into the next session (contract §1.3).
        internal static void ResetEvents()
        {
            OnSignedIn = null;
            OnSignedOut = null;
            OnLaunchSignInFailed = null;
        }
    }
}
