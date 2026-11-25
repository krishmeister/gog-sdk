using System;
using System.Collections.Generic;
using System.IO;
#if UNITY_IOS
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using UnityEditor.iOS.Xcode.Extensions;
using UnityEngine;
#endif

namespace Gog.Sdk.Editor
{
    /// <summary>
    /// The pure half of the iOS build support: what the entitlements must say and where the
    /// native core comes from. Free of <c>UnityEditor.iOS.Xcode</c>, so it compiles whatever the
    /// active build target is and the Editor tests exercise it directly (same seam pattern as
    /// <c>GogManifestInjector.InjectInto</c>).
    /// </summary>
    internal static class GogIosBuildSupport
    {
        internal const string KeychainGroupsKey = "keychain-access-groups";
        internal const string AssociatedDomainsKey = "com.apple.developer.associated-domains";
        internal const string AppIdentifierPrefix = "$(AppIdentifierPrefix)";

        /// <summary>The team-shared Keychain group the hub writes the install handoff into
        /// (Gog-ios-sdk <c>InstallHandoff.swift</c>).</summary>
        internal const string SharedKeychainGroup = "online.godofgaming.shared";

        /// <summary>Each game's launch links — and, on iOS, the hub's "Sign in with GOG" return —
        /// arrive as Universal Links on this host (Gog-ios-sdk <c>GogHosts.link</c>).</summary>
        internal const string LaunchLinkDomain = "applinks:link.godofgaming.online";

        /// <summary>GOG.xcframework's deployment floor (Gog-ios-sdk Package.swift /
        /// build-xcframework.sh). An app below it links, installs on older iOS, and dies at launch
        /// when dyld cannot bind the framework's iOS 17.4 symbols.</summary>
        internal const string MinimumIosVersion = "17.4";

        internal const string PackageName = "com.gog.sdk";
        internal const string XcframeworkName = "GOG.xcframework";

        /// <summary>
        /// The <c>keychain-access-groups</c> to write: the app's OWN group first, the shared group
        /// second, then anything the project already declared, in its order.
        ///
        /// <para>🔴 ORDER IS THE SECURITY PROPERTY. The first entry is the default group for every
        /// Keychain write that names none — which is how the SDK stores the player's session. Own
        /// group first keeps each game's session private to that game and keeps sessions written
        /// before this entitlement existed (which landed in the app's own group, the default with
        /// no entitlement) readable. Shared group first would put every game's session where the
        /// whole team can read it.</para>
        /// </summary>
        internal static List<string> KeychainGroups(IEnumerable<string> existing, string bundleId)
        {
            var result = new List<string>
            {
                AppIdentifierPrefix + bundleId,
                AppIdentifierPrefix + SharedKeychainGroup,
            };
            if (existing != null)
            {
                foreach (string group in existing)
                {
                    if (!string.IsNullOrEmpty(group) && !result.Contains(group)) result.Add(group);
                }
            }
            return result;
        }

        /// <summary>The project's associated domains, in order, plus the launch-link domain if it
        /// is missing. Merge, never clobber: a game's own domains survive.</summary>
        internal static List<string> AssociatedDomains(IEnumerable<string> existing)
        {
            var result = new List<string>();
            if (existing != null)
            {
                foreach (string domain in existing)
                {
                    if (!string.IsNullOrEmpty(domain) && !result.Contains(domain)) result.Add(domain);
                }
            }
            if (!result.Contains(LaunchLinkDomain)) result.Add(LaunchLinkDomain);
            return result;
        }

        /// <summary>Whether a Player Settings "Target minimum iOS Version" string is at or above
        /// <see cref="MinimumIosVersion"/>. Unparseable or blank counts as below.</summary>
        internal static bool MeetsMinimumIos(string targetOsVersion)
        {
            string text = (targetOsVersion ?? string.Empty).Trim();
            if (text.Length == 0) return false;
            if (text.IndexOf('.') < 0) text += ".0"; // System.Version needs major.minor
            Version version;
            return Version.TryParse(text, out version) && version >= new Version(MinimumIosVersion);
        }

        /// <summary>The packaged GOG.xcframework on disk, or null when it is not there. Read
        /// straight from the package folder, so it does not depend on how (or whether) Unity's
        /// plugin importer handles an .xcframework.</summary>
        internal static string FindPackagedXcframework()
        {
            var roots = new List<string>();
            UnityEditor.PackageManager.PackageInfo info =
                UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(GogIosBuildSupport).Assembly);
            if (info != null && !string.IsNullOrEmpty(info.resolvedPath)) roots.Add(info.resolvedPath);
            roots.Add(Path.GetFullPath(Path.Combine("Packages", PackageName)));

            foreach (string root in roots)
            {
                string candidate = Path.Combine(root, "Runtime", "Plugins", "iOS", XcframeworkName);
                if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "Info.plist")))
                    return candidate;
            }
            return null;
        }

        internal const string MissingXcframeworkMessage =
            "GOG SDK: Runtime/Plugins/iOS/GOG.xcframework is missing from the com.gog.sdk package, so " +
            "this iOS build would have no native sign-in core and would fail to link (or ship broken). " +
            "Install a com.gog.sdk release that includes it, or build it on a Mac with " +
            "Gog-ios-sdk/scripts/build-xcframework.sh --package-json <this package's package.json> and " +
            "place dist/GOG.xcframework at Runtime/Plugins/iOS/ (see the README there).";
    }

#if UNITY_IOS
    /// <summary>
    /// Fails an iOS build BEFORE the long IL2CPP/Xcode export when it could only produce a broken
    /// app: no Game ID, no native core, or a deployment target below the core's floor. The iOS
    /// twin of <see cref="GogBuildValidator"/> (which stays Android-only and unchanged).
    /// </summary>
    internal sealed class GogIosBuildValidator : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.iOS) return;

            var settings = GogSettingsProvider.LoadOrNull();
            if (settings == null || !GogSettings.IsValidGameId(settings.GameId))
            {
                throw new BuildFailedException(
                    "GOG SDK: set a valid Game ID in Project Settings → GOG SDK before building.");
            }

            if (GogIosBuildSupport.FindPackagedXcframework() == null)
                throw new BuildFailedException(GogIosBuildSupport.MissingXcframeworkMessage);

            string target = PlayerSettings.iOS.targetOSVersionString;
            if (!GogIosBuildSupport.MeetsMinimumIos(target))
            {
                throw new BuildFailedException(
                    "GOG SDK: GOG.xcframework requires iOS " + GogIosBuildSupport.MinimumIosVersion +
                    " or newer, but Player Settings → Other Settings → Target minimum iOS Version is '" +
                    target + "'. Raise it to " + GogIosBuildSupport.MinimumIosVersion + " — a lower target " +
                    "installs on devices where the app would crash at launch.");
            }
        }
    }

    /// <summary>
    /// Wires the native iOS core and its entitlements into the exported Xcode project:
    ///
    /// <list type="bullet">
    /// <item><b>GOG.xcframework</b> — copied from the package, LINKED into UnityFramework (where
    /// IL2CPP's <c>DllImport("__Internal")</c> calls resolve) and Embedded &amp; Signed in the
    /// Unity-iPhone app target (a dynamic framework must ship inside the app). Done explicitly
    /// rather than left to Unity's plugin importer, whose .xcframework handling depends on the
    /// Unity version and on the package carrying a .meta for the framework. If Unity DID import
    /// and copy it, that copy is reused instead of adding a second.</item>
    /// <item><b>Entitlements</b>, merged into the app target's file (created if there is none):
    /// <c>keychain-access-groups</c> = [own group, shared group, …existing] — see
    /// <see cref="GogIosBuildSupport.KeychainGroups"/> for why the order is load-bearing — and
    /// <c>applinks:link.godofgaming.online</c> in the associated domains.</item>
    /// </list>
    ///
    /// <para>Runs late (order 1000) so it merges into whatever entitlements other post-processors
    /// produced rather than being overwritten by them.</para>
    /// </summary>
    internal static class GogIosPostProcessor
    {
        private const string OwnCopy = "Frameworks/GOG/" + GogIosBuildSupport.XcframeworkName;
        private const string DefaultEntitlements = "Unity-iPhone/Unity-iPhone.entitlements";
        private const string RunpathFrameworks = "@executable_path/Frameworks";

        [PostProcessBuild(1000)]
        public static void OnPostProcessBuild(BuildTarget target, string pathToBuiltProject)
        {
            if (target != BuildTarget.iOS) return;

            // Defense in depth — the validator already failed the build earlier.
            string source = GogIosBuildSupport.FindPackagedXcframework();
            if (source == null) throw new BuildFailedException(GogIosBuildSupport.MissingXcframeworkMessage);

            string projectPath = PBXProject.GetPBXProjectPath(pathToBuiltProject);
            var project = new PBXProject();
            project.ReadFromFile(projectPath);
            string appTarget = project.GetUnityMainTargetGuid();
            string frameworkTarget = project.GetUnityFrameworkTargetGuid();

            WireXcframework(project, appTarget, frameworkTarget, source, pathToBuiltProject);
            WriteEntitlements(project, appTarget, pathToBuiltProject);

            project.WriteToFile(projectPath);
        }

        private static void WireXcframework(PBXProject project, string appTarget, string frameworkTarget,
            string source, string root)
        {
            string ownFull = Path.Combine(root, OwnCopy);
            string unityCopy = FindUnityCopy(root, ownFull);
            string relative;

            if (unityCopy != null)
            {
                // Unity's importer already copied the framework (and links plugins into
                // UnityFramework). Drop any copy of ours from an earlier Append build so the
                // project never carries two.
                string staleGuid = FindFileGuid(project, OwnCopy);
                if (!string.IsNullOrEmpty(staleGuid)) project.RemoveFile(staleGuid);
                if (Directory.Exists(ownFull)) Directory.Delete(ownFull, true);
                relative = unityCopy;
            }
            else
            {
                // Always refresh from the package: an Append build must not keep a stale core.
                if (Directory.Exists(ownFull)) Directory.Delete(ownFull, true);
                CopyDirectory(source, ownFull);
                relative = OwnCopy;
            }

            string fileGuid = FindFileGuid(project, relative);
            bool alreadyReferenced = !string.IsNullOrEmpty(fileGuid);
            if (!alreadyReferenced) fileGuid = project.AddFile(relative, relative, PBXSourceTree.Source);

            // Our own copy, already referenced = an Append build this post-process wired last
            // time. Re-adding would duplicate the link and embed entries.
            if (unityCopy == null && alreadyReferenced) return;

            // LINK into UnityFramework: the IL2CPP code that calls gog_bridge_* lives there.
            if (unityCopy != null)
                project.AddFileToBuild(frameworkTarget, fileGuid); // no-op if Unity linked it already
            else
                project.AddFileToBuildSection(frameworkTarget,
                    project.GetFrameworksBuildPhaseByTarget(frameworkTarget), fileGuid);

            // EMBED & SIGN in the app: a dynamic framework must be inside the .app to load.
            // (A static-library build of the core — build-xcframework.sh's fallback — needs
            // linking only.)
            if (Directory.GetDirectories(source, "GOG.framework", SearchOption.AllDirectories).Length > 0)
            {
                project.AddFileToEmbedFrameworks(appTarget, fileGuid);
                project.AddBuildProperty(appTarget, "LD_RUNPATH_SEARCH_PATHS", RunpathFrameworks);
                project.AddBuildProperty(frameworkTarget, "LD_RUNPATH_SEARCH_PATHS", RunpathFrameworks);
            }
        }

        // A GOG.xcframework Unity's plugin importer placed under Frameworks/, as a project-relative
        // path with forward slashes — or null.
        private static string FindUnityCopy(string root, string ownFull)
        {
            string frameworks = Path.Combine(root, "Frameworks");
            if (!Directory.Exists(frameworks)) return null;
            string ownNormalized = Path.GetFullPath(ownFull);
            foreach (string dir in Directory.GetDirectories(frameworks, GogIosBuildSupport.XcframeworkName,
                         SearchOption.AllDirectories))
            {
                string full = Path.GetFullPath(dir);
                if (string.Equals(full, ownNormalized, StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(Path.Combine(full, "Info.plist"))) continue;
                return full.Substring(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, '/').Length + 1)
                    .Replace('\\', '/');
            }
            return null;
        }

        private static string FindFileGuid(PBXProject project, string relative)
        {
            string guid = project.FindFileGuidByProjectPath(relative);
            if (string.IsNullOrEmpty(guid)) guid = project.FindFileGuidByRealPath(relative, PBXSourceTree.Source);
            return guid;
        }

        private static void CopyDirectory(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string file in Directory.GetFiles(from))
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
            foreach (string dir in Directory.GetDirectories(from))
                CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
        }

        private static void WriteEntitlements(PBXProject project, string appTarget, string root)
        {
            string bundleId = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.iOS);
            if (string.IsNullOrEmpty(bundleId))
                throw new BuildFailedException(
                    "GOG SDK: the iOS Bundle Identifier is empty. Set it in Player Settings → Other Settings.");

            string configured = project.GetBuildPropertyForAnyConfig(appTarget, "CODE_SIGN_ENTITLEMENTS");
            string relative = NormalizeEntitlementsPath(configured);
            bool fresh = string.IsNullOrEmpty(relative);
            if (fresh) relative = DefaultEntitlements;
            if (relative.Contains("$("))
                throw new BuildFailedException(
                    "GOG SDK: CODE_SIGN_ENTITLEMENTS is '" + configured + "', which uses a build variable " +
                    "this post-processor cannot resolve, so the GOG entitlements cannot be merged into it. " +
                    "Point it at a project-relative path.");
            string full = Path.IsPathRooted(relative) ? relative : Path.Combine(root, relative);

            var plist = new PlistDocument();
            if (File.Exists(full)) plist.ReadFromFile(full);
            PlistElementDict entitlements = plist.root;

            List<string> existingGroups = ReadStrings(entitlements, GogIosBuildSupport.KeychainGroupsKey);
            List<string> groups = GogIosBuildSupport.KeychainGroups(existingGroups, bundleId);
            if (existingGroups.Count > 0 && existingGroups[0] != groups[0])
                Debug.LogWarning("[GOG SDK] keychain-access-groups started with '" + existingGroups[0] +
                                 "'; the GOG SDK moved '" + groups[0] + "' to the front. The first group is " +
                                 "the default for every Keychain write, and it must be this app's own.");
            WriteStrings(entitlements, GogIosBuildSupport.KeychainGroupsKey, groups);

            WriteStrings(entitlements, GogIosBuildSupport.AssociatedDomainsKey,
                GogIosBuildSupport.AssociatedDomains(ReadStrings(entitlements, GogIosBuildSupport.AssociatedDomainsKey)));

            string directory = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            plist.WriteToFile(full);

            if (fresh)
            {
                if (string.IsNullOrEmpty(FindFileGuid(project, relative)))
                    project.AddFile(relative, relative, PBXSourceTree.Source);
                project.SetBuildProperty(appTarget, "CODE_SIGN_ENTITLEMENTS", relative);
            }
        }

        // "$(SRCROOT)/Unity-iPhone/x.entitlements" or a quoted value → a project-relative path.
        private static string NormalizeEntitlementsPath(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            string path = value.Trim().Trim('"');
            foreach (string prefix in new[] { "$(SRCROOT)/", "$(PROJECT_DIR)/", "${SRCROOT}/", "${PROJECT_DIR}/" })
            {
                if (path.StartsWith(prefix, StringComparison.Ordinal)) path = path.Substring(prefix.Length);
            }
            return path.Length == 0 ? null : path;
        }

        private static List<string> ReadStrings(PlistElementDict dict, string key)
        {
            var result = new List<string>();
            PlistElement element;
            if (dict.values.TryGetValue(key, out element))
            {
                var array = element as PlistElementArray;
                if (array != null)
                {
                    foreach (PlistElement item in array.values)
                    {
                        var text = item as PlistElementString;
                        if (text != null) result.Add(text.value);
                    }
                }
            }
            return result;
        }

        private static void WriteStrings(PlistElementDict dict, string key, List<string> values)
        {
            PlistElementArray array = dict.CreateArray(key); // replaces the old array
            foreach (string value in values) array.AddString(value);
        }
    }
#endif
}
