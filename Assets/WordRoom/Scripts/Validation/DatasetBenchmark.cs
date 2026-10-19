using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace WordRoom.Data.Validation
{
    /// <summary>Query timings on a loaded dataset (random words, fixed seed). Measurement only; results are discarded.</summary>
    public sealed class BenchmarkResult
    {
        public readonly List<KeyValuePair<string, double>> MicrosecondsPerCall = new List<KeyValuePair<string, double>>();
        public readonly List<KeyValuePair<string, int>> Calls = new List<KeyValuePair<string, int>>();

        public override string ToString()
        {
            var sb = new StringBuilder("조회 시간(평균, µs/회): ");
            for (var i = 0; i < MicrosecondsPerCall.Count; i++)
                sb.Append($"{MicrosecondsPerCall[i].Key} {MicrosecondsPerCall[i].Value:F2} ({Calls[i].Value:N0}회){(i + 1 < MicrosecondsPerCall.Count ? " · " : "")}");
            return sb.ToString();
        }
    }

    public static class DatasetBenchmark
    {
        public static BenchmarkResult Run(WordRoomDataset d, int words = 2000, int seed = 12345)
        {
            var rng = new Random(seed);
            var result = new BenchmarkResult();
            var picks = new int[words];
            for (var i = 0; i < words; i++) picks[i] = rng.Next(d.WordCount);
            var rel = d.Relations;
            var ladder = d.ThresholdLadder;
            var scope = d.CreateScope(Math.Min(10000, d.Identity.vocabularyLimit), d.Identity.minLength, d.Identity.maxLength);
            var buffer = new List<int>(256);
            long sink = 0;

            void Measure(string name, int calls, Action body)
            {
                body(); // warm-up
                var w = Stopwatch.StartNew();
                body();
                result.MicrosecondsPerCall.Add(new KeyValuePair<string, double>(name, w.Elapsed.TotalMilliseconds * 1000.0 / calls));
                result.Calls.Add(new KeyValuePair<string, int>(name, calls));
            }

            Measure("승인 이웃(전체 어휘, 기준 8단계)", words * ladder.Count, () =>
            {
                foreach (var w in picks)
                    foreach (var t in ladder)
                    {
                        buffer.Clear();
                        rel.GetApprovedNeighbors(w, t, buffer);
                        sink += buffer.Count;
                    }
            });
            Measure("승인 이웃(10,000 범위, 기준 8단계)", words * ladder.Count, () =>
            {
                foreach (var w in picks)
                    foreach (var t in ladder)
                    {
                        buffer.Clear();
                        rel.GetApprovedNeighbors(w, t, buffer, scope);
                        sink += buffer.Count;
                    }
            });
            Measure("단어 쌍 상태", words, () =>
            {
                for (var i = 0; i < words; i++) sink += (int)rel.GetWordPairState(picks[i], picks[(i + 1) % words], ladder[0]);
            });
            Measure("최고 근거(승인 이웃 첫 쌍)", words, () =>
            {
                foreach (var w in picks)
                {
                    buffer.Clear();
                    rel.GetApprovedNeighbors(w, ladder[ladder.Count - 1], buffer);
                    if (buffer.Count > 0 && rel.TryGetBestEvidence(w, buffer[0], ladder[ladder.Count - 1], out var e)) sink += e.SenseRow;
                }
            });
            Measure("fastText 유사도", words, () =>
            {
                for (var i = 0; i < words; i++)
                    if (d.TryFastTextSimilarity(picks[i], picks[(i + 1) % words], out var v)) sink += (long)(v * 1000);
            });
            Measure("E5 뜻 유사도", words, () =>
            {
                for (var i = 0; i < words; i++)
                    sink += (long)(d.E5Similarity(d.SenseRowsOf(picks[i])[0], d.SenseRowsOf(picks[(i + 1) % words])[0]) * 1000);
            });
            Measure("표기 조회(문자열→순위)", words, () =>
            {
                foreach (var w in picks)
                    if (d.TryGetWordRank(d.GetWord(w), out var r)) sink += r;
            });
            if (sink == long.MinValue) Console.WriteLine(sink);
            return result;
        }
    }
}
