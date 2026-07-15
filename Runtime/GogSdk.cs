using System;
using System.Threading;
using Gog.Sdk.Internal;
using UnityEngine;

namespace Gog.Sdk
{
    /// <summary>
    /// The SDK root (docs/sdk-api-contract.md §1.3, §2). One initialization model for the
    /// whole SDK: <see cref="Initialize"/> takes NO arguments and reads the single
    /// <see cref="GogSettings"/> asset — the SAME asset the build-time Android manifest
    /// injector reads. The manifest and the runtime therefore cannot disagree about the
    /// game id, because there is only one place a game id can come from.
    /// </summary>
    public static class GogSdk
    {
        /// <summary>Package version — the C# half of the binding↔core handshake (§1.5).
        /// Mirrors package.json via the generated <see cref="GogSdkVersion"/>; asserted at
        /// <see cref="Initialize"/> against the native core's build-time version over JNI.</summary>
        public const string Version = GogSdkVersion.Value;

        /// <summary>True between a successful <see cref="Initialize"/> and <see cref="Shutdown"/>.</summary>
        public static bool IsInitialized { get; private set; }

        // The config authority for every other surface (GogCoins reads game_id / base URL
        // from here). Set only from the one loaded asset — never a per-call argument.
        internal static GogSettings Settings { get; private set; }

        // Captured on the main thread at SubsystemRegistration; used to reject an
        // off-main-thread Initialize() as the programmer error it is (§1.3).
        private static int _mainThreadId;

        /// <summary>
        /// Wire up the SDK. Call once at startup, on the main thread. Reads
        /// <see cref="GogSettings"/> from Resources.
        ///
        /// <para>Throws (does NOT return an error) only for programmer errors — a missing
        /// or invalid <see cref="GogSettings"/> asset, or an off-main-thread call. These are
        /// boot-time integration bugs; failing loudly is the feature. Every runtime failure
        /// (network, auth state, platform) stays a typed <see cref="GogResult{T}"/>.</para>
        ///
        /// <para>A second call is a logged no-op — runtime reconfiguration does not exist,
        /// which is what makes it impossible for the C# config and the native core to hold
        /// different game ids.</para>
        /// </summary>
        public static void Initialize() => InitializeInternal(GogSettings.Load());

        // The seam the public entry and the tests share. `settings` is whatever the caller
        // resolved (Resources for the real path; an injected asset/null for tests).
        internal static void InitializeInternal(GogSettings settings)
        {
            EnsureMainThread();

            if (IsInitialized)
            {
                Debug.Log("[GOG SDK] Initialize() called again — ignored. Runtime reconfiguration does not exist.");
                return;
            }

            if (settings == null)
                throw new InvalidOperationException(
                    "GOG SDK: no GogSettings asset found. Create it in Project Settings → GOG SDK and set your Game ID.");
            if (!GogSettings.IsValidGameId(settings.GameId))
                throw new InvalidOperationException(
                    "GOG SDK: Game ID is missing or not a valid UUID. Set it in Project Settings → GOG SDK.");

            // Bring up the native bridge. This runs the binding↔core version handshake and
            // THROWS on a mismatch (§1.5) — before the SDK is marked initialized, so a stale or
            // hand-copied .aar cannot half-boot. Settings/IsInitialized are set only if it returns.
            AndroidAuthBridge.Initialize(settings.GameId, settings.ApiBaseUrl, GogAuth.RaiseSignedOut);
            Settings = settings;
            IsInitialized = true;
        }

        /// <summary>
        /// Return the SDK to pre-init state: clear event subscriptions, drop the native
        /// bridge and the main-thread dispatcher. Does NOT clear the persisted session
        /// (contract §1.3) — a later <see cref="Initialize"/> restores it.
        /// </summary>
        public static void Shutdown()
        {
            GogAuth.ResetEvents();
            GogDeepLink.ResetState();
            AndroidAuthBridge.Shutdown();
            GogMainThreadDispatcher.Shutdown();
            Settings = null;
            IsInitialized = false;
        }

        // Enter Play Mode without Domain Reload keeps C# statics — including event
        // subscription lists and the native bridge — alive across editor play sessions.
        // Reset them so nothing leaks between sessions (contract §1.3).
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticsOnLoad()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId; // runs on the main thread
            IsInitialized = false;
            Settings = null;
            GogAuth.ResetEvents();
            GogDeepLink.ResetState();
            AndroidAuthBridge.ResetStatics();
            GogMainThreadDispatcher.ResetStatics();
        }

        private static void EnsureMainThread()
        {
            if (_mainThreadId != 0 && Thread.CurrentThread.ManagedThreadId != _mainThreadId)
                throw new InvalidOperationException(
                    "GOG SDK: Initialize() must be called on the Unity main thread.");
        }
    }
}
