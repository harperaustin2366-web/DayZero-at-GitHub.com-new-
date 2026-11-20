// Building kit for MapGen (art bible section 7): per-archetype massing with real proportions,
// window grids with frames, parapets and roof clutter, gable roofs, damage states 0-3, ivy,
// grime, rubble, fences, parking lots. All vertex-coloured, lighting baked (MapGeo.Shade).
using System;
using System.Collections.Generic;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed partial class MapGen
    {
        enum Arch { None, House, Apartment, Office, Industrial, Shop, Civic, Church, Gas, Parking, Farm, Hut, Barn }

        struct LotInfo
        {
            public Arch A;
            public ORect Lot, R;          // whole lot (tile coverage) and building footprint (visual: clipped to land)
            public ORect R0;              // building footprint from the unclipped lot rect (sim-facing camp layout)
            public bool SimBld;           // Floors > 0 before clipping (sim-facing: camp in a building)
            public bool Bank;             // clipped against water: drawn with a foundation skirt down the bank
            public int Floors, Dmg;
            public float FloorH, WallTop, Top;
            public Color Wall, Roof;
            public int Front;              // facade index facing the door / street
            public bool Valid;
        }

        // ---------------- palette (bible 2.2) ----------------
        static readonly Color[] CONCRETE = { Hex("#726356"), Hex("#6e6458"), Hex("#6a5e50"), Hex("#655c50"), Hex("#77695a") };
        static readonly Color[] SIDING = { Hex("#64624d"), Hex("#7a6e58"), Hex("#5a5c44"), Hex("#4e5a5c"), Hex("#6e6656"), Hex("#706a5a") };
        static readonly Color[] SHINGLE = { Hex("#595241"), Hex("#414038"), Hex("#504935"), Hex("#4a463c"), Hex("#5e4a3a") };
        static readonly Color[] OFFICE = { Hex("#6e6a62"), Hex("#5c6670"), Hex("#7a7468"), Hex("#8a8376"), Hex("#626058") };
        static readonly Color[] SHED = { Hex("#6a6a5e"), Hex("#6e6656"), Hex("#5e5a4e"), Hex("#5a6048") };
        static readonly Color BRICK = Hex("#7a4a3a"), PALE = Hex("#8a8376"), FLATROOF = Hex("#57534a"), TAR = Hex("#423f38");
        static readonly Color GLASS = Hex("#353f3e"), GLASS_HI = Hex("#4c5652"), VOIDC = Hex("#151412"), BOARD = Hex("#865e3e");
        static readonly Color TRIM = Hex("#8c8678"), MOSS = Hex("#4a4f26"), MOSS_DEEP = Hex("#33361c"), IVY = Hex("#444124");
        static readonly Color CHAR = Hex("#262320"), SCORCH = Hex("#1f1d1a"), RUBBLE_L = Hex("#807a5c"), RUBBLE_D = Hex("#585b4e");
        static readonly Color INTERIOR = Hex("#2a2620"), DOOR = Hex("#3a2e24"), RUST = Hex("#7a4a2a"), FENCE = Hex("#6b5a46");
        static readonly Color LIT_CORE = Hex("#f2b45a"), LIT_RIM = Hex("#c8752e");

        static Color Pick(Color[] a, int l, int salt) => a[(int)(Hf(l, salt) * a.Length) % a.Length];

        string DistrictKind(Lot L) => d.Districts.Count > 0 ? d.Districts[Mathf.Clamp(L.District, 0, d.Districts.Count - 1)].Kind ?? "" : "";

        /// <summary>Oriented rectangle covering the lot's tiles (axes = lot angle). This is the footprint the sim-facing
        /// queries keep (Layout / YardFor: the starter palisade), so it must not change.</summary>
        ORect LotRect(int l) => RectOfTiles(l, LotTiles(l));

        List<Vector2Int> LotTiles(int l)
        {
            var L = d.Lots[l]; var o = new List<Vector2Int>();
            for (int y = L.Y; y < L.Y + L.H; y++)
                for (int x = L.X; x < L.X + L.W; x++)
                    if (x >= 0 && y >= 0 && x < d.W && y < d.H && d.LotId[y * d.W + x] == l) o.Add(new Vector2Int(x, y));
            return o;
        }

        ORect RectOfTiles(int l, List<Vector2Int> tiles)
        {
            double th = d.LotAngle(l) * Math.PI / 180.0;
            float cu = (float)Math.Cos(th), su = (float)Math.Sin(th);
            float a0 = 1e9f, a1 = -1e9f, b0 = 1e9f, b1 = -1e9f;
            foreach (var t in tiles)
            {
                float px = t.x + 0.5f, py = t.y + 0.5f;
                float u = px * cu + py * su, v = -px * su + py * cu;
                a0 = Mathf.Min(a0, u); a1 = Mathf.Max(a1, u); b0 = Mathf.Min(b0, v); b1 = Mathf.Max(b1, v);
            }
            if (a0 > a1) return new ORect(Vector2.zero, Vector2.right, 0f, 0f);
            const float pad = 0.42f;
            a0 -= pad; a1 += pad; b0 -= pad; b1 += pad;
            var U = new Vector2(cu, su); var V = new Vector2(-su, cu);
            var c = U * ((a0 + a1) * 0.5f) + V * ((b0 + b1) * 0.5f);
            return new ORect(c, U, (a1 - a0) * 0.5f, (b1 - b0) * 0.5f);
        }

        // ---------------- v0.19 map audit: lots stay on land ----------------
        // The baked city has lots the river was carved through (a parking lot with tiles on both banks) and lots whose
        // oriented rectangle reaches over the bank. Visual footprints (yard, building) use the lot's largest connected
        // tile group and are clipped back from water / pier tiles with a bank margin. Deterministic, visual only.
        static bool Wet(int t) => t == Data.T_WATER || t == Data.T_PIER;

        /// <summary>Water / pier tiles overlapping r grown by m (separating axes: the rect's and the tile grid's).</summary>
        void WetTilesNear(ORect r, float m, List<Vector2Int> o)
        {
            o.Clear();
            float ex = Mathf.Abs(r.U.x) * r.Hu + Mathf.Abs(r.V.x) * r.Hv + m, ey = Mathf.Abs(r.U.y) * r.Hu + Mathf.Abs(r.V.y) * r.Hv + m;
            for (int y = Mathf.FloorToInt(r.C.y - ey); y <= Mathf.FloorToInt(r.C.y + ey); y++)
                for (int x = Mathf.FloorToInt(r.C.x - ex); x <= Mathf.FloorToInt(r.C.x + ex); x++)
                {
                    if (!Wet(TileAt(x, y))) continue;
                    float u0 = 1e9f, u1 = -1e9f, w0 = 1e9f, w1 = -1e9f;
                    for (int k = 0; k < 4; k++)
                    {
                        var q = new Vector2(x + (k & 1), y + (k >> 1)) - r.C;
                        float u = Vector2.Dot(q, r.U), w = Vector2.Dot(q, r.V);
                        u0 = Mathf.Min(u0, u); u1 = Mathf.Max(u1, u); w0 = Mathf.Min(w0, w); w1 = Mathf.Max(w1, w);
                    }
                    if (u1 <= -r.Hu - m || u0 >= r.Hu + m || w1 <= -r.Hv - m || w0 >= r.Hv + m) continue;
                    o.Add(new Vector2Int(x, y));
                }
        }

        readonly List<Vector2Int> _wet = new List<Vector2Int>(32);

        /// <summary>Shrinks r side by side until no water / pier tile is within m of it: each step cuts the one side
        /// (against the wet tiles on that side of the centre) that keeps the most area.</summary>
        ORect ClipToLand(ORect r, float m)
        {
            for (int it = 0; it < 8; it++)
            {
                WetTilesNear(r, m, _wet);
                if (_wet.Count == 0) return r;
                float bestA = -1f; float na0 = 0, na1 = 0, nb0 = 0, nb1 = 0;
                for (int side = 0; side < 4; side++)
                {
                    float a0 = -r.Hu, a1 = r.Hu, b0 = -r.Hv, b1 = r.Hv; bool any = false;
                    foreach (var t in _wet)
                    {
                        float u0 = 1e9f, u1 = -1e9f, w0 = 1e9f, w1 = -1e9f;
                        for (int k = 0; k < 4; k++)
                        {
                            var q = new Vector2(t.x + (k & 1), t.y + (k >> 1)) - r.C;
                            float u = Vector2.Dot(q, r.U), w = Vector2.Dot(q, r.V);
                            u0 = Mathf.Min(u0, u); u1 = Mathf.Max(u1, u); w0 = Mathf.Min(w0, w); w1 = Mathf.Max(w1, w);
                        }
                        float cu = (u0 + u1) * 0.5f, cw = (w0 + w1) * 0.5f;
                        switch (side)
                        {
                            case 0: if (cu > 0f) { a1 = Mathf.Min(a1, u0 - m); any = true; } break;
                            case 1: if (cu <= 0f) { a0 = Mathf.Max(a0, u1 + m); any = true; } break;
                            case 2: if (cw > 0f) { b1 = Mathf.Min(b1, w0 - m); any = true; } break;
                            default: if (cw <= 0f) { b0 = Mathf.Max(b0, w1 + m); any = true; } break;
                        }
                    }
                    if (!any) continue;
                    float area = Mathf.Max(0f, a1 - a0) * Mathf.Max(0f, b1 - b0);
                    if (area > bestA) { bestA = area; na0 = a0; na1 = a1; nb0 = b0; nb1 = b1; }
                }
                if (bestA <= 0f || na1 - na0 < 0.1f || nb1 - nb0 < 0.1f) return new ORect(r.C, r.U, 0f, 0f);
                r = new ORect(r.C + r.U * ((na0 + na1) * 0.5f) + r.V * ((nb0 + nb1) * 0.5f), r.U, (na1 - na0) * 0.5f, (nb1 - nb0) * 0.5f);
            }
            WetTilesNear(r, m, _wet);
            return _wet.Count == 0 ? r : new ORect(r.C, r.U, 0f, 0f);
        }

        /// <summary>v0.19 map audit: p lies inside a drawn building footprint (grown by pad), checked against the lots of
        /// the 3x3 tiles around it. Props, clutter, street furniture and trees use it to stay out of walls.</summary>
        bool InBuilding(Vector2 p, float pad = 0.06f, int except = -1)
        {
            int tx = Mathf.FloorToInt(p.x), ty = Mathf.FloorToInt(p.y), last = -2;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = tx + dx, y = ty + dy;
                    if (x < 0 || y < 0 || x >= d.W || y >= d.H) continue;
                    int l = d.LotId[y * d.W + x];
                    if (l < 0 || l == last || l == except || l >= _info.Length) continue;
                    last = l;
                    var i = _info[l];
                    if (!i.Valid || i.Floors <= 0 || i.R.Hu < 0.05f) continue;
                    if (i.R.Contains(p, pad)) return true;
                }
            return false;
        }

        /// <summary>Height of the LOD0 ground mesh at p (AddGround: banks dip toward the water; 0 elsewhere).</summary>
        float GroundH(Vector2 p)
        {
            int x = Mathf.FloorToInt(p.x), y = Mathf.FloorToInt(p.y);
            float fx = p.x - x, fy = p.y - y;
            float VH(int vx, int vy)
            {
                int w = 0;
                for (int k = 0; k < 4; k++) if (TileAt(vx - 1 + (k & 1), vy - 1 + (k >> 1)) == Data.T_WATER) w++;
                return w == 0 ? 0f : -0.03f - 0.27f * (w / 4f);
            }
            float a = VH(x, y), b = VH(x + 1, y), c = VH(x + 1, y + 1), e = VH(x, y + 1);
            if (a == 0f && b == 0f && c == 0f && e == 0f) return 0f;
            // triangles (a,b,c) and (a,c,e) as AddGround splits the quad
            return fx >= fy ? a + (b - a) * fx + (c - b) * fy : a + (c - e) * fx + (e - a) * fy;
        }

        /// <summary>Drops the vertices added since v0 by the ground height at p (trees / bushes on a sloping bank).</summary>
        void SinkToGround(Geo g, int v0, Vector2 p)
        {
            float h = GroundH(p);
            if (h == 0f) return;
            for (int i = v0; i < g.M.V.Count; i++) { var v = g.M.V[i]; g.M.V[i] = new Vector3(v.x, v.y + h, v.z); }
        }

        /// <summary>v0.19 map audit: no water / pier tile within r of p (props stay off the river and its bank edge).</summary>
        bool DryAt(Vector2 p, float r = 0.2f)
        {
            for (int k = 0; k < 5; k++)
            {
                var q = k == 0 ? p : p + new Vector2(k == 1 ? r : k == 2 ? -r : 0f, k == 3 ? r : k == 4 ? -r : 0f);
                if (Wet(TileAt(Mathf.FloorToInt(q.x), Mathf.FloorToInt(q.y)))) return false;
            }
            return true;
        }

        /// <summary>Last resort for small shoreline lots: the footprint scaled down about the centre of the lot's land tiles
        /// until it is dry (keeps a building where the sim has one).</summary>
        ORect ShrinkToLand(int l, ORect r, float m)
        {
            Vector2 c = Vector2.zero; int n = 0;
            foreach (var t in LotTiles(l)) if (!Wet(TileAt(t.x, t.y))) { c += new Vector2(t.x + 0.5f, t.y + 0.5f); n++; }
            if (n == 0) return new ORect(r.C, r.U, 0f, 0f);
            c /= n;
            for (float k = 0.9f; k >= 0.35f; k -= 0.05f)
            {
                var q = new ORect(c, r.U, Mathf.Max(0.22f, r.Hu * k), Mathf.Max(0.22f, r.Hv * k));
                WetTilesNear(q, m, _wet);
                if (_wet.Count == 0) return q;
            }
            return new ORect(r.C, r.U, 0f, 0f);
        }

        /// <summary>Visual lot rectangle: the sim rectangle when it is dry, else the largest connected tile group of the
        /// lot (a river carved through it) clipped to land with a bank margin.</summary>
        ORect LandLotRect(int l, ORect sim, out bool clipped)
        {
            clipped = false;
            if (sim.Hu <= 0.05f) return sim;
            WetTilesNear(sim, LOT_BANK, _wet);
            if (_wet.Count == 0) return sim;
            clipped = true;
            var tiles = LotTiles(l);
            // largest 8-connected group of land tiles
            var set = new HashSet<Vector2Int>();
            foreach (var t in tiles) if (!Wet(TileAt(t.x, t.y))) set.Add(t);
            List<Vector2Int> best = null; var seen = new HashSet<Vector2Int>(); var q = new Queue<Vector2Int>();
            foreach (var t0 in tiles)
            {
                if (!set.Contains(t0) || seen.Contains(t0)) continue;
                var grp = new List<Vector2Int>(); seen.Add(t0); q.Enqueue(t0);
                while (q.Count > 0)
                {
                    var t = q.Dequeue(); grp.Add(t);
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                        {
                            var n = new Vector2Int(t.x + dx, t.y + dy);
                            if (set.Contains(n) && seen.Add(n)) q.Enqueue(n);
                        }
                }
                if (best == null || grp.Count > best.Count) best = grp;
            }
            if (best == null) return new ORect(sim.C, sim.U, 0f, 0f);
            return ClipToLand(RectOfTiles(l, best), LOT_BANK);
        }

        /// <summary>Building footprint inside lot rect lr per archetype (the insets PlanLot uses).</summary>
        static ORect Foot(Arch a, ORect lr)
        {
            switch (a)
            {
                case Arch.House: return lr.Inset(Mathf.Min(0.42f, lr.Short * 0.28f));
                case Arch.Hut: return lr.Inset(Mathf.Min(0.35f, lr.Short * 0.3f));
                case Arch.Barn: case Arch.Church: return lr.Inset(0.2f);
                case Arch.Apartment: return lr.Inset(0.14f);
                case Arch.Office: return lr.Inset(0.12f);
                case Arch.Industrial: case Arch.Civic: case Arch.Gas: return lr.Inset(0.15f);
                case Arch.Parking: case Arch.Farm: return lr;
                default: return lr.Inset(0.1f);
            }
        }

        const float LOT_BANK = 0.02f, BLD_BANK = 0.15f;   // clearance from water / pier tiles: lot surfaces, building walls

        static Arch ArchOf(string k)
        {
            switch (k)
            {
                case "house": case "farmhouse": return Arch.House;
                case "cabin": case "shack": case "ranger": case "camp_hut": case "stand": return Arch.Hut;
                case "barn": return Arch.Barn;
                case "apartment": return Arch.Apartment;
                case "office": return Arch.Office;
                case "factory": case "warehouse": case "supermarket": return Arch.Industrial;
                case "hospital": case "police": case "fire_station": case "school": case "checkpoint": return Arch.Civic;
                case "church": return Arch.Church;
                case "gas_station": return Arch.Gas;
                case "parking": return Arch.Parking;
                case "farm": return Arch.Farm;
                default: return Arch.Shop;
            }
        }

        LotInfo PlanLot(int l)
        {
            var L = d.Lots[l];
            var simLot = LotRect(l);
            var info = new LotInfo { A = ArchOf(L.Kind), Lot = LandLotRect(l, simLot, out bool clipped) };
            info.Bank = clipped;
            if (info.Lot.Hu <= 0.05f || info.Lot.Hv <= 0.05f) return info;
            info.Valid = true;
            float r = Hf(l, 1), r2 = Hf(l, 2);
            bool landmark = !string.IsNullOrEmpty(L.Landmark);
            string dk = DistrictKind(L);
            // damage state (bible: 35/35/20/10; downtown heavier, landmarks lighter)
            float dr = Hf(l, 7);
            if (dk == "downtown") dr = dr * 0.85f + 0.15f;
            info.Dmg = dr < 0.40f ? 0 : dr < 0.74f ? 1 : dr < 0.92f ? 2 : 3;
            if (landmark) info.Dmg = Math.Min(info.Dmg, 1);
            info.FloorH = FLOOR;
            var lr = info.Lot;
            switch (info.A)
            {
                case Arch.House:
                    info.R = lr.Inset(Mathf.Min(0.42f, lr.Short * 0.28f));
                    info.Floors = L.Kind == "farmhouse" || r > 0.45f ? 2 : 1;
                    info.WallTop = info.Floors * FLOOR * 0.94f + 0.08f;
                    info.Wall = Hf(l, 3) < 0.15f ? BRICK : Pick(SIDING, l, 3);
                    info.Roof = Hf(l, 4) < 0.14f ? Hex("#6a4632") : Pick(SHINGLE, l, 4);
                    break;
                case Arch.Hut:
                    info.R = lr.Inset(Mathf.Min(0.35f, lr.Short * 0.3f));
                    info.Floors = 1; info.WallTop = FLOOR * 0.85f;
                    info.Wall = Hex("#5e4a36"); info.Roof = Pick(SHINGLE, l, 4);
                    break;
                case Arch.Barn:
                    info.R = lr.Inset(0.2f); info.Floors = 1; info.WallTop = 1.3f;
                    info.Wall = Hex("#6e3a2e"); info.Roof = Hex("#4a4a40");
                    break;
                case Arch.Apartment:
                    info.R = lr.Inset(0.14f);
                    info.Floors = 2 + (int)(r * 2.99f);
                    if (dk == "downtown" || dk == "midtown") info.Floors += (int)(r2 * 2.5f);
                    info.Floors = Math.Min(info.Floors, Math.Max(2, Mathf.RoundToInt(info.R.Short * 2f / FLOOR * 1.4f)));
                    info.WallTop = info.Floors * FLOOR + 0.12f;
                    info.Wall = Hf(l, 3) < 0.22f ? BRICK : Hf(l, 3) < 0.32f ? PALE : Pick(CONCRETE, l, 5);
                    info.Roof = FLATROOF;
                    break;
                case Arch.Office:
                {
                    info.R = lr.Inset(0.12f);
                    int fl = 3 + (int)(r * 2.99f);
                    if (dk == "downtown") fl += 2 + (int)(r2 * 5f);
                    else if (dk == "midtown") fl += (int)(r2 * 2f);
                    fl = Math.Min(fl, Math.Max(3, Mathf.RoundToInt(info.R.Short * 2f / (3.6f * MT) * 2.2f)));
                    info.Floors = fl; info.FloorH = 3.6f * MT; info.WallTop = fl * info.FloorH + 0.1f;
                    info.Wall = Pick(OFFICE, l, 5); info.Roof = FLATROOF;
                    break;
                }
                case Arch.Industrial:
                    info.R = lr.Inset(0.15f); info.Floors = 1;
                    info.WallTop = L.Kind == "supermarket" ? 1.45f : 1.7f + r * 0.6f;
                    info.Wall = Pick(SHED, l, 5); info.Roof = Hex("#5a6048");
                    break;
                case Arch.Civic:
                    info.R = lr.Inset(0.15f);
                    info.Floors = L.Kind == "hospital" ? 4 : L.Kind == "checkpoint" ? 1 : 3;
                    info.WallTop = info.Floors * FLOOR + 0.15f;
                    info.Wall = L.Kind == "police" ? Hex("#6a6e70") : L.Kind == "fire_station" ? Hex("#7a4a3a") : PALE;
                    info.Roof = FLATROOF;
                    break;
                case Arch.Church:
                    info.R = lr.Inset(0.2f); info.Floors = 1; info.WallTop = 1.6f;
                    info.Wall = Hex("#7a7468"); info.Roof = Hex("#4a463c");
                    break;
                case Arch.Gas:
                    info.R = lr.Inset(0.15f); info.Floors = 1; info.WallTop = 1.25f;
                    info.Wall = Hex("#a8a49a"); info.Roof = Hex("#a8a49a");
                    break;
                case Arch.Parking: case Arch.Farm:
                    info.R = lr; info.Floors = 0; info.WallTop = 0f;
                    break;
                default: // shop row / commercial
                    info.R = lr.Inset(0.1f);
                    info.Floors = r > 0.4f ? 2 : 1;
                    info.WallTop = 1.15f + (info.Floors - 1) * FLOOR;
                    info.Wall = Hf(l, 3) < 0.3f ? BRICK : Pick(CONCRETE, l, 5);
                    info.Roof = FLATROOF;
                    break;
            }
            if (info.A == Arch.Parking || info.A == Arch.Farm) info.Dmg = 0;
            // sim-facing footprint (camp layout / starter palisade) from the unclipped lot rect, visual one clipped to land
            info.R0 = clipped ? Foot(info.A, simLot) : info.R;
            info.SimBld = info.Floors > 0;
            if (clipped && info.A != Arch.Parking && info.A != Arch.Farm)
            {
                var full = info.R;
                info.R = ClipToLand(full, BLD_BANK);
                if (info.R.Hu < 0.2f || info.R.Hv < 0.2f) info.R = ClipToLand(full, 0.03f);   // small shoreline lots: hug the bank
                if (info.R.Hu < 0.2f || info.R.Hv < 0.2f) info.R = ShrinkToLand(l, full, 0.03f);   // corner of the rect in the water: smaller block on the land tiles
                if (info.R.Hu < 0.2f || info.R.Hv < 0.2f) { info.R = new ORect(info.R.C, info.R.U, 0f, 0f); info.Floors = 0; info.WallTop = 0f; info.Dmg = 0; }
                else if (info.A == Arch.Apartment) { info.Floors = Math.Min(info.Floors, Math.Max(2, Mathf.RoundToInt(info.R.Short * 2f / FLOOR * 1.4f))); info.WallTop = info.Floors * FLOOR + 0.12f; }
                else if (info.A == Arch.Office) { info.Floors = Math.Min(info.Floors, Math.Max(3, Mathf.RoundToInt(info.R.Short * 2f / (3.6f * MT) * 2.2f))); info.WallTop = info.Floors * info.FloorH + 0.1f; }
            }
            info.Wall = Geo.Desat(info.Wall, 0.04f);
            info.Front = FrontFace(info.R, new Vector2(L.DoorX + 0.5f, L.DoorY + 0.5f));
            info.Top = info.WallTop;
            if (info.A == Arch.House || info.A == Arch.Hut || info.A == Arch.Barn) info.Top = info.WallTop + Mathf.Min(info.R.Hu, info.R.Hv) * 0.84f;
            else if (info.A == Arch.Church) info.Top = 4.6f;
            else if (info.Floors > 0) info.Top = info.WallTop + 0.17f;
            if (info.Dmg == 3) info.Top = info.WallTop * 0.5f;
            LotH[l] = info.Top;
            return info;
        }

        /// <summary>Facade index (0 -V, 1 +U, 2 +V, 3 -U) whose outward normal points most toward p.</summary>
        static int FrontFace(ORect r, Vector2 p)
        {
            var dd = p - r.C;
            float du = Vector2.Dot(dd, r.U) / Mathf.Max(0.1f, r.Hu), dv = Vector2.Dot(dd, r.V) / Mathf.Max(0.1f, r.Hv);
            if (Mathf.Abs(du) > Mathf.Abs(dv)) return du > 0 ? 1 : 3;
            return dv > 0 ? 2 : 0;
        }

        /// <summary>Facade edge (a -> b) and outward direction.</summary>
        static void FaceEdge(ORect r, int f, out Vector2 a, out Vector2 b, out Vector2 o)
        {
            switch (f & 3)
            {
                case 0: a = r.P(-1, -1); b = r.P(1, -1); o = -r.V; break;
                case 1: a = r.P(1, -1); b = r.P(1, 1); o = r.U; break;
                case 2: a = r.P(1, 1); b = r.P(-1, 1); o = r.V; break;
                default: a = r.P(-1, 1); b = r.P(-1, -1); o = -r.U; break;
            }
        }

        // ================= lot entry =================
        void AddLot(Geo g, int l)
        {
            var info = _info[l];
            if (!info.Valid) return;
            if (!_campLots.Contains(l) && ClearedRect(info.R)) return;   // cleared for a camp yard
            if (info.Bank) BankSkirt(g, info);
            if (info.R.Hu < 0.05f) { if (info.A != Arch.Parking && info.A != Arch.Farm) Yard(g, l, info.Lot.Inset(0.06f), Hex("#6c675a")); return; }   // clipped away by the river: bare lot
            if (RuinLot(g, l, info)) return;                               // collapsed building (MapGen.Ruins.cs)
            if (lo) { LoLot(g, l, info); return; }
            switch (info.A)
            {
                case Arch.Parking: ParkingLot(g, l, info); return;
                case Arch.Farm: FarmPlot(g, l, info); return;
            }
            // yard surface for non-residential lots (residential keeps the lawn)
            if (info.A != Arch.House && info.A != Arch.Hut && info.A != Arch.Barn)
                Yard(g, l, info.Lot.Inset(0.06f), info.A == Arch.Industrial ? Hex("#615c50") : Hex("#6c675a"));
            switch (info.A)
            {
                case Arch.House: case Arch.Hut: case Arch.Barn: House(g, l, info); break;
                case Arch.Church: Church(g, l, info); break;
                case Arch.Gas: GasStation(g, l, info); break;
                case Arch.Industrial: Industrial(g, l, info); break;
                default: Block(g, l, info); break;
            }
        }

        /// <summary>Lots clipped against the river sit on the bank, where the ground slopes down to the water: a dark
        /// foundation skirt under the building and the yard edge so nothing floats above the slope.</summary>
        void BankSkirt(Geo g, LotInfo info)
        {
            var c = Lerp(BANK, Hex("#5a554a"), 0.5f);
            if (info.R.Hu >= 0.05f && info.Floors > 0) g.Box(info.R.Quad(), -0.32f, 0.004f, Mul(c, 0.85f), c, 0f, false);
            if (info.A != Arch.House && info.A != Arch.Hut && info.A != Arch.Barn) g.Box(info.Lot.Inset(0.06f).Quad(), -0.32f, PAVE_Y, c, c, 0f, false);
        }

        /// <summary>Far LOD: massing only (walls with baked shading, flat or gable roof), plus its shadow.</summary>
        void LoLot(Geo g, int l, LotInfo info)
        {
            var R = info.R;
            switch (info.A)
            {
                case Arch.Parking:
                    g.Flat(info.Lot.Inset(0.05f).Quad(), PAVE_Y + 0.002f, Lerp(ASPH, ASPH_DARK, 0.4f)); return;
                case Arch.Farm:
                    g.Flat(info.Lot.Inset(0.1f).Quad(), PAVE_Y, Hex("#5a4a36")); return;
                case Arch.House: case Arch.Hut: case Arch.Barn: case Arch.Church:
                {
                    if (R.Hu < R.Hv) R = new ORect(R.C, R.V, R.Hv, R.Hu);
                    if (info.Dmg == 3) { g.Box(R.Quad(), 0f, info.WallTop * 0.5f, Lerp(info.Wall, CHAR, 0.7f), INTERIOR, 0.2f); return; }
                    g.Box(R.Quad(), 0f, info.WallTop, info.Wall, info.Wall, 0.22f, false);
                    float ridge = info.A == Arch.Church ? info.WallTop + R.Hv * 1.2f : info.Top;
                    g.Gable(R, info.WallTop, ridge, info.Roof, info.Wall, 0.1f, 1);
                    g.Shadow(R.Inset(-0.1f).Quad(), (info.WallTop + ridge) * 0.5f, 0.34f);
                    return;
                }
                default:
                {
                    if (info.A != Arch.Gas) Yard(g, l, info.Lot.Inset(0.06f), info.A == Arch.Industrial ? Hex("#615c50") : Hex("#6c675a"));
                    float H = info.Dmg == 3 ? info.WallTop * 0.5f : info.WallTop;
                    g.Box(R.Quad(), 0f, H, info.Wall, info.Dmg == 3 ? INTERIOR : info.Roof, 0.3f);
                    g.Shadow(R.Quad(), H + 0.17f, 0.36f);
                    return;
                }
            }
        }

        void Yard(Geo g, int l, ORect r, Color c)
        {
            var q = r.Quad();
            Color[] k = new Color[4];
            for (int i = 0; i < 4; i++)
            {
                float n = Noise(q[i].x, q[i].y, 3f, 91 + l % 7);
                k[i] = Lerp(Lerp(c, Mul(c, 0.86f), n), LAWN, Noise(q[i].x, q[i].y, 6f, 93) * 0.45f);
            }
            g.Flat(q[0], q[1], q[2], q[3], PAVE_Y, k[0], k[1], k[2], k[3]);
        }

        // ================= flat-roof blocks: apartments, offices, shops, civic =================
        void Block(Geo g, int l, LotInfo info)
        {
            var R = info.R;
            // randomise which corner a collapse eats by flipping the axes
            if (Hf(l, 15) < 0.5f) R = new ORect(R.C, -R.U, R.Hu, R.Hv);
            float H = info.WallTop;
            Color wall = info.Wall;
            int dmg = info.Dmg;
            if (dmg == 3) { Shell(g, l, R, info); return; }
            if (dmg == 2 && R.Hu > 0.6f && R.Hv > 0.6f)
            {
                float sc = 0.1f + Hf(l, 16) * 0.3f, tc = 0.0f + Hf(l, 17) * 0.35f;
                var A = R.Sub(-1f, sc, -1f, 1f);
                var B = R.Sub(sc, 1f, -1f, tc);
                var Cc = R.Sub(sc, 1f, tc, 1f);
                float hc = H * (0.18f + Hf(l, 18) * 0.25f);
                BlockMass(g, l, A, H, wall, info, new[] { true, false, true, true }, 0);
                BlockMass(g, l, B, H, wall, info, new[] { true, true, false, false }, 1);
                // blown-open interior faces toward the collapsed corner
                InteriorFace(g, A, 1, -1f + 2f * ((tc + 1f) * 0.5f), 1f, hc, H, info);
                InteriorFace(g, B, 2, -1f, 1f, hc, H, info);
                // collapsed corner: stepped stumps + rubble mound spilling outward
                var c1 = Cc.Sub(-1f, 0.1f, -1f, 1f); var c2 = Cc.Sub(0.1f, 1f, -1f, 0.2f);
                g.Box(c1.Quad(), 0f, hc, wall, Mul(INTERIOR, 1.3f), 0.25f);
                g.Box(c2.Quad(), 0f, hc * 0.55f, wall, Mul(INTERIOR, 1.3f), 0.2f);
                RubbleMound(g, l, new ORect(Cc.C + (Cc.U + Cc.V) * 0.25f, Cc.U, Cc.Hu * 1.25f, Cc.Hv * 1.25f), hc * 0.6f, wall);
                Rebar(g, l, A.P(1f, 1f), H * 0.7f);
                g.Shadow(A.Quad(), H, 0.36f);
                g.Shadow(B.Quad(), H, 0.36f);
                return;
            }
            BlockMass(g, l, R, H, wall, info, new[] { true, true, true, true }, -1);
            g.Shadow(R.Quad(), H + 0.17f, 0.36f);
            if (dmg >= 1) RubbleChunks(g, l, R, 4 + (int)(Hf(l, 19) * 6), wall, 0.12f);
        }

        /// <summary>One flat-roofed volume: walls, windows per facade flag, parapet, roof, clutter, ivy.</summary>
        void BlockMass(Geo g, int l, ORect R, float H, Color wall, LotInfo info, bool[] faces, int part)
        {
            var q = R.Quad();
            g.Box(q, 0f, H, wall, info.Roof, 0.3f, false);
            // ground-floor plinth band (darker, grimy)
            for (int f = 0; f < 4; f++)
            {
                FaceEdge(R, f, out var a, out var b, out var o);
                if (info.A == Arch.Shop || (info.A == Arch.Office && f == info.Front))
                {
                    if (faces[f]) Shopfront(g, l * 4 + f, a, b, o, info, f == info.Front);
                }
            }
            // windows
            for (int f = 0; f < 4; f++)
                if (faces[f]) Facade(g, l, f + part * 8, R, f, info, false);
            // parapet ring + roof
            Parapet(g, R, H, wall, l);
            RoofTop(g, l, R, H, info);
            // ivy / moss streaks
            Ivy(g, l * 3 + part, R, H, faces);
        }

        void Parapet(Geo g, ORect R, float H, Color wall, int l)
        {
            const float ph = 0.17f, pt = 0.06f;
            var outer = R.Quad();
            var inner = R.Inset(pt).Quad();
            var ctr = R.C;
            Color cap = Lerp(Mul(wall, 1.12f), PALE, 0.3f);
            for (int i = 0; i < 4; i++)
            {
                Vector2 a = outer[i], b = outer[(i + 1) & 3], ia = inner[i], ib = inner[(i + 1) & 3];
                g.Wall(a, b, Geo.OutDir(a, b, ctr), H, H + ph, wall);
                g.Wall(ia, ib, -Geo.OutDir(ia, ib, ctr), H, H + ph, Mul(wall, 0.9f));
                var ck = Geo.Shade(cap, Vector3.up);
                g.Flat(a, b, ib, ia, H + ph, ck, ck, ck, ck);
            }
        }

        void RoofTop(Geo g, int l, ORect R, float H, LotInfo info)
        {
            var ri = R.Inset(0.06f);
            var q = ri.Quad();
            Color[] k = new Color[4];
            for (int i = 0; i < 4; i++) k[i] = Lerp(info.Roof, TAR, Noise(q[i].x * 2f, q[i].y * 2f, 1.5f, 97 + l % 5));
            g.Flat(q[0], q[1], q[2], q[3], H + 0.002f, k[0], k[1], k[2], k[3]);
            var dir = R.U;
            // stair hut, water tank, AC units, vents
            if (R.Hu > 0.55f && R.Hv > 0.45f && Hf(l, 40) < 0.75f)
            {
                var sp = ri.P(-0.5f + Hf(l, 41) * 0.3f, 0.3f - Hf(l, 42) * 0.6f);
                g.Box(new ORect(sp, dir, 0.24f, 0.18f).Quad(), H, H + 0.48f, Lerp(info.Wall, PALE, 0.2f), Mul(TAR, 1.1f), 0.06f);
                g.Wall(new ORect(sp, dir, 0.24f, 0.18f).P(-0.3f, -1f) , new ORect(sp, dir, 0.24f, 0.18f).P(0.3f, -1f), -R.V, H, H + 0.32f, DOOR);
            }
            if (Hf(l, 43) < 0.35f && R.Hu > 0.5f)
            {
                var tp = ri.P(0.55f, -0.4f + Hf(l, 44) * 0.8f);
                for (int k2 = 0; k2 < 4; k2++) g.BoxAt(tp + new Vector2((k2 & 1) * 0.22f - 0.11f, (k2 >> 1) * 0.22f - 0.11f), dir, 0.015f, 0.015f, H, H + 0.25f, Hex("#3a3631"), Hex("#3a3631"));
                g.Cyl(tp, 0.17f, H + 0.25f, H + 0.62f, Hex("#5a5246"), Hex("#4a443a"), 8);
                g.Cone(tp, 0.18f, H + 0.62f, H + 0.72f, Hex("#4a443a"), 0.2f, 8);
            }
            int n = 1 + (int)(Hf(l, 45) * 3.5f);
            for (int k2 = 0; k2 < n; k2++)
            {
                var p = ri.P(-0.7f + 1.4f * Hf(l, 46 + k2), -0.7f + 1.4f * Hf(l, 52 + k2));
                if (Hf(l, 58 + k2) < 0.6f)
                {
                    g.BoxAt(p, dir, 0.09f, 0.07f, H, H + 0.11f, Hex("#8a8678"), Hex("#9a968a"));          // AC unit
                    g.Disc(p + dir * 0.02f, 0.045f, H + 0.112f, Hex("#2e2c28"), Hex("#4a4842"), 6);
                }
                else g.Cyl(p, 0.03f, H, H + 0.16f, Hex("#5a5852"), Hex("#3a3836"), 5);                  // vent pipe
            }
            // moss / puddle patches on the roof
            if (Hf(l, 60) < 0.55f)
            {
                var p = ri.P(-0.5f + Hf(l, 61), -0.5f + Hf(l, 62));
                float s = 0.15f + Hf(l, 63) * Mathf.Min(0.4f, ri.Short * 0.6f);
                g.Disc(p, s, H + 0.004f, Lerp(MOSS, MOSS_DEEP, 0.3f), Lerp(MOSS, info.Roof, 0.5f), 7, Hf(l, 64) * 6f);
            }
            // roof hole (damage 1+): dark void with debris
            if (info.Dmg >= 1 && Hf(l, 65) < 0.6f && ri.Short > 0.35f)
            {
                var p = ri.P(-0.4f + Hf(l, 66) * 0.8f, -0.4f + Hf(l, 67) * 0.8f);
                float s = 0.12f + Hf(l, 68) * 0.18f;
                var hq = new[] { p + R.U * (-s) + R.V * (-s * 0.7f), p + R.U * (s * 0.8f) + R.V * (-s), p + R.U * s + R.V * (s * 0.6f), p + R.U * (-s * 0.6f) + R.V * s };
                g.Flat(hq, H + 0.006f, VOIDC);
                for (int k2 = 0; k2 < 3; k2++)
                {
                    var bp = p + R.U * (Hf(l, 70 + k2) - 0.5f) * s * 3f + R.V * (Hf(l, 73 + k2) - 0.5f) * s * 3f;
                    g.BoxAt(bp, new Vector2(Mathf.Cos(k2 * 2.1f), Mathf.Sin(k2 * 2.1f)), 0.05f, 0.04f, H, H + 0.04f, RUBBLE_L, RUBBLE_L);
                }
            }
        }

        /// <summary>Window grid on one facade. lit=true emits only warm lit panes (dynamic camp layer).</summary>
        void Facade(Geo g, int l, int salt, ORect R, int f, LotInfo info, bool lit)
        {
            FaceEdge(R, f, out var a, out var b, out var o);
            float len = (b - a).magnitude;
            if (len < 0.3f) return;
            var dir = (b - a) / len;
            float fh = info.FloorH;
            bool office = info.A == Arch.Office;
            float bay = office ? 0.42f : 0.86f;
            int n = Mathf.Max(1, Mathf.FloorToInt((len - 0.12f) / bay));
            float step = len / n;
            float ww = office ? step - 0.06f : Mathf.Min(0.36f, step * 0.55f);
            float wh = office ? fh * 0.52f : 0.42f;
            float sill = office ? fh * 0.26f : 0.26f;
            int f0 = (info.A == Arch.Shop || (office && f == info.Front)) ? 1 : 0;   // shopfronts take the ground floor
            float baseY = info.A == Arch.Shop ? 1.15f - fh : (info.A == Arch.Apartment || info.A == Arch.Civic ? 0.12f : 0.1f);
            for (int fl = f0; fl < info.Floors; fl++)
            {
                float y0 = baseY + fl * fh + sill;
                if (fl == 0 && info.A != Arch.Shop) y0 = 0.1f + sill;
                float y1 = y0 + wh;
                if (y1 > info.WallTop - 0.05f) break;
                for (int k = 0; k < n; k++)
                {
                    // the entrance takes the middle bay of the front ground floor
                    if (fl == 0 && f == info.Front && k == n / 2 && !office)
                    {
                        if (!lit) Door(g, a + dir * (step * (k + 0.5f)), dir, o, info);
                        continue;
                    }
                    float h = Hf(l * 131 + salt * 17 + fl, k * 7 + 3);
                    var c = a + dir * (step * (k + 0.5f));
                    if (lit)
                    {
                        if (h < 0.55f) Pane(g, c, dir, o, ww * 0.5f, y0, y1, LIT_RIM, LIT_CORE, 0.016f, true);
                        continue;
                    }
                    float fr = office ? 0.0f : 0.035f;
                    if (fr > 0f)
                    {
                        Color tc = Lerp(TRIM, info.Wall, 0.45f);
                        Pane(g, c, dir, o, ww * 0.5f + fr, y0 - fr * 1.6f, y1 + fr, tc, tc, 0.006f, false);
                    }
                    if (ShatterPane(g, l * 131 + salt * 17, fl, k, c, dir, o, ww * 0.5f, y0, y1, info, h, 0.86f)) continue;   // MapGen.Ruins.cs
                    if (info.Dmg >= 1 && h > 0.97f) continue;                                   // pane missing entirely, frame only
                    Color top, bot;
                    if (h < 0.6f) { top = GLASS_HI; bot = GLASS; }
                    else if (h < 0.86f) { top = Mul(VOIDC, 1.4f); bot = VOIDC; }
                    else { top = BOARD; bot = Mul(BOARD, 0.8f); }
                    if (office && h < 0.6f) { top = Hex("#525c5c"); bot = Hex("#2e3638"); }
                    Pane(g, c, dir, o, ww * 0.5f, y0, y1, bot, top, 0.011f, false);
                    if (h >= 0.86f && h < 0.97f)
                    {
                        // boarded: two dark plank gaps
                        Pane(g, c, dir, o, ww * 0.5f, y0 + wh * 0.32f, y0 + wh * 0.36f, Mul(BOARD, 0.5f), Mul(BOARD, 0.5f), 0.013f, false);
                        Pane(g, c, dir, o, ww * 0.5f, y0 + wh * 0.66f, y0 + wh * 0.70f, Mul(BOARD, 0.5f), Mul(BOARD, 0.5f), 0.013f, false);
                    }
                    if (info.Dmg >= 1 && h > 0.55f && h < 0.68f)
                    {
                        // scorch streak above a burnt-out window
                        Pane(g, c, dir, o, ww * 0.55f, y1 + 0.02f, Mathf.Min(info.WallTop - 0.02f, y1 + fh * 0.7f), SCORCH, Lerp(SCORCH, info.Wall, 0.85f), 0.009f, false);
                    }
                }
            }
        }

        /// <summary>Rectangle on a facade centred at c (tile space), half width hw, y0..y1, offset out from the wall.</summary>
        static void Pane(Geo g, Vector2 c, Vector2 dir, Vector2 o, float hw, float y0, float y1, Color bottom, Color top, float off, bool unshaded)
        {
            var a = c - dir * hw + o * off; var b = c + dir * hw + o * off;
            if (unshaded) g.QuadRaw(Geo.W(a, y0), Geo.W(b, y0), Geo.W(b, y1), Geo.W(a, y1), bottom, bottom, top, top);
            else g.Wall(a, b, o, y0, y1, bottom, 0f, top);
        }

        void Door(Geo g, Vector2 c, Vector2 dir, Vector2 o, LotInfo info)
        {
            float hw = info.A == Arch.House ? 0.14f : 0.2f;
            Pane(g, c, dir, o, hw + 0.03f, 0.08f, 0.72f, Lerp(TRIM, info.Wall, 0.5f), Lerp(TRIM, info.Wall, 0.5f), 0.006f, false);
            Pane(g, c, dir, o, hw, 0.08f, 0.68f, Mul(DOOR, 0.8f), DOOR, 0.01f, false);
            // step / canopy
            var s0 = c - dir * (hw + 0.06f); var s1 = c + dir * (hw + 0.06f);
            g.Box(new[] { s0, s1, s1 + o * 0.16f, s0 + o * 0.16f }, 0f, 0.07f, Hex("#7a7468"), Hex("#8a8376"));
            if (info.A != Arch.House) g.Box(new[] { s0 + o * 0.01f, s1 + o * 0.01f, s1 + o * 0.22f, s0 + o * 0.22f }, 0.78f, 0.82f, Mul(info.Wall, 0.9f), Lerp(info.Wall, PALE, 0.3f));
        }

        static readonly Color[] AWNING = { Hex("#6e3a2e"), Hex("#3a4a3a"), Hex("#5a4a2e"), Hex("#4a5560") };

        void Shopfront(Geo g, int salt, Vector2 a, Vector2 b, Vector2 o, LotInfo info, bool front)
        {
            float len = (b - a).magnitude; if (len < 0.5f) return;
            var dir = (b - a) / len;
            var c = (a + b) * 0.5f;
            float hw = len * 0.5f - 0.08f;
            float h = Hf(salt, 3);
            // dark shattered glazing with a frame, roll shutter on some
            Pane(g, c, dir, o, hw + 0.03f, 0.06f, 0.84f, Hex("#3a3630"), Hex("#4a453c"), 0.006f, false);
            if (h < 0.3f) Pane(g, c, dir, o, hw, 0.08f, 0.8f, Hex("#5e5c54"), Hex("#6e6a5e"), 0.01f, false);
            else Pane(g, c, dir, o, hw, 0.08f, 0.8f, VOIDC, Hex("#2e3436"), 0.01f, false);
            if (!front) return;
            // sign band
            Pane(g, c, dir, o, hw + 0.03f, 0.88f, 1.06f, Hex("#2e2c28"), Hex("#3a3832"), 0.008f, false);
            Pane(g, c - dir * hw * 0.3f, dir, o, hw * 0.45f, 0.92f, 1.02f, Lerp(Pick(AWNING, salt, 5), PALE, 0.3f), Lerp(Pick(AWNING, salt, 5), PALE, 0.4f), 0.011f, false);
            if (h > 0.45f)
            {
                // sloped striped awning
                Color c1 = Pick(AWNING, salt, 6), c2 = Hex("#a8a090");
                int nst = Mathf.Max(2, (int)(hw * 2f / 0.18f));
                for (int k = 0; k < nst; k++)
                {
                    var p0 = a + dir * (0.08f + (len - 0.16f) * k / nst); var p1 = a + dir * (0.08f + (len - 0.16f) * (k + 1) / nst);
                    Color col = (k & 1) == 0 ? c1 : Lerp(c2, c1, 0.25f);
                    if (Hf(salt, 10 + k) < 0.12f) continue; // torn
                    g.Face(Geo.W(p0 + o * 0.01f, 0.86f), Geo.W(p1 + o * 0.01f, 0.86f), Geo.W(p1 + o * 0.32f, 0.68f), Geo.W(p0 + o * 0.32f, 0.68f), col, new Vector3(o.x, 1f, -o.y));
                }
            }
        }

        void InteriorFace(Geo g, ORect R, int f, float t0, float t1, float y0, float y1, LotInfo info)
        {
            FaceEdge(R, f, out var a, out var b, out var o);
            g.Wall(a + o * 0.01f, b + o * 0.01f, o, y0, y1, INTERIOR, 0f, Mul(INTERIOR, 1.2f));
            // exposed floor slabs
            for (float y = y0 + info.FloorH * 0.6f; y < y1 - 0.05f; y += info.FloorH)
                g.Wall(a + o * 0.03f, b + o * 0.03f, o, y, y + 0.06f, Lerp(info.Wall, PALE, 0.4f));
        }

        void Shell(Geo g, int l, ORect R, LotInfo info)
        {
            // roof gone, walls broken off at uneven heights, rubble and a tree inside
            float H = info.WallTop;
            const float t = 0.07f;
            for (int f = 0; f < 4; f++)
            {
                FaceEdge(R, f, out var a, out var b, out var o);
                for (int s = 0; s < 3; s++)
                {
                    var p0 = Vector2.Lerp(a, b, s / 3f); var p1 = Vector2.Lerp(a, b, (s + 1) / 3f);
                    float hs = H * (0.3f + 0.5f * Hf(l * 7 + f, s));
                    if (Hf(l * 5 + f, s + 9) < 0.15f) hs *= 0.3f;
                    g.Box(new[] { p0, p1, p1 - o * t, p0 - o * t }, 0f, hs, info.Wall, Lerp(info.Wall, INTERIOR, 0.3f), 0.25f);
                    // dark window holes on what stands
                    for (float y = 0.4f; y + 0.4f < hs; y += FLOOR)
                        Pane(g, (p0 + p1) * 0.5f, (p1 - p0).normalized, o, 0.15f, y, y + 0.38f, VOIDC, Mul(VOIDC, 1.4f), 0.01f, false);
                    g.Shadow(new[] { p0, p1, p1 - o * t, p0 - o * t }, hs, 0.34f);
                }
            }
            var inner = R.Inset(t).Quad();
            g.Flat(inner, 0.03f, Lerp(INTERIOR, RUBBLE_D, 0.5f));
            RubbleMound(g, l, R.Inset(0.15f), H * 0.12f, info.Wall);
            if (Hf(l, 80) < 0.7f) Tree(g, R.P(-0.3f + Hf(l, 81) * 0.6f, -0.3f + Hf(l, 82) * 0.6f), l * 7 + 1, 0.85f, true);
            RubbleChunks(g, l, R, 8, info.Wall, 0.15f);
        }

        void Ivy(Geo g, int l, ORect R, float H, bool[] faces)
        {
            if (FxOvergrowth.ReplacesStaticIvy) return;   // v0.16: 3D vines in FxOvergrowth replace these streaks
            if (Hf(l, 90) < 0.3f) return;
            for (int f = 0; f < 4; f++)
            {
                if (!faces[f]) continue;
                FaceEdge(R, f, out var a, out var b, out var o);
                float len = (b - a).magnitude;
                var dir = (b - a) / len;
                int n = (int)(Hf(l * 5 + f, 91) * 2.4f);
                for (int k = 0; k < n; k++)
                {
                    float s = Hf(l * 5 + f, 92 + k) * len;
                    float w0 = 0.12f + Hf(l * 5 + f, 96 + k) * 0.3f;
                    float drop = H * (0.25f + Hf(l * 5 + f, 100 + k) * 0.7f);
                    float top = H + 0.12f, bot = Mathf.Max(0.05f, top - drop);
                    var c = a + dir * s + o * 0.017f;
                    Color ct = Geo.Shade(IVY, new Vector3(o.x, 0f, -o.y)), cb = Geo.Shade(Lerp(MOSS, IVY, 0.3f), new Vector3(o.x, 0f, -o.y));
                    var ta = c - dir * w0; var tb = c + dir * w0;
                    var ba = c - dir * (w0 * 0.25f) + dir * (Hf(l, 104 + k) - 0.5f) * 0.2f; var bb = ba + dir * (w0 * 0.5f);
                    g.QuadRaw(Geo.W(ba, bot), Geo.W(bb, bot), Geo.W(tb, top), Geo.W(ta, top), cb, cb, ct, ct);
                    // a fuller clump of leaves at the top
                    if (Hf(l * 3 + f, 110 + k) < 0.5f)
                        g.Blob(Geo.W(c + o * 0.05f, top - 0.05f), w0 * 0.9f, 0.12f, IVY, l * 9 + k);
                }
                // ground-level moss / weeds along the base
                if (Hf(l * 5 + f, 120) < 0.5f)
                    g.Wall(a + o * 0.014f, b + o * 0.014f, o, 0f, 0.12f + Hf(l, 121) * 0.12f, Lerp(MOSS, MOSS_DEEP, 0.4f), 0f, Lerp(MOSS, MOSS_DEEP, 0.1f));
            }
        }

        void RubbleMound(Geo g, int l, ORect r, float h, Color wall)
        {
            Color c = Lerp(wall, RUBBLE_D, 0.4f);
            g.Frustum(r.Quad(), 0f, r.Inset(r.Short * 0.55f).Quad(), Mathf.Max(0.08f, h), Mul(c, 0.95f), Lerp(c, RUBBLE_L, 0.3f));
            RubbleChunks(g, l, r, 10, wall, h * 0.8f + 0.05f);
        }

        void RubbleChunks(Geo g, int l, ORect r, int n, Color wall, float maxH)
        {
            var big = new ORect(r.C, r.U, r.Hu + 0.3f, r.Hv + 0.3f);
            for (int k = 0; k < n; k++)
            {
                var p = big.P(Hf(l, 200 + k) * 2f - 1f, Hf(l, 220 + k) * 2f - 1f);
                if (r.Contains(p, -0.05f) && maxH < 0.2f) continue;
                float s = 0.05f + Hf(l, 240 + k) * 0.1f;
                float hh = 0.03f + Hf(l, 260 + k) * Mathf.Min(0.12f, maxH);
                Color c = Hf(l, 280 + k) < 0.5f ? Lerp(wall, RUBBLE_L, 0.4f) : RUBBLE_D;
                float a = Hf(l, 300 + k) * 6.28f;
                g.BoxAt(p, new Vector2(Mathf.Cos(a), Mathf.Sin(a)), s, s * 0.7f, 0f, hh, c, Mul(c, 1.05f));
            }
            // a few planks
            for (int k = 0; k < n / 3; k++)
            {
                var p = big.P(Hf(l, 320 + k) * 2f - 1f, Hf(l, 330 + k) * 2f - 1f);
                float a = Hf(l, 340 + k) * 6.28f;
                g.BoxAt(p, new Vector2(Mathf.Cos(a), Mathf.Sin(a)), 0.2f, 0.025f, 0f, 0.025f, Hex("#865e3e"), Hex("#8a6a48"));
            }
        }

        void Rebar(Geo g, int l, Vector2 p, float h)
        {
            for (int k = 0; k < 3; k++)
            {
                var b = Geo.W(p + new Vector2(Hf(l, 400 + k) - 0.5f, Hf(l, 410 + k) - 0.5f) * 0.2f, h - 0.1f);
                var e = b + new Vector3((Hf(l, 420 + k) - 0.5f) * 0.25f, 0.2f + Hf(l, 430 + k) * 0.15f, (Hf(l, 440 + k) - 0.5f) * 0.25f);
                g.Beam(b, e, 0.008f, Hex("#3a2a22"));
            }
        }

        // ================= houses =================
        void House(Geo g, int l, LotInfo info)
        {
            var R = info.R;
            if (R.Hu < R.Hv) R = new ORect(R.C, R.V, R.Hv, R.Hu);   // ridge along the long axis
            float wt = info.WallTop;
            float ridge = info.Top;
            Color wall = info.Wall, roof = info.Roof;
            int dmg = info.A == Arch.Barn ? Math.Min(info.Dmg, 1) : info.Dmg;
            var ri = new LotInfo { A = Arch.House, R = R, Floors = info.Floors, FloorH = FLOOR, WallTop = wt, Wall = wall, Dmg = dmg, Front = FrontFace(R, R.C + (info.R.P(0, 0) - R.C)) };
            ri.Front = FrontFace(R, new Vector2(d.Lots[l].DoorX + 0.5f, d.Lots[l].DoorY + 0.5f));
            // foundation
            g.Box(R.Inset(-0.03f).Quad(), 0f, 0.08f, Hex("#6a6458"), Hex("#6a6458"));
            if (dmg == 3)
            {
                // burnt-out shell: charred walls, no roof
                var shellInfo = info; shellInfo.Wall = Lerp(wall, CHAR, 0.7f); shellInfo.WallTop = wt;
                Shell(g, l, R, shellInfo);
                Fence(g, l, info);
                return;
            }
            g.Box(R.Quad(), 0.08f, wt, wall, wall, 0.22f, false);
            for (int f = 0; f < 4; f++) HouseWindows(g, l, R, f, ri);
            float ov = 0.1f;
            if (dmg <= 1)
            {
                g.Gable(R, wt, ridge, roof, wall, ov);
                if (dmg == 1 && Hf(l, 30) < 0.4f) RoofHole(g, l, R, wt, ridge, ov);
            }
            else
            {
                // one roof side caved in: the other side stands, interior exposed
                g.Gable(R, wt, ridge, roof, wall, ov, 3);
                RoofHole(g, l, R, wt, ridge, ov, true);
                RubbleChunks(g, l, R, 8, wall, 0.1f);
            }
            if (Hf(l, 31) < 0.5f && info.A == Arch.House)
            {
                var cp = R.At(R.Hu * 0.55f * (Hf(l, 32) < 0.5f ? -1f : 1f), R.Hv * 0.3f);
                g.Box(new ORect(cp, R.U, 0.08f, 0.08f).Quad(), wt, ridge + 0.12f, BRICK, Mul(CHAR, 1.3f), 0f);
            }
            // porch roof over the door on some houses
            if (info.A == Arch.House && Hf(l, 33) < 0.35f)
            {
                FaceEdge(R, ri.Front, out var a, out var b, out var o);
                var c = (a + b) * 0.5f; var dir = (b - a).normalized;
                float hw = Mathf.Min(0.5f, (b - a).magnitude * 0.35f);
                var p0 = c - dir * hw; var p1 = c + dir * hw;
                g.Face(Geo.W(p0, 0.84f), Geo.W(p1, 0.84f), Geo.W(p1 + o * 0.4f, 0.72f), Geo.W(p0 + o * 0.4f, 0.72f), roof, new Vector3(o.x, 1f, -o.y));
                g.BoxAt(p0 + o * 0.37f, dir, 0.02f, 0.02f, 0f, 0.72f, TRIM, TRIM);
                g.BoxAt(p1 + o * 0.37f, dir, 0.02f, 0.02f, 0f, 0.72f, TRIM, TRIM);
            }
            Ivy(g, l, R, wt - 0.12f, new[] { Hf(l, 34) < 0.4f, false, Hf(l, 35) < 0.4f, false });
            g.Shadow(R.Inset(-ov).Quad(), (wt + ridge) * 0.5f, 0.34f);
            if (dmg >= 1) RubbleChunks(g, l, R, 3, wall, 0.08f);
            Fence(g, l, info);
        }

        void HouseWindows(Geo g, int l, ORect R, int f, LotInfo info)
        {
            FaceEdge(R, f, out var a, out var b, out var o);
            float len = (b - a).magnitude; var dir = (b - a) / len;
            bool gableEnd = f == 1 || f == 3;
            int n = gableEnd ? 1 : Mathf.Max(1, Mathf.RoundToInt(len / 0.8f));
            float step = len / n;
            for (int fl = 0; fl < info.Floors; fl++)
            {
                float y0 = 0.08f + fl * FLOOR * 0.94f + 0.26f, y1 = y0 + 0.36f;
                if (y1 > info.WallTop - 0.03f) break;
                for (int k = 0; k < n; k++)
                {
                    var c = a + dir * (step * (k + 0.5f));
                    if (fl == 0 && f == info.Front && k == n / 2 && !gableEnd) { Door(g, c, dir, o, info); continue; }
                    float h = Hf(l * 37 + f * 5 + fl, k + 11);
                    Pane(g, c, dir, o, 0.15f, y0 - 0.05f, y1 + 0.035f, Lerp(TRIM, info.Wall, 0.3f), Lerp(TRIM, info.Wall, 0.25f), 0.006f, false);
                    if (ShatterPane(g, l * 37 + f * 5, fl, k, c, dir, o, 0.12f, y0, y1, info, h, 0.8f)) continue;   // MapGen.Ruins.cs
                    Color top, bot;
                    if (h < 0.5f) { top = GLASS_HI; bot = GLASS; } else if (h < 0.8f) { top = Mul(VOIDC, 1.4f); bot = VOIDC; } else { top = BOARD; bot = Mul(BOARD, 0.8f); }
                    Pane(g, c, dir, o, 0.12f, y0, y1, bot, top, 0.011f, false);
                    if (h < 0.5f) Pane(g, c, dir, o, 0.012f, y0, y1, TRIM, TRIM, 0.013f, false);    // mullion
                }
            }
            if (gableEnd && info.Floors >= 1)
            {
                // small attic window in the gable
                var c = (a + b) * 0.5f;
                float y0 = info.WallTop + 0.12f;
                Pane(g, c, dir, o, 0.09f, y0, y0 + 0.18f, Hex("#2e3436"), Hex("#3e4a4a"), 0.01f, false);
            }
        }

        /// <summary>Hole in one roof plane (sloped) with exposed rafters. caved: half the plane is gone.</summary>
        void RoofHole(Geo g, int l, ORect R, float y0, float y1, float ov, bool caved = false)
        {
            float side = Hf(l, 36) < 0.5f ? -1f : 1f;
            float a0 = caved ? -R.Hu * 0.55f : (Hf(l, 37) - 0.5f) * R.Hu - 0.15f, a1 = caved ? R.Hu * 0.35f : a0 + 0.22f + Hf(l, 38) * 0.18f;
            float t0 = caved ? 0.1f : 0.3f, t1 = caved ? 0.85f : 0.65f;
            float hv = R.Hv;
            Vector3 P(float al, float t) { var p = R.At(al, side * hv * (1f - t)); return Geo.W(p, Mathf.Lerp(y0, y1, t) + 0.012f); }
            // ragged outline: 6-gon
            var c = (P(a0, t0) + P(a1, t1)) * 0.5f;
            var pts = new[] { P(a0, t0 + 0.05f), P((a0 + a1) * 0.5f, t0), P(a1, t0 + 0.1f), P(a1 - 0.05f, t1), P((a0 + a1) * 0.5f, t1 - 0.06f), P(a0 + 0.06f, t1) };
            for (int k = 0; k < pts.Length; k++) g.TriRaw(c, pts[k], pts[(k + 1) % pts.Length], Hex("#1e1a16"));
            // a couple of broken rafters across the hole
            for (float al = a0 + 0.1f; al < a1 - 0.05f; al += 0.3f)
                g.Beam(P(al, t0 - 0.05f) + Vector3.up * 0.01f, P(al, Mathf.Min(1f, t1 + 0.05f)) + Vector3.up * 0.01f, 0.015f, Hex("#5a4632"));
        }

        void Fence(Geo g, int l, LotInfo info)
        {
            if (info.A != Arch.House || Hf(l, 140) < 0.3f) return;
            var r = info.Lot.Inset(0.06f);
            int front = FrontFace(r, new Vector2(d.Lots[l].DoorX + 0.5f, d.Lots[l].DoorY + 0.5f));
            Color fc = Lerp(FENCE, Hex("#5a5248"), Hf(l, 141));
            for (int f = 0; f < 4; f++)
            {
                if (f == front) continue;
                FaceEdge(r, f, out var a, out var b, out var o);
                float len = (b - a).magnitude;
                int n = Mathf.Max(1, Mathf.RoundToInt(len / 0.7f));
                var dir = (b - a) / len;
                for (int k = 0; k < n; k++)
                {
                    float h = Hf(l * 11 + f, k + 150);
                    if (h < 0.22f) continue;                                         // missing panel
                    var p0 = a + dir * (len * k / n); var p1 = a + dir * (len * (k + 1) / n);
                    float ht = h < 0.32f ? 0.12f : 0.3f;                             // knocked flat-ish
                    g.Box(new[] { p0, p1, p1 - o * 0.025f, p0 - o * 0.025f }, 0f, ht, Mul(fc, 0.95f + 0.1f * h), Lerp(fc, TRIM, 0.2f));
                    g.BoxAt(p0 - o * 0.012f, dir, 0.022f, 0.022f, 0f, 0.36f, Mul(fc, 0.8f), fc);
                }
            }
        }

        // ================= other archetypes =================
        void Industrial(Geo g, int l, LotInfo info)
        {
            var R = info.R;
            if (R.Hu < R.Hv) R = new ORect(R.C, R.V, R.Hv, R.Hu);
            float H = info.WallTop;
            Color wall = info.Wall;
            if (info.Dmg == 3) { Shell(g, l, R, info); return; }
            // corrugated walls: alternating shade panels
            for (int f = 0; f < 4; f++)
            {
                FaceEdge(R, f, out var a, out var b, out var o);
                float len = (b - a).magnitude;
                int n = Mathf.Clamp(Mathf.RoundToInt(len / 0.45f), 1, 14);
                for (int k = 0; k < n; k++)
                {
                    var p0 = Vector2.Lerp(a, b, (float)k / n); var p1 = Vector2.Lerp(a, b, (float)(k + 1) / n);
                    Color c = Mul(wall, (k & 1) == 0 ? 0.97f : 1.04f);
                    if (Hf(l * 7 + f, k) < 0.18f) c = Lerp(c, RUST, 0.35f);
                    g.Wall(p0, p1, o, 0f, H, c, 0.3f);
                }
                // roll-up doors and high windows
                var dir = (b - a) / len; var mid = (a + b) * 0.5f;
                if (f == 0 || f == 2)
                {
                    int nd = len > 2.5f ? 2 : 1;
                    for (int k = 0; k < nd; k++)
                    {
                        var c = a + dir * (len * (k + 0.5f) / nd);
                        Pane(g, c, dir, o, 0.3f, 0f, 0.95f, Hex("#55554c"), Hex("#6e6c60"), 0.01f, false);
                        for (float y = 0.15f; y < 0.9f; y += 0.16f) Pane(g, c, dir, o, 0.3f, y, y + 0.02f, Hex("#4a4a42"), Hex("#4a4a42"), 0.013f, false);
                    }
                    int nw = Mathf.Max(1, (int)(len / 0.6f));
                    for (int k = 0; k < nw; k++) Pane(g, a + dir * (len * (k + 0.5f) / nw), dir, o, 0.16f, H - 0.42f, H - 0.18f, VOIDC, Hex("#3a4444"), 0.01f, false);
                }
            }
            if (d.Lots[l].Kind == "supermarket")
            {
                FaceEdge(R, info.Front, out var a, out var b, out var o);
                Shopfront(g, l * 4 + 9, a, b, o, info, true);
                Parapet(g, R, H, wall, l);
                RoofTop(g, l, R, H, info);
                g.Shadow(R.Quad(), H + 0.17f, 0.36f);
            }
            else
            {
                // shallow corrugated gable roof with rust streaks
                float ridge = H + R.Hv * 0.22f;
                g.Gable(R, H, ridge, Lerp(info.Roof, RUST, Hf(l, 20) * 0.4f), wall, 0.06f, 6);
                if (info.Dmg >= 1) RoofHole(g, l, R, H, ridge, 0.06f, info.Dmg >= 2);
                g.Shadow(R.Quad(), (H + ridge) * 0.5f, 0.36f);
            }
            Ivy(g, l, R, H - 0.12f, new[] { true, true, true, true });
            // yard clutter: pallets / drums / container
            if (Hf(l, 21) < 0.5f)
            {
                var p = info.Lot.P(0.8f, 0.8f);
                g.Cyl(p, 0.09f, 0f, 0.26f, Hex("#3a4a5a"), Hex("#2e3a46"), 7);
                g.Cyl(p + info.Lot.U * 0.2f, 0.09f, 0f, 0.26f, Lerp(Hex("#6e3226"), RUST, 0.4f), Hex("#5a2a22"), 7);
            }
            if (info.Dmg >= 1) RubbleChunks(g, l, R, 5, wall, 0.1f);
        }

        void Church(Geo g, int l, LotInfo info)
        {
            var R = info.R;
            if (R.Hu < R.Hv) R = new ORect(R.C, R.V, R.Hv, R.Hu);
            var nave = new ORect(R.At(R.Hu * 0.15f, 0f), R.U, R.Hu * 0.85f, R.Hv * 0.75f);
            g.Box(nave.Quad(), 0f, info.WallTop, info.Wall, info.Wall, 0.3f, false);
            g.Gable(nave, info.WallTop, info.WallTop + nave.Hv * 1.2f, info.Roof, info.Wall, 0.08f);
            for (int f = 0; f < 4; f += 2)
            {
                FaceEdge(nave, f, out var a, out var b, out var o);
                var dir = (b - a).normalized; float len = (b - a).magnitude;
                for (int k = 0; k < 3; k++) Pane(g, a + dir * (len * (k + 0.5f) / 3f), dir, o, 0.1f, 0.4f, 1.2f, Hex("#2e3436"), Hex("#4a4a3a"), 0.01f, false);
            }
            var tw = new ORect(R.At(-R.Hu + 0.38f, 0f), R.U, 0.36f, 0.36f);
            g.Box(tw.Quad(), 0f, 3.2f, info.Wall, info.Wall, 0.3f);
            g.Pyramid(tw.Inset(-0.04f).Quad(), 3.2f, tw.C, 4.6f, info.Roof);
            Pane(g, tw.P(0, -1), tw.U, -tw.V, 0.12f, 2.4f, 2.9f, VOIDC, Hex("#2a2620"), 0.01f, false);
            Ivy(g, l, nave, info.WallTop, new[] { true, false, true, false });
            g.Shadow(nave.Quad(), info.WallTop + 0.5f, 0.34f);
            g.Shadow(tw.Quad(), 3.6f, 0.34f);
        }

        void GasStation(Geo g, int l, LotInfo info)
        {
            var R = info.R;
            var shop = R.Sub(-1f, -0.2f, -1f, 0.2f);
            g.Box(shop.Quad(), 0f, 0.95f, Hex("#7a7468"), FLATROOF, 0.25f);
            FaceEdge(shop, 1, out var a, out var b, out var o);
            Shopfront(g, l * 4 + 1, a, b, o, info, false);
            var can = R.Sub(0.0f, 1f, -0.9f, 0.9f);
            foreach (var cp in new[] { can.P(-0.75f, -0.7f), can.P(0.75f, -0.7f), can.P(-0.75f, 0.7f), can.P(0.75f, 0.7f) })
                g.BoxAt(cp, R.U, 0.05f, 0.05f, 0f, 1.25f, Hex("#8a8376"), Hex("#8a8376"));
            g.Box(can.Quad(), 1.25f, 1.45f, Hex("#a8a49a"), Hex("#8a8678"));
            FaceEdge(can, 0, out var ca, out var cb, out var co);
            g.Wall(ca + co * 0.01f, cb + co * 0.01f, co, 1.3f, 1.4f, Hex("#6e3a2e"));
            for (int k = 0; k < 2; k++) g.BoxAt(can.P(k == 0 ? -0.35f : 0.35f, 0f), R.U, 0.08f, 0.05f, 0f, 0.4f, Hex("#7a7468"), Hex("#6e3a2e"));
            g.Shadow(can.Quad(), 1.45f, 0.3f);
            if (info.Dmg >= 2) RubbleChunks(g, l, can, 6, Hex("#a8a49a"), 0.1f);
        }

        static readonly Color[] CAR_PAINT = { Hex("#a8a49a"), Hex("#6e3a2e"), Hex("#4a5560"), Hex("#7a7468"), Hex("#8a7a4a"), Hex("#3a4a3a"), Hex("#5a5a58") };

        void ParkingLot(Geo g, int l, LotInfo info)
        {
            var r = info.Lot.Inset(0.05f);
            var q = r.Quad();
            Color[] k = new Color[4];
            for (int i = 0; i < 4; i++) k[i] = Lerp(Lerp(ASPH, ASPH_DARK, Noise(q[i].x, q[i].y, 4f, 17)), LAWN, Noise(q[i].x, q[i].y, 5f, 19) * 0.25f);
            g.Flat(q[0], q[1], q[2], q[3], PAVE_Y + 0.002f, k[0], k[1], k[2], k[3]);
            // stall lines across the long axis
            var U = r.Hu >= r.Hv ? r.U : r.V; var V = r.Hu >= r.Hv ? r.V : r.U;
            float hu = Mathf.Max(r.Hu, r.Hv), hv = Mathf.Min(r.Hu, r.Hv);
            Color pc = Lerp(PAINT, ASPH, 0.35f);
            for (float s = -hu + 0.35f; s < hu - 0.1f; s += 0.72f)
                for (int side = -1; side <= 1; side += 2)
                {
                    if (hv < 0.9f && side > 0) continue;
                    var p0 = r.C + U * s + V * (side * hv); var p1 = r.C + U * s + V * (side * (hv - 0.75f));
                    var nn = U * 0.018f;
                    g.Flat(p0 - nn, p1 - nn, p1 + nn, p0 + nn, PAVE_Y + 0.006f, pc, pc, pc, pc);
                }
            int n = 1 + (int)(Hf(l, 21) * 4f);
            for (int c = 0; c < n; c++)
            {
                float s = -hu + 0.7f + Hf(l, 30 + c) * Mathf.Max(0.1f, hu * 2f - 1.4f);
                float side = Hf(l, 40 + c) < 0.5f ? -1f : 1f;
                var p = r.C + U * s + V * (side * Mathf.Max(0f, hv - 0.4f));
                float ang = Mathf.Atan2(V.y, V.x) + (Hf(l, 50 + c) - 0.5f) * 0.5f;
                AddCar(g, p, ang, l * 7 + c, PAVE_Y);
            }
            if (Hf(l, 60) < 0.5f) Lamp(g, r.P(-0.9f, -0.9f), r.U, l, PAVE_Y);
        }

        void FarmPlot(Geo g, int l, LotInfo info)
        {
            var r = info.Lot.Inset(0.1f);
            g.Flat(r.Quad(), PAVE_Y, Hex("#5a4a36"));
            var U = r.Hu >= r.Hv ? r.U : r.V; var V = r.Hu >= r.Hv ? r.V : r.U;
            float hu = Mathf.Max(r.Hu, r.Hv), hv = Mathf.Min(r.Hu, r.Hv);
            for (float t = -hv + 0.15f; t < hv; t += 0.3f)
            {
                var p0 = r.C + U * (-hu + 0.1f) + V * t; var p1 = r.C + U * (hu - 0.1f) + V * t;
                var nn = V * 0.07f;
                Color c = Lerp(Hex("#5a6a2e"), Hex("#7a7a3e"), Hf(l, (int)(t * 10f) + 50));
                g.Box(new[] { p0 - nn, p1 - nn, p1 + nn, p0 + nn }, 0f, 0.06f, c, c);
            }
        }
    }
}
