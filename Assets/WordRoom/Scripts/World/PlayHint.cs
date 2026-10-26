using TMPro;
using UnityEngine;
using UnityEngine.Serialization;

namespace WordRoom.World
{
    /// <summary>
    /// One line of play guidance on the screen (e.g. "[E] 문 열기"). The display is a canvas saved in the scene
    /// (prefab Assets/Prefabs/UI/PlayHintCanvas: sort order 100, centered text, no raycast targets, 1920×1080
    /// reference with UiScaleBinding); this component only controls it and is kept apart from it, so hiding the
    /// display never disables the control. Set writes the text only when it changed and turns the canvas on; an
    /// empty or null text hides it. Hide only turns the canvas off (the same text set again shows it again);
    /// Clear empties the text and hides; nothing is ever created or destroyed. While this component is disabled
    /// the hint stays hidden whoever calls Set, and disabling clears it. Missing references are reported once and
    /// the hint then stays off. Main thread only.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayHint : MonoBehaviour
    {
        [Tooltip("안내 표시 영역(씬에 배치된 PlayHintCanvas의 Canvas). 숨길 때 이 Canvas만 끄고 이 컴포넌트는 켜 둡니다.")]
        [SerializeField] Canvas canvas;
        [Tooltip("안내 문구 텍스트(글꼴·크기·위치·외곽선은 프리팹에서 조정)")]
        [SerializeField] TextMeshProUGUI label;

#pragma warning disable CS0414, CS0649   // read and written only by the Editor setup (SerializedObject), never at runtime
        // Values saved by the former runtime-built hint (fields "font", "fontSize", "offset"), moved once onto this
        // scene's hint canvas instance. -1 / NaN = no saved value could be read.
        [SerializeField, HideInInspector, FormerlySerializedAs("font")] TMP_FontAsset legacyFont = null;
        [SerializeField, HideInInspector, FormerlySerializedAs("fontSize")] float legacyFontSize = -1f;
        [SerializeField, HideInInspector, FormerlySerializedAs("offset")] Vector2 legacyOffset = new Vector2(float.NaN, float.NaN);
        [SerializeField, HideInInspector] bool legacyUiMigrated;
#pragma warning restore CS0414, CS0649

        string current = "";                // text on the label
        bool initialized, ready;

        void Awake()
        {
            if (Initialize()) Hide();       // nothing is shown before the first Set
        }

        public void Set(string text)
        {
            if (!isActiveAndEnabled) return;                       // disabled: stays hidden
            if (string.IsNullOrEmpty(text))
            {
                Hide();
                return;
            }
            if (!Initialize()) return;
            if (text != current) label.text = current = text;
            if (!canvas.enabled) canvas.enabled = true;
        }

        /// <summary>Turns only the display off.</summary>
        public void Hide()
        {
            if (canvas != null && canvas.enabled) canvas.enabled = false;
        }

        /// <summary>Empties the text and its cache, then hides. The scene's UI stays.</summary>
        public void Clear()
        {
            if (ready && current.Length > 0) label.text = "";
            current = "";
            Hide();
        }

        void OnDisable() => Clear();

        bool Initialize()
        {
            if (initialized) return ready;
            initialized = true;
            if (canvas != null && label != null) return ready = true;
            Debug.LogError($"[안내] PlayHint의 표시 참조가 비어 있어 화면 안내를 표시하지 않습니다(플레이는 계속): " +
                           $"{(canvas == null ? "canvas " : "")}{(label == null ? "label" : "")}. WordRoom/메인 메뉴와 게임 설정 구성 메뉴로 PlayHintCanvas를 배치·연결하세요.", this);
            return ready = false;
        }
    }
}
