// Godot Vector2/Vector2i/Rect2i semantics used by the game logic, as static helpers / extension
// methods over UnityEngine.Vector2 / Vector2Int / RectInt. Names are chosen so they don't clash
// with Unity's own instance members. Godot vectors are float32 (real_t), like Unity's.
// NOTE: never use Unity's Vector2 == (it is approximate); use V.Same() for exact compares.
using System;
using UnityEngine;

namespace DayZero.Core
{
    public static class V
    {
        public const double TAU = Math.PI * 2.0;

        public static Vector2 F(Vector2Int p) => new Vector2(p.x, p.y);
        public static Vector2 F(double x, double y) => new Vector2((float)x, (float)y);
        /// <summary>Vector2i(Vector2): truncation toward zero (Godot casts with (int32_t)).</summary>
        public static Vector2Int I(Vector2 p) => new Vector2Int((int)p.x, (int)p.y);
        public static Vector2Int I(int x, int y) => new Vector2Int(x, y);
        public static readonly Vector2Int NONE = new Vector2Int(-1, -1);
        public static readonly Vector2 HALF = new Vector2(0.5f, 0.5f);

        public static float DistTo(this Vector2 a, Vector2 b)
        {
            float dx = a.x - b.x, dy = a.y - b.y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }
        public static float Dist2To(this Vector2 a, Vector2 b)
        {
            float dx = a.x - b.x, dy = a.y - b.y;
            return dx * dx + dy * dy;
        }
        public static float DistTo(this Vector2Int a, Vector2Int b) => F(a).DistTo(F(b));
        public static float Len(this Vector2 a) => (float)Math.Sqrt(a.x * a.x + a.y * a.y);
        public static float Len(float x, float y) => (float)Math.Sqrt(x * x + y * y);
        public static Vector2 LerpTo(this Vector2 a, Vector2 b, float w) => new Vector2(a.x + (b.x - a.x) * w, a.y + (b.y - a.y) * w);
        public static float Ang(this Vector2 a) => (float)Math.Atan2(a.y, a.x);
        public static Vector2 Norm(this Vector2 a)
        {
            float l = a.x * a.x + a.y * a.y;
            if (l == 0f) return a;
            l = (float)Math.Sqrt(l);
            return new Vector2(a.x / l, a.y / l);
        }
        public static Vector2 MoveToward(this Vector2 a, Vector2 to, float delta)
        {
            Vector2 vd = new Vector2(to.x - a.x, to.y - a.y);
            float len = vd.Len();
            return len <= delta || len < 0.00001f ? to : new Vector2(a.x + vd.x / len * delta, a.y + vd.y / len * delta);
        }
        public static Vector2 Mul(this Vector2 a, double s) => new Vector2(a.x * (float)s, a.y * (float)s);
        public static Vector2 Add(this Vector2 a, Vector2 b) => new Vector2(a.x + b.x, a.y + b.y);
        public static Vector2 Sub(this Vector2 a, Vector2 b) => new Vector2(a.x - b.x, a.y - b.y);
        public static Vector2Int Add(this Vector2Int a, int x, int y) => new Vector2Int(a.x + x, a.y + y);
        public static Vector2Int Sub(this Vector2Int a, Vector2Int b) => new Vector2Int(a.x - b.x, a.y - b.y);
        public static Vector2Int Mul(this Vector2Int a, int s) => new Vector2Int(a.x * s, a.y * s);
        public static bool Same(this Vector2 a, Vector2 b) => a.x == b.x && a.y == b.y;
        public static bool Same(this Vector2Int a, Vector2Int b) => a.x == b.x && a.y == b.y;
        public static bool IsZero(this Vector2Int a) => a.x == 0 && a.y == 0;
        /// <summary>Vector2.round(): per-component half away from zero.</summary>
        public static Vector2 Rnd(this Vector2 a) => new Vector2((float)Gd.Round(a.x), (float)Gd.Round(a.y));
        public static Vector2Int Clamp(Vector2Int p, int w, int h) => new Vector2Int(Math.Max(0, Math.Min(w - 1, p.x)), Math.Max(0, Math.Min(h - 1, p.y)));

        // ---- scalar helpers (GDScript built-ins) ----
        public static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
        public static int Clampi(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
        public static double Wrapf(double value, double min, double max)
        {
            double range = max - min;
            if (Math.Abs(range) < 0.00001) return min;
            double result = value - (range * Math.Floor((value - min) / range));
            if (Math.Abs(result - max) < 0.00001 * Math.Max(1.0, Math.Abs(max))) return min;
            return result;
        }
        public static double Fposmod(double x, double y)
        {
            double value = x % y;
            if ((value < 0 && y > 0) || (value > 0 && y < 0)) value += y;
            return value + 0.0;
        }
        public static double Snapped(double v, double step) => step != 0 ? Math.Floor(v / step + 0.5) * step : v;

        // ---- Rect2i ----
        public static RectInt Expand(RectInt r, Vector2Int p)
        {
            int bx = r.x, by = r.y, ex = r.x + r.width, ey = r.y + r.height;
            if (p.x < bx) bx = p.x;
            if (p.y < by) by = p.y;
            if (p.x > ex) ex = p.x;
            if (p.y > ey) ey = p.y;
            return new RectInt(bx, by, ex - bx, ey - by);
        }
        public static RectInt Merge(RectInt a, RectInt b)
        {
            int bx = Math.Min(a.x, b.x), by = Math.Min(a.y, b.y);
            int ex = Math.Max(a.x + a.width, b.x + b.width), ey = Math.Max(a.y + a.height, b.y + b.height);
            return new RectInt(bx, by, ex - bx, ey - by);
        }
        public static bool Intersects(RectInt a, RectInt b)
        {
            if (a.x >= b.x + b.width) return false;
            if (a.x + a.width <= b.x) return false;
            if (a.y >= b.y + b.height) return false;
            if (a.y + a.height <= b.y) return false;
            return true;
        }
        /// <summary>Rect2i.intersection(): Rect2i() (all zero) when they don't overlap.</summary>
        public static RectInt Intersection(RectInt a, RectInt b)
        {
            if (!Intersects(b, a)) return new RectInt(0, 0, 0, 0);
            int bx = Math.Max(a.x, b.x), by = Math.Max(a.y, b.y);
            int ex = Math.Min(a.x + a.width, b.x + b.width), ey = Math.Min(a.y + a.height, b.y + b.height);
            return new RectInt(bx, by, ex - bx, ey - by);
        }
        public static RectInt Resize(RectInt r, int dw, int dh) => new RectInt(r.x, r.y, r.width + dw, r.height + dh);
    }
}
