using System;
using System.Collections;
using System.Runtime.InteropServices;
using Gog.Sdk;
using Gog.Sdk.Internal;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Gog.Sdk.Tests
{
    /// <summary>
    /// The C# half of the iOS seam (<see cref="IosAuthBridge"/>) — everything that does not need
    /// the native core: the reply parsing, the error-token table, the UTF-8 marshalling and the
    /// install-handoff entry point's off-iOS behaviour. The P/Invoke calls themselves only exist in
    /// an iOS player build; Gog-ios-sdk's UnityBridgeTests pins the native side of the same table.
    /// </summary>
    public class IosAuthBridgeTests
    {
        [SetUp]
        public void SetUp() => GogSdk.Shutdown();

        [TearDown]
        public void TearDown() => GogSdk.Shutdown();

        // COMPLETENESS PROOF (contract §5), iOS edition. Every token BridgeResult.reason(for:) in
        // GogUnityBridge.swift can emit, plus the bridge's own "not_initialized", maps EXPLICITLY.
        // Anyone adding a token on the Swift side adds a row here AND a case in MapReason.
        [Test]
        public void NoKnownIosReasonFallsThroughToInternalError()
        {
            var cases = new (string reason, bool nativeRetryable, bool exchange, GogErrorCode code, bool retryable)[]
            {
                ("network",                 true,  true,  GogErrorCode.NetworkError,         false), // exchange: code likely burned
                ("network",                 true,  false, GogErrorCode.NetworkError,         true),  // token refresh: transport says retry
                ("network",                 false, false, GogErrorCode.NetworkError,         false), // ...and the transport can say no
                ("malformed_response",      false, true,  GogErrorCode.NetworkError,         false),
                ("malformed_response",      false, false, GogErrorCode.NetworkError,         true),
                ("unauthorized",            false, false, GogErrorCode.Unauthorized,         false),
                ("account_banned",          false, false, GogErrorCode.Unauthorized,         false),
                ("rate_limited",            false, false, GogErrorCode.RateLimited,          true),
                ("insufficient_funds",      false, false, GogErrorCode.InsufficientFunds,    false),
                ("earn_too_large",          false, false, GogErrorCode.EarnTooLarge,         false),
                ("idempotency_key_reused",  false, false, GogErrorCode.IdempotencyKeyReused, false),
                ("invalid_input",           false, false, GogErrorCode.InvalidRequest,       false),
                ("city_required",           false, false, GogErrorCode.InvalidRequest,       false),
                ("unknown_game",            false, true,  GogErrorCode.UnknownGame,          false),
                ("invalid_or_expired_code", false, true,  GogErrorCode.InvalidOrExpiredCode, false),
                ("sign_in_cancelled",       false, true,  GogErrorCode.SignInCancelled,      true),
                ("hub_not_installed",       false, true,  GogErrorCode.HubNotInstalled,      false),
                ("sign_in_in_progress",     false, true,  GogErrorCode.SignInInProgress,     true),
                ("launch_link_invalid",     false, true,  GogErrorCode.InvalidRequest,       false),
                ("not_configured",          false, true,  GogErrorCode.InvalidRequest,       false),
                ("not_initialized",         false, true,  GogErrorCode.NotInitialized,       false),
            };

            foreach (var c in cases)
            {
                GogError e = IosAuthBridge.MapReason(c.reason, "native detail", c.nativeRetryable, c.exchange);
                Assert.AreNotEqual(GogErrorCode.InternalError, e.Code,
                    $"KNOWN iOS reason '{c.reason}' fell through to InternalError");
                Assert.AreEqual(c.code, e.Code, $"reason '{c.reason}' code");
                Assert.AreEqual(c.retryable, e.Retryable,
                    $"reason '{c.reason}' (native={c.nativeRetryable}, exchange={c.exchange}) retryable");
                Assert.AreEqual(c.reason, e.RawReason, $"reason '{c.reason}' raw preserved");
                StringAssert.Contains("native detail", e.Message, $"reason '{c.reason}' keeps the native message");
            }
        }

        // "server" is a KNOWN token that maps to InternalError by design (5xx, as in GogHttp), and
        // "internal" is native's own unexpected-error token. Pinned separately so the
        // completeness table above can keep its "never InternalError" assertion.
        [Test]
        public void ServerAndInternalTokens_AreInternalError_ByDesign()
        {
            GogError server = IosAuthBridge.MapReason("server", null, false, exchange: false);
            Assert.AreEqual(GogErrorCode.InternalError, server.Code);
            Assert.IsTrue(server.Retryable, "a 5xx on a refresh is retryable, as in GogHttp");
            Assert.IsFalse(IosAuthBridge.MapReason("server", null, false, exchange: true).Retryable,
                "a 5xx on a code exchange may have spent the code");

            GogError native = IosAuthBridge.MapReason("internal", null, false, exchange: true);
            Assert.AreEqual(GogErrorCode.InternalError, native.Code);
            Assert.IsFalse(native.Retryable);
        }

        [Test]
        public void UnknownIosReason_IsInternalError_RawPreserved_NotRetryable()
        {
            GogError e = IosAuthBridge.MapReason("some_future_token_x9", null, true, exchange: false);
            Assert.AreEqual(GogErrorCode.InternalError, e.Code);
            Assert.AreEqual("some_future_token_x9", e.RawReason);
            Assert.IsFalse(e.Retryable, "an unrecognised reason must not claim to be retryable");
        }

        // ── reply parsing: the exact shapes BridgeResult encodes ─────────────────

        [Test]
        public void ParseReply_UserSuccess()
        {
            BridgeReply r = IosAuthBridge.ParseReply("{\"ok\":true,\"user\":{\"id\":\"u1\",\"email\":\"a@b.c\"}}");
            Assert.IsTrue(r.Ok);
            Assert.IsNotNull(r.User);
            Assert.AreEqual("u1", r.User.Id);
            Assert.AreEqual("a@b.c", r.User.Email);

            GogResult<GogUser> result = IosAuthBridge.ToUserResult(r, "signin");
            Assert.IsTrue(result.Ok);
            Assert.AreEqual("u1", result.Value.Id);
        }

        // Swift's JSONEncoder omits a nil email and escapes "/" — both must read cleanly.
        [Test]
        public void ParseReply_UserWithoutEmail_AndEscapedSlashes()
        {
            BridgeReply r = IosAuthBridge.ParseReply("{\"ok\":true,\"user\":{\"id\":\"u\\/2\"}}");
            Assert.AreEqual("u/2", r.User.Id);
            Assert.IsNull(r.User.Email);
        }

        [Test]
        public void ParseReply_Token()
        {
            BridgeReply r = IosAuthBridge.ParseReply("{\"ok\":true,\"token\":\"tok-1\"}");
            GogResult<string> result = IosAuthBridge.ToTokenResult(r);
            Assert.IsTrue(result.Ok);
            Assert.AreEqual("tok-1", result.Value);
        }

        [Test]
        public void ParseReply_Failure_MapsThroughTheTable()
        {
            BridgeReply r = IosAuthBridge.ParseReply(
                "{\"ok\":false,\"reason\":\"invalid_or_expired_code\",\"message\":\"invalidOrExpiredCode\",\"retryable\":false}");
            Assert.IsFalse(r.Ok);

            GogResult<GogUser> result = IosAuthBridge.ToUserResult(r, "signin");
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(GogErrorCode.InvalidOrExpiredCode, result.Error.Code);
            Assert.AreEqual("invalid_or_expired_code", result.Error.RawReason);
        }

        [Test]
        public void ParseReply_HubNotInstalled_IsHubNotInstalled()
        {
            BridgeReply r = IosAuthBridge.ParseReply(
                "{\"ok\":false,\"reason\":\"hub_not_installed\",\"message\":\"hubNotInstalled\",\"retryable\":false}");
            GogResult<GogUser> result = IosAuthBridge.ToUserResult(r, "hub");
            Assert.AreEqual(GogErrorCode.HubNotInstalled, result.Error.Code,
                "game scripts branch on HubNotInstalled — same code as Android");
        }

        [Test]
        public void ParseReply_SuccessWithoutUser_IsInternalError_NotASilentSuccess()
        {
            GogResult<GogUser> result = IosAuthBridge.ToUserResult(IosAuthBridge.ParseReply("{\"ok\":true}"), "hub");
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(GogErrorCode.InternalError, result.Error.Code);
            Assert.AreEqual("hub_no_user", result.Error.RawReason);

            GogResult<string> token = IosAuthBridge.ToTokenResult(IosAuthBridge.ParseReply("{\"ok\":true}"));
            Assert.IsFalse(token.Ok, "a token reply without a token is never a success");
        }

        [Test]
        public void ParseReply_Launch_HandledWithAndWithoutUser_AndNotHandled()
        {
            GogResult<IosLaunchOutcome> withUser = IosAuthBridge.ToLaunchResult(IosAuthBridge.ParseReply(
                "{\"ok\":true,\"handled\":true,\"user\":{\"id\":\"u1\",\"email\":\"a@b.c\"}}"));
            Assert.IsTrue(withUser.Ok);
            Assert.IsTrue(withUser.Value.Handled);
            Assert.AreEqual("u1", withUser.Value.User.Id);

            // Legal: a pending Sign in with GOG finishes this exchange and owns the user.
            GogResult<IosLaunchOutcome> noUser = IosAuthBridge.ToLaunchResult(IosAuthBridge.ParseReply(
                "{\"ok\":true,\"handled\":true}"));
            Assert.IsTrue(noUser.Ok);
            Assert.IsTrue(noUser.Value.Handled);
            Assert.IsNull(noUser.Value.User);

            GogResult<IosLaunchOutcome> notHandled = IosAuthBridge.ToLaunchResult(IosAuthBridge.ParseReply(
                "{\"ok\":true,\"handled\":false}"));
            Assert.IsTrue(notHandled.Ok);
            Assert.IsFalse(notHandled.Value.Handled);
        }

        [Test]
        public void ParseReply_Unreadable_IsAFailure_NeverASuccess()
        {
            foreach (string bad in new[] { null, "", "not json", "[1,2]", "{\"ok\":" })
            {
                BridgeReply r = IosAuthBridge.ParseReply(bad);
                Assert.IsFalse(r.Ok, $"'{bad}' must not parse as a success");
                GogResult<GogUser> result = IosAuthBridge.ToUserResult(r, "signin");
                Assert.AreEqual(GogErrorCode.InternalError, result.Error.Code);
                Assert.IsFalse(result.Error.Retryable);
            }
        }

        [Test]
        public void ParseUserJson_BareObject_OrNull()
        {
            GogUser user = IosAuthBridge.ParseUserJson("{\"id\":\"u1\",\"email\":\"a@b.c\"}");
            Assert.AreEqual("u1", user.Id);
            Assert.IsNull(IosAuthBridge.ParseUserJson(null), "NULL from native = signed out");
            Assert.IsNull(IosAuthBridge.ParseUserJson("{}"), "no id = no user");
            Assert.IsNull(IosAuthBridge.ParseUserJson("garbage"));
        }

        // ── UTF-8 across the seam ────────────────────────────────────────────────

        [Test]
        public void Utf8_RoundTrips_NonAscii_AndIsNulTerminated()
        {
            const string text = "https://link.godofgaming.online/launch/x#code=é✓😀";
            byte[] bytes = IosAuthBridge.Utf8Z(text);
            Assert.AreEqual(0, bytes[bytes.Length - 1], "must be NUL-terminated for the C side");

            IntPtr native = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, native, bytes.Length);
                Assert.AreEqual(text, IosAuthBridge.ReadUtf8(native));
            }
            finally
            {
                Marshal.FreeHGlobal(native);
            }

            Assert.IsNull(IosAuthBridge.ReadUtf8(IntPtr.Zero));
            Assert.AreEqual(new byte[] { 0 }, IosAuthBridge.Utf8Z(null), "null goes down as an empty C string");
        }

        [Test]
        public void VersionHandshake_iOS_MatchIsNull_MismatchNamesBothVersionsAndTheFramework()
        {
            Assert.IsNull(IosAuthBridge.VersionHandshakeError("0.3.0", "0.3.0"));

            string msg = IosAuthBridge.VersionHandshakeError("0.3.0", "0.0.0-unstamped");
            Assert.IsNotNull(msg);
            StringAssert.Contains("0.3.0", msg);
            StringAssert.Contains("0.0.0-unstamped", msg);
            StringAssert.Contains("GOG.xcframework", msg);
            StringAssert.Contains("Reinstall the package", msg);
        }

        // ── the public install-handoff entry, off iOS ────────────────────────────

        [Test]
        public void InstallHandoff_NullCallback_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => GogAuth.TrySignInFromInstallHandoff(null));
        }

        [Test]
        public void InstallHandoff_BeforeInitialize_IsNotInitialized()
        {
            GogResult<bool> result = default;
            bool done = false;
            GogAuth.TrySignInFromInstallHandoff(r => { result = r; done = true; });

            Assert.IsTrue(done, "the pre-init answer is synchronous");
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(GogErrorCode.NotInitialized, result.Error.Code);
        }

        [UnityTest]
        public IEnumerator InstallHandoff_OffIos_IsOkFalse_AndRaisesNothing()
        {
            Assert.AreNotEqual(RuntimePlatform.IPhonePlayer, Application.platform, "precondition: off-device");
            GogSdk.Initialize();

            int events = 0;
            GogAuth.OnSignedIn += _ => events++;
            GogAuth.OnLaunchSignInFailed += _ => events++;

            GogResult<bool> result = default;
            bool done = false;
            GogAuth.TrySignInFromInstallHandoff(r => { result = r; done = true; });
            yield return null;

            Assert.IsTrue(done);
            Assert.IsTrue(result.Ok, "off iOS there is never a handoff — a success, not an error");
            Assert.IsFalse(result.Value);
            Assert.AreEqual(0, events, "nothing happened, so no event fires");
        }
    }
}
