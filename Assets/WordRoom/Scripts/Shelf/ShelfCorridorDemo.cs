using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using WordRoom.Data;
using WordRoom.UnityLayer;
using WordRoom.World;

namespace WordRoom.Shelf
{
    /// <summary>
    /// Prototype of corridor-shelf exploration on the Standard Korean Dictionary package. Each room is one headword
    /// and one fixed sense (rooms are told apart by headword and senseId): its definition is read along the corridor as
    /// wall text and word doors.
    /// After the dictionary is loaded, a ShelfLinkGraph is computed once on a worker thread: door candidates (top
    /// vocabularyLimit, at least doorMinLength long, not restricted, conservative particle cut), each room's door words
    /// (own headword out, room-setting selection, first place per headword), and which rooms can be left again — a
    /// room with no door leading to such a room is dropped unless it is the goal, repeatedly. Door placement and
    /// destination choice both read that result: a link word whose destinations were all dropped stays wall text, and
    /// behind a door each dictionary entry (EntryId) with a valid sense is one destination (its representative sense):
    /// one entry is a direct link, two or more make a short fork, and the player picks one by walking into its room.
    /// Aiming at a handle of the current room and pressing E builds everything behind the door first (the fork and
    /// every branch room, or the one room, floors and colliders included), then the door slides aside. Another handle
    /// closes the open door and removes all of that first (refused while the player is in or right in front of that
    /// opening, or anywhere behind it). The current room and the move count change once, when the feet pass one
    /// destination room's entrance by entryMargin: the previous room, the fork and the other branches are removed then
    /// and the connecting passage sealed behind the player. Opening a door or waiting in the passage or the fork keeps
    /// the current room, and the player can walk back. Entering the goal word's room (of the goal sense, when one is
    /// set) is success. A room's entry is resolved once and kept, so a revisited room shows the same sense and layout.
    /// Reuses FirstPersonPlayer for walking, look, cursor and E; PlayHint canvases show the aim hint and the status.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShelfCorridorDemo : MonoBehaviour
    {
        /// <summary>Where a link word's door leads: one room per remaining dictionary entry, with its sign.</summary>
        sealed class Targets
        {
            public readonly List<ShelfEntry> Rooms = new List<ShelfEntry>();
            public readonly List<string> Labels = new List<string>();
            public bool IsFork => Rooms.Count > 1;
        }

        [Header("장면 참조")]
        [Tooltip("표준국어대사전 데이터 패키지(StreamingAssets/WordRoomData). 비어 있으면 같은 오브젝트에서 찾습니다.")]
        [SerializeField] WordRoomDataService data;
        [Tooltip("1인칭 플레이어(이동·시점·E 입력 재사용)")]
        [SerializeField] FirstPersonPlayer player;
        [Tooltip("벽 글·문 단어·제목 글꼴(한글 TMP 글꼴)")]
        [SerializeField] TMP_FontAsset font;
        [Tooltip("화면 중앙 아래 안내(손잡이 조준·성공 표시)")]
        [SerializeField] PlayHint hint;
        [Tooltip("좌상단 상태(시작·목표·현재·이동 횟수·경로)")]
        [SerializeField] PlayHint status;

        [Header("시작·목표 (우클릭 메뉴로 예제 1·2 설정)")]
        [Tooltip("시작 표제어")]
        [SerializeField] string startWord = ShelfExample.SeaWord;
        [Tooltip("시작 방 senseId. 비어 있으면 방 설정의 뜻, 그것도 없으면 첫 뜻.")]
        [SerializeField] string startSenseId = ShelfExample.SeaSense;
        [Tooltip("목표 표제어")]
        [SerializeField] string goalWord = ShelfExample.EarthWord;
        [Tooltip("목표 senseId. 지정하면 그 뜻의 방에 들어가야 성공, 비어 있으면 목표 표제어의 어느 뜻 방이든 성공. 목표 방은 출구가 없어도 목적지로 남습니다.")]
        [SerializeField] string goalSenseId = ShelfExample.PlanetSense;

        [Header("문 후보")]
        [Tooltip("문 후보로 쓸 빈도 상위 표제어 수(사전 패키지 순위 기준)")]
        [Min(1)] [SerializeField] int vocabularyLimit = 15000;
        [Tooltip("문 후보 표제어의 최소 글자 수(GetWordLength). 미만인 단어는 원래 자리의 벽 글로 둡니다.")]
        [Min(1)] [SerializeField] int doorMinLength = 2;

        [Header("사전 방 설정")]
        [Tooltip("방별 뜻(senseId)·이동 링크 선정. 목록에 없는 방은 그 뜻(없으면 첫 뜻)과 링크 후보 전부를 씁니다.")]
        [SerializeField] List<ShelfRoom> rooms = ShelfExample.Rooms();
        [Tooltip("동음이의어 갈래 참고 설정(선택). 갈림길은 사전의 EntryId 그룹으로 자동 생성하며, 여기 적은 senseId는 그 항목의 대표 뜻 후보, label은 그 대표 뜻 갈래의 안내 문구로만 씁니다. 비워도 동작합니다.")]
        [SerializeField] List<ShelfFork> forks = ShelfExample.Forks();

        [Header("메뉴·결과 (WordRoom/서가 시안 메뉴·결과 구성으로 연결)")]
        [Tooltip("Esc 메뉴(재시작 = 이어서 하기). 비어 있으면 같은 오브젝트에서 찾고, 없으면 Esc는 커서 해제만 합니다.")]
        [SerializeField] ShelfPauseMenu pauseMenu;
        [Tooltip("목표 도달 결과 화면(다시 도전). 비어 있으면 같은 오브젝트에서 찾고, 없으면 화면 아래 안내만 남습니다.")]
        [SerializeField] ShelfResultScreen resultScreen;

        [Header("배치")]
        [SerializeField] ShelfLayout layout = new ShelfLayout();
        [SerializeField] ShelfMaterials materials = new ShelfMaterials();

        readonly Dictionary<string, ShelfEntry> entries = new Dictionary<string, ShelfEntry>();    // room key → entry
        readonly Dictionary<string, ShelfEntry> requests = new Dictionary<string, ShelfEntry>();   // "word#senseId or empty" → entry or null
        readonly Dictionary<string, Targets> targets = new Dictionary<string, Targets>();
        readonly List<string> path = new List<string>();         // room labels (word + SenseId) for the HUD
        readonly List<string> pathWords = new List<string>();    // headwords in order, for the result
        ShelfEntry startEntry;
        double runStart;                                         // realtime when the run became playable
        bool retrying;
        WordRoomDataset dataset;
        Task<ShelfLinkGraph> graphTask;          // the one running computation of this component (null: none)
        CancellationTokenSource graphCancel;
        ShelfLinkGraph graph;
        ShelfRoomBuilder builder;
        ShelfRoomView current;
        ShelfPassage openPassage;
        int moves;
        bool reached;
        bool running;
        bool stopped;
        string startLabel;
        string goalSurface;
        string goalSense;                        // null: any sense of the goal word

        public string CurrentWord => current != null ? current.Room.Word : null;
        public string CurrentSenseId => current != null ? current.Room.SenseId : null;
        public int Moves => moves;
        public bool Reached => reached;

        void Reset() => ApplyEarthExample();

        [ContextMenu("예제 1 설정: 항구 → 바다")]
        void ApplyHarborExample() => ApplyExample(ShelfExample.HarborWord, ShelfExample.HarborSense, ShelfExample.SeaWord, ShelfExample.SeaSense);

        [ContextMenu("예제 2 설정: 바다 → 지구(행성)")]
        void ApplyEarthExample() => ApplyExample(ShelfExample.SeaWord, ShelfExample.SeaSense, ShelfExample.EarthWord, ShelfExample.PlanetSense);

        /// <summary>"…70%가 바다이다." in the planet sense: 바다 is a door, 이다 stays wall text.</summary>
        [ContextMenu("예제 3 설정: 지구(행성) → 바다")]
        void ApplyPlanetExample() => ApplyExample(ShelfExample.EarthWord, ShelfExample.PlanetSense, ShelfExample.SeaWord, ShelfExample.SeaSense);

        /// <summary>"…에도 시대에 집대성하였다.": the noun 에도, spelled like the ending 에도, is a door.</summary>
        [ContextMenu("예제 4 설정: 가부키 → 에도")]
        void ApplyEdoExample() => ApplyExample("가부키", "412174", "에도", "466082");

        /// <summary>해안(317035) "바다와 육지가 맞닿은 부분." → 바다(331764) "…육지 면적의 …" → 면적(148273): 2 moves.</summary>
        [ContextMenu("예제 5 설정: 해안 → 면적")]
        void ApplyCoastExample() => ApplyExample("해안", "317035", "면적", "148273");

        void ApplyExample(string fromWord, string fromSense, string toWord, string toSense)
        {
#if UNITY_EDITOR
            UnityEditor.Undo.RecordObject(this, "서가 시안 예제 설정");
#endif
            startWord = fromWord;
            startSenseId = fromSense;
            goalWord = toWord;
            goalSenseId = toSense;
            rooms = ShelfExample.Rooms();
            forks = ShelfExample.Forks();
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }

        void Awake()
        {
            if (data == null) data = GetComponent<WordRoomDataService>();
            if (pauseMenu == null) pauseMenu = GetComponent<ShelfPauseMenu>();
            if (resultScreen == null) resultScreen = GetComponent<ShelfResultScreen>();
        }

        void OnEnable()
        {
            if (resultScreen != null) resultScreen.RetryRequested += Retry;
        }

        void Start()
        {
            if (data == null) Stop("WordRoomDataService 참조가 비어 있습니다");
            else if (player == null) Stop("FirstPersonPlayer 참조가 비어 있습니다");
            else if (font == null) Stop("글꼴(TMP_FontAsset) 참조가 비어 있습니다");
            else SetHint("사전 불러오는 중…");
        }

        /// <summary>
        /// Leaving the scene, destroying the object or turning the component off cancels a running link computation and
        /// drops it, so its result can never build a room. Turned on again before the start room exists, Update starts
        /// a new computation with the current settings.
        /// </summary>
        void OnDisable()
        {
            if (resultScreen != null) resultScreen.RetryRequested -= Retry;
            if (graphTask == null) return;
            CancelGraph();
            if (!stopped) SetHint(null);
            Debug.Log("[서가 시안] 문 연결 계산을 취소했습니다. 컴포넌트를 다시 켜면 현재 설정으로 다시 계산합니다.", this);
        }

        void OnDestroy() => CancelGraph();

        void CancelGraph()
        {
            if (graphCancel != null)
            {
                graphCancel.Cancel();
                graphCancel.Dispose();
                graphCancel = null;
            }
            graphTask = null;                        // a computation that still finishes is never read
        }

        void Update()
        {
            if (stopped) return;
            if (!running)
            {
                if (graphTask != null)
                {
                    if (graphTask.IsCompleted) Ready(graphTask);
                }
                else if (data.State == DataLoadState.Failed) Stop($"사전 데이터 로딩 실패: {data.Error?.Message}");
                else if (data.TryGetDataset(out var d)) Begin(d);   // rooms only after the dictionary is loaded
                return;
            }
            var entered = EnteredDestination();
            if (entered != null) Enter(openPassage, entered);
            UpdateAim();
        }

        /// <summary>Checks the goal, snapshots the settings and starts the link graph on a worker thread.</summary>
        void Begin(WordRoomDataset d)
        {
            dataset = d;
            if (string.IsNullOrWhiteSpace(goalWord) || !d.TryGetWordRank(goalWord.Trim(), out var goalRank))
            {
                Stop($"목표 표제어 '{goalWord}'가 사전 패키지에 없습니다");
                return;
            }
            goalSurface = d.GetWord(goalRank);
            goalSense = null;
            if (!string.IsNullOrWhiteSpace(goalSenseId))
            {
                var id = goalSenseId.Trim();
                if (!SenseOf(goalRank, id, out _))
                {
                    Stop($"목표 senseId '{id}'는 '{goalSurface}'의 뜻이 아니거나 뜻풀이가 없습니다");
                    return;
                }
                goalSense = id;
            }
            ValidateForks();

            var rules = new List<ShelfLinkGraph.RoomRule>();
            if (rooms != null)
                foreach (var r in rooms)
                    if (r != null && !string.IsNullOrWhiteSpace(r.word))
                        rules.Add(new ShelfLinkGraph.RoomRule
                        {
                            Word = r.word.Trim(),
                            SenseId = string.IsNullOrWhiteSpace(r.senseId) ? null : r.senseId.Trim(),
                            Links = Selection(r),
                        });
            var forkSenses = new Dictionary<string, List<string>>();
            if (forks != null)
                foreach (var f in forks)
                {
                    if (f?.branches == null || string.IsNullOrWhiteSpace(f.word)) continue;
                    var word = f.word.Trim();
                    if (!forkSenses.TryGetValue(word, out var list)) forkSenses[word] = list = new List<string>();
                    foreach (var b in f.branches)
                        if (b != null && !string.IsNullOrWhiteSpace(b.senseId)) list.Add(b.senseId.Trim());
                }
            int limit = vocabularyLimit, minLength = doorMinLength;
            string gw = goalSurface, gs = goalSense;
            CancelGraph();                           // never two computations for one component
            graphCancel = new CancellationTokenSource();
            var token = graphCancel.Token;
            graphTask = Task.Run(() => new ShelfLinkGraph(d, limit, minLength, rules, forkSenses, gw, gs, token), token);
            graphTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);   // a dropped task's failure is observed
            SetHint("문 연결 계산 중…");
        }

        /// <summary>
        /// The link graph is done: checks the goal can be entered and the start room has a door, then builds the start
        /// room and places the player. Only the current computation gets here (a cancelled one was dropped).
        /// </summary>
        void Ready(Task<ShelfLinkGraph> task)
        {
            graphCancel?.Dispose();
            graphCancel = null;
            graphTask = null;
            if (task.IsCanceled)
            {
                Debug.Log("[서가 시안] 문 연결 계산이 취소됐습니다.", this);
                SetHint(null);
                return;
            }
            if (task.IsFaulted)
            {
                Stop($"문 연결 계산 실패: {task.Exception?.GetBaseException().Message}");
                return;
            }
            graph = task.Result;
            foreach (var problem in graph.ExceptionProblems())
                Debug.LogWarning($"[서가 시안] 분리 예외 '{problem}'는 표제어가 어절 앞부분이 아니거나 사전에 없어 적용하지 않습니다.", this);
            Debug.Log($"[서가 시안] 문 연결 계산: 방 후보 {graph.RoomCount:N0}개 중 출구 없는 방 {graph.RemovedCount:N0}개 제외 " +
                      $"(상위 {vocabularyLimit:N0}개 · {doorMinLength}글자 이상 · 목표 {GoalLabel} 유지)", this);

            var reason = GoalUnreachable();
            if (reason != null)
            {
                Stop(reason);
                return;
            }
            var start = EntryOf(startWord, startSenseId);
            if (start == null)
            {
                Stop($"시작 '{startWord}'({Blank(startSenseId)})의 뜻풀이를 사전 패키지에서 찾지 못했습니다");
                return;
            }
            if (IsGoal(start))
            {
                Stop("시작 방이 이미 목표입니다");
                return;
            }
            if (start.DoorCount == 0)
            {
                Stop($"시작 방 {start.Label}에 들어갈 수 있는 문이 없습니다.");   // before the room is built
                return;
            }

            startLabel = start.Label;
            startEntry = start;
            builder = new ShelfRoomBuilder(layout, materials, font, transform);
            if (!BeginRun(out var failure))
            {
                Stop($"시작 방에 플레이어를 배치하지 못했습니다: {failure}");
                return;
            }
            running = true;
            Debug.Log($"[서가 시안] 시작: {start.Label} → 목표 {GoalLabel} · {Describe(start)}", this);
        }

        /// <summary>
        /// A new run of this challenge: the start room built again from the kept entry (same layout and links), the
        /// player placed near its entrance, 0 moves, a path of the start room only, and the timer started — from here,
        /// when the player can play (dictionary loading and the link graph are not counted; an open Escape menu is).
        /// </summary>
        bool BeginRun(out string failure)
        {
            current = builder.Build(startEntry, Vector3.zero, Quaternion.identity, false);
            openPassage = null;
            moves = 0;
            reached = false;
            path.Clear();
            path.Add(startEntry.Label);
            pathWords.Clear();
            pathWords.Add(startEntry.Word);

            // PlaceAt faces world +Z, which is the start room's corridor; then step back near the entrance.
            var spawn = current.transform.TransformPoint(new Vector3(0f, 0f, Mathf.Min(layout.spawnDepth, current.Length * 0.5f)));
            if (!player.PlaceAt(current.Floor, out failure) || !player.MoveOnto(current.transform, spawn, out failure)) return false;
            runStart = Time.realtimeSinceStartupAsDouble;
            SetHint(null);
            RefreshStatus();
            return true;
        }

        /// <summary>
        /// The goal was entered: time, moves and path are fixed once, the challenge's personal bests updated and saved,
        /// and the result screen shown (the Escape menu is closed and off meanwhile). Without a usable result screen the
        /// success hint stays on screen and the menu comes back.
        /// </summary>
        void Finish()
        {
            var seconds = Time.realtimeSinceStartupAsDouble - runStart;
            var challenge = ShelfRecords.ChallengeKey(startEntry.Word, startEntry.SenseId, goalSurface, goalSense);
            var best = ShelfRecords.Submit(challenge, seconds, moves, out var newTime, out var newMoves);
            Debug.Log($"[서가 시안] 기록 확정 {challenge}: {seconds:0.0}초 · 이동 {moves}회 · {string.Join(" → ", pathWords)}" +
                      $" (최단 {best.Seconds:0.0}초{(newTime ? " 새 기록" : "")} · 최소 {best.Moves}회{(newMoves ? " 새 기록" : "")})", this);
            if (pauseMenu != null)
            {
                pauseMenu.Close();
                pauseMenu.enabled = false;                   // no Escape menu over the result
            }
            if (resultScreen != null && resultScreen.Show(seconds, moves, best, newTime, newMoves)) return;
            if (pauseMenu != null) pauseMenu.enabled = true;
        }

        /// <summary>
        /// 다시 도전: the same challenge from the start in this scene (no scene load). Everything of the last run goes —
        /// the goal room, an open door's rooms and fork — the start room is built again from the same entry, and the
        /// dictionary, the link graph and the saved bests are reused. Taken once per result (the button is disabled at
        /// the press and only a finished run can be retried), so the start room is never built twice.
        /// </summary>
        void Retry()
        {
            if (!running || !reached || retrying) return;
            retrying = true;
            try
            {
                if (resultScreen != null) resultScreen.Hide();
                if (openPassage != null) openPassage.Close();
                if (current != null)
                {
                    current.gameObject.SetActive(false);         // its colliders go now, before the new start room is placed
                    Destroy(current.gameObject);
                }
                current = null;
                if (!BeginRun(out var failure))
                {
                    Stop($"다시 도전: 시작 방에 플레이어를 배치하지 못했습니다: {failure}");
                    return;
                }
                if (pauseMenu != null) pauseMenu.enabled = true;
                Debug.Log($"[서가 시안] 다시 도전: {startEntry.Label} → 목표 {GoalLabel}", this);
            }
            finally
            {
                retrying = false;
            }
        }

        void UpdateAim()
        {
            if (reached)
            {
                if (resultScreen != null && resultScreen.IsShown) SetHint(null);
                else SetHint($"목표 도달 성공 · {startLabel} → {current.Room.Label} · 이동 {moves}회");
                return;
            }
            if (!player.CanInteract || !player.TryGetAimed(layout.interactDistance, out ShelfHandle handle) ||
                handle.Passage == null || handle.Passage.Owner != current)
            {
                SetHint(null);
                return;
            }
            var passage = handle.Passage;
            if (passage.Current != ShelfPassage.State.Closed)
            {
                SetHint(passage.Current == ShelfPassage.State.Open ? $"문 열림 · [{handle.TargetId}] 방으로 걸어 들어가세요" : $"문 여는 중 · [{handle.TargetId}]");
                return;
            }
            if (openPassage != null && openPassage.IsNear(player.transform.position, layout))
            {
                SetHint("열린 통로에서 나온 뒤 다른 손잡이를 당기세요");
                return;
            }
            var to = TargetsOf(handle.TargetId);
            SetHint(to.IsFork ? $"[E] 손잡이 당기기 · [{handle.TargetId}] 갈림길 {to.Rooms.Count}갈래" : $"[E] 손잡이 당기기 · [{handle.TargetId}]");
            if (player.InteractPressed) Pull(handle, to);
        }

        /// <summary>
        /// Builds everything behind the door first (one room, or the fork and every branch room — floors and colliders
        /// exist before the door moves), then opens. The current room stays.
        /// </summary>
        void Pull(ShelfHandle handle, Targets to)
        {
            if (to.Rooms.Count == 0)
            {
                Debug.LogWarning($"[서가 시안] '{handle.TargetId}'에 들어갈 수 있는 뜻이 없어 문을 열지 않습니다.", this);
                return;
            }
            if (openPassage != null) openPassage.Close();
            var passage = handle.Passage;
            var built = new List<ShelfRoomView>();
            GameObject fork = null;
            if (!to.IsFork)
            {
                passage.DestinationPose(layout, out var position, out var rotation);
                built.Add(builder.Build(to.Rooms[0], position, rotation, true));
            }
            else
            {
                passage.OuterPose(layout, out var position, out var rotation);
                var poses = new List<Pose>();
                fork = builder.BuildFork(position, rotation, to.Labels, poses);
                for (var i = 0; i < to.Rooms.Count; i++) built.Add(builder.Build(to.Rooms[i], poses[i].position, poses[i].rotation, true));
            }
            passage.Open(handle, built, fork);
            openPassage = passage;
            var where = to.IsFork ? $"갈림길 {string.Join(" / ", to.Rooms.ConvertAll(e => e.Label))}" : to.Rooms[0].Label;
            Debug.Log($"[서가 시안] 손잡이: {current.Room.Label}의 [{handle.TargetId}] 문을 엽니다 → {where} (현재 방 유지).", this);
        }

        /// <summary>The destination room of the open door whose entrance the feet have passed, if any.</summary>
        ShelfRoomView EnteredDestination()
        {
            if (openPassage == null) return null;
            var feet = player.transform.position;
            foreach (var room in openPassage.Destinations)
                if (room != null && room.HasEntered(feet, layout.entryMargin)) return room;
            return null;
        }

        /// <summary>The only place where the current room and the move count change, once per passage.</summary>
        void Enter(ShelfPassage passage, ShelfRoomView room)
        {
            var previous = current;
            passage.Commit(room);                    // the fork and the other branches go
            current = room;
            openPassage = null;
            moves++;
            path.Add(room.Room.Label);
            pathWords.Add(room.Room.Word);
            Destroy(previous.gameObject);
            current.SealEntrance();
            reached = IsGoal(current.Room);
            Debug.Log($"[서가 시안] 입장: {previous.Room.Label} → {current.Room.Label} (이동 {moves}회, 문 {current.Room.DoorCount}개){(reached ? " · 목표 도달 성공" : "")} · {Describe(current.Room)}", this);
            RefreshStatus();
            if (reached) Finish();                               // once: a reached run takes no more doors
        }

        bool IsGoal(ShelfEntry e) => e.Word == goalSurface && (goalSense == null || e.SenseId == goalSense);

        string GoalLabel => goalSense != null ? $"{goalSurface}({goalSense})" : $"{goalSurface}(뜻 무관)";

        /// <summary>
        /// Null when a goal room can be the destination of a door: the goal word must be a door candidate and, with a
        /// goal sense, that sense must be its entry's representative (goal rooms always stay valid, but a setting can
        /// name another sense of the same entry first). Otherwise the reason.
        /// </summary>
        string GoalUnreachable()
        {
            var why = graph.WhyNotCandidate(goalSurface);
            if (why != null) return $"목표 표제어 '{goalSurface}'는 문 후보가 아니어서({why}) 문으로 들어갈 수 없습니다";
            if (goalSense == null) return null;
            dataset.TryGetWordRank(goalSurface, out var rank);
            dataset.TryGetSenseRow(goalSense, out var goalRow);
            foreach (var group in graph.EntryGroups(rank))
            {
                if (!group.Contains(goalRow)) continue;
                var rep = graph.Representative(goalSurface, group);
                if (rep == goalRow) return null;
                var entry = dataset.GetSense(goalRow).EntryId;
                return $"목표 뜻 {goalSense}는 사전 항목 {entry}의 대표 뜻이 아니라 갈래로 선택될 수 없습니다(현재 대표 뜻 {dataset.GetSenseId(rep)}). 방 설정이나 갈래 설정에 {goalSurface} {goalSense}를 지정하세요";
            }
            return null;
        }

        // ---- dictionary --------------------------------------------------------------------------------------------

        /// <summary>
        /// The room of a headword and sense, resolved once. With a senseId it must be a sense of that word with a
        /// definition (else null, warned); without one the word's room settings give the sense, else its first sense.
        /// Its doors are the link graph's doors of that sense (the same result the destinations come from); every
        /// other part of the definition, a dropped link included, stays wall text in its original place.
        /// </summary>
        ShelfEntry EntryOf(string word, string senseId)
        {
            if (string.IsNullOrWhiteSpace(word)) return null;
            word = word.Trim();
            senseId = string.IsNullOrWhiteSpace(senseId) ? null : senseId.Trim();
            var request = ShelfEntry.KeyOf(word, senseId);
            if (requests.TryGetValue(request, out var cached)) return cached;
            var entry = Resolve(word, senseId);
            requests[request] = entry;
            return entry;
        }

        ShelfEntry Resolve(string word, string senseId)
        {
            if (!dataset.TryGetWordRank(word, out var rank))
            {
                Debug.LogWarning($"[서가 시안] 표제어 '{word}'가 사전 패키지에 없습니다.", this);
                return null;
            }
            var surface = dataset.GetWord(rank);
            int row;
            if (senseId != null)
            {
                if (!SenseOf(rank, senseId, out row))
                {
                    Debug.LogWarning($"[서가 시안] senseId '{senseId}'는 '{surface}'의 뜻이 아니거나 뜻풀이가 없습니다.", this);
                    return null;
                }
            }
            else
            {
                var configured = DefaultSetting(surface)?.senseId?.Trim();
                if (string.IsNullOrEmpty(configured) || !SenseOf(rank, configured, out row))
                {
                    if (!string.IsNullOrEmpty(configured))
                        Debug.LogWarning($"[서가 시안] 방 설정의 senseId '{configured}'는 '{surface}'의 뜻이 아니거나 뜻풀이가 없어 첫 뜻을 씁니다.", this);
                    row = FirstSense(rank);
                }
                if (row < 0) return null;
            }

            var sense = dataset.GetSense(row);
            var key = ShelfEntry.KeyOf(surface, sense.SenseId);
            if (entries.TryGetValue(key, out var known)) return known;

            var text = ShelfLinkText.DisplayDefinition(sense.Definition);   // the same text the graph searched
            var doors = graph.Doors(row);
            ReportDropped(surface, sense, text);
            var entry = new ShelfEntry(surface, sense.SenseId, sense.EntryId, sense.Pos, text, ShelfLinkText.Split(text, doors));
            entries[key] = entry;
            return entry;
        }

        /// <summary>Logs the links of a room that stay wall text: selected words that are no door, and doors with no destination left.</summary>
        void ReportDropped(string surface, SenseInfo sense, string text)
        {
            var selection = graph.RuleOf(surface, sense.SenseId)?.Links;
            var present = new HashSet<string>();
            var blocked = new List<string>();
            foreach (var c in graph.Extract(text, sense.SenseId))
            {
                if (!present.Add(c.Target) || c.Target == surface) continue;
                if (selection != null && !selection.Contains(c.Target)) continue;
                if (!graph.IsValidDoor(c.Target)) blocked.Add(c.Target);
            }
            if (blocked.Count > 0)
                Debug.Log($"[서가 시안] {surface}({sense.SenseId}): 들어갈 수 있는 뜻이 없어 벽 글로 두는 링크 {string.Join(", ", blocked)}", this);
            if (selection == null) return;
            foreach (var s in selection)
                if (!present.Contains(s))
                    Debug.Log($"[서가 시안] {surface}({sense.SenseId}) 선정 링크 '{s}'는 문 후보가 아니어서({graph.WhyNotCandidate(s) ?? "뜻풀이에 나오지 않음"}) 원래 자리의 벽 글로 둡니다.", this);
        }

        bool SenseOf(int rank, string senseId, out int row) =>
            dataset.TryGetSenseRow(senseId, out row) && dataset.WordOfSense(row) == rank && !string.IsNullOrEmpty(dataset.GetSense(row).Definition);

        /// <summary>The first room setting of a word, for its default room (no sense requested).</summary>
        ShelfRoom DefaultSetting(string surface)
        {
            if (rooms == null) return null;
            foreach (var r in rooms)
                if (r != null && r.word != null && r.word.Trim() == surface) return r;
            return null;
        }

        int FirstSense(int rank)
        {
            foreach (var row in dataset.SenseRowsOf(rank))
                if (!string.IsNullOrEmpty(dataset.GetSense(row).Definition)) return row;
            return -1;
        }

        static HashSet<string> Selection(ShelfRoom settings)
        {
            if (settings?.links == null) return null;
            var set = new HashSet<string>();
            foreach (var l in settings.links)
                if (!string.IsNullOrWhiteSpace(l)) set.Add(l.Trim());
            return set.Count > 0 ? set : null;
        }

        // ---- forks -------------------------------------------------------------------------------------------------

        ShelfFork ForkOf(string surface)
        {
            if (forks == null) return null;
            foreach (var f in forks)
                if (f != null && f.word != null && f.word.Trim() == surface) return f;
            return null;
        }

        /// <summary>
        /// Rooms behind a door of `word`, from the link graph: one per dictionary entry with a valid sense (its
        /// representative), in dictionary order. One is a direct link; two or more make a fork whose signs are the fork
        /// settings' label for that sense, else the start of its definition.
        /// </summary>
        Targets TargetsOf(string word)
        {
            if (targets.TryGetValue(word, out var known)) return known;
            var to = new Targets();
            if (dataset.TryGetWordRank(word, out var rank))
            {
                var surface = dataset.GetWord(rank);
                foreach (var row in graph.Destinations(surface))
                {
                    var e = EntryOf(surface, dataset.GetSenseId(row));
                    if (e == null) continue;
                    to.Rooms.Add(e);
                    to.Labels.Add(ForkLabel(surface, e.SenseId) ?? Excerpt(e.Definition));
                }
                var groups = graph.EntryGroups(rank).Count;
                if (to.IsFork || groups > to.Rooms.Count)
                    Debug.Log($"[서가 시안] [{surface}] 사전 항목 {groups}개 중 {to.Rooms.Count}개로 연결" +
                              $"{(to.IsFork ? "(갈림길)" : "")}: {string.Join(" / ", to.Rooms.ConvertAll(r => $"{r.Label} 항목 {r.EntryId}"))}", this);
            }
            targets[word] = to;
            return to;
        }

        string ForkLabel(string surface, string senseId)
        {
            var fork = ForkOf(surface);
            if (fork?.branches == null) return null;
            foreach (var b in fork.branches)
                if (b != null && b.senseId != null && b.senseId.Trim() == senseId && !string.IsNullOrWhiteSpace(b.label)) return b.label.Trim();
            return null;
        }

        /// <summary>Checks the optional fork settings once at start: each branch's senseId must be a sense of its word.</summary>
        void ValidateForks()
        {
            if (forks == null) return;
            foreach (var f in forks)
            {
                if (f == null || string.IsNullOrWhiteSpace(f.word) || f.branches == null) continue;
                var word = f.word.Trim();
                if (!dataset.TryGetWordRank(word, out var rank))
                {
                    Debug.LogWarning($"[서가 시안] 갈래 설정의 표제어 '{word}'가 사전 패키지에 없어 무시합니다.", this);
                    continue;
                }
                foreach (var b in f.branches)
                    if (b != null && !string.IsNullOrWhiteSpace(b.senseId) && !SenseOf(rank, b.senseId.Trim(), out _))
                        Debug.LogWarning($"[서가 시안] 갈래 설정의 senseId '{b.senseId.Trim()}'는 '{word}'의 뜻이 아니거나 뜻풀이가 없어 무시합니다.", this);
            }
        }

        /// <summary>First sentence of a definition, shortened, for a branch sign without a label.</summary>
        static string Excerpt(string definition)
        {
            var s = definition ?? "";
            var dot = s.IndexOf('.');
            if (dot > 0) s = s.Substring(0, dot);
            return s.Length > 22 ? s.Substring(0, 22) + "…" : s;
        }

        static string Describe(ShelfEntry e)
        {
            var sb = new StringBuilder();
            foreach (var p in e.Parts)
            {
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(p.IsLink ? "문 " : "벽 ").Append(p.Text);
            }
            return sb.ToString();
        }

        static string Blank(string s) => string.IsNullOrWhiteSpace(s) ? "뜻 미지정" : s.Trim();

        // ---- display -----------------------------------------------------------------------------------------------

        void RefreshStatus()
        {
            if (status == null) return;
            var sb = new StringBuilder();
            sb.Append("시작  ").Append(startLabel).Append('\n');
            sb.Append("목표  ").Append(GoalLabel).Append('\n');
            sb.Append("현재  ").Append(current.Room.Label).Append('\n');
            sb.Append("이동  ").Append(moves).Append("회\n");
            sb.Append("경로  ").Append(string.Join(" → ", path));
            if (reached) sb.Append('\n').Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(materials.linkColor)).Append(">목표 도달 성공</color>");
            status.Set(sb.ToString());
        }

        void SetHint(string text)
        {
            if (hint == null) return;
            if (string.IsNullOrEmpty(text)) hint.Hide();
            else hint.Set(text);
        }

        void Stop(string failure)
        {
            stopped = true;
            Debug.LogError($"[서가 시안] 시작하지 않습니다: {failure}", this);
            SetHint(failure);
            enabled = false;
        }
    }
}
