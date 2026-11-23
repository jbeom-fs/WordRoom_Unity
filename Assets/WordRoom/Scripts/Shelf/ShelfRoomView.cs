using System.Collections.Generic;
using UnityEngine;

namespace WordRoom.Shelf
{
    /// <summary>
    /// One built corridor room. Origin at the entrance (inner face of the entrance wall), corridor toward local +Z,
    /// interior X within ±HalfWidth and Z within 0..Length. Rooms reached through a passage also own the connecting
    /// passage behind their entrance; its far end is sealed once the player has entered (the previous room is gone).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShelfRoomView : MonoBehaviour
    {
        readonly List<ShelfPassage> passages = new List<ShelfPassage>();
        GameObject entranceSeal;

        public ShelfEntry Room { get; private set; }
        public float Length { get; private set; }
        public float HalfWidth { get; private set; }
        public Transform Floor { get; private set; }
        public IReadOnlyList<ShelfPassage> Passages => passages;

        internal void Init(ShelfEntry room, float length, float halfWidth, Transform floor, GameObject entranceSeal)
        {
            Room = room;
            Length = length;
            HalfWidth = halfWidth;
            Floor = floor;
            this.entranceSeal = entranceSeal;
        }

        internal void Add(ShelfPassage passage) => passages.Add(passage);

        /// <summary>Feet past the entrance by more than `margin` and between the side walls.</summary>
        public bool HasEntered(Vector3 feet, float margin)
        {
            var p = transform.InverseTransformPoint(feet);
            return p.z > margin && Mathf.Abs(p.x) < HalfWidth;
        }

        /// <summary>Closes the far end of the connecting passage behind the entrance (no-op for the start room).</summary>
        public void SealEntrance()
        {
            if (entranceSeal != null) entranceSeal.SetActive(true);
        }
    }
}
