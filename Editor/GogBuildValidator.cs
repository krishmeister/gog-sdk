using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace Gog.Sdk.Editor
{
    /// <summary>
    /// Fails an Android build BEFORE the long Gradle export if the Game ID is missing or
    /// not a UUID. Failing loudly here beats shipping a broken App-Link filter (or spending
    /// minutes on a Gradle export that the injector would then have to abort).
    /// </summary>
    internal sealed class GogBuildValidator : IPreprocessBuildWithReport
    {
        // Low — run early so we fail before other preprocessors do expensive work.
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.Android) return; // no-op off Android

            var settings = GogSettingsProvider.LoadOrNull();
            if (settings == null || !GogSettings.IsValidGameId(settings.GameId))
            {
                throw new BuildFailedException(
                    "GOG SDK: set a valid Game ID in Project Settings → GOG SDK before building.");
            }

            // AdmobAppId is OPTIONAL (blank = no AdMob metadata, the safe state) but a
            // non-blank malformed value fails HERE, loudly — AdMob crashes at launch on a
            // malformed app id, and a launch crash the partner debugs blind is exactly what
            // this validator exists to prevent (same principle as the GameId check).
            var admobAppId = settings.AdmobAppId != null ? settings.AdmobAppId.Trim() : "";
            if (admobAppId.Length > 0 && !GogSettings.IsValidAdmobAppId(admobAppId))
            {
                throw new BuildFailedException(
                    "GOG SDK: AdmobAppId '" + admobAppId + "' is not a valid AdMob APP id " +
                    "(expected ca-app-pub-<16 digits>~<10 digits>, e.g. " +
                    "ca-app-pub-3940256099942544~3347511713). Note: an AD UNIT id (contains " +
                    "'/') is not an app id. Fix it in Project Settings → GOG SDK. AdMob " +
                    "crashes at launch on a malformed app id — failing the build instead.");
            }
        }
    }
}
