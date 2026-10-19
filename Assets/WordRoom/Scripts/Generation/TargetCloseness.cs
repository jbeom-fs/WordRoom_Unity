using System;
using System.Threading;
using WordRoom.Data;

namespace WordRoom.Generation
{
    /// <summary>
    /// Engine-local arithmetic that keeps Python's data types. Python multiplies float32 vectors with BLAS and keeps
    /// the float32 result (v[i]@v[j], ft@ft[w]); here the dot product is summed in float64 and rounded once to float32.
    /// That is the correctly rounded float32 value; BLAS may differ from it in the last bit (about 1e-7), which can
    /// only change an order when two values are that close. WordRoomDataset's own float64 similarity is not changed.
    /// </summary>
    internal static class EngineMath
    {
        public static float Dot32(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        {
            var s = 0.0;
            for (var i = 0; i < a.Length; i++) s += (double)a[i] * b[i];
            return (float)s;
        }

        public static float Clip01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
    }

    /// <summary>
    /// kw_semantic_engine.target_closeness: per surface, the E5 percentile (best sense pair with the target) and
    /// the fastText percentile (surface cosine; left out for restricted surfaces or a target without a usable
    /// vector, neutral 0.5 for such words), averaged; percentiles only over the allowed surfaces; target = 1.
    /// A selection weight, not a relation judgment.
    /// </summary>
    internal static class TargetCloseness
    {
        public static double[] Compute(WordRoomDataset d, int target, bool[] restricted, bool[] allowed, CancellationToken cancel)
        {
            var n = d.WordCount;
            var targetSenses = d.GetSenseRows(target);
            // sims = v @ v[target senses].T; best = max over target senses (first maximum, as numpy argmax).
            var e5 = new double[n];
            for (var w = 0; w < n; w++) e5[w] = -1.0;
            var senses = d.SenseCount;
            for (var i = 0; i < senses; i++)
            {
                if ((i & 1023) == 0) cancel.ThrowIfCancellationRequested();
                var vi = d.GetE5Vector(i);
                var best = float.NegativeInfinity;
                for (var k = 0; k < targetSenses.Length; k++)
                {
                    var s = EngineMath.Dot32(vi, d.GetE5Vector(targetSenses[k]));
                    if (s > best) best = s;
                }
                var w = d.WordOfSense(i);
                if (best > e5[w]) e5[w] = best;
            }
            var ft = new double[n];
            for (var w = 0; w < n; w++) ft[w] = double.NaN;
            if (d.HasFastText(target) && !restricted[target])
            {
                var tv = d.GetFastTextVector(target);
                for (var w = 0; w < n; w++)
                    if (d.HasFastText(w) && !restricted[w]) ft[w] = EngineMath.Dot32(d.GetFastTextVector(w), tv);
            }
            var e5Pct = Percentile(e5, w => allowed[w]);
            var ftPct = Percentile(ft, w => !double.IsNaN(ft[w]) && allowed[w]);
            var ftAllNaN = true;
            foreach (var v in ftPct)
                if (!double.IsNaN(v))
                {
                    ftAllNaN = false;
                    break;
                }
            var close = new double[n];
            for (var w = 0; w < n; w++)
            {
                if (!allowed[w])
                {
                    close[w] = 0.0;
                    continue;
                }
                close[w] = ftAllNaN ? e5Pct[w] : (e5Pct[w] + (double.IsNaN(ftPct[w]) ? 0.5 : ftPct[w])) / 2;
            }
            close[target] = 1.0;
            return close;
        }

        /// <summary>Rank of each valid value among the valid values, 0..1 (stable: ties keep index order); NaN elsewhere.</summary>
        static double[] Percentile(double[] values, Func<int, bool> valid)
        {
            var output = new double[values.Length];
            var idx = new System.Collections.Generic.List<int>();
            for (var w = 0; w < values.Length; w++)
            {
                output[w] = double.NaN;
                if (valid(w)) idx.Add(w);
            }
            if (idx.Count > 1)
            {
                var order = idx.ToArray();
                Array.Sort(order, (a, b) =>
                {
                    var c = values[a].CompareTo(values[b]);
                    return c != 0 ? c : a.CompareTo(b);
                });
                for (var r = 0; r < order.Length; r++) output[order[r]] = (double)r / (order.Length - 1);
            }
            return output;
        }
    }
}
