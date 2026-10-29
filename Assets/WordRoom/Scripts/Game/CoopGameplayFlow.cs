using System.Collections.Generic;
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
    /// 4) the common start: when Photon's server time reaches the host's start time, play begins;
    /// 5) exploration: this device's door and entry requests go to the host; every device takes the host's messages in
    ///    arrival order — confirmed changes (applied in revision order; a missing one asks the host for the whole
    ///    state), outcomes of its own requests and full states — and tells the host the revision it shows (Ack). The host
    ///    runs the requests (its own and the participants', PlayerId from the real sender) one at a time in arrival
    ///    order: the coordinator checks and applies each on the worker, then the change goes to everyone (the host's own
    ///    display included) and the outcome to the requester; outcomes are recorded and answered again on a query of the
    ///    same RequestId, never by running it again. A participant whose Ack stays behind gets the whole state (limited
    ///    tries, then reported); an error while applying a request is never answered as a refusal: the host's state is
    ///    secured and sent to everyone, or everyone stops with the reason. Near-simultaneous host priority is not applied
    ///    (its time range is not decided): strictly first come, which is not a final decision.
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
        long initialRevision;               // revision of the initial world every placed device showed

        // host: requests in arrival order, their recorded outcomes, and how far each participant has applied the state
        readonly Queue<HostJob> hostJobs = new Queue<HostJob>();
        readonly Dictionary<(int, long), HostRecord> records = new Dictionary<(int, long), HostRecord>();
        readonly Dictionary<int, Peer> peers = new Dictionary<int, Peer>();
        bool hostWorking;                   // one request (or state read) is being handled; the next waits
        long hostHead;                      // latest revision this host confirmed or read

        // host: the shared stop (no valid shared state after an error). Requests are no longer run; the stop is sent to
        // every participant still in the room until it confirms (StopAck), within a limit, then the rest is reported.
        string hostStopReason;              // null = exploring
        readonly Dictionary<int, StopNotice> stopNotices = new Dictionary<int, StopNotice>();
        bool stopReported;

        // this device: the whole state asked for, the applied revision told to the host, the waiting request's queries
        bool resyncWanted;                  // the whole state is needed (asked for now or again later) until one is applied
        int resyncTries;                    // attempts of this need, sent or not
        double nextResyncAt;                // the next attempt (or the end of the last one's wait)
        long ackSent;
        double ackFailedAt = double.NegativeInfinity;
        double behindSince = -1;            // a known outcome's revision is ahead of the shown state since then
        long queried;                       // the request whose outcome is being queried
        int queryTries;                     // queries of it, sent or not
        int querySent;                      // of those, the ones that went out
        bool queryPendingSeen;              // the host answered "pending" (it has the request) at least once
        double nextQueryAt;                 // the next query (or the end of the last one's wait)
        bool queryAfterCopy;                // a whole state was applied: ask a waiting request's outcome now
        bool stoppedHere;                   // this device stopped for good (shared stop, or an outcome or state it could not confirm)

        /// <summary>One host job: a participant's request, or (Request null) the whole state for that participant.</summary>
        readonly struct HostJob
        {
            public readonly int Sender;
            public readonly CoopProtocol.Request Request;

            public HostJob(int sender, CoopProtocol.Request request)
            {
                Sender = sender;
                Request = request;
            }
        }

        /// <summary>A request's outcome as the host recorded it (answered to the requester and to its queries; never run again).</summary>
        sealed class HostRecord
        {
            public RequestOutcome Outcome;
            public long Revision;
            public string Reason;
        }

        /// <summary>
        /// Host: one participant's applied revision (its Ack) and its recovery. It is stalled when its Ack stays on the
        /// same revision behind this host's for StateCatchUpSeconds (any real progress restarts that time, never the
        /// failures). A recovery sends the whole state of revision Target: queued first (never timed out while queued),
        /// then sent at SentAt and confirmed when the Ack reaches Target (success) or not within StateCatchUpSeconds (one
        /// failure). Failures counts the failed recoveries since the last success, whatever their kind — a state that could
        /// not be read, or one sent that its Ack never reached — and only an Ack-confirmed success (the recovery target, or
        /// this host's revision) resets it; at RecoveryAttempts the recovery ends for good (EndReason), as at once for a
        /// state that can never be made (too large, not encodable). After the end no state is read or queued for it any
        /// more; only the end is delivered to it — a Stop for this participant alone, sent every StateCatchUpSeconds up to
        /// RecoveryAttempts times until its StopAck, then reported once as unconfirmed.
        /// </summary>
        sealed class Peer
        {
            public long Applied;
            public double StalledSince = -1;
            public bool Queued;
            public long Target;              // revision of the whole state sent; 0 = no recovery waiting
            public double SentAt;
            public int Failures;             // failed recoveries since the last Ack-confirmed success
            public string EndReason;         // recovery ended for good; null = going on
            public int EndSends;             // the end's delivery: sends tried, next try, confirmed by its StopAck, settled
            public double EndNextAt;
            public bool EndConfirmed;
            public bool EndSettled;          // confirmed, or reported unconfirmed after the last send's wait
        }

        /// <summary>Host: the shared stop's delivery to one participant (sent at most RecoveryAttempts times, confirmed only by its StopAck).</summary>
        sealed class StopNotice
        {
            public int Sends;
            public double NextAt;
            public bool Confirmed;
            public bool Left;
        }

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
            if (!placedOk) return;
            TakePlayMessages();
            var now = Time.realtimeSinceStartupAsDouble;
            if (s.IsHost)
            {
                if (hostStopReason != null) WatchStop(now);                // no more requests; only the stop's delivery
                else if (started)
                {
                    WatchPeers(now);
                    RunHostJob();
                }
            }
            else if (!stoppedHere) AckApplied(now);
            if (!stoppedHere) CheckOwnRecovery(now);
        }

        // ---- exploration -------------------------------------------------------------------------------------------
        /// <summary>This device's request to the host (from the game's E or doorway crossing). False when it could not be sent.</summary>
        bool SendRequest(RequestKind kind, GridPos from, Direction side, long requestId) =>
            session.SendToHost(CoopProtocol.RequestCode, CoopProtocol.Encode(new CoopProtocol.Request
            {
                Attempt = entry.Attempt, SessionId = game.CoopSessionId, RequestId = requestId, Kind = kind, Expected = from, Direction = side,
            }));

        /// <summary>Every exploration message that arrived, in arrival order: the host's jobs are queued, the rest applied now.</summary>
        void TakePlayMessages()
        {
            while (session.TryTakePlay(out var m))
            {
                switch (m.Code)
                {
                    case CoopProtocol.RequestCode: HostReceiveRequest(m); break;   // only the host receives these five (CoopSession)
                    case CoopProtocol.ResyncCode: HostReceiveResync(m); break;
                    case CoopProtocol.QueryCode: HostAnswerQuery(m); break;
                    case CoopProtocol.AckCode: HostReceiveAck(m); break;
                    case CoopProtocol.StopAckCode: HostReceiveStopAck(m); break;
                    case CoopProtocol.ChangeCode: TakeChange(m.Content); break;
                    case CoopProtocol.ReplyCode: TakeReply(m.Content); break;
                    case CoopProtocol.CopyCode: TakeCopy(m.Content); break;
                    case CoopProtocol.StopCode: TakeStop(m.Content); break;
                }
            }
        }

        // ---- this device: applied state, recovery and its request's outcome ----------------------------------------
        /// <summary>Tells the host the revision this device shows now (once per new revision; a failed send is tried again after a pause).</summary>
        void AckApplied(double now)
        {
            var revision = game.CoopRevision;
            if (revision <= ackSent || now - ackFailedAt < CoopRules.StateCatchUpSeconds) return;
            if (session.SendToHost(CoopProtocol.AckCode, CoopProtocol.Encode(new CoopProtocol.Ack { Attempt = entry.Attempt, SessionId = game.CoopSessionId, Revision = revision })))
                ackSent = revision;
            else ackFailedAt = now;
        }

        /// <summary>
        /// This device's own recovery, all limited and on a schedule (never every frame, never waiting for another message):
        /// an outcome whose revision stays ahead of the shown state past the normal order of a change and its answer
        /// needs the whole state; the whole state is asked for until one is applied; a request without an outcome is
        /// queried by its RequestId. Nothing is guessed and no Prepare or Enter is sent again.
        /// </summary>
        void CheckOwnRecovery(double now)
        {
            if (game.CoopAwaitedRevision > game.CoopRevision)
            {
                if (behindSince < 0) behindSince = now;
                else if (now - behindSince >= CoopRules.StateCatchUpSeconds) RequestResync();
            }
            else behindSince = -1;
            if (resyncWanted && now >= nextResyncAt && !TryResync(now)) return;
            QueryOutcome(now);
        }

        /// <summary>
        /// One attempt to get the whole state: each attempt (sent or failed at once) is followed by RequestWaitNoticeSeconds
        /// for the next one — the wait for a sent one's answer, the pause after a failed one. After RecoveryAttempts
        /// attempts and that last wait, this device stops with the reason. False when it stopped.
        /// </summary>
        bool TryResync(double now)
        {
            if (resyncTries >= CoopRules.RecoveryAttempts)
            {
                StopHere("호스트와 지도 상태를 맞추지 못했습니다");
                return false;
            }
            resyncTries++;
            nextResyncAt = now + CoopRules.RequestWaitNoticeSeconds;
            if (resyncTries > 1) game.SetCoopHint("호스트와 지도 상태를 맞추는 중입니다. 계속 기다리거나 Esc로 메인 메뉴에 나갈 수 있습니다");
            var content = CoopProtocol.Encode(new CoopProtocol.Resync { Attempt = entry.Attempt, SessionId = game.CoopSessionId, Have = game.CoopRevision });
            if (!session.SendToHost(CoopProtocol.ResyncCode, content))
                Debug.LogWarning($"[협동] 전체 상태 요청({resyncTries}/{CoopRules.RecoveryAttempts})을 보낼 수 없습니다. 잠시 뒤 다시 시도합니다");
            return true;
        }

        /// <summary>
        /// The outcome of this device's waiting request, asked again by its RequestId: the first query RequestWaitNoticeSeconds
        /// after the request — or at once when a newer whole state is applied before that first query was tried — then one
        /// per RequestWaitNoticeSeconds after each try, whether it went out or not, RecoveryAttempts in all; once a query was
        /// tried, whole states neither bring the next one forward nor push it back. "Pending" means the host has it (still handled, never taken as
        /// cancelled) and keeps waiting within the same limit. When the last query's wait has passed without an outcome
        /// this device stops — it never guesses accepted or refused and never just lifts the walking lock.
        /// </summary>
        void QueryOutcome(double now)
        {
            if (!game.TryGetCoopUnanswered(out var id, out var sentAt))
            {
                queryAfterCopy = false;
                return;
            }
            if (id != queried)
            {
                queried = id;
                queryTries = querySent = 0;
                queryPendingSeen = false;
                nextQueryAt = sentAt + CoopRules.RequestWaitNoticeSeconds;
            }
            if (queryAfterCopy && queryTries == 0) nextQueryAt = now;     // only before this request's first try (a failed send counts as tried)
            queryAfterCopy = false;
            if (now < nextQueryAt) return;
            if (queryTries >= CoopRules.RecoveryAttempts)
            {
                StopHere(querySent == 0 ? "요청 결과를 호스트에게 물어볼 수 없어 결과를 확인하지 못했습니다"
                    : queryPendingSeen ? "호스트가 요청을 처리 중이라는 응답만 받고 결과를 확인하지 못했습니다"
                    : "호스트가 요청 결과 조회에 응답하지 않아 결과를 확인하지 못했습니다");
                return;
            }
            queryTries++;
            nextQueryAt = now + CoopRules.RequestWaitNoticeSeconds;
            if (session.SendToHost(CoopProtocol.QueryCode, CoopProtocol.Encode(new CoopProtocol.Query { Attempt = entry.Attempt, SessionId = game.CoopSessionId, RequestId = id })))
                querySent++;
            else Debug.LogWarning($"[협동] 요청 {id}의 결과 조회({queryTries}/{CoopRules.RecoveryAttempts})를 보낼 수 없습니다");
        }

        /// <summary>This device stops for good with the reason (late states and outcomes may still be applied, play never resumes).</summary>
        void StopHere(string reason)
        {
            if (stoppedHere) return;
            stoppedHere = true;
            resyncWanted = false;
            game.SetCoopHint(null);
            game.ReportCoopHalt($"{reason}. Esc로 메인 메뉴에 나가세요");
        }

        /// <summary>
        /// A confirmed change from the host: applied when it is the next revision. While the whole state is awaited, only
        /// the next revision is applied (older ones are inside the coming state). A missing revision, a change that does not
        /// fit, or a malformed one asks the host once for the whole state; nothing is guessed or skipped.
        /// </summary>
        void TakeChange(object content)
        {
            if (!CoopProtocol.TryDecodeChange(content, dataset.WordCount, out var m, out var error))
            {
                Debug.LogWarning($"[협동] 호스트의 확정 변경을 받아들이지 않았습니다: {error}");
                RequestResync();
                return;
            }
            if (m.Attempt != entry.Attempt) return;
            if (resyncWanted && m.Data.Revision != game.CoopRevision + 1) return;
            if (game.ApplyCoopChange(m.Data, out var why) != CoopChangeResult.Gap) return;
            Debug.LogWarning($"[협동] 확정 변경 {m.Data.Revision}을 적용하지 못했습니다 — {why}. 같은 순번의 전체 상태를 호스트에게 요청합니다.");
            RequestResync();
        }

        void TakeReply(object content)
        {
            if (!CoopProtocol.TryDecode(content, out CoopProtocol.Reply m, out var error))
            {
                Debug.LogWarning($"[협동] 호스트의 응답을 받아들이지 않았습니다: {error}");
                return;
            }
            if (m.Attempt != entry.Attempt || m.SessionId != game.CoopSessionId) return;
            if (m.Outcome == RequestOutcome.Pending && m.RequestId == queried) queryPendingSeen = true;   // the host has it: still within the same limit
            game.ApplyCoopReply(m.RequestId, m.Outcome, m.Revision, m.Refusal);   // only the waiting request's first outcome counts
        }

        /// <summary>
        /// The host's stop — for everyone (Team) or for this device only (its state recovery ended): this device stops once
        /// (a repeated stop changes nothing) and confirms every stop it receives with a StopAck of the same kind, so a lost
        /// confirmation is sent again with the host's next try.
        /// </summary>
        void TakeStop(object content)
        {
            if (!CoopProtocol.TryDecode(content, out CoopProtocol.Stop m, out var error))
            {
                Debug.LogWarning($"[협동] 호스트의 중단 통지를 받아들이지 않았습니다: {error}");
                return;
            }
            if (m.Attempt != entry.Attempt || m.SessionId != game.CoopSessionId) return;
            if (!stoppedHere) Debug.LogError($"[협동] 호스트가 {(m.Team ? "모든 참가자의" : "이 기기의")} 탐색을 멈췄습니다: {m.Reason}");
            StopHere(!string.IsNullOrEmpty(m.Reason) ? m.Reason : m.Team ? "호스트가 공유 상태 오류로 탐색을 멈췄습니다" : "호스트가 이 기기의 탐색을 멈췄습니다");
            if (!session.IsHost && !session.SendToHost(CoopProtocol.StopAckCode, CoopProtocol.Encode(new CoopProtocol.StopAck { Attempt = entry.Attempt, SessionId = m.SessionId, Team = m.Team })))
                Debug.LogWarning("[협동] 중단 확인을 호스트에게 보내지 못했습니다(호스트가 다시 보내면 다시 확인합니다)");
        }

        /// <summary>
        /// The host's whole state (asked for, sent to a device behind, or sent to everyone after an error in a request):
        /// checked like the initial world, then shown when newer. A waiting request's outcome is then queried, never
        /// guessed from the state. A missing or unusable state stops play here.
        /// </summary>
        void TakeCopy(object content)
        {
            var before = game.CoopRevision;
            string failure = null;
            if (!CoopProtocol.TryDecodeCopy(content, dataset.WordCount, out var m, out var error))
            {
                Debug.LogWarning($"[협동] 호스트의 전체 상태를 받아들이지 않았습니다: {error}");
                failure = "호스트의 전체 상태를 확인하지 못했습니다";
            }
            else if (m.Attempt != entry.Attempt || m.SessionId != game.CoopSessionId) return;   // another start's or session's: nothing of the recovery changes
            else if (m.Error.Length > 0) failure = $"호스트가 전체 상태를 보내지 못했습니다 — {m.Error}";
            else if (m.State.PackageId != dataset.PackageId || m.State.Host != entry.Host || !m.State.Actors.SequenceEqual(entry.Actors) || m.State.Difficulty != entry.Difficulty)
                failure = "호스트의 전체 상태가 시작 정보와 맞지 않습니다";
            else if (!game.ApplyCoopCopy(m.State.Copy, out var why) && why != null) failure = $"호스트의 전체 상태를 적용하지 못했습니다 — {why}";
            if (failure != null)
            {
                StopHere(failure);
                return;
            }
            if (resyncWanted && resyncTries > 1) game.SetCoopHint(null);
            resyncWanted = false;                                           // this need is met; a later gap is a new one
            resyncTries = 0;
            behindSince = -1;
            if (game.CoopRevision > before) queryAfterCopy = true;          // only a newer state (a repeated or older one changes no query)
            ackSent = 0;                                                    // tell the host the shown revision again
            ackFailedAt = double.NegativeInfinity;
        }

        /// <summary>
        /// The whole state is needed: the first attempt now, the next ones on CheckOwnRecovery's schedule (also when the
        /// send fails at once and nothing else arrives), until one is applied. The confirmed state is never rolled back.
        /// </summary>
        void RequestResync()
        {
            if (resyncWanted || stoppedHere) return;
            resyncWanted = true;
            resyncTries = 0;
            nextResyncAt = Time.realtimeSinceStartupAsDouble;
        }

        // ---- host: requests, outcomes and every participant's applied state ----------------------------------------
        /// <summary>
        /// Host: a request is recorded Pending and queued once; the same RequestId again is never queued a second time
        /// (its recorded outcome is answered when known).
        /// </summary>
        void HostReceiveRequest(CoopSession.PlayMessage m)
        {
            if (!CoopProtocol.TryDecode(m.Content, out CoopProtocol.Request r, out var error))
            {
                Debug.LogWarning($"[협동] 참가자 {m.Sender}의 요청을 무시했습니다: {error}");
                return;
            }
            if (r.Attempt != entry.Attempt) return;
            if (hostStopReason != null)
            {
                SendStop(m.Sender);                                         // stopped: never run, recorded or queued; the stop is the answer
                return;
            }
            if (Ended(m.Sender, out var endedPeer))
            {
                SendEnd(m.Sender, endedPeer);                                   // this participant's recovery ended: its end is the answer (not counted as a delivery try)
                return;
            }
            var key = (m.Sender, r.RequestId);
            if (records.TryGetValue(key, out var known))
            {
                if (known.Outcome != RequestOutcome.Pending) SendReply(m.Sender, r.SessionId, r.RequestId, known);
                return;
            }
            records[key] = new HostRecord { Outcome = RequestOutcome.Pending };
            hostJobs.Enqueue(new HostJob(m.Sender, r));
        }

        void HostReceiveResync(CoopSession.PlayMessage m)
        {
            if (!CoopProtocol.TryDecode(m.Content, out CoopProtocol.Resync r, out var error))
            {
                Debug.LogWarning($"[협동] 참가자 {m.Sender}의 전체 상태 요청을 무시했습니다: {error}");
                return;
            }
            if (r.Attempt != entry.Attempt) return;
            if (hostStopReason != null) SendStop(m.Sender);
            else if (Ended(m.Sender, out var endedPeer)) SendEnd(m.Sender, endedPeer);
            else QueueState(m.Sender);
        }

        /// <summary>Host: the recorded outcome of the same request (Pending while queued or running, Unknown when it never arrived); nothing is run.</summary>
        void HostAnswerQuery(CoopSession.PlayMessage m)
        {
            if (!CoopProtocol.TryDecode(m.Content, out CoopProtocol.Query q, out var error))
            {
                Debug.LogWarning($"[협동] 참가자 {m.Sender}의 결과 조회를 무시했습니다: {error}");
                return;
            }
            if (q.Attempt != entry.Attempt || q.SessionId != game.CoopSessionId) return;
            var unrun = !records.TryGetValue((m.Sender, q.RequestId), out var done) || done.Outcome == RequestOutcome.Pending;
            if (hostStopReason != null && unrun)
            {
                SendStop(m.Sender);                                         // a request left unrun by the stop: the stop is its answer
                return;
            }
            if (unrun && Ended(m.Sender, out var endedPeer))
            {
                SendEnd(m.Sender, endedPeer);                                   // likewise for a participant whose recovery ended; a known outcome is still answered
                return;
            }
            SendReply(m.Sender, q.SessionId, q.RequestId, records.TryGetValue((m.Sender, q.RequestId), out var known) ? known : new HostRecord { Outcome = RequestOutcome.Unknown });
        }

        /// <summary>
        /// Host: a participant's applied revision. Only a real progress counts: a repeated or older Ack changes nothing, and
        /// a revision this host never confirmed is refused (another start's or session's Acks are dropped first).
        /// </summary>
        void HostReceiveAck(CoopSession.PlayMessage m)
        {
            if (!CoopProtocol.TryDecode(m.Content, out CoopProtocol.Ack a, out var error))
            {
                Debug.LogWarning($"[협동] 참가자 {m.Sender}의 적용 확인을 무시했습니다: {error}");
                return;
            }
            if (a.Attempt != entry.Attempt || a.SessionId != game.CoopSessionId || hostStopReason != null) return;
            hostHead = System.Math.Max(hostHead, game.CoopRevision);
            if (a.Revision > hostHead)
            {
                Debug.LogWarning($"[협동] 참가자 {m.Sender}의 적용 확인 {a.Revision}이 호스트 순번 {hostHead}보다 앞서 무시했습니다");
                return;
            }
            var p = PeerOf(m.Sender);
            if (a.Revision <= p.Applied) return;
            p.Applied = a.Revision;
            p.StalledSince = -1;                                            // progress: the stall time starts over (the failures do not)
        }

        /// <summary>
        /// Host: a participant confirmed a stop — the shared one (Team) or the end of its own recovery, each only for its
        /// own kind (never a revision Ack or a send). Another start's or session's confirmation changes nothing.
        /// </summary>
        void HostReceiveStopAck(CoopSession.PlayMessage m)
        {
            if (!CoopProtocol.TryDecode(m.Content, out CoopProtocol.StopAck a, out var error))
            {
                Debug.LogWarning($"[협동] 참가자 {m.Sender}의 중단 확인을 무시했습니다: {error}");
                return;
            }
            if (a.Attempt != entry.Attempt || a.SessionId != game.CoopSessionId) return;
            if (a.Team)
            {
                if (hostStopReason != null && stopNotices.TryGetValue(m.Sender, out var n)) n.Confirmed = true;
                return;
            }
            if (!Ended(m.Sender, out var p) || p.EndConfirmed) return;
            p.EndConfirmed = true;
            var late = p.EndSettled;
            p.EndSettled = true;
            Debug.Log($"[협동] {session.ParticipantName(CoopRules.PlayerIdOf(m.Sender))}이(가) 탐색 정지를 확인했습니다{(late ? "(미확인 보고 뒤 늦게 도착)" : "")}");
        }

        /// <summary>
        /// Host: no valid shared state can be secured — exploration stops for everyone, once. Requests are no longer run
        /// (queued ones are dropped, their records stay Pending and are answered with the stop); this host's own play stops
        /// at once; the stop goes to every other participant until confirmed (WatchStop).
        /// </summary>
        void HostStop(string reason)
        {
            if (hostStopReason != null) return;
            hostStopReason = reason;
            hostJobs.Clear();
            foreach (var actor in entry.Actors)
                if (actor != entry.LocalActor) stopNotices[actor] = new StopNotice();
            Debug.LogError($"[협동] 공유 상태를 확보하지 못해 모든 참가자의 탐색을 멈춥니다: {reason}");
            StopHere($"호스트의 공유 상태 오류로 탐색을 멈췄습니다 — {reason}");
        }

        /// <summary>
        /// Host after the stop: each participant still in the room that has not confirmed gets the stop again every
        /// StateCatchUpSeconds, RecoveryAttempts times at most (a send that went out is not a confirmation). Once every
        /// participant has confirmed, left, or used up its tries and the last wait, the result is reported once — the
        /// participants that never confirmed by name, never as "everyone stopped".
        /// </summary>
        void WatchStop(double now)
        {
            if (stopReported) return;
            var open = false;
            var unconfirmed = new List<string>();
            var confirmed = 0;
            foreach (var kv in stopNotices)
            {
                var n = kv.Value;
                if (n.Confirmed)
                {
                    confirmed++;
                    continue;
                }
                if (n.Left || !session.IsInRoom(kv.Key))
                {
                    n.Left = true;                                          // left the room: no longer a target
                    continue;
                }
                if (now < n.NextAt)
                {
                    open = true;
                    continue;
                }
                if (n.Sends >= CoopRules.RecoveryAttempts)
                {
                    unconfirmed.Add(session.ParticipantName(CoopRules.PlayerIdOf(kv.Key)));
                    continue;
                }
                n.Sends++;
                n.NextAt = now + CoopRules.StateCatchUpSeconds;
                SendStop(kv.Key);
                open = true;
            }
            if (open) return;
            stopReported = true;
            var summary = unconfirmed.Count == 0
                ? $"남은 참가자 {confirmed}명이 중단을 확인했습니다"
                : $"중단 확인 {confirmed}명, 확인되지 않은 참가자: {string.Join(", ", unconfirmed)}";
            if (unconfirmed.Count > 0) Debug.LogError($"[협동] 탐색 중단 통지를 확인하지 못한 참가자: {string.Join(", ", unconfirmed)}");
            else Debug.Log($"[협동] {summary}");
            game.ReportCoopHalt($"호스트의 공유 상태 오류로 탐색을 멈췄습니다 — {hostStopReason}\n{summary}. Esc로 메인 메뉴에 나가세요");
        }

        void SendStop(int actor)
        {
            var content = CoopProtocol.Encode(new CoopProtocol.Stop { Attempt = entry.Attempt, SessionId = game.CoopSessionId, Team = true, Reason = $"호스트의 공유 상태 오류로 탐색을 멈췄습니다 — {hostStopReason}" });
            if (!session.SendTo(actor, CoopProtocol.StopCode, content)) Debug.LogWarning($"[협동] 참가자 {actor}에게 중단 통지를 보내지 못했습니다(다시 시도합니다)");
        }

        Peer PeerOf(int actor)
        {
            if (!peers.TryGetValue(actor, out var p)) peers[actor] = p = new Peer { Applied = initialRevision };   // every placed device showed the initial world
            return p;
        }

        void SendReply(int actor, string sessionId, long requestId, HostRecord r)
        {
            var reply = CoopProtocol.Encode(new CoopProtocol.Reply { Attempt = entry.Attempt, SessionId = sessionId, RequestId = requestId, Outcome = r.Outcome, Revision = r.Revision, Refusal = r.Reason });
            if (!session.SendTo(actor, CoopProtocol.ReplyCode, reply))
                Debug.LogWarning($"[협동] 참가자 {actor}에게 요청 {requestId}의 결과를 보내지 못했습니다(요청자가 같은 요청의 결과를 다시 조회합니다)");
        }

        /// <summary>
        /// Host: each participant still in the room (one that left is dropped) — see Peer. A recovery that was sent is
        /// judged by its own target, never by this host's moving head: the Ack reaching that target is a success; still
        /// below it StateCatchUpSeconds after the send is one more failure. Up to date ends any lag. A participant whose
        /// Ack stays on one revision behind for StateCatchUpSeconds gets the whole state (also when the missing change was
        /// the last one); after RecoveryAttempts failures in a row it is reported once and no more is sent until it
        /// catches up by itself (a sent message is never taken as applied; only the Ack counts).
        /// </summary>
        void WatchPeers(double now)
        {
            hostHead = System.Math.Max(hostHead, game.CoopRevision);
            foreach (var actor in entry.Actors)
            {
                if (actor == entry.LocalActor) continue;                    // the host's own display follows its own queue
                if (!session.IsInRoom(actor))
                {
                    peers.Remove(actor);
                    continue;
                }
                var p = PeerOf(actor);
                if (p.EndReason != null)
                {
                    DeliverEnd(actor, p, now);                              // ended: no state any more, only the end's delivery
                    continue;
                }
                if (p.Queued) continue;                                     // queued or being read: not sent yet, never judged
                if (p.Target > 0)
                {
                    if (p.Applied >= p.Target)
                    {
                        p.Target = 0;                                       // that recovery worked
                        p.Failures = 0;
                    }
                    else if (now - p.SentAt < CoopRules.StateCatchUpSeconds) continue;
                    else
                    {
                        var target = p.Target;
                        p.Target = 0;
                        if (Fail(actor, p, now, $"보낸 전체 상태(순번 {target})의 적용 확인이 오지 않았습니다")) continue;
                    }
                }
                if (p.Applied >= hostHead)
                {
                    p.StalledSince = -1;
                    p.Failures = 0;                                         // up to date by its own Ack: a success
                    continue;
                }
                if (p.StalledSince < 0) p.StalledSince = now;
                if (now - p.StalledSince < CoopRules.StateCatchUpSeconds) continue;
                QueueState(actor);
            }
        }

        /// <summary>Host: the whole state for one device, queued once until read and sent (in turn with the requests); never after its recovery ended.</summary>
        void QueueState(int actor)
        {
            var p = PeerOf(actor);
            if (p.Queued || p.EndReason != null) return;
            p.Queued = true;
            hostJobs.Enqueue(new HostJob(actor, null));
        }

        /// <summary>Host: the next job in arrival order, once the one before has been answered (its change sent first).</summary>
        void RunHostJob()
        {
            if (hostWorking || hostJobs.Count == 0) return;
            var job = hostJobs.Dequeue();
            if (job.Request != null)
            {
                if (Ended(job.Sender, out _)) return;                       // queued before its recovery ended: never run (stays Pending; a query gets the end)
                HostRequest(job);
            }
            else HostSendState(job.Sender);
        }

        /// <summary>
        /// Host: one participant's request, as the coordinator sees it — PlayerId from the real sender (never the body).
        /// The coordinator checks session, participant, progress, confirmed position, adjacency, room, door and
        /// preparation, and reads the goal entry's end time itself when it confirms it. Accepted: the change goes to
        /// everyone, then the answer to the requester. Refused: only the answer (nothing changed). Failed: see HostAfterFailure.
        /// </summary>
        void HostRequest(HostJob job)
        {
            var m = job.Request;
            var key = (job.Sender, m.RequestId);
            var request = new SessionRequest(m.SessionId, CoopRules.PlayerIdOf(job.Sender), m.RequestId, m.Kind, m.Expected, m.Direction);
            hostWorking = true;
            game.SubmitCoopHost(request, outcome =>
            {
                if (ended || CoopSession.Current != session)
                {
                    hostWorking = false;
                    return;
                }
                if (outcome.Outcome == RequestOutcome.Failed)
                {
                    HostAfterFailure(job, outcome);                         // the next job waits until the state is secured
                    return;
                }
                hostWorking = false;
                var record = records[key] = new HostRecord { Outcome = outcome.Outcome, Revision = outcome.Revision, Reason = outcome.Refusal };
                if (outcome.Accepted) hostHead = System.Math.Max(hostHead, outcome.Revision);
                if (outcome.Change != null && !session.SendToAll(CoopProtocol.ChangeCode, CoopProtocol.EncodeChange(entry.Attempt, outcome.Change)))
                    Debug.LogWarning($"[협동] 확정 변경 {outcome.Change.Revision}을 다른 참가자에게 보내지 못했습니다(적용 확인이 늦는 참가자에게 전체 상태를 보냅니다)");
                SendReply(job.Sender, m.SessionId, m.RequestId, record);
            });
        }

        /// <summary>
        /// Host: an error while a request was applied — the state may have changed partly, so it is never answered as a
        /// refusal. The current state is read and checked: when consistent and sendable it goes to everyone (this host's
        /// display too) and the requester is answered Failed with that revision (its outcome then follows that state); the
        /// coordinator keeps the failure, so the request is never applied again. When no valid state can be secured,
        /// everyone stops exploring with the reason (this host takes no more requests).
        /// </summary>
        void HostAfterFailure(HostJob job, CoopHostOutcome outcome)
        {
            var m = job.Request;
            var key = (job.Sender, m.RequestId);
            game.ReadCoopHost((copy, config, failure) =>
            {
                hostWorking = false;
                if (ended || CoopSession.Current != session) return;
                var stop = failure;
                string why = null;
                object[] world = null;
                if (stop == null && !copy.Validate(out why)) stop = $"호스트 상태가 일관되지 않습니다({why})";
                if (stop == null && !CoopProtocol.FitsWorld(copy, out why)) stop = $"전체 상태가 너무 큽니다({why})";
                if (stop == null)
                {
                    try
                    {
                        world = CoopProtocol.EncodeWorld(entry.Attempt, entry.Host, entry.Actors, entry.Difficulty, copy.PackageId, config, copy);
                    }
                    catch (System.ArgumentException e)
                    {
                        stop = e.Message;
                    }
                }
                var revision = copy != null ? copy.Revision : System.Math.Max(1, outcome.Revision);
                var record = records[key] = new HostRecord { Outcome = RequestOutcome.Failed, Revision = revision, Reason = outcome.Refusal };
                Debug.LogError($"[협동] 참가자 {job.Sender}의 요청 {m.RequestId} 처리 중 오류: {outcome.Refusal}");
                if (stop != null)
                {
                    HostStop(stop);                                         // everyone stops, delivery confirmed one by one (WatchStop)
                    return;
                }
                hostHead = System.Math.Max(hostHead, copy.Revision);
                if (!session.SendToAll(CoopProtocol.CopyCode, CoopProtocol.EncodeCopy(entry.Attempt, copy.SessionId, copy.Revision, world, copy.Result, null)))
                    Debug.LogWarning("[협동] 오류 뒤 전체 상태를 다른 참가자에게 보내지 못했습니다(적용 확인이 늦는 참가자에게 다시 보냅니다)");
                SendReply(job.Sender, m.SessionId, m.RequestId, record);
            });
        }

        /// <summary>
        /// Host: the whole current state for one device (it asked, or its Ack stayed behind) — read in turn with the
        /// requests, so every change after it is sent after it. A state over the receivers' limits is not sent; the
        /// reason is sent instead (never reported as sent).
        /// </summary>
        void HostSendState(int actor)
        {
            hostWorking = true;
            game.ReadCoopHost((copy, config, failure) =>
            {
                hostWorking = false;
                var p = PeerOf(actor);
                p.Queued = false;
                if (ended || CoopSession.Current != session || hostStopReason != null) return;
                var now = Time.realtimeSinceStartupAsDouble;
                if (failure != null)
                {
                    p.Target = 0;                                           // nothing sent: no Ack to wait for
                    Fail(actor, p, now, $"호스트가 전체 상태를 읽지 못했습니다 — {failure}");   // maybe passing: tried again after a pause, within the limit
                    return;
                }
                if (!CoopProtocol.FitsWorld(copy, out var why))
                {
                    EndRecovery(actor, p, $"전체 상태가 너무 큽니다({why})");    // the same state again never fits: no retry
                    return;
                }
                object[] content;
                try
                {
                    var world = CoopProtocol.EncodeWorld(entry.Attempt, entry.Host, entry.Actors, entry.Difficulty, copy.PackageId, config, copy);
                    content = CoopProtocol.EncodeCopy(entry.Attempt, copy.SessionId, copy.Revision, world, copy.Result, null);
                }
                catch (System.ArgumentException e)
                {
                    EndRecovery(actor, p, $"전체 상태를 만들지 못했습니다 — {e.Message}");   // the same state again fails the same way
                    return;
                }
                hostHead = System.Math.Max(hostHead, copy.Revision);
                p.Target = copy.Revision;                                   // sent (or tried): judged by its Ack from now on
                p.SentAt = now;
                if (!session.SendTo(actor, CoopProtocol.CopyCode, content)) Debug.LogWarning($"[협동] 참가자 {actor}에게 전체 상태를 보내지 못했습니다(적용 확인으로 다시 판단합니다)");
            });
        }

        /// <summary>
        /// Host: one failed recovery of a participant (a state not read, or one sent that its Ack never reached): the next
        /// try only after another stall; at RecoveryAttempts failures since the last success the recovery ends. True when it ended.
        /// </summary>
        bool Fail(int actor, Peer p, double now, string reason)
        {
            p.Failures++;
            p.StalledSince = now;
            if (p.Failures >= CoopRules.RecoveryAttempts)
            {
                EndRecovery(actor, p, reason);
                return true;
            }
            Debug.LogWarning($"[협동] 참가자 {actor}의 상태 복구 실패({p.Failures}/{CoopRules.RecoveryAttempts}): {reason}");
            return false;
        }

        /// <summary>
        /// Host: one participant's recovery ends for good (RecoveryAttempts failures, or a state that can never be made).
        /// Reported once; no state is read or queued for it any more; its end is delivered to it by WatchPeers (DeliverEnd,
        /// starting now) — also to a participant that never asked for a state. Only this participant: the session goes on.
        /// </summary>
        void EndRecovery(int actor, Peer p, string reason)
        {
            if (p.EndReason != null) return;
            p.EndReason = reason;
            p.Target = 0;
            p.EndNextAt = Time.realtimeSinceStartupAsDouble;
            var name = session.ParticipantName(CoopRules.PlayerIdOf(actor));
            Debug.LogError($"[협동] {name}의 상태 복구를 끝냅니다: {reason}");
            game.NotifyCoop($"{name}의 화면을 호스트 상태로 맞추지 못해 그 참가자의 탐색을 멈춥니다");
        }

        /// <summary>
        /// Host: the end of one participant's recovery sent every StateCatchUpSeconds, RecoveryAttempts times at most, until
        /// its StopAck (a send that went out, or a revision Ack, is no confirmation). When the last send's wait has passed
        /// without it, that participant is reported once as unconfirmed — never as stopped — and nothing more is sent.
        /// </summary>
        void DeliverEnd(int actor, Peer p, double now)
        {
            if (p.EndSettled || now < p.EndNextAt) return;
            if (p.EndSends >= CoopRules.RecoveryAttempts)
            {
                p.EndSettled = true;
                var name = session.ParticipantName(CoopRules.PlayerIdOf(actor));
                Debug.LogError($"[협동] {name}이(가) 탐색 정지 통지를 확인하지 않았습니다(정지 여부 미확인)");
                game.NotifyCoop($"{name}의 탐색 정지를 확인하지 못했습니다");
                return;
            }
            p.EndSends++;
            p.EndNextAt = now + CoopRules.StateCatchUpSeconds;
            SendEnd(actor, p);
        }

        void SendEnd(int actor, Peer p)
        {
            var content = CoopProtocol.Encode(new CoopProtocol.Stop { Attempt = entry.Attempt, SessionId = game.CoopSessionId, Team = false, Reason = $"호스트가 이 기기의 화면을 호스트 상태로 맞추지 못해 이 기기의 탐색을 멈춥니다 — {p.EndReason}" });
            if (!session.SendTo(actor, CoopProtocol.StopCode, content)) Debug.LogWarning($"[협동] 참가자 {actor}에게 탐색 정지 통지를 보내지 못했습니다");
        }

        /// <summary>A participant whose recovery ended (its requests are no longer run; the end is the answer).</summary>
        bool Ended(int actor, out Peer p) => peers.TryGetValue(actor, out p) && p.EndReason != null;

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
            if (failure == null)
            {
                fixedSettings = world.Settings();                          // kept as this device's independent copy (display only; never generates)
                initialRevision = hostHead = world.Copy.Revision;         // what every placed device shows first
                game.SetCoopPlay(SendRequest, CoopRules.RequestWaitNoticeSeconds, session.ParticipantName);
            }
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
