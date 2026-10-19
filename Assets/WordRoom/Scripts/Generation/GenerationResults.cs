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
    }

    public sealed class PositionReport
    {
        public GridPos Position;
        public List<int> Neighbors = new List<int>();
        public int Candidates;
        public int RepeatBanned;
        public int PartialCandidates;
        public readonly List<KeyValuePair<string, int>> BlockedBy = new List<KeyValuePair<string, int>>();
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
    }

    public sealed class EmptyPosition
    {
        public GridPos Position;
        public string Reason;
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
    }

    /// <summary>
    /// What one engine request changed. Self-contained data, so a host can apply the same change elsewhere
    /// (e.g. send it to other players) without re-running the generator.
    /// </summary>
    public sealed class GenerationUpdate
    {
        /// <summary>"start" or "move".</summary>
        public string Kind;
        public GridPos From;
        public GridPos To;
        public bool Moved;
        public bool FirstVisit;
        public int Moves;
        public string Status;
        public bool Arrived;
        public double GuidanceStrength;
        public FillReport Fill;
        /// <summary>Rooms added by this request (same objects as Fill.Added).</summary>
        public IReadOnlyList<PlacedRoom> Added => Fill != null ? (IReadOnlyList<PlacedRoom>)Fill.Added : new List<PlacedRoom>();
    }

    public static class StatusText
    {
        public static string Korean(string status)
        {
            switch (status)
            {
                case "running": return "탐색 중";
                case "success": return "목표 도달";
                case "move_limit": return "이동 한도 도달";
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
