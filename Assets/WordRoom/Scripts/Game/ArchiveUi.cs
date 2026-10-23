using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using WordRoom.World;

namespace WordRoom.Game
{
    /// <summary>
    /// Builds the archive-style menus at runtime from built-in UI and TMP only (no images): dark walnut panels with
    /// a 1px brass frame, warm ivory text in the project's Korean font. Every canvas is Screen Space Overlay on the
    /// 1920×1080 reference and follows the UI size setting (UiScaleBinding). Buttons show normal, hover, pressed,
    /// keyboard-selected and disabled states with distinct colors and click through UiSound. The EventSystem is
    /// made only when the scene has none, with the Input System UI module (mouse and keyboard navigation).
    /// </summary>
    public static class ArchiveUi
    {
        public static readonly Color32 Walnut = new Color32(0x49, 0x35, 0x2A, 255);          // #49352A
        public static readonly Color32 PanelWalnut = new Color32(0x49, 0x35, 0x2A, 242);
        public static readonly Color32 Hover = new Color32(0x5E, 0x46, 0x37, 255);
        public static readonly Color32 Pressed = new Color32(0x33, 0x25, 0x1D, 255);
        public static readonly Color32 Focused = new Color32(0x6E, 0x55, 0x37, 255);         // keyboard selection, brass-tinted
        public static readonly Color32 Chosen = new Color32(0x86, 0x69, 0x3C, 255);          // chosen option (difficulty)
        public static readonly Color32 Disabled = new Color32(0x3A, 0x32, 0x2D, 150);
        public static readonly Color32 Brass = new Color32(0xA8, 0x87, 0x4B, 255);           // #A8874B
        public static readonly Color32 Ivory = new Color32(0xF2, 0xEB, 0xDD, 255);           // #F2EBDD
        public static readonly Color32 IvoryDim = new Color32(0xD8, 0xD0, 0xBF, 255);        // #D8D0BF
        public static readonly Color32 Track = new Color32(0x2B, 0x1F, 0x18, 255);
        public static readonly Color32 Dim = new Color32(0, 0, 0, 150);

        /// <summary>Font for every text made here (set by the scene's menu component before building).</summary>
        public static TMP_FontAsset Font { get; set; }

        public static Canvas CreateCanvas(Transform parent, string name, int sortingOrder)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            UiScaleBinding.Configure(go);
            go.AddComponent<GraphicRaycaster>();
            return canvas;
        }

        /// <summary>One EventSystem per scene: made only when none exists, with the Input System UI module.</summary>
        public static void EnsureEventSystem()
        {
            if (EventSystem.current != null || UnityEngine.Object.FindAnyObjectByType<EventSystem>() != null) return;
            var go = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            go.GetComponent<InputSystemUIInputModule>().AssignDefaultActions();
        }

        public static void Select(GameObject go)
        {
            if (go != null && EventSystem.current != null) EventSystem.current.SetSelectedGameObject(go);
        }

        public static void QuitGame()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // ---- building blocks ------------------------------------------------------------------------------
        public static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var r = (RectTransform)go.transform;
            r.SetParent(parent, false);
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

        public static Image AddImage(GameObject go, Color32 color, bool raycast)
        {
            var image = go.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = raycast;
            return image;
        }

        /// <summary>A 1px brass line on each edge, inside the rect, never catching clicks.</summary>
        public static void AddFrame(RectTransform r)
        {
            Edge(r, "FrameTop", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(0.5f, 1f));
            Edge(r, "FrameBottom", new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(0.5f, 0f));
            Edge(r, "FrameLeft", new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 0f), new Vector2(0f, 0.5f));
            Edge(r, "FrameRight", new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0f), new Vector2(1f, 0.5f));
        }

        static void Edge(RectTransform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 size, Vector2 pivot)
        {
            var r = NewRect(name, parent);
            r.anchorMin = anchorMin;
            r.anchorMax = anchorMax;
            r.pivot = pivot;
            r.anchoredPosition = Vector2.zero;
            r.sizeDelta = size;
            AddImage(r.gameObject, Brass, false);
            r.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
        }

        /// <summary>A walnut panel with a brass frame. It catches clicks so nothing behind it reacts.</summary>
        public static RectTransform Panel(Transform parent, string name)
        {
            var r = NewRect(name, parent);
            AddImage(r.gameObject, PanelWalnut, true);
            AddFrame(r);
            return r;
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

        public static TextMeshProUGUI AddText(Transform parent, string text, float size, Color32 color, TextAlignmentOptions align)
        {
            var r = NewRect("Text", parent);
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

        static ColorBlock Colors(Color normal) => new ColorBlock
        {
            normalColor = normal,
            highlightedColor = Hover,
            pressedColor = Pressed,
            selectedColor = Focused,
            disabledColor = Disabled,
            colorMultiplier = 1f,
            fadeDuration = 0.08f,
        };

        /// <summary>A framed walnut button with a centered ivory label; the click sound plays before the action.</summary>
        public static Button AddButton(Transform parent, string label, Action onClick, float width, float height, float fontSize = 26f)
        {
            var r = NewRect(label, parent);
            var image = AddImage(r.gameObject, new Color32(255, 255, 255, 255), true);   // tinted by the color block
            var b = r.gameObject.AddComponent<Button>();
            b.targetGraphic = image;
            b.colors = Colors(Walnut);
            AddFrame(r);
            var t = AddText(r, label, fontSize, Ivory, TextAlignmentOptions.Center);
            Stretch(t.rectTransform, 8f);
            t.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            Size(r.gameObject, width, height);
            b.onClick.AddListener(() =>
            {
                UiSound.Click();
                onClick?.Invoke();
            });
            return b;
        }

        public static void SetLabel(Button b, string text) => b.GetComponentInChildren<TextMeshProUGUI>(true).text = text;

        /// <summary>Marks a button as the chosen option (brass-tinted walnut) or back to normal.</summary>
        public static void SetChosen(Button b, bool chosen) => b.colors = Colors(chosen ? (Color)Chosen : (Color)Walnut);

        /// <summary>Horizontal slider: dark track, brass fill, ivory handle (brass tint when selected).</summary>
        public static Slider AddSlider(Transform parent, float min, float max, bool wholeNumbers, float width)
        {
            var root = NewRect("Slider", parent);
            Size(root.gameObject, width, 36f);
            var background = NewRect("Background", root);
            background.anchorMin = new Vector2(0f, 0.38f);
            background.anchorMax = new Vector2(1f, 0.62f);
            background.offsetMin = background.offsetMax = Vector2.zero;
            AddImage(background.gameObject, Track, true);
            var fillArea = NewRect("Fill Area", root);
            fillArea.anchorMin = new Vector2(0f, 0.38f);
            fillArea.anchorMax = new Vector2(1f, 0.62f);
            fillArea.offsetMin = new Vector2(5f, 0f);
            fillArea.offsetMax = new Vector2(-15f, 0f);
            var fill = NewRect("Fill", fillArea);
            fill.sizeDelta = new Vector2(10f, 0f);
            AddImage(fill.gameObject, Brass, false);
            var handleArea = NewRect("Handle Slide Area", root);
            Stretch(handleArea);
            handleArea.offsetMin = new Vector2(10f, 0f);
            handleArea.offsetMax = new Vector2(-10f, 0f);
            var handle = NewRect("Handle", handleArea);
            handle.sizeDelta = new Vector2(20f, 0f);
            var handleImage = AddImage(handle.gameObject, Ivory, true);
            var s = root.gameObject.AddComponent<Slider>();
            s.fillRect = fill;
            s.handleRect = handle;
            s.targetGraphic = handleImage;
            s.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
            s.minValue = min;
            s.maxValue = max;
            s.wholeNumbers = wholeNumbers;
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
        /// selection is kept in view. `content` receives the rows.
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
            AddImage(bar.gameObject, Track, true);
            var area = NewRect("Sliding Area", bar);
            Stretch(area, 1f);
            var handle = NewRect("Handle", area);
            Stretch(handle);
            var handleImage = AddImage(handle.gameObject, Brass, true);
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
    }
}
