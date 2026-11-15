// Vegetation, wrecks, street furniture and the dynamic survivor-camp layer for MapGen
// (art bible sections 2.3, 4.3, 5, 6).
using System;
using System.Collections.Generic;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed partial class MapGen
    {
        static readonly Color[] FOLIAGE = { Hex("#565c3b"), Hex("#656835"), Hex("#5a6234"), Hex("#4e5a36"), Hex("#606a3c") };
        static readonly Color CONIFER = Hex("#3a4a2c"), BARK = Hex("#4f3a28"), DEAD = Hex("#5a5040"), BUSH = Hex("#4e5632");

        // ================= vegetation =================
        void AddVegetation(Geo g, int x0, int y0, int x1, int y1)
        {
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    int t = d.Tiles[y * d.W + x];
                    if (t == Data.T_WATER || t == Data.T_VOID) continue;
                    float r = Hf(x * 3 + 17, y * 5 + 3);
                    if (r > 0.4f) continue;
                    var p = new Vector2(x + 0.15f + Hf(x, y * 7 + 1) * 0.7f, y + 0.15f + Hf(x * 11 + 2, y) * 0.7f);
                    if (_clear.Count > 0 && Cleared(p)) continue;
                    int salt = y * 4099 + x;
                    if (InCity(p.x, p.y))
                    {
                        if (ClsAt(p) != C_NONE) continue;           // roads / sidewalks / plazas
                        float tree = 0f, bush = 0f;
                        switch (t)
                        {
                            case Data.T_PARK: tree = 0.17f; bush = 0.32f; break;
                            case Data.T_BLD:
                            {
                                int l = d.LotId[y * d.W + x];
                                if (l < 0 || l >= _info.Length || !_info[l].Valid) { tree = 0.04f; bush = 0.1f; break; }
                                var inf = _info[l];
                                if (inf.A == Arch.Parking || inf.A == Arch.Farm || inf.A == Arch.Gas) { bush = 0.04f; break; }
                                if (inf.R.Contains(p, 0.25f)) continue;
                                if (inf.A == Arch.House || inf.A == Arch.Hut) { tree = 0.13f; bush = 0.3f; }
                                else { tree = 0.06f; bush = 0.22f; }
                                if (!inf.Lot.Contains(p, -0.1f)) { tree *= 0.5f; }
                                break;
                            }
                            case Data.T_PLAZA: tree = 0.02f; bush = 0.05f; break;
                            case Data.T_ROAD: case Data.T_AVE: case Data.T_BRIDGE: break;
                            default: tree = 0.05f; bush = 0.1f; break;
                        }
                        if (r < tree) { if (lo) LoTree(g, p, salt); else Tree(g, p, salt, 0.85f + Hf(salt, 3) * 0.3f, false); }
                        else if (r < bush && !lo) Bush(g, p, salt);
                    }
                    else
                    {
                        float tree = 0f, bush = 0f;
                        switch (t)
                        {
                            case Data.T_FOREST: tree = 0.10f; bush = 0.15f; break;
                            case Data.T_MEADOW: tree = 0.015f; bush = 0.05f; break;
                            case Data.T_FIELD: tree = 0.003f; break;
                            case Data.T_DIRT: tree = 0.01f; bush = 0.03f; break;
                            case Data.T_BLD: tree = 0.02f; bush = 0.06f; break;
                        }
                        if (t == Data.T_BLD)
                        {
                            int l = d.LotId[y * d.W + x];
                            if (l >= 0 && l < _info.Length && _info[l].Valid && _info[l].R.Contains(p, 0.25f)) continue;
                        }
                        if (r < tree) { if (lo) LoTree(g, p, salt); else ForestTree(g, p, salt); }
                        else if (r < bush && !lo) Bush(g, p, salt);
                    }
                }
        }

        /// <summary>Deciduous / conifer / dead tree, ~7-10 m. ruin=true: young tree growing out of a shell.</summary>
        void Tree(Geo g, Vector2 p, int salt, float scale, bool ruin)
        {
            float H = (1.9f + Hf(salt, 71) * 0.8f) * scale;
            float R = (0.62f + Hf(salt, 72) * 0.3f) * scale;
            float kind = Hf(salt, 73);
            if (kind < 0.05f && !ruin)
            {
                g.Beam(Geo.W(p, 0f), Geo.W(p, H * 0.8f), 0.04f * scale, DEAD);
                for (int k = 0; k < 3; k++)
                {
                    float a = Hf(salt, 80 + k) * 6.28f;
                    var b = Geo.W(p, H * (0.45f + k * 0.12f));
                    g.Beam(b, b + new Vector3(Mathf.Cos(a) * R * 0.8f, R * 0.6f, Mathf.Sin(a) * R * 0.8f), 0.02f * scale, DEAD);
                }
                return;
            }
            g.Beam(Geo.W(p, 0f), Geo.W(p, H * 0.42f), 0.05f * scale, BARK);
            if (kind < 0.22f)
            {
                Color c = Lerp(CONIFER, FOLIAGE[1], Hf(salt, 74) * 0.3f);
                g.Cone(p, R * 0.75f, H * 0.25f, H * 0.7f, c, salt * 0.3f, 6);
                g.Cone(p, R * 0.58f, H * 0.5f, H * 0.9f, Mul(c, 1.05f), salt * 0.3f + 0.5f, 6);
                g.Cone(p, R * 0.38f, H * 0.72f, H * 1.08f, Mul(c, 1.1f), salt * 0.3f + 1f, 6);
                g.ShadowBlob(p, R * 0.6f, H * 0.55f, 0.3f);
                return;
            }
            Color f = FOLIAGE[(int)(Hf(salt, 75) * FOLIAGE.Length) % FOLIAGE.Length];
            var c0 = Geo.W(p, H * 0.64f);
            g.Blob(c0, R, R * 0.8f, f, salt);
            float a0 = Hf(salt, 76) * 6.28f;
            g.Blob(c0 + new Vector3(Mathf.Cos(a0) * R * 0.5f, -R * 0.2f, Mathf.Sin(a0) * R * 0.5f), R * 0.7f, R * 0.55f, Mul(f, 0.94f), salt + 1, 6);
            if (Hf(salt, 77) < 0.6f)
                g.Blob(c0 + new Vector3(-Mathf.Cos(a0) * R * 0.45f, R * 0.15f, -Mathf.Sin(a0) * R * 0.45f), R * 0.6f, R * 0.5f, Mul(f, 1.06f), salt + 2, 6);
            g.ShadowBlob(p, R * 0.95f, H * 0.62f, 0.3f);
        }

        void LoTree(Geo g, Vector2 p, int salt)
        {
            float H = 2.2f + Hf(salt, 71) * 1.0f, R = 0.6f + Hf(salt, 72) * 0.3f;
            bool con = Hf(salt, 73) < 0.4f;
            if (con) g.Cone(p, R * 0.75f, 0.1f, H, Lerp(CONIFER, Hex("#24301e"), Hf(salt, 74) * 0.5f), salt * 0.3f, 4);
            else g.Blob(Geo.W(p, H * 0.62f), R, R * 0.75f, FOLIAGE[(int)(Hf(salt, 75) * FOLIAGE.Length) % FOLIAGE.Length], salt, 4);
            g.ShadowBlob(p, R * 0.85f, H * 0.55f, 0.28f);
        }

        void ForestTree(Geo g, Vector2 p, int salt)
        {
            float H = 2.2f + Hf(salt, 71) * 1.4f;
            float R = 0.5f + Hf(salt, 72) * 0.35f;
            if (Hf(salt, 73) < 0.5f)
            {
                Color c = Lerp(CONIFER, Hex("#24301e"), Hf(salt, 74) * 0.5f);
                g.Cone(p, R * 0.8f, H * 0.15f, H * 0.68f, c, salt * 0.3f, 5);
                g.Cone(p, R * 0.55f, H * 0.45f, H, Mul(c, 1.08f), salt * 0.3f + 0.6f, 5);
            }
            else
            {
                Color f = Lerp(FOLIAGE[(int)(Hf(salt, 75) * FOLIAGE.Length) % FOLIAGE.Length], CONIFER, 0.25f);
                g.Beam(Geo.W(p, 0f), Geo.W(p, H * 0.3f), 0.05f, BARK);
                g.Blob(Geo.W(p, H * 0.55f), R * 1.15f, R * 0.85f, f, salt, 6);
            }
            g.ShadowBlob(p, R * 0.8f, H * 0.5f, 0.26f);
        }

        void Bush(Geo g, Vector2 p, int salt)
        {
            float R = 0.18f + Hf(salt, 81) * 0.2f;
            Color c = Lerp(BUSH, FOLIAGE[(int)(Hf(salt, 82) * FOLIAGE.Length) % FOLIAGE.Length], Hf(salt, 83));
            g.Blob(Geo.W(p, R * 0.45f), R, R * 0.7f, c, salt + 5, 5);
        }

        void Tuft(Geo g, Vector2 p, int salt, float y)
        {
            Color c = Lerp(LAWN, DRYGRASS, Hf(salt, 3));
            for (int k = 0; k < 3; k++)
            {
                float a = k * 2.1f + salt;
                var dd = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 0.07f;
                g.Tri(Geo.W(p - dd, y), Geo.W(p + dd, y), Geo.W(p + dd * 0.3f, y + 0.12f + Hf(salt, k) * 0.08f), c, new Vector3(-dd.y, 0.5f, -dd.x));
            }
        }

        // ================= vehicles & debris =================
        void AddCar(Geo g, Vector2 p, float ang, int salt, float y)
        {
            var dir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
            float kind = Hf(salt, 1);
            Color body = CAR_PAINT[(int)(Hf(salt, 2) * CAR_PAINT.Length) % CAR_PAINT.Length];
            bool rust = Hf(salt, 3) < 0.45f, burnt = Hf(salt, 4) < 0.1f;
            if (rust) body = Lerp(body, Hex("#654530"), 0.75f);
            if (burnt) body = Lerp(Hex("#2a2622"), Hex("#4c3022"), Hf(salt, 5));
            Color glass = burnt ? Hex("#151412") : Hex("#262a2a");
            Color tyre = Hex("#1e1e1c");
            float sink = Hf(salt, 6) < 0.3f ? 0.03f : 0f;
            float hl, hw;
            if (kind < 0.55f)
            {
                hl = 0.64f; hw = 0.26f;
                g.Box(new ORect(p, dir, hl, hw).Quad(), y + 0.07f - sink, y + 0.24f, body, body, 0f);
                var cb = new ORect(p - dir * 0.06f, dir, 0.34f, 0.24f); var ct = new ORect(p - dir * 0.1f, dir, 0.22f, 0.2f);
                g.Frustum(cb.Quad(), y + 0.24f, ct.Quad(), y + 0.4f, glass, Mul(body, 0.95f));
            }
            else if (kind < 0.8f)
            {
                hl = 0.68f; hw = 0.28f;
                g.Box(new ORect(p, dir, hl, hw).Quad(), y + 0.08f - sink, y + 0.28f, body, body, 0f);
                var cb = new ORect(p - dir * 0.12f, dir, 0.5f, 0.27f); var ct = new ORect(p - dir * 0.16f, dir, 0.42f, 0.24f);
                g.Frustum(cb.Quad(), y + 0.28f, ct.Quad(), y + 0.48f, glass, Mul(body, 0.95f));
            }
            else if (kind < 0.92f)
            {
                hl = 0.76f; hw = 0.29f;   // pickup
                g.Box(new ORect(p, dir, hl, hw).Quad(), y + 0.09f - sink, y + 0.3f, body, Mul(body, 0.6f), 0f);
                var cb = new ORect(p + dir * 0.12f, dir, 0.24f, 0.28f); var ct = new ORect(p + dir * 0.08f, dir, 0.18f, 0.25f);
                g.Frustum(cb.Quad(), y + 0.3f, ct.Quad(), y + 0.52f, glass, Mul(body, 0.95f));
                var bed = new ORect(p - dir * 0.4f, dir, 0.34f, 0.28f);
                foreach (int f in new[] { 0, 2, 3 })
                {
                    FaceEdge(bed, f, out var a, out var b, out var o);
                    g.Box(new[] { a, b, b - o * 0.03f, a - o * 0.03f }, y + 0.3f, y + 0.42f, body, body);
                }
            }
            else
            {
                hl = 0.74f; hw = 0.29f;   // van
                g.Box(new ORect(p - dir * 0.1f, dir, 0.64f, hw).Quad(), y + 0.08f - sink, y + 0.66f, body, Mul(body, 1.02f), 0f);
                g.Box(new ORect(p + dir * 0.62f, dir, 0.12f, hw * 0.96f).Quad(), y + 0.08f - sink, y + 0.32f, body, body, 0f);
                for (int s = -1; s <= 1; s += 2)
                {
                    var a = p + dir * 0.45f + new Vector2(-dir.y, dir.x) * (hw + 0.006f) * s; var b = a - dir * 0.3f;
                    g.Wall(a, b, new Vector2(-dir.y, dir.x) * s, y + 0.38f, y + 0.58f, glass);
                }
                g.Face(Geo.W(p + dir * 0.54f + new Vector2(-dir.y, dir.x) * hw, y + 0.66f), Geo.W(p + dir * 0.54f - new Vector2(-dir.y, dir.x) * hw, y + 0.66f),
                       Geo.W(p + dir * 0.6f - new Vector2(-dir.y, dir.x) * hw, y + 0.34f), Geo.W(p + dir * 0.6f + new Vector2(-dir.y, dir.x) * hw, y + 0.34f), glass, new Vector3(dir.x, 0.4f, -dir.y));
            }
            // wheels (two axle blocks, a little wider than the body)
            g.Box(new ORect(p + dir * (hl * 0.62f), dir, 0.1f, hw + 0.025f).Quad(), y - sink, y + 0.17f - sink, tyre, tyre);
            g.Box(new ORect(p - dir * (hl * 0.62f), dir, 0.1f, hw + 0.025f).Quad(), y - sink, y + 0.17f - sink, tyre, tyre);
            // lights
            var side = new Vector2(-dir.y, dir.x);
            for (int s = -1; s <= 1; s += 2)
            {
                var hp = p + dir * (hl + 0.004f) + side * (hw * 0.7f * s);
                g.Wall(hp - side * 0.05f, hp + side * 0.05f, dir, y + 0.17f, y + 0.22f, Hex("#a8a490"));
                var tp = p - dir * (hl + 0.004f) + side * (hw * 0.7f * s);
                g.Wall(tp - side * 0.05f, tp + side * 0.05f, -dir, y + 0.17f, y + 0.22f, Hex("#6a2a24"));
            }
            g.Shadow(new ORect(p, dir, hl, hw).Quad(), 0.32f, 0.38f);
        }

        void AddBarrier(Geo g, Vector2 p, Vector2 dir, int salt, float y)
        {
            float a = Mathf.Atan2(dir.y, dir.x) + (Hf(salt, 3) - 0.5f) * 0.5f;
            var dd = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            bool striped = Hf(salt, 4) < 0.35f;
            for (int k = 0; k < (striped ? 4 : 1); k++)
            {
                float hl = striped ? 0.11f : 0.43f;
                var c = striped ? p + dd * ((k - 1.5f) * 0.22f) : p;
                Color col = striped ? ((k & 1) == 0 ? Hex("#8a2e26") : Hex("#a8a090")) : Hex("#7d7061");
                g.Frustum(new ORect(c, dd, hl, 0.09f).Quad(), y, new ORect(c, dd, hl, 0.035f).Quad(), y + 0.23f, col, Mul(col, 1.05f));
            }
            g.Shadow(new ORect(p, dd, 0.43f, 0.09f).Quad(), 0.2f, 0.32f);
        }

        void Debris(Geo g, Vector2 p, long salt, float y)
        {
            for (int k = 0; k < 3; k++)
            {
                var q = p + new Vector2(Hf(salt, 90 + k) - 0.5f, Hf(salt, 93 + k) - 0.5f) * 0.4f;
                float a = Hf(salt, 96 + k) * 6.28f;
                var dd = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                float h = Hf(salt, 99 + k);
                if (h < 0.4f) g.BoxAt(q, dd, 0.05f + h * 0.08f, 0.04f, y, y + 0.04f + h * 0.05f, Lerp(RUBBLE_L, CURB, 0.4f), RUBBLE_L);
                else if (h < 0.7f) g.BoxAt(q, dd, 0.2f, 0.02f, y, y + 0.02f, BOARD, Hex("#8a6a48"));
                else g.Cyl(q, 0.06f, y, y + 0.05f, Hex("#1e1e1c"), Hex("#2a2a28"), 7);
            }
        }

        // ================= street furniture =================
        static readonly Color POLE = Hex("#4a4c48"), WOOD_POLE = Hex("#4a3a2a");

        void StreetFurniture(Geo g, int i, int j)
        {
            // only on curb cells: find the adjacent road and its direction
            int rd = 0; Vector2 toRoad = Vector2.zero; int ni = 0, nj = 0;
            for (int s = 0; s < 4 && rd == 0; s++)
            {
                ni = i + (s == 0 ? -1 : s == 1 ? 1 : 0); nj = j + (s == 2 ? -1 : s == 3 ? 1 : 0);
                int dr = Dir(ni, nj);
                if (dr == 1 && (s == 2 || s == 3)) { rd = 1; toRoad = s == 2 ? -GV : GV; }
                else if (dr == 2 && (s == 0 || s == 1)) { rd = 2; toRoad = s == 0 ? -GU : GU; }
            }
            var c = CellC(i, j);
            if (_clear.Count > 0 && Cleared(c)) return;
            long hs = (long)i * 6151 + j * 3079;
            if (rd == 0)
            {
                // inner sidewalk: occasional street tree, bins, benches
                float r = Hf(hs, 7);
                if (r < 0.035f) { g.Box(new ORect(c, GU, 0.15f, 0.15f).Quad(), SW_Y, SW_Y + 0.01f, Hex("#3a3226"), Hex("#3a3226")); Tree(g, c, (int)(hs & 0xffff), 0.7f, false); }
                else if (r < 0.045f) g.Cyl(c, 0.08f, SW_Y, SW_Y + 0.26f, Hex("#4a4a44"), Hex("#2e2e2a"), 7);
                return;
            }
            int along = rd == 1 ? i : j;
            int sideK = (toRoad.x + toRoad.y) > 0 ? 1 : 0;
            int cross = rd == 1 ? j : i;
            // lamps: every 7 tiles, staggered sides
            if ((along + sideK * 7) % 14 == 0)
                Lamp(g, c - toRoad * 0.1f, toRoad, (int)(hs & 0xffff), SW_Y);
            // power poles + wires along one side
            else if (sideK == 0 && (along % 12) == 5)
            {
                Vector2 dir = rd == 1 ? GU : GV;
                PowerPole(g, c - toRoad * 0.15f, dir, (int)(hs & 0xffff));
                int ai = rd == 1 ? i + 12 : i, aj = rd == 1 ? j : j + 12;
                if (Cls(ai, aj) == C_SIDE && Hf(hs, 9) > 0.15f)
                {
                    var c2 = CellC(ai, aj) - toRoad * 0.15f;
                    var side = new Vector2(-dir.y, dir.x);
                    for (int s = -1; s <= 1; s += 2)
                    {
                        var a = Geo.W(c + side * (0.24f * s) - toRoad * 0.15f + toRoad * 0.15f, 2.43f);
                        var b = Geo.W(c2 + side * (0.24f * s), 2.43f);
                        var m = (a + b) * 0.5f + Vector3.down * 0.22f;
                        g.Beam(a, m, 0.006f, Hex("#1e1e1c")); g.Beam(m, b, 0.006f, Hex("#1e1e1c"));
                    }
                }
            }
            else if ((along * 7 + cross) % 37 == 3) g.Cyl(c, 0.04f, SW_Y, SW_Y + 0.22f, Hex("#7a2e24"), Hex("#8a8a84"), 7);   // hydrant
            else if ((along * 3 + cross) % 41 == 7) Sign(g, c - toRoad * 0.05f, toRoad, (int)(hs & 0xffff));
        }

        void Lamp(Geo g, Vector2 p, Vector2 toRoad, int salt, float y)
        {
            float lean = Hf(salt, 1) < 0.15f ? 0.25f : 0f;
            var b = Geo.W(p, y); var t = Geo.W(p + toRoad * lean, y + 2.2f);
            g.Beam(b, t, 0.022f, POLE);
            var arm = t + new Vector3(toRoad.x * 0.45f, 0.05f, -toRoad.y * 0.45f);
            g.Beam(t, arm, 0.012f, POLE);
            var hp = p + toRoad * (lean + 0.45f);
            g.BoxAt(hp, toRoad, 0.1f, 0.045f, y + 2.17f, y + 2.23f, POLE, Hex("#5a5c58"));
            g.Shadow(new ORect(p, toRoad, 0.03f, 0.03f).Quad(), 1.6f, 0.2f);
        }

        void PowerPole(Geo g, Vector2 p, Vector2 dir, int salt)
        {
            var side = new Vector2(-dir.y, dir.x);
            g.Beam(Geo.W(p, 0f), Geo.W(p, 2.6f), 0.035f, WOOD_POLE);
            g.Beam(Geo.W(p - side * 0.28f, 2.45f), Geo.W(p + side * 0.28f, 2.45f), 0.018f, WOOD_POLE);
            if (Hf(salt, 3) < 0.3f) g.Cyl(p + dir * 0.08f, 0.07f, 2.0f, 2.3f, Hex("#5a5c58"), Hex("#4a4c48"), 6);
            g.Shadow(new ORect(p, dir, 0.04f, 0.04f).Quad(), 1.8f, 0.2f);
        }

        void Sign(Geo g, Vector2 p, Vector2 toRoad, int salt)
        {
            g.Beam(Geo.W(p, SW_Y), Geo.W(p, SW_Y + 0.75f), 0.01f, Hex("#6a6a66"));
            var side = new Vector2(-toRoad.y, toRoad.x);
            float bend = Hf(salt, 2) < 0.25f ? 0.4f : 0f;
            var dd = (side + toRoad * bend).normalized;
            Color plate = Hf(salt, 3) < 0.5f ? Hex("#8a2e26") : Hex("#2e5a3a");
            g.Box(new ORect(p + toRoad * 0.012f, dd, 0.1f, 0.006f).Quad(), SW_Y + 0.6f, SW_Y + 0.8f, plate, plate);
        }

        // ================= dynamic layer: camps, markers, lit windows =================
        // ---- camp yards: computed once before the static build so the chunks leave the yard clear ----
        readonly List<ORect> _clear = new List<ORect>();
        readonly HashSet<int> _campLots = new HashSet<int>();

        /// <summary>Call before BuildChunk: buildings, trees and road props inside camp yards are left out.</summary>
        public void SetCamps(Game game)
        {
            _clear.Clear(); _campLots.Clear();
            if (game?.S == null) return;
            foreach (var b in game.AllBases())
            {
                CampRect(b, out var c, out float half, out _);
                _clear.Add(new ORect(c, GU, half + 0.15f, half + 0.15f));
                if (b.Lot >= 0 && b.Lot < _info.Length)
                {
                    _campLots.Add(b.Lot);
                    // survivors hole up in a sound building: no collapse / shell for the camp itself
                    var inf = _info[b.Lot];
                    if (inf.Valid && inf.Dmg > 1) { inf.Dmg = 1; if (inf.Top < inf.WallTop) inf.Top = inf.WallTop + 0.17f; _info[b.Lot] = inf; LotH[b.Lot] = inf.Top; }
                }
            }
        }

        /// <summary>City axis (+-U / +-V) pointing most toward the default camera (MapView.YAW = 75).</summary>
        Vector2 CampFront()
        {
            var t = new Vector2(-0.966f, 0.259f);   // tile-space direction toward the camera at yaw 75
            Vector2 best = -GV; float bd = -9f;
            foreach (var a in new[] { GU, -GU, GV, -GV }) { float dd = Vector2.Dot(a, t); if (dd > bd) { bd = dd; best = a; } }
            return best;
        }

        bool Cleared(Vector2 p)
        {
            for (int i = 0; i < _clear.Count; i++) if (_clear[i].Contains(p)) return true;
            return false;
        }

        bool ClearedRect(ORect r)
        {
            if (_clear.Count == 0) return false;
            for (int i = 0; i < _clear.Count; i++) if (Overlap(_clear[i], r)) return true;
            return false;
        }

        /// <summary>Oriented rectangles overlap (separating axis test). v0.11.4a: any building can become the camp,
        /// so big neighbours that cross the yard edge without a corner inside it must be cleared too.</summary>
        static bool Overlap(ORect a, ORect b)
        {
            var d = b.C - a.C;
            foreach (var ax in new[] { a.U, a.V, b.U, b.V })
            {
                float ra = a.Hu * Mathf.Abs(Vector2.Dot(a.U, ax)) + a.Hv * Mathf.Abs(Vector2.Dot(a.V, ax));
                float rb = b.Hu * Mathf.Abs(Vector2.Dot(b.U, ax)) + b.Hv * Mathf.Abs(Vector2.Dot(b.V, ax));
                if (Mathf.Abs(Vector2.Dot(d, ax)) > ra + rb) return false;
            }
            return true;
        }

        void CampRect(Base b, out Vector2 c, out float half, out ORect? bld)
        {
            bool inBld = b.Lot >= 0 && b.Lot < _info.Length && _info[b.Lot].Valid && _info[b.Lot].Floors > 0;
            bld = null;
            if (inBld)
            {
                var inf = _info[b.Lot];
                bld = inf.R;
                half = Mathf.Max(inf.R.Hu, inf.R.Hv) + (b.Main ? 1.5f : 0.9f);
                // shift the yard toward the default camera so the fire and gate face the viewer
                c = inf.R.C + CampFront() * (b.Main ? 0.9f : 0.5f);
            }
            else { c = new Vector2(b.Pos.x + 0.5f, b.Pos.y + 0.5f); half = b.Main ? 3.0f : 2.0f; }
        }

        public Chunk BuildDynamic(Game game)
        {
            var g = new Geo(new MeshData(), new MeshData());
            if (game?.S == null) return new Chunk { Mesh = g.M, Shadow = g.S };
            int me = game.CurCol;
            var conv = game.S.Conv;
            var lit = game.LitLots();
            foreach (var kv in conv) Marker(g, kv.Key, Hex("#6a9a4a"));
            foreach (var kv in lit) if (!conv.ContainsKey(kv.Key)) Marker(g, kv.Key, kv.Value == me ? Hex("#d8a840") : Hex("#b04a3a"));
            foreach (var b in game.AllBases())
            {
                bool mine = b.Col == me;
                CampRect(b, out var c, out float half, out var bld);
                if (bld.HasValue && mine) LitWindows(g, b.Lot);
                Camp(g, c, half, b.Id, b.Main, bld, mine);
            }
            return new Chunk { Mesh = g.M, Shadow = g.S };
        }

        void Marker(Geo g, int l, Color c)
        {
            if (l < 0 || l >= _info.Length || !_info[l].Valid) return;
            var inf = _info[l];
            float top = inf.Top;
            var p = inf.R.C;
            g.Beam(Geo.W(p, top - 0.1f), Geo.W(p, top + 0.85f), 0.012f, Hex("#3a3631"));
            var f0 = p + inf.R.U * 0.02f; var f1 = p + inf.R.U * 0.42f;
            g.QuadRaw(Geo.W(f0, top + 0.55f), Geo.W(f1, top + 0.58f), Geo.W(f1, top + 0.8f), Geo.W(f0, top + 0.83f), c, Mul(c, 0.85f), Mul(c, 0.9f), c);
        }

        /// <summary>Warm lit windows over a camp building (the base must be the warmest spot on screen).</summary>
        void LitWindows(Geo g, int l)
        {
            var info = _info[l];
            if (info.Dmg >= 3) return;
            switch (info.A)
            {
                case Arch.House: case Arch.Hut: case Arch.Barn:
                {
                    var R = info.R;
                    if (R.Hu < R.Hv) R = new ORect(R.C, R.V, R.Hv, R.Hu);
                    for (int f = 0; f < 4; f++)
                    {
                        FaceEdge(R, f, out var a, out var b, out var o);
                        float len = (b - a).magnitude; var dir = (b - a) / len;
                        bool gableEnd = f == 1 || f == 3;
                        int n = gableEnd ? 1 : Mathf.Max(1, Mathf.RoundToInt(len / 0.8f));
                        for (int fl = 0; fl < info.Floors; fl++)
                        {
                            float y0 = 0.08f + fl * FLOOR * 0.94f + 0.26f;
                            for (int k = 0; k < n; k++)
                                if (Hf(l * 37 + f * 5 + fl, k + 11) < 0.7f) Pane(g, a + dir * (len / n * (k + 0.5f)), dir, o, 0.12f, y0, y0 + 0.36f, LIT_RIM, LIT_CORE, 0.016f, true);
                        }
                    }
                    break;
                }
                case Arch.Parking: case Arch.Farm: case Arch.Church: case Arch.Gas: case Arch.Industrial:
                    break;
                default:
                {
                    var R = info.R;
                    if (Hf(l, 15) < 0.5f) R = new ORect(R.C, -R.U, R.Hu, R.Hv);
                    for (int f = 0; f < 4; f++) Facade(g, l, f - 8, R, f, info, true);
                    break;
                }
            }
        }

        static readonly Color LOG = Hex("#5e4530"), LOG_SHADE = Hex("#3a2a1e"), LOG_TIP = Hex("#8a6a48"), TIMBER = Hex("#443226");
        static readonly Color YARD = Hex("#5e4c34"), YARD_DARK = Hex("#3a2c1c"), FIRE_GLOW = Hex("#c07a34");

        /// <summary>Survivor FOB (bible section 6, tier 1): palisade with gate, watchtower, cabin with lit
        /// windows, campfire with a warm light pool, crates, barrels, sandbags, flag, an armoured truck.</summary>
        void Camp(Geo g, Vector2 c, float half, int salt, bool main, ORect? bld, bool mine)
        {
            // V points away from the gate: the gate / fire side faces the camera
            var V = -CampFront(); var U = new Vector2(V.y, -V.x);
            var yard = new ORect(c, U, half, half);
            // fire position: centre of the yard, or between the building front and the gate
            var fire = CampFireAt(c, half, U, V, bld);   // shared with Layout() (MapGen.Select.cs)
            // yard ground: packed dirt with a warm pool around the fire
            const int N = 10;
            float y = 0.049f;
            for (int a = 0; a < N; a++)
                for (int b2 = 0; b2 < N; b2++)
                {
                    var p00 = yard.P(-1f + 2f * a / N, -1f + 2f * b2 / N); var p10 = yard.P(-1f + 2f * (a + 1) / N, -1f + 2f * b2 / N);
                    var p11 = yard.P(-1f + 2f * (a + 1) / N, -1f + 2f * (b2 + 1) / N); var p01 = yard.P(-1f + 2f * a / N, -1f + 2f * (b2 + 1) / N);
                    g.Flat(p00, p10, p11, p01, y, YardCol(p00, fire, yard, main), YardCol(p10, fire, yard, main), YardCol(p11, fire, yard, main), YardCol(p01, fire, yard, main));
                }
            // palisade
            for (int side = 0; side < 4; side++)
            {
                Vector2 along = side % 2 == 0 ? U : V, nrm = side % 2 == 0 ? V : U;
                float sg = side < 2 ? -1f : 1f;
                int n = Mathf.RoundToInt(half * 2f / 0.105f);
                for (int k = 0; k < n; k++)
                {
                    float t = -half + (k + 0.5f) * (2f * half / n);
                    if (side == 0 && Mathf.Abs(t) < 0.5f) continue;                  // gate opening
                    var p = c + nrm * (half * sg) + along * t;
                    float h = 0.88f + Hf(salt * 31 + side * 977, k) * 0.12f;
                    Color lc = Lerp(LOG, LOG_SHADE, Hf(salt + side, k * 3 + 1) * 0.4f);
                    if (Hf(salt + side * 5, k) < 0.08f) lc = Lerp(LOG_TIP, LOG, 0.4f);   // fresh repair
                    var q = new ORect(p, along, 0.05f, 0.05f).Quad();
                    g.Box(q, 0f, h, lc, lc, 0.2f, false);
                    g.Pyramid(q, h, p, h + 0.12f, Lerp(lc, LOG_TIP, 0.5f));
                }
                // inner binding rails
                var r0 = c + nrm * (half * sg - 0.07f * sg) - along * half; var r1 = c + nrm * (half * sg - 0.07f * sg) + along * half;
                if (side == 0)
                {
                    var g0 = c + nrm * (half * sg - 0.07f * sg) - along * 0.5f; var g1 = c + nrm * (half * sg - 0.07f * sg) + along * 0.5f;
                    foreach (float yy in new[] { 0.25f, 0.7f }) { g.Beam(Geo.W(r0, yy), Geo.W(g0, yy), 0.02f, TIMBER); g.Beam(Geo.W(g1, yy), Geo.W(r1, yy), 0.02f, TIMBER); }
                }
                else foreach (float yy in new[] { 0.25f, 0.7f }) g.Beam(Geo.W(r0, yy), Geo.W(r1, yy), 0.02f, TIMBER);
                var e0 = c + nrm * (half * sg) - along * half; var e1 = c + nrm * (half * sg) + along * half;
                g.Shadow(new[] { e0, e1, e1 + nrm * 0.05f, e0 + nrm * 0.05f }, 0.95f, 0.34f);
            }
            // firing step (v0.11.4a): plank walkway inside the palisade so guards on duty can shoot over it
            FiringStep(g, c, half, U, V);
            // gate: tall posts, doors swung open, plank sign
            var gc = c - V * half;
            foreach (int s in new[] { -1, 1 })
            {
                var gp = gc + U * (0.55f * s);
                g.Cyl(gp, 0.075f, 0f, 1.25f, LOG, LOG_TIP, 6);
                var hinge = gc + U * (0.5f * s);
                var open = hinge + (U * (-0.32f * s) - V * 0.32f);
                g.Box(new[] { hinge, open, open + V * 0.04f, hinge + V * 0.04f }, 0.02f, 0.9f, Lerp(LOG, LOG_TIP, 0.25f), LOG_TIP);
            }
            g.Beam(Geo.W(gc - U * 0.6f, 1.2f), Geo.W(gc + U * 0.6f, 1.2f), 0.04f, TIMBER);
            g.Box(new ORect(gc - V * 0.02f, U, 0.3f, 0.02f).Quad(), 1.0f, 1.16f, BOARD, BOARD);
            // sandbag nests either side of the gate (outside)
            foreach (int s in new[] { -1, 1 })
                for (int course = 0; course < 3; course++)
                    for (int k = 0; k < 4 - course; k++)
                    {
                        var sp = gc - V * 0.25f + U * (s * (0.75f + k * 0.17f + course * 0.085f));
                        Color sc = Lerp(Hex("#8a7a54"), Hex("#5a4e36"), Hf(salt + course * 13, k + s * 7) * 0.6f);
                        g.BoxAt(sp, U, 0.085f, 0.06f, course * 0.05f, course * 0.05f + 0.05f, sc, Mul(sc, 1.05f));
                    }
            // campfire
            Campfire(g, fire, salt);
            // watchtower in a back corner
            if (main)
            {
                var tp = c + U * (half - 0.5f) + V * (half - 0.5f);
                Watchtower(g, tp, U, V);
            }
            // cabin along the back wall (or a tent beside the building)
            var cabinP = c - U * (half * 0.35f) + V * (half - 0.65f);
            if (!bld.HasValue || !bld.Value.Contains(cabinP, 0.75f)) Cabin(g, cabinP, U, V, salt);
            else
            {
                var tentP = c - U * (half - 0.7f) - V * (half - 0.9f);
                if (!bld.Value.Contains(tentP, 0.5f)) Tent(g, tentP, V, salt);
            }
            // crates & barrels in the corners
            var corners = new[] { c - U * (half - 0.3f) + V * (half - 0.3f), c + U * (half - 0.35f) - V * (half - 0.35f), c - U * (half - 0.3f) - V * (half - 0.3f) };
            for (int k = 0; k < corners.Length; k++)
            {
                var p = corners[k];
                if (bld.HasValue && bld.Value.Contains(p, 0.15f)) continue;
                g.BoxAt(p, U, 0.11f, 0.11f, y, y + 0.2f, Hex("#7a5536"), Hex("#8a6a48"));
                g.BoxAt(p + U * 0.26f, U, 0.1f, 0.1f, y, y + 0.17f, Hex("#6b4a30"), Hex("#7a5a3a"));
                g.BoxAt(p + U * 0.1f + V * 0.02f, U * 0.9f + V * 0.3f, 0.09f, 0.09f, y + 0.2f, y + 0.36f, Hex("#7a5536"), Hex("#8a6a48"));
                g.Cyl(p + V * 0.26f * (k == 1 ? 1f : -1f), 0.085f, y, y + 0.26f, k == 0 ? Hex("#3a4a5a") : Hex("#6e3226"), Hex("#2e2c28"), 8);
                g.Cyl(p + V * 0.26f * (k == 1 ? 1f : -1f) + U * 0.19f, 0.085f, y, y + 0.26f, Lerp(Hex("#3a4a5a"), RUST, 0.4f), Hex("#2e2c28"), 8);
            }
            // flag on the gate (player mustard)
            var fp = gc + U * 0.55f;
            g.Beam(Geo.W(fp, 1.2f), Geo.W(fp, 1.85f), 0.012f, TIMBER);
            Color flag = mine ? Hex("#bd882f") : Hex("#6e3226");
            g.QuadRaw(Geo.W(fp, 1.55f), Geo.W(fp + U * 0.38f, 1.58f), Geo.W(fp + U * 0.4f, 1.8f), Geo.W(fp, 1.84f), Mul(flag, 0.9f), Mul(flag, 0.8f), flag, flag);
            // string lights along the inside of the back wall
            if (main)
            {
                var a0 = c - U * (half - 0.2f) + V * (half - 0.12f); var a1 = c + U * (half - 1.0f) + V * (half - 0.12f);
                int nb = 14;
                for (int k = 0; k <= nb; k++)
                {
                    float t = (float)k / nb;
                    var bp = Vector2.Lerp(a0, a1, t);
                    float yy = 0.95f - Mathf.Sin(t * Mathf.PI * 3f) * Mathf.Sin(t * Mathf.PI * 3f) * 0.12f;
                    var bc = Geo.W(bp, yy);
                    g.QuadRaw(bc + new Vector3(-0.025f, 0, 0), bc + new Vector3(0, -0.035f, 0), bc + new Vector3(0.025f, 0, 0), bc + new Vector3(0, 0.035f, 0), Hex("#ffb14a"), Hex("#e08a30"), Hex("#ffb14a"), Hex("#ffd27a"));
                }
                // armoured truck parked outside the gate
                MRAP(g, gc - V * 1.1f + U * 1.6f, U, salt);
            }
        }

        public const float STEP_H = 0.6f, STEP_IN = 0.2f;

        void FiringStep(Geo g, Vector2 c, float half, Vector2 U, Vector2 V)
        {
            Color plank = Hex("#6a5038"), plankTop = Hex("#7a5e42");
            float w = 0.13f, r = half - STEP_IN;
            for (int side = 0; side < 4; side++)
            {
                Vector2 along = side % 2 == 0 ? U : V, nrm = side % 2 == 0 ? V : U;
                float sg = side < 2 ? -1f : 1f;
                var mid = c + nrm * (r * sg);
                float len = r;
                if (side == 0)
                {
                    // leave the gate open: two runs either side of it
                    foreach (int k in new[] { -1, 1 })
                    {
                        float a = 0.6f, b = r; var m2 = mid + along * (k * (a + b) * 0.5f);
                        g.BoxAt(m2, along, (b - a) * 0.5f, w, STEP_H - 0.04f, STEP_H, plank, plankTop);
                        for (float t = a + 0.2f; t < b; t += 0.9f) g.Beam(Geo.W(mid + along * (k * t), 0.05f), Geo.W(mid + along * (k * t), STEP_H - 0.04f), 0.025f, TIMBER);
                    }
                    continue;
                }
                g.BoxAt(mid, along, len, w, STEP_H - 0.04f, STEP_H, plank, plankTop);
                for (float t = -len + 0.3f; t < len; t += 0.9f) g.Beam(Geo.W(mid + along * t, 0.05f), Geo.W(mid + along * t, STEP_H - 0.04f), 0.025f, TIMBER);
            }
        }

        Color YardCol(Vector2 p, Vector2 fire, ORect yard, bool main)
        {
            float df = (p - fire).magnitude;
            float edge = Mathf.Min(yard.Hu - Mathf.Abs(Vector2.Dot(p - yard.C, yard.U)), yard.Hv - Mathf.Abs(Vector2.Dot(p - yard.C, yard.V)));
            Color c = Lerp(YARD, Hex("#6e5c3e"), Noise(p.x, p.y, 1.3f, 501) * 0.7f);
            c = Lerp(c, YARD_DARK, Mathf.Clamp01(1f - edge / 0.6f) * 0.6f);
            c = Lerp(c, FIRE_GLOW, Mathf.Clamp01(1f - df / 2.6f) * 0.9f);
            if (df < 0.55f) c = Lerp(c, Hex("#d08a3a"), (0.55f - df) * 1.2f);
            return c;
        }

        void Campfire(Geo g, Vector2 p, int salt)
        {
            const float y = 0.05f;
            g.Disc(p, 0.3f, y + 0.003f, Hex("#3a2a1c"), Hex("#6a4224"), 8);
            for (int k = 0; k < 8; k++)
            {
                float a = k * Mathf.PI / 4f;
                var sp = p + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 0.2f;
                g.BoxAt(sp, new Vector2(Mathf.Cos(a), Mathf.Sin(a)), 0.045f, 0.035f, y, y + 0.05f, Hex("#6a665c"), Hex("#7a766a"));
            }
            for (int k = 0; k < 3; k++)
            {
                float a = k * 2.1f + 0.4f;
                var dd = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 0.15f;
                g.Beam(Geo.W(p - dd, y + 0.02f), Geo.W(p + dd * 0.3f, y + 0.12f), 0.025f, Hex("#2a1e14"));
            }
            g.Disc(p, 0.12f, y + 0.02f, Hex("#ffe070"), Hex("#e0702a"), 7);
            // flames: unshaded bright cones
            FlameCone(g, p, 0.11f, y + 0.02f, y + 0.36f, Hex("#e0702a"), Hex("#fadf2f"));
            FlameCone(g, p + new Vector2(0.04f, 0.03f), 0.06f, y + 0.02f, y + 0.46f, Hex("#f0a030"), Hex("#fff3a0"));
            FlameCone(g, p - new Vector2(0.05f, 0.02f), 0.05f, y + 0.02f, y + 0.3f, Hex("#e0702a"), Hex("#fadf2f"));
            // log benches
            for (int k = 0; k < 2; k++)
            {
                float a = k * Mathf.PI + 0.6f;
                var bp = p + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 0.55f;
                var dd = new Vector2(-Mathf.Sin(a), Mathf.Cos(a));
                g.Beam(Geo.W(bp - dd * 0.25f, y + 0.05f), Geo.W(bp + dd * 0.25f, y + 0.05f), 0.045f, Hex("#5a3e28"));
            }
        }

        static void FlameCone(Geo g, Vector2 p, float r, float y0, float y1, Color bottom, Color top)
        {
            for (int k = 0; k < 5; k++)
            {
                float a0 = k * Mathf.PI * 2f / 5f, a1 = (k + 1) * Mathf.PI * 2f / 5f;
                var A = Geo.W(p + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * r, y0);
                var B = Geo.W(p + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * r, y0);
                var m = g.M; int i = m.V.Count;
                m.V.Add(A); m.V.Add(B); m.V.Add(Geo.W(p, y1));
                m.C.Add(bottom); m.C.Add(bottom); m.C.Add(top);
                m.T.Add(i); m.T.Add(i + 1); m.T.Add(i + 2);
            }
        }

        void Watchtower(Geo g, Vector2 p, Vector2 U, Vector2 V)
        {
            const float top = 1.85f;
            var legsB = new[] { p - U * 0.42f - V * 0.42f, p + U * 0.42f - V * 0.42f, p + U * 0.42f + V * 0.42f, p - U * 0.42f + V * 0.42f };
            var legsT = new[] { p - U * 0.32f - V * 0.32f, p + U * 0.32f - V * 0.32f, p + U * 0.32f + V * 0.32f, p - U * 0.32f + V * 0.32f };
            for (int k = 0; k < 4; k++) g.Beam(Geo.W(legsB[k], 0f), Geo.W(legsT[k], top), 0.04f, TIMBER);
            for (int k = 0; k < 4; k++)
            {
                int n = (k + 1) & 3;
                for (int bay = 0; bay < 2; bay++)
                {
                    float t0 = bay * 0.5f, t1 = t0 + 0.5f;
                    var a0 = Geo.W(Vector2.Lerp(legsB[k], legsT[k], t0), top * t0); var a1 = Geo.W(Vector2.Lerp(legsB[n], legsT[n], t1), top * t1);
                    var b0 = Geo.W(Vector2.Lerp(legsB[n], legsT[n], t0), top * t0); var b1 = Geo.W(Vector2.Lerp(legsB[k], legsT[k], t1), top * t1);
                    g.Beam(a0, a1, 0.015f, Mul(TIMBER, 1.1f)); g.Beam(b0, b1, 0.015f, Mul(TIMBER, 1.1f));
                }
            }
            var plat = new ORect(p, U, 0.42f, 0.42f);
            g.Box(plat.Quad(), top, top + 0.07f, TIMBER, Hex("#5a4632"));
            for (int f = 0; f < 4; f++)
            {
                FaceEdge(plat, f, out var a, out var b, out var o);
                g.Box(new[] { a, b, b - o * 0.03f, a - o * 0.03f }, top + 0.07f, top + 0.36f, Lerp(TIMBER, LOG_TIP, 0.2f), LOG_TIP);
            }
            foreach (var cp in plat.Quad()) g.Beam(Geo.W(cp, top + 0.07f), Geo.W(cp, top + 0.72f), 0.022f, TIMBER);
            g.Pyramid(plat.Inset(-0.1f).Quad(), top + 0.72f, p, top + 1.0f, Hex("#3e2e22"));
            // lantern glow in the cabin
            g.BoxAt(p, U, 0.04f, 0.04f, top + 0.4f, top + 0.48f, Hex("#ffb14a"), Hex("#ffd27a"));
            // ladder
            var la = p - V * 0.46f - U * 0.1f; var lb = p - V * 0.46f + U * 0.1f;
            g.Beam(Geo.W(la - V * 0.2f, 0f), Geo.W(la, top), 0.012f, TIMBER); g.Beam(Geo.W(lb - V * 0.2f, 0f), Geo.W(lb, top), 0.012f, TIMBER);
            g.Shadow(plat.Quad(), top + 0.9f, 0.3f);
        }

        void Cabin(Geo g, Vector2 p, Vector2 U, Vector2 V, int salt)
        {
            var r = new ORect(p, U, 0.85f, 0.55f);
            Color wall = Hex("#6b4a30");
            g.Box(r.Quad(), 0.05f, 0.75f, wall, wall, 0.15f, false);
            // plank lines
            for (int f = 0; f < 4; f++)
            {
                FaceEdge(r, f, out var a, out var b, out var o);
                for (float yy = 0.17f; yy < 0.74f; yy += 0.12f) g.Wall(a + o * 0.005f, b + o * 0.005f, o, yy, yy + 0.012f, Mul(wall, 0.7f));
            }
            g.Gable(r, 0.75f, 1.2f, TIMBER, wall, 0.1f, 4);
            // lit windows + door on the yard side (-V)
            FaceEdge(r, 0, out var fa, out var fb, out var fo);
            var dir = (fb - fa).normalized; float len = (fb - fa).magnitude;
            foreach (float t in new[] { 0.22f, 0.78f })
            {
                var wc = fa + dir * (len * t);
                Pane(g, wc, dir, fo, 0.15f, 0.27f, 0.6f, Hex("#3a2a1c"), Hex("#3a2a1c"), 0.006f, false);
                Pane(g, wc, dir, fo, 0.12f, 0.3f, 0.57f, LIT_RIM, LIT_CORE, 0.012f, true);
                Pane(g, wc, dir, fo, 0.01f, 0.3f, 0.57f, Hex("#3a2a1c"), Hex("#3a2a1c"), 0.014f, false);
            }
            Pane(g, fa + dir * (len * 0.5f), dir, fo, 0.12f, 0.05f, 0.62f, Hex("#2e2016"), Hex("#3a2a1c"), 0.008f, false);
            // warm spill of light on the ground under the windows
            foreach (float t in new[] { 0.22f, 0.78f })
                g.Disc(fa + dir * (len * t) + fo * 0.3f, 0.3f, 0.051f, Hex("#9a5a28"), Hex("#5a4230"), 6);
            g.Cyl(r.P(0.6f, 0.4f), 0.035f, 1.0f, 1.45f, Hex("#3a3631"), Hex("#2a2622"), 6);   // stove pipe
            g.Shadow(r.Inset(-0.1f).Quad(), 1.0f, 0.34f);
        }

        void Tent(Geo g, Vector2 p, Vector2 dir, int salt)
        {
            var side = new Vector2(-dir.y, dir.x);
            float hl = 0.32f, hw = 0.24f, h = 0.4f;
            Color c = Hf(salt, 9) < 0.5f ? Hex("#7a6a4a") : Hex("#3a4e5e");
            var a0 = p - dir * hl - side * hw; var a1 = p + dir * hl - side * hw; var b0 = p - dir * hl + side * hw; var b1 = p + dir * hl + side * hw;
            var r0 = p - dir * hl; var r1 = p + dir * hl;
            g.Face(Geo.W(a0, 0.05f), Geo.W(a1, 0.05f), Geo.W(r1, h), Geo.W(r0, h), c, Geo.W(-side, 1f));
            g.Face(Geo.W(b0, 0.05f), Geo.W(b1, 0.05f), Geo.W(r1, h), Geo.W(r0, h), c, Geo.W(side, 1f));
            g.Tri(Geo.W(a0, 0.05f), Geo.W(b0, 0.05f), Geo.W(r0, h), Mul(c, 0.6f), Geo.W(-dir, 0f));
            g.Tri(Geo.W(a1, 0.05f), Geo.W(b1, 0.05f), Geo.W(r1, h), Mul(c, 0.9f), Geo.W(dir, 0f));
            g.Shadow(new[] { a0, a1, b1, b0 }, 0.3f, 0.3f);
        }

        void MRAP(Geo g, Vector2 p, Vector2 dir, int salt)
        {
            Color ol = Hex("#4f4838"), ol2 = Hex("#5a5a48"), tyre = Hex("#1e1e1c");
            var side = new Vector2(-dir.y, dir.x);
            g.Frustum(new ORect(p, dir, 0.85f, 0.25f).Quad(), 0.1f, new ORect(p, dir, 0.88f, 0.34f).Quad(), 0.3f, Mul(ol, 0.85f), ol);
            g.Box(new ORect(p - dir * 0.15f, dir, 0.7f, 0.35f).Quad(), 0.3f, 0.78f, ol, ol2);
            g.Frustum(new ORect(p + dir * 0.62f, dir, 0.22f, 0.34f).Quad(), 0.3f, new ORect(p + dir * 0.58f, dir, 0.18f, 0.32f).Quad(), 0.62f, ol, ol2);
            for (int s = -1; s <= 1; s += 2)
            {
                var wa = p + dir * 0.45f + side * (0.351f * s);
                g.Wall(wa - dir * 0.12f, wa + dir * 0.12f, side * s, 0.52f, 0.66f, Hex("#262a2a"));
                g.Wall(p - dir * 0.3f + side * (0.351f * s) - dir * 0.08f, p - dir * 0.3f + side * (0.351f * s) + dir * 0.08f, side * s, 0.55f, 0.66f, Hex("#262a2a"));
            }
            foreach (float t in new[] { -0.55f, 0.0f, 0.55f })
                g.Box(new ORect(p + dir * t, dir, 0.14f, 0.38f).Quad(), 0f, 0.27f, tyre, tyre);
            g.Cyl(p - dir * 0.2f, 0.12f, 0.78f, 0.88f, ol2, ol, 8);
            g.Beam(Geo.W(p - dir * 0.2f, 0.85f), Geo.W(p - dir * 0.2f + dir * 0.35f, 0.88f), 0.015f, Hex("#2a2a26"));
            g.Beam(Geo.W(p - dir * 0.75f + side * 0.3f, 0.78f), Geo.W(p - dir * 0.9f + side * 0.3f, 1.55f), 0.005f, Hex("#2a2a26"));
            g.Shadow(new ORect(p, dir, 0.88f, 0.36f).Quad(), 0.75f, 0.38f);
        }
    }
}
