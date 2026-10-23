using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace WordRoom.World
{
    /// <summary>
    /// Top-left HUD with two rows only: "현재" + the current room's word and "목표" + the target word, styled like
    /// the rooms' wall plates (dark walnut panel, 1px brass edge, warm ivory text, no images). Display only: no
    /// raycast targets, no GraphicRaycaster or EventSystem, so mouse clicks, cursor lock and keys are untouched.
    /// Its overlay canvas sorts below PlayHint so the centered hints stay on top. The canvas, panel and texts are
    /// made once at runtime under this object when first shown; Show only rewrites a text whose string changed;
    /// Hide keeps them for reuse; Clear (also on disable/destroy) removes them. Sizes are in 1920×1080 reference
    /// pixels scaled with the screen (CanvasScaler, match 0.5). The word keeps one line in a fixed-width field:
    /// 24px, reduced down to 20px only when it would not fit (words are at most 8 characters, which fit at 24px by
    /// design; the field never wraps). Main thread only.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WordRoomHud : MonoBehaviour
    {
        const float Margin = 32f;
        static readonly Vector2 PanelSize = new Vector2(320f, 96f);
        const float Padding = 16f;
        const float RowHeight = 28f;
        const float RowGap = 8f;
        const float LabelWidth = 44f;
        const float LabelGap = 12f;
        const float WordWidth = 232f;
        const float LabelSize = 18f;
        const float WordSize = 24f;
        const float WordMinSize = 20f;
        const float Border = 1f;

        static readonly Color32 Walnut = new Color32(0x49, 0x35, 0x2A, 230);         // #49352A, ~90%
        static readonly Color32 Brass = new Color32(0xA8, 0x87, 0x4B, 255);          // #A8874B
        static readonly Color32 Ivory = new Color32(0xF2, 0xEB, 0xDD, 255);          // #F2EBDD (word)
        static readonly Color32 IvoryDim = new Color32(0xD8, 0xD0, 0xBF, 255);       // #D8D0BF (label)

        [Tooltip("HUD 글꼴(한글 TMP 폰트, 예: Assets/Font/Pretendard-Black SDF). 비우면 TMP 기본 폰트라 한글이 보이지 않을 수 있습니다.")]
        [SerializeField] TMP_FontAsset font;
        [Tooltip("Canvas 정렬 순서. PlayHint(100)보다 낮게 두어 화면 중앙 안내를 가리지 않습니다.")]
        [SerializeField] int sortingOrder = 50;

        GameObject canvasObject;
        TextMeshProUGUI currentText;
        TextMeshProUGUI targetText;
        string shownCurrent;
        string shownTarget;

        /// <summary>Shows both rows. Null or empty words hide the HUD instead (never an empty or placeholder row).</summary>
        public void Show(string current, string target)
        {
            if (string.IsNullOrEmpty(current) || string.IsNullOrEmpty(target))
            {
                Hide();
                return;
            }
            if (canvasObject == null) Build();
            if (current != shownCurrent) SetWord(currentText, shownCurrent = current);
            if (target != shownTarget) SetWord(targetText, shownTarget = target);
            if (!canvasObject.activeSelf) canvasObject.SetActive(true);
        }

        public void Hide()
        {
            if (canvasObject != null && canvasObject.activeSelf) canvasObject.SetActive(false);
        }

        /// <summary>Removes the made UI; the next Show builds it again.</summary>
        public void Clear()
        {
            if (canvasObject != null) Destroy(canvasObject);
            canvasObject = null;
            currentText = targetText = null;
            shownCurrent = shownTarget = null;
        }

        void OnDisable() => Clear();

        void Build()
        {
            canvasObject = new GameObject("WordRoomHud Canvas");
            canvasObject.transform.SetParent(transform, false);
            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            var scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            var panel = AddRect("Panel", canvasObject.transform, new Vector2(Margin, -Margin), PanelSize);
            Fill(AddImage("Background", panel), Walnut);
            Edge("BorderTop", panel, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, Border));
            Edge("BorderBottom", panel, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, Border));
            Edge("BorderLeft", panel, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(Border, 0f));
            Edge("BorderRight", panel, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(Border, 0f));

            currentText = Row(panel, 0, "현재");
            targetText = Row(panel, 1, "목표");
            if (font == null) Debug.LogWarning("[HUD] WordRoomHud의 Font가 비어 있어 TMP 기본 폰트를 씁니다. 한글이 보이지 않으면 Pretendard-Black SDF를 지정하세요.", this);
        }

        /// <summary>One row: fixed label then the word field; returns the word text.</summary>
        TextMeshProUGUI Row(RectTransform panel, int index, string label)
        {
            var y = -(Padding + index * (RowHeight + RowGap));
            var l = AddText($"Label{index}", panel, new Vector2(Padding, y), new Vector2(LabelWidth, RowHeight), LabelSize, IvoryDim);
            l.text = label;
            return AddText($"Word{index}", panel, new Vector2(Padding + LabelWidth + LabelGap, y), new Vector2(WordWidth, RowHeight), WordSize, Ivory);
        }

        /// <summary>24px when it fits the field on one line, else smaller down to 20px (width only; the row height never shrinks it).</summary>
        static void SetWord(TextMeshProUGUI t, string word)
        {
            t.fontSize = WordSize;
            t.text = word;
            var width = t.GetPreferredValues(word, float.PositiveInfinity, float.PositiveInfinity).x;
            if (width > WordWidth) t.fontSize = Mathf.Max(WordMinSize, Mathf.Floor(WordSize * WordWidth / width));
        }

        /// <summary>Top-left anchored rect at `position` (from the parent's top-left corner, y down negative).</summary>
        static RectTransform AddRect(string name, Transform parent, Vector2 position, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var r = (RectTransform)go.transform;
            r.SetParent(parent, false);
            r.anchorMin = r.anchorMax = r.pivot = new Vector2(0f, 1f);
            r.anchoredPosition = position;
            r.sizeDelta = size;
            return r;
        }

        static Image AddImage(string name, RectTransform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var r = (RectTransform)go.transform;
            r.SetParent(parent, false);
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.offsetMin = r.offsetMax = Vector2.zero;
            return go.AddComponent<Image>();
        }

        static void Fill(Image image, Color32 color)
        {
            image.color = color;
            image.raycastTarget = false;
        }

        /// <summary>A 1px brass line along one edge of the panel (inside it).</summary>
        static void Edge(string name, RectTransform panel, Vector2 anchorMin, Vector2 anchorMax, Vector2 thickness)
        {
            var image = AddImage(name, panel);
            var r = image.rectTransform;
            r.anchorMin = anchorMin;
            r.anchorMax = anchorMax;
            r.pivot = new Vector2(anchorMin.x == anchorMax.x ? anchorMin.x : 0.5f, anchorMin.y == anchorMax.y ? anchorMin.y : 0.5f);
            r.offsetMin = r.offsetMax = Vector2.zero;
            r.sizeDelta = thickness;
            Fill(image, Brass);
        }

        TextMeshProUGUI AddText(string name, RectTransform panel, Vector2 position, Vector2 size, float fontSize, Color32 color)
        {
            var r = AddRect(name, panel, position, size);
            var t = r.gameObject.AddComponent<TextMeshProUGUI>();
            if (font != null) t.font = font;
            t.fontSize = fontSize;
            t.enableAutoSizing = false;
            t.color = color;
            t.alignment = TextAlignmentOptions.Left;                                  // left, vertically centered
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Overflow;                       // never cut vertically; the width is sized in SetWord
            t.raycastTarget = false;
            return t;
        }
    }
}
