using System;
using System.Collections.Generic;
using System.Linq;

namespace WordRoom.Generation
{
    /// <summary>
    /// What became of one request (by its player and RequestId) as the authority recorded it — told to the requester as
    /// the answer or on a later query, never by running it again: Accepted (with the revision that confirmed it), Refused
    /// (checked and refused: nothing changed), Failed (an error while it was applied: the state may have changed; the
    /// revision is the state after it), Pending (received, not handled yet) or Unknown (never received).
    /// </summary>
    public enum RequestOutcome
    {
        Refused,
        Accepted,
        Failed,
        Pending,
        Unknown,
    }

    /// <summary>
    /// One confirmed exploration change of a session as plain values — what a device without a coordinator needs to
    /// bring its SessionCopy from Revision − 1 to Revision (SessionCopy.Apply): who asked (PlayerId, RequestId), the
    /// door opening with the rooms its fill committed and the room it prepared (Prepare), or the requester's entry
    /// with its moves and the team's first visit (Enter), the requester's return to the start room with its return count
    /// (Return: To is the origin, nothing else changes), the session status after it and the one result when this
    /// change reached the goal. Made by the host from the coordinator's confirmed update (FromUpdate); no generation
    /// records, relations, vectors or recent words.
    /// </summary>
    public sealed class SessionChange
    {
        public string SessionId;
        public long Revision;
        public string PlayerId;
        public long RequestId;
        /// <summary>Prepare, Enter or Return (Move is a single-player debug request and never a coop change).</summary>
        public RequestKind Kind;
        public GridPos From;
        public GridPos To;
        /// <summary>Prepare: this change stored the connection From–To Open (false: it was already open).</summary>
        public bool DoorOpened;
        /// <summary>Prepare: rooms committed by the fill around To, in commit order.</summary>
        public readonly List<KeyValuePair<GridPos, int>> Added = new List<KeyValuePair<GridPos, int>>();
        /// <summary>Enter: nobody of the team had entered To before.</summary>
        public bool TeamFirstVisit;
        /// <summary>The requester's moves after this change.</summary>
        public int Moves;
        /// <summary>The requester's returns to the start room after this change (one more than before for a Return, unchanged otherwise).</summary>
        public int Returns;
        public string Status;
        /// <summary>The session result confirmed by this change (the goal entry); null otherwise.</summary>
        public SessionResult Result;

        /// <summary>The confirmed update of an accepted Prepare or Enter (a copy handed out by the coordinator).</summary>
        public static SessionChange FromUpdate(GenerationUpdate u)
        {
            if (u == null) throw new ArgumentException("확정 변경이 없습니다.");
            RequestKind kind;
            if (u.Kind == "prepare") kind = RequestKind.Prepare;
            else if (u.Kind == "enter") kind = RequestKind.Enter;
            else if (u.Kind == "return") kind = RequestKind.Return;
            else throw new ArgumentException($"협동 변경으로 보낼 수 없는 종류입니다({u.Kind}).");
            var c = new SessionChange
            {
                SessionId = u.SessionId, Revision = u.Revision, PlayerId = u.PlayerId, RequestId = u.RequestId, Kind = kind,
                From = u.From, To = u.To, DoorOpened = u.OpenedDoor.HasValue, TeamFirstVisit = u.TeamFirstVisit, Moves = u.Moves,
                Returns = u.Returns, Status = u.Status, Result = u.Result,
            };
            foreach (var r in u.Added) c.Added.Add(new KeyValuePair<GridPos, int>(r.Position, r.Word));
            return c;
        }

        /// <summary>A result received from the host (the coordinator's own is made only inside the goal entry).</summary>
        public static SessionResult MakeResult(long revision, string reachedBy, double seconds, int teamVisitedRooms, IEnumerable<PlayerResult> players) =>
            new SessionResult(revision, reachedBy, seconds, teamVisitedRooms, players.ToArray());
    }
}
