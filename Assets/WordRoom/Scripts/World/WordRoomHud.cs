using TMPro;
using UnityEngine;
using UnityEngine.Serialization;

namespace WordRoom.World
{
    /// <summary>
    /// Top-left HUD with two rows only: "현재" + the current room's word and "목표" + the target word, styled like
    /// the rooms' wall plates (dark walnut panel, 4-unit brass edge, warm ivory text, no images). Display only: no
    /// raycast targets and no GraphicRaycaster, so mouse clicks, cursor lock and keys are untouched.
    /// The display is a canvas saved in the scene (prefab Assets/Prefabs/UI/HudCanvas: sort order 50, below
    /// PlayHint, 1920×1080 reference with UiScaleBinding); this component only controls it and is kept apart from
    /// it, so hiding the display never disables the control. Show rewrites a text only when its word changed and
    /// turns the canvas on; Hide only turns the canvas off; Clear empties the words and the cache and hides; nothing
    /// is ever created or destroyed. While this component is disabled the HUD stays hidden whoever calls Show;
    /// disabling clears it, so after enabling again the next Show writes the words anew (the cache never skips it).
    /// The word keeps one line in its fixed-width field: the font size saved on the word text, reduced down to
    /// wordMinSize only when the word would not fit. Missing references are reported once and the HUD then stays
    /// off. Main thread only.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WordRoomHud : MonoBehaviour
    {
        [Tooltip("HUD 표시 영역(씬에 배치된 HudCanvas의 Canvas). 숨길 때 이 Canvas만 끄고 이 컴포넌트는 켜 둡니다.")]
        [SerializeField] Canvas canvas;
        [Tooltip("현재 단어 텍스트(고정 폭 칸)")]
        [SerializeField] TextMeshProUGUI currentText;
        [Tooltip("목표 단어 텍스트(고정 폭 칸)")]
        [SerializeField] TextMeshProUGUI targetText;
        [Tooltip("단어가 칸 폭을 넘을 때 줄일 수 있는 최소 글자 크기. 기본 크기는 각 단어 텍스트에 저장된 Font Size")]
        [SerializeField] float wordMinSize = 20f;

#pragma warning disable CS0414, CS0649   // read and written only by the Editor setup (SerializedObject), never at runtime
        // Values saved by the former runtime-built HUD (fields "font", "sortingOrder"), moved once onto this scene's
        // HUD canvas instance. int.MinValue = no saved value could be read.
        [SerializeField, HideInInspector, FormerlySerializedAs("font")] TMP_FontAsset legacyFont = null;
        [SerializeField, HideInInspector, FormerlySerializedAs("sortingOrder")] int legacySortingOrder = int.MinValue;
        [SerializeField, HideInInspector] bool legacyUiMigrated;
#pragma warning restore CS0414, CS0649

        string shownCurrent;
        string shownTarget;
        float currentSize, targetSize;      // the saved font sizes, read once before any change
        bool initialized, ready;

        void Awake()
        {
            if (Initialize()) Hide();       // nothing is shown before the first Show
        }

        /// <summary>Shows both rows. Null or empty words hide the HUD instead (never an empty or placeholder row).</summary>
        public void Show(string current, string target)
        {
            if (!isActiveAndEnabled) return;                       // disabled: stays hidden (KI-008)
            if (string.IsNullOrEmpty(current) || string.IsNullOrEmpty(target))
            {
                Hide();
                return;
            }
            if (!Initialize()) return;
            if (current != shownCurrent) SetWord(currentText, currentSize, shownCurrent = current);
            if (target != shownTarget) SetWord(targetText, targetSize, shownTarget = target);
            if (!canvas.enabled) canvas.enabled = true;
        }

        /// <summary>Turns only the display off; the words stay for the next Show.</summary>
        public void Hide()
        {
            if (canvas != null && canvas.enabled) canvas.enabled = false;
        }

        /// <summary>Empties the words and the cache, then hides. The scene's UI stays.</summary>
        public void Clear()
        {
            shownCurrent = shownTarget = null;
            if (ready)
            {
                if (currentText.text.Length > 0) currentText.text = "";
                if (targetText.text.Length > 0) targetText.text = "";
            }
            Hide();
        }

        void OnDisable() => Clear();

        bool Initialize()
        {
            if (initialized) return ready;
            initialized = true;
            if (canvas == null || currentText == null || targetText == null)
            {
                Debug.LogError($"[HUD] WordRoomHud의 표시 참조가 비어 있어 현재·목표 단어를 표시하지 않습니다(플레이는 계속): " +
                               $"{(canvas == null ? "canvas " : "")}{(currentText == null ? "currentText " : "")}{(targetText == null ? "targetText" : "")}. " +
                               "WordRoom/메인 메뉴와 게임 설정 구성 메뉴로 HudCanvas를 배치·연결하세요.", this);
                return ready = false;
            }
            currentSize = currentText.fontSize;
            targetSize = targetText.fontSize;
            return ready = true;
        }

        /// <summary>`size` when it fits the field on one line, else smaller down to wordMinSize (width only; the row height never shrinks it).</summary>
        void SetWord(TextMeshProUGUI t, float size, string word)
        {
            t.fontSize = size;
            t.text = word;
            var field = t.rectTransform.rect.width;
            var width = t.GetPreferredValues(word, float.PositiveInfinity, float.PositiveInfinity).x;
            if (field > 0f && width > field) t.fontSize = Mathf.Max(Mathf.Min(wordMinSize, size), Mathf.Floor(size * field / width));
        }
    }
}
