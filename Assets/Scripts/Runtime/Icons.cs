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
        // v0.14.0a: richer badges (lit fill, bevel ring, gloss, glyph drop shadow + dark keyline, warm ink gradient)
        // and bare "glyph" icons in a colour for the resource chips. Same ids, same sizes, same 1-px SDF anti-aliasing.
        static readonly Color RIM = new Color(0.08f, 0.07f, 0.06f, 1f);
        static readonly Color INK_TOP = new Color(1f, 0.98f, 0.92f, 1f), INK_BOT = new Color(0.88f, 0.83f, 0.70f, 1f);

        /// <summary>RGBA pixels, row 0 = top. ids: the conversion types plus "repair", "siren", "warn", "wall",
        /// "gate", "tower", "trap", "upgrade", and the chip / clock glyphs "food", "meds", "ammo", "fuel", "mats",
        /// "morale", "sun", "moon", "skull".</summary>
        public static Color32[] Draw(string id, int size, Color badge)
        {
            var px = new Color32[size * size];
            float s = size;
            Color dark = badge * 0.30f; dark.a = 1f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    // normalised coords: centre 0, radius 1, +y up
                    float u = ((x + 0.5f) / s - 0.5f) * 2f, v = -((y + 0.5f) / s - 0.5f) * 2f;
                    float pxw = 2f / s;   // one pixel in normalised units
                    float r = Len(u, v);
                    float aOuter = Cov(r - 0.96f, pxw), aInner = Cov(r - 0.84f, pxw);
                    // rim: near-black, catching a little of the badge colour on its upper half
                    Color c = Color.Lerp(RIM, badge * 0.42f, Mathf.Clamp01(v * 0.5f + 0.5f) * 0.55f); c.a = 1f;
                    // fill: lit from the top-left, darker toward the bottom-right
                    float lit = Mathf.Clamp01(0.5f + 0.42f * (v * 0.8f - u * 0.35f));
                    Color fill = Color.Lerp(badge * 0.60f, Color.Lerp(badge, new Color(1f, 1f, 1f, 1f), 0.16f), lit); fill.a = 1f;
                    // bevel: light ring just inside the rim at the top, a shadow ring at the bottom
                    float band = aInner - Cov(r - 0.76f, pxw);
                    fill = Color.Lerp(fill, new Color(1f, 0.97f, 0.88f, 1f), band * Mathf.Clamp01(v) * 0.38f);
                    fill = Color.Lerp(fill, new Color(0f, 0f, 0f, 1f), band * Mathf.Clamp01(-v) * 0.30f);
                    // soft gloss on the upper half
                    float gloss = Cov(Len(u * 0.92f, (v - 0.46f) * 1.7f) - 0.6f, pxw * 6f);
                    fill = Color.Lerp(fill, new Color(1f, 1f, 1f, 1f), gloss * 0.10f);
                    c = Color.Lerp(c, fill, aInner);
                    // glyph: soft drop shadow, dark keyline, then the ink with a warm top-to-bottom gradient
                    float g = Glyph(id, u / 0.84f, v / 0.84f) * 0.84f;
                    float gs = Glyph(id, (u - 0.035f) / 0.84f, (v + 0.055f) / 0.84f) * 0.84f;
                    c = Color.Lerp(c, new Color(0f, 0f, 0f, 1f), Cov(gs - 0.02f, pxw * 3f) * 0.45f * aInner);
                    c = Color.Lerp(c, dark, Cov(g - 0.045f, pxw) * 0.9f * aInner);
                    Color ink = Color.Lerp(INK_BOT, INK_TOP, Mathf.Clamp01(v * 0.6f + 0.55f));
                    c = Color.Lerp(c, ink, Cov(g, pxw) * aInner);
                    c.a = aOuter;
                    px[y * size + x] = c;
                }
            return px;
        }

        /// <summary>A bare glyph (no badge) in a colour, with a dark keyline and a soft drop shadow: the resource
        /// chip icons. Row 0 = top.</summary>
        public static Color32[] DrawGlyph(string id, int size, Color col)
        {
            var px = new Color32[size * size];
            float s = size, k = 0.80f;
            Color hi = Color.Lerp(col, new Color(1f, 0.98f, 0.9f, 1f), 0.30f), lo = col * 0.72f, line = col * 0.18f;
            hi.a = lo.a = 1f; line.a = 1f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float u = ((x + 0.5f) / s - 0.5f) * 2f, v = -((y + 0.5f) / s - 0.5f) * 2f;
                    float pxw = 2f / s;
                    float g = Glyph(id, u * k, v * k) / k;
                    float gs = Glyph(id, (u - 0.05f) * k, (v + 0.08f) * k) / k;
                    float aSh = Cov(gs - 0.06f, pxw * 3f) * 0.55f;
                    float aLine = Cov(g - 0.085f, pxw), aFill = Cov(g, pxw);
                    Color fill = Color.Lerp(lo, hi, Mathf.Clamp01(v * 0.55f + 0.55f));
                    // shadow under keyline under fill, premultiplied by hand into a straight-alpha pixel
                    float a = aSh + (1f - aSh) * aLine;
                    Color c = line;
                    c = Color.Lerp(c, fill, aFill);
                    c.a = Mathf.Max(a, aFill);
                    if (aLine <= 0f && aFill <= 0f) c = new Color(0f, 0f, 0f, aSh);
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
        static float RBox(float x, float y, float cx, float cy, float hx, float hy, float ang, float r = 0f)
        {
            float c = Mathf.Cos(-ang), s = Mathf.Sin(-ang);
            float lx = (x - cx) * c - (y - cy) * s, ly = (x - cx) * s + (y - cy) * c;
            return Box(lx, ly, 0f, 0f, hx, hy, r);
        }
        static float Circ(float x, float y, float cx, float cy, float r) => Len(x - cx, y - cy) - r;
        static float Ring(float x, float y, float cx, float cy, float r, float t) => Mathf.Abs(Len(x - cx, y - cy) - r) - t;
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

        // Strokes are 0.07 (thin) / 0.09 (main) wide throughout, corners rounded 0.03-0.06, so the set reads as one family.
        const float ST = 0.075f;

        static float Glyph(string id, float x, float y)
        {
            switch (id)
            {
                case "hospital": return U(Box(x, y, 0, 0, 0.6f, 0.2f, 0.06f), Box(x, y, 0, 0, 0.2f, 0.6f, 0.06f));
                case "farm":    // sprout: stem + two leaves + soil mound
                {
                    float stem = Seg(x, y, 0, -0.4f, 0.02f, 0.3f, ST);
                    float l1 = Sub(RBox(x, y, -0.27f, 0.12f, 0.27f, 0.12f, 0.5f, 0.11f), Seg(x, y, -0.45f, 0.02f, -0.1f, 0.21f, 0.022f));
                    float l2 = Sub(RBox(x, y, 0.27f, 0.3f, 0.27f, 0.12f, -0.5f, 0.11f), Seg(x, y, 0.45f, 0.2f, 0.1f, 0.39f, 0.022f));
                    float soil = Sub(Circ(x, y, 0, -0.95f, 0.55f), Box(x, y, 0, -1.2f, 1f, 0.6f));
                    return U(U(stem, U(l1, l2)), soil);
                }
                case "water":   // drop with a carved shine
                {
                    float d = U(Circ(x, y, 0, -0.18f, 0.4f), Tri(x, y, -0.35f, -0.0f, 0.35f, -0.0f, 0, 0.66f));
                    float shine = Mathf.Max(Ring(x, y, 0, -0.18f, 0.24f, 0.045f), Mathf.Max(x + 0.04f, -y - 0.12f));
                    return Sub(d, shine);
                }
                case "barracks": // bed: mattress + headboard + legs + pillow
                    return U(U(Box(x, y, 0.05f, -0.08f, 0.6f, 0.12f, 0.04f), Box(x, y, -0.58f, 0.08f, 0.08f, 0.32f, 0.04f)), U(Box(x, y, -0.3f, 0.12f, 0.16f, 0.09f, 0.06f), U(Box(x, y, 0.58f, -0.3f, 0.06f, 0.14f, 0.02f), Box(x, y, -0.58f, -0.3f, 0.06f, 0.14f, 0.02f))));
                case "storage": // crate: frame and X brace
                {
                    float o = Box(x, y, 0, 0, 0.56f, 0.56f, 0.06f), i = Box(x, y, 0, 0, 0.41f, 0.41f);
                    float xx = U(Seg(x, y, -0.4f, -0.4f, 0.4f, 0.4f, ST), Seg(x, y, -0.4f, 0.4f, 0.4f, -0.4f, ST));
                    float bolts = U(U(Circ(x, y, -0.485f, -0.485f, 0.035f), Circ(x, y, 0.485f, 0.485f, 0.035f)), U(Circ(x, y, -0.485f, 0.485f, 0.035f), Circ(x, y, 0.485f, -0.485f, 0.035f)));
                    return U(Sub(Sub(o, i), bolts), Mathf.Max(xx, i));
                }
                case "workshop": // wrench
                {
                    float h = Seg(x, y, -0.45f, -0.45f, 0.2f, 0.2f, 0.1f);
                    float head = Sub(Circ(x, y, 0.32f, 0.32f, 0.28f), U(Circ(x, y, 0.45f, 0.45f, 0.15f), RBox(x, y, 0.5f, 0.5f, 0.12f, 0.3f, -0.785f)));
                    float hole = Circ(x, y, -0.38f, -0.38f, 0.04f);
                    return Sub(U(h, head), hole);
                }
                case "armory":  // three cartridges with case rims
                {
                    float d = 9f;
                    for (int k = -1; k <= 1; k++)
                    {
                        float cx = k * 0.34f;
                        d = U(d, Box(x, y, cx, -0.2f, 0.11f, 0.34f, 0.02f));
                        d = U(d, Box(x, y, cx, -0.56f, 0.13f, 0.04f, 0.01f));
                        d = U(d, U(Circ(x, y, cx, 0.17f, 0.11f), Tri(x, y, cx - 0.11f, 0.17f, cx + 0.11f, 0.17f, cx, 0.47f)));
                        d = Sub(d, Box(x, y, cx, 0.1f, 0.14f, 0.022f));
                    }
                    return d;
                }
                case "ammo":    // two big rounds
                {
                    float d = 9f;
                    for (int k = -1; k <= 1; k += 2)
                    {
                        float cx = k * 0.22f;
                        d = U(d, Box(x, y, cx, -0.2f, 0.14f, 0.36f, 0.02f));
                        d = U(d, Box(x, y, cx, -0.57f, 0.16f, 0.05f, 0.015f));
                        d = U(d, U(Circ(x, y, cx, 0.2f, 0.14f), Tri(x, y, cx - 0.14f, 0.2f, cx + 0.14f, 0.2f, cx, 0.62f)));
                        d = Sub(d, Box(x, y, cx, 0.12f, 0.17f, 0.028f));
                    }
                    return d;
                }
                case "garage":  // jerrycan
                case "fuel":
                {
                    float body = Box(x, y, 0.05f, -0.12f, 0.42f, 0.5f, 0.08f);
                    float handle = Sub(Box(x, y, -0.08f, 0.45f, 0.26f, 0.12f, 0.04f), Box(x, y, -0.08f, 0.45f, 0.14f, 0.04f));
                    float spout = RBox(x, y, 0.42f, 0.45f, 0.08f, 0.16f, -0.6f, 0.02f);
                    float cut = Seg(x, y, -0.22f, -0.38f, 0.3f, 0.14f, 0.045f);
                    return Sub(U(U(body, handle), spout), cut);
                }
                case "school":  // open book with page lines
                {
                    float l = RBox(x, y, -0.3f, 0f, 0.28f, 0.42f, 0.12f, 0.03f), r = RBox(x, y, 0.3f, 0f, 0.28f, 0.42f, -0.12f, 0.03f);
                    float spine = Box(x, y, 0, 0f, 0.03f, 0.45f);
                    float lines = 9f;
                    for (int k = 0; k < 3; k++)
                    {
                        float yy = 0.18f - k * 0.18f;
                        lines = U(lines, Seg(x, y, -0.48f, yy - 0.06f, -0.14f, yy + 0.0f, 0.022f));
                        lines = U(lines, Seg(x, y, 0.14f, yy + 0.0f, 0.48f, yy - 0.06f, 0.022f));
                    }
                    return Sub(Sub(U(l, r), spine), lines);
                }
                case "kitchen": // pot with steam
                {
                    float pot = Box(x, y, 0, -0.25f, 0.46f, 0.3f, 0.1f);
                    float lid = Box(x, y, 0, 0.1f, 0.52f, 0.06f, 0.03f);
                    float knob = Circ(x, y, 0, 0.2f, 0.08f);
                    float st = U(Seg(x, y, -0.2f, 0.34f, -0.12f, 0.64f, 0.05f), Seg(x, y, 0.12f, 0.34f, 0.2f, 0.64f, 0.05f));
                    float handles = U(Box(x, y, -0.55f, -0.15f, 0.1f, 0.05f, 0.02f), Box(x, y, 0.55f, -0.15f, 0.1f, 0.05f, 0.02f));
                    return U(U(U(pot, lid), U(knob, st)), handles);
                }
                case "food":    // drumstick: meat with a shine, bone with two knobs
                {
                    float meat = U(Circ(x, y, -0.14f, 0.16f, 0.4f), Tri(x, y, -0.2f, -0.18f, 0.18f, 0.2f, 0.2f, -0.2f));
                    float shine = Mathf.Max(Ring(x, y, -0.14f, 0.16f, 0.25f, 0.04f), Mathf.Max(x + 0.1f, -(y - 0.2f)));
                    float bone = U(Seg(x, y, 0.1f, -0.1f, 0.4f, -0.4f, 0.075f), U(Circ(x, y, 0.36f, -0.52f, 0.1f), Circ(x, y, 0.52f, -0.36f, 0.1f)));
                    return U(Sub(meat, shine), bone);
                }
                case "meds":    // first-aid case with a cut-out cross
                {
                    float body = Box(x, y, 0, -0.1f, 0.6f, 0.44f, 0.1f);
                    float cross = U(Box(x, y, 0, -0.1f, 0.25f, 0.08f, 0.02f), Box(x, y, 0, -0.1f, 0.08f, 0.25f, 0.02f));
                    float handle = Sub(Box(x, y, 0, 0.42f, 0.24f, 0.14f, 0.07f), Box(x, y, 0, 0.38f, 0.13f, 0.08f, 0.03f));
                    return U(Sub(body, cross), handle);
                }
                case "mats":    // stacked planks with end grain
                {
                    float a = Box(x, y, 0, -0.44f, 0.62f, 0.12f, 0.03f);
                    float b = Box(x, y, -0.06f, -0.16f, 0.56f, 0.12f, 0.03f);
                    float c = RBox(x, y, 0.06f, 0.16f, 0.56f, 0.11f, 0.14f, 0.03f);
                    float grain = U(Seg(x, y, -0.35f, -0.44f, 0.2f, -0.44f, 0.018f), Seg(x, y, -0.2f, -0.16f, 0.32f, -0.16f, 0.018f));
                    float nails = U(Circ(x, y, 0.48f, -0.44f, 0.035f), Circ(x, y, -0.5f, -0.16f, 0.035f));
                    return Sub(U(U(a, b), c), U(grain, nails));
                }
                case "morale":  // heart
                {
                    float h = U(U(Circ(x, y, -0.23f, 0.16f, 0.29f), Circ(x, y, 0.23f, 0.16f, 0.29f)), Tri(x, y, -0.5f, 0.07f, 0.5f, 0.07f, 0, -0.58f));
                    float shine = Mathf.Max(Ring(x, y, -0.23f, 0.16f, 0.17f, 0.035f), Mathf.Max(x + 0.23f, -(y - 0.16f)));
                    return Sub(h, shine);
                }
                case "sun":
                {
                    float d = Circ(x, y, 0, 0, 0.3f);
                    for (int k = 0; k < 8; k++)
                    {
                        float a = k * 0.785398f, cs = Mathf.Cos(a), sn = Mathf.Sin(a);
                        d = U(d, Seg(x, y, cs * 0.44f, sn * 0.44f, cs * 0.62f, sn * 0.62f, 0.065f));
                    }
                    return d;
                }
                case "moon": return Sub(Circ(x, y, 0, 0, 0.52f), Circ(x, y, 0.26f, 0.2f, 0.44f));
                case "skull":
                {
                    float head = U(Circ(x, y, 0, 0.12f, 0.46f), Box(x, y, 0, -0.3f, 0.26f, 0.18f, 0.06f));
                    float holes = U(U(Circ(x, y, -0.18f, 0.06f, 0.13f), Circ(x, y, 0.18f, 0.06f, 0.13f)), Tri(x, y, -0.06f, -0.16f, 0.06f, -0.16f, 0f, -0.04f));
                    float teeth = U(Box(x, y, -0.09f, -0.38f, 0.022f, 0.1f), Box(x, y, 0.09f, -0.38f, 0.022f, 0.1f));
                    return Sub(head, U(holes, teeth));
                }
                case "repair":  // claw hammer
                {
                    float handle = RBox(x, y, -0.12f, -0.12f, 0.085f, 0.52f, -0.785f, 0.05f);
                    float grip = RBox(x, y, -0.34f, -0.34f, 0.11f, 0.2f, -0.785f, 0.06f);
                    float head = RBox(x, y, 0.24f, 0.24f, 0.36f, 0.13f, -0.785f, 0.04f);
                    float claw = Sub(RBox(x, y, 0.02f, 0.5f, 0.1f, 0.13f, -0.785f, 0.03f), Circ(x, y, -0.06f, 0.58f, 0.08f));
                    return Sub(U(U(handle, grip), U(head, claw)), RBox(x, y, -0.34f, -0.34f, 0.13f, 0.018f, -0.785f));
                }
                case "siren":   // megaphone + sound waves
                {
                    float horn = U(Tri(x, y, -0.32f, 0.15f, -0.32f, -0.15f, 0.22f, -0.42f), Tri(x, y, -0.32f, 0.15f, 0.22f, -0.42f, 0.22f, 0.42f));
                    float back = Box(x, y, -0.44f, 0f, 0.12f, 0.15f, 0.04f);
                    float grip = RBox(x, y, -0.22f, -0.3f, 0.06f, 0.15f, 0.25f, 0.03f);
                    float cone = Mathf.Abs(y) - 0.62f * (x - 0.12f);
                    float w1 = Mathf.Max(Ring(x, y, 0.12f, 0f, 0.5f, 0.055f), cone);
                    float w2 = Mathf.Max(Ring(x, y, 0.12f, 0f, 0.72f, 0.055f), cone);
                    return U(U(U(horn, back), grip), U(w1, w2));
                }
                case "warn":    // ! in a triangle outline
                {
                    float t = Tri(x, y, -0.64f, -0.5f, 0.64f, -0.5f, 0f, 0.62f) - 0.04f;
                    float ti = Tri(x, y, -0.4f, -0.36f, 0.4f, -0.36f, 0f, 0.36f);
                    float bang = U(Box(x, y, 0, 0.03f, 0.065f, 0.18f, 0.04f), Circ(x, y, 0, -0.25f, 0.075f));
                    return U(Sub(t, ti), bang);
                }
                case "wall":    // palisade logs with a cross rail
                {
                    float d = 9f;
                    for (int k = -2; k <= 2; k++) d = U(d, U(Box(x, y, k * 0.25f, -0.12f, 0.09f, 0.4f, 0.02f), Tri(x, y, k * 0.25f - 0.09f, 0.28f, k * 0.25f + 0.09f, 0.28f, k * 0.25f, 0.52f)));
                    return Sub(d, Box(x, y, 0, -0.02f, 0.7f, 0.025f));
                }
                case "gate":
                    return U(Sub(Box(x, y, 0, -0.05f, 0.55f, 0.5f, 0.05f), Box(x, y, 0, -0.12f, 0.4f, 0.45f)), U(Seg(x, y, -0.4f, -0.5f, 0.4f, 0.3f, 0.065f), Box(x, y, 0, -0.1f, 0.035f, 0.4f)));
                case "tower":
                    return U(U(Box(x, y, 0, 0.3f, 0.36f, 0.13f, 0.02f), Tri(x, y, -0.48f, 0.42f, 0.48f, 0.42f, 0, 0.68f)), U(Seg(x, y, -0.25f, 0.2f, -0.4f, -0.6f, 0.055f), U(Seg(x, y, 0.25f, 0.2f, 0.4f, -0.6f, 0.055f), U(Seg(x, y, -0.33f, -0.2f, 0.33f, -0.2f, 0.045f), Seg(x, y, -0.3f, -0.05f, 0.36f, -0.5f, 0.035f)))));
                case "trap":    // spikes on a beam
                {
                    float d = Box(x, y, 0, -0.48f, 0.62f, 0.08f, 0.03f);
                    for (int k = -2; k <= 2; k++) d = U(d, Tri(x, y, k * 0.24f - 0.1f, -0.42f, k * 0.24f + 0.1f, -0.42f, k * 0.24f + 0.04f * k, 0.35f + (k & 1) * 0.15f));
                    return d;
                }
                case "upgrade": // double chevron arrow
                    return U(U(Tri(x, y, -0.5f, 0.08f, 0.5f, 0.08f, 0, 0.62f), Box(x, y, 0, -0.22f, 0.18f, 0.3f, 0.02f)), Sub(Box(x, y, 0, -0.6f, 0.4f, 0.05f, 0.03f), Box(x, y, 0, -0.6f, 0.2f, 0.06f)));
                default: return Circ(x, y, 0, 0, 0.3f);
            }
        }

#if !DZ_PREVIEW
        // ---- Unity side ----
        static readonly Dictionary<(string, int, int, bool), Texture2D> _tex = new Dictionary<(string, int, int, bool), Texture2D>();

        static int Pack(Color c) { Color32 k = c; return (k.r << 24) | (k.g << 16) | (k.b << 8) | k.a; }

        /// <summary>Badge icon, cached per id / colour / size (no per-frame allocation).</summary>
        public static Texture2D Tex(string id, Color badge, int size = 64) => Get(id, badge, size, false);

        /// <summary>Bare glyph in a colour (resource chips, clock), cached.</summary>
        public static Texture2D GlyphTex(string id, Color col, int size = 48) => Get(id, col, size, true);

        static Texture2D Get(string id, Color col, int size, bool glyph)
        {
            var key = (id, Pack(col), size, glyph);
            if (_tex.TryGetValue(key, out var t) && t != null) return t;
            t = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
            var px = glyph ? DrawGlyph(id, size, col) : Draw(id, size, col);
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
