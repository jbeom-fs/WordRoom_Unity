using System;
using System.Collections.Generic;
using System.Text;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using Hashtable = ExitGames.Client.Photon.Hashtable;

namespace WordRoom.Network
{
    public enum CoopPhase
    {
        /// <summary>Not used (no coop screen) and nothing of ours is connected.</summary>
        Idle,
        Connecting,
        JoiningLobby,
        InLobby,
        CreatingRoom,
        JoiningRoom,
        InRoom,
        LeavingRoom,
        LeavingLobby,
        /// <summary>새로고침: the lobby is left and joined again until its first full room list arrives.</summary>
        RefreshingList,
        /// <summary>Connected to the master server but not in the lobby (a lobby join could not be sent); 새로고침 joins again.</summary>
        OutsideLobby,
        Disconnecting,
        /// <summary>The connection ended without being asked (or could not be made); a retry is the user's choice.</summary>
        Disconnected,
    }

    /// <summary>One participant of the current Photon room as the waiting room shows it.</summary>
    public readonly struct Participant
    {
        public readonly int ActorNumber;
        /// <summary>CoopRules.PlayerIdOf(ActorNumber): this room's identity, not a persistent one.</summary>
        public readonly string PlayerId;
        public readonly string Name;
        public readonly bool IsHost;
        public readonly bool IsReady;
        public readonly bool IsLocal;

        public Participant(int actorNumber, string name, bool isHost, bool isReady, bool isLocal)
        {
            ActorNumber = actorNumber;
            PlayerId = CoopRules.PlayerIdOf(actorNumber);
            Name = name;
            IsHost = isHost;
            IsReady = isReady;
            IsLocal = isLocal;
        }
    }

    /// <summary>The current Photon room as the waiting room shows it. Code is the real Room.Name.</summary>
    public readonly struct WaitingRoom
    {
        public readonly string Code;
        public readonly string DisplayName;
        public readonly int PlayerCount;
        public readonly int MaxPlayers;

        public WaitingRoom(string code, string displayName, int playerCount, int maxPlayers)
        {
            Code = code;
            DisplayName = displayName;
            PlayerCount = playerCount;
            MaxPlayers = maxPlayers;
        }
    }

    /// <summary>
    /// The coop lobby's Photon side (PUN 2): connecting, the default lobby and its room list, creating and joining
    /// rooms by their Room.Name (the RoomCode), leaving, and the participants' ready state. No word data, generator or
    /// session is started here.
    /// One operation at a time: a request while another runs is refused (double clicks never send twice). Every
    /// operation that returns false at once is reported and leaves nothing pending; the others end with their
    /// success or failure callback (or the disconnect callback). What happens next follows the screen's intent:
    /// while Active it goes to the lobby (connect when the user entered, join the lobby, rejoin it after leaving a
    /// room or a failed entry); when no longer Active it winds down step by step after the running operation — leave
    /// a room of its own entered too late, then disconnect a connection it made itself (Owned), else only leave the
    /// lobby. A connection it did not make is never disconnected, and a room it did not enter is never left by winding
    /// down. Connecting is wanted only when the user entered (or pressed Retry) while there was no connection — or while
    /// this component was disconnecting its own one — and that wish ends once connected or at an unexpected
    /// disconnect; such a disconnect is explained and never retried automatically (Retry is the user's). Rejoining the
    /// lobby after leaving a room is not a reconnect and needs no wish. Callbacks are registered once, on first use,
    /// and removed when disabled. Disabling disconnects an owned connection at once; on a borrowed connection a room
    /// entry still pending (or its own current room) is handed to CoopRoomCleanup, which leaves exactly that room when
    /// Photon reports it and then unregisters.
    /// The local participant's ready flag is removed before every create or join, so a value of an earlier room never
    /// travels into the next one (PUN sends the local player's properties when entering a room).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CoopLobby : MonoBehaviour, IConnectionCallbacks, ILobbyCallbacks, IMatchmakingCallbacks, IInRoomCallbacks
    {
        enum Op
        {
            None,
            Connect,
            JoinLobby,
            LeaveLobby,
            Create,
            Join,
            LeaveRoom,
            Disconnect,
        }

        [Tooltip("방 목록 진단 로그(기본 꺼짐). 로비 참가 구간마다 수신·캐시·화면 반영을 같은 번호로 남깁니다. 빌드에서는 실행 인수 -wordroom-list-log로도 켭니다. 인증 정보는 출력하지 않습니다.")]
        [SerializeField] bool listDiagnostics;

        readonly RoomListCache rooms = new RoomListCache();
        readonly List<RoomInfo> noRooms = new List<RoomInfo>();
        Op op;
        bool registered;
        bool active;
        bool connectWanted;           // the user asked to be connected while there was no connection (entering or retry); used once
        bool owned;                   // the current connection was made by this component
        string requestedRoom;         // Room.Name of the create/join this component has pending; null = none
        string ourRoom;               // Room.Name of the room this component entered and is in; null = none
        bool ourDisconnect;           // the next disconnect callback answers our own Disconnect
        bool lobbyJoinedByUs;         // the lobby (and its list) was joined by this component
        bool readyPending;
        bool unexpectedDisconnect;
        bool quitting;
        bool handedOver;              // a confirmed coop start took the connection and the room (CoopSession); nothing to wind down here

        static CoopLobby instance;    // the scene's lobby that registered for callbacks (one per scene)

        /// <summary>Raised on the main thread after every change of phase, list, room, participants or notice.</summary>
        public event Action Changed;

        /// <summary>A coop start was confirmed: the game session owns the room and the connection now; the scene is about to change.</summary>
        public bool HandedOver => handedOver;

        /// <summary>A coop screen is open and wants the lobby or a room.</summary>
        public bool Active => active;

        public bool Busy => op != Op.None;

        /// <summary>The current connection was made by this component (only that one is disconnected when leaving).</summary>
        public bool Owned => owned;

        public RoomListCache Rooms => rooms;

        /// <summary>Last result or failure worth telling the user; null when there is nothing to say.</summary>
        public string Notice { get; private set; }
        public bool NoticeIsError { get; private set; }

        public bool ReadyPending => readyPending;

        public CoopPhase Phase
        {
            get
            {
                if (refresh != RefreshStage.None) return CoopPhase.RefreshingList;   // also while its leave or join runs
                switch (op)
                {
                    case Op.Connect: return CoopPhase.Connecting;
                    case Op.JoinLobby: return CoopPhase.JoiningLobby;
                    case Op.LeaveLobby: return CoopPhase.LeavingLobby;
                    case Op.Create: return CoopPhase.CreatingRoom;
                    case Op.Join: return CoopPhase.JoiningRoom;
                    case Op.LeaveRoom: return CoopPhase.LeavingRoom;
                    case Op.Disconnect: return CoopPhase.Disconnecting;
                }
                if (!registered) return CoopPhase.Idle;
                if (PhotonNetwork.InRoom) return ClaimedByCleanup ? CoopPhase.Connecting : CoopPhase.InRoom;   // a stopped flow's late room is being left
                if (PhotonNetwork.InLobby) return CoopPhase.InLobby;
                if (active && OnMasterServer) return CoopPhase.OutsideLobby;  // nothing runs: the lobby join could not be sent
                if (!IdleDisconnected) return CoopPhase.Connecting;          // switching servers, or a connection of someone else in progress
                return unexpectedDisconnect ? CoopPhase.Disconnected : CoopPhase.Idle;
            }
        }

        static bool IdleDisconnected
        {
            get
            {
                var s = PhotonNetwork.NetworkClientState;
                return s == ClientState.Disconnected || s == ClientState.PeerCreated;
            }
        }

        static bool OnMasterServer => PhotonNetwork.IsConnectedAndReady && PhotonNetwork.Server == ServerConnection.MasterServer && !PhotonNetwork.InRoom;

        static string CurrentRoomName => PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom != null ? PhotonNetwork.CurrentRoom.Name : null;

        /// <summary>The current room is being left by a CoopRoomCleanup or a leaving CoopSession (not this flow's to show or leave).</summary>
        static bool ClaimedByCleanup => CoopRoomCleanup.Claims(CurrentRoomName) || CoopSession.ClaimsLeaving(CurrentRoomName);

        /// <summary>In the room this component itself created or joined (and no cleanup has taken it).</summary>
        bool InOurRoom => ourRoom != null && CurrentRoomName == ourRoom && !ClaimedByCleanup;

        /// <summary>
        /// Whether entering or Retry should start a connection: there is none, or this component is disconnecting its
        /// own one right now (the user came back during the wind-down). Any other state is reused as it is.
        /// </summary>
        bool NeedsConnection => IdleDisconnected || (op == Op.Disconnect && ourDisconnect) || CoopSession.DisconnectingOwned;

        // ---- requests from the screens -----------------------------------------------------------------------
        /// <summary>A coop screen opened: go to the lobby (connecting when needed). False with a notice when coop cannot be used.</summary>
        public bool Enter()
        {
            if (!registered)
            {
                PhotonNetwork.AddCallbackTarget(this);              // once; removed in OnDisable
                registered = true;
            }
            WatchEvents(true);                                     // full lists are told from change sets only in the event
            if (op == Op.None) AdoptLobbyCleanup();                // a stopped lobby's join or leave still runs: wait for it, never overlap
            instance = this;
            handedOver = false;
            active = true;
            if (PhotonNetwork.IsConnectedAndReady) CoopSession.OwnDisconnect.Forget();   // a live connection: no intended end is still coming
            connectWanted = NeedsConnection;                       // an existing connection is reused: no wish is left behind
            SetNotice(null, false);
            if (PhotonNetwork.OfflineMode)
            {
                SetNotice("Photon 오프라인 모드에서는 협동 로비를 쓸 수 없습니다.", true);
                Raise();
                return false;
            }
            AdoptSession();
            if (ListDiagnostics) ListLog($"협동 화면 진입 · {ConnectionText()} · 로비 {(PhotonNetwork.InLobby ? (lobbyJoinedByUs ? "참가 중(이 화면)" : "참가 중(다른 경로)") : "밖")} · 캐시 {CacheText()}");
            Next();
            return true;
        }

        /// <summary>
        /// Back in the waiting room after a stopped start (same room, same connection): the room becomes this lobby's
        /// own again and so does the connection's ownership, which the game session held meanwhile; the reason the
        /// start stopped is shown once.
        /// </summary>
        void AdoptSession()
        {
            var s = CoopSession.Current;
            if (s == null || s.Leaving || !PhotonNetwork.InRoom || s.RoomName != CurrentRoomName) return;
            owned |= s.ReleaseOwnership();
            ourRoom = CurrentRoomName;
            if (s.AbortReason != null) SetNotice($"게임 시작이 중단되었습니다 — {s.AbortReason}", true);
            s.ClearNotices();
            s.ResumeWaiting();
        }

        /// <summary>
        /// A lobby join or leave that a stopped CoopLobby handed to CoopLobbyCleanup still runs: it becomes this
        /// component's own pending operation (nothing new is sent, its callback ends it like any other), and the cleanup
        /// ends without leaving the lobby later.
        /// </summary>
        void AdoptLobbyCleanup()
        {
            switch (CoopLobbyCleanup.Adopt())
            {
                case CoopLobbyCleanup.Request.Join:
                    op = Op.JoinLobby;
                    listLobby++;                                   // its list is a new section (logs only)
                    ClearRooms("이전 화면의 로비 참가 인수");
                    lobbyJoinedByUs = false;
                    break;
                case CoopLobbyCleanup.Request.Leave:
                    op = Op.LeaveLobby;                            // the lobby is joined again once the leave is through
                    break;
            }
        }

        /// <summary>
        /// A confirmed start (CoopSession, also for a participant) takes over: this lobby's ownership of the connection
        /// goes to the session, and it stops handling callbacks and winding down — the coming scene change is not an exit.
        /// </summary>
        internal static void HandOverToSession(CoopSession session)
        {
            var l = instance;
            if (l == null || !l.registered || session == null) return;
            session.TakeOwnership(l.owned);
            l.owned = false;
            l.handedOver = true;
            l.active = false;
            l.connectWanted = false;
            l.op = Op.None;
            l.requestedRoom = l.ourRoom = null;
            l.readyPending = false;
            l.ResetSettingsGate();
            l.WatchEvents(false);
            PhotonNetwork.RemoveCallbackTarget(l);
            l.registered = false;
            l.Raise();
        }

        /// <summary>The session currently in charge of this room's start, when there is one.</summary>
        public CoopSession Session => watched != null && watched.RoomName == CurrentRoomName ? watched : null;

        /// <summary>The coop screens closed: winds down after the running operation (see the class summary).</summary>
        public void Exit()
        {
            if (!registered) return;
            active = false;
            connectWanted = false;                                 // closing the screen also cancels a wish to reconnect
            rejoinCode = null;                                     // and a pending 다시 입장 (the settings question ends only when the room is left)
            EndRefresh(null);                                      // and a 새로고침 (a request still running ends by itself; nothing is joined again)
            lobbyToRejoin = null;
            Next();
        }

        /// <summary>After an unexpected disconnect or a failed connect: one new attempt (the user's choice).</summary>
        public bool Retry()
        {
            if (!active || Busy || !NeedsConnection) return false;   // still connected (or switching servers): nothing to retry
            connectWanted = true;
            SetNotice(null, false);
            Next();
            return true;
        }

        /// <summary>Creates a room named by a new RoomCode with `displayName` (published to the lobby) and `maxPlayers` (2–4).</summary>
        public bool CreateRoom(string displayName, int maxPlayers)
        {
            if (!CanRequestFromLobby()) return false;
            var code = CoopRules.NewRoomCode();
            var options = new RoomOptions
            {
                MaxPlayers = CoopRules.ClampMaxPlayers(maxPlayers),
                IsOpen = true,
                IsVisible = true,
                PlayerTtl = 0,
                EmptyRoomTtl = 0,
                CustomRoomProperties = new Hashtable
                {
                    { CoopRules.DisplayNameKey, CoopRules.CleanDisplayName(displayName, code) },
                    { CoopRules.DifficultyKey, CoopRules.DefaultDifficulty },   // the host's choice (보통 first); not published to the lobby
                    { CoopRules.SettingsKey, 1 },
                },
                CustomRoomPropertiesForLobby = new[] { CoopRules.DisplayNameKey },
            };
            ClearLocalReady();
            SetNotice(null, false);
            if (!Begin(Op.Create, PhotonNetwork.CreateRoom(code, options), "방 만들기 요청을 보낼 수 없습니다.")) return false;
            requestedRoom = code;
            return true;
        }

        /// <summary>Joins the room whose Room.Name is `input` (surrounding spaces removed only). Never creates a room.</summary>
        public bool JoinByCode(string input)
        {
            if (RefreshBlocks()) return false;                     // also Enter in the RoomCode field
            var code = CoopRules.CleanCode(input);
            if (code.Length == 0)
            {
                SetNotice("RoomCode를 입력하세요.", true);
                Raise();
                return false;
            }
            return Join(code);
        }

        /// <summary>Joins a room of the list; refused at once when the list already shows it full, closed or gone.</summary>
        public bool JoinListed(string roomName)
        {
            if (RefreshBlocks()) return false;                     // the list is being received anew
            if (!listComplete)                                     // change sets alone tell too little about a room
            {
                SetNotice("전체 방 목록을 확인한 뒤에 목록에서 입장할 수 있습니다. 새로고침하거나 RoomCode로 참가하세요.", true);
                Raise();
                return false;
            }
            if (!rooms.TryGet(roomName, out var r))
            {
                SetNotice("선택한 방이 목록에 없습니다.", true);
                Raise();
                return false;
            }
            switch (r.Availability)
            {
                case RoomAvailability.Full:
                    SetNotice("방 정원이 가득 찼습니다.", true);
                    Raise();
                    return false;
                case RoomAvailability.Closed:
                    SetNotice("방이 입장을 받지 않습니다(입장 닫힘).", true);
                    Raise();
                    return false;
                case RoomAvailability.Gone:
                    SetNotice("선택한 방이 목록에서 사라졌습니다.", true);
                    Raise();
                    return false;
            }
            return Join(r.Name);
        }

        bool Join(string code)
        {
            if (!CanRequestFromLobby()) return false;
            ClearLocalReady();
            SetNotice(null, false);
            if (!Begin(Op.Join, PhotonNetwork.JoinRoom(code), "방 입장 요청을 보낼 수 없습니다.")) return false;
            requestedRoom = code;
            return true;
        }

        /// <summary>Leaves the current room; the lobby follows once Photon confirmed it (left and back on the master server).</summary>
        public bool LeaveRoom()
        {
            if (Busy || !PhotonNetwork.InRoom || ClaimedByCleanup) return false;
            SetNotice(null, false);
            return Begin(Op.LeaveRoom, PhotonNetwork.LeaveRoom(false), "방 나가기 요청을 보낼 수 없습니다.");
        }

        /// <summary>
        /// Sets the local participant's ready state in the room (one change at a time, confirmed by the room): ready is
        /// stored as the settings revision it is for, so it counts only while those settings stay.
        /// </summary>
        public bool SetReady(bool ready)
        {
            if (Busy || readyPending || !PhotonNetwork.InRoom || StartInProgress || !settingsGate.Settled) return false;
            var value = ready ? SettingsRevision : CoopRules.NotReady;
            if (!PhotonNetwork.LocalPlayer.SetCustomProperties(new Hashtable { { CoopRules.ReadyKey, value } }))
            {
                SetNotice("준비 상태를 보낼 수 없습니다.", true);
                Raise();
                return false;
            }
            readyPending = true;
            Raise();
            return true;
        }

        // ---- settings and start (waiting room) -------------------------------------------------------------------
        /// <summary>The host's difficulty of the current room (0 쉬움 · 1 보통 · 2 어려움; 보통 when the room has none).</summary>
        public int Difficulty => CoopSession.RoomInt((registered || handedOver) && PhotonNetwork.InRoom ? PhotonNetwork.CurrentRoom : null, CoopRules.DifficultyKey, CoopRules.DefaultDifficulty);

        /// <summary>Settings revision of the current room; every change voids the ready states given before it.</summary>
        public int SettingsRevision => CoopSession.RoomInt((registered || handedOver) && PhotonNetwork.InRoom ? PhotonNetwork.CurrentRoom : null, CoopRules.SettingsKey, 0);

        /// <summary>A start of this room is being confirmed or played (the waiting room's settings and ready are fixed meanwhile).</summary>
        public bool StartInProgress
        {
            get
            {
                var s = CoopSession.Current;
                return handedOver || (s != null && s.RoomName == CurrentRoomName && s.Stage != CoopStage.Waiting && s.Stage != CoopStage.Ended);
            }
        }

        /// <summary>
        /// Host only: a new difficulty, with a new settings revision (so every earlier ready is void). Sent as a checked
        /// change of the revision the host saw, so two changes never merge. Only while the settings are settled.
        /// </summary>
        public bool SetDifficulty(int difficulty)
        {
            if (!IsLocalHost || Busy || StartInProgress || readyPending || !settingsGate.Settled || !CoopRules.IsValidDifficulty(difficulty) || difficulty == Difficulty) return false;
            var room = PhotonNetwork.CurrentRoom;
            var revision = SettingsRevision;
            var hasRevision = room.CustomProperties != null && room.CustomProperties.ContainsKey(CoopRules.SettingsKey);
            settingsGate.Begin(difficulty, revision + 1, room.Name, roomEntry, Now);   // start is blocked from the request on
            WatchResponses(true);
            var sent = room.SetCustomProperties(
                new Hashtable { { CoopRules.DifficultyKey, difficulty }, { CoopRules.SettingsKey, revision + 1 } },
                hasRevision ? new Hashtable { { CoopRules.SettingsKey, revision } } : null);
            if (!sent)
            {
                settingsGate.NotSent();                                // nothing went out: the known settings stand
                WatchResponses(false);
                SetNotice("난이도 변경을 보낼 수 없었습니다. 설정은 그대로입니다.", true);
            }
            Raise();
            return sent;
        }

        // ---- difficulty confirmation -----------------------------------------------------------------------------
        // PUN reports a room property change through OnRoomPropertiesUpdate (sent to everyone, the sender included) and
        // a refused change (e.g. the checked revision no longer matching) only as an operation response with an error
        // code, which tells no request apart. While the change is not settled nothing else of this device changes
        // properties (ready and start are blocked), so a refused SetProperties response then belongs to it. Neither a
        // refusal nor the time limit tells the server's final settings: the state becomes Unknown and only a confirmed
        // room update — or leaving the room (e.g. 다시 입장) — settles it. The gate belongs to one room entry (roomEntry):
        // nothing of an older room or connection ever reaches a newer one.
        readonly SettingsChangeGate settingsGate = new SettingsChangeGate();
        bool watchingResponses;
        int roomEntry;                       // grows with every room this component enters
        string rejoinCode;                   // 다시 입장: the Room.Name to join once back in the lobby (one try); null = none

        static double Now => Time.realtimeSinceStartupAsDouble;

        /// <summary>This device's view of the room's settings (Settled, Waiting for a change, or Unknown).</summary>
        public SettingsState SettingsStateHere => settingsGate.State;

        /// <summary>The scene's lobby has a difficulty change not settled (checked again by the start itself).</summary>
        internal static bool SettingsUnsettled => instance != null && !instance.settingsGate.Settled;

        void WatchResponses(bool on)
        {
            if (on == watchingResponses || PhotonNetwork.NetworkingClient == null) return;
            if (on) PhotonNetwork.NetworkingClient.OpResponseReceived += OnOperationResponse;
            else PhotonNetwork.NetworkingClient.OpResponseReceived -= OnOperationResponse;
            watchingResponses = on;
        }

        void OnOperationResponse(ExitGames.Client.Photon.OperationResponse response)
        {
            if (response.OperationCode != OperationCode.SetProperties || response.ReturnCode == 0) return;
            if (settingsGate.Refused(CurrentRoomName, roomEntry) != SettingsChangeResult.BecameUnknown) return;
            SetNotice("난이도 변경이 거절되어 현재 설정을 확인하지 못했습니다. 설정이 다시 알려지면 이어서 할 수 있고, 그렇지 않으면 다시 입장하세요.", true);
            Raise();
        }

        /// <summary>A room update of this entry: the requested pair confirms it; other values of that revision or later settle it as they are.</summary>
        void ObserveSettings()
        {
            var r = settingsGate.Observe(CurrentRoomName, roomEntry, Difficulty, SettingsRevision);
            if (r == SettingsChangeResult.None) return;
            WatchResponses(false);
            if (r == SettingsChangeResult.Superseded) SetNotice("난이도가 다른 값으로 확정되었습니다. 현재 설정으로 다시 준비하세요.", true);
            else SetNotice(null, false);
        }

        /// <summary>Only leaving the room (or the connection), entering anew, or this component going away ends the question.</summary>
        void ResetSettingsGate()
        {
            settingsGate.Reset();
            WatchResponses(false);
        }

        void Update()
        {
            CheckRefreshTimeout();
            if (settingsGate.CheckTimeout(Now) != SettingsChangeResult.BecameUnknown) return;
            SetNotice("난이도 변경을 아직 확인하지 못했습니다. 늦게 확인되면 이어서 할 수 있고, 그렇지 않으면 다시 입장하세요.", true);
            Raise();
        }

        /// <summary>
        /// Recovery from Unknown settings: leaves this room and, once back in the lobby, joins the same Room.Name once —
        /// the room's settings are then those received with the new entry. Refused when the leave cannot be sent (the
        /// question then stays). The host role may pass to another participant meanwhile.
        /// </summary>
        public bool RejoinToRecover()
        {
            if (settingsGate.State != SettingsState.Unknown || Busy || !PhotonNetwork.InRoom || ClaimedByCleanup) return false;
            var code = CurrentRoomName;
            SetNotice(null, false);
            if (!Begin(Op.LeaveRoom, PhotonNetwork.LeaveRoom(false), "방 나가기 요청을 보낼 수 없습니다. 설정 확인 전까지 시작할 수 없습니다.")) return false;
            rejoinCode = code;                                         // joined once the lobby is back (Advance)
            return true;
        }

        /// <summary>Host only: confirms the start (every condition is checked again by the session).</summary>
        public bool RequestStart()
        {
            var s = CoopSession.Current;
            string why;
            if (!IsLocalHost) why = "호스트만 시작할 수 있습니다.";
            else if (settingsGate.State == SettingsState.Waiting) why = "난이도 변경을 확인하는 중입니다. 잠시 후 다시 시작하세요.";
            else if (settingsGate.State == SettingsState.Unknown) why = "현재 설정을 확인하지 못해 시작할 수 없습니다. 다시 입장하세요.";
            else if (Busy || s == null || s.RoomName != CurrentRoomName) why = "아직 시작할 수 없습니다.";
            else if (s.TryStartAsHost(Difficulty, SettingsRevision, out why)) return true;
            SetNotice(why, true);
            Raise();
            return false;
        }

        bool CanRequestFromLobby()
        {
            string why = null;
            if (!active) why = "협동 로비가 열려 있지 않습니다.";
            else if (refresh != RefreshStage.None) why = RefreshingNotice;
            else if (Busy) why = "이전 요청을 처리하는 중입니다.";
            else if (!PhotonNetwork.InLobby || !PhotonNetwork.IsConnectedAndReady) why = "로비 연결이 아직 완료되지 않았습니다.";
            if (why == null) return true;
            SetNotice(why, true);
            Raise();
            return false;
        }

        // ---- list refresh (새로고침) ------------------------------------------------------------------------------
        // The lobby is really left and the same lobby (name and type kept at the start) joined again: LeaveLobby →
        // OnLeftLobby → the usual Advance joins (JoinLobby takes the kept lobby) → OnJoinedLobby → the join's first full
        // list. PUN tells the full list (EventCode.GameList) from a change set (GameListUpdate) only in the event, which
        // reaches EventReceived after OnRoomListUpdate has applied it; a list counts only when it arrives in the lobby
        // after this join was confirmed (the client is JoinedLobby only from the join's response on). Whether the server
        // sends that list before or after the join's response is not visible in PUN's source: a full list dropped
        // before the response (not in the lobby yet) never completes the refresh — the time limit reports it instead.
        // An empty list is a received list. The time limit ends the refresh only: a leave or join already sent runs on
        // (Busy) and nothing else is sent meanwhile. No automatic retry; the connection and its region/version stay.
        // The end of a refresh and a complete list are two things: listComplete (below) tells whether the current lobby
        // join's full list arrived — also for an ordinary join, and also late after a refresh already failed (which
        // then stays failed).
        enum RefreshStage
        {
            None,
            Leaving,
            Joining,
            Waiting,
        }

        const string RefreshingNotice = "방 목록을 새로고침하는 중입니다. 끝난 뒤 다시 시도하세요.";

        RefreshStage refresh;
        TypedLobby lobbyToRejoin;            // the lobby a refresh left, joined by the next JoinLobby; null = the default lobby
        double refreshDeadline;
        bool watchingEvents;
        bool listComplete;                   // the current lobby join's full list (EventCode.GameList) arrived; reset with the cache

        /// <summary>
        /// The list holds the current lobby join's full list (an empty one too), not change sets alone: only then does a
        /// room missing from it count as gone (Rooms.Received only tells that some list update arrived).
        /// </summary>
        public bool ListComplete => listComplete;

        /// <summary>
        /// 새로고침 can start: the screen is open, nothing runs, connected to the master server outside a room, and either
        /// in the lobby this component joined or outside any lobby (then only the join is sent).
        /// </summary>
        public bool CanRefresh => registered && active && op == Op.None && refresh == RefreshStage.None && OnMasterServer && (!PhotonNetwork.InLobby || lobbyJoinedByUs);

        /// <summary>Leaves the current lobby and joins the same one again for its full room list (see above). False when it cannot start or the leave cannot be sent.</summary>
        public bool RefreshList()
        {
            if (refresh != RefreshStage.None) return false;           // a second click: the running one goes on
            if (!CanRefresh)
            {
                SetNotice("지금은 방 목록을 새로고침할 수 없습니다(다른 요청 처리 중이거나 로비 연결 전).", true);
                Raise();
                return false;
            }
            var current = PhotonNetwork.CurrentLobby;
            var lobby = current == null ? TypedLobby.Default : new TypedLobby(current.Name, current.Type);
            SetNotice(null, false);
            if (!PhotonNetwork.InLobby)
            {
                lobbyToRejoin = lobby;                                 // outside the lobby: the join alone
                StartRefresh(RefreshStage.Joining);
                JoinLobby();
                Raise();
                return refresh != RefreshStage.None;
            }
            if (!Begin(Op.LeaveLobby, PhotonNetwork.LeaveLobby(), "로비 나가기 요청을 보낼 수 없어 새로고침하지 못했습니다. 목록은 그대로입니다.")) return false;
            lobbyToRejoin = lobby;
            StartRefresh(RefreshStage.Leaving);
            Raise();
            return true;
        }

        /// <summary>True (with a notice) while a refresh runs: room requests wait for the new list.</summary>
        bool RefreshBlocks()
        {
            if (refresh == RefreshStage.None) return false;
            SetNotice(RefreshingNotice, true);
            Raise();
            return true;
        }

        void StartRefresh(RefreshStage stage)
        {
            refresh = stage;
            refreshDeadline = Now + CoopRules.ListRefreshTimeoutSeconds;
            if (ListDiagnostics) ListLog($"새로고침 시작({stage}) · {ConnectionText()}");
        }

        /// <summary>Ends a running refresh: `failure` is shown (null = nothing to say, e.g. the screen closed or the connection's own notice).</summary>
        void EndRefresh(string failure)
        {
            if (refresh == RefreshStage.None) return;
            if (ListDiagnostics) ListLog($"새로고침 끝({refresh}) · {failure ?? "알림 없음"} · 캐시 {CacheText()}");
            refresh = RefreshStage.None;                               // the list's own state (listComplete) is not touched
            if (failure != null) SetNotice(failure, true);
        }

        void WatchEvents(bool on)
        {
            if (on == watchingEvents || PhotonNetwork.NetworkingClient == null) return;
            if (on) PhotonNetwork.NetworkingClient.EventReceived += OnPhotonEvent;
            else PhotonNetwork.NetworkingClient.EventReceived -= OnPhotonEvent;
            watchingEvents = on;
        }

        /// <summary>
        /// A full list in the lobby (already applied by OnRoomListUpdate, which runs first): the current join's list is
        /// complete; while a refresh waits for it, the refresh succeeds. A change set completes neither, and a late full
        /// list never turns a refresh that already ended into a success.
        /// </summary>
        void OnPhotonEvent(ExitGames.Client.Photon.EventData e)
        {
            if (!PhotonNetwork.InLobby) return;
            if (e.Code == EventCode.GameListUpdate)
            {
                if (!listComplete && ListDiagnostics) ListLog("변경분이 전체 목록보다 먼저 도착(전체 목록으로 보지 않음)");
                return;
            }
            if (e.Code != EventCode.GameList) return;
            listComplete = true;
            if (ListDiagnostics) ListLog($"전체 목록 확인 · 캐시 {CacheText()}");
            if (refresh == RefreshStage.Waiting)
            {
                EndRefresh(null);
                SetNotice($"방 목록을 새로 받았습니다({rooms.Count}개).", false);
            }
            Raise();
        }

        void CheckRefreshTimeout()
        {
            if (refresh == RefreshStage.None || Now < refreshDeadline) return;
            EndRefresh(refresh == RefreshStage.Waiting
                ? "새 방 목록을 시간 안에 받지 못했습니다. 표시된 목록은 완전하지 않을 수 있습니다. 새로고침으로 다시 시도하세요."
                : "로비 응답이 늦어 새로고침을 마치지 못했습니다. 처리 중인 요청이 끝난 뒤 다시 새로고침하세요.");
            Raise();
        }

        // ---- current room ------------------------------------------------------------------------------------
        public bool TryGetRoom(out WaitingRoom room)
        {
            var r = (registered || handedOver) && PhotonNetwork.InRoom ? PhotonNetwork.CurrentRoom : null;
            if (r == null)
            {
                room = default;
                return false;
            }
            var display = r.CustomProperties != null && r.CustomProperties.TryGetValue(CoopRules.DisplayNameKey, out var v) && v is string s && s.Length > 0 ? s : r.Name;
            room = new WaitingRoom(r.Name, display, r.PlayerCount, r.MaxPlayers);
            return true;
        }

        /// <summary>Participants of the current room by ActorNumber (identity never by the changeable name).</summary>
        public void GetParticipants(List<Participant> into)
        {
            into.Clear();
            var r = (registered || handedOver) && PhotonNetwork.InRoom ? PhotonNetwork.CurrentRoom : null;
            if (r == null) return;
            foreach (var p in r.Players.Values)
            {
                var name = string.IsNullOrWhiteSpace(p.NickName) ? CoopRules.DefaultParticipantName(p.ActorNumber) : p.NickName;
                into.Add(new Participant(p.ActorNumber, name, p.ActorNumber == r.MasterClientId, IsReady(p), p.IsLocal));
            }
            into.Sort((a, b) => a.ActorNumber.CompareTo(b.ActorNumber));
        }

        /// <summary>Ready for the room's current settings revision (a ready of earlier settings does not count).</summary>
        bool IsReady(Player p) =>
            p.CustomProperties != null && p.CustomProperties.TryGetValue(CoopRules.ReadyKey, out var v) && CoopRules.IsReadyFor(v, SettingsRevision);

        public bool LocalReady => (registered || handedOver) && PhotonNetwork.InRoom && IsReady(PhotonNetwork.LocalPlayer);

        public bool IsLocalHost => (registered || handedOver) && PhotonNetwork.InRoom && PhotonNetwork.IsMasterClient;

        // ---- next step ---------------------------------------------------------------------------------------
        bool Begin(Op o, bool sent, string failure)
        {
            if (sent) op = o;
            else SetNotice(failure, true);
            Raise();
            return sent;
        }

        /// <summary>After any change: nothing while an operation runs; otherwise towards the lobby (Active) or winding down.</summary>
        void Next()
        {
            if (op == Op.None)
            {
                if (active) Advance();
                else Settle();
            }
            if (active && PhotonNetwork.InRoom && !ClaimedByCleanup) Watch(CoopSession.EnsureForCurrentRoom());   // the room's start manager
            Raise();
        }

        CoopSession watched;

        /// <summary>The waiting room follows the session's start state; one subscription per session object at most.</summary>
        void Watch(CoopSession s)
        {
            if (watched == s) return;
            if (watched != null) watched.Changed -= Raise;
            watched = s;
            if (s != null) s.Changed += Raise;
        }

        void Advance()
        {
            if (PhotonNetwork.InRoom) return;
            if (PhotonNetwork.InLobby)
            {
                if (!lobbyJoinedByUs) JoinLobby();                    // someone else's lobby: join again for a full list
                else if (rejoinCode != null)
                {
                    var code = rejoinCode;                             // 다시 입장: one join of the room just left
                    rejoinCode = null;
                    if (!Join(code)) SetNotice("방에 다시 들어가지 못했습니다. 목록이나 RoomCode로 다시 참가하세요.", true);
                }
                return;
            }
            if (PhotonNetwork.IsConnectedAndReady && PhotonNetwork.Server == ServerConnection.MasterServer)
            {
                JoinLobby();
                return;
            }
            if (IdleDisconnected && connectWanted)
            {
                connectWanted = false;
                Connect();
            }
        }

        void Settle()
        {
            if (InOurRoom) Begin(Op.LeaveRoom, PhotonNetwork.LeaveRoom(false), "방 나가기 요청을 보낼 수 없습니다.");
            else if (owned && !IdleDisconnected)
            {
                op = Op.Disconnect;
                ourDisconnect = true;
                PhotonNetwork.Disconnect();
            }
            else if (PhotonNetwork.InLobby && lobbyJoinedByUs) Begin(Op.LeaveLobby, PhotonNetwork.LeaveLobby(), "로비 나가기 요청을 보낼 수 없습니다.");
        }

        void JoinLobby()
        {
            var again = PhotonNetwork.InLobby;                     // a lobby joined by someone else: joined again for a full list
            var lobby = lobbyToRejoin;                             // the lobby a refresh left; null = the default lobby
            lobbyToRejoin = null;
            listLobby++;                                           // a new diagnostics section (logs only)
            ClearRooms("로비 참가 요청");                           // the full list follows the join
            lobbyJoinedByUs = false;
            var sent = Begin(Op.JoinLobby, PhotonNetwork.JoinLobby(lobby), "로비 참가 요청을 보낼 수 없습니다.");
            if (refresh != RefreshStage.None)
            {
                if (sent) refresh = RefreshStage.Joining;
                else EndRefresh("로비에 다시 참가하지 못해 방 목록을 받지 못했습니다. 새로고침으로 다시 참가하거나 뒤로 갔다가 다시 들어오세요.");
            }
            if (ListDiagnostics) ListLog($"로비 참가 요청 {(sent ? "전송" : "전송 실패")}{(again ? " · 이미 로비에 있던 연결의 재참가" : "")} · {ConnectionText()}");
        }

        void Connect()
        {
            var settings = PhotonNetwork.PhotonServerSettings;
            if (settings == null || settings.AppSettings == null)
            {
                SetNotice("Photon 설정(PhotonServerSettings)을 찾지 못해 연결할 수 없습니다.", true);
                return;
            }
            var app = settings.AppSettings;
            if (app.UseNameServer ? string.IsNullOrEmpty(app.AppIdRealtime) : string.IsNullOrEmpty(app.Server))
            {
                SetNotice("Photon 설정의 AppId 또는 서버 주소가 비어 있어 연결할 수 없습니다.", true);   // the values themselves are never shown
                return;
            }
            unexpectedDisconnect = false;
            CoopSession.OwnDisconnect.Forget();                     // a new connection: an older intended end never describes it
            if (Begin(Op.Connect, PhotonNetwork.ConnectUsingSettings(), "Photon 연결을 시작할 수 없습니다(설정·상태 확인).")) owned = true;
        }

        static void ClearLocalReady()
        {
            PhotonNetwork.LocalPlayer.SetCustomProperties(new Hashtable { { CoopRules.ReadyKey, null } });   // outside a room: removed locally
        }

        void SetNotice(string text, bool error)
        {
            Notice = text;
            NoticeIsError = error && text != null;
        }

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
                Debug.LogException(e);                              // a screen's failure never stops the connection handling
            }
        }

        // ---- lifetime ------------------------------------------------------------------------------------------
        void OnApplicationQuit() => quitting = true;

        /// <summary>
        /// Disabled or destroyed (scene change): no callbacks reach this component any more, so its responsibilities end
        /// here or are handed over exactly once — an owned connection is disconnected (that also ends any room or
        /// entry); on a borrowed connection a pending create/join, or the room of its own it is in, goes to
        /// CoopRoomCleanup (which leaves only that Room.Name and unregisters when done); a lobby join still running, or a
        /// lobby leave sent (also the one sent here for a lobby it joined), goes to CoopLobbyCleanup (which leaves the
        /// lobby once the join is through and ends with the leave). The connection itself is kept when borrowed.
        /// </summary>
        void OnDisable()
        {
            if (instance == this) instance = null;
            Watch(null);
            ResetSettingsGate();                                   // this component goes away (its room is left or handed over)
            EndRefresh(null);                                      // a leave or join still running is settled below like any other
            lobbyToRejoin = null;
            WatchEvents(false);
            if (!registered) return;                               // never entered, or handed over to a coop start: nothing to wind down
            PhotonNetwork.RemoveCallbackTarget(this);
            registered = false;
            active = false;
            connectWanted = false;
            if (!quitting)
            {
                if (owned && !IdleDisconnected) PhotonNetwork.Disconnect();
                else if ((op == Op.Create || op == Op.Join) && requestedRoom != null) CoopRoomCleanup.Take(requestedRoom);
                else if (op != Op.LeaveRoom && InOurRoom) CoopRoomCleanup.Take(ourRoom);
                else if (op == Op.JoinLobby) CoopLobbyCleanup.TakeJoin();          // borrowed: leave the lobby once this join is through
                else if (op == Op.LeaveLobby) CoopLobbyCleanup.TakeLeave();        // borrowed: the leave runs on; a new lobby waits for it
                else if (op == Op.None && PhotonNetwork.InLobby && lobbyJoinedByUs && PhotonNetwork.LeaveLobby()) CoopLobbyCleanup.TakeLeave();
            }
            op = Op.None;
            requestedRoom = ourRoom = null;
            owned = ourDisconnect = lobbyJoinedByUs = readyPending = unexpectedDisconnect = false;
            ClearRooms("컴포넌트 비활성화");
        }

        // ---- IConnectionCallbacks ----------------------------------------------------------------------------
        public void OnConnected()
        {
        }

        public void OnConnectedToMaster()
        {
            if (op == Op.Connect || op == Op.LeaveRoom) op = Op.None;   // a left room ends here, back on the master server
            connectWanted = false;                                       // connected: the wish is fulfilled (lobby rejoins need none)
            CoopSession.OwnDisconnect.Forget();                          // a connection is up: an older intended end is over
            Next();
        }

        /// <summary>
        /// Intended (this lobby's own Disconnect, or a coop session's recorded one — also when that session has already
        /// gone) or a failure. After an intended end the wish of a user who entered meanwhile is kept and connects once;
        /// after a failure every wish is dropped (no automatic reconnect).
        /// </summary>
        public void OnDisconnected(DisconnectCause cause)
        {
            op = Op.None;
            owned = false;
            lobbyJoinedByUs = false;
            readyPending = false;
            requestedRoom = ourRoom = null;
            rejoinCode = null;
            EndRefresh(null);                                            // the disconnect's own notice follows (or none when intended)
            lobbyToRejoin = null;
            ClearRooms($"연결 끊김 {cause}");
            ResetSettingsGate();
            var bySession = CoopSession.OwnDisconnect.Consume();
            var requested = ourDisconnect || bySession;
            ourDisconnect = false;
            if (!DisconnectExpectation.ShouldReconnect(requested, active, connectWanted)) connectWanted = false;
            if (!requested)
            {
                connectWanted = false;                                   // never reconnect by itself after an unexpected end
                unexpectedDisconnect = true;
                SetNotice(Explain(cause), true);
                Debug.LogWarning($"[협동] Photon 연결 끊김: {cause}");
            }
            Next();
        }

        public void OnRegionListReceived(RegionHandler regionHandler)
        {
        }

        public void OnCustomAuthenticationResponse(Dictionary<string, object> data)
        {
        }

        public void OnCustomAuthenticationFailed(string debugMessage)
        {
            SetNotice("Photon 인증에 실패했습니다.", true);
            Raise();
        }

        // ---- ILobbyCallbacks ---------------------------------------------------------------------------------
        public void OnJoinedLobby()
        {
            if (op == Op.JoinLobby) op = Op.None;
            lobbyJoinedByUs = true;
            if (refresh == RefreshStage.Joining) refresh = RefreshStage.Waiting;   // from now on a full list is this join's
            if (ListDiagnostics) ListLog($"로비 참가 완료 · {ConnectionText()} · 캐시 {CacheText()}");
            Next();
        }

        public void OnLeftLobby()
        {
            if (op == Op.LeaveLobby) op = Op.None;
            lobbyJoinedByUs = false;
            ClearRooms("로비 이탈");
            Next();
        }

        public void OnRoomListUpdate(List<RoomInfo> roomList)
        {
            if (!PhotonNetwork.InLobby)
            {
                if (ListDiagnostics) ListLog($"수신 {(roomList == null ? 0 : roomList.Count)}개 무시: 로비 상태가 아님(상태 {PhotonNetwork.NetworkClientState})");
                return;
            }
            if (!ListDiagnostics)
            {
                if (rooms.Apply(Convert(roomList ?? noRooms))) Raise();
                return;
            }
            var changes = new List<LobbyRoom>(Convert(roomList ?? noRooms));
            var reasons = DescribeChanges(changes);                 // against the list before this change set
            var before = CacheText();
            var changed = rooms.Apply(changes);
            ListLog($"수신 {changes.Count}개 · 캐시 {before} → {CacheText()}{(changed ? "" : " (변경 없음)")}{reasons}");
            if (changed) Raise();
        }

        static IEnumerable<LobbyRoom> Convert(List<RoomInfo> list)
        {
            foreach (var r in list)
            {
                var display = r.CustomProperties != null && r.CustomProperties.TryGetValue(CoopRules.DisplayNameKey, out var v) && v is string s && s.Length > 0 ? s : r.Name;
                yield return new LobbyRoom(r.Name, display, r.PlayerCount, r.MaxPlayers, r.IsOpen, r.IsVisible, r.RemovedFromList);
            }
        }

        public void OnLobbyStatisticsUpdate(List<TypedLobbyInfo> lobbyStatistics)
        {
        }

        // ---- room list diagnostics (off by default) -------------------------------------------------------------
        // Receive, cache and screen write one line each per event (never per frame), tagged with the lobby join they
        // belong to (L<n>, raised by every join request), so the three can be compared for the same list. Shown: region,
        // AppVersion, lobby, rooms by Room.Name; never the AppId, tokens or other connection settings.
        const string ListLogArgument = "-wordroom-list-log";
        static bool? listLogArgument;
        int listLobby;

        /// <summary>Room list diagnostics are on (Inspector, or the command line argument -wordroom-list-log in a build).</summary>
        public bool ListDiagnostics => listDiagnostics || ListLogArgumentGiven;

        static bool ListLogArgumentGiven
        {
            get
            {
                if (listLogArgument == null) listLogArgument = Array.IndexOf(Environment.GetCommandLineArgs(), ListLogArgument) >= 0;
                return listLogArgument.Value;
            }
        }

        /// <summary>One diagnostics line in the current lobby join's section; nothing when diagnostics are off.</summary>
        public void ListLog(string message)
        {
            if (ListDiagnostics) Debug.Log($"[협동 목록 L{listLobby}] {message}", this);
        }

        void ClearRooms(string why)
        {
            if (ListDiagnostics) ListLog($"캐시 비움({why}) · {CacheText()} → 0개");
            rooms.Clear();
            listComplete = false;                                  // a new join, a left lobby or connection: no earlier join's full list counts
        }

        string CacheText() => $"{rooms.Count}개 · Version {rooms.Version} · Received {rooms.Received} · 전체 목록 {(listComplete ? "확인" : "미확인")}";

        string ConnectionText()
        {
            var lobby = PhotonNetwork.CurrentLobby;
            var lobbyText = lobby == null || lobby.IsDefault ? $"기본 로비({(lobby == null ? LobbyType.Default : lobby.Type)})" : $"로비 '{lobby.Name}'({lobby.Type})";
            return $"지역 {PhotonNetwork.CloudRegion ?? "-"} · AppVersion {PhotonNetwork.AppVersion} · {lobbyText} · {(owned ? "직접 만든 연결" : "빌린 연결")} · 상태 {PhotonNetwork.NetworkClientState}";
        }

        /// <summary>What applying `changes` to the current list does to each room (the same rules as RoomListCache.Apply).</summary>
        string DescribeChanges(List<LobbyRoom> changes)
        {
            var sb = new StringBuilder();
            foreach (var r in changes)
            {
                string why;
                if (string.IsNullOrEmpty(r.Name)) why = "무시(이름 없음)";
                else
                {
                    var known = rooms.TryGet(r.Name, out var old);
                    if (r.Removed || !r.IsVisible)
                        why = known ? (r.Removed ? "제거(RemovedFromList)" : "제거(비공개)") : (r.Removed ? "무시(목록에 없는 방의 제거)" : "무시(목록에 없는 비공개 방)");
                    else if (!known) why = "추가";
                    else why = old.Equals(r) ? "동일" : "갱신";
                }
                sb.Append($"\n  {why}: {r.Name} '{r.DisplayName}' {r.PlayerCount}/{r.MaxPlayers} open={r.IsOpen} visible={r.IsVisible} removed={r.Removed}");
            }
            return sb.ToString();
        }

        // ---- IMatchmakingCallbacks -----------------------------------------------------------------------------
        public void OnFriendListUpdate(List<FriendInfo> friendList)
        {
        }

        public void OnCreatedRoom()
        {
        }

        public void OnCreateRoomFailed(short returnCode, string message)
        {
            if (op == Op.Create)                                    // ours (a cleanup's late failure is not this flow's to report)
            {
                op = Op.None;
                requestedRoom = null;
                SetNotice(EntryFailure(returnCode, "만들기"), true);
            }
            Next();                                                 // back into the lobby when the failure came from the game server
        }

        public void OnJoinedRoom()
        {
            if (op == Op.Create || op == Op.Join)
            {
                op = Op.None;
                ourRoom = CurrentRoomName;                          // the room this component asked for
            }
            requestedRoom = null;
            lobbyJoinedByUs = false;                                // entering a room leaves the lobby
            readyPending = false;
            roomEntry++;                                            // a new entry: its settings are those received with it
            ResetSettingsGate();
            EndRefresh(null);                                       // not by this flow during a refresh (room requests wait); never kept into a room
            lobbyToRejoin = null;
            ClearRooms("방 입장");
            Next();                                                 // no longer Active (went back meanwhile): leaves its own room again
        }

        public void OnJoinRoomFailed(short returnCode, string message)
        {
            if (op == Op.Join)
            {
                op = Op.None;
                requestedRoom = null;
                SetNotice(EntryFailure(returnCode, "입장"), true);
            }
            Next();
        }

        public void OnJoinRandomFailed(short returnCode, string message)
        {
        }

        public void OnLeftRoom()
        {
            readyPending = false;
            ResetSettingsGate();                                   // the room is really left: its question is gone
            if (op != Op.LeaveRoom && active && ourRoom != null) SetNotice("방에서 나왔습니다.", false);   // not for a cleanup's leave
            ourRoom = null;
            Raise();                                                // the lobby follows on OnConnectedToMaster
        }

        // ---- IInRoomCallbacks ----------------------------------------------------------------------------------
        public void OnPlayerEnteredRoom(Player newPlayer) => Raise();

        public void OnPlayerLeftRoom(Player otherPlayer) => Raise();

        public void OnRoomPropertiesUpdate(Hashtable propertiesThatChanged)
        {
            ObserveSettings();
            Raise();
        }

        public void OnPlayerPropertiesUpdate(Player targetPlayer, Hashtable changedProps)
        {
            if (targetPlayer != null && targetPlayer.IsLocal && changedProps != null && changedProps.ContainsKey(CoopRules.ReadyKey)) readyPending = false;
            Raise();
        }

        public void OnMasterClientSwitched(Player newMasterClient)
        {
            // a change of this device stays in question when the host role moves (it may still apply on the server)
            if (newMasterClient != null)
                SetNotice($"호스트가 {(string.IsNullOrWhiteSpace(newMasterClient.NickName) ? CoopRules.DefaultParticipantName(newMasterClient.ActorNumber) : newMasterClient.NickName)}(으)로 바뀌었습니다.", false);
            Raise();
        }

        // ---- texts ---------------------------------------------------------------------------------------------
        static string EntryFailure(short code, string verb)
        {
            switch (code)
            {
                case ErrorCode.GameDoesNotExist: return "해당 RoomCode의 방이 없습니다(대소문자 구분).";
                case ErrorCode.GameFull: return "방 정원이 가득 찼습니다.";
                case ErrorCode.GameClosed: return "방이 입장을 받지 않습니다(입장 닫힘).";
                case ErrorCode.GameIdAlreadyExists: return "같은 RoomCode의 방이 이미 있습니다. 다시 만들어 주세요.";
                case ErrorCode.ServerFull: return "서버가 가득 차 방에 들어갈 수 없습니다.";
                default: return $"방 {verb}에 실패했습니다(오류 코드 {code}).";
            }
        }

        static string Explain(DisconnectCause cause)
        {
            switch (cause)
            {
                case DisconnectCause.ExceptionOnConnect:
                case DisconnectCause.DnsExceptionOnConnect:
                case DisconnectCause.ServerAddressInvalid:
                    return "Photon 서버에 연결할 수 없습니다. 네트워크를 확인한 뒤 다시 연결하세요.";
                case DisconnectCause.ServerTimeout:
                case DisconnectCause.ClientTimeout:
                    return "서버 응답이 없어 연결이 끊겼습니다.";
                case DisconnectCause.InvalidAuthentication:
                    return "Photon 인증에 실패했습니다(AppId 설정 확인).";
                case DisconnectCause.CustomAuthenticationFailed:
                case DisconnectCause.AuthenticationTicketExpired:
                    return "Photon 인증이 거절되었거나 만료되었습니다.";
                case DisconnectCause.MaxCcuReached:
                    return "동시 접속 한도에 도달해 연결할 수 없습니다.";
                case DisconnectCause.InvalidRegion:
                    return "Photon 지역 설정이 올바르지 않습니다.";
                case DisconnectCause.Exception:
                case DisconnectCause.SendException:
                case DisconnectCause.ReceiveException:
                    return "네트워크 오류로 연결이 끊겼습니다.";
                default:
                    return $"연결이 끊겼습니다({cause}).";
            }
        }
    }
}
