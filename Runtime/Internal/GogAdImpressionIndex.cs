using UnityEngine;

namespace Gog.Sdk.Internal
{
    /// <summary>
    /// The impression index: an SDK-internal, per-install, monotonic, persisted counter that
    /// the server uses to rotate house creatives deterministically
    /// (docs/sdk-api-contract.md §6).
    ///
    /// <para><b>Why it is internal and has no reset.</b> The server picks a creative from
    /// <c>(game_id, index)</c>. If a game could restart the counter, every player would see the
    /// first creative forever — the exact starvation this mechanism exists to prevent. So the
    /// index is API-invisible: nothing in the frozen public surface (§2) reads, writes, or
    /// clears it, and no reset method exists outside the test seams below.</para>
    ///
    /// <para><b>Why the store is PlayerPrefs and not EncryptedSharedPreferences.</b> The index
    /// is not a secret. Mandating ESP would force a JNI hop into the Kotlin path that §1.4
    /// deliberately keeps out of the ad flow. §6 freezes the PROPERTY (persisted, monotonic,
    /// API-invisible), not the store — so this can move later with no API impact. PlayerPrefs
    /// was chosen over a file under <c>Application.persistentDataPath</c> because it always maps
    /// to internal-storage SharedPreferences on Android, whereas persistentDataPath relocates to
    /// external storage if the game sets Player Settings → Write Permission = External.</para>
    ///
    /// <para><b>KNOWN LIMITATION — <c>PlayerPrefs.DeleteAll()</c>.</b> PlayerPrefs is an
    /// app-global namespace, so the host game can clear this key and rewind its own ad rotation.
    /// The realistic path is not malice but a game's own "Reset Progress" button, which very
    /// commonly calls <c>PlayerPrefs.DeleteAll()</c>. This is ACCEPTED, not overlooked: the blast
    /// radius is one install's rotation fairness (index 0 is always a legal slot, so the server
    /// still serves a valid creative), and it is self-limiting as the counter climbs again.
    /// Do NOT "harden" the store in response — see §6.</para>
    /// </summary>
    internal static class GogAdImpressionIndex
    {
        /// <summary>The persisted key. Namespaced to avoid colliding with the game's own prefs.
        /// Deliberately NOT namespaced by game id: per §1.3 there is exactly one GameId per
        /// install, and keying by it would let a build-time GameId change rewind the counter —
        /// closer to "resettable" than a flat key.</summary>
        internal const string Key = "com.gog.sdk.ads.impression_index";

        /// <summary>
        /// Consume one impression opportunity: return the current value and persist the next one.
        ///
        /// <para><b>Called once per opportunity</b> — per <c>ShowInterstitial</c> that passes the
        /// precheck and the concurrency guard — and never per render, per retry, or per rejected
        /// call. A <c>GogHttp</c> transport retry re-sends the URL built here, index already
        /// baked in, so a retry cannot consume a second opportunity.</para>
        ///
        /// <para><b>Increments BEFORE the fetch, not after a success.</b> If it incremented on
        /// success, a player on a flaky network — or an app the device OOM-kills mid-fetch —
        /// would replay the same index indefinitely and see one creative forever. Consuming the
        /// opportunity up front means a failed fetch SKIPS a creative, and a skipped creative is
        /// strictly better than a repeated one.</para>
        ///
        /// <para><b>Save() is explicit</b> rather than left to the automatic flush at
        /// OnApplicationPause/Quit: an OOM kill gets no such callback, and an unflushed counter
        /// is a silently rewound counter. One small synchronous write per interstitial is an
        /// acceptable cost at that frequency.</para>
        /// </summary>
        internal static int Next()
        {
            int current = PlayerPrefs.GetInt(Key, 0);
            // Defensive: a corrupted or externally-written negative value restarts at 0 rather
            // than sending an index the server rejects with a 400 (index must be >= 0).
            if (current < 0) current = 0;

            // Saturate instead of wrapping. Wrapping would go negative and produce that same 400.
            // Unreachable in practice (2.1 billion impressions on one install).
            int next = current == int.MaxValue ? int.MaxValue : current + 1;

            PlayerPrefs.SetInt(Key, next);
            PlayerPrefs.Save();
            return current;
        }

        // ── test seams ────────────────────────────────────────────────────────────
        // Internal, reached only through [assembly: InternalsVisibleTo("Gog.Sdk.Tests")].
        // §6 forbids a reset "through the API"; the public surface (§2) has none. These are not
        // the API — they exist so the persistence semantics can be proven without a real restart.

        internal static int PeekForTests() => PlayerPrefs.GetInt(Key, 0);

        internal static void SetForTests(int value)
        {
            PlayerPrefs.SetInt(Key, value);
            PlayerPrefs.Save();
        }

        internal static void ClearForTests()
        {
            PlayerPrefs.DeleteKey(Key);
            PlayerPrefs.Save();
        }
    }
}
