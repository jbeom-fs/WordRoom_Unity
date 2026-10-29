using System;
using System.Collections.Generic;
using UnityEngine;

namespace WordRoom.World
{
    /// <summary>
    /// The other coop participants' robots on this device — display only: they follow the poses those participants
    /// send (feet position and heading), never the rooms their confirmed state names, and they change nothing of the
    /// session. One robot per participant of the fixed list, never this device's own player (its first-person view stays
    /// as it is). A robot is made at its first pose, already where that pose puts it (never at the origin first); poses
    /// are played InterpolationDelay late and interpolated between the two around that time (position, and heading by
    /// the short way), never extrapolated: with no newer pose the robot holds the last one, and after StaleSeconds
    /// without any it is hidden. A pose of another Placement (the sender was placed or put back, not walked) is never
    /// blended with the ones before it. A robot standing outside the rooms this device shows is hidden (no room or word
    /// is made for it); when it shows again it starts from its newest pose. Robots carry no collider, rigidbody,
    /// camera, light or listener and sit on the Ignore Raycast layer, so they never stop a player or the door aim.
    /// Each robot's Accent slots get the participant's color through a property block set once (no material is
    /// copied, the shared materials never change); the color follows the participant's place in the fixed list, the
    /// same on every device. Main thread only.
    /// Read access for a map (TryGetPose, TryGetAccent, Participants) uses the same poses and colors.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RemotePlayers : MonoBehaviour
    {
        const int IgnoreRaycastLayer = 2;
        const int BufferSize = 6;

        [Header("외형 (WordRoom/협동 로봇 외형 구성으로 연결)")]
        [Tooltip("다른 참가자 표시용 프리팹(루트 = 발밑, +Z 정면, 모델은 Visual 자식 아래). 비우면 다른 참가자 외형을 표시하지 않습니다(플레이는 계속).")]
        [SerializeField] GameObject robotPrefab;
        [Tooltip("참가자 색으로 바꿀 재질(ArchiveRobot_Accent). 이 재질을 쓰는 슬롯만 참가자별 색이 됩니다. 비우면 색 구분 없이 표시합니다.")]
        [SerializeField] Material accentMaterial;
        [Tooltip("참가자 순서(ActorNumber 오름차순)별 Accent 색. 모든 기기에서 같은 참가자가 같은 색이 됩니다.")]
        [SerializeField] Color[] accentColors =
        {
            new Color32(0x72, 0xFF, 0xD5, 0xFF),   // mint
            new Color32(0xFF, 0xB5, 0x47, 0xFF),   // amber
            new Color32(0x6E, 0xC8, 0xFF, 0xFF),   // sky
            new Color32(0xFF, 0x7A, 0x9C, 0xFF),   // rose
        };
        [Tooltip("Accent 발광 세기(색 × 값)")]
        [SerializeField] float accentEmission = 1.5f;

        [Header("보간 (운영값)")]
        [Tooltip("받은 포즈를 이만큼(초) 늦게 재생해 두 포즈 사이를 보간합니다. 이동 중 전송 간격(0.1초)보다 크게 둡니다.")]
        [SerializeField] float interpolationDelay = 0.2f;
        [Tooltip("이 시간(초) 동안 새 포즈가 없으면 그 참가자를 숨깁니다(외삽하지 않음). 정지 중 전송 간격(1초)보다 크게 둡니다.")]
        [SerializeField] float staleSeconds = 3f;

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        readonly Dictionary<string, Remote> remotes = new Dictionary<string, Remote>();
        readonly List<string> participants = new List<string>();
        string localPlayer;
        Func<Vector3, bool> isShown;
        bool missingLogged;
        bool strippedLogged;

        readonly struct Pose
        {
            public readonly double Time;
            public readonly Vector3 Position;
            public readonly float Yaw;
            public readonly int Placement;

            public Pose(double time, Vector3 position, float yaw, int placement)
            {
                Time = time;
                Position = position;
                Yaw = yaw;
                Placement = placement;
            }
        }

        sealed class Remote
        {
            public Color Accent;
            public GameObject Instance;
            public int LastSeq;
            public double LastReceived;
            public bool Shown;
            public readonly List<Pose> Poses = new List<Pose>(BufferSize);
        }

        /// <summary>The fixed participants of the current game in their fixed order (this device's own included; one that left stays listed).</summary>
        public IReadOnlyList<string> Participants => participants;

        /// <summary>Grows whenever the set of shown participants changes (a new game, a clear, a participant leaving), so a reader rebinds only then.</summary>
        public int Version { get; private set; }

        /// <summary>Another participant of the current game that has not left (its pose may still be missing or stale).</summary>
        public bool IsPresent(string playerId) => playerId != null && remotes.ContainsKey(playerId);

        /// <summary>
        /// A new game: every robot and pose of an earlier one is dropped; the participants (fixed order, the same on every
        /// device) give the colors; `localPlayerId` is never shown; `shown` tells whether a world position lies in a room
        /// this device shows (null: always).
        /// </summary>
        public void Begin(IReadOnlyList<string> fixedParticipants, string localPlayerId, Func<Vector3, bool> shown)
        {
            Clear();
            localPlayer = localPlayerId;
            isShown = shown;
            for (var i = 0; i < fixedParticipants.Count; i++)
            {
                var id = fixedParticipants[i];
                participants.Add(id);
                if (id != localPlayerId) remotes[id] = new Remote { Accent = ColorAt(i) };
            }
            Version++;
        }

        /// <summary>Every robot and pose is dropped (game ended, connection lost, scene left).</summary>
        public void Clear()
        {
            foreach (var r in remotes.Values)
                if (r.Instance != null) Destroy(r.Instance);
            remotes.Clear();
            participants.Clear();
            localPlayer = null;
            isShown = null;
            Version++;
        }

        /// <summary>A participant left: its robot and poses go (it never comes back in this game).</summary>
        public void Remove(string playerId)
        {
            if (playerId == null || !remotes.TryGetValue(playerId, out var r)) return;
            if (r.Instance != null) Destroy(r.Instance);
            remotes.Remove(playerId);
            Version++;
        }

        /// <summary>
        /// A received pose of a participant (`now` = this device's real time): kept when it is newer than the last one
        /// (older or repeated ones change nothing). A new Placement, or a pose after a gap longer than StaleSeconds,
        /// starts over from it. False when it was not taken.
        /// </summary>
        public bool Apply(string playerId, int seq, int placement, Vector3 position, float yaw, double now)
        {
            if (playerId == null || !remotes.TryGetValue(playerId, out var r) || seq <= r.LastSeq) return false;
            if (!IsFinite(position) || float.IsNaN(yaw) || float.IsInfinity(yaw)) return false;
            r.LastSeq = seq;
            var poses = r.Poses;
            if (poses.Count > 0 && (poses[poses.Count - 1].Placement != placement || now - r.LastReceived > staleSeconds)) poses.Clear();
            poses.Add(new Pose(now, position, yaw, placement));
            if (poses.Count > BufferSize) poses.RemoveAt(0);
            r.LastReceived = now;
            return true;
        }

        /// <summary>The newest received pose of a participant while it is not stale (for a map); false otherwise.</summary>
        public bool TryGetPose(string playerId, out Vector3 position, out float yaw)
        {
            position = default;
            yaw = 0f;
            if (playerId == null || !remotes.TryGetValue(playerId, out var r) || r.Poses.Count == 0) return false;
            if (Time.realtimeSinceStartupAsDouble - r.LastReceived > staleSeconds) return false;
            var p = r.Poses[r.Poses.Count - 1];
            position = p.Position;
            yaw = p.Yaw;
            return true;
        }

        /// <summary>A participant's color (this device's own included), the same on every device.</summary>
        public bool TryGetAccent(string playerId, out Color color)
        {
            var i = participants.IndexOf(playerId);
            color = i >= 0 ? ColorAt(i) : default;
            return i >= 0;
        }

        Color ColorAt(int i) => accentColors != null && accentColors.Length > 0 ? accentColors[i % accentColors.Length] : Color.white;

        static bool IsFinite(Vector3 v) =>
            !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));

        void LateUpdate()
        {
            if (remotes.Count == 0) return;
            var now = Time.realtimeSinceStartupAsDouble;
            foreach (var r in remotes.Values)
            {
                var poses = r.Poses;
                var visible = poses.Count > 0 && now - r.LastReceived <= staleSeconds && (isShown == null || isShown(poses[poses.Count - 1].Position));
                if (!visible)
                {
                    Hide(r);
                    continue;
                }
                if (!r.Shown && poses.Count > 1) poses.RemoveRange(0, poses.Count - 1);   // shown again: from the newest pose, never flying in
                Sample(poses, now - interpolationDelay, out var position, out var yaw);
                var rotation = Quaternion.Euler(0f, yaw, 0f);
                if (!EnsureInstance(r, position, rotation)) continue;
                r.Instance.transform.SetPositionAndRotation(position, rotation);
                if (!r.Instance.activeSelf) r.Instance.SetActive(true);
                r.Shown = true;
            }
        }

        static void Hide(Remote r)
        {
            r.Shown = false;
            if (r.Instance != null && r.Instance.activeSelf) r.Instance.SetActive(false);
        }

        /// <summary>The pose at `time` between the two poses around it; before the first or after the last, that pose (no extrapolation).</summary>
        static void Sample(List<Pose> poses, double time, out Vector3 position, out float yaw)
        {
            var last = poses[poses.Count - 1];
            position = last.Position;
            yaw = last.Yaw;
            if (time <= poses[0].Time)
            {
                position = poses[0].Position;
                yaw = poses[0].Yaw;
                return;
            }
            for (var i = 1; i < poses.Count; i++)
            {
                var b = poses[i];
                if (b.Time < time) continue;
                var a = poses[i - 1];
                var span = b.Time - a.Time;
                var t = span > 0 ? (float)((time - a.Time) / span) : 1f;
                position = Vector3.Lerp(a.Position, b.Position, t);
                yaw = Mathf.LerpAngle(a.Yaw, b.Yaw, t);
                return;
            }
        }

        /// <summary>The robot of `r`, made once at its first shown pose (colors set once, nothing that collides or renders a view).</summary>
        bool EnsureInstance(Remote r, Vector3 position, Quaternion rotation)
        {
            if (r.Instance != null) return true;
            if (robotPrefab == null)
            {
                if (!missingLogged)
                {
                    missingLogged = true;
                    Debug.LogWarning("[협동] 원격 플레이어 프리팹이 연결되지 않아 다른 참가자 외형을 표시하지 않습니다(플레이는 계속). WordRoom/협동 로봇 외형 구성 메뉴를 실행하세요.", this);
                }
                return false;
            }
            var go = Instantiate(robotPrefab, position, rotation, transform);
            go.name = "Remote Robot";
            Strip(go);
            SetLayer(go.transform, IgnoreRaycastLayer);
            Paint(go, r.Accent);
            r.Instance = go;
            return true;
        }

        /// <summary>Display only: anything that collides, simulates, renders a view, lights or listens is taken off; an Animator is turned off.</summary>
        void Strip(GameObject go)
        {
            var removed = 0;
            foreach (var c in go.GetComponentsInChildren<Rigidbody>(true)) { Destroy(c); removed++; }
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) { c.enabled = false; Destroy(c); removed++; }
            foreach (var c in go.GetComponentsInChildren<Camera>(true)) { c.enabled = false; Destroy(c); removed++; }
            foreach (var c in go.GetComponentsInChildren<AudioListener>(true)) { c.enabled = false; Destroy(c); removed++; }
            foreach (var c in go.GetComponentsInChildren<Light>(true)) { c.enabled = false; Destroy(c); removed++; }
            foreach (var c in go.GetComponentsInChildren<Animator>(true)) c.enabled = false;
            if (removed == 0 || strippedLogged) return;
            strippedLogged = true;
            Debug.LogWarning($"[협동] 원격 플레이어 프리팹에 충돌·카메라·조명 등 표시용이 아닌 구성 요소 {removed}개가 있어 인스턴스에서 제거했습니다. 프리팹을 확인하세요.", this);
        }

        static void SetLayer(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            foreach (Transform child in t) SetLayer(child, layer);
        }

        /// <summary>The participant's color on the Accent slots only, once per robot (a property block: no material copy).</summary>
        void Paint(GameObject go, Color accent)
        {
            if (accentMaterial == null) return;
            var block = new MaterialPropertyBlock();
            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                for (var i = 0; i < materials.Length; i++)
                {
                    if (materials[i] != accentMaterial) continue;
                    block.Clear();
                    block.SetColor(BaseColorId, accent);
                    block.SetColor(EmissionColorId, accent * accentEmission);
                    renderer.SetPropertyBlock(block, i);
                }
            }
        }

        void OnDestroy() => Clear();
    }
}
