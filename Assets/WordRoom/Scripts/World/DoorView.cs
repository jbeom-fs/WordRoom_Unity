using System;
using System.Linq;
using UnityEngine;

namespace WordRoom.World
{
    /// <summary>
    /// One pooled door instance bound to a connection. Added to instances at runtime (the prefab asset is not
    /// changed). The TestDoor parent is placed and turned; the child body slides up when open. The body's closed
    /// local position and its colliders' enabled flags are read once from the fresh instance and are the reset
    /// values for every rent and return, so a new connection never inherits an earlier door's open position.
    /// Apply sets a state immediately (restoring an open connection without replaying an opening) and stops any
    /// opening in progress; BeginOpening slides the body up over a duration, keeping its colliders on until the
    /// exact end position is reached. State = the applied target; Opening = the slide is still running.
    /// A separate passage block (a runtime child on the Ignore Raycast layer with the closed body's box, not
    /// moving with it) closes the doorway whenever passing is not allowed, independent of the door state.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DoorView : MonoBehaviour
    {
        const int IgnoreRaycastLayer = 2;          // built-in layer: not hit by default raycasts (aim, floor search)

        [SerializeField] string boundTo = "";      // Inspector display only
        [SerializeField] DoorState state;

        Transform body;
        Vector3 closedLocalPosition;
        Collider[] colliders;
        bool[] colliderDefaults;
        BoxCollider passageBlock;
        bool opening;
        float openProgress;
        float openSeconds;
        Vector3 openLocalPosition;

        public bool Bound { get; private set; }
        public RoomConnection Connection { get; private set; }
        public DoorState State => state;

        /// <summary>The body is still sliding up (state is already Open).</summary>
        public bool Opening => opening;

        /// <summary>Open and at its exact end position, colliders off.</summary>
        public bool FullyOpen => state == DoorState.Open && !opening;

        /// <summary>Called once after Instantiate. Throws InvalidOperationException when the body is missing.</summary>
        internal void Init(string bodyName)
        {
            var bodies = GetComponentsInChildren<Transform>(true).Where(t => t != transform && t.name == bodyName).ToList();
            if (bodies.Count != 1) throw new InvalidOperationException($"문 인스턴스에서 '{bodyName}' 본체를 하나로 찾지 못했습니다 ({bodies.Count}개).");
            body = bodies[0];
            closedLocalPosition = body.localPosition;
            colliders = body.GetComponentsInChildren<Collider>(true);
            colliderDefaults = colliders.Select(c => c.enabled).ToArray();
            passageBlock = CreatePassageBlock(body);
        }

        /// <summary>
        /// Sibling of the body with the body's closed local pose and box (its BoxCollider, else its mesh bounds), so it
        /// fills the doorway exactly like the closed door but stays when the body slides up. Blocks by default.
        /// </summary>
        static BoxCollider CreatePassageBlock(Transform body)
        {
            var go = new GameObject("PassageBlock") { layer = IgnoreRaycastLayer };
            var t = go.transform;
            t.SetParent(body.parent, false);
            t.localPosition = body.localPosition;
            t.localRotation = body.localRotation;
            t.localScale = body.localScale;
            var box = go.AddComponent<BoxCollider>();
            var source = body.GetComponent<BoxCollider>();
            var mesh = body.GetComponent<MeshFilter>();
            if (source != null)
            {
                box.center = source.center;
                box.size = source.size;
            }
            else if (mesh != null && mesh.sharedMesh != null)
            {
                box.center = mesh.sharedMesh.bounds.center;
                box.size = mesh.sharedMesh.bounds.size;
            }
            return box;
        }

        internal void Bind(RoomConnection id, Vector3 position, Quaternion rotation, Vector3 scale, DoorState s, float openLift)
        {
            Connection = id;
            Bound = true;
            boundTo = id.ToString();
            name = $"Door {id}";
            transform.SetPositionAndRotation(position, rotation);
            transform.localScale = scale;
            Apply(s, openLift);
            SetPassable(false);
        }

        /// <summary>
        /// Immediate state: body position and colliders together, any opening in progress stopped.
        /// Open = closed + openLift upward (world up), colliders off.
        /// </summary>
        public void Apply(DoorState s, float openLift)
        {
            opening = false;
            state = s;
            var open = s == DoorState.Open;
            body.localPosition = open ? OpenLocalPosition(openLift) : closedLocalPosition;
            for (var i = 0; i < colliders.Length; i++)
                if (colliders[i] != null) colliders[i].enabled = !open && colliderDefaults[i];
        }

        /// <summary>
        /// Starts sliding a closed door up over `seconds` (0 or less = at once). The state becomes Open now; the
        /// colliders stay as closed until the end position is reached. False when not bound or not closed.
        /// </summary>
        internal bool BeginOpening(float openLift, float seconds)
        {
            if (!Bound || state != DoorState.Closed) return false;
            if (seconds <= 0f)
            {
                Apply(DoorState.Open, openLift);
                return true;
            }
            state = DoorState.Open;
            opening = true;
            openProgress = 0f;
            openSeconds = seconds;
            openLocalPosition = OpenLocalPosition(openLift);
            return true;
        }

        /// <summary>
        /// Bound to `id`, active in the scene, body present and the passage block present, active and in the wanted
        /// state (off only when `passable`). Call only on a live instance.
        /// </summary>
        internal bool IsReady(RoomConnection id, bool passable) =>
            Bound && Connection == id && gameObject.activeInHierarchy && body != null &&
            passageBlock != null && passageBlock.gameObject.activeInHierarchy && passageBlock.enabled == !passable;

        /// <summary>Passage block off (passing allowed) or on. Independent of the door state and its colliders.</summary>
        internal void SetPassable(bool passable)
        {
            if (passageBlock != null && passageBlock.enabled == passable) passageBlock.enabled = !passable;
        }

        Vector3 OpenLocalPosition(float openLift) => closedLocalPosition + body.parent.InverseTransformVector(Vector3.up * openLift);

        void Update()
        {
            if (!opening) return;
            openProgress += Time.deltaTime / openSeconds;
            if (openProgress >= 1f)
            {
                opening = false;
                body.localPosition = openLocalPosition;                  // exact end position, then colliders off
                for (var i = 0; i < colliders.Length; i++)
                    if (colliders[i] != null) colliders[i].enabled = false;
                return;
            }
            body.localPosition = Vector3.Lerp(closedLocalPosition, openLocalPosition, Mathf.SmoothStep(0f, 1f, openProgress));
        }

        internal void Unbind()
        {
            Apply(DoorState.Closed, 0f);
            SetPassable(false);
            Bound = false;
            boundTo = "";
            name = "Door (pooled)";
        }
    }
}
