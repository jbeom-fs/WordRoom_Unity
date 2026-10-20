using UnityEngine;
using WordRoom.Generation;

namespace WordRoom.World
{
    /// <summary>
    /// One pooled room instance bound to a coordinate. Added to instances at runtime (the prefab asset is not
    /// changed). Holds no logical data of its own: the word comes from RoomMap and stays there when returned.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RoomView : MonoBehaviour
    {
        [SerializeField] string boundTo = "";      // Inspector display only

        public bool Bound { get; private set; }
        public GridPos Position { get; private set; }
        public int Word { get; private set; }

        /// <summary>Sets position, rotation and scale explicitly on every rent (no leftovers from earlier use).</summary>
        internal void Bind(GridPos p, int word, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            Position = p;
            Word = word;
            Bound = true;
            boundTo = $"{p} · 단어 #{word}";
            name = $"Room {p}";
            transform.SetPositionAndRotation(position, rotation);
            transform.localScale = scale;
        }

        internal void Unbind()
        {
            Bound = false;
            boundTo = "";
            name = "Room (pooled)";
        }
    }
}
