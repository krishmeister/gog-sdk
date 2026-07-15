using System.Text.RegularExpressions;
using UnityEngine;

namespace Gog.Sdk
{
    /// <summary>
    /// The single source of GOG SDK configuration (docs/sdk-api-contract.md §1.3). The
    /// SAME asset drives build-time Android manifest injection (the per-game App-Link
    /// intent filter) AND runtime initialization — so the manifest and the runtime can
    /// never disagree about the game id.
    ///
    /// <para>The asset lives in PROJECT space (Assets/GogSdk/Resources/GogSettings.asset),
    /// created via Project Settings → GOG SDK — never inside the read-only package.</para>
    ///
    /// <para>The deep-link host and path are deliberately NOT fields here: they are
    /// constants (link.godofgaming.online, /launch/&lt;GameId&gt;) that must match the
    /// generated manifest and the server's assetlinks.json exactly (§1.3).</para>
    /// </summary>
    public sealed class GogSettings : ScriptableObject
    {
        [SerializeField] private string gameId;
        [SerializeField] private string apiBaseUrl = "https://api.godofgaming.online";
        [SerializeField] private bool autoSignInFromLaunch = true;
        [SerializeField] private string admobAppId = "";

        /// <summary>This game's GOG UUID. Required.</summary>
        public string GameId => gameId;

        /// <summary>API base URL. Overridable for staging; defaults to production.</summary>
        public string ApiBaseUrl => apiBaseUrl;

        /// <summary>When true (default), Initialize() exchanges a launch code with no
        /// integrator code (contract §4, I6).</summary>
        public bool AutoSignInFromLaunch => autoSignInFromLaunch;

        /// <summary>Optional AdMob APP id (contract §1.3) — REQUIRED before enabling an
        /// AdMob-serving mode for this game. Build-time only: the manifest injector writes it
        /// as the com.google.android.gms.ads.APPLICATION_ID meta-data. Blank = no tag, no
        /// AdMob metadata, no crash surface. MUST belong to the same AdMob account as the ad
        /// unit ids configured in the dashboard (account coupling).</summary>
        public string AdmobAppId => admobAppId;

        // Canonical lowercase-Resources name the runtime loads by (also the file name).
        internal const string ResourceName = "GogSettings";

        private static readonly Regex UuidRe = new Regex(
            "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            RegexOptions.Compiled);

        /// <summary>A well-formed GOG game id (a UUID). Shared by the Editor validation,
        /// the build-time gate, and (later) runtime Initialize so "valid" means one thing.</summary>
        public static bool IsValidGameId(string id) => !string.IsNullOrEmpty(id) && UuidRe.IsMatch(id);

        // AdMob APP id shape: ca-app-pub-<16 digits>~<10 digits>. The tilde matters — an ad
        // UNIT id uses a slash, and pasting a unit id where the app id belongs is THE classic
        // mistake. AdMob crashes at launch on a malformed app id, so builds validate this.
        private static readonly Regex AdmobAppIdRe = new Regex(
            "^ca-app-pub-[0-9]{16}~[0-9]{10}$", RegexOptions.Compiled);

        /// <summary>A well-formed AdMob APP id. Shared by the Editor validation, the build
        /// gate, and the manifest injector so "valid" means one thing (mirrors IsValidGameId).</summary>
        public static bool IsValidAdmobAppId(string id) => !string.IsNullOrEmpty(id) && AdmobAppIdRe.IsMatch(id);

        /// <summary>Load the configured settings from Resources, or null if the asset does
        /// not exist yet. The runtime path (S7 calls this); Editor tooling loads the same
        /// asset via AssetDatabase for edit/build-time reliability.</summary>
        public static GogSettings Load() => Resources.Load<GogSettings>(ResourceName);
    }
}
