using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Gog.Sdk.Internal
{
    /// <summary>
    /// The P/Invoke seam to the Swift core (<c>GOG.xcframework</c>,
    /// <c>Sources/GOG/Bridge/GogUnityBridge.swift</c>) — the iOS counterpart of
    /// <see cref="AndroidAuthBridge"/>, with the same internal surface. It holds no auth logic and
    /// no tokens: it marshals calls down, maps the core's one-JSON-object replies onto the unified
    /// <see cref="GogResult{T}"/>/<see cref="GogError"/> taxonomy, and dispatches results onto the
    /// main thread. Access tokens and identity pass back up; refresh tokens never do.
    ///
    /// <para>ABI (stated once, in the Swift header, and obeyed here): strings go DOWN as
    /// NUL-terminated UTF-8 borrowed for the call; strings RETURNED are <c>strdup</c>ed and handed
    /// back to <c>gog_bridge_free</c>; async results arrive as ONE JSON object on the main thread
    /// through a C callback tagged with the caller's request id. Both directions are encoded and
    /// decoded by hand rather than trusting the marshaller's string conversion.</para>
    /// </summary>
    internal static class IosAuthBridge
    {
        /// <summary>The one platform this bridge drives. Everything else keeps its existing path
        /// (Android → <see cref="AndroidAuthBridge"/>; Editor/other → the non-Android behaviour).</summary>
        internal static bool IsActivePlatform => Application.platform == RuntimePlatform.IPhonePlayer;

        // `UnityBridgeEvent` raw values — ABI.
        private const int EventSessionExpired = 1;

        private static bool _live;           // the native core accepted gog_bridge_initialize
        private static Action _onSignedOut;  // GogAuth.RaiseSignedOut, held for the event callback

        // request id → the C# continuation waiting for that reply. Native replies on the main
        // thread, but a game may CALL from a worker, so the map is locked.
        private static readonly object Gate = new object();
        private static readonly Dictionary<int, PendingReply> Pending = new Dictionary<int, PendingReply>();
        private static int _nextRequestId;   // never reset: a late reply must not match a new id
        private static int _hubSignInsInFlight;

        // The function pointers native keeps. Static, so the delegates live as long as the process
        // and IL2CPP can hand out a stable pointer to the [MonoPInvokeCallback] targets below.
        private static readonly NativeResultCallback ResultCallback = OnNativeResult;
        private static readonly NativeEventCallback EventCallback = OnNativeEvent;

        internal static void Initialize(string gameId, string apiBaseUrl, Action onSignedOut)
        {
            if (_live) return;
            GogMainThreadDispatcher.Ensure(); // main thread here

            if (!IsActivePlatform) return;

            // Version handshake FIRST — before any init work — exactly as Android does: a stale or
            // hand-copied GOG.xcframework fails at boot with a sentence (contract §1.5). The Swift
            // side leaves this check to the binding on purpose.
            string coreVersion = TakeString(Native.gog_bridge_sdk_version()) ?? "(none)";
            string mismatch = VersionHandshakeError(GogSdk.Version, coreVersion);
            if (mismatch != null) throw new InvalidOperationException(mismatch);

            _onSignedOut = onSignedOut;
            string refusal = TakeString(Native.gog_bridge_initialize(
                Utf8Z(gameId), Utf8Z(apiBaseUrl), EventCallback));
            if (refusal != null)
            {
                // Android's core.Call("initialize") throws out of Initialize on failure too.
                _onSignedOut = null;
                throw new InvalidOperationException("GOG SDK: the native iOS core refused to start: " + refusal);
            }
            _live = true; // publish only after a passing handshake + successful init
        }

        // The binding↔core version handshake (contract §1.5), iOS wording. Exact ordinal match —
        // the two ship together in one package, so any inequality is a stale or tampered install.
        internal static string VersionHandshakeError(string bindingVersion, string coreVersion)
            => bindingVersion == coreVersion
                ? null
                : $"com.gog.sdk C# binding is v{bindingVersion} but the bundled native iOS core " +
                  $"(GOG.xcframework) reports v{coreVersion}. Reinstall the package; never hand-copy " +
                  "GOG.xcframework.";

        // Drop the native bridge. Does NOT sign out (contract §1.3) — the Keychain session survives
        // a Shutdown()/Initialize() cycle. In-flight replies stay best-effort, as on Android: the
        // dispatcher is gone, so they are dropped when they land.
        internal static void Shutdown()
        {
            bool wasLive = _live;
            _live = false;
            _onSignedOut = null;
            if (wasLive) Native.gog_bridge_shutdown();
        }

        // Domain-reload path. Never live in the Editor, so there is no native state to drop.
        internal static void ResetStatics()
        {
            _live = false;
            _onSignedOut = null;
            lock (Gate)
            {
                Pending.Clear();
                _hubSignInsInFlight = 0;
            }
        }

        internal static void SignInWithCode(string code, Action<GogResult<GogUser>> callback)
        {
            if (!RequireLive(callback)) return;
            int id = Register(reply => callback(ToUserResult(reply, "signin")), hubSignIn: false);
            Native.gog_bridge_sign_in_with_code(id, Utf8Z(code), ResultCallback);
        }

        // "Sign in with GOG": the native side opens the hub APP, and the hub hands the code back
        // through this game's universal link — which reaches native via GogDeepLink →
        // HandleLaunchUrl. The native side then finishes THIS request with the user. Counted while
        // in flight so the launch-link reply does not raise OnSignedIn a second time.
        internal static void SignInWithGog(Action<GogResult<GogUser>> callback)
        {
            if (!RequireLive(callback)) return;
            int id = Register(reply => callback(ToUserResult(reply, "hub")), hubSignIn: true);
            Native.gog_bridge_sign_in_with_gog(id, ResultCallback);
        }

        internal static void GetAccessToken(Action<GogResult<string>> callback)
        {
            if (!RequireLive(callback)) return;
            int id = Register(reply => callback(ToTokenResult(reply)), hubSignIn: false);
            Native.gog_bridge_get_access_token(id, ResultCallback);
        }

        // A universal link handed over by GogDeepLink. Reply shape {handled, user}.
        internal static void HandleLaunchUrl(string url, Action<GogResult<IosLaunchOutcome>> callback)
        {
            if (!RequireLive(callback)) return;
            int id = Register(reply => callback(ToLaunchResult(reply)), hubSignIn: false);
            Native.gog_bridge_handle_launch_url(id, Utf8Z(url), ResultCallback);
        }

        // The hub's App Store install handoff. Same {handled, user} shape as a launch link.
        internal static void SignInFromInstallHandoff(Action<GogResult<IosLaunchOutcome>> callback)
        {
            if (!RequireLive(callback)) return;
            int id = Register(reply => callback(ToLaunchResult(reply)), hubSignIn: false);
            Native.gog_bridge_sign_in_from_install_handoff(id, ResultCallback);
        }

        /// <summary>Whether <paramref name="url"/> is THIS game's launch link. Synchronous, so
        /// GogDeepLink can raise OnSignInStarted before the round trip.</summary>
        internal static bool IsLaunchLink(string url)
            => _live && !string.IsNullOrEmpty(url) && Native.gog_bridge_is_launch_link(Utf8Z(url)) == 1;

        /// <summary>True while a <see cref="SignInWithGog"/> has not had its reply yet.</summary>
        internal static bool IsHubSignInPending
        {
            get { lock (Gate) { return _hubSignInsInFlight > 0; } }
        }

        // Synchronous by contract (§2, C5): native has cleared the Keychain when this returns.
        internal static void SignOut()
        {
            if (_live) Native.gog_bridge_sign_out();
        }

        internal static GogSession CurrentSession()
        {
            if (!_live) return new GogSession { IsSignedIn = false, User = null };
            var user = ParseUserJson(TakeString(Native.gog_bridge_current_user_json()));
            return new GogSession { IsSignedIn = user != null, User = user };
        }

        // ── reply → GogResult (pure; Editor-testable) ────────────────────────────

        internal static GogResult<GogUser> ToUserResult(BridgeReply reply, string ctx)
        {
            if (!reply.Ok)
                return GogResult<GogUser>.Fail(MapReason(reply.Reason, reply.Message, reply.Retryable, exchange: true));
            return reply.User != null
                ? GogResult<GogUser>.Success(reply.User)
                : GogResult<GogUser>.Fail(new GogError(GogErrorCode.InternalError, "Sign-in returned no user.", true, ctx + "_no_user"));
        }

        internal static GogResult<string> ToTokenResult(BridgeReply reply)
        {
            if (!reply.Ok)
                return GogResult<string>.Fail(MapReason(reply.Reason, reply.Message, reply.Retryable, exchange: false));
            return !string.IsNullOrEmpty(reply.Token)
                ? GogResult<string>.Success(reply.Token)
                : GogResult<string>.Fail(new GogError(GogErrorCode.InternalError, "The token call returned no token.", true, "token_missing"));
        }

        internal static GogResult<IosLaunchOutcome> ToLaunchResult(BridgeReply reply)
        {
            if (!reply.Ok)
                return GogResult<IosLaunchOutcome>.Fail(MapReason(reply.Reason, reply.Message, reply.Retryable, exchange: true));
            return GogResult<IosLaunchOutcome>.Success(new IosLaunchOutcome(reply.Handled, reply.User));
        }

        // Map the Swift core's error token → the unified taxonomy (contract §2/§5).
        //
        // The tokens are `BridgeResult.reason(for:)` in GogUnityBridge.swift — the iOS SDK's own
        // stable snake_case set, NOT Android's strings — plus "not_initialized" and "internal".
        // Exactly as on Android there is NO compiler check across the seam: anyone adding a token
        // MUST add it BOTH here AND to the table-driven test
        // IosAuthBridgeTests.NoKnownIosReasonFallsThroughToInternalError. ("internal" is the one
        // token that maps to InternalError on purpose: native's own "unexpected error".)
        //
        // `nativeRetryable` is the transport's opinion; CONTEXT still overrides it. `exchange` =
        // this is a sign-in CODE EXCHANGE: a network failure there means the single-use code is
        // likely burned → NOT retryable; on a token refresh (exchange = false) it may be.
        internal static GogError MapReason(string reason, string message, bool nativeRetryable, bool exchange)
        {
            switch (reason)
            {
                case "network":
                    return new GogError(GogErrorCode.NetworkError, Describe("Network error.", message), !exchange && nativeRetryable, reason);
                case "malformed_response":
                    return new GogError(GogErrorCode.NetworkError, Describe("Malformed response.", message), !exchange, reason);
                case "server":
                    return new GogError(GogErrorCode.InternalError, Describe("The GOG server failed.", message), !exchange, reason);
                case "unauthorized":
                    return new GogError(GogErrorCode.Unauthorized, Describe("Not signed in, or the session was rejected.", message), false, reason);
                case "account_banned":
                    return new GogError(GogErrorCode.Unauthorized, Describe("This GOG account is banned.", message), false, reason);
                case "rate_limited":
                    return new GogError(GogErrorCode.RateLimited, Describe("Rate limited.", message), true, reason);
                case "insufficient_funds":
                    return new GogError(GogErrorCode.InsufficientFunds, Describe("Insufficient funds.", message), false, reason);
                case "earn_too_large":
                    return new GogError(GogErrorCode.EarnTooLarge, Describe("Earn amount too large.", message), false, reason);
                case "idempotency_key_reused":
                    return new GogError(GogErrorCode.IdempotencyKeyReused, Describe("Idempotency key reused with different parameters.", message), false, reason);
                case "invalid_input":
                    return new GogError(GogErrorCode.InvalidRequest, Describe("Invalid request.", message), false, reason);
                case "city_required": // GogHttp maps the same 428 to InvalidRequest
                    return new GogError(GogErrorCode.InvalidRequest, Describe("The server requires a city for this account.", message), false, reason);
                case "unknown_game":
                    return new GogError(GogErrorCode.UnknownGame, Describe("This game is not recognized.", message), false, reason);
                case "invalid_or_expired_code":
                    return new GogError(GogErrorCode.InvalidOrExpiredCode, Describe("The code was invalid or has expired.", message), false, reason);
                case "sign_in_cancelled":
                    return new GogError(GogErrorCode.SignInCancelled, Describe("Sign-in was cancelled.", message), true, reason);
                case "hub_not_installed": // native has already opened the hub's App Store page
                    return new GogError(GogErrorCode.HubNotInstalled, Describe("The GOG hub app is not installed.", message), false, reason);
                case "sign_in_in_progress":
                    return new GogError(GogErrorCode.SignInInProgress, Describe("A sign-in is already in progress.", message), true, reason);
                case "launch_link_invalid": // ours, but broken: an integration/link bug, not a player one
                    return new GogError(GogErrorCode.InvalidRequest, Describe("The GOG launch link was rejected.", message), false, reason);
                case "not_configured": // native carries the remediation text in `message`
                    return new GogError(GogErrorCode.InvalidRequest, Describe("The GOG SDK is misconfigured.", message), false, reason);
                case "not_initialized":
                    return new GogError(GogErrorCode.NotInitialized, Describe("GOG SDK was not initialized.", message), false, reason);
                case "internal":
                    return new GogError(GogErrorCode.InternalError, Describe("The native iOS core failed unexpectedly.", message), false, reason);

                default:
                    // Genuinely UNRECOGNISED (a newer core, or an unreadable reply). Never claim it
                    // is retryable; always keep the raw string for triage.
                    return new GogError(GogErrorCode.InternalError, message ?? ("Sign-in failed: " + reason), false, reason);
            }
        }

        // Native messages are terse developer text ("invalidOrExpiredCode", or remediation for
        // notConfigured), so they ride along after a sentence rather than replacing it.
        private static string Describe(string sentence, string nativeMessage)
            => string.IsNullOrEmpty(nativeMessage) ? sentence : sentence + " (" + nativeMessage + ")";

        // ── JSON (Newtonsoft LINQ: no reflection over our own types, so nothing for IL2CPP
        //    managed-code stripping to remove) ─────────────────────────────────────

        internal static BridgeReply ParseReply(string json)
        {
            JObject obj = null;
            if (!string.IsNullOrEmpty(json))
            {
                try { obj = JObject.Parse(json); }
                catch (Exception) { obj = null; }
            }
            if (obj == null)
            {
                return new BridgeReply
                {
                    Ok = false,
                    Reason = "malformed_bridge_reply",
                    Message = "The native iOS core sent an unreadable reply.",
                };
            }
            return new BridgeReply
            {
                Ok = ReadBool(obj, "ok"),
                Handled = ReadBool(obj, "handled"),
                Token = ReadString(obj, "token"),
                User = ParseUser(obj["user"]),
                Reason = ReadString(obj, "reason"),
                Message = ReadString(obj, "message"),
                Retryable = ReadBool(obj, "retryable"),
            };
        }

        // A bare user object ({"id":…,"email":…}) — what gog_bridge_current_user_json returns.
        internal static GogUser ParseUserJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return ParseUser(JObject.Parse(json)); }
            catch (Exception) { return null; }
        }

        private static GogUser ParseUser(JToken token)
        {
            var obj = token as JObject;
            if (obj == null) return null;
            string id = ReadString(obj, "id");
            if (string.IsNullOrEmpty(id)) return null;
            return new GogUser(id, ReadString(obj, "email"));
        }

        private static bool ReadBool(JObject obj, string key)
        {
            JToken token = obj[key];
            return token != null && token.Type == JTokenType.Boolean && (bool)token;
        }

        private static string ReadString(JObject obj, string key)
        {
            JToken token = obj[key];
            return token != null && token.Type == JTokenType.String ? (string)token : null;
        }

        // ── strings across the seam ──────────────────────────────────────────────

        // NUL-terminated UTF-8. A byte[] parameter is pinned and passed as a pointer for the
        // duration of the call — "borrowed for the call", exactly as the native side expects.
        internal static byte[] Utf8Z(string value)
        {
            string s = value ?? string.Empty;
            var bytes = new byte[Encoding.UTF8.GetByteCount(s) + 1]; // last byte stays 0
            Encoding.UTF8.GetBytes(s, 0, s.Length, bytes, 0);
            return bytes;
        }

        // Decode a NUL-terminated UTF-8 C string. Does NOT free it.
        internal static string ReadUtf8(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero) return null;
            int length = 0;
            while (Marshal.ReadByte(pointer, length) != 0) length++;
            if (length == 0) return string.Empty;
            var bytes = new byte[length];
            Marshal.Copy(pointer, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }

        // Decode a string native RETURNED, then hand it back to the one allocator that made it.
        private static string TakeString(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero) return null;
            try { return ReadUtf8(pointer); }
            finally { Native.gog_bridge_free(pointer); }
        }

        // ── request bookkeeping ──────────────────────────────────────────────────

        private static bool RequireLive<T>(Action<GogResult<T>> callback)
        {
            // NotInitialized wins (contract §4/I4). Invoked synchronously on the caller's thread.
            if (!GogSdk.IsInitialized)
            {
                callback(GogResult<T>.Fail(new GogError(
                    GogErrorCode.NotInitialized, "GogSdk.Initialize() was not called.", false, "not_initialized")));
                return false;
            }
            if (!_live)
            {
                callback(GogResult<T>.Fail(new GogError(
                    GogErrorCode.UnsupportedPlatform, "The GOG SDK iOS core is not running.", false, "ios_core_not_live")));
                return false;
            }
            return true;
        }

        private static int Register(Action<BridgeReply> onReply, bool hubSignIn)
        {
            lock (Gate)
            {
                if (_nextRequestId == int.MaxValue) _nextRequestId = 0;
                int id = ++_nextRequestId;
                Pending[id] = new PendingReply(onReply, hubSignIn);
                if (hubSignIn) _hubSignInsInFlight++;
                return id;
            }
        }

        // Reverse P/Invoke target (IL2CPP: static + MonoPInvokeCallback). Native calls it on the
        // main thread with a BORROWED string. Nothing may escape into native code, so everything
        // is caught; the game's continuation runs from the dispatcher, like every Android result.
        [AOT.MonoPInvokeCallback(typeof(NativeResultCallback))]
        private static void OnNativeResult(int requestId, IntPtr json)
        {
            try
            {
                string text = ReadUtf8(json);
                PendingReply pending;
                lock (Gate)
                {
                    if (!Pending.TryGetValue(requestId, out pending)) return; // reset/unknown: drop
                    Pending.Remove(requestId);
                    // Settled HERE, not in the dispatcher, so a reply dropped by Shutdown cannot
                    // leave a hub sign-in counted forever.
                    if (pending.HubSignIn && _hubSignInsInFlight > 0) _hubSignInsInFlight--;
                }
                BridgeReply reply = ParseReply(text);
                GogMainThreadDispatcher.Enqueue(() => pending.OnReply(reply));
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        [AOT.MonoPInvokeCallback(typeof(NativeEventCallback))]
        private static void OnNativeEvent(int kind, IntPtr json)
        {
            try
            {
                // Only the unsolicited sign-out is forwarded (Android's onSignedOut). An unknown
                // kind is a newer core than this binding — additive, so it is ignored.
                if (kind != EventSessionExpired) return;
                Action onSignedOut = _onSignedOut;
                if (onSignedOut != null) GogMainThreadDispatcher.Enqueue(onSignedOut);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        private sealed class PendingReply
        {
            internal readonly Action<BridgeReply> OnReply;
            internal readonly bool HubSignIn;

            internal PendingReply(Action<BridgeReply> onReply, bool hubSignIn)
            {
                OnReply = onReply;
                HubSignIn = hubSignIn;
            }
        }

        /// <summary>
        /// The C ABI, names identical to the <c>@_cdecl</c> names in GogUnityBridge.swift.
        ///
        /// <para>🔴 Compiled in ONLY for iOS players. IL2CPP binds <c>"__Internal"</c> imports
        /// STATICALLY on every target, so on Android (or any other IL2CPP player) these externs
        /// would be unresolved symbols at link time — a runtime platform check alone does not stop
        /// that. Everywhere else the stubs below compile instead; they are unreachable, because
        /// every caller is behind <see cref="IsActivePlatform"/> / <c>_live</c>.</para>
        /// </summary>
        private static class Native
        {
#if UNITY_IOS && !UNITY_EDITOR
            [DllImport("__Internal")] internal static extern void gog_bridge_free(IntPtr pointer);
            [DllImport("__Internal")] internal static extern IntPtr gog_bridge_sdk_version();
            [DllImport("__Internal")] internal static extern IntPtr gog_bridge_initialize(byte[] gameId, byte[] apiBaseUrl, NativeEventCallback onEvent);
            [DllImport("__Internal")] internal static extern void gog_bridge_shutdown();
            [DllImport("__Internal")] internal static extern void gog_bridge_sign_in_with_code(int requestId, byte[] code, NativeResultCallback callback);
            [DllImport("__Internal")] internal static extern void gog_bridge_sign_in_with_gog(int requestId, NativeResultCallback callback);
            [DllImport("__Internal")] internal static extern void gog_bridge_get_access_token(int requestId, NativeResultCallback callback);
            [DllImport("__Internal")] internal static extern void gog_bridge_handle_launch_url(int requestId, byte[] url, NativeResultCallback callback);
            [DllImport("__Internal")] internal static extern void gog_bridge_sign_in_from_install_handoff(int requestId, NativeResultCallback callback);
            [DllImport("__Internal")] internal static extern int gog_bridge_is_launch_link(byte[] url);
            [DllImport("__Internal")] internal static extern IntPtr gog_bridge_current_user_json();
            [DllImport("__Internal")] internal static extern void gog_bridge_sign_out();
#else
            internal static void gog_bridge_free(IntPtr pointer) => throw Unavailable();
            internal static IntPtr gog_bridge_sdk_version() => throw Unavailable();
            internal static IntPtr gog_bridge_initialize(byte[] gameId, byte[] apiBaseUrl, NativeEventCallback onEvent) => throw Unavailable();
            internal static void gog_bridge_shutdown() => throw Unavailable();
            internal static void gog_bridge_sign_in_with_code(int requestId, byte[] code, NativeResultCallback callback) => throw Unavailable();
            internal static void gog_bridge_sign_in_with_gog(int requestId, NativeResultCallback callback) => throw Unavailable();
            internal static void gog_bridge_get_access_token(int requestId, NativeResultCallback callback) => throw Unavailable();
            internal static void gog_bridge_handle_launch_url(int requestId, byte[] url, NativeResultCallback callback) => throw Unavailable();
            internal static void gog_bridge_sign_in_from_install_handoff(int requestId, NativeResultCallback callback) => throw Unavailable();
            internal static int gog_bridge_is_launch_link(byte[] url) => throw Unavailable();
            internal static IntPtr gog_bridge_current_user_json() => throw Unavailable();
            internal static void gog_bridge_sign_out() => throw Unavailable();

            private static Exception Unavailable() => new PlatformNotSupportedException(
                "GOG SDK: the native iOS core is linked into iOS player builds only.");
#endif
        }
    }

    /// <summary><c>void (*)(int32_t requestId, const char *json)</c> — json is borrowed.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void NativeResultCallback(int requestId, IntPtr json);

    /// <summary><c>void (*)(int32_t kind, const char *json)</c> — json is borrowed.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void NativeEventCallback(int kind, IntPtr json);

    /// <summary>The one JSON object every async iOS call resolves with (Swift
    /// <c>BridgeResult.Wire</c>). Absent keys read as false/null.</summary>
    internal sealed class BridgeReply
    {
        internal bool Ok;
        internal bool Handled;
        internal string Token;
        internal GogUser User;
        internal string Reason;
        internal string Message;
        internal bool Retryable;
    }

    /// <summary>The <c>{handled, user}</c> reply of a launch link or an install handoff.
    /// <see cref="Handled"/> false = nothing to do. Handled with a null <see cref="User"/> is
    /// legal (manual mode, a redelivered link, or a pending Sign in with GOG finishing the
    /// exchange) and must never be reported as a sign-in.</summary>
    internal sealed class IosLaunchOutcome
    {
        internal bool Handled { get; }
        internal GogUser User { get; }

        internal IosLaunchOutcome(bool handled, GogUser user)
        {
            Handled = handled;
            User = user;
        }
    }
}
