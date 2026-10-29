using System.Collections.Generic;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace WordRoom.Network
{
    /// <summary>
    /// Takes over the lobby request still running when a CoopLobby on a connection it did not make is disabled or
    /// destroyed (an owned connection is simply disconnected instead; rooms are CoopRoomCleanup's): a lobby join is left
    /// once it is through (one LeaveLobby), a lobby leave already sent is only awaited. It ends when the leave is
    /// reported, the leave cannot be sent, a room is entered or the connection ends — the connection itself is never
    /// disconnected here. A CoopLobby that is entered meanwhile adopts the running request as its own pending operation
    /// (Adopt): the cleanup then ends at once, so nothing is sent twice and a lobby the new flow uses is never left late.
    /// Not a component; at most one exists (a client has one lobby request at a time), registered for callbacks only
    /// while it works.
    /// </summary>
    internal sealed class CoopLobbyCleanup : ILobbyCallbacks, IMatchmakingCallbacks, IConnectionCallbacks
    {
        public enum Request
        {
            None,
            Join,
            Leave,
        }

        static CoopLobbyCleanup current;

        bool leaving;
        bool finished;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => current = null;

        CoopLobbyCleanup(bool leaving) => this.leaving = leaving;

        /// <summary>A lobby join was sent and not answered yet: leave the lobby once it is joined.</summary>
        public static void TakeJoin() => Start(false);

        /// <summary>A lobby leave was sent: wait for it (a new CoopLobby must not join meanwhile).</summary>
        public static void TakeLeave() => Start(true);

        static void Start(bool leaving)
        {
            if (current != null) current.Finish();                 // only one lobby request can be pending: the older claim is over
            current = new CoopLobbyCleanup(leaving);
            PhotonNetwork.AddCallbackTarget(current);
        }

        /// <summary>The running request for a CoopLobby that is entered now (None when there is none); the cleanup ends.</summary>
        public static Request Adopt()
        {
            var c = current;
            if (c == null) return Request.None;
            var request = c.leaving ? Request.Leave : Request.Join;
            c.Finish();
            return request;
        }

        void Finish()
        {
            if (finished) return;
            finished = true;
            PhotonNetwork.RemoveCallbackTarget(this);
            if (current == this) current = null;
        }

        // ---- ILobbyCallbacks -------------------------------------------------------------------------------------
        public void OnJoinedLobby()
        {
            if (finished || leaving) return;
            leaving = true;                                         // the stopped flow's join is through: leave exactly once
            if (PhotonNetwork.LeaveLobby()) return;
            Debug.LogWarning("[협동] 정리할 로비의 나가기 요청을 보낼 수 없어 정리를 끝냅니다(연결은 유지).");
            Finish();
        }

        public void OnLeftLobby()
        {
            if (leaving) Finish();
        }

        public void OnRoomListUpdate(List<RoomInfo> roomList)
        {
        }

        public void OnLobbyStatisticsUpdate(List<TypedLobbyInfo> lobbyStatistics)
        {
        }

        // ---- IMatchmakingCallbacks -------------------------------------------------------------------------------
        public void OnJoinedRoom() => Finish();                     // entering a room leaves the lobby: nothing pending any more

        public void OnFriendListUpdate(List<FriendInfo> friendList)
        {
        }

        public void OnCreatedRoom()
        {
        }

        public void OnCreateRoomFailed(short returnCode, string message)
        {
        }

        public void OnJoinRoomFailed(short returnCode, string message)
        {
        }

        public void OnJoinRandomFailed(short returnCode, string message)
        {
        }

        public void OnLeftRoom()
        {
        }

        // ---- IConnectionCallbacks ------------------------------------------------------------------------------
        public void OnDisconnected(DisconnectCause cause) => Finish();

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
    }
}
