using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

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

        private Action<GogAdOutcome> _resolve;
        private Texture2D _texture;
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
        internal static GogAdOverlay Show(Texture2D texture, string clickUrl, float forcedViewSeconds,
            Action<GogAdOutcome> resolve)
        {
            var go = new GameObject("GogAdOverlay") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            var overlay = go.AddComponent<GogAdOverlay>();
            _instance = overlay;

            overlay._texture = texture;
            overlay._clickUrl = clickUrl;
            overlay._forcedViewSeconds = forcedViewSeconds;
            overlay._resolve = resolve;
            overlay.Build();
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

            var raw = go.AddComponent<RawImage>();
            raw.texture = _texture;
            raw.raycastTarget = true;

            // Letterbox inside the screen rather than stretching. The aspect comes from the
            // decoded texture, so no server-side width/height is needed.
            var fitter = go.AddComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            fitter.aspectRatio = _texture != null && _texture.height > 0
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

        // ── lifetime ──────────────────────────────────────────────────────────────

        private void Update()
        {
            if (_torn || _paused) return;

            _elapsed += Time.unscaledDeltaTime;

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
        private void OnApplicationPause(bool paused) => _paused = paused;

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
