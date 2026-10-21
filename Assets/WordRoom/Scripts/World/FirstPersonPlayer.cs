using UnityEngine;
using UnityEngine.InputSystem;

namespace WordRoom.World
{
    /// <summary>
    /// First-person walking inside the shown rooms: CharacterController with the root at the feet, WASD along the
    /// horizontal look direction, gravity, mouse look (yaw on this root, pitch on the camera child). Does nothing
    /// until PlaceAt put it on a room floor, so it never falls before the first room exists; Suspend stops it again
    /// for a new experiment. The cursor is locked while playing: Escape or focus loss releases it, a click in the
    /// Game view locks it again; no movement or look input while it is released. No jump, run, crouch or head bob.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterController))]
    public sealed class FirstPersonPlayer : MonoBehaviour
    {
        const float GroundedSpeed = -2f;           // keeps the controller pressed onto the floor while grounded

        [Tooltip("시점 카메라(이 오브젝트의 자식, 발 기준 높이 1.6m). 상하 회전만 이 카메라에 적용합니다.")]
        [SerializeField] Transform view;
        [Tooltip("걷기 속도(m/s)")]
        [SerializeField] float moveSpeed = 3f;
        [Tooltip("마우스 이동 1픽셀당 회전 각도(도). Input System의 마우스 delta는 프레임 이동량이라 deltaTime을 곱하지 않습니다.")]
        [SerializeField] float lookSensitivity = 0.1f;
        [Tooltip("상하 시점 제한(도)")]
        [SerializeField] float pitchLimit = 85f;

        CharacterController controller;
        float pitch;
        float verticalSpeed;
        bool placed;

        static bool CursorLocked => Cursor.lockState == CursorLockMode.Locked;

        void Awake()
        {
            controller = GetComponent<CharacterController>();
            if (view == null) Debug.LogError("[1인칭] 시점 카메라(View)가 지정되지 않아 상하 시점을 적용하지 않습니다.", this);
        }

        /// <summary>Stops walking, looking and falling until the next PlaceAt (new experiment, rooms not shown yet).</summary>
        public void Suspend()
        {
            placed = false;
            verticalSpeed = 0f;
        }

        /// <summary>
        /// Puts the feet on the floor below the center of a shown room (its actual height, found by a ray that skips
        /// this player's own colliders), facing 상(+Z) with a level view and no falling speed, then allows movement.
        /// Returns false with the reason and stays suspended when no floor of that room is below its center.
        /// </summary>
        public bool PlaceAt(Transform room, out string failure)
        {
            Suspend();
            Physics.SyncTransforms();                  // pooled rooms were just moved and activated
            var origin = room.position + Vector3.up * controller.height;
            RaycastHit? floor = null;
            foreach (var h in Physics.RaycastAll(origin, Vector3.down, controller.height * 2f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                if (!h.collider.transform.IsChildOf(transform) && (floor == null || h.distance < floor.Value.distance)) floor = h;   // never our own collider
            if (floor == null)
            {
                failure = $"방 '{room.name}' 중앙 아래에서 바닥 Collider를 찾지 못했습니다";
                return false;
            }
            if (!floor.Value.collider.transform.IsChildOf(room))
            {
                failure = $"방 '{room.name}' 중앙 아래 첫 Collider '{floor.Value.collider.name}'가 이 방의 것이 아닙니다";
                return false;
            }

            controller.enabled = false;                // a moved transform is not picked up by an enabled controller
            transform.SetPositionAndRotation(floor.Value.point + Vector3.up * controller.skinWidth, Quaternion.identity);
            controller.enabled = true;
            pitch = 0f;
            if (view != null) view.localRotation = Quaternion.identity;
            placed = true;
            if (Application.isFocused) SetCursorLock(true);
            failure = null;
            return true;
        }

        void Update()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame) SetCursorLock(false);
            else if (placed && !CursorLocked && Application.isFocused && mouse != null && mouse.leftButton.wasPressedThisFrame) SetCursorLock(true);
            if (!placed) return;

            var input = Vector2.zero;
            if (CursorLocked && Application.isFocused)
            {
                if (mouse != null) Look(mouse.delta.ReadValue());
                if (kb != null)
                    input = new Vector2((kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f),
                                        (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f));
            }
            input = Vector2.ClampMagnitude(input, 1f);  // diagonal no faster than straight
            var move = (transform.right * input.x + transform.forward * input.y) * moveSpeed;

            if (controller.isGrounded && verticalSpeed < 0f) verticalSpeed = GroundedSpeed;
            verticalSpeed += Physics.gravity.y * Time.deltaTime;
            controller.Move((move + Vector3.up * verticalSpeed) * Time.deltaTime);
        }

        /// <summary>Mouse delta is already a per-frame amount: no deltaTime here.</summary>
        void Look(Vector2 delta)
        {
            transform.Rotate(0f, delta.x * lookSensitivity, 0f);
            pitch = Mathf.Clamp(pitch - delta.y * lookSensitivity, -pitchLimit, pitchLimit);
            if (view != null) view.localRotation = Quaternion.Euler(pitch, 0f, 0f);
        }

        void OnApplicationFocus(bool focus)
        {
            if (!focus) SetCursorLock(false);
        }

        void OnDisable() => SetCursorLock(false);       // also runs when the component or object is destroyed

        static void SetCursorLock(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }
    }
}
