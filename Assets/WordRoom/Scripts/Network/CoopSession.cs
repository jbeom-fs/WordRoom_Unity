using System;
using System.Collections.Generic;
using System.Linq;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using Hashtable = ExitGames.Client.Photon.Hashtable;

namespace WordRoom.Network
{
    /// <summary>
    /// The one coop session manager while this device is in a coop room: a single object kept across scene loads
    /// (no UI), made when a CoopLobby enters a room and gone when the room is left or the connection ends.
    /// Waiting room: the host starts here (checks: host, 2–4 participants, all ready for the current settings, no start
    /// in progress); the room is closed and hidden at once and the participants of that moment are fixed in the start
    /// message (a later joiner is never added). Each participant that is in the list takes the connection over from the
    /// scene's CoopLobby (CoopLobby.HandOverToSession: its ownership and wind-down end there) and loads Gameplay with
    /// PhotonNetwork.LoadLevel (messages are paused during the load and delivered after it; they are kept here until
    /// the game side asks for them, so nothing is lost while a scene loads).
    /// Game: data report → (host) all data in with one PackageId → initial world from the host → placement reports →
    /// (host) all placed → the common start as Photon server time plus a short lead. Every host message is accepted
    /// only from the room's real master client, for the current start id and in the expected stage; reports are
    /// counted by their real sender (CoopStartAttempt). The host applies its own messages through the same handlers.
    /// Stopping: a failure, a participant leaving, a host change, or the time limit (ReadyTimeoutSeconds; participants
    /// wait GuestGraceSeconds longer) stops the start for everyone: the room opens again, the settings revision is
    /// raised (every ready is void) and everyone still in the room loads the main menu's waiting room. A disconnect or
    /// leaving the room ends this object (LastEndReason tells the game why). Leaving on purpose (LeaveGame) leaves the
    /// room, or disconnects when this session owns the connection; a connection it was lent is never disconnected.
    /// Exploration (from the common start on): requests and full-state requests reach the host only from a
    /// participant of the start (the event's real sender); replies, confirmed changes and full states are accepted only
    /// from the real host. They are kept in arrival order until the game side takes them (TryTakePlay); the host's own
    /// messages go through the same queue. Nothing here judges or applies them. No reconnection or host migration.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CoopSession : MonoBehaviour, IOnEventCallback, IInRoomCallbacks, IMatchmakingCallbacks, IConnectionCallbacks
    {
        static CoopSession current;
        static bool entryPending;          // a coop game scene load was started for an entry; consumed by Gameplay
        static bool entryLost;             // that entry ended before Gameplay took it (never fall back to single play)
        static bool waitingRoomPending;    // the main menu should open the waiting room again

        /// <summary>Limit from the confirmed start to everyone placed (data loading included). Set by the game scene; default CoopRules.DefaultReadyTimeoutSeconds.</summary>
        public static float ReadyTimeoutSeconds = CoopRules.DefaultReadyTimeoutSeconds;

        public static CoopSession Current => current;

        /// <summary>Why the last session ended (left, disconnected); null for an intended leave.</summary>
        public static string LastEndReason { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            current = null;
            entryPending = entryLost = waitingRoomPending = false;
            LastEndReason = null;
            ReadyTimeoutSeconds = CoopRules.DefaultReadyTimeoutSeconds;
            ownDisconnect.Forget();
        }

        static readonly DisconnectExpectation ownDisconnect = new DisconnectExpectation();

        /// <summary>
        /// The disconnect a session asked for when leaving a game whose connection it owned: kept apart from the session
        /// object, so a lobby can still tell it from a failure after the session has gone (see DisconnectExpectation).
        /// </summary>
        public static DisconnectExpectation OwnDisconnect => ownDisconnect;

        readonly HashSet<string> seenAttempts = new HashSet<string>();
        readonly HashSet<string> stoppedAttempts = new HashSet<string>();   // stopped start ids: never started or stopped again
        string roomName;
        bool owned;
        bool leaving;
        bool quitting;
        CoopStage stage = CoopStage.Waiting;
        CoopEntry entry;
        CoopStartAttempt hostAttempt;
        object worldContent;
        bool worldTaken;
        string placedSession;
        long placedRevision;
        CoopProtocol.Go go;
        double startedAt;
        readonly Queue<PlayMessage> play = new Queue<PlayMessage>();
        readonly Queue<PlayMessage> poses = new Queue<PlayMessage>();   // display poses, apart from the ordered logical queue
        readonly Queue<PlayMessage> sketches = new Queue<PlayMessage>();   // map drawing messages, in arrival order, apart from exploration
        const int MaxPoses = 64;                                        // a few frames of every participant; older ones are dropped first

        /// <summary>Raised on the main thread after every change of stage, entry or notice.</summary>
        public event Action Changed;

        public string RoomName => roomName;
        public CoopStage Stage => stage;
        public CoopEntry Entry => entry;
        public bool Leaving => leaving;

        /// <summary>This session owns the connection now (it was made by the CoopLobby that handed it over).</summary>
        public bool Owned => owned;

        /// <summary>Reason of the last stopped start (shown in the waiting room); null when none.</summary>
        public string AbortReason { get; private set; }

        /// <summary>Something worth telling in the waiting room (e.g. not part of a start); null when nothing.</summary>
        public string Notice { get; private set; }

        public bool IsHost => entry != null && PhotonNetwork.IsMasterClient && PhotonNetwork.LocalPlayer.ActorNumber == entry.Host;

        /// <summary>Photon's server time (int milliseconds, wrapping), read on the main thread.</summary>
        public int ServerTimestamp => PhotonNetwork.ServerTimestamp;

        static string CurrentRoomName => PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom != null ? PhotonNetwork.CurrentRoom.Name : null;

        /// <summary>A leaving session is responsible for this room (a lobby must not show or leave it).</summary>
        public static bool ClaimsLeaving(string room) => room != null && current != null && current.leaving && current.roomName == room;

        /// <summary>A session is disconnecting its own connection right now (a lobby entered meanwhile may connect again afterwards).</summary>
        public static bool DisconnectingOwned => ownDisconnect.Pending;

        // ---- lifetime --------------------------------------------------------------------------------------------
        /// <summary>The session of the current room (made once; a stale one of another room ends first). Null when not in a room.</summary>
        public static CoopSession EnsureForCurrentRoom()
        {
            var name = CurrentRoomName;
            if (name == null) return null;
            if (current != null)
            {
                if (current.roomName == name && !current.leaving && current.stage != CoopStage.Ended) return current;
                if (!current.leaving) current.End(null);
                else return null;                                  // the old room is still being left
            }
            var go = new GameObject("Coop Session");
            DontDestroyOnLoad(go);
            current = go.AddComponent<CoopSession>();
            current.roomName = name;
            return current;
        }

        /// <summary>
        /// Gameplay asks once whether it was opened for a coop start. True when a coop entry was expected (then `entry`
        /// is it, or null with `failure` when it was lost meanwhile — never single play instead).
        /// </summary>
        public static bool TryConsumeEntry(out CoopEntry entry, out string failure)
        {
            entry = null;
            failure = null;
            if (!entryPending && !entryLost) return false;
            var lost = entryLost;
            entryPending = entryLost = false;
            if (!lost && current != null && current.entry != null && (current.stage == CoopStage.Loading || current.stage == CoopStage.Placing))
            {
                entry = current.entry;
                return true;
            }
            failure = LastEndReason ?? "협동 게임 정보가 없어 시작할 수 없습니다";
            return true;
        }

        /// <summary>A started single game forgets any expected coop entry.</summary>
        public static void ForgetEntry() => entryPending = entryLost = false;

        /// <summary>The main menu asks once whether to open the waiting room again (after a stopped start).</summary>
        public static bool ConsumeWaitingRoomReturn()
        {
            var r = waitingRoomPending && current != null && !current.leaving && current.roomName == CurrentRoomName;
            waitingRoomPending = false;
            return r;
        }

        void OnEnable() => PhotonNetwork.AddCallbackTarget(this);

        void OnDisable() => PhotonNetwork.RemoveCallbackTarget(this);

        void OnApplicationQuit() => quitting = true;

        void OnDestroy()
        {
            if (current == this) current = null;
            if (quitting) return;
            if (entryPending) entryLost = true;
        }

        void End(string reason)
        {
            if (stage == CoopStage.Ended) return;
            stage = CoopStage.Ended;
            LastEndReason = reason;
            if (entryPending) entryLost = true;
            waitingRoomPending = false;
            if (current == this) current = null;
            Raise();
            Destroy(gameObject);
        }

        // ---- ownership --------------------------------------------------------------------------------------------
        /// <summary>From the CoopLobby giving up the connection for the game (its own wind-down ends with this).</summary>
        internal void TakeOwnership(bool ownedConnection) => owned |= ownedConnection;

        /// <summary>A CoopLobby adopts the room again (back in the waiting room): ownership goes back to it.</summary>
        internal bool ReleaseOwnership()
        {
            var o = owned;
            owned = false;
            return o;
        }

        /// <summary>Back in the waiting room after a stopped start: ready for a new start (a new id).</summary>
        internal void ResumeWaiting()
        {
            if (leaving || stage == CoopStage.Ended) return;
            stage = CoopStage.Waiting;
            entry = null;
            hostAttempt = null;
            worldContent = null;
            go = null;
            play.Clear();
            poses.Clear();
            sketches.Clear();
            Raise();
        }

        internal void ClearNotices()
        {
            AbortReason = null;
            Notice = null;
        }

        // ---- host: start ------------------------------------------------------------------------------------------
        /// <summary>
        /// The host confirms a start for `difficulty` at settings `revision` (what its waiting room shows). Every
        /// condition is checked again here, whatever the button showed; a second call while one runs is refused.
        /// </summary>
        public bool TryStartAsHost(int difficulty, int revision, out string why)
        {
            why = null;
            var room = PhotonNetwork.CurrentRoom;
            if (leaving || stage != CoopStage.Waiting) why = "이미 게임 시작을 처리하는 중입니다.";
            else if (!PhotonNetwork.InRoom || room == null || room.Name != roomName) why = "방에 있지 않습니다.";
            else if (!PhotonNetwork.IsMasterClient) why = "호스트만 시작할 수 있습니다.";
            else if (CoopLobby.SettingsUnsettled) why = "현재 설정이 확인되지 않아 시작할 수 없습니다.";   // only confirmed settings are fixed
            else if (!CoopRules.IsValidDifficulty(difficulty) || RoomInt(room, CoopRules.DifficultyKey, CoopRules.DefaultDifficulty) != difficulty || RoomInt(room, CoopRules.SettingsKey, 0) != revision)
                why = "설정이 바뀌었습니다. 다시 확인하세요.";
            else
            {
                var players = room.Players.Values.ToList();
                var ready = players.Count(p => CoopRules.IsReadyFor(Prop(p, CoopRules.ReadyKey), revision));
                if (!CoopStartRules.HostCanStart(true, true, players.Count, ready, SettingsState.Settled, stage))   // the same condition as the button
                    why = players.Count < CoopRules.MinPlayers || players.Count > CoopRules.MaxPlayers
                        ? $"{CoopRules.MinPlayers}~{CoopRules.MaxPlayers}명이 있어야 시작할 수 있습니다."
                        : "모든 참가자가 준비해야 시작할 수 있습니다.";
            }
            if (why != null) return false;

            var local = PhotonNetwork.LocalPlayer.ActorNumber;
            var actors = room.Players.Keys.OrderBy(a => a).ToArray();
            var id = Guid.NewGuid().ToString("N").Substring(0, 16);
            room.IsOpen = false;                                   // no new entry from now on; a joiner meanwhile is not in `actors`
            room.IsVisible = false;
            var content = CoopProtocol.Encode(new CoopProtocol.Begin { Attempt = id, Host = local, Actors = actors, Difficulty = difficulty, SettingsRevision = revision });
            if (!Send(CoopProtocol.BeginCode, content, ReceiverGroup.Others))
            {
                room.IsOpen = true;
                room.IsVisible = true;
                why = "게임 시작을 알릴 수 없습니다.";
                return false;
            }
            hostAttempt = new CoopStartAttempt(id, local, actors, difficulty, revision);
            HandleBegin(local, content);
            return true;
        }

        // ---- game side requests -------------------------------------------------------------------------------------
        /// <summary>This device's data result (PackageId of the data it really loaded). A failure is never "ready".</summary>
        public void ReportData(bool ok, string packageId, string error)
        {
            if (entry == null || stage != CoopStage.Loading) return;
            var content = CoopProtocol.Encode(new CoopProtocol.DataReport { Attempt = entry.Attempt, Ok = ok, PackageId = ok ? packageId : "", Error = error });
            ToHost(CoopProtocol.DataCode, content);
        }

        /// <summary>Host: every participant reported the same loaded package.</summary>
        public bool TryHostAgreedPackage(out string packageId)
        {
            packageId = null;
            return IsHost && hostAttempt != null && stage == CoopStage.Loading && hostAttempt.TryAgreedPackage(out packageId);
        }

        /// <summary>Host: sends the initial world (encoded by CoopProtocol) once and applies it through the same checks as everyone.</summary>
        public bool SendWorld(object[] content, string sessionId, long revision)
        {
            if (!IsHost || hostAttempt == null || stage != CoopStage.Loading || hostAttempt.SessionId != null) return false;
            hostAttempt.SetWorld(sessionId, revision);
            if (!Send(CoopProtocol.WorldCode, content, ReceiverGroup.Others))
            {
                AbortAttempt("초기 상태를 다른 참가자에게 보낼 수 없습니다");
                return false;
            }
            HandleWorld(PhotonNetwork.LocalPlayer.ActorNumber, content);
            return true;
        }

        /// <summary>The host's initial world as received (once); the game side decodes and checks it with its own data.</summary>
        public bool TryTakeWorld(out object content)
        {
            content = null;
            if (worldTaken || worldContent == null || stage != CoopStage.Placing) return false;
            worldTaken = true;
            content = worldContent;
            return true;
        }

        /// <summary>This device's placement result for the initial world (`sessionId`/`revision` of the state shown, own PlayerId).</summary>
        public void ReportPlaced(string sessionId, long revision, bool ok, string error)
        {
            if (entry == null || stage != CoopStage.Placing) return;
            if (ok)
            {
                stage = CoopStage.Placed;
                placedSession = sessionId;
                placedRevision = revision;
            }
            var content = CoopProtocol.Encode(new CoopProtocol.Placed
            {
                Attempt = entry.Attempt, SessionId = string.IsNullOrEmpty(sessionId) ? "none" : sessionId, Revision = revision < 1 ? 1 : revision,
                PlayerId = entry.LocalPlayerId, Ok = ok, Error = error,
            });
            ToHost(CoopProtocol.PlacedCode, content);
            Raise();
        }

        /// <summary>The common start as Photon server time, once the host fixed it.</summary>
        public bool TryGetStart(out int startServerTime)
        {
            startServerTime = go != null ? go.StartServerTime : 0;
            return go != null && (stage == CoopStage.Counting || stage == CoopStage.Playing);
        }

        public void MarkPlaying()
        {
            if (stage != CoopStage.Counting) return;
            stage = CoopStage.Playing;
            Raise();
        }

        // ---- exploration --------------------------------------------------------------------------------------------
        /// <summary>One exploration message as it arrived (its real sender), for the game side to check and use.</summary>
        public readonly struct PlayMessage
        {
            public readonly byte Code;
            public readonly int Sender;
            public readonly object Content;

            public PlayMessage(byte code, int sender, object content)
            {
                Code = code;
                Sender = sender;
                Content = content;
            }
        }

        /// <summary>Display name of a participant of this start by its PlayerId (actor-N): its NickName, or the default name.</summary>
        public string ParticipantName(string playerId)
        {
            if (entry != null)
                foreach (var actor in entry.Actors)
                    if (CoopRules.PlayerIdOf(actor) == playerId)
                    {
                        var p = PhotonNetwork.CurrentRoom?.GetPlayer(actor);
                        return p != null ? Name(p) : CoopRules.DefaultParticipantName(actor);
                    }
            return playerId;
        }

        /// <summary>A participant (ActorNumber) is still in this room (one that left gets no state any more).</summary>
        public bool IsInRoom(int actor) => PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom != null && PhotonNetwork.CurrentRoom.Name == roomName && PhotonNetwork.CurrentRoom.GetPlayer(actor) != null;

        /// <summary>The common start time is known or passed: exploration messages are kept from now on.</summary>
        bool InPlay => entry != null && (stage == CoopStage.Counting || stage == CoopStage.Playing);

        /// <summary>The next exploration message in arrival order (the host's own included).</summary>
        public bool TryTakePlay(out PlayMessage message)
        {
            if (play.Count > 0)
            {
                message = play.Dequeue();
                return true;
            }
            message = default;
            return false;
        }

        /// <summary>
        /// The next map drawing message in arrival order (the host's own included). Sent and checked like the exploration
        /// messages (SendToHost / SendToAll / SendTo, real sender) but kept apart, so drawing never delays exploration.
        /// </summary>
        public bool TryTakeSketch(out PlayMessage message)
        {
            if (sketches.Count > 0)
            {
                message = sketches.Dequeue();
                return true;
            }
            message = default;
            return false;
        }

        /// <summary>A request (or a full-state request) of this device to the host; the host's own goes straight into its queue. False when it could not be sent.</summary>
        public bool SendToHost(byte code, object[] content)
        {
            if (!InPlay || leaving) return false;
            if (IsHost)
            {
                HandleToHost(code, PhotonNetwork.LocalPlayer.ActorNumber, content);
                return true;
            }
            return Send(code, content, ReceiverGroup.MasterClient);
        }

        /// <summary>Host: a confirmed change to everyone, applied here through the same queue. False when the others could not be sent it.</summary>
        public bool SendToAll(byte code, object[] content)
        {
            if (!IsHost || !InPlay || leaving) return false;
            var sent = Send(code, content, ReceiverGroup.Others);
            HandleFromHost(code, PhotonNetwork.LocalPlayer.ActorNumber, content);
            return sent;
        }

        /// <summary>Host: a reply or a full state to one participant (this device's own goes into its queue). False when it could not be sent.</summary>
        public bool SendTo(int actor, byte code, object[] content)
        {
            if (!IsHost || !InPlay || leaving) return false;
            if (actor == PhotonNetwork.LocalPlayer.ActorNumber)
            {
                HandleFromHost(code, actor, content);
                return true;
            }
            return PhotonNetwork.RaiseEvent(code, content, new RaiseEventOptions { TargetActors = new[] { actor } }, SendOptions.SendReliable);
        }

        /// <summary>
        /// This device's body pose to the other participants: unreliable (a lost one is replaced by the next; never queued
        /// with the logical messages). False when it could not be sent.
        /// </summary>
        public bool SendPose(object[] content)
        {
            if (!InPlay || leaving) return false;
            return PhotonNetwork.RaiseEvent(CoopProtocol.PoseCode, content, new RaiseEventOptions { Receivers = ReceiverGroup.Others }, SendOptions.SendUnreliable);
        }

        /// <summary>The next received pose (oldest first; at most MaxPoses kept), for the game side to check and show.</summary>
        public bool TryTakePose(out PlayMessage message)
        {
            if (poses.Count > 0)
            {
                message = poses.Dequeue();
                return true;
            }
            message = default;
            return false;
        }

        /// <summary>Poses only from another participant of this start (by the event's real sender), while playing.</summary>
        void HandlePose(int sender, object content)
        {
            if (!InPlay || leaving || sender == PhotonNetwork.LocalPlayer.ActorNumber || Array.IndexOf(entry.Actors, sender) < 0) return;
            if (poses.Count >= MaxPoses) poses.Dequeue();
            poses.Enqueue(new PlayMessage(CoopProtocol.PoseCode, sender, content));
        }

        /// <summary>Requests reach only the host of this start, and only from a participant of it (by the event's real sender).</summary>
        void HandleToHost(byte code, int sender, object content)
        {
            if (!IsHost || !InPlay || Array.IndexOf(entry.Actors, sender) < 0) return;
            (CoopProtocol.IsSketchCode(code) ? sketches : play).Enqueue(new PlayMessage(code, sender, content));
        }

        /// <summary>Replies, changes and full states only from the real host of this start.</summary>
        void HandleFromHost(byte code, int sender, object content)
        {
            if (!FromHost(sender) || !InPlay) return;
            (CoopProtocol.IsSketchCode(code) ? sketches : play).Enqueue(new PlayMessage(code, sender, content));
        }

        /// <summary>
        /// Host: stops the start for everyone, with the reason shown to all — possible until the host itself started play
        /// (CoopStartRules.HostMayStop); every participant of the attempt applies it whatever its own stage.
        /// </summary>
        public void AbortAttempt(string reason)
        {
            if (!IsHost || hostAttempt == null || !CoopStartRules.HostMayStop(stage)) return;
            hostAttempt.Fail(reason);
            var content = CoopProtocol.Encode(new CoopProtocol.Abort { Attempt = hostAttempt.Id, Reason = reason });
            Send(CoopProtocol.AbortCode, content, ReceiverGroup.Others);
            HandleAbort(PhotonNetwork.LocalPlayer.ActorNumber, content);
        }

        /// <summary>
        /// This device leaves the coop game on purpose (game menu → main menu): before the common start a host first
        /// tells the others the start is stopped. Then the room is left — or the connection ended when this session owns
        /// it; a lent connection is kept. The object goes away when Photon confirms.
        /// </summary>
        public void LeaveGame()
        {
            if (leaving || stage == CoopStage.Ended) return;
            if (IsHost && hostAttempt != null && CoopStartRules.HostMayStop(stage))
                Send(CoopProtocol.AbortCode, CoopProtocol.Encode(new CoopProtocol.Abort { Attempt = hostAttempt.Id, Reason = "호스트가 나가 시작을 중단했습니다" }), ReceiverGroup.Others);
            leaving = true;
            entryPending = entryLost = waitingRoomPending = false;
            if (owned && PhotonNetwork.IsConnected)
            {
                ownDisconnect.Request();                           // an intended end, not a failure — also after this object is gone
                PhotonNetwork.Disconnect();
            }
            else if (PhotonNetwork.InRoom && CurrentRoomName == roomName)
            {
                if (!PhotonNetwork.LeaveRoom(false)) End(null);
            }
            else End(null);
        }

        // ---- time limit ------------------------------------------------------------------------------------------------
        void Update()
        {
            if (entry == null || leaving || go != null) return;
            if (stage != CoopStage.Loading && stage != CoopStage.Placing && stage != CoopStage.Placed) return;
            var waited = Time.realtimeSinceStartupAsDouble - startedAt;
            if (IsHost)
            {
                if (waited > ReadyTimeoutSeconds) AbortAttempt("준비 시간이 초과되어 시작을 중단했습니다");
            }
            else if (waited > ReadyTimeoutSeconds + CoopRules.GuestGraceSeconds) StopLocally("호스트의 시작 확인을 받지 못해 대기실로 돌아갑니다");
        }

        // ---- receiving -----------------------------------------------------------------------------------------------------
        public void OnEvent(EventData e)
        {
            if (!CoopProtocol.IsCoopCode(e.Code) || leaving || stage == CoopStage.Ended) return;
            if (!PhotonNetwork.InRoom || CurrentRoomName != roomName) return;
            switch (e.Code)
            {
                case CoopProtocol.BeginCode: HandleBegin(e.Sender, e.CustomData); break;
                case CoopProtocol.DataCode: HandleData(e.Sender, e.CustomData); break;
                case CoopProtocol.WorldCode: HandleWorld(e.Sender, e.CustomData); break;
                case CoopProtocol.PlacedCode: HandlePlaced(e.Sender, e.CustomData); break;
                case CoopProtocol.GoCode: HandleGo(e.Sender, e.CustomData); break;
                case CoopProtocol.AbortCode: HandleAbort(e.Sender, e.CustomData); break;
                case CoopProtocol.RequestCode:
                case CoopProtocol.ResyncCode:
                case CoopProtocol.QueryCode:
                case CoopProtocol.AckCode:
                case CoopProtocol.StopAckCode:
                case CoopProtocol.SketchRequestCode:
                case CoopProtocol.SketchResyncCode:
                case CoopProtocol.SketchAckCode: HandleToHost(e.Code, e.Sender, e.CustomData); break;
                case CoopProtocol.ReplyCode:
                case CoopProtocol.ChangeCode:
                case CoopProtocol.CopyCode:
                case CoopProtocol.StopCode:
                case CoopProtocol.SketchChangeCode:
                case CoopProtocol.SketchReplyCode:
                case CoopProtocol.SketchCopyCode: HandleFromHost(e.Code, e.Sender, e.CustomData); break;
                case CoopProtocol.PoseCode: HandlePose(e.Sender, e.CustomData); break;
            }
        }

        /// <summary>The real master client of the room (and, during a start, its host): a body value is never trusted for this.</summary>
        bool FromHost(int sender) =>
            PhotonNetwork.CurrentRoom != null && sender == PhotonNetwork.CurrentRoom.MasterClientId && (entry == null || sender == entry.Host);

        void HandleBegin(int sender, object content)
        {
            if (!FromHost(sender) || stage != CoopStage.Waiting) return;
            if (!CoopProtocol.TryDecode(content, out CoopProtocol.Begin b, out var error)) Warn(error);
            else if (b.Host != sender) Warn("시작 메시지의 호스트가 송신자와 다릅니다");
            else if (seenAttempts.Add(b.Attempt))
            {
                var local = PhotonNetwork.LocalPlayer.ActorNumber;
                if (Array.IndexOf(b.Actors, local) < 0)
                {
                    Notice = "진행 중인 게임 시작에 포함되지 않았습니다. 시작이 끝나면 다시 준비할 수 있습니다.";
                    Raise();
                    return;
                }
                entry = new CoopEntry(b.Attempt, sender, b.Actors, local, b.Difficulty, b.SettingsRevision);
                stage = CoopStage.Loading;
                startedAt = Time.realtimeSinceStartupAsDouble;
                AbortReason = Notice = null;
                worldContent = null;
                worldTaken = false;
                go = null;
                CoopLobby.HandOverToSession(this);                 // the scene's lobby stops; its wind-down must not end this room
                entryPending = true;
                entryLost = waitingRoomPending = false;
                Raise();
                PhotonNetwork.LoadLevel(CoopRules.GameplayScene);
            }
        }

        void HandleData(int sender, object content)
        {
            if (!IsHost || hostAttempt == null || stage != CoopStage.Loading) return;
            if (!CoopProtocol.TryDecode(content, out CoopProtocol.DataReport m, out var error)) Warn(error);
            else if (m.Attempt == hostAttempt.Id && hostAttempt.RecordData(sender, m.Ok, m.PackageId, m.Error) == ReportResult.Accepted && hostAttempt.Failure != null)
                AbortAttempt(hostAttempt.Failure);
        }

        void HandleWorld(int sender, object content)
        {
            if (!FromHost(sender) || entry == null || stage != CoopStage.Loading || worldContent != null) return;
            var a = content as object[];
            if (a == null || a.Length < 2 || !(a[1] is string attempt) || attempt != entry.Attempt) return;   // another start's: ignored
            worldContent = content;                                // fully checked by the game side with its own data
            stage = CoopStage.Placing;
            Raise();
        }

        void HandlePlaced(int sender, object content)
        {
            if (!IsHost || hostAttempt == null || hostAttempt.SessionId == null || go != null) return;
            if (!CoopProtocol.TryDecode(content, out CoopProtocol.Placed m, out var error))
            {
                Warn(error);
                return;
            }
            if (m.Attempt != hostAttempt.Id) return;
            var r = hostAttempt.RecordPlaced(sender, m.PlayerId, m.SessionId, m.Revision, m.Ok, m.Error);
            if (r == ReportResult.Invalid) Warn($"참가자 {sender}의 배치 응답이 현재 시작과 맞지 않아 무시했습니다");
            if (hostAttempt.Failure != null) AbortAttempt(hostAttempt.Failure);
            else if (hostAttempt.AllPlaced) SendGo();
        }

        void SendGo()
        {
            var start = unchecked(PhotonNetwork.ServerTimestamp + CoopRules.StartLeadMilliseconds);
            var content = CoopProtocol.Encode(new CoopProtocol.Go { Attempt = hostAttempt.Id, SessionId = hostAttempt.SessionId, Revision = hostAttempt.Revision, StartServerTime = start });
            if (!Send(CoopProtocol.GoCode, content, ReceiverGroup.Others))
            {
                AbortAttempt("공동 시작을 알릴 수 없습니다");
                return;
            }
            HandleGo(PhotonNetwork.LocalPlayer.ActorNumber, content);
        }

        void HandleGo(int sender, object content)
        {
            if (!FromHost(sender) || entry == null || go != null) return;
            if (!CoopProtocol.TryDecode(content, out CoopProtocol.Go m, out var error)) Warn(error);
            else if (CoopStartRules.AcceptsStart(stage, entry.Attempt, m.Attempt, stoppedAttempts) && m.SessionId == placedSession && m.Revision == placedRevision)
            {
                go = m;
                stage = CoopStage.Counting;
                Raise();
            }
        }

        /// <summary>The host's stop of the current attempt applies in every stage of it, also Playing (no split at the start boundary).</summary>
        void HandleAbort(int sender, object content)
        {
            if (!FromHost(sender) || entry == null) return;
            if (!CoopProtocol.TryDecode(content, out CoopProtocol.Abort m, out var error)) Warn(error);
            else if (CoopStartRules.AppliesStop(stage, entry.Attempt, m.Attempt, stoppedAttempts)) StopLocally(m.Reason);
        }

        /// <summary>
        /// The start ends here, once per attempt id (local stop: stage, notice, the waiting room is loaded once). Later
        /// messages of this attempt — a late start time, world, reports, a repeated stop — change nothing (its id is
        /// recorded as stopped; a new start gets a new id). The room itself is reopened separately (ReopenRoomIfMaster),
        /// by whichever device is the master — now, or later when it becomes the master.
        /// </summary>
        void StopLocally(string reason)
        {
            if (entry == null || !stoppedAttempts.Add(entry.Attempt)) return;
            AbortReason = string.IsNullOrEmpty(reason) ? "시작이 중단되었습니다" : reason;
            stage = CoopStage.Aborted;
            hostAttempt = null;
            play.Clear();                                          // nothing of the stopped game is used any more
            poses.Clear();
            sketches.Clear();
            entryPending = entryLost = false;
            Debug.LogWarning($"[협동] 게임 시작 중단: {AbortReason}");
            ReopenRoomIfMaster();
            waitingRoomPending = true;
            Raise();
            PhotonNetwork.LoadLevel(CoopRules.MainMenuScene);
        }

        /// <summary>
        /// The master reopens the room and raises the settings revision (every ready void) when the room is still closed
        /// and no start runs here (CoopStartRules.ReopensRoom). The reopening is applied locally at once, so it runs once
        /// per closing on this device; a new master that finds the room already reopened does nothing.
        /// </summary>
        void ReopenRoomIfMaster()
        {
            var room = PhotonNetwork.CurrentRoom;
            if (room == null || room.Name != roomName || !CoopStartRules.ReopensRoom(PhotonNetwork.IsMasterClient, room.IsOpen, stage)) return;
            room.IsOpen = true;
            room.IsVisible = true;
            room.SetCustomProperties(new Hashtable { { CoopRules.SettingsKey, RoomInt(room, CoopRules.SettingsKey, 0) + 1 } });
            Debug.Log("[협동] 중단된 시작의 방을 다시 열고 준비를 초기화했습니다.");
        }

        // ---- callbacks -------------------------------------------------------------------------------------------------
        public void OnPlayerEnteredRoom(Player newPlayer) => Raise();

        public void OnPlayerLeftRoom(Player otherPlayer)
        {
            if (entry != null && otherPlayer != null && Array.IndexOf(entry.Actors, otherPlayer.ActorNumber) >= 0 && IsHost)
                AbortAttempt($"{Name(otherPlayer)}이(가) 나가 시작을 중단했습니다");
            Raise();
        }

        public void OnRoomPropertiesUpdate(Hashtable propertiesThatChanged) => Raise();

        public void OnPlayerPropertiesUpdate(Player targetPlayer, Hashtable changedProps) => Raise();

        public void OnMasterClientSwitched(Player newMasterClient)
        {
            // every remaining participant stops the attempt, whatever its stage or whether the start time arrived (no
            // host migration at this stage); in the plain waiting room nothing stops
            if (entry != null && CoopStartRules.StopsOnHostChange(stage)) StopLocally("호스트가 나가 게임 시작을 중단했습니다. 대기실에서 다시 준비하세요");
            ReopenRoomIfMaster();                                  // also when this device had already stopped before it became the master
            Raise();
        }

        public void OnLeftRoom() => End(leaving ? null : "방에서 나와 협동 게임이 끝났습니다");

        public void OnDisconnected(DisconnectCause cause) => End(leaving ? null : "연결이 끊겨 협동 게임이 끝났습니다");

        public void OnFriendListUpdate(List<FriendInfo> friendList)
        {
        }

        public void OnCreatedRoom()
        {
        }

        public void OnCreateRoomFailed(short returnCode, string message)
        {
        }

        public void OnJoinedRoom()
        {
        }

        public void OnJoinRoomFailed(short returnCode, string message)
        {
        }

        public void OnJoinRandomFailed(short returnCode, string message)
        {
        }

        public void OnConnected()
        {
        }

        public void OnConnectedToMaster()
        {
        }

        public void OnRegionListReceived(RegionHandler regionHandler)
        {
        }

        public void OnCustomAuthenticationResponse(Dictionary<string, object> data)
        {
        }

        public void OnCustomAuthenticationFailed(string debugMessage)
        {
        }

        // ---- helpers ---------------------------------------------------------------------------------------------------
        void ToHost(byte code, object[] content)
        {
            if (IsHost)
            {
                var local = PhotonNetwork.LocalPlayer.ActorNumber;
                if (code == CoopProtocol.DataCode) HandleData(local, content);   // the host's own report: same checks
                else HandlePlaced(local, content);
            }
            else if (!Send(code, content, ReceiverGroup.MasterClient)) Warn("호스트에게 응답을 보낼 수 없습니다");
        }

        static bool Send(byte code, object[] content, ReceiverGroup to) =>
            PhotonNetwork.RaiseEvent(code, content, new RaiseEventOptions { Receivers = to }, SendOptions.SendReliable);

        static object Prop(Player p, string key) => p.CustomProperties != null && p.CustomProperties.TryGetValue(key, out var v) ? v : null;

        internal static int RoomInt(Room r, string key, int fallback) =>
            r != null && r.CustomProperties != null && r.CustomProperties.TryGetValue(key, out var v) && v is int i ? i : fallback;

        static string Name(Player p) => string.IsNullOrWhiteSpace(p.NickName) ? CoopRules.DefaultParticipantName(p.ActorNumber) : p.NickName;

        static void Warn(string what) => Debug.LogWarning($"[협동] 메시지를 무시했습니다: {what}");

        void Raise()
        {
            var handlers = Changed;
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
    }
}
