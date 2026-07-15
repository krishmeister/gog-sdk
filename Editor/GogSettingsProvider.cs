using System.IO;
using UnityEditor;
using UnityEngine;

namespace Gog.Sdk.Editor
{
    /// <summary>
    /// Project Settings → GOG SDK. Creates and edits the single GogSettings asset in
    /// project space. Also the one place that knows the asset's path, shared with the
    /// build validator and the manifest injector so "the settings asset" means one file.
    /// </summary>
    internal static class GogSettingsProvider
    {
        internal const string SettingsAssetPath = "Assets/GogSdk/Resources/GogSettings.asset";

        /// <summary>Load the configured asset via AssetDatabase (edit/build-time reliable),
        /// or null if it does not exist yet.</summary>
        internal static GogSettings LoadOrNull()
            => AssetDatabase.LoadAssetAtPath<GogSettings>(SettingsAssetPath);

        /// <summary>Create the asset with defaults in project space, making the folders as
        /// needed. Idempotent — returns the existing asset if one is already there.</summary>
        internal static GogSettings CreateSettingsAsset()
        {
            var existing = LoadOrNull();
            if (existing != null) return existing;

            var dir = Path.GetDirectoryName(SettingsAssetPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var asset = ScriptableObject.CreateInstance<GogSettings>();
            AssetDatabase.CreateAsset(asset, SettingsAssetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return LoadOrNull();
        }

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new SettingsProvider("Project/GOG SDK", SettingsScope.Project)
            {
                label = "GOG SDK",
                keywords = new[] { "gog", "sdk", "auth", "game", "gameid", "game id" },
                guiHandler = _ =>
                {
                    var asset = LoadOrNull();
                    if (asset == null)
                    {
                        EditorGUILayout.HelpBox(
                            "No GOG SDK settings yet. Create the asset to set your Game ID.",
                            MessageType.Info);
                        if (GUILayout.Button("Create GOG SDK Settings", GUILayout.Height(28)))
                            CreateSettingsAsset();
                        return;
                    }

                    var so = new SerializedObject(asset);
                    so.Update();
                    var gameId = so.FindProperty("gameId");
                    var apiBaseUrl = so.FindProperty("apiBaseUrl");
                    var autoSignIn = so.FindProperty("autoSignInFromLaunch");
                    var admobAppId = so.FindProperty("admobAppId");

                    EditorGUILayout.LabelField("Identity", EditorStyles.boldLabel);
                    EditorGUILayout.PropertyField(
                        gameId, new GUIContent("Game ID", "Your game's GOG UUID."));
                    // Inline validation — surface the problem, never block editing.
                    if (!GogSettings.IsValidGameId(gameId.stringValue))
                    {
                        EditorGUILayout.HelpBox(
                            string.IsNullOrEmpty(gameId.stringValue)
                                ? "Game ID is required. Android builds fail until it is set."
                                : "Game ID is not a valid UUID.",
                            MessageType.Error);
                    }

                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Connection", EditorStyles.boldLabel);
                    EditorGUILayout.PropertyField(apiBaseUrl, new GUIContent("API Base URL"));
                    EditorGUILayout.PropertyField(
                        autoSignIn, new GUIContent("Auto sign-in from launch"));

                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Ads", EditorStyles.boldLabel);
                    EditorGUILayout.PropertyField(
                        admobAppId,
                        new GUIContent(
                            "AdMob app id",
                            "Optional. Required before enabling an AdMob-serving mode for this " +
                            "game. Injected into the Android manifest at build time. Must belong " +
                            "to the same AdMob account as the ad units GOG configures."));
                    // Inline validation — blank is a valid (safe) state; only a malformed
                    // value is an error, because AdMob crashes at launch on one.
                    var admobTrim = admobAppId.stringValue == null ? "" : admobAppId.stringValue.Trim();
                    if (admobTrim.Length > 0 && !GogSettings.IsValidAdmobAppId(admobTrim))
                    {
                        EditorGUILayout.HelpBox(
                            "Not a valid AdMob APP id (expected ca-app-pub-<16 digits>~<10 digits>). " +
                            "An ad UNIT id (contains '/') is not an app id. Android builds fail " +
                            "until this is fixed or cleared.",
                            MessageType.Error);
                    }

                    so.ApplyModifiedProperties();
                },
            };
        }
    }
}
