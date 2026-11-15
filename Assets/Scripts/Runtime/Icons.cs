// v0.12.0a: HUD icons drawn in code (no image assets, no font glyphs: the IMGUI default font on Android has
// no ✚ ☼ ⚒ ...). Each icon is a round badge in the building's colour with a dark rim and a light pictogram,
// rasterised from signed-distance shapes with 1-px anti-aliasing. Pure C# (Color32 arrays), so the offline
// preview composites the exact same pixels; GameUI turns them into Texture2Ds.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace DayZero.Runtime
{
    /// <summary>Floating-label declutter shared by GameUI and the offline preview: labels sorted nearest-first
    /// (lowest on screen) keep their spot, later ones are pushed up until they overlap neither another label nor a
    /// HUD panel; a label pushed more than maxShift is dropped.</summary>
    public static class HudLayout
    {
        public static List<Rect> Declutter(IList<Rect> want, IList<Rect> avoid, float maxShift = 90f, float gap = 3f)
        {
            var order = new List<int>();
            for (int i = 0; i < want.Count; i++) order.Add(i);
            order.Sort((a, b) => want[b].y.CompareTo(want[a].y));
            var placed = new List<Rect>();
            var o = new List<Rect>(new Rect[want.Count]);
            var drop = new bool[want.Count];
            foreach (int i in order)
            {
                var r = want[i];
                float shifted = 0f;
                for (int iter = 0; iter < 12; iter++)
                {
                    bool hit = false;
                    foreach (var p in placed) if (Over(r, p, gap)) { float ny = p.y - r.height - gap; shifted += r.y - ny; r = new Rect(r.x, ny, r.width, r.height); hit = true; }
                    if (!hit) break;
                }
                bool bad = shifted > maxShift;
                if (avoid != null) foreach (var a in avoid) if (Over(r, a, 0f)) bad = true;
                drop[i] = bad;
                o[i] = r;
                if (!bad) placed.Add(r);
            }
            for (int i = 0; i < o.Count; i++) if (drop[i]) o[i] = new Rect(-9999f, -9999f, 0f, 0f);
            return o;
        }

        static bool Over(Rect a, Rect b, float gap) => a.x < b.x + b.width + gap && b.x < a.x + a.width + gap && a.y < b.y + b.height + gap && b.y < a.y + a.height + gap;
    }

    public static class Icons
    {
        /// <summary>RGBA pixels, row 0 = top. ids: the conversion types plus "repair", "siren", "warn", "wall",
        /// "gate", "tower", "trap", "upgrade".</summary>
        public static Color32[] Draw(string id, int size, Color badge)
        {
            var px = new Color32[size * size];
            float s = size;
            Color rim = new Color(0.08f, 0.07f, 0.06f, 1f), ink = new Color(0.97f, 0.95f, 0.88f, 1f);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    // normalised coords: centre 0, radius 1, +y up
                    float u = ((x + 0.5f) / s - 0.5f) * 2f, v = -((y + 0.5f) / s - 0.5f) * 2f;
                    float pxw = 2f / s;   // one pixel in normalised units
                    float dOuter = Len(u, v) - 0.96f, dInner = Len(u, v) - 0.84f;
                    float aOuter = Cov(dOuter, pxw), aInner = Cov(dInner, pxw);
                    float g = Glyph(id, u / 0.84f, v / 0.84f) * 0.84f;
                    float aG = Cov(g, pxw) * aInner;
                    // badge: rim -> colour (slightly lighter at the top) -> glyph
                    Color fill = Color.Lerp(badge * 0.78f, badge, Mathf.Clamp01(v * 0.5f + 0.5f)); fill.a = 1f;
                    Color c = Color.Lerp(rim, fill, aInner);
                    c = Color.Lerp(c, ink, aG);
                    c.a = aOuter;
                    px[y * size + x] = c;
                }
            return px;
        }

        static float Len(float x, float y) => Mathf.Sqrt(x * x + y * y);
        static float Cov(float d, float pxw) => Mathf.Clamp01(0.5f - d / pxw);

        // ---- SDF primitives (normalised badge space, glyph roughly within radius 0.75) ----
        static float Box(float x, float y, float cx, float cy, float hx, float hy, float r = 0f)
        {
            float dx = Mathf.Abs(x - cx) - hx + r, dy = Mathf.Abs(y - cy) - hy + r;
            float ox = Mathf.Max(dx, 0f), oy = Mathf.Max(dy, 0f);
            return Len(ox, oy) + Mathf.Min(Mathf.Max(dx, dy), 0f) - r;
        }
        static float RBox(float x, float y, float cx, float cy, float hx, float hy, float ang)
        {
            float c = Mathf.Cos(-ang), s = Mathf.Sin(-ang);
            float lx = (x - cx) * c - (y - cy) * s, ly = (x - cx) * s + (y - cy) * c;
            return Box(lx, ly, 0f, 0f, hx, hy);
        }
        static float Circ(float x, float y, float cx, float cy, float r) => Len(x - cx, y - cy) - r;
        static float Seg(float x, float y, float ax, float ay, float bx, float by, float r)
        {
            float px = x - ax, py = y - ay, dx = bx - ax, dy = by - ay;
            float h = Mathf.Clamp01((px * dx + py * dy) / Mathf.Max(1e-6f, dx * dx + dy * dy));
            return Len(px - dx * h, py - dy * h) - r;
        }
        static float Tri(float x, float y, float ax, float ay, float bx, float by, float cx, float cy)
        {
            // signed distance to a triangle (negative inside)
            float d = Mathf.Min(Mathf.Min(SegD(x, y, ax, ay, bx, by), SegD(x, y, bx, by, cx, cy)), SegD(x, y, cx, cy, ax, ay));
            float s1 = (bx - ax) * (y - ay) - (by - ay) * (x - ax), s2 = (cx - bx) * (y - by) - (cy - by) * (x - bx), s3 = (ax - cx) * (y - cy) - (ay - cy) * (x - cx);
            bool inside = (s1 >= 0 && s2 >= 0 && s3 >= 0) || (s1 <= 0 && s2 <= 0 && s3 <= 0);
            return inside ? -d : d;
        }
        static float SegD(float x, float y, float ax, float ay, float bx, float by) => Seg(x, y, ax, ay, bx, by, 0f);
        static float U(float a, float b) => Mathf.Min(a, b);
        static float Sub(float a, float b) => Mathf.Max(a, -b);

        static float Glyph(string id, float x, float y)
        {
            switch (id)
            {
                case "hospital": return U(Box(x, y, 0, 0, 0.62f, 0.2f, 0.04f), Box(x, y, 0, 0, 0.2f, 0.62f, 0.04f));
                case "farm":    // sprout: stem + two leaves + soil
                    return U(U(Seg(x, y, 0, -0.45f, 0, 0.25f, 0.07f), U(RBox(x, y, -0.25f, 0.18f, 0.24f, 0.11f, 0.5f), RBox(x, y, 0.25f, 0.32f, 0.24f, 0.11f, -0.5f))), Box(x, y, 0, -0.52f, 0.5f, 0.08f, 0.04f));
                case "water":   // drop
                    return U(Circ(x, y, 0, -0.18f, 0.38f), Tri(x, y, -0.33f, -0.0f, 0.33f, -0.0f, 0, 0.62f));
                case "barracks": // bed: mattress + headboard + legs + pillow
                    return U(U(Box(x, y, 0.05f, -0.08f, 0.6f, 0.12f), Box(x, y, -0.58f, 0.08f, 0.08f, 0.32f)), U(Box(x, y, -0.3f, 0.12f, 0.16f, 0.09f, 0.05f), U(Box(x, y, 0.58f, -0.3f, 0.06f, 0.14f), Box(x, y, -0.58f, -0.3f, 0.06f, 0.14f))));
                case "storage": // crate with an X
                {
                    float o = Box(x, y, 0, 0, 0.55f, 0.55f, 0.04f), i = Box(x, y, 0, 0, 0.4f, 0.4f);
                    float frame = Sub(o, i);
                    float xx = U(Seg(x, y, -0.4f, -0.4f, 0.4f, 0.4f, 0.07f), Seg(x, y, -0.4f, 0.4f, 0.4f, -0.4f, 0.07f));
                    return U(frame, Mathf.Max(xx, i));
                }
                case "workshop": // wrench
                {
                    float h = Seg(x, y, -0.45f, -0.45f, 0.2f, 0.2f, 0.1f);
                    float head = Sub(Circ(x, y, 0.32f, 0.32f, 0.27f), U(Circ(x, y, 0.45f, 0.45f, 0.15f), RBox(x, y, 0.5f, 0.5f, 0.12f, 0.3f, -0.785f)));
                    return U(h, head);
                }
                case "armory":  // three cartridges
                {
                    float d = 9f;
                    for (int k = -1; k <= 1; k++)
                    {
                        float cx = k * 0.34f;
                        d = U(d, Box(x, y, cx, -0.2f, 0.11f, 0.34f, 0.02f));
                        d = U(d, U(Circ(x, y, cx, 0.17f, 0.11f), Tri(x, y, cx - 0.11f, 0.17f, cx + 0.11f, 0.17f, cx, 0.45f)));
                    }
                    return d;
                }
                case "garage":  // jerrycan
                {
                    float body = Box(x, y, 0.05f, -0.12f, 0.42f, 0.5f, 0.08f);
                    float handle = Sub(Box(x, y, -0.08f, 0.45f, 0.26f, 0.12f, 0.04f), Box(x, y, -0.08f, 0.45f, 0.14f, 0.04f));
                    float spout = RBox(x, y, 0.42f, 0.45f, 0.08f, 0.16f, -0.6f);
                    float cut = Seg(x, y, -0.2f, -0.35f, 0.25f, 0.15f, 0.045f);
                    return Sub(U(U(body, handle), spout), cut);
                }
                case "school":  // open book
                {
                    float l = RBox(x, y, -0.3f, 0f, 0.28f, 0.42f, 0.12f), r = RBox(x, y, 0.3f, 0f, 0.28f, 0.42f, -0.12f);
                    float spine = Box(x, y, 0, 0f, 0.03f, 0.45f);
                    return Sub(U(l, r), spine);
                }
                case "kitchen": // pot with steam
                {
                    float pot = Box(x, y, 0, -0.25f, 0.46f, 0.3f, 0.1f);
                    float lid = Box(x, y, 0, 0.1f, 0.52f, 0.06f, 0.03f);
                    float knob = Circ(x, y, 0, 0.2f, 0.08f);
                    float st = U(Seg(x, y, -0.2f, 0.32f, -0.12f, 0.62f, 0.05f), Seg(x, y, 0.12f, 0.32f, 0.2f, 0.62f, 0.05f));
                    float handles = U(Box(x, y, -0.55f, -0.15f, 0.1f, 0.05f), Box(x, y, 0.55f, -0.15f, 0.1f, 0.05f));
                    return U(U(U(pot, lid), U(knob, st)), handles);
                }
                case "repair":  // hammer
                    return U(RBox(x, y, -0.1f, -0.1f, 0.08f, 0.5f, -0.785f), RBox(x, y, 0.22f, 0.22f, 0.36f, 0.13f, -0.785f));
                case "siren":   // horn speaker + waves
                {
                    float horn = U(Box(x, y, -0.45f, 0, 0.12f, 0.16f), Tri(x, y, -0.35f, 0.16f, -0.35f, -0.16f, 0.05f, 0.0f) + 0f);
                    horn = U(horn, Tri(x, y, -0.35f, 0.4f, -0.35f, -0.4f, 0.0f, 0f));
                    float w1 = Mathf.Abs(Circ(x, y, -0.15f, 0, 0.42f)) - 0.05f; w1 = Mathf.Max(w1, -(x - 0.15f));
                    float w2 = Mathf.Abs(Circ(x, y, -0.15f, 0, 0.66f)) - 0.05f; w2 = Mathf.Max(w2, -(x - 0.32f));
                    return U(horn, U(Mathf.Max(w1, Mathf.Abs(y) - 0.32f), Mathf.Max(w2, Mathf.Abs(y) - 0.45f)));
                }
                case "warn":    // ! in a triangle outline
                {
                    float t = Tri(x, y, -0.62f, -0.5f, 0.62f, -0.5f, 0f, 0.6f);
                    float ti = Tri(x, y, -0.4f, -0.37f, 0.4f, -0.37f, 0f, 0.35f);
                    float bang = U(Box(x, y, 0, 0.02f, 0.06f, 0.18f, 0.03f), Circ(x, y, 0, -0.26f, 0.07f));
                    return U(Sub(t, ti), bang);
                }
                case "wall":    // palisade logs
                {
                    float d = 9f;
                    for (int k = -2; k <= 2; k++) d = U(d, U(Box(x, y, k * 0.25f, -0.12f, 0.09f, 0.4f), Tri(x, y, k * 0.25f - 0.09f, 0.28f, k * 0.25f + 0.09f, 0.28f, k * 0.25f, 0.5f)));
                    return d;
                }
                case "gate":
                    return U(Sub(Box(x, y, 0, -0.05f, 0.55f, 0.5f, 0.03f), Box(x, y, 0, -0.12f, 0.4f, 0.45f)), U(Seg(x, y, -0.4f, -0.5f, 0.4f, 0.3f, 0.06f), Box(x, y, 0, -0.1f, 0.03f, 0.4f)));
                case "tower":
                    return U(U(Box(x, y, 0, 0.3f, 0.36f, 0.13f), Tri(x, y, -0.45f, 0.42f, 0.45f, 0.42f, 0, 0.65f)), U(Seg(x, y, -0.25f, 0.2f, -0.4f, -0.6f, 0.05f), U(Seg(x, y, 0.25f, 0.2f, 0.4f, -0.6f, 0.05f), Seg(x, y, -0.33f, -0.2f, 0.33f, -0.2f, 0.04f))));
                case "trap":    // spikes
                {
                    float d = Box(x, y, 0, -0.48f, 0.6f, 0.07f);
                    for (int k = -2; k <= 2; k++) d = U(d, Tri(x, y, k * 0.24f - 0.1f, -0.42f, k * 0.24f + 0.1f, -0.42f, k * 0.24f + 0.04f * k, 0.35f + (k & 1) * 0.15f));
                    return d;
                }
                case "upgrade": // up arrow
                    return U(Tri(x, y, -0.5f, 0.05f, 0.5f, 0.05f, 0, 0.6f), Box(x, y, 0, -0.25f, 0.18f, 0.32f));
                default: return Circ(x, y, 0, 0, 0.3f);
            }
        }

#if !DZ_PREVIEW
        // ---- Unity side ----
        static readonly Dictionary<string, Texture2D> _tex = new Dictionary<string, Texture2D>();

        public static Texture2D Tex(string id, Color badge, int size = 64)
        {
            string key = id + "#" + ColorUtility.ToHtmlStringRGB(badge) + size;
            if (_tex.TryGetValue(key, out var t) && t != null) return t;
            t = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
            var px = Draw(id, size, badge);
            // Texture2D rows run bottom-up
            var flip = new Color32[px.Length];
            for (int y = 0; y < size; y++) Array.Copy(px, y * size, flip, (size - 1 - y) * size, size);
            t.SetPixels32(flip);
            t.Apply(false, true);
            _tex[key] = t;
            return t;
        }

#endif
        public static Color ConvColor(string type) => DayZero.Core.Data.CONVERSIONS.TryGetValue(type, out var cd) ? cd.Color : new Color(0.78f, 0.7f, 0.55f);
    }
}
