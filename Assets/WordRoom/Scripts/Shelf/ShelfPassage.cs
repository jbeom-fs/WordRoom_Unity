using System.Collections.Generic;
using UnityEngine;

namespace WordRoom.Shelf
{
    /// <summary>
    /// One link word's door: the wall section that carries the word and its handle, over a passage-size opening in a
    /// side wall. Open(handle, rooms, fork): the handle swings down and hides, then the door (with its word) slides by
    /// its width into the wall in reading direction (the pocket kept free of other openings), leaving the opening free;
    /// wall text in front of the pocket stays visible. What was built beforehand stays behind it: one destination room,
    /// or a fork (stem, hall and branch signs) with one destination room per branch.
    /// Close: everything built for the opening is destroyed and the door and handle are back as built.
    /// Commit(room): keeps only the entered room and destroys the fork and the other branches' rooms. Nothing built
    /// for the opening is a child of this door's room (separate roots), so removing that room never removes them.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShelfPassage : MonoBehaviour
    {
        public enum State
        {
            Closed,
            Opening,
            Open
        }

        const float HandleShare = 0.3f;          // part of openSeconds used by the handle before the door moves

        readonly List<ShelfHandle> handles = new List<ShelfHandle>();
        readonly List<ShelfRoomView> destinations = new List<ShelfRoomView>();
        GameObject fork;
        Vector3 closedPosition;
        Vector3 slide;
        float openSeconds;
        float elapsed;
        ShelfHandle pulled;

        public ShelfRoomView Owner { get; private set; }
        /// <summary>-1 left wall, +1 right wall (room-local X sign of the wall).</summary>
        public int Side { get; private set; }
        /// <summary>Room-local Z of the opening's center.</summary>
        public float CenterZ { get; private set; }
        public float Width { get; private set; }
        public State Current { get; private set; }
        /// <summary>Target headword of the handle that opened the door (null while closed).</summary>
        public string TargetId => Current == State.Closed || pulled == null ? null : pulled.TargetId;
        /// <summary>Rooms reachable through the open door (one, or one per fork branch); empty while closed.</summary>
        public IReadOnlyList<ShelfRoomView> Destinations => destinations;
        public bool HasFork => fork != null;

        /// <summary>This object is the door itself (a child of the room); `slide` is its room-local opening move.</summary>
        internal void Init(ShelfRoomView owner, int side, float centerZ, float width, Vector3 slide, float openSeconds)
        {
            Owner = owner;
            Side = side;
            CenterZ = centerZ;
            Width = width;
            closedPosition = transform.localPosition;
            this.slide = slide;
            this.openSeconds = openSeconds;
        }

        internal void Add(ShelfHandle handle) => handles.Add(handle);

        /// <summary>Pose at this wall's outer face, centered on the opening, +Z pointing away from the room.</summary>
        public void OuterPose(ShelfLayout layout, out Vector3 position, out Quaternion rotation)
        {
            var room = Owner.transform;
            position = room.TransformPoint(new Vector3(Side * (layout.corridorWidth * 0.5f + layout.wallThickness), 0f, CenterZ));
            rotation = room.rotation * Quaternion.LookRotation(new Vector3(Side, 0f, 0f), Vector3.up);
        }

        /// <summary>
        /// Pose for a directly linked room's origin (its entrance, facing away from this wall): this wall's outer
        /// face, then `tunnel` of connecting passage, then the room's own entrance wall.
        /// </summary>
        public void DestinationPose(ShelfLayout layout, out Vector3 position, out Quaternion rotation)
        {
            OuterPose(layout, out var outer, out rotation);
            position = outer + rotation * new Vector3(0f, 0f, layout.tunnelLength + layout.wallThickness);
        }

        /// <summary>
        /// Feet within reach of this opening: in it, close enough in front of it that the door coming back could
        /// overlap the body, or anywhere outside the room on this side (passage or fork).
        /// </summary>
        public bool IsNear(Vector3 feet, ShelfLayout layout)
        {
            var p = Owner.transform.InverseTransformPoint(feet);
            var half = layout.corridorWidth * 0.5f;
            var across = Side * p.x;
            return across > half || (across > half - 0.5f && Mathf.Abs(p.z - CenterZ) < Width * 0.5f + 0.5f);
        }

        /// <summary>Starts opening toward rooms (and their fork) already built for `handle`. Ignored unless closed.</summary>
        public void Open(ShelfHandle handle, IReadOnlyList<ShelfRoomView> rooms, GameObject forkRoot)
        {
            if (Current != State.Closed) return;
            pulled = handle;
            destinations.Clear();
            destinations.AddRange(rooms);
            fork = forkRoot;
            Current = State.Opening;
            elapsed = 0f;
            foreach (var h in handles) h.SetAimable(false);
        }

        /// <summary>Destroys everything built for the opening and puts the door and handle back.</summary>
        public void Close()
        {
            DestroyBuilt(null);
            pulled = null;
            Current = State.Closed;
            transform.localPosition = closedPosition;
            foreach (var h in handles)
            {
                h.gameObject.SetActive(true);
                h.SetPull(0f);
                h.SetAimable(true);
            }
        }

        /// <summary>The player entered `chosen`: the fork and every other destination are destroyed, `chosen` stays.</summary>
        public void Commit(ShelfRoomView chosen) => DestroyBuilt(chosen);

        void DestroyBuilt(ShelfRoomView keep)
        {
            foreach (var room in destinations)
                if (room != null && room != keep) Destroy(room.gameObject);
            destinations.Clear();
            if (fork != null) Destroy(fork);
            fork = null;
        }

        void Update()
        {
            if (Current != State.Opening) return;
            elapsed += Time.deltaTime;
            var k = openSeconds > 0f ? Mathf.Clamp01(elapsed / openSeconds) : 1f;
            if (pulled != null) pulled.SetPull(k / HandleShare);
            if (k < HandleShare) return;
            // The door goes into the wall; its handles would stick out over the wall text there, so they hide.
            foreach (var h in handles) h.gameObject.SetActive(false);
            var move = Mathf.SmoothStep(0f, 1f, (k - HandleShare) / (1f - HandleShare));
            transform.localPosition = closedPosition + slide * move;
            if (k < 1f) return;
            Current = State.Open;
        }
    }
}
