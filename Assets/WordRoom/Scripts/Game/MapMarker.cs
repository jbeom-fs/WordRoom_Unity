using UnityEngine;
using UnityEngine.UI;

namespace WordRoom.Game
{
    /// <summary>
    /// The player's mark on the map: one triangle pointing 상 (+Y) inside this rect, in this Graphic's color (mint
    /// #72FFD5 in the prefab). MapView moves and turns its RectTransform; the mesh itself never changes with the
    /// player, so following the player costs no mesh rebuild. Never a raycast target.
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class MapMarker : MaskableGraphic
    {
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            var r = rectTransform.rect;
            var c = (Color32)color;
            vh.AddVert(new Vector3(r.center.x, r.yMax), c, Vector2.zero);                     // tip: the facing direction
            vh.AddVert(new Vector3(r.xMax, r.yMin), c, Vector2.zero);
            vh.AddVert(new Vector3(r.center.x, r.yMin + r.height * 0.28f), c, Vector2.zero);  // notch at the back
            vh.AddVert(new Vector3(r.xMin, r.yMin), c, Vector2.zero);
            vh.AddTriangle(0, 1, 2);
            vh.AddTriangle(0, 2, 3);
        }
    }
}
