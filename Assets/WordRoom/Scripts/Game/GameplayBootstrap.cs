using UnityEngine;
using WordRoom.Network;
using WordRoom.RoomDebug;
using WordRoom.UnityLayer;

namespace WordRoom.Game
{
    /// <summary>
    /// Starts the game of the Gameplay scene exactly once, single or coop — never one in place of the other.
    /// Single (the main menu's request; a scene opened directly in the Editor falls back to 보통): keeps the room
    /// generator from auto-starting with its Inspector words, waits for the data (loaded once per application run),
    /// then draws two different words and a seed from the difficulty's range and starts the generator with them.
    /// Coop (opened by a confirmed coop start, CoopSession): runs CoopGameplayFlow — data report, the host's initial
    /// world, display and placement, common start — and never draws words, starts a single game or makes a
    /// coordinator on a guest. A coop entry that was lost on the way (e.g. the connection ended during the load) stops
    /// play with the reason instead of falling back to single play. Preferences are applied by the components
    /// themselves as they enable (player, HUD, hint, view radius), before the first room is shown. A data failure, an
    /// empty range or a refused start stops play with the reason on screen; nothing is drawn again. The player stays
    /// unplaced (no input, no falling) until the start room is shown.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GameplayBootstrap : MonoBehaviour
    {
        [Tooltip("시작할 방 생성 제어. 비우면 같은 오브젝트의 것을 씁니다.")]
        [SerializeField] RoomGenerationDebug game;
        [Tooltip("데이터 서비스. 비우면 같은 오브젝트의 것을 씁니다.")]
        [SerializeField] WordRoomDataService data;
        [Tooltip("협동: 시작 확정부터 모든 참가자 배치 완료까지 기다리는 최대 시간(초, 데이터 로딩 포함). 넘으면 호스트가 시작을 중단합니다.")]
        [SerializeField] float coopReadyTimeoutSeconds = CoopRules.DefaultReadyTimeoutSeconds;

        Difficulty difficulty;
        bool started;
        bool coop;
        CoopEntry coopEntry;
        string coopFailure;
        CoopGameplayFlow coopFlow;

        void Awake()
        {
            if (game == null) game = GetComponent<RoomGenerationDebug>();
            if (data == null) data = GetComponent<WordRoomDataService>();
            if (game != null) game.AutoStartFromInspector = false;     // before any Start: never the Inspector words here
            if (GameLaunch.TryConsume(out difficulty))
            {
                CoopSession.ForgetEntry();                              // an explicit single start wins over any stale coop marker
                return;
            }
            if (CoopSession.TryConsumeEntry(out coopEntry, out coopFailure))
            {
                coop = true;
                return;
            }
            Debug.Log($"[게임] 메인 메뉴의 시작 요청 없이 실행되어 기본 난이도 {GameLaunch.Label(difficulty)}로 시작합니다.");
        }

        void OnEnable()
        {
            if (data != null) data.StateChanged += OnDataState;
        }

        void OnDisable()
        {
            if (data != null) data.StateChanged -= OnDataState;
        }

        void Start()
        {
            if (game == null || data == null)
            {
                Debug.LogError("[게임] RoomGenerationDebug 또는 WordRoomDataService가 없어 게임을 시작할 수 없습니다.");
                started = true;
                if (coop && game != null) game.ReportFatal("게임 구성이 완전하지 않아 협동 게임을 시작할 수 없습니다");
                return;
            }
            if (coop)
            {
                started = true;
                if (coopEntry == null)
                {
                    game.ReportFatal($"{coopFailure}. 메인 메뉴로 돌아가 다시 협동을 시작하세요");
                    return;
                }
                CoopSession.ReadyTimeoutSeconds = Mathf.Max(10f, coopReadyTimeoutSeconds);
                coopFlow = new CoopGameplayFlow(game, data, CoopSession.Current, coopEntry);
                return;
            }
            OnDataState(data);                                           // already ready (loaded earlier) or failed
        }

        void Update()
        {
            if (coopFlow != null) coopFlow.Tick();
        }

        void OnDataState(WordRoomDataService s)
        {
            if (started || game == null || coop) return;
            if (s.State == DataLoadState.Failed)
            {
                started = true;
                game.ReportFatal($"단어 데이터를 불러오지 못했습니다 — {s.Error?.Message}");
                return;
            }
            if (!s.TryGetDataset(out var d)) return;                     // still loading
            started = true;                                              // one draw per game, whatever happens next
            if (!GameLaunch.TryPickWords(d, difficulty, out var start, out var target, out var seed, out var error))
            {
                game.ReportFatal(error);
                return;
            }
            Debug.Log($"[게임] 난이도 {GameLaunch.Label(difficulty)}(상위 {GameLaunch.VocabularyLimit(difficulty):N0}개): {d.GetWord(start)} → {d.GetWord(target)}, 시드 {seed}");
            if (!game.StartGame(start, target, seed, GameLaunch.VocabularyLimit(difficulty)))
                game.ReportFatal("게임을 시작하지 못했습니다(Console 확인)");
        }
    }
}
