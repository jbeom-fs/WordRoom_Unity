using System;
using UnityEngine;

namespace WordRoom.Shelf
{
    /// <summary>
    /// Personal bests of the shelf prototype, kept locally in PlayerPrefs (like the game settings). The reading-room
    /// version keys a challenge by start headword and goal headword under its own version mark (ReadingKey, e.g.
    /// "lectern1.바다>지구"), so the former corridor records ("바다(331764)>지구(*)", ChallengeKey) stay untouched and are
    /// no longer written. The shortest time and the fewest moves are compared and kept separately (a run may improve
    /// one, both or none). Nothing is stored for a challenge until it is finished once.
    /// </summary>
    public static class ShelfRecords
    {
        const string Prefix = "WordRoom.Shelf.Best.";
        const string AnySense = "*";
        /// <summary>Version mark of the reading-room records (start and goal are headwords).</summary>
        public const string ReadingVersion = "lectern1";

        public readonly struct Best
        {
            public readonly bool HasTime;
            public readonly double Seconds;
            public readonly bool HasMoves;
            public readonly int Moves;

            public Best(bool hasTime, double seconds, bool hasMoves, int moves)
            {
                HasTime = hasTime;
                Seconds = seconds;
                HasMoves = hasMoves;
                Moves = moves;
            }
        }

        /// <summary>"lectern1.start>goal": the reading-room challenge part of the PlayerPrefs keys.</summary>
        public static string ReadingKey(string startWord, string goalWord) => $"{ReadingVersion}.{startWord}>{goalWord}";

        /// <summary>Former corridor challenge "start(SenseId)>goal(*)" (records kept as they are, no longer written).</summary>
        public static string ChallengeKey(string startWord, string startSense, string goalWord) =>
            $"{startWord}({startSense})>{goalWord}({AnySense})";

        public static Best Load(string challenge)
        {
            var time = PlayerPrefs.GetFloat(Prefix + challenge + ".time", -1f);
            var moves = PlayerPrefs.GetInt(Prefix + challenge + ".moves", -1);
            return new Best(time >= 0f, Math.Max(0f, time), moves >= 0, Math.Max(0, moves));
        }

        /// <summary>
        /// Compares one finished run with the saved bests and saves each one it beats (time and moves separately).
        /// Returns the bests after this run; `newTime` / `newMoves` tell which were set by it (also a first record).
        /// </summary>
        public static Best Submit(string challenge, double seconds, int moves, out bool newTime, out bool newMoves)
        {
            var before = Load(challenge);
            newTime = !before.HasTime || seconds < before.Seconds;
            newMoves = !before.HasMoves || moves < before.Moves;
            if (newTime) PlayerPrefs.SetFloat(Prefix + challenge + ".time", (float)seconds);
            if (newMoves) PlayerPrefs.SetInt(Prefix + challenge + ".moves", moves);
            if (newTime || newMoves) PlayerPrefs.Save();
            return new Best(true, newTime ? seconds : before.Seconds, true, newMoves ? moves : before.Moves);
        }
    }
}
