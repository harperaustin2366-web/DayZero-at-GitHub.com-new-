// Minimal interfaces for scripts referenced by data.gd / regions.gd but not ported yet.
using System.Collections.Generic;

namespace DayZero.Core
{
    /// <summary>
    /// STUB for scripts/core/forest.gd (RefCounted; constructed as ForestGen.new(Data)).
    /// Data.SetWorldSeed calls Generate(seed), then reads Counts and Timing.
    /// Register an implementation via Data.ForestGenFactory once forest.gd is ported.
    /// </summary>
    public interface IForestGen
    {
        void Generate(int seed);
        Dictionary<string, int> Counts { get; }
        List<long> Timing { get; }
    }

    /// <summary>
    /// forest.gd's static hash helpers (h32 / hf), ported in full because regions.gd uses hf.
    /// GDScript ints are 64-bit, so all arithmetic is done in long with explicit 32-bit masking.
    /// </summary>
    public static class ForestGen
    {
        public static long H32(long a, long b, long s)
        {
            unchecked
            {
                long h = (a * 374761393L + b * 668265263L + s * 974634173L + 1013904223L) & 4294967295L;
                h = ((h ^ (h >> 13)) * 1274126177L) & 4294967295L;
                h = (h ^ (h >> 16)) & 4294967295L;
                return h;
            }
        }

        public static double Hf(long a, long b, long s) => (double)(H32(a, b, s) & 65535) / 65535.0;
    }
}
