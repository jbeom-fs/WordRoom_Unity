using System;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using WordRoom.Game;
using WordRoom.Settings;
using WordRoom.World;
using Object = UnityEngine.Object;

namespace WordRoom.EditorTools
{
    /// <summary>
    /// Editor-only construction of the archive-style screen UI (moved from the former runtime ArchiveUi/SettingsPanel/
    /// MainMenuController/PauseMenu/WordRoomHud/PlayHint builders, same look): built-in UI and TMP only, dark walnut
    /// panels with a 4-unit brass frame, warm ivory text in the project's Korean font. Every canvas is Screen Space
    /// Overlay on the 1920×1080 reference (match 0.5) with UiScaleBinding; menus have a GraphicRaycaster, the HUD
    /// and the hint have none and no raycast targets. Buttons get their colors here but no click listeners: the
    /// runtime components add those once by code (with the click sound), so nothing is connected twice.
    /// Each Build* returns a new, unsaved root object; GameMenuSetup saves it as a prefab and destroys it. Internal
    /// references of a prefab (SettingsPanel) are connected here; scene components are connected by GameMenuSetup.
    /// Names given here are the paths GameMenuSetup uses to connect scene references.
    /// </summary>
    public static class ArchiveUiBuilder
    {
        public const string PrefabFolder = "Assets/Prefabs/UI";
        public const string SettingsPrefabPath = PrefabFolder + "/SettingsPanel.prefab";
        public const string MainMenuPrefabPath = PrefabFolder + "/MainMenuCanvas.prefab";
        public const string GameMenuPrefabPath = PrefabFolder + "/GameMenuCanvas.prefab";
        public const string HudPrefabPath = PrefabFolder + "/HudCanvas.prefab";
        public const string HintPrefabPath = PrefabFolder + "/PlayHintCanvas.prefab";
        public const string ResultPrefabPath = PrefabFolder + "/ResultCanvas.prefab";
        /// <summary>Outline preset for PlayHint (TMP shows presets named after the font). Saved as an asset so the outline survives in the prefab.</summary>
        public const string HintMaterialPath = "Assets/Font/Pretendard-Black SDF - Outline.mat";

        public const int HudOrder = 50, HintOrder = 100, MainMenuOrder = 100, GameMenuOrder = 200, ResultOrder = 300;

        const float LabelWidth = 250f;
        const float ValueWidth = 130f;
        const float RowHeight = 52f;
        /// <summary>Frame thickness in canvas units (1920×1080 reference, scaled like the rest of the UI).</summary>
        public const float FrameThickness = 4f;

        /// <summary>Font for every text made here (set by GameMenuSetup before building).</summary>
        public static TMP_FontAsset Font { get; set; }

        // ---- prefabs ------------------------------------------------------------------------------------------
        /// <summary>Settings panel: centered, 960 wide, 48 px from the top and bottom of the canvas. Active root (menus hide their instance).</summary>
        public static GameObject BuildSettingsPanel()
        {
            var r = Panel(null, "SettingsPanel");
            r.anchorMin = new Vector2(0.5f, 0f);
            r.anchorMax = new Vector2(0.5f, 1f);
            r.pivot = new Vector2(0.5f, 0.5f);
            r.sizeDelta = new Vector2(960f, -96f);
            r.anchoredPosition = Vector2.zero;

            var layout = Vertical(r, 18f, 36, TextAnchor.UpperCenter);
            layout.childForceExpandWidth = true;
            var title = AddText(r, "Title", "설정", 40f, ArchiveUi.Ivory, TextAlignmentOptions.Left);
            Size(title.gameObject, -1f, 56f);

            var scroll = AddScroll(r, out var content);
            Size(scroll.gameObject, -1f, 200f, -1f, 1f);                  // takes the remaining height

            Section(content, "게임");
            var radius = Stepper(content, "DisplayRadius", "방 표시 거리", "1칸 (최대 5개)");

            Section(content, "조작");
            var sensitivity = SliderRow(content, "MouseSensitivity", "마우스 감도", GameSettings.MinMouseSensitivity, GameSettings.MaxMouseSensitivity, false, GameSettings.DefaultMouseSensitivity, "1.0배");
            var invert = Stepper(content, "InvertMouseY", "마우스 Y축 반전", "꺼짐");

            Section(content, "화면");
            var fov = SliderRow(content, "FieldOfView", "시야각", GameSettings.MinFieldOfView, GameSettings.MaxFieldOfView, true, GameSettings.DefaultFieldOfView, "60도");
            var uiScale = SliderRow(content, "UiScale", "UI 크기", GameSettings.MinUiScale * 100f, GameSettings.MaxUiScale * 100f, true, 100f, "100%");
            var uiScaleRelease = uiScale.slider.gameObject.AddComponent<SliderRelease>();
            var mode = Stepper(content, "ScreenMode", "화면 모드", "전체 화면");
            var size = Stepper(content, "Resolution", "해상도", "1920 x 1080");

            var applyRow = Row(content, "ApplyRow");
            Spacer(applyRow, LabelWidth);
            var applyButton = AddButton(applyRow, "ApplyButton", "화면 적용", 200f, 48f, 22f);
            var note = AddText(applyRow, "Note", "적용 후 15초 안에 유지를 선택하세요", 18f, ArchiveUi.IvoryDim, TextAlignmentOptions.Left);
            Size(note.gameObject, -1f, 48f, 1f);

            var confirmRow = Row(content, "ConfirmRow", 96f).gameObject;
            var confirmText = AddText(confirmRow.transform, "ConfirmText", "", 20f, ArchiveUi.Ivory, TextAlignmentOptions.Left);
            Size(confirmText.gameObject, -1f, 96f, 1f);
            var keepButton = AddButton(confirmRow.transform, "KeepButton", "유지", 120f, 48f, 22f);
            var revertButton = AddButton(confirmRow.transform, "RevertButton", "되돌리기", 150f, 48f, 22f);
            confirmRow.SetActive(false);

            Section(content, "소리");
            var master = SliderRow(content, "MasterVolume", "전체 음량", 0f, 100f, true, 100f, "100%");
            var sfx = SliderRow(content, "SfxVolume", "효과음 음량", 0f, 100f, true, 100f, "100%");
            var sfxRelease = sfx.slider.gameObject.AddComponent<SliderRelease>();

            var footer = NewRect("Footer", r);
            Horizontal(footer, 16f, TextAnchor.MiddleRight);
            Size(footer.gameObject, -1f, 60f);
            var resetButton = AddButton(footer, "ResetButton", "기본값 복원", 220f, 56f, 22f);
            var closeButton = AddButton(footer, "CloseButton", "닫기", 180f, 56f, 22f);

            var panel = r.gameObject.AddComponent<SettingsPanel>();
            var so = new SerializedObject(panel);
            SetStepper(so, "displayRadius", radius);
            SetSlider(so, "mouseSensitivity", sensitivity);
            SetStepper(so, "invertMouseY", invert);
            SetSlider(so, "fieldOfView", fov);
            SetSlider(so, "uiScale", uiScale);
            Set(so, "uiScaleRelease", uiScaleRelease);
            SetStepper(so, "screenMode", mode);
            SetStepper(so, "resolution", size);
            Set(so, "applyButton", applyButton);
            Set(so, "confirmRow", confirmRow);
            Set(so, "confirmText", confirmText);
            Set(so, "keepButton", keepButton);
            Set(so, "revertButton", revertButton);
            SetSlider(so, "masterVolume", master);
            SetSlider(so, "sfxVolume", sfx);
            Set(so, "sfxRelease", sfxRelease);
            Set(so, "resetButton", resetButton);
            Set(so, "closeButton", closeButton);
            Set(so, "firstSelected", radius.previous);
            so.ApplyModifiedPropertiesWithoutUndo();
            return r.gameObject;
        }

        /// <summary>Main menu canvas (order 100): Main, Difficulty (inactive) and a hidden SettingsPanel instance.</summary>
        public static GameObject BuildMainMenuCanvas(GameObject settingsPrefab)
        {
            var root = CanvasRoot("MainMenu Canvas", MainMenuOrder, true);

            var main = Panel(root.transform, "Main");
            Center(main, 520f);
            Vertical(main, 20f, 48, TextAnchor.UpperCenter);
            var title = AddText(main, "Title", "WordRoom", 84f, ArchiveUi.Ivory, TextAlignmentOptions.Center);
            Size(title.gameObject, 424f, 120f);
            AddButton(main, "StartButton", "시작", 360f, 68f, 28f);
            AddButton(main, "SettingsButton", "설정", 360f, 68f, 28f);
            AddButton(main, "QuitButton", "종료", 360f, 68f, 28f);

            var difficulty = Panel(root.transform, "Difficulty");
            Center(difficulty, 760f);
            Vertical(difficulty, 18f, 44, TextAnchor.UpperCenter);
            var heading = AddText(difficulty, "Title", "난이도 선택", 44f, ArchiveUi.Ivory, TextAlignmentOptions.Center);
            Size(heading.gameObject, 672f, 64f);
            foreach (var d in new[] { Difficulty.Easy, Difficulty.Normal, Difficulty.Hard })
            {
                var label = $"{GameLaunch.Label(d)}\n<size=20><color=#D8D0BF>{GameLaunch.Description(d)}</color></size>";
                AddButton(difficulty, $"{d}Button", label, 640f, 96f, 30f);
            }
            var message = AddText(difficulty, "Message", "", 20f, ArchiveUi.IvoryDim, TextAlignmentOptions.Center);
            Size(message.gameObject, 640f, 30f);
            var footer = NewRect("Footer", difficulty);
            Horizontal(footer, 20f, TextAnchor.MiddleCenter);
            Size(footer.gameObject, 640f, 68f);
            AddButton(footer, "BackButton", "뒤로", 260f, 64f, 26f);
            AddButton(footer, "StartGameButton", "게임 시작", 260f, 64f, 26f);
            difficulty.gameObject.SetActive(false);

            AddSettings(root.transform, settingsPrefab);
            return root;
        }

        /// <summary>Game menu canvas (order 200): dim overlay catching clicks, the Menu list and a hidden SettingsPanel instance.</summary>
        public static GameObject BuildGameMenuCanvas(GameObject settingsPrefab)
        {
            var root = CanvasRoot("GameMenu Canvas", GameMenuOrder, true);   // above the HUD (50) and PlayHint (100)
            var overlay = NewRect("Overlay", root.transform);
            Stretch(overlay);
            AddImage(overlay.gameObject, ArchiveUi.Dim, true);                // clicks never reach the game

            var list = Panel(root.transform, "Menu");
            list.anchorMin = list.anchorMax = list.pivot = new Vector2(0.5f, 0.5f);
            list.sizeDelta = new Vector2(480f, 0f);
            list.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            Vertical(list, 18f, 44, TextAnchor.UpperCenter);
            var title = AddText(list, "Title", "메뉴", 40f, ArchiveUi.Ivory, TextAlignmentOptions.Center);
            Size(title.gameObject, 392f, 60f);
            AddButton(list, "ContinueButton", "계속", 360f, 64f, 26f);
            AddButton(list, "SettingsButton", "설정", 360f, 64f, 26f);
            AddButton(list, "MainMenuButton", "메인 메뉴", 360f, 64f, 26f);
            AddButton(list, "QuitButton", "종료", 360f, 64f, 26f);

            AddSettings(root.transform, settingsPrefab);
            return root;
        }

        /// <summary>
        /// Goal result canvas (order 300, above the game menu): dim overlay catching clicks, a centered framed panel
        /// with 목표 달성, the 소요 시간 and 방문 방 수 rows (values written by ResultScreen) and the 메인 메뉴 button.
        /// The root (ResultScreen.display, with its Canvas as ResultScreen.canvas) is saved active and enabled; the
        /// scene instance is placed inactive and ResultScreen shows it only after checking that Canvas and the button.
        /// </summary>
        public static GameObject BuildResultCanvas()
        {
            var root = CanvasRoot("Result Canvas", ResultOrder, true);
            var overlay = NewRect("Overlay", root.transform);
            Stretch(overlay);
            AddImage(overlay.gameObject, ArchiveUi.Dim, true);                // clicks never reach the game

            var panel = Panel(root.transform, "Result");
            Center(panel, 560f);
            Vertical(panel, 20f, 48, TextAnchor.UpperCenter);
            var title = AddText(panel, "Title", "목표 달성", 56f, ArchiveUi.Ivory, TextAlignmentOptions.Center);
            Size(title.gameObject, 464f, 80f);
            ResultRow(panel, "TimeRow", "소요 시간", "00:00");
            ResultRow(panel, "VisitedRow", "방문 방 수", "1개");
            var spacer = NewRect("Spacer", panel);
            Size(spacer.gameObject, 464f, 8f);
            AddButton(panel, "MainMenuButton", "메인 메뉴", 360f, 68f, 28f);
            return root;
        }

        /// <summary>Label (left) and value (right, child "Value") in one 464×52 row.</summary>
        static void ResultRow(RectTransform panel, string name, string label, string preview)
        {
            var row = NewRect(name, panel);
            Horizontal(row, 12f, TextAnchor.MiddleLeft);
            Size(row.gameObject, 464f, RowHeight);
            var l = AddText(row, "Label", label, 26f, ArchiveUi.IvoryDim, TextAlignmentOptions.Left);
            Size(l.gameObject, 200f, RowHeight);
            var v = AddText(row, "Value", preview, 32f, ArchiveUi.Ivory, TextAlignmentOptions.Right);
            Size(v.gameObject, 252f, RowHeight);
        }

        /// <summary>
        /// HUD canvas (order 50, no raycaster): top-left (32, -32) 320×96 panel, walnut background ~90%, 4-unit brass edge,
        /// labels 18px and words 24px in 232px fields. The Canvas is saved disabled (enable it to preview while editing);
        /// WordRoomHud turns it on with the first words.
        /// </summary>
        public static GameObject BuildHudCanvas()
        {
            const float padding = 16f, rowHeight = 28f, rowGap = 8f, labelWidth = 44f, labelGap = 12f, wordWidth = 232f;
            var root = CanvasRoot("HUD Canvas", HudOrder, false);
            var panel = TopLeft("Panel", root.transform, new Vector2(32f, -32f), new Vector2(320f, 96f));
            var background = NewRect("Background", panel);
            Stretch(background);
            AddImage(background.gameObject, new Color32(0x49, 0x35, 0x2A, 230), false);   // #49352A, ~90%
            AddFrame(panel);
            for (var i = 0; i < 2; i++)
            {
                var y = -(padding + i * (rowHeight + rowGap));
                var label = HudText(panel, $"Label{i}", new Vector2(padding, y), new Vector2(labelWidth, rowHeight), 18f, ArchiveUi.IvoryDim);
                label.text = i == 0 ? "현재" : "목표";
                var word = HudText(panel, i == 0 ? "CurrentWord" : "TargetWord", new Vector2(padding + labelWidth + labelGap, y), new Vector2(wordWidth, rowHeight), 24f, ArchiveUi.Ivory);
                word.text = i == 0 ? "커피" : "우유";                         // preview only; cleared before the first Show
            }
            root.GetComponent<Canvas>().enabled = false;
            return root;
        }

        /// <summary>
        /// Hint canvas (order 100, no raycaster): one centered 1200×200 text at (0, -90), 30px, white with a black
        /// outline (material preset `outline`). The Canvas is saved disabled (enable it to preview); PlayHint turns it on.
        /// </summary>
        public static GameObject BuildPlayHintCanvas(Material outline)
        {
            var root = CanvasRoot("PlayHint Canvas", HintOrder, false);
            var r = NewRect("Text", root.transform);
            var label = r.gameObject.AddComponent<TextMeshProUGUI>();
            if (Font != null) label.font = Font;
            if (outline != null) label.fontSharedMaterial = outline;
            label.text = "[E] 문 열기";                                        // preview only
            label.fontSize = 30f;
            label.alignment = TextAlignmentOptions.Center;
            label.color = Color.white;
            label.raycastTarget = false;
            r.anchorMin = r.anchorMax = r.pivot = new Vector2(0.5f, 0.5f);
            r.sizeDelta = new Vector2(1200f, 200f);
            r.anchoredPosition = new Vector2(0f, -90f);
            root.GetComponent<Canvas>().enabled = false;
            return root;
        }

        /// <summary>The PlayHint outline (width 0.2, black) as a copy of the font's material.</summary>
        public static Material NewHintMaterial(TMP_FontAsset font)
        {
            var m = new Material(font.material) { name = "Pretendard-Black SDF - Outline" };
            m.SetFloat("_OutlineWidth", 0.2f);
            m.SetColor("_OutlineColor", Color.black);
            m.EnableKeyword("OUTLINE_ON");
            return m;
        }

        // ---- canvas -------------------------------------------------------------------------------------------
        static GameObject CanvasRoot(string name, int sortingOrder, bool raycaster)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            var s = go.AddComponent<CanvasScaler>();
            s.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            s.referenceResolution = UiScaleBinding.ReferenceResolution;
            s.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            s.matchWidthOrHeight = 0.5f;
            go.AddComponent<UiScaleBinding>();
            if (raycaster) go.AddComponent<GraphicRaycaster>();
            return go;
        }

        static void AddSettings(Transform canvas, GameObject settingsPrefab)
        {
            var settings = (GameObject)PrefabUtility.InstantiatePrefab(settingsPrefab);
            settings.name = "SettingsPanel";
            settings.transform.SetParent(canvas, false);
            settings.SetActive(false);
        }

        // ---- building blocks ----------------------------------------------------------------------------------
        public static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var r = (RectTransform)go.transform;
            if (parent != null) r.SetParent(parent, false);
            return r;
        }

        public static void Stretch(RectTransform r, float inset = 0f)
        {
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.pivot = new Vector2(0.5f, 0.5f);
            r.offsetMin = new Vector2(inset, inset);
            r.offsetMax = new Vector2(-inset, -inset);
        }

        static RectTransform TopLeft(string name, Transform parent, Vector2 position, Vector2 size)
        {
            var r = NewRect(name, parent);
            r.anchorMin = r.anchorMax = r.pivot = new Vector2(0f, 1f);
            r.anchoredPosition = position;
            r.sizeDelta = size;
            return r;
        }

        public static Image AddImage(GameObject go, Color32 color, bool raycast)
        {
            var image = go.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = raycast;
            return image;
        }

        /// <summary>A brass line FrameThickness (4) canvas units thick on each edge, inside the rect, never catching clicks.</summary>
        public static void AddFrame(RectTransform r)
        {
            Edge(r, "FrameTop", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, FrameThickness), new Vector2(0.5f, 1f));
            Edge(r, "FrameBottom", new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, FrameThickness), new Vector2(0.5f, 0f));
            Edge(r, "FrameLeft", new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(FrameThickness, 0f), new Vector2(0f, 0.5f));
            Edge(r, "FrameRight", new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(FrameThickness, 0f), new Vector2(1f, 0.5f));
        }

        static void Edge(RectTransform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 size, Vector2 pivot)
        {
            var r = NewRect(name, parent);
            r.anchorMin = anchorMin;
            r.anchorMax = anchorMax;
            r.pivot = pivot;
            r.anchoredPosition = Vector2.zero;
            r.sizeDelta = size;
            AddImage(r.gameObject, ArchiveUi.Brass, false);
            r.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
        }

        /// <summary>A walnut panel with a brass frame. It catches clicks so nothing behind it reacts.</summary>
        public static RectTransform Panel(Transform parent, string name)
        {
            var r = NewRect(name, parent);
            AddImage(r.gameObject, ArchiveUi.PanelWalnut, true);
            AddFrame(r);
            return r;
        }

        /// <summary>Centered panel of fixed width whose height follows its content.</summary>
        static void Center(RectTransform panel, float width)
        {
            panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0.5f, 0.5f);
            panel.sizeDelta = new Vector2(width, 0f);
            panel.anchoredPosition = Vector2.zero;
            panel.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        public static LayoutElement Size(GameObject go, float width, float height, float flexibleWidth = -1f, float flexibleHeight = -1f)
        {
            var e = go.GetComponent<LayoutElement>();
            if (e == null) e = go.AddComponent<LayoutElement>();
            if (width >= 0f) e.preferredWidth = e.minWidth = width;
            if (height >= 0f) e.preferredHeight = e.minHeight = height;
            e.flexibleWidth = flexibleWidth;
            e.flexibleHeight = flexibleHeight;
            return e;
        }

        public static VerticalLayoutGroup Vertical(RectTransform r, float spacing, int padding, TextAnchor align)
        {
            var v = r.gameObject.AddComponent<VerticalLayoutGroup>();
            v.spacing = spacing;
            v.padding = new RectOffset(padding, padding, padding, padding);
            v.childAlignment = align;
            v.childControlWidth = true;
            v.childControlHeight = true;
            v.childForceExpandWidth = false;
            v.childForceExpandHeight = false;
            return v;
        }

        public static HorizontalLayoutGroup Horizontal(RectTransform r, float spacing, TextAnchor align)
        {
            var h = r.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = spacing;
            h.childAlignment = align;
            h.childControlWidth = true;
            h.childControlHeight = true;
            h.childForceExpandWidth = false;
            h.childForceExpandHeight = false;
            return h;
        }

        public static TextMeshProUGUI AddText(Transform parent, string name, string text, float size, Color32 color, TextAlignmentOptions align)
        {
            var r = NewRect(name, parent);
            var t = r.gameObject.AddComponent<TextMeshProUGUI>();
            if (Font != null) t.font = Font;
            t.text = text;
            t.fontSize = size;
            t.color = color;
            t.alignment = align;
            t.textWrappingMode = TextWrappingModes.Normal;
            t.raycastTarget = false;
            return t;
        }

        /// <summary>A HUD text: fixed size, one line, never cut vertically, no auto size (WordRoomHud fits the width).</summary>
        static TextMeshProUGUI HudText(RectTransform panel, string name, Vector2 position, Vector2 size, float fontSize, Color32 color)
        {
            var r = TopLeft(name, panel, position, size);
            var t = r.gameObject.AddComponent<TextMeshProUGUI>();
            if (Font != null) t.font = Font;
            t.fontSize = fontSize;
            t.enableAutoSizing = false;
            t.color = color;
            t.alignment = TextAlignmentOptions.Left;                                  // left, vertically centered
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Overflow;
            t.raycastTarget = false;
            return t;
        }

        static ColorBlock Colors(Color normal) => new ColorBlock
        {
            normalColor = normal,
            highlightedColor = ArchiveUi.Hover,
            pressedColor = ArchiveUi.Pressed,
            selectedColor = ArchiveUi.Focused,
            disabledColor = ArchiveUi.Disabled,
            colorMultiplier = 1f,
            fadeDuration = 0.08f,
        };

        /// <summary>A framed walnut button with a centered ivory label (child "Label"). No click listener: the runtime adds it.</summary>
        public static Button AddButton(Transform parent, string name, string label, float width, float height, float fontSize = 26f)
        {
            var r = NewRect(name, parent);
            var image = AddImage(r.gameObject, new Color32(255, 255, 255, 255), true);   // tinted by the color block
            var b = r.gameObject.AddComponent<Button>();
            b.targetGraphic = image;
            b.colors = Colors(ArchiveUi.Walnut);
            AddFrame(r);
            var t = AddText(r, "Label", label, fontSize, ArchiveUi.Ivory, TextAlignmentOptions.Center);
            Stretch(t.rectTransform, 8f);
            t.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            Size(r.gameObject, width, height);
            return b;
        }

        /// <summary>Horizontal slider: dark track, brass fill, ivory handle (brass tint when selected).</summary>
        public static Slider AddSlider(Transform parent, float min, float max, bool wholeNumbers, float width, float value)
        {
            var root = NewRect("Slider", parent);
            Size(root.gameObject, width, 36f);
            var background = NewRect("Background", root);
            background.anchorMin = new Vector2(0f, 0.38f);
            background.anchorMax = new Vector2(1f, 0.62f);
            background.offsetMin = background.offsetMax = Vector2.zero;
            AddImage(background.gameObject, ArchiveUi.Track, true);
            var fillArea = NewRect("Fill Area", root);
            fillArea.anchorMin = new Vector2(0f, 0.38f);
            fillArea.anchorMax = new Vector2(1f, 0.62f);
            fillArea.offsetMin = new Vector2(5f, 0f);
            fillArea.offsetMax = new Vector2(-15f, 0f);
            var fill = NewRect("Fill", fillArea);
            fill.sizeDelta = new Vector2(10f, 0f);
            AddImage(fill.gameObject, ArchiveUi.Brass, false);
            var handleArea = NewRect("Handle Slide Area", root);
            Stretch(handleArea);
            handleArea.offsetMin = new Vector2(10f, 0f);
            handleArea.offsetMax = new Vector2(-10f, 0f);
            var handle = NewRect("Handle", handleArea);
            handle.sizeDelta = new Vector2(20f, 0f);
            var handleImage = AddImage(handle.gameObject, ArchiveUi.Ivory, true);
            var s = root.gameObject.AddComponent<Slider>();
            s.fillRect = fill;
            s.handleRect = handle;
            s.targetGraphic = handleImage;
            s.direction = Slider.Direction.LeftToRight;
            s.minValue = min;
            s.maxValue = max;
            s.wholeNumbers = wholeNumbers;
            s.SetValueWithoutNotify(value);
            s.colors = new ColorBlock
            {
                normalColor = Color.white,
                highlightedColor = new Color32(0xFF, 0xF4, 0xDC, 255),
                pressedColor = new Color32(0xC9, 0xBC, 0xA2, 255),
                selectedColor = new Color32(0xE6, 0xC2, 0x7A, 255),
                disabledColor = new Color32(0x80, 0x78, 0x6E, 160),
                colorMultiplier = 1f,
                fadeDuration = 0.08f,
            };
            return s;
        }

        /// <summary>
        /// A vertical scroll area (mouse wheel, drag, scrollbar) whose content stacks rows top-down; the keyboard
        /// selection is kept in view (ScrollFollowSelection). `content` receives the rows.
        /// </summary>
        public static ScrollRect AddScroll(Transform parent, out RectTransform content)
        {
            var root = NewRect("Scroll", parent);
            AddImage(root.gameObject, new Color32(0, 0, 0, 0), true);               // catches wheel and drag
            var viewport = NewRect("Viewport", root);
            Stretch(viewport);
            viewport.offsetMax = new Vector2(-22f, 0f);                              // room for the scrollbar
            viewport.gameObject.AddComponent<RectMask2D>();
            content = NewRect("Content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = content.offsetMax = Vector2.zero;
            var v = Vertical(content, 14f, 4, TextAnchor.UpperLeft);
            v.childForceExpandWidth = true;
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var bar = NewRect("Scrollbar", root);
            bar.anchorMin = new Vector2(1f, 0f);
            bar.anchorMax = new Vector2(1f, 1f);
            bar.pivot = new Vector2(1f, 0.5f);
            bar.sizeDelta = new Vector2(12f, 0f);
            bar.anchoredPosition = Vector2.zero;
            AddImage(bar.gameObject, ArchiveUi.Track, true);
            var area = NewRect("Sliding Area", bar);
            Stretch(area, 1f);
            var handle = NewRect("Handle", area);
            Stretch(handle);
            var handleImage = AddImage(handle.gameObject, ArchiveUi.Brass, true);
            var scrollbar = bar.gameObject.AddComponent<Scrollbar>();
            scrollbar.handleRect = handle;
            scrollbar.targetGraphic = handleImage;
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            scrollbar.navigation = new Navigation { mode = Navigation.Mode.None };     // keyboard moves between settings, not onto the bar

            var scroll = root.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 40f;
            scroll.verticalScrollbar = scrollbar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            root.gameObject.AddComponent<ScrollFollowSelection>();
            return scroll;
        }

        // ---- settings rows ------------------------------------------------------------------------------------
        static RectTransform Row(RectTransform content, string name, float height = RowHeight)
        {
            var row = NewRect(name, content);
            Horizontal(row, 12f, TextAnchor.MiddleLeft);
            Size(row.gameObject, -1f, height);
            return row;
        }

        static void Spacer(RectTransform row, float width) => Size(NewRect("Spacer", row).gameObject, width, 1f);

        static void Section(RectTransform content, string title)
        {
            var t = AddText(content, "Section", title, 24f, ArchiveUi.Brass, TextAlignmentOptions.BottomLeft);
            Size(t.gameObject, -1f, 44f);
        }

        static void Label(RectTransform row, string text)
        {
            var t = AddText(row, "Label", text, 22f, ArchiveUi.IvoryDim, TextAlignmentOptions.Left);
            Size(t.gameObject, LabelWidth, RowHeight);
        }

        /// <summary>Label, "&lt;" button, value, "&gt;" button.</summary>
        static SettingsPanel.Stepper Stepper(RectTransform content, string name, string label, string preview)
        {
            var row = Row(content, name);
            Label(row, label);
            var previous = AddButton(row, "Previous", "<", 56f, 46f, 24f);
            var value = AddText(row, "Value", preview, 22f, ArchiveUi.Ivory, TextAlignmentOptions.Center);
            Size(value.gameObject, 300f, RowHeight);
            var next = AddButton(row, "Next", ">", 56f, 46f, 24f);
            return new SettingsPanel.Stepper { previous = previous, next = next, value = value };
        }

        static SettingsPanel.SliderField SliderRow(RectTransform content, string name, string label, float min, float max, bool whole, float value, string preview)
        {
            var row = Row(content, name);
            Label(row, label);
            var slider = AddSlider(row, min, max, whole, 400f, value);
            var text = AddText(row, "Value", preview, 22f, ArchiveUi.Ivory, TextAlignmentOptions.Right);
            Size(text.gameObject, ValueWidth, RowHeight);
            return new SettingsPanel.SliderField { slider = slider, value = text };
        }

        // ---- serialized references ----------------------------------------------------------------------------
        static void Set(SerializedObject so, string path, Object value)
        {
            var p = so.FindProperty(path) ?? throw new InvalidOperationException($"{so.targetObject.GetType().Name}에 '{path}' 필드가 없습니다");
            p.objectReferenceValue = value;
        }

        static void SetStepper(SerializedObject so, string field, SettingsPanel.Stepper s)
        {
            Set(so, field + ".previous", s.previous);
            Set(so, field + ".next", s.next);
            Set(so, field + ".value", s.value);
        }

        static void SetSlider(SerializedObject so, string field, SettingsPanel.SliderField s)
        {
            Set(so, field + ".slider", s.slider);
            Set(so, field + ".value", s.value);
        }
    }
}
