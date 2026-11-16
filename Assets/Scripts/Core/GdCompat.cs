// Small helpers that reproduce Godot 4 semantics the ported code depends on.
// Godot Rect2/Rect2i.has_point use [position, end) half-open bounds; we implement
// them here explicitly instead of relying on Unity's Rect/RectInt helpers.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using UnityEngine;

namespace DayZero.Core
{
    public static class Gd
    {
        static readonly Stopwatch _clock = Stopwatch.StartNew();

        /// <summary>Godot Time.get_ticks_msec().</summary>
        public static long TicksMsec() => _clock.ElapsedMilliseconds;

        /// <summary>Godot Color("#rrggbb") / Color("#rrggbbaa").</summary>
        public static Color Hex(string hex)
        {
            string h = hex.TrimStart('#');
            float C(int i) => int.Parse(h.Substring(i, 2), NumberStyles.HexNumber) / 255f;
            return new Color(C(0), C(2), C(4), h.Length >= 8 ? C(6) : 1f);
        }

        // ---- Rect2i ----
        public static bool Has(RectInt r, Vector2Int p) =>
            p.x >= r.x && p.y >= r.y && p.x < r.x + r.width && p.y < r.y + r.height;
        public static RectInt Grow(RectInt r, int by) =>
            new RectInt(r.x - by, r.y - by, r.width + 2 * by, r.height + 2 * by);
        /// <summary>Rect2i.get_center(): integer position + size / 2.</summary>
        public static Vector2Int Center(RectInt r) => new Vector2Int(r.x + r.width / 2, r.y + r.height / 2);
        public static int EndX(RectInt r) => r.x + r.width;
        public static int EndY(RectInt r) => r.y + r.height;

        // ---- Rect2 ----
        public static Rect ToRect(RectInt r) => new Rect(r.x, r.y, r.width, r.height);
        public static bool Has(Rect r, Vector2 p) =>
            p.x >= r.x && p.y >= r.y && p.x < r.x + r.width && p.y < r.y + r.height;
        public static Rect Grow(Rect r, float by) =>
            new Rect(r.x - by, r.y - by, r.width + 2 * by, r.height + 2 * by);

        /// <summary>Geometry2D.get_closest_point_to_segment.</summary>
        public static Vector2 ClosestPointToSegment(Vector2 p, Vector2 s0, Vector2 s1)
        {
            Vector2 d = p - s0, n = s1 - s0;
            float l2 = n.x * n.x + n.y * n.y;
            if (l2 < 1e-20f) return s0;
            float t = (n.x * d.x + n.y * d.y) / l2;
            if (t <= 0f) return s0;
            if (t >= 1f) return s1;
            return s0 + n * t;
        }

        /// <summary>GDScript float roundf(): half away from zero (C# Math.Round defaults to banker's rounding).</summary>
        public static double Round(double v) => Math.Round(v, MidpointRounding.AwayFromZero);

        /// <summary>GDScript Array indexing: negative indices count from the end.</summary>
        public static T At<T>(IList<T> a, int i) => a[i < 0 ? a.Count + i : i];

        /// <summary>Approximation of Godot String.capitalize() ("gas_station" -> "Gas Station").</summary>
        public static string Capitalize(string s)
        {
            var parts = s.Replace('_', ' ').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
                parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1).ToLowerInvariant();
            return string.Join(" ", parts);
        }

        /// <summary>PackedByteArray.to_int32_array() (little-endian).</summary>
        public static int[] ToInt32Array(byte[] raw)
        {
            var o = new int[raw.Length / 4];
            Buffer.BlockCopy(raw, 0, o, 0, o.Length * 4);
            return o;
        }

        /// <summary>Stable sort (Godot sort_custom is unstable; we pick stable for determinism).</summary>
        /// <summary>Array.sort_custom / Array.sort: a port of Godot 4's SortArray (core/templates/sort_array.h,
        /// introsort + final insertion sort, Validate on). Godot's sort is NOT stable; reproducing its exact element
        /// order on ties keeps seeded runs identical to the original. cmp(a, b) &lt; 0 means "a before b" (the
        /// GDScript comparator returning true). Name kept for the existing call sites.</summary>
        public static void StableSort<T>(List<T> list, Comparison<T> cmp) => GodotSort(list, (a, b) => cmp(a, b) < 0);

        public static void GodotSort<T>(List<T> a, Func<T, T, bool> less)
        {
            int n = a.Count;
            if (n == 0) return;
            new SortArray<T>(a, less).Sort(n);
        }

        sealed class SortArray<T>
        {
            const int THRESH = 16;
            readonly List<T> A; readonly Func<T, T, bool> L;
            public SortArray(List<T> a, Func<T, T, bool> l) { A = a; L = l; }
            public void Sort(int n) { Introsort(0, n, Bitlog(n) * 2); FinalInsertion(0, n); }
            static int Bitlog(int n) { int k = 0; for (; n != 1; n >>= 1) k++; return k; }
            T Median3(T a, T b, T c)
            {
                if (L(a, b)) { if (L(b, c)) return b; else if (L(a, c)) return c; else return a; }
                else if (L(a, c)) return a;
                else if (L(b, c)) return c;
                else return b;
            }
            void PushHeap(int first, int hole, int top, T value)
            {
                int parent = (hole - 1) / 2;
                while (hole > top && L(A[first + parent], value)) { A[first + hole] = A[first + parent]; hole = parent; parent = (hole - 1) / 2; }
                A[first + hole] = value;
            }
            void PopHeap(int first, int last, int result, T value) { A[result] = A[first]; AdjustHeap(first, 0, last - first, value); }
            void AdjustHeap(int first, int hole, int len, T value)
            {
                int top = hole, second = 2 * hole + 2;
                while (second < len)
                {
                    if (L(A[first + second], A[first + (second - 1)])) second--;
                    A[first + hole] = A[first + second]; hole = second; second = 2 * (second + 1);
                }
                if (second == len) { A[first + hole] = A[first + (second - 1)]; hole = second - 1; }
                PushHeap(first, hole, top, value);
            }
            void SortHeap(int first, int last) { while (last - first > 1) { last--; PopHeap(first, last, last, A[last]); } }
            void MakeHeap(int first, int last)
            {
                if (last - first < 2) return;
                int len = last - first, parent = (len - 2) / 2;
                while (true) { AdjustHeap(first, parent, len, A[first + parent]); if (parent == 0) return; parent--; }
            }
            void PartialSort(int first, int last, int middle)
            {
                MakeHeap(first, middle);
                for (int i = middle; i < last; i++) if (L(A[i], A[first])) PopHeap(first, middle, i, A[i]);
                SortHeap(first, middle);
            }
            int Partitioner(int first, int last, T pivot)
            {
                int uf = first, ul = last;
                while (true)
                {
                    while (L(A[first], pivot)) { if (first == ul - 1) break; first++; }
                    last--;
                    while (L(pivot, A[last])) { if (last == uf) break; last--; }
                    if (!(first < last)) return first;
                    T t = A[first]; A[first] = A[last]; A[last] = t;
                    first++;
                }
            }
            void Introsort(int first, int last, int depth)
            {
                while (last - first > THRESH)
                {
                    if (depth == 0) { PartialSort(first, last, last); return; }
                    depth--;
                    int cut = Partitioner(first, last, Median3(A[first], A[first + (last - first) / 2], A[last - 1]));
                    Introsort(cut, last, depth);
                    last = cut;
                }
            }
            void UnguardedLinearInsert(int last, T value)
            {
                int next = last - 1;
                while (L(value, A[next])) { if (next == 0) break; A[last] = A[next]; last = next; next--; }
                A[last] = value;
            }
            void LinearInsert(int first, int last)
            {
                T val = A[last];
                if (L(val, A[first])) { for (int i = last; i > first; i--) A[i] = A[i - 1]; A[first] = val; }
                else UnguardedLinearInsert(last, val);
            }
            void InsertionSort(int first, int last) { if (first == last) return; for (int i = first + 1; i != last; i++) LinearInsert(first, i); }
            void UnguardedInsertionSort(int first, int last) { for (int i = first; i != last; i++) UnguardedLinearInsert(i, A[i]); }
            void FinalInsertion(int first, int last)
            {
                if (last - first > THRESH) { InsertionSort(first, first + THRESH); UnguardedInsertionSort(first + THRESH, last); }
                else InsertionSort(first, last);
            }
        }

        public static Dictionary<string, double> D(params (string k, double v)[] kv)
        {
            var d = new Dictionary<string, double>();
            foreach (var (k, v) in kv) d[k] = v;
            return d;
        }
    }

    /// <summary>
    /// The subset of Godot's RandomNumberGenerator that data.gd needs. GdRng (GdRng.cs) is the
    /// PCG32 port the game uses; SystemRng is only for tests that don't need Godot sequences.
    /// </summary>
    public interface IRng
    {
        float Randf();                    // [0, 1]
        int RandiRange(int from, int to); // inclusive
    }

    /// <summary>Simple System.Random-backed IRng (NOT Godot-compatible sequence).</summary>
    public sealed class SystemRng : IRng
    {
        readonly System.Random _r;
        public SystemRng(int seed) { _r = new System.Random(seed); }
        public float Randf() => (float)_r.NextDouble();
        public int RandiRange(int from, int to) => _r.Next(from, to + 1);
    }
}
