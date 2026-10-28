using System;
using System.Collections.Generic;
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

        /// <summary>Raised on the main thread after every change of phase, list, room, participants or notice.</summary>
        public event Action Changed;

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

        static string CurrentRoomName => PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom != null ? PhotonNetwork.CurrentRoom.Name : null;

        /// <summary>The current room is being left by a CoopRoomCleanup (not this flow's to show or leave).</summary>
        static bool ClaimedByCleanup => CoopRoomCleanup.Claims(CurrentRoomName);

        /// <summary>In the room this component itself created or joined (and no cleanup has taken it).</summary>
        bool InOurRoom => ourRoom != null && CurrentRoomName == ourRoom && !ClaimedByCleanup;

        /// <summary>
        /// Whether entering or Retry should start a connection: there is none, or this component is disconnecting its
        /// own one right now (the user came back during the wind-down). Any other state is reused as it is.
        /// </summary>
        bool NeedsConnection => IdleDisconnected || (op == Op.Disconnect && ourDisconnect);

        // ---- requests from the screens -----------------------------------------------------------------------
        /// <summary>A coop screen opened: go to the lobby (connecting when needed). False with a notice when coop cannot be used.</summary>
        public bool Enter()
        {
            if (!registered)
            {
                PhotonNetwork.AddCallbackTarget(this);              // once; removed in OnDisable
                registered = true;
            }
            active = true;
            connectWanted = NeedsConnection;                       // an existing connection is reused: no wish is left behind
            SetNotice(null, false);
            if (PhotonNetwork.OfflineMode)
            {
                SetNotice("Photon 오프라인 모드에서는 협동 로비를 쓸 수 없습니다.", true);
                Raise();
                return false;
            }
            Next();
            return true;
        }

        /// <summary>The coop screens closed: winds down after the running operation (see the class summary).</summary>
        public void Exit()
        {
            if (!registered) return;
            active = false;
            connectWanted = false;
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
                CustomRoomProperties = new Hashtable { { CoopRules.DisplayNameKey, CoopRules.CleanDisplayName(displayName, code) } },
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

        /// <summary>Sets the local participant's ready flag in the room (one change at a time, confirmed by the room).</summary>
        public bool SetReady(bool ready)
        {
            if (Busy || readyPending || !PhotonNetwork.InRoom) return false;
            if (!PhotonNetwork.LocalPlayer.SetCustomProperties(new Hashtable { { CoopRules.ReadyKey, ready } }))
            {
                SetNotice("준비 상태를 보낼 수 없습니다.", true);
                Raise();
                return false;
            }
            readyPending = true;
            Raise();
            return true;
        }

        bool CanRequestFromLobby()
        {
            string why = null;
            if (!active) why = "협동 로비가 열려 있지 않습니다.";
            else if (Busy) why = "이전 요청을 처리하는 중입니다.";
            else if (!PhotonNetwork.InLobby || !PhotonNetwork.IsConnectedAndReady) why = "로비 연결이 아직 완료되지 않았습니다.";
            if (why == null) return true;
            SetNotice(why, true);
            Raise();
            return false;
        }

        // ---- current room ------------------------------------------------------------------------------------
        public bool TryGetRoom(out WaitingRoom room)
        {
            var r = registered && PhotonNetwork.InRoom ? PhotonNetwork.CurrentRoom : null;
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
            var r = registered && PhotonNetwork.InRoom ? PhotonNetwork.CurrentRoom : null;
            if (r == null) return;
            foreach (var p in r.Players.Values)
            {
                var name = string.IsNullOrWhiteSpace(p.NickName) ? CoopRules.DefaultParticipantName(p.ActorNumber) : p.NickName;
                into.Add(new Participant(p.ActorNumber, name, p.ActorNumber == r.MasterClientId, IsReady(p), p.IsLocal));
            }
            into.Sort((a, b) => a.ActorNumber.CompareTo(b.ActorNumber));
        }

        static bool IsReady(Player p) => p.CustomProperties != null && p.CustomProperties.TryGetValue(CoopRules.ReadyKey, out var v) && v is bool b && b;

        public bool LocalReady => registered && PhotonNetwork.InRoom && IsReady(PhotonNetwork.LocalPlayer);

        public bool IsLocalHost => registered && PhotonNetwork.InRoom && PhotonNetwork.IsMasterClient;

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
            Raise();
        }

        void Advance()
        {
            if (PhotonNetwork.InRoom) return;
            if (PhotonNetwork.InLobby)
            {
                if (!lobbyJoinedByUs) JoinLobby();                    // someone else's lobby: join again for a full list
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
            rooms.Clear();                                         // the full list follows the join
            lobbyJoinedByUs = false;
            Begin(Op.JoinLobby, PhotonNetwork.JoinLobby(), "로비 참가 요청을 보낼 수 없습니다.");
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
        /// CoopRoomCleanup (which leaves only that Room.Name and unregisters when done); a lobby it joined is left. A
        /// leave already sent needs nothing more. The connection itself is kept when borrowed.
        /// </summary>
        void OnDisable()
        {
            if (!registered) return;
            PhotonNetwork.RemoveCallbackTarget(this);
            registered = false;
            active = false;
            connectWanted = false;
            if (!quitting)
            {
                if (owned && !IdleDisconnected) PhotonNetwork.Disconnect();
                else if ((op == Op.Create || op == Op.Join) && requestedRoom != null) CoopRoomCleanup.Take(requestedRoom);
                else if (op != Op.LeaveRoom && InOurRoom) CoopRoomCleanup.Take(ourRoom);
                else if (op == Op.None && PhotonNetwork.InLobby && lobbyJoinedByUs) PhotonNetwork.LeaveLobby();
            }
            op = Op.None;
            requestedRoom = ourRoom = null;
            owned = ourDisconnect = lobbyJoinedByUs = readyPending = unexpectedDisconnect = false;
            rooms.Clear();
        }

        // ---- IConnectionCallbacks ----------------------------------------------------------------------------
        public void OnConnected()
        {
        }

        public void OnConnectedToMaster()
        {
            if (op == Op.Connect || op == Op.LeaveRoom) op = Op.None;   // a left room ends here, back on the master server
            connectWanted = false;                                       // connected: the wish is fulfilled (lobby rejoins need none)
            Next();
        }

        public void OnDisconnected(DisconnectCause cause)
        {
            op = Op.None;
            owned = false;
            lobbyJoinedByUs = false;
            readyPending = false;
            requestedRoom = ourRoom = null;
            rooms.Clear();
            var requested = ourDisconnect;
            ourDisconnect = false;
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
            Next();
        }

        public void OnLeftLobby()
        {
            if (op == Op.LeaveLobby) op = Op.None;
            lobbyJoinedByUs = false;
            rooms.Clear();
            Next();
        }

        public void OnRoomListUpdate(List<RoomInfo> roomList)
        {
            if (!PhotonNetwork.InLobby) return;
            if (rooms.Apply(Convert(roomList ?? noRooms))) Raise();
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
            rooms.Clear();
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
            if (op != Op.LeaveRoom && active && ourRoom != null) SetNotice("방에서 나왔습니다.", false);   // not for a cleanup's leave
            ourRoom = null;
            Raise();                                                // the lobby follows on OnConnectedToMaster
        }

        // ---- IInRoomCallbacks ----------------------------------------------------------------------------------
        public void OnPlayerEnteredRoom(Player newPlayer) => Raise();

        public void OnPlayerLeftRoom(Player otherPlayer) => Raise();

        public void OnRoomPropertiesUpdate(Hashtable propertiesThatChanged) => Raise();

        public void OnPlayerPropertiesUpdate(Player targetPlayer, Hashtable changedProps)
        {
            if (targetPlayer != null && targetPlayer.IsLocal && changedProps != null && changedProps.ContainsKey(CoopRules.ReadyKey)) readyPending = false;
            Raise();
        }

        public void OnMasterClientSwitched(Player newMasterClient)
        {
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
