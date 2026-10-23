using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using WordRoom.Settings;

namespace WordRoom.Game
{
    /// <summary>
    /// The settings screen shared by the main menu and the game menu. Rows scroll inside the panel; the title and
    /// the footer (기본값 복원, 닫기) stay fixed, so closing is always reachable at any window or UI size.
    /// Changes apply and are stored at once (UI size on slider release when dragged), except the screen mode and
    /// size: those apply with 화면 적용 and must be kept within 15 seconds (unscaled real time), otherwise — or on
    /// 되돌리기, closing the panel or leaving the scene — the previous screen comes back. Only a kept screen setup
    /// is stored. 기본값 복원 resets every preference (never other PlayerPrefs, never the game session) and runs the
    /// default screen through the same confirmation. The controls always show the stored values when opened.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SettingsPanel : MonoBehaviour
    {
        const float ConfirmSeconds = 15f;
        const float LabelWidth = 250f;
        const float ValueWidth = 130f;
        const float RowHeight = 52f;

        public event Action Closed;

        TextMeshProUGUI radiusValue, invertValue, modeValue, sizeValue, confirmText;
        Slider sensitivity, fieldOfView, uiScale, masterVolume, sfxVolume;
        TextMeshProUGUI sensitivityValue, fieldOfViewValue, uiScaleValue, masterValue, sfxValue;
        SliderRelease uiScaleRelease;
        GameObject confirmRow, first;
        Button applyButton, keepButton;

        bool selectedFull;
        List<Vector2Int> sizes = new List<Vector2Int>();
        int sizeIndex;
        bool pending;
        ScreenChoice previous;
        float deadline;
        int shownSeconds = -1;

        public bool IsOpen => gameObject.activeSelf;

        /// <summary>Builds the (hidden) panel on `canvas`: centered, 960 wide, as tall as the canvas allows.</summary>
        public static SettingsPanel Create(Transform canvas)
        {
            var r = ArchiveUi.Panel(canvas, "Settings");
            r.anchorMin = new Vector2(0.5f, 0f);
            r.anchorMax = new Vector2(0.5f, 1f);
            r.pivot = new Vector2(0.5f, 0.5f);
            r.sizeDelta = new Vector2(960f, -96f);                // 48 px from the top and bottom edges
            r.anchoredPosition = Vector2.zero;
            var panel = r.gameObject.AddComponent<SettingsPanel>();
            panel.Build(r);
            r.gameObject.SetActive(false);
            return panel;
        }

        public void Open()
        {
            gameObject.SetActive(true);
            SelectCurrentScreen();
            RefreshAll();
            ArchiveUi.Select(first);
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

        void OnDisable()
        {
            if (pending) RevertScreen();                            // scene change or menu closed around us
        }

        void Update()
        {
            if (!pending) return;
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

        // ---- build ------------------------------------------------------------------------------------------
        void Build(RectTransform r)
        {
            var layout = ArchiveUi.Vertical(r, 18f, 36, TextAnchor.UpperCenter);
            layout.childForceExpandWidth = true;
            var title = ArchiveUi.AddText(r, "설정", 40f, ArchiveUi.Ivory, TextAlignmentOptions.Left);
            ArchiveUi.Size(title.gameObject, -1f, 56f);

            var scroll = ArchiveUi.AddScroll(r, out var content);
            ArchiveUi.Size(scroll.gameObject, -1f, 200f, -1f, 1f);   // takes the remaining height

            Section(content, "게임");
            radiusValue = Stepper(content, "방 표시 거리", () => GameSettings.DisplayRadius--, () => GameSettings.DisplayRadius++, out var radiusPrev);
            first = radiusPrev.gameObject;

            Section(content, "조작");
            sensitivity = SliderRow(content, "마우스 감도", GameSettings.MinMouseSensitivity, GameSettings.MaxMouseSensitivity, false, out sensitivityValue);
            sensitivity.onValueChanged.AddListener(v => GameSettings.MouseSensitivity = Mathf.Round(v * 10f) / 10f);
            invertValue = Stepper(content, "마우스 Y축 반전", ToggleInvert, ToggleInvert, out _);

            Section(content, "화면");
            fieldOfView = SliderRow(content, "시야각", GameSettings.MinFieldOfView, GameSettings.MaxFieldOfView, true, out fieldOfViewValue);
            fieldOfView.onValueChanged.AddListener(v => GameSettings.FieldOfView = v);
            uiScale = SliderRow(content, "UI 크기", GameSettings.MinUiScale * 100f, GameSettings.MaxUiScale * 100f, true, out uiScaleValue);
            uiScaleRelease = uiScale.gameObject.AddComponent<SliderRelease>();
            uiScale.onValueChanged.AddListener(v =>
            {
                uiScaleValue.text = $"{v:0}%";
                if (!uiScaleRelease.Held) GameSettings.UiScale = v / 100f;           // keyboard: at once
            });
            uiScaleRelease.Released += () => GameSettings.UiScale = uiScale.value / 100f;   // dragged: on release
            modeValue = Stepper(content, "화면 모드", ToggleMode, ToggleMode, out _);
            sizeValue = Stepper(content, "해상도", () => StepSize(1), () => StepSize(-1), out _);

            var applyRow = Row(content);
            Spacer(applyRow, LabelWidth);
            applyButton = ArchiveUi.AddButton(applyRow, "화면 적용", ApplyScreen, 200f, 48f, 22f);
            var note = ArchiveUi.AddText(applyRow, "적용 후 15초 안에 유지를 선택하세요", 18f, ArchiveUi.IvoryDim, TextAlignmentOptions.Left);
            ArchiveUi.Size(note.gameObject, -1f, 48f, 1f);

            confirmRow = Row(content, 96f).gameObject;
            confirmText = ArchiveUi.AddText(confirmRow.transform, "", 20f, ArchiveUi.Ivory, TextAlignmentOptions.Left);
            ArchiveUi.Size(confirmText.gameObject, -1f, 96f, 1f);
            keepButton = ArchiveUi.AddButton(confirmRow.transform, "유지", KeepScreen, 120f, 48f, 22f);
            ArchiveUi.AddButton(confirmRow.transform, "되돌리기", RevertScreen, 150f, 48f, 22f);
            confirmRow.SetActive(false);

            Section(content, "소리");
            masterVolume = SliderRow(content, "전체 음량", 0f, 100f, true, out masterValue);
            masterVolume.onValueChanged.AddListener(v => GameSettings.MasterVolume = v / 100f);
            sfxVolume = SliderRow(content, "효과음 음량", 0f, 100f, true, out sfxValue);
            sfxVolume.onValueChanged.AddListener(v => GameSettings.SfxVolume = v / 100f);
            sfxVolume.gameObject.AddComponent<SliderRelease>().Released += UiSound.Click;   // hear the new level once

            var footer = ArchiveUi.NewRect("Footer", r);
            ArchiveUi.Horizontal(footer, 16f, TextAnchor.MiddleRight);
            ArchiveUi.Size(footer.gameObject, -1f, 60f);
            ArchiveUi.AddButton(footer, "기본값 복원", ResetDefaults, 220f, 56f, 22f);
            ArchiveUi.AddButton(footer, "닫기", Close, 180f, 56f, 22f);

            GameSettings.Changed += RefreshAll;
        }

        void OnDestroy() => GameSettings.Changed -= RefreshAll;

        static RectTransform Row(RectTransform content, float height = RowHeight)
        {
            var row = ArchiveUi.NewRect("Row", content);
            ArchiveUi.Horizontal(row, 12f, TextAnchor.MiddleLeft);
            ArchiveUi.Size(row.gameObject, -1f, height);
            return row;
        }

        static void Spacer(RectTransform row, float width) => ArchiveUi.Size(ArchiveUi.NewRect("Spacer", row).gameObject, width, 1f);

        static void Section(RectTransform content, string title)
        {
            var t = ArchiveUi.AddText(content, title, 24f, ArchiveUi.Brass, TextAlignmentOptions.BottomLeft);
            ArchiveUi.Size(t.gameObject, -1f, 44f);
        }

        static TextMeshProUGUI Label(RectTransform row, string text)
        {
            var t = ArchiveUi.AddText(row, text, 22f, ArchiveUi.IvoryDim, TextAlignmentOptions.Left);
            ArchiveUi.Size(t.gameObject, LabelWidth, RowHeight);
            return t;
        }

        /// <summary>Label, "&lt;" button, value, "&gt;" button. Returns the value text.</summary>
        static TextMeshProUGUI Stepper(RectTransform content, string label, Action previousOption, Action nextOption, out Button previousButton)
        {
            var row = Row(content);
            Label(row, label);
            previousButton = ArchiveUi.AddButton(row, "<", previousOption, 56f, 46f, 24f);
            var value = ArchiveUi.AddText(row, "", 22f, ArchiveUi.Ivory, TextAlignmentOptions.Center);
            ArchiveUi.Size(value.gameObject, 300f, RowHeight);
            ArchiveUi.AddButton(row, ">", nextOption, 56f, 46f, 24f);
            return value;
        }

        static Slider SliderRow(RectTransform content, string label, float min, float max, bool whole, out TextMeshProUGUI value)
        {
            var row = Row(content);
            Label(row, label);
            var slider = ArchiveUi.AddSlider(row, min, max, whole, 400f);
            value = ArchiveUi.AddText(row, "", 22f, ArchiveUi.Ivory, TextAlignmentOptions.Right);
            ArchiveUi.Size(value.gameObject, ValueWidth, RowHeight);
            return slider;
        }

        // ---- values -----------------------------------------------------------------------------------------
        /// <summary>Shows the stored preferences without triggering their change handlers.</summary>
        void RefreshAll()
        {
            if (radiusValue == null) return;
            var radius = GameSettings.DisplayRadius;
            radiusValue.text = $"{radius}칸 (최대 {2 * radius * radius + 2 * radius + 1}개)";
            sensitivity.SetValueWithoutNotify(GameSettings.MouseSensitivity);
            sensitivityValue.text = $"{GameSettings.MouseSensitivity:0.0}배";
            invertValue.text = GameSettings.InvertMouseY ? "켜짐" : "꺼짐";
            fieldOfView.SetValueWithoutNotify(GameSettings.FieldOfView);
            fieldOfViewValue.text = $"{GameSettings.FieldOfView:0}도";
            if (!uiScaleRelease.Held)
            {
                uiScale.SetValueWithoutNotify(Mathf.Round(GameSettings.UiScale * 100f));
                uiScaleValue.text = $"{GameSettings.UiScale * 100f:0}%";
            }
            masterVolume.SetValueWithoutNotify(Mathf.Round(GameSettings.MasterVolume * 100f));
            masterValue.text = $"{GameSettings.MasterVolume * 100f:0}%";
            sfxVolume.SetValueWithoutNotify(Mathf.Round(GameSettings.SfxVolume * 100f));
            sfxValue.text = $"{GameSettings.SfxVolume * 100f:0}%";
            RefreshScreenLabels();
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
            if (modeValue == null) return;
            modeValue.text = selectedFull ? "전체 화면" : "창 모드";
            sizeValue.text = sizes.Count > 0 ? $"{sizes[sizeIndex].x} x {sizes[sizeIndex].y}" : $"{Screen.width} x {Screen.height}";
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

        void RevertScreen()
        {
            if (!pending) return;
            pending = false;
            ScreenOptions.Apply(previous);
            if (confirmRow != null) confirmRow.SetActive(false);
            SelectScreen(previous.FullScreen, new Vector2Int(previous.Width, previous.Height));
            if (IsOpen && applyButton != null) ArchiveUi.Select(applyButton.gameObject);
        }
    }
}
