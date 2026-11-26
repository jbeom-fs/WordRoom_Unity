using UnityEngine;

namespace WordRoom.Shelf
{
    /// <summary>
    /// An open book lying on a reading stand (OpenBook_01…08.prefab). Pivot at the center of its underside, which lies
    /// on the stand's slanted panel; local +X to the reader's right, +Y toward the head (uphill), -Z out of the panel
    /// (the book's thickness). Covers and pages carry no text: what the book holds is only read on the reading screen.
    /// Its box collider fits the book and is what the screen-center ray picks, so books side by side on one desk are
    /// told apart. Pooled per variant by ShelfRoomView and parented to the stand's book anchor while in use, so it
    /// scales with the stand.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShelfBook : MonoBehaviour
    {
        [Tooltip("책 종류 번호(0부터, OpenBook_01 = 0)")]
        [Min(0)] [SerializeField] int variant;
        [Tooltip("조준 범위(책 크기에 맞춘 Collider)")]
        [SerializeField] BoxCollider aim;

        public int Variant => variant;
        public BoxCollider Aim => aim;
        /// <summary>The stand holding this book now (null while pooled).</summary>
        public ShelfLectern Holder { get; private set; }
        public bool IsComplete => aim != null;

        internal void AttachTo(ShelfLectern lectern)
        {
            Holder = lectern;
            transform.SetParent(lectern.BookAnchor, false);
            transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            transform.localScale = Vector3.one;
            if (!gameObject.activeSelf) gameObject.SetActive(true);
        }

        internal void PutAway(Transform pool)
        {
            Holder = null;
            if (gameObject.activeSelf) gameObject.SetActive(false);
            if (transform.parent != pool) transform.SetParent(pool, false);
        }

#if UNITY_EDITOR
        /// <summary>Editor setup only.</summary>
        public void EditorSetup(int variant, BoxCollider aim)
        {
            this.variant = variant;
            this.aim = aim;
        }
#endif
    }
}
