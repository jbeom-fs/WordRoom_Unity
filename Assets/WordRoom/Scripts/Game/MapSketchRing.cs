using UnityEngine;
using UnityEngine.UI;

namespace WordRoom.Game
{
    /// <summary>
    /// The eraser's reach on the map: a thin ring filling this rect (MapSketchView sizes it to the eraser's diameter at
    /// the current zoom and puts it under the pointer). In this Graphic's color; never a raycast target.
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class MapSketchRing : MaskableGraphic
    {
        const int Segments = 40;

        [Tooltip("링 두께(Canvas 단위)")]
        [SerializeField] float thickness = 2f;

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            var r = rectTransform.rect;
            var outer = Mathf.Min(r.width, r.height) * 0.5f;
            if (outer <= 0f) return;
            var inner = Mathf.Max(0f, outer - thickness);
            var c = r.center;
            var color32 = (Color32)color;
            for (var i = 0; i < Segments; i++)
            {
                var a = i * Mathf.PI * 2f / Segments;
                var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                vh.AddVert(c + d * outer, color32, Vector2.zero);
                vh.AddVert(c + d * inner, color32, Vector2.zero);
            }
            for (var i = 0; i < Segments; i++)
            {
                var o0 = 2 * i;
                var o1 = 2 * ((i + 1) % Segments);
                vh.AddTriangle(o0, o1, o1 + 1);
                vh.AddTriangle(o1 + 1, o0 + 1, o0);
            }
        }
    }
}
