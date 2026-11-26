using System;
using System.Collections.Generic;

namespace WordRoom.Shelf
{
    /// <summary>One desk of a reading room: the side it stands at and the documents (a run in document order) on it.</summary>
    public readonly struct ShelfDeskPlan
    {
        /// <summary>Side 0..7: 0 straight ahead on arrival (+Z), numbered clockwise seen from above.</summary>
        public readonly int Side;
        /// <summary>Index of the first document on this desk (ShelfWordRoom.Documents).</summary>
        public readonly int First;
        /// <summary>Number of lecterns on this desk (≥ 1).</summary>
        public readonly int Count;

        public ShelfDeskPlan(int side, int first, int count)
        {
            Side = side;
            First = first;
            Count = count;
        }
    }

    /// <summary>
    /// Logical furniture of a reading room, apart from any scene object: one lectern per document and at most one desk
    /// per side. It depends on the number of documents only, so the same room data gives the same plan on every
    /// client and on every visit (a host only has to agree on the headword).
    /// Document i of n goes to side ⌊i·8/n⌋: up to eight documents get a side each, spread as evenly as eight sides
    /// allow (1 → side 0, 3 → sides 0·2·5, 8 → every side); more share the eight desks, the extra ones spread over
    /// the sides (10 → two lecterns on sides 0 and 4). The sides are non-decreasing in document order, so each desk
    /// holds a run of documents, left to right seen from the room center. The open book on each lectern cycles through
    /// the variants in document order (BookVariant).
    /// </summary>
    public static class ShelfFurniturePlan
    {
        public const int Sides = 8;

        /// <summary>Side of document `index` of `count`.</summary>
        public static int SideOf(int index, int count) => count <= 1 ? 0 : (int)((long)index * Sides / count);

        /// <summary>The desks of a room with `documentCount` documents, by side (none for no document).</summary>
        public static List<ShelfDeskPlan> Create(int documentCount)
        {
            var desks = new List<ShelfDeskPlan>(Math.Min(Sides, Math.Max(0, documentCount)));
            var i = 0;
            while (i < documentCount)
            {
                var side = SideOf(i, documentCount);
                var first = i;
                while (i < documentCount && SideOf(i, documentCount) == side) i++;
                desks.Add(new ShelfDeskPlan(side, first, i - first));
            }
            return desks;
        }

        /// <summary>
        /// Book variant on the lectern of document `index`: the variants in turn by document order, so the same room
        /// always shows the same book on the same lectern.
        /// </summary>
        public static int BookVariant(int index, int variants) => variants <= 0 ? 0 : index % variants;

        /// <summary>Largest number of lecterns on one desk for `documentCount` documents.</summary>
        public static int MaxPerDesk(int documentCount)
        {
            var max = 0;
            foreach (var d in Create(documentCount)) max = Math.Max(max, d.Count);
            return max;
        }
    }

    /// <summary>
    /// How `count` lecterns share one desk top: the top width, the lectern scale and their offsets from the top center
    /// (meters along the desk's right, seen from the reader). Lecterns stand side by side, symmetric about the center
    /// and evenly spaced over the usable width (the top minus an edge margin on each side). When they do not fit the
    /// normal top with at least `minGap` between them, the top widens up to `maxTopWidth` (the width that keeps the
    /// walkway to the neighboring desks); beyond that the lecterns are scaled down so they still never overlap or
    /// leave the top.
    /// </summary>
    public readonly struct ShelfDeskFit
    {
        public readonly float TopWidth;
        public readonly float LecternScale;
        /// <summary>Center distance of neighboring lecterns (0 for one).</summary>
        public readonly float Pitch;
        public readonly int Count;

        ShelfDeskFit(float topWidth, float scale, float pitch, int count)
        {
            TopWidth = topWidth;
            LecternScale = scale;
            Pitch = pitch;
            Count = count;
        }

        /// <summary>Offset of lectern `j` (0 = leftmost seen from the reader) from the top center.</summary>
        public float Offset(int j) => (j - (Count - 1) * 0.5f) * Pitch;

        /// <summary>Gap between neighboring lectern edges (meters).</summary>
        public float Gap(float lecternWidth) => Count > 1 ? Pitch - lecternWidth * LecternScale : 0f;

        public static ShelfDeskFit Compute(int count, float baseTopWidth, float maxTopWidth, float lecternWidth, float edgeMargin, float minGap)
        {
            count = Math.Max(1, count);
            maxTopWidth = Math.Max(baseTopWidth, maxTopWidth);
            var usableBase = baseTopWidth - 2f * edgeMargin;
            if (count == 1) return new ShelfDeskFit(baseTopWidth, Math.Min(1f, usableBase / lecternWidth), 0f, 1);

            var needed = count * lecternWidth + (count - 1) * minGap + 2f * edgeMargin;   // at full size
            float width, scale;
            if (needed <= baseTopWidth)
            {
                width = baseTopWidth;
                scale = 1f;
            }
            else if (needed <= maxTopWidth)
            {
                width = needed;
                scale = 1f;
            }
            else
            {
                width = maxTopWidth;
                scale = (maxTopWidth - 2f * edgeMargin - (count - 1) * minGap) / (count * lecternWidth);
            }
            // Evenly over the usable width: the outer lecterns touch the edge margins.
            var pitch = (width - 2f * edgeMargin - lecternWidth * scale) / (count - 1);
            return new ShelfDeskFit(width, scale, pitch, count);
        }
    }
}
