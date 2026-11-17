// Ruins for MapGen (v0.16): shattered windows on many facades (more on damaged buildings) and a few
// collapsed buildings per map. Visual only: every choice is a deterministic hash of the lot index (Hf), never
// the game RNG; nothing here touches the sim. A collapsed lot keeps its LotInfo (footprint, floors, Top) so
// picking, the selection box and looting behave exactly as before; only its mesh changes, and LotH (visual
// roof height used by rooftop fires / crows) follows the rubble.
//   * ShatterPane: black hole, jagged glass shards in the frame edges, sometimes a curtain blowing out or a
//     plank nailed across, glass specks on the ground below, soot streak above (burned).
//   * collapse styles: 0 pancake (slab pile + 1-2 standing walls with empty frames), 1 half-collapsed (one side
//     sheared off, hanging floor slabs, dark interior, rubble spilling into the street), 2 gutted shell.
using System;
using System.Collections.Generic;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed partial class MapGen
    {
        const float DEG = 0.0174533f;
        static readonly Color DUST = Hex("#8c877b"), CONC_P = Hex("#8a867c"), CONC_M = Hex("#76726a"), CONC_K = Hex("#5c5952");
        static readonly Color SHARD = Hex("#8e9c9a"), SHARD_HI = Hex("#b2bebb"), SPECK = Hex("#aab4b0"), REBAR_C = Hex("#4a3226");
        static readonly Color PLASTER = Hex("#6e675c");
        static readonly Color[] CURTAIN = { Hex("#7a3a32"), Hex("#8a8068"), Hex("#4a5a66"), Hex("#6a6a4a"), Hex("#7a6a5a") };

        // lot -> collapse style (0 pancake, 1 half, 2 shell); recomputed with the camps (SetCamps hook)
        readonly Dictionary<int, int> _ruin = new Dictionary<int, int>();

        /// <summary>Collapsed lots and their style (preview / debug).</summary>
        public IEnumerable<KeyValuePair<int, int>> Ruins => _ruin;

        /// <summary>Picks ~1 in 25 eligible lots to draw collapsed. Never the player's bases or their claims,
        /// faction camps or their claims, landmarks or civic / special buildings. Called at the end of SetCamps.</summary>
        void PlanRuins(Game game)
        {
            foreach (var kv in _ruin) LotH[kv.Key] = _info[kv.Key].Top;   // restore the visual height
            _ruin.Clear();
            var keep = new HashSet<int>(_campLots);
            if (game?.S != null)
            {
                foreach (var b in game.AllBases())
                {
                    if (b.Lot >= 0) keep.Add(b.Lot);
                    if (b.Claim != null) foreach (int l in b.Claim) keep.Add(l);
                }
                if (game.S.Camps != null)
                    foreach (var c in game.S.Camps)
                    {
                        if (c.Lot >= 0) keep.Add(c.Lot);
                        if (c.Claim != null) foreach (int l in c.Claim) keep.Add(l);
                    }
            }
            for (int l = 0; l < _info.Length; l++)
            {
                var inf = _info[l];
                if (!inf.Valid || inf.Floors <= 0 || keep.Contains(l)) continue;
                if (inf.A != Arch.Apartment && inf.A != Arch.Office && inf.A != Arch.Shop && inf.A != Arch.House) continue;
                var L = d.Lots[l];
                if (!string.IsNullOrEmpty(L.Landmark) || L.Kind == "farmhouse" || inf.R.Short < 0.35f) continue;
                if (Hf(l, 9001) >= 0.04f) continue;
                float s = Hf(l, 9002);
                int style = inf.A == Arch.House ? (s < 0.55f ? 0 : 2)
                          : inf.Floors <= 1 ? (s < 0.6f ? 0 : 2)
                          : s < 0.38f ? 0 : s < 0.72f ? 1 : 2;
                _ruin[l] = style;
                float H = inf.WallTop;
                LotH[l] = style == 0 ? Mathf.Clamp(0.12f + 0.07f * inf.Floors, 0.18f, 1.0f) : style == 1 ? H : H * 0.9f;
            }
        }

        bool RuinLot(Geo g, int l, LotInfo info)
        {
            if (!_ruin.TryGetValue(l, out int style)) return false;
            var R = info.R;
            if (Hf(l, 9003) < 0.5f) R = new ORect(R.C, -R.U, R.Hu, R.Hv);
            float H = info.WallTop;
            if (info.A == Arch.House) { info.Wall = Lerp(info.Wall, CONC_M, 0.25f); }
            if (lo)
            {
                if (style == 0) g.Frustum(R.Inset(-0.1f).Quad(), 0f, R.Inset(R.Short * 0.5f).Quad(), Mathf.Min(0.5f, 0.15f + H * 0.12f), Lerp(info.Wall, DUST, 0.5f), DUST);
                else if (style == 1) { g.Box(R.Sub(-1f, 0f, -1f, 1f).Quad(), 0f, H, info.Wall, INTERIOR, 0.2f); g.Frustum(R.Sub(0f, 1.3f, -1.1f, 1.1f).Quad(), 0f, R.Sub(0.1f, 0.6f, -0.5f, 0.5f).Quad(), H * 0.2f, DUST, DUST); }
                else g.Box(R.Quad(), 0f, H * 0.9f, info.Wall, INTERIOR, 0.2f);
                return true;
            }
            if (info.A != Arch.House) Yard(g, l, info.Lot.Inset(0.06f), Hex("#6c675a"));
            // dust apron: pale grey spill around the footprint
            var ap = R.Inset(-0.26f);
            g.Frustum(ap.Quad(), 0f, R.Inset(-0.04f).Quad(), 0.025f, Lerp(DUST, RUBBLE_D, 0.55f), Lerp(DUST, RUBBLE_D, 0.4f));
            switch (style)
            {
                case 0: Pancake(g, l, R, info); break;
                case 1: HalfCollapse(g, l, R, info); break;
                default: GuttedShell(g, l, R, info); break;
            }
            return true;
        }

        // ---------------- helpers ----------------
        static Vector3 V3(Vector2 d) => new Vector3(d.x, 0f, -d.y);

        /// <summary>Tilted thick slab: centre c, in-plane unit axes ax / ay (world), half sizes, thickness t.</summary>
        void Slab(Geo g, Vector3 c, Vector3 ax, Vector3 ay, float hx, float hy, float t, Color top, Color side)
        {
            var n = Vector3.Cross(ax, ay).normalized; if (n.y < 0f) n = -n;
            ax = ax * hx; ay = ay * hy; var nt = n * (t * 0.5f);
            var p = new Vector3[8];
            for (int i = 0; i < 4; i++)
            {
                float sx = (i == 0 || i == 3) ? -1f : 1f, sy = i < 2 ? -1f : 1f;
                p[i] = c + ax * sx + ay * sy - nt; p[i + 4] = c + ax * sx + ay * sy + nt;
            }
            g.Face(p[4], p[5], p[6], p[7], top, n);
            for (int i = 0; i < 4; i++)
            {
                int j = (i + 1) & 3;
                g.Face(p[i], p[j], p[j + 4], p[i + 4], side, (p[i] + p[j + 4]) * 0.5f - c);
            }
        }

        /// <summary>Slab axes: dir in tile space, yaw jitter, pitch (tilts dir up/down), roll (tilts the cross axis).</summary>
        static void SlabAxes(Vector2 dir, float yaw, float pitch, float roll, out Vector3 ax, out Vector3 ay)
        {
            float cy = Mathf.Cos(yaw), sy = Mathf.Sin(yaw);
            var d2 = new Vector2(dir.x * cy - dir.y * sy, dir.x * sy + dir.y * cy);
            var p2 = new Vector2(-d2.y, d2.x);
            ax = (V3(d2) * Mathf.Cos(pitch) + Vector3.up * Mathf.Sin(pitch)).normalized;
            ay = V3(p2) * Mathf.Cos(roll) + Vector3.up * Mathf.Sin(roll);
            ay = (ay - ax * Vector3.Dot(ay, ax)).normalized;
        }

        void Bricks(Geo g, int l, ORect r, int n, int salt)
        {
            for (int k = 0; k < n; k++)
            {
                var p = r.P(Hf(l, salt + k) * 2f - 1f, Hf(l, salt + 40 + k) * 2f - 1f);
                float a = Hf(l, salt + 80 + k) * 6.28f;
                Color c = Lerp(BRICK, DUST, Hf(l, salt + 120 + k) * 0.35f);
                g.BoxAt(p, new Vector2(Mathf.Cos(a), Mathf.Sin(a)), 0.035f, 0.018f, 0f, 0.025f, c, Mul(c, 1.08f));
            }
        }

        void RebarAt(Geo g, int l, Vector3 b, Vector3 outDir, int n, int salt)
        {
            for (int k = 0; k < n; k++)
            {
                var s = b + new Vector3(Hf(l, salt + k) - 0.5f, 0f, Hf(l, salt + 10 + k) - 0.5f) * 0.18f;
                var m = s + outDir * (0.06f + Hf(l, salt + 20 + k) * 0.1f) + Vector3.up * (0.04f + Hf(l, salt + 30 + k) * 0.08f);
                var e = m + new Vector3(Hf(l, salt + 40 + k) - 0.5f, Hf(l, salt + 50 + k) * 0.6f - 0.35f, Hf(l, salt + 60 + k) - 0.5f) * 0.14f;
                g.Beam(s, m, 0.007f, REBAR_C);
                g.Beam(m, e, 0.006f, REBAR_C);
            }
        }

        /// <summary>Standing wall piece along a->b (outward o), thickness t, jagged top stepping from hA to hB,
        /// with empty window frames (dark both sides, trim inside) on each floor.</summary>
        void BrokenWall(Geo g, int l, int salt, Vector2 a, Vector2 b, Vector2 o, float hA, float hB, LotInfo info, int segs)
        {
            const float t = 0.065f;
            float len = (b - a).magnitude; if (len < 0.1f) return;
            var dir = (b - a) / len;
            Color inner = Lerp(PLASTER, info.Wall, 0.3f), cap = Lerp(CONC_P, info.Wall, 0.3f);
            for (int s = 0; s < segs; s++)
            {
                var p0 = Vector2.Lerp(a, b, (float)s / segs); var p1 = Vector2.Lerp(a, b, (float)(s + 1) / segs);
                float h = Mathf.Lerp(hA, hB, (s + 0.5f) / segs) * (0.85f + 0.3f * Hf(l * 13 + salt, s));
                h = Mathf.Max(0.12f, h);
                var q = new[] { p0, p1, p1 - o * t, p0 - o * t };
                // outer face, inner face (plaster), ends, broken top
                g.Wall(p0, p1, o, 0f, h, info.Wall, 0.25f);
                g.Wall(p1 - o * t, p0 - o * t, -o, 0f, h, inner, 0.2f, Lerp(inner, CONC_K, 0.3f));
                g.Wall(p0 - o * t, p0, -dir, 0f, h, CONC_M);
                g.Wall(p1, p1 - o * t, dir, 0f, h, CONC_M);
                g.Flat(q, h, Geo.Shade(cap, Vector3.up));
                // empty frames per floor
                var c = (p0 + p1) * 0.5f;
                float hw = Mathf.Min(0.15f, (p1 - p0).magnitude * 0.3f);
                for (int fl = 0; ; fl++)
                {
                    float y0 = 0.1f + fl * info.FloorH + 0.26f * (info.FloorH / FLOOR), y1 = y0 + 0.4f * (info.FloorH / FLOOR);
                    if (y1 > h - 0.06f) break;
                    Color tc = Lerp(TRIM, info.Wall, 0.45f);
                    Pane(g, c, dir, o, hw + 0.03f, y0 - 0.05f, y1 + 0.03f, tc, tc, 0.006f, false);
                    Pane(g, c, dir, o, hw, y0, y1, VOIDC, Mul(VOIDC, 1.3f), 0.011f, false);
                    Pane(g, c, -dir, -o, hw, y0, y1, VOIDC, Mul(VOIDC, 1.3f), t + 0.006f, false);
                    if (Hf(l * 17 + salt + s, fl + 3) < 0.35f) ShardsIn(g, l * 17 + salt + s, fl, c, dir, o, hw, y0, y1);
                }
                g.Shadow(q, h, 0.34f);
            }
        }

        // ---------------- chunky rubble pile ----------------
        /// <summary>Rubble heap over area r: a low dusty base with a plateau (top rect = r.Sub(ts0..ts1, tt0..tt1)),
        /// tilted concrete chunks and broken slab pieces sitting on its surface, a few bricks. Sizes scale with the area.</summary>
        void RubblePile(Geo g, int l, int salt, ORect r, float h, Color wall, float ts0 = -0.45f, float ts1 = 0.45f, float tt0 = -0.45f, float tt1 = 0.45f, int maxChunks = 48)
        {
            float area = 4f * r.Hu * r.Hv;
            float sc = Mathf.Clamp(Mathf.Sqrt(area) * 0.045f, 0.05f, 0.24f);
            h = Mathf.Max(0.06f, h);
            var top = r.Sub(ts0, ts1, tt0, tt1);
            Color baseC = Lerp(Lerp(wall, DUST, 0.55f), RUBBLE_D, 0.35f);
            g.Frustum(r.Quad(), 0f, top.Quad(), h * 0.75f, Mul(baseC, 0.92f), Lerp(baseC, DUST, 0.2f));
            float Prof(float s, float t)
            {
                float fs = s < ts0 ? (s + 1f) / Mathf.Max(0.05f, ts0 + 1f) : s > ts1 ? (1f - s) / Mathf.Max(0.05f, 1f - ts1) : 1f;
                float ft = t < tt0 ? (t + 1f) / Mathf.Max(0.05f, tt0 + 1f) : t > tt1 ? (1f - t) / Mathf.Max(0.05f, 1f - tt1) : 1f;
                return h * 0.75f * Mathf.Clamp01(Mathf.Min(fs, ft));
            }
            int n = Mathf.Clamp((int)(area * 3.5f), 8, maxChunks);
            for (int k = 0; k < n; k++)
            {
                int key = salt * 101 + k;
                float s = Hf(l, 20000 + key) * 2f - 1f, t = Hf(l, 30000 + key) * 2f - 1f;
                var p = r.P(s * 0.95f, t * 0.95f);
                float z = Hf(l, 40000 + key);
                bool slab = z < 0.3f;
                float sx = sc * (slab ? 1.4f + 1.4f * Hf(l, 50000 + key) : 0.45f + 0.8f * Hf(l, 50000 + key));
                float sy = sx * (0.45f + 0.5f * Hf(l, 60000 + key));
                float th = slab ? sc * 0.3f : sy * (0.6f + 0.6f * Hf(l, 70000 + key));
                float pitch = (Hf(l, 80000 + key) - 0.5f) * (slab ? 70f : 50f) * DEG;
                float roll = (Hf(l, 90000 + key) - 0.5f) * 40f * DEG;
                SlabAxes(r.U, Hf(l, 100000 + key) * 6.28f, pitch, roll, out var ax, out var ay);
                Color c = z < 0.3f ? Lerp(CONC_P, DUST, Hf(l, 110000 + key) * 0.5f)
                        : z < 0.55f ? Lerp(CONC_M, wall, 0.35f)
                        : z < 0.8f ? Lerp(wall, DUST, 0.3f)
                        : z < 0.88f ? Lerp(BRICK, DUST, 0.4f) : CONC_K;
                Slab(g, Geo.W(p, Prof(s, t) + th * 0.25f + Mathf.Abs(Mathf.Sin(pitch)) * sx * 0.3f), ax, ay, sx, sy, th, Lerp(c, DUST, 0.12f), Mul(c, 0.92f));
                if (slab && Hf(l, 120000 + key) < 0.35f) RebarAt(g, l, Geo.W(p, Prof(s, t) + 0.03f) + ax * sx, ax, 2, 130000 + key * 70);
            }
            Bricks(g, l, new ORect(r.C, r.U, r.Hu + 0.2f, r.Hv + 0.2f), Mathf.Clamp((int)(area * 1.5f), 4, 14), 140000 + salt * 200);
        }

        // ---------------- (a) pancake ----------------
        void Pancake(Geo g, int l, ORect R, LotInfo info)
        {
            float H = info.WallTop;
            float hm = Mathf.Clamp(0.12f + 0.07f * info.Floors, 0.18f, 1.0f);
            Color wall = info.Wall;
            // standing walls (behind the pile from the default camera): the facade facing away from it
            var cam = new Vector2(-0.966f, 0.259f);
            int back = 0; float bd = 9f;
            for (int f = 0; f < 4; f++) { FaceEdge(R, f, out _, out _, out var o); float dd = Vector2.Dot(o, cam); if (dd < bd) { bd = dd; back = f; } }
            float hTop = Mathf.Max(0.35f, H * (0.4f + Hf(l, 9012) * 0.25f));
            {
                FaceEdge(R, back, out var a, out var b, out var o);
                float s0 = 0.04f + Hf(l, 9010) * 0.2f, s1 = 0.55f + Hf(l, 9011) * 0.35f;
                int segs = Mathf.Clamp(Mathf.RoundToInt((b - a).magnitude * (s1 - s0) / 0.45f), 2, 10);
                bool rev = Hf(l, 9013) < 0.5f;
                BrokenWall(g, l, 1, Vector2.Lerp(a, b, s0), Vector2.Lerp(a, b, s1), o, rev ? hTop * 0.3f : hTop, rev ? hTop : hTop * 0.3f, info, segs);
                // a short corner return on one side
                if (Hf(l, 9014) < 0.7f)
                {
                    int sf = (back + (Hf(l, 9015) < 0.5f ? 1 : 3)) & 3;
                    FaceEdge(R, sf, out var a2, out var b2, out var o2);
                    bool atStart = sf == ((back + 1) & 3);
                    float rl = Mathf.Min(0.4f, 1.2f / Mathf.Max(0.5f, (b2 - a2).magnitude));
                    var e0 = atStart ? a2 : Vector2.Lerp(a2, b2, 1f - rl); var e1 = atStart ? Vector2.Lerp(a2, b2, rl) : b2;
                    BrokenWall(g, l, 2, e0, e1, o2, atStart ? hTop * 0.75f : hTop * 0.2f, atStart ? hTop * 0.2f : hTop * 0.75f, info, 2);
                }
            }
            // the pile: pancaked floors as broken slab pieces on a heap
            var pile = R.Inset(-0.12f);
            RubblePile(g, l, 1, pile, hm, wall, -0.55f, 0.55f, -0.5f, 0.5f, 44);
            // broken floor plates lying in layers across the top, gaps between them
            float cell = Mathf.Max(0.55f, Mathf.Sqrt(4f * R.Hu * R.Hv / 18f));
            int nu = Mathf.Max(1, Mathf.RoundToInt(R.Hu * 2f * 0.8f / cell)), nv = Mathf.Max(1, Mathf.RoundToInt(R.Hv * 2f * 0.8f / cell));
            for (int i = 0; i < nu; i++)
                for (int j = 0; j < nv; j++)
                {
                    int key = i * 31 + j;
                    if (Hf(l, 9500 + key) < 0.25f) continue;
                    float su = -0.8f + 1.6f * (i + 0.5f) / nu + (Hf(l, 9600 + key) - 0.5f) * 0.3f / nu;
                    float sv = -0.8f + 1.6f * (j + 0.5f) / nv + (Hf(l, 9700 + key) - 0.5f) * 0.3f / nv;
                    var c = R.P(su, sv);
                    float edge = Mathf.Max(Mathf.Abs(su), Mathf.Abs(sv));
                    float pitch = (Hf(l, 9800 + key) - 0.5f) * (12f + 40f * edge) * DEG;
                    float roll = (Hf(l, 9900 + key) - 0.5f) * (10f + 30f * edge) * DEG;
                    SlabAxes(R.U, (Hf(l, 10000 + key) - 0.5f) * 0.5f, pitch, roll, out var ax, out var ay);
                    float hx = R.Hu / nu * (0.75f + 0.3f * Hf(l, 10100 + key)), hy = R.Hv / nv * (0.75f + 0.3f * Hf(l, 10200 + key));
                    float y = hm * 0.75f * (1f - Mathf.Max(0f, edge - 0.55f) * 1.4f) + 0.03f + Hf(l, 10300 + key) * 0.05f;
                    Color top = info.A == Arch.House && Hf(l, 10400 + key) < 0.6f ? info.Roof : Lerp(CONC_P, DUST, Hf(l, 10500 + key) * 0.6f);
                    Slab(g, Geo.W(c, y), ax, ay, hx, hy, 0.06f, top, Mul(Lerp(CONC_M, wall, 0.3f), 0.9f));
                    if (Hf(l, 10600 + key) < 0.3f) RebarAt(g, l, Geo.W(c, y) + ax * hx, ax, 2, 10700 + key * 70);
                }
            g.Shadow(R.Quad(), hm, 0.26f);
        }

        // ---------------- (b) half collapsed ----------------
        void HalfCollapse(Geo g, int l, ORect R0, LotInfo info)
        {
            float H = info.WallTop;
            Color wall = info.Wall;
            // collapse toward the front (rubble into the street) or a side
            int cf = Hf(l, 9300) < 0.55f ? info.Front : (info.Front + (Hf(l, 9301) < 0.5f ? 1 : 3)) & 3;
            FaceEdge(R0, cf, out _, out _, out var cdir);
            float along = (cf & 1) == 1 ? R0.Hu : R0.Hv, across = (cf & 1) == 1 ? R0.Hv : R0.Hu;
            var R = new ORect(R0.C, cdir, along, across);
            float s = -0.15f + Hf(l, 9302) * 0.35f;
            var A = R.Sub(-1f, s, -1f, 1f);
            var door = new Vector2(d.Lots[l].DoorX + 0.5f, d.Lots[l].DoorY + 0.5f);
            var ia = info; ia.Front = FrontFace(A, door); ia.Dmg = Math.Max(2, info.Dmg);
            BlockMass(g, l, A, H, wall, ia, new[] { true, false, true, true }, 2);
            g.Shadow(A.Quad(), H + 0.17f, 0.36f);
            // cut face: dark interior, floor slabs hanging out at angles
            FaceEdge(A, 1, out var ca, out var cb, out var co);
            float clen = (cb - ca).magnitude; var cd = (cb - ca) / clen;
            g.Wall(ca + co * 0.004f, cb + co * 0.004f, co, 0f, H, INTERIOR, 0.2f, Mul(INTERIOR, 1.25f));
            // a couple of dark room partitions + doorway voids
            for (int k = 1; k < 3; k++)
            {
                var pp = Vector2.Lerp(ca, cb, k / 3f + (Hf(l, 9310 + k) - 0.5f) * 0.15f);
                g.Wall(pp - cd * 0.015f + co * 0.006f, pp + cd * 0.015f + co * 0.006f, co, 0f, H, Mul(PLASTER, 0.7f));
            }
            Color slabC = Lerp(CONC_P, wall, 0.2f);
            float fh = info.FloorH;
            for (int fl = 1; fl <= info.Floors; fl++)
            {
                float y = Mathf.Min(H, fl * fh + 0.1f);
                // slab edge band on the cut face
                g.Wall(ca + co * 0.008f, cb + co * 0.008f, co, y - 0.05f, y + 0.01f, Lerp(slabC, PALE, 0.3f));
                int pieces = 2 + (int)(Hf(l * 3 + fl, 9320) * 2f);
                for (int k = 0; k < pieces; k++)
                {
                    float t0 = (float)k / pieces, t1 = (float)(k + 1) / pieces;
                    float hv = Hf(l * 7 + fl, 9330 + k);
                    if (hv < 0.2f) continue;                                 // this piece fell
                    float L = 0.08f + hv * 0.4f;
                    float pitch = -(8f + Hf(l * 7 + fl, 9340 + k) * 45f) * DEG;
                    float roll = (Hf(l * 7 + fl, 9350 + k) - 0.5f) * 14f * DEG;
                    var mid = Vector2.Lerp(ca, cb, (t0 + t1) * 0.5f);
                    SlabAxes(co, 0f, pitch, roll, out var ax, out var ay);
                    var hinge = Geo.W(mid, y - 0.02f);
                    var c = hinge + ax * L;
                    float hw = clen * (t1 - t0) * 0.5f * (0.8f + 0.18f * hv);
                    Slab(g, c, ax, ay, L, hw, 0.05f, Lerp(slabC, DUST, 0.3f), Mul(slabC, 0.85f));
                    if (hv > 0.6f) RebarAt(g, l, c + ax * L, V3(co), 2, 9500 + fl * 40 + k * 7);
                }
            }
            // what is left of the sheared part: low stumps along the outer walls
            var B = R.Sub(s, 1f, -1f, 1f);
            float hb = Mathf.Min(0.6f, H * (0.08f + Hf(l, 9360) * 0.08f));
            for (int f = 0; f < 4; f++)
            {
                if (f == 3) continue;
                FaceEdge(B, f, out var a, out var b, out var o);
                float hA = f == 0 ? hb * 1.6f : f == 2 ? hb * 0.5f : hb, hB = f == 0 ? hb * 0.5f : f == 2 ? hb * 1.6f : hb * 0.6f;
                BrokenWall(g, l, 10 + f, a, Vector2.Lerp(a, b, 0.55f + 0.35f * Hf(l, 9361 + f)), o, hA, hB, info, 2);
            }
            // big fallen slabs leaning against the cut face
            int nl = 2 + (int)(Hf(l, 9370) * 2f);
            for (int k = 0; k < nl; k++)
            {
                float tt = (k + 0.5f) / nl + (Hf(l, 9371 + k) - 0.5f) * 0.2f;
                var foot = Vector2.Lerp(ca, cb, tt) + co * (0.35f + Hf(l, 9375 + k) * 0.35f);
                float topY = H * (0.25f + Hf(l, 9380 + k) * 0.35f);
                var topP = Geo.W(Vector2.Lerp(ca, cb, tt) + co * 0.04f, topY);
                var botP = Geo.W(foot, 0.02f);
                var ax = (topP - botP).normalized; float L = (topP - botP).magnitude * 0.5f;
                var ay = (V3(cd) - ax * Vector3.Dot(V3(cd), ax)).normalized;
                float hw = Mathf.Min(0.3f, clen * 0.18f) * (0.7f + 0.5f * Hf(l, 9385 + k));
                Slab(g, (topP + botP) * 0.5f, ax, ay, L, hw, 0.05f, Lerp(slabC, wall, 0.3f), CONC_M);
            }
            // rubble spilling out past the footprint, highest against the cut face
            var spill = new ORect(B.C + R.U * 0.35f, R.U, B.Hu + 0.4f, B.Hv + 0.15f);
            float ps = Mathf.Clamp((-B.Hu - 0.35f + 0.8f * B.Hu) / spill.Hu, -0.8f, 0.3f);
            RubblePile(g, l, 2, spill, Mathf.Clamp(H * 0.25f, 0.2f, 1.2f), wall, -0.95f, ps, -0.5f, 0.5f, 44);
        }

        // ---------------- (c) gutted shell ----------------
        void GuttedShell(Geo g, int l, ORect R, LotInfo info)
        {
            float H = info.WallTop * (info.A == Arch.House ? 1.0f : 0.95f);
            const float t = 0.07f;
            Color wall = Lerp(info.Wall, CHAR, 0.18f), inner = Lerp(INTERIOR, PLASTER, 0.35f);
            int gapF = (int)(Hf(l, 9800) * 4f);
            for (int f = 0; f < 4; f++)
            {
                FaceEdge(R, f, out var a, out var b, out var o);
                float len = (b - a).magnitude; var dir = (b - a) / len;
                int n = Mathf.Clamp(Mathf.RoundToInt(len / (info.A == Arch.Office ? 0.42f : 0.62f)), 2, 12);
                for (int s = 0; s < n; s++)
                {
                    var p0 = Vector2.Lerp(a, b, (float)s / n); var p1 = Vector2.Lerp(a, b, (float)(s + 1) / n);
                    bool corner = s == 0 || s == n - 1;
                    float hs = H * (corner ? 1f : 0.86f + 0.14f * Hf(l * 11 + f, s));
                    if (f == gapF && !corner) hs = H * (0.35f + 0.5f * Mathf.Abs((s + 0.5f) / n - 0.5f) * 2f * Hf(l, 9801));
                    var q = new[] { p0, p1, p1 - o * t, p0 - o * t };
                    g.Wall(p0, p1, o, 0f, hs, wall, 0.3f);
                    g.Wall(p1 - o * t, p0 - o * t, -o, 0f, hs, inner, 0.2f, Lerp(inner, CHAR, 0.5f));
                    g.Flat(q, hs, Geo.Shade(Lerp(CONC_P, wall, 0.4f), Vector3.up));
                    // window holes (both faces), burned soot above some
                    var c = (p0 + p1) * 0.5f; float hw = Mathf.Min(0.16f, (p1 - p0).magnitude * 0.3f);
                    for (int fl = 0; ; fl++)
                    {
                        float y0 = 0.1f + fl * info.FloorH + 0.26f * (info.FloorH / FLOOR), y1 = y0 + 0.42f * (info.FloorH / FLOOR);
                        if (y1 > hs - 0.05f) break;
                        if (fl == 0 && f == info.Front && s == n / 2) { Pane(g, c, dir, o, hw + 0.04f, 0.0f, y1, VOIDC, Mul(VOIDC, 1.3f), 0.011f, false); continue; }
                        Pane(g, c, dir, o, hw, y0, y1, VOIDC, Mul(VOIDC, 1.3f), 0.011f, false);
                        Pane(g, c, -dir, -o, hw, y0, y1, VOIDC, Mul(VOIDC, 1.3f), t + 0.006f, false);
                        if (Hf(l * 19 + f * 5 + s, fl + 40) < 0.4f)
                            Soot(g, c, dir, o, hw, y1, Mathf.Min(hs - 0.02f, y1 + info.FloorH * 0.75f), wall);
                    }
                    g.Shadow(q, hs, 0.34f);
                }
            }
            var ri = R.Inset(t);
            g.Flat(ri.Quad(), 0.03f, Lerp(INTERIOR, RUBBLE_D, 0.45f));
            // remaining floor joists / steel beams spanning the void, one fallen diagonal
            float fh = info.FloorH;
            for (int fl = 1; fl < info.Floors; fl++)
            {
                float y = fl * fh + 0.1f;
                if (y > H - 0.1f) break;
                int nb = 1 + (int)(Hf(l, 9810 + fl) * 2f);
                for (int k = 0; k < nb; k++)
                {
                    float tt = -0.7f + 1.4f * Hf(l, 9820 + fl * 4 + k);
                    g.Beam(Geo.W(ri.P(-1f, tt), y), Geo.W(ri.P(1f, tt), y - (Hf(l, 9830 + k) < 0.3f ? 0.1f : 0f)), 0.018f, Hex("#3e3832"));
                }
            }
            if (info.Floors > 1)
            {
                // a floor slab hanging from one wall down into the pit
                SlabAxes(R.V, (Hf(l, 9840) - 0.5f) * 0.3f, -(35f + Hf(l, 9841) * 25f) * DEG, 0f, out var ax, out var ay);
                var hinge = Geo.W(ri.P(-0.2f + Hf(l, 9842) * 0.4f, -1f), fh + 0.1f);
                float L = Mathf.Min(ri.Hv * 0.8f, fh * 1.1f);
                Slab(g, hinge + ax * L, ax, ay, L, Mathf.Min(0.35f, ri.Hu * 0.5f), 0.045f, Lerp(CONC_P, CHAR, 0.3f), CONC_K);
            }
            float ph = Mathf.Clamp(H * 0.12f, 0.1f, 0.5f);
            int np = ri.Short > 1.2f ? 2 : 1;
            for (int k = 0; k < np; k++)
            {
                var pr = ri.Sub(-0.95f + 0.9f * Hf(l, 9850 + k), 0.05f + 0.9f * Hf(l, 9855 + k), -0.95f + 0.7f * Hf(l, 9860 + k), 0.25f + 0.7f * Hf(l, 9865 + k));
                RubblePile(g, l, 10 + k, pr, ph * (0.6f + 0.6f * Hf(l, 9870 + k)), Lerp(info.Wall, CHAR, 0.3f), -0.4f, 0.4f, -0.4f, 0.4f, 30);
            }
            RubbleChunks(g, l + 3333, R.Inset(-0.05f), 6, info.Wall, 0.1f);
        }

        // ---------------- shattered windows ----------------
        /// <summary>Hook from Facade / HouseWindows: replaces some panes by a shattered window. Returns true when drawn.
        /// h is the pane's own hash (boarded windows h >= boardFrom stay boarded).</summary>
        bool ShatterPane(Geo g, int key, int fl, int k, Vector2 c, Vector2 dir, Vector2 o, float hw, float y0, float y1, LotInfo info, float h, float boardFrom)
        {
            if (h >= boardFrom) return false;
            float s = Hf(key * 977 + fl * 31, k * 13 + 5);
            float p = info.Dmg <= 0 ? 0.1f : info.Dmg == 1 ? 0.22f : info.Dmg == 2 ? 0.38f : 0.55f;
            if (info.A == Arch.Office) p *= 0.6f;                         // ribbon glazing: many small bays
            if (s >= p) return false;
            int salt = key * 53 + fl * 11 + k;
            // black hole
            Pane(g, c, dir, o, hw, y0, y1, VOIDC, Mul(VOIDC, 1.25f), 0.011f, false);
            float r = Hf(salt, 1);
            if (r < 0.16f) Curtain(g, salt, c, dir, o, hw, y0, y1);
            else if (r < 0.28f)
            {
                // a plank nailed across
                float u0 = -hw, u1 = hw, ya = y0 + (y1 - y0) * (0.2f + Hf(salt, 2) * 0.2f), yb = y1 - (y1 - y0) * (0.15f + Hf(salt, 3) * 0.25f);
                if (Hf(salt, 4) < 0.5f) { float tmp = ya; ya = yb; yb = tmp; }
                float w = 0.022f;
                var a0 = c + dir * (u0 - 0.02f) + o * 0.02f; var a1 = c + dir * (u1 + 0.02f) + o * 0.02f;
                var n3 = V3(o); Color bc = Geo.Shade(Lerp(BOARD, DUST, 0.2f), n3);
                g.QuadRaw(Geo.W(a0, ya - w), Geo.W(a1, yb - w), Geo.W(a1, yb + w), Geo.W(a0, ya + w), Mul(bc, 0.9f), Mul(bc, 0.9f), bc, bc);
            }
            ShardsIn(g, salt, 0, c, dir, o, hw, y0, y1);
            // glass on the ground below
            if (fl <= 1 && Hf(salt, 6) < 0.75f)
            {
                int ns = 2 + (int)(Hf(salt, 7) * 2.5f);
                for (int q = 0; q < ns; q++)
                {
                    var gp = c + dir * ((Hf(salt, 20 + q) - 0.5f) * hw * 2.6f) + o * (0.05f + Hf(salt, 30 + q) * (0.12f + 0.05f * fl));
                    float gy = ClsAt(gp) == C_SIDE ? SW_Y + 0.004f : PAVE_Y + 0.008f;
                    float sz = 0.016f + Hf(salt, 40 + q) * 0.022f, an = Hf(salt, 50 + q) * 6.28f;
                    var e1 = new Vector2(Mathf.Cos(an), Mathf.Sin(an)) * sz; var e2 = new Vector2(-e1.y, e1.x) * 0.6f;
                    g.TriRaw(Geo.W(gp - e1, gy), Geo.W(gp + e1 * 0.8f + e2, gy), Geo.W(gp + e2 * -1f, gy), Lerp(SPECK, SHARD_HI, Hf(salt, 60 + q)));
                }
            }
            // burned: soot streak above
            float sootP = info.Dmg >= 1 ? 0.3f : 0.1f;
            if (Hf(salt, 8) < sootP && y1 < info.WallTop - 0.08f)
                Soot(g, c, dir, o, hw, y1, Mathf.Min(info.WallTop - 0.02f, y1 + info.FloorH * (0.5f + 0.4f * Hf(salt, 9))), info.Wall);
            return true;
        }

        /// <summary>Jagged glass triangles left in the frame edges of a hole.</summary>
        void ShardsIn(Geo g, int salt, int fl, Vector2 c, Vector2 dir, Vector2 o, float hw, float y0, float y1)
        {
            var n3 = V3(o);
            float wh = y1 - y0, off = 0.013f;
            Vector3 P(float u, float y) => Geo.W(c + dir * u + o * off, y);
            int cnt = 0;
            for (int e = 0; e < 4 && cnt < 3; e++)
            {
                int m = 1 + (int)(Hf(salt * 7 + fl, 70 + e) * 1.6f);
                for (int j = 0; j < m && cnt < 3; j++, cnt++)
                {
                    float t0 = Hf(salt * 7 + fl, 80 + e * 3 + j) * 0.7f, tw = 0.18f + Hf(salt * 7 + fl, 90 + e * 3 + j) * 0.3f;
                    float depth = 0.25f + Hf(salt * 7 + fl, 100 + e * 3 + j) * 0.45f;
                    float t1 = Mathf.Min(1f, t0 + tw), ta = t0 + (t1 - t0) * Hf(salt * 7 + fl, 110 + e * 3 + j);
                    Vector3 A, B, C;
                    switch (e)
                    {
                        case 0: A = P(-hw + 2f * hw * t0, y0); B = P(-hw + 2f * hw * t1, y0); C = P(-hw + 2f * hw * ta, y0 + wh * depth * 0.7f); break;    // bottom
                        case 1: A = P(-hw + 2f * hw * t0, y1); B = P(-hw + 2f * hw * t1, y1); C = P(-hw + 2f * hw * ta, y1 - wh * depth * 0.8f); break;    // top
                        case 2: A = P(-hw, y0 + wh * t0); B = P(-hw, y0 + wh * t1); C = P(-hw + 2f * hw * depth * 0.6f, y0 + wh * ta); break;             // left
                        default: A = P(hw, y0 + wh * t0); B = P(hw, y0 + wh * t1); C = P(hw - 2f * hw * depth * 0.6f, y0 + wh * ta); break;               // right
                    }
                    Color sc = Geo.Shade(Lerp(SHARD, SHARD_HI, Hf(salt * 7 + fl, 120 + cnt)), n3);
                    g.TriRaw(A, B, C, sc);
                }
            }
        }

        void Curtain(Geo g, int salt, Vector2 c, Vector2 dir, Vector2 o, float hw, float y0, float y1)
        {
            Color cc = Pick(CURTAIN, salt, 11);
            var n3 = V3(o);
            Color ct = Geo.Shade(cc, n3), cb = Geo.Shade(Mul(cc, 0.8f), n3);
            float side = Hf(salt, 12) < 0.5f ? -1f : 1f;
            float u0 = side * hw * 0.95f, u1 = side * hw * (0.05f + Hf(salt, 13) * 0.4f);
            float wh = y1 - y0;
            float yb0 = y0 + wh * (0.05f + Hf(salt, 14) * 0.3f), yb1 = y0 + wh * (0.25f + Hf(salt, 15) * 0.4f);
            float blow = 0.02f + Hf(salt, 16) * 0.07f;   // bottom blows out of the window
            var t0 = c + dir * u0 + o * 0.016f; var t1 = c + dir * u1 + o * 0.016f;
            var b0 = c + dir * (u0 * 1.05f) + o * (0.016f + blow); var b1 = c + dir * (u1 * 0.9f) + o * (0.016f + blow * 1.3f);
            float ym = (y1 + Mathf.Max(yb0, yb1)) * 0.5f;
            var m0 = Vector2.Lerp(t0, b0, 0.5f); var m1 = Vector2.Lerp(t1, b1, 0.5f);
            g.QuadRaw(Geo.W(m0, ym), Geo.W(m1, ym), Geo.W(t1, y1 - 0.01f), Geo.W(t0, y1 - 0.01f), Lerp(cb, ct, 0.5f), Lerp(cb, ct, 0.5f), ct, ct);
            g.QuadRaw(Geo.W(b0, yb0), Geo.W(b1, yb1), Geo.W(m1, ym), Geo.W(m0, ym), cb, cb, Lerp(cb, ct, 0.5f), Lerp(cb, ct, 0.5f));
        }

        void Soot(Geo g, Vector2 c, Vector2 dir, Vector2 o, float hw, float y0, float y1, Color wall)
        {
            var n3 = V3(o);
            Color b = Geo.Shade(SCORCH, n3), t = Geo.Shade(Lerp(SCORCH, wall, 0.8f), n3);
            var a0 = c - dir * hw * 0.95f + o * 0.009f; var a1 = c + dir * hw * 0.95f + o * 0.009f;
            var e0 = c - dir * hw * 0.55f + o * 0.009f; var e1 = c + dir * hw * 0.75f + o * 0.009f;
            g.QuadRaw(Geo.W(a0, y0), Geo.W(a1, y0), Geo.W(e1, y1), Geo.W(e0, y1), b, b, t, t);
        }
    }
}
