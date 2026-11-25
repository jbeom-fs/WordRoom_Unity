using System.Collections.Generic;
using UnityEngine;

namespace WordRoom.Shelf
{
    /// <summary>
    /// One built reading room: an octagon centered on this object (floor top at local Y 0), one lectern per document
    /// of its headword in document order.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShelfRoomView : MonoBehaviour
    {
        ShelfLectern[] lecterns;

        public ShelfWordRoom Room { get; private set; }
        /// <summary>Center to the inner wall face.</summary>
        public float Apothem { get; private set; }
        /// <summary>The floor box, centered under the room center (the player is placed on it).</summary>
        public Transform Floor { get; private set; }
        public IReadOnlyList<ShelfLectern> Lecterns => lecterns;

        internal void Init(ShelfWordRoom room, float apothem, Transform floor, ShelfLectern[] lecterns)
        {
            Room = room;
            Apothem = apothem;
            Floor = floor;
            this.lecterns = lecterns;
        }
    }
}
