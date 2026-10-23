using System;
using UnityEngine;
using WordRoom.Data;

namespace WordRoom.Game
{
    public enum Difficulty
    {
        Easy,
        Normal,
        Hard,
    }

    /// <summary>
    /// The start request handed from the main menu to the Gameplay scene: only the chosen difficulty. The words and
    /// the seed are drawn in Gameplay, once per game, after the data is ready. Consumed exactly once; a scene opened
    /// without a request (Editor, direct play) falls back to Normal and says so. Not a preference: never stored.
    /// </summary>
    public static class GameLaunch
    {
        public const string MainMenuScene = "MainMenu";
        public const string GameplayScene = "Gameplay";
        public const int MinWordLength = 1;
        public const int MaxWordLength = 8;

        static Difficulty? pending;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => pending = null;

        /// <summary>Top-frequency surfaces in play: 쉬움 5,000 · 보통 10,000 · 어려움 15,000 (the whole package).</summary>
        public static int VocabularyLimit(Difficulty d)
        {
            switch (d)
            {
                case Difficulty.Easy: return 5000;
                case Difficulty.Hard: return 15000;
                default: return 10000;
            }
        }

        public static string Label(Difficulty d)
        {
            switch (d)
            {
                case Difficulty.Easy: return "쉬움";
                case Difficulty.Hard: return "어려움";
                default: return "보통";
            }
        }

        public static string Description(Difficulty d) => $"자주 쓰는 상위 {VocabularyLimit(d):N0}개 단어 중에서 시작과 목표를 정합니다";

        public static void Request(Difficulty d) => pending = d;

        /// <summary>The pending request, removed so it is applied only once.</summary>
        public static bool TryConsume(out Difficulty d)
        {
            d = pending ?? Difficulty.Normal;
            var had = pending.HasValue;
            pending = null;
            return had;
        }

        /// <summary>
        /// Draws two different start and target words from the difficulty's range (top-`limit` surfaces of 1–8
        /// characters, the dataset's own scope) and a fresh generation seed. False with the reason when the range
        /// has fewer than two words; never substitutes words from outside the range.
        /// </summary>
        public static bool TryPickWords(WordRoomDataset data, Difficulty d, out int start, out int target, out long seed, out string error)
        {
            start = target = -1;
            seed = 0;
            VocabularyScope scope;
            try
            {
                scope = data.CreateScope(VocabularyLimit(d), MinWordLength, MaxWordLength);
            }
            catch (Exception e)
            {
                error = $"{Label(d)} 난이도의 단어 범위를 만들 수 없습니다 — {e.Message}";
                return false;
            }
            var ranks = scope.Ranks;
            if (ranks.Count < 2)
            {
                error = $"{Label(d)} 난이도 범위({scope})에 서로 다른 단어가 2개 이상 없습니다";
                return false;
            }
            var rng = new System.Random(Guid.NewGuid().GetHashCode());
            var i = rng.Next(ranks.Count);
            var j = rng.Next(ranks.Count - 1);
            if (j >= i) j++;                                   // a different word, no retry loop
            start = ranks[i];
            target = ranks[j];
            seed = rng.Next(1, int.MaxValue);
            error = null;
            return true;
        }
    }
}
