using UnityEngine;

namespace WordRoom.Shelf
{
    /// <summary>
    /// A link's handle, right below its word on the word's door. Its root holds the one non-trigger collider the
    /// screen-center ray finds. Pulling it opens its door toward the handle's target headword's room.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShelfHandle : MonoBehaviour
    {
        static readonly Quaternion Rest = Quaternion.Euler(-20f, 0f, 0f);
        static readonly Quaternion Pulled = Quaternion.Euler(-115f, 0f, 0f);

        Transform lever;
        Collider aim;

        public ShelfPassage Passage { get; private set; }
        public string TargetId { get; private set; }

        internal void Init(ShelfPassage passage, string targetId, Transform lever, Collider aim)
        {
            Passage = passage;
            TargetId = targetId;
            this.lever = lever;
            this.aim = aim;
            SetPull(0f);
        }

        /// <summary>0 = at rest, 1 = pulled down toward the corridor.</summary>
        internal void SetPull(float amount) => lever.localRotation = Quaternion.Slerp(Rest, Pulled, Mathf.Clamp01(amount));

        /// <summary>Off while its door slides, so a moving handle never pushes into the player.</summary>
        internal void SetAimable(bool on)
        {
            if (aim != null) aim.enabled = on;
        }
    }
}
