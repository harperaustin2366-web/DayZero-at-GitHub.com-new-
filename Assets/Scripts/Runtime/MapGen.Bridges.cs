// v0.19.x: real bridges over the river (visual only; the sim still walks the T_BRIDGE tiles).
// Before, a crossing was the road strip drawn 0.06 above the water. Now every connected run of
// T_BRIDGE tiles becomes one oriented span (principal axis of its tiles, snapped to the city grid
// when close), drawn in the chunk that owns its centre:
//  * raised deck that ramps up from each bank and arches slightly, abutment walls and end posts where
//    it meets land, the river carried on underneath (T_BRIDGE counts as water for ground / water)
//  * style by district and width: short concrete girder spans (solid parapets with pilasters, lamp
//    posts, hammerhead piers) or longer steel pony-truss spans (riveted girder fascia, Warren truss,
//    twin round piers), rust and moss on both
//  * piers down into the water with a foam ring and a short wake, the deck's shadow on the water
//  * lane paint, footways, expansion joints, cracks, potholes, a missing parapet / truss piece, and
//    sometimes an abandoned car
//  * LOD1 (far chunks) keeps deck, parapets / top chord, piers and shadow only
// DeckLift(p) gives the deck height above road level so Crowd (and wrecks) stand on the deck.
using System;
using System.Collections.Generic;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed partial class MapGen
    {
        sealed class Span
        {
            public Vector2 O, A, S;          // bank end (centre line), axis, side
            public float L, Hw;              // length along A, half width (outer parapet face)
            public float W0, W1;             // waterline along the centre line (start / end)
            public float R0, R1;             // ramp lengths from each end
            public float Hd, Arch;           // deck height over the water, extra arch at mid span
            public bool Steel;
            public int Salt, Owner;
            public Vector2 Min, Max;         // tile-space bbox (lift lookups)
            public readonly List<float> Piers = new List<float>(4);
        }

        readonly List<Span> _spans = new List<Span>();
        bool DeckDrawn => _spans.Count > 0;

        static bool WetT(int t) => t == Data.T_WATER || t == Data.T_BRIDGE;

        static readonly Color B_CONC = Hex("#7e776a"), B_CONC_DARK = Hex("#5c564c"), B_CONC_STAIN = Hex("#4a463c");
        static readonly Color B_STEEL = Hex("#55605a"), B_STEEL_DARK = Hex("#3c4440"), B_RUST = Hex("#74462a"), B_RUST_DARK = Hex("#4c3022");
        static readonly Color B_JOINT = Hex("#34322c"), B_JOINT_PLATE = Hex("#5a574d"), B_FOAM_B = Hex("#7f887b"), B_WAKE = Hex("#4d574d");
        static readonly Color B_MOSS_B = Hex("#4a4f26");

        // ---------------- planning ----------------
        void PlanBridges()
        {
            // 1) every bridge tile takes the axis its bridge run is longest along (city grid axes in the
            //    city, the tile axes outside), so a V of two bridges sharing a bank corner splits in two
            int W = d.W, H = d.H;
            var axis = new sbyte[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int k = y * W + x;
                    if (d.Tiles[k] != Data.T_BRIDGE) continue;
                    var c = new Vector2(x + 0.5f, y + 0.5f);
                    bool city = InCity(c.x, c.y);
                    Vector2 a0 = city ? GU : new Vector2(1f, 0f), a1 = city ? GV : new Vector2(0f, 1f);
                    axis[k] = (sbyte)(BridgeRun(c, a0) >= BridgeRun(c, a1) ? 1 : 2);
                }
            // 2) flood fill tiles of the same axis into spans
            var seen = new bool[W * H];
            var pts = new List<Vector2>(256);
            var q = new Queue<int>();
            int ncx = (W + CH - 1) / CH;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int k = y * W + x;
                    if (seen[k] || axis[k] == 0) continue;
                    int ax0 = axis[k];
                    pts.Clear(); q.Clear(); q.Enqueue(k); seen[k] = true;
                    while (q.Count > 0)
                    {
                        int a = q.Dequeue(); int px = a % W, py = a / W;
                        pts.Add(new Vector2(px + 0.5f, py + 0.5f));
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int bx = px + dx, by = py + dy;
                                if (bx < 0 || by < 0 || bx >= W || by >= H) continue;
                                int b = by * W + bx;
                                if (!seen[b] && axis[b] == ax0) { seen[b] = true; q.Enqueue(b); }
                            }
                    }
                    if (pts.Count < 4) continue;
                    var m = pts[0]; bool city = InCity(m.x, m.y);
                    var A = ax0 == 1 ? (city ? GU : new Vector2(1f, 0f)) : (city ? GV : new Vector2(0f, 1f));
                    PlanSpan(pts, A, ncx);
                }
        }

        /// <summary>Length of the bridge-tile run through c along dir (both ways).</summary>
        float BridgeRun(Vector2 c, Vector2 dir)
        {
            float n = 0f;
            for (int sgn = -1; sgn <= 1; sgn += 2)
                for (float t = 0.4f; t < 40f; t += 0.4f)
                {
                    var p = c + dir * (t * sgn);
                    if (TileAtF(p.x, p.y) != Data.T_BRIDGE) break;
                    n += 0.4f;
                }
            return n;
        }

        void PlanSpan(List<Vector2> pts, Vector2 A, int ncx)
        {
            var S = new Vector2(-A.y, A.x);
            float amin = 1e9f, amax = -1e9f, csum = 0f;
            foreach (var p in pts) { float a = Vector2.Dot(p, A); amin = Mathf.Min(amin, a); amax = Mathf.Max(amax, a); csum += Vector2.Dot(p, S); }
            float padA = 0.5f * (Mathf.Abs(A.x) + Mathf.Abs(A.y));
            amin -= padA; amax += padA;
            float len = amax - amin;
            if (len < 2f) return;
            float width = Mathf.Clamp(pts.Count / len, 1.2f, 7f);   // tile area / length
            float cmid = csum / pts.Count;
            float hw = width * 0.5f + 0.12f;
            // run each end out until the whole end line stands on land, then a little onto the road
            float ext0 = 0f, ext1 = 0f;
            for (int side = 0; side < 2; side++)
            {
                float ext = 0f;
                for (; ext < 3f; ext += 0.2f)
                {
                    float a = side == 0 ? amin - ext : amax + ext;
                    bool wet = false;
                    for (int s = -2; s <= 2 && !wet; s++)
                    {
                        var p = A * a + S * (cmid + hw * 0.5f * s);
                        if (WetT(TileAtF(p.x, p.y))) wet = true;
                    }
                    if (!wet) break;
                }
                if (side == 0) ext0 = ext + 0.3f; else ext1 = ext + 0.3f;
            }
            var sp = new Span { A = A, S = S, Hw = hw };
            float a0 = amin - ext0, a1 = amax + ext1;
            sp.O = A * a0 + S * cmid; sp.L = a1 - a0;
            // waterline along the centre line
            sp.W0 = -1f; sp.W1 = -1f;
            for (float a = 0f; a <= sp.L; a += 0.1f)
            {
                var p = sp.O + A * a;
                if (WetT(TileAtF(p.x, p.y))) { if (sp.W0 < 0f) sp.W0 = a; sp.W1 = a; }
            }
            if (sp.W0 < 0f) { sp.W0 = sp.L * 0.25f; sp.W1 = sp.L * 0.75f; }
            var ctr = sp.O + A * (sp.L * 0.5f);
            sp.Salt = (int)(ctr.x * 131f) * 7 + (int)(ctr.y * 71f);
            float wspan = sp.W1 - sp.W0;
            // style: steel truss for wide / long crossings and the industrial / waterfront districts
            string kind = "";
            try { var ds = d.DistrictAt(new Vector2Int(Mathf.FloorToInt(ctr.x), Mathf.FloorToInt(ctr.y))); if (ds != null && ds.Kind != null) kind = ds.Kind; } catch { }
            float h = Hf(sp.Salt, 3);
            bool favour = kind == "industrial" || kind == "waterfront" || width >= 4.2f;
            sp.Steel = favour ? h < 0.8f : h < 0.25f;
            sp.Hd = sp.Steel ? 0.34f : 0.3f;
            sp.Arch = Mathf.Clamp(wspan * 0.007f, 0.02f, 0.09f);
            sp.R0 = Mathf.Max(1.3f, sp.W0 + 1.1f); sp.R1 = Mathf.Max(1.3f, sp.L - sp.W1 + 1.1f);
            float lim = sp.L * 0.42f; sp.R0 = Mathf.Min(sp.R0, lim); sp.R1 = Mathf.Min(sp.R1, lim);
            // piers
            float spacing = sp.Steel ? 3.4f : 2.7f;
            int np = Mathf.Max(1, Mathf.RoundToInt(wspan / spacing) - 1);
            for (int k = 1; k <= np; k++) sp.Piers.Add(sp.W0 + wspan * k / (np + 1f));
            // bbox + owner chunk
            sp.Min = new Vector2(1e9f, 1e9f); sp.Max = new Vector2(-1e9f, -1e9f);
            foreach (var c in new[] { P(sp, 0f, -hw), P(sp, 0f, hw), P(sp, sp.L, -hw), P(sp, sp.L, hw) }) { sp.Min = new Vector2(Mathf.Min(sp.Min.x, c.x), Mathf.Min(sp.Min.y, c.y)); sp.Max = new Vector2(Mathf.Max(sp.Max.x, c.x), Mathf.Max(sp.Max.y, c.y)); }
            sp.Owner = Mathf.Clamp(Mathf.FloorToInt(ctr.y) / CH, 0, 9999) * ncx + Mathf.Clamp(Mathf.FloorToInt(ctr.x) / CH, 0, ncx - 1);
            _spans.Add(sp);
        }

        /// <summary>True when p lies on the roadway of another span (two bridges sharing a bank corner):
        /// parapets, fascia and truss stop there instead of cutting across the other deck.</summary>
        bool OnOtherDeck(Span me, Vector2 p)
        {
            foreach (var s in _spans)
            {
                if (s == me || p.x < s.Min.x || p.y < s.Min.y || p.x > s.Max.x || p.y > s.Max.y) continue;
                var e = p - s.O; float a = Vector2.Dot(e, s.A), c = Vector2.Dot(e, s.S);
                if (a > 0f && a < s.L && Mathf.Abs(c) < s.Hw - 0.05f) return true;
            }
            return false;
        }

        static Vector2 P(Span s, float a, float c) => s.O + s.A * a + s.S * c;

        static float Smooth01(float t) { t = Mathf.Clamp01(t); return t * t * (3f - 2f * t); }

        static float DeckY(Span s, float a)
        {
            float k = Mathf.Min(Smooth01(a / s.R0), Smooth01((s.L - a) / s.R1));
            float y0 = ROAD_Y + 0.004f;
            return y0 + (s.Hd - y0) * k + s.Arch * Mathf.Sin(Mathf.PI * Mathf.Clamp01(a / s.L)) * k;
        }

        /// <summary>Height of the bridge deck above road level at a tile-space point (0 off the bridges).
        /// Visual only: Crowd lifts walkers / vehicles by it, wrecks on a deck sit on it.</summary>
        public float DeckLift(Vector2 p)
        {
            for (int i = 0; i < _spans.Count; i++)
            {
                var s = _spans[i];
                if (p.x < s.Min.x || p.y < s.Min.y || p.x > s.Max.x || p.y > s.Max.y) continue;
                var e = p - s.O;
                float a = Vector2.Dot(e, s.A), c = Vector2.Dot(e, s.S);
                if (a < 0f || a > s.L || Mathf.Abs(c) > s.Hw + 0.05f) continue;
                return Mathf.Max(0f, DeckY(s, a) - ROAD_Y);
            }
            return 0f;
        }

        // ---------------- drawing ----------------
        Vector3 DW(Span s, float a, float c, float yo) => Geo.W(P(s, a, c), DeckY(s, a) + yo);

        /// <summary>Sloped prism following the deck between along a0..a1 and across c0..c1, from deck+y0 to deck+y1.</summary>
        void DStrip(Geo g, Span s, float a0, float a1, float c0, float c1, float y0, float y1, Color side, Color top, bool ends = false, Color? outer = null)
        {
            var t00 = DW(s, a0, c0, y1); var t10 = DW(s, a1, c0, y1); var t11 = DW(s, a1, c1, y1); var t01 = DW(s, a0, c1, y1);
            var b00 = DW(s, a0, c0, y0); var b10 = DW(s, a1, c0, y0); var b11 = DW(s, a1, c1, y0); var b01 = DW(s, a0, c1, y0);
            var sd = new Vector3(s.S.x, 0f, -s.S.y); var ad = new Vector3(s.A.x, 0f, -s.A.y);
            g.Face(t00, t10, t11, t01, top, Vector3.up);
            g.Face(b00, b10, t10, t00, c0 < 0f && outer.HasValue ? outer.Value : side, -sd);
            g.Face(b01, b11, t11, t01, c1 > 0f && outer.HasValue ? outer.Value : side, sd);
            if (ends)
            {
                g.Face(b00, b01, t01, t00, side, -ad);
                g.Face(b10, b11, t11, t10, side, ad);
            }
        }

        /// <summary>Flat paint / joint quad on the deck surface.</summary>
        void DQuad(Geo g, Span s, float a0, float a1, float c0, float c1, float yo, Color col)
        {
            g.QuadRaw(DW(s, a0, c0, yo), DW(s, a1, c0, yo), DW(s, a1, c1, yo), DW(s, a0, c1, yo), col, col, col, col);
        }

        void AddBridges(Geo g, int cx, int cy)
        {
            if (_spans.Count == 0) return;
            int ncx = (d.W + CH - 1) / CH;
            int me = cy * ncx + cx;
            foreach (var s in _spans) if (s.Owner == me) DrawSpan(g, s);
        }

        void DrawSpan(Geo g, Span s)
        {
            float st = lo ? 1.0f : 0.5f;
            int nseg = Mathf.Max(2, Mathf.CeilToInt(s.L / st));
            st = s.L / nseg;
            float hw = s.Hw, pt = s.Steel ? 0.05f : 0.08f;
            float fw = lo ? 0f : (hw >= 1.5f ? 0.26f : 0.16f);
            float hr = hw - pt - fw;                     // half roadway
            float roadW = hr * 2f;
            long salt = s.Salt;
            Color conc = Lerp(B_CONC, Hex("#6e6a5c"), Hf(salt, 5) * 0.6f);
            Color steel = Lerp(B_STEEL, Hex("#5a5648"), Hf(salt, 6) * 0.5f);

            // --- deck surface, footways, fascia ---
            for (int k = 0; k < nseg; k++)
            {
                float a0 = k * st, a1 = (k + 1) * st;
                var p00 = P(s, a0, -hr); var p10 = P(s, a1, -hr); var p11 = P(s, a1, hr); var p01 = P(s, a0, hr);
                if (lo)
                {
                    Color ca = AsphaltCol(P(s, a0, 0f), 0f, roadW), cb = AsphaltCol(P(s, a1, 0f), 0f, roadW);
                    g.QuadRaw(DW(s, a0, -hr, 0f), DW(s, a1, -hr, 0f), DW(s, a1, hr, 0f), DW(s, a0, hr, 0f), ca, cb, cb, ca);
                }
                else
                {
                    // two lanes so the wheel tracks / gutters vary across
                    g.QuadRaw(DW(s, a0, -hr, 0f), DW(s, a1, -hr, 0f), DW(s, a1, 0f, 0f), DW(s, a0, 0f, 0f),
                        AsphaltCol(p00, -1f, roadW), AsphaltCol(p10, -1f, roadW), AsphaltCol(P(s, a1, 0f), 0f, roadW), AsphaltCol(P(s, a0, 0f), 0f, roadW));
                    g.QuadRaw(DW(s, a0, 0f, 0f), DW(s, a1, 0f, 0f), DW(s, a1, hr, 0f), DW(s, a0, hr, 0f),
                        AsphaltCol(P(s, a0, 0f), 0f, roadW), AsphaltCol(P(s, a1, 0f), 0f, roadW), AsphaltCol(p11, 1f, roadW), AsphaltCol(p01, 1f, roadW));
                    // footways (raised, worn concrete, moss creeping from the parapet)
                    for (int sd = -1; sd <= 1; sd += 2)
                    {
                        float c0 = sd < 0 ? -hr - fw : hr, c1 = sd < 0 ? -hr : hr + fw;
                        var pm = P(s, (a0 + a1) * 0.5f, (c0 + c1) * 0.5f);
                        Color wc = Mul(WalkCol(pm, 0, 0), 0.82f);
                        if (Noise(pm.x, pm.y, 3f, 401) > 0.62f) wc = Lerp(wc, B_MOSS_B, 0.5f);
                        DStrip(g, s, a0, a1, c0, c1, -0.01f, 0.03f, CURB, wc);
                    }
                }
                // fascia (the slab edge, stained below)
                float fy0 = s.Steel ? -0.19f : -0.16f;
                for (int sd = -1; sd <= 1; sd += 2)
                {
                    float c = sd * (hw + 0.01f);
                    if (OnOtherDeck(s, P(s, (a0 + a1) * 0.5f, c))) continue;
                    var sdv = new Vector3(s.S.x * sd, 0f, -s.S.y * sd);
                    Color fa = FasciaCol(s, P(s, a0, c), steel, conc), fb = FasciaCol(s, P(s, a1, c), steel, conc);
                    var n = FN(DW(s, a0, c, fy0), DW(s, a1, c, fy0), DW(s, a1, c, 0.02f), sdv);
                    Color fa2 = Geo.Shade(fa, n), fb2 = Geo.Shade(fb, n);
                    Color sa = Geo.Shade(Lerp(fa, s.Steel ? B_RUST_DARK : B_CONC_STAIN, 0.55f), n), sb = Geo.Shade(Lerp(fb, s.Steel ? B_RUST_DARK : B_CONC_STAIN, 0.55f), n);
                    if (sd < 0) g.QuadRaw(DW(s, a0, c, fy0), DW(s, a1, c, fy0), DW(s, a1, c, 0.02f), DW(s, a0, c, 0.02f), sa, sb, fb2, fa2);
                    else g.QuadRaw(DW(s, a1, c, fy0), DW(s, a0, c, fy0), DW(s, a0, c, 0.02f), DW(s, a1, c, 0.02f), sb, sa, fa2, fb2);
                }
            }

            // --- parapets / railings ---
            float pa0 = 0.15f, pa1 = s.L - 0.15f;
            int gapSeg = -1;
            if (!lo && Hf(salt, 21) < 0.55f) gapSeg = Mathf.FloorToInt(Mathf.Lerp(s.W0, s.W1, 0.25f + Hf(salt, 22) * 0.5f) / st);
            int gapSide = Hf(salt, 23) < 0.5f ? -1 : 1;
            for (int k = 0; k < nseg; k++)
            {
                float a0 = Mathf.Max(pa0, k * st), a1 = Mathf.Min(pa1, (k + 1) * st);
                if (a1 <= a0) continue;
                for (int sd = -1; sd <= 1; sd += 2)
                {
                    float c0 = sd < 0 ? -hw : hw - pt, c1 = sd < 0 ? -hw + pt : hw;
                    bool gap = k == gapSeg && sd == gapSide;
                    if (OnOtherDeck(s, P(s, (a0 + a1) * 0.5f, (c0 + c1) * 0.5f))) continue;
                    if (s.Steel)
                    {
                        // steel: low curb under the truss (the truss is the parapet)
                        if (!gap) DStrip(g, s, a0, a1, c0, c1, -0.005f, 0.05f, steel, Mul(steel, 1.05f));
                        continue;
                    }
                    if (gap)
                    {
                        // knocked-out parapet section: stubs and rubble on the footway
                        DStrip(g, s, a0, a0 + 0.08f, c0, c1, -0.005f, 0.09f, conc, conc, true);
                        DStrip(g, s, a1 - 0.06f, a1, c0, c1, -0.005f, 0.05f, conc, conc, true);
                        if (!lo) for (int r = 0; r < 3; r++)
                        {
                            float aa = Mathf.Lerp(a0, a1, 0.2f + 0.3f * r), cc = sd * (hw - pt - 0.12f - Hf(salt, 30 + r) * 0.15f);
                            g.BoxAt(P(s, aa, cc), s.A, 0.04f + Hf(salt, 33 + r) * 0.03f, 0.035f, DeckY(s, aa) + 0.02f, DeckY(s, aa) + 0.06f, conc, conc);
                        }
                        continue;
                    }
                    Color top = Lerp(conc, B_MOSS_B, Noise(P(s, a0, c0).x, P(s, a0, c0).y, 4f, 403) > 0.6f ? 0.45f : 0f);
                    DStrip(g, s, a0, a1, c0, c1, -0.005f, 0.14f, conc, Mul(top, 1.06f), false, Lerp(conc, B_CONC_STAIN, 0.25f));
                }
            }
            if (!s.Steel && !lo)
            {
                // pilasters every ~1.2 tiles and taller end posts at the bridge heads
                int np = Mathf.Max(2, Mathf.RoundToInt((pa1 - pa0) / 1.2f));
                for (int k = 0; k <= np; k++)
                {
                    float a = Mathf.Lerp(pa0 + 0.06f, pa1 - 0.06f, k / (float)np);
                    bool end = k == 0 || k == np;
                    for (int sd = -1; sd <= 1; sd += 2)
                    {
                        if (gapSeg >= 0 && sd == gapSide && Mathf.FloorToInt(a / st) == gapSeg) continue;
                        float hgt = end ? 0.3f : 0.17f, half = end ? 0.09f : 0.06f;
                        var c = P(s, a, sd * (hw - pt * 0.5f));
                        if (OnOtherDeck(s, c)) continue;
                        float y = DeckY(s, a);
                        g.Box(new ORect(c, s.A, half, pt * 0.5f + (end ? 0.05f : 0.02f)).Quad(), y - 0.01f, y + hgt, Mul(conc, 0.96f), Mul(conc, 1.1f));
                    }
                }
                // lamp posts on alternating sides, some bent or snapped
                int nl = Mathf.Max(1, Mathf.RoundToInt((s.W1 - s.W0) / 3.6f));
                for (int k = 0; k < nl; k++)
                {
                    float a = Mathf.Lerp(s.W0, s.W1, (k + 0.5f) / nl);
                    int sd = (k & 1) == 0 ? 1 : -1;
                    var b = P(s, a, sd * (hw - pt - 0.05f));
                    float y = DeckY(s, a);
                    float hs = Hf(salt, 60 + k);
                    var top = Geo.W(b, y + 0.8f);
                    if (hs < 0.25f) top = Geo.W(b + s.S * sd * 0.35f + s.A * 0.2f, y + 0.62f);              // bent over the rail
                    else if (hs < 0.4f) top = Geo.W(b, y + 0.38f);                                          // snapped
                    g.Beam(Geo.W(b, y), top, 0.018f, POLE);
                    if (hs >= 0.4f) g.Beam(top, top + new Vector3(-s.S.x * sd * 0.28f, -0.02f, s.S.y * sd * 0.28f), 0.014f, POLE);
                }
            }
            if (s.Steel) DrawTruss(g, s, steel, gapSeg, gapSide, st);

            // --- abutments: retaining walls on the banks, the seat facing the water, end posts ---
            for (int e = 0; e < 2; e++)
            {
                float aw = e == 0 ? s.W0 : s.W1;                         // waterline
                float aFrom = e == 0 ? 0f : aw, aTo = e == 0 ? aw + 0.15f : s.L;
                int n = Mathf.Max(1, Mathf.CeilToInt((aTo - aFrom) / (lo ? 1f : 0.5f)));
                for (int sd = -1; sd <= 1; sd += 2)
                {
                    float c = sd * (hw + 0.06f);
                    var sdv = new Vector3(s.S.x * sd, 0f, -s.S.y * sd);
                    for (int k = 0; k < n; k++)
                    {
                        float a0 = Mathf.Lerp(aFrom, aTo, k / (float)n), a1 = Mathf.Lerp(aFrom, aTo, (k + 1) / (float)n);
                        if (OnOtherDeck(s, P(s, (a0 + a1) * 0.5f, c))) continue;
                        var v0 = Geo.W(P(s, a0, c), WATER_Y - 0.06f); var v1 = Geo.W(P(s, a1, c), WATER_Y - 0.06f);
                        var v2 = DW(s, a1, c, -0.02f); var v3 = DW(s, a0, c, -0.02f);
                        if (sd < 0) g.Face(v0, v1, v2, v3, Mul(conc, 0.9f), sdv); else g.Face(v1, v0, v3, v2, Mul(conc, 0.9f), sdv);
                    }
                }
                // seat wall across the full width at the waterline
                var sc = P(s, aw + (e == 0 ? 0.08f : -0.08f), 0f);
                float yTop = DeckY(s, aw) - 0.1f;
                g.Box(new ORect(sc, s.A, 0.12f, hw + 0.08f).Quad(), WATER_Y - 0.12f, yTop, Mul(conc, 0.85f), conc, 0.06f);
                if (!lo)
                {
                    // waterline foam along the seat
                    FoamRing(g, new ORect(sc, s.A, 0.12f, hw + 0.08f), 0.07f);
                }
            }

            // --- piers ---
            foreach (float ap in s.Piers)
            {
                float yb = DeckY(s, ap) + (s.Steel ? -0.19f : -0.16f);
                if (s.Steel)
                {
                    // twin round concrete columns just outside the deck edges under a steel cap beam
                    for (int sd = -1; sd <= 1; sd += 2)
                    {
                        var c = P(s, ap, sd * (hw + 0.06f));
                        g.Cyl(c, 0.15f, WATER_Y - 0.15f, yb - 0.06f, Mul(B_CONC, 0.9f), B_CONC, lo ? 6 : 8);
                        if (!lo) { g.Cyl(c, 0.16f, WATER_Y - 0.01f, WATER_Y + 0.05f, B_CONC_STAIN, B_CONC_STAIN, 8); FoamDisc(g, c, 0.16f); }
                    }
                    g.Box(new ORect(P(s, ap, 0f), s.S, hw + 0.22f, 0.1f).Quad(), yb - 0.08f, yb + 0.005f, Lerp(steel, B_RUST, 0.35f), steel);
                }
                else
                {
                    // wall pier with pointed cutwaters, wider than the deck so it shows from above
                    // aligned with the current (like a real skew bridge), long enough to carry the deck width
                    var F = FlowDir(P(s, ap, 0f));
                    float ph = Mathf.Min((hw + 0.12f) / Mathf.Max(0.35f, Mathf.Abs(Vector2.Dot(F, s.S))), hw * 2.6f + 0.3f);
                    var r = new ORect(P(s, ap, 0f), F, ph, 0.13f);
                    g.Box(r.Quad(), WATER_Y - 0.15f, yb - 0.07f, Mul(conc, 0.88f), conc, 0.08f);
                    for (int sd = -1; sd <= 1; sd += 2)
                    {
                        var nose = new ORect(r.C + F * (sd * ph), (F + r.V).normalized, 0.092f, 0.092f);
                        g.Box(nose.Quad(), WATER_Y - 0.15f, yb - 0.07f, Mul(conc, 0.85f), conc, 0.08f);
                    }
                    g.Box(new ORect(P(s, ap, 0f), s.S, hw + 0.06f, 0.16f).Quad(), yb - 0.08f, yb + 0.005f, conc, conc);
                    if (!lo) FoamRing(g, new ORect(r.C, r.U, ph + 0.1f, r.Hv), 0.07f);
                }
            }

            // --- shadow on the water under the deck ---
            if (g.S != null)
            {
                int n = Mathf.Max(1, Mathf.CeilToInt((s.W1 - s.W0 + 0.6f) / (lo ? 1f : 0.5f)));
                var col = new Color32(22, 24, 18, 150);
                for (int k = 0; k < n; k++)
                {
                    float a0 = Mathf.Lerp(s.W0 - 0.3f, s.W1 + 0.3f, k / (float)n), a1 = Mathf.Lerp(s.W0 - 0.3f, s.W1 + 0.3f, (k + 1) / (float)n);
                    Vector2 o0 = SunOff(DeckY(s, a0) + 0.1f - WATER_Y), o1 = SunOff(DeckY(s, a1) + 0.1f - WATER_Y);
                    float w = hw + 0.05f;
                    int i0 = g.S.V.Count;
                    g.S.V.Add(Geo.W(P(s, a0, -w) + o0, WATER_Y + 0.012f)); g.S.V.Add(Geo.W(P(s, a1, -w) + o1, WATER_Y + 0.012f));
                    g.S.V.Add(Geo.W(P(s, a1, w) + o1, WATER_Y + 0.012f)); g.S.V.Add(Geo.W(P(s, a0, w) + o0, WATER_Y + 0.012f));
                    for (int t = 0; t < 4; t++) g.S.C.Add(col);
                    g.S.T.Add(i0); g.S.T.Add(i0 + 1); g.S.T.Add(i0 + 2); g.S.T.Add(i0); g.S.T.Add(i0 + 2); g.S.T.Add(i0 + 3);
                }
            }

            if (lo) return;

            // --- paint, joints, wear ---
            for (int k = 0; k < nseg; k++)
            {
                float a0 = k * st, a1 = (k + 1) * st;
                float am = (a0 + a1) * 0.5f;
                bool fadedSeg = Hf(salt * 3 + k, 77) < 0.2f;
                // centre: double yellow on two-lane decks, dashed white lanes on wide ones, edge lines
                if (roadW >= 1.6f)
                {
                    Color y = fadedSeg ? Lerp(PAINT_Y, ASPH, 0.5f) : PAINT_Y;
                    DQuad(g, s, a0, a1, -0.085f, -0.035f, 0.004f, y);
                    DQuad(g, s, a0, a1, 0.035f, 0.085f, 0.004f, y);
                }
                if (roadW >= 3.4f && (k % 4) < 2 && Hf(salt * 5 + k, 78) > 0.2f)
                    for (int sd = -1; sd <= 1; sd += 2) DQuad(g, s, a0, a1, sd * hr * 0.5f - 0.03f, sd * hr * 0.5f + 0.03f, 0.004f, PAINT);
                if (!fadedSeg)
                    for (int sd = -1; sd <= 1; sd += 2) DQuad(g, s, a0, a1, sd * (hr - 0.1f) - 0.022f, sd * (hr - 0.1f) + 0.022f, 0.004f, Lerp(PAINT, ASPH, 0.3f));
            }
            // expansion joints: at both waterlines and over every pier
            var joints = new List<float>(s.Piers) { s.W0 - 0.1f, s.W1 + 0.1f };
            foreach (float aj in joints)
            {
                DQuad(g, s, aj - 0.018f, aj + 0.018f, -hr, hr, 0.006f, B_JOINT);
                DQuad(g, s, aj - 0.05f, aj - 0.018f, -hr, hr, 0.005f, B_JOINT_PLATE);
                DQuad(g, s, aj + 0.018f, aj + 0.05f, -hr, hr, 0.005f, B_JOINT_PLATE);
            }
            // cracks, potholes, weeds, debris (mid-span where the deck is nearly level)
            int nd = Mathf.Max(2, Mathf.RoundToInt((s.W1 - s.W0) * roadW / 3f));
            for (int k = 0; k < nd; k++)
            {
                long hs = salt * 31 + k * 7919;
                float a = Mathf.Lerp(s.W0 + 0.6f, s.W1 - 0.6f, Hf(hs, 1));
                float c = (Hf(hs, 2) * 2f - 1f) * (hr - 0.15f);
                var p = P(s, a, c);
                float y = DeckY(s, a);
                float r = Hf(hs, 3);
                if (r < 0.45f) Crack(g, p, hs, y + 0.003f);
                else if (r < 0.62f) g.Disc(p, 0.06f + Hf(hs, 4) * 0.07f, y + 0.005f, Hex("#24221d"), PATCH, 7, r * 9f);
                else if (r < 0.8f) Tuft(g, P(s, a, Mathf.Sign(c + 0.001f) * (hr - 0.06f)), (int)(hs & 0xffff), y);
                else Debris(g, p, hs, y);
            }
            // an abandoned car on about half the bridges
            if (Hf(salt, 90) < 0.5f && roadW >= 1.2f)
            {
                float a = Mathf.Lerp(s.W0 + 1f, s.W1 - 1f, 0.3f + Hf(salt, 91) * 0.4f);
                float c = (Hf(salt, 92) < 0.5f ? -1f : 1f) * Mathf.Min(hr * 0.5f, 0.6f);
                float ang = Mathf.Atan2(s.A.y, s.A.x) + (Hf(salt, 93) < 0.5f ? 0f : Mathf.PI) + (Hf(salt, 94) - 0.5f) * 0.9f;
                AddCar(g, P(s, a, c), ang, (int)(salt & 0x7fffffff) + 17, DeckY(s, a));
            }
        }

        Color FasciaCol(Span s, Vector2 p, Color steel, Color conc)
        {
            float n = Noise(p.x, p.y, 0.9f, 411), n2 = Noise(p.x, p.y, 3f, 413);
            if (s.Steel) return Lerp(Lerp(B_STEEL_DARK, steel, 0.6f), B_RUST, Mathf.Clamp01(n * 1.6f - 0.5f) * 0.8f);
            return Lerp(Mul(conc, 0.92f), B_CONC_STAIN, Mathf.Clamp01(n * 1.5f - 0.55f) * 0.6f + n2 * 0.1f);
        }

        static Vector3 FN(Vector3 a, Vector3 b, Vector3 c, Vector3 outward)
        {
            var n = Vector3.Cross(b - a, c - a); float m = n.magnitude;
            if (m < 1e-8f) return Vector3.up;
            n /= m; return Vector3.Dot(n, outward) < 0f ? -n : n;
        }

        /// <summary>Direction the river runs at p (the tile axis with the longer stretch of water).</summary>
        Vector2 FlowDir(Vector2 p)
        {
            float rx = 0f, ry = 0f;
            for (int sgn = -1; sgn <= 1; sgn += 2)
            {
                for (float t = 0.5f; t < 12f; t += 0.5f) { var q = p + new Vector2(t * sgn, 0f); if (!WetT(TileAtF(q.x, q.y))) break; rx += 0.5f; }
                for (float t = 0.5f; t < 12f; t += 0.5f) { var q = p + new Vector2(0f, t * sgn); if (!WetT(TileAtF(q.x, q.y))) break; ry += 0.5f; }
            }
            return rx >= ry ? new Vector2(1f, 0f) : new Vector2(0f, 1f);
        }

        static Vector2 SunOff(float h) => new Vector2(-Geo.SUN.x / Geo.SUN.y * h, Geo.SUN.z / Geo.SUN.y * h);

        /// <summary>Pony Warren truss along both deck edges over the water span (+ end posts on the abutments).</summary>
        void DrawTruss(Geo g, Span s, Color steel, int gapSeg, int gapSide, float st)
        {
            float t0 = Mathf.Max(0.3f, s.W0 - 0.35f), t1 = Mathf.Min(s.L - 0.3f, s.W1 + 0.35f);
            int np = Mathf.Max(3, Mathf.RoundToInt((t1 - t0) / 0.85f));
            float ht = 0.4f, bw = lo ? 0.03f : 0.022f;
            long salt = s.Salt;
            for (int sd = -1; sd <= 1; sd += 2)
            {
                float c = sd * (s.Hw - 0.03f);
                Vector3 B(float a) => DW(s, a, c, 0.04f);
                Vector3 T(float a) => DW(s, a, c, ht);
                int bent = Hf(salt, 120 + sd) < 0.5f ? 1 + (int)(Hf(salt, 122 + sd) * (np - 2)) : -1;   // a panel knocked in
                for (int k = 0; k < np; k++)
                {
                    float a0 = Mathf.Lerp(t0, t1, k / (float)np), a1 = Mathf.Lerp(t0, t1, (k + 1) / (float)np);
                    if (OnOtherDeck(s, P(s, (a0 + a1) * 0.5f, c))) continue;
                    Color col = Lerp(steel, B_RUST, Hf(salt * 7 + k, 130 + sd) * 0.55f);
                    bool first = k == 0, last = k == np - 1;
                    // top chord (between panel tops, end panels slope down as inclined end posts)
                    Vector3 ta = first ? B(a0) : T(a0), tb = last ? B(a1) : T(a1);
                    if (k == bent) { ta += new Vector3(0f, -0.08f, 0f); tb += new Vector3(0f, -0.05f, 0f); }
                    g.Beam(ta, tb, bw * 1.25f, col);
                    if (lo) { if (!first && (k & 1) == 0) g.Beam(B(a0), T(a0), bw, col); continue; }
                    g.Beam(B(a0), B(a1), bw, Mul(col, 0.9f));                                 // bottom chord
                    if (!first) g.Beam(B(a0), T(a0), bw * 0.8f, col);                        // vertical
                    if (!first && !last && k != bent)                                        // Warren diagonal
                    {
                        if ((k & 1) == 0) g.Beam(B(a0), T(a1), bw * 0.8f, col); else g.Beam(T(a0), B(a1), bw * 0.8f, col);
                    }
                    // rivet plates (gussets) at the panel points
                    if (!first) g.BoxAt(P(s, a0, c), s.A, 0.035f, 0.03f, DeckY(s, a0) + ht - 0.03f, DeckY(s, a0) + ht + 0.03f, B_STEEL_DARK, steel);
                }
            }
        }

        static void FoamRing(Geo g, ORect r, float w)
        {
            var o = r.Inset(-w).Quad(); var i = r.Quad();
            float y = WATER_Y + 0.004f;
            for (int k = 0; k < 4; k++)
            {
                int k2 = (k + 1) & 3;
                g.QuadRaw(Geo.W(i[k], y), Geo.W(i[k2], y), Geo.W(o[k2], y), Geo.W(o[k], y), B_FOAM_B, B_FOAM_B, B_WAKE, B_WAKE);
            }
            // short wake trailing downstream (along the long side of the pier)
            var dir = r.U; float len = r.Hu;
            var tip = r.C + dir * (len + 0.4f);
            var b0 = r.C + dir * len + r.V * r.Hv; var b1 = r.C + dir * len - r.V * r.Hv;
            g.QuadRaw(Geo.W(b0, y), Geo.W(b1, y), Geo.W(tip - r.V * 0.02f, y), Geo.W(tip + r.V * 0.02f, y), B_FOAM_B, B_FOAM_B, B_WAKE, B_WAKE);
        }

        static void FoamDisc(Geo g, Vector2 c, float r)
        {
            float y = WATER_Y + 0.004f;
            const int N = 8;
            for (int k = 0; k < N; k++)
            {
                float a0 = k * Mathf.PI * 2f / N, a1 = (k + 1) * Mathf.PI * 2f / N;
                var d0 = new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)); var d1 = new Vector2(Mathf.Cos(a1), Mathf.Sin(a1));
                g.QuadRaw(Geo.W(c + d0 * r, y), Geo.W(c + d1 * r, y), Geo.W(c + d1 * (r + 0.07f), y), Geo.W(c + d0 * (r + 0.07f), y), B_FOAM_B, B_FOAM_B, B_WAKE, B_WAKE);
            }
        }
    }
}
