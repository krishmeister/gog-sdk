using System;
using Gog.Sdk.Internal;
using UnityEngine;

namespace Gog.Sdk
{
    /// <summary>
    /// Phase-1 inbound App Link handling: turns a verified deep link from the GOG hub
    /// (e.g. <c>https://gog-api.fly.dev/launch?code=&lt;sso_code&gt;</c>) into a signed-in
    /// player by extracting the one-time code and driving the PROVEN launch sign-in path.
    /// Outcomes surface through <see cref="GogAuth.OnSignedIn"/> (success) and
    /// <see cref="GogAuth.OnLaunchSignInFailed"/> (failure) — this is an unsolicited
    /// transition, so it travels on events, not a callback (contract §1.1).
    ///
    /// <para>This is pure Unity glue; the Kotlin/.aar core is untouched. The link reaches us
    /// via Unity's engine-native deep-link surface (cold start:
    /// <see cref="Application.absoluteURL"/>; warm start:
    /// <see cref="Application.deepLinkActivated"/>).</para>
    ///
    /// <para>Call <see cref="Enable"/> once after <see cref="GogSdk.Initialize"/>, on the
    /// main thread. (The host/path stay <c>gog-api.fly.dev/launch</c> this slice; the flip to
    /// <c>link.godofgaming.online</c> is S7.7.)</para>
    ///
    /// <para>iOS: the same two Unity entry points deliver the game's Universal Links, and the
    /// native iOS core validates and exchanges them (<c>link.godofgaming.online/launch/&lt;Game
    /// ID&gt;</c>); <see cref="Host"/>/<see cref="PathPrefix"/>/<see cref="CodeParam"/> apply to
    /// Android only. On iOS this is also the return leg of <see cref="GogAuth.SignInWithGog"/>,
    /// so it must be enabled there.</para>
    /// </summary>
    public static class GogDeepLink
    {
        /// <summary>App Link host the hub deep-links to. Must match the manifest intent
        /// filter's <c>android:host</c> and the assetlinks.json host.</summary>
        public static string Host = "gog-api.fly.dev";

        /// <summary>App Link path prefix. Must match the manifest intent filter's
        /// <c>android:pathPrefix</c>.</summary>
        public static string PathPrefix = "/launch";

        /// <summary>Query parameter carrying the one-time SSO code.</summary>
        public static string CodeParam = "code";

        /// <summary>Fired when a matching link is caught and the exchange is about to start —
        /// handy for a "Signing in…" UI. The outcome arrives via
        /// <see cref="GogAuth.OnSignedIn"/> / <see cref="GogAuth.OnLaunchSignInFailed"/>.</summary>
        public static event Action OnSignInStarted;

        private static bool _enabled;
        private static string _lastHandledUrl;

        // Read by GogAuth.SignInWithGog on iOS, whose return leg is a universal link.
        internal static bool IsEnabled => _enabled;

        /// <summary>Start catching inbound App Links. Idempotent. Call once on the main
        /// thread after <see cref="GogSdk.Initialize"/>. Immediately processes the
        /// cold-start launch URL if the app was opened by a link.</summary>
        public static void Enable()
        {
            if (_enabled) return;
            _enabled = true;
            Application.deepLinkActivated += HandleUrl;          // warm start
            if (!string.IsNullOrEmpty(Application.absoluteURL))  // cold start
                HandleUrl(Application.absoluteURL);
        }

        /// <summary>Stop catching inbound App Links.</summary>
        public static void Disable()
        {
            if (!_enabled) return;
            Application.deepLinkActivated -= HandleUrl;
            _enabled = false;
        }

        /// <summary>Feed a launch URL directly (used by <see cref="Enable"/>; also callable
        /// for tests). Extracts the code and drives the exchange iff the URL matches our
        /// verified launch link.</summary>
        public static void HandleUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            // Dedupe: the cold-start URL persists in Application.absoluteURL and can also
            // re-arrive via deepLinkActivated — a single-use code must exchange only once.
            // A repeat is expected, not a misconfiguration, so it stays silent.
            if (url == _lastHandledUrl) return;

            if (IosAuthBridge.IsActivePlatform)
            {
                HandleIosUrl(url);
                return;
            }

            var code = ExtractCode(url);
            if (string.IsNullOrEmpty(code))
            {
                // Previously a silent return. A misconfigured launch link (wrong host/path,
                // or a missing code) vanished without a trace — the single hardest failure to
                // diagnose in the field. Make it loud.
                Debug.LogWarning(
                    $"[GOG SDK] Ignoring a deep link that is not a valid GOG launch link: \"{url}\". " +
                    $"Expected https://{Host}{PathPrefix}...?{CodeParam}=<code>. If this IS the launch " +
                    "link, the manifest host/pathPrefix or assetlinks.json is misconfigured.");
                return;
            }

            _lastHandledUrl = url;
            OnSignInStarted?.Invoke();
            GogAuth.SignInFromLaunch(code); // launch path → OnSignedIn / OnLaunchSignInFailed
        }

        // iOS: the native core owns the launch-link format — host, /launch/<Game ID>, and where
        // the code rides — so the Host/PathPrefix/CodeParam knobs above are not consulted. The
        // synchronous check comes first so OnSignInStarted fires only for a link we will act on.
        private static void HandleIosUrl(string url)
        {
            if (!IosAuthBridge.IsLaunchLink(url))
            {
                Debug.LogWarning(
                    $"[GOG SDK] Ignoring a universal link that is not this game's GOG launch link: \"{url}\". " +
                    "Expected https://link.godofgaming.online/launch/<your Game ID> carrying a code. If this IS " +
                    "the launch link, the Game ID in Project Settings → GOG SDK does not match it (or " +
                    "GogSdk.Initialize() has not run yet).");
                return;
            }

            _lastHandledUrl = url;
            OnSignInStarted?.Invoke();
            GogAuth.SignInFromLaunchUrl(url); // native validates + exchanges → OnSignedIn / OnLaunchSignInFailed
        }

        /// <summary>Parse <c>https://{Host}{PathPrefix}…?{CodeParam}=&lt;code&gt;</c> and
        /// return the decoded code, or null if the URL isn't our verified launch link.</summary>
        internal static string ExtractCode(string url)
        {
            Uri uri;
            try { uri = new Uri(url); }
            catch { return null; }

            if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)) return null;
            if (!string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase)) return null;
            if (!uri.AbsolutePath.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase)) return null;

            var query = uri.Query.TrimStart('?');
            if (query.Length == 0) return null;
            foreach (var pair in query.Split('&'))
            {
                var kv = pair.Split(new[] { '=' }, 2);
                if (kv.Length == 2 && string.Equals(kv[0], CodeParam, StringComparison.Ordinal))
                {
                    var value = Uri.UnescapeDataString(kv[1]).Trim();
                    return value.Length == 0 ? null : value;
                }
            }
            return null;
        }

        // Reset to pre-Enable state for Shutdown()/domain-reload: unsubscribe (safe even if
        // never subscribed), drop the dedupe marker and any subscribers (contract §1.3).
        internal static void ResetState()
        {
            Application.deepLinkActivated -= HandleUrl;
            _enabled = false;
            _lastHandledUrl = null;
            OnSignInStarted = null;
        }
    }
}
