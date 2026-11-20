// v0.14.0a: the other survivor colonies (S.Camps, Game.Factions.cs) and the vehicle wrecks (S.Wrecks,
// Game.Gear.cs) as real geometry on the map. The original (view3d.gd) put a flag on the camp's roof, a fire, a
// barrel and a bench at its door and drew each wreck with a vehicle mesh; here every faction camp is a fortified
// compound on its own lot (palisade or scrap wall in the faction's style, gate toward its door, tents and
// shacks, cook fire, banners and pennants in the faction colour, a watchtower for the bigger ones) plus
// pennants on the extra lots it has claimed, and every wreck is a procedural vehicle (MapGen.Vehicles.cs).
// A camp the dead overran stays as a burnt-out ruin. Layout only reads the sim; nothing here changes it.
using System;
using System.Collections.Generic;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed partial class MapGen
    {
        /// <summary>Faction compound as drawn: yard (palisade at its edge), camp building, gate, fire, hut spots.</summary>
        public struct FactionLayout
        {
            public ORect Yard; public bool HasBld; public ORect Bld; public Vector2 Gate, GateOut, Fire; public float Roof;
            public List<Vector2> Huts;
        }

        /// <summary>Compound on the camp's lot: the lot itself, grown so there is at least a strip of yard around the
        /// building, capped so a factory lot doesn't become a fortress the size of a district.</summary>
        public bool FactionLayoutOf(Camp c, out FactionLayout o)
        {
            o = default;
            if (c == null || c.Lot < 0 || c.Lot >= _info.Length) return false;
            var inf = _info[c.Lot];
            var door = new Vector2(c.Pos.x + 0.5f, c.Pos.y + 0.5f);
            ORect lot = inf.Valid ? inf.Lot : new ORect(door, GU, 2.5f, 2.5f);
            bool hasB = inf.Valid && inf.Floors > 0;
            var U = lot.U;
            float hu = lot.Hu - 0.12f, hv = lot.Hv - 0.12f;
            var ctr = lot.C;
            if (hasB)
            {
                var b = inf.R;
                float bu = Mathf.Abs(Vector2.Dot(b.U, U)) * b.Hu + Mathf.Abs(Vector2.Dot(b.V, U)) * b.Hv;
                float bv = Mathf.Abs(Vector2.Dot(b.U, lot.V)) * b.Hu + Mathf.Abs(Vector2.Dot(b.V, lot.V)) * b.Hv;
                var off = b.C - ctr;
                float ou = Vector2.Dot(off, U), ov = Vector2.Dot(off, lot.V);
                // grow toward the roomier side so huts fit beside the building
                // never cut through the building: at least a strip around it, else capped at the lot
                hu = Mathf.Max(Mathf.Min(Mathf.Max(hu, Mathf.Abs(ou) + bu + 1.1f), 6.5f), Mathf.Abs(ou) + bu + 0.7f);
                hv = Mathf.Max(Mathf.Min(Mathf.Max(hv, Mathf.Abs(ov) + bv + 1.1f), 6.5f), Mathf.Abs(ov) + bv + 0.7f);
                o.Roof = Mathf.Max(0.2f, inf.Top);
            }
            else { hu = Mathf.Clamp(hu, 2.2f, 6.5f); hv = Mathf.Clamp(hv, 2.2f, 6.5f); }
            hu = Mathf.Max(hu, 2.2f); hv = Mathf.Max(hv, 2.2f);
            o.Yard = new ORect(ctr, U, hu, hv);
            o.HasBld = hasB; if (hasB) o.Bld = inf.R;
            // gate on the yard side facing the camp door (the sim's c.Pos)
            var d = door - ctr;
            float du = Vector2.Dot(d, U) / hu, dv = Vector2.Dot(d, o.Yard.V) / hv;
            Vector2 outv = Mathf.Abs(du) > Mathf.Abs(dv) ? U * Mathf.Sign(du == 0f ? 1f : du) : o.Yard.V * Mathf.Sign(dv == 0f ? 1f : dv);
            float half = Mathf.Abs(Vector2.Dot(outv, U)) > 0.5f ? hu : hv;
            var along = new Vector2(-outv.y, outv.x);
            float t = Mathf.Clamp(Vector2.Dot(d, along), -(Mathf.Abs(Vector2.Dot(along, U)) > 0.5f ? hu : hv) + 0.8f, (Mathf.Abs(Vector2.Dot(along, U)) > 0.5f ? hu : hv) - 0.8f);
            o.Gate = ctr + outv * half + along * t;
            o.GateOut = outv;
            // fire: just inside the gate, clear of the building
            var fire = o.Gate - outv * 1.0f + along * 0.5f;
            if (hasB && inf.R.Contains(fire, 0.35f)) fire = o.Gate - outv * 0.55f + along * 0.9f;
            o.Fire = fire;
            // hut spots: a ring inside the palisade, clear of the building, the gate lane and the fire
            o.Huts = new List<Vector2>();
            int salt = c.Lot * 13 + 7;
            for (int k = 0; k < 28 && o.Huts.Count < 7; k++)
            {
                float a = k * 2.399f + Hf(salt, k) * 0.4f;
                var q = o.Yard.P(Mathf.Cos(a) * 0.82f, Mathf.Sin(a) * 0.82f);
                q = o.Yard.P(Mathf.Clamp(Vector2.Dot(q - ctr, U) / hu, -0.78f, 0.78f), Mathf.Clamp(Vector2.Dot(q - ctr, o.Yard.V) / hv, -0.78f, 0.78f));
                if (hasB && inf.R.Contains(q, 0.55f)) continue;
                if ((q - o.Gate).sqrMagnitude < 1.4f * 1.4f || (q - o.Fire).sqrMagnitude < 0.9f * 0.9f) continue;
                bool clash = false;
                foreach (var h in o.Huts) if ((h - q).sqrMagnitude < 1.0f) { clash = true; break; }
                if (!clash) o.Huts.Add(q);
            }
            return true;
        }

        public static Color StanceColor(string att) =>
            att == "Friendly" ? Hex("#6ab04a") : att == "Neutral" ? Hex("#d9a640") : Hex("#d04030");

        /// <summary>Faction compounds, claimed-lot pennants, ruins of fallen camps and every wreck (one mesh).</summary>
        public Chunk BuildWorldLayer(Game game, int selWreck = -1)
        {
            var g = new Geo(new MeshData(), new MeshData());
            if (game?.S == null) return new Chunk { Mesh = g.M, Shadow = g.S };
            foreach (var c in game.S.Camps)
            {
                if (!FactionLayoutOf(c, out var lay)) continue;
                Color fc = Game.FactionColor(c);
                if (!c.Alive) { FactionRuin(g, c, lay); continue; }
                FactionCamp(g, c, lay, fc);
                if (c.Claim != null)
                    foreach (int l in c.Claim)
                        if (l != c.Lot && l >= 0 && l < _info.Length && _info[l].Valid) ClaimPennant(g, l, fc);
            }
            if (game.S.Wrecks != null)
                foreach (var w in game.S.Wrecks)
                {
                    var wp = new Vector2(w.Pos.x + 0.5f, w.Pos.y + 0.5f);
                    float a = (w.Id % 7) * 0.9f + (w.Id % 3) * 0.3f;   // view3d.gd wreck_xf
                    Vehicle(g, wp, new Vector2(Mathf.Cos(a), Mathf.Sin(a)), w.Type, w.Id * 97 + 11, VehLook.Wreck, ROAD_Y + DeckLift(wp));
                    if (w.Id == selWreck) Ring(g, wp, VehicleSize(w.Type).x + 0.35f, Hex("#e0a030"));
                }
            return new Chunk { Mesh = g.M, Shadow = g.S };
        }

        /// <summary>Flat ring on the ground (selection).</summary>
        static void Ring(Geo g, Vector2 p, float r, Color col)
        {
            const int N = 20;
            for (int k = 0; k < N; k++)
            {
                float a0 = k * Mathf.PI * 2f / N, a1 = (k + 1) * Mathf.PI * 2f / N;
                var o0 = new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)); var o1 = new Vector2(Mathf.Cos(a1), Mathf.Sin(a1));
                g.Flat(p + o0 * r, p + o1 * r, p + o1 * (r + 0.09f), p + o0 * (r + 0.09f), 0.06f, col, col, col, col);
            }
        }

        void FactionCamp(Geo g, Camp c, FactionLayout lay, Color fc)
        {
            int salt = c.Lot * 31 + 5;
            var y = lay.Yard;
            bool raiders = c.Att == "Raiders";
            bool scrap = raiders || c.Id == "scrapyard";
            // packed-dirt yard
            Color dirt = raiders ? Hex("#4e4636") : Hex("#5e4c34");
            const int N = 8;
            for (int a = 0; a < N; a++)
                for (int b = 0; b < N; b++)
                {
                    var p00 = y.P(-1f + 2f * a / N, -1f + 2f * b / N); var p10 = y.P(-1f + 2f * (a + 1) / N, -1f + 2f * b / N);
                    var p11 = y.P(-1f + 2f * (a + 1) / N, -1f + 2f * (b + 1) / N); var p01 = y.P(-1f + 2f * a / N, -1f + 2f * (b + 1) / N);
                    Color c0 = Lerp(dirt, YARD_DARK, Noise(p00.x, p00.y, 1.1f, 77) * 0.5f);
                    Color c1 = Lerp(dirt, YARD_DARK, Noise(p10.x, p10.y, 1.1f, 77) * 0.5f);
                    Color c2 = Lerp(dirt, YARD_DARK, Noise(p11.x, p11.y, 1.1f, 77) * 0.5f);
                    Color c3 = Lerp(dirt, YARD_DARK, Noise(p01.x, p01.y, 1.1f, 77) * 0.5f);
                    g.Flat(p00, p10, p11, p01, 0.047f, c0, c1, c2, c3);
                }
            // perimeter: four sides, gap at the gate
            var corners = y.Quad();
            for (int s = 0; s < 4; s++)
            {
                var a = corners[s]; var b = corners[(s + 1) & 3];
                var mid = (a + b) * 0.5f;
                var outd = Geo.OutDir(a, b, y.C);
                float len = (b - a).magnitude; var dirv = (b - a) / len;
                bool gateSide = Vector2.Dot(outd, lay.GateOut) > 0.9f;
                float gt = gateSide ? Vector2.Dot(lay.Gate - a, dirv) : -99f;
                if (scrap) ScrapWall(g, a, dirv, len, outd, gt, salt + s * 101);
                else Palisade(g, a, dirv, len, outd, gt, salt + s * 101, c.Pop >= 20);
            }
            // gate posts, crossbeam with the faction banner
            var gs = new Vector2(-lay.GateOut.y, lay.GateOut.x);
            foreach (int s in new[] { -1, 1 }) g.Cyl(lay.Gate + gs * (0.58f * s), 0.075f, 0f, 1.35f, LOG, LOG_TIP, 6);
            g.Beam(Geo.W(lay.Gate - gs * 0.65f, 1.3f), Geo.W(lay.Gate + gs * 0.65f, 1.3f), 0.045f, TIMBER);
            var b0 = lay.Gate - gs * 0.4f + lay.GateOut * 0.06f; var b1 = lay.Gate + gs * 0.4f + lay.GateOut * 0.06f;
            g.Face(Geo.W(b0, 1.28f), Geo.W(b1, 1.28f), Geo.W(b1 + gs * -0.05f, 0.92f), Geo.W(b0 + gs * 0.05f, 0.92f), fc, Geo.W(lay.GateOut, 0.1f));
            g.Face(Geo.W(b0 + gs * 0.12f, 1.12f), Geo.W(b1 - gs * 0.12f, 1.12f), Geo.W(b1 - gs * 0.14f, 1.05f), Geo.W(b0 + gs * 0.14f, 1.05f), Mul(fc, 0.55f), Geo.W(lay.GateOut, 0.12f));
            // tall flag poles flanking the gate (visible from far)
            foreach (int s in new[] { -1, 1 }) Pennant(g, lay.Gate + gs * (0.95f * s) + lay.GateOut * 0.25f, 2.3f, gs * s, fc, salt + s);
            // flag on the roof (view3d.gd _flag at the lot centre)
            if (lay.HasBld) Pennant(g, lay.Bld.C, lay.Roof + 1.0f, lay.Bld.U, fc, salt + 9, lay.Roof - 0.05f);
            // fire, barrel and bench at the door (view3d.gd), more barrels
            Campfire(g, lay.Fire, salt);
            g.Cyl(lay.Fire + gs * 0.9f, 0.085f, 0.047f, 0.3f, raiders ? Hex("#6e3226") : Hex("#3a4a5a"), Hex("#2e2c28"), 8);
            if (raiders) { g.Disc(lay.Fire + gs * 0.9f, 0.06f, 0.302f, Hex("#ffe070"), Hex("#e0702a"), 6); FlameCone(g, lay.Fire + gs * 0.9f, 0.05f, 0.3f, 0.5f, Hex("#e0702a"), Hex("#fadf2f")); }
            // huts: tents, shacks, a lean-to; colours of the faction on the tent cloth
            int nh = Math.Min(lay.Huts.Count, 2 + c.Pop / 6);
            for (int k = 0; k < nh; k++)
            {
                var hp = lay.Huts[k];
                var toC = (y.C - hp); var hd = toC.sqrMagnitude > 1e-4f ? toC.normalized : y.U;
                float r = Hf(salt, k + 20);
                if (r < 0.45f) FactionTent(g, hp, hd, Lerp(fc, Hex("#6a6048"), 0.45f), salt + k);
                else if (r < 0.8f) Shack(g, hp, y.U, salt + k, scrap);
                else LeanTo(g, hp, hd, salt + k, fc);
            }
            // watchtower for the big camps
            if (c.Pop >= 18 && !lay.HasBld || c.Pop >= 24)
            {
                var tp = y.P(0.8f, 0.8f);
                if (!(lay.HasBld && lay.Bld.Contains(tp, 0.6f))) Watchtower(g, tp, y.U, y.V);
            }
            // crops for the friendly farming camps (the ones with food to spare)
            if (c.Att == "Friendly" && c.Surplus.Contains("food"))
            {
                var cp = y.P(-0.62f, 0.62f);
                if (!(lay.HasBld && lay.Bld.Contains(cp, 0.8f)))
                    for (int row = 0; row < 3; row++)
                    {
                        var rc = cp + y.V * (row * 0.3f - 0.3f);
                        g.BoxAt(rc, y.U, 0.55f, 0.08f, 0.047f, 0.08f, Hex("#4a3a26"), Hex("#4e3e2a"));
                        for (int k = 0; k < 5; k++) g.Cone(rc + y.U * (k * 0.24f - 0.48f), 0.07f, 0.08f, 0.22f, Hex("#5a7a34"), k, 4);
                    }
            }
            // scrap camps park a gun truck at the gate
            if (scrap) Vehicle(g, lay.Gate + lay.GateOut * 1.1f + gs * 1.5f, gs, "pickup", salt, VehLook.Driving, 0.012f);
        }

        void Palisade(Geo g, Vector2 a, Vector2 dirv, float len, Vector2 outd, float gateT, int salt, bool tall)
        {
            int n = Mathf.Max(2, Mathf.RoundToInt(len / 0.11f));
            float h0 = tall ? 0.95f : 0.8f;
            for (int k = 0; k < n; k++)
            {
                float t = (k + 0.5f) * len / n;
                if (gateT > -50f && Mathf.Abs(t - gateT) < 0.52f) continue;
                var p = a + dirv * t;
                float h = h0 + Hf(salt, k) * 0.14f;
                Color lc = Lerp(LOG, LOG_SHADE, Hf(salt + 1, k) * 0.45f);
                var q = new ORect(p, dirv, 0.052f, 0.052f).Quad();
                g.Box(q, 0f, h, lc, lc, 0.2f, false);
                g.Pyramid(q, h, p, h + 0.13f, Lerp(lc, LOG_TIP, 0.5f));
            }
            foreach (float yy in new[] { 0.25f, 0.65f })
            {
                if (gateT > -50f)
                {
                    float g0 = Mathf.Max(0f, gateT - 0.55f), g1 = Mathf.Min(len, gateT + 0.55f);
                    if (g0 > 0.05f) g.Beam(Geo.W(a - outd * 0.07f, yy), Geo.W(a + dirv * g0 - outd * 0.07f, yy), 0.02f, TIMBER);
                    if (g1 < len - 0.05f) g.Beam(Geo.W(a + dirv * g1 - outd * 0.07f, yy), Geo.W(a + dirv * len - outd * 0.07f, yy), 0.02f, TIMBER);
                }
                else g.Beam(Geo.W(a - outd * 0.07f, yy), Geo.W(a + dirv * len - outd * 0.07f, yy), 0.02f, TIMBER);
            }
            var e1 = a + dirv * len;
            g.Shadow(new[] { a, e1, e1 + outd * 0.05f, a + outd * 0.05f }, h0, 0.32f);
        }

        /// <summary>Raider / scrapyard wall: corrugated sheets, car doors and pallets on posts, spikes in front.</summary>
        void ScrapWall(Geo g, Vector2 a, Vector2 dirv, float len, Vector2 outd, float gateT, int salt)
        {
            float t = 0f; int k = 0;
            while (t < len - 0.05f)
            {
                float w = 0.35f + Hf(salt, k * 3) * 0.35f;
                float t1 = Mathf.Min(len, t + w);
                float mid = (t + t1) * 0.5f;
                if (!(gateT > -50f && Mathf.Abs(mid - gateT) < 0.6f))
                {
                    float r = Hf(salt, k * 3 + 1);
                    Color col = r < 0.4f ? Lerp(Hex("#6a6a62"), RUST, Hf(salt, k * 3 + 2) * 0.8f) : r < 0.7f ? Hex("#6b5a46") : CAR_PAINT[k % CAR_PAINT.Length];
                    float h = 0.7f + Hf(salt, k * 5) * 0.35f;
                    var p0 = a + dirv * t; var p1 = a + dirv * t1;
                    g.Box(new[] { p0, p1, p1 - outd * 0.04f, p0 - outd * 0.04f }, 0f, h, col, Mul(col, 1.05f), 0.1f);
                    if (Hf(salt, k * 7) < 0.5f) g.Beam(Geo.W(p0 + outd * 0.05f, 0.1f), Geo.W(p0 + outd * 0.38f, 0.42f), 0.018f, Hex("#5a4632"));   // spike
                }
                t = t1; k++;
            }
            var e1 = a + dirv * len;
            g.Shadow(new[] { a, e1, e1 + outd * 0.05f, a + outd * 0.05f }, 0.8f, 0.32f);
        }

        /// <summary>Flag pole with a two-tone pennant in the faction colour. base0 = foot height (roof flags).</summary>
        void Pennant(Geo g, Vector2 p, float top, Vector2 fly, Color fc, int salt, float base0 = 0f)
        {
            if (fly.sqrMagnitude < 1e-4f) fly = new Vector2(1, 0);
            fly = fly.normalized;
            g.Beam(Geo.W(p, base0), Geo.W(p, top), 0.016f, Hex("#3a3631"));
            float wv = (Hf(salt, 3) - 0.5f) * 0.08f;
            var f1 = p + fly * 0.55f;
            g.QuadRaw(Geo.W(p, top - 0.36f), Geo.W(f1, top - 0.3f + wv), Geo.W(f1, top - 0.05f + wv), Geo.W(p, top - 0.01f), Mul(fc, 0.82f), Mul(fc, 0.72f), fc, Mul(fc, 1.05f));
            g.QuadRaw(Geo.W(p, top - 0.01f), Geo.W(f1, top - 0.05f + wv), Geo.W(f1, top - 0.3f + wv), Geo.W(p, top - 0.36f), Mul(fc, 0.6f), Mul(fc, 0.55f), Mul(fc, 0.65f), Mul(fc, 0.7f));   // back face
            var m0 = p + fly * 0.18f; var m1 = p + fly * 0.4f;
            g.QuadRaw(Geo.W(m0, top - 0.2f), Geo.W(m1, top - 0.19f + wv * 0.7f), Geo.W(m1, top - 0.15f + wv * 0.7f), Geo.W(m0, top - 0.16f), Mul(fc, 0.4f), Mul(fc, 0.4f), Mul(fc, 0.4f), Mul(fc, 0.4f));
        }

        /// <summary>A claimed lot: a pennant on its roof (or at its centre) and a sandbag pile at the door side.</summary>
        void ClaimPennant(Geo g, int l, Color fc)
        {
            var inf = _info[l];
            var p = inf.Floors > 0 ? inf.R.C : inf.Lot.C;
            float roof = inf.Floors > 0 ? Mathf.Max(0.2f, inf.Top) : 0f;
            Pennant(g, p, roof + 0.85f, inf.Lot.U, fc, l, roof - 0.05f);
        }

        void FactionTent(Geo g, Vector2 p, Vector2 dir, Color cloth, int salt)
        {
            var side = new Vector2(-dir.y, dir.x);
            float hl = 0.42f, hw = 0.3f, h = 0.5f;
            var a0 = p - dir * hl - side * hw; var a1 = p + dir * hl - side * hw; var b0 = p - dir * hl + side * hw; var b1 = p + dir * hl + side * hw;
            var r0 = p - dir * hl; var r1 = p + dir * hl;
            g.Face(Geo.W(a0, 0.05f), Geo.W(a1, 0.05f), Geo.W(r1, h), Geo.W(r0, h), cloth, Geo.W(-side, 1f));
            g.Face(Geo.W(b0, 0.05f), Geo.W(b1, 0.05f), Geo.W(r1, h), Geo.W(r0, h), Mul(cloth, 0.92f), Geo.W(side, 1f));
            g.Tri(Geo.W(a0, 0.05f), Geo.W(b0, 0.05f), Geo.W(r0, h), Mul(cloth, 0.6f), Geo.W(-dir, 0f));
            g.Tri(Geo.W(a1, 0.05f), Geo.W(b1, 0.05f), Geo.W(r1, h), Hex("#2a2218"), Geo.W(dir, 0f));   // open flap, dark inside
            g.Shadow(new[] { a0, a1, b1, b0 }, 0.35f, 0.3f);
        }

        void Shack(Geo g, Vector2 p, Vector2 U, int salt, bool scrap)
        {
            var V = new Vector2(-U.y, U.x);
            var r = new ORect(p, Hf(salt, 1) < 0.5f ? U : V, 0.5f, 0.38f);
            Color wall = scrap ? Lerp(Hex("#6a6a62"), RUST, Hf(salt, 2) * 0.7f) : Lerp(Hex("#6b4a30"), Hex("#5a5040"), Hf(salt, 2));
            g.Box(r.Quad(), 0.047f, 0.56f, wall, wall, 0.12f, false);
            // single-pitch tin roof
            var q = r.Inset(-0.06f).Quad();
            Color roof = Lerp(Hex("#6e6a5e"), RUST, Hf(salt, 3) * 0.8f);
            g.Face(Geo.W(q[0], 0.56f), Geo.W(q[1], 0.56f), Geo.W(q[2], 0.7f), Geo.W(q[3], 0.7f), roof, new Vector3(0f, 1f, 0f));
            FaceEdge(r, 0, out var fa, out var fb, out var fo);
            Pane(g, (fa + fb) * 0.5f, (fb - fa).normalized, fo, 0.1f, 0.05f, 0.45f, Hex("#2e2016"), Hex("#3a2a1c"), 0.008f, false);
            if (Hf(salt, 4) < 0.6f) Pane(g, Vector2.Lerp(fa, fb, 0.2f), (fb - fa).normalized, fo, 0.07f, 0.26f, 0.4f, LIT_RIM, LIT_CORE, 0.01f, true);
            g.Shadow(r.Inset(-0.06f).Quad(), 0.68f, 0.32f);
        }

        void LeanTo(Geo g, Vector2 p, Vector2 dir, int salt, Color fc)
        {
            var side = new Vector2(-dir.y, dir.x);
            var back = p - dir * 0.25f;
            foreach (int s in new[] { -1, 1 }) g.Beam(Geo.W(p + dir * 0.2f + side * 0.4f * s, 0f), Geo.W(p + dir * 0.2f + side * 0.4f * s, 0.5f), 0.02f, TIMBER);
            g.Face(Geo.W(back - side * 0.45f, 0.05f), Geo.W(back + side * 0.45f, 0.05f), Geo.W(p + dir * 0.22f + side * 0.45f, 0.52f), Geo.W(p + dir * 0.22f - side * 0.45f, 0.52f), Lerp(fc, Hex("#5a5040"), 0.6f), Geo.W(dir, 1f));
            g.BoxAt(p - dir * 0.05f + side * 0.2f, dir, 0.09f, 0.09f, 0.047f, 0.22f, Hex("#7a5536"), Hex("#8a6a48"));
            g.BoxAt(p - dir * 0.05f - side * 0.15f, dir, 0.08f, 0.12f, 0.047f, 0.16f, Hex("#5a5034"), Hex("#6a5a3a"));
        }

        /// <summary>A camp the dead took: broken, charred palisade, collapsed tents, scorched ground. No flags.</summary>
        void FactionRuin(Geo g, Camp c, FactionLayout lay)
        {
            int salt = c.Lot * 31 + 5;
            var y = lay.Yard;
            g.Disc(lay.Fire, 1.3f, 0.048f, Hex("#1f1d1a"), Hex("#3a3226"), 9, salt);
            var corners = y.Quad();
            for (int s = 0; s < 4; s++)
            {
                var a = corners[s]; var b = corners[(s + 1) & 3];
                float len = (b - a).magnitude; var dirv = (b - a) / len;
                int n = Mathf.Max(2, Mathf.RoundToInt(len / 0.14f));
                for (int k = 0; k < n; k++)
                {
                    if (Hf(salt + s, k) < 0.45f) continue;
                    var p = a + dirv * ((k + 0.5f) * len / n);
                    float h = 0.2f + Hf(salt + s * 3, k) * 0.6f;
                    g.Box(new ORect(p, dirv, 0.05f, 0.05f).Quad(), 0f, h, CHAR, Hex("#3a3226"), 0f, true);
                }
            }
            for (int k = 0; k < Math.Min(3, lay.Huts.Count); k++)
            {
                var hp = lay.Huts[k];
                g.Frustum(new ORect(hp, y.U, 0.4f, 0.3f).Quad(), 0.047f, new ORect(hp, y.U, 0.3f, 0.2f).Quad(), 0.12f, Hex("#3a342a"), Hex("#4a4030"));
            }
        }
    }
}
