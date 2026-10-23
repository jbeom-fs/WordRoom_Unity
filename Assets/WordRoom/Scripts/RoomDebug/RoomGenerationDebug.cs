using System;
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
    /// Debug-only check of the room generator: Inspector settings, arrow keys (↑ 상, ↓ 하, ← 좌, → 우) and Console
    /// output. No map or network; the only UI is the optional one-line PlayHint in player mode. Generation runs on
    /// a worker thread; input is ignored (not queued) while data is loading or a request is running. The settings
    /// are copied when an experiment starts.
    /// Optional 3D view (RoomWorldView): after each finished request a snapshot of the current room and its
    /// existing neighbors is taken on the worker and shown on the main thread; without it only Console output.
    /// Optional first-person player: placed on the start room floor once it is shown; while it is set and active,
    /// the arrow keys do not move the logical position and the debug camera is not moved (one rule, ActivePlayer,
    /// for placement, input, arrow keys and camera). The player opens doors of the current room with E: the door
    /// slides up while one generator Prepare fills the neighbors of the room behind it (no move, no visit); the
    /// doorway opens when both are done. Walking through it is one generator Enter (no fill), and walking stays
    /// locked during each request until its result is shown (and, for an entry, the player stands on the arrival
    /// floor). The arrow keys keep the combined Move. A player that is disconnected, replaced or disabled is
    /// stopped and only placed again by a new experiment.
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

        [Header("목표 유도 (Python 1.1.0 기본: 켬, 0 → 0.03, 10부터 40에 걸쳐, 고유 방문 방 수)")]
        [SerializeField] bool targetGuidance = true;
        [SerializeField] double guidanceStrength = 0.0;
        [SerializeField] int guidanceStart = 10;
        [SerializeField] int guidanceRamp = 40;
        [SerializeField] double guidanceMax = 0.03;
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

        [Tooltip("진행 정지·플레이어 미배치 안내 뒤에 붙는 다음 행동 안내. 메뉴로 실행하는 씬은 메인 메뉴 복귀를 안내합니다.")]
        [SerializeField] string haltAdvice = "새 실험을 시작하세요";

        const float DoorReach = 2f;          // E reaches doors up to this far from the eye along the screen center

        WordRoomDataService service;
        RoomGenerator generator;
        WordRoomDataset dataset;
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
        string HaltHint => halt != null ? $"진행 정지: {halt.Replace("·", ", ")}\n{haltAdvice}" : null;

        void Awake()
        {
            service = GetComponent<WordRoomDataService>();
            if (worldView == null) worldView = GetComponent<RoomWorldView>();
            if (hint == null) hint = GetComponent<PlayHint>();
            if (hud == null) hud = GetComponent<WordRoomHud>();
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
            SetHint(null);
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
            else if (s.State == DataLoadState.Ready && startWhenDataReady && generator == null && !busy)
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
            generator = null;
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
            shownRadius = -1;
            SetHudWords(null, null);                              // the previous experiment's words never come back
            if (view) ViewCall(() => worldView.BeginSession());   // previous rooms/doors back to the pools, door states cleared
            Debug.Log($"[방 생성] 새 실험 준비: {d.GetWord(start)} → {d.GetWord(target)} · {config}" +
                      (config.TargetGuidance ? " · 목표 근접도 계산 중" : ""));
            var radius = GameSettings.DisplayRadius;
            try
            {
                var (gen, update, snapshot) = await Task.Run(() =>
                {
                    var g = RoomGenerator.Create(d, config, start, target, token);
                    var u = g.Start(token);
                    return (g, u, view ? RoomViewSnapshot.Capture(g.Map, d.GetWord, radius) : null);   // taken here, after the request finished
                }, token);
                if (this == null || token.IsCancellationRequested) return;
                generator = gen;
                status = update.Status;
                LogUpdate(update);
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
            if (ActivePlayer != null)
            {
                if (direction != null)
                    Debug.Log("[방 생성] 1인칭 플레이어가 연결되어 방향키 논리 이동을 하지 않습니다(플레이어 위치와 지도 위치 불일치 방지). 방향키 디버그는 Player 칸을 비우고 사용하세요.");
                UpdatePlay();
                return;
            }
            SetHint(HaltHint);                // debug mode: only a stop reason stays on screen
            if (direction == null) return;
            if (busy || generator == null)
            {
                if (verboseLog) Debug.Log("[방 생성] 로딩·생성 중이라 입력을 무시했습니다 (입력은 쌓아두지 않음).");
                return;
            }
            var map = generator.Map;
            if (map.Status != "running")
            {
                Debug.Log($"[방 생성] 실험이 끝나 이동하지 않습니다: {StatusText.Korean(map.Status)}");
                return;
            }
            var dest = map.Position.Add(Directions.Offset(direction.Value));
            if (!map.HasRoom(dest))
            {
                var reason = map.EmptyReason(map.Position, dest);
                Debug.Log($"[방 생성] {Directions.Korean(direction.Value)}: 미생성이라 이동할 수 없습니다" +
                          (reason != null ? $" — {ReasonText(reason)}" : ""));
                return;
            }
            Move(direction.Value);
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
            busy = true;
            var token = cts.Token;
            var gen = generator;
            var d = dataset;
            var view = worldView != null;
            var radius = GameSettings.DisplayRadius;
            try
            {
                var (update, refusal, snapshot) = await Task.Run(() =>
                {
                    var u = gen.Move(direction, out var r, token);
                    return (u, r, view && u != null ? RoomViewSnapshot.Capture(gen.Map, d.GetWord, radius) : null);
                }, token);
                if (this == null || token.IsCancellationRequested || gen != generator) return;   // stale result of an earlier experiment
                if (update == null) Debug.Log($"[방 생성] 이동 불가: {refusal}");     // refused: the 3D view is not moved
                else
                {
                    status = update.Status;
                    LogUpdate(update);
                    if (ShowView(snapshot, out _)) shownRadius = radius;   // Console debug: an incomplete view is logged by the view itself
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
            if (busy || generator == null || worldView == null || halt != null || shownRadius < 0) return;
            var radius = GameSettings.DisplayRadius;
            if (radius != shownRadius) RefreshView(radius);
        }

        /// <summary>
        /// Re-captures the existing map on the worker with `radius` and shows it (rooms leaving the range go back to
        /// the pool, rooms entering it are rented; the current room always stays). Only a snapshot: no Fill, Prepare,
        /// Move or Enter, so no word, visit or move changes. Runs as a request (busy), so no generator request can
        /// change the map meanwhile and an older result never overwrites a newer one.
        /// </summary>
        async void RefreshView(int radius)
        {
            busy = true;
            var token = cts.Token;
            var gen = generator;
            var d = dataset;
            try
            {
                var snapshot = await Task.Run(() => RoomViewSnapshot.Capture(gen.Map, d.GetWord, radius), token);
                if (this == null || token.IsCancellationRequested || gen != generator) return;   // stale result
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
                if (this == null || gen != generator || token.IsCancellationRequested) return;
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
            if (busy)
            {
                SetHint(null);                // start or entry request running: no caption, no "not placed"
                return;
            }
            if (controlled == null)
            {
                SetHint(generator != null ? $"플레이어가 배치되지 않았습니다\n{haltAdvice}" : null);
                return;
            }
            if (status != "running")
            {
                SetHint(status == "success" ? "목표 도달" : $"실험 종료: {StatusText.Korean(status)}");
                return;
            }
            string text = null;
            if (worldView != null && controlled.HasControlFocus && controlled.TryGetAimedDoor(DoorReach, out var door) && worldView.CanOpen(door, out var id))
            {
                if (controlled.InteractPressed && worldView.OpenDoor(id, out var side)) PrepareRoom(side);   // slide and fill start together; prompt cleared
                else text = "[E] 문 열기";
            }
            SetHint(text);
        }

        /// <summary>
        /// The door toward `side` was just opened (its slide has started): in the same frame one generator Prepare
        /// fills the empty neighbors of the room behind it on the worker, with the snapshot taken there. The current
        /// room, moves, visits and status do not change. Walking and door use are locked meanwhile (look, gravity and
        /// Escape go on); no other request is accepted or queued. The main thread then shows the result still
        /// centered on the current room (new words on the signs; the room behind the door now counts as prepared),
        /// and walking resumes. The doorway opens only when the slide has also finished (RoomWorldView.IsPassable).
        /// A partial fill is a normal result; a refusal, a failed display or an error stops play instead, with
        /// nothing undone or retried. A result after a new experiment, disabling or a player change does nothing.
        /// </summary>
        async void PrepareRoom(Direction side)
        {
            var p = controlled;
            p.SetMovementLocked(true);
            busy = true;
            SetHint(null);                                                         // no caption while waiting
            var token = cts.Token;
            var gen = generator;
            var d = dataset;
            var view = worldView != null;
            var radius = GameSettings.DisplayRadius;
            try
            {
                var (update, refusal, snapshot) = await Task.Run(() =>
                {
                    var u = gen.Prepare(side, out var r, token);
                    return (u, r, view && u != null ? RoomViewSnapshot.Capture(gen.Map, d.GetWord, radius) : null);
                }, token);
                if (this == null || token.IsCancellationRequested || gen != generator) return;   // stale result
                if (update == null)
                {
                    Halt($"{Directions.Korean(side)} 방 주변 준비가 거절되었습니다 — {refusal}");
                    return;
                }
                status = update.Status;
                LogUpdate(update);
                var shown = ShowView(snapshot, out var showFailure);
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
                if (this != null && gen == generator && !token.IsCancellationRequested) Halt("주변 준비 중 오류가 났습니다(Console 확인)");
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
            if (controlled == null || busy || halt != null || status != "running" || worldView == null) return;
            if (controlled != player || !controlled.Placed) return;                // TrackPlayer releases it next frame
            var to = controlled.transform.position;
            var from = lastPosition;
            lastPosition = to;
            if (worldView.TryDetectEntry(from, to, out var side)) EnterRoom(side);
        }

        /// <summary>
        /// Entered the neighbor on `side`: walking and door use stop at once (look, gravity and Escape go on), one
        /// generator Enter (position, moves, visits, target and test limit only — its neighbors were filled when its
        /// door was opened, so there is no generation wait) runs on the worker with the snapshot taken there, the
        /// main thread shows the result centered on the arrival room, and walking resumes where the player stands
        /// (no re-placement) once that view is complete and the arrival room is under the player. A refused entry,
        /// a failed display or an error stops play instead; nothing is undone or retried. A result arriving after
        /// a new experiment, disabling or a player change never gives control back.
        /// </summary>
        async void EnterRoom(Direction side)
        {
            var p = controlled;
            p.SetMovementLocked(true);
            busy = true;
            SetHint(null);                                                         // no caption while waiting
            var token = cts.Token;
            var gen = generator;
            var d = dataset;
            var view = worldView != null;
            var radius = GameSettings.DisplayRadius;
            try
            {
                var (update, refusal, snapshot) = await Task.Run(() =>
                {
                    var u = gen.Enter(side, out var r, token);
                    return (u, r, view && u != null ? RoomViewSnapshot.Capture(gen.Map, d.GetWord, radius) : null);
                }, token);
                if (this == null || token.IsCancellationRequested || gen != generator) return;   // stale result
                if (update == null)
                {
                    Halt($"{Directions.Korean(side)} 방 입장이 거절되었습니다 — {refusal}");
                    return;
                }
                status = update.Status;
                LogUpdate(update);
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
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (this != null && gen == generator && !token.IsCancellationRequested) Halt("입장 처리 중 오류가 났습니다(Console 확인)");
            }
            finally
            {
                busy = false;
            }
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
            RefreshHud();
        }

        /// <summary>
        /// The HUD is visible only while a first-person player is placed for this experiment and play is not stopped
        /// (also while a door opens or a request runs, and after the goal or another end). It shows the words of the
        /// last confirmed placement or entry; a door opening never changes them. Arrow-key/Console debug, a
        /// released player, a stop or a new experiment hide it. Cheap per frame: the HUD only compares strings.
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
                Debug.LogWarning("[HUD] WordRoomHud가 없어 현재·목표 단어를 화면에 표시하지 않습니다(플레이는 계속). RoomGenerationDebug 오브젝트에 WordRoomHud를 추가하세요.");
            }
        }

        void SetHint(string text)
        {
            if (hint != null) hint.Set(text);
            else if (!string.IsNullOrEmpty(text) && !hintMissingLogged)
            {
                hintMissingLogged = true;
                Debug.LogWarning("[안내] PlayHint가 없어 화면 안내를 표시하지 않습니다(플레이는 계속). RoomGenerationDebug 오브젝트에 PlayHint를 추가하세요.");
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

        // ---- output -----------------------------------------------------------------------------------------
        string Word(int w) => dataset.GetWord(w);

        void LogUpdate(GenerationUpdate u)
        {
            var map = generator.Map;
            var sb = new StringBuilder();
            if (u.Kind == "prepare")                         // door opened: the room behind it, not entered yet
            {
                map.TryGetWord(u.To, out var there);
                sb.Append($"문 열기 주변 준비: {Directions.Between(u.From, u.To).Map(Directions.Korean)} {Word(there)} {u.To} — 입장 전(현재 위치·이동·방문 그대로)");
                foreach (var d in Directions.DisplayOrder)
                {
                    var q = u.To.Add(Directions.Offset(d));
                    sb.Append("\n  ").Append(Directions.Korean(d)).Append(": ").Append(map.TryGetWord(q, out var w) ? Word(w) : "미생성");
                }
                sb.Append('\n');
            }
            map.TryGetWord(map.Position, out var here);      // the current room always exists
            sb.Append($"현재: {Word(here)} {map.Position} | 목표: {Word(map.Target)}");
            foreach (var d in Directions.DisplayOrder)
            {
                var q = map.Position.Add(Directions.Offset(d));
                sb.Append('\n').Append(Directions.Korean(d)).Append(": ").Append(map.TryGetWord(q, out var w) ? Word(w) : "미생성");
            }
            if (u.Arrived) sb.Append("\n목표 도착");
            else if (map.Status != "running") sb.Append($"\n실험 종료: {StatusText.Korean(map.Status)}{(map.FailureReason != null ? $" — {map.FailureReason}" : "")}");
            if (verboseLog) AppendDetails(sb, u);
            Debug.Log(sb.ToString());
        }

        void AppendDetails(StringBuilder sb, GenerationUpdate u)
        {
            var map = generator.Map;
            var f = u.Fill;
            sb.Append("\n--- 상세");
            sb.Append($"\n{KindText(u)} · " +
                      $"이동 {u.Moves} · 고유 방문 {map.UniqueVisited} · 생성 방 {map.RoomCount} · 목표 유도 강도 {u.GuidanceStrength.ToString("0.#####", CultureInfo.InvariantCulture)}");
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

    static class NullableExtensions
    {
        public static string Map(this Direction? d, Func<Direction, string> f) => d.HasValue ? f(d.Value) : "";
    }
}
