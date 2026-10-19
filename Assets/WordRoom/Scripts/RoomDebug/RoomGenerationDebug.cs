using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using WordRoom.Data;
using WordRoom.Generation;
using WordRoom.UnityLayer;

namespace WordRoom.RoomDebug
{
    public enum GuidanceBasisOption
    {
        고유방문방수,
        누적이동,
    }

    /// <summary>
    /// Debug-only check of the room generator: Inspector settings, arrow keys (↑ 상, ↓ 하, ← 좌, → 우) and Console
    /// output. No UI, map, 3D room or network. Generation runs on a worker thread; input is ignored (not queued)
    /// while data is loading or a request is running. The settings are copied when an experiment starts.
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

        [Header("로그")]
        [Tooltip("생성 시간·관계 기준·보충 근거·실패 사유 등을 추가로 출력")]
        [SerializeField] bool verboseLog;

        WordRoomDataService service;
        RoomGenerator generator;
        WordRoomDataset dataset;
        CancellationTokenSource cts;
        bool busy;
        bool waitingLogged;

        void Awake() => service = GetComponent<WordRoomDataService>();

        void OnEnable()
        {
            if (service == null) service = GetComponent<WordRoomDataService>();
            service.StateChanged += OnDataState;
        }

        void OnDisable()
        {
            if (service != null) service.StateChanged -= OnDataState;
            cts?.Cancel();          // results of running work are discarded (see the token checks after await)
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
            c.Source = "데이터 패키지 settings.json(Python load_config 적용값) + Inspector(시드·어휘 범위·목표 유도)";
            return c;
        }

        /// <summary>Starts a new experiment with the current Inspector values (also from the component's context menu).</summary>
        [ContextMenu("새 실험 시작")]
        public async void StartExperiment()
        {
            if (busy)
            {
                Debug.Log("[방 생성] 생성 중이라 새 실험을 시작하지 않습니다.");
                return;
            }
            if (!service.TryGetDataset(out var d))
            {
                Debug.Log($"[방 생성] 데이터가 아직 준비되지 않았습니다 (상태: {service.State}).");
                return;
            }
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
            var config = BuildConfig(d);            // copied now; later Inspector edits do not reach this experiment
            cts?.Cancel();
            cts = new CancellationTokenSource();
            var token = cts.Token;
            busy = true;
            generator = null;
            dataset = d;
            Debug.Log($"[방 생성] 새 실험 준비: {d.GetWord(start)} → {d.GetWord(target)} · {config}" +
                      (config.TargetGuidance ? " · 목표 근접도 계산 중" : ""));
            try
            {
                var (gen, update) = await Task.Run(() =>
                {
                    var g = RoomGenerator.Create(d, config, start, target, token);
                    var u = g.Start(token);
                    return (g, u);
                }, token);
                if (this == null || token.IsCancellationRequested) return;
                generator = gen;
                LogUpdate(update);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ArgumentException e)
            {
                Debug.LogError($"[방 생성] 실험을 시작할 수 없습니다: {e.Message}");
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

        void Update()
        {
            var direction = ReadDirection();
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
            try
            {
                var (update, refusal) = await Task.Run(() =>
                {
                    var u = gen.Move(direction, out var r, token);
                    return (u, r);
                }, token);
                if (this == null || token.IsCancellationRequested || gen != generator) return;
                if (update == null) Debug.Log($"[방 생성] 이동 불가: {refusal}");
                else LogUpdate(update);
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

        // ---- output -----------------------------------------------------------------------------------------
        string Word(int w) => dataset.GetWord(w);

        void LogUpdate(GenerationUpdate u)
        {
            var map = generator.Map;
            var sb = new StringBuilder();
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
            sb.Append($"\n{(u.Kind == "start" ? "시작" : $"이동 {Directions.Between(u.From, u.To).Map(Directions.Korean)} {u.From}→{u.To}{(u.FirstVisit ? " (첫 방문)" : " (재방문)")}")} · " +
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
