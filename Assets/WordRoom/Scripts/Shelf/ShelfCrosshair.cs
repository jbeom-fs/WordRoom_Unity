using UnityEngine;
using UnityEngine.UI;

namespace WordRoom.Shelf
{
    /// <summary>
    /// Small thin cross at the exact center of the screen: the screen-center ray FirstPersonPlayer.TryGetAimed uses to
    /// find books runs through it. It builds its own overlay canvas once (sort order 150: over the play hints at 100,
    /// under the Escape menu, reading and result screens and the blackout) with four arms around a small gap, each with
    /// a faint dark edge so it reads on light pages and dark wood. Nothing in it is a raycast target and the canvas has
    /// no raycaster, so it never takes a UI click. Hidden until Show(true); ShelfCorridorDemo shows it exactly while
    /// exploring, independent of the "[E] 읽기" hint.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShelfCrosshair : MonoBehaviour
    {
        const int SortingOrder = 150;

        [Tooltip("중앙에서 각 선이 시작하는 간격(기준 해상도 1920×1080의 픽셀)")]
        [Min(0f)] [SerializeField] float gap = 4f;
        [Tooltip("선 길이(픽셀)")]
        [Min(1f)] [SerializeField] float armLength = 7f;
        [Tooltip("선 두께(픽셀)")]
        [Min(1f)] [SerializeField] float thickness = 2f;
        [SerializeField] Color color = new Color(1f, 0.97f, 0.9f, 0.9f);
        [Tooltip("선 둘레의 어두운 테두리(밝은 바탕에서도 보이도록)")]
        [SerializeField] Color edgeColor = new Color(0f, 0f, 0f, 0.45f);

        Canvas canvas;
        bool shown;

        public bool IsShown => shown;

        void Awake() => Build();

        void OnDisable() => Show(false);

        /// <summary>Shows or hides the cross (only the canvas is toggled).</summary>
        public void Show(bool on)
        {
            shown = on;
            if (canvas == null && on) Build();
            if (canvas != null && canvas.enabled != on) canvas.enabled = on;
        }

        void Build()
        {
            if (canvas != null) return;
            var go = new GameObject("Shelf Crosshair Canvas", typeof(RectTransform));
            go.transform.SetParent(transform, false);
            canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = SortingOrder;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            // Edges first (drawn below), then the light arms.
            AddArms(go.transform, edgeColor, 1f);
            AddArms(go.transform, color, 0f);
            canvas.enabled = shown;
        }

        void AddArms(Transform parent, Color c, float grow)
        {
            var center = gap + armLength * 0.5f;
            var along = armLength + 2f * grow;
            var across = thickness + 2f * grow;
            Arm(parent, c, new Vector2(0f, center), new Vector2(across, along));
            Arm(parent, c, new Vector2(0f, -center), new Vector2(across, along));
            Arm(parent, c, new Vector2(center, 0f), new Vector2(along, across));
            Arm(parent, c, new Vector2(-center, 0f), new Vector2(along, across));
        }

        static void Arm(Transform parent, Color c, Vector2 position, Vector2 size)
        {
            var go = new GameObject("Arm", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = position;
            rt.sizeDelta = size;
            var image = go.AddComponent<Image>();
            image.color = c;
            image.raycastTarget = false;
        }
    }
}
