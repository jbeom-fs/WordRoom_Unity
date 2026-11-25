using UnityEngine;

namespace WordRoom.Shelf
{
    /// <summary>
    /// A lectern of a reading room holding one document. Its root holds the one solid collider the screen-center ray
    /// finds. Local frame: origin on the floor under its center, +Z from the reader toward the wall behind it.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShelfLectern : MonoBehaviour
    {
        public ShelfDocument Document { get; private set; }
        /// <summary>Position in the room's document order.</summary>
        public int Index { get; private set; }

        internal void Init(ShelfDocument document, int index)
        {
            Document = document;
            Index = index;
        }

        /// <summary>
        /// Feet in the reading area in front of this lectern: on the reader side (toward the room center), within
        /// `range` of its center along its facing and within its width plus a margin sideways.
        /// </summary>
        public bool IsInReach(Vector3 feet, float range, float width)
        {
            var p = transform.InverseTransformPoint(feet);
            return p.z < 0f && p.z > -range && Mathf.Abs(p.x) < width * 0.5f + 0.5f;
        }
    }
}
