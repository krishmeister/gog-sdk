using System;
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
