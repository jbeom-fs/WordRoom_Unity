using UnityEngine;
using UnityEngine.InputSystem;
using WordRoom.Settings;

namespace WordRoom.World
{
    /// <summary>
    /// First-person walking inside the shown rooms: CharacterController with the root at the feet, WASD along the
    /// horizontal look direction, gravity, mouse look (yaw on this root, pitch on the camera child). Does nothing
    /// until PlaceAt put it on a room floor, so it never falls before the first room exists; Suspend stops it again
    /// for a new experiment, and disabling the component also drops the placement (re-enabling never resumes an
    /// earlier session). SetMovementLocked stops only walking: look, gravity and Escape keep working (room entry
    /// in progress, finished experiment, stop after an error). The cursor is locked while playing: Escape or focus
    /// loss releases it, a click in the Game view locks it again; no movement or look input while it is released.
    /// No jump, run, crouch or head bob. Door aiming and the E key are read here; what they do is decided outside.
    /// Player settings: look = base sensitivity × GameSettings.MouseSensitivity (computed at use, never stored back),
    /// optional inverted vertical look, and the view camera's field of view, applied on enable and on every change.
    /// SetInputBlocked (an open game menu) is separate from SetMovementLocked (a generation request): it stops look,
    /// walking, E and click-to-lock but not gravity, and unblocking takes effect from the next frame so the closing
    /// click or key never acts in the game. With HandlesEscape off a menu owns the Escape key.
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
        [Tooltip("켜져 있으면 아래 '시점 기준 위치'를 원래 시점으로 씁니다. 꺼져 있으면 실행 시 확인용 카메라가 옮기기 전의 시점 카메라 위치를 한 번 기록합니다.")]
        [SerializeField] bool viewHomeSaved;
        [Tooltip("시점 카메라의 원래 로컬 위치(발 기준, 기본 장면 1.6m). 배치할 때마다 이 위치·수평 시점으로 되돌립니다. 시점 카메라 높이를 바꾸면 이 값도 맞추세요.")]
        [SerializeField] Vector3 viewHomeLocalPosition;

        CharacterController controller;
        Camera viewCamera;
        float pitch;
        float verticalSpeed;
        bool placed;
        bool movementLocked;
        bool inputBlocked;
        int inputResumeFrame;
        bool relockOnResume;
        bool mapMode;

        static bool CursorLocked => Cursor.lockState == CursorLockMode.Locked;

        /// <summary>Placed on a floor by PlaceAt and enabled: walking (unless locked), looking and falling run.</summary>
        public bool Placed => placed && isActiveAndEnabled;

        /// <summary>Not blocked by a menu, and not in the frame its closing input arrived.</summary>
        bool InputAllowed => !inputBlocked && Time.frameCount >= inputResumeFrame;

        /// <summary>Placed, cursor locked, the game focused, no menu open and no map shown: the state in which mouse look is read.</summary>
        public bool HasControlFocus => Placed && CursorLocked && Application.isFocused && InputAllowed && !mapMode;

        /// <summary>
        /// Walking keys are read now: placed and enabled, window focused, no menu block and not in the frame a block or
        /// the map mode was lifted, walking not locked, and the cursor locked — or the map mode on (the map keeps the
        /// cursor free for itself while the player may walk).
        /// </summary>
        bool WalkInputAllowed => Placed && Application.isFocused && InputAllowed && !movementLocked && (CursorLocked || mapMode);

        /// <summary>
        /// The player can really be played now: exactly the walking condition read in Update (also with the map open
        /// and the cursor free), so nothing counts as playable before walking works.
        /// </summary>
        public bool CanTakeInput => WalkInputAllowed;

        /// <summary>
        /// Map mode (set by the map screen while it is shown): the game keys go on — walking, gravity, aiming at doors and
        /// E (CanInteract) — while mouse look and click-to-lock stop and the cursor stays free for the map. Separate from
        /// the menu block (SetInputBlocked) and the generation lock (SetMovementLocked): turning it off lifts neither.
        /// Turning it off skips input for one frame (the closing click or key never acts in the game), then resumes mouse
        /// look and locks the cursor again only when no menu block is on.
        /// </summary>
        public void SetMapMode(bool on)
        {
            if (on)
            {
                mapMode = true;
                return;
            }
            if (!mapMode) return;
            mapMode = false;
            inputResumeFrame = Time.frameCount + 1;                            // the closing click or key never acts in the game
            relockOnResume = true;
        }

        /// <summary>Escape releases the cursor here (default). Off when a game menu handles Escape instead.</summary>
        public bool HandlesEscape { get; set; } = true;

        /// <summary>
        /// Aiming at doors and E work now: placed and enabled, window focused, no menu block and not in the frame a block
        /// or the map mode was lifted, and the cursor locked — or the map mode on (the map is an information screen: the
        /// keys stay in the game while the mouse serves the map). Unlike HasControlFocus it allows the map mode, so
        /// mouse look stays off there. Walking locks do not belong here: the caller stops door use while a request runs.
        /// </summary>
        public bool CanInteract => Placed && Application.isFocused && InputAllowed && (CursorLocked || mapMode);

        /// <summary>E pressed in this frame (once per press, holding does not repeat) while CanInteract.</summary>
        public bool InteractPressed => CanInteract && Keyboard.current != null && Keyboard.current.eKey.wasPressedThisFrame;

        void Awake()
        {
            controller = GetComponent<CharacterController>();
            if (view == null) Debug.LogError("[1인칭] 시점 카메라(View)가 지정되지 않아 상하 시점을 적용하지 않습니다.", this);
            else viewCamera = view.GetComponent<Camera>();
            RememberViewHome();
        }

        void OnEnable()
        {
            ApplySettings();
            GameSettings.Changed += ApplySettings;
        }

        /// <summary>Only the play camera's field of view; look sensitivity and inversion are read at use.</summary>
        void ApplySettings()
        {
            if (viewCamera != null) viewCamera.fieldOfView = GameSettings.FieldOfView;
        }

        /// <summary>
        /// Blocked by a game menu: no look, walking, E or click-to-lock (gravity goes on). Unblocking applies from the
        /// next frame and locks the cursor again then, if the player is placed and the game focused.
        /// </summary>
        public void SetInputBlocked(bool blocked)
        {
            if (blocked)
            {
                inputBlocked = true;
                return;
            }
            if (!inputBlocked) return;
            inputBlocked = false;
            inputResumeFrame = Time.frameCount + 1;
            relockOnResume = true;
        }

        /// <summary>
        /// Records the view camera's current local position as the original eye position, once, unless one is saved
        /// already (serialized in the scene or recorded earlier). Callable while this object is inactive (Awake has
        /// not run yet): the coordinator calls it before the debug camera — possibly this view — can be moved, so a
        /// debug pose is never taken for the eye position.
        /// </summary>
        public void RememberViewHome()
        {
            if (viewHomeSaved || view == null) return;
            viewHomeLocalPosition = view.localPosition;
            viewHomeSaved = true;
        }

        /// <summary>Unlocks and shows the cursor (shared by every player; no player needed).</summary>
        public static void ReleaseCursor() => SetCursorLock(false);

        /// <summary>Stops walking, looking and falling until the next PlaceAt (new experiment, rooms not shown yet).</summary>
        public void Suspend()
        {
            placed = false;
            movementLocked = false;
            verticalSpeed = 0f;
        }

        /// <summary>No longer controlled (disconnected or replaced): stopped like Suspend and the cursor released.</summary>
        public void Release()
        {
            Suspend();
            SetCursorLock(false);
        }

        /// <summary>Locked: no walking; look, gravity and Escape go on. Input is dropped, never queued.</summary>
        public void SetMovementLocked(bool locked) => movementLocked = locked;

        /// <summary>
        /// Puts the feet on the floor below the center of a shown room (its actual height, found by a ray that skips
        /// this player's own colliders), facing 상(+Z) with a level view and no falling speed, then allows movement.
        /// Returns false with the reason and stays suspended when no floor of that room is below its center, or
        /// when this component or its object is inactive (no placement and no cursor lock then).
        /// </summary>
        public bool PlaceAt(Transform room, out string failure)
        {
            Suspend();
            if (!isActiveAndEnabled)
            {
                failure = "플레이어 컴포넌트나 오브젝트가 비활성입니다";
                return false;
            }
            Physics.SyncTransforms();                  // pooled rooms were just moved and activated
            var floor = FloorBelow(room.position);
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
            if (view != null) view.SetLocalPositionAndRotation(viewHomeSaved ? viewHomeLocalPosition : view.localPosition, Quaternion.identity);
            placed = true;
            if (Application.isFocused && InputAllowed) SetCursorLock(true);
            else if (inputBlocked) relockOnResume = true;                // a menu is open: lock when it closes
            failure = null;
            return true;
        }

        /// <summary>
        /// Puts the feet on the floor of `room` below `point` keeping the facing and view as they are, and stops any
        /// fall (e.g. back inside the confirmed room after an entry that was not confirmed). Only for a placed player;
        /// false with the reason (nothing moved) when that floor is not `room`'s.
        /// </summary>
        public bool MoveOnto(Transform room, Vector3 point, out string failure)
        {
            if (!Placed)
            {
                failure = "배치되지 않은 플레이어입니다";
                return false;
            }
            Physics.SyncTransforms();
            var floor = FloorBelow(point);
            if (floor == null || !floor.Value.collider.transform.IsChildOf(room))
            {
                failure = $"방 '{room.name}' 안쪽 지점 아래에서 이 방의 바닥을 찾지 못했습니다";
                return false;
            }
            controller.enabled = false;
            transform.position = floor.Value.point + Vector3.up * controller.skinWidth;
            controller.enabled = true;
            verticalSpeed = 0f;
            failure = null;
            return true;
        }

        /// <summary>
        /// Whether the first floor below the player's current position (same ray rules as PlaceAt) belongs to `room`.
        /// Position and view are left as they are.
        /// </summary>
        public bool IsOnFloorOf(Transform room, out string failure)
        {
            Physics.SyncTransforms();                  // rooms were just shown or moved
            var floor = FloorBelow(transform.position);
            if (floor == null) failure = $"플레이어 아래에서 바닥 Collider를 찾지 못했습니다(방 '{room.name}')";
            else if (!floor.Value.collider.transform.IsChildOf(room)) failure = $"플레이어 아래 첫 Collider '{floor.Value.collider.name}'가 방 '{room.name}'의 것이 아닙니다";
            else failure = null;
            return failure == null;
        }

        /// <summary>
        /// The door whose collider is the first valid obstacle on the screen-center ray within `maxDistance`.
        /// This player's own colliders, triggers and the Ignore Raycast layer (door passage blocks) are skipped;
        /// any other first hit (wall, sign, floor) hides the doors behind it.
        /// </summary>
        public bool TryGetAimedDoor(float maxDistance, out DoorView door)
        {
            door = null;
            var eye = view != null ? view : transform;
            RaycastHit? first = null;
            foreach (var h in Physics.RaycastAll(eye.position, eye.forward, maxDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                if (!h.collider.transform.IsChildOf(transform) && (first == null || h.distance < first.Value.distance)) first = h;
            if (first == null) return false;
            door = first.Value.collider.GetComponentInParent<DoorView>();
            return door != null;
        }

        /// <summary>Nearest non-trigger collider below `point` (from body height above it), never our own.</summary>
        RaycastHit? FloorBelow(Vector3 point)
        {
            var origin = point + Vector3.up * controller.height;
            RaycastHit? floor = null;
            foreach (var h in Physics.RaycastAll(origin, Vector3.down, controller.height * 2f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                if (!h.collider.transform.IsChildOf(transform) && (floor == null || h.distance < floor.Value.distance)) floor = h;   // never our own collider
            return floor;
        }

        void Update()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            var allowed = InputAllowed;
            if (allowed && relockOnResume && !mapMode)                        // no relock under a menu block or an open map
            {
                relockOnResume = false;
                if (placed && Application.isFocused) SetCursorLock(true);
            }
            if (HandlesEscape && kb != null && kb.escapeKey.wasPressedThisFrame) SetCursorLock(false);
            else if (allowed && !mapMode && placed && !CursorLocked && Application.isFocused && mouse != null && mouse.leftButton.wasPressedThisFrame) SetCursorLock(true);
            if (!placed) return;

            var input = Vector2.zero;
            if (allowed && CursorLocked && Application.isFocused && !mapMode && mouse != null) Look(mouse.delta.ReadValue());
            if (WalkInputAllowed && kb != null)
                input = new Vector2((kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f),
                                    (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f));
            input = Vector2.ClampMagnitude(input, 1f);  // diagonal no faster than straight
            var move = (transform.right * input.x + transform.forward * input.y) * moveSpeed;

            if (controller.isGrounded && verticalSpeed < 0f) verticalSpeed = GroundedSpeed;
            verticalSpeed += Physics.gravity.y * Time.deltaTime;
            controller.Move((move + Vector3.up * verticalSpeed) * Time.deltaTime);
        }

        /// <summary>Mouse delta is already a per-frame amount: no deltaTime here. The setting multiplier is applied here only.</summary>
        void Look(Vector2 delta)
        {
            var sensitivity = lookSensitivity * GameSettings.MouseSensitivity;
            var vertical = GameSettings.InvertMouseY ? -1f : 1f;
            transform.Rotate(0f, delta.x * sensitivity, 0f);
            pitch = Mathf.Clamp(pitch - delta.y * sensitivity * vertical, -pitchLimit, pitchLimit);
            if (view != null) view.localRotation = Quaternion.Euler(pitch, 0f, 0f);
        }

        void OnApplicationFocus(bool focus)
        {
            if (!focus) SetCursorLock(false);
        }

        // Also runs when the component or object is destroyed. Dropping the placement means enabling it again
        // never resumes walking or falling from an earlier placement; only the next PlaceAt does.
        void OnDisable()
        {
            GameSettings.Changed -= ApplySettings;
            Suspend();
            SetCursorLock(false);
        }

        static void SetCursorLock(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }
    }
}
