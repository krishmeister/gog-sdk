using System.IO;
using System.Xml;
using UnityEditor.Android;
using UnityEditor.Build;

namespace Gog.Sdk.Editor
{
    /// <summary>
    /// Injects the per-game Android manifest entries at build time, reading from the SAME
    /// GogSettings asset the runtime reads — so the manifest and the runtime can never
    /// disagree (docs/sdk-api-contract.md §1.3). Two injections:
    ///
    /// <list type="bullet">
    /// <item>The App-Link intent filter (link.godofgaming.online/launch/&lt;GameId&gt;) on
    /// the LAUNCHER activity.</item>
    /// <item>The AdMob APPLICATION_ID &lt;meta-data&gt; under &lt;application&gt; — ONLY when
    /// <c>AdmobAppId</c> is set. Blank injects NOTHING: a house-only game carries no AdMob
    /// metadata and cannot crash on it (§1.3). The Android manifest merger propagates a
    /// library-level &lt;application&gt;&lt;meta-data&gt; into the final APK manifest.</item>
    /// </list>
    ///
    /// Merge, never clobber: it appends/updates exactly its own elements and leaves every
    /// other element (including a dev's own manifest entries) untouched. Re-injection is
    /// idempotent — the filter is added at most once (keyed by host+pathPrefix) and the
    /// meta-data is UPSERTED (keyed by android:name; the value refreshes on rebuild, so a
    /// changed AdmobAppId takes effect without manual cleanup). The LAUNCHER activity is
    /// found by its category, never by class name, so it survives Unity changing its
    /// launcher activity.
    /// </summary>
    internal sealed class GogManifestInjector : IPostGenerateGradleAndroidProject
    {
        public int callbackOrder => 100;

        private const string AndroidNs = "http://schemas.android.com/apk/res/android";
        private const string LinkHost = "link.godofgaming.online";
        internal const string AdmobAppIdMetaName = "com.google.android.gms.ads.APPLICATION_ID";

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            // Unity passes the unityLibrary module dir; its manifest holds the launcher
            // activity (S5 discovery: the launcher module's own manifest is a 458 B shell).
            var manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(manifestPath))
                throw new BuildFailedException("GOG SDK: manifest not found at " + manifestPath);

            var settings = GogSettingsProvider.LoadOrNull();
            InjectInto(
                manifestPath,
                settings != null ? settings.GameId : null,
                settings != null ? settings.AdmobAppId : null);
        }

        // The injection itself, seamed out (same pattern as MapReason/VersionHandshakeError)
        // so the Editor tests exercise the REAL manifest mutation against real files.
        internal static void InjectInto(string manifestPath, string gameId, string admobAppId)
        {
            if (!GogSettings.IsValidGameId(gameId))
            {
                // Defense in depth — the build validator already fails earlier, but never
                // emit pathPrefix="/launch/" if we somehow got here.
                throw new BuildFailedException(
                    "GOG SDK: Game ID is missing or not a UUID; refusing to write a broken " +
                    "App-Link filter. Set it in Project Settings → GOG SDK.");
            }

            // AdmobAppId: blank = valid safe state (inject nothing). Non-blank must be
            // well-formed — defense in depth behind the validator's earlier, louder failure —
            // because AdMob crashes at launch on a malformed app id.
            var admobTrim = admobAppId != null ? admobAppId.Trim() : string.Empty;
            if (admobTrim.Length > 0 && !GogSettings.IsValidAdmobAppId(admobTrim))
            {
                throw new BuildFailedException(
                    "GOG SDK: AdmobAppId '" + admobTrim + "' is not a valid AdMob APP id " +
                    "(expected ca-app-pub-<16 digits>~<10 digits>). Note: an AD UNIT id " +
                    "(contains '/') is not an app id. Fix it in Project Settings → GOG SDK.");
            }

            var pathPrefix = "/launch/" + gameId;

            var doc = new XmlDocument();
            doc.Load(manifestPath);

            var launcher = FindLauncherActivity(doc);
            if (launcher == null)
                throw new BuildFailedException(
                    "GOG SDK: could not find the LAUNCHER activity in " + manifestPath);

            if (!HasOurFilter(launcher, pathPrefix))
                launcher.AppendChild(BuildFilter(doc, pathPrefix));

            if (admobTrim.Length > 0)
                UpsertAdmobAppId(doc, manifestPath, admobTrim);

            doc.Save(manifestPath);
        }

        // Upsert the AdMob APPLICATION_ID meta-data under <application>. Keyed by
        // android:name: an existing tag gets its value refreshed (idempotent across rebuilds,
        // correct when the operator changes the id); otherwise exactly one tag is appended.
        private static void UpsertAdmobAppId(XmlDocument doc, string manifestPath, string appId)
        {
            var application = doc.GetElementsByTagName("application").Count > 0
                ? doc.GetElementsByTagName("application")[0] as XmlElement
                : null;
            if (application == null)
                throw new BuildFailedException(
                    "GOG SDK: could not find <application> in " + manifestPath);

            foreach (XmlElement md in application.GetElementsByTagName("meta-data"))
            {
                if (md.GetAttribute("name", AndroidNs) == AdmobAppIdMetaName)
                {
                    md.SetAttribute("value", AndroidNs, appId);
                    return;
                }
            }

            var el = doc.CreateElement("meta-data");
            el.SetAttribute("name", AndroidNs, AdmobAppIdMetaName);
            el.SetAttribute("value", AndroidNs, appId);
            application.AppendChild(el);
        }

        // The <activity> whose intent-filter carries category LAUNCHER.
        private static XmlElement FindLauncherActivity(XmlDocument doc)
        {
            foreach (XmlElement activity in doc.GetElementsByTagName("activity"))
            {
                foreach (XmlElement filter in activity.GetElementsByTagName("intent-filter"))
                {
                    foreach (XmlElement category in filter.GetElementsByTagName("category"))
                    {
                        if (category.GetAttribute("name", AndroidNs) ==
                            "android.intent.category.LAUNCHER")
                            return activity;
                    }
                }
            }
            return null;
        }

        private static bool HasOurFilter(XmlElement activity, string pathPrefix)
        {
            foreach (XmlElement filter in activity.GetElementsByTagName("intent-filter"))
            {
                foreach (XmlElement data in filter.GetElementsByTagName("data"))
                {
                    if (data.GetAttribute("host", AndroidNs) == LinkHost &&
                        data.GetAttribute("pathPrefix", AndroidNs) == pathPrefix)
                        return true;
                }
            }
            return false;
        }

        private static XmlElement BuildFilter(XmlDocument doc, string pathPrefix)
        {
            var filter = doc.CreateElement("intent-filter");
            filter.SetAttribute("autoVerify", AndroidNs, "true");

            filter.AppendChild(MakeNamed(doc, "action", "android.intent.action.VIEW"));
            filter.AppendChild(MakeNamed(doc, "category", "android.intent.category.DEFAULT"));
            filter.AppendChild(MakeNamed(doc, "category", "android.intent.category.BROWSABLE"));

            var data = doc.CreateElement("data");
            data.SetAttribute("scheme", AndroidNs, "https");
            data.SetAttribute("host", AndroidNs, LinkHost);
            data.SetAttribute("pathPrefix", AndroidNs, pathPrefix);
            filter.AppendChild(data);
            return filter;
        }

        private static XmlElement MakeNamed(XmlDocument doc, string tag, string name)
        {
            var el = doc.CreateElement(tag);
            el.SetAttribute("name", AndroidNs, name);
            return el;
        }
    }
}
