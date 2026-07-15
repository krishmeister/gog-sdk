using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using NUnit.Framework;
using UnityEditor.Build;

namespace Gog.Sdk.Editor.Tests
{
    /// <summary>
    /// The manifest injector, proven against REAL manifest files on disk (contract §1.3,
    /// S8a gate): the AdMob APPLICATION_ID meta-data lands under &lt;application&gt; exactly
    /// once when AdmobAppId is set, never when blank, never duplicated on re-injection, and
    /// a malformed app id fails the build with an actionable message instead of becoming a
    /// launch crash. Exercises the same InjectInto seam the build callback calls.
    /// </summary>
    public class GogManifestInjectorTests
    {
        private const string GameId = "5ce9b0d3-be97-4a25-bdb2-195d58f4a91f";
        private const string AppId = "ca-app-pub-3940256099942544~3347511713";
        private const string AndroidNs = "http://schemas.android.com/apk/res/android";

        private string _dir;
        private string _manifest;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "gog-injector-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _manifest = Path.Combine(_dir, "AndroidManifest.xml");
            File.WriteAllText(_manifest,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                "<manifest xmlns:android=\"http://schemas.android.com/apk/res/android\">\n" +
                "  <application>\n" +
                "    <activity android:name=\"com.unity3d.player.UnityPlayerGameActivity\">\n" +
                "      <intent-filter>\n" +
                "        <action android:name=\"android.intent.action.MAIN\" />\n" +
                "        <category android:name=\"android.intent.category.LAUNCHER\" />\n" +
                "      </intent-filter>\n" +
                "    </activity>\n" +
                "  </application>\n" +
                "</manifest>\n");
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        // ── helpers ──────────────────────────────────────────────────────────

        private XmlDocument Load()
        {
            var doc = new XmlDocument();
            doc.Load(_manifest);
            return doc;
        }

        /// <summary>All APPLICATION_ID meta-data elements, with their parent element name —
        /// so the tests assert both count and placement (must be &lt;application&gt;).</summary>
        private static List<(string parent, string value)> AdmobMetas(XmlDocument doc)
        {
            var result = new List<(string, string)>();
            foreach (XmlElement md in doc.GetElementsByTagName("meta-data"))
            {
                if (md.GetAttribute("name", AndroidNs) == GogManifestInjector.AdmobAppIdMetaName)
                    result.Add((md.ParentNode?.Name, md.GetAttribute("value", AndroidNs)));
            }
            return result;
        }

        private static int AppLinkFilterCount(XmlDocument doc)
        {
            int n = 0;
            foreach (XmlElement data in doc.GetElementsByTagName("data"))
                if (data.GetAttribute("host", AndroidNs) == "link.godofgaming.online") n++;
            return n;
        }

        // ── gate 3a: set → exactly one tag, right value, inside <application> ──

        [Test]
        public void AppIdSet_InjectsExactlyOneMetaData_UnderApplication()
        {
            GogManifestInjector.InjectInto(_manifest, GameId, AppId);

            var metas = AdmobMetas(Load());
            Assert.AreEqual(1, metas.Count, "exactly one APPLICATION_ID meta-data");
            Assert.AreEqual("application", metas[0].parent,
                "the meta-data must sit under <application>, not an activity/intent-filter");
            Assert.AreEqual(AppId, metas[0].value);
            Assert.AreEqual(1, AppLinkFilterCount(Load()), "the App-Link filter still injects");
        }

        // ── gate 3b: blank → nothing (the safe state) ──

        [Test]
        public void AppIdBlank_InjectsNoMetaData()
        {
            foreach (var blank in new[] { null, "", "   " })
            {
                SetUpFreshManifest();
                GogManifestInjector.InjectInto(_manifest, GameId, blank);
                Assert.AreEqual(0, AdmobMetas(Load()).Count,
                    $"blank AdmobAppId ('{blank ?? "null"}') must inject NOTHING");
                Assert.AreEqual(1, AppLinkFilterCount(Load()),
                    "the App-Link filter injects regardless of AdmobAppId");
            }
        }

        // ── gate 3c: run twice → no duplicates (idempotent), value refreshes ──

        [Test]
        public void RunTwice_NoDuplicateTag_AndValueRefreshes()
        {
            GogManifestInjector.InjectInto(_manifest, GameId, AppId);
            GogManifestInjector.InjectInto(_manifest, GameId, AppId);

            var metas = AdmobMetas(Load());
            Assert.AreEqual(1, metas.Count, "re-injection must not duplicate the tag");
            Assert.AreEqual(1, AppLinkFilterCount(Load()), "filter idempotency intact too");

            // A changed app id between builds must take effect (upsert, not skip).
            const string changed = "ca-app-pub-1111111111111111~2222222222";
            GogManifestInjector.InjectInto(_manifest, GameId, changed);
            metas = AdmobMetas(Load());
            Assert.AreEqual(1, metas.Count);
            Assert.AreEqual(changed, metas[0].value, "an existing tag's value refreshes on rebuild");
        }

        // ── gate 3d: malformed → build FAILS with the actionable message ──

        [Test]
        public void MalformedAppId_FailsBuild_WithActionableMessage()
        {
            // The classic mistake first: an ad UNIT id (slash) where the APP id belongs.
            foreach (var bad in new[]
                     {
                         "ca-app-pub-3940256099942544/1033173712", // unit id, not app id
                         "ca-app-pub-394025609994254~3347511713",  // 15-digit publisher
                         "banana",
                     })
            {
                SetUpFreshManifest();
                var ex = Assert.Throws<BuildFailedException>(
                    () => GogManifestInjector.InjectInto(_manifest, GameId, bad),
                    $"malformed '{bad}' must fail the build");
                StringAssert.Contains("not a valid AdMob APP id", ex.Message);
                StringAssert.Contains("Project Settings", ex.Message);
                Assert.AreEqual(0, AdmobMetas(Load()).Count, "nothing may be injected on failure");
            }
        }

        // Blank-GameId defense-in-depth still holds after the refactor.
        [Test]
        public void InvalidGameId_StillFailsBuild()
        {
            Assert.Throws<BuildFailedException>(
                () => GogManifestInjector.InjectInto(_manifest, "not-a-uuid", AppId));
        }

        private void SetUpFreshManifest()
        {
            TearDown();
            SetUp();
        }
    }
}
