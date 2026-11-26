using UnityEngine;

namespace WordRoom.Shelf
{
    /// <summary>
    /// Writing desk prefab of the reading room (ReadingDesk.prefab). Pivot on the floor under the desk center; local +Z
    /// from the reader toward the bookcase behind it. `top` marks where lecterns stand (center of the worktop at its
    /// height); it is never scaled, so lectern offsets stay in meters. Widening the top scales only `model` along X and
    /// resizes the blocking collider with it. Pooled by ShelfRoomView: one instance per used side, reused across rooms.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShelfDesk : MonoBehaviour
    {
        [Tooltip("책상 모델(상판을 넓힐 때 X로만 늘림)")]
        [SerializeField] Transform model;
        [Tooltip("독서대가 놓이는 기준점: 상판 중앙, 상판 높이")]
        [SerializeField] Transform top;
        [Tooltip("책상 전체를 막는 Collider(상판 폭에 맞춰 X 크기 조정)")]
        [SerializeField] BoxCollider body;
        [Tooltip("원본 상판 폭(m)")]
        [Min(0.3f)] [SerializeField] float topWidth = 1.64f;
        [Tooltip("원본 상판 깊이(m)")]
        [Min(0.3f)] [SerializeField] float topDepth = 0.76f;

        float bodyWidth = -1f;                   // collider X size at the original top width

        public Transform Top => top;
        public float BaseTopWidth => topWidth;
        public float TopDepth => topDepth;
        /// <summary>Current top width after SetTopWidth.</summary>
        public float TopWidth { get; private set; }

        public bool IsComplete => model != null && top != null && body != null;

        void Awake() => Remember();

        void Remember()
        {
            if (bodyWidth > 0f || body == null) return;
            bodyWidth = body.size.x;
            TopWidth = topWidth;
        }

        /// <summary>Stretches the top (and only the model and collider) to `width` meters; the original width or more.</summary>
        public void SetTopWidth(float width)
        {
            Remember();
            width = Mathf.Max(topWidth, width);
            TopWidth = width;
            var k = width / topWidth;
            if (model != null) model.localScale = new Vector3(k, 1f, 1f);
            if (body != null)
            {
                var size = body.size;
                size.x = bodyWidth * k;
                body.size = size;
            }
        }

#if UNITY_EDITOR
        /// <summary>Editor setup only: wires the prefab's parts and measured sizes.</summary>
        public void EditorSetup(Transform model, Transform top, BoxCollider body, float topWidth, float topDepth)
        {
            this.model = model;
            this.top = top;
            this.body = body;
            this.topWidth = topWidth;
            this.topDepth = topDepth;
        }
#endif
    }
}
