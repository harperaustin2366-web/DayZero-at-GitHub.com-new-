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
        public static void StableSort<T>(List<T> list, Comparison<T> cmp)
        {
            var idx = new List<(T v, int i)>(list.Count);
            for (int i = 0; i < list.Count; i++) idx.Add((list[i], i));
            idx.Sort((a, b) => { int c = cmp(a.v, b.v); return c != 0 ? c : a.i.CompareTo(b.i); });
            for (int i = 0; i < list.Count; i++) list[i] = idx[i].v;
        }

        public static Dictionary<string, double> D(params (string k, double v)[] kv)
        {
            var d = new Dictionary<string, double>();
            foreach (var (k, v) in kv) d[k] = v;
            return d;
        }
    }

    /// <summary>
    /// Stand-in for Godot's RandomNumberGenerator. NOTE: Godot uses PCG32; outputs will not
    /// match the Godot build unless an implementation ports PCG32 + Godot's randf/randi_range.
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
