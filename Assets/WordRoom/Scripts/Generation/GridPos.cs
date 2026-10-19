using System;
using System.Collections.Generic;

namespace WordRoom.Generation
{
    /// <summary>Room coordinate. Start (0, 0); 상 = y + 1, 하 = y - 1, 좌 = x - 1, 우 = x + 1.</summary>
    public readonly struct GridPos : IEquatable<GridPos>, IComparable<GridPos>
    {
        public readonly int X;
        public readonly int Y;

        public GridPos(int x, int y)
        {
            X = x;
            Y = y;
        }

        public static readonly GridPos Origin = new GridPos(0, 0);

        public GridPos Add(GridPos d) => new GridPos(X + d.X, Y + d.Y);

        /// <summary>Manhattan distance (kw_semantic_engine.dist).</summary>
        public static int Distance(GridPos p, GridPos q) => Math.Abs(p.X - q.X) + Math.Abs(p.Y - q.Y);

        public bool Equals(GridPos other) => X == other.X && Y == other.Y;
        public override bool Equals(object obj) => obj is GridPos other && Equals(other);
        public override int GetHashCode() => unchecked(X * 73856093 ^ Y * 19349663);
        public static bool operator ==(GridPos a, GridPos b) => a.Equals(b);
        public static bool operator !=(GridPos a, GridPos b) => !a.Equals(b);

        /// <summary>Python tuple order (x, then y), used where Python sorts coordinate tuples.</summary>
        public int CompareTo(GridPos other) => X != other.X ? X.CompareTo(other.X) : Y.CompareTo(other.Y);

        public override string ToString() => $"({X}, {Y})";
    }

    public enum Direction
    {
        Up,
        Down,
        Left,
        Right,
    }

    public static class Directions
    {
        /// <summary>
        /// Iteration order of Python kw_engine.DIRECTIONS ((0,1), (1,0), (0,-1), (-1,0)) = 상, 우, 하, 좌.
        /// Every generator loop over the four sides uses this order so planning matches the original.
        /// </summary>
        public static readonly Direction[] GenerationOrder = { Direction.Up, Direction.Right, Direction.Down, Direction.Left };

        /// <summary>Display order for logs: 상, 하, 좌, 우.</summary>
        public static readonly Direction[] DisplayOrder = { Direction.Up, Direction.Down, Direction.Left, Direction.Right };

        public static GridPos Offset(Direction d)
        {
            switch (d)
            {
                case Direction.Up: return new GridPos(0, 1);
                case Direction.Down: return new GridPos(0, -1);
                case Direction.Left: return new GridPos(-1, 0);
                default: return new GridPos(1, 0);
            }
        }

        public static string Korean(Direction d)
        {
            switch (d)
            {
                case Direction.Up: return "상";
                case Direction.Down: return "하";
                case Direction.Left: return "좌";
                default: return "우";
            }
        }

        /// <summary>Direction from `from` to an adjacent `to`, or null when they are not adjacent.</summary>
        public static Direction? Between(GridPos from, GridPos to)
        {
            foreach (var d in GenerationOrder)
                if (from.Add(Offset(d)) == to) return d;
            return null;
        }

        /// <summary>The four neighbor coordinates in generation order.</summary>
        public static IEnumerable<GridPos> Around(GridPos p)
        {
            foreach (var d in GenerationOrder) yield return p.Add(Offset(d));
        }
    }
}
