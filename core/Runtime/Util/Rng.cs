namespace Tailed.Core.Util
{
    /// <summary>
    /// Deterministic SplitMix64 RNG. Used instead of System.Random so sequences are
    /// identical across Mono, IL2CPP and .NET (needed for seeded maps and replays).
    /// </summary>
    public struct Rng
    {
        ulong _state;

        public Rng(ulong seed) => _state = seed;

        public ulong NextULong()
        {
            ulong z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>Uniform in [0, maxExclusive).</summary>
        public int NextInt(int maxExclusive) => (int)(NextULong() % (ulong)maxExclusive);

        /// <summary>Uniform in [0, 1).</summary>
        public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

        public float Range(float min, float max) => min + (float)NextDouble() * (max - min);

        /// <summary>Index into <paramref name="weights"/>, chosen proportionally to weight.</summary>
        public int PickWeighted(float[] weights)
        {
            float total = 0f;
            foreach (var w in weights) total += w;
            float r = (float)NextDouble() * total;
            for (int i = 0; i < weights.Length; i++)
            {
                r -= weights[i];
                if (r < 0f) return i;
            }
            return weights.Length - 1;
        }
    }
}
