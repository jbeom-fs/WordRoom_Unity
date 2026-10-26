using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using WordRoom.Settings;

namespace WordRoom.Game
{
    /// <summary>
    /// The settings screen shared by the main menu and the game menu: a prefab (Assets/Prefabs/UI/SettingsPanel)
    /// placed, hidden, in each menu; it is never made at runtime. Rows scroll inside the panel; the title and the
    /// footer (기본값 복원, 닫기) stay fixed, so closing is always reachable at any window or UI size.
    /// Changes apply and are stored at once, except two: the UI size dragged with the pointer applies when it is
    /// let go (keyboard steps apply in the same frame, from LateUpdate, so a first press on the track — which
    /// changes the value before SliderRelease sees the press — never applies early), and the screen mode and size:
    /// those apply with 화면 적용 and must be kept within 15 seconds (unscaled real time), otherwise — or on
    /// 되돌리기, closing the panel or leaving the scene — the previous screen comes back. Only a kept screen setup
    /// is stored. 기본값 복원 resets every preference (never other PlayerPrefs, never the game session) and runs the
    /// default screen through the same confirmation. The controls show the stored values whenever it is open;
    /// texts are rewritten only when the shown value changes.
    /// References and listeners are checked and added once (Initialize, from Awake or from the first Open, so a
    /// panel that starts inactive opens correctly); missing references are reported once and the panel then never
    /// opens. Only the listeners added here are removed again.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SettingsPanel : MonoBehaviour
    {
        /// <summary>"&lt;" button, value text, "&gt;" button.</summary>
        [Serializable]
        public struct Stepper
        {
            public Button previous;
            public Button next;
            public TextMeshProUGUI value;
        }

        [Serializable]
        public struct SliderField
        {
            public Slider slider;
            public TextMeshProUGUI value;
        }

        const float ConfirmSeconds = 15f;

        [Header("게임·조작")]
        [SerializeField] Stepper displayRadius;
        [SerializeField] SliderField mouseSensitivity;
        [SerializeField] Stepper invertMouseY;

        [Header("화면")]
        [SerializeField] SliderField fieldOfView;
        [SerializeField] SliderField uiScale;
        [Tooltip("UI 크기 슬라이더의 포인터 누름·놓기 감지(같은 오브젝트)")]
        [SerializeField] SliderRelease uiScaleRelease;
        [SerializeField] Stepper screenMode;
        [SerializeField] Stepper resolution;
        [SerializeField] Button applyButton;
        [Tooltip("화면 적용 후 유지·되돌리기 확인 줄. 확인 대기 중에만 켜짐")]
        [SerializeField] GameObject confirmRow;
        [SerializeField] TextMeshProUGUI confirmText;
        [SerializeField] Button keepButton;
        [SerializeField] Button revertButton;

        [Header("소리")]
        [SerializeField] SliderField masterVolume;
        [SerializeField] SliderField sfxVolume;
        [Tooltip("효과음 슬라이더를 놓을 때 클릭음으로 새 음량을 들려줌(같은 오브젝트)")]
        [SerializeField] SliderRelease sfxRelease;

        [Header("하단")]
        [SerializeField] Button resetButton;
        [SerializeField] Button closeButton;
        [Tooltip("열 때 키보드 선택을 받을 컨트롤. 비우면 방 표시 거리의 < 버튼")]
        [SerializeField] Selectable firstSelected;

        public event Action Closed;

        readonly List<ButtonBinding> bindings = new List<ButtonBinding>();
        UnityAction<float> onSensitivity, onFieldOfView, onUiScale, onMaster, onSfx;
        Action refreshAll, applyUiScale, playClick;
        bool initialized, ready;

        bool uiScaleDirty;                   // the UI size slider moved and the value is not applied yet

        // Values the texts show now (rewritten only on change).
        int shownRadius = -1, shownInvert = -1, shownUiScale = -1, shownMaster = -1, shownSfx = -1, shownMode = -1;
        float shownSensitivity = float.NaN, shownFieldOfView = float.NaN;
        Vector2Int shownSize = new Vector2Int(-1, -1);

        bool selectedFull;
        List<Vector2Int> sizes = new List<Vector2Int>();
        int sizeIndex;
        bool pending;
        ScreenChoice previous;
        float deadline;
        int shownSeconds = -1;

        public bool IsOpen => gameObject.activeSelf;

        void Awake() => Initialize();

        /// <summary>
        /// Shows the panel with the stored values and selects its first control. False (nothing shown) when its
        /// references are incomplete — the caller keeps its own screen then.
        /// </summary>
        public bool Open()
        {
            if (!Initialize()) return false;
            SelectCurrentScreen();
            if (confirmRow.activeSelf != pending) confirmRow.SetActive(pending);   // left as it was when disabled mid-trial
            if (IsOpen) RefreshAll();
            else gameObject.SetActive(true);                        // OnEnable shows the stored values
            ArchiveUi.Select(firstSelected != null ? firstSelected.gameObject : displayRadius.previous.gameObject);
            return true;
        }

        /// <summary>Closes (an unconfirmed screen change goes back first) and writes the preferences.</summary>
        public void Close()
        {
            if (!IsOpen) return;
            if (pending) RevertScreen();
            GameSettings.Save();
            gameObject.SetActive(false);
            Closed?.Invoke();
        }

        /// <summary>Once: checks every reference, then adds this panel's listeners. Later calls return the result.</summary>
        bool Initialize()
        {
            if (initialized) return ready;
            initialized = true;
            string missing = null;
            Need(displayRadius, "displayRadius", ref missing);
            Need(mouseSensitivity, "mouseSensitivity", ref missing);
            Need(invertMouseY, "invertMouseY", ref missing);
            Need(fieldOfView, "fieldOfView", ref missing);
            Need(uiScale, "uiScale", ref missing);
            ArchiveUi.Need(uiScaleRelease, "uiScaleRelease", ref missing);
            Need(screenMode, "screenMode", ref missing);
            Need(resolution, "resolution", ref missing);
            ArchiveUi.Need(applyButton, "applyButton", ref missing);
            ArchiveUi.Need(confirmRow, "confirmRow", ref missing);
            ArchiveUi.Need(confirmText, "confirmText", ref missing);
            ArchiveUi.Need(keepButton, "keepButton", ref missing);
            ArchiveUi.Need(revertButton, "revertButton", ref missing);
            Need(masterVolume, "masterVolume", ref missing);
            Need(sfxVolume, "sfxVolume", ref missing);
            ArchiveUi.Need(sfxRelease, "sfxRelease", ref missing);
            ArchiveUi.Need(resetButton, "resetButton", ref missing);
            ArchiveUi.Need(closeButton, "closeButton", ref missing);
            if (missing != null)
            {
                Debug.LogError($"[설정] '{name}'의 참조가 비어 있어 설정 화면을 열지 않습니다: {missing}. WordRoom/메인 메뉴와 게임 설정 구성 메뉴나 Inspector에서 SettingsPanel 프리팹의 참조를 확인하세요.", this);
                return ready = false;
            }

            ArchiveUi.Bind(bindings, displayRadius.previous, () => GameSettings.DisplayRadius--);
            ArchiveUi.Bind(bindings, displayRadius.next, () => GameSettings.DisplayRadius++);
            ArchiveUi.Bind(bindings, invertMouseY.previous, ToggleInvert);
            ArchiveUi.Bind(bindings, invertMouseY.next, ToggleInvert);
            ArchiveUi.Bind(bindings, screenMode.previous, ToggleMode);
            ArchiveUi.Bind(bindings, screenMode.next, ToggleMode);
            ArchiveUi.Bind(bindings, resolution.previous, () => StepSize(1));
            ArchiveUi.Bind(bindings, resolution.next, () => StepSize(-1));
            ArchiveUi.Bind(bindings, applyButton, ApplyScreen);
            ArchiveUi.Bind(bindings, keepButton, KeepScreen);
            ArchiveUi.Bind(bindings, revertButton, RevertScreen);
            ArchiveUi.Bind(bindings, resetButton, ResetDefaults);
            ArchiveUi.Bind(bindings, closeButton, Close);

            mouseSensitivity.slider.onValueChanged.AddListener(onSensitivity = v => GameSettings.MouseSensitivity = Mathf.Round(v * 10f) / 10f);
            fieldOfView.slider.onValueChanged.AddListener(onFieldOfView = v => GameSettings.FieldOfView = v);
            uiScale.slider.onValueChanged.AddListener(onUiScale = OnUiScaleChanged);
            masterVolume.slider.onValueChanged.AddListener(onMaster = v => GameSettings.MasterVolume = v / 100f);
            sfxVolume.slider.onValueChanged.AddListener(onSfx = v => GameSettings.SfxVolume = v / 100f);
            uiScaleRelease.Released += applyUiScale = ApplyUiScale;   // dragged: on release
            sfxRelease.Released += playClick = UiSound.Click;          // hear the new level once
            refreshAll = RefreshAll;
            return ready = true;
        }

        static void Need(Stepper s, string field, ref string missing)
        {
            ArchiveUi.Need(s.previous, field + ".previous", ref missing);
            ArchiveUi.Need(s.next, field + ".next", ref missing);
            ArchiveUi.Need(s.value, field + ".value", ref missing);
        }

        static void Need(SliderField s, string field, ref string missing)
        {
            ArchiveUi.Need(s.slider, field + ".slider", ref missing);
            ArchiveUi.Need(s.value, field + ".value", ref missing);
        }

        void OnDestroy()
        {
            if (!ready) return;
            ArchiveUi.Unbind(bindings);
            Remove(mouseSensitivity.slider, onSensitivity);
            Remove(fieldOfView.slider, onFieldOfView);
            Remove(uiScale.slider, onUiScale);
            Remove(masterVolume.slider, onMaster);
            Remove(sfxVolume.slider, onSfx);
            if (uiScaleRelease != null) uiScaleRelease.Released -= applyUiScale;
            if (sfxRelease != null) sfxRelease.Released -= playClick;
        }

        static void Remove(Slider s, UnityAction<float> listener)
        {
            if (s != null) s.onValueChanged.RemoveListener(listener);
        }

        void OnEnable()
        {
            if (!Initialize()) return;
            GameSettings.Changed += refreshAll;
            RefreshAll();
        }

        void OnDisable()
        {
            if (!ready) return;
            GameSettings.Changed -= refreshAll;
            if (uiScaleDirty && !uiScaleRelease.Held) ApplyUiScale();   // a keyboard step in the closing frame
            if (pending) Revert(false);                             // scene change or menu closed around us
        }

        void LateUpdate()
        {
            if (uiScaleDirty && !uiScaleRelease.Held) ApplyUiScale();   // after the EventSystem: a press has set Held by now
            if (pending) UpdateConfirm();
        }

        void UpdateConfirm()
        {
            var left = deadline - Time.unscaledTime;
            if (left <= 0f)
            {
                RevertScreen();
                return;
            }
            var seconds = Mathf.CeilToInt(left);
            if (seconds == shownSeconds) return;
            shownSeconds = seconds;
            var now = ScreenOptions.Current;
            confirmText.text = $"{now.Width} x {now.Height} {(now.FullScreen ? "전체 화면" : "창 모드")}을 유지할까요? {seconds}초 후 이전 화면으로 돌아갑니다";
        }

        // ---- values -----------------------------------------------------------------------------------------
        /// <summary>Shows the stored preferences without triggering their change handlers.</summary>
        void RefreshAll()
        {
            if (!ready) return;
            var radius = GameSettings.DisplayRadius;
            if (radius != shownRadius)
            {
                shownRadius = radius;
                displayRadius.value.text = $"{radius}칸 (최대 {2 * radius * radius + 2 * radius + 1}개)";
            }
            var sensitivity = GameSettings.MouseSensitivity;
            mouseSensitivity.slider.SetValueWithoutNotify(sensitivity);
            if (sensitivity != shownSensitivity)
            {
                shownSensitivity = sensitivity;
                mouseSensitivity.value.text = $"{sensitivity:0.0}배";
            }
            var invert = GameSettings.InvertMouseY ? 1 : 0;
            if (invert != shownInvert)
            {
                shownInvert = invert;
                invertMouseY.value.text = invert == 1 ? "켜짐" : "꺼짐";
            }
            var fov = GameSettings.FieldOfView;
            fieldOfView.slider.SetValueWithoutNotify(fov);
            if (fov != shownFieldOfView)
            {
                shownFieldOfView = fov;
                fieldOfView.value.text = $"{fov:0}도";
            }
            if (!uiScaleRelease.Held && !uiScaleDirty)                // never pull the slider back while it is being changed
            {
                var percent = Mathf.RoundToInt(GameSettings.UiScale * 100f);
                uiScale.slider.SetValueWithoutNotify(percent);
                ShowUiScale(percent);
            }
            ShowPercent(masterVolume, GameSettings.MasterVolume, ref shownMaster);
            ShowPercent(sfxVolume, GameSettings.SfxVolume, ref shownSfx);
            RefreshScreenLabels();
        }

        static void ShowPercent(SliderField field, float value, ref int shown)
        {
            var percent = Mathf.RoundToInt(value * 100f);
            field.slider.SetValueWithoutNotify(percent);
            if (percent == shown) return;
            shown = percent;
            field.value.text = $"{percent}%";
        }

        void ShowUiScale(int percent)
        {
            if (percent == shownUiScale) return;
            shownUiScale = percent;
            uiScale.value.text = $"{percent}%";
        }

        /// <summary>The value text follows at once; the size itself applies in LateUpdate (keyboard) or on release (pointer).</summary>
        void OnUiScaleChanged(float v)
        {
            ShowUiScale(Mathf.RoundToInt(v));
            uiScaleDirty = true;
        }

        void ApplyUiScale()
        {
            uiScaleDirty = false;
            GameSettings.UiScale = uiScale.slider.value / 100f;
        }

        static void ToggleInvert() => GameSettings.InvertMouseY = !GameSettings.InvertMouseY;

        void ResetDefaults()
        {
            GameSettings.ResetToDefaults();
            if (pending) return;                                     // a screen trial is already waiting for an answer
            var d = ScreenOptions.Default;
            SelectScreen(d.FullScreen, new Vector2Int(d.Width, d.Height));
            ApplyScreen();                                            // the default screen also needs confirming
        }

        // ---- screen -----------------------------------------------------------------------------------------
        void SelectCurrentScreen()
        {
            if (pending) return;
            var c = ScreenOptions.Current;
            SelectScreen(c.FullScreen, new Vector2Int(c.Width, c.Height));
        }

        void SelectScreen(bool fullScreen, Vector2Int size)
        {
            selectedFull = fullScreen;
            sizes = ScreenOptions.Sizes(fullScreen);
            sizeIndex = Nearest(sizes, size);
            RefreshScreenLabels();
        }

        static int Nearest(List<Vector2Int> list, Vector2Int size)
        {
            var best = 0;
            for (var i = 1; i < list.Count; i++)
                if (Mathf.Abs(list[i].x * list[i].y - size.x * size.y) < Mathf.Abs(list[best].x * list[best].y - size.x * size.y)) best = i;
            return best;
        }

        void ToggleMode()
        {
            if (pending) return;
            var keep = sizes.Count > 0 ? sizes[sizeIndex] : new Vector2Int(Screen.width, Screen.height);
            SelectScreen(!selectedFull, keep);
        }

        void StepSize(int step)
        {
            if (pending || sizes.Count == 0) return;
            sizeIndex = Mathf.Clamp(sizeIndex + step, 0, sizes.Count - 1);
            RefreshScreenLabels();
        }

        void RefreshScreenLabels()
        {
            if (!ready) return;
            var mode = selectedFull ? 1 : 0;
            if (mode != shownMode)
            {
                shownMode = mode;
                screenMode.value.text = selectedFull ? "전체 화면" : "창 모드";
            }
            var size = sizes.Count > 0 ? sizes[sizeIndex] : new Vector2Int(Screen.width, Screen.height);
            if (size != shownSize)
            {
                shownSize = size;
                resolution.value.text = $"{size.x} x {size.y}";
            }
            applyButton.interactable = !pending;
        }

        /// <summary>Tries the selected screen; it must be kept within 15 seconds or it goes back.</summary>
        void ApplyScreen()
        {
            if (pending || sizes.Count == 0) return;
            var size = sizes[sizeIndex];
            var target = ScreenOptions.Validate(new ScreenChoice(size.x, size.y, selectedFull));
            var now = ScreenOptions.Current;
            if (target.SameAs(now)) return;                          // nothing to try
            previous = now;
            ScreenOptions.Apply(target);
            pending = true;
            deadline = Time.unscaledTime + ConfirmSeconds;
            shownSeconds = -1;
            confirmText.text = "";
            confirmRow.SetActive(true);
            RefreshScreenLabels();
            ArchiveUi.Select(keepButton.gameObject);
        }

        /// <summary>Stores what is really on screen now (the applied result, not just the selection).</summary>
        void KeepScreen()
        {
            if (!pending) return;
            pending = false;
            var now = ScreenOptions.Current;
            GameSettings.CommitScreen(now.Width, now.Height, now.FullScreen);
            confirmRow.SetActive(false);
            SelectScreen(now.FullScreen, new Vector2Int(now.Width, now.Height));
            ArchiveUi.Select(applyButton.gameObject);
        }

        void RevertScreen() => Revert(true);

        /// <summary>
        /// The previous screen comes back. While the panel is being disabled (`visible` false) its child row is not
        /// switched — the hierarchy is changing state — and Open sets it again.
        /// </summary>
        void Revert(bool visible)
        {
            if (!pending) return;
            pending = false;
            ScreenOptions.Apply(previous);
            if (visible) confirmRow.SetActive(false);
            SelectScreen(previous.FullScreen, new Vector2Int(previous.Width, previous.Height));
            if (visible && IsOpen) ArchiveUi.Select(applyButton.gameObject);
        }
    }
}
