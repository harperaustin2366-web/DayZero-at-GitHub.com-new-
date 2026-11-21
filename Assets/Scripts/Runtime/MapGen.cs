// Procedural city mesh generator (v0.11.3a). Pure C# on top of MapGeo, so it runs in Unity
// (MapView turns MeshData into Meshes) and in the offline preview harness
// (work/unity-verify/Preview renders the same meshes to PNG for review without an editor).
//
// Look target: dayzero-style-concept.png + art/ART_BIBLE.md. Scale: 1 tile ~ 3.5 m.
//  * ground: one shared-vertex grid per 64x64-tile chunk; colours blended from the tile types
//    around each vertex + large-scale noise only (no per-tile random colour); banks slope into water
//  * the city is rotated (Data.RgAngle) but rasterised on an axis-aligned tile grid, which made
//    roads read as staircase checkerboards. Roads, sidewalks and plazas are now re-sampled on a
//    0.5-tile grid aligned to the city angle with direction-aware smoothing, so road edges are
//    straight: continuous asphalt, worn centre, lane paint, crossings, curbs and raised sidewalks
//  * buildings per archetype with real proportions (3 m floors), windows, parapets, roofs,
//    damage states, ivy, grime (MapGen.Buildings.cs); trees, wrecks, street furniture and the
//    survivor camp (MapGen.Props.cs)
using System;
using System.Collections.Generic;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed partial class MapGen
    {
        public const int CH = 64;                 // chunk size in tiles
        public const float MT = 1f / 3.5f;        // metres -> tiles
        public const float FLOOR = 3.0f * MT;     // residential floor height
        public const float CELL = 0.5f;           // road grid cell (tiles)
        public const float ROAD_Y = 0.012f, SW_Y = 0.045f, PAVE_Y = 0.008f, WATER_Y = -0.07f;

        readonly Data d;
        public readonly float[] LotH;             // roof height per lot (labels, camp markers)
        readonly LotInfo[] _info;

        // rotated city grid
        readonly float cs, sn;                    // city angle
        readonly Vector2 GU, GV;                  // grid axes in tile space
        float u0, v0; int nu, nv;
        byte[] cls;                               // 0 none, 1 road, 2 sidewalk, 3 bridge deck, 4 plaza
        short[] runU, runV, posU, posV;

        const byte C_NONE = 0, C_ROAD = 1, C_SIDE = 2, C_BRIDGE = 3, C_PLAZA = 4;

        public MapGen(Data data)
        {
            d = data;
            double th = d.RgAngle * Math.PI / 180.0;
            cs = (float)Math.Cos(th); sn = (float)Math.Sin(th);
            GU = new Vector2(cs, sn); GV = new Vector2(-sn, cs);
            LotH = new float[d.Lots.Count];
            _info = new LotInfo[d.Lots.Count];
            BuildGrid();
            PlanBridges();   // MapGen.Bridges.cs
            for (int l = 0; l < d.Lots.Count; l++) _info[l] = PlanLot(l);
        }

        // ---------------- hashing / noise ----------------
        /// <summary>Godot view3d._hf (64-bit int maths like GDScript).</summary>
        public static float Hf(long a, long b)
        {
            unchecked
            {
                long h = (a * 73856093L) ^ (b * 19349663L);
                h = (h ^ (h >> 13)) * 1274126177L;
                return (h & 65535) / 65535f;
            }
        }

        public static float Noise(float x, float y, float cell, int salt)
        {
            float fx = x / cell, fy = y / cell;
            int ix = Mathf.FloorToInt(fx), iy = Mathf.FloorToInt(fy);
            float tx = fx - ix, ty = fy - iy;
            tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty);
            float a = Hf(ix * 131 + salt, iy), b = Hf((ix + 1) * 131 + salt, iy);
            float c = Hf(ix * 131 + salt, iy + 1), e = Hf((ix + 1) * 131 + salt, iy + 1);
            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, e, tx), ty);
        }

        static Color Hex(string s) => Geo.Hex(s);
        static Color Mul(Color c, float k) => Geo.Mul(c, k);
        static Color Lerp(Color a, Color b, float t) => Geo.Lerp(a, b, t);

        int TileAt(int x, int y) => x < 0 || y < 0 || x >= d.W || y >= d.H ? -1 : d.Tiles[y * d.W + x];
        int TileAtF(float x, float y) => TileAt(Mathf.FloorToInt(x), Mathf.FloorToInt(y));
        static bool IsRoadT(int t) => t == Data.T_ROAD || t == Data.T_AVE || t == Data.T_BRIDGE;
        bool InCity(float x, float y) { var r = d.CityRect; return x >= r.x && y >= r.y && x < r.x + r.width && y < r.y + r.height; }

        Vector2 G2T(float u, float v) => GU * u + GV * v;                 // grid (u,v) -> tile space
        float TU(Vector2 p) => p.x * cs + p.y * sn;
        float TV(Vector2 p) => -p.x * sn + p.y * cs;
        Vector2 CellC(int i, int j) => G2T(u0 + (i + 0.5f) * CELL, v0 + (j + 0.5f) * CELL);
        Vector2 CellP(float i, float j) => G2T(u0 + i * CELL, v0 + j * CELL);
        int Cls(int i, int j) => i < 0 || j < 0 || i >= nu || j >= nv ? C_NONE : cls[j * nu + i];
        static bool Roadish(int c) => c == C_ROAD || c == C_BRIDGE;

        /// <summary>Road class of a tile-space point: 0 / 1 road / 3 bridge, via the rotated grid in the city.</summary>
        public int ClsAt(Vector2 p)
        {
            int i = Mathf.FloorToInt((TU(p) - u0) / CELL), j = Mathf.FloorToInt((TV(p) - v0) / CELL);
            return Cls(i, j);
        }

        // ---------------- rotated road grid ----------------
        void BuildGrid()
        {
            var r = d.CityRect;
            float umin = 1e9f, umax = -1e9f, vmin = 1e9f, vmax = -1e9f;
            foreach (var p in new[] { new Vector2(r.x, r.y), new Vector2(r.x + r.width, r.y), new Vector2(r.x, r.y + r.height), new Vector2(r.x + r.width, r.y + r.height) })
            {
                umin = Mathf.Min(umin, TU(p)); umax = Mathf.Max(umax, TU(p)); vmin = Mathf.Min(vmin, TV(p)); vmax = Mathf.Max(vmax, TV(p));
            }
            u0 = umin - 1f; v0 = vmin - 1f;
            nu = Mathf.CeilToInt((umax - u0 + 1f) / CELL); nv = Mathf.CeilToInt((vmax - v0 + 1f) / CELL);
            cls = new byte[nu * nv];
            for (int j = 0; j < nv; j++)
                for (int i = 0; i < nu; i++)
                {
                    var c = CellC(i, j);
                    if (!InCity(c.x, c.y)) continue;
                    if (RoadAt(c))
                    {
                        int t = TileAtF(c.x, c.y);
                        cls[j * nu + i] = (t == Data.T_BRIDGE || t == Data.T_WATER) ? C_BRIDGE : C_ROAD;
                    }
                }
            SnapRoads();
            TrimWetRoads();
            // runs of road cells along u (rows) and v (columns)
            runU = new short[nu * nv]; posU = new short[nu * nv]; runV = new short[nu * nv]; posV = new short[nu * nv];
            for (int j = 0; j < nv; j++)
            {
                int i = 0;
                while (i < nu)
                {
                    if (!Roadish(cls[j * nu + i])) { i++; continue; }
                    int s = i; while (i < nu && Roadish(cls[j * nu + i])) i++;
                    for (int k = s; k < i; k++) { runU[j * nu + k] = (short)Math.Min(i - s, 32000); posU[j * nu + k] = (short)(k - s); }
                }
            }
            for (int i = 0; i < nu; i++)
            {
                int j = 0;
                while (j < nv)
                {
                    if (!Roadish(cls[j * nu + i])) { j++; continue; }
                    int s = j; while (j < nv && Roadish(cls[j * nu + i])) j++;
                    for (int k = s; k < j; k++) { runV[k * nu + i] = (short)Math.Min(j - s, 32000); posV[k * nu + i] = (short)(k - s); }
                }
            }
            // sidewalks: a 1-tile band along every road edge (not over water)
            for (int j = 0; j < nv; j++)
                for (int i = 0; i < nu; i++)
                {
                    if (cls[j * nu + i] != C_ROAD) continue;
                    for (int dj = -2; dj <= 2; dj++)
                        for (int di = -2; di <= 2; di++)
                        {
                            int ii = i + di, jj = j + dj;
                            if (ii < 0 || jj < 0 || ii >= nu || jj >= nv || cls[jj * nu + ii] != C_NONE) continue;
                            var c = CellC(ii, jj);
                            if (!InCity(c.x, c.y)) continue;
                            int t = TileAtF(c.x, c.y);
                            if (t == Data.T_WATER || t == Data.T_PIER || !CellDry(c)) continue;   // v0.19 audit: no sidewalk slab over the water
                            cls[jj * nu + ii] = C_SIDE;
                        }
                }
            // plazas: only real squares (5+ plaza tiles around), the 1-tile plaza fringe reads as sidewalk/lawn
            for (int j = 0; j < nv; j++)
                for (int i = 0; i < nu; i++)
                {
                    if (cls[j * nu + i] != C_NONE) continue;
                    var c = CellC(i, j);
                    if (!InCity(c.x, c.y)) continue;
                    int tx = Mathf.FloorToInt(c.x), ty = Mathf.FloorToInt(c.y);
                    if (TileAt(tx, ty) != Data.T_PLAZA) continue;
                    int n = 0;
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) if (TileAt(tx + dx, ty + dy) == Data.T_PLAZA) n++;
                    if (n >= 6) cls[j * nu + i] = C_PLAZA;
                }
        }

        /// <summary>v0.19 map audit: road cells that hang over the river bank (centre on land, a corner over water) are
        /// dropped, except next to a bridge deck, so asphalt ends at the waterline instead of floating over it.</summary>
        void TrimWetRoads()
        {
            var drop = new List<int>();
            for (int j = 0; j < nv; j++)
                for (int i = 0; i < nu; i++)
                {
                    if (cls[j * nu + i] != C_ROAD || CellDry(CellC(i, j))) continue;
                    bool nearBridge = false;
                    for (int dj = -3; dj <= 3 && !nearBridge; dj++)
                        for (int di = -3; di <= 3; di++)
                            if (Cls(i + di, j + dj) == C_BRIDGE) { nearBridge = true; break; }
                    if (!nearBridge) drop.Add(j * nu + i);
                }
            foreach (int k in drop) cls[k] = C_NONE;
        }

        /// <summary>v0.19 map audit: all four corners of the 0.5-tile cell centred at c are on dry tiles.</summary>
        bool CellDry(Vector2 c)
        {
            for (int k = 0; k < 4; k++)
            {
                var q = c + GU * ((k & 1) == 0 ? -0.24f : 0.24f) + GV * ((k >> 1) == 0 ? -0.24f : 0.24f);
                if (Wet(TileAtF(q.x, q.y))) return false;
            }
            return true;
        }

        /// <summary>Straighten road edges: every cross-section of a street (a short run of road cells
        /// across it) takes the median start/end of its neighbours along the street, so the 30-degree
        /// staircase of the tile raster no longer jitters the curbs.</summary>
        void SnapRoads()
        {
            const int MAXW = 14, WIN = 7;
            var src = (byte[])cls.Clone();
            bool R(int i, int j) => i >= 0 && j >= 0 && i < nu && j < nv && Roadish(src[j * nu + i]);
            var ss = new List<int>(16); var es = new List<int>(16);
            for (int pass = 0; pass < 2; pass++)
            {
                // pass 0: u-streets (cross-sections are column runs); pass 1: v-streets (row runs)
                int na = pass == 0 ? nu : nv, nb = pass == 0 ? nv : nu;
                bool Rd(int a, int b) => pass == 0 ? R(a, b) : R(b, a);
                for (int a = 0; a < na; a++)
                {
                    int b = 0;
                    while (b < nb)
                    {
                        if (!Rd(a, b)) { b++; continue; }
                        int s0 = b; while (b < nb && Rd(a, b)) b++;
                        int e0 = b;                                   // [s0, e0)
                        if (e0 - s0 > MAXW) continue;                 // intersection / plaza: leave
                        // the street must continue along a (otherwise it's a cross street's edge)
                        if (!(Rd(a - 1, (s0 + e0) / 2) || Rd(a + 1, (s0 + e0) / 2))) continue;
                        ss.Clear(); es.Clear();
                        for (int k = a - WIN; k <= a + WIN; k++)
                        {
                            if (k < 0 || k >= na) continue;
                            int m = (s0 + e0) / 2;
                            if (!Rd(k, m)) continue;
                            int s1 = m; while (s1 > 0 && Rd(k, s1 - 1)) s1--;
                            int e1 = m; while (e1 < nb && Rd(k, e1)) e1++;
                            if (e1 - s1 > MAXW) continue;
                            ss.Add(s1); es.Add(e1);
                        }
                        if (ss.Count < 3) continue;
                        ss.Sort(); es.Sort();
                        int sm = ss[ss.Count / 2], em = es[es.Count / 2];
                        if (em - sm < 2) continue;
                        byte rc = C_ROAD;
                        for (int k = s0; k < e0; k++) { int id = pass == 0 ? k * nu + a : a * nu + k; if (src[id] == C_BRIDGE) rc = C_BRIDGE; }
                        for (int k = Math.Min(s0, sm); k < Math.Max(e0, em); k++)
                        {
                            int id = pass == 0 ? k * nu + a : a * nu + k;
                            bool inside = k >= sm && k < em;
                            if (inside) cls[id] = cls[id] == C_NONE || cls[id] == C_ROAD || cls[id] == C_BRIDGE ? rc : cls[id];
                            else if (k >= s0 && k < e0) cls[id] = C_NONE;
                        }
                    }
                }
                src = (byte[])cls.Clone();
            }
        }

        /// <summary>Direction-aware smoothing of the staircase raster: a cell is road when the road
        /// fraction along either city axis (a 3.2-tile line filter) reaches one half.</summary>
        bool RoadAt(Vector2 c)
        {
            int n = 0;
            for (int a = -1; a <= 1; a++)
                for (int b = -1; b <= 1; b++)
                    if (IsRoadT(TileAtF(c.x + GU.x * a * 0.9f + GV.x * b * 0.9f, c.y + GU.y * a * 0.9f + GV.y * b * 0.9f))) n++;
            if (n == 9) return true;
            if (n == 0) return false;
            float best = 0f;
            for (int axis = 0; axis < 2; axis++)
            {
                Vector2 A = axis == 0 ? GU : GV, B = axis == 0 ? GV : GU;
                int hit = 0, tot = 0;
                for (int o = -1; o <= 1; o++)
                    for (int k = -4; k <= 4; k++)
                    {
                        var p = c + A * (k * 0.4f) + B * (o * 0.12f);
                        if (IsRoadT(TileAtF(p.x, p.y))) hit++;
                        tot++;
                    }
                best = Mathf.Max(best, (float)hit / tot);
            }
            return best >= 0.5f;
        }

        /// <summary>Road direction at a cell: 1 along u, 2 along v, 3 intersection, 0 not road.</summary>
        int Dir(int i, int j)
        {
            if (!Roadish(Cls(i, j))) return 0;
            int k = j * nu + i;
            int ru = runU[k], rv = runV[k];
            bool lu = ru >= 16, lv = rv >= 16;
            if (lu && lv) return 3;
            if (ru >= rv) return 1;
            return 2;
        }

        // ================= chunk =================
        public sealed class Chunk { public MeshData Mesh, Shadow; public Vector3 Center; }
        /// <summary>Vertex counts per stage (diagnostics for the preview harness).</summary>
        public readonly Dictionary<string, long> Stats = new Dictionary<string, long>();

        /// <summary>Debug: road grid class * 10 + direction at a tile-space point.</summary>
        public int DebugCell(Vector2 p)
        {
            int i = Mathf.FloorToInt((TU(p) - u0) / CELL), j = Mathf.FloorToInt((TV(p) - v0) / CELL);
            return Cls(i, j) * 10 + Dir(i, j);
        }

        bool lo;   // building a low-detail (LOD1) chunk: no windows / props / markings, coarse ground

        /// <summary>Builds one 64x64-tile chunk. lod=1 is the cheap far version MapView shows for chunks
        /// away from the camera; lod=0 is the full-detail version streamed in around the camera.</summary>
        public Chunk BuildChunk(int cx, int cy, List<int> lots, int lod = 0)
        {
            lo = lod > 0;
            var g = new Geo(new MeshData(), new MeshData());
            int x0 = cx * CH, y0 = cy * CH, x1 = Math.Min(d.W, x0 + CH), y1 = Math.Min(d.H, y0 + CH);
            int v0c = 0;
            void Stat(string k) { Stats.TryGetValue(k, out long n); Stats[k] = n + g.M.V.Count - v0c; v0c = g.M.V.Count; }
            AddGround(g, x0, y0, x1, y1); Stat("ground");
            AddWater(g, x0, y0, x1, y1); Stat("water");
            AddCityOverlay(g, x0, y0, x1, y1); Stat("roads+props");
            AddWildRoads(g, x0, y0, x1, y1); Stat("wildroads");
            AddBridges(g, cx, cy); Stat("bridges");   // MapGen.Bridges.cs: raised decks, piers, railings
            var lotVerts = new List<LotSpan>(lots.Count);   // (lot, first vertex, end vertex): one tint per building
            foreach (int l in lots) { int lv = g.M.V.Count; AddLot(g, l); lotVerts.Add(new LotSpan(l, lv, g.M.V.Count)); }
            Stat("lots");
            DistrictPass(g, x0, y0, x1, y1, lots, lotVerts); Stat("districts");   // MapDistricts.cs: district palettes + props
            StreetPass(g, x0, y0, x1, y1, lots); Stat("street+graffiti");   // MapGen.Street.cs: street clutter + graffiti
            AddVegetation(g, x0, y0, x1, y1); Stat("vegetation");
            return new Chunk { Mesh = g.M, Shadow = g.S, Center = new Vector3(x0 + CH * 0.5f, 0f, -(y0 + CH * 0.5f)) };
        }

        // ---------------- ground ----------------
        static readonly Color LAWN = Hex("#6e7046"), LAWN2 = Hex("#646048"), LAWN_SHADE = Hex("#525a3c"), DRYGRASS = Hex("#827850");
        static readonly Color DIRT = Hex("#756552"), DIRT_DARK = Hex("#5a4c3c"), MEADOW = Hex("#66704a"), FOREST_FLOOR = Hex("#4c5040");
        static readonly Color FIELD = Hex("#857650"), ROCK = Hex("#6a665c"), BANK = Hex("#4a4632"), BANK_DEEP = Hex("#2e3428");
        static readonly Color VOID = Hex("#303129"), PIER = Hex("#4f3f30");

        Color TileBase(int t, float x, float y)
        {
            switch (t)
            {
                case Data.T_FOREST: return Lerp(FOREST_FLOOR, LAWN_SHADE, Noise(x, y, 7f, 5) * 0.6f);
                case Data.T_MEADOW: return Lerp(MEADOW, DRYGRASS, Noise(x, y, 11f, 6) * 0.5f);
                case Data.T_FIELD: return Lerp(FIELD, DIRT, ((int)(x * 0.5f) % 3 == 0) ? 0.45f : 0.1f);
                case Data.T_DIRT: return Lerp(DIRT, LAWN, Noise(x, y, 5f, 7) * 0.4f);
                case Data.T_ROCK: return ROCK;
                case Data.T_PIER: return PIER;
                case Data.T_VOID: return VOID;
                case -1: return VOID;
                default: return LAWN; // city: lawn under everything (roads, lots, plazas are overlays)
            }
        }

        /// <summary>City lawn: soft large-scale variation (green / olive-grey / dry / dirt patches), no per-tile noise.</summary>
        Color Lawn(Color baseC, float x, float y)
        {
            float n1 = Noise(x, y, 23f, 11), n2 = Noise(x, y, 9f, 29), n3 = Noise(x, y, 4f, 31);
            Color c = Lerp(baseC, LAWN2, Mathf.Clamp01(n1 * 1.4f - 0.35f) * 0.5f);
            c = Lerp(c, DRYGRASS, Mathf.Clamp01(n2 * 2.2f - 1.35f) * 0.7f);
            c = Lerp(c, DIRT, Mathf.Clamp01(n3 * 2.5f - 1.9f) * 0.45f);
            c = Lerp(c, LAWN_SHADE, Mathf.Clamp01((1f - n2) * 2f - 1.3f) * 0.35f);
            return c;
        }

        void AddGround(Geo g, int x0, int y0, int x1, int y1)
        {
            var m = g.M;
            int st = lo ? 2 : 1;
            int gw = (x1 - x0 + st - 1) / st + 1, gh = (y1 - y0 + st - 1) / st + 1;
            int i0 = m.V.Count;
            for (int yi = 0; yi < gh; yi++)
                for (int xi = 0; xi < gw; xi++)
                {
                    int x = Math.Min(x1, x0 + xi * st), y = Math.Min(y1, y0 + yi * st);
                    // blend the 4 tiles that share this vertex
                    float wr = 0f, wg = 0f, wb = 0f; int water = 0, n = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        int tx = x - 1 + (k & 1), ty = y - 1 + (k >> 1);
                        int t = TileAt(tx, ty);
                        if (t == -1) { t = TileAt(Mathf.Clamp(tx, 0, d.W - 1), Mathf.Clamp(ty, 0, d.H - 1)); }
                        if (WetT(t)) { water++; continue; }   // bridges: river under the deck (MapGen.Bridges.cs)
                        var c = TileBase(t, x, y);
                        wr += c.r; wg += c.g; wb += c.b; n++;
                    }
                    Color col = n > 0 ? new Color(wr / n, wg / n, wb / n, 1f) : BANK_DEEP;
                    col = Lawn(col, x, y);
                    float f = water / 4f;
                    // second ring: water two tiles away softens the bank
                    if (water == 0)
                    {
                        int near = 0;
                        for (int k = 0; k < 4; k++) { int tx = x - 2 + (k & 1) * 3, ty = y - 2 + (k >> 1) * 3; if (WetT(TileAt(tx, ty))) near++; }
                        if (near > 0) col = Mul(Lerp(col, BANK, 0.3f * near), 1f - 0.03f * near);   // damp, darker bank ring
                    }
                    else col = Mul(Lerp(Lerp(col, BANK, 0.85f), BANK_DEEP, f), 0.9f);              // wet mud at the waterline
                    float h = water == 0 ? 0f : -0.03f - 0.27f * f;
                    m.V.Add(new Vector3(x, h, -y));
                    m.C.Add(new Color32((byte)(Mathf.Clamp01(col.r) * 255f), (byte)(Mathf.Clamp01(col.g) * 255f), (byte)(Mathf.Clamp01(col.b) * 255f), 255));
                }
            for (int y = 0; y < gh - 1; y++)
                for (int x = 0; x < gw - 1; x++)
                {
                    int a = i0 + y * gw + x, b = a + 1, c = a + gw + 1, e = a + gw;
                    m.T.Add(a); m.T.Add(b); m.T.Add(c); m.T.Add(a); m.T.Add(c); m.T.Add(e);
                }
            // v0.19 audit: skirt along the chunk border where the bank dips. A near (LOD0) chunk next to a far (LOD1) one
            // has twice the border vertices, so their edges disagree on the slopes and left hairline cracks to the sky.
            for (int side = 0; side < 4; side++)
            {
                int n = side < 2 ? gw : gh;
                for (int k = 0; k + 1 < n; k++)
                {
                    int ia, ib;
                    if (side == 0) { ia = i0 + k; ib = ia + 1; }                          // top row
                    else if (side == 1) { ia = i0 + (gh - 1) * gw + k; ib = ia + 1; }     // bottom row
                    else if (side == 2) { ia = i0 + k * gw; ib = ia + gw; }               // left column
                    else { ia = i0 + k * gw + gw - 1; ib = ia + gw; }                     // right column
                    var A = m.V[ia]; var B = m.V[ib];
                    if (A.y > -0.005f && B.y > -0.005f) continue;
                    int q = m.V.Count; var ca = m.C[ia]; var cb = m.C[ib];
                    m.V.Add(A); m.V.Add(B); m.V.Add(new Vector3(B.x, -0.42f, B.z)); m.V.Add(new Vector3(A.x, -0.42f, A.z));
                    m.C.Add(ca); m.C.Add(cb); m.C.Add(cb); m.C.Add(ca);
                    m.T.Add(q); m.T.Add(q + 1); m.T.Add(q + 2); m.T.Add(q); m.T.Add(q + 2); m.T.Add(q + 3);
                    m.T.Add(q); m.T.Add(q + 2); m.T.Add(q + 1); m.T.Add(q); m.T.Add(q + 3); m.T.Add(q + 2);   // both faces
                }
            }
        }

        static readonly Color WATER = Hex("#454e45"), WATER_DEEP = Hex("#364338");

        // Water surface quads carry a marker in vertex alpha: every other map vertex is 255, water is
        // 0..200 = shore..deep. DayZero/Map animates those pixels (ripples, sky sheen, sparkle, shore
        // foam); the fallback shader and the offline preview ignore alpha and show the static colours.
        void AddWater(Geo g, int x0, int y0, int x1, int y1)
        {
            var m = g.M;
            for (int y = y0; y < y1; y++)
            {
                int x = x0;
                while (x < x1)
                {
                    if (!NearWater(x, y)) { x++; continue; }
                    int xe = x + 1;
                    // v0.19 audit: merge only across tiles whose corner colour / depth (alpha) is the same, otherwise the
                    // shore foam and depth smear along a 16-tile quad and read as streaks / dots on the river
                    var ta = WaterV(x, y); var tb = WaterV(x, y + 1);
                    while (xe < x1 && xe - x < 16 && NearWater(xe, y) && Same(WaterV(xe, y), ta) && Same(WaterV(xe, y + 1), tb)) xe++;
                    int i = m.V.Count;
                    m.V.Add(new Vector3(x, WATER_Y, -y)); m.V.Add(new Vector3(xe, WATER_Y, -y));
                    m.V.Add(new Vector3(xe, WATER_Y, -(y + 1))); m.V.Add(new Vector3(x, WATER_Y, -(y + 1)));
                    m.C.Add(WaterV(x, y)); m.C.Add(WaterV(xe, y)); m.C.Add(WaterV(xe, y + 1)); m.C.Add(WaterV(x, y + 1));
                    m.T.Add(i); m.T.Add(i + 1); m.T.Add(i + 2); m.T.Add(i); m.T.Add(i + 2); m.T.Add(i + 3);
                    x = xe;
                }
            }
        }

        static readonly Color FOAM = Hex("#8c9688");
        static bool Same(Color32 a, Color32 b) => Math.Abs(a.r - b.r) + Math.Abs(a.g - b.g) + Math.Abs(a.b - b.b) <= 3 && a.a == b.a;

        Color32 WaterV(int x, int y)
        {
            int n = 0;
            for (int dy = -3; dy <= 3; dy += 3) for (int dx = -3; dx <= 3; dx += 3) if (WetT(TileAt(x + dx, y + dy))) n++;
            float depth = Mathf.Clamp01((n - 2) / 6f);
            Color c = WaterCol(x, y);
            c = Lerp(c, FOAM, (1f - Mathf.Clamp01(depth / 0.2f)) * 0.3f);   // static pale shore strip (fallback / preview)
            return new Color32((byte)(Mathf.Clamp01(c.r) * 255f + 0.5f), (byte)(Mathf.Clamp01(c.g) * 255f + 0.5f), (byte)(Mathf.Clamp01(c.b) * 255f + 0.5f), (byte)(depth * 200f + 0.5f));
        }

        bool NearWater(int x, int y)
        {
            for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) if (WetT(TileAt(x + dx, y + dy))) return true;
            return false;
        }

        Color WaterCol(int x, int y)
        {
            int n = 0;
            for (int dy = -3; dy <= 3; dy += 3) for (int dx = -3; dx <= 3; dx += 3) if (WetT(TileAt(x + dx, y + dy))) n++;
            Color c = Lerp(Hex("#5a6a5e"), WATER, Mathf.Clamp01(n / 5f));
            c = Lerp(c, WATER_DEEP, Mathf.Clamp01((n - 6) / 3f) * 0.8f);
            float k = Noise(x, y, 13f, 77);
            return Lerp(c, Mul(c, 1.1f), k);
        }

        // ---------------- roads, sidewalks, plazas (rotated grid) ----------------
        static readonly Color ASPH = Hex("#524f40"), ASPH_DARK = Hex("#454337"), ASPH_LIT = Hex("#615c4f"), PATCH = Hex("#3a3832");
        static readonly Color PAINT = Hex("#90867a"), PAINT_Y = Hex("#8a7a3a"), CRACK = Hex("#26221c");
        static readonly Color CURB = Hex("#978c7c"), WALK = Hex("#8a8172"), WALK2 = Hex("#7a7365"), PAVE = Hex("#7d776a");

        void CellRange(int x0, int y0, int x1, int y1, out int i0, out int i1, out int j0, out int j1)
        {
            float umin = 1e9f, umax = -1e9f, vmin = 1e9f, vmax = -1e9f;
            foreach (var p in new[] { new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x0, y1), new Vector2(x1, y1) })
            {
                umin = Mathf.Min(umin, TU(p)); umax = Mathf.Max(umax, TU(p)); vmin = Mathf.Min(vmin, TV(p)); vmax = Mathf.Max(vmax, TV(p));
            }
            i0 = Math.Max(0, Mathf.FloorToInt((umin - u0) / CELL) - 1); i1 = Math.Min(nu, Mathf.CeilToInt((umax - u0) / CELL) + 1);
            j0 = Math.Max(0, Mathf.FloorToInt((vmin - v0) / CELL) - 1); j1 = Math.Min(nv, Mathf.CeilToInt((vmax - v0) / CELL) + 1);
        }

        static bool InRect(Vector2 p, int x0, int y0, int x1, int y1) => p.x >= x0 && p.x < x1 && p.y >= y0 && p.y < y1;

        /// <summary>Asphalt colour at a point. across: -1..1 position across the road (0 = centre), w = width in tiles.</summary>
        Color AsphaltCol(Vector2 p, float across, float w)
        {
            float n1 = Noise(p.x, p.y, 7f, 51), n2 = Noise(p.x, p.y, 2.3f, 53);
            Color c = Lerp(ASPH, ASPH_DARK, n1 * 0.7f);
            float a = Mathf.Abs(across);
            // worn wheel tracks lighter, gutters darker and silted
            if (w >= 1.4f) c = Lerp(c, ASPH_LIT, Mathf.Clamp01(1f - Mathf.Abs(a - 0.5f) * 3f) * 0.45f);
            if (a > 0.82f) c = Lerp(c, Lerp(ASPH_DARK, LAWN_SHADE, 0.35f), (a - 0.82f) * 4f);
            // overgrowth: moss and grass creeping in from the gutters in patches
            float ng = Noise(p.x, p.y, 4.5f, 57);
            if (ng > 0.58f && a > 0.45f) c = Lerp(c, LAWN, Mathf.Clamp01((ng - 0.58f) * 3f) * Mathf.Clamp01((a - 0.45f) * 2.2f) * 0.85f);
            if (n2 > 0.72f) c = Lerp(c, PATCH, (n2 - 0.72f) * 2.5f);
            return c;
        }

        void AddCityOverlay(Geo g, int x0, int y0, int x1, int y1)
        {
            CellRange(x0, y0, x1, y1, out int i0, out int i1, out int j0, out int j1);
            // pass A: rows (u-roads, intersections, sidewalks, plazas)
            for (int j = j0; j < j1; j++)
            {
                int i = i0;
                while (i < i1)
                {
                    int c = Cls(i, j);
                    if (c == C_NONE || !InRect(CellC(i, j), x0, y0, x1, y1)) { i++; continue; }
                    int dir = Dir(i, j);
                    if (Roadish(c) && dir == 2) { i++; continue; }
                    int k = j * nu + i;
                    int e = i + 1;
                    while (e < i1 && e - i < 12 && Cls(e, j) == c && InRect(CellC(e, j), x0, y0, x1, y1))
                    {
                        if (Roadish(c)) { int de = Dir(e, j); if (de == 2 || (de == 3) != (dir == 3) || (dir == 1 && posV[j * nu + e] != posV[k])) break; }
                        e++;
                    }
                    Vector2 a = CellP(i, j), b = CellP(e, j), cc = CellP(e, j + 1), dd = CellP(i, j + 1);
                    if (Roadish(c))
                    {
                        float w = runV[k] * CELL;
                        float acr0, acr1;
                        if (dir == 3) { acr0 = acr1 = 0f; w = 0f; }
                        else { acr0 = (posV[k] / (float)runV[k]) * 2f - 1f; acr1 = ((posV[k] + 1) / (float)runV[k]) * 2f - 1f; }
                        float y = c == C_BRIDGE ? 0.06f : ROAD_Y;
                        if (c == C_BRIDGE && DeckDrawn) { i = e; continue; }   // the raised deck replaces it (MapGen.Bridges.cs)
                        g.Flat(a, b, cc, dd, y, AsphaltCol(a, acr0, w), AsphaltCol(b, acr0, w), AsphaltCol(cc, acr1, w), AsphaltCol(dd, acr1, w));
                    }
                    else if (c == C_SIDE)
                    {
                        Color ca = WalkCol(a, i, j), cb = WalkCol(b, e, j), c2 = WalkCol(cc, e, j + 1), c3 = WalkCol(dd, i, j + 1);
                        g.Flat(a, b, cc, dd, SW_Y, ca, cb, c2, c3);
                    }
                    else if (c == C_PLAZA)
                    {
                        g.Flat(a, b, cc, dd, PAVE_Y, PaveCol(a), PaveCol(b), PaveCol(cc), PaveCol(dd));
                    }
                    i = e;
                }
            }
            // pass B: columns (v-roads)
            for (int i = i0; i < i1; i++)
            {
                int j = j0;
                while (j < j1)
                {
                    int c = Cls(i, j);
                    if (!Roadish(c) || Dir(i, j) != 2 || !InRect(CellC(i, j), x0, y0, x1, y1)) { j++; continue; }
                    int k = j * nu + i;
                    int e = j + 1;
                    while (e < j1 && e - j < 12 && Cls(i, e) == c && Dir(i, e) == 2 && posU[e * nu + i] == posU[k] && InRect(CellC(i, e), x0, y0, x1, y1)) e++;
                    Vector2 a = CellP(i, j), b = CellP(i + 1, j), cc = CellP(i + 1, e), dd = CellP(i, e);
                    float w = runU[k] * CELL;
                    float acr0 = (posU[k] / (float)runU[k]) * 2f - 1f, acr1 = ((posU[k] + 1) / (float)runU[k]) * 2f - 1f;
                    float y = c == C_BRIDGE ? 0.06f : ROAD_Y;
                    if (c == C_BRIDGE && DeckDrawn) { j = e; continue; }
                    g.Flat(a, b, cc, dd, y, AsphaltCol(a, acr0, w), AsphaltCol(b, acr1, w), AsphaltCol(cc, acr1, w), AsphaltCol(dd, acr0, w));
                    j = e;
                }
            }
            // per-cell details: curbs, markings, cracks, wrecks, street furniture
            if (lo) return;
            for (int j = j0; j < j1; j++)
                for (int i = i0; i < i1; i++)
                {
                    int c = Cls(i, j);
                    if (c == C_NONE) continue;
                    var ctr = CellC(i, j);
                    if (!InRect(ctr, x0, y0, x1, y1)) continue;
                    if (c == C_SIDE) SidewalkCell(g, i, j);
                    else if (c == C_ROAD || (c == C_BRIDGE && !DeckDrawn)) RoadCell(g, i, j, c);
                }
        }

        Color WalkCol(Vector2 p, int i, int j)
        {
            float n = Noise(p.x, p.y, 3f, 61), n2 = Noise(p.x, p.y, 8f, 63);
            Color c = Lerp(WALK, WALK2, n * 0.8f);
            // weeds / moss creeping in on the outer edge (away from the curb)
            if (n2 > 0.6f) c = Lerp(c, LAWN, (n2 - 0.6f) * 0.9f);   // weeds creeping over in patches
            return c;
        }

        Color PaveCol(Vector2 p)
        {
            float n = Noise(p.x, p.y, 3f, 71), n2 = Noise(p.x, p.y, 7f, 73);
            Color c = Lerp(PAVE, WALK2, n);
            if (n2 > 0.62f) c = Lerp(c, LAWN, (n2 - 0.62f) * 1.6f);
            return c;
        }

        void SidewalkCell(Geo g, int i, int j)
        {
            // curb faces toward road cells, low edge toward lawn
            for (int s = 0; s < 4; s++)
            {
                int ni = i + (s == 0 ? -1 : s == 1 ? 1 : 0), nj = j + (s == 2 ? -1 : s == 3 ? 1 : 0);
                int nc = Cls(ni, nj);
                if (nc == C_SIDE) continue;
                Vector2 a, b;
                if (s == 0) { a = CellP(i, j); b = CellP(i, j + 1); }
                else if (s == 1) { a = CellP(i + 1, j); b = CellP(i + 1, j + 1); }
                else if (s == 2) { a = CellP(i, j); b = CellP(i + 1, j); }
                else { a = CellP(i, j + 1); b = CellP(i + 1, j + 1); }
                var outDir = Geo.OutDir(a, b, CellC(i, j));
                if (Roadish(nc)) g.Wall(a, b, outDir, ROAD_Y, SW_Y, CURB);
                else g.Wall(a, b, outDir, Mathf.Min(-0.01f, Mathf.Min(GroundH(a), GroundH(b)) - 0.02f), SW_Y, Mul(WALK2, 0.85f));   // down the bank where the ground dips
            }
            StreetFurniture(g, i, j);
        }

        void RoadCell(Geo g, int i, int j, int c)
        {
            int k = j * nu + i;
            int dir = Dir(i, j);
            var ctr = CellC(i, j);
            float y = c == C_BRIDGE ? 0.06f : ROAD_Y;
            long hs = (long)i * 7919 + j * 104729;
            if (c == C_BRIDGE)
            {
                // deck edge + low rail where the deck meets non-road
                for (int s = 0; s < 4; s++)
                {
                    int ni = i + (s == 0 ? -1 : s == 1 ? 1 : 0), nj = j + (s == 2 ? -1 : s == 3 ? 1 : 0);
                    if (Roadish(Cls(ni, nj))) continue;
                    Vector2 a, b;
                    if (s == 0) { a = CellP(i, j); b = CellP(i, j + 1); }
                    else if (s == 1) { a = CellP(i + 1, j); b = CellP(i + 1, j + 1); }
                    else if (s == 2) { a = CellP(i, j); b = CellP(i + 1, j); }
                    else { a = CellP(i, j + 1); b = CellP(i + 1, j + 1); }
                    var o = Geo.OutDir(a, b, ctr);
                    g.Wall(a, b, o, -0.25f, y + 0.02f, Hex("#6a655a"));
                    var a2 = a - o * 0.03f; var b2 = b - o * 0.03f;
                    g.Box(new[] { a2, b2, b2 - o * 0.05f, a2 - o * 0.05f }, y, y + 0.16f, Hex("#7d7666"), Hex("#8a8376"));
                }
            }
            if (dir == 1 || dir == 2)
            {
                bool alongU = dir == 1;
                int run = alongU ? runV[k] : runU[k], pos = alongU ? posV[k] : posU[k];
                float w = run * CELL;
                // centre of road in cross-axis coordinate (grid units of the cross axis)
                float cStart = alongU ? (v0 + (j - pos) * CELL) : (u0 + (i - pos) * CELL);
                float cellA = alongU ? v0 + j * CELL : u0 + i * CELL;            // this cell's cross range
                float along0 = alongU ? u0 + i * CELL : v0 + j * CELL;           // this cell's along range
                Vector2 A = alongU ? GU : GV, B = alongU ? GV : GU;
                float mid = cStart + w * 0.5f;
                if (w >= 3.0f)
                {
                    Line(g, A, B, along0, cellA, mid - 0.06f, 0.028f, PAINT_Y, false, y, hs);
                    Line(g, A, B, along0, cellA, mid + 0.06f, 0.028f, PAINT_Y, false, y, hs + 1);
                    Line(g, A, B, along0, cellA, mid - w * 0.25f, 0.035f, PAINT, true, y, hs + 2);
                    Line(g, A, B, along0, cellA, mid + w * 0.25f, 0.035f, PAINT, true, y, hs + 3);
                }
                else if (w >= 1.5f) Line(g, A, B, along0, cellA, mid, 0.035f, PAINT, true, y, hs);
                // zebra crossing on the approach to an intersection (avenues only)
                if (w >= 3.0f)
                {
                    bool near = false;
                    for (int s = 1; s <= 1 && !near; s++)
                    {
                        if (alongU) near = Dir(i + s, j) == 3 || Dir(i - s, j) == 3;
                        else near = Dir(i, j + s) == 3 || Dir(i, j - s) == 3;
                    }
                    if (near && pos > 0 && pos < run - 1)
                    {
                        float fade = MapGen.Hf(hs, 5);
                        if (fade > 0.25f)
                        {
                            var p0 = G2Ta(A, B, along0 + 0.04f, cellA + 0.12f); var p1 = G2Ta(A, B, along0 + CELL - 0.04f, cellA + 0.12f);
                            var p2 = G2Ta(A, B, along0 + CELL - 0.04f, cellA + 0.34f); var p3 = G2Ta(A, B, along0 + 0.04f, cellA + 0.34f);
                            Color pc = Lerp(PAINT, ASPH, fade < 0.5f ? 0.45f : 0.1f);
                            g.Flat(p0, p1, p2, p3, y + 0.004f, pc, pc, pc, pc);
                        }
                    }
                }
                // wrecks parked / abandoned in the lanes
                float r = (_clear.Count > 0 && Cleared(ctr)) || InBuilding(ctr, 0.7f) ? 0.5f : MapGen.Hf(hs, 11);   // v0.19 audit: no wreck half inside a wall
                if (pos == run / 4 || pos == (3 * run) / 4)
                {
                    if (r < 0.022f && w >= 1.2f && !Game.ExpansionRules)   // v0.19.3 Expansion: road cars are sim wrecks (S.Wrecks)
                    {
                        float ang = Mathf.Atan2(A.y, A.x) + (MapGen.Hf(hs, 12) < 0.5f ? 0f : Mathf.PI) + (MapGen.Hf(hs, 13) - 0.5f) * (MapGen.Hf(hs, 14) < 0.2f ? 1.4f : 0.35f);
                        AddCar(g, ctr, ang, (int)(hs & 0x7fffffff), y);
                    }
                }
                if (r > 0.995f && w >= 2f)
                {
                    // police block-off: a short run of jersey barriers across a lane
                    for (int q = 0; q < 3; q++)
                    {
                        var bp = ctr + B * ((q - 1) * 0.27f) + A * ((MapGen.Hf(hs, 20 + q) - 0.5f) * 0.12f);
                        AddBarrier(g, bp, A, (int)(hs & 0xffff) + q, y);
                    }
                }
            }
            // cracks, potholes, debris, weeds
            if (_clear.Count > 0 && Cleared(ctr)) return;
            if (InBuilding(ctr, 0.1f)) return;   // v0.19 audit: a facade reaching over the curb hides the cell
            float rc = MapGen.Hf(hs, 31);
            if (rc < 0.05f) Crack(g, ctr, hs, y);
            else if (rc < 0.058f) g.Disc(ctr + GU * (MapGen.Hf(hs, 32) - 0.5f) * 0.3f, 0.07f + MapGen.Hf(hs, 33) * 0.08f, y + 0.003f, Hex("#24221d"), PATCH, 7, rc * 50f);
            else if (rc < 0.072f) Debris(g, ctr, hs, y);
            else if (rc < 0.11f) Tuft(g, ctr + GV * (MapGen.Hf(hs, 34) - 0.5f) * 0.3f, (int)(hs & 0xffff), y);
        }

        Vector2 G2Ta(Vector2 A, Vector2 B, float along, float cross) => A * along + B * cross;

        /// <summary>Paint line segment inside this cell at cross-axis coordinate pos (absolute).</summary>
        void Line(Geo g, Vector2 A, Vector2 B, float along0, float cellA, float pos, float hw, Color col, bool dashed, float y, long salt)
        {
            if (pos < cellA - hw || pos > cellA + CELL + hw) return;
            float a0 = along0, a1 = along0 + CELL;
            if (dashed)
            {
                const float P = 2.6f, DL = 0.95f;
                float k = Mathf.Floor(a0 / P);
                float ds = k * P, de = ds + DL;
                if (de <= a0) { ds += P; de += P; k += 1f; }
                a0 = Mathf.Max(a0, ds); a1 = Mathf.Min(a1, de);
                if (a1 <= a0) return;
                float miss = MapGen.Hf((long)k * 31 + (long)(pos * 10f), 7);
                if (miss < 0.22f) return;                                      // ~1/4 of dashes worn away
                if (miss < 0.4f) col = Lerp(col, ASPH, 0.45f);
            }
            else if (MapGen.Hf(salt, 9) < 0.18f) col = Lerp(col, ASPH, 0.55f);
            var p0 = G2Ta(A, B, a0, pos - hw); var p1 = G2Ta(A, B, a1, pos - hw); var p2 = G2Ta(A, B, a1, pos + hw); var p3 = G2Ta(A, B, a0, pos + hw);
            g.Flat(p0, p1, p2, p3, y + 0.003f, col, col, col, col);
        }

        void Crack(Geo g, Vector2 p, long salt, float y)
        {
            float ang = MapGen.Hf(salt, 41) * 6.28f;
            Color c = Lerp(CRACK, ASPH, 0.15f);
            int n = 3 + (int)(MapGen.Hf(salt, 42) * 4f);
            for (int s = 0; s < n; s++)
            {
                float len = 0.15f + MapGen.Hf(salt, 50 + s) * 0.25f;
                ang += (MapGen.Hf(salt, 60 + s) - 0.5f) * 1.6f;
                var q = p + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * len;
                var nrm = new Vector2(-Mathf.Sin(ang), Mathf.Cos(ang)) * 0.012f;
                g.Flat(p - nrm, q - nrm, q + nrm, p + nrm, y + 0.002f, c, c, c, c);
                if (MapGen.Hf(salt, 70 + s) < 0.3f)
                {
                    float a2 = ang + (MapGen.Hf(salt, 80 + s) < 0.5f ? 1f : -1f) * 0.9f;
                    var q2 = q + new Vector2(Mathf.Cos(a2), Mathf.Sin(a2)) * len * 0.6f;
                    var n2 = new Vector2(-Mathf.Sin(a2), Mathf.Cos(a2)) * 0.009f;
                    g.Flat(q - n2, q2 - n2, q2 + n2, q + n2, y + 0.002f, c, c, c, c);
                }
                p = q;
            }
        }

        void AddWildRoads(Geo g, int x0, int y0, int x1, int y1)
        {
            // roads outside the rotated city grid (forest ring): axis-aligned, dirt-blended asphalt
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    int t = d.Tiles[y * d.W + x];
                    if (!IsRoadT(t) || InCity(x + 0.5f, y + 0.5f) || (t == Data.T_BRIDGE && DeckDrawn)) continue;
                    Color c = Lerp(ASPH, DIRT, 0.35f + Noise(x, y, 5f, 3) * 0.3f);
                    g.Flat(new Vector2(x, y), new Vector2(x + 1, y), new Vector2(x + 1, y + 1), new Vector2(x, y + 1), ROAD_Y, c, c, c, c);
                }
        }
    }
}
