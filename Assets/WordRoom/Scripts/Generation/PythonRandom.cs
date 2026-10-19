using System;
using System.Globalization;
using System.Text;

namespace WordRoom.Generation
{
    /// <summary>
    /// Ports of the random sources the Python generator uses, so draws can be compared with Python:
    ///   Simulation.coordinate_rng(pos) = np.random.default_rng(int.from_bytes(blake2b(f'generator:{seed}:{x}:{y}',
    ///   digest_size=16), 'little')), i.e. SeedSequence → PCG64 → Generator; the generator only ever takes the first
    ///   draw of a fresh generator per call (Generator.choice(a, p=weights)).
    /// The player RNG (random.Random(f'player:{seed}')) belongs to the automatic player and is not ported.
    /// Bit-exactness is asserted by the EditMode tests against published NumPy/BLAKE2 values; compare with Python
    /// using Docs/WordRoom_방생성이식_2단계.md before relying on it.
    /// </summary>
    public static class PythonRandom
    {
        /// <summary>First uniform double [0, 1) of the coordinate generator of `seed` at `pos`.</summary>
        public static double CoordinateUniform(long seed, GridPos pos)
        {
            var text = "generator:" + seed.ToString(CultureInfo.InvariantCulture) + ":" + pos.X.ToString(CultureInfo.InvariantCulture) + ":" +
                       pos.Y.ToString(CultureInfo.InvariantCulture);
            var digest = Blake2b.Hash(Encoding.UTF8.GetBytes(text), 16);
            var rng = Pcg64.FromSeedSequence(SeedSequence.FromLittleEndianInteger(digest));
            return rng.NextDouble();
        }

        /// <summary>
        /// numpy Generator.choice(len(p) or a, p=p) with replace=True and size=None: cdf = p.cumsum(); cdf /= cdf[-1];
        /// index = cdf.searchsorted(u, side='right').
        /// </summary>
        public static int Choice(double[] p, double u)
        {
            var cdf = new double[p.Length];
            var sum = 0.0;
            for (var i = 0; i < p.Length; i++)
            {
                sum += p[i];
                cdf[i] = sum;
            }
            var last = cdf[cdf.Length - 1];
            for (var i = 0; i < cdf.Length; i++) cdf[i] /= last;
            var index = 0;
            while (index < cdf.Length && cdf[index] <= u) index++;
            return Math.Min(index, p.Length - 1);
        }
    }

    /// <summary>numpy.random.SeedSequence (pool size 4, no spawn key): entropy mixing and generate_state.</summary>
    public sealed class SeedSequence
    {
        const uint InitA = 0x43b0d7e5, MultA = 0x931e8875, InitB = 0x8b51f9dd, MultB = 0x58f38ded;
        const uint MixMultL = 0xca01f9dd, MixMultR = 0x4973f715;
        const int XShift = 16;
        const int PoolSize = 4;
        readonly uint[] pool = new uint[PoolSize];

        public SeedSequence(uint[] entropy)
        {
            var hashConst = InitA;
            uint HashMix(uint value)
            {
                unchecked
                {
                    value ^= hashConst;
                    hashConst *= MultA;
                    value *= hashConst;
                    value ^= value >> XShift;
                    return value;
                }
            }
            uint Mix(uint x, uint y)
            {
                unchecked
                {
                    var result = MixMultL * x - MixMultR * y;
                    result ^= result >> XShift;
                    return result;
                }
            }
            for (var i = 0; i < PoolSize; i++) pool[i] = HashMix(i < entropy.Length ? entropy[i] : 0u);
            for (var src = 0; src < PoolSize; src++)
                for (var dst = 0; dst < PoolSize; dst++)
                    if (src != dst) pool[dst] = Mix(pool[dst], HashMix(pool[src]));
            for (var src = PoolSize; src < entropy.Length; src++)
                for (var dst = 0; dst < PoolSize; dst++)
                    pool[dst] = Mix(pool[dst], HashMix(entropy[src]));
        }

        /// <summary>Python int → uint32 words, least significant first (numpy _int_to_uint32_array).</summary>
        public static SeedSequence FromLittleEndianInteger(byte[] littleEndian)
        {
            var words = new uint[(littleEndian.Length + 3) / 4];
            for (var i = 0; i < littleEndian.Length; i++) words[i / 4] |= (uint)littleEndian[i] << (8 * (i % 4));
            var n = words.Length;
            while (n > 1 && words[n - 1] == 0) n--;
            if (n == 0) return new SeedSequence(new uint[] { 0 });
            var trimmed = new uint[n];
            Array.Copy(words, trimmed, n);
            return new SeedSequence(trimmed);
        }

        public static SeedSequence FromInt(ulong value)
        {
            if (value == 0) return new SeedSequence(new uint[] { 0 });
            return value <= uint.MaxValue ? new SeedSequence(new[] { (uint)value }) : new SeedSequence(new[] { (uint)value, (uint)(value >> 32) });
        }

        /// <summary>generate_state(nWords, uint64): 2*nWords uint32 values viewed as little-endian uint64.</summary>
        public ulong[] GenerateState64(int nWords)
        {
            var state = new uint[nWords * 2];
            var hashConst = InitB;
            for (var i = 0; i < state.Length; i++)
            {
                unchecked
                {
                    var v = pool[i % PoolSize];
                    v ^= hashConst;
                    hashConst *= MultB;
                    v *= hashConst;
                    v ^= v >> XShift;
                    state[i] = v;
                }
            }
            var result = new ulong[nWords];
            for (var i = 0; i < nWords; i++) result[i] = state[2 * i] | ((ulong)state[2 * i + 1] << 32);
            return result;
        }
    }

    /// <summary>numpy PCG64 (pcg_setseq_128_xsl_rr_64): step, then output of the new state.</summary>
    public sealed class Pcg64
    {
        const ulong MulHi = 2549297995355413924UL, MulLo = 4865540595714422341UL;
        ulong stateHi, stateLo;
        readonly ulong incHi, incLo;

        Pcg64(ulong seedHi, ulong seedLo, ulong seqHi, ulong seqLo)
        {
            stateHi = 0;
            stateLo = 0;
            incHi = (seqHi << 1) | (seqLo >> 63);
            incLo = (seqLo << 1) | 1UL;
            Step();
            Add(ref stateHi, ref stateLo, seedHi, seedLo);
            Step();
        }

        /// <summary>PCG64(SeedSequence): state from generate_state(4, uint64) = [seed hi, seed lo, inc hi, inc lo].</summary>
        public static Pcg64 FromSeedSequence(SeedSequence seq)
        {
            var v = seq.GenerateState64(4);
            return new Pcg64(v[0], v[1], v[2], v[3]);
        }

        static void Add(ref ulong hi, ref ulong lo, ulong bHi, ulong bLo)
        {
            unchecked
            {
                var newLo = lo + bLo;
                hi = hi + bHi + (newLo < lo ? 1UL : 0UL);
                lo = newLo;
            }
        }

        static void Mul64(ulong a, ulong b, out ulong hi, out ulong lo)
        {
            unchecked
            {
                ulong aL = (uint)a, aH = a >> 32, bL = (uint)b, bH = b >> 32;
                var ll = aL * bL;
                var lh = aL * bH;
                var hl = aH * bL;
                var hh = aH * bH;
                var mid = (ll >> 32) + (uint)lh + (uint)hl;
                lo = (mid << 32) | (uint)ll;
                hi = hh + (lh >> 32) + (hl >> 32) + (mid >> 32);
            }
        }

        void Step()
        {
            unchecked
            {
                Mul64(stateLo, MulLo, out var hi, out var lo);
                hi += stateHi * MulLo + stateLo * MulHi;
                stateHi = hi;
                stateLo = lo;
                Add(ref stateHi, ref stateLo, incHi, incLo);
            }
        }

        public ulong NextUInt64()
        {
            Step();
            var x = stateHi ^ stateLo;
            var rot = (int)(stateHi >> 58);
            return (x >> rot) | (x << ((64 - rot) & 63));
        }

        /// <summary>numpy next_double: (next_uint64 &gt;&gt; 11) * 2^-53.</summary>
        public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);
    }

    /// <summary>BLAKE2b (RFC 7693), unkeyed, digest length 1..64 (hashlib.blake2b(data, digest_size=n)).</summary>
    public static class Blake2b
    {
        static readonly ulong[] IV =
        {
            0x6a09e667f3bcc908UL, 0xbb67ae8584caa73bUL, 0x3c6ef372fe94f82bUL, 0xa54ff53a5f1d36f1UL,
            0x510e527fade682d1UL, 0x9b05688c2b3e6c1fUL, 0x1f83d9abfb41bd6bUL, 0x5be0cd19137e2179UL,
        };

        static readonly byte[,] Sigma =
        {
            { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
            { 14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3 },
            { 11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4 },
            { 7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8 },
            { 9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13 },
            { 2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9 },
            { 12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11 },
            { 13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10 },
            { 6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5 },
            { 10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0 },
        };

        public static byte[] Hash(byte[] data, int outLength)
        {
            if (outLength < 1 || outLength > 64) throw new ArgumentOutOfRangeException(nameof(outLength));
            var h = (ulong[])IV.Clone();
            h[0] ^= 0x01010000UL ^ (ulong)outLength;
            var m = new ulong[16];
            ulong counter = 0;
            var offset = 0;
            var block = new byte[128];
            do
            {
                var n = Math.Min(128, data.Length - offset);
                var last = offset + n >= data.Length;
                Array.Clear(block, 0, 128);
                Array.Copy(data, offset, block, 0, n);
                counter += (ulong)n;
                for (var i = 0; i < 16; i++) m[i] = BitConverter.ToUInt64(block, i * 8);
                if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("little-endian only");
                Compress(h, m, counter, last);
                offset += n;
                if (last) break;
            } while (true);
            var output = new byte[outLength];
            for (var i = 0; i < outLength; i++) output[i] = (byte)(h[i / 8] >> (8 * (i % 8)));
            return output;
        }

        static ulong RotR(ulong x, int n) => (x >> n) | (x << (64 - n));

        static void Compress(ulong[] h, ulong[] m, ulong counter, bool last)
        {
            var v = new ulong[16];
            Array.Copy(h, v, 8);
            Array.Copy(IV, 0, v, 8, 8);
            v[12] ^= counter;
            if (last) v[14] = ~v[14];
            for (var r = 0; r < 12; r++)
            {
                var s = r % 10;
                G(v, 0, 4, 8, 12, m[Sigma[s, 0]], m[Sigma[s, 1]]);
                G(v, 1, 5, 9, 13, m[Sigma[s, 2]], m[Sigma[s, 3]]);
                G(v, 2, 6, 10, 14, m[Sigma[s, 4]], m[Sigma[s, 5]]);
                G(v, 3, 7, 11, 15, m[Sigma[s, 6]], m[Sigma[s, 7]]);
                G(v, 0, 5, 10, 15, m[Sigma[s, 8]], m[Sigma[s, 9]]);
                G(v, 1, 6, 11, 12, m[Sigma[s, 10]], m[Sigma[s, 11]]);
                G(v, 2, 7, 8, 13, m[Sigma[s, 12]], m[Sigma[s, 13]]);
                G(v, 3, 4, 9, 14, m[Sigma[s, 14]], m[Sigma[s, 15]]);
            }
            for (var i = 0; i < 8; i++) h[i] ^= v[i] ^ v[i + 8];
        }

        static void G(ulong[] v, int a, int b, int c, int d, ulong x, ulong y)
        {
            unchecked
            {
                v[a] = v[a] + v[b] + x;
                v[d] = RotR(v[d] ^ v[a], 32);
                v[c] = v[c] + v[d];
                v[b] = RotR(v[b] ^ v[c], 24);
                v[a] = v[a] + v[b] + y;
                v[d] = RotR(v[d] ^ v[a], 16);
                v[c] = v[c] + v[d];
                v[b] = RotR(v[b] ^ v[c], 63);
            }
        }
    }
}
