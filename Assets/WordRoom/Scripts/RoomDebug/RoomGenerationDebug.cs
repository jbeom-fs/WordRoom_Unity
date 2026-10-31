using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using WordRoom.Data;
using WordRoom.Generation;
using WordRoom.Settings;
using WordRoom.UnityLayer;
using WordRoom.World;

namespace WordRoom.RoomDebug
{
    public enum GuidanceBasisOption
    {
        고유방문방수,
        누적이동,
    }

    /// <summary>
    /// Single-player front end of a session: Inspector settings, arrow keys (↑ 상, ↓ 하, ← 좌, → 우), Console output,
    /// and the local display (3D view, HUD, hint, explored map, first-person player). Each experiment is one
    /// SessionCoordinator in solo mode with one registered player (LocalPlayer); every logical change — start, door
    /// opening (Prepare), entry (Enter), arrow-key Move — is a request to it, and every display copy (3D snapshot,
    /// map change, log text) is captured inside that request on the worker. The main thread never reads the
    /// session; it shows the copies and keeps only local state (player, input locks, menus, HUD words, shown
    /// radius). Requests run one at a time; input is ignored (not queued) while data is loading or a request is
    /// running. The settings are copied when an experiment starts.
    /// Optional 3D view (RoomWorldView): after each finished request a snapshot around the local player's confirmed
    /// room is shown on the main thread; without it only Console output.
    /// Optional first-person player: placed on the start room floor once it is shown; while it is set and active,
    /// the arrow keys do not move the logical position and the debug camera is not moved (one rule, ActivePlayer,
    /// for placement, input, arrow keys and camera). The player opens doors of the current room with E: the door
    /// slides up locally while one Prepare request stores the connection Open and fills the neighbors of the room
    /// behind it (no move, no visit); the doorway opens when both are done. Walking through it is one Enter request
    /// (no fill), and walking stays locked during each request until its result is shown (and, for an entry, the
    /// player stands on the arrival floor). The arrow keys keep the combined Move. A player that is disconnected,
    /// replaced or disabled is stopped and only placed again by a new experiment.
    /// Two ways to start: the Inspector words (StartExperiment; automatically when startWhenDataReady, the
    /// development scene) or StartGame with words, seed and vocabulary chosen by the game (the menu flow, with
    /// automatic start off). The display radius (GameSettings.DisplayRadius) is read when each request starts; a
    /// change while idle re-captures the existing map on the worker and shows it again — never a Fill, Prepare,
    /// Move or Enter — and a change during a request is shown once that request finished (latest value only).
    /// </summary>
    [RequireComponent(typeof(WordRoomDataService))]
    public sealed class RoomGenerationDebug : MonoBehaviour
    {
        [Header("실험 (시작할 때 복사되어 고정됨)")]
        [SerializeField] string startWord = "커피";
        [SerializeField] string targetWord = "우유";
        [SerializeField] int seed = 42;
        [SerializeField] bool startWhenDataReady = true;

        [Header("어휘 범위 (Python 1.1.0 기본: 상위 15000, 1~8글자)")]
        [SerializeField] int vocabularyLimit = 15000;
        [SerializeField] int minWordLength = 1;
        [SerializeField] int maxWordLength = 8;

        [Header("목표 유도 (현재 기본: 켬, 0 → 0.09, 10부터 20에 걸쳐, 고유 방문 방 수 · Python 1.1.0 값 0.03·40에서 조정)")]
        [SerializeField] bool targetGuidance = true;
        [SerializeField] double guidanceStrength = 0.0;
        [SerializeField] int guidanceStart = 10;
        [SerializeField] int guidanceRamp = 20;
        [SerializeField] double guidanceMax = 0.09;
        [SerializeField] GuidanceBasisOption guidanceBasis = GuidanceBasisOption.고유방문방수;

        [Header("테스트 전용 이동 제한 (기본 꺼짐: 일반 플레이는 제한 없음)")]
        [Tooltip("켜면 누적 이동이 최대 이동 횟수에 도달한 이동을 마친 뒤 '테스트 이동 제한 도달'로 끝납니다. 같은 이동에서 목표에 도착하면 목표 도달이 우선. 끄면 이동 횟수로는 끝나지 않습니다(이동 횟수 기록은 계속).")]
        [SerializeField] bool useMoveLimit;
        [Tooltip("테스트 이동 제한을 켰을 때만 사용(1 이상). 제한이 꺼져 있으면 이 값은 사용되지 않습니다.")]
        [SerializeField] int maxMoves = 300;

        [Header("로그")]
        [Tooltip("생성 시간·관계 기준·보충 근거·실패 사유 등을 추가로 출력")]
        [SerializeField] bool verboseLog;

        [Header("3D 표시 (선택)")]
        [Tooltip("현재 방과 상·하·좌·우 방을 3D로 표시할 RoomWorldView. 비우면 같은 오브젝트의 것을 찾고, 없으면 Console 디버그만 합니다.")]
        [SerializeField] RoomWorldView worldView;

        [Header("1인칭 (선택)")]
        [Tooltip("연결하고 활성이면 시작 방 표시 후 이 플레이어를 시작 방 바닥에 배치하고, 확인용 카메라 자동 이동과 방향키 논리 이동을 하지 않습니다. 비우거나 비활성이면 기존 방향키·확인용 카메라 디버그. 실행 중 해제·교체·비활성화하면 이전 플레이어를 정지하고, 다시 배치는 새 실험에서 합니다.")]
        [SerializeField] FirstPersonPlayer player;
        [Tooltip("화면 안내(문 열기·목표 도달·실험 종료·진행 정지·재배치 필요). 진행 중 자막은 없습니다. 비우면 같은 오브젝트의 것을 찾고, 없으면 안내 없이 진행합니다.")]
        [SerializeField] PlayHint hint;
        [Tooltip("화면 좌상단 현재·목표 단어 HUD. 1인칭 플레이어가 배치된 동안만 보입니다. 비우면 같은 오브젝트의 것을 찾고, 없으면 HUD 없이 진행합니다.")]
        [SerializeField] WordRoomHud hud;
        [Tooltip("협동에서 다른 참가자의 로봇 외형을 표시합니다(표시 전용). 비우면 같은 오브젝트의 것을 찾고, 없으면 다른 참가자를 표시하지 않습니다.")]
        [SerializeField] RemotePlayers remotePlayers;

        [Tooltip("진행 정지·플레이어 미배치 안내 뒤에 붙는 다음 행동 안내. 메뉴로 실행하는 씬은 메인 메뉴 복귀를 안내합니다.")]
        [SerializeField] string haltAdvice = "새 실험을 시작하세요";

        const float DoorReach = 2f;          // E reaches doors up to this far from the eye along the screen center

        /// <summary>The one logical player of a single-player session (this device's player).</summary>
        public const string LocalPlayer = "local";

        WordRoomDataService service;
        SessionCoordinator session;          // the current experiment's authority; null while none is running
        WordRoomDataset dataset;
        long nextRequest;                    // request ids of this device (never reused within a session)
        GridPos expectedRoom;                // the local player's room from the last accepted change, sent as ExpectedFrom
        CancellationTokenSource cts;
        bool busy;
        bool waitingLogged;
        bool hintMissingLogged;
        bool hudMissingLogged;

        // First-person play of the current experiment (main thread only).
        FirstPersonPlayer controlled;        // the player placed for this experiment; null = nobody walks
        string status;                       // experiment status from the last finished request (copied there)
        string halt;                         // why play stopped after an error; null = not stopped
        Vector3 lastPosition;                // player position at the previous entry check
        string hudCurrent;                   // HUD words from the last confirmed placement or entry snapshot; null = none
        string hudTarget;
        int shownRadius = -1;                // display radius of the last snapshot shown in this experiment; -1 = none yet
        string haltHintFor, haltHintText;    // hint texts kept for the reason/status they were made from
        string endHintFor, endHintText;
        string unplacedHintText;
        bool goalReported;                   // this experiment's GoalReached was raised (at most once; the result itself is the session's)

        /// <summary>
        /// Raised once per experiment when a first-person entry into the target room is confirmed (entry accepted,
        /// view shown, player on the arrival floor; never for door opening, Prepare, arrow-key Moves, a return to the start
        /// room, refusals, duplicates, cancelled or stale results), with the session's fixed time and visit count. Only
        /// when the play clock had started. In coop: once the shown state holds the host's confirmed result — on every
        /// participant's device, whoever reached the goal, from the goal's change or a whole state (once per game).
        /// </summary>
        public event Action<GoalResult> GoalReached;

        /// <summary>Raised when a new experiment begins (previous results no longer apply).</summary>
        public event Action ExperimentStarted;

        /// <summary>Set by a result screen that shows the goal itself: the "목표 도달" hint is then not shown. Cleared by a new experiment.</summary>
        public bool SuppressGoalHint { get; set; }

        /// <summary>
        /// The end of a 끼임방지 request of this device: true once the body stands on the start room's floor (the request
        /// confirmed and carried out), false with why when it was refused, could not be sent, failed or could not be
        /// carried out (then play may also be stopped with the reason). Raised once per request.
        /// </summary>
        public event Action<bool, string> UnstuckFinished;

        bool unstuckRunning;                 // single play: this device's return request runs
        int soloReturns;                     // single play: the local player's confirmed returns, copied with each confirmed change (the session's count)

        /// <summary>This player has used its one 끼임방지 of this game (a confirmed return; the session's own count, not a UI flag).</summary>
        public bool UnstuckUsed => coopMode ? coopView != null && (coopView.FindPlayer(localPlayer)?.Returns ?? 0) > 0 : soloReturns > 0;

        // Explored map for a map screen (main thread only; filled from the finished requests' worker copies).
        readonly ExploredMap exploredMap = new ExploredMap();
        bool trackMap;
        bool mapResync;                      // the next request copies the whole map (after a gap or late tracking)
        bool mapRecovering;
        bool mapDesyncLogged;

        /// <summary>
        /// Every room generated in this experiment, the team's visits, and each player's confirmed room (Current =
        /// the local player's), for a map screen. Updated only from the changes of start, Prepare, Enter or Move
        /// requests, in revision order (never by looking at it); empty until the start finished and while
        /// TrackExploredMap is off.
        /// </summary>
        public ExploredMap ExploredMap => exploredMap;

        /// <summary>
        /// Copy map changes in each request (off by default: the development scene has no map). Turning it on during
        /// an experiment makes the next request copy the whole map.
        /// </summary>
        public bool TrackExploredMap
        {
            get => trackMap;
            set
            {
                if (value && !trackMap) mapResync = true;
                trackMap = value;
            }
        }

        /// <summary>A placed, connected, active player in a running or finished experiment that is not stopped: a map can be used.</summary>
        public bool CanShowMap => halt == null && (session != null || coopView != null) && controlled != null && controlled == player && controlled.Placed;

        /// <summary>The connected player when its component and object are active; null = arrow-key/camera debug.</summary>
        FirstPersonPlayer ActivePlayer => player != null && player.isActiveAndEnabled ? player : null;

        /// <summary>The connected first-person player (may be inactive or null).</summary>
        public FirstPersonPlayer Player => player;

        /// <summary>Start from the Inspector words as soon as the data is ready (development scene). The menu flow turns it off.</summary>
        public bool AutoStartFromInspector
        {
            get => startWhenDataReady;
            set => startWhenDataReady = value;
        }

        /// <summary>Stop reason and what to do, kept on screen until the next new experiment; null when not stopped.</summary>
        // The hint font (Pretendard-Black SDF, static atlas) has no '·', which the reasons may contain.
        // Built once per stop reason, not every frame.
        string HaltHint
        {
            get
            {
                if (halt == null) return null;
                if (!ReferenceEquals(halt, haltHintFor))
                {
                    haltHintFor = halt;
                    haltHintText = $"진행 정지: {halt.Replace("·", ", ")}\n{haltAdvice}";
                }
                return haltHintText;
            }
        }

        /// <summary>Hint of a finished experiment, built once per status (not every frame).</summary>
        string EndHint
        {
            get
            {
                if (!ReferenceEquals(status, endHintFor))
                {
                    endHintFor = status;
                    endHintText = status == "success" ? "목표 도달" : $"실험 종료: {StatusText.Korean(status)}";
                }
                return endHintText;
            }
        }

        string UnplacedHint => unplacedHintText ??= $"플레이어가 배치되지 않았습니다\n{haltAdvice}";

        void Awake()
        {
            service = GetComponent<WordRoomDataService>();
            if (worldView == null) worldView = GetComponent<RoomWorldView>();
            if (hint == null) hint = GetComponent<PlayHint>();
            if (hud == null) hud = GetComponent<WordRoomHud>();
            if (remotePlayers == null) remotePlayers = GetComponent<RemotePlayers>();
            if (player != null) player.RememberViewHome();   // also for an inactive player, before any debug camera move
        }

        void OnEnable()
        {
            if (service == null) service = GetComponent<WordRoomDataService>();
            service.StateChanged += OnDataState;
        }

        void OnDisable()
        {
            if (service != null) service.StateChanged -= OnDataState;
            cts?.Cancel();          // results of running work are discarded (see the token checks after await)
            ReleaseControlled();    // nobody checks room entries now; enabling again does not resume this player
            if (hint != null) hint.Clear();
            if (hud != null) hud.Clear();   // words already dropped with the player; enabling again shows nothing old
        }

        void Start()
        {
            if (service.State == DataLoadState.Ready) OnDataState(service);
            else if (!waitingLogged)
            {
                waitingLogged = true;
                Debug.Log("[방 생성] 데이터 로딩 대기 중 — 완료 전에는 입력을 받지 않습니다.");
            }
        }

        void OnDataState(WordRoomDataService s)
        {
            if (s.State == DataLoadState.Failed)
                Debug.LogError($"[방 생성] 데이터 로딩 실패로 실험을 시작할 수 없습니다: {s.Error?.Message}");
            else if (s.State == DataLoadState.Ready && startWhenDataReady && session == null && !busy && !coopMode)
                StartExperiment();
        }

        GenerationConfig BuildConfig(WordRoomDataset d)
        {
            var c = GenerationConfig.FromSettings(d.Settings);
            c.Seed = seed;
            c.VocabularyLimit = vocabularyLimit;
            c.MinWordLength = minWordLength;
            c.MaxWordLength = maxWordLength;
            c.TargetGuidance = targetGuidance;
            c.GuidanceStrength = guidanceStrength;
            c.GuidanceStart = guidanceStart;
            c.GuidanceRamp = guidanceRamp;
            c.GuidanceMax = guidanceMax;
            c.GuidanceBasis = guidanceBasis == GuidanceBasisOption.누적이동 ? "moves" : "unique";
            c.UseMoveLimit = useMoveLimit;
            c.MaxMoves = maxMoves;
            c.Source = "데이터 패키지 settings.json(Python load_config 적용값) + Inspector(시드·어휘 범위·목표 유도·테스트 이동 제한)";
            return c;
        }

        /// <summary>Starts a new experiment with the current Inspector values (also from the component's context menu).</summary>
        [ContextMenu("새 실험 시작")]
        public void StartExperiment()
        {
            if (!CanStart(out var d)) return;
            if (!d.TryGetWordRank(startWord, out var start))
            {
                Debug.LogError($"[방 생성] 시작 단어가 어휘에 없습니다: '{startWord}' (정규화 '{WordNormalizer.Normalize(startWord)}'). 다른 단어로 바꾸지 않았습니다.");
                return;
            }
            if (!d.TryGetWordRank(targetWord, out var target))
            {
                Debug.LogError($"[방 생성] 목표 단어가 어휘에 없습니다: '{targetWord}' (정규화 '{WordNormalizer.Normalize(targetWord)}'). 다른 단어로 바꾸지 않았습니다.");
                return;
            }
            RunExperiment(d, start, target, BuildConfig(d));   // copied now; later Inspector edits do not reach this experiment
        }

        /// <summary>
        /// Starts a game with words and seed chosen by the game (word ranks of the dataset). The vocabulary is the
        /// top `vocabularyLimit` surfaces of 1~8 characters, the test move limit is off; every other generation rule
        /// comes from the data package and the Inspector as in StartExperiment. False when busy or the data is not
        /// ready; invalid words or ranges are reported by the generator and stop play (no other words are tried).
        /// </summary>
        public bool StartGame(int startRank, int targetRank, long gameSeed, int gameVocabularyLimit)
        {
            if (!CanStart(out var d)) return false;
            var config = BuildConfig(d);
            config.Seed = gameSeed;
            config.VocabularyLimit = gameVocabularyLimit;
            config.MinWordLength = 1;
            config.MaxWordLength = 8;
            config.UseMoveLimit = false;                          // normal play has no move limit
            config.Source = "데이터 패키지 settings.json + Inspector(목표 유도) + 게임 난이도(어휘 범위)·자동 선택(시작·목표·시드)";
            RunExperiment(d, startRank, targetRank, config);
            return true;
        }

        /// <summary>
        /// Play cannot begin or go on (e.g. the data failed to load): play stops with the reason on screen, the
        /// player is released and the cursor freed. Nothing is retried.
        /// </summary>
        public void ReportFatal(string reason)
        {
            ReleaseControlled();
            halt = reason;
            FirstPersonPlayer.ReleaseCursor();
            Debug.LogError($"[방 생성] 진행할 수 없습니다: {reason}");
        }

        bool CanStart(out WordRoomDataset d)
        {
            d = null;
            if (coopMode)
            {
                Debug.LogWarning("[방 생성] 협동 게임 중에는 단독 실험·게임을 시작하지 않습니다.");
                return false;
            }
            if (busy)
            {
                Debug.Log("[방 생성] 생성 중이라 새 실험을 시작하지 않습니다.");
                return false;
            }
            if (!service.TryGetDataset(out d))
            {
                Debug.Log($"[방 생성] 데이터가 아직 준비되지 않았습니다 (상태: {service.State}).");
                return false;
            }
            return true;
        }

        async void RunExperiment(WordRoomDataset d, int start, int target, GenerationConfig config)
        {
            cts?.Cancel();
            cts = new CancellationTokenSource();
            var token = cts.Token;
            busy = true;
            session = null;
            dataset = d;
            var view = worldView != null;
            // Kept by reference: a failed start releases exactly these (cursor too), even after `controlled` is cleared.
            var stoppedControlled = controlled;
            var stoppedPlayer = player;
            if (stoppedControlled != null) stoppedControlled.Suspend();   // no walking or falling until the new start room is shown
            if (stoppedPlayer != null) stoppedPlayer.Suspend();
            controlled = null;
            status = null;
            halt = null;
            goalReported = false;                                 // time, visits and result belong to the new session
            SuppressGoalHint = false;
            nextRequest = 0;
            expectedRoom = GridPos.Origin;
            soloReturns = 0;                                      // a new game: 끼임방지 can be used once again
            exploredMap.Reset(null, LocalPlayer);                 // the explored map starts over; older updates are refused
            mapResync = false;
            Raise(ExperimentStarted);
            shownRadius = -1;
            SetHudWords(null, null);                              // the previous experiment's words never come back
            if (view) ViewCall(() => worldView.BeginSession());   // previous rooms/doors back to the pools (door states are the session's)
            Debug.Log($"[방 생성] 새 실험 준비: {d.GetWord(start)} → {d.GetWord(target)} · {config}" +
                      (config.TargetGuidance ? " · 목표 근접도 계산 중" : ""));
            var capture = new CaptureRequest(d, view, GameSettings.DisplayRadius, new MapRequest(TrackExploredMap, true), verboseLog);
            var radius = capture.Radius;
            try
            {
                var (created, result) = await Task.Run(() =>
                {
                    var c = SessionCoordinator.CreateSolo(d, config, start, target, LocalPlayer, token);
                    var r = c.Start(capture.Run, token);          // copies taken inside the start
                    return (c, r);
                }, token);
                if (this == null || token.IsCancellationRequested) return;
                if (!result.Accepted)
                {
                    FailStart($"실험을 시작할 수 없습니다 — {result.Refusal}", stoppedControlled, stoppedPlayer, ActivePlayer);
                    return;
                }
                var captured = result.Captured;                       // a start is never a duplicate
                session = created;
                exploredMap.Reset(created.SessionId, LocalPlayer);
                TakeConfirmed(captured);                              // start room and its first neighbors (full copy)
                var snapshot = captured.View;
                var shown = ShowView(snapshot, out var showFailure);
                if (shown) shownRadius = radius;
                var placeFailure = PlacePlayer(snapshot, shown, showFailure);
                if (placeFailure != null) FailStart($"시작 위치 배치 실패 — {placeFailure}", stoppedControlled, stoppedPlayer, ActivePlayer);
                else if (controlled != null) SetHudWords(snapshot.CurrentWord, snapshot.TargetWord);   // after the view and placement
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // normal cancel (disabled, destroyed or replaced): not an error, and nothing of this request is applied
            }
            catch (ArgumentException e)
            {
                Debug.LogError($"[방 생성] 실험을 시작할 수 없습니다: {e.Message}");
                if (this != null && !token.IsCancellationRequested) FailStart($"실험을 시작할 수 없습니다 — {e.Message}", stoppedControlled, stoppedPlayer, ActivePlayer);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (this != null && !token.IsCancellationRequested) FailStart("실험 시작 중 오류가 났습니다(Console 확인)", stoppedControlled, stoppedPlayer, ActivePlayer);
            }
            finally
            {
                busy = false;
            }
        }

        /// <summary>
        /// The new experiment cannot be played: play stays stopped with the reason on screen until the next new
        /// experiment, which recovers once the settings are fixed. The given players (those stopped for this start
        /// and the one connected now; destroyed ones are skipped) stay stopped and the cursor is released even when
        /// none of them is alive any more. Only called for the current request (checked by the caller).
        /// </summary>
        void FailStart(string reason, params FirstPersonPlayer[] stopped)
        {
            halt = reason;
            controlled = null;
            foreach (var p in stopped)
                if (p != null) p.Release();
            FirstPersonPlayer.ReleaseCursor();
        }

        void Update()
        {
            TrackPlayer();
            RefreshHud();
            RefreshRadiusIfChanged();
            var direction = ReadDirection();
            if (coopMode)                                                  // coop: no arrow-key Move; doors and entries are host requests (see the coop section)
            {
                if (ActivePlayer != null) UpdatePlay();
                else SetHint(HaltHint ?? coopHint);
                return;
            }
            if (ActivePlayer != null)
            {
                if (direction != null)
                    Debug.Log("[방 생성] 1인칭 플레이어가 연결되어 방향키 논리 이동을 하지 않습니다(플레이어 위치와 지도 위치 불일치 방지). 방향키 디버그는 Player 칸을 비우고 사용하세요.");
                UpdatePlay();
                return;
            }
            SetHint(HaltHint);                // debug mode: only a stop reason stays on screen
            if (direction == null) return;
            if (busy || session == null)
            {
                if (verboseLog) Debug.Log("[방 생성] 로딩·생성 중이라 입력을 무시했습니다 (입력은 쌓아두지 않음).");
                return;
            }
            if (status != "running")                       // the status copied from the last change; the session checks it again
            {
                Debug.Log($"[방 생성] 실험이 끝나 이동하지 않습니다: {StatusText.Korean(status)}");
                return;
            }
            Move(direction.Value);                         // a 미생성 direction is refused by the session (reason logged)
        }

        /// <summary>One direction per frame: the first newly pressed arrow in the order ↑ ↓ ← →.</summary>
        static Direction? ReadDirection()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) return null;
            if (kb.upArrowKey.wasPressedThisFrame) return Direction.Up;
            if (kb.downArrowKey.wasPressedThisFrame) return Direction.Down;
            if (kb.leftArrowKey.wasPressedThisFrame) return Direction.Left;
            if (kb.rightArrowKey.wasPressedThisFrame) return Direction.Right;
#else
            if (Input.GetKeyDown(KeyCode.UpArrow)) return Direction.Up;
            if (Input.GetKeyDown(KeyCode.DownArrow)) return Direction.Down;
            if (Input.GetKeyDown(KeyCode.LeftArrow)) return Direction.Left;
            if (Input.GetKeyDown(KeyCode.RightArrow)) return Direction.Right;
#endif
            return null;
        }

        async void Move(Direction direction)
        {
            if (coopMode) return;                                          // never in coop (single-player request path)
            busy = true;
            var token = cts.Token;
            var s = session;
            var request = NewRequest(RequestKind.Move, direction);
            var capture = CaptureNow();
            var radius = capture.Radius;
            try
            {
                var result = await Task.Run(() => s.Submit(request, capture.Run, token), token);
                if (this == null || token.IsCancellationRequested || s != session) return;   // stale result of an earlier experiment
                if (!result.Accepted && !result.Duplicate) Debug.Log($"[방 생성] 이동 불가: {result.Refusal}");     // refused: nothing changed, the 3D view is not moved
                else
                {
                    var current = await Current(result, s, capture, token);
                    if (current == null) return;
                    var captured = current.Value;
                    TakeConfirmed(captured);
                    if (ShowView(captured.View, out _)) shownRadius = radius;   // Console debug: an incomplete view is logged by the view itself
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                busy = false;
            }
        }

        // ---- display radius -------------------------------------------------------------------------------
        /// <summary>
        /// Shows the current map again with the latest display radius when it differs from the shown one and no
        /// request runs (a request in progress shows its own radius first; this then follows with the latest).
        /// Nothing while stopped or before the first view of the experiment.
        /// </summary>
        void RefreshRadiusIfChanged()
        {
            if (busy || (session == null && coopView == null) || worldView == null || halt != null || shownRadius < 0) return;
            var radius = GameSettings.DisplayRadius;
            if (radius != shownRadius) RefreshView(radius);
        }

        /// <summary>
        /// Re-captures the existing map on the worker with `radius` and shows it (rooms leaving the range go back to
        /// the pool, rooms entering it are rented; the current room always stays). Only a snapshot (the session's
        /// Read, inside its lock, so no change can mix in): no Fill, Prepare, Move or Enter, so no word, visit or
        /// move changes. Runs as a local request (busy), so an older result never overwrites a newer one.
        /// </summary>
        async void RefreshView(int radius)
        {
            if (coopMode)
            {
                RefreshCoopView(radius);
                return;
            }
            busy = true;
            var token = cts.Token;
            var s = session;
            var d = dataset;
            try
            {
                var snapshot = await Task.Run(() => s.Read(r => RoomViewSnapshot.Capture(r, LocalPlayer, d.GetWord, radius)), token);
                if (this == null || token.IsCancellationRequested || s != session) return;   // stale result
                if (ShowView(snapshot, out var failure)) shownRadius = radius;
                else if (controlled != null) Halt($"표시 거리 변경 후 방 표시 준비 실패 — {failure}");
                else shownRadius = radius;                                         // Console debug: logged by the view; no retry loop
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (this == null || s != session || token.IsCancellationRequested) return;
                if (controlled != null) Halt("표시 거리 변경 중 오류가 났습니다(Console 확인)");
                else shownRadius = radius;                                         // Console debug: no retry every frame
            }
            finally
            {
                busy = false;
            }
        }

        // ---- first-person play ------------------------------------------------------------------------------
        /// <summary>
        /// A placed player that is no longer the connected, active one (field cleared or replaced, component or
        /// object disabled, destroyed) is stopped and the cursor released, so it never moves alongside the arrow
        /// keys or the debug camera. Only a new experiment places a player again.
        /// </summary>
        void TrackPlayer()
        {
            if (ReferenceEquals(controlled, null)) return;
            // Placed is false after a disable/enable within one frame (OnDisable dropped the placement) and after Halt.
            if (controlled != null && controlled == player && controlled.isActiveAndEnabled && (controlled.Placed || halt != null)) return;
            ReleaseControlled();
            Debug.Log("[1인칭] 플레이어 연결이 해제·교체되었거나 비활성화되어 이전 플레이어를 정지하고 커서를 해제했습니다. 연결된 플레이어는 새 실험에서 배치합니다.");
        }

        void ReleaseControlled()
        {
            if (controlled != null) controlled.Release();
            controlled = null;
            SetHudWords(null, null);         // shown again only after a new experiment places a player
        }

        /// <summary>
        /// Player mode, once per frame: the hint for the current state and, while exploring, the door under the
        /// screen center (E opens it once; holding, repeating or pressing during the slide adds nothing). There is
        /// no progress caption: while a door slides or a request runs nothing is shown, and an earlier prompt goes.
        /// </summary>
        void UpdatePlay()
        {
            if (halt != null)
            {
                SetHint(HaltHint);
                return;
            }
            if (coopMode)
            {
                UpdateCoopPlay();
                return;
            }
            if (busy)
            {
                SetHint(null);                // start or entry request running: no caption, no "not placed"
                return;
            }
            if (controlled == null)
            {
                SetHint(session != null ? UnplacedHint : null);
                return;
            }
            if (status != "running")
            {
                SetHint(status == "success" && SuppressGoalHint ? null : EndHint);   // a result screen shows the goal instead
                return;
            }
            string text = null;
            if (worldView != null && controlled.CanInteract && controlled.TryGetAimedDoor(DoorReach, out var door) && worldView.CanOpen(door, out var id))   // same condition as InteractPressed (also with the map shown)
            {
                if (controlled.InteractPressed && worldView.OpenDoor(id, out var side)) PrepareRoom(side);   // slide and fill start together; prompt cleared
                else text = "[E] 문 열기";
            }
            SetHint(text);
        }

        /// <summary>
        /// The door toward `side` was just opened here (its slide has started): in the same frame one Prepare request
        /// stores the connection Open and fills the empty neighbors of the room behind it on the worker, with the
        /// snapshot taken inside the request. The local player's room, moves, visits and the session status do not
        /// change. Walking and door use are locked meanwhile (look, gravity and Escape go on); no other request is
        /// accepted or queued. The main thread then shows the result still centered on the current room (new words on
        /// the signs; the door confirmed Open; the room behind it now counts as prepared), and walking resumes. The
        /// doorway opens only when the slide has also finished (RoomWorldView.IsPassable). A partial fill is a normal
        /// result; a refusal, a failed display or an error stops this device's play instead (the session itself is
        /// not ended), with nothing undone or retried. A result after a new experiment, disabling or a player change
        /// does nothing.
        /// </summary>
        async void PrepareRoom(Direction side)
        {
            if (coopMode) return;                                          // never in coop (single-player request path)
            var p = controlled;
            p.SetMovementLocked(true);
            busy = true;
            SetHint(null);                                                         // no caption while waiting
            var token = cts.Token;
            var s = session;
            var request = NewRequest(RequestKind.Prepare, side);
            var capture = CaptureNow();
            var radius = capture.Radius;
            try
            {
                var result = await Task.Run(() => s.Submit(request, capture.Run, token), token);
                if (this == null || token.IsCancellationRequested || s != session) return;   // stale result
                if (!result.Accepted && !result.Duplicate)
                {
                    Halt($"{Directions.Korean(side)} 방 주변 준비가 거절되었습니다 — {result.Refusal}");
                    return;
                }
                var current = await Current(result, s, capture, token);           // a duplicate: the current state, read separately
                if (current == null) return;
                var captured = current.Value;
                TakeConfirmed(captured);                                           // new rooms join the map unvisited
                var shown = ShowView(captured.View, out var showFailure);
                if (shown) shownRadius = radius;
                if (controlled != p) return;                                       // released meanwhile
                if (!shown)
                {
                    Halt($"주변 방 표시 준비 실패 — {showFailure}");
                    return;
                }
                lastPosition = p.transform.position;
                p.SetMovementLocked(status != "running");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (this != null && s == session && !token.IsCancellationRequested) Halt("주변 준비 중 오류가 났습니다(Console 확인)");
            }
            finally
            {
                busy = false;
            }
        }

        /// <summary>
        /// After every Update (the player has moved): one entry at most, when the player's path in this frame went
        /// 0.15m past the boundary of the current room through a passable doorway.
        /// </summary>
        void LateUpdate()
        {
            StartPlayClock();                                                      // after every Update: the player's cursor/focus handling of this frame is done
            if (coopMode)
            {
                LateUpdateCoop();                                                  // coop: an entry is a request to the host
                return;
            }
            if (controlled == null || busy || halt != null || status != "running" || worldView == null) return;
            if (controlled != player || !controlled.Placed) return;                // TrackPlayer releases it next frame
            var to = controlled.transform.position;
            var from = lastPosition;
            lastPosition = to;
            if (worldView.TryDetectEntry(from, to, out var side)) EnterRoom(side);
        }

        /// <summary>
        /// Entered the neighbor on `side`: walking and door use stop at once (look, gravity and Escape go on), one
        /// Enter request (the local player's position, moves, visits, the team visit set, target and test limit only
        /// — its neighbors were filled when its door was opened, so there is no generation wait) runs on the worker
        /// with the snapshot taken inside it, carrying the boundary crossing time as the end of the play time; the
        /// main thread shows the result centered on the arrival room, and walking resumes where the player stands (no
        /// re-placement) once that view is complete and the arrival room is under the player. Only then is the
        /// session's confirmed result handed to the result screen (its time stays the crossing time). A refused
        /// entry, a failed display or an error stops this device's play instead; nothing is undone or retried (a
        /// confirmed entry stays confirmed). A result arriving after a new experiment, disabling or a player change
        /// never gives control back.
        /// </summary>
        async void EnterRoom(Direction side)
        {
            if (coopMode) return;                                                  // never in coop (single-player request path)
            var crossedAt = Time.realtimeSinceStartupAsDouble;                     // end of the play time if this entry reaches the goal
            var p = controlled;
            p.SetMovementLocked(true);
            busy = true;
            SetHint(null);                                                         // no caption while waiting
            var token = cts.Token;
            var s = session;
            var request = NewRequest(RequestKind.Enter, side, crossedAt);
            var capture = CaptureNow();
            var radius = capture.Radius;
            try
            {
                var result = await Task.Run(() => s.Submit(request, capture.Run, token), token);
                if (this == null || token.IsCancellationRequested || s != session) return;   // stale result
                if (!result.Accepted && !result.Duplicate)
                {
                    Halt($"{Directions.Korean(side)} 방 입장이 거절되었습니다 — {result.Refusal}");
                    return;
                }
                var current = await Current(result, s, capture, token);           // a duplicate: the current state, read separately
                if (current == null) return;
                var captured = current.Value;
                var snapshot = captured.View;
                TakeConfirmed(captured);                                           // the entered room becomes this player's room and team-visited
                var shown = ShowView(snapshot, out var showFailure);
                if (shown) shownRadius = radius;
                if (controlled != p) return;                                       // released meanwhile
                string failure = null;
                if (!shown) failure = $"주변 방 표시 준비 실패 — {showFailure}";
                else if (!worldView.TryGetShownRoom(snapshot.Center, out var room)) failure = $"도착 방 {snapshot.Center}이 3D로 표시되지 않았습니다";
                else p.IsOnFloorOf(room, out failure);
                if (failure != null)
                {
                    Halt(failure);
                    return;
                }
                lastPosition = p.transform.position;
                p.SetMovementLocked(status != "running");                          // finished: look and Escape only
                SetHudWords(snapshot.CurrentWord, hudTarget);                      // entry confirmed and shown: new current room, same target
                if (!result.Duplicate && result.Update.Result != null) ReportGoal(result.Update.Result);   // a duplicate never reports again
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (this != null && s == session && !token.IsCancellationRequested) Halt("입장 처리 중 오류가 났습니다(Console 확인)");
            }
            finally
            {
                busy = false;
            }
        }

        // ---- 끼임방지: back to the start room (an explicit Return request; never an entry, a visit or a move) -----------
        /// <summary>
        /// Whether this device may ask for 끼임방지 now, else why not: a placed player of a running game (after the common
        /// start in coop) that is not stopped, with nothing of its own unanswered — single play: no request running;
        /// coop: no door opening, entry or return of this device waiting (it is never sent twice, and a waiting request
        /// is never cancelled or dropped for it) — and its one use of this game not spent (UnstuckUsed; the coordinator
        /// refuses a second return anyway).
        /// </summary>
        public bool CanRequestUnstuck(out string why)
        {
            why = null;
            if (controlled == null || controlled != player || !controlled.Placed) why = "배치된 플레이어가 없어 사용할 수 없습니다";
            else if (halt != null) why = "진행이 정지되어 사용할 수 없습니다";
            else if (coopMode ? coopView == null || coopWaiting : session == null) why = "게임이 시작되기 전에는 사용할 수 없습니다";
            else if (status != "running") why = "탐색이 끝나 사용할 수 없습니다";
            else if (UnstuckWaiting) why = "시작 방으로 돌아가는 요청을 처리하고 있습니다";
            else if (UnstuckUsed) why = "끼임방지는 한 게임에 한 번만 사용할 수 있습니다";   // the host checks it again
            else if (coopMode ? prepareWait != null || enterWait != null : busy) why = "문 열기나 입장 처리를 기다리는 중입니다. 확인된 뒤에 사용할 수 있습니다";
            else if (worldView == null) why = "3D 방 표시가 없어 사용할 수 없습니다";
            return why == null;
        }

        /// <summary>A 끼임방지 request of this device is waiting for its outcome.</summary>
        public bool UnstuckWaiting => coopMode ? returnWait != null : unstuckRunning;

        /// <summary>
        /// 끼임방지: one Return request of this player (single play: to its own coordinator; coop: to the host). The body is
        /// moved only after the confirmed state has this player in the start room (UnstuckFinished tells the end). False
        /// with why when it cannot be asked now (CanRequestUnstuck) — nothing is sent then.
        /// </summary>
        public bool RequestUnstuck(out string why)
        {
            if (!CanRequestUnstuck(out why)) return false;
            if (coopMode) SendCoop(RequestKind.Return, Direction.Up, default);  // the direction is unused for a return
            else ReturnToStart();
            return true;
        }

        void FinishUnstuck(bool ok, string why)
        {
            var handlers = UnstuckFinished;
            if (handlers == null) return;
            try
            {
                handlers(ok, why);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        /// <summary>
        /// Single play: one Return request on the worker with the snapshot taken inside it; once confirmed the view is
        /// shown around the start room and the body put on its floor (PlaceInStartRoom). A refusal changes nothing and is
        /// told; a failed display or placement stops play with the reason (never told as done). Walking is locked while
        /// it runs (the game menu's own block is separate). A stale result does nothing.
        /// </summary>
        async void ReturnToStart()
        {
            var p = controlled;
            busy = true;
            unstuckRunning = true;
            p.SetMovementLocked(true);
            var token = cts.Token;
            var s = session;
            var request = NewRequest(RequestKind.Return, Direction.Up);
            var capture = CaptureNow();
            var radius = capture.Radius;
            string failed = null;
            var done = false;
            try
            {
                var result = await Task.Run(() => s.Submit(request, capture.Run, token), token);
                if (this == null || token.IsCancellationRequested || s != session) return;   // stale result
                if (!result.Accepted && !result.Duplicate)
                {
                    failed = $"시작 방으로 돌아갈 수 없습니다 — {result.Refusal}";
                    if (controlled == p) p.SetMovementLocked(status != "running");
                    return;
                }
                var current = await Current(result, s, capture, token);
                if (current == null) return;
                var captured = current.Value;
                var snapshot = captured.View;
                TakeConfirmed(captured);                                           // the start room is this player's room again (no visit)
                var shown = ShowView(snapshot, out var showFailure);
                if (shown) shownRadius = radius;
                if (controlled != p) return;                                       // released meanwhile
                var failure = PlaceInStartRoom(p, snapshot.Center, shown, showFailure);
                if (failure != null)
                {
                    failed = $"시작 방 복귀 배치 실패 — {failure}";
                    Halt(failed);
                    return;
                }
                SetHudWords(snapshot.CurrentWord, hudTarget);
                done = true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (this != null && s == session && !token.IsCancellationRequested)
                {
                    failed = "시작 방 복귀 중 오류가 났습니다(Console 확인)";
                    Halt(failed);
                }
            }
            finally
            {
                busy = false;
                unstuckRunning = false;
                if (this != null && (done || failed != null)) FinishUnstuck(done, failed);
            }
        }

        /// <summary>
        /// After the view around the start room is shown: the body on the start room's floor center (the safe spot, as at
        /// the start), facing 상, no fall; walking as the status allows. Null when placed, else why (nothing more done).
        /// </summary>
        string PlaceInStartRoom(FirstPersonPlayer p, GridPos shownCenter, bool shown, string showFailure)
        {
            if (!shown) return showFailure ?? "시작 방 주변을 3D로 표시하지 못했습니다";
            if (shownCenter != GridPos.Origin) return $"표시 중심 {shownCenter}이 시작 방이 아닙니다";
            if (!worldView.TryGetShownRoom(GridPos.Origin, out var room)) return "시작 방이 3D로 표시되지 않았습니다";
            if (!p.PlaceAt(room, out var failure)) return failure;
            lastPosition = p.transform.position;
            p.SetMovementLocked(status != "running" || coopWaiting);
            return null;
        }

        /// <summary>
        /// Physical and logical position can no longer be trusted to match: the player stops completely (no
        /// walking, falling or door use; Escape still releases the cursor) until a new experiment.
        /// </summary>
        void Halt(string reason)
        {
            halt = reason;
            if (controlled != null) controlled.Suspend();
            RefreshHud();                    // hidden: the shown words may no longer match the map
            Debug.LogError($"[1인칭] 진행 정지 — 이동·문 열기를 멈춥니다. {haltAdvice}: {reason}");
        }

        void SetHudWords(string current, string target)
        {
            hudCurrent = current;
            hudTarget = target;
            if (current == null && hud != null) hud.Clear();   // words dropped: nothing old can come back
            RefreshHud();
        }

        /// <summary>
        /// The HUD is visible only while a first-person player is placed for this experiment and play is not stopped
        /// (also while a door opens or a request runs, and after the goal or another end). It shows the words of the
        /// last confirmed placement or entry; a door opening never changes them. Arrow-key/Console debug, a
        /// released player, a stop or a new experiment hide it (dropped words also Clear it). Cheap per frame: the
        /// HUD only compares strings. A disabled HUD ignores Show and stays hidden; after it is enabled again the next
        /// frame's Show writes the words anew.
        /// </summary>
        void RefreshHud()
        {
            var visible = controlled != null && halt == null && hudCurrent != null && hudTarget != null;
            if (hud != null)
            {
                if (visible) hud.Show(hudCurrent, hudTarget);
                else hud.Hide();
            }
            else if (visible && !hudMissingLogged)
            {
                hudMissingLogged = true;
                Debug.LogWarning("[HUD] WordRoomHud가 없어 현재·목표 단어를 화면에 표시하지 않습니다(플레이는 계속). RoomGenerationDebug 오브젝트에 WordRoomHud를 두고 WordRoom/메인 메뉴와 게임 설정 구성 메뉴로 HudCanvas를 연결하세요.");
            }
        }

        // ---- requests and captures -------------------------------------------------------------------------------
        /// <summary>A request of the local player from its last confirmed room (the session checks it against its own record).</summary>
        SessionRequest NewRequest(RequestKind kind, Direction side, double crossedAt = double.NaN) =>
            new SessionRequest(session.SessionId, LocalPlayer, ++nextRequest, kind, expectedRoom, side, crossedAt);

        /// <summary>
        /// Main thread: the copies of one revision become the local state — map change, status and the room sent as
        /// ExpectedFrom next — all from the same capture or Read, never from an old Update.
        /// </summary>
        void TakeConfirmed(Captured c)
        {
            ApplyMap(c.Map);
            status = c.Status;
            expectedRoom = c.Room;
            soloReturns = c.Returns;
            if (c.Log != null) Debug.Log(c.Log);
        }

        /// <summary>
        /// What one request copies, inside the session's lock right after its change (worker thread), or what a Read
        /// copies of the current state (after a duplicate response, which carries no capture). Status and Room are
        /// the session status and the local player's confirmed room of the same revision.
        /// </summary>
        readonly struct Captured
        {
            public readonly RoomViewSnapshot View;
            public readonly MapUpdate Map;
            public readonly string Log;
            public readonly string Status;
            public readonly GridPos Room;
            public readonly int Returns;     // the local player's confirmed returns to the start room (끼임방지)

            public Captured(RoomViewSnapshot view, MapUpdate map, string log, string status, GridPos room, int returns)
            {
                View = view;
                Map = map;
                Log = log;
                Status = status;
                Room = room;
                Returns = returns;
            }
        }

        /// <summary>The copies a request wants, decided on the main thread when the request starts (Unity values read there).</summary>
        sealed class CaptureRequest
        {
            readonly WordRoomDataset data;
            readonly bool view;
            readonly MapRequest map;
            readonly bool verbose;
            public readonly int Radius;

            public CaptureRequest(WordRoomDataset data, bool view, int radius, MapRequest map, bool verbose)
            {
                this.data = data;
                this.view = view;
                Radius = radius;
                this.map = map;
                this.verbose = verbose;
            }

            /// <summary>Worker, inside the coordinator's lock right after the change: every copy belongs to the revision of `u`.</summary>
            public Captured Run(SessionReader r, GenerationUpdate u) =>
                new Captured(view ? RoomViewSnapshot.Capture(r, LocalPlayer, data.GetWord, Radius) : null, map.Capture(r, u, data.GetWord),
                    Describe(r, u, data, verbose), r.Status, r.PlayerPosition(LocalPlayer), r.PlayerReturns(LocalPlayer));

            /// <summary>Worker, inside a Read: the current state (no change of its own), the map as a full copy of this revision.</summary>
            public Captured Latest(SessionReader r) =>
                new Captured(view ? RoomViewSnapshot.Capture(r, LocalPlayer, data.GetWord, Radius) : null, map.CaptureFull(r, data.GetWord),
                    null, r.Status, r.PlayerPosition(LocalPlayer), r.PlayerReturns(LocalPlayer));
        }

        /// <summary>
        /// A request's result as the display uses it: its own capture, or — for a duplicate response, whose Update may
        /// be older than the session — the current state read separately (never the old Update's room or status).
        /// Null when the result is stale (new experiment, disabled) by the time the read finished.
        /// </summary>
        async Task<Captured?> Current(RequestResult<Captured> result, SessionCoordinator s, CaptureRequest capture, CancellationToken token)
        {
            if (!result.Duplicate) return result.Captured;
            Debug.LogWarning($"[방 생성] 중복 요청 응답을 받아 최초 결과를 다시 적용하지 않고 현재 상태로 표시를 맞춥니다(요청 {result.Update?.RequestId}, 최초 {(result.Accepted ? $"확정 순번 {result.Update.Revision}" : $"거절: {result.Refusal}")}).");
            var latest = await Task.Run(() => s.Read(capture.Latest), token);
            if (this == null || token.IsCancellationRequested || s != session) return null;
            return latest;
        }

        CaptureRequest CaptureNow() => new CaptureRequest(dataset, worldView != null, GameSettings.DisplayRadius, MapRequestNow(), verboseLog);

        // ---- explored map --------------------------------------------------------------------------------------
        /// <summary>What a request copies for the map, decided on the main thread when the request starts.</summary>
        readonly struct MapRequest
        {
            readonly bool track;
            readonly bool full;

            public MapRequest(bool track, bool full)
            {
                this.track = track;
                this.full = full;
            }

            /// <summary>Worker, inside the request's capture: null when the map is not tracked; the whole map of this revision when `full`.</summary>
            public MapUpdate Capture(SessionReader r, GenerationUpdate u, Func<int, string> wordText) =>
                !track ? null : full ? MapUpdate.FromCopy(r.CopyAll(), wordText) : MapUpdate.FromChange(r, u, wordText);

            /// <summary>Worker, inside a Read: the whole map of the current revision (null when not tracked).</summary>
            public MapUpdate CaptureFull(SessionReader r, Func<int, string> wordText) => track ? MapUpdate.FromCopy(r.CopyAll(), wordText) : null;
        }

        MapRequest MapRequestNow() => new MapRequest(trackMap, mapResync || !exploredMap.HasData);

        /// <summary>
        /// Main thread, after the request's stale checks: the copied change joins the explored map in revision order.
        /// Duplicates, older changes and other sessions' copies are dropped. A missed change (should not happen:
        /// requests never overlap and their results are applied in order, but an interrupted request may leave a
        /// revision without a result) is reported once and recovered from a full copy of one revision; nothing is
        /// generated or retried for it.
        /// </summary>
        void ApplyMap(MapUpdate u)
        {
            if (u == null) return;
            switch (exploredMap.Apply(u, out var failure))
            {
                case MapApplyResult.Applied:
                    if (u.Full) mapResync = false;
                    return;
                case MapApplyResult.Gap:
                    mapResync = true;
                    if (!mapDesyncLogged)
                    {
                        mapDesyncLogged = true;
                        Debug.LogWarning($"[지도] 지도 갱신을 건너뛰었습니다 — {failure}. 같은 순번의 전체 사본으로 다시 맞춥니다.");
                    }
                    RecoverMap();
                    return;
                default:
                    return;                                                        // duplicate, older or another session's copy
            }
        }

        /// <summary>A full copy of the session (world, players, session of one revision) for the map; no request, no change.</summary>
        async void RecoverMap()
        {
            if (mapRecovering || session == null || !trackMap) return;
            mapRecovering = true;
            var token = cts.Token;
            var s = session;
            var d = dataset;
            try
            {
                var full = await Task.Run(() => s.Read(r => MapUpdate.FromCopy(r.CopyAll(), d.GetWord)), token);
                if (this == null || token.IsCancellationRequested || s != session) return;
                ApplyMap(full);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception e)
            {
                Debug.LogException(e);                                            // the next request copies the whole map instead
            }
            finally
            {
                mapRecovering = false;
            }
        }

        // ---- play time and goal result ------------------------------------------------------------------------
        /// <summary>
        /// Reports the play start once per experiment to the session (which records it), in the first frame (checked
        /// in LateUpdate, after every Update, so no script order is needed) the placed player really reads input:
        /// FirstPersonPlayer.CanTakeInput — placed and enabled, cursor locked, window focused, no menu block or its
        /// resume frame, walking not locked. Loading, the first generation and placement are not counted; a menu
        /// open, a lost focus or a free cursor at placement delays the start. No key press is awaited. A failed
        /// start or placement leaves no controlled player, so the clock never starts; once started, menus, the map,
        /// focus loss, cursor release and generation waits neither stop nor restart it.
        /// </summary>
        void StartPlayClock()
        {
            if (coopMode || session == null || controlled == null || halt != null || session.PlayClockStarted) return;   // coop: the common start is the time base
            if (controlled == player && controlled.CanTakeInput) session.StartPlayClock(Time.realtimeSinceStartupAsDouble);
        }

        /// <summary>
        /// The confirmed goal entry is shown: hands the session's result (fixed by the coordinator in that entry —
        /// time up to its boundary crossing, not its worker or display time) to the result screen, once.
        /// </summary>
        void ReportGoal(SessionResult confirmed)
        {
            if (goalReported) return;
            goalReported = true;
            if (double.IsNaN(confirmed.Seconds))
            {
                Debug.LogWarning("[결과] 조작 시작 시각이 기록되지 않아 목표 도달 결과를 만들지 않았습니다(안내만 표시).");
                return;
            }
            var result = new GoalResult(confirmed, localPlayer);              // this device's player ("local" in single play)
            Debug.Log($"[결과] 목표 도달 — 소요 {result.Seconds:0.0}초, 방문 방 {result.VisitedRooms}개(팀 {confirmed.TeamVisitedRooms}개)");
            var handlers = GoalReached;
            if (handlers == null) return;                                      // development scene: no result screen
            try
            {
                handlers(result);
            }
            catch (Exception e)
            {
                Debug.LogException(e);                                        // a listener's failure never stops play here
            }
        }

        static void Raise(Action handlers)
        {
            if (handlers == null) return;
            try
            {
                handlers();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        void SetHint(string text)
        {
            if (hint != null) hint.Set(text);
            else if (!string.IsNullOrEmpty(text) && !hintMissingLogged)
            {
                hintMissingLogged = true;
                Debug.LogWarning("[안내] PlayHint가 없어 화면 안내를 표시하지 않습니다(플레이는 계속). RoomGenerationDebug 오브젝트에 PlayHint를 두고 WordRoom/메인 메뉴와 게임 설정 구성 메뉴로 PlayHintCanvas를 연결하세요.");
            }
        }

        // ---- 3D view (optional; its failures never stop generation or Console output) -----------------------
        /// <summary>
        /// True only when the view reports every needed room, door and passage block usable (see RoomWorldView.Show);
        /// "no exception" alone is not success. False with the reason when there is no view or snapshot, the view
        /// is not ready, something is missing, or the update threw.
        /// </summary>
        bool ShowView(RoomViewSnapshot snapshot, out string failure)
        {
            if (worldView == null || snapshot == null)
            {
                failure = "RoomWorldView가 없어 3D로 표시하지 않았습니다";
                return false;
            }
            if (player != null) player.RememberViewHome();     // before the debug camera (maybe its view) can move
            var complete = false;
            string reason = null;
            if (!ViewCall(() => complete = worldView.Show(snapshot, ActivePlayer == null, out reason)))
                reason = "3D 표시 갱신 중 오류가 났습니다(Console 확인)";
            failure = complete ? null : reason;
            return complete;
        }

        /// <summary>
        /// After the start room is shown: puts the player that is connected and active now on its floor. Returns
        /// null when placed or when no active player is connected (arrow-key/Console debug; an inactive player is
        /// never placed and the cursor is not locked for it), otherwise why play cannot start.
        /// </summary>
        string PlacePlayer(RoomViewSnapshot snapshot, bool shown, string showFailure)
        {
            var p = ActivePlayer;
            if (p == null)
            {
                if (player != null) Debug.LogWarning("[1인칭] 연결된 플레이어가 비활성이라 배치하지 않습니다. 활성화한 뒤 새 실험을 시작하세요.");
                return null;
            }
            string failure;
            if (!shown) failure = showFailure ?? "시작 방을 3D로 표시하지 못했습니다";
            else if (!worldView.TryGetShownRoom(snapshot.Center, out var room)) failure = "시작 방이 3D로 표시되지 않았습니다(위 [3D 방] 오류 확인)";
            else if (p.PlaceAt(room, out failure))
            {
                controlled = p;
                lastPosition = p.transform.position;
                p.SetMovementLocked(status != "running");                          // e.g. no exit around the start room
                return null;
            }
            Debug.LogError($"[1인칭] 시작 위치 배치 실패 — 이동하지 않습니다(단어 생성·Console 디버그는 계속): {failure}");
            return failure;
        }

        static bool ViewCall(Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[3D 방] 표시 갱신 실패(단어 생성은 계속): {e}");
                return false;
            }
        }

        // ---- coop (every device shows the host's confirmed state; only the host's coordinator changes it) ---------
        // Coop mode is entered by the game's coop flow only. Every device shows its own SessionCopy of the host's state:
        // the initial copy, then each confirmed change applied in revision order (SessionCopy.Apply) or a full copy after
        // a missed one — the host too, through the same messages; only the host holds a coordinator (the authority, never
        // used for display). The single-player paths (StartGame/StartExperiment, arrow-key Move, solo Prepare and Enter)
        // never run in coop mode. After the common start the local player opens a door with E (the door slides up here;
        // one Prepare request goes to the host) or walks through an open, prepared doorway (one Enter request). A request
        // never stops walking: a door waiting for its Prepare keeps its passage block until the confirmed state opens it;
        // while an Enter waits, only the doorway between the room left and the room entered stays passable (the player
        // moves in those two rooms, both shown, never on into a third one), and the 3D view keeps centering on the
        // confirmed room. The confirmed room and the room the player really stands in may differ for that while; once the
        // entry is confirmed, a player that has walked back is seen standing past the boundary and enters back with one
        // more Enter. An accepted request never moves or turns the player; a refusal closes the door again (unless the
        // confirmed state has it open) or, only when the player stands outside its confirmed room, puts it back inside.
        // Another player's change only updates rooms, signs, doors and the map. A display failure stops this device only.
        bool coopMode;
        SessionCopy coopView;                // the confirmed state this device shows (independent copy of one revision)
        string coopHint;                     // the coop flow's message on screen; null = none
        bool coopWaiting;                    // placed, waiting for the common start: walking locked by the coop gate
        string localPlayer = LocalPlayer;    // coop: this device's PlayerId (actor-N); single play: "local"
        Func<RequestKind, GridPos, Direction, long, bool> coopSend;   // set by the coop flow: sends one request to the host
        Func<string, string> coopName;       // display name of a PlayerId (set by the coop flow)
        double coopWaitNotice = 10;          // a request unanswered this long is told as delayed (never cancelled)
        CoopPending prepareWait;             // this device's door opening waiting for the host; null = none
        CoopPending enterWait;               // this device's entry waiting for the host (at most one); null = none
        CoopPending returnWait;              // this device's return to the start room (끼임방지) waiting for the host; null = none
        int placedReturns;                   // this player's confirmed returns already carried out by putting the body in the start room
        string coopShownWord;                // current word of the last shown copy (this player's confirmed room)
        string coopEndText;                  // what the end of the session looks like here; null while running
        string coopNote;                     // a short local message (a refusal), shown until coopNoteUntil
        double coopNoteUntil;

        const float ReturnDepth = 0.8f;      // a player whose entry was not confirmed is put back this far inside its room
        const float NoteSeconds = 4f;

        /// <summary>
        /// This device's request waiting for the host (one door opening and one entry at most). Its outcome is known only from the host — the
        /// answer, the answer to a query of the same RequestId, or this request's own confirmed change — never guessed
        /// from the world (an open door or a prepared room may be another player's). Accepted and Failed also wait until
        /// the shown copy has reached their revision.
        /// </summary>
        sealed class CoopPending
        {
            public long RequestId;
            public RequestKind Kind;
            public Direction Side;
            public GridPos From;
            public GridPos To;
            public RoomConnection Door;      // Prepare: the door that slides up here
            public double SentAt;
            public RequestOutcome? Outcome;  // null = no outcome yet (also while the host reports it pending)
            public long Target;              // Accepted: the confirming revision; Failed: the revision right after the error
            public string Reason;            // Refused, Unknown or Failed: why
            public bool Delayed;             // told as delayed (still waiting)
        }

        public bool IsCoop => coopMode;

        /// <summary>The session id of the state this device shows (coop; null before the initial state).</summary>
        public string CoopSessionId => coopView?.SessionId;

        /// <summary>The revision of the state this device shows (coop; 0 before the initial state).</summary>
        public long CoopRevision => coopView?.Revision ?? 0;

        /// <summary>This device's oldest request whose outcome the host has not told yet (for a query of the same RequestId).</summary>
        public bool TryGetCoopUnanswered(out long requestId, out double sentAt)
        {
            CoopPending p = null;
            foreach (var w in new[] { prepareWait, enterWait, returnWait })
                if (w != null && w.Outcome == null && (p == null || w.RequestId < p.RequestId)) p = w;
            requestId = p?.RequestId ?? 0;
            sentAt = p?.SentAt ?? 0;
            return p != null;
        }

        /// <summary>The revision this device's waiting requests need the shown copy to reach (their known outcomes); 0 when none.</summary>
        public long CoopAwaitedRevision => Math.Max(Math.Max(Awaited(prepareWait), Awaited(enterWait)), Awaited(returnWait));

        /// <summary>The coop game's one result is confirmed in the shown state (the goal was reached by anyone).</summary>
        public bool CoopResultConfirmed => coopMode && coopView != null && coopView.Status == "success" && coopView.Result != null;

        static long Awaited(CoopPending p) => p != null && p.Outcome != null ? p.Target : 0;

        /// <summary>A short message of the coop flow on this device (e.g. the host failing to bring a participant up to date).</summary>
        public void NotifyCoop(string text) => ShowNote(text);

        // ---- other participants' robots (display only) --------------------------------------------------------------
        int coopPlacement;                   // grows whenever this player is placed or put back rather than walking
        bool remoteMissingLogged;

        /// <summary>The other participants' robots of this scene (null when the scene has none: they are simply not shown).</summary>
        public RemotePlayers RemotePlayers => remotePlayers;

        /// <summary>
        /// The game's fixed participants (fixed order, the same on every device): the robots of every other one are shown
        /// from now on, within the rooms this device shows. Never this device's own player.
        /// </summary>
        public void BeginRemotePlayers(IReadOnlyList<string> fixedParticipants)
        {
            if (remotePlayers == null)
            {
                if (!remoteMissingLogged)
                {
                    remoteMissingLogged = true;
                    Debug.LogWarning("[협동] RemotePlayers가 없어 다른 참가자 외형을 표시하지 않습니다(플레이는 계속). WordRoom/협동 로봇 외형 구성 메뉴로 Gameplay에 연결하세요.", this);
                }
                return;
            }
            remotePlayers.Begin(fixedParticipants, localPlayer, worldView != null ? (Func<Vector3, bool>)worldView.IsShownAt : null);
        }

        /// <summary>
        /// This device's body as the others should see it: the placed player's real feet position and heading (the
        /// camera's pitch is not part of it), and how many times it was placed or put back. Also while an entry waits or
        /// after a stop (it stands where it is); false when no player is placed for this coop game.
        /// </summary>
        public bool TryGetCoopLocalPose(out Vector3 position, out float yaw, out int placement)
        {
            position = default;
            yaw = 0f;
            placement = coopPlacement;
            if (!coopMode || coopView == null || controlled == null || controlled != player || !controlled.isActiveAndEnabled) return false;
            var t = controlled.transform;
            position = t.position;
            yaw = t.eulerAngles.y;
            return true;
        }

        /// <summary>
        /// The coop flow's part for play: how a request reaches the host (false = it could not be sent), how long an
        /// unanswered request waits before it is told as delayed, and the display name of a PlayerId.
        /// </summary>
        public void SetCoopPlay(Func<RequestKind, GridPos, Direction, long, bool> send, double waitNoticeSeconds, Func<string, string> nameOf)
        {
            coopSend = send;
            coopWaitNotice = waitNoticeSeconds;
            coopName = nameOf;
        }

        /// <summary>
        /// Enters coop mode for this scene (before anything is shown): no single-player start, the previous state and
        /// view are dropped, the player is suspended (no walking or falling) until it is placed.
        /// </summary>
        public void BeginCoop(WordRoomDataset d, string localPlayerId)
        {
            cts?.Cancel();
            cts = new CancellationTokenSource();
            coopMode = true;
            coopView = null;
            coopWaiting = false;
            prepareWait = enterWait = returnWait = null;
            placedReturns = 0;
            coopShownWord = coopEndText = coopNote = null;
            coopPlacement = 0;
            if (remotePlayers != null) remotePlayers.Clear();            // nothing of an earlier game is shown again
            dataset = d;
            localPlayer = localPlayerId;
            session = null;
            if (controlled != null) controlled.Suspend();
            if (player != null) player.Suspend();
            controlled = null;
            status = null;
            halt = null;
            goalReported = false;
            SuppressGoalHint = false;
            nextRequest = 0;
            expectedRoom = GridPos.Origin;
            exploredMap.Reset(null, localPlayer);
            mapResync = false;
            Raise(ExperimentStarted);
            shownRadius = -1;
            SetHudWords(null, null);
            if (worldView != null) ViewCall(() => worldView.BeginSession());
        }

        /// <summary>The coop flow's message (loading, waiting, failures); null clears it. A stop reason (Halt) wins.</summary>
        public void SetCoopHint(string text) => coopHint = text;

        /// <summary>
        /// Host only: the coop coordinator with the fixed participants (in their fixed order) is made and started once on
        /// the worker; `done` gets the confirmed initial state copied inside that start (world, players and session of
        /// one revision) and the fixed generation settings, or a failure. Nothing is shown here — the host shows the
        /// state it sends, through the same checks as everyone.
        /// </summary>
        public async void StartCoopHost(int startRank, int targetRank, long seed, int vocabularyLimit, string[] playerIds, Action<SessionCopy, GenerationConfig, string> done)
        {
            if (!coopMode || busy || session != null || dataset == null)
            {
                done(null, null, "초기 방을 만들 수 있는 상태가 아닙니다");
                return;
            }
            busy = true;
            var token = cts.Token;
            var d = dataset;
            var config = BuildConfig(d);                                   // this device (the host) fixes the settings once
            config.Seed = seed;
            config.VocabularyLimit = vocabularyLimit;
            config.MinWordLength = 1;
            config.MaxWordLength = 8;
            config.UseMoveLimit = false;
            config.Source = "데이터 패키지 settings.json + 호스트 Inspector(목표 유도) + 협동 난이도(어휘 범위)·호스트 자동 선택(시작·목표·시드)";
            try
            {
                var (created, copy) = await Task.Run(() =>
                {
                    var c = SessionCoordinator.Create(d, config, startRank, targetRank, SessionMode.Coop, playerIds, token);
                    var r = c.Start((reader, u) => reader.CopyAll(), token);   // the copy belongs to the start's revision
                    if (!r.Accepted) throw new InvalidOperationException(r.Refusal);
                    return (c, r.Captured);
                }, token);
                if (this == null || token.IsCancellationRequested || !coopMode) return;
                session = created;                                         // the authority of every exploration request
                created.UseEndClock(RealtimeClockForWorkers());            // the goal entry's end: read when it is confirmed, on the play clock's axis
                done(copy, created.Config, null);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (ArgumentException e)
            {
                if (this != null && !token.IsCancellationRequested) done(null, null, $"게임을 시작할 수 없습니다 — {e.Message}");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (this != null && !token.IsCancellationRequested) done(null, null, "초기 방을 만드는 중 오류가 났습니다");
            }
            finally
            {
                busy = false;
            }
        }

        /// <summary>
        /// Shows the confirmed initial state (already checked by the coop flow) around this device's player: the 3D
        /// rooms, doors and passage blocks, the whole map, the HUD words, and the player on the start room's floor.
        /// True only when every part is shown, the player is placed and stands on that floor; walking then stays
        /// locked until BeginCoopPlay. False with the reason otherwise (play stops; nothing is retried).
        /// </summary>
        public bool ShowCoopInitial(SessionCopy copy, out string failure)
        {
            failure = null;
            if (!coopMode || copy == null || dataset == null || coopView != null)
            {
                failure = "협동 화면을 준비할 수 있는 상태가 아닙니다";
                return false;
            }
            coopView = copy;
            status = copy.Status;
            exploredMap.Reset(copy.SessionId, localPlayer);
            if (TrackExploredMap) ApplyMap(MapUpdate.FromCopy(copy, dataset.GetWord));
            var radius = GameSettings.DisplayRadius;
            RoomViewSnapshot snapshot;
            try
            {
                snapshot = RoomViewSnapshot.Capture(copy, localPlayer, dataset.GetWord, radius);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                failure = "방 표시 정보를 만들지 못했습니다";
                ReportFatal(failure);
                return false;
            }
            var shown = ShowView(snapshot, out var showFailure);
            if (shown) shownRadius = radius;
            failure = PlacePlayer(snapshot, shown, showFailure);
            if (failure == null && controlled == null) failure = "1인칭 플레이어가 연결되지 않아 배치할 수 없습니다";
            if (failure == null && (!worldView.TryGetShownRoom(snapshot.Center, out var room) || !controlled.IsOnFloorOf(room, out failure)))
                failure = failure ?? $"시작 방 {snapshot.Center}이 표시되지 않았습니다";
            if (failure != null)
            {
                ReportFatal($"협동 시작 준비 실패 — {failure}");
                return false;
            }
            coopWaiting = true;
            placedReturns = copy.FindPlayer(localPlayer)?.Returns ?? 0;    // returns before this placement are already where it stands
            coopPlacement++;                                               // placed, not walked: others never glide to here
            controlled.SetMovementLocked(true);                            // the coop gate: look yes, walking and doors not yet
            lastPosition = controlled.transform.position;
            coopShownWord = snapshot.CurrentWord;
            SetHudWords(snapshot.CurrentWord, snapshot.TargetWord);
            return true;
        }

        /// <summary>
        /// The common start: walking, doors and entries begin and, on the host, the session's one time base is set to
        /// `localStartRealtime`, the common server-time start converted to this device's clock (never the moment this
        /// device became controllable).
        /// </summary>
        public void BeginCoopPlay(double localStartRealtime)
        {
            if (!coopMode || coopView == null || !coopWaiting) return;
            coopWaiting = false;
            if (controlled != null && halt == null) controlled.SetMovementLocked(status != "running");
            if (session != null) session.StartPlayClock(localStartRealtime);
        }

        /// <summary>
        /// A clock on this device's real-time axis (Time.realtimeSinceStartupAsDouble, the play clock's) that any thread
        /// may read: the high-resolution Stopwatch plus the offset measured now, on the main thread. No engine API is
        /// called when it is read (the coordinator reads it on its worker, inside the request's lock).
        /// </summary>
        static Func<double> RealtimeClockForWorkers()
        {
            var frequency = (double)System.Diagnostics.Stopwatch.Frequency;
            var offset = Time.realtimeSinceStartupAsDouble - System.Diagnostics.Stopwatch.GetTimestamp() / frequency;
            return () => System.Diagnostics.Stopwatch.GetTimestamp() / frequency + offset;
        }

        /// <summary>
        /// The coop flow's stop reason, shown now: stops this device when it is not stopped yet, otherwise replaces the
        /// shown reason (e.g. the host's report of which participants confirmed a shared stop). Never resumes play.
        /// </summary>
        public void ReportCoopHalt(string reason)
        {
            if (halt == null) HaltCoop(reason);
            else halt = reason;
        }

        /// <summary>Coop play cannot go on on this device (connection ended, start failed): stops with the reason on screen.</summary>
        public void HaltCoop(string reason)
        {
            if (halt != null) return;
            if (controlled != null) Halt(reason);
            else ReportFatal(reason);
        }

        /// <summary>
        /// Coop player mode, once per frame: the hint (the flow's message, the end of the session, a refusal, a delayed
        /// request) and, while exploring, the door under the screen center — E slides it up here and sends one Prepare
        /// request (same aim and E rules as single play, also with the map shown). Walking goes on meanwhile.
        /// </summary>
        void UpdateCoopPlay()
        {
            var now = Time.realtimeSinceStartupAsDouble;
            foreach (var w in new[] { prepareWait, enterWait, returnWait })
                if (w != null && !w.Delayed && now - w.SentAt >= coopWaitNotice) w.Delayed = true;
            var text = coopHint ?? (status == "success" && SuppressGoalHint ? null : coopEndText);   // a result screen shows the goal instead
            if (text == null && coopNote != null && now < coopNoteUntil) text = coopNote;
            if (text == null && returnWait != null && returnWait.Delayed)
                text = "호스트의 시작 방 복귀 확인이 늦어지고 있습니다. 확인되면 시작 방으로 옮겨집니다(Esc로 메인 메뉴에 나갈 수도 있습니다)";
            else if (text == null && enterWait != null && enterWait.Delayed)
                text = "호스트의 입장 확인이 늦어지고 있습니다. 다음 방으로는 확인 뒤에 갈 수 있습니다(Esc로 메인 메뉴에 나갈 수도 있습니다)";
            else if (text == null && prepareWait != null && prepareWait.Delayed)
                text = "호스트의 문 열기 확인이 늦어지고 있습니다. 확인 뒤에 그 문으로 지나갈 수 있습니다(Esc로 메인 메뉴에 나갈 수도 있습니다)";
            if (text == null && CanUseCoopDoors && controlled.TryGetAimedDoor(DoorReach, out var door) && worldView.CanOpen(door, out var id))
            {
                if (controlled.InteractPressed && worldView.OpenDoor(id, out var side)) SendCoop(RequestKind.Prepare, side, id);   // prompt cleared; the same door is no longer openable here
                else text = "[E] 문 열기";
            }
            SetHint(text);
        }

        /// <summary>
        /// A door can be opened: exploring, no door opening of this device waiting, and no entry waiting (its confirmed
        /// room — the opening's ExpectedFrom — is about to change).
        /// </summary>
        bool CanUseCoopDoors =>
            controlled != null && halt == null && !coopWaiting && prepareWait == null && enterWait == null && returnWait == null && status == "running" && coopView != null && worldView != null && controlled.CanInteract;

        /// <summary>
        /// After every Update in coop: one entry at most at a time — when this player stands 0.15m or more past a boundary of
        /// its confirmed room through a passable doorway (also right after a confirmation, when it had walked back
        /// meanwhile), one Enter request goes to the host and walking goes on. Nothing is checked while an entry of this
        /// device waits (it is never sent twice), before the common start or after the end.
        /// </summary>
        void LateUpdateCoop()
        {
            if (controlled == null || halt != null || coopWaiting || enterWait != null || returnWait != null || status != "running" || coopView == null || worldView == null) return;   // a waiting return: its outcome decides the room first
            if (controlled != player || !controlled.Placed) return;              // TrackPlayer releases it next frame
            if (worldView.TryGetPassedSide(controlled.transform.position, true, out var side)) SendCoop(RequestKind.Enter, side, default);
        }

        /// <summary>
        /// One request of this device to the host, from its confirmed room (sent as ExpectedFrom — no entry of this device
        /// waits then, so that room is current). Walking is never stopped for it; an entry limits the passable doorways to
        /// the one it went through until its outcome. Nothing is shown as done before the host confirms it. A request that
        /// cannot be sent is handled like a refusal at once (the door closes again; a player outside goes back).
        /// </summary>
        void SendCoop(RequestKind kind, Direction side, RoomConnection door)
        {
            var from = coopView.PlayerPosition(localPlayer);
            var p = new CoopPending
            {
                RequestId = ++nextRequest, Kind = kind, Side = side, From = from, To = kind == RequestKind.Return ? GridPos.Origin : from.Add(Directions.Offset(side)), Door = door,
                SentAt = Time.realtimeSinceStartupAsDouble,
            };
            if (kind == RequestKind.Enter)
            {
                enterWait = p;
                worldView.LimitPassage(RoomConnection.Between(p.From, p.To));   // the two rooms only, until the outcome
            }
            else if (kind == RequestKind.Return) returnWait = p;            // the body is moved only after the host confirms it
            else prepareWait = p;
            coopNote = null;
            SetHint(null);
            var sent = false;
            try
            {
                sent = coopSend != null && coopSend(kind, from, side, p.RequestId);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            if (sent) return;
            p.Outcome = RequestOutcome.Unknown;                                   // never reached the host: nothing changed there
            p.Reason = "요청을 호스트에게 보낼 수 없습니다";
            TryCompletePending();
        }

        /// <summary>
        /// The host's outcome of one of this device's requests — its answer or the answer to a query of the same RequestId.
        /// Only a waiting request counts and only its first known outcome (a later, repeated or other answer changes
        /// nothing; a completed request's late answer never touches a newer one or the other kind). Pending keeps waiting.
        /// </summary>
        public void ApplyCoopReply(long requestId, RequestOutcome outcome, long revision, string reason)
        {
            var p = prepareWait != null && prepareWait.RequestId == requestId ? prepareWait : enterWait != null && enterWait.RequestId == requestId ? enterWait
                : returnWait != null && returnWait.RequestId == requestId ? returnWait : null;
            if (!coopMode || p == null || p.Outcome != null || outcome == RequestOutcome.Pending) return;
            p.Outcome = outcome;
            p.Target = outcome == RequestOutcome.Accepted || outcome == RequestOutcome.Failed ? revision : 0;
            p.Reason = !string.IsNullOrEmpty(reason) ? reason
                : outcome == RequestOutcome.Unknown ? "호스트가 요청을 받지 못했습니다" : outcome == RequestOutcome.Failed ? "호스트에서 처리 중 오류가 났습니다" : "호스트가 요청을 거절했습니다";
            TryCompletePending();
        }

        /// <summary>Ends each waiting request whose outcome is known and shown (see Complete); the door opening and the entry independently.</summary>
        void TryCompletePending()
        {
            prepareWait = Complete(prepareWait);
            enterWait = Complete(enterWait);
            returnWait = Complete(returnWait);
        }

        /// <summary>
        /// One waiting request, ended once its outcome is known and, for Accepted or Failed, the shown copy has reached that
        /// revision (it stays waiting otherwise and is returned). A door opening: refused, unknown or failed closes the door
        /// again unless the shown state has it open; accepted needs nothing (the view opens the passage). An entry: the
        /// passage limit ends (the confirmed room's doorways follow the shown state again); confirmed into the entered room,
        /// the HUD shows it — the player is never moved or turned, also when it stands elsewhere now (it walked back: the
        /// next frame enters back from there); not confirmed (refused, unknown, or failed without that move), the player
        /// goes back inside its confirmed room only when it stands outside it, once, facing kept. Walking is never locked
        /// or unlocked here (menus, the start gate and the end keep their own locks).
        /// </summary>
        CoopPending Complete(CoopPending p)
        {
            if (p == null || coopView == null || p.Outcome == null) return p;
            var outcome = p.Outcome.Value;
            if ((outcome == RequestOutcome.Accepted || outcome == RequestOutcome.Failed) && coopView.Revision < p.Target) return p;   // that revision is not shown yet
            if (p.Kind == RequestKind.Return)
            {
                // Accepted: the shown state has reached its revision, so AdoptCoopState has already put the body in the start
                // room (or stopped play saying why). Otherwise nothing moved here; the reason is told.
                if (outcome == RequestOutcome.Accepted) FinishUnstuck(halt == null && controlled != null, halt ?? "배치된 플레이어가 없어 시작 방으로 옮기지 못했습니다");
                else
                {
                    Debug.Log($"[협동] 시작 방 복귀 요청 {p.RequestId} {(outcome == RequestOutcome.Failed ? "처리 오류" : "거절")}: {p.Reason}");
                    ShowNote($"시작 방으로 돌아갈 수 없습니다 — {p.Reason}");
                    FinishUnstuck(false, outcome == RequestOutcome.Failed ? $"{p.Reason}. 호스트의 현재 상태로 맞췄습니다" : p.Reason);
                }
                return null;
            }
            if (p.Kind == RequestKind.Enter && worldView != null) worldView.LimitPassage(null);
            if (controlled == null || halt != null) return null;
            if (outcome != RequestOutcome.Accepted)
            {
                Debug.Log($"[협동] 요청 {p.RequestId} {(outcome == RequestOutcome.Failed ? "처리 오류" : "거절")}: {p.Reason}");
                var what = p.Kind == RequestKind.Prepare ? "문을 열 수 없습니다" : "입장할 수 없습니다";
                ShowNote(outcome == RequestOutcome.Failed ? $"{what} — {p.Reason}. 호스트의 현재 상태로 맞췄습니다" : $"{what} — {p.Reason}");
            }
            if (p.Kind == RequestKind.Prepare)
            {
                if (outcome != RequestOutcome.Accepted && worldView != null) worldView.RevertOpening(p.Door);   // stays open when the shown state has it open
                return null;
            }
            var here = coopView.PlayerPosition(localPlayer);
            if (here == p.To)
            {
                SetHudWords(coopShownWord, hudTarget);                             // the entered room is the confirmed one now (where the player stands is not touched)
                return null;
            }
            if (outcome == RequestOutcome.Accepted)
            {
                Halt($"입장이 확정됐지만 확정 위치 {here}가 입장한 방 {p.To}와 다릅니다");   // its own change moved it: should not happen
                return null;
            }
            if (worldView != null && worldView.TryGetPassedSide(controlled.transform.position, false, out var side)) ReturnInside(side);   // only when it really stands outside
            return null;
        }

        /// <summary>An entry that was not confirmed: the player, standing outside its confirmed room toward `side`, goes back inside by that doorway (facing kept).</summary>
        bool ReturnInside(Direction side)
        {
            var here = coopView.PlayerPosition(localPlayer);
            string failure;
            if (worldView == null || !worldView.TryGetShownRoom(here, out var room) || !worldView.TryGetInsidePoint(here, side, ReturnDepth, out var point))
                failure = $"확정된 방 {here}이 3D로 표시되지 않았습니다";
            else controlled.MoveOnto(room, point, out failure);
            if (failure == null)
            {
                coopPlacement++;                                           // put back, not walked: others never glide across the wall
                return true;
            }
            Halt($"입장이 확정되지 않아 원래 방으로 돌아가지 못했습니다 — {failure}");
            return false;
        }

        void ShowNote(string text)
        {
            coopNote = text;
            coopNoteUntil = Time.realtimeSinceStartupAsDouble + NoteSeconds;
        }

        /// <summary>
        /// One confirmed change of the host (any player's) for the copy this device shows: Applied (the copy, map and
        /// view follow it), Ignored (another session's, or a revision already shown) or Gap (a revision is missing, or
        /// the change does not fit this copy — `failure` says why; nothing applied, the whole state is needed).
        /// </summary>
        public CoopChangeResult ApplyCoopChange(SessionChange c, out string failure)
        {
            failure = null;
            if (!coopMode || coopView == null || c == null || c.SessionId != coopView.SessionId || c.Revision <= coopView.Revision) return CoopChangeResult.Ignored;
            SessionCopy next;
            try
            {
                next = coopView.Apply(c);
            }
            catch (ArgumentException e)
            {
                failure = e.Message;
                return CoopChangeResult.Gap;
            }
            SyncCoopMap(c, next);
            AdoptCoopState(next);
            foreach (var w in new[] { prepareWait, enterWait, returnWait })
                if (w != null && w.Outcome == null && c.PlayerId == localPlayer && c.RequestId == w.RequestId)
                {
                    w.Outcome = RequestOutcome.Accepted;                           // this request's own confirmed change
                    w.Target = c.Revision;
                }
            TryCompletePending();
            return CoopChangeResult.Applied;
        }

        /// <summary>
        /// The host's whole state of a later revision (after a missed change, or sent by the host to a device behind). The
        /// world is brought up to date only: a waiting request's outcome still comes from the host (a query of its
        /// RequestId), never from what the state shows. This player's confirmed room may differ only by its own waiting
        /// entry; anything else fails with the reason (play stops on this device).
        /// </summary>
        public bool ApplyCoopCopy(SessionCopy copy, out string failure)
        {
            failure = null;
            if (!coopMode || coopView == null || copy == null) return false;
            var theirs = copy.SessionId == coopView.SessionId ? copy.FindPlayer(localPlayer) : null;
            if (theirs == null)
            {
                failure = "다른 세션이거나 내 플레이어가 없는 상태입니다";
                return false;
            }
            if (copy.Revision <= coopView.Revision) return true;                  // nothing newer
            var mine = coopView.PlayerPosition(localPlayer);
            var entering = enterWait != null && theirs.Position == enterWait.To;
            var returned = theirs.Returns > coopView.FindPlayer(localPlayer).Returns && theirs.Position == GridPos.Origin;   // a confirmed return this device missed
            if (theirs.Position != mine && !entering && !returned)
            {
                failure = $"내 확정 위치가 {mine}에서 {theirs.Position}로 설명 없이 바뀌었습니다";
                return false;
            }
            SyncCoopMap(null, copy);
            AdoptCoopState(copy);
            TryCompletePending();
            return true;
        }

        /// <summary>
        /// The copy becomes the shown state: status, the end text, the 3D view around this player's confirmed room, a
        /// confirmed return of this player carried out (CarryOutReturn), and — once per game, whoever reached the goal, from
        /// a change or a whole state alike — the session's one confirmed result handed to the result screen (ReportGoal;
        /// a repeated or older state never reports again). Nothing here stops the coop flow's acks or recovery.
        /// </summary>
        void AdoptCoopState(SessionCopy next)
        {
            coopView = next;
            status = next.Status;
            if (status != "running" && coopEndText == null)
            {
                coopEndText = CoopEndText(next);
                Debug.Log($"[협동] 탐색 종료 — {coopEndText.Replace("\n", " / ")}");
            }
            if (halt == null && controlled != null)
            {
                ShowCoopState();
                if (halt == null) CarryOutReturn();
            }
            if (next.Status == "success" && next.Result != null) ReportGoal(next.Result);   // goalReported keeps it to once
        }

        /// <summary>
        /// This player's confirmed returns beyond those already carried out (from its own change, or a whole state after a
        /// missed one): the body goes on the start room's floor once — the view is already centered there — and the others
        /// see a placement (no glide across walls). A repeated or older state carries nothing out again. A failure stops play.
        /// </summary>
        void CarryOutReturn()
        {
            var me = coopView.FindPlayer(localPlayer);
            if (me == null || me.Returns <= placedReturns) return;
            placedReturns = me.Returns;                                    // once per confirmed return, whatever happens below
            if (me.Position != GridPos.Origin) return;                    // entered again since (cannot be this device's own): nothing to put back
            var failure = PlaceInStartRoom(controlled, me.Position, true, null);   // ShowCoopState has just shown the view around it
            if (failure != null)
            {
                Halt($"시작 방 복귀 배치 실패 — {failure}");
                return;
            }
            coopPlacement++;                                               // placed, not walked: others never glide to here
            SetHudWords(coopShownWord, hudTarget);
        }

        /// <summary>
        /// The shown copy around this player's confirmed room (never moved for another player's change; the room left and
        /// the room entered by a waiting entry are both within the display radius, so the floor under the player stays). A
        /// display failure stops this device only. After the end, walking stops (look and Escape go on).
        /// </summary>
        void ShowCoopState()
        {
            var radius = GameSettings.DisplayRadius;
            RoomViewSnapshot snapshot;
            try
            {
                snapshot = RoomViewSnapshot.Capture(coopView, localPlayer, dataset.GetWord, radius);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Halt("확정 상태의 표시 정보를 만들지 못했습니다");
                return;
            }
            if (!ShowView(snapshot, out var failure))
            {
                Halt($"확정 상태 표시 준비 실패 — {failure}");
                return;
            }
            shownRadius = radius;
            coopShownWord = snapshot.CurrentWord;
            if (status != "running") controlled.SetMovementLocked(true);   // the end: no more exploring (a waiting entry still completes)
        }

        /// <summary>The explored map follows the shown copy: the change itself, or the whole copy (full state, late tracking, or a map gap).</summary>
        void SyncCoopMap(SessionChange c, SessionCopy next)
        {
            if (!TrackExploredMap) return;
            if (c != null && !mapResync && exploredMap.HasData && exploredMap.Apply(MapUpdate.FromSessionChange(c, next, dataset.GetWord), out _) != MapApplyResult.Gap) return;
            if (exploredMap.Apply(MapUpdate.FromCopy(next, dataset.GetWord), out _) == MapApplyResult.Applied) mapResync = false;
        }

        /// <summary>What the end of the session looks like on this device (the existing Escape → main menu stays the way out).</summary>
        string CoopEndText(SessionCopy c)
        {
            const string exit = "Esc로 메인 메뉴에 돌아갈 수 있습니다";
            if (c.Status != "success") return $"탐색 종료: {StatusText.Korean(c.Status)}\n{exit}";
            var r = c.Result;
            if (r == null) return $"목표 도달 — 탐색이 끝났습니다\n{exit}";
            var who = r.ReachedBy == localPlayer ? "내가 목표 방에 도착했습니다" : $"{(coopName != null ? coopName(r.ReachedBy) : r.ReachedBy)}이(가) 목표 방에 도착했습니다";
            var time = double.IsNaN(r.Seconds) ? "" : $"소요 {r.Seconds:0.0}초, ";
            var mine = r.TryGetPlayer(localPlayer, out var p) ? $", 내 방문 {p.VisitedRooms}개" : "";
            return $"목표 도달 — {who}\n{time}팀 방문 {r.TeamVisitedRooms}개{mine}\n{exit}";
        }

        // ---- coop host (the authority: requests in, confirmed changes out; display goes through the messages) -------
        /// <summary>
        /// Host only: one request (this host's own or a participant's, PlayerId from the real sender) checked and applied
        /// by the coordinator on the worker — all checks, the change and its confirmed revision inside its lock — then
        /// `done` on the main thread with the outcome: the change to send to everyone (a first accepted request), the
        /// first outcome again for a duplicate (no change, no new copy), a refusal (nothing changed), or Failed — an error
        /// while it was applied: the state may have changed partly, so it is never answered as a refusal (the coordinator
        /// recorded it: the same request is never applied again). Nothing is shown here.
        /// </summary>
        public async void SubmitCoopHost(SessionRequest request, Action<CoopHostOutcome> done)
        {
            var s = session;
            if (!coopMode || s == null || cts == null)
            {
                done(CoopHostOutcome.Refused("호스트 세션이 준비되지 않았습니다"));   // nothing was run
                return;
            }
            var token = cts.Token;
            try
            {
                var result = await Task.Run(() => s.Submit<bool>(request, null, token), token);
                if (this == null || token.IsCancellationRequested || s != session) return;   // the scene or game ended: nothing is sent
                done(CoopHostOutcome.From(result));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (this != null && s == session && !token.IsCancellationRequested) done(CoopHostOutcome.Failed("호스트에서 요청을 처리하는 중 오류가 났습니다"));
            }
        }

        /// <summary>Host only: the whole current state (world, players, session and result of one revision) and the fixed settings, read on the worker.</summary>
        public async void ReadCoopHost(Action<SessionCopy, GenerationConfig, string> done)
        {
            var s = session;
            if (!coopMode || s == null || cts == null)
            {
                done(null, null, "호스트 세션이 준비되지 않았습니다");
                return;
            }
            var token = cts.Token;
            try
            {
                var copy = await Task.Run(() => s.Read(r => r.CopyAll()), token);
                if (this == null || token.IsCancellationRequested || s != session) return;
                done(copy, s.Config, null);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (this != null && s == session && !token.IsCancellationRequested) done(null, null, "호스트에서 전체 상태를 읽지 못했습니다");
            }
        }

        /// <summary>A display radius change in coop: the same confirmed copy captured again (nothing generated).</summary>
        void RefreshCoopView(int radius)
        {
            if (coopView == null) return;
            try
            {
                var snapshot = RoomViewSnapshot.Capture(coopView, localPlayer, dataset.GetWord, radius);
                if (ShowView(snapshot, out var failure)) shownRadius = radius;
                else if (controlled != null) Halt($"표시 거리 변경 후 방 표시 준비 실패 — {failure}");
                else shownRadius = radius;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (controlled != null) Halt("표시 거리 변경 중 오류가 났습니다(Console 확인)");
                else shownRadius = radius;
            }
        }

        // ---- output -----------------------------------------------------------------------------------------
        /// <summary>Console text of one change, built inside the request's capture (worker) from the session reader.</summary>
        static string Describe(SessionReader r, GenerationUpdate u, WordRoomDataset data, bool verbose)
        {
            string Word(int w) => data.GetWord(w);
            var who = u.PlayerId ?? LocalPlayer;
            var here = r.PlayerPosition(who);
            var sb = new StringBuilder();
            if (u.Kind == "prepare")                         // door opened: the room behind it, not entered yet
            {
                r.TryGetWord(u.To, out var there);
                sb.Append($"문 열기 주변 준비: {Directions.Between(u.From, u.To).Map(Directions.Korean)} {Word(there)} {u.To} — 입장 전(현재 위치·이동·방문 그대로)");
                foreach (var d in Directions.DisplayOrder)
                {
                    var q = u.To.Add(Directions.Offset(d));
                    sb.Append("\n  ").Append(Directions.Korean(d)).Append(": ").Append(r.TryGetWord(q, out var w) ? Word(w) : "미생성");
                }
                sb.Append('\n');
            }
            r.TryGetWord(here, out var word);                // the player's room always exists
            sb.Append($"현재: {Word(word)} {here} | 목표: {Word(r.TargetWord)}");
            foreach (var d in Directions.DisplayOrder)
            {
                var q = here.Add(Directions.Offset(d));
                sb.Append('\n').Append(Directions.Korean(d)).Append(": ").Append(r.TryGetWord(q, out var w) ? Word(w) : "미생성");
            }
            if (u.Arrived) sb.Append("\n목표 도착");
            else if (r.Status != "running") sb.Append($"\n실험 종료: {StatusText.Korean(r.Status)}{(r.FailureReason != null ? $" — {r.FailureReason}" : "")}");
            if (verbose) AppendDetails(sb, r, u, data, who);
            return sb.ToString();
        }

        static void AppendDetails(StringBuilder sb, SessionReader r, GenerationUpdate u, WordRoomDataset data, string who)
        {
            string Word(int w) => data.GetWord(w);
            var f = u.Fill;
            sb.Append("\n--- 상세");
            sb.Append($"\n{KindText(u)} · 변경 순번 {u.Revision} · " +
                      $"이동 {u.Moves} · 고유 방문 {r.PlayerUniqueVisited(who)}(팀 {r.TeamVisitedCount}) · 생성 방 {r.RoomCount} · 목표 유도 강도 {u.GuidanceStrength.ToString("0.#####", CultureInfo.InvariantCulture)}");
            if (f == null) return;
            switch (f.Stage)
            {
                case "none":
                    sb.Append("\n주변 4방향이 이미 모두 생성되어 있어 새로 생성하지 않음");
                    return;
                case "skipped_same_state":
                    sb.Append("\n이 방은 같은 지도 상태에서 이미 생성에 실패해 다시 시도하지 않음(원본 정책: 방 수가 바뀐 뒤에만 재시도)");
                    break;
                default:
                    sb.Append($"\n생성 {f.Seconds:0.000}초 · 단계 {StageText(f.Stage)}" +
                              (double.IsNaN(f.Threshold) ? "" : $" · 관계 기준 {f.Threshold.ToString("0.##", CultureInfo.InvariantCulture)}") +
                              $" · {(f.Complete ? "4방향 완성" : "미완성")}");
                    break;
            }
            foreach (var s in f.Steps)
                sb.Append($"\n  기준 {s.Threshold.ToString("0.##", CultureInfo.InvariantCulture)}: {StepText(s.Result)}" +
                          $" (후보 {string.Join(", ", s.CandidateCounts.Select(kv => $"{kv.Key} {kv.Value}"))}{(s.Nodes > 0 ? $", 검사 {s.Nodes}" : "")})");
            if (!string.IsNullOrEmpty(f.DbMessage)) sb.Append("\n관계 DB 실패 사유: ").Append(f.DbMessage);
            foreach (var a in f.SupplementAttempts) sb.Append("\n  보충 탐색: ").Append(a);
            foreach (var room in f.Added)
            {
                var dir = Directions.Between(f.Center, room.Position);
                sb.Append($"\n+ {(dir.HasValue ? Directions.Korean(dir.Value) : "")} {room.Position} {Word(room.Word)} · {StageText(room.Stage)}" +
                          (double.IsNaN(room.Threshold) ? "" : $" 기준 {room.Threshold.ToString("0.##", CultureInfo.InvariantCulture)}") +
                          $" · 후보 {room.Candidates} · 점수 {room.Score:0.0000}" + (room.GuidanceBonus != 0 ? $" (유도 +{room.GuidanceBonus:0.00000})" : ""));
                foreach (var l in room.Links)
                {
                    var ld = Directions.Between(room.Position, l.Neighbor);
                    var name = $"{(ld.HasValue ? Directions.Korean(ld.Value) : "")} {Word(l.NeighborWord)}";
                    if (l.Source == "relation_db")
                        sb.Append($"\n    {name}: 관계 DB [{l.SenseId}]–[{l.NeighborSenseId}] YES {l.YesScore:0.0000} (기준 {l.Threshold.ToString("0.##", CultureInfo.InvariantCulture)}{(l.Relaxed ? ", 완화" : "")})");
                    else
                        sb.Append($"\n    {name}: fastText 유사도 {l.Similarity:0.0000} · DB 상태 {StatusText.State(l.DbState)}" +
                                  (l.Restricted ? " · 제한 표기" : "") + (l.Weak ? $" · 약한 연결(최후 수단: {ReasonText(l.LastResortReason)})" : ""));
                }
            }
            foreach (var e in f.Empty) sb.Append($"\n- {e.Position} 빈 위치: {ReasonText(e.Reason)}");
        }

        static string KindText(GenerationUpdate u)
        {
            var step = $"{Directions.Between(u.From, u.To).Map(Directions.Korean)} {u.From}→{u.To}";
            var visit = u.FirstVisit ? " (첫 방문)" : " (재방문)";
            switch (u.Kind)
            {
                case "start": return "시작";
                case "prepare": return $"문 열기 주변 준비 {step} (입장 전: 이동·방문 변화 없음)";
                case "enter": return $"입장 {step}{visit} · 주변 생성 없음(문을 열 때 준비됨)";
                case "return": return $"끼임방지: {u.From}에서 시작 방으로 복귀 {u.Returns}회째 (이동·방문·경로 집계 없음, 주변 생성 없음)";
                default: return $"이동 {step}{visit}";
            }
        }

        static string StageText(string stage)
        {
            switch (stage)
            {
                case "relation_db": return "관계 DB";
                case "fasttext": return "fastText 보충";
                case "relation_db_partial": return "관계 DB 부분 배치(fastText 끔)";
                default: return stage;
            }
        }

        static string StepText(string result)
        {
            switch (result)
            {
                case "placed": return "배치 성공";
                case "no_candidate": return "후보 없는 위치 있음";
                case "budget_exhausted": return "탐색 한도 도달(가능한 배치가 없다는 뜻은 아님)";
                case "placement_conflict": return "위치별 후보는 있으나 함께 배치 불가";
                default: return result;
            }
        }

        static string ReasonText(string reason)
        {
            switch (reason)
            {
                case "no candidate": return "후보 없음";
                case "no placement within the search limit": return "탐색 한도 안에서 배치를 찾지 못함";
                case "conflict with the other planned rooms": return "함께 계획한 다른 방과 충돌";
                case "no candidate at this position meets the preferred fastText minimum without restricted surfaces":
                    return "이 위치에 선호 하한·제한 표기 조건을 만족하는 후보 없음";
                case null: return "";
                default:
                    return reason.StartsWith("better candidates conflicted", StringComparison.Ordinal)
                        ? "더 나은 후보가 다른 계획 방과 충돌" + (reason.Contains("search limit") ? "(탐색 한도 도달)" : "")
                        : reason;
            }
        }
    }

    /// <summary>What applying one confirmed coop change did (see RoomGenerationDebug.ApplyCoopChange).</summary>
    public enum CoopChangeResult
    {
        Applied,
        /// <summary>Another session's change or a revision already shown: nothing to do.</summary>
        Ignored,
        /// <summary>A revision is missing (or the change does not fit the shown copy): the whole state is needed.</summary>
        Gap,
    }

    /// <summary>
    /// The host coordinator's outcome of one coop request: Accepted (Revision = the confirmed revision; Change = the
    /// change to send to everyone, only for a first accepted request — a duplicate gets its first outcome again with no
    /// change and no new copy), Refused (checked and refused: nothing changed) or Failed (an error while it was applied:
    /// the state may have changed; Revision is the revision after it when the coordinator reported one, else 0 — the
    /// caller reads the state). Refusal is the reason of Refused or Failed.
    /// </summary>
    public sealed class CoopHostOutcome
    {
        public RequestOutcome Outcome { get; private set; }
        public bool Accepted => Outcome == RequestOutcome.Accepted;
        public bool Duplicate { get; private set; }
        public long Revision { get; private set; }
        public string Refusal { get; private set; }
        public SessionChange Change { get; private set; }

        internal static CoopHostOutcome From(RequestResult<bool> r) => new CoopHostOutcome
        {
            Outcome = r.Accepted ? RequestOutcome.Accepted : r.Failure != null ? RequestOutcome.Failed : RequestOutcome.Refused,
            Duplicate = r.Duplicate,
            Revision = r.Accepted ? r.Update.Revision : r.Failure != null ? r.FailedRevision : 0,
            Refusal = r.Refusal ?? r.Failure,
            Change = r.Accepted && !r.Duplicate ? SessionChange.FromUpdate(r.Update) : null,
        };

        internal static CoopHostOutcome Refused(string reason) => new CoopHostOutcome { Outcome = RequestOutcome.Refused, Refusal = reason };

        internal static CoopHostOutcome Failed(string reason) => new CoopHostOutcome { Outcome = RequestOutcome.Failed, Refusal = reason };
    }

    static class NullableExtensions
    {
        public static string Map(this Direction? d, Func<Direction, string> f) => d.HasValue ? f(d.Value) : "";
    }
}
