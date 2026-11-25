using System.Collections.Generic;
using NUnit.Framework;

namespace Gog.Sdk.Editor.Tests
{
    /// <summary>
    /// The pure half of the iOS post-process (<see cref="GogIosBuildSupport"/>): what goes into the
    /// entitlements and the deployment-target gate. The Xcode-project mutation itself needs the
    /// iOS build module and a real export, and is verified on a Mac.
    /// </summary>
    public class GogIosBuildSupportTests
    {
        private const string Own = "$(AppIdentifierPrefix)com.godofgaming.tennis";
        private const string Shared = "$(AppIdentifierPrefix)online.godofgaming.shared";

        // 🔴 The security property: the app's OWN group is first (the default for every Keychain
        // write), the shared group second. Reversed, every game's session is team-readable.
        [Test]
        public void KeychainGroups_OwnGroupFirst_SharedSecond()
        {
            List<string> groups = GogIosBuildSupport.KeychainGroups(null, "com.godofgaming.tennis");
            CollectionAssert.AreEqual(new[] { Own, Shared }, groups);
        }

        [Test]
        public void KeychainGroups_ExistingEntriesKeptAfterOurs_NoDuplicates()
        {
            List<string> groups = GogIosBuildSupport.KeychainGroups(
                new[] { Shared, "$(AppIdentifierPrefix)com.studio.extra", Own },
                "com.godofgaming.tennis");
            CollectionAssert.AreEqual(new[] { Own, Shared, "$(AppIdentifierPrefix)com.studio.extra" }, groups,
                "own first even when an existing file listed the shared group first; nothing duplicated or lost");
        }

        [Test]
        public void AssociatedDomains_AddsLaunchDomain_KeepsTheGamesOwn()
        {
            List<string> domains = GogIosBuildSupport.AssociatedDomains(new[] { "applinks:studio.example" });
            CollectionAssert.AreEqual(
                new[] { "applinks:studio.example", "applinks:link.godofgaming.online" }, domains);

            List<string> again = GogIosBuildSupport.AssociatedDomains(domains);
            CollectionAssert.AreEqual(domains, again, "re-running the post-process is idempotent");
        }

        [Test]
        public void AssociatedDomains_FromNothing_IsJustTheLaunchDomain()
        {
            CollectionAssert.AreEqual(new[] { "applinks:link.godofgaming.online" },
                GogIosBuildSupport.AssociatedDomains(null));
        }

        [TestCase("17.4", true)]
        [TestCase("17.5", true)]
        [TestCase("18", true)]
        [TestCase("18.0.1", true)]
        [TestCase("17.3", false)]
        [TestCase("13.0", false)]
        [TestCase("17", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        [TestCase("not-a-version", false)]
        public void MeetsMinimumIos(string target, bool expected)
        {
            Assert.AreEqual(expected, GogIosBuildSupport.MeetsMinimumIos(target));
        }
    }
}
