using System;
using Gog.Sdk.Internal;

namespace Gog.Sdk
{
    /// <summary>
    /// The GOG SDK auth surface (docs/sdk-api-contract.md §2). A thin facade over the
    /// native Kotlin core: no auth logic, no token storage here. Identity and short-lived
    /// access tokens flow up; the refresh token never crosses into managed code.
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
        /// <see cref="GogResult{T}"/>.</summary>
        public static event Action<GogError> OnLaunchSignInFailed;

        /// <summary>Game-initiated "Sign in with GOG" — the reverse handoff. Opens the GOG
        /// hub, then signs this game in with the minted code. Completes through
        /// <paramref name="callback"/>; on success <see cref="OnSignedIn"/> also fires.</summary>
        public static void SignInWithGog(Action<GogResult<GogUser>> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            AndroidAuthBridge.SignInWithGog(r =>
            {
                callback(r);                           // initiator first (§1.1 ordering)
                if (r.Ok) OnSignedIn?.Invoke(r.Value); // then the global event
            });
        }

        /// <summary>Exchange an SSO code for a session — the escape hatch for games that own
        /// their own link plumbing. Completes through <paramref name="callback"/>; on success
        /// <see cref="OnSignedIn"/> also fires.</summary>
        public static void SignInWithCode(string code, Action<GogResult<GogUser>> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            AndroidAuthBridge.SignInWithCode(code, r =>
            {
                callback(r);
                if (r.Ok) OnSignedIn?.Invoke(r.Value);
            });
        }

        /// <summary>Current session — identity only, never tokens.</summary>
        public static GogSession CurrentSession => AndroidAuthBridge.CurrentSession();

        /// <summary>Get a fresh access token, refreshing on demand. The only way to obtain a
        /// token. On failure the result carries the reason (a dead refresh token also raises
        /// <see cref="OnSignedOut"/>).</summary>
        public static void GetAccessToken(Action<GogResult<string>> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            AndroidAuthBridge.GetAccessToken(callback);
        }

        /// <summary>Local sign-out (v1: no server revocation). Synchronous — a local token
        /// clear with no I/O, the one documented exception to the callback rule (contract
        /// §2, C5). Clears stored tokens and raises <see cref="OnSignedOut"/>.</summary>
        public static void SignOut()
        {
            AndroidAuthBridge.SignOut();
            OnSignedOut?.Invoke();
        }

        // Launch/deep-link sign-in has no explicit caller: success → OnSignedIn, failure →
        // OnLaunchSignInFailed (§1.1). Driven by GogDeepLink; internal on purpose.
        internal static void SignInFromLaunch(string code)
        {
            AndroidAuthBridge.SignInWithCode(code, r =>
            {
                if (r.Ok) OnSignedIn?.Invoke(r.Value);
                else OnLaunchSignInFailed?.Invoke(r.Error);
            });
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
