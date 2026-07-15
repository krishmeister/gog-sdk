using System;
using System.Collections.Generic;
using Gog.Sdk.Internal;
using UnityEngine;

namespace Gog.Sdk
{
    /// <summary>
    /// Drains background-thread callbacks onto Unity's main thread. JNI callbacks
    /// from the Kotlin core arrive on a background (Java) thread; they enqueue here
    /// and run in <c>Update()</c> on the main thread, where Unity APIs and game
    /// code are safe to touch.
    /// </summary>
    internal sealed class GogMainThreadDispatcher : MonoBehaviour
    {
        private static GogMainThreadDispatcher _instance;
        private static readonly object Gate = new object();
        private readonly Queue<Action> _queue = new Queue<Action>();

        /// <summary>Create the dispatcher GameObject. MUST be called on the main thread
        /// (done from <see cref="GogSdk.Initialize"/>).</summary>
        internal static void Ensure()
        {
            if (_instance != null) return;
            var go = new GameObject("GogMainThreadDispatcher") { hideFlags = HideFlags.HideAndDontSave };
            _instance = go.AddComponent<GogMainThreadDispatcher>();
            DontDestroyOnLoad(go);
        }

        /// <summary>Enqueue work for the main thread. Safe to call from any thread,
        /// provided <see cref="Ensure"/> has run (Initialize guarantees it).</summary>
        internal static void Enqueue(Action action)
        {
            if (action == null || _instance == null) return;
            lock (Gate) { _instance._queue.Enqueue(action); }
        }

        /// <summary>Tear down the dispatcher GameObject and drop the reference
        /// (GogSdk.Shutdown). In-flight callbacks are dropped — best-effort by design
        /// (contract §1.4.7).</summary>
        internal static void Shutdown()
        {
            var inst = _instance;
            _instance = null;
            if (inst == null) return;
            if (Application.isPlaying) Destroy(inst.gameObject);
            else DestroyImmediate(inst.gameObject);
        }

        /// <summary>Drop the (already-destroyed) reference on Enter-Play-Mode-without-Domain-
        /// Reload — the GameObject was destroyed when the previous play session ended.</summary>
        internal static void ResetStatics() => _instance = null;

        private void Update()
        {
            while (true)
            {
                Action next = null;
                lock (Gate)
                {
                    if (_queue.Count > 0) next = _queue.Dequeue();
                }
                if (next == null) break;
                try { next(); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        // Belt-and-suspenders for contract I6: the core resumes a persisted handoff at
        // initialize(), but a game with runInBackground / very-early init could initialize
        // BEFORE the proxy persists the returning result. Re-checking on every focus regain
        // closes that ordering hole; the core claim-guards, so extra calls are no-ops (and
        // this is a no-op off Android / pre-init, where the bridge holds no core).
        private void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus) AndroidAuthBridge.ResumePendingHandoff();
        }
    }
}
