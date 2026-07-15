using System;
using UnityEngine;

namespace Gog.Sdk.Internal
{
    /// <summary>
    /// The JNI seam to the Kotlin core (<c>com.gog.sdk.GogAuthCore</c>). This is the ONLY
    /// file that talks to the native layer. It holds no auth logic and no tokens — it
    /// marshals calls down, maps the core's outcomes onto the unified
    /// <see cref="GogResult{T}"/>/<see cref="GogError"/> taxonomy, and dispatches results
    /// onto the main thread. Access tokens and identity pass back up; refresh tokens never do.
    ///
    /// <para>This is C# JNI glue, not the native core — the string-matched proxy method names
    /// below are the ONLY contract with the .aar, and they are unchanged. No Kotlin is touched.</para>
    /// </summary>
    internal static class AndroidAuthBridge
    {
        private static AndroidJavaObject _core; // com.gog.sdk.GogAuthCore (Android only)

        // game id + base URL come from GogSettings (via GogSdk.Initialize) and are passed
        // straight across the JNI initialize() call. Nothing is stored here — GogCoins reads
        // config from GogSdk, so there is no second copy to drift.
        internal static void Initialize(string gameId, string apiBaseUrl, Action onSignedOut)
        {
            if (_core != null) return;
            GogMainThreadDispatcher.Ensure(); // main thread here

            if (Application.platform != RuntimePlatform.Android)
            {
                // No native core off Android ⇒ no JNI seam to handshake. The version check is
                // Android-specific and is skipped cleanly here (contract §1.5, D4).
                return;
            }

            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            {
                var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                var core = new AndroidJavaObject("com.gog.sdk.GogAuthCore");

                // Version handshake FIRST — before any init work — so a stale/tampered .aar
                // fails at boot with a sentence instead of an AndroidJavaException mid-session
                // (contract §1.5, D3). sdkVersion() is a pure getter with no side effects.
                string coreVersion;
                try { coreVersion = core.Call<string>("sdkVersion"); }
                catch (Exception) { coreVersion = "(none: pre-handshake .aar)"; } // old .aar lacks the method
                string mismatch = VersionHandshakeError(GogSdk.Version, coreVersion);
                if (mismatch != null)
                {
                    core.Dispose();
                    throw new InvalidOperationException(mismatch);
                }

                // State-listener callbacks arrive on JNI/background threads → dispatch to
                // main. Beyond auto sign-out, the listener now carries the HEADLESS
                // completions (contract I6): a reverse handoff that outlived its process
                // finishes with no live per-call callback, so success/failure arrive here
                // and surface through OnSignedIn / OnLaunchSignInFailed.
                var listener = new StateListenerProxy(
                    () => GogMainThreadDispatcher.Enqueue(onSignedOut),
                    userJson =>
                    {
                        var user = ParseUser(userJson);
                        GogMainThreadDispatcher.Enqueue(() =>
                        {
                            if (user != null) GogAuth.RaiseSignedIn(user);
                            else GogAuth.RaiseLaunchSignInFailed(new GogError(
                                GogErrorCode.InternalError, "Sign-in returned no user.", false, "headless_no_user"));
                        });
                    },
                    (reason, message) =>
                    {
                        var error = MapReason(reason, message, exchange: true);
                        GogMainThreadDispatcher.Enqueue(() => GogAuth.RaiseLaunchSignInFailed(error));
                    });
                // UNCHANGED JNI seam: same method name "initialize", same arity
                // (activity, gameId, apiBaseUrl, listener). Only the value SOURCE changed.
                core.Call("initialize", activity, gameId, apiBaseUrl, listener);
                _core = core; // publish only after a passing handshake + successful init
            }
        }

        // Re-check for a persisted, completable reverse handoff (contract I6). The core's
        // own initialize() already runs this once; this extra trigger covers integrations
        // where the hub result could land after Initialize (e.g. runInBackground games that
        // initialize before the proxy's onActivityResult) — the core claim-guards, so extra
        // calls are no-ops. Invoked by the dispatcher on app-focus regain.
        internal static void ResumePendingHandoff() => _core?.Call("resumePendingHandoff");

        // The binding↔core version handshake (contract §1.5). Exact-match, because the two ship
        // together in one package — any inequality is a stale or tampered install. Returns the
        // remediation message on mismatch, or null when they agree. Pure and Editor-testable;
        // the on-device test proves the throw-at-boot behaviour end to end.
        internal static string VersionHandshakeError(string bindingVersion, string coreVersion)
            => bindingVersion == coreVersion
                ? null
                : $"com.gog.sdk C# binding is v{bindingVersion} but the bundled native core " +
                  $"reports v{coreVersion}. Reinstall the package; never hand-copy .aar files.";

        // Drop the native bridge reference. Does NOT sign out (contract §1.3) — the persisted
        // session survives a Shutdown()/Initialize() cycle. ResetStatics is the domain-reload
        // path (the JNI object is already gone when the play session ends).
        internal static void Shutdown() => _core = null;
        internal static void ResetStatics() => _core = null;

        internal static void SignInWithCode(string code, Action<GogResult<GogUser>> callback)
        {
            if (!RequireCore(callback)) return;
            var cb = new AuthCallbackProxy(
                resultJson =>
                {
                    var user = ParseUser(resultJson);
                    GogMainThreadDispatcher.Enqueue(() => callback(ToUserResult(user, "signin")));
                },
                (codeName, msg) => GogMainThreadDispatcher.Enqueue(
                    () => callback(GogResult<GogUser>.Fail(MapReason(codeName, msg, exchange: true)))));
            _core.Call("signInWithCode", code, cb);
        }

        internal static void SignInWithGog(Action<GogResult<GogUser>> callback)
        {
            if (!GogSdk.IsInitialized)
            {
                callback(GogResult<GogUser>.Fail(new GogError(
                    GogErrorCode.NotInitialized, "GogSdk.Initialize() was not called.", false, "not_initialized")));
                return;
            }
            if (_core == null)
            {
                // Initialized but no native core ⇒ non-Android (Editor). Surface synchronously.
                callback(GogResult<GogUser>.Fail(new GogError(
                    GogErrorCode.UnsupportedPlatform, "Reverse handoff runs on an Android device/build only.", false, "editor_unsupported")));
                return;
            }
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            {
                // currentActivity: the proxy must launch into the game's task so the hub's
                // getCallingPackage() attributes the request to this game.
                var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                var proxy = new HubSignInCallbackProxy(
                    userJson =>
                    {
                        var user = ParseUser(userJson);
                        GogMainThreadDispatcher.Enqueue(() => callback(ToUserResult(user, "hub")));
                    },
                    () => GogMainThreadDispatcher.Enqueue(() => callback(GogResult<GogUser>.Fail(
                        new GogError(GogErrorCode.HubNotInstalled, "The GOG hub app is not installed.", false, "hub_not_installed")))),
                    () => GogMainThreadDispatcher.Enqueue(() => callback(GogResult<GogUser>.Fail(
                        new GogError(GogErrorCode.SignInCancelled, "Sign-in was cancelled.", true, "user_cancelled")))),
                    reason => GogMainThreadDispatcher.Enqueue(() => callback(GogResult<GogUser>.Fail(
                        MapReason(reason, null, exchange: true)))));
                _core.Call("signInWithHub", activity, proxy);
            }
        }

        internal static void GetAccessToken(Action<GogResult<string>> callback)
        {
            if (!RequireCore(callback)) return;
            var cb = new AuthCallbackProxy(
                token => GogMainThreadDispatcher.Enqueue(() => callback(GogResult<string>.Success(token))),
                (codeName, msg) => GogMainThreadDispatcher.Enqueue(
                    () => callback(GogResult<string>.Fail(MapReason(codeName, msg, exchange: false)))));
            _core.Call("getAccessToken", cb);
        }

        internal static void SignOut() => _core?.Call("signOut");

        internal static GogSession CurrentSession()
        {
            if (_core == null) return new GogSession { IsSignedIn = false, User = null };
            var json = _core.Call<string>("currentUserJson");
            var user = ParseUser(json);
            return new GogSession { IsSignedIn = user != null, User = user };
        }

        // ── helpers ──────────────────────────────────────────────────────────

        private static GogResult<GogUser> ToUserResult(GogUser user, string ctx)
            => user != null
                ? GogResult<GogUser>.Success(user)
                : GogResult<GogUser>.Fail(new GogError(GogErrorCode.InternalError, "Sign-in returned no user.", true, ctx + "_no_user"));

        private static bool RequireCore<T>(Action<GogResult<T>> callback)
        {
            // NotInitialized wins on any platform (contract §4/I4); an initialized-but-null
            // core means non-Android (Editor). Invoked synchronously on the caller/main thread.
            if (!GogSdk.IsInitialized)
            {
                callback(GogResult<T>.Fail(new GogError(
                    GogErrorCode.NotInitialized, "GogSdk.Initialize() was not called.", false, "not_initialized")));
                return false;
            }
            if (_core == null)
            {
                callback(GogResult<T>.Fail(new GogError(
                    GogErrorCode.UnsupportedPlatform, "GOG SDK runs on an Android device/build only (not the Editor).", false, "editor_unsupported")));
                return false;
            }
            return true;
        }

        // Map a native/hub reason string → the unified taxonomy (contract §2/§5).
        //
        // ⚠ TWO NAMING CONVENTIONS ARRIVE HERE, and there is NO compiler check on these string
        // literals. The native Kotlin core emits PascalCase — the GogAuthErrorCode.name values
        // ("InvalidOrExpiredCode", "InvalidRequest", "NetworkError") sent via
        // GogAuthCallback.onError over JNI. The hub (and the core's own onFailed) emit
        // snake_case — the GogAuthContract ERROR_* constants ("invalid_request",
        // "caller_mismatch", "unsupported", …) plus "not_initialized", "hub_untrusted",
        // "unsupported_os", … Both land in this one switch. A reason matched by NEITHER falls to
        // default: = InternalError, which means "the SDK does not recognise this reason". A
        // KNOWN reason reaching InternalError is a BUG — that is exactly how the HTTP-400
        // "InvalidRequest" case once shipped mis-typed and wrongly retryable. Anyone adding a
        // reason MUST add it BOTH here AND to the table-driven test
        // NoKnownReasonFallsThroughToInternalError; renaming a Kotlin enum member without both
        // silently re-opens the hole.
        //
        // `exchange` = this is a sign-in CODE EXCHANGE: a NetworkError on the exchange means the
        // single-use code is likely burned → NOT retryable ("relaunch from GOG", §1.2); on a
        // token refresh (exchange = false) the same NetworkError IS retryable.
        internal static GogError MapReason(string reason, string message, bool exchange)
        {
            switch (reason)
            {
                // ── PascalCase: the native core's GogAuthErrorCode.name (via onError) ──
                case "InvalidOrExpiredCode":
                    return new GogError(GogErrorCode.InvalidOrExpiredCode, message ?? "The code was invalid or has expired.", false, reason);
                case "InvalidRequest": // HTTP 4xx (non-401) from the exchange/refresh — a malformed
                                       // request fails identically on retry → NOT retryable.
                    return new GogError(GogErrorCode.InvalidRequest, message ?? "The request was rejected as invalid.", false, reason);
                case "NetworkError": // core exchange/refresh transport failure
                    return new GogError(GogErrorCode.NetworkError, message ?? "Network error.", !exchange, reason);

                // ── snake_case: the core's onFailed strings + the hub/RN EXTRA_ERROR values ──
                case "not_initialized":
                    return new GogError(GogErrorCode.NotInitialized, message ?? "GOG SDK was not initialized.", false, reason);
                case "in_progress":
                    return new GogError(GogErrorCode.SignInInProgress, message ?? "A sign-in is already in progress.", true, reason);
                case "hub_untrusted": // a GENUINE cert-pin failure — API >= 28 only now (the OS case
                                      // is unsupported_os), so this really is a suspicious hub.
                    return new GogError(GogErrorCode.HubVerificationFailed, message ?? "The GOG hub failed verification.", false, reason);
                case "caller_mismatch":
                    return new GogError(GogErrorCode.HubVerificationFailed, message ?? "The caller could not be verified.", false, reason);
                case "hub_unavailable":
                    return new GogError(GogErrorCode.HubNotInstalled, message ?? "The GOG hub app is not available.", false, reason);
                case "unsupported_os": // SDK HubIdentity: API < 28 can't attest the caller cert
                case "unsupported":    // hub broker: the same API < 28 limit (its ERROR_UNSUPPORTED)
                    return new GogError(GogErrorCode.UnsupportedOsVersion,
                        message ?? "Reverse sign-in needs Android 9 (API 28) or newer; launch from the GOG hub instead.",
                        false, reason);
                case "unknown_game":
                    return new GogError(GogErrorCode.UnknownGame, message ?? "This game is not recognized.", false, reason);
                case "invalid_request":
                    return new GogError(GogErrorCode.InvalidRequest, message ?? "Invalid request.", false, reason);
                case "user_cancelled":
                    return new GogError(GogErrorCode.SignInCancelled, message ?? "Sign-in was cancelled.", true, reason);
                case "network": // an RN-side refusal BEFORE a code is minted → retrying the flow is safe
                    return new GogError(GogErrorCode.NetworkError, message ?? "Network error.", true, reason);
                case "editor_unsupported":
                    return new GogError(GogErrorCode.UnsupportedPlatform, message ?? "Not supported in the Editor.", false, reason);

                default:
                    // Genuinely UNRECOGNISED. InternalError = "the SDK doesn't know this reason";
                    // never claim it is retryable, and always keep the raw string for triage. A
                    // KNOWN reason landing here is a bug — add it above AND to the test.
                    return new GogError(GogErrorCode.InternalError, message ?? ("Sign-in failed: " + reason), false, reason);
            }
        }

        [Serializable]
        private struct UserDto { public string id; public string email; }

        private static GogUser ParseUser(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var dto = JsonUtility.FromJson<UserDto>(json);
                if (string.IsNullOrEmpty(dto.id)) return null;
                return new GogUser(dto.id, dto.email);
            }
            catch { return null; }
        }
    }

    /// <summary>AndroidJavaProxy for <c>com.gog.sdk.GogAuthCallback</c>. Method names MUST
    /// match the Kotlin interface exactly. Invoked on a JNI background thread.</summary>
    internal sealed class AuthCallbackProxy : AndroidJavaProxy
    {
        private readonly Action<string> _onSuccess;
        private readonly Action<string, string> _onError;

        public AuthCallbackProxy(Action<string> onSuccess, Action<string, string> onError)
            : base("com.gog.sdk.GogAuthCallback")
        {
            _onSuccess = onSuccess;
            _onError = onError;
        }

        // ReSharper disable once InconsistentNaming — must match Java method name.
        public void onSuccess(string result) => _onSuccess(result);

        // ReSharper disable once InconsistentNaming — must match Java method name.
        public void onError(string errorCode, string message) => _onError(errorCode, message);
    }

    /// <summary>AndroidJavaProxy for <c>com.gog.sdk.GogStateListener</c>. Method names MUST
    /// match the Kotlin interface exactly. Invoked on JNI/background threads — each forwards
    /// to a supplied action that marshals onto Unity's main thread.</summary>
    internal sealed class StateListenerProxy : AndroidJavaProxy
    {
        private readonly Action _onSignedOut;
        private readonly Action<string> _onSignedIn;
        private readonly Action<string, string> _onLaunchSignInFailed;

        public StateListenerProxy(
            Action onSignedOut,
            Action<string> onSignedIn,
            Action<string, string> onLaunchSignInFailed)
            : base("com.gog.sdk.GogStateListener")
        {
            _onSignedOut = onSignedOut;
            _onSignedIn = onSignedIn;
            _onLaunchSignInFailed = onLaunchSignInFailed;
        }

        // ReSharper disable InconsistentNaming — must match Java method names.
        public void onSignedOut() => _onSignedOut();
        public void onSignedIn(string userJson) => _onSignedIn(userJson);
        public void onLaunchSignInFailed(string reason, string message) => _onLaunchSignInFailed(reason, message);
        // ReSharper restore InconsistentNaming
    }

    /// <summary>AndroidJavaProxy for <c>com.gog.sdk.GogHubSignInCallback</c> (the
    /// reverse-handoff outcome callback). Method names MUST match the Kotlin interface
    /// exactly. Invoked on a JNI background thread — each forwards to the supplied action,
    /// which marshals onto Unity's main thread (see
    /// <see cref="AndroidAuthBridge.SignInWithGog"/>).</summary>
    internal sealed class HubSignInCallbackProxy : AndroidJavaProxy
    {
        private readonly Action<string> _onSignedIn;
        private readonly Action _onHubNotInstalled;
        private readonly Action _onUserCancelled;
        private readonly Action<string> _onFailed;

        public HubSignInCallbackProxy(
            Action<string> onSignedIn,
            Action onHubNotInstalled,
            Action onUserCancelled,
            Action<string> onFailed)
            : base("com.gog.sdk.GogHubSignInCallback")
        {
            _onSignedIn = onSignedIn;
            _onHubNotInstalled = onHubNotInstalled;
            _onUserCancelled = onUserCancelled;
            _onFailed = onFailed;
        }

        // ReSharper disable InconsistentNaming — must match Java method names.
        public void onSignedIn(string userJson) => _onSignedIn(userJson);
        public void onHubNotInstalled() => _onHubNotInstalled();
        public void onUserCancelled() => _onUserCancelled();
        public void onFailed(string reason) => _onFailed(reason);
        // ReSharper restore InconsistentNaming
    }
}
