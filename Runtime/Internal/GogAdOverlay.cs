using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.Video;

namespace Gog.Sdk.Internal
{
    /// <summary>
    /// The house-ad render surface: a full-screen Canvas built entirely from code, so a game
    /// needs no prefab, no scene object, and no integration beyond calling
    /// <c>GogAds.ShowInterstitial</c>.
    ///
    /// <para><b>Two clocks, deliberately.</b> The forced-view window and the failsafe accumulate
    /// <c>Time.unscaledDeltaTime</c>, so they ignore a game that sets <c>Time.timeScale = 0</c> to
    /// pause for the ad, and they stop while the app is backgrounded. The directive's TTL uses
    /// <c>Time.realtimeSinceStartup</c> (in <c>GogAds</c>) precisely because it must NOT pause —
    /// a directive genuinely ages while the player is away.</para>
    ///
    /// <para><b>Teardown is single and idempotent.</b> Every exit path — close tapped, failsafe,
    /// SDK shutdown — funnels through <see cref="Finish"/>, guarded by <c>_torn</c>. The target
    /// hardware OOM-kills on routine operations, so a leaked decoded texture across a few
    /// show/dismiss cycles is a crash, not a slow leak.</para>
    ///
    /// <para><b>Video renders through the SAME surface as an image.</b> A video creative decodes
    /// into a RenderTexture which is swapped onto the one RawImage the image path already uses,
    /// so the backdrop, letterboxing, click handling, forced-view countdown, close control and
    /// failsafe are all literally the same code. Playback uses UnityEngine.Video.VideoPlayer — a
    /// BUILT-IN Unity module, no package, no EDM4U dependency, nothing for a partner to import
    /// (the contrast with AdMob, which needs an external plugin, is deliberate).</para>
    ///
    /// <para><b>The poster always wins ties.</b> The still frame is fetched and shown FIRST, and
    /// the video is swapped in only once it has actually prepared. Every video failure — codec
    /// refused, network stalled, prepare timed out — simply leaves the poster on screen. A video
    /// creative can therefore never render worse than the image ad it falls back to (I7).</para>
    /// </summary>
    internal sealed class GogAdOverlay : MonoBehaviour
    {
        /// <summary>
        /// Absolute maximum foreground lifetime, after which the overlay tears itself down and
        /// resolves regardless of input state.
        ///
        /// <para><b>Why this exists.</b> If a game has no <c>EventSystem</c> and the one we create
        /// fails to work — a wrong input module, an input system we could not detect — the close
        /// button never fires and the ad is PERMANENTLY undismissable: the player must force-quit.
        /// That is the single worst bug this feature could ship. The failsafe makes "stuck
        /// forever" impossible even if every input assumption is wrong.</para>
        ///
        /// <para><b>Why 90 seconds.</b> The longest legitimate forced-view window is 30s (the
        /// clamp ceiling), which leaves a full minute of foreground dwell for a player to notice
        /// and tap close — far past any plausible interaction, and comfortably short of the two-
        /// to-three minutes at which a player concludes the game has frozen and force-quits. It
        /// counts FOREGROUND time only (same unscaled clock as the forced-view timer), so a player
        /// who taps through to a browser and reads for five minutes does not come back to a
        /// vanished ad — while the genuine stuck case, which by definition has the app in the
        /// foreground, is always caught.</para>
        /// </summary>
        internal const float FailsafeMaxLifetimeSeconds = 90f;

        private static GogAdOverlay _instance;

        /// <summary>
        /// How long to wait for a video to become playable before giving up and staying on the
        /// poster, in unscaled FOREGROUND seconds.
        ///
        /// <para><b>Why 6 seconds.</b> The forced-view window is 1-30s with a 4s default, so a
        /// video that has not started by 6s would spend most of a typical ad as a still frame
        /// anyway — at which point the honest outcome is "this is an image ad", not a longer
        /// stall. It also has to stay well under the 90s failsafe so a wedged prepare surfaces as
        /// a poster the player can dismiss, never as a frozen overlay.</para>
        /// </summary>
        internal const float VideoPrepareTimeoutSeconds = 6f;

        /// <summary>Upper bound on the RenderTexture we allocate for playback. A 4K ad on a
        /// budget phone is an OOM; 1920 on the long edge is beyond any interstitial's needs and
        /// keeps the allocation near the ~8 MB the image path already peaks at.</summary>
        private const int VideoMaxDimension = 1920;

        private Action<GogAdOutcome> _resolve;
        private Texture2D _texture;
        private RawImage _creative;
        private AspectRatioFitter _fitter;
        private string _videoUrl;
        private VideoPlayer _video;
        private RenderTexture _videoTarget;
        private bool _videoPlaying;
        private bool _videoSettled;
        private bool _videoReleasePending;
        private float _videoWait;
        private Image _ring;
        private GameObject _closeGlyph;
        private Button _closeButton;
        private string _clickUrl;
        private float _forcedViewSeconds;
        private float _elapsed;
        private bool _closeReady;
        private bool _paused;
        private bool _torn;

        /// <summary>True while an ad surface is live. Read by the concurrency guard.</summary>
        internal static bool IsShowing => _instance != null;

        /// <summary>
        /// Build and present the overlay. <paramref name="resolve"/> is invoked exactly once, on
        /// the main thread, when the ad finishes for any reason.
        /// </summary>
        /// <param name="texture">The decoded still. For a video creative this is its POSTER —
        /// shown immediately and kept if playback never starts.</param>
        /// <param name="videoUrl">Optional. When set, playback is attempted and swapped in over
        /// the poster once prepared.</param>
        internal static GogAdOverlay Show(Texture2D texture, string clickUrl, float forcedViewSeconds,
            Action<GogAdOutcome> resolve, string videoUrl = null)
        {
            var go = new GameObject("GogAdOverlay") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            var overlay = go.AddComponent<GogAdOverlay>();
            _instance = overlay;

            overlay._texture = texture;
            overlay._clickUrl = clickUrl;
            overlay._forcedViewSeconds = forcedViewSeconds;
            overlay._resolve = resolve;
            overlay._videoUrl = videoUrl;
            overlay.Build();
            // AFTER Build: the poster is already on screen, so a prepare that fails or hangs
            // costs the player nothing.
            if (!string.IsNullOrEmpty(videoUrl)) overlay.BeginVideo();
            return overlay;
        }

        /// <summary>Tear down any live surface and resolve it as NoAd. Safe when nothing is
        /// showing, and safe on a stale static after a domain reload.</summary>
        internal static void Teardown()
        {
            GogAdOverlay inst = _instance;
            _instance = null;
            if (inst == null) return;
            inst.Finish(GogAdOutcome.NoAd);
        }

        // ── construction ──────────────────────────────────────────────────────────

        private void Build()
        {
            EnsureEventSystem();

            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Above essentially any game UI. Not a guarantee the SDK can enforce — a game using an
            // equal or higher order on its own overlay canvas could still draw over us.
            canvas.sortingOrder = short.MaxValue;

            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 0.5f;

            gameObject.AddComponent<GraphicRaycaster>();

            // Sibling order is draw order in uGUI: backdrop, then creative, then close control.
            BuildBackdrop();
            BuildCreative();
            BuildCloseControl();
        }

        /// <summary>
        /// Opaque, raycast-blocking backdrop. Without this, taps land in the game underneath the
        /// ad. It is deliberately NOT tappable-through: only the creative opens the click URL, so
        /// a stray tap on the letterbox margin cannot inflate clicks or annoy the player.
        /// </summary>
        private void BuildBackdrop()
        {
            var go = NewUiChild("Backdrop", transform);
            Stretch(go.GetComponent<RectTransform>());
            var img = go.AddComponent<Image>();
            img.color = Color.black;
            img.raycastTarget = true;
        }

        private void BuildCreative()
        {
            var go = NewUiChild("Creative", transform);
            var rt = go.GetComponent<RectTransform>();
            Stretch(rt);

            // Held: the video path swaps this same RawImage's texture and re-fits it once the
            // real frame size is known, so both media kinds share one surface.
            _creative = go.AddComponent<RawImage>();
            _creative.texture = _texture;
            _creative.raycastTarget = true;

            // Letterbox inside the screen rather than stretching. The aspect comes from the
            // decoded texture, so no server-side width/height is needed.
            _fitter = go.AddComponent<AspectRatioFitter>();
            _fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            _fitter.aspectRatio = _texture != null && _texture.height > 0
                ? _texture.width / (float)_texture.height
                : 1f;

            if (!string.IsNullOrEmpty(_clickUrl))
            {
                var btn = go.AddComponent<Button>();
                btn.transition = Selectable.Transition.None;
                btn.onClick.AddListener(OnCreativeTapped);
            }
        }

        /// <summary>
        /// The countdown, which becomes the close control. Deliberately primitive: a radial-filled
        /// quad draining to empty, then a code-drawn cross. No font, no TextMeshPro, no generated
        /// texture, no added package — a designed asset can replace the visual later without
        /// changing any behaviour. Silence would read as a freeze; a visibly draining indicator
        /// tells the player the wait is finite.
        /// </summary>
        private void BuildCloseControl()
        {
            var go = NewUiChild("CloseControl", transform);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = new Vector2(-36f, -36f);
            rt.sizeDelta = new Vector2(96f, 96f);

            var plate = go.AddComponent<Image>();
            plate.color = new Color(0f, 0f, 0f, 0.55f);
            plate.raycastTarget = true;

            var ringGo = NewUiChild("Ring", go.transform);
            Stretch(ringGo.GetComponent<RectTransform>());
            _ring = ringGo.AddComponent<Image>();
            _ring.color = new Color(1f, 1f, 1f, 0.9f);
            _ring.type = Image.Type.Filled;
            _ring.fillMethod = Image.FillMethod.Radial360;
            _ring.fillOrigin = (int)Image.Origin360.Top;
            _ring.fillClockwise = true;
            _ring.fillAmount = 1f;
            _ring.raycastTarget = false;

            _closeGlyph = NewUiChild("Glyph", go.transform);
            Stretch(_closeGlyph.GetComponent<RectTransform>());
            AddCrossBar(_closeGlyph.transform, 45f);
            AddCrossBar(_closeGlyph.transform, -45f);
            _closeGlyph.SetActive(false);

            _closeButton = go.AddComponent<Button>();
            _closeButton.transition = Selectable.Transition.None;
            _closeButton.interactable = false; // until the forced-view window elapses
            _closeButton.onClick.AddListener(OnClosePressed);
        }

        private static void AddCrossBar(Transform parent, float zRotation)
        {
            var bar = NewUiChild("Bar", parent);
            var rt = bar.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(44f, 6f);
            rt.localRotation = Quaternion.Euler(0f, 0f, zRotation);
            var img = bar.AddComponent<Image>();
            img.color = Color.white;
            img.raycastTarget = false;
        }

        private static GameObject NewUiChild(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        /// <summary>
        /// uGUI needs an <c>EventSystem</c> for any button to fire. If the game has none and we
        /// did not create one, the close control would never respond and the ad would be
        /// permanently undismissable — the failure the failsafe exists to backstop.
        ///
        /// <para>The input module is chosen by type probe rather than a compile-time reference:
        /// the SDK must not force <c>com.unity.inputsystem</c> on partners who do not use it.
        /// Order matters — a project with Active Input Handling set to "Input System Package"
        /// only will throw from <c>StandaloneInputModule</c>, so the new module wins when present.</para>
        /// </summary>
        private void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;

            // Parented to the overlay, so it is destroyed with us and never outlives the ad.
            var esGo = new GameObject("GogAdEventSystem", typeof(RectTransform));
            esGo.transform.SetParent(transform, false);
            esGo.AddComponent<EventSystem>();

            Type inputSystemModule = Type.GetType(
                "UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
            if (inputSystemModule != null) esGo.AddComponent(inputSystemModule);
            else esGo.AddComponent<StandaloneInputModule>();

            Debug.Log("[GOG SDK] No EventSystem in the scene — created one for the ad overlay. " +
                      "It is destroyed with the ad.");
        }

        // ── video ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Start preparing the video. Called AFTER the poster is on screen, so everything here is
        /// an upgrade to an ad that is already running — which is why every failure path below
        /// simply stops trying rather than resolving the ad.
        /// </summary>
        private void BeginVideo()
        {
            try
            {
                _video = gameObject.AddComponent<VideoPlayer>();
                _video.source = VideoSource.Url;
                _video.url = _videoUrl;
                _video.playOnAwake = false;
                _video.waitForFirstFrame = true;
                _video.skipOnDrop = true;
                // Loop: the forced-view window is operator-set and independent of the file, so a
                // 5s cut under a 15s window would otherwise freeze on its last frame for ten
                // seconds and read as a hang.
                _video.isLooping = true;
                // Direct output needs no AudioSource and no mixer wiring in the partner's scene.
                // Ads carry sound; a game that has muted itself is unaffected because this does
                // not route through the game's AudioListener volume.
                _video.audioOutputMode = VideoAudioOutputMode.Direct;
                // Render into our own texture rather than a camera target — the overlay is a
                // ScreenSpaceOverlay canvas and may have no camera of its own at all.
                _video.renderMode = VideoRenderMode.RenderTexture;

                _video.errorReceived += OnVideoError;
                _video.prepareCompleted += OnVideoPrepared;
                _video.Prepare();
            }
            catch (Exception e)
            {
                // A missing Video module or a platform that refuses the component must not take
                // the ad down with it — the poster is still a valid impression.
                Debug.LogWarning("[GOG SDK] Video playback could not start (" + e.Message +
                                 ") — showing the creative's poster image instead.");
                Debug.LogException(e);
                AbandonVideo();
            }
        }

        private void OnVideoError(VideoPlayer source, string message)
        {
            Debug.LogWarning("[GOG SDK] Ad video failed to play (" + message +
                             ") — showing the creative's poster image instead.");
            AbandonVideo();
        }

        private void OnVideoPrepared(VideoPlayer source)
        {
            if (_torn || _videoSettled) return;

            try
            {
                int w = (int)source.width;
                int h = (int)source.height;
                if (w <= 0 || h <= 0)
                {
                    AbandonVideo();
                    return;
                }

                // Scale the long edge down to the cap, preserving aspect. A 4K creative on a
                // 512 MB device is an OOM, and nothing above 1080p is visible on an interstitial.
                int longEdge = Mathf.Max(w, h);
                if (longEdge > VideoMaxDimension)
                {
                    float k = VideoMaxDimension / (float)longEdge;
                    w = Mathf.Max(2, Mathf.RoundToInt(w * k));
                    h = Mathf.Max(2, Mathf.RoundToInt(h * k));
                }

                _videoTarget = new RenderTexture(w, h, 0) { name = "GogAdVideoTarget" };
                _videoTarget.Create();
                source.targetTexture = _videoTarget;

                if (_creative != null) _creative.texture = _videoTarget;
                if (_fitter != null) _fitter.aspectRatio = w / (float)h;

                source.Play();
                _videoPlaying = true;
                _videoSettled = true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[GOG SDK] Ad video could not be presented (" + e.Message +
                                 ") — showing the creative's poster image instead.");
                Debug.LogException(e);
                AbandonVideo();
            }
        }

        /// <summary>
        /// Give up on video and leave the poster showing. Idempotent, and deliberately NOT an ad
        /// failure: the still frame is the creative's own fallback asset, so the impression is
        /// real and the outcome is still Shown.
        ///
        /// <para><b>The teardown is DEFERRED to the next Update.</b> The commonest caller is
        /// <see cref="OnVideoError"/>, which runs inside VideoPlayer's own native callback —
        /// calling Stop() and destroying the component from in there is a native-side crash on
        /// some Android drivers. Swapping the texture back is immediate (that is what the player
        /// sees); only the destruction waits a frame.</para>
        /// </summary>
        private void AbandonVideo()
        {
            _videoSettled = true;
            _videoPlaying = false;
            if (_creative != null && _texture != null) _creative.texture = _texture;
            _videoReleasePending = true;
        }

        /// <summary>Tear down the player and its target texture. Safe to call twice and safe
        /// before <see cref="BeginVideo"/> ever ran.</summary>
        private void ReleaseVideo()
        {
            if (_video != null)
            {
                _video.errorReceived -= OnVideoError;
                _video.prepareCompleted -= OnVideoPrepared;
                // Stop before dropping the target: releasing a RenderTexture still bound to a
                // playing VideoPlayer is a native-side crash on some Android drivers.
                try { _video.Stop(); }
                catch (Exception e) { Debug.LogException(e); }
                _video.targetTexture = null;
                DestroyObject(_video);
                _video = null;
            }

            if (_videoTarget != null)
            {
                // A RenderTexture is native memory Unity will not reclaim promptly — the same
                // reason the decoded poster is destroyed explicitly.
                _videoTarget.Release();
                DestroyObject(_videoTarget);
                _videoTarget = null;
            }
        }

        // ── lifetime ──────────────────────────────────────────────────────────────

        private void Update()
        {
            if (_torn || _paused) return;

            _elapsed += Time.unscaledDeltaTime;

            // The deferred teardown from AbandonVideo — one frame after the callback that asked
            // for it, so we are never inside VideoPlayer's native stack when it happens.
            if (_videoReleasePending)
            {
                _videoReleasePending = false;
                ReleaseVideo();
            }

            // Prepare watchdog. Counted on the same unscaled foreground clock as the forced-view
            // window, so a player who backgrounds the app mid-prepare is not charged the wait.
            if (!_videoSettled && _video != null)
            {
                _videoWait += Time.unscaledDeltaTime;
                if (_videoWait >= VideoPrepareTimeoutSeconds)
                {
                    Debug.Log("[GOG SDK] Ad video was still preparing after " +
                              VideoPrepareTimeoutSeconds + "s — showing the creative's poster " +
                              "image instead.");
                    AbandonVideo();
                }
            }

            if (!_closeReady)
            {
                if (_elapsed >= _forcedViewSeconds) RevealCloseControl();
                else if (_ring != null && _forcedViewSeconds > 0f)
                    _ring.fillAmount = Mathf.Clamp01(1f - (_elapsed / _forcedViewSeconds));
            }

            if (_elapsed >= FailsafeMaxLifetimeSeconds)
            {
                Debug.LogError(
                    "[GOG SDK] Ad overlay hit its " + FailsafeMaxLifetimeSeconds + "s failsafe and was " +
                    "torn down. The close control was never used, which usually means UI input is not " +
                    "reaching it — check for a missing or misconfigured EventSystem, or another " +
                    "canvas drawing above sortingOrder " + short.MaxValue + ".");
                // The forced-view window (max 30s) has necessarily elapsed by now, so the player
                // did see the ad: Shown is the truthful outcome.
                Finish(GogAdOutcome.Shown);
            }
        }

        private void RevealCloseControl()
        {
            _closeReady = true;
            if (_ring != null) _ring.fillAmount = 0f;
            if (_closeGlyph != null) _closeGlyph.SetActive(true);
            if (_closeButton != null) _closeButton.interactable = true;
        }

        // Backgrounding stops the forced-view and failsafe clocks explicitly, rather than relying
        // on Update() not running — a game that sets Application.runInBackground = true would
        // otherwise keep both advancing while the player is in a browser or another app.
        private void OnApplicationPause(bool paused)
        {
            _paused = paused;
            // Keep playback in step with the clocks above: a video that ran on in the background
            // would come back mid-way through a cut the player never saw the start of.
            if (_video == null || !_videoPlaying) return;
            try
            {
                if (paused) _video.Pause();
                else _video.Play();
            }
            catch (Exception e) { Debug.LogException(e); }
        }

        private void OnCreativeTapped()
        {
            if (_torn) return;
            string url = _clickUrl;
            if (string.IsNullOrEmpty(url)) return;

            // Defence in depth: the admin API already enforces https on write, but never hand an
            // arbitrary scheme (intent://, file://) to OpenURL.
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogWarning("[GOG SDK] Ignoring a non-http(s) ad click URL.");
                return;
            }

            // Tapping does NOT dismiss. OpenURL backgrounds the game; on return the ad should
            // still be there with its timer intact, and the player dismisses deliberately.
            // Auto-dismissing would drop them back into gameplay disoriented and make an
            // accidental tap destructive.
            Application.OpenURL(url);
        }

        private void OnClosePressed()
        {
            if (_torn || !_closeReady) return;
            Finish(GogAdOutcome.Shown);
        }

        /// <summary>
        /// The one exit. Idempotent via <c>_torn</c>, so a double-tap, a failsafe racing a close,
        /// or a shutdown racing either cannot double-deliver or double-destroy.
        /// </summary>
        private void Finish(GogAdOutcome outcome)
        {
            if (_torn) return;
            _torn = true;

            Action<GogAdOutcome> resolve = _resolve;
            _resolve = null;

            Cleanup();

            if (resolve == null) return;
            // A throwing partner callback is logged, never swallowed and never allowed to escape
            // into Unity's Update loop (mirrors GogHttp.Once).
            try { resolve(outcome); }
            catch (Exception e) { Debug.LogException(e); }
        }

        private void Cleanup()
        {
            if (_instance == this) _instance = null;

            // Before the poster: ReleaseVideo unbinds the RenderTexture the RawImage may still
            // be pointing at, so neither destroy races the other. Runs unconditionally — a
            // teardown deferred by AbandonVideo may still be outstanding, and Cleanup is the
            // last chance to honour it.
            _videoReleasePending = false;
            ReleaseVideo();

            // The decoded texture is ours: DownloadHandlerTexture created it and nothing else
            // references it. Unity does not collect textures promptly, and a 1080x1920 RGBA32
            // decode is ~8 MB — a handful of leaked cycles is an OOM on the target hardware.
            if (_texture != null)
            {
                DestroyObject(_texture);
                _texture = null;
            }

            _ring = null;
            _closeGlyph = null;
            _closeButton = null;

            if (this != null && gameObject != null) DestroyObject(gameObject);
        }

        private static void DestroyObject(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

        // ── test seams (internal; see GogAdImpressionIndex for the rationale) ──────

        internal static GogAdOverlay ActiveForTests => _instance;

        /// <summary>True once a video creative is actually playing (as opposed to still
        /// preparing, or fallen back to its poster).</summary>
        internal bool VideoPlayingForTests => _videoPlaying;

        internal bool CloseControlReadyForTests => _closeReady;

        internal bool CloseControlInteractableForTests => _closeButton != null && _closeButton.interactable;

        /// <summary>Fire the close control the way a pointer would. Batchmode cannot synthesise a
        /// real touch, so this proves the wiring and the interactable gate; the surrounding test
        /// separately asserts an EventSystem exists to deliver that pointer on a device.</summary>
        internal void InvokeCloseForTests()
        {
            if (_closeButton != null) _closeButton.onClick.Invoke();
        }
    }
}
