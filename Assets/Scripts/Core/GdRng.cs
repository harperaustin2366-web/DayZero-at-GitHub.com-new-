// Port of Godot 4's RandomNumberGenerator (core/math/random_pcg.{h,cpp} + thirdparty pcg.cpp).
// Written from the Godot 4.x sources as remembered: PCG32 (XSH-RR), default stream
// inc = 1442695040888963407, seed() = pcg32_srandom_r(seed, inc), randf() uses the
// "proto exponent" method (two rand() calls), randi_range uses pcg32_boundedrand_r.
// REVIEW: verify a few sequences against Godot 4.4.1 (e.g. seed 12345 -> first randi(), randf()).
// If they match, seeded runs (world gen, start positions, events) reproduce the Godot build.
using System;
using System.Collections.Generic;

namespace DayZero.Core
{
    public sealed class GdRng : IRng
    {
        const ulong DefaultInc = 1442695040888963407UL;
        ulong _state, _inc;
        ulong _seed;

        public GdRng() : this(0) { }
        public GdRng(ulong seed) { Seed = seed; }
        public GdRng(long seed) { Seed = unchecked((ulong)seed); }

        /// <summary>RandomNumberGenerator.seed (setting it reseeds, like Godot).</summary>
        public ulong Seed
        {
            get => _seed;
            set { _seed = value; SRandom(value, DefaultInc); }
        }
        public void SetSeed(long s) => Seed = unchecked((ulong)s);

        /// <summary>RandomNumberGenerator.state (the raw PCG state, for save/restore).</summary>
        public ulong State { get => _state; set => _state = value; }

        void SRandom(ulong initstate, ulong initseq)
        {
            _state = 0UL;
            _inc = (initseq << 1) | 1UL;
            Next();
            _state = unchecked(_state + initstate);
            Next();
        }

        uint Next()
        {
            unchecked
            {
                ulong old = _state;
                _state = old * 6364136223846793005UL + (_inc | 1UL);
                uint xorshifted = (uint)(((old >> 18) ^ old) >> 27);
                int rot = (int)(old >> 59);
                return (xorshifted >> rot) | (xorshifted << ((-rot) & 31));
            }
        }

        uint Bounded(uint bound)
        {
            unchecked
            {
                if (bound == 0) return Next();
                uint threshold = (uint)(-bound) % bound;
                for (;;)
                {
                    uint r = Next();
                    if (r >= threshold) return r % bound;
                }
            }
        }

        /// <summary>randi(): full 32-bit unsigned value.</summary>
        public uint Randi() => Next();

        /// <summary>randf() in [0, 1].</summary>
        public float Randf()
        {
            uint protoExpOffset = Next();
            if (protoExpOffset == 0) return 0f;
            uint m = Next() | 0x80000001u;
            return (float)Math.ScaleB((float)m, -32 - Clz32(protoExpOffset));
        }

        /// <summary>randf_range(from, to): randf() * (to - from) + from (float math, like Godot).</summary>
        public float RandfRange(double from, double to) => Randf() * (float)(to - from) + (float)from;

        /// <summary>randi_range(from, to), inclusive.</summary>
        public int RandiRange(int from, int to)
        {
            if (from == to) return from;
            return (int)Bounded((uint)Math.Abs(from - to) + 1u) + Math.Min(from, to);
        }

        static int Clz32(uint x)
        {
            if (x == 0) return 32;
            int n = 0;
            while ((x & 0x80000000u) == 0) { n++; x <<= 1; }
            return n;
        }

        /// <summary>Array.shuffle(). Godot uses the global RNG (Math::rand); we use this generator so
        /// runs are reproducible. Same Fisher-Yates shape as Godot's Array::shuffle.</summary>
        public void Shuffle<T>(IList<T> a)
        {
            int n = a.Count;
            if (n < 2) return;
            for (int i = n - 1; i >= 1; i--)
            {
                int j = (int)(Next() % (uint)(i + 1));
                T tmp = a[i]; a[i] = a[j]; a[j] = tmp;
            }
        }
    }

    public static class GdHash
    {
        /// <summary>Godot String::hash() (djb2, 32-bit) as returned by GDScript hash("...").
        /// Godot hashes the UTF-32 code points; for ASCII strings this equals hashing chars.</summary>
        public static uint StringHash(string s)
        {
            unchecked
            {
                uint h = 5381;
                foreach (char c in s) h = ((h << 5) + h) + c;
                return h;
            }
        }
    }
}
