using TMPro;
using UnityEngine;

namespace WordRoom.World
{
    /// <summary>
    /// One line of play guidance on the screen (e.g. "[E] 문 열기"): a screen-space overlay canvas with one
    /// TextMeshPro text, made at runtime under this object the first time something is shown. Set replaces the
    /// text and an empty or null text hides it; setting the same text again changes nothing (no per-frame
    /// rebuild). Uses the given font (the project's Korean TMP font); without it TMP's default font is used and
    /// Korean may not render. Main thread only.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayHint : MonoBehaviour
    {
        [Tooltip("안내 문구 폰트(한글 TMP 폰트, 예: Assets/Font/Pretendard-Black SDF). 비우면 TMP 기본 폰트라 한글이 보이지 않을 수 있습니다.")]
        [SerializeField] TMP_FontAsset font;
        [Tooltip("글자 크기(화면 픽셀)")]
        [SerializeField] float fontSize = 30f;
        [Tooltip("화면 중앙 기준 위치(픽셀). 기본은 중앙 조금 아래")]
        [SerializeField] Vector2 offset = new Vector2(0f, -90f);

        GameObject canvasObject;
        TextMeshProUGUI label;
        string current = "";

        public void Set(string text)
        {
            text = text ?? "";
            if (text == current) return;
            current = text;
            if (text.Length == 0)
            {
                if (canvasObject != null) canvasObject.SetActive(false);
                return;
            }
            if (label == null) Build();
            label.text = text;
            canvasObject.SetActive(true);
        }

        void Build()
        {
            if (canvasObject != null) Destroy(canvasObject);
            canvasObject = new GameObject("PlayHint Canvas");
            canvasObject.transform.SetParent(transform, false);
            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            var textObject = new GameObject("Text", typeof(RectTransform));
            textObject.transform.SetParent(canvasObject.transform, false);
            label = textObject.AddComponent<TextMeshProUGUI>();
            if (font != null) label.font = font;
            else Debug.LogWarning("[안내] PlayHint의 Font가 비어 있어 TMP 기본 폰트를 씁니다. 한글이 보이지 않으면 Pretendard-Black SDF를 지정하세요.", this);
            label.fontSize = fontSize;
            label.alignment = TextAlignmentOptions.Center;
            label.color = Color.white;
            label.outlineWidth = 0.2f;
            label.outlineColor = new Color32(0, 0, 0, 255);
            label.raycastTarget = false;
            var rect = label.rectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(1200f, 200f);
            rect.anchoredPosition = offset;
        }

        void OnDestroy()
        {
            if (canvasObject != null) Destroy(canvasObject);
        }
    }
}
