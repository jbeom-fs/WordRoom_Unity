using UnityEngine;
using UnityEngine.UI;
using WordRoom.Generation;
using WordRoom.World;

namespace WordRoom.Game
{
    /// <summary>
    /// One reusable UI mesh drawing the visible part of the explored map: connection lines between generated
    /// neighbors (상·하·좌·우), room tiles (bright when visited, dark when not, each with a thin edge in the line color so
    /// a dark tile never sinks into the background), the current room's border and each target room's inner border
    /// and star. North-up (GridPos X → right, Y → up); the view (center in map units, cell size in canvas units) is set
    /// by MapView. Only cells inside this rect (plus one around for lines) are looked up, so the cost follows the
    /// viewport, not the map size; the mesh is rebuilt only when MapView marks it dirty (data or view change), never
    /// per frame. No GameObjects are made.
    /// Vertex costs are the constants below, used both by the drawing helpers and by MaxVertices, which MapView uses
    /// to keep the visible cells within VertexBudget (below UGUI's limit: VertexHelper.FillMesh refuses 65000).
    /// Colors are this component's Inspector values; the legend swatches take them when play starts.
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class MapGraphic : MaskableGraphic
    {
        public const int QuadVertices = 4;
        /// <summary>Tile: edge quad + fill quad.</summary>
        public const int TileVertices = 2 * QuadVertices;
        /// <summary>A room draws at most its 우 and 상 connection lines.</summary>
        public const int LineVertices = 2 * QuadVertices;
        /// <summary>Border: four quads.</summary>
        public const int FrameVertices = 4 * QuadVertices;
        public const int StarPoints = 5;
        /// <summary>Star: center + outer and inner point of each arm.</summary>
        public const int StarVertices = 1 + 2 * StarPoints;
        /// <summary>Each target room in view: its border and star.</summary>
        public const int TargetVertices = FrameVertices + StarVertices;
        /// <summary>Mesh budget, kept well below UGUI's 65000-vertex limit.</summary>
        public const int VertexBudget = 60000;

        /// <summary>Margin of cells looked up around the rect (lines toward rooms just outside).</summary>
        public const int LookupMargin = 1;

        [Header("색상 (범례 견본도 이 값을 씀)")]
        [SerializeField] Color visitedColor = new Color32(0x80, 0x60, 0x47, 255);       // #806047
        [SerializeField] Color unvisitedColor = new Color32(0x30, 0x24, 0x1C, 255);     // #30241C
        [SerializeField] Color currentBorderColor = new Color32(0xD7, 0xB5, 0x68, 255);
        [SerializeField] Color targetColor = new Color32(0x7D, 0xA5, 0x8D, 255);
        [SerializeField] Color lineColor = new Color32(0x8C, 0x73, 0x55, 255);

        [Header("모양 (칸 크기 대비 비율)")]
        [Tooltip("방 타일 한 변 / 칸 크기")]
        [Range(0.4f, 0.95f)] [SerializeField] float roomFraction = 0.74f;
        [Tooltip("연결선 두께 / 칸 크기")]
        [Range(0.02f, 0.3f)] [SerializeField] float lineFraction = 0.08f;
        [Tooltip("방 타일 가장자리(연결선 색) 두께 / 칸 크기 (최소 1). 0이면 가장자리 없음")]
        [Range(0f, 0.1f)] [SerializeField] float tileEdgeFraction = 0.025f;
        [Tooltip("현재·목표 테두리 두께 / 칸 크기 (최소 2)")]
        [Range(0.02f, 0.2f)] [SerializeField] float borderFraction = 0.06f;
        [Tooltip("목표 별 바깥 반지름 / 칸 크기")]
        [Range(0.05f, 0.3f)] [SerializeField] float starFraction = 0.12f;

        [Header("범례 견본 (선택)")]
        [SerializeField] Graphic visitedSwatch;
        [SerializeField] Graphic unvisitedSwatch;
        [SerializeField] Graphic currentSwatch;
        [SerializeField] Graphic targetSwatch;
        [SerializeField] Graphic lineSwatch;

        ExploredMap map;
        Vector2 center;
        float cell = 100f;

        public float RoomFraction => roomFraction;

        /// <summary>Most vertices a view can need: every looked-up cell a room with tile and lines, the current border, and a target mark for each target room (all of them may be in view).</summary>
        public static int MaxVertices(int cells, int targetRooms) =>
            cells * (TileVertices + LineVertices) + FrameVertices + Mathf.Min(cells, Mathf.Max(0, targetRooms)) * TargetVertices;

        /// <summary>Most cells VisibleRange can return for a rect of `size` at `cellSize` (floor/ceil add up to 3 per axis, plus the margin on both sides).</summary>
        public static int MaxCellsInRange(Vector2 size, float cellSize, int margin) =>
            (Mathf.CeilToInt(size.x / cellSize) + 3 + 2 * margin) * (Mathf.CeilToInt(size.y / cellSize) + 3 + 2 * margin);

        protected override void OnEnable()
        {
            base.OnEnable();
            if (Application.isPlaying) ApplyLegend();                         // edit mode: no writes into the prefab instance
        }

        /// <summary>The legend swatches show this graphic's colors.</summary>
        public void ApplyLegend()
        {
            Paint(visitedSwatch, visitedColor);
            Paint(unvisitedSwatch, unvisitedColor);
            Paint(currentSwatch, currentBorderColor);
            Paint(targetSwatch, targetColor);
            Paint(lineSwatch, lineColor);
        }

        static void Paint(Graphic g, Color c)
        {
            if (g != null && g.color != c) g.color = c;
        }

        public void SetView(ExploredMap source, Vector2 viewCenter, float cellSize)
        {
            map = source;
            center = viewCenter;
            cell = cellSize;
            SetVerticesDirty();
        }

        /// <summary>Range of grid cells whose center lies within this rect, widened by `margin` cells.</summary>
        public static void VisibleRange(Rect r, Vector2 center, float cell, int margin, out int minX, out int maxX, out int minY, out int maxY)
        {
            var hw = r.width * 0.5f / cell;
            var hh = r.height * 0.5f / cell;
            minX = Mathf.FloorToInt(center.x - hw) - margin;
            maxX = Mathf.CeilToInt(center.x + hw) + margin;
            minY = Mathf.FloorToInt(center.y - hh) - margin;
            maxY = Mathf.CeilToInt(center.y + hh) + margin;
        }

        /// <summary>Half the tile side at `cellSize` (canvas units).</summary>
        public float TileHalf(float cellSize) => cellSize * roomFraction * 0.5f;

        float Border(float cellSize) => Mathf.Max(2f, cellSize * borderFraction);

        /// <summary>The target star's box relative to its room center (canvas units); `insideCurrent` when the room is also the current one.</summary>
        public Rect StarBox(float cellSize, bool insideCurrent)
        {
            var half = TileHalf(cellSize);
            var border = Border(cellSize);
            var inset = insideCurrent ? border : 0f;
            var radius = cellSize * starFraction;
            var c = half - inset - border - radius * 1.1f;
            return new Rect(c - radius, c - radius, 2f * radius, 2f * radius);
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (map == null || !map.HasData || cell <= 0f) return;
            var r = rectTransform.rect;
            VisibleRange(r, center, cell, LookupMargin, out var minX, out var maxX, out var minY, out var maxY);
            var half = TileHalf(cell);
            var line = Mathf.Max(1f, cell * lineFraction) * 0.5f;
            var edge = tileEdgeFraction > 0f ? Mathf.Max(1f, cell * tileEdgeFraction) : 0f;
            var border = Border(cell);

            for (var y = minY; y <= maxY; y++)                                  // lines first, under the tiles
                for (var x = minX; x <= maxX; x++)
                {
                    if (!map.Has(new GridPos(x, y))) continue;
                    var c = Local(x, y);
                    if (map.Has(new GridPos(x + 1, y))) Quad(vh, new Vector2(c.x + half, c.y - line), new Vector2(c.x + cell - half, c.y + line), lineColor);
                    if (map.Has(new GridPos(x, y + 1))) Quad(vh, new Vector2(c.x - line, c.y + half), new Vector2(c.x + line, c.y + cell - half), lineColor);
                }

            for (var y = minY; y <= maxY; y++)
                for (var x = minX; x <= maxX; x++)
                    if (map.TryGet(new GridPos(x, y), out var room))
                    {
                        var c = Local(x, y);
                        var fill = room.Visited ? visitedColor : unvisitedColor;
                        if (edge > 0f && edge < half)
                        {
                            Quad(vh, c - new Vector2(half, half), c + new Vector2(half, half), lineColor);
                            Quad(vh, c - new Vector2(half - edge, half - edge), c + new Vector2(half - edge, half - edge), fill);
                        }
                        else Quad(vh, c - new Vector2(half, half), c + new Vector2(half, half), fill);
                    }

            var current = map.Current;
            if (map.Has(current) && InRange(current, minX, maxX, minY, maxY))
                Frame(vh, Local(current.X, current.Y), half, border, currentBorderColor);
            for (var y = minY; y <= maxY; y++)                                  // target: inner border (inside the current border when both) and a star
                for (var x = minX; x <= maxX; x++)
                    if (map.TryGet(new GridPos(x, y), out var room) && room.IsTarget)
                    {
                        var c = Local(x, y);
                        var both = room.Position == current;
                        Frame(vh, c, half - (both ? border : 0f), border, targetColor);
                        Star(vh, c + StarBox(cell, both).center, cell * starFraction, targetColor);
                    }
        }

        Vector2 Local(int x, int y) => rectTransform.rect.center + new Vector2((x - center.x) * cell, (y - center.y) * cell);

        static bool InRange(GridPos p, int minX, int maxX, int minY, int maxY) => p.X >= minX && p.X <= maxX && p.Y >= minY && p.Y <= maxY;

        /// <summary>QuadVertices vertices.</summary>
        static void Quad(VertexHelper vh, Vector2 min, Vector2 max, Color32 color)
        {
            var i = vh.currentVertCount;
            vh.AddVert(new Vector3(min.x, min.y), color, Vector2.zero);
            vh.AddVert(new Vector3(min.x, max.y), color, Vector2.zero);
            vh.AddVert(new Vector3(max.x, max.y), color, Vector2.zero);
            vh.AddVert(new Vector3(max.x, min.y), color, Vector2.zero);
            vh.AddTriangle(i, i + 1, i + 2);
            vh.AddTriangle(i + 2, i + 3, i);
        }

        /// <summary>FrameVertices vertices: a border `width` thick just inside the square of half size `half` around `c`.</summary>
        static void Frame(VertexHelper vh, Vector2 c, float half, float width, Color32 color)
        {
            if (half <= width) return;
            Quad(vh, new Vector2(c.x - half, c.y + half - width), new Vector2(c.x + half, c.y + half), color);
            Quad(vh, new Vector2(c.x - half, c.y - half), new Vector2(c.x + half, c.y - half + width), color);
            Quad(vh, new Vector2(c.x - half, c.y - half + width), new Vector2(c.x - half + width, c.y + half - width), color);
            Quad(vh, new Vector2(c.x + half - width, c.y - half + width), new Vector2(c.x + half, c.y + half - width), color);
        }

        /// <summary>StarVertices vertices: a five-pointed star (point up) of outer radius `radius`.</summary>
        static void Star(VertexHelper vh, Vector2 c, float radius, Color32 color)
        {
            var i = vh.currentVertCount;
            vh.AddVert(new Vector3(c.x, c.y), color, Vector2.zero);
            for (var k = 0; k < 2 * StarPoints; k++)
            {
                var a = Mathf.PI * 0.5f + k * Mathf.PI / StarPoints;
                var rr = k % 2 == 0 ? radius : radius * 0.45f;
                vh.AddVert(new Vector3(c.x + Mathf.Cos(a) * rr, c.y + Mathf.Sin(a) * rr), color, Vector2.zero);
            }
            for (var k = 0; k < 2 * StarPoints; k++) vh.AddTriangle(i, i + 1 + k, i + 1 + (k + 1) % (2 * StarPoints));
        }
    }
}
