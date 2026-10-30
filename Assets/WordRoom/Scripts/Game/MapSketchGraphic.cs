using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using WordRoom.Generation;

namespace WordRoom.Game
{
    /// <summary>
    /// One reusable UI mesh of map drawing lines, in map units (its parent layer is moved and scaled by the view, so a
    /// pan or a zoom never rebuilds it; widths grow and shrink with the map). Each segment is one quad reaching half the
    /// width past both ends (so the joints close); a line of one point (or all in one place) is a small octagon. Rebuilt
    /// only when its lines are set again. Never a raycast target; masked by the viewport. No GameObjects per line.
    /// Vertex costs are Cost(line); MapSketchView keeps each mesh within MapGraphic.VertexBudget.
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class MapSketchGraphic : MaskableGraphic
    {
        public const int SegmentVertices = 4;
        public const int DotSides = 8;
        public const int DotVertices = DotSides + 1;

        IList<SketchStroke> lines;
        Func<string, Color32> colorOf;
        List<float> loose;                   // a line being drawn (x, y pairs) instead of `lines`
        float looseWidth;
        Color32 looseColor;

        /// <summary>Vertices one line needs here.</summary>
        public static int Cost(SketchStroke s) => s.Count == 1 || s.IsDot ? DotVertices : SegmentVertices * (s.Count - 1);

        /// <summary>Draws `set` (kept by reference: set it again after changing it) in each author's color.</summary>
        public void SetLines(IList<SketchStroke> set, Func<string, Color32> color)
        {
            lines = set;
            colorOf = color;
            loose = null;
            SetVerticesDirty();
        }

        /// <summary>Draws the points of one line in progress (kept by reference: set it again after changing it).</summary>
        public void SetLoose(List<float> points, float width, Color32 color)
        {
            loose = points;
            looseWidth = width;
            looseColor = color;
            lines = null;
            SetVerticesDirty();
        }

        public void Clear()
        {
            if (lines == null && loose == null) return;
            lines = null;
            loose = null;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (lines != null)
                foreach (var s in lines)
                    Line(vh, i => s.X(i), i => s.Y(i), s.Count, s.Width * 0.5f, colorOf != null ? colorOf(s.Author) : (Color32)Color.white);
            if (loose != null && loose.Count >= 2)
                Line(vh, i => loose[2 * i], i => loose[2 * i + 1], loose.Count / 2, looseWidth * 0.5f, looseColor);
        }

        static void Line(VertexHelper vh, Func<int, float> x, Func<int, float> y, int count, float half, Color32 color)
        {
            var drawn = false;
            for (var i = 0; i < count - 1; i++)
            {
                var a = new Vector2(x(i), y(i));
                var b = new Vector2(x(i + 1), y(i + 1));
                var d = b - a;
                var length = d.magnitude;
                if (length <= 0f) continue;
                var dir = d / length * half;
                var n = new Vector2(-dir.y, dir.x);
                a -= dir;
                b += dir;
                var k = vh.currentVertCount;
                vh.AddVert(a + n, color, Vector2.zero);
                vh.AddVert(b + n, color, Vector2.zero);
                vh.AddVert(b - n, color, Vector2.zero);
                vh.AddVert(a - n, color, Vector2.zero);
                vh.AddTriangle(k, k + 1, k + 2);
                vh.AddTriangle(k + 2, k + 3, k);
                drawn = true;
            }
            if (!drawn && count > 0) Dot(vh, new Vector2(x(0), y(0)), half, color);
        }

        static void Dot(VertexHelper vh, Vector2 c, float radius, Color32 color)
        {
            var k = vh.currentVertCount;
            vh.AddVert(c, color, Vector2.zero);
            for (var i = 0; i < DotSides; i++)
            {
                var a = i * Mathf.PI * 2f / DotSides;
                vh.AddVert(c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius, color, Vector2.zero);
            }
            for (var i = 0; i < DotSides; i++) vh.AddTriangle(k, k + 1 + i, k + 1 + (i + 1) % DotSides);
        }
    }
}
