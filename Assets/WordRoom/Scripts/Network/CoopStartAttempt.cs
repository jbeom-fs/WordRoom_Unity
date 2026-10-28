using System;
using System.Collections.Generic;
using System.Linq;

namespace WordRoom.Network
{
    public enum ReportResult
    {
        Accepted,
        /// <summary>The same participant reported before: not counted again.</summary>
        Duplicate,
        /// <summary>The sender is not one of the participants fixed for this start.</summary>
        NotMember,
        /// <summary>The report does not belong to the current state of this start (other session, revision or id).</summary>
        Invalid,
    }

    /// <summary>
    /// The host's record of one start attempt (no Photon type; testable alone): the id, the host, the participants
    /// fixed when the start was confirmed (ActorNumbers, nobody added later), the difficulty and settings revision,
    /// then each participant's data report and placement report — counted once per real sender, with the sender's
    /// PlayerId, SessionId and initial revision checked. Any failure or package mismatch is kept as the reason to stop.
    /// </summary>
    public sealed class CoopStartAttempt
    {
        readonly int[] actors;
        readonly Dictionary<int, string> packages = new Dictionary<int, string>();
        readonly HashSet<int> placed = new HashSet<int>();

        public string Id { get; }
        public int Host { get; }
        public IReadOnlyList<int> Actors => actors;
        public int Difficulty { get; }
        public int SettingsRevision { get; }

        /// <summary>Set once the host sent the initial world (the reports of placement must match it).</summary>
        public string SessionId { get; private set; }
        public long Revision { get; private set; }

        /// <summary>Why this start cannot go on (first failure seen); null while it can.</summary>
        public string Failure { get; private set; }

        public CoopStartAttempt(string id, int host, IEnumerable<int> participants, int difficulty, int settingsRevision)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("시작 ID가 비어 있습니다.");
            actors = (participants ?? Enumerable.Empty<int>()).Distinct().OrderBy(a => a).ToArray();
            if (actors.Length < CoopRules.MinPlayers || actors.Length > CoopRules.MaxPlayers) throw new ArgumentException($"참가자 수가 올바르지 않습니다({actors.Length}).");
            if (!actors.Contains(host)) throw new ArgumentException("호스트가 참가자에 없습니다.");
            if (!CoopRules.IsValidDifficulty(difficulty)) throw new ArgumentException($"난이도가 올바르지 않습니다({difficulty}).");
            Id = id;
            Host = host;
            Difficulty = difficulty;
            SettingsRevision = settingsRevision;
        }

        public bool IsMember(int actor) => Array.IndexOf(actors, actor) >= 0;

        public int DataCount => packages.Count;
        public int PlacedCount => placed.Count;

        /// <summary>A participant's data report (`sender` is the real Photon sender). A failed load is a failure, never "ready".</summary>
        public ReportResult RecordData(int sender, bool ok, string packageId, string error)
        {
            if (!IsMember(sender)) return ReportResult.NotMember;
            if (packages.ContainsKey(sender)) return ReportResult.Duplicate;
            if (!ok || string.IsNullOrEmpty(packageId))
            {
                packages[sender] = null;
                Fail($"{CoopRules.DefaultParticipantName(sender)}의 데이터를 준비하지 못했습니다{(string.IsNullOrEmpty(error) ? "" : $" — {error}")}");
                return ReportResult.Accepted;
            }
            packages[sender] = packageId;
            var first = packages.Values.FirstOrDefault(p => p != null && p != packageId);
            if (first != null) Fail("참가자들의 단어 데이터 버전이 서로 달라 시작할 수 없습니다");
            return ReportResult.Accepted;
        }

        /// <summary>Every participant reported loaded data of one and the same package (compared by PackageId only).</summary>
        public bool TryAgreedPackage(out string packageId)
        {
            packageId = null;
            if (Failure != null || packages.Count != actors.Length) return false;
            var ids = packages.Values.Distinct().ToList();
            if (ids.Count != 1 || ids[0] == null) return false;
            packageId = ids[0];
            return true;
        }

        public void SetWorld(string sessionId, long revision)
        {
            if (SessionId != null) throw new InvalidOperationException("초기 월드는 한 번만 정합니다.");
            if (string.IsNullOrEmpty(sessionId) || revision < 1) throw new ArgumentException("초기 월드 정보가 올바르지 않습니다.");
            SessionId = sessionId;
            Revision = revision;
        }

        /// <summary>
        /// A participant's placement report. Counted only for the real sender, its own PlayerId, this start's SessionId
        /// and initial revision; a failed display or placement is a failure.
        /// </summary>
        public ReportResult RecordPlaced(int sender, string playerId, string sessionId, long revision, bool ok, string error)
        {
            if (!IsMember(sender)) return ReportResult.NotMember;
            if (placed.Contains(sender)) return ReportResult.Duplicate;
            if (!ok)                                               // a failure counts even when it could not read the state (no SessionId to match)
            {
                Fail($"{CoopRules.DefaultParticipantName(sender)}의 방 표시·배치에 실패했습니다{(string.IsNullOrEmpty(error) ? "" : $" — {error}")}");
                return ReportResult.Accepted;
            }
            if (SessionId == null || sessionId != SessionId || revision != Revision || playerId != CoopRules.PlayerIdOf(sender)) return ReportResult.Invalid;
            placed.Add(sender);
            return ReportResult.Accepted;
        }

        public bool AllPlaced => Failure == null && SessionId != null && placed.Count == actors.Length;

        public void Fail(string reason)
        {
            if (Failure == null) Failure = reason;
        }
    }
}
