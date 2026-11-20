// v0.16 graphics idea #12: camps that grow visibly. Every facility the player builds at a camp (Base.Up levels) adds
// props around and on top of the camp building: garden -> planter beds with green rows, water collectors -> blue
// barrels under rain-catch tarps (+ a tank), workshop / lab -> solar panels and a generator with its cable, med bay ->
// a white tent with a red cross, barracks -> laundry lines, tarps and mattresses, armory -> sandbag nest and ammo
// crates, radio -> a mast. Always a burn barrel, plus tarps that grow with the number of people living there.
// Visual only: reads Base.Up / People, never writes to the sim and never touches the game RNG (positions are hashed).
// Hooked into MapView.UpdateDynamic: Key() is appended to the dynamic-layer key, so the props are rebuilt only when
// the facility set, the camp itself or the colony-size bucket changes; Build() appends them to the dynamic mesh.
using System;
using System.Collections.Generic;
using System.Text;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public static class FxCampGrowth
    {
        static readonly string[] FAC = { "garden", "water", "workshop", "medbay", "barracks", "armory", "lab", "radio" };

        /// <summary>Tarps that grow with the colony: 1, plus one per 4 people beyond the first 8 living at the camp, at most 5.</summary>
        public static int TarpCount(int people) => 1 + Math.Min(4, Math.Max(0, people - 8) / 4);

        static int PeopleAt(Game g, int bid)
        {
            int n = 0;
            var c = g.C;
            if (c == null) return 0;
            foreach (var p in c.People) if (p.Base == bid && p.St != "dead") n++;
            return n;
        }

        /// <summary>Signature of everything the props depend on (own finished camps, their facility levels, tarp bucket).</summary>
        public static string Key(Game g)
        {
            if (g?.S == null || g.C == null) return "";
            var sb = new StringBuilder(64);
            foreach (var b in g.C.Bases)
            {
                if (b.Done < 1.0) continue;
                sb.Append(b.Id).Append('@').Append(b.Lot).Append(':');
                foreach (var k in FAC) sb.Append(b.U(k));
                sb.Append('t').Append(TarpCount(PeopleAt(g, b.Id))).Append(';');
            }
            return sb.ToString();
        }

        /// <summary>Append the grown-camp props of the player's camps to a dynamic chunk (MapGen.BuildDynamic output).</summary>
        public static void Build(MapGen gen, Game g, MapGen.Chunk ch)
        {
            if (gen == null || ch == null || g?.S == null || g.C == null) return;
            var geo = new Geo(ch.Mesh, ch.Shadow);
            foreach (var b in g.C.Bases)
            {
                if (b.Done < 1.0) continue;
                int t0 = geo.M.T.Count, s0 = geo.S != null ? geo.S.T.Count : 0;
                gen.CampGrowth(geo, b, PeopleAt(g, b.Id));
                gen.ClipWetPublic(geo, t0, s0);   // v0.19 audit: nothing stands in the river
            }
        }
    }

    public sealed partial class MapGen
    {
        struct GSlot { public Vector2 P, N, A; public float Score, Off; }   // Off: distance from the wall

        static readonly Color CG_TARP_BLUE = Hex("#2f5d8c"), CG_TARP_GREEN = Hex("#4f6a3a"), CG_TARP_ORANGE = Hex("#b0602a"), CG_TARP_GREY = Hex("#76766c");
        static readonly Color CG_BARREL = Hex("#2a609e"), CG_BARREL_TOP = Hex("#1c3a5e"), CG_POLE = Hex("#4a3a2a");
        static readonly Color CG_WOOD = Hex("#6e4c30"), CG_SOIL = Hex("#3a2a1a"), CG_LEAF = Hex("#5f9a3a"), CG_LEAF2 = Hex("#86b84a");
        static readonly Color CG_SOLAR = Hex("#1a2a4e"), CG_SOLAR_HI = Hex("#34508a"), CG_FRAME = Hex("#a8acb0");
        static readonly Color CG_TENT = Hex("#dcdad0"), CG_CROSS = Hex("#b8241e"), CG_BAG = Hex("#8a7a54"), CG_AMMO = Hex("#4c5634"), CG_STENCIL = Hex("#c8b048");
        static readonly Color CG_MATT = Hex("#cfc6aa"), CG_GEN = Hex("#c89a2a"), CG_DARK = Hex("#26241f"), CG_RUSTB = Hex("#6e3a22");
        static readonly Color[] CG_CLOTH = { Hex("#a43a30"), Hex("#3a5e94"), Hex("#d8d4c8"), Hex("#d0a83a"), Hex("#5a7e4a"), Hex("#8a5a9a") };
        static readonly Color[] CG_TARPS = { CG_TARP_BLUE, CG_TARP_GREEN, CG_TARP_ORANGE, CG_TARP_GREY, CG_TARP_BLUE };

        /// <summary>Roof of a camp building: 1 flat (parapet roof), 2 gable (ridge along r.U, eaves wallTop, ridge top), 0 none.</summary>
        int CampRoof(int l, out ORect r, out float wallTop, out float top)
        {
            r = default; wallTop = 0f; top = 0f;
            if (l < 0 || l >= _info.Length || !_info[l].Valid || _info[l].Floors <= 0) return 0;
            var inf = _info[l];
            r = inf.R; wallTop = inf.WallTop; top = inf.Top;
            switch (inf.A)
            {
                case Arch.House: case Arch.Hut: case Arch.Barn:
                    if (r.Hu < r.Hv) r = new ORect(r.C, r.V, r.Hv, r.Hu);
                    return inf.Dmg >= 3 ? 0 : 2;
                case Arch.Apartment: case Arch.Office: case Arch.Civic: case Arch.Shop:
                    return inf.Dmg >= 3 ? 0 : 1;
                default:
                    return 0;
            }
        }

        /// <summary>v0.16 idea #12: props for one of the player's camps (see FxCampGrowth).</summary>
        internal void CampGrowth(Geo g, Base b, int people)
        {
            if (!Layout(b, out var lay)) return;
            int salt = b.Id * 131 + 7;
            int kind = CampRoof(b.Lot, out var R, out float wallTop, out float top);
            bool hasBld = lay.HasBld && b.Lot >= 0;
            if (!hasBld) { R = new ORect(new Vector2(b.Pos.x + 0.5f, b.Pos.y + 0.5f), GU, 0.45f, 0.45f); kind = 0; }
            else if (kind == 0 && R.Hu <= 0f) R = lay.Bld;
            var door = lay.Gate; var fire = lay.Fire;
            var outv = fire - door; if (outv.sqrMagnitude < 1e-4f) outv = GU; outv = outv.normalized;
            var sideV = new Vector2(-outv.y, outv.x);
            var flag = door - sideV * 0.55f; var supplies = door + sideV * 0.65f;
            var cam = new Vector2(-0.966f, 0.259f);   // tile-space direction toward the default camera (MapView.YAW)

            // ---- ground slots: a ring around the building, outside the walls, clear of door / fire / flag / neighbours
            var slots = new List<GSlot>();
            for (int s = 0; s < 4; s++)
            {
                Vector2 n = s == 0 ? R.U : s == 1 ? -R.U : s == 2 ? R.V : -R.V;
                Vector2 a = s < 2 ? R.V : R.U;
                float wd = s < 2 ? R.Hu : R.Hv, hl = s < 2 ? R.Hv : R.Hu;
                for (int ring = 0; ring < 2; ring++)
                {
                    float hr = hl + (ring == 0 ? 0.2f : 0.6f), off = ring == 0 ? 0.32f : 0.8f;
                    int cnt = Mathf.Max(1, Mathf.FloorToInt(hr * 2f / 0.6f));
                    for (int k = 0; k < cnt; k++)
                    {
                        float t = -hr + (k + 0.5f) * (hr * 2f / cnt);
                        var p = R.C + n * (wd + off) + a * t;
                        if (!GroundFree(p, b.Lot, door, fire, flag, supplies)) continue;
                        bool clash = false;
                        foreach (var o in slots) if ((o.P - p).sqrMagnitude < 0.46f * 0.46f) { clash = true; break; }
                        if (clash) continue;
                        slots.Add(new GSlot { P = p, N = n, A = a, Off = off, Score = Vector2.Dot(n, cam) + Hf(salt + s, k + ring * 50) * 0.3f - ring * 1.2f });
                    }
                }
            }
            slots.Sort((x, y) => y.Score.CompareTo(x.Score));   // camera-facing sides first
            int si = 0;
            bool Take(out GSlot o) { if (si < slots.Count) { o = slots[si++]; return true; } o = default; return false; }

            // ---- roof cells (flat roofs): a grid on the roof, inside the parapet
            var cells = new List<Vector2>();
            float roofY = wallTop + 0.006f;
            if (kind == 1)
            {
                var rr = R.Inset(0.2f);
                int nu = Mathf.Max(1, Mathf.FloorToInt(rr.Hu * 2f / 0.6f)), nv = Mathf.Max(1, Mathf.FloorToInt(rr.Hv * 2f / 0.6f));
                var obst = RoofObstacles(b.Lot);
                for (int i = 0; i < nu; i++)
                    for (int j = 0; j < nv; j++)
                    {
                        var cp = rr.At(-rr.Hu + (i + 0.5f) * rr.Hu * 2f / nu, -rr.Hv + (j + 0.5f) * rr.Hv * 2f / nv);
                        bool hit = false;
                        foreach (var o in obst) if ((cp - new Vector2(o.x, o.y)).magnitude < o.z + 0.24f) { hit = true; break; }
                        if (!hit) cells.Add(cp);
                    }
                // visible edge first, hashed within the row
                cells.Sort((x, y) => (Vector2.Dot(y - R.C, cam) + Hf(salt, (int)(y.x * 7 + y.y * 13)) * 0.2f).CompareTo(Vector2.Dot(x - R.C, cam) + Hf(salt, (int)(x.x * 7 + x.y * 13)) * 0.2f));
            }
            int ci = 0;
            bool Cell(out Vector2 o) { if (ci < cells.Count) { o = cells[ci++]; return true; } o = default; return false; }

            int lvG = b.U("garden"), lvW = b.U("water"), lvWs = b.U("workshop"), lvM = b.U("medbay"), lvB = b.U("barracks"), lvA = b.U("armory"), lvL = b.U("lab"), lvR = b.U("radio");
            const float Y = 0.05f;

            // always: burn barrel by the door, and the colony's tarps
            {
                var bp = supplies + outv * 0.55f + sideV * 0.1f;
                if (GroundFree(bp, b.Lot, door, fire, flag, door)) BurnBarrel(g, bp, salt);
                else if (Take(out var s0)) BurnBarrel(g, s0.P, salt);
            }
            // ground props are requests in tiers: every built facility shows once before any gets its extras,
            // so a small house lot still reads as a camp with each facility, and big lots fill up
            var req = new List<(int tier, Action<GSlot> draw)>();
            if (lvM > 0) req.Add((0, s => { MedTent(g, s, lvM); if (lvM >= 2) g.BoxAt(s.P + s.A * 0.42f - s.N * 0.06f, s.N, 0.16f, 0.055f, Y, Y + 0.05f, Hex("#5a6a4a"), Hex("#7a8a62")); }));
            if (lvA > 0) { req.Add((0, s => SandbagNest(g, s, salt, lvA))); req.Add((1, s => AmmoCrates(g, s, salt, lvA * 2))); }
            int panels = (lvWs > 0 ? 4 : 0) + lvL * 3;
            if (lvWs > 0) req.Add((0, s => Generator(g, s, hasBld ? (kind == 1 ? wallTop : wallTop * 0.9f) : 0f, R)));
            for (int k = 0; k < lvB; k++) { int kk = k; req.Add((kk == 0 ? 0 : 1, s => LaundryLine(g, s, salt + kk * 17))); }
            int tarps = FxCampGrowth.TarpCount(people) + lvB;
            for (int k = 0; k < tarps; k++) { int kk = k; req.Add((kk == 0 ? 0 : kk <= lvB ? 1 : 2, s => LeanTo(g, s, CG_TARPS[(salt + kk) % CG_TARPS.Length], salt + kk * 7, kk < lvB ? 2 : kk == lvB ? 1 : 0))); }
            // solar panels: on the flat roof (tilted on stands), on the camera-facing slope of a gable roof, else in the yard
            if (panels > 0)
            {
                if (kind == 1) { for (int k = 0; k < panels; k += 2) if (Cell(out var pc)) { SolarStand(g, pc + R.U * 0.13f, cam, roofY); SolarStand(g, pc - R.U * 0.13f, cam, roofY); } }
                else if (kind == 2) SolarRoof(g, R, wallTop, top, cam, panels);
                else for (int k = 0; k < panels; k += 2) req.Add((k == 0 ? 0 : 2, s => { SolarStand(g, s.P + s.A * 0.14f, cam, Y); SolarStand(g, s.P - s.A * 0.14f, cam, Y); }));
            }
            // radio: mast with guy wires on the roof (or in the yard)
            if (lvR > 0)
            {
                if (kind == 1 && Cell(out var rp)) RadioMast(g, rp, roofY, 0.85f);
                else if (kind == 2) RadioMast(g, R.At(R.Hu * 0.7f, 0f), top - (top - wallTop) * 0.02f, 0.7f);
                else req.Add((1, s => RadioMast(g, s.P, Y, 1.0f)));
            }
            // water collectors: rain-catch tarp over blue barrels per level, a tank at level 3 (roof first)
            for (int k = 0; k < lvW; k++)
            {
                int kk = k;
                if (kk == 2) { if (kind == 1 && Cell(out var tp)) WaterTank(g, tp, roofY); else req.Add((1, s => WaterTank(g, s.P, Y))); continue; }
                if (kind == 1 && Cell(out var wp)) RainCatch(g, wp, R.U, roofY, salt + kk * 3);
                else req.Add((kk == 0 ? 0 : 1, s => RainCatch(g, s.P, s.A, Y, salt + kk * 3)));
            }
            // garden: planter beds on a flat roof, raised beds in the yard otherwise (a pair of beds per slot)
            for (int k = 0; k < lvG * 3; k++)
            {
                int kk = k;
                if (kind == 1 && Cell(out var cp)) { PlanterPair(g, cp, R.U, roofY, salt + kk * 5, lvG); continue; }
                if (kk % 2 == 1) continue;
                req.Add((kk == 0 ? 0 : 1, s => PlanterPair(g, s.P, s.A, Y, salt + kk * 5, lvG)));
            }
            for (int tier = 0; tier <= 2; tier++)
                foreach (var r in req)
                    if (r.tier == tier && Take(out var s)) r.draw(s);
        }

        /// <summary>Rooftop clutter MapGen.RoofTop puts on an intact flat block (stair hut, tank, AC units): x,y centre, z radius.</summary>
        List<Vector3> RoofObstacles(int l)
        {
            var o = new List<Vector3>();
            if (l < 0 || l >= _info.Length) return o;
            var info = _info[l];
            var R = info.R;
            if (Hf(l, 15) < 0.5f) R = new ORect(R.C, -R.U, R.Hu, R.Hv);
            var ri = R.Inset(0.06f);
            if (R.Hu > 0.55f && R.Hv > 0.45f && Hf(l, 40) < 0.75f) { var p = ri.P(-0.5f + Hf(l, 41) * 0.3f, 0.3f - Hf(l, 42) * 0.6f); o.Add(new Vector3(p.x, p.y, 0.3f)); }
            if (Hf(l, 43) < 0.35f && R.Hu > 0.5f) { var p = ri.P(0.55f, -0.4f + Hf(l, 44) * 0.8f); o.Add(new Vector3(p.x, p.y, 0.24f)); }
            int n = 1 + (int)(Hf(l, 45) * 3.5f);
            for (int k = 0; k < n; k++) { var p = ri.P(-0.7f + 1.4f * Hf(l, 46 + k), -0.7f + 1.4f * Hf(l, 52 + k)); o.Add(new Vector3(p.x, p.y, 0.08f)); }
            return o;
        }

        bool GroundFree(Vector2 p, int lot, Vector2 door, Vector2 fire, Vector2 flag, Vector2 supplies)
        {
            if ((p - door).sqrMagnitude < 0.75f * 0.75f || (p - fire).sqrMagnitude < 0.85f * 0.85f) return false;
            if ((p - flag).sqrMagnitude < 0.45f * 0.45f || (p - supplies).sqrMagnitude < 0.45f * 0.45f) return false;
            var t = new Vector2Int(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y));
            if (!d.InMap(t)) return false;
            int tl = d.Tiles[t.y * d.W + t.x];
            bool ownLot = lot >= 0 && lot < _info.Length && _info[lot].Valid && _info[lot].Lot.Contains(p, -0.08f);
            if (!ownLot && (tl == Data.T_ROAD || tl == Data.T_AVE)) return false;
            // stay out of every other building (check the tile's lot and its four neighbours)
            for (int k = 0; k < 5; k++)
            {
                var q = k == 0 ? t : new Vector2Int(t.x + (k == 1 ? 1 : k == 2 ? -1 : 0), t.y + (k == 3 ? 1 : k == 4 ? -1 : 0));
                if (!d.InMap(q)) continue;
                int l = d.BuildingAt(q);
                if (l < 0 || l == lot || l >= _info.Length || !_info[l].Valid || _info[l].Floors <= 0) continue;
                if (_info[l].R.Contains(p, 0.22f)) return false;
            }
            if (lot >= 0 && lot < _info.Length && _info[lot].Valid && _info[lot].R.Contains(p, 0.12f)) return false;
            return true;
        }

        // ---------------- props ----------------
        static void TwoSided(Geo g, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color col)
        {
            var n = Vector3.Cross(b - a, d - a);
            if (n.y < 0f) n = -n;
            g.Face(a, b, c, d, col, n);
            g.Face(a, b, c, d, Mul(col, 0.72f), -n);
        }

        void BurnBarrel(Geo g, Vector2 p, int salt)
        {
            const float Y = 0.05f;
            g.Cyl(p, 0.075f, Y, Y + 0.23f, Lerp(CG_RUSTB, RUST, Hf(salt, 3) * 0.5f), CG_DARK, 8);
            g.Disc(p, 0.06f, Y + 0.232f, Hex("#ffe070"), Hex("#c05a20"), 6);
            FlameCone(g, p, 0.05f, Y + 0.23f, Y + 0.4f, Hex("#e0702a"), Hex("#fadf2f"));
            FlameCone(g, p + new Vector2(0.02f, -0.015f), 0.03f, Y + 0.23f, Y + 0.47f, Hex("#f0a030"), Hex("#fff3a0"));
            g.Disc(p, 0.32f, Y - 0.002f, Lerp(YARD_DARK, FIRE_GLOW, 0.55f), YARD_DARK, 8);
        }

        /// <summary>Lean-to: tarp sloping from the wall side (high) out to the front (low) on four poles; mattresses under it.</summary>
        void LeanTo(Geo g, GSlot s, Color tarp, int salt, int matts)
        {
            const float Y = 0.05f;
            float hw = 0.27f, back = 0.17f, front = 0.2f, hb = 0.44f, hf = 0.27f;
            var b0 = s.P - s.N * back - s.A * hw; var b1 = s.P - s.N * back + s.A * hw;
            var f0 = s.P + s.N * front - s.A * hw; var f1 = s.P + s.N * front + s.A * hw;
            foreach (var (pp, h) in new[] { (b0, hb), (b1, hb), (f0, hf), (f1, hf) }) g.Beam(Geo.W(pp, Y), Geo.W(pp, Y + h), 0.012f, CG_POLE);
            // sagging tarp: two strips with a slightly lower middle
            var m0 = Vector2.Lerp(b0, f0, 0.5f); var m1 = Vector2.Lerp(b1, f1, 0.5f);
            float hm = (hb + hf) * 0.5f - 0.03f;
            TwoSided(g, Geo.W(b0, Y + hb), Geo.W(b1, Y + hb), Geo.W(m1, Y + hm), Geo.W(m0, Y + hm), tarp);
            TwoSided(g, Geo.W(m0, Y + hm), Geo.W(m1, Y + hm), Geo.W(f1 + s.N * 0.05f, Y + hf - 0.04f), Geo.W(f0 + s.N * 0.05f, Y + hf - 0.04f), Mul(tarp, 0.93f));
            g.Shadow(new[] { b0, b1, f1 + s.N * 0.05f, f0 + s.N * 0.05f }, Y + (hb + hf) * 0.5f, 0.3f);
            for (int k = 0; k < matts; k++)
            {
                var mp = s.P + s.A * (k == 0 ? -0.12f : 0.12f) + s.N * 0.0f;
                g.BoxAt(mp, s.N, 0.17f, 0.085f, Y, Y + 0.035f, Mul(CG_MATT, 0.85f), Lerp(CG_MATT, CG_CLOTH[(salt + k) % CG_CLOTH.Length], 0.25f));
            }
            if (matts == 0)
            {
                // supplies under an ordinary tarp
                g.BoxAt(s.P - s.A * 0.1f, s.A, 0.08f, 0.08f, Y, Y + 0.14f, Hex("#7a5536"), Hex("#8a6a48"));
                g.Cyl(s.P + s.A * 0.12f + s.N * 0.03f, 0.06f, Y, Y + 0.18f, Hex("#5a5a52"), CG_DARK, 7);
            }
        }

        void LaundryLine(Geo g, GSlot s, int salt)
        {
            const float Y = 0.05f;
            float hl = 0.34f, h = 0.44f;
            var p0 = s.P - s.A * hl; var p1 = s.P + s.A * hl;
            g.Beam(Geo.W(p0, Y), Geo.W(p0, Y + h + 0.03f), 0.012f, CG_POLE);
            g.Beam(Geo.W(p1, Y), Geo.W(p1, Y + h + 0.03f), 0.012f, CG_POLE);
            var mid = Vector2.Lerp(p0, p1, 0.5f);
            g.Beam(Geo.W(p0, Y + h), Geo.W(mid, Y + h - 0.035f), 0.005f, Hex("#cfc8b8"));
            g.Beam(Geo.W(mid, Y + h - 0.035f), Geo.W(p1, Y + h), 0.005f, Hex("#cfc8b8"));
            int n = 5;
            for (int k = 0; k < n; k++)
            {
                float t = (k + 0.5f) / n;
                float w = 0.045f + Hf(salt, k) * 0.03f, len = 0.08f + Hf(salt, k + 9) * 0.08f;
                float tc = t - 0.5f;
                float yTop = Y + h - 0.035f * (1f - 4f * tc * tc) - 0.004f;
                var c = Vector2.Lerp(p0, p1, t);
                var ca = c - s.A * w; var cb = c + s.A * w;
                var col = CG_CLOTH[(salt + k * 3) % CG_CLOTH.Length];
                TwoSided(g, Geo.W(ca, yTop), Geo.W(cb, yTop), Geo.W(cb + s.N * 0.01f, yTop - len), Geo.W(ca + s.N * 0.01f, yTop - len), col);
            }
        }

        void MedTent(Geo g, GSlot s, int lv)
        {
            const float Y = 0.05f;
            float hu = 0.26f + lv * 0.04f, hv = 0.2f + lv * 0.02f, wall = 0.15f, ridge = 0.38f + lv * 0.03f;
            var r = new ORect(s.P, s.A, hu, hv);
            g.Box(r.Quad(), Y, Y + wall, CG_TENT, CG_TENT, 0.15f, false);
            g.Gable(r, Y + wall, Y + ridge, CG_TENT, CG_TENT, 0.03f, 1);
            // door flap on the outward end, red cross on both slopes
            g.Shadow(r.Inset(-0.04f).Quad(), Y + (wall + ridge) * 0.5f, 0.3f);
            for (int side = -1; side <= 1; side += 2)
            {
                float vc = side * hv * 0.5f;
                float H(float v) => Y + wall + (ridge - wall) * (1f - Mathf.Abs(v) / hv) + 0.006f;
                float a = 0.075f, w = 0.024f;   // half arm length / half width (along u; across in slope v)
                var outw = new Vector3(r.V.x * side, 0.9f, -r.V.y * side);
                Vector3 P(float u, float v) => Geo.W(r.At(u, v), H(v));
                g.Face(P(-a, vc - w), P(a, vc - w), P(a, vc + w), P(-a, vc + w), CG_CROSS, outw);
                g.Face(P(-w, vc - a * 0.9f), P(w, vc - a * 0.9f), P(w, vc + a * 0.9f), P(-w, vc + a * 0.9f), CG_CROSS, outw);
            }
        }

        void SandbagNest(Geo g, GSlot s, int salt, int lv)
        {
            const float Y = 0.05f;
            int courses = 2 + (lv >= 3 ? 1 : 0);
            float rad = 0.22f;
            for (int c = 0; c < courses; c++)
            {
                int n = 7;
                for (int k = 0; k < n; k++)
                {
                    float ang = Mathf.PI * (k + (c & 1) * 0.5f) / (n - 1) - Mathf.PI * 0.5f * 0f;   // half ring opening toward the wall
                    if (ang > Mathf.PI + 0.01f) continue;
                    var dir = s.A * Mathf.Cos(ang) + s.N * Mathf.Sin(ang);
                    var tan = new Vector2(-dir.y, dir.x);
                    Color sc = Lerp(CG_BAG, Hex("#5a4e36"), Hf(salt + c * 13, k) * 0.6f);
                    g.BoxAt(s.P + dir * rad, tan, 0.065f, 0.042f, Y + c * 0.045f, Y + c * 0.045f + 0.045f, sc, Mul(sc, 1.06f));
                }
            }
            // a crate and a jerrycan inside the nest
            g.BoxAt(s.P - s.N * 0.04f, s.A, 0.06f, 0.05f, Y, Y + 0.08f, CG_AMMO, Lerp(CG_AMMO, CG_STENCIL, 0.25f));
        }

        void AmmoCrates(Geo g, GSlot s, int salt, int n)
        {
            const float Y = 0.05f;
            n = Mathf.Min(n, 6);
            for (int k = 0; k < n; k++)
            {
                int row = k % 3, layer = k / 3;
                var p = s.P + s.A * ((row - 1) * 0.17f) + s.N * (Hf(salt, k) * 0.04f - 0.02f) + s.A * (layer * 0.08f);
                float y0 = Y + layer * 0.09f;
                g.BoxAt(p, s.A, 0.075f, 0.05f, y0, y0 + 0.09f, CG_AMMO, Lerp(CG_AMMO, Hex("#6a7448"), 0.4f));
                // stencil band on the outward face
                var f = p + s.N * 0.051f;
                g.Face(Geo.W(f - s.A * 0.05f, y0 + 0.035f), Geo.W(f + s.A * 0.05f, y0 + 0.035f), Geo.W(f + s.A * 0.05f, y0 + 0.055f), Geo.W(f - s.A * 0.05f, y0 + 0.055f), CG_STENCIL, new Vector3(s.N.x, 0f, -s.N.y));
            }
        }

        void Generator(Geo g, GSlot s, float roofY, ORect bld)
        {
            const float Y = 0.05f;
            g.BoxAt(s.P, s.A, 0.12f, 0.08f, Y, Y + 0.13f, CG_GEN, CG_DARK);
            g.BoxAt(s.P + s.A * 0.02f, s.A, 0.07f, 0.06f, Y + 0.13f, Y + 0.16f, Hex("#3a3832"), Hex("#4a4740"));
            g.Beam(Geo.W(s.P - s.A * 0.09f, Y + 0.13f), Geo.W(s.P - s.A * 0.09f, Y + 0.24f), 0.012f, Hex("#2e2c28"));
            g.Shadow(new ORect(s.P, s.A, 0.12f, 0.08f).Quad(), Y + 0.13f, 0.3f);
            // jerrycans
            g.BoxAt(s.P + s.A * 0.2f, s.A, 0.035f, 0.05f, Y, Y + 0.1f, Hex("#8a2a20"), Hex("#6e2219"));
            // cable: across the ground to the wall, then up into a window
            var wallPt = s.P - s.N * (s.Off - 0.015f);
            g.Beam(Geo.W(s.P - s.N * 0.08f, Y + 0.02f), Geo.W(wallPt, Y + 0.01f), 0.007f, Hex("#141414"));
            if (roofY > 0.2f) g.Beam(Geo.W(wallPt, Y + 0.01f), Geo.W(wallPt, Y + Mathf.Min(0.34f, roofY - 0.05f)), 0.007f, Hex("#141414"));   // up the wall into the ground-floor window
        }

        void PlanterPair(Geo g, Vector2 p, Vector2 along, float y, int salt, int lv)
        {
            var across = new Vector2(-along.y, along.x);
            for (int k = 0; k < 2; k++)
            {
                var c = p + across * (k == 0 ? -0.12f : 0.12f);
                var r = new ORect(c, along, 0.24f, 0.08f);
                g.Box(r.Quad(), y, y + 0.07f, CG_WOOD, CG_WOOD);
                g.Flat(r.Inset(0.015f).Quad(), y + 0.072f, CG_SOIL);
                // green rows: a low hedge strip plus leafy clumps
                var rr = r.Inset(0.025f);
                g.Box(new ORect(c, along, rr.Hu, 0.03f).Quad(), y + 0.07f, y + 0.1f, Mul(CG_LEAF, 0.85f), CG_LEAF);
                int n = 4;
                for (int i = 0; i < n; i++)
                {
                    var q = c + along * (-rr.Hu + (i + 0.5f) * rr.Hu * 2f / n);
                    float s = 0.035f + Hf(salt + k, i) * 0.015f + (lv >= 2 ? 0.012f : 0f);
                    g.Blob(Geo.W(q, y + 0.1f + s * 0.5f), s, s * 0.9f, Lerp(CG_LEAF, CG_LEAF2, Hf(salt + k * 7, i + 3)), salt + i * 11 + k, 5);
                }
                if (lv >= 2)
                {
                    // stakes with climbing plants at the ends
                    foreach (int e in new[] { -1, 1 })
                    {
                        var sp = c + along * (e * (rr.Hu - 0.02f));
                        g.Beam(Geo.W(sp, y + 0.07f), Geo.W(sp, y + 0.27f), 0.006f, Hex("#8a6a48"));
                        g.Blob(Geo.W(sp, y + 0.2f), 0.035f, 0.07f, Lerp(CG_LEAF, Hex("#a83a2a"), 0.12f), salt + e + 50, 5);
                    }
                }
            }
        }

        void RainCatch(Geo g, Vector2 p, Vector2 along, float y, int salt)
        {
            var across = new Vector2(-along.y, along.x);
            // barrels under the funnel, one more beside it
            g.Cyl(p, 0.08f, y, y + 0.24f, CG_BARREL, CG_BARREL_TOP, 8);
            g.Cyl(p + along * 0.19f + across * 0.05f, 0.08f, y, y + 0.24f, Mul(CG_BARREL, 0.92f), CG_BARREL_TOP, 8);
            g.Cyl(p - along * 0.17f - across * 0.1f, 0.075f, y, y + 0.22f, Lerp(CG_BARREL, Hex("#3a6aa8"), 0.4f), CG_BARREL_TOP, 8);
            // funnel tarp on four posts, dipping into the first barrel
            float h = 0.4f, hw = 0.21f;
            var cs = new[] { p + (along + across) * hw, p + (along - across) * hw, p + (-along - across) * hw, p + (-along + across) * hw };
            foreach (var c in cs) g.Beam(Geo.W(c, y), Geo.W(c, y + h + 0.01f), 0.01f, CG_POLE);
            var mid = Geo.W(p, y + 0.27f);
            var tc = (salt & 1) == 0 ? CG_TARP_BLUE : CG_TARP_GREY;
            for (int k = 0; k < 4; k++)
            {
                var a = Geo.W(cs[k], y + h); var b = Geo.W(cs[(k + 1) & 3], y + h);
                var n = Vector3.Cross(b - a, mid - a); if (n.y < 0f) n = -n;
                g.Tri(a, b, mid, Mul(tc, 1f - k * 0.04f), n);
                g.Tri(a, b, mid, Mul(tc, 0.7f), -n);
            }
            if (y < 0.1f) g.Shadow(cs, y + h * 0.8f, 0.22f);
        }

        void WaterTank(Geo g, Vector2 p, float y)
        {
            foreach (var o in new[] { new Vector2(0.12f, 0.12f), new Vector2(-0.12f, 0.12f), new Vector2(0.12f, -0.12f), new Vector2(-0.12f, -0.12f) })
                g.Beam(Geo.W(p + o, y), Geo.W(p + o, y + 0.14f), 0.012f, Hex("#5a5a54"));
            g.Box(new ORect(p, GU, 0.17f, 0.17f).Quad(), y + 0.12f, y + 0.15f, Hex("#5a5a54"), Hex("#6a6a62"));
            g.Cyl(p, 0.16f, y + 0.15f, y + 0.47f, Hex("#c4c0b2"), Hex("#d8d4c6"), 10);
            g.Cyl(p, 0.05f, y + 0.47f, y + 0.5f, Hex("#5a5a54"), Hex("#4a4a44"), 6);
            g.Beam(Geo.W(p + new Vector2(0.15f, 0f), y + 0.2f), Geo.W(p + new Vector2(0.3f, 0f), y + 0.02f), 0.012f, Hex("#2a2a28"));
        }

        /// <summary>Solar panel tilted on a stand, facing dir (tile space).</summary>
        void SolarStand(Geo g, Vector2 p, Vector2 dir, float y)
        {
            dir = dir.normalized; var side = new Vector2(-dir.y, dir.x);
            float hw = 0.11f, hd = 0.12f, lo = y + 0.05f, hi = y + 0.18f;
            var f0 = p + dir * hd - side * hw; var f1 = p + dir * hd + side * hw;
            var b0 = p - dir * hd - side * hw; var b1 = p - dir * hd + side * hw;
            g.Beam(Geo.W(b0, y), Geo.W(b0, hi), 0.008f, CG_FRAME); g.Beam(Geo.W(b1, y), Geo.W(b1, hi), 0.008f, CG_FRAME);
            g.Beam(Geo.W(f0, y), Geo.W(f0, lo), 0.008f, CG_FRAME); g.Beam(Geo.W(f1, y), Geo.W(f1, lo), 0.008f, CG_FRAME);
            SolarQuad(g, f0, f1, b1, b0, lo, lo, hi, hi);
        }

        /// <summary>Panel with a frame and a 2 x 3 grid of cells. Corners a,b (front) c,d (back) with heights.</summary>
        static void SolarQuad(Geo g, Vector2 a, Vector2 b, Vector2 c, Vector2 d, float ha, float hb, float hc, float hd)
        {
            Vector3 A = Geo.W(a, ha), B = Geo.W(b, hb), C = Geo.W(c, hc), D = Geo.W(d, hd);
            var n = Vector3.Cross(B - A, D - A); if (n.y < 0f) n = -n;
            g.Face(A, B, C, D, CG_FRAME, n);
            g.Face(A, B, C, D, Mul(CG_FRAME, 0.6f), -n);
            var lift = n.normalized * 0.004f;
            Vector3 L3(Vector3 x, Vector3 y, float k) => x + (y - x) * k;
            Vector3 Q(float s, float t) => L3(L3(A, B, s), L3(D, C, s), t) + lift;
            const int NU = 2, NV = 3; const float GAP = 0.06f;
            for (int i = 0; i < NU; i++)
                for (int j = 0; j < NV; j++)
                {
                    float s0 = (i + GAP) / NU, s1 = (i + 1 - GAP) / NU, t0 = (j + GAP) / NV, t1 = (j + 1 - GAP) / NV;
                    var col = Lerp(CG_SOLAR, CG_SOLAR_HI, 0.15f + 0.25f * ((i + j) & 1));
                    g.QuadRaw(Q(s0, t0), Q(s1, t0), Q(s1, t1), Q(s0, t1), Mul(col, 0.9f), Mul(col, 0.9f), Lerp(col, CG_SOLAR_HI, 0.5f), Lerp(col, CG_SOLAR_HI, 0.5f));
                    g.QuadRaw(Q(s0, t0), Q(s0, t1), Q(s1, t1), Q(s1, t0), col, col, col, col);
                }
        }

        /// <summary>Panels laid flush on the camera-facing slope of a gable roof (ridge along R.U).</summary>
        void SolarRoof(Geo g, ORect R, float wallTop, float top, Vector2 cam, int count)
        {
            float side = Vector2.Dot(R.V, cam) >= 0f ? 1f : -1f;
            int per = Mathf.Clamp(Mathf.FloorToInt(R.Hu * 1.4f / 0.27f), 1, 6);
            int rows = Mathf.Min(2, (count + per - 1) / per);
            float pw = 0.12f;
            float H(float v) => top - (top - wallTop) * (Mathf.Abs(v) / R.Hv) + 0.012f;
            int placed = 0;
            for (int rw = 0; rw < rows; rw++)
            {
                float v0 = side * (R.Hv * (0.12f + rw * 0.42f)), v1 = side * (R.Hv * (0.48f + rw * 0.42f));
                if (Mathf.Abs(v1) > R.Hv * 0.92f) v1 = side * R.Hv * 0.92f;
                for (int k = 0; k < per && placed < count; k++, placed++)
                {
                    float u = -R.Hu * 0.7f + (k + 0.5f) * (R.Hu * 1.4f / per);
                    SolarQuad(g, R.At(u - pw, v1), R.At(u + pw, v1), R.At(u + pw, v0), R.At(u - pw, v0), H(v1), H(v1), H(v0), H(v0));
                }
            }
        }

        void RadioMast(Geo g, Vector2 p, float y, float h)
        {
            var c = Hex("#8a8c8a");
            g.Beam(Geo.W(p, y), Geo.W(p, y + h), 0.012f, c);
            g.Beam(Geo.W(p - GU * 0.1f, y + h * 0.8f), Geo.W(p + GU * 0.1f, y + h * 0.8f), 0.006f, c);
            g.Beam(Geo.W(p - GV * 0.08f, y + h * 0.65f), Geo.W(p + GV * 0.08f, y + h * 0.65f), 0.006f, c);
            foreach (var o in new[] { GU + GV, GU - GV, -GU })
                g.Beam(Geo.W(p, y + h * 0.7f), Geo.W(p + o.normalized * 0.28f, y), 0.003f, Hex("#3a3a38"));
            g.Disc(p, 0.025f, y + h + 0.005f, Hex("#ff4a3a"), Hex("#c02a20"), 5);
        }
    }
}
