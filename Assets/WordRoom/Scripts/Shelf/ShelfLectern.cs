using UnityEngine;

namespace WordRoom.Shelf
{
    /// <summary>
    /// Low reading stand prefab of the reading room (ReadingStand.prefab), holding one document and one open book.
    /// Pivot at the desk top under the stand center; local +Z from the reader toward the bookcase. `bookAnchor` lies on
    /// the slanted display panel (+X right, +Y uphill, -Z out of the panel toward the reader) and the book's underside
    /// rests on it. Nothing on the stand shows the document: its title and text appear only on the reading screen,
    /// opened with E while aiming at the book. Pooled by ShelfRoomView apart from desks and books: each reuse replaces
    /// the document and the book (Bind); put away it holds neither.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShelfLectern : MonoBehaviour
    {
        [Tooltip("기울어진 판 표면의 책 기준점(+X 오른쪽, +Y 판 위쪽, -Z 판 바깥). 책 밑면 중심이 여기에 놓입니다.")]
        [SerializeField] Transform bookAnchor;
        [Tooltip("독서대 폭(m, 배율 1 기준)")]
        [Min(0.1f)] [SerializeField] float width = 0.692f;

        /// <summary>What E opens on the reading screen (null when put away).</summary>
        public ShelfDocument Document { get; private set; }
        /// <summary>Position in the room's document order.</summary>
        public int Index { get; private set; } = -1;
        /// <summary>The open book on the panel (null when put away).</summary>
        public ShelfBook Book { get; private set; }
        public Transform BookAnchor => bookAnchor;
        /// <summary>Width at scale 1 (meters).</summary>
        public float BaseWidth => width;
        public bool IsComplete => bookAnchor != null;

        /// <summary>Holds `document` (document order `index`) with `book` on its panel; the previous ones are gone.</summary>
        public void Bind(ShelfDocument document, int index, ShelfBook book)
        {
            Document = document;
            Index = index;
            Book = book;
        }

        /// <summary>Put away (pool): no document, no book.</summary>
        public void Unbind()
        {
            Document = null;
            Index = -1;
            Book = null;
        }

        /// <summary>
        /// Feet in the reading area in front of this lectern, in meters on the floor plane: on the reader side (toward
        /// the room center) within `range` of its center along its facing, and within its width plus a margin sideways.
        /// </summary>
        public bool IsInReach(Vector3 feet, float range)
        {
            var forward = transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-6f) return false;
            forward.Normalize();
            var right = new Vector3(forward.z, 0f, -forward.x);
            var d = feet - transform.position;
            d.y = 0f;
            var along = Vector3.Dot(d, forward);
            var side = Vector3.Dot(d, right);
            return along < 0f && along > -range && Mathf.Abs(side) < width * transform.lossyScale.x * 0.5f + 0.5f;
        }

#if UNITY_EDITOR
        /// <summary>Editor setup only: wires the prefab's book anchor and measured width.</summary>
        public void EditorSetup(Transform bookAnchor, float width)
        {
            this.bookAnchor = bookAnchor;
            this.width = width;
        }
#endif
    }
}
