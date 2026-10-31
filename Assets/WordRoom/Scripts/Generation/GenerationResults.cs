using System.Collections.Generic;

namespace WordRoom.Generation
{
    /// <summary>Evidence of one connection between a new room and an existing neighbor.</summary>
    public sealed class LinkEvidence
    {
        public GridPos Room;
        public GridPos Neighbor;
        public int NeighborWord;
        /// <summary>"relation_db" (stored YES, B rule) or "fasttext" (surface vector cosine, not a sense judgment).</summary>
        public string Source;
        public string SenseId;
        public string NeighborSenseId;
        public double YesScore = double.NaN;
        public double Threshold = double.NaN;
        public bool Relaxed;
        public double Similarity = double.NaN;
        /// <summary>fastText links: relation-DB state of the word pair at the floor threshold (never written back).</summary>
        public string DbState;
        public bool Weak;
        public bool Restricted;
        public bool LastResort;
        public string LastResortReason;
        public string VectorVersion;

        /// <summary>Independent copy (value fields only).</summary>
        public LinkEvidence Copy() => (LinkEvidence)MemberwiseClone();
    }

    /// <summary>One room committed by a fill.</summary>
    public sealed class PlacedRoom
    {
        public GridPos Position;
        public int Word;
        /// <summary>"relation_db", "fasttext" or "relation_db_partial".</summary>
        public string Stage;
        /// <summary>Relation threshold of a relation-DB plan; NaN for a supplement.</summary>
        public double Threshold = double.NaN;
        public bool Relaxed;
        public bool FastText;
        public int Candidates;
        public double Score;
        public double GuidanceStrength;
        public double GuidanceBonus;
        public readonly List<LinkEvidence> Links = new List<LinkEvidence>();

        /// <summary>Deep copy: changing it (or its links) never reaches the room stored in the shared world.</summary>
        public PlacedRoom Copy()
        {
            var c = new PlacedRoom
            {
                Position = Position, Word = Word, Stage = Stage, Threshold = Threshold, Relaxed = Relaxed, FastText = FastText,
                Candidates = Candidates, Score = Score, GuidanceStrength = GuidanceStrength, GuidanceBonus = GuidanceBonus,
            };
            foreach (var l in Links) c.Links.Add(l.Copy());
            return c;
        }
    }

    public sealed class PositionReport
    {
        public GridPos Position;
        public List<int> Neighbors = new List<int>();
        public int Candidates;
        public int RepeatBanned;
        public int PartialCandidates;
        public readonly List<KeyValuePair<string, int>> BlockedBy = new List<KeyValuePair<string, int>>();

        /// <summary>Deep copy (the lists are new; their elements are values or strings).</summary>
        public PositionReport Copy()
        {
            var c = new PositionReport
            {
                Position = Position, Neighbors = Neighbors != null ? new List<int>(Neighbors) : null, Candidates = Candidates,
                RepeatBanned = RepeatBanned, PartialCandidates = PartialCandidates,
            };
            c.BlockedBy.AddRange(BlockedBy);
            return c;
        }
    }

    /// <summary>One threshold of the relation-DB ladder.</summary>
    public sealed class ThresholdStep
    {
        public double Threshold;
        public readonly List<KeyValuePair<GridPos, int>> CandidateCounts = new List<KeyValuePair<GridPos, int>>();
        public int Nodes;
        /// <summary>"no_candidate", "placed", "budget_exhausted" (search limit) or "placement_conflict".</summary>
        public string Result;
        public readonly List<PositionReport> Positions = new List<PositionReport>();

        public ThresholdStep Copy()
        {
            var c = new ThresholdStep { Threshold = Threshold, Nodes = Nodes, Result = Result };
            c.CandidateCounts.AddRange(CandidateCounts);
            foreach (var p in Positions) c.Positions.Add(p.Copy());
            return c;
        }
    }

    public sealed class EmptyPosition
    {
        public GridPos Position;
        public string Reason;

        public EmptyPosition Copy() => new EmptyPosition { Position = Position, Reason = Reason };
    }

    /// <summary>Result of filling the empty neighbors of one room (Simulation.fill).</summary>
    public sealed class FillReport
    {
        public GridPos Center;
        /// <summary>"none" (nothing missing), "skipped_same_state", "relation_db", "fasttext", "relation_db_partial".</summary>
        public string Stage = "none";
        public bool Complete;
        public double Threshold = double.NaN;
        public readonly List<ThresholdStep> Steps = new List<ThresholdStep>();
        public string DbMessage;
        public readonly List<string> SupplementAttempts = new List<string>();
        public bool SupplementBudgetHit;
        public readonly List<PlacedRoom> Added = new List<PlacedRoom>();
        public readonly List<EmptyPosition> Empty = new List<EmptyPosition>();
        public double Seconds;

        /// <summary>Deep copy: steps with their position reports, added rooms with their links, empty positions.</summary>
        public FillReport Copy()
        {
            var c = new FillReport
            {
                Center = Center, Stage = Stage, Complete = Complete, Threshold = Threshold, DbMessage = DbMessage,
                SupplementBudgetHit = SupplementBudgetHit, Seconds = Seconds,
            };
            foreach (var s in Steps) c.Steps.Add(s.Copy());
            c.SupplementAttempts.AddRange(SupplementAttempts);
            foreach (var r in Added) c.Added.Add(r.Copy());
            foreach (var e in Empty) c.Empty.Add(e.Copy());
            return c;
        }
    }

    /// <summary>
    /// One confirmed change of a session, made by SessionCoordinator for an accepted request (never for a refusal).
    /// Self-contained data: the rooms in Fill are copies, so nothing here reaches the shared world. Revision is the
    /// session's confirmed change number after this change (start = 1, one more per accepted request); a later
    /// display applies changes in revision order and recovers a gap from a full SessionCopy of one revision.
    /// FillReport is the local detailed report (logs); it is not meant to be sent to other players as is.
    /// Every GenerationUpdate handed out by SessionCoordinator is a fresh deep copy of the confirmed one it keeps for
    /// duplicate requests: changing a received update changes neither that record nor the shared world.
    /// </summary>
    public sealed class GenerationUpdate
    {
        /// <summary>
        /// "start", "move" (enter + fill), "prepare" (fill around an adjacent room, no move; Moved false),
        /// "enter" (move into a prepared room, no fill; Fill null) or "return" (끼임방지: the requester's position back to
        /// the start room; not a move or visit — Moved false, Fill null, never the goal).
        /// </summary>
        public string Kind;
        public string SessionId;
        /// <summary>Requesting player; null for the start (every registered player begins in the start room).</summary>
        public string PlayerId;
        public long RequestId;
        public long Revision;
        public GridPos From;
        public GridPos To;
        public bool Moved;
        /// <summary>The requesting player entered To for the first time (personal record).</summary>
        public bool FirstVisit;
        /// <summary>Nobody of the team had entered To before (shared visit set; the start room counts once).</summary>
        public bool TeamFirstVisit;
        /// <summary>The requesting player's moves after this change.</summary>
        public int Moves;
        /// <summary>The requesting player's returns to the start room (끼임방지) after this change.</summary>
        public int Returns;
        public string Status;
        public bool Arrived;
        public double GuidanceStrength;
        /// <summary>Connection this request stored Open (a door opened by this Prepare); null when no door changed.</summary>
        public RoomConnection? OpenedDoor;
        /// <summary>The session result confirmed by this change (the goal entry); null otherwise. Confirmed once per session.</summary>
        public SessionResult Result;
        public FillReport Fill;
        /// <summary>Rooms added by this request (same copies as Fill.Added).</summary>
        public IReadOnlyList<PlacedRoom> Added => Fill != null ? (IReadOnlyList<PlacedRoom>)Fill.Added : new List<PlacedRoom>();

        /// <summary>Deep copy (Fill included; the immutable Result and the value fields are shared as they are).</summary>
        public GenerationUpdate Copy()
        {
            var c = (GenerationUpdate)MemberwiseClone();
            c.Fill = Fill?.Copy();
            return c;
        }
    }

    public static class StatusText
    {
        public static string Korean(string status)
        {
            switch (status)
            {
                case "running": return "탐색 중";
                case "success": return "목표 도달";
                case "move_limit": return "테스트 이동 제한 도달";
                case "no_exit": return "생성된 인접 방이 없어 이동 불가";
                default: return status;
            }
        }

        /// <summary>STATE_LABEL of kw_semantic_engine.</summary>
        public static string State(string state)
        {
            switch (state)
            {
                case "unchecked": return "미검사";
                case "below": return "저장된 YES 점수가 기준 미만";
                case "error": return "판정 오류·불완전";
                case "rejected": return "NO 판정";
                case "approved": return "승인";
                default: return state;
            }
        }
    }
}
