using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using WordRoom.Data;
using WordRoom.UnityLayer;
using WordRoom.World;

namespace WordRoom.Shelf
{
    /// <summary>
    /// Prototype of reading-room exploration on the Standard Korean Dictionary package. Each room is one headword
    /// shown in the scene's one octagonal reading room (ShelfRoomView): one low reading stand (lectern) per sense of
    /// that headword that has a link, standing on the desk of its side (ShelfFurniturePlan: at most one desk per side),
    /// each with an open book (no text on it) on its slanted panel. The player walks up to a book, sees only "[E] 읽기"
    /// while aiming at it from its reading area, presses E to read that sense spread open in the middle of the screen
    /// (ShelfReader) and clicks a link word to go to that headword's room.
    /// After the dictionary is loaded, a ShelfLinkGraph is computed once on a worker thread: link candidates (top
    /// vocabularyLimit, at least doorMinLength long, not restricted, conservative particle cut), each sense's link
    /// words (own headword out, per-sense link selection, first place per headword) and which headword rooms can be
    /// left again — a room's exits are the links of all of its senses together, and a room without an exit to such a
    /// room is dropped unless it is the goal, repeatedly. A link word whose room was dropped stays plain text.
    /// The challenge is drawn on the same worker thread each time the scene is entered: a random goal among the link
    /// candidates that some room really reaches along the final links in at least ChallengeMinMoves links at the
    /// shortest (rooms and distances found by following them backwards from the goal; a goal without such a room is
    /// skipped), then a random start among those rooms. The minimum applies to the draw only: arriving at the goal room
    /// in any number of moves finishes the run. 다시 도전 keeps the drawn pair. A fixed start and goal (the context-menu examples) are for development checks only.
    /// Moving (TryTravel — a link click now, a destination confirmed by a co-op host later): the destination room data
    /// is checked first, then input is locked (reading screen, Escape menu, walking) and the screen fades to black.
    /// In the dark the room title, the desks and lecterns and their documents are replaced and the player is placed at
    /// the room center facing side 0; only then are the reading screen closed and the current word, the path and the
    /// move count updated — the only place where they change, once per move. The screen fades back in and input
    /// returns. When furnishing or placing fails, the previous room is furnished again, the player put back where it
    /// stood, the reading screen stays open with the reason and the record is untouched. Opening and closing documents
    /// and walking inside a room never count as a move. Entering the goal headword's room finishes the run.
    /// The logical room (ShelfWordRoom: headword and documents) is resolved once per headword and kept apart from the
    /// physical room; its placement depends on the document count only, so a revisited room — or the same room on
    /// another client — has the same lecterns in the same places. Play UI shows headwords only; the logs add SenseIds.
    /// Reuses FirstPersonPlayer for walking, look, cursor and E; PlayHint canvases show the aim hint and the status.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShelfCorridorDemo : MonoBehaviour
    {
        const int LabelWhole = 12;               // a first sentence up to this many letters is the title as is
        const int LabelTail = 10;                // else the last eojeols up to this many letters, after "…"
        const int ChallengeMinMoves = 10;        // a random challenge's shortest way, in links, is at least this
        const string ReadHint = "[E] 읽기";       // the only aim hint: no title or preview before the book is opened
        const float RelockSeconds = 0.3f;        // after control comes back, the cursor is locked again for this long

        [Header("장면 참조")]
        [Tooltip("표준국어대사전 데이터 패키지(StreamingAssets/WordRoomData). 비어 있으면 같은 오브젝트에서 찾습니다.")]
        [SerializeField] WordRoomDataService data;
        [Tooltip("1인칭 플레이어(이동·시점·E 입력 재사용)")]
        [SerializeField] FirstPersonPlayer player;
        [Tooltip("화면 중앙 아래 안내([E] 읽기·성공 표시)")]
        [SerializeField] PlayHint hint;
        [Tooltip("좌상단 상태(시작·목표·현재·이동 횟수·경로)")]
        [SerializeField] PlayHint status;

        [Header("독서실")]
        [Tooltip("씬에 하나만 둔 팔각형 독서실(고정 모델). 방을 옮길 때 이 방의 제목과 가구만 바꿉니다.")]
        [SerializeField] ShelfRoomView roomView;
        [Tooltip("책상 프리팹(ReadingDesk). 한 면에 하나")]
        [SerializeField] ShelfDesk deskPrefab;
        [Tooltip("독서대 프리팹(ReadingStand). 뜻풀이 하나에 하나")]
        [SerializeField] ShelfLectern lecternPrefab;
        [Tooltip("독서대 위 펼친 책 프리팹(OpenBook_01~08). 독서대 순서대로 돌아가며 씁니다.")]
        [SerializeField] ShelfBook[] bookPrefabs = new ShelfBook[0];
        [Tooltip("방 이동 암전 화면. 비어 있으면 암전 없이 바로 바꿉니다.")]
        [SerializeField] ShelfScreenFade fade;

        [Header("개발 확인용 고정 도전 (우클릭 메뉴로 예제 설정)")]
        [Tooltip("켜면 아래 시작·목표로 시작합니다(개발 확인용). 끄면 씬에 들어올 때마다 시작·목표를 무작위로 뽑습니다.")]
        [SerializeField] bool fixedChallenge;
        [Tooltip("고정 도전의 시작 표제어. 그 표제어의 방 중앙에서 시작합니다.")]
        [SerializeField] string startWord = ShelfExample.SeaWord;
        [Tooltip("고정 도전의 목표 표제어. 링크를 눌러 이 표제어의 방에 도착하면 완주입니다. 목표 방은 출구가 없어도 도착할 수 있습니다.")]
        [SerializeField] string goalWord = ShelfExample.EarthWord;

        [Header("링크 후보")]
        [Tooltip("링크 후보로 쓸 빈도 상위 표제어 수(사전 패키지 순위 기준)")]
        [Min(1)] [SerializeField] int vocabularyLimit = 15000;
        [Tooltip("링크 후보 표제어의 최소 글자 수(GetWordLength). 미만인 단어는 일반 글로 둡니다.")]
        [Min(1)] [SerializeField] int doorMinLength = 2;

        [Header("사전 방 설정")]
        [Tooltip("뜻별 이동 링크 선정. senseId가 있으면 그 뜻에만, 없으면 그 표제어에서 자기 설정이 없는 모든 뜻에 적용합니다. 목록에 없는 뜻은 링크 후보 전부를 씁니다.")]
        [SerializeField] List<ShelfRoom> rooms = ShelfExample.Rooms();
        [Tooltip("독서대 뜻 표제(선택). 그 senseId의 독서대에만 label을 붙입니다. 없는 독서대는 그 뜻풀이 끝부분을 씁니다.")]
        [SerializeField] List<ShelfLecternLabel> lecternLabels = ShelfExample.Labels();

        [Header("읽기·메뉴·결과")]
        [Tooltip("독서대 자료를 펼치는 읽기 화면. 비어 있으면 같은 오브젝트에서 찾고, 없으면 시작하지 않습니다.")]
        [SerializeField] ShelfReader reader;
        [Tooltip("Esc 메뉴(재시작 = 이어서 하기). 비어 있으면 같은 오브젝트에서 찾고, 없으면 Esc는 커서 해제만 합니다.")]
        [SerializeField] ShelfPauseMenu pauseMenu;
        [Tooltip("목표 도달 결과 화면(다시 도전). 비어 있으면 같은 오브젝트에서 찾고, 없으면 화면 아래 안내만 남습니다.")]
        [SerializeField] ShelfResultScreen resultScreen;

        [Header("배치·표시")]
        [SerializeField] ShelfLayout layout = new ShelfLayout();
        [Tooltip("화면 정중앙 조준점(탐색 중에만 표시). 비어 있으면 같은 오브젝트에서 찾습니다.")]
        [SerializeField] ShelfCrosshair crosshair;

        readonly Dictionary<string, ShelfWordRoom> wordRooms = new Dictionary<string, ShelfWordRoom>();   // surface → room, or null
        readonly List<string> pathWords = new List<string>();    // headwords in order (HUD 경로)
        ShelfWordRoom startRoom;
        double runStart;                                         // realtime when the run first became playable
        bool timerStarted;                                       // runStart is set (once per run)
        bool transitioning;                                      // a move or a retry runs (input locked)
        bool wasExploring;                                       // Exploring as of the last LateUpdate
        float relockUntil;                                       // unscaled time until which the cursor is locked again
        bool relockWhileEscapeHeld;                              // the relock also lasts while that Escape is held
        WordRoomDataset dataset;
        Task<Setup> graphTask;                   // the one running computation of this component (null: none)
        CancellationTokenSource graphCancel;
        ShelfLinkGraph graph;
        int moves;
        bool reached;
        bool running;
        bool stopped;
        string goalSurface;
        string startSurface;                                     // the challenge's start headword (random or fixed)

        /// <summary>The link graph and the challenge decided on the worker thread.</summary>
        sealed class Setup
        {
            public ShelfLinkGraph Graph;
            public bool Random;          // drawn (false: the fixed challenge)
            public bool Picked;          // a random challenge was found
            public string Start;
            public string Goal;
            public int Tried;            // goals looked at by the draw
            public int Moves;            // shortest distance of the drawn start to the goal
            public int Seed;
        }

        public string CurrentWord => roomView != null && roomView.Room != null ? roomView.Room.Word : null;
        public int Moves => moves;
        public bool Reached => reached;
        /// <summary>A room transition (move or retry) runs: input is locked and the screen fades.</summary>
        public bool IsTransitioning => transitioning;

        void Reset()
        {
            ApplyEarthExample();
            fixedChallenge = false;                              // a new component draws random challenges
        }

        [ContextMenu("예제 1 설정: 항구 → 바다")]
        void ApplyHarborExample() => ApplyExample(ShelfExample.HarborWord, ShelfExample.SeaWord);

        /// <summary>바다's document links 지구 at its first place: one click.</summary>
        [ContextMenu("예제 2 설정: 바다 → 지구")]
        void ApplyEarthExample() => ApplyExample(ShelfExample.SeaWord, ShelfExample.EarthWord);

        /// <summary>"…70%가 바다이다." on the 지구 room's 행성 lectern: 바다 is a link, 이다 stays plain.</summary>
        [ContextMenu("예제 3 설정: 지구 → 바다")]
        void ApplyPlanetExample() => ApplyExample(ShelfExample.EarthWord, ShelfExample.SeaWord);

        /// <summary>"…에도 시대에 집대성하였다.": the noun 에도, spelled like the ending 에도, is a link.</summary>
        [ContextMenu("예제 4 설정: 가부키 → 에도")]
        void ApplyEdoExample() => ApplyExample("가부키", "에도");

        /// <summary>해안 "바다와 육지가 맞닿은 부분." → 바다 "…육지 면적의 …" → 면적: 2 moves.</summary>
        [ContextMenu("예제 5 설정: 해안 → 면적")]
        void ApplyCoastExample() => ApplyExample("해안", "면적");

        void ApplyExample(string fromWord, string toWord)
        {
#if UNITY_EDITOR
            UnityEditor.Undo.RecordObject(this, "서가 시안 예제 설정");
#endif
            fixedChallenge = true;                               // the examples are fixed challenges (development check)
            startWord = fromWord;
            goalWord = toWord;
            rooms = ShelfExample.Rooms();
            lecternLabels = ShelfExample.Labels();
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }

        void Awake()
        {
            if (data == null) data = GetComponent<WordRoomDataService>();
            if (reader == null) reader = GetComponent<ShelfReader>();
            if (pauseMenu == null) pauseMenu = GetComponent<ShelfPauseMenu>();
            if (resultScreen == null) resultScreen = GetComponent<ShelfResultScreen>();
            if (crosshair == null) crosshair = GetComponent<ShelfCrosshair>();
        }

        void OnEnable()
        {
            if (resultScreen != null) resultScreen.RetryRequested += Retry;
            if (reader != null) reader.LinkClicked += OnLinkClicked;
        }

        void Start()
        {
            if (data == null) Stop("WordRoomDataService 참조가 비어 있습니다");
            else if (player == null) Stop("FirstPersonPlayer 참조가 비어 있습니다");
            else if (reader == null) Stop("읽기 화면(ShelfReader) 참조가 비어 있습니다");
            else if (roomView == null || !roomView.IsComplete) Stop("독서실(ShelfRoomView) 참조나 그 floor·furniture 참조가 비어 있습니다");
            else if (deskPrefab == null || !deskPrefab.IsComplete) Stop("책상 프리팹(ShelfDesk) 참조나 그 model·top·body 참조가 비어 있습니다");
            else if (lecternPrefab == null || !lecternPrefab.IsComplete) Stop("독서대 프리팹(ShelfLectern) 참조나 그 bookAnchor 참조가 비어 있습니다");
            else if (!BooksComplete()) Stop("책 프리팹(ShelfBook) 참조가 비어 있거나 그 조준 Collider 참조가 비어 있습니다");
            else
            {
                if (fade == null) Debug.LogWarning("[서가 시안] 암전 화면(ShelfScreenFade) 참조가 비어 있어 방을 옮길 때 암전 없이 바로 바꿉니다.", this);
                SetHint("사전 불러오는 중…");
            }
        }

        /// <summary>
        /// Leaving the scene, destroying the object or turning the component off cancels a running link computation and
        /// drops it, so its result can never furnish a room. Turned on again before the start room exists, Update
        /// starts a new computation with the current settings. A transition in progress stops with the component
        /// (its coroutine ends): the blackout is cleared and the locks it took are released.
        /// </summary>
        void OnDisable()
        {
            if (resultScreen != null) resultScreen.RetryRequested -= Retry;
            if (reader != null) reader.LinkClicked -= OnLinkClicked;
            if (transitioning) AbortTransition();
            if (crosshair != null) crosshair.Show(false);
            wasExploring = false;
            if (graphTask == null) return;
            CancelGraph();
            if (!stopped) SetHint(null);
            Debug.Log("[서가 시안] 링크 연결 계산을 취소했습니다. 컴포넌트를 다시 켜면 현재 설정으로 다시 계산합니다.", this);
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
            if (transitioning) return;                           // locked: no aim, no E, no timer start in the dark
            TryStartTimer();                                     // first frame the player can really play
            UpdateAim();
        }

        /// <summary>
        /// Free exploration: the run goes on with no transition and no reading screen, Escape menu or result screen over
        /// the room. Those screens own the mouse (cursor free); exploration owns it locked and hidden.
        /// </summary>
        bool Exploring => running && !stopped && !transitioning && !reader.IsOpen &&
                          (pauseMenu == null || !pauseMenu.IsOpen) && (resultScreen == null || !resultScreen.IsShown);

        /// <summary>
        /// After every Update (the reading screen, the menus and the transition have settled the frame): the crosshair
        /// shows exactly while exploring, and when exploration comes back — a book closed with Escape or its button, a
        /// move finished, the menu or the result closed — the cursor is locked and hidden for RelockSeconds (and while
        /// the Escape that closed a screen is still held), so mouse look works at once without a click even when the
        /// player's own one-time relock is missed or the Editor resets the cursor on Escape. Outside that window a cursor
        /// freed on purpose stays free.
        /// </summary>
        void LateUpdate()
        {
            var exploring = Exploring;
            var escapeHeld = EscapeHeld();
            if (exploring && !wasExploring)
            {
                relockUntil = Time.unscaledTime + RelockSeconds;
                relockWhileEscapeHeld = escapeHeld;              // the Escape that closed a screen, until released
            }
            if (!escapeHeld) relockWhileEscapeHeld = false;
            wasExploring = exploring;
            if (exploring && (Time.unscaledTime < relockUntil || relockWhileEscapeHeld)) player.TryLockCursor();
            if (crosshair != null) crosshair.Show(exploring && player.Placed);
        }

        static bool EscapeHeld() => UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.escapeKey.isPressed;

        /// <summary>
        /// Snapshots the settings and starts the link graph and the challenge choice on a worker thread: a random start
        /// and goal (each entry into the scene draws anew), or the fixed development challenge, whose goal is checked here.
        /// </summary>
        void Begin(WordRoomDataset d)
        {
            dataset = d;
            string gw = null;
            if (fixedChallenge)
            {
                if (string.IsNullOrWhiteSpace(goalWord) || !d.TryGetWordRank(goalWord.Trim(), out var goalRank))
                {
                    Stop($"목표 표제어 '{goalWord}'가 사전 패키지에 없습니다");
                    return;
                }
                gw = d.GetWord(goalRank);
            }
            ValidateLabels();

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
            int limit = vocabularyLimit, minLength = doorMinLength;
            var seed = System.Guid.NewGuid().GetHashCode();     // a new draw on every entry into the scene
            CancelGraph();                           // never two computations for one component
            graphCancel = new CancellationTokenSource();
            var token = graphCancel.Token;
            graphTask = Task.Run(() =>
            {
                var setup = new Setup { Graph = new ShelfLinkGraph(d, limit, minLength, rules, token), Random = gw == null, Seed = seed };
                if (gw != null)
                {
                    setup.Graph.SetGoal(gw);                     // a non-candidate goal is reported in Ready
                    setup.Goal = gw;
                }
                else
                {
                    setup.Picked = setup.Graph.PickChallenge(new System.Random(seed), ChallengeMinMoves, token,
                        out setup.Goal, out setup.Start, out setup.Moves, out setup.Tried);
                }
                return setup;
            }, token);
            graphTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);   // a dropped task's failure is observed
            SetHint(gw == null ? "링크 연결 계산·도전 선택 중…" : "링크 연결 계산 중…");
        }

        /// <summary>
        /// The link graph and the challenge are done: a random challenge needs a goal some room reaches (else the run
        /// stops with the reason); the fixed one checks the goal can be reached by a link. Then the start room must have
        /// an exit, and it is furnished and the player placed. Only the current computation gets here (a cancelled one
        /// was dropped).
        /// </summary>
        void Ready(Task<Setup> task)
        {
            graphCancel?.Dispose();
            graphCancel = null;
            graphTask = null;
            if (task.IsCanceled)
            {
                Debug.Log("[서가 시안] 링크 연결 계산이 취소됐습니다.", this);
                SetHint(null);
                return;
            }
            if (task.IsFaulted)
            {
                Stop($"링크 연결 계산 실패: {task.Exception?.GetBaseException().Message}");
                return;
            }
            var setup = task.Result;
            graph = setup.Graph;
            foreach (var problem in graph.ExceptionProblems())
                Debug.LogWarning($"[서가 시안] 분리 예외 '{problem}'는 표제어가 어절 앞부분이 아니거나 사전에 없어 적용하지 않습니다.", this);

            if (setup.Random)
            {
                if (!setup.Picked)
                {
                    Stop($"최단 이동 {ChallengeMinMoves}회 이상인 시작·목표 조합이 없습니다 (목표 후보 {setup.Tried:N0}개 확인)");
                    return;
                }
                startSurface = setup.Start;
                goalSurface = setup.Goal;
                Debug.Log($"[서가 시안] 무작위 도전: {startSurface} → {goalSurface} · 최단 이동 {setup.Moves}회 " +
                          $"(시드 {setup.Seed} · 목표 {setup.Tried:N0}개째 선택 · 이 목표에 도달하는 방 {graph.ReachingCount:N0}개 중 {ChallengeMinMoves}회 이상에서 추첨)", this);
            }
            else
            {
                goalSurface = setup.Goal;
                var why = graph.WhyNotCandidate(goalSurface);
                if (why != null)
                {
                    Stop($"목표 표제어 '{goalSurface}'는 링크 후보가 아니어서({why}) 링크로 도착할 수 없습니다");
                    return;
                }
                startSurface = string.IsNullOrWhiteSpace(startWord) ? null : startWord.Trim();
            }
            Debug.Log($"[서가 시안] 링크 연결 계산: 표제어 방 후보 {graph.RoomCount:N0}개 중 출구 없는 방 {graph.RemovedCount:N0}개 제외 " +
                      $"(상위 {vocabularyLimit:N0}개 · {doorMinLength}글자 이상 · 목표 {goalSurface} 유지)", this);

            var start = startSurface == null ? null : RoomOf(startSurface);
            if (start == null)
            {
                Stop($"시작 표제어 '{startSurface}'의 뜻풀이를 사전 패키지에서 찾지 못했습니다");
                return;
            }
            if (!setup.Random && graph.DistanceToGoal(start.Word) < 0)
                Debug.LogWarning($"[서가 시안] 고정 도전: 시작 방 {start.Word}에서 링크를 따라 목표 {goalSurface}에 도착할 수 없습니다(개발 확인용이라 그대로 시작합니다).", this);
            if (IsGoal(start.Word))
            {
                Stop("시작 방이 이미 목표입니다");
                return;
            }
            if (start.ExitCount == 0)
            {
                Stop($"시작 방 {start.Word}의 자료에 이동할 수 있는 링크가 없습니다.");   // before the room is furnished
                return;
            }

            startRoom = start;
            if (!BeginRun(out var failure))
            {
                Stop($"시작 방에 플레이어를 배치하지 못했습니다: {failure}");
                return;
            }
            running = true;
            Debug.Log($"[서가 시안] 시작: {start.Word} → 목표 {goalSurface} · {Describe(start)}", this);
        }

        /// <summary>The first run of this challenge: the start room placed (PlaceStart), then the run state set (CommitRun).</summary>
        bool BeginRun(out string failure)
        {
            if (!PlaceStart(out failure, out _)) return false;
            CommitRun();
            return true;
        }

        /// <summary>
        /// Furnishes the start room from the kept room data (same lecterns and links) and places the player at its
        /// center facing side 0. The run's moves, path, goal state and timer are not touched. `playerTouched` tells
        /// whether PlaceAt was tried: it suspends the player when it fails; the earlier steps leave the player as it was.
        /// </summary>
        bool PlaceStart(out string failure, out bool playerTouched)
        {
            playerTouched = false;
            if (!roomView.Furnish(startRoom, deskPrefab, lecternPrefab, bookPrefabs, layout, out failure) || !SafeCenter(out failure)) return false;
            playerTouched = true;
            return player.PlaceAt(roomView.Floor, out failure);
        }

        /// <summary>
        /// The new run is decided (only after the start room is placed): 0 moves, a path of the start room only, goal not
        /// reached and the timer reset to not started. It starts once the player can really play (TryStartTimer).
        /// </summary>
        void CommitRun()
        {
            timerStarted = false;                                // never the last run's start time
            runStart = 0d;
            moves = 0;
            reached = false;
            pathWords.Clear();
            pathWords.Add(startRoom.Word);
            TryStartTimer();                                     // playable right away: starts now
            SetHint(null);
            RefreshStatus();
        }

        /// <summary>
        /// Fixes the run's start time once, at the first moment FirstPersonPlayer.CanTakeInput is true (placed, window
        /// focused, no menu block and not in the frame it was lifted, walking not locked, cursor locked). Dictionary
        /// loading, the link graph and an Escape menu kept open over the placement are therefore not counted; once
        /// started, time in the Escape menu, on the reading screen and in room transitions counts (the start time is
        /// never moved).
        /// </summary>
        void TryStartTimer()
        {
            if (timerStarted || reached || !player.CanTakeInput) return;
            runStart = Time.realtimeSinceStartupAsDouble;
            timerStarted = true;
        }

        /// <summary>
        /// The goal was entered: time (fixed at the goal entry), moves and path are recorded once, the challenge's personal
        /// bests (reading-room version key) updated and saved, and the result screen shown (the Escape menu is closed and
        /// off meanwhile). Without a usable result screen the success hint stays on screen and the menu comes back.
        /// </summary>
        void Finish(double seconds)
        {
            var challenge = ShelfRecords.ReadingKey(startRoom.Word, goalSurface);
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
        /// 다시 도전: the same challenge from the start in this scene (no scene load) and in the same room model, behind
        /// the blackout. Only once the start room is furnished and the player placed at its center is the result closed
        /// and the new run decided (0 moves; the timer starts when the player can play again); the dictionary, the link
        /// graph and the saved bests are reused. When the placement fails, the finished room's furniture comes back (and
        /// the player where it stood, if it was moved), the blackout clears and the same result stays — time, moves, path
        /// and bests untouched, input still blocked and the cursor free — with the reason and 다시 도전 usable again.
        /// Taken once per press (the button is disabled at the press and only a finished run can be retried).
        /// </summary>
        void Retry()
        {
            if (!running || !reached || transitioning) return;
            StartCoroutine(RetryRoutine());
        }

        IEnumerator RetryRoutine()
        {
            var finished = roomView.Room;
            var keepPosition = player.transform.position;
            var keepRotation = player.transform.rotation;
            Lock();
            yield return FadeOut();
            if (!PlaceStart(out var failure, out var playerTouched))
            {
                var restored = roomView.Furnish(finished, deskPrefab, lecternPrefab, bookPrefabs, layout, out var restoreWhy);
                if (restored && playerTouched)
                {
                    restored = player.PlaceAt(roomView.Floor, out restoreWhy) && player.MoveOnto(roomView.transform, keepPosition, out restoreWhy);
                    if (restored) player.transform.rotation = keepRotation;
                }
                if (!restored)
                {
                    AbortTransition();
                    Stop($"다시 도전 실패 후 완료한 방 {finished.Word}을(를) 되돌리지 못했습니다: {restoreWhy}");
                    yield break;
                }
                Debug.LogWarning($"[서가 시안] 다시 도전하지 않습니다: 시작 방 {startRoom.Word}을(를) 준비하지 못했습니다({failure}). 결과·기록은 그대로입니다.", this);
                yield return FadeIn();
                transitioning = false;                           // the result keeps the input block; no Escape menu over it
                reader.SetLocked(false);
                if (resultScreen != null) resultScreen.ShowRetryFailure($"다시 도전하지 못했습니다 — {failure}");
                yield break;
            }
            if (resultScreen != null) resultScreen.Hide();
            reader.Close();
            player.SetInputBlocked(true);                        // Hide gave input back; keep it until the room shows
            CommitRun();
            Debug.Log($"[서가 시안] 다시 도전: {startRoom.Word} → 목표 {goalSurface} · 독서대 {roomView.Lecterns.Count}개 · 책상 {roomView.ActiveDeskCount}개", this);
            yield return Hold();
            yield return FadeIn();
            Unlock();
        }

        // ---- lecterns and reading ----------------------------------------------------------------------------------

        bool BooksComplete()
        {
            if (bookPrefabs == null || bookPrefabs.Length == 0) return false;
            foreach (var b in bookPrefabs)
                if (b == null || !b.IsComplete) return false;
            return true;
        }

        /// <summary>
        /// Exactly "[E] 읽기" while the screen-center ray finds an open book of the room within the interact distance and
        /// the player stands in its stand's reading area; hidden otherwise. Nothing of the document shows before E: E
        /// opens the aimed book's document and its links on the reading screen.
        /// </summary>
        void UpdateAim()
        {
            if (reached)
            {
                if (resultScreen != null && resultScreen.IsShown) SetHint(null);
                else SetHint($"목표 도달 성공 · {startRoom.Word} → {CurrentWord} · 이동 {moves}회");
                return;
            }
            ShelfLectern lectern = null;
            if (reader.IsOpen || !player.CanInteract || !player.TryGetAimed(layout.interactDistance, out ShelfBook book) ||
                (lectern = book.Holder) == null || lectern.Book != book || lectern.Document == null ||
                !lectern.transform.IsChildOf(roomView.transform) || !lectern.IsInReach(player.transform.position, layout.readRange))
            {
                SetHint(null);
                return;
            }
            SetHint(ReadHint);
            if (!player.InteractPressed) return;
            if (reader.Open(lectern.Document))
            {
                SetHint(null);
                Debug.Log($"[서가 시안] 읽기: {CurrentWord}의 독서대 {lectern.Index + 1} '{lectern.Document.Title}' " +
                          $"(항목 {lectern.Document.EntryId} · 뜻 {lectern.Document.SenseId} · 링크 {lectern.Document.LinkCount}개)", this);
            }
            else Debug.LogWarning("[서가 시안] 읽기 화면을 열지 못했습니다.", this);
        }

        /// <summary>A link of the open document was clicked: one move at most, never while another runs or after the goal.</summary>
        void OnLinkClicked(string target)
        {
            if (reader == null || !reader.IsOpen) return;
            TryTravel(target);
        }

        /// <summary>
        /// Starts the move to `headword`'s room through the blackout (a link click now; later the destination a co-op
        /// host confirmed, which goes through the same flow). The destination room data is checked before anything is
        /// locked: false when no run is going on, a transition runs, the goal was reached, or the destination is
        /// refused (the reason is shown on the reading screen when it is open, and nothing changes).
        /// </summary>
        public bool TryTravel(string headword)
        {
            if (!running || reached || transitioning || stopped || string.IsNullOrWhiteSpace(headword) || roomView.Room == null) return false;
            var room = RoomOf(headword.Trim());
            if (room == null)
            {
                Fail(headword, $"'{headword}' 방의 뜻풀이를 사전 패키지에서 찾지 못했습니다");
                return false;
            }
            if (room.Documents.Count == 0 && !IsGoal(room.Word))
            {
                Fail(headword, $"'{room.Word}' 방에 이동 링크가 있는 자료가 없습니다");     // the graph never links such a room
                return false;
            }
            StartCoroutine(TravelRoutine(room));
            return true;
        }

        /// <summary>
        /// The one place where the current room, the path and the move count change. Order: lock input → fade out →
        /// room title, desks, lecterns and documents replaced → player placed at the center → (success) reading screen
        /// closed and the record updated once → fade in → input back. A failure in the dark furnishes the previous
        /// room again, puts the player back where it stood and keeps the reading screen and the record.
        /// </summary>
        IEnumerator TravelRoutine(ShelfWordRoom room)
        {
            var from = roomView.Room;
            var keepPosition = player.transform.position;
            var keepRotation = player.transform.rotation;
            Lock();
            yield return FadeOut();

            var safe = roomView.Furnish(room, deskPrefab, lecternPrefab, bookPrefabs, layout, out var why) && SafeCenter(out why);
            var moved = safe && player.PlaceAt(roomView.Floor, out why);
            if (!moved)
            {
                // The previous room's furniture comes back in the same places. The player was only touched when
                // PlaceAt itself failed (it suspends the player): then it is placed again and put back where it stood.
                var restored = roomView.Furnish(from, deskPrefab, lecternPrefab, bookPrefabs, layout, out var restoreWhy);
                if (restored && safe)
                {
                    restored = player.PlaceAt(roomView.Floor, out restoreWhy) && player.MoveOnto(roomView.transform, keepPosition, out restoreWhy);
                    if (restored) player.transform.rotation = keepRotation;
                }
                Fail(room.Word, $"'{room.Word}' 방을 준비하지 못했습니다: {why}");
                if (!restored)
                {
                    AbortTransition();
                    Stop($"이동 실패 후 이전 방 {from.Word}을(를) 되돌리지 못했습니다: {restoreWhy}");
                    yield break;
                }
                yield return FadeIn();
                Unlock();
                yield break;
            }

            reader.Close();
            player.SetInputBlocked(true);                        // Close gave input back; keep it until the fade in ends
            moves++;
            pathWords.Add(room.Word);
            reached = IsGoal(room.Word);
            var seconds = timerStarted ? Time.realtimeSinceStartupAsDouble - runStart : 0d;   // fixed at the goal entry
            Debug.Log($"[서가 시안] 이동: {from.Word} → {room.Word} (이동 {moves}회, 독서대 {room.Documents.Count}개 · 책상 {roomView.ActiveDeskCount}개, 출구 {room.ExitCount}개)" +
                      $"{(reached ? " · 목표 도달 성공" : "")} · {Describe(room)}", this);
            SetHint(null);
            RefreshStatus();
            yield return Hold();
            yield return FadeIn();
            Unlock();
            if (reached) Finish(seconds);                        // once: a reached run takes no more links
        }

        /// <summary>Input locked for a transition: reading screen controls, Escape menu, walking and look.</summary>
        void Lock()
        {
            transitioning = true;
            SetHint(null);
            reader.SetLocked(true);
            if (pauseMenu != null)
            {
                pauseMenu.Close();
                pauseMenu.enabled = false;
            }
            player.SetInputBlocked(true);
        }

        /// <summary>Input back: the reading screen keeps the block while it is still open (a failed move).</summary>
        void Unlock()
        {
            transitioning = false;
            reader.SetLocked(false);
            if (!reader.IsOpen) player.SetInputBlocked(false);   // from the next frame; the cursor locks again then
            if (pauseMenu != null && !reached) pauseMenu.enabled = true;
        }

        /// <summary>A transition ended early (component off, run stopped): no blackout left, locks released.</summary>
        void AbortTransition()
        {
            StopAllCoroutines();
            if (fade != null) fade.Clear();
            transitioning = false;
            if (reader != null)
            {
                reader.SetLocked(false);
                if (!reader.IsOpen && player != null) player.SetInputBlocked(false);
            }
        }

        IEnumerator FadeOut()
        {
            if (fade != null) yield return fade.FadeOut();
        }

        IEnumerator FadeIn()
        {
            if (fade != null) yield return fade.FadeIn();
        }

        IEnumerator Hold()
        {
            if (fade != null && fade.HoldSeconds > 0f) yield return new WaitForSecondsRealtime(fade.HoldSeconds);
        }

        void Fail(string target, string reason)
        {
            Debug.LogWarning($"[서가 시안] 이동하지 않습니다({CurrentWord} → {target}): {reason}. 현재 방·읽기 화면·이동 기록은 그대로입니다.", this);
            if (reader != null) reader.ShowNotice($"이동하지 못했습니다 — {reason}");
        }

        /// <summary>
        /// The room center is a safe place for the player: the first floor below it (from body height, the player's own
        /// colliders skipped, as FirstPersonPlayer.PlaceAt looks) is the room's floor, and a body there touches nothing
        /// else (no desk or lectern). Checked before the player is moved, so a failure leaves the player as it was.
        /// </summary>
        bool SafeCenter(out string failure)
        {
            Physics.SyncTransforms();
            var body = player.GetComponent<CharacterController>();
            var height = body != null ? body.height : 1.8f;
            var radius = body != null ? body.radius : 0.3f;
            var floorRoot = roomView.Floor;
            var center = floorRoot.position;
            RaycastHit? floor = null;
            foreach (var h in Physics.RaycastAll(center + Vector3.up * height, Vector3.down, height * 2f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                if (!h.collider.transform.IsChildOf(player.transform) && (floor == null || h.distance < floor.Value.distance)) floor = h;
            if (floor == null || !floor.Value.collider.transform.IsChildOf(floorRoot))
            {
                failure = floor == null ? "중앙 아래에 바닥이 없습니다" : $"중앙 아래 첫 Collider '{floor.Value.collider.name}'가 이 방의 바닥이 아닙니다";
                return false;
            }
            var feet = floor.Value.point;
            var bottom = feet + Vector3.up * (radius + 0.1f);
            var top = feet + Vector3.up * Mathf.Max(radius + 0.1f, height - radius);
            foreach (var c in Physics.OverlapCapsule(bottom, top, radius, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                if (!c.transform.IsChildOf(player.transform) && !c.transform.IsChildOf(floorRoot))
                {
                    failure = $"중앙에 '{c.name}'가 있습니다";
                    return false;
                }
            failure = null;
            return true;
        }

        /// <summary>The goal is a headword: arriving in its room finishes the run.</summary>
        bool IsGoal(string word) => word == goalSurface;

        // ---- dictionary --------------------------------------------------------------------------------------------

        /// <summary>
        /// The reading room of a headword, resolved once (null, kept, when the word or its definitions are missing). The
        /// senses with a definition are read by dictionary entry, in order; each sense is numbered within its entry first
        /// (when the entry has two or more), then gets its own document and lectern with its own definition and links —
        /// the link graph's final links of that sense (ShelfLinkGraph.Doors, the same result the room exits come from).
        /// A sense without a link gets no lectern; the others keep their numbers and order (2 dropped: 3 stays 3). The
        /// room's exits are the links of all of its documents, and its size and lectern places come from the documents left.
        /// A room may end up with no document: the start check (no exit) then stops the run, a link never leads to such
        /// a room unless it is the goal (the graph keeps only rooms with an exit, and the goal), and the goal room is
        /// built without lecterns. Every other part of a definition, a dropped link included, stays plain text.
        /// </summary>
        ShelfWordRoom RoomOf(string word)
        {
            if (wordRooms.TryGetValue(word, out var known)) return known;
            ShelfWordRoom room = null;
            if (!dataset.TryGetWordRank(word, out var rank)) Debug.LogWarning($"[서가 시안] 표제어 '{word}'가 사전 패키지에 없습니다.", this);
            else
            {
                var surface = dataset.GetWord(rank);
                if (wordRooms.TryGetValue(surface, out known)) room = known;
                else
                {
                    var documents = new List<ShelfDocument>();
                    foreach (var group in graph.EntryGroups(rank))
                    {
                        for (var i = 0; i < group.Count; i++)
                        {
                            var number = group.Count > 1 ? i + 1 : 0;           // fixed before the link filter below
                            var row = group[i];
                            var info = dataset.GetSense(row);
                            var searched = ShelfLinkText.DisplayDefinition(info.Definition);   // the same text the graph searched
                            ReportDropped(surface, info, searched);
                            // "‘「1」’을 세는 단위" → "사람을 세는 단위": shown text only, the graph's links moved along.
                            var text = ShelfLinkText.ResolveSelfReferences(searched, surface, number, group.Count, graph.Doors(row), out var doors);
                            if (!ReferenceEquals(text, searched))
                                Debug.Log($"[서가 시안] {surface} {number}번 뜻({info.SenseId})의 같은 항목 뜻 번호 참조를 표제어로 풀어 씁니다: '{text}'", this);
                            var sense = new ShelfSense(info.SenseId, text, ShelfLinkText.Split(text, doors));
                            var links = 0;
                            foreach (var part in sense.Parts)
                                if (part.IsLink) links++;
                            if (links == 0)
                            {
                                Debug.Log($"[서가 시안] {surface} 항목 {info.EntryId}{(number > 0 ? $" {number}번" : "")} 뜻({info.SenseId})은 이동 링크가 없어 독서대를 두지 않습니다.", this);
                                continue;
                            }
                            documents.Add(new ShelfDocument(surface, info.EntryId, info.Pos, number, LabelOf(surface, info.SenseId) ?? ShortLabel(text), sense));
                        }
                    }
                    if (graph.EntryGroups(rank).Count == 0) Debug.LogWarning($"[서가 시안] 표제어 '{surface}'에 뜻풀이가 있는 뜻이 없습니다.", this);
                    else room = new ShelfWordRoom(surface, documents);   // no document: the goal only (see above)
                    wordRooms[surface] = room;
                }
            }
            wordRooms[word] = room;
            return room;
        }

        /// <summary>Logs the links of a sense that stay plain text: selected words that are no link, and links whose room was dropped.</summary>
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
                Debug.Log($"[서가 시안] {surface}({sense.SenseId}): 도착한 방에서 나갈 링크가 없어 일반 글로 두는 링크 {string.Join(", ", blocked)}", this);
            if (selection == null) return;
            foreach (var s in selection)
                if (!present.Contains(s))
                    Debug.Log($"[서가 시안] {surface}({sense.SenseId}) 선정 링크 '{s}'는 링크 후보가 아니어서({graph.WhyNotCandidate(s) ?? "뜻풀이에 나오지 않음"}) 일반 글로 둡니다.", this);
        }

        static HashSet<string> Selection(ShelfRoom settings)
        {
            if (settings?.links == null) return null;
            var set = new HashSet<string>();
            foreach (var l in settings.links)
                if (!string.IsNullOrWhiteSpace(l)) set.Add(l.Trim());
            return set.Count > 0 ? set : null;
        }

        // ---- lectern titles ----------------------------------------------------------------------------------------

        /// <summary>The configured title of the lectern of this sense (a label setting naming exactly this senseId).</summary>
        string LabelOf(string surface, string senseId)
        {
            if (lecternLabels == null) return null;
            foreach (var l in lecternLabels)
            {
                if (l == null || l.word == null || l.word.Trim() != surface || string.IsNullOrWhiteSpace(l.label) || l.senseId == null) continue;
                if (l.senseId.Trim() == senseId) return l.label.Trim();
            }
            return null;
        }

        /// <summary>
        /// A short lectern title from a definition: its first sentence when short, else its last eojeols after "…" (Korean
        /// definitions end on the noun they define: "…가까운 행성", "…땅의 한 구획").
        /// </summary>
        static string ShortLabel(string definition)
        {
            var s = (definition ?? "").Trim();
            var dot = s.IndexOf('.');
            if (dot > 0) s = s.Substring(0, dot).Trim();
            if (s.Length <= LabelWhole) return s;
            var words = s.Split(' ');
            var tail = words[words.Length - 1];
            for (var i = words.Length - 2; i >= 0 && tail.Length + 1 + words[i].Length <= LabelTail; i--) tail = words[i] + " " + tail;
            if (tail.Length > LabelTail + 2) tail = tail.Substring(tail.Length - LabelTail);
            return "…" + tail;
        }

        /// <summary>Checks the optional title settings once at start: each senseId must be a sense of its word.</summary>
        void ValidateLabels()
        {
            if (lecternLabels == null) return;
            foreach (var l in lecternLabels)
            {
                if (l == null || string.IsNullOrWhiteSpace(l.word) || string.IsNullOrWhiteSpace(l.senseId)) continue;
                var word = l.word.Trim();
                if (!dataset.TryGetWordRank(word, out var rank))
                {
                    Debug.LogWarning($"[서가 시안] 독서대 표제 설정의 표제어 '{word}'가 사전 패키지에 없어 무시합니다.", this);
                    continue;
                }
                if (!dataset.TryGetSenseRow(l.senseId.Trim(), out var row) || dataset.WordOfSense(row) != rank)
                    Debug.LogWarning($"[서가 시안] 독서대 표제 설정의 senseId '{l.senseId.Trim()}'는 '{word}'의 뜻이 아니어서 무시합니다.", this);
            }
        }

        static string Describe(ShelfWordRoom room)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < room.Documents.Count; i++)
            {
                var doc = room.Documents[i];
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(i + 1).Append(' ').Append(doc.Title).Append('[').Append(doc.EntryId).Append('/').Append(doc.SenseId).Append("] 링크 ");
                var links = new List<string>();
                foreach (var p in doc.Sense.Parts)
                    if (p.IsLink) links.Add(p.Target);
                sb.Append(links.Count > 0 ? string.Join(", ", links) : "없음");
            }
            return sb.ToString();
        }

        // ---- display -----------------------------------------------------------------------------------------------

        void RefreshStatus()
        {
            if (status == null) return;
            var sb = new StringBuilder();
            sb.Append("시작  ").Append(startRoom.Word).Append('\n');
            sb.Append("목표  ").Append(goalSurface).Append('\n');
            sb.Append("현재  ").Append(CurrentWord);            // moves: result screen; path: console log
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
            if (crosshair != null) crosshair.Show(false);
            enabled = false;
        }
    }
}
