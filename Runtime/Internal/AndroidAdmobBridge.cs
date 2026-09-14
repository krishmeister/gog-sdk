using System;
using UnityEngine;

namespace Gog.Sdk.Internal
{
    /// <summary>
    /// The JNI seam to the native AdMob path (<c>com.gog.sdk.GogAdmob</c>). Holds no decision
    /// logic — the server already chose AdMob and the ad unit. It hands the unit to the core,
    /// which loads and shows the interstitial with the Google Mobile Ads Android SDK, and marshals
    /// the single outcome back onto the main thread.
    ///
    /// <para>No Google Mobile Ads Unity plugin is involved: the Android SDK arrives through
    /// <c>GogSdkDependencies.xml</c> (EDM4U) and the app id through
    /// <c>GogSettings.AdmobAppId</c>. When either is missing the core reports it and the
    /// opportunity resolves as NoAd (I7).</para>
    /// </summary>
    internal static class AndroidAdmobBridge
    {
        /// <summary>Upper bound on the LOAD, in seconds. AdMob fills usually land in 1-3s; past
        /// this the player is waiting on an ad, which I7 forbids. Never beyond the directive's
        /// own TTL either.</summary>
        internal const int MaxLoadSeconds = 10;

        /// <param name="onResult">Fires exactly once, on the main thread: whether the ad was shown,
        /// and a stable reason token when it was not.</param>
        internal static void ShowInterstitial(string adUnitId, int expiresInSeconds,
            Action<bool, string> onResult)
        {
            if (Application.platform != RuntimePlatform.Android)
            {
                onResult(false, "unsupported_platform");
                return;
            }

            try
            {
                int loadSeconds = Math.Max(1, Math.Min(MaxLoadSeconds, expiresInSeconds));
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var admob = new AndroidJavaClass("com.gog.sdk.GogAdmob"))
                {
                    var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                    admob.CallStatic("showInterstitial", activity, adUnitId, loadSeconds * 1000L,
                        new AdmobCallbackProxy((shown, reason) =>
                            GogMainThreadDispatcher.Enqueue(() => onResult(shown, reason))));
                }
            }
            catch (Exception e)
            {
                // A stale .aar without GogAdmob, or any JNI fault: never strand the callback.
                Debug.LogException(e);
                onResult(false, "bridge_exception");
            }
        }
    }

    /// <summary>AndroidJavaProxy for <c>com.gog.sdk.GogAdmobCallback</c>. The method name MUST
    /// match the Kotlin interface exactly. Invoked on the Android main thread or a JNI thread —
    /// never Unity's; the supplied action marshals.</summary>
    internal sealed class AdmobCallbackProxy : AndroidJavaProxy
    {
        private readonly Action<bool, string> _onResult;

        public AdmobCallbackProxy(Action<bool, string> onResult) : base("com.gog.sdk.GogAdmobCallback")
        {
            _onResult = onResult;
        }

        // ReSharper disable once InconsistentNaming — must match Java method name.
        public void onResult(bool shown, string reason) => _onResult(shown, reason);
    }
}
