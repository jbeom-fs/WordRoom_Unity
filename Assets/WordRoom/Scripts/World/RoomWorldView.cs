using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using WordRoom.Generation;

namespace WordRoom.World
{
    /// <summary>
    /// 3D view of the logical map around the current room: the current room and its existing 상·하·좌·우 rooms
    /// (at most five), plus one shared door per connection of those rooms. Reads only RoomViewSnapshot; never
    /// calls the generator, never creates words or rooms, and returning an instance never removes map data.
    /// Each update computes the wanted rooms and doors, returns what is no longer wanted first, then rents only
    /// what is missing; instances still wanted stay as they are. Door states live in DoorStateStore per
    /// connection, apart from the door instances. Each update also writes the snapshot's neighbor words to the wall
    /// signs of every shown room. Main thread only.
    /// Doors and passing are separate: a door of the current room toward a shown room can be opened (stored Open,
    /// then slid up; the caller prepares the neighbors of the room behind it at the same time); a doorway lets the
    /// player through only while its door is fully open and it joins the current room (the center of the last shown
    /// snapshot) to another shown room that snapshot reports prepared — every other doorway keeps its passage block
    /// on, also for open doors toward rooms outside the shown set or still being prepared. TryDetectEntry tells when
    /// the player has passed such a doorway; moving the logical position is up to the caller (generator request).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RoomWorldView : MonoBehaviour
    {
        const int MaxShownRooms = 5;
        const float EntryDepth = 0.15f;      // player center this far past the room boundary = entered the next room

        [Header("프리팹 (사용자 제작 — 코드가 형태·크기·재질을 바꾸지 않음)")]
        [Tooltip("방 프리팹 (예: Assets/Prefabs/TestRoomPrefab)")]
        [SerializeField] GameObject roomPrefab;
        [Tooltip("문 프리팹 (예: Assets/Prefabs/TestDoor — 배치용 부모 + 자식 문 본체)")]
        [SerializeField] GameObject doorPrefab;

        [Header("출입구 기준점")]
        [Tooltip("방 프리팹 안 출입구 기준점의 이름. Overrides가 비어 있으면 이 이름의 오브젝트 네 개를 방 기준 위치로 분류(+X 우, -X 좌, +Z 상, -Z 하)해 고정합니다.")]
        [SerializeField] string doorPointName = "DoorPoint";
        [Tooltip("직접 지정할 때만 사용: Project 창에서 방 프리팹을 펼쳐 각 방향의 DoorPoint를 넣습니다. 하나라도 넣으면 네 방향 모두 필요합니다.")]
        [SerializeField] DoorPointOverrides doorPointOverrides = new DoorPointOverrides();

        [Header("문")]
        [Tooltip("문 프리팹 안에서 위로 미끄러질 본체 자식의 이름")]
        [SerializeField] string doorBodyName = "Door";
        [Tooltip("닫힌 문 위치 보정(출입구 기준 좌표). x: 벽 바깥쪽(+)/방 안쪽(-), y: 위(+), z: 출입구 가운데 방향(+). 0이면 DoorPoint 모서리에 문 프리팹 피벗을 둡니다.")]
        [SerializeField] Vector3 doorOffset = Vector3.zero;
        [Tooltip("열림 상태에서 문 본체가 월드 위쪽으로 올라가는 거리. 0 이하면 문 본체 높이(TestDoor 2.2m). 새 실험 시작 시 고정.")]
        [SerializeField] float doorOpenLift;
        [Tooltip("문이 다 열릴 때까지 걸리는 시간(초). 다 열린 뒤에만 통과할 수 있습니다. 새 실험 시작 시 고정.")]
        [SerializeField] float doorOpenSeconds = 0.5f;

        [Header("배치 (새 실험 시작 시 고정)")]
        [Tooltip("좌표 (0, 0) 방의 월드 위치")]
        [SerializeField] Vector3 worldOrigin = Vector3.zero;
        [Tooltip("이웃 좌표 사이 거리. 0 이하면 방 프리팹의 메시 크기(가로·세로 중 큰 값)를 씁니다. 아카이브 방은 바닥 외곽 6.2를 지정합니다(장식·수납부가 자동 측정에 섞이지 않게).")]
        [SerializeField] float roomSpacing;
        [Tooltip("방 인스턴스의 부모(장면 오브젝트). 비우면 이 오브젝트 아래 자동 루트 'Rooms'를 만들어 재사용합니다. 바꾸면 다음 새 실험부터 적용되고, 그때 이전 루트의 풀 인스턴스만 정리됩니다. 스케일 1 권장.")]
        [SerializeField] Transform roomRoot;
        [Tooltip("문 인스턴스의 부모(장면 오브젝트, 방과 별도). 비우면 자동 루트 'Doors'를 만들어 재사용합니다. 바꾸면 다음 새 실험부터 적용됩니다. 스케일 1 권장.")]
        [SerializeField] Transform doorRoot;

        [Header("확인용 카메라 (선택 — 없어도 생성·표시는 동작)")]
        [SerializeField] bool moveCamera = true;
        [SerializeField] Camera debugCamera;
        [Tooltip("현재 방 중심 기준 카메라 위치. 기본은 위에서 내려다봄(화면 위쪽 = 상)")]
        [SerializeField] Vector3 cameraOffset = new Vector3(0f, 16f, 0f);
        [Tooltip("카메라 회전(오일러). 기본 (90, 0, 0) = 아래를 봄")]
        [SerializeField] Vector3 cameraEuler = new Vector3(90f, 0f, 0f);

        [Header("로그")]
        [Tooltip("갱신마다 현재 좌표, 활성 방·문, 대여·반환·새 생성 수를 출력")]
        [SerializeField] bool verbosePoolLog;

        readonly Dictionary<GridPos, RoomView> rooms = new Dictionary<GridPos, RoomView>();
        readonly Dictionary<RoomConnection, DoorView> doors = new Dictionary<RoomConnection, DoorView>();
        readonly DoorStateStore doorStates = new DoorStateStore();

        // Fixed at BeginSession; rents, binds and pool create callbacks use only these, never the Inspector fields:
        // layout = prefabs, door body name, DoorPoints and door poses; each pool's Parent = the root resolved for
        // this experiment; spacing/origin/openLift/openSeconds/doorwayHalfWidth = placement values.
        RoomPrefabLayout layout;
        string layoutKey;
        ViewPool<RoomView> roomPool;
        ViewPool<DoorView> doorPool;
        float spacing;
        Vector3 origin;
        float openLift;
        float openSeconds;
        float doorwayHalfWidth;
        bool ready;
        GridPos? center;                      // current room of the last shown snapshot
        RoomViewSnapshot shown;               // last shown snapshot: which shown rooms are prepared (map data, not pooled)

        // Roots this component created itself (never written to the Inspector fields), reused across experiments.
        Transform autoRoomRoot;
        Transform autoDoorRoot;

        public DoorStateStore DoorStates => doorStates;
        public IReadOnlyCollection<GridPos> ShownRooms => rooms.Keys;
        public IReadOnlyCollection<RoomConnection> ShownDoors => doors.Keys;

        /// <summary>
        /// New experiment: 1) return what is shown and clear the previous door states, 2) fix the prefab settings
        /// (layout rebuilt only when they changed), 3) resolve the two roots, 4) rebuild a pool only when its layout
        /// or root changed (otherwise its instances are reused), 5) fix the placement values. Inspector edits made
        /// during an experiment take effect here, at the next one. On a setting error the 3D view stays off for this
        /// experiment (word generation and Console output continue); fixing it and starting a new one recovers.
        /// </summary>
        public void BeginSession()
        {
            ReleaseAll();
            doorStates.Clear();
            ready = false;
            center = null;
            shown = null;                                 // preparation info goes with the door states
            HandOverIfUserRoot(ref autoRoomRoot);         // before any cleanup or root resolving, also on a setting error
            HandOverIfUserRoot(ref autoDoorRoot);

            var owned = new HashSet<Transform>();         // instances of retired pools, to tell them from user objects
            var rebuilt = PrepareLayout();
            if (layout == null)
            {
                RetirePools(owned);
                ReleaseAutoRootIfUnused(ref autoRoomRoot, owned);
                ReleaseAutoRootIfUnused(ref autoDoorRoot, owned);
                return;
            }
            var roomOk = ResolveRoot(roomRoot, ref autoRoomRoot, "Rooms", "Room Root", out var roomParent);
            var doorOk = ResolveRoot(doorRoot, ref autoDoorRoot, "Doors", "Door Root", out var doorParent);
            if (!roomOk || !doorOk)
            {
                RetirePools(owned);
                ReleaseAutoRootIfUnused(ref autoRoomRoot, owned);
                ReleaseAutoRootIfUnused(ref autoDoorRoot, owned);
                return;
            }

            var l = layout;                                // captured: the create callbacks never see later changes
            if (rebuilt || roomPool == null || roomPool.Parent != roomParent)
            {
                RetirePool(ref roomPool, owned);
                roomPool = new ViewPool<RoomView>(roomParent, parent => CreateRoom(l, parent));
            }
            if (rebuilt || doorPool == null || doorPool.Parent != doorParent)
            {
                RetirePool(ref doorPool, owned);
                doorPool = new ViewPool<DoorView>(doorParent, parent => CreateDoor(l, parent));
            }
            ReleaseAutoRootIfUnused(ref autoRoomRoot, owned);
            ReleaseAutoRootIfUnused(ref autoDoorRoot, owned);

            spacing = roomSpacing > 0 ? roomSpacing : layout.MeasuredSpacing;
            origin = worldOrigin;
            openLift = doorOpenLift > 0 ? doorOpenLift : layout.DoorBody.size.y;
            openSeconds = Mathf.Max(0f, doorOpenSeconds);
            doorwayHalfWidth = Mathf.Max(layout.DoorBody.size.x, layout.DoorBody.size.z) * 0.5f;   // closed body width = doorway
            ready = true;
            LogPrepared(rebuilt);
            var missingSigns = new List<string>();
            RoomView.FindSigns(layout.RoomPrefab.transform, missingSigns);
            if (missingSigns.Count > 0)
                Debug.LogWarning($"[3D 방] 벽 표지판 텍스트가 없어 해당 방향은 단어를 표시하지 않습니다(방·문 표시와 단어 생성은 계속): {string.Join(", ", missingSigns)}");
        }

        /// <summary>
        /// Shows the snapshot's rooms (current + existing 상·하·좌·우) and the doors of their connections. Needs BeginSession.
        /// `allowDebugCamera` false (first-person player in use) leaves the debug camera where it is.
        /// Returns whether everything the snapshot needs is really usable afterwards: each of its rooms and the door
        /// of every doorway of those rooms (also toward 미생성 or hidden rooms) is a live, bound, active instance, and
        /// every door's passage block exists and is in the state passing allows. A failed rent is logged and the
        /// update goes on as before, but the result is then false with the missing parts in `failure`. Rooms that
        /// were never generated are not part of the snapshot and never count as missing.
        /// </summary>
        public bool Show(RoomViewSnapshot s, bool allowDebugCamera, out string failure)
        {
            if (s == null)
            {
                failure = "표시할 스냅샷이 없습니다";
                return false;
            }
            if (!ready)
            {
                failure = "3D 표시 설정 오류로 이번 실험은 3D 표시를 하지 않습니다(위 [3D 방] 오류 확인)";
                return false;
            }
            var wantRooms = new Dictionary<GridPos, int>();
            foreach (var kv in s.Rooms)
                if (wantRooms.Count < MaxShownRooms) wantRooms[kv.Key] = kv.Value;
            var wantDoors = new SortedSet<RoomConnection>();
            foreach (var p in wantRooms.Keys)
                foreach (var d in Directions.GenerationOrder)
                    wantDoors.Add(RoomConnection.Of(p, d));        // also toward missing or hidden rooms: closed boundary

            int roomsOut = 0, doorsOut = 0, roomsIn = 0, doorsIn = 0, roomsNew = 0, doorsNew = 0;
            foreach (var p in rooms.Keys.Where(p => !wantRooms.ContainsKey(p)).ToList())
            {
                ReleaseRoom(p);
                roomsOut++;
            }
            foreach (var id in doors.Keys.Where(id => !wantDoors.Contains(id)).ToList())
            {
                ReleaseDoor(id);
                doorsOut++;
            }
            foreach (var kv in s.Rooms)
            {
                if (!wantRooms.ContainsKey(kv.Key) || rooms.ContainsKey(kv.Key)) continue;
                if (!RentRoom(kv.Key, kv.Value, out var created)) continue;
                roomsIn++;
                if (created) roomsNew++;
            }
            foreach (var id in wantDoors)
            {
                if (doors.ContainsKey(id)) continue;
                if (!RentDoor(id, out var created)) continue;
                doorsIn++;
                if (created) doorsNew++;
            }
            foreach (var kv in rooms)                              // new and kept rooms: a new neighbor replaces 미생성
                if (kv.Value != null && s.Signs.TryGetValue(kv.Key, out var words)) kv.Value.SetSigns(words);
            center = s.Center;
            shown = s;
            UpdatePassages();
            var complete = CheckShown(wantRooms.Keys, wantDoors, out failure);
            if (!complete) Debug.LogError($"[3D 방] 표시 준비 실패 — {failure}");

            if (allowDebugCamera && moveCamera && debugCamera != null)
                debugCamera.transform.SetPositionAndRotation(RoomOrigin(s.Center) + cameraOffset, Quaternion.Euler(cameraEuler));

            if (verbosePoolLog) LogShown(s, roomsIn, roomsNew, roomsOut, doorsIn, doorsNew, doorsOut);
            return complete;
        }

        /// <summary>Wanted rooms and doors that are not usable instances (by value, not by dictionary key).</summary>
        bool CheckShown(IEnumerable<GridPos> wantRooms, IEnumerable<RoomConnection> wantDoors, out string failure)
        {
            var missing = new List<string>();
            foreach (var p in wantRooms)
                if (!rooms.TryGetValue(p, out var r) || r == null || !r.IsUsable(p)) missing.Add($"방 {p}");
            foreach (var id in wantDoors)
                if (!doors.TryGetValue(id, out var door) || door == null || !door.IsReady(id, center != null && IsPassable(id, center.Value)))
                    missing.Add($"문·출입 차단 {id}");
            failure = missing.Count == 0 ? null : $"사용할 수 없는 방·문이 있습니다: {string.Join(", ", missing)}";
            return failure == null;
        }

        void LogShown(RoomViewSnapshot s, int roomsIn, int roomsNew, int roomsOut, int doorsIn, int doorsNew, int doorsOut)
        {
            var sb = new StringBuilder();
            sb.Append($"[3D 방] 현재 {s.Center} ({StatusText.Korean(s.Status)})");
            sb.Append($"\n활성 방 {rooms.Count}개: {string.Join(" ", rooms.Keys.OrderBy(p => p))}");
            sb.Append($"\n활성 문 {doors.Count}개: {string.Join(" ", doors.Keys.OrderBy(id => id))}");
            sb.Append($"\n이번 갱신: 방 대여 {roomsIn}(새 생성 {roomsNew})·반환 {roomsOut}, 문 대여 {doorsIn}(새 생성 {doorsNew})·반환 {doorsOut}");
            sb.Append($"\n풀 누적: 방 생성 {roomPool.Created}·대여 {roomPool.Rents}·반환 {roomPool.Returns}·대기 {roomPool.FreeCount}, " +
                      $"문 생성 {doorPool.Created}·대여 {doorPool.Rents}·반환 {doorPool.Returns}·대기 {doorPool.FreeCount} · 저장된 문 상태 {doorStates.Count}");
            Debug.Log(sb.ToString());
        }

        /// <summary>The shown room instance at p (e.g. to put the player on its floor); false when it is not shown or not usable.</summary>
        public bool TryGetShownRoom(GridPos p, out Transform room)
        {
            room = IsShownUsable(p) ? rooms[p].transform : null;
            return room != null;
        }

        bool IsShownUsable(GridPos p) => rooms.TryGetValue(p, out var v) && v != null && v.IsUsable(p);

        /// <summary>
        /// Stores the state of a connection and applies it immediately to its shown door, if any (no animation).
        /// The player's door opening uses OpenDoor instead.
        /// </summary>
        public void SetDoorState(RoomConnection id, DoorState state)
        {
            doorStates.Set(id, state);
            if (doors.TryGetValue(id, out var door) && door != null) door.Apply(state, openLift);
            UpdatePassages();
        }

        /// <summary>
        /// Whether `door` (e.g. the one the player aims at) may be opened: it is the shown door of a closed connection
        /// between the current room and a shown room. Doors toward 미생성 or hidden rooms, and doors of neighbor rooms
        /// that do not touch the current room, cannot be opened.
        /// </summary>
        public bool CanOpen(DoorView door, out RoomConnection id)
        {
            id = default;
            if (!ready || center == null || door == null || !door.Active) return false;   // a disabled DoorView never finishes opening
            id = door.Connection;
            return doors.TryGetValue(id, out var instance) && instance == door && door.State == DoorState.Closed &&
                   JoinsCurrentToShown(id, center.Value);
        }

        /// <summary>
        /// Opens a door that CanOpen allows: the shown door starts sliding up and the connection is stored Open (so a
        /// door returned to the pool while sliding comes back fully open). `side` is the direction from the current
        /// room to the room behind the door, for the caller's preparation request. False when refused — then nothing
        /// is stored and nothing slides. Opening itself never creates rooms, moves the logical position or counts a visit.
        /// </summary>
        public bool OpenDoor(RoomConnection id, out Direction side)
        {
            side = default;
            if (!doors.TryGetValue(id, out var door) || !CanOpen(door, out _)) return false;
            if (!door.BeginOpening(openLift, openSeconds)) return false;
            doorStates.Set(id, DoorState.Open);
            side = Directions.Between(center.Value, id.A == center.Value ? id.B : id.A).Value;
            UpdatePassages();
            return true;
        }

        /// <summary>
        /// Whether the player's center, moving from `from` to `to` in this frame, went EntryDepth (0.15m) past the
        /// boundary between the current room and a neighbor through a passable doorway (door fully open, both rooms
        /// shown, crossing point within the doorway width). Uses the spacing, origin and door placement fixed for
        /// this experiment; works the same on every side and for negative coordinates. Only a frame that crosses the
        /// depth counts, so standing on the threshold or swaying around it never reports twice, and after the
        /// current room changes the way back needs EntryDepth past the boundary on the other side again.
        /// </summary>
        public bool TryDetectEntry(Vector3 from, Vector3 to, out Direction side)
        {
            side = default;
            if (!ready || center == null) return false;
            var c = center.Value;
            var half = spacing * 0.5f;
            var co = RoomOrigin(c);
            foreach (var d in Directions.GenerationOrder)
            {
                var n = RoomPrefabLayout.Normal(d);
                var depthFrom = Vector3.Dot(from - co, n) - half;
                var depthTo = Vector3.Dot(to - co, n) - half;
                if (depthFrom >= EntryDepth || depthTo < EntryDepth) continue;   // did not cross the entry depth outward
                var id = RoomConnection.Of(c, d);
                if (!IsPassable(id, c)) continue;
                var crossing = Vector3.Lerp(from, to, (EntryDepth - depthFrom) / (depthTo - depthFrom));
                var t = RoomPrefabLayout.Tangent(d);
                var doorway = RoomOrigin(id.Anchor) + layout.Pose(id.AnchorSide).BodyCenter;
                if (Mathf.Abs(Vector3.Dot(crossing - doorway, t)) > doorwayHalfWidth) continue;
                side = d;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Door active and fully open, joining the current room `c` to another shown room whose neighbors are prepared
        /// in the last shown snapshot. Both conditions are needed: a finished slide alone or a finished preparation
        /// alone never opens the passage (UpdatePassages runs every frame and follows this).
        /// </summary>
        bool IsPassable(RoomConnection id, GridPos c) =>
            doors.TryGetValue(id, out var door) && door != null && door.Active && door.FullyOpen &&
            JoinsCurrentToShown(id, c) && shown != null && shown.IsPrepared(id.A == c ? id.B : id.A);

        bool JoinsCurrentToShown(RoomConnection id, GridPos c)
        {
            if (id.A != c && id.B != c) return false;
            var other = id.A == c ? id.B : id.A;
            return IsShownUsable(c) && IsShownUsable(other);
        }

        /// <summary>Passage blocks: off only where passing is allowed now (see IsPassable); on everywhere else.</summary>
        void UpdatePassages()
        {
            foreach (var kv in doors)
                if (kv.Value != null) kv.Value.SetPassable(center != null && IsPassable(kv.Key, center.Value));
        }

        void Update()
        {
            if (ready && center != null) UpdatePassages();   // a door that finished sliding opens its passage
        }

        void OnDestroy()
        {
            ready = false;
            center = null;
            shown = null;
            rooms.Clear();
            doors.Clear();
            doorStates.Clear();
            var owned = new HashSet<Transform>();
            RetirePools(owned);
            ReleaseAutoRootIfUnused(ref autoRoomRoot, owned);
            ReleaseAutoRootIfUnused(ref autoDoorRoot, owned);
        }

        // ---- setup (BeginSession only) ----------------------------------------------------------------------
        /// <summary>
        /// Reads the prefab settings from the Inspector and rebuilds the layout when they differ from the fixed ones
        /// (or the last attempt failed). Returns true when rebuilt; layout is null after a logged failure.
        /// </summary>
        bool PrepareLayout()
        {
            var o = doorPointOverrides ?? (doorPointOverrides = new DoorPointOverrides());
            var key = string.Join("|", Id(roomPrefab), Id(doorPrefab), doorPointName, doorBodyName, doorOffset.ToString("R"),
                Id(o.up), Id(o.down), Id(o.left), Id(o.right));
            if (layout != null && key == layoutKey) return false;
            layout = null;
            layoutKey = null;
            try
            {
                layout = RoomPrefabLayout.Build(roomPrefab, doorPrefab, o, doorPointName, doorBodyName, doorOffset);
                layoutKey = key;
                return true;
            }
            catch (InvalidOperationException e)
            {
                Debug.LogError($"[3D 방] 설정 문제로 이번 실험은 3D 표시를 하지 않습니다(단어 생성·Console 디버그는 계속). 고친 뒤 새 실험을 시작하세요: {e.Message}");
                return false;
            }
        }

        static string Id(UnityEngine.Object o) => o == null ? "-" : o.GetInstanceID().ToString();

        /// <summary>Assigned once but destroyed since (an unassigned field has instance id 0, also in the Editor).</summary>
        static bool IsDestroyed(UnityEngine.Object o) => !ReferenceEquals(o, null) && o == null && o.GetInstanceID() != 0;

        /// <summary>
        /// The Inspector root when set (must be a live scene object), otherwise this component's own automatic root,
        /// created once and reused while it exists. Logs and returns false for an unusable root (no fallback).
        /// </summary>
        bool ResolveRoot(Transform configured, ref Transform auto, string autoName, string label, out Transform root)
        {
            root = null;
            if (IsDestroyed(configured))
            {
                Debug.LogError($"[3D 방] {label}로 지정한 오브젝트가 파괴되어 이번 실험은 3D 표시를 하지 않습니다. 비우거나 다시 지정한 뒤 새 실험을 시작하세요.");
                return false;
            }
            if (configured != null)
            {
                if (!configured.gameObject.scene.IsValid())
                {
                    Debug.LogError($"[3D 방] {label} '{configured.name}'는 장면 오브젝트가 아니라(프리팹 자산 등) 루트로 쓸 수 없습니다. 장면의 오브젝트를 지정하세요.");
                    return false;
                }
                if (configured == auto) auto = null;      // the user now names this root: no longer ours to delete
                root = configured;
                return true;
            }
            if (auto == null)
            {
                var go = new GameObject(autoName);
                go.transform.SetParent(transform, false);
                auto = go.transform;
            }
            root = auto;
            return true;
        }

        static RoomView CreateRoom(RoomPrefabLayout l, Transform parent)
        {
            var go = Instantiate(l.RoomPrefab, parent, false);
            go.SetActive(false);
            var v = go.GetComponent<RoomView>();
            if (v == null) v = go.AddComponent<RoomView>();
            v.Init();                                      // sign texts found once per instance
            return v;
        }

        static DoorView CreateDoor(RoomPrefabLayout l, Transform parent)
        {
            var go = Instantiate(l.DoorPrefab, parent, false);
            go.SetActive(false);
            var v = go.GetComponent<DoorView>();
            if (v == null) v = go.AddComponent<DoorView>();
            try
            {
                v.Init(l.DoorBodyName);                   // the name the layout measured the body with
            }
            catch
            {
                Destroy(go);
                throw;
            }
            return v;
        }

        /// <summary>Destroys only the pool's own instances (recorded in `owned`); the root and other children stay.</summary>
        static void RetirePool<T>(ref ViewPool<T> pool, HashSet<Transform> owned) where T : Component
        {
            if (pool == null) return;
            pool.CollectInstances(owned);
            pool.DestroyAll();
            pool = null;
        }

        void RetirePools(HashSet<Transform> owned)
        {
            RetirePool(ref roomPool, owned);
            RetirePool(ref doorPool, owned);
        }

        /// <summary>
        /// An automatic root the user now names as Room Root or Door Root (either one) is theirs: it is no longer
        /// managed and never deleted by this component. Neither the object nor the Inspector fields change.
        /// </summary>
        bool HandOverIfUserRoot(ref Transform auto)
        {
            if (auto == null || (auto != roomRoot && auto != doorRoot)) return false;
            auto = null;
            return true;
        }

        /// <summary>
        /// Deletes an automatic root no pool uses any more, unless it holds objects that are not pool instances:
        /// then it is left in place (with a warning) and simply no longer managed. A root the user names is kept.
        /// </summary>
        void ReleaseAutoRootIfUnused(ref Transform auto, HashSet<Transform> owned)
        {
            if (ReferenceEquals(auto, null)) return;
            if (auto == null)
            {
                auto = null;                               // destroyed elsewhere
                return;
            }
            if (HandOverIfUserRoot(ref auto)) return;
            if ((roomPool != null && roomPool.Parent == auto) || (doorPool != null && doorPool.Parent == auto)) return;
            var foreign = new List<string>();
            foreach (Transform child in auto)
                if (!owned.Contains(child)) foreign.Add(child.name);
            if (foreign.Count == 0) Destroy(auto.gameObject);
            else Debug.LogWarning($"[3D 방] 자동 루트 '{auto.name}'에 풀 인스턴스가 아닌 오브젝트({string.Join(", ", foreign)})가 있어 삭제하지 않고 남깁니다.");
            auto = null;
        }

        void LogPrepared(bool rebuilt)
        {
            var sb = new StringBuilder("[3D 방] 표시 준비 — 현재 방 + 상·하·좌·우 중 생성된 방(최대 5개), 연결마다 문 1개" +
                                       (rebuilt ? " (프리팹 배치 정보 새로 계산)" : " (프리팹 배치 정보 재사용)") + "\n");
            sb.Append(layout.Describe(spacing));
            sb.Append($"\n  루트: 방 '{roomPool.Parent.name}'{(roomPool.Parent == autoRoomRoot ? "(자동)" : "")}, 문 '{doorPool.Parent.name}'{(doorPool.Parent == autoDoorRoot ? "(자동)" : "")}" +
                      $" · 풀 대기 인스턴스 방 {roomPool.FreeCount}·문 {doorPool.FreeCount}");
            if (roomSpacing > 0 && Mathf.Abs(roomSpacing - layout.MeasuredSpacing) > 1e-3f)
                sb.Append($"\n  주의: 지정 간격 {roomSpacing:0.###}이 방 프리팹 크기 {layout.MeasuredSpacing:0.###}와 달라 방 사이가 벌어지거나 겹칩니다.");
            foreach (var root in new[] { roomPool.Parent, doorPool.Parent })
                if ((root.lossyScale - Vector3.one).sqrMagnitude > 1e-6f)
                    sb.Append($"\n  주의: '{root.name}'의 스케일이 {RoomPrefabLayout.Fmt(root.lossyScale)}라 인스턴스 크기가 프리팹과 달라집니다.");
            if (debugCamera == null && moveCamera) sb.Append("\n  확인용 카메라가 지정되지 않아 카메라는 움직이지 않습니다.");
            Debug.Log(sb.ToString());
        }

        // ---- rent / return ----------------------------------------------------------------------------------
        Vector3 RoomOrigin(GridPos p) => origin + new Vector3(p.X * spacing, 0f, p.Y * spacing);

        bool RentRoom(GridPos p, int word, out bool created)
        {
            created = false;
            RoomView v = null;
            try
            {
                v = roomPool.Rent(out created);
                v.Bind(p, word, RoomOrigin(p), layout.RoomRotation, layout.RoomScale);
                v.gameObject.SetActive(true);
                rooms[p] = v;
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[3D 방] 방 {p} 배치 실패: {e.Message}");
                if (v != null)
                {
                    v.Unbind();
                    roomPool.Return(v);
                }
                return false;
            }
        }

        /// <summary>Door of a connection, placed from the anchor's (left/lower) 우/상 doorway. The anchor room need not be shown or exist.</summary>
        bool RentDoor(RoomConnection id, out bool created)
        {
            created = false;
            DoorView v = null;
            try
            {
                var pose = layout.Pose(id.AnchorSide);
                v = doorPool.Rent(out created);
                v.Bind(id, RoomOrigin(id.Anchor) + pose.Position, pose.Rotation, layout.DoorScale, doorStates.Get(id), openLift);
                v.gameObject.SetActive(true);
                doors[id] = v;
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[3D 방] 문 {id} 배치 실패: {e.Message}");
                if (v != null)
                {
                    v.Unbind();
                    doorPool.Return(v);
                }
                return false;
            }
        }

        void ReleaseRoom(GridPos p)
        {
            var v = rooms[p];
            rooms.Remove(p);
            if (v != null) v.Unbind();
            if (roomPool == null || !roomPool.Return(v)) Debug.LogError($"[3D 방] 방 {p} 반환 실패: 대여 중인 인스턴스가 아닙니다(중복 반환).");
        }

        void ReleaseDoor(RoomConnection id)
        {
            var v = doors[id];
            doors.Remove(id);
            if (v != null) v.Unbind();       // closed position and colliders restored; the stored state stays in DoorStateStore
            if (doorPool == null || !doorPool.Return(v)) Debug.LogError($"[3D 방] 문 {id} 반환 실패: 대여 중인 인스턴스가 아닙니다(중복 반환).");
        }

        /// <summary>Returns every shown instance. Only the view is cleared; RoomMap is never touched here.</summary>
        void ReleaseAll()
        {
            foreach (var p in rooms.Keys.ToList()) ReleaseRoom(p);
            foreach (var id in doors.Keys.ToList()) ReleaseDoor(id);
        }
    }
}
