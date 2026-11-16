// v0.12.0a "Defend the base": meshes for the defenses the sim keeps (wall runs over S.Fences tiles with
// per-tile damage and breaches, gates, towers, traps), the exterior dressing of converted buildings (signs,
// red cross, garage doors, crop rows, tanks, sandbags...) and scaffolding while a conversion is being set up,
// plus the placement ghost of the DEFENSE tools. Pure C# on UnityEngine value types, so the offline preview
// renders exactly what the game draws.
using System;
using System.Collections.Generic;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed partial class MapGen
    {
        public int MapW => d.W;

        // ---------------- yard geometry for the sim (Game.YardProvider) ----------------
        /// <summary>The camp yard exactly as Camp() draws it, so the sim's starter palisade sits under the drawn one.</summary>
        public YardGeo? YardFor(Base b)
        {
            if (b == null || !Layout(b, out var lay)) return null;
            return new YardGeo { C = lay.Yard.C, U = lay.Yard.U, V = lay.Yard.V, Half = lay.Yard.Hu, Gate = lay.Gate };
        }

        static readonly Color SCRAP_A = Hex("#7a6a58"), SCRAP_B = Hex("#6e4a32"), SCRAP_C = Hex("#5c6468"), SCRAP_D = Hex("#8a7660");
        static readonly Color CONC = Hex("#a8a294"), CONC_TOP = Hex("#bdb8aa"), CONC_D = Hex("#7e796e"), CHAIN = Hex("#9aa2a4");
        static readonly Color SAND = Hex("#8a7a54"), SAND_D = Hex("#5a4e36"), RUBBLE_W = Hex("#5a4632");

        // ================= walls =================
        /// <summary>Draw every wall run: elements spaced along the straight line, each looking up the sim tile under
        /// it for its hp (lean / gaps / scorch when damaged) or its breach (rubble, nothing standing).</summary>
        void DrawWalls(Geo g, Game game)
        {
            var S = game.S;
            if (S?.Walls == null) return;
            foreach (var r in S.Walls)
            {
                var a = r.A; var b = r.B;
                float len = (b - a).magnitude;
                if (len < 0.2f) continue;
                var dir = (b - a) / len; var nrm = new Vector2(-dir.y, dir.x);
                int tierRun = r.Tier;
                float step = WallStep(tierRun);
                int n = Mathf.Max(1, Mathf.RoundToInt(len / step));
                float st = len / n;
                for (int i = 0; i < n; i++)
                {
                    float t = (i + 0.5f) * st;
                    if (r.GapT >= 0f && Mathf.Abs(t - r.GapT * len) < r.GapW) continue;   // camp gate (drawn by Camp)
                    var p = a + dir * t;
                    var tile = new Vector2Int(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y));
                    if (!d.InMap(tile)) continue;
                    int k = tile.y * d.W + tile.x;
                    int tier = tierRun; float frac = 1f; bool breach = false;
                    if (S.Fences.TryGetValue(k, out var f)) { tier = f.Tier; frac = (float)(f.Hp / Math.Max(1.0, game.FenceTileMax(k))); }
                    else if (S.Breaches.TryGetValue(k, out var br)) { breach = true; tier = br.Tier; }
                    else if (!r.Starter) continue;   // natural wall (building / water) under a placed run
                    else if (d.Walkable(tile)) { breach = true; }   // starter tile removed (old save / sold)
                    int salt = r.Id * 977 + i;
                    if (breach) { Rubble(g, p, dir, nrm, tier, salt); continue; }
                    if (S.Gates.ContainsKey(k) && !r.Starter) { if (i % 3 == 1) GateLeaf(g, p, dir, nrm, tier, frac); else WallPiece(g, p, dir, nrm, st, tier, frac, salt); continue; }
                    WallPiece(g, p, dir, nrm, st, tier, frac, salt);
                }
                // binding rails / caps along intact stretches, and a contact shadow
                if (tierRun == 0) RailRun(g, r, a, dir, nrm, len, S, game);
            }
        }

        static float WallStep(int tier) => tier == 0 ? 0.105f : tier == 1 ? 0.6f : tier == 2 ? 0.34f : 0.55f;

        void WallPiece(Geo g, Vector2 p, Vector2 dir, Vector2 nrm, float st, int tier, float frac, int salt)
        {
            float dmg = 1f - frac;
            // damage: missing pieces, lean, darker
            if (dmg > 0.34f && Hf(salt, 3) < dmg * 0.45f) { if (Hf(salt, 4) < 0.5f) Debris1(g, p + nrm * (Hf(salt, 5) - 0.5f) * 0.4f, dir, salt); return; }
            float lean = dmg > 0.2f ? (Hf(salt, 6) - 0.35f) * dmg * 0.35f : 0f;
            float hMul = 1f - (dmg > 0.5f ? Hf(salt, 7) * dmg * 0.45f : 0f);
            switch (tier)
            {
                case 0:   // sharpened logs
                {
                    float h = (0.9f + Hf(salt, 1) * 0.12f) * hMul;
                    Color lc = Lerp(LOG, LOG_SHADE, Hf(salt, 2) * 0.4f);
                    if (dmg > 0.25f) lc = Lerp(lc, Hex("#2a201a"), dmg * 0.5f);
                    var q = new ORect(p, dir, 0.05f, 0.05f).Quad();
                    if (Mathf.Abs(lean) < 0.01f) { g.Box(q, 0f, h, lc, lc, 0.2f, false); g.Pyramid(q, h, p, h + 0.12f, Lerp(lc, LOG_TIP, 0.5f)); }
                    else
                    {
                        var top = p + nrm * lean;
                        g.Beam(Geo.W(p, 0f), Geo.W(top, h), 0.05f, lc);
                        g.Pyramid(new ORect(top, dir, 0.05f, 0.05f).Quad(), h, top, h + 0.1f, Lerp(lc, LOG_TIP, 0.5f));
                    }
                    break;
                }
                case 1:   // chain-link: post + mesh panel
                {
                    float h = 0.78f * hMul;
                    g.Cyl(p - dir * st * 0.5f, 0.025f, 0f, 0.82f, Hex("#6e7476"), Hex("#8a9092"), 5);
                    var a0 = p - dir * st * 0.5f; var a1 = p + dir * st * 0.5f;
                    var top0 = a0 + nrm * lean; var top1 = a1 + nrm * lean;
                    Color mc = Lerp(CHAIN, Hex("#5a5e5e"), dmg * 0.6f);
                    g.QuadRaw(Geo.W(a0, 0.03f), Geo.W(a1, 0.03f), Geo.W(top1, h), Geo.W(top0, h), Mul(mc, 0.8f), Mul(mc, 0.8f), mc, mc);
                    g.QuadRaw(Geo.W(a1, 0.03f), Geo.W(a0, 0.03f), Geo.W(top0, h), Geo.W(top1, h), Mul(mc, 0.7f), Mul(mc, 0.7f), Mul(mc, 0.9f), Mul(mc, 0.9f));
                    g.Beam(Geo.W(top0, h), Geo.W(top1, h), 0.012f, Hex("#7a8082"));
                    break;
                }
                case 2:   // scrap-metal sheets: mixed rusty panels, ragged tops, rivets
                {
                    float h = (0.95f + (Hf(salt, 1) - 0.5f) * 0.14f) * hMul;
                    Color[] pal = { SCRAP_A, SCRAP_B, SCRAP_C, SCRAP_D };
                    Color pc = pal[(int)(Hf(salt, 2) * 4f) & 3];
                    if (dmg > 0.25f) pc = Lerp(pc, Hex("#2e2622"), dmg * 0.45f);
                    var c0 = p - dir * st * 0.52f; var c1 = p + dir * st * 0.52f;
                    var t0 = c0 + nrm * lean; var t1 = c1 + nrm * lean;
                    float h0 = h, h1 = h * (0.93f + Hf(salt, 9) * 0.1f);
                    var o = nrm * 0.03f;
                    g.QuadRaw(Geo.W(c0 - o, 0f), Geo.W(c1 - o, 0f), Geo.W(t1 - o, h1), Geo.W(t0 - o, h0), Shade(Mul(pc, 0.9f), -nrm), Shade(Mul(pc, 0.9f), -nrm), Shade(pc, -nrm), Shade(pc, -nrm));
                    g.QuadRaw(Geo.W(c1 + o, 0f), Geo.W(c0 + o, 0f), Geo.W(t0 + o, h0), Geo.W(t1 + o, h1), Shade(Mul(pc, 0.85f), nrm), Shade(Mul(pc, 0.85f), nrm), Shade(pc, nrm), Shade(pc, nrm));
                    g.QuadRaw(Geo.W(t0 - o, h0), Geo.W(t1 - o, h1), Geo.W(t1 + o, h1), Geo.W(t0 + o, h0), Lerp(pc, Hex("#9a8a76"), 0.3f), Lerp(pc, Hex("#9a8a76"), 0.3f), pc, pc);
                    // corrugation lines + rust streak
                    for (int c = 1; c < 3; c++)
                    {
                        var cp = Vector2.Lerp(c0, c1, c / 3f) - o * 1.4f;
                        g.Beam(Geo.W(cp, 0.03f), Geo.W(cp + nrm * lean, h * 0.95f), 0.008f, Mul(pc, 0.7f));
                    }
                    if (Hf(salt, 11) < 0.4f) { var rp = Vector2.Lerp(c0, c1, 0.3f + Hf(salt, 12) * 0.4f) - o * 1.5f; g.Beam(Geo.W(rp, 0.05f), Geo.W(rp, h * 0.7f), 0.02f, RUST); }
                    if ((salt & 3) == 0) g.Cyl(c0, 0.03f, 0f, h + 0.05f, Hex("#4a4440"), Hex("#5a5450"), 5);
                    break;
                }
                default:  // concrete slabs (3) / reinforced (4)
                {
                    float h = (tier >= 4 ? 1.15f : 1.05f) * hMul;
                    Color cc = Lerp(CONC, CONC_D, Hf(salt, 2) * 0.35f);
                    if (dmg > 0.25f) cc = Lerp(cc, Hex("#4a463e"), dmg * 0.5f);
                    var slab = new ORect(p + nrm * lean * 0.3f, dir, st * 0.48f, 0.08f);
                    g.Box(slab.Quad(), 0f, h, cc, CONC_TOP, 0.25f);
                    // base footing + top chamfer
                    g.Box(new ORect(p, dir, st * 0.5f, 0.12f).Quad(), 0f, 0.12f, Mul(cc, 0.85f), Mul(CONC_TOP, 0.9f));
                    if (tier >= 4) g.Box(new ORect(p, dir, st * 0.5f, 0.1f).Quad(), h - 0.08f, h, Hex("#7a7466"), Hex("#8a8476"));
                    // cracks / exposed rebar when hurt
                    if (dmg > 0.3f)
                    {
                        var cp = p - nrm * 0.085f + dir * (Hf(salt, 14) - 0.5f) * st * 0.6f;
                        g.Beam(Geo.W(cp, h * 0.3f), Geo.W(cp + dir * 0.12f, h * 0.8f), 0.01f, Hex("#2a2622"));
                        if (dmg > 0.55f) g.Beam(Geo.W(cp + dir * 0.05f, h - 0.05f), Geo.W(cp + dir * 0.1f - nrm * 0.1f, h + 0.18f), 0.008f, Hex("#3a2a22"));
                    }
                    // stencilled hazard band on every 3rd slab
                    if ((salt % 3) == 0)
                    {
                        var bp0 = p - dir * st * 0.4f - nrm * 0.082f; var bp1 = p + dir * st * 0.4f - nrm * 0.082f;
                        g.QuadRaw(Geo.W(bp0, h * 0.62f), Geo.W(bp1, h * 0.62f), Geo.W(bp1, h * 0.7f), Geo.W(bp0, h * 0.7f), Hex("#a87a2a"), Hex("#a87a2a"), Hex("#b88a3a"), Hex("#b88a3a"));
                    }
                    break;
                }
            }
        }

        static Color Shade(Color c, Vector2 n2) => Geo.Shade(c, new Vector3(n2.x, 0f, -n2.y));

        void RailRun(Geo g, WallRun r, Vector2 a, Vector2 dir, Vector2 nrm, float len, GameState S, Game game)
        {
            // inner binding rails over stretches whose tiles still stand (inside = toward the camp for the starter)
            Vector2 inside = nrm;
            if (r.Starter && game != null)
            {
                var b = game.BaseById(r.Bid) ?? game.MainBase();
                if (b != null && Layout(b, out var lay) && Vector2.Dot(lay.Yard.C - (a + dir * len * 0.5f), nrm) < 0f) inside = -nrm;
            }
            float seg0 = -1f;
            int n = Mathf.Max(1, Mathf.RoundToInt(len / 0.25f));
            for (int i = 0; i <= n; i++)
            {
                float t = len * i / n;
                bool ok = i < n;
                if (ok)
                {
                    var p = a + dir * t;
                    var tile = new Vector2Int(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y));
                    int k = tile.y * d.W + tile.x;
                    bool gap = r.GapT >= 0f && Mathf.Abs(t - r.GapT * len) < r.GapW + 0.05f;
                    ok = !gap && d.InMap(tile) && (S.Fences.ContainsKey(k) || (r.Starter && !d.Walkable(tile) && !S.Breaches.ContainsKey(k)));
                }
                if (ok && seg0 < 0f) seg0 = t;
                if (!ok && seg0 >= 0f)
                {
                    var r0 = a + dir * seg0 + inside * 0.07f; var r1 = a + dir * t + inside * 0.07f;
                    foreach (float yy in new[] { 0.25f, 0.7f }) g.Beam(Geo.W(r0, yy), Geo.W(r1, yy), 0.02f, TIMBER);
                    seg0 = -1f;
                }
            }
        }

        void Rubble(Geo g, Vector2 p, Vector2 dir, Vector2 nrm, int tier, int salt)
        {
            // what's left of the wall at a breach: broken pieces lying both ways, splinters / slab chunks
            Color c = tier == 0 ? RUBBLE_W : tier == 2 ? SCRAP_B : tier == 1 ? CHAIN : CONC_D;
            if (Hf(salt, 21) < 0.55f)
            {
                var q = p + nrm * ((Hf(salt, 22) - 0.5f) * 0.7f);
                float a = Hf(salt, 23) * Mathf.PI;
                var dd = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                if (tier == 0) g.Beam(Geo.W(q - dd * 0.32f, 0.05f), Geo.W(q + dd * 0.32f, 0.07f), 0.045f, Lerp(c, LOG_SHADE, Hf(salt, 24) * 0.5f));
                else g.BoxAt(q, dd, 0.16f, 0.08f, 0f, 0.07f + Hf(salt, 25) * 0.06f, c, Mul(c, 1.1f));
            }
            if (Hf(salt, 26) < 0.35f) { var s = p + nrm * ((Hf(salt, 27) - 0.5f) * 0.5f); g.Beam(Geo.W(s, 0f), Geo.W(s + dir * 0.05f, 0.18f + Hf(salt, 28) * 0.2f), 0.035f, Mul(c, 0.8f)); }
            g.Flat(new ORect(p, dir, 0.06f, 0.3f).Quad(), 0.052f, Hex("#3a3026"));
        }

        void Debris1(Geo g, Vector2 p, Vector2 dir, int salt)
        {
            g.BoxAt(p, new Vector2(dir.y, -dir.x), 0.12f, 0.03f, 0f, 0.05f, Hex("#4a3a2a"), Hex("#5a4632"));
        }

        void GateLeaf(Geo g, Vector2 p, Vector2 dir, Vector2 nrm, int tier, float frac)
        {
            Color c = tier == 0 ? Lerp(LOG, LOG_TIP, 0.25f) : tier == 2 ? SCRAP_C : tier == 1 ? CHAIN : Hex("#6e6a5e");
            g.Cyl(p - dir * 0.45f, 0.07f, 0f, 1.25f, LOG, LOG_TIP, 6);
            g.Cyl(p + dir * 0.45f, 0.07f, 0f, 1.25f, LOG, LOG_TIP, 6);
            g.Box(new ORect(p, dir, 0.42f, 0.035f).Quad(), 0.02f, 0.95f, c, Mul(c, 1.1f));
            g.Beam(Geo.W(p - dir * 0.4f + nrm * 0.05f, 0.15f), Geo.W(p + dir * 0.4f + nrm * 0.05f, 0.85f), 0.025f, TIMBER);
            g.Beam(Geo.W(p - dir * 0.5f, 1.2f), Geo.W(p + dir * 0.5f, 1.2f), 0.04f, TIMBER);
        }

        // ================= towers & traps =================
        void DrawTowersTraps(Geo g, Game game)
        {
            var S = game.S;
            foreach (var kv in S.Towers)
            {
                var tp = new Vector2(kv.Key % d.W + 0.5f, kv.Key / d.W + 0.5f);
                var t = kv.Value;
                if (t.Tier == 0) Watchtower(g, tp, GU, GV);
                else
                {
                    // MG nest / rifle tower / turret: sandbag ring with a gun, steel legs for the rifle tower
                    if (t.Tier == 2) { Watchtower(g, tp, GU, GV); continue; }
                    for (int c = 0; c < 3; c++)
                        for (int k = 0; k < 10; k++)
                        {
                            float a = k * Mathf.PI * 2f / 10f + c * 0.3f;
                            var sp = tp + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 0.42f;
                            g.BoxAt(sp, new Vector2(-Mathf.Sin(a), Mathf.Cos(a)), 0.13f, 0.07f, c * 0.07f, c * 0.07f + 0.07f, Lerp(SAND, SAND_D, Hf(k, c) * 0.5f), SAND);
                        }
                    g.Beam(Geo.W(tp, 0.25f), Geo.W(tp + GU * 0.45f, 0.28f), 0.03f, Hex("#2a2a26"));
                }
            }
            foreach (var kv in S.Traps)
            {
                var p = new Vector2(kv.Key % d.W + 0.5f, kv.Key / d.W + 0.5f);
                var tr = kv.Value;
                float frac = tr.Hp > 0 ? 1f : 0.2f;
                int salt = kv.Key;
                string id = tr.Kind < Data.TRAPS.Length ? Data.TRAPS[tr.Kind].Id : "spikes";
                switch (id)
                {
                    case "spikes":
                        g.Disc(p, 0.36f, 0.052f, Hex("#2a2218"), Hex("#4a3a28"), 8);
                        for (int k = 0; k < 9; k++)
                        {
                            float a = k * 2.4f;
                            var sp = p + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (0.08f + 0.22f * Hf(salt, k));
                            var tip = sp + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 0.08f;
                            g.Beam(Geo.W(sp, 0.03f), Geo.W(tip, 0.32f * frac + 0.05f), 0.022f, k % 2 == 0 ? LOG_TIP : LOG);
                        }
                        break;
                    case "wire": case "barbed":
                        for (int c = 0; c < 3; c++)
                        {
                            var cc = p + GU * ((c - 1) * 0.3f);
                            for (int k = 0; k < 8; k++)
                            {
                                float a0 = k * Mathf.PI / 4f, a1 = (k + 1) * Mathf.PI / 4f;
                                float rr = id == "wire" ? 0.16f : 0.11f;
                                var q0 = Geo.W(cc + GV * (Mathf.Cos(a0) * rr), 0.17f + Mathf.Sin(a0) * rr);
                                var q1 = Geo.W(cc + GV * (Mathf.Cos(a1) * rr) + GU * 0.04f, 0.17f + Mathf.Sin(a1) * rr);
                                g.Beam(q0, q1, 0.008f, Hex("#8a8e8c"));
                            }
                        }
                        foreach (int s2 in new[] { -1, 1 }) g.Beam(Geo.W(p + GU * (s2 * 0.45f), 0f), Geo.W(p + GU * (s2 * 0.45f), 0.32f), 0.02f, TIMBER);
                        break;
                    case "fire":
                        g.Flat(new ORect(p, GU, 0.42f, 0.14f).Quad(), 0.052f, Hex("#1e1a16"));
                        g.Cyl(p + GU * 0.36f, 0.05f, 0f, 0.14f, Hex("#7a2e24"), Hex("#2e2c28"), 6);
                        break;
                    case "lure":
                        g.Beam(Geo.W(p, 0f), Geo.W(p, 0.6f), 0.025f, TIMBER);
                        g.BoxAt(p, GU, 0.07f, 0.05f, 0.5f, 0.6f, Hex("#5a6a4a"), Hex("#6a7a5a"));
                        break;
                    case "tripwire":
                        foreach (int s2 in new[] { -1, 1 }) g.Beam(Geo.W(p + GU * (s2 * 0.4f), 0f), Geo.W(p + GU * (s2 * 0.4f), 0.25f), 0.015f, TIMBER);
                        g.Beam(Geo.W(p - GU * 0.4f, 0.12f), Geo.W(p + GU * 0.4f, 0.12f), 0.004f, Hex("#c8c0b0"));
                        break;
                    default:   // deadfall: log on an A-frame
                        foreach (int s2 in new[] { -1, 1 }) { g.Beam(Geo.W(p + GU * (s2 * 0.35f) - GV * 0.15f, 0f), Geo.W(p + GU * (s2 * 0.35f), 0.55f), 0.025f, TIMBER); g.Beam(Geo.W(p + GU * (s2 * 0.35f) + GV * 0.15f, 0f), Geo.W(p + GU * (s2 * 0.35f), 0.55f), 0.025f, TIMBER); }
                        g.Beam(Geo.W(p - GU * 0.42f, 0.52f), Geo.W(p + GU * 0.42f, 0.52f), 0.07f, LOG);
                        break;
                }
            }
        }

        // ================= converted buildings =================
        /// <summary>Exterior changes per use, so a converted building reads at a glance even without its label.</summary>
        void ConvDressing(Geo g, int l, Conversion cv, bool pending, float progress)
        {
            if (l < 0 || l >= _info.Length || !_info[l].Valid) return;
            var inf = _info[l];
            if (Cleared(inf.R.C)) return;   // cleared into a camp yard (older saves converted those)
            var R = inf.R; float top = inf.Top, wt = inf.WallTop > 0f ? inf.WallTop : top;
            FaceEdge(R, inf.Front, out var fa, out var fb, out var fo);
            float flen = (fb - fa).magnitude; var fd = (fb - fa) / Mathf.Max(0.01f, flen);
            var fm = (fa + fb) * 0.5f;
            var def = Data.CONVERSIONS.TryGetValue(cv.Type, out var cd) ? cd : null;
            Color tc = def != null ? def.Color : Hex("#c8b48c");
            Color board = Lerp(BOARD, tc, 0.55f);
            bool flat = inf.A != Arch.House && inf.A != Arch.Hut && inf.A != Arch.Barn && inf.A != Arch.Church;
            float roofY = flat ? top + 0.01f : wt + 0.01f;
            // sign board over the door in the use's colour, on two posts
            float sy = Mathf.Min(wt - 0.05f, 0.95f);
            var sp = fm + fo * 0.06f;
            g.Box(new ORect(sp, fd, Mathf.Min(0.55f, flen * 0.3f), 0.025f).Quad(), sy, sy + 0.24f, board, Mul(board, 1.1f));
            g.Box(new ORect(sp + fo * 0.03f, fd, Mathf.Min(0.42f, flen * 0.24f), 0.01f).Quad(), sy + 0.07f, sy + 0.17f, Hex("#e8e0c8"), Hex("#e8e0c8"));
            switch (cv.Type)
            {
                case "hospital":
                {
                    // red cross on the roof (white square) and on a banner on the front
                    var rc = R.C; float s = Mathf.Min(R.Hu, R.Hv) * 0.55f;
                    g.Flat(new ORect(rc, R.U, s, s).Quad(), roofY + 0.012f, Hex("#e8e4dc"));
                    g.Flat(new ORect(rc, R.U, s * 0.75f, s * 0.22f).Quad(), roofY + 0.02f, Hex("#c0302a"));
                    g.Flat(new ORect(rc, R.U, s * 0.22f, s * 0.75f).Quad(), roofY + 0.02f, Hex("#c0302a"));
                    var bp = fm + fo * 0.04f + fd * Mathf.Min(0.9f, flen * 0.3f);
                    float by = Mathf.Min(wt * 0.75f, 1.6f);
                    g.Box(new ORect(bp, fd, 0.24f, 0.012f).Quad(), by - 0.24f, by + 0.24f, Hex("#e8e4dc"), Hex("#e8e4dc"));
                    g.Box(new ORect(bp + fo * 0.016f, fd, 0.17f, 0.006f).Quad(), by - 0.05f, by + 0.05f, Hex("#c0302a"), Hex("#c0302a"));
                    g.Box(new ORect(bp + fo * 0.016f, fd, 0.05f, 0.006f).Quad(), by - 0.17f, by + 0.17f, Hex("#c0302a"), Hex("#c0302a"));
                    // stretchers / crates by the door
                    g.BoxAt(fm + fo * 0.45f - fd * 0.5f, fd, 0.28f, 0.1f, 0.05f, 0.14f, Hex("#5a6a5a"), Hex("#8a9a88"));
                    break;
                }
                case "farm":
                {
                    // crop rows on the ground in front, planters on the roof, a little hoop greenhouse
                    for (int row = 0; row < 5; row++)
                    {
                        var rp = fm + fo * (0.45f + row * 0.22f);
                        g.Flat(new ORect(rp, fd, Mathf.Min(flen * 0.45f, 1.6f), 0.06f).Quad(), 0.055f, Hex("#3a2e1e"));
                        for (int k = -6; k <= 6; k++)
                        {
                            var cp = rp + fd * (k * Mathf.Min(flen * 0.45f, 1.6f) / 6.5f);
                            g.Cone(cp, 0.07f, 0.05f, 0.18f + Hf(l * 7 + row, k + 9) * 0.08f, Lerp(Hex("#5a8a3a"), Hex("#8aa84a"), Hf(row, k + 30)), k * 0.7f, 4);
                        }
                    }
                    var rc = R.C; float s = Mathf.Min(R.Hu, R.Hv) * 0.6f;
                    for (int k = -1; k <= 1; k++) g.BoxAt(rc + R.V * (k * s * 0.6f), R.U, s, 0.12f, roofY, roofY + 0.12f, Hex("#5a4632"), Hex("#4a7a32"));
                    var gh = fm + fo * 0.5f + fd * Mathf.Min(flen * 0.5f + 0.5f, 2.2f);
                    for (int k = 0; k < 4; k++)
                    {
                        float a0 = k * Mathf.PI / 4f, a1 = (k + 1) * Mathf.PI / 4f;
                        var A0 = gh + fo * (Mathf.Cos(a0) * 0.35f); var A1 = gh + fo * (Mathf.Cos(a1) * 0.35f);
                        g.QuadRaw(Geo.W(A0 - fd * 0.45f, Mathf.Sin(a0) * 0.35f + 0.05f), Geo.W(A0 + fd * 0.45f, Mathf.Sin(a0) * 0.35f + 0.05f), Geo.W(A1 + fd * 0.45f, Mathf.Sin(a1) * 0.35f + 0.05f), Geo.W(A1 - fd * 0.45f, Mathf.Sin(a1) * 0.35f + 0.05f),
                            Hex("#b8c8b0"), Hex("#b8c8b0"), Hex("#d0dcc8"), Hex("#d0dcc8"));
                    }
                    break;
                }
                case "water":
                {
                    var tp = R.C + R.U * (R.Hu * 0.4f);
                    foreach (var lg in new[] { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) })
                        g.Beam(Geo.W(tp + R.U * (lg.x * 0.22f) + R.V * (lg.y * 0.22f), roofY), Geo.W(tp + R.U * (lg.x * 0.18f) + R.V * (lg.y * 0.18f), roofY + 0.45f), 0.02f, Hex("#4a4440"));
                    g.Cyl(tp, 0.3f, roofY + 0.45f, roofY + 0.95f, Hex("#4a6a7a"), Hex("#5a7a8a"), 10);
                    g.Pyramid(new ORect(tp, R.U, 0.3f, 0.3f).Quad(), roofY + 0.95f, tp, roofY + 1.08f, Hex("#3a5a6a"));
                    for (int k = 0; k < 4; k++) g.Cyl(fm + fo * 0.35f + fd * ((k - 1.5f) * 0.2f), 0.085f, 0.05f, 0.32f, k % 2 == 0 ? Hex("#2e5a7a") : Hex("#3a6a8a"), Hex("#2a3a44"), 8);
                    g.Beam(Geo.W(tp - R.U * 0.3f, roofY + 0.5f), Geo.W(fm + fo * 0.02f, 0.3f), 0.022f, Hex("#5a5a56"));
                    break;
                }
                case "barracks":
                {
                    // sandbag walls flanking the door, a tarp on the roof, laundry line
                    foreach (int s2 in new[] { -1, 1 })
                        for (int c = 0; c < 4; c++)
                            for (int k = 0; k < 4 - (c > 2 ? 1 : 0); k++)
                            {
                                var bp = fm + fo * 0.32f + fd * (s2 * (0.45f + k * 0.17f + c * 0.085f));
                                g.BoxAt(bp, fd, 0.085f, 0.06f, c * 0.05f, c * 0.05f + 0.05f, Lerp(SAND, SAND_D, Hf(l + c, k + s2) * 0.6f), Mul(SAND, 1.05f));
                            }
                    if (flat) g.Flat(new ORect(R.C, R.U, R.Hu * 0.5f, R.Hv * 0.4f).Quad(), roofY + 0.02f, Hex("#4a5a3a"));
                    var l0 = fm + fo * 0.6f - fd * 0.8f; var l1 = fm + fo * 0.6f + fd * 0.8f;
                    g.Beam(Geo.W(l0, 0f), Geo.W(l0, 0.5f), 0.015f, TIMBER); g.Beam(Geo.W(l1, 0f), Geo.W(l1, 0.5f), 0.015f, TIMBER);
                    for (int k = 0; k < 4; k++) { var cp = Vector2.Lerp(l0, l1, 0.2f + k * 0.2f); g.QuadRaw(Geo.W(cp - fd * 0.06f, 0.48f), Geo.W(cp + fd * 0.06f, 0.48f), Geo.W(cp + fd * 0.06f, 0.33f), Geo.W(cp - fd * 0.06f, 0.33f), Hex("#8a7a5a"), Hex("#8a7a5a"), Hex("#6a5a4a"), Hex("#6a5a4a")); }
                    break;
                }
                case "storage":
                {
                    for (int k = 0; k < 5; k++)
                    {
                        var cp = fm + fo * (0.35f + (k % 2) * 0.25f) + fd * ((k - 2) * 0.3f);
                        int hgt = 1 + (k % 3);
                        for (int s2 = 0; s2 < hgt; s2++) g.BoxAt(cp, fd, 0.12f, 0.12f, 0.05f + s2 * 0.22f, 0.25f + s2 * 0.22f, Hex("#7a5536"), Hex("#8a6a48"));
                    }
                    var cn = fm + fo * 0.75f + fd * Mathf.Min(flen * 0.5f + 0.8f, 2.5f);
                    g.Box(new ORect(cn, fo, 0.35f, 0.85f).Quad(), 0f, 0.7f, Hex("#5a6a4a"), Hex("#6a7a5a"));
                    break;
                }
                case "workshop": case "garage":
                {
                    // roll-up door on the front face, tyres and drums / a workbench outside
                    float dw = Mathf.Min(0.55f, flen * 0.3f), dh = Mathf.Min(wt * 0.8f, 0.8f);
                    var dp = fm - fd * dw * 0.6f + fo * 0.015f;
                    g.QuadRaw(Geo.W(dp - fd * dw, 0.02f), Geo.W(dp + fd * dw, 0.02f), Geo.W(dp + fd * dw, dh), Geo.W(dp - fd * dw, dh), Hex("#6a6e6a"), Hex("#6a6e6a"), Hex("#8a8e88"), Hex("#8a8e88"));
                    for (int k = 1; k < 7; k++) g.Beam(Geo.W(dp - fd * dw + fo * 0.005f, dh * k / 7f), Geo.W(dp + fd * dw + fo * 0.005f, dh * k / 7f), 0.006f, Hex("#4a4e4a"));
                    var tz = fm + fo * 0.4f + fd * (dw + 0.3f);
                    if (cv.Type == "garage")
                    {
                        for (int k = 0; k < 3; k++) g.Cyl(tz + fd * (k * 0.2f), 0.09f, 0.05f, 0.33f, Hex("#7a2e24"), Hex("#2e2c28"), 8);
                        AddCar(g, fm + fo * 0.95f - fd * 0.6f, Mathf.Atan2(fo.y, fo.x), l, 0.05f);
                    }
                    else
                    {
                        g.BoxAt(tz, fd, 0.3f, 0.1f, 0.3f, 0.36f, Hex("#5a4632"), Hex("#6a5642"));
                        foreach (int s2 in new[] { -1, 1 }) g.Beam(Geo.W(tz + fd * (s2 * 0.25f), 0.05f), Geo.W(tz + fd * (s2 * 0.25f), 0.3f), 0.02f, TIMBER);
                        for (int k = 0; k < 3; k++) g.Cyl(tz + fo * 0.35f + fd * (k * 0.12f), 0.08f, 0.05f + k * 0.0f, 0.12f, Hex("#2a2a28"), Hex("#3a3a36"), 8);
                    }
                    break;
                }
                case "armory":
                {
                    // sandbag nest with a gun at the door, ammo crates, steel-plated door
                    for (int c = 0; c < 3; c++)
                        for (int k = 0; k < 9; k++)
                        {
                            float a = Mathf.PI * (0.1f + 0.8f * k / 8f);
                            var bp = fm + fo * 0.15f + (fd * Mathf.Cos(a) + fo * Mathf.Sin(a)) * 0.55f;
                            g.BoxAt(bp, new Vector2(-Mathf.Sin(a), Mathf.Cos(a)), 0.09f, 0.06f, c * 0.05f, c * 0.05f + 0.05f, Lerp(SAND, SAND_D, Hf(k, c + l) * 0.6f), SAND);
                        }
                    g.Beam(Geo.W(fm + fo * 0.5f, 0.2f), Geo.W(fm + fo * 0.85f, 0.22f), 0.025f, Hex("#2a2a26"));
                    g.QuadRaw(Geo.W(fm - fd * 0.18f + fo * 0.01f, 0.02f), Geo.W(fm + fd * 0.18f + fo * 0.01f, 0.02f), Geo.W(fm + fd * 0.18f + fo * 0.01f, 0.6f), Geo.W(fm - fd * 0.18f + fo * 0.01f, 0.6f), Hex("#4a5054"), Hex("#4a5054"), Hex("#5a6064"), Hex("#5a6064"));
                    for (int k = 0; k < 3; k++) g.BoxAt(fm + fo * 0.35f + fd * (0.8f + k * 0.22f), fd, 0.09f, 0.06f, 0.05f, 0.15f, Hex("#4a5a3a"), Hex("#5a6a4a"));
                    break;
                }
                case "school":
                {
                    var fp = fm + fo * 0.5f + fd * Mathf.Min(flen * 0.45f, 1.4f);
                    g.Beam(Geo.W(fp, 0f), Geo.W(fp, 1.6f), 0.014f, Hex("#8a8a86"));
                    g.QuadRaw(Geo.W(fp, 1.3f), Geo.W(fp + fd * 0.36f, 1.33f), Geo.W(fp + fd * 0.38f, 1.55f), Geo.W(fp, 1.58f), Hex("#2e5a7a"), Hex("#2e5a7a"), Hex("#d8c040"), Hex("#d8c040"));
                    var cb = fm + fo * 0.4f - fd * Mathf.Min(flen * 0.4f, 1.2f);
                    g.Box(new ORect(cb, fd, 0.3f, 0.02f).Quad(), 0.35f, 0.7f, Hex("#2a3a2e"), Hex("#3a2e22"));
                    foreach (int s2 in new[] { -1, 1 }) g.Beam(Geo.W(cb + fd * (s2 * 0.28f), 0f), Geo.W(cb + fd * (s2 * 0.28f), 0.7f), 0.015f, TIMBER);
                    for (int k = 0; k < 2; k++) g.BoxAt(fm + fo * 0.7f + fd * ((k - 0.5f) * 0.6f), fd, 0.22f, 0.05f, 0.12f, 0.16f, Hex("#6a5038"), Hex("#7a5e42"));
                    break;
                }
                case "kitchen":
                {
                    // long tables and benches out front, a barrel stove with a pot, a stovepipe on the roof
                    for (int k = 0; k < 2; k++)
                    {
                        var tp = fm + fo * (0.45f + k * 0.45f);
                        g.BoxAt(tp, fd, 0.45f, 0.11f, 0.24f, 0.28f, Hex("#6a5038"), Hex("#7a5e42"));
                        foreach (int s2 in new[] { -1, 1 }) g.BoxAt(tp + fo * (s2 * 0.2f), fd, 0.42f, 0.04f, 0.13f, 0.16f, Hex("#5a4230"), Hex("#6a5038"));
                    }
                    var st2 = fm + fo * 0.5f + fd * Mathf.Min(flen * 0.45f, 1.3f);
                    g.Cyl(st2, 0.1f, 0.05f, 0.34f, Hex("#3a3a36"), Hex("#2a2a26"), 8);
                    g.Disc(st2, 0.06f, 0.345f, Hex("#ffb14a"), Hex("#c86a2a"), 6);
                    g.Cyl(st2 + fd * 0.04f, 0.075f, 0.36f, 0.48f, Hex("#5a5a56"), Hex("#4a4a46"), 8);
                    var pipe = R.C - R.U * (R.Hu * 0.5f);
                    g.Cyl(pipe, 0.05f, roofY, roofY + 0.55f, Hex("#4a4a46"), Hex("#2a2a26"), 6);
                    break;
                }
            }
            // set-up in progress: scaffolding on the front and a pile of materials
            if (pending)
            {
                float sh = Mathf.Min(wt, 2.4f);
                int nb = Mathf.Max(2, Mathf.RoundToInt(flen / 0.7f));
                var s0 = fa + fo * 0.22f; var s1 = fb + fo * 0.22f;
                for (int k = 0; k <= nb; k++) { var pp = Vector2.Lerp(s0, s1, (float)k / nb); g.Beam(Geo.W(pp, 0f), Geo.W(pp, sh), 0.018f, Hex("#8a8a84")); }
                for (float yy = 0.6f; yy < sh; yy += 0.7f)
                {
                    g.Beam(Geo.W(s0, yy), Geo.W(s1, yy), 0.016f, Hex("#8a8a84"));
                    g.Box(new ORect((s0 + s1) * 0.5f - fo * 0.1f, fd, flen * 0.5f, 0.1f).Quad(), yy - 0.02f, yy, Hex("#7a5e42"), Hex("#8a6e52"));
                }
                for (int k = 0; k < nb; k++) { var p0 = Vector2.Lerp(s0, s1, (float)k / nb); var p1 = Vector2.Lerp(s0, s1, (float)(k + 1) / nb); g.Beam(Geo.W(p0, 0.05f), Geo.W(p1, Mathf.Min(sh, 1.3f)), 0.008f, Hex("#7a7a74")); }
                var pile = fm + fo * 0.75f + fd * 0.6f;
                for (int k = 0; k < 5; k++) g.Beam(Geo.W(pile + fd * (k * 0.04f), 0.05f + k * 0.03f), Geo.W(pile + fd * (k * 0.04f) + fo * 0.5f, 0.05f + k * 0.03f), 0.025f, BOARD);
                // progress shown as how far up a tarp has been fixed
                float py = 0.1f + (sh - 0.1f) * Mathf.Clamp01(progress);
                g.QuadRaw(Geo.W(s0 + fo * 0.02f, 0.05f), Geo.W(s1 + fo * 0.02f, 0.05f), Geo.W(s1 + fo * 0.02f, py), Geo.W(s0 + fo * 0.02f, py), Hex("#4a6a5a"), Hex("#4a6a5a"), Hex("#5a7a6a"), Hex("#5a7a6a"));
            }
        }

        /// <summary>Anchor of a converted building's floating label (world space, above the roof).</summary>
        public bool LabelAnchor(int l, out Vector3 w)
        {
            w = default;
            if (!LotBox(l, out var r, out float top) || Cleared(r.C)) return false;
            w = Geo.W(r.C, top + 1.0f);
            return true;
        }

        // ================= placement ghost =================
        public enum GhostKind { Wall, Gate, Tower, Trap, Upgrade, Route }

        /// <summary>Ghost for the DEFENSE tools: per-tile ground glow (green builds, amber already there,
        /// grey natural wall, red blocked) and a pale preview of what will stand there.</summary>
        public Chunk Ghost(GhostKind kind, Vector2 a, Vector2 b, IList<int> tiles, IList<int> state, int tier, bool ok, int trapKind = 0)
        {
            var g = new Geo(new MeshData(), new MeshData());
            Color good = Hex("#7ad04a"), bad = Hex("#e05040"), have = Hex("#e0b040"), nat = Hex("#9a9a90");
            for (int i = 0; i < tiles.Count; i++)
            {
                int k = tiles[i]; int s = state != null && i < state.Count ? state[i] : (ok ? 0 : 2);
                Color c = s == 0 ? good : s == 1 ? nat : s == 3 ? have : bad;
                float al = s == 1 ? 0.25f : 0.5f;
                float x = k % d.W, y = k / d.W;
                var q0 = Geo.W(new Vector2(x + 0.04f, y + 0.04f), SHADOW_Y_GLOW + 0.004f); var q1 = Geo.W(new Vector2(x + 0.96f, y + 0.04f), SHADOW_Y_GLOW + 0.004f);
                var q2 = Geo.W(new Vector2(x + 0.96f, y + 0.96f), SHADOW_Y_GLOW + 0.004f); var q3 = Geo.W(new Vector2(x + 0.04f, y + 0.96f), SHADOW_Y_GLOW + 0.004f);
                GlowQuad(g.S, q0, q1, q2, q3, c, al, al, al, al);
            }
            Color pale = ok ? Hex("#d8f0c0") : Hex("#f0b0a0");
            switch (kind)
            {
                case GhostKind.Wall: case GhostKind.Upgrade:
                {
                    float len = (b - a).magnitude;
                    if (len < 0.05f) { GlowBeam(g, Geo.W(a, 0.05f), Geo.W(a, 1.2f), 0.04f, pale); break; }
                    var dir = (b - a) / len;
                    float h = tier == 0 ? 0.95f : tier == 2 ? 0.95f : tier == 1 ? 0.78f : 1.05f;
                    // translucent-looking frame: posts + top and bottom beams in an unshaded pale colour
                    int n = Mathf.Max(1, Mathf.RoundToInt(len / 0.5f));
                    for (int i = 0; i <= n; i++) { var p = a + dir * (len * i / n); GlowBeam(g, Geo.W(p, 0f), Geo.W(p, h), 0.025f, pale); }
                    GlowBeam(g, Geo.W(a, h), Geo.W(b, h), 0.03f, pale);
                    GlowBeam(g, Geo.W(a, 0.1f), Geo.W(b, 0.1f), 0.02f, pale);
                    // end handles (drag points) and a rotate arrow at the middle
                    foreach (var e in new[] { a, b }) { GlowBeam(g, Geo.W(e, 0f), Geo.W(e, h + 0.5f), 0.05f, Hex("#ffd27a")); }
                    var mid = (a + b) * 0.5f; var nrm = new Vector2(-dir.y, dir.x);
                    GlowBeam(g, Geo.W(mid + nrm * 0.6f, 0.08f), Geo.W(mid - nrm * 0.6f, 0.08f), 0.03f, Hex("#ffd27a"));
                    break;
                }
                case GhostKind.Tower:
                {
                    var p = a;
                    foreach (var lg in new[] { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) })
                        GlowBeam(g, Geo.W(p + (GU * lg.x + GV * lg.y) * 0.4f, 0f), Geo.W(p + (GU * lg.x + GV * lg.y) * 0.3f, 1.85f), 0.03f, pale);
                    var q = new ORect(p, GU, 0.45f, 0.45f).Quad();
                    for (int i = 0; i < 4; i++) GlowBeam(g, Geo.W(q[i], 1.9f), Geo.W(q[(i + 1) & 3], 1.9f), 0.03f, pale);
                    break;
                }
                case GhostKind.Trap:
                {
                    var p = a;
                    var q = new ORect(p, GU, 0.42f, 0.42f).Quad();
                    for (int i = 0; i < 4; i++) GlowBeam(g, Geo.W(q[i], 0.08f), Geo.W(q[(i + 1) & 3], 0.08f), 0.025f, pale);
                    for (int k = 0; k < 5; k++) { var sp = p + new Vector2(Mathf.Cos(k * 1.3f), Mathf.Sin(k * 1.3f)) * 0.2f; GlowBeam(g, Geo.W(sp, 0f), Geo.W(sp, 0.35f), 0.02f, pale); }
                    break;
                }
                case GhostKind.Route:
                {
                    // squad route / target marker (tiles are glow squares above); a beacon where the player tapped
                    if (a.x < 0f) break;
                    GlowBeam(g, Geo.W(a, 0f), Geo.W(a, 3.2f), 0.06f, ok ? Hex("#ffd27a") : Hex("#f0b0a0"));
                    var q = new ORect(a, GU, 0.5f, 0.5f).Quad();
                    for (int i = 0; i < 4; i++) GlowBeam(g, Geo.W(q[i], 0.06f), Geo.W(q[(i + 1) & 3], 0.06f), 0.035f, pale);
                    break;
                }
                case GhostKind.Gate:
                {
                    var p = a;
                    GlowBeam(g, Geo.W(p - GU * 0.45f, 0f), Geo.W(p - GU * 0.45f, 1.25f), 0.04f, pale);
                    GlowBeam(g, Geo.W(p + GU * 0.45f, 0f), Geo.W(p + GU * 0.45f, 1.25f), 0.04f, pale);
                    GlowBeam(g, Geo.W(p - GU * 0.5f, 1.2f), Geo.W(p + GU * 0.5f, 1.2f), 0.04f, pale);
                    break;
                }
            }
            return new Chunk { Mesh = g.M, Shadow = g.S };
        }

        /// <summary>Mesh for the static "needs repair" markers is done in the HUD; this returns the world points of
        /// breaches and badly damaged wall tiles (one per cluster) for it.</summary>
        public static List<Vector3> RepairMarks(Game game, int maxN = 12)
        {
            var o = new List<Vector3>();
            var S = game?.S;
            if (S == null) return o;
            var d = Data.Instance; int me = game.CurCol;
            void Add(int k)
            {
                var p = new Vector2(k % d.W + 0.5f, k / d.W + 0.5f);
                foreach (var e in o) if ((new Vector2(e.x, -e.z) - p).sqrMagnitude < 9f) return;
                if (o.Count < maxN) o.Add(Geo.W(p, 1.55f));
            }
            foreach (var kv in S.Breaches) if (kv.Value.Col == me && !S.Fences.ContainsKey(kv.Key)) Add(kv.Key);
            foreach (var kv in S.Fences) if (kv.Value.Col == me && kv.Value.Hp < game.FenceTileMax(kv.Key) * 0.5) Add(kv.Key);
            return o;
        }
    }
}
