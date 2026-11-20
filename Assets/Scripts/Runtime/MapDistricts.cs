// District mood (graphics idea #8): every district kind gets a subtle palette shift, and special lots get
// their own story props, so the city reads as places rather than one grey sprawl.
//  (a) palette: after ground, roads and lots of a chunk are built, every vertex colour is nudged by the
//      district under it (downtown faded signage, midtown commercial, industrial rust, waterfront cool,
//      suburbs warm, parkside green) or by the lot kind it stands on (hospital / clinic pale teal-white,
//      police / checkpoint cool blue-grey, shops faded signage, factory / warehouse rust). Avenues
//      (the "highway") are pulled toward scorched grey. Shifts stay within ~10 % so the bible palette holds.
//  (b) props, deterministic per lot / road cell, full LOD only: medical tents with red crosses around
//      hospitals and clinics, sawhorse barricades around police stations and checkpoints, rubble piles
//      in the yards of gutted supermarkets / shops, burned-out car shells along avenues.
// Visual only: no Game / sim state is read or changed. Hook: one line in MapGen.BuildChunk.
using System;
using System.Collections.Generic;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed partial class MapGen
    {
        // multiply tints per district kind (subtle)
        static Vector3 DistrictMul(string kind)
        {
            switch (kind)
            {
                case "downtown": return new Vector3(1.04f, 0.99f, 0.95f);    // faded signage, warm dust
                case "midtown": return new Vector3(1.02f, 1.0f, 0.97f);
                case "industrial": return new Vector3(1.07f, 0.97f, 0.88f);  // rust
                case "waterfront": return new Vector3(0.95f, 1.0f, 1.04f);   // cool, damp
                case "suburbs": return new Vector3(1.02f, 1.01f, 0.97f);
                case "parkside": return new Vector3(0.97f, 1.03f, 0.95f);    // greener
                default: return new Vector3(1f, 1f, 1f);
            }
        }

        // lot-kind overrides: (target colour, lerp amount, multiply)
        static readonly Color DM_MED = Hex("#9aa8a2"), DM_COP = Hex("#58616c"), DM_SIGN = Hex("#8a6a48"), DM_RUSTY = Hex("#7a4a2a"), DM_SCORCHED = Hex("#3c3934");

        static bool LotTint(string kind, out Color to, out float amt)
        {
            switch (kind)
            {
                case "hospital": to = DM_MED; amt = 0.28f; return true;
                case "clinic": case "pharmacy": to = DM_MED; amt = 0.18f; return true;
                case "police": case "checkpoint": to = DM_COP; amt = 0.26f; return true;
                case "supermarket": case "hardware": case "gun_shop": to = DM_SIGN; amt = 0.14f; return true;
                case "factory": case "warehouse": to = DM_RUSTY; amt = 0.12f; return true;
            }
            to = DM_MED; amt = 0f; return false;
        }

        /// <summary>Tints every vertex built so far in this chunk by district / lot kind, then adds district props.</summary>
        void DistrictPass(Geo g, int x0, int y0, int x1, int y1, List<int> lots)
        {
            var V = g.M.V; var C = g.M.C;
            int n = V.Count, lastT = int.MinValue, lastLot = -2;
            Vector3 mul = new Vector3(1f, 1f, 1f); Color to = DM_MED; float amt = 0f; bool ave = false, skip = false;
            for (int i = 0; i < n; i++)
            {
                var v = V[i];
                int tx = Mathf.FloorToInt(v.x), ty = Mathf.FloorToInt(-v.z);
                if (tx < 0 || ty < 0 || tx >= d.W || ty >= d.H) continue;
                int key = ty * d.W + tx;
                if (key != lastT)
                {
                    lastT = key;
                    int t = d.Tiles[key];
                    skip = t == Data.T_WATER || t == Data.T_VOID;
                    var ds = d.Districts[d.DistrictMap[key]];
                    mul = DistrictMul(ds.Kind);
                    int l = d.LotId[key];
                    if (l != lastLot)
                    {
                        lastLot = l;
                        if (l < 0 || _campLots.Contains(l) || !LotTint(d.Lots[l].Kind, out to, out amt)) amt = 0f;
                    }
                    ave = t == Data.T_AVE && v.y < 0.03f;
                    if (_campLots.Contains(l)) skip = true;   // the survivors' base keeps its own warm look
                }
                if (skip) continue;
                var c = C[i];
                float r = c.r / 255f, gg = c.g / 255f, b = c.b / 255f;
                if (amt > 0f) { r += (to.r - r) * amt; gg += (to.g - gg) * amt; b += (to.b - b) * amt; }
                else if (ave) { r += (DM_SCORCHED.r - r) * 0.16f; gg += (DM_SCORCHED.g - gg) * 0.16f; b += (DM_SCORCHED.b - b) * 0.16f; }
                C[i] = new Color32(B(r * mul.x), B(gg * mul.y), B(b * mul.z), c.a);
            }
            if (lo) return;
            foreach (int l in lots) DistrictLotProps(g, l);
            HighwayWrecks(g, x0, y0, x1, y1);
        }

        static byte B(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);

        void DistrictLotProps(Geo g, int l)
        {
            var info = _info[l];
            if (!info.Valid || _campLots.Contains(l) || ClearedRect(info.R)) return;
            var lot = d.Lots[l];
            string kind = lot.Kind;
            int mode = kind == "hospital" || kind == "clinic" ? 1 : kind == "police" || kind == "checkpoint" ? 2 : kind == "supermarket" || kind == "hardware" ? 3 : 0;
            if (mode == 0) return;
            var R = info.R;
            // props gather in front of the door: on the yard, sidewalk and street outside it
            var L = info.Lot;
            var door = new Vector2(lot.DoorX + 0.5f, lot.DoorY + 0.5f) - L.C;
            float du = Vector2.Dot(door, L.U) / Mathf.Max(0.1f, L.Hu), dv = Vector2.Dot(door, L.V) / Mathf.Max(0.1f, L.Hv);
            Vector2 outv; float half;
            if (Mathf.Abs(du) >= Mathf.Abs(dv)) { outv = L.U * (du < 0f ? -1f : 1f); half = L.Hu; } else { outv = L.V * (dv < 0f ? -1f : 1f); half = L.Hv; }
            var tan = new Vector2(-outv.y, outv.x);
            var edge = L.C + outv * half + tan * Vector2.Dot(door, tan) * 0.5f;
            int want = mode == 1 ? (kind == "hospital" ? 6 : 3) : mode == 2 ? 6 : 5;
            float spread = mode == 1 && kind == "hospital" ? 2.6f : 1.8f;
            int placed = 0;
            var used = new List<Vector2>();
            for (int k = 0; k < 40 && placed < want; k++)
            {
                var p = edge + outv * (0.25f + Hf(l, 650 + k) * 1.3f) + tan * ((Hf(l, 600 + k) * 2f - 1f) * spread);
                float clear = mode == 1 ? 0.75f : 0.35f;
                if (R.Contains(p, clear)) continue;
                int x = Mathf.FloorToInt(p.x), y = Mathf.FloorToInt(p.y);
                if (x < 0 || y < 0 || x >= d.W || y >= d.H) continue;
                int t = d.Tiles[y * d.W + x], ol = d.LotId[y * d.W + x];
                if (t == Data.T_WATER || t == Data.T_VOID || (ol >= 0 && ol != l)) continue;
                if (InBuilding(p, 0.3f) || !DryAt(p, 0.45f) || ClsAt(p) == C_BRIDGE) continue;   // v0.19 audit: props scale up 1.4-1.6x
                bool near = false; foreach (var u in used) if ((u - p).sqrMagnitude < (mode == 1 ? 1.6f : 0.4f)) { near = true; break; }
                if (near) continue;
                used.Add(p);
                Vector2 dir = Hf(l, 700 + k) < 0.7f ? tan : outv;
                int salt = l * 31 + k, v0 = g.M.Count;
                if (mode == 1) MedTent(g, p, dir, salt);
                else if (mode == 2) Sawhorse(g, p, dir, salt);
                else RubblePile(g, p, salt);
                float gy = GroundY(p), sc = mode == 1 ? 1.6f : mode == 2 ? 1.4f : 1.5f;   // scaled up to read at map zoom
                if (gy != 0f || sc != 1f)
                    for (int vi = v0; vi < g.M.Count; vi++)
                    {
                        var vv = g.M.V[vi];
                        g.M.V[vi] = new Vector3(p.x + (vv.x - p.x) * sc, vv.y * sc + gy, -p.y + (vv.z + p.y) * sc);
                    }
                placed++;
            }
            Stats.TryGetValue("props:" + kind, out long pn); Stats["props:" + kind] = pn + placed;
        }

        /// <summary>Surface height at a tile-space point (sidewalk / road / plaza overlays sit above the ground).</summary>
        float GroundY(Vector2 p)
        {
            int i = Mathf.FloorToInt((TU(p) - u0) / CELL), j = Mathf.FloorToInt((TV(p) - v0) / CELL);
            int c = Cls(i, j);
            return c == C_SIDE ? SW_Y : c == C_ROAD ? ROAD_Y : c == C_BRIDGE ? 0.06f : c == C_PLAZA ? PAVE_Y : 0f;
        }

        static readonly Color DM_CANVAS = Hex("#8f938a"), DM_CANVAS_D = Hex("#6f736a"), DM_RED_CROSS = Hex("#8a2e26");

        /// <summary>Field hospital tent: canvas box with a pitched roof, red cross on both long sides and the roof.</summary>
        void MedTent(Geo g, Vector2 p, Vector2 dir, int salt)
        {
            var side = new Vector2(-dir.y, dir.x);
            float hl = 0.42f + Hf(salt, 1) * 0.12f, hw = 0.26f, wh = 0.22f, rh = 0.42f;
            var r = new ORect(p, dir, hl, hw);
            Color cv = Lerp(DM_CANVAS, DM_CANVAS_D, Hf(salt, 2) * 0.6f);
            g.Box(r.Quad(), 0f, wh, Mul(cv, 0.9f), cv, 0.15f, false);
            g.Gable(r, wh, rh, cv, Mul(cv, 0.9f), 0.04f, 1);
            // red cross on each long wall
            for (int sgn = -1; sgn <= 1; sgn += 2)
            {
                var o = side * (sgn * (hw + 0.006f));
                var c = p + o;
                g.Wall(c - dir * 0.025f, c + dir * 0.025f, side * sgn, 0.05f, 0.19f, DM_RED_CROSS);
                g.Wall(c - dir * 0.075f, c + dir * 0.075f, side * sgn, 0.095f, 0.145f, DM_RED_CROSS);
            }
            // cross flat on the roof slope facing up (small raised plate near the ridge)
            var rc = p - side * (hw * 0.45f);
            float ry = Mathf.Lerp(wh, rh, 0.55f) + 0.012f;
            g.Flat(new ORect(rc, dir, 0.03f, 0.09f).Quad(), ry, DM_RED_CROSS);
            g.Flat(new ORect(rc, dir, 0.09f, 0.03f).Quad(), ry + 0.002f, DM_RED_CROSS);
            // a stretcher / supply crate beside it
            if (Hf(salt, 3) < 0.6f) g.BoxAt(p + side * (hw + 0.14f), dir, 0.08f, 0.06f, 0f, 0.09f, Hex("#5a5040"), Hex("#6a604c"));
            g.Shadow(r.Inset(-0.05f).Quad(), 0.3f, 0.32f);
        }

        /// <summary>Police sawhorse barricade: two A-frame legs and a striped board, sometimes knocked over.</summary>
        void Sawhorse(Geo g, Vector2 p, Vector2 dir, int salt)
        {
            float a = Mathf.Atan2(dir.y, dir.x) + (Hf(salt, 4) - 0.5f) * 0.6f;
            var dd = new Vector2(Mathf.Cos(a), Mathf.Sin(a)); var sd = new Vector2(-dd.y, dd.x);
            Color legC = Hex("#4a4c48"), white = Hex("#a8a090"), red = Hex("#8a2e26");
            bool down = Hf(salt, 5) < 0.25f;
            if (down)
            {
                for (int k = 0; k < 4; k++)
                    g.BoxAt(p + dd * ((k - 1.5f) * 0.12f), dd, 0.06f, 0.05f, 0f, 0.025f, (k & 1) == 0 ? red : white, (k & 1) == 0 ? red : white);
                g.Shadow(new ORect(p, dd, 0.26f, 0.06f).Quad(), 0.03f, 0.2f);
                return;
            }
            for (int e = -1; e <= 1; e += 2)
            {
                var lp = p + dd * (0.2f * e);
                g.Beam(Geo.W(lp - sd * 0.08f, 0f), Geo.W(lp, 0.2f), 0.022f, legC);
                g.Beam(Geo.W(lp + sd * 0.08f, 0f), Geo.W(lp, 0.2f), 0.022f, legC);
            }
            for (int k = 0; k < 4; k++)
            {
                var c = p + dd * ((k - 1.5f) * 0.1f);
                Color col = (k & 1) == 0 ? red : white;
                g.BoxAt(c, dd, 0.05f, 0.018f, 0.16f, 0.24f, col, Mul(col, 1.04f));
            }
            g.Shadow(new ORect(p, dd, 0.26f, 0.08f).Quad(), 0.2f, 0.28f);
        }

        /// <summary>Gutted-store rubble: a low mound of broken concrete with slabs, planks and a bent beam.</summary>
        void RubblePile(Geo g, Vector2 p, int salt)
        {
            float rad = 0.16f + Hf(salt, 6) * 0.12f;
            var rr = new ORect(p, new Vector2(Mathf.Cos(Hf(salt, 7) * 3f), Mathf.Sin(Hf(salt, 7) * 3f)), rad, rad * 0.8f);
            g.Frustum(rr.Quad(), 0f, rr.Inset(rad * 0.55f).Quad(), 0.06f + rad * 0.35f, RUBBLE_D, Lerp(RUBBLE_L, RUBBLE_D, 0.4f));
            for (int k = 0; k < 4; k++)
            {
                var q = p + new Vector2(Hf(salt, 10 + k) - 0.5f, Hf(salt, 20 + k) - 0.5f) * rad * 2.2f;
                float an = Hf(salt, 30 + k) * 6.28f; var d2 = new Vector2(Mathf.Cos(an), Mathf.Sin(an));
                float h = Hf(salt, 40 + k);
                if (h < 0.5f) g.BoxAt(q, d2, 0.07f + h * 0.08f, 0.05f, 0f, 0.04f + h * 0.06f, Lerp(RUBBLE_L, CHAR, 0.2f), RUBBLE_L);
                else if (h < 0.8f) g.BoxAt(q, d2, 0.18f, 0.02f, 0.01f, 0.03f, BOARD, Hex("#7a5e42"));
                else g.BoxAt(q, d2, 0.12f, 0.06f, 0f, 0.1f, Hex("#5a5040"), Hex("#6a604c"));   // crate / shelf chunk
            }
            if (Hf(salt, 50) < 0.5f)
            {
                float an = Hf(salt, 51) * 6.28f; var d2 = new Vector2(Mathf.Cos(an), Mathf.Sin(an));
                g.Beam(Geo.W(p - d2 * rad, 0.02f), Geo.W(p + d2 * rad * 0.6f, 0.2f), 0.025f, Lerp(RUST, CHAR, 0.3f));
            }
            g.Shadow(rr.Inset(-0.04f).Quad(), 0.1f, 0.26f);
        }

        /// <summary>Burned-out car shells scattered along avenues (deterministic per road cell).</summary>
        void HighwayWrecks(Geo g, int x0, int y0, int x1, int y1)
        {
            CellRange(x0, y0, x1, y1, out int i0, out int i1, out int j0, out int j1);
            for (int j = j0; j < j1; j++)
                for (int i = i0; i < i1; i++)
                {
                    if (Cls(i, j) != C_ROAD) continue;
                    int dir = Dir(i, j);
                    if (dir != 1 && dir != 2) continue;
                    var ctr = CellC(i, j);
                    if (!InRect(ctr, x0, y0, x1, y1) || TileAtF(ctr.x, ctr.y) != Data.T_AVE) continue;
                    int k = j * nu + i;
                    int run = dir == 1 ? runV[k] : runU[k], pos = dir == 1 ? posV[k] : posU[k];
                    if (run < 4 || (pos != run / 4 && pos != (run * 3) / 4)) continue;
                    if (Hf(k, 701) > 0.022f) continue;
                    if (ClearedRect(new ORect(ctr, GU, 0.7f, 0.4f))) continue;
                    var ax = dir == 1 ? GU : GV;
                    if (Hf(k, 702) < 0.5f) ax = -ax;
                    float an = Mathf.Atan2(ax.y, ax.x) + (Hf(k, 703) - 0.5f) * 0.7f;
                    BurnedShell(g, ctr, new Vector2(Mathf.Cos(an), Mathf.Sin(an)), k);
                }
        }

        void BurnedShell(Geo g, Vector2 p, Vector2 dir, int salt)
        {
            var side = new Vector2(-dir.y, dir.x);
            float y = ROAD_Y;
            // scorch mark on the asphalt
            var sc = new ORect(p, dir, 0.95f, 0.5f);
            g.Flat(sc.Quad(), y + 0.003f, Lerp(SCORCH, Hex("#3c3934"), 0.3f));
            Color body = Lerp(Hex("#2a2622"), Hex("#4c3022"), Hf(salt, 8) * 0.8f), dark = Hex("#151412");
            float hl = 0.64f, hw = 0.27f;
            // sits on its rims (no tyres), body slightly sunk and crooked
            g.Box(new ORect(p, dir, hl, hw).Quad(), y + 0.02f, y + 0.2f, body, Mul(body, 0.85f), 0.1f);
            var cb = new ORect(p - dir * 0.08f, dir, 0.36f, 0.25f); var ct = new ORect(p - dir * 0.12f, dir, 0.24f, 0.2f);
            g.Frustum(cb.Quad(), y + 0.2f, ct.Quad(), y + 0.36f, dark, Lerp(body, dark, 0.5f));
            // rims
            for (int a = -1; a <= 1; a += 2)
                g.BoxAt(p + dir * (hl * 0.62f * a), side, hw + 0.02f, 0.06f, y, y + 0.07f, Hex("#2e2a26"), Hex("#3a3632"));
            // popped bonnet / boot lid hanging open on some
            if (Hf(salt, 9) < 0.4f)
                g.Beam(Geo.W(p + dir * (hl - 0.05f), y + 0.21f), Geo.W(p + dir * (hl + 0.08f), y + 0.4f), 0.1f, Mul(body, 1.1f));
            g.Shadow(new ORect(p, dir, hl, hw).Quad(), 0.28f, 0.36f);
        }
    }
}
