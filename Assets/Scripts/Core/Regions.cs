// Port of scripts/core/regions.gd (RefCounted; constructed as RegionGen.new(Data)).
// Generates the Small Town and Farmland regions in the band below the forest.
// NOTE: GDScript `tiles = D.tiles` took a copy-on-write handle to a PackedByteArray, so writes did
// not reach D.tiles until the final reassignment. C# arrays are references, so writes land in
// D.Tiles immediately. Nothing reads D.Tiles during generation, so the outcome is identical.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace DayZero.Core
{
    public sealed class Regions
    {
        readonly Data D;
        readonly int W;
        long _seed; // GDScript int is 64-bit; seed + salt must not overflow 32 bits
        byte[] _tiles;
        int[] _lotId;
        List<Lot> _lots;
        public Dictionary<string, int> Counts = new Dictionary<string, int>();

        public Regions(Data data)
        {
            D = data;
            W = D.W;
        }

        int Idx(int x, int y) => y * W + x;
        static double Hf(long a, long b, long s) => ForestGen.Hf(a, b, s);

        public void Generate(int seedValue)
        {
            _seed = seedValue & 2147483647;
            _tiles = D.Tiles; _lotId = D.LotId; _lots = D.Lots;

            for (int y = D.H0; y < D.H; y++)
                for (int x = 0; x < W; x++)
                {
                    int k = Idx(x, y);
                    _tiles[k] = Data.T_VOID; _lotId[k] = -1;
                }
            Town(D.REGIONS["small_town"].Rect);
            Farmland(D.REGIONS["farmland"].Rect);
            D.Tiles = _tiles; D.LotId = _lotId; D.Lots = _lots;
        }

        void Fill(RectInt r, int t)
        {
            for (int y = r.y; y < Gd.EndY(r); y++)
                for (int x = r.x; x < Gd.EndX(r); x++)
                    _tiles[Idx(x, y)] = (byte)t;
        }

        void Disc(Vector2Int c, double rad, int t, RectInt clip)
        {
            int ri = (int)Math.Ceiling(rad);
            for (int dy = -ri; dy < ri + 1; dy++)
                for (int dx = -ri; dx < ri + 1; dx++)
                {
                    var q = c + new Vector2Int(dx, dy);
                    // Godot Vector2.length() is float32; same result for these small integers.
                    if (Gd.Has(clip, q) && Math.Sqrt(dx * dx + dy * dy) <= rad + 0.01) _tiles[Idx(q.x, q.y)] = (byte)t;
                }
        }

        static bool IsRoad(int t) => t == Data.T_ROAD || t == Data.T_AVE || t == Data.T_DIRT;

        bool Free(int x0, int y0, int w, int h)
        {
            for (int y = y0 - 1; y < y0 + h + 1; y++)
                for (int x = x0 - 1; x < x0 + w + 1; x++)
                {
                    if (x < 0 || x >= W || y < D.H0 || y >= D.H) return false;
                    int t = _tiles[Idx(x, y)];
                    if (t != Data.T_MEADOW && t != Data.T_PARK && t != Data.T_FIELD) return false;
                }
            return true;
        }

        int MakeLot(string kind, int x0, int y0, int w, int h, Vector2Int dir, int pathT, bool check = true)
        {
            if (check && !Free(x0, y0, w, h)) return -1;
            int l = _lots.Count;
            for (int y = y0; y < y0 + h; y++)
                for (int x = x0; x < x0 + w; x++)
                {
                    _tiles[Idx(x, y)] = Data.T_BLD; _lotId[Idx(x, y)] = l;
                }
            Vector2Int door;
            if (dir.y < 0) door = new Vector2Int(x0 + w / 2, y0 - 1);
            else if (dir.y > 0) door = new Vector2Int(x0 + w / 2, y0 + h);
            else if (dir.x < 0) door = new Vector2Int(x0 - 1, y0 + h / 2);
            else door = new Vector2Int(x0 + w, y0 + h / 2);
            var path = new List<Vector2Int>();
            var q = door;
            bool hit = false;
            for (int i = 0; i < 12; i++)
            {
                int t = _tiles[Idx(q.x, q.y)];
                if (IsRoad(t)) { hit = true; break; }
                if (t == Data.T_BLD || t == Data.T_VOID) break;
                path.Add(q);
                q += dir;
            }
            if (hit)
            {
                foreach (var pp in path) _tiles[Idx(pp.x, pp.y)] = (byte)pathT;
            }
            else if (_tiles[Idx(door.x, door.y)] != Data.T_PLAZA)
            {
                _tiles[Idx(door.x, door.y)] = Data.T_MEADOW;
            }
            _lots.Add(new Lot(x0, y0, w, h, kind, door.x, door.y, D.DistrictMap[Idx(x0, y0)], w * h, ""));
            Counts[kind] = (Counts.TryGetValue(kind, out int n) ? n : 0) + 1;
            return l;
        }

        public static readonly int[] TOWN_VX = { 6, 50, 94, 138, 182, 226, 270, 314, 358, 392 };
        public static readonly int[] TOWN_HY = { 6, 36, 66, 96, 126, 160, 190, 220, 248 };
        public const int TOWN_MAIN = 4;

        void Town(RectInt R)
        {
            int ox = R.x, oy = R.y;
            Fill(R, Data.T_MEADOW);
            int xEnd = ox + TOWN_VX[TOWN_VX.Length - 1] + 2; // TOWN_VX[-1]
            int yEnd = oy + TOWN_HY[TOWN_HY.Length - 1] + 2;

            for (int j = 0; j < TOWN_HY.Length; j++)
            {
                int w = j == TOWN_MAIN ? 4 : 2;
                Fill(new RectInt(ox + TOWN_VX[0], oy + TOWN_HY[j], xEnd - (ox + TOWN_VX[0]), w), j == TOWN_MAIN ? Data.T_AVE : Data.T_ROAD);
            }
            for (int i = 0; i < TOWN_VX.Length; i++)
                Fill(new RectInt(ox + TOWN_VX[i], oy + TOWN_HY[0], 2, yEnd - (oy + TOWN_HY[0])), Data.T_ROAD);

            int ncul = 0;
            for (int i = 1; i < TOWN_VX.Length - 1; i++)
                for (int j = 0; j < TOWN_HY.Length - 1; j++)
                {
                    if (j == TOWN_MAIN - 1 || j == TOWN_MAIN) continue;
                    if (Hf(i, j, _seed + 41) > 0.32 && !(ncul == 0 && i == 3 && j == 1)) continue;
                    int ya = oy + TOWN_HY[j] + (j == TOWN_MAIN ? 4 : 2);
                    int yb = oy + TOWN_HY[j + 1];
                    int mid = (ya + yb) / 2;
                    int x = ox + TOWN_VX[i];
                    Fill(new RectInt(x, mid - 4, 2, 8), Data.T_MEADOW);
                    Disc(new Vector2Int(x, mid - 7), 2.6, Data.T_ROAD, R);
                    Disc(new Vector2Int(x + 1, mid + 6), 2.6, Data.T_ROAD, R);
                    ncul += 1;
                }
            Counts["cul_de_sac"] = ncul;

            RectInt Blk(int i, int j)
            {
                int x0 = ox + TOWN_VX[i] + 2, y0 = oy + TOWN_HY[j] + (j == TOWN_MAIN ? 4 : 2);
                return new RectInt(x0, y0, ox + TOWN_VX[i + 1] - x0, oy + TOWN_HY[j + 1] - y0);
            }

            var specials = new Dictionary<int, string> { [2] = "pharmacy", [3] = "gun_shop", [4] = "clinic" };
            for (int i = 2; i < 6; i++)
                foreach (int side in new[] { TOWN_MAIN - 1, TOWN_MAIN })
                {
                    RectInt b = Blk(i, side);
                    for (int k = 0; k < 4; k++)
                    {
                        int x = b.x + 1 + k * 10;
                        string kind = (k == 1 && side == TOWN_MAIN - 1 && specials.ContainsKey(i)) ? specials[i] : "shop";
                        if (side == TOWN_MAIN - 1) MakeLot(kind, x, Gd.EndY(b) - 9, 8, 8, new Vector2Int(0, 1), Data.T_PLAZA);
                        else MakeLot(kind, x, b.y + 1, 8, 8, new Vector2Int(0, -1), Data.T_PLAZA);
                    }
                }

            RectInt bs = Blk(6, TOWN_MAIN - 1);
            Fill(new RectInt(bs.x + 4, Gd.EndY(bs) - 11, 34, 10), Data.T_PLAZA);
            MakeLot("supermarket", bs.x + 6, Gd.EndY(bs) - 24, 28, 12, new Vector2Int(0, 1), Data.T_PLAZA, false);

            RectInt bh = Blk(6, TOWN_MAIN);
            MakeLot("hardware", bh.x + 4, bh.y + 2, 18, 10, new Vector2Int(0, -1), Data.T_PLAZA);

            RectInt bg = Blk(1, TOWN_MAIN);
            Fill(new RectInt(bg.x, bg.y, 18, 8), Data.T_PLAZA);
            MakeLot("gas_station", bg.x + 4, bg.y + 9, 8, 6, new Vector2Int(0, -1), Data.T_PLAZA, false);

            RectInt bc = Blk(7, TOWN_MAIN);
            MakeLot("church", bc.x + 14, bc.y + 2, 10, 16, new Vector2Int(0, -1), Data.T_PLAZA);

            RectInt bsc = Blk(1, 1);
            MakeLot("school", bsc.x + 5, bsc.y + 3, 30, 13, new Vector2Int(0, -1), Data.T_PLAZA);
            Fill(new RectInt(bsc.x + 5, bsc.y + 18, 30, bsc.height - 19), Data.T_PARK);

            for (int j = 0; j < TOWN_HY.Length - 1; j++)
                for (int i = 0; i < TOWN_VX.Length - 1; i++)
                {
                    RectInt b = Blk(i, j);
                    for (int k = 0; k < b.width / 10; k++)
                    {
                        int x = b.x + 2 + k * 10 + (Hf(i * 31 + k, j, _seed + 47) < 0.5 ? 1 : 0);
                        int hw = Hf(k, i * 17 + j, _seed + 53) < 0.6 ? 6 : 7;
                        MakeLot("house", x, b.y + 3, hw, 6, new Vector2Int(0, -1), Data.T_PLAZA);
                        MakeLot("house", x, Gd.EndY(b) - 9, hw, 6, new Vector2Int(0, 1), Data.T_PLAZA);
                    }
                }
        }

        public static readonly int[] FARM_VX = { 4, 112, 220, 328, 434 };
        public const int FARM_MAIN = 126;

        void Farmland(RectInt R)
        {
            int ox = R.x, oy = R.y;
            Fill(R, Data.T_MEADOW);
            int x0 = ox + FARM_VX[0], x1 = ox + FARM_VX[FARM_VX.Length - 1] + 2;
            foreach (int yy in new[] { 4, 250 }) Fill(new RectInt(x0, oy + yy, x1 - x0, 2), Data.T_DIRT);
            Fill(new RectInt(x0, oy + FARM_MAIN, x1 - x0, 3), Data.T_DIRT);
            foreach (int vx in FARM_VX) Fill(new RectInt(ox + vx, oy + 4, 2, 248), Data.T_DIRT);
            int nsilo = 0;
            for (int i = 0; i < FARM_VX.Length - 1; i++)
                foreach (int side in new[] { 0, 1 })
                {
                    int cx0 = ox + FARM_VX[i] + 2, cx1 = ox + FARM_VX[i + 1];
                    int cy0 = oy + (side == 0 ? 6 : FARM_MAIN + 3), cy1 = oy + (side == 0 ? FARM_MAIN : 250);
                    var dir = side == 0 ? new Vector2Int(0, 1) : new Vector2Int(0, -1);
                    int yardY = side == 0 ? cy1 - 11 : cy0 + 4;
                    if (i == 2 && side == 1)
                    {
                        MakeLot("grain_elevator", cx0 + 20, yardY, 10, 16, dir, Data.T_DIRT);
                        for (int s = 0; s < 3; s++) MakeLot("silo", cx0 + 34 + s * 6, yardY, 4, 4, dir, Data.T_DIRT);
                        nsilo += 3;
                        Fields(new RectInt(cx0 + 60, cy0 + 4, cx1 - cx0 - 64, cy1 - cy0 - 8));
                        continue;
                    }
                    if (i == 0 && side == 0)
                    {
                        Fill(new RectInt(cx1 - 22, cy1 - 9, 20, 9), Data.T_PLAZA);
                        MakeLot("gas_station", cx1 - 18, cy1 - 16, 8, 6, dir, Data.T_DIRT, false);
                    }
                    int fx = cx0 + 6 + (int)(Hf(i, side, _seed + 61) * 10.0);
                    MakeLot("farmhouse", fx, yardY + (side == 0 ? 2 : 0), 8, 7, dir, Data.T_DIRT);
                    MakeLot("barn", fx + 14, yardY, 12, 9, dir, Data.T_DIRT);
                    if (Hf(i, side, _seed + 67) < 0.6 || nsilo < 2)
                    {
                        MakeLot("silo", fx + 30, yardY + (side == 0 ? 5 : 0), 4, 4, dir, Data.T_DIRT); nsilo += 1;
                    }

                    int fy0 = side == 0 ? cy0 + 4 : yardY + 14;
                    int fy1 = side == 0 ? yardY - 4 : cy1 - 4;
                    Fields(new RectInt(cx0 + 4, fy0, cx1 - cx0 - 8, fy1 - fy0));
                }
        }

        void Fields(RectInt r)
        {
            if (r.width < 8 || r.height < 8) return;
            int n = 3;
            int sw = r.width / n;
            for (int s = 0; s < n; s++)
                Fill(new RectInt(r.x + s * sw, r.y, sw - 2, r.height), Data.T_FIELD);
            Counts["field"] = (Counts.TryGetValue("field", out int c) ? c : 0) + n;
        }
    }
}
