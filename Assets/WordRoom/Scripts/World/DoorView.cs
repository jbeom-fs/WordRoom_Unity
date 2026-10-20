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
    /// Apply sets a state immediately (used to restore an open connection without replaying an opening);
    /// there is no opening input or animation in this step.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DoorView : MonoBehaviour
    {
        [SerializeField] string boundTo = "";      // Inspector display only
        [SerializeField] DoorState state;

        Transform body;
        Vector3 closedLocalPosition;
        Collider[] colliders;
        bool[] colliderDefaults;

        public bool Bound { get; private set; }
        public RoomConnection Connection { get; private set; }
        public DoorState State => state;

        /// <summary>Called once after Instantiate. Throws InvalidOperationException when the body is missing.</summary>
        internal void Init(string bodyName)
        {
            var bodies = GetComponentsInChildren<Transform>(true).Where(t => t != transform && t.name == bodyName).ToList();
            if (bodies.Count != 1) throw new InvalidOperationException($"문 인스턴스에서 '{bodyName}' 본체를 하나로 찾지 못했습니다 ({bodies.Count}개).");
            body = bodies[0];
            closedLocalPosition = body.localPosition;
            colliders = body.GetComponentsInChildren<Collider>(true);
            colliderDefaults = colliders.Select(c => c.enabled).ToArray();
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
        }

        /// <summary>Immediate state: body position and colliders together. Open = closed + openLift upward, colliders off.</summary>
        public void Apply(DoorState s, float openLift)
        {
            state = s;
            var open = s == DoorState.Open;
            body.localPosition = open ? closedLocalPosition + body.parent.InverseTransformVector(Vector3.up * openLift) : closedLocalPosition;
            for (var i = 0; i < colliders.Length; i++)
                if (colliders[i] != null) colliders[i].enabled = !open && colliderDefaults[i];
        }

        internal void Unbind()
        {
            Apply(DoorState.Closed, 0f);
            Bound = false;
            boundTo = "";
            name = "Door (pooled)";
        }
    }
}
