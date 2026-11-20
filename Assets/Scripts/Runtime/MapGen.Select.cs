// Start-site selection and camp layout queries for MapGen (v0.11.4a): ray picking of buildings,
// the amber selection highlight, a readable description of a lot, and the camp yard layout the
// crowd layer (Crowd.cs) uses to keep survivors inside and zombies at the palisade.
using System;
using System.Collections.Generic;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed partial class MapGen
    {
        /// <summary>Camp yard as drawn by Camp(): square yard (palisade at its edge), the camp building
        /// footprint (if the camp is in a building), the campfire and the watchtower (main camp only).</summary>
        public struct CampLayout
        {
            public ORect Yard; public bool HasBld; public ORect Bld; public Vector2 Fire, Tower, Gate; public bool Main; public float TowerTop;
        }

        public bool Layout(Base b, out CampLayout o)
        {
            o = default;
            if (b == null) return false;
            if (PlainCamp) return PlainLayout(b, out o);
            CampRect(b, out var c, out float half, out var bld);
            var V = -CampFront(); var U = new Vector2(V.y, -V.x);
            o.Yard = new ORect(c, U, half, half);
            o.HasBld = bld.HasValue; if (bld.HasValue) o.Bld = bld.Value;
            o.Fire = DrySpot(CampFireAt(c, half, U, V, bld), c);   // v0.19 audit: never in the river (visual / crowd only)
            o.Tower = c + U * (half - 0.5f) + V * (half - 0.5f);
            o.Gate = c - V * half;
            o.Main = b.Main;
            o.TowerTop = 1.92f;
            return true;
        }

        /// <summary>v0.14.0a: under the original rules there is no pre-built compound (Austin: "get rid of the original
        /// starting base"): the camp is the building itself with a campfire and flag at its door, and the player fences
        /// it in with the FENCE tool. The yard is the building footprint plus a narrow apron; the "tower" spot is the
        /// door at ground level (TowerTop 0), so a guard on duty stands at the door.</summary>
        bool PlainLayout(Base b, out CampLayout o)
        {
            o = default;
            var door = new Vector2(b.Pos.x + 0.5f, b.Pos.y + 0.5f);
            bool inBld = b.Lot >= 0 && b.Lot < _info.Length && _info[b.Lot].Valid && _info[b.Lot].SimBld;
            if (inBld)
            {
                var r = _info[b.Lot].R0;   // sim-facing: unclipped footprint (MapGen.Buildings.cs LandLotRect)
                o.HasBld = true; o.Bld = r;
                o.Yard = new ORect(r.C, r.U, r.Hu + 0.9f, r.Hv + 0.9f);
                var outv = door - r.C; if (outv.sqrMagnitude < 1e-4f) outv = GU;
                outv = outv.normalized;
                o.Fire = DrySpot(door + outv * 0.9f + new Vector2(-outv.y, outv.x) * 0.6f, door);   // v0.19 audit: not in the river
            }
            else
            {
                o.Yard = new ORect(door, GU, 2.0f, 2.0f);
                o.Fire = DrySpot(door + GU * 0.5f, door);
            }
            o.Gate = door; o.Tower = door; o.TowerTop = 0f; o.Main = b.Main;
            return true;
        }

        static Vector2 CampFireAt(Vector2 c, float half, Vector2 U, Vector2 V, ORect? bld)
        {
            if (bld.HasValue)
            {
                var b = bld.Value;
                float front = Mathf.Abs(Vector2.Dot(b.C - c, V)) + Mathf.Abs(Vector2.Dot(b.U, V)) * b.Hu + Mathf.Abs(Vector2.Dot(b.V, V)) * b.Hv;
                return c - V * ((front + half) * 0.5f) + U * 0.6f;
            }
            return c + U * 0.3f - V * 0.4f;
        }

        /// <summary>Building footprint and roof height of a lot (false for open lots).</summary>
        public bool LotBox(int l, out ORect r, out float top)
        {
            r = default; top = 0f;
            if (l < 0 || l >= _info.Length || !_info[l].Valid || _info[l].Floors <= 0) return false;
            r = _info[l].R; top = Mathf.Max(0.2f, _info[l].Top);
            return true;
        }

        /// <summary>First building hit by a ray (world space; tile (x,y) = world (x, h, -y)). Marches the ray in
        /// 0.12-tile steps against building boxes; a ray that reaches the ground picks the building whose footprint
        /// (padded) contains the hit, so a tap at the foot of a wall still counts. -1 when nothing is hit.</summary>
        public int PickLot(Vector3 o, Vector3 dir, float maxT = 3000f)
        {
            if (dir.y >= -1e-4f) return -1;
            // start where the ray comes down to roof height (no building is taller than ~40 tiles)
            float t0 = Mathf.Max(0f, (o.y - 40f) / -dir.y), t1 = Mathf.Min(maxT, o.y / -dir.y);
            const float step = 0.12f;
            int last = -2;
            for (float t = t0; t <= t1 + step; t += step)
            {
                var p = o + dir * t;
                var tp = new Vector2(p.x, -p.z);
                int tx = Mathf.FloorToInt(tp.x), ty = Mathf.FloorToInt(tp.y);
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int x = tx + dx, y = ty + dy;
                        if (x < 0 || y < 0 || x >= d.W || y >= d.H) continue;
                        int l = d.LotId[y * d.W + x];
                        if (l < 0 || l == last || !LotBox(l, out var r, out float top)) continue;
                        if (p.y <= top + 0.05f && r.Contains(tp)) return l;
                    }
                if (p.y <= 0.05f)
                {
                    // ground: nearest footprint within a small pad
                    int best = -1; float bd = 0.45f;
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int x = tx + dx, y = ty + dy;
                            if (x < 0 || y < 0 || x >= d.W || y >= d.H) continue;
                            int l = d.LotId[y * d.W + x];
                            if (l < 0 || !LotBox(l, out var r, out _)) continue;
                            var dd = tp - r.C;
                            float ou = Mathf.Max(0f, Mathf.Abs(Vector2.Dot(dd, r.U)) - r.Hu), ov = Mathf.Max(0f, Mathf.Abs(Vector2.Dot(dd, r.V)) - r.Hv);
                            float e = Mathf.Sqrt(ou * ou + ov * ov);
                            if (e < bd) { bd = e; best = l; }
                        }
                    return best;
                }
            }
            return -1;
        }

        static readonly Dictionary<string, string> KIND_NAMES = new Dictionary<string, string>
        {
            { "house", "House" }, { "farmhouse", "Farmhouse" }, { "cabin", "Cabin" }, { "shack", "Shack" }, { "ranger", "Ranger station" },
            { "camp_hut", "Hut" }, { "stand", "Farm stand" }, { "barn", "Barn" }, { "apartment", "Apartment block" }, { "office", "Office building" },
            { "factory", "Factory" }, { "warehouse", "Warehouse" }, { "supermarket", "Supermarket" }, { "hospital", "Hospital" },
            { "police", "Police station" }, { "fire_station", "Fire station" }, { "school", "School" }, { "checkpoint", "Checkpoint" },
            { "church", "Church" }, { "gas_station", "Gas station" }, { "parking", "Parking lot" }, { "farm", "Farm" }, { "shop", "Shop" },
        };

        /// <summary>Readable description for the selection panel: type, footprint in metres, floors.</summary>
        public void Describe(int l, out string type, out float widthM, out float depthM, out int floors)
        {
            type = "Building"; widthM = depthM = 0f; floors = 0;
            if (l < 0 || l >= d.Lots.Count) return;
            var L = d.Lots[l];
            if (!KIND_NAMES.TryGetValue(L.Kind ?? "", out type))
                type = string.IsNullOrEmpty(L.Kind) ? "Building" : char.ToUpperInvariant(L.Kind[0]) + L.Kind.Substring(1).Replace('_', ' ');
            if (!string.IsNullOrEmpty(L.Landmark)) type += " (landmark)";
            if (LotBox(l, out var r, out _))
            {
                float a = r.Hu * 2f * 3.5f, b = r.Hv * 2f * 3.5f;
                widthM = Mathf.Max(a, b); depthM = Mathf.Min(a, b);
                floors = Mathf.Max(1, _info[l].Floors);
            }
        }

        /// <summary>A building tile of the lot (the game addresses lots by tile: StartAt, CanStartAt).</summary>
        public Vector2Int LotTile(int l)
        {
            if (l < 0 || l >= d.Lots.Count) return new Vector2Int(-1, -1);
            var c = d.LotCenter(l);
            var ct = new Vector2Int(Mathf.FloorToInt(c.x), Mathf.FloorToInt(c.y));
            if (d.BuildingAt(ct) == l) return ct;
            var L = d.Lots[l];
            Vector2Int best = new Vector2Int(-1, -1); float bd = 1e9f;
            for (int y = L.Y; y < L.Y + L.H; y++)
                for (int x = L.X; x < L.X + L.W; x++)
                {
                    var q = new Vector2Int(x, y);
                    if (d.BuildingAt(q) != l) continue;
                    float dd = (x + 0.5f - c.x) * (x + 0.5f - c.x) + (y + 0.5f - c.y) * (y + 0.5f - c.y);
                    if (dd < bd) { bd = dd; best = q; }
                }
            return best.x >= 0 ? best : ct;
        }

        static readonly Color SEL = Hex("#f2b440"), SEL_HOT = Hex("#ffd27a"), SEL_BAD = Hex("#d0583e");

        /// <summary>Selection highlight: glowing amber outline on the roof and base edges, corner posts, a ground
        /// glow decal (alpha, drawn with the shadow material) and a floating marker over the roof. ok=false paints it red.</summary>
        public Chunk Highlight(int l, bool ok)
        {
            var g = new Geo(new MeshData(), new MeshData());
            if (!LotBox(l, out var r, out float top)) return new Chunk { Mesh = g.M, Shadow = g.S };
            Color c = ok ? SEL : SEL_BAD, hot = ok ? SEL_HOT : Hex("#f08060");
            var R = new ORect(r.C, r.U, r.Hu + 0.03f, r.Hv + 0.03f);
            var q = R.Quad();
            float w = 0.05f;
            for (int i = 0; i < 4; i++)
            {
                var a = q[i]; var b = q[(i + 1) & 3];
                GlowBeam(g, Geo.W(a, top + 0.04f), Geo.W(b, top + 0.04f), w, hot);
                GlowBeam(g, Geo.W(a, 0.06f), Geo.W(b, 0.06f), w * 1.2f, c);
                GlowBeam(g, Geo.W(a, 0.06f), Geo.W(a, top + 0.04f), w, c);
            }
            // ground glow: alpha ring fading outward (shadow material = alpha-blended)
            const int RINGS = 3;
            for (int k = 0; k < RINGS; k++)
            {
                float e0 = 0.05f + k * 0.28f, e1 = e0 + 0.28f;
                float a0 = 0.62f * (1f - (float)k / RINGS), a1 = 0.62f * (1f - (float)(k + 1) / RINGS);
                var i0 = new ORect(r.C, r.U, r.Hu + e0, r.Hv + e0).Quad(); var o0 = new ORect(r.C, r.U, r.Hu + e1, r.Hv + e1).Quad();
                for (int i = 0; i < 4; i++)
                {
                    int j = (i + 1) & 3;
                    GlowQuad(g.S, Geo.W(i0[i], SHADOW_Y_GLOW), Geo.W(i0[j], SHADOW_Y_GLOW), Geo.W(o0[j], SHADOW_Y_GLOW), Geo.W(o0[i], SHADOW_Y_GLOW), c, a0, a0, a1, a1);
                }
            }
            // floating marker: downward-pointing diamond over the roof
            var m = Geo.W(r.C, top + 1.7f);
            float s = 0.5f;
            GlowBeam(g, Geo.W(r.C, top + 0.05f), m + new Vector3(0f, -s * 1.3f, 0f), 0.025f, c);
            var tip = m + new Vector3(0f, -s * 1.3f, 0f); var up = m + new Vector3(0f, s * 0.9f, 0f);
            var e = new[] { m + new Vector3(s, 0, 0), m + new Vector3(0, 0, s), m + new Vector3(-s, 0, 0), m + new Vector3(0, 0, -s) };
            for (int i = 0; i < 4; i++)
            {
                var a = e[i]; var b = e[(i + 1) & 3];
                g.Tri(a, b, tip, Geo.Lerp(c, Hex("#6a4a1a"), i % 2 == 0 ? 0.0f : 0.25f), (a + b) * 0.5f - m + new Vector3(0, -0.3f, 0));
                g.Tri(a, b, up, Geo.Lerp(hot, c, i % 2 == 0 ? 0f : 0.3f), (a + b) * 0.5f - m + new Vector3(0, 0.3f, 0));
            }
            return new Chunk { Mesh = g.M, Shadow = g.S };
        }

        const float SHADOW_Y_GLOW = 0.055f;

        /// <summary>Unshaded box beam (emissive look).</summary>
        static void GlowBeam(Geo g, Vector3 a, Vector3 b, float w, Color col)
        {
            var dd = b - a; float len = dd.magnitude; if (len < 1e-5f) return;
            var f = dd / len;
            var side = Vector3.Cross(f, Mathf.Abs(f.y) > 0.9f ? Vector3.right : Vector3.up).normalized * w;
            var up = Vector3.Cross(side.normalized, f).normalized * w;
            var p = new[] { a - side - up, a + side - up, a + side + up, a - side + up };
            var q = new[] { b - side - up, b + side - up, b + side + up, b - side + up };
            for (int i = 0; i < 4; i++)
            {
                int j = (i + 1) & 3;
                var k = i == 2 ? col : Geo.Mul(col, 0.86f);
                g.QuadRaw(p[i], p[j], q[j], q[i], k, k, k, k);
            }
        }

        static void GlowQuad(MeshData s, Vector3 a, Vector3 b, Vector3 c, Vector3 e, Color col, float aa, float ab, float ac, float ae)
        {
            int i = s.V.Count;
            s.V.Add(a); s.V.Add(b); s.V.Add(c); s.V.Add(e);
            s.C.Add(A(col, aa)); s.C.Add(A(col, ab)); s.C.Add(A(col, ac)); s.C.Add(A(col, ae));
            s.T.Add(i); s.T.Add(i + 1); s.T.Add(i + 2); s.T.Add(i); s.T.Add(i + 2); s.T.Add(i + 3);
        }

        static Color32 A(Color c, float a) => new Color32((byte)(Mathf.Clamp01(c.r) * 255f), (byte)(Mathf.Clamp01(c.g) * 255f), (byte)(Mathf.Clamp01(c.b) * 255f), (byte)(Mathf.Clamp01(a) * 255f));
    }
}
