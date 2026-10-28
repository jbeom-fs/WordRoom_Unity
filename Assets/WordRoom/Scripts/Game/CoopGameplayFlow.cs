using System.Linq;
using UnityEngine;
using WordRoom.Data;
using WordRoom.Generation;
using WordRoom.Network;
using WordRoom.RoomDebug;
using WordRoom.UnityLayer;

namespace WordRoom.Game
{
    /// <summary>
    /// The Gameplay side of one confirmed coop start, driven by GameplayBootstrap once per frame (plain object; the
    /// session manager is CoopSession, the display is RoomGenerationDebug). Steps, each only after the one before:
    /// 1) this device's data (WordRoomDataService) — its real PackageId (or the failure) goes to the host;
    /// 2) host only, once every participant reported the same package: start/target/seed drawn once by the existing
    ///    GameLaunch rule for the start's difficulty, the coop coordinator made and started with the fixed participants,
    ///    and the initial world sent; 3) the host's initial world checked against this device's own data (package,
    ///    start id, host, participants, difficulty, own PlayerId) and shown; the placement reported (success only after
    ///    view, player placement and floor check; the SessionId and initial revision of what was shown);
    /// 4) the common start: when Photon's server time reaches the host's start time, walking resumes (start room only).
    /// A stopped start is shown until the session loads the waiting room; a lost session stops play with the reason.
    /// Never a single-player start, word draw or coordinator on a guest.
    /// </summary>
    sealed class CoopGameplayFlow
    {
        readonly RoomGenerationDebug game;
        readonly WordRoomDataService data;
        readonly CoopSession session;
        readonly CoopEntry entry;
        WordRoomDataset dataset;
        GenerationConfig fixedSettings;     // the host's fixed generation settings as received and checked (independent copy)
        bool dataReported;
        bool worldRequested;
        bool worldHandled;
        bool placedOk;
        bool started;
        bool ended;

        /// <summary>The host's fixed generation settings this device received and checked (a copy; null before the initial world).</summary>
        public GenerationConfig FixedSettings => fixedSettings?.Clone();

        public CoopGameplayFlow(RoomGenerationDebug game, WordRoomDataService data, CoopSession session, CoopEntry entry)
        {
            this.game = game;
            this.data = data;
            this.session = session;
            this.entry = entry;
            game.SetCoopHint("단어 데이터를 불러오는 중…");
        }

        public void Tick()
        {
            if (ended) return;
            var s = CoopSession.Current;
            if (s == null || s != session || s.Stage == CoopStage.Ended)
            {
                End(CoopSession.LastEndReason ?? "협동 게임이 끝났습니다");
                return;
            }
            if (s.Stage == CoopStage.Aborted)
            {
                game.SetCoopHint($"게임 시작이 중단되었습니다 — {s.AbortReason}\n대기실로 돌아갑니다");   // the session loads the waiting room
                return;
            }
            if (!dataReported && !ReportData()) return;
            if (s.IsHost && !worldRequested && s.TryHostAgreedPackage(out var package)) RequestWorld(package);
            if (!worldHandled && dataset != null && s.TryTakeWorld(out var content)) ApplyWorld(content);
            if (placedOk && !started) TryBegin();
        }

        bool ReportData()
        {
            if (data == null)
            {
                dataReported = true;
                session.ReportData(false, null, "데이터 서비스가 없습니다");
                game.HaltCoop("단어 데이터를 준비할 수 없습니다");
                return false;
            }
            if (data.State == DataLoadState.Failed)
            {
                dataReported = true;
                session.ReportData(false, null, data.Error?.Message ?? "데이터 로딩 실패");
                game.SetCoopHint("단어 데이터를 불러오지 못했습니다. 게임 시작이 중단됩니다");
                return false;
            }
            if (!data.TryGetDataset(out dataset)) return false;        // still loading
            dataReported = true;
            game.BeginCoop(dataset, entry.LocalPlayerId);
            game.SetCoopHint("다른 참가자를 기다리는 중…");
            session.ReportData(true, dataset.PackageId, null);
            return true;
        }

        /// <summary>Host: once, with every participant on `package` (this host's own data among them).</summary>
        void RequestWorld(string package)
        {
            worldRequested = true;
            if (package != dataset.PackageId)
            {
                session.AbortAttempt("참가자들의 단어 데이터 버전이 서로 달라 시작할 수 없습니다");
                return;
            }
            var difficulty = (Difficulty)entry.Difficulty;
            if (!GameLaunch.TryPickWords(dataset, difficulty, out var start, out var target, out var seed, out var error))
            {
                session.AbortAttempt(error);                               // no second draw to hide a failure
                return;
            }
            Debug.Log($"[협동] 난이도 {GameLaunch.Label(difficulty)}: {dataset.GetWord(start)} → {dataset.GetWord(target)}, 시드 {seed}");
            game.SetCoopHint("방을 준비하는 중…");
            var ids = entry.Actors.Select(CoopRules.PlayerIdOf).ToArray();
            game.StartCoopHost(start, target, seed, GameLaunch.VocabularyLimit(difficulty), ids, (copy, config, failure) =>
            {
                if (ended || CoopSession.Current != session) return;
                if (failure != null)
                {
                    session.AbortAttempt(failure);
                    return;
                }
                if (copy.Status != "running")
                {
                    session.AbortAttempt("시작 방 주변에 이동할 방이 만들어지지 않아 시작할 수 없습니다");
                    return;
                }
                object[] content;
                try
                {
                    content = CoopProtocol.EncodeWorld(entry.Attempt, entry.Host, entry.Actors, entry.Difficulty, copy.PackageId, config, copy);   // the settings the coordinator really used
                }
                catch (System.ArgumentException e)
                {
                    Debug.LogError($"[협동] 초기 상태를 보낼 수 없습니다: {e.Message}");
                    session.AbortAttempt("초기 상태를 만들지 못했습니다");
                    return;
                }
                session.SendWorld(content, copy.SessionId, copy.Revision);
            });
        }

        /// <summary>Every device (the host too): the received world, checked with this device's own data, then shown.</summary>
        void ApplyWorld(object content)
        {
            worldHandled = true;
            string failure = null;
            CoopProtocol.World world = null;
            if (!CoopProtocol.TryDecodeWorld(content, dataset.WordCount, out world, out var error))
            {
                Debug.LogWarning($"[협동] 초기 상태를 받아들이지 않았습니다: {error}");
                failure = "초기 상태를 확인하지 못했습니다";
            }
            else if (world.PackageId != dataset.PackageId || world.Copy.PackageId != dataset.PackageId) failure = "단어 데이터 버전이 호스트와 다릅니다";
            else if (world.Attempt != entry.Attempt || world.Host != entry.Host || !world.Actors.SequenceEqual(entry.Actors) || world.Difficulty != entry.Difficulty)
                failure = "시작 정보가 서로 맞지 않습니다";
            else if (world.Copy.FindPlayer(entry.LocalPlayerId) == null) failure = "내 플레이어 정보가 초기 상태에 없습니다";
            else if (!SettingsMatch(world, out var why))
            {
                Debug.LogWarning($"[협동] 고정 생성 설정이 시작 정보와 맞지 않습니다: {why}");
                failure = "공통 생성 설정이 시작 정보와 맞지 않습니다";
            }
            else if (!game.ShowCoopInitial(world.Copy, out failure)) failure = failure ?? "방 표시·배치에 실패했습니다";
            if (failure == null) fixedSettings = world.Settings();          // kept as this device's independent copy (display only; never generates)
            placedOk = failure == null;
            session.ReportPlaced(world?.Copy.SessionId, world?.Copy.Revision ?? 0, placedOk, failure);
            game.SetCoopHint(placedOk ? "모든 참가자의 준비를 기다리는 중…" : $"준비에 실패했습니다 — {failure}");
        }

        /// <summary>
        /// The host's fixed settings agree with the start: the difficulty's vocabulary range and word lengths, no test
        /// move limit (normal play), and start and target inside that range of this device's own data.
        /// </summary>
        bool SettingsMatch(CoopProtocol.World world, out string why)
        {
            var c = world.Config;
            var d = (Difficulty)entry.Difficulty;
            why = null;
            if (c.VocabularyLimit != GameLaunch.VocabularyLimit(d) || c.MinWordLength != GameLaunch.MinWordLength || c.MaxWordLength != GameLaunch.MaxWordLength)
                why = $"어휘 범위 {c.VocabularyLimit} · {c.MinWordLength}~{c.MaxWordLength}글자 (난이도 {GameLaunch.Label(d)})";
            else if (c.UseMoveLimit) why = "테스트 이동 제한이 켜져 있음";
            else if (world.Copy.Seed != c.Seed) why = "초기 사본의 시드가 설정과 다름";
            else
            {
                try
                {
                    var scope = dataset.CreateScope(c.VocabularyLimit, c.MinWordLength, c.MaxWordLength);
                    if (!scope.Contains(world.Copy.StartWord) || !scope.Contains(world.Copy.TargetWord)) why = "시작·목표 단어가 어휘 범위 밖";
                }
                catch (System.Exception e)
                {
                    why = $"어휘 범위를 만들 수 없음 — {e.Message}";
                }
            }
            return why == null;
        }

        /// <summary>The common start as Photon server time; this device's clock is only used to convert it, never to make it.</summary>
        void TryBegin()
        {
            if (!session.TryGetStart(out var startServerTime)) return;
            var now = session.ServerTimestamp;
            if (!CoopRules.ServerTimeReached(now, startServerTime))
            {
                game.SetCoopHint("곧 시작합니다");
                return;
            }
            started = true;
            var localStart = Time.realtimeSinceStartupAsDouble - CoopRules.ServerSecondsSince(now, startServerTime);
            game.SetCoopHint(null);
            game.BeginCoopPlay(localStart);
            session.MarkPlaying();
        }

        void End(string reason)
        {
            ended = true;
            game.SetCoopHint(null);
            game.HaltCoop(reason);
        }
    }
}
