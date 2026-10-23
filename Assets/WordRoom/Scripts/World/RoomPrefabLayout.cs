using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using WordRoom.Generation;

namespace WordRoom.World
{
    /// <summary>Optional explicit DoorPoint per side (children of the room prefab). All four or none.</summary>
    [Serializable]
    public sealed class DoorPointOverrides
    {
        [Tooltip("상(+Z) 출입구의 DoorPoint")] public Transform up;
        [Tooltip("하(-Z) 출입구의 DoorPoint")] public Transform down;
        [Tooltip("좌(-X) 출입구의 DoorPoint")] public Transform left;
        [Tooltip("우(+X) 출입구의 DoorPoint")] public Transform right;

        public Transform Get(Direction d)
        {
            switch (d)
            {
                case Direction.Up: return up;
                case Direction.Down: return down;
                case Direction.Left: return left;
                default: return right;
            }
        }

        public bool Any => up != null || down != null || left != null || right != null;
    }

    /// <summary>Closed door placement relative to the anchor room's pivot (room rotation/scale already applied).</summary>
    public readonly struct DoorPose
    {
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public readonly float Yaw;
        public readonly Vector3 BodyCenter;

        public DoorPose(Vector3 position, Quaternion rotation, float yaw, Vector3 bodyCenter)
        {
            Position = position;
            Rotation = rotation;
            Yaw = yaw;
            BodyCenter = bodyCenter;
        }
    }

    /// <summary>
    /// Geometry read once from the two prefab assets (never instantiated, never modified): the room footprint,
    /// the four DoorPoints in room space (through their wall parents), and the door body box in door space.
    /// From these it fixes, per side, the yaw of the TestDoor parent and the position that makes the closed body
    /// fill the doorway. Works without any room instance, so a door can be placed from an inactive or missing
    /// anchor room. Room space = prefab root rotation and scale applied, root position ignored (instances are
    /// placed explicitly).
    /// </summary>
    public sealed class RoomPrefabLayout
    {
        const float Eps = 1e-3f;

        readonly Vector3[] doorPoints = new Vector3[4];
        readonly DoorPose[] poses = new DoorPose[4];

        public GameObject RoomPrefab { get; private set; }
        public GameObject DoorPrefab { get; private set; }

        /// <summary>Name of the sliding body child, the same one the door body box was measured from.</summary>
        public string DoorBodyName { get; private set; }
        public Quaternion RoomRotation { get; private set; }
        public Vector3 RoomScale { get; private set; }
        public Quaternion DoorRotation { get; private set; }
        public Vector3 DoorScale { get; private set; }

        /// <summary>Bounds of all room meshes in room space.</summary>
        public Bounds RoomBounds { get; private set; }

        /// <summary>Room size on the floor plane (larger of X and Z); the automatic room spacing.</summary>
        public float MeasuredSpacing => Mathf.Max(RoomBounds.size.x, RoomBounds.size.z);

        /// <summary>Closed door body box in door space (door root rotation/scale applied).</summary>
        public Bounds DoorBody { get; private set; }

        public string DoorPointSource { get; private set; }
        public List<string> Warnings { get; } = new List<string>();

        public Vector3 DoorPoint(Direction d) => doorPoints[(int)d];
        public DoorPose Pose(Direction d) => poses[(int)d];

        /// <summary>Outward normal of a side in room space (상 +Z, 하 -Z, 좌 -X, 우 +X).</summary>
        public static Vector3 Normal(Direction d)
        {
            switch (d)
            {
                case Direction.Up: return Vector3.forward;
                case Direction.Down: return Vector3.back;
                case Direction.Left: return Vector3.left;
                default: return Vector3.right;
            }
        }

        /// <summary>Axis along a side's wall (상·하 → X, 좌·우 → Z).</summary>
        public static Vector3 Tangent(Direction d) => d == Direction.Up || d == Direction.Down ? Vector3.right : Vector3.forward;

        /// <summary>Reads both prefabs. Throws InvalidOperationException with a user-facing reason.</summary>
        public static RoomPrefabLayout Build(GameObject roomPrefab, GameObject doorPrefab, DoorPointOverrides overrides,
                                             string doorPointName, string doorBodyName, Vector3 doorOffset)
        {
            if (roomPrefab == null) throw new InvalidOperationException("방 프리팹(Room Prefab)이 지정되지 않았습니다.");
            if (doorPrefab == null) throw new InvalidOperationException("문 프리팹(Door Prefab)이 지정되지 않았습니다.");
            var l = new RoomPrefabLayout
            {
                RoomPrefab = roomPrefab,
                DoorPrefab = doorPrefab,
                DoorBodyName = doorBodyName,
                RoomRotation = roomPrefab.transform.localRotation,
                RoomScale = roomPrefab.transform.localScale,
                DoorRotation = doorPrefab.transform.localRotation,
                DoorScale = doorPrefab.transform.localScale,
            };
            var room = roomPrefab.transform;
            l.RoomBounds = MeshBounds(room, room) ?? throw new InvalidOperationException($"방 프리팹 '{roomPrefab.name}'에 MeshFilter가 없어 방 크기를 잴 수 없습니다.");
            var size = l.RoomBounds.size;
            if (Mathf.Abs(size.x - size.z) > Eps)
                l.Warnings.Add($"방 크기가 정사각형이 아닙니다 ({size.x:0.###}×{size.z:0.###}). 자동 간격은 큰 쪽 {l.MeasuredSpacing:0.###}을 씁니다.");
            var c = l.RoomBounds.center;
            if (Mathf.Abs(c.x) > Eps || Mathf.Abs(c.z) > Eps)
                l.Warnings.Add($"방 프리팹의 메시 중심이 피벗에서 벗어나 있습니다 ({c.x:0.###}, {c.z:0.###}). 좌표 격자는 피벗 기준으로 배치합니다.");

            l.ReadDoorPoints(room, overrides, doorPointName);

            var door = doorPrefab.transform;
            var bodies = door.GetComponentsInChildren<Transform>(true).Where(t => t != door && t.name == doorBodyName).ToList();
            if (bodies.Count == 0) throw new InvalidOperationException($"문 프리팹 '{doorPrefab.name}'에 '{doorBodyName}' 자식(문 본체)이 없습니다.");
            if (bodies.Count > 1) throw new InvalidOperationException($"문 프리팹 '{doorPrefab.name}'에 '{doorBodyName}' 자식이 {bodies.Count}개 있습니다. 하나만 두거나 Door Body Name을 바꾸세요.");
            l.DoorBody = MeshBounds(bodies[0], door) ?? ColliderBounds(bodies[0], door) ??
                         throw new InvalidOperationException($"문 본체 '{doorBodyName}'에 MeshFilter나 BoxCollider가 없어 크기를 알 수 없습니다.");
            if (Mathf.Abs(l.DoorBody.size.x - l.DoorBody.size.z) < Eps)
                l.Warnings.Add("문 본체의 X·Z 크기가 같아 두께 방향을 X로 가정했습니다.");

            foreach (var d in Directions.DisplayOrder) l.poses[(int)d] = l.ComputePose(d, doorOffset);
            return l;
        }

        void ReadDoorPoints(Transform room, DoorPointOverrides overrides, string name)
        {
            if (overrides != null && overrides.Any)
            {
                var missing = Directions.DisplayOrder.Where(d => overrides.Get(d) == null).Select(Directions.Korean).ToList();
                if (missing.Count > 0) throw new InvalidOperationException($"DoorPoint 직접 지정은 네 방향 모두 필요합니다. 누락: {string.Join(", ", missing)}");
                var seen = new Dictionary<Transform, Direction>();
                foreach (var d in Directions.DisplayOrder)
                {
                    var t = overrides.Get(d);
                    if (!t.IsChildOf(room)) throw new InvalidOperationException($"{Directions.Korean(d)} DoorPoint '{t.name}'가 방 프리팹 '{room.name}'의 자식이 아닙니다.");
                    if (seen.TryGetValue(t, out var other)) throw new InvalidOperationException($"같은 DoorPoint가 {Directions.Korean(other)}·{Directions.Korean(d)}에 중복 지정됐습니다.");
                    seen[t] = d;
                    var p = Space(t, room).MultiplyPoint3x4(Vector3.zero);
                    doorPoints[(int)d] = p;
                    if (Classify(p) is Direction g && g != d)
                        Warnings.Add($"{Directions.Korean(d)}에 지정한 DoorPoint의 위치 {Fmt(p)}는 {Directions.Korean(g)} 쪽입니다. 지정이 맞는지 확인하세요.");
                }
                DoorPointSource = "Inspector 직접 지정";
                return;
            }
            var points = room.GetComponentsInChildren<Transform>(true).Where(t => t != room && t.name == name).ToList();
            if (points.Count == 0) throw new InvalidOperationException($"방 프리팹 '{room.name}'에 '{name}' 이름의 출입구 기준점이 없습니다.");
            var found = new Dictionary<Direction, List<(Transform t, Vector3 p)>>();
            foreach (var t in points)
            {
                var p = Space(t, room).MultiplyPoint3x4(Vector3.zero);
                var d = Classify(p) ?? throw new InvalidOperationException($"'{Path(t, room)}' 위치 {Fmt(p)}가 대각선 위치라 방향을 정할 수 없습니다. DoorPoint를 직접 지정하세요.");
                if (!found.TryGetValue(d, out var list)) found[d] = list = new List<(Transform, Vector3)>();
                list.Add((t, p));
            }
            var problems = new List<string>();
            foreach (var d in Directions.DisplayOrder)
            {
                if (!found.TryGetValue(d, out var list)) problems.Add($"{Directions.Korean(d)}: 없음");
                else if (list.Count > 1) problems.Add($"{Directions.Korean(d)}: {list.Count}개 중복 ({string.Join(", ", list.Select(x => $"{Path(x.t, room)} {Fmt(x.p)}"))})");
                else doorPoints[(int)d] = list[0].p;
            }
            if (problems.Count > 0)
                throw new InvalidOperationException($"'{name}' 자동 분류 실패 — {string.Join(" / ", problems)}. Door Point Overrides에 네 방향을 직접 지정하세요.");
            DoorPointSource = $"'{name}' {points.Count}개를 위치로 자동 분류";
        }

        /// <summary>
        /// The TestDoor parent turns only about Y. Its thin horizontal body axis must lie along the side's normal;
        /// of the two yaws that do this, the one whose body extends from the corner DoorPoint toward the middle of
        /// the wall is used (DoorPoint is an edge of the doorway, the body is offset from the parent's pivot).
        /// doorOffset is then added in the side's frame: x outward, y up, z toward the doorway middle.
        /// </summary>
        DoorPose ComputePose(Direction d, Vector3 doorOffset)
        {
            var n = Normal(d);
            var t = Tangent(d);
            var p = doorPoints[(int)d];
            var body = DoorBody;
            var thin = body.size.x <= body.size.z ? Vector3.right : Vector3.forward;
            var a = Vector3.Dot(p, t);
            var towardMiddle = Mathf.Abs(a) < Eps ? t : (a > 0 ? -t : t);
            float? chosen = null, fallback = null;
            for (var yaw = 0; yaw < 360; yaw += 90)
            {
                var q = Quaternion.Euler(0, yaw, 0);
                if (Mathf.Abs(Mathf.Abs(Vector3.Dot(q * thin, n)) - 1f) > Eps) continue;
                if (fallback == null) fallback = yaw;
                var center = q * body.center;
                var good = Mathf.Abs(a) < Eps ? Vector3.Dot(center, n) >= 0 : Vector3.Dot(center, towardMiddle) > Eps;
                if (good)
                {
                    chosen = yaw;
                    break;
                }
            }
            var y = chosen ?? fallback ?? 0f;
            if (chosen == null)
                Warnings.Add($"{Directions.Korean(d)}: 문 본체가 DoorPoint에서 출입구 가운데 쪽으로 뻗는 회전을 찾지 못해 {y}°를 사용합니다. Door Offset으로 보정하세요.");
            var rot = Quaternion.Euler(0, y, 0);
            var pos = p + n * doorOffset.x + Vector3.up * doorOffset.y + towardMiddle * doorOffset.z;
            return new DoorPose(pos, rot * DoorRotation, y, pos + rot * body.center);
        }

        static Direction? Classify(Vector3 p)
        {
            var ax = Mathf.Abs(p.x);
            var az = Mathf.Abs(p.z);
            if (Mathf.Abs(ax - az) < Eps) return null;
            if (ax > az) return p.x > 0 ? Direction.Right : Direction.Left;
            return p.z > 0 ? Direction.Up : Direction.Down;
        }

        /// <summary>Transform of t relative to the root's pivot, including the root's rotation and scale.</summary>
        static Matrix4x4 Space(Transform t, Transform root)
        {
            var m = Matrix4x4.identity;
            for (var x = t; x != null && x != root; x = x.parent) m = Matrix4x4.TRS(x.localPosition, x.localRotation, x.localScale) * m;
            return Matrix4x4.TRS(Vector3.zero, root.localRotation, root.localScale) * m;
        }

        static Bounds? MeshBounds(Transform under, Transform root)
        {
            Bounds? total = null;
            foreach (var f in under.GetComponentsInChildren<MeshFilter>(true))
                if (f.sharedMesh != null) total = Encapsulate(total, f.sharedMesh.bounds, Space(f.transform, root));
            return total;
        }

        static Bounds? ColliderBounds(Transform under, Transform root)
        {
            Bounds? total = null;
            foreach (var b in under.GetComponentsInChildren<BoxCollider>(true))
                total = Encapsulate(total, new Bounds(b.center, b.size), Space(b.transform, root));
            return total;
        }

        static Bounds Encapsulate(Bounds? total, Bounds local, Matrix4x4 m)
        {
            var result = total ?? new Bounds(m.MultiplyPoint3x4(local.center), Vector3.zero);
            for (var i = 0; i < 8; i++)
            {
                var corner = local.center + Vector3.Scale(local.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                result.Encapsulate(m.MultiplyPoint3x4(corner));
            }
            return result;
        }

        static string Path(Transform t, Transform root)
        {
            var names = new List<string>();
            for (var x = t; x != null && x != root; x = x.parent) names.Insert(0, x.name);
            return string.Join("/", names);
        }

        public static string Fmt(Vector3 v) => $"({v.x:0.###}, {v.y:0.###}, {v.z:0.###})";

        public string Describe(float spacing)
        {
            var lines = new List<string>
            {
                $"방 프리팹 '{RoomPrefab.name}' 크기 {RoomBounds.size.x:0.###}×{RoomBounds.size.z:0.###} (높이 {RoomBounds.size.y:0.###}) · 배치 간격 {spacing:0.###}",
                $"문 프리팹 '{DoorPrefab.name}' 본체 중심 {Fmt(DoorBody.center)} 크기 {Fmt(DoorBody.size)} · 출입구 기준점: {DoorPointSource}",
            };
            foreach (var d in Directions.DisplayOrder)
            {
                var pose = poses[(int)d];
                lines.Add($"  {Directions.Korean(d)}: DoorPoint {Fmt(doorPoints[(int)d])} → 문 회전 Y {pose.Yaw:0}° · 문 위치 {Fmt(pose.Position)} · 닫힌 본체 중심 {Fmt(pose.BodyCenter)}");
            }
            foreach (var w in Warnings) lines.Add("  주의: " + w);
            return string.Join("\n", lines);
        }
    }
}
