using System;
using WordRoom.Generation;

namespace WordRoom.World
{
    /// <summary>
    /// Shared connection between two adjacent coordinates, normalized so A→B and B→A give the same id.
    /// A is the left coordinate of a 좌우 connection and the lower one of a 상하 connection; the door of the
    /// connection is always placed from A's 우 or 상 doorway, whatever the visit or request order.
    /// The id says nothing about whether either room exists or can be entered.
    /// </summary>
    public readonly struct RoomConnection : IEquatable<RoomConnection>, IComparable<RoomConnection>
    {
        public readonly GridPos A;
        public readonly GridPos B;

        RoomConnection(GridPos a, GridPos b)
        {
            A = a;
            B = b;
        }

        /// <summary>Connection between two adjacent coordinates (any order). Throws for non-adjacent ones.</summary>
        public static RoomConnection Between(GridPos p, GridPos q)
        {
            if (GridPos.Distance(p, q) != 1) throw new ArgumentException($"인접하지 않은 좌표의 연결: {p}–{q}");
            return p.CompareTo(q) < 0 ? new RoomConnection(p, q) : new RoomConnection(q, p);   // x first, then y
        }

        public static RoomConnection Of(GridPos p, Direction side) => Between(p, p.Add(Directions.Offset(side)));

        /// <summary>좌우 connection (same y).</summary>
        public bool Horizontal => A.Y == B.Y;

        /// <summary>Room whose doorway places the door: the left (좌우) or lower (상하) coordinate.</summary>
        public GridPos Anchor => A;

        /// <summary>Doorway side of the anchor room: 우 for 좌우, 상 for 상하.</summary>
        public Direction AnchorSide => Horizontal ? Direction.Right : Direction.Up;

        public bool Equals(RoomConnection other) => A == other.A && B == other.B;
        public override bool Equals(object obj) => obj is RoomConnection other && Equals(other);
        public override int GetHashCode() => unchecked(A.GetHashCode() * 397 ^ B.GetHashCode());
        public static bool operator ==(RoomConnection a, RoomConnection b) => a.Equals(b);
        public static bool operator !=(RoomConnection a, RoomConnection b) => !a.Equals(b);
        public int CompareTo(RoomConnection other) => A != other.A ? A.CompareTo(other.A) : B.CompareTo(other.B);
        public override string ToString() => $"{A}-{B}";
    }
}
