using System.Collections.Generic;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace WordRoom.Network
{
    /// <summary>
    /// Takes over one room entry of a CoopLobby that was disabled or destroyed (scene change) on a connection it did not
    /// make (an owned connection is simply disconnected instead): the Room.Name it asked to create or join, or the room
    /// of its own it was in. When that exact room is (or becomes) the current room, it is left once and the cleanup ends
    /// when Photon reports the leave; it also ends when the entry fails, another room is entered, the leave cannot be
    /// sent or the connection ends. The connection itself is never disconnected here. Not a component and never kept
    /// past its job: at most one exists (a client has one room entry at a time; a newer claim ends an older one), it is
    /// registered for callbacks only while it works, and a CoopLobby treats a claimed room as not its own (no second
    /// leave request, not shown as its waiting room).
    /// </summary>
    internal sealed class CoopRoomCleanup : IMatchmakingCallbacks, IConnectionCallbacks
    {
        static CoopRoomCleanup current;

        readonly string room;
        bool leaving;
        bool finished;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => current = null;

        CoopRoomCleanup(string room) => this.room = room;

        /// <summary>A cleanup is responsible for this Room.Name now.</summary>
        public static bool Claims(string roomName) => roomName != null && current != null && current.room == roomName;

        /// <summary>
        /// Starts the cleanup of `roomName` (requested by a CoopLobby that stops now). Leaves at once when it is already
        /// the current room, else waits for the entry's result.
        /// </summary>
        public static void Take(string roomName)
        {
            if (string.IsNullOrEmpty(roomName)) return;
            if (current != null)
            {
                if (current.room == roomName) return;               // already handled
                current.Finish();                                  // only one entry can be pending: the older claim is over
            }
            var c = new CoopRoomCleanup(roomName);
            current = c;
            PhotonNetwork.AddCallbackTarget(c);
            if (IsCurrent(roomName)) c.Leave();
        }

        static bool IsCurrent(string roomName) => PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom != null && PhotonNetwork.CurrentRoom.Name == roomName;

        void Leave()
        {
            leaving = true;
            if (PhotonNetwork.LeaveRoom(false)) return;
            Debug.LogWarning($"[협동] 정리할 방 {room}의 나가기 요청을 보낼 수 없어 정리를 끝냅니다.");
            Finish();
        }

        void Finish()
        {
            if (finished) return;
            finished = true;
            PhotonNetwork.RemoveCallbackTarget(this);
            if (current == this) current = null;
        }

        // ---- IMatchmakingCallbacks -------------------------------------------------------------------------------
        public void OnJoinedRoom()
        {
            if (finished || leaving) return;
            if (IsCurrent(room)) Leave();                           // the late result of the stopped request: leave exactly this room
            else Finish();                                          // another room: the request is not pending any more
        }

        public void OnCreateRoomFailed(short returnCode, string message)
        {
            if (!leaving) Finish();
        }

        public void OnJoinRoomFailed(short returnCode, string message)
        {
            if (!leaving) Finish();
        }

        public void OnLeftRoom()
        {
            if (leaving) Finish();
        }

        public void OnFriendListUpdate(List<FriendInfo> friendList)
        {
        }

        public void OnCreatedRoom()
        {
        }

        public void OnJoinRandomFailed(short returnCode, string message)
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
