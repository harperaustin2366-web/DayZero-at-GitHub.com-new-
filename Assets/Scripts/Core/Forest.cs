// Port of scripts/core/forest.gd (RefCounted, built as ForestGen.new(Data)): generates the
// forest ring around the city - terrain noise, bay and rivers, creeks, the ring road and spur
// roads, and wild points of interest (cabins, farmsteads, ranger station, campgrounds, sawmill,
// stands, shacks) as new lots. Writes Data.Tiles / LotId / Lots in place.
//
// Faithfulness notes (see PORT_NOTES.md):
//  * Image.resize(BILINEAR) is replaced by a port of Godot 4's _scale_bilinear<1,uint8> (8-bit
//    fixed point, pixel-centre sampling), then crop() keeps the top-left W x H.
//  * RandomNumberGenerator -> GdRng (PCG32 port).
//  * Godot stops iterating a Dictionary when the current key is erased inside the loop; the
//    "drop short sea runs" loop in _bay_and_river relies on that (only keys up to the first erase
//    are visited). Reproduced on purpose.
//  * Vector math is float32 like Godot; sin/cos/atan2/pow come from .NET, so results can differ
//    from Godot in the last bit and occasionally shift a road/creek by a tile.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace DayZero.Core
{
    /// <summary>Contract Data.SetWorldSeed uses (was a stub in Stubs.cs).</summary>
    public interface IForestGen
    {
        void Generate(int seed);
        Dictionary<string, int> Counts { get; }
        List<long> Timing { get; }
    }

    /// <summary>forest.gd static hash helpers (h32 / hf). GDScript ints are 64-bit, so arithmetic
    /// is done in long with explicit 32-bit masking. Also used by regions.gd and game.gd.</summary>
    public static class ForestGen
    {
        public static long H32(long a, long b, long s)
        {
            unchecked
            {
                long h = (a * 374761393L + b * 668265263L + s * 974634173L + 1013904223L) & 4294967295L;
                h = ((h ^ (h >> 13)) * 1274126177L) & 4294967295L;
                h = (h ^ (h >> 16)) & 4294967295L;
                return h;
            }
        }

        public static double Hf(long a, long b, long s) => (double)(H32(a, b, s) & 65535) / 65535.0;
    }

    public sealed class Forest : IForestGen
    {
        public const int SEA_MIN = 25;
        public const int CS = 8;
        const double TAU = Math.PI * 2.0;

        readonly Data D;
        readonly int W, H, OFF, CW, CHt;
        int seed;
        byte[] tiles;
        int[] lotId;
        List<Lot> lots;
        byte[] sea;
        readonly GdRng rng = new GdRng();
        readonly List<List<Vector2>> roads = new List<List<Vector2>>();
        byte[] fF, fWet, fRid;
        readonly Dictionary<Vector2Int, List<Vector2>> _occ = new Dictionary<Vector2Int, List<Vector2>>();
        readonly Dictionary<string, int> counts = new Dictionary<string, int>();
        readonly List<long> timing = new List<long>();

        public Dictionary<string, int> Counts => counts;
        public List<long> Timing => timing;

        /// <summary>Installs this generator as Data.ForestGenFactory (idempotent).</summary>
        public static void Register()
        {
            if (Data.ForestGenFactory == null) Data.ForestGenFactory = d => new Forest(d);
        }

        public Forest(Data data)
        {
            D = data;
            W = D.W; H = D.H0; OFF = Data.OFF; CW = D.CW; CHt = D.CHt;
        }

        static long H32(long a, long b, long s) => ForestGen.H32(a, b, s);
        static double Hf(long a, long b, long s) => ForestGen.Hf(a, b, s);

        bool InCity(int x, int y) => x >= OFF && y >= OFF && x < OFF + CW && y < OFF + CHt;

        double Dist(int x, int y)
        {
            int dx = Math.Max(0, Math.Max(OFF - x, x - (OFF + CW - 1)));
            int dy = Math.Max(0, Math.Max(OFF - y, y - (OFF + CHt - 1)));
            return Math.Sqrt((double)(dx * dx + dy * dy));
        }

        int Idx(int x, int y) => y * W + x;
        bool Inside(int x, int y) => x >= 0 && y >= 0 && x < W && y < H;

        static Vector2 F(double x, double y) => new Vector2((float)x, (float)y);
        static Vector2 F(Vector2Int p) => new Vector2(p.x, p.y);
        static Vector2 Add(Vector2 a, Vector2 b) => new Vector2(a.x + b.x, a.y + b.y);
        static Vector2 Mul(Vector2 a, double s) { float f = (float)s; return new Vector2(a.x * f, a.y * f); }

        byte[] Field((double L, double amp)[] octaves, int salt)
        {
            int cw = W / CS + 2, ch = H / CS + 2;
            var raw = new byte[cw * ch];
            double ampSum = 0.0;
            foreach (var o in octaves) ampSum += o.amp;
            for (int j = 0; j < ch; j++)
                for (int i = 0; i < cw; i++)
                {
                    double v = 0.0;
                    int oi = 0;
                    foreach (var o in octaves)
                    {
                        double L = o.L;
                        double fx = (double)(i * CS) / L, fy = (double)(j * CS) / L;
                        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
                        double tx = fx - x0, ty = fy - y0;
                        tx = tx * tx * (3.0 - 2.0 * tx); ty = ty * ty * (3.0 - 2.0 * ty);
                        long s = salt * 31 + oi * 7 + seed;
                        double a = Hf(x0, y0, s), b = Hf(x0 + 1, y0, s);
                        double c = Hf(x0, y0 + 1, s), d = Hf(x0 + 1, y0 + 1, s);
                        v += o.amp * ((a + (b - a) * tx) + ((c + (d - c) * tx) - (a + (b - a) * tx)) * ty);
                        oi++;
                    }
                    raw[j * cw + i] = (byte)Math.Max(0, Math.Min(255, (int)(v / ampSum * 255.0)));
                }
            var big = ScaleBilinear(raw, cw, ch, cw * CS, ch * CS);
            // crop(W, H): keep the top-left W x H pixels
            int bw = cw * CS;
            var o2 = new byte[W * H];
            for (int y = 0; y < H; y++) Buffer.BlockCopy(big, y * bw, o2, y * W, Math.Min(W, bw));
            return o2;
        }

        /// <summary>Godot 4 Image::_scale_bilinear&lt;1, uint8_t&gt;.</summary>
        static byte[] ScaleBilinear(byte[] src, int sw, int sh, int dw, int dh)
        {
            const int FRAC_BITS = 8, FRAC_LEN = 1 << FRAC_BITS, FRAC_HALF = FRAC_LEN >> 1, FRAC_MASK = FRAC_LEN - 1;
            var dst = new byte[dw * dh];
            unchecked
            {
                for (uint i = 0; i < dh; i++)
                {
                    uint yfp = (uint)((i + 0.5) * sh * FRAC_LEN / dh);
                    uint yUp = yfp >= FRAC_HALF ? (yfp - FRAC_HALF) >> FRAC_BITS : 0;
                    uint yDown = (yfp + FRAC_HALF) >> FRAC_BITS;
                    if (yDown >= sh) yDown = (uint)sh - 1;
                    uint yFrac = yfp >= FRAC_HALF ? (yfp - FRAC_HALF) & FRAC_MASK : 0;
                    uint yOfsUp = yUp * (uint)sw, yOfsDown = yDown * (uint)sw;
                    for (uint j = 0; j < dw; j++)
                    {
                        uint xfp = (uint)((j + 0.5) * sw * FRAC_LEN / dw);
                        uint xL = xfp >= FRAC_HALF ? (xfp - FRAC_HALF) >> FRAC_BITS : 0;
                        uint xR = (xfp + FRAC_HALF) >> FRAC_BITS;
                        if (xR >= sw) xR = (uint)sw - 1;
                        uint xFrac = xfp >= FRAC_HALF ? (xfp - FRAC_HALF) & FRAC_MASK : 0;
                        uint p00 = (uint)src[yOfsUp + xL] << FRAC_BITS;
                        uint p10 = (uint)src[yOfsUp + xR] << FRAC_BITS;
                        uint p01 = (uint)src[yOfsDown + xL] << FRAC_BITS;
                        uint p11 = (uint)src[yOfsDown + xR] << FRAC_BITS;
                        uint up = p00 + (((p10 - p00) * xFrac) >> FRAC_BITS);
                        uint down = p01 + (((p11 - p01) * xFrac) >> FRAC_BITS);
                        uint interp = up + (((down - up) * yFrac) >> FRAC_BITS);
                        interp >>= FRAC_BITS;
                        dst[i * dw + j] = (byte)interp;
                    }
                }
            }
            return dst;
        }

        public void Generate(int seedValue)
        {
            seed = seedValue & 2147483647;
            rng.SetSeed((long)seed * 7919 + 17);
            tiles = D.Tiles; lotId = D.LotId; lots = D.Lots;
            sea = new byte[W * H];
            var sw = Stopwatch.StartNew();
            long t0 = 0;
            fF = Field(new[] { (60.0, 0.55), (24.0, 0.3), (12.0, 0.15) }, 1);
            fWet = Field(new[] { (72.0, 0.7), (24.0, 0.3) }, 2);
            fRid = Field(new[] { (96.0, 0.6), (32.0, 0.4) }, 3);
            long t1 = sw.ElapsedMilliseconds;
            BaseTerrain();
            long t2 = sw.ElapsedMilliseconds;
            BayAndRiver();
            long t3 = sw.ElapsedMilliseconds;
            Creeks();
            Roads();
            long t4 = sw.ElapsedMilliseconds;
            Pois();
            long t5 = sw.ElapsedMilliseconds;
            timing.Clear();
            timing.AddRange(new[] { t1 - t0, t2 - t1, t3 - t2, t4 - t3, t5 - t4 });
            D.Tiles = tiles; D.LotId = lotId; D.Lots = lots;
        }

        void BaseTerrain()
        {
            for (int y = 0; y < H; y++)
            {
                int row = y * W;
                bool incy = y >= OFF && y < OFF + CHt;
                int dy = Math.Max(0, Math.Max(OFF - y, y - (OFF + CHt - 1)));
                for (int x = 0; x < W; x++)
                {
                    if (incy && x == OFF) continue;
                    if (incy && x > OFF && x < OFF + CW) continue;
                    int k = row + x;
                    int dx = Math.Max(0, Math.Max(OFF - x, x - (OFF + CW - 1)));
                    double d = Math.Sqrt((double)(dx * dx + dy * dy));
                    int t = Data.T_FOREST;
                    int f = fF[k];
                    if (d <= 3.5) t = Data.T_MEADOW;
                    else if (fWet[k] > 186 && d > 18.0) t = Data.T_WATER;
                    else if (f < 92) t = Data.T_MEADOW;
                    else if (d > 12.0 && Math.Abs(fRid[k] - 128) < 3 && f > 125) t = Data.T_ROCK;
                    else if (d > 12.0 && fRid[k] > 212 && (H32(x, y, seed) & 7) < 2) t = Data.T_ROCK;
                    tiles[k] = (byte)t;
                }
            }
        }

        (Vector2Int o, Vector2Int st, int n, Vector2Int nrm)[] Sides() => new[]
        {
            (new Vector2Int(OFF, OFF), new Vector2Int(1, 0), CW, new Vector2Int(0, -1)),
            (new Vector2Int(OFF, OFF + CHt - 1), new Vector2Int(1, 0), CW, new Vector2Int(0, 1)),
            (new Vector2Int(OFF, OFF), new Vector2Int(0, 1), CHt, new Vector2Int(-1, 0)),
            (new Vector2Int(OFF + CW - 1, OFF), new Vector2Int(0, 1), CHt, new Vector2Int(1, 0)),
        };

        void BayAndRiver()
        {
            var sides = Sides();
            var sealen = new ODict<Vector2Int, int>();
            for (int si = 0; si < sides.Length; si++)
            {
                var (o, st, n, nrm) = sides[si];
                int i = 0;
                while (i < n)
                {
                    var p = new Vector2Int(o.x + st.x * i, o.y + st.y * i);
                    if (tiles[Idx(p.x, p.y)] != Data.T_WATER) { i++; continue; }
                    int j = i;
                    while (j < n && tiles[Idx(o.x + st.x * j, o.y + st.y * j)] == Data.T_WATER) j++;
                    int run = j - i;
                    for (int q = i; q < j; q++)
                    {
                        var pq = new Vector2Int(o.x + st.x * q, o.y + st.y * q);
                        sealen[pq] = Math.Max(sealen.Get(pq, 0), run);
                    }
                    if (run < SEA_MIN) River(new Vector2Int(o.x + st.x * i, o.y + st.y * i), st, run, nrm, si);
                    i = j;
                }
            }
            const int maxd = 62;
            // Godot ends `for q in sealen` as soon as the current key is erased (see header).
            foreach (var q in sealen.KeyList())
                if (sealen[q] < SEA_MIN) { sealen.Remove(q); break; }
            if (sealen.Count == 0) return;
            var keys = sealen.KeyList();
            var bb = new RectInt(keys[0].x, keys[0].y, 1, 1);
            foreach (var q in keys) bb = V.Expand(bb, q);
            bb = V.Intersection(Gd.Grow(bb, maxd), new RectInt(0, 0, W, H));
            bb = V.Resize(bb, 1, 1);
            bb = V.Intersection(bb, new RectInt(0, 0, W, H));
            for (int y = bb.y; y < bb.y + bb.height; y++)
                for (int x = bb.x; x < bb.x + bb.width; x++)
                {
                    if (InCity(x, y)) continue;
                    var q = new Vector2Int(Math.Max(OFF, Math.Min(OFF + CW - 1, x)), Math.Max(OFF, Math.Min(OFF + CHt - 1, y)));
                    if (!sealen.Has(q)) continue;
                    int k = Idx(x, y);
                    int qk = Idx(q.x, q.y);
                    double depth = 28.0 + 26.0 * fF[qk] / 255.0 + (Hf(x / 3, y / 3, seed + 5) - 0.5) * 5.0;
                    double d = Dist(x, y);
                    if (d <= depth) { tiles[k] = Data.T_WATER; sea[k] = 1; }
                    else if (d <= depth + 2.5 && tiles[k] == Data.T_FOREST) tiles[k] = Data.T_MEADOW;
                }
        }

        void River(Vector2Int start, Vector2Int along, int w, Vector2Int nrm, int salt)
        {
            var c = Add(Mul(Mul(F(along), w - 1), 0.5), F(start));
            c = Add(c, new Vector2(0.5f, 0.5f));
            double ph = Hf(salt, w, seed) * TAU, ph2 = Hf(w, salt, seed + 1) * TAU;
            double s = 0.0;
            double r = w * 0.5;
            while (s < 400.0)
            {
                double ramp = Math.Max(0.0, Math.Min(1.0, (s - 4.0) / 30.0));
                double off = (Math.Sin(s * 0.045 + ph) * 9.0 + Math.Sin(s * 0.13 + ph2) * 3.0) * ramp;
                var p = Add(Add(c, Mul(F(nrm), s)), Mul(F(along), off));
                if (!Inside((int)p.x, (int)p.y) && s > 2.0) break;
                Disc(p, r, Data.T_WATER, true);
                s += 0.5;
            }
        }

        void Disc(Vector2 p, double r, int t, bool water)
        {
            int ir = (int)Math.Ceiling(r);
            for (int dy = -ir; dy < ir + 1; dy++)
                for (int dx = -ir; dx < ir + 1; dx++)
                {
                    int x = (int)Math.Floor(p.x) + dx, y = (int)Math.Floor(p.y) + dy;
                    if (!Inside(x, y) || InCity(x, y)) continue;
                    if (F(x + 0.5, y + 0.5).DistTo(p) > r + 0.05) continue;
                    int k = Idx(x, y);
                    if (water) tiles[k] = (byte)t;
                    else if (tiles[k] != Data.T_WATER && tiles[k] != Data.T_BLD) tiles[k] = (byte)t;
                }
        }

        void Creeks()
        {
            for (int c = 0; c < 2; c++)
            {
                var p = new Vector2(0f, 0f);
                bool ok = false;
                for (int t = 0; t < 80; t++)
                {
                    p = new Vector2(rng.RandiRange(4, W - 5), rng.RandiRange(4, H - 5));
                    double d = Dist((int)p.x, (int)p.y);
                    if (d > 28.0 && d < 80.0 && tiles[Idx((int)p.x, (int)p.y)] != Data.T_WATER) { ok = true; break; }
                }
                if (!ok) continue;
                var ctr = F(OFF + CW * 0.5, OFF + CHt * 0.5);
                double ang = p.Sub(ctr).Ang() + rng.RandfRange(-0.6, 0.6);
                double ph = rng.Randf() * TAU;
                for (int s = 0; s < 600; s++)
                {
                    double a = ang + Math.Sin(s * 0.06 + ph) * 0.7;
                    p = Add(p, Mul(F(Math.Cos(a), Math.Sin(a)), 0.6));
                    int x = (int)p.x, y = (int)p.y;
                    if (!Inside(x, y)) break;
                    if (InCity(x, y) || Dist(x, y) < 8.0) break;
                    int k = Idx(x, y);
                    if (tiles[k] == Data.T_WATER && s > 20 && sea[k] == 0 && c < 9) break;
                    tiles[k] = Data.T_WATER;
                }
            }
        }

        static bool IsRoadT(int t) => t == Data.T_ROAD || t == Data.T_AVE || t == Data.T_BRIDGE;

        void Roads()
        {
            var mouths = new List<(Vector2Int at, Vector2Int nrm, bool ave)>();
            foreach (var (o, st, n, nrm) in Sides())
            {
                int i = 0, last = -100;
                while (i < n)
                {
                    int t = tiles[Idx(o.x + st.x * i, o.y + st.y * i)];
                    if (!IsRoadT(t)) { i++; continue; }
                    int j = i; bool ave = false;
                    while (j < n)
                    {
                        int t2 = tiles[Idx(o.x + st.x * j, o.y + st.y * j)];
                        if (!IsRoadT(t2)) break;
                        if (t2 == Data.T_AVE) ave = true;
                        j++;
                    }
                    int mid = (i + j - 1) / 2;
                    if (mid - last >= 44 && (ave || Hf(mid, n, seed + 11) < 0.3))
                    {
                        mouths.Add((new Vector2Int(o.x + st.x * mid, o.y + st.y * mid), nrm, ave));
                        last = mid;
                    }
                    i = j;
                }
            }
            var ctr = F(OFF + CW * 0.5, OFF + CHt * 0.5);
            double ph = rng.Randf() * TAU;
            var ring = new List<Vector2>();
            const int steps = 1400;
            for (int i = 0; i < steps + 1; i++)
            {
                double a = TAU * i / steps;
                double dd = 66.0 + 7.0 * Math.Sin(3.0 * a + ph) + 3.0 * Math.Sin(7.0 * a + ph * 2.0);
                double hw = CW * 0.5 + dd, hh = CHt * 0.5 + dd;
                double c = Math.Cos(a), sn = Math.Sin(a);
                double px = Math.Sign(c) * Math.Pow(Math.Abs(c), 2.0 / 6.0) * hw;
                double py = Math.Sign(sn) * Math.Pow(Math.Abs(sn), 2.0 / 6.0) * hh;
                ring.Add(Add(ctr, F(px, py)));
            }
            Lay(ring, 2.2);
            roads.Add(ring);
            foreach (var m in mouths)
            {
                var p = Add(F(m.at), new Vector2(0.5f, 0.5f));
                var nrm = F(m.nrm);
                bool far = Hf((int)p.x, (int)p.y, seed + 3) < 0.4;
                var pts = new List<Vector2>();
                double ph2 = Hf((int)p.y, (int)p.x, seed + 4) * TAU;
                double ang = nrm.Ang();
                double s = 0.0;
                while (true)
                {
                    double ramp = Math.Max(0.0, Math.Min(1.0, (s - 8.0) / 25.0));
                    double a = ang + 0.45 * Math.Sin(s * 0.035 + ph2) * ramp;
                    p = Add(p, Mul(F(Math.Cos(a), Math.Sin(a)), 0.5));
                    s += 0.5;
                    if (!Inside((int)p.x, (int)p.y)) break;
                    if (InCity((int)p.x, (int)p.y)) continue;
                    if (sea[Idx((int)p.x, (int)p.y)] == 1) break;
                    pts.Add(p);
                    if (!far && Dist((int)p.x, (int)p.y) > 72.0) break;
                    if (s > 300.0) break;
                }
                if (pts.Count > 4)
                {
                    Lay(pts, m.ave ? 2.2 : 1.9);
                    roads.Add(pts);
                }
            }
            for (int e = 0; e < 4; e++)
            {
                double a0 = rng.Randf() * TAU;
                var p2 = ring[(int)(a0 / TAU * steps) % ring.Count];
                double ang2 = p2.Sub(ctr).Ang();
                var pts2 = new List<Vector2>();
                double ph3 = rng.Randf() * TAU;
                for (int s2 = 0; s2 < 600; s2++)
                {
                    double a = ang2 + 0.4 * Math.Sin(s2 * 0.02 + ph3);
                    p2 = Add(p2, Mul(F(Math.Cos(a), Math.Sin(a)), 0.5));
                    if (!Inside((int)p2.x, (int)p2.y)) break;
                    if (sea[Idx((int)p2.x, (int)p2.y)] == 1) break;
                    pts2.Add(p2);
                }
                if (pts2.Count > 4)
                {
                    Lay(pts2, 1.9);
                    roads.Add(pts2);
                }
            }
        }

        void Lay(List<Vector2> pts, double w)
        {
            double r = w * 0.5;
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                if (i > 0)
                {
                    var q = pts[i - 1];
                    int n = (int)Math.Ceiling(p.DistTo(q) * 2.0);
                    for (int s = 1; s < n; s++) RoadDisc(q.LerpTo(p, (float)((double)s / n)), r);
                }
                RoadDisc(p, r);
            }
        }

        void RoadDisc(Vector2 p, double r)
        {
            int ir = (int)Math.Ceiling(r);
            for (int dy = -ir; dy < ir + 1; dy++)
                for (int dx = -ir; dx < ir + 1; dx++)
                {
                    int x = (int)Math.Floor(p.x) + dx, y = (int)Math.Floor(p.y) + dy;
                    if (!Inside(x, y) || InCity(x, y)) continue;
                    if (F(x + 0.5, y + 0.5).DistTo(p) > r) continue;
                    int k = Idx(x, y);
                    int t = tiles[k];
                    if (t == Data.T_WATER)
                    {
                        if (sea[k] == 0) tiles[k] = Data.T_BRIDGE;
                    }
                    else if (t != Data.T_BLD && t != Data.T_BRIDGE) tiles[k] = Data.T_DIRT;
                }
        }

        static readonly string[] KINDS = { "cabin", "farmstead", "ranger", "campground", "sawmill", "stand" };
        static readonly (string k, int q)[] QUOTA = { ("cabin", 46), ("farmstead", 8), ("ranger", 2), ("campground", 3), ("sawmill", 1), ("stand", 30) };
        static readonly (string k, int w)[] KIND_W = { ("cabin", 10), ("farmstead", 3), ("ranger", 1), ("campground", 1), ("sawmill", 1), ("stand", 6) };
        static int Quota(string k) { foreach (var e in QUOTA) if (e.k == k) return e.q; return 0; }

        void Pois()
        {
            foreach (var e in QUOTA) counts[e.k] = 0;
            foreach (var pts in new List<List<Vector2>>(roads))
            {
                int i = rng.RandiRange(6, 20);
                while (i < pts.Count - 2)
                {
                    var p = pts[i];
                    var dirv = pts[Math.Min(i + 2, pts.Count - 1)].Sub(pts[Math.Max(i - 2, 0)]).Norm();
                    double side = rng.Randf() < 0.5f ? 1.0 : -1.0;
                    var perp = Mul(new Vector2(-dirv.y, dirv.x), side);
                    string kind = PickKind();
                    if (kind != "") TryPlace(kind, p, perp);
                    i += rng.RandiRange(18, 34);
                }
            }
            foreach (var kind in new[] { "sawmill", "ranger", "farmstead", "campground" })
                for (int t = 0; t < 260; t++)
                {
                    if (counts[kind] >= Quota(kind)) break;
                    var pts2 = roads[rng.RandiRange(0, roads.Count - 1)];
                    if (pts2.Count < 8) continue;
                    int i2 = rng.RandiRange(3, pts2.Count - 4);
                    var dv = pts2[i2 + 2].Sub(pts2[i2 - 2]).Norm();
                    double sd = rng.Randf() < 0.5f ? 1.0 : -1.0;
                    TryPlace(kind, pts2[i2], Mul(new Vector2(-dv.y, dv.x), sd));
                }
            const int step = 22;
            for (int gy = step / 2; gy < H; gy += step)
                for (int gx = step / 2; gx < W; gx += step)
                {
                    int x = gx + rng.RandiRange(-6, 6), y = gy + rng.RandiRange(-6, 6);
                    if (!Inside(x, y) || InCity(x, y) || Dist(x, y) < 8.0) continue;
                    if (NearLot(new Vector2(x, y), 15.0)) continue;
                    int t = tiles[Idx(x, y)];
                    if (t == Data.T_MEADOW) PlaceSimple("shack", new Vector2Int(x - 1, y - 1), 3, 3, 4.5);
                    else if (t == Data.T_FOREST && rng.Randf() < 0.8f) PlaceSimple("cabin", new Vector2Int(x - 1, y - 1), 3, 3, 4.0);
                    else if ((t == Data.T_DIRT || t == Data.T_ROCK) && rng.Randf() < 0.5f) PlaceSimple("cabin", new Vector2Int(x - 1, y + 2), 3, 3, 4.0);
                }
        }

        string PickKind()
        {
            int tot = 0;
            foreach (var e in KIND_W) if (counts[e.k] < Quota(e.k)) tot += e.w;
            if (tot == 0) return "";
            int r = rng.RandiRange(1, tot);
            foreach (var e in KIND_W)
            {
                if (counts[e.k] >= Quota(e.k)) continue;
                r -= e.w;
                if (r <= 0) return e.k;
            }
            return "cabin";
        }

        bool NearLot(Vector2 c, double r)
        {
            var cc = new Vector2Int((int)c.x / 16, (int)c.y / 16);
            for (int dy = -2; dy < 3; dy++)
                for (int dx = -2; dx < 3; dx++)
                    if (_occ.TryGetValue(cc.Add(dx, dy), out var l))
                        foreach (var q in l) if (q.DistTo(c) < r) return true;
            return false;
        }

        void Occupy(Vector2 c)
        {
            var cc = new Vector2Int((int)c.x / 16, (int)c.y / 16);
            if (!_occ.TryGetValue(cc, out var l)) { l = new List<Vector2>(); _occ[cc] = l; }
            l.Add(c);
        }

        bool Free(int x0, int y0, int w, int h, int margin = 1, bool allowRoad = false)
        {
            for (int y = y0 - margin; y < y0 + h + margin; y++)
                for (int x = x0 - margin; x < x0 + w + margin; x++)
                {
                    if (!Inside(x, y) || x < 2 || y < 2 || x >= W - 2 || y >= H - 2) return false;
                    if (InCity(x, y) || Dist(x, y) < 6.0) return false;
                    int t = tiles[Idx(x, y)];
                    if (t == Data.T_WATER || t == Data.T_BLD || t == Data.T_BRIDGE || t == Data.T_FIELD) return false;
                    if (t == Data.T_DIRT && !allowRoad) return false;
                }
            return true;
        }

        static Vector2Int Axis(Vector2 v)
        {
            if (Math.Abs(v.x) >= Math.Abs(v.y)) return new Vector2Int(Math.Sign(v.x), 0);
            return new Vector2Int(0, Math.Sign(v.y));
        }

        void Clear(Vector2 c, double rx, double ry)
        {
            for (int y = (int)(c.y - ry) - 1; y < (int)(c.y + ry) + 2; y++)
                for (int x = (int)(c.x - rx) - 1; x < (int)(c.x + rx) + 2; x++)
                {
                    if (!Inside(x, y) || InCity(x, y)) continue;
                    double dx = (x + 0.5 - c.x) / rx, dy = (y + 0.5 - c.y) / ry;
                    double e = dx * dx + dy * dy + (Hf(x, y, seed + 9) - 0.5) * 0.25;
                    if (e > 1.0) continue;
                    int k = Idx(x, y);
                    if (tiles[k] == Data.T_FOREST || tiles[k] == Data.T_ROCK) tiles[k] = Data.T_MEADOW;
                }
        }

        int District(int x, int y) => D.DistrictMap[Idx(Math.Max(0, Math.Min(W - 1, x)), Math.Max(0, Math.Min(H - 1, y)))];

        int AddLot(int x0, int y0, int w, int h, string kind, Vector2Int toward)
        {
            int l = lots.Count;
            for (int y = y0; y < y0 + h; y++)
                for (int x = x0; x < x0 + w; x++) { tiles[Idx(x, y)] = Data.T_BLD; lotId[Idx(x, y)] = l; }
            Vector2Int door;
            if (toward.y < 0) door = new Vector2Int(x0 + w / 2, y0 - 1);
            else if (toward.y > 0) door = new Vector2Int(x0 + w / 2, y0 + h);
            else if (toward.x < 0) door = new Vector2Int(x0 - 1, y0 + h / 2);
            else door = new Vector2Int(x0 + w, y0 + h / 2);
            int dk = Idx(door.x, door.y);
            if (tiles[dk] != Data.T_DIRT && tiles[dk] != Data.T_BRIDGE) tiles[dk] = Data.T_MEADOW;
            lots.Add(new Lot(x0, y0, w, h, kind, door.x, door.y, District(x0, y0), w * h, ""));
            Occupy(new Vector2((float)(x0 + w * 0.5), (float)(y0 + h * 0.5)));
            return l;
        }

        void Trail(Vector2Int p, Vector2Int d, int maxn = 18)
        {
            var q = p;
            var path = new List<Vector2Int>();
            for (int i = 0; i < maxn; i++)
            {
                q = q + d;
                if (!Inside(q.x, q.y) || InCity(q.x, q.y)) return;
                int t = tiles[Idx(q.x, q.y)];
                if (t == Data.T_DIRT || t == Data.T_BRIDGE || t == Data.T_ROAD || t == Data.T_AVE) break;
                if (t == Data.T_WATER || t == Data.T_BLD) return;
                path.Add(q);
            }
            foreach (var pp in path)
            {
                int k = Idx(pp.x, pp.y);
                if (tiles[k] != Data.T_FIELD) tiles[k] = Data.T_DIRT;
            }
        }

        bool PlaceSimple(string kind, Vector2Int at, int w, int h, double clearR)
        {
            if (!Free(at.x, at.y, w, h, 2)) return false;
            var c = new Vector2((float)(at.x + w * 0.5), (float)(at.y + h * 0.5));
            Clear(c, clearR + 1.0, clearR);
            var tw = Hf(at.x, at.y, seed) < 0.5 ? new Vector2Int(0, 1) : new Vector2Int(0, -1);
            AddLot(at.x, at.y, w, h, kind, tw);
            counts[kind] = counts.GetOr(kind, 0) + 1;
            return true;
        }

        void TryPlace(string kind, Vector2 p, Vector2 perp)
        {
            var a = Axis(perp);
            var along = new Vector2Int(a.y, -a.x);
            var toward = new Vector2Int(-a.x, -a.y);
            var bse = new Vector2Int((int)p.x, (int)p.y);
            switch (kind)
            {
                case "cabin":
                    {
                        var c = bse + a.Mul(4 + rng.RandiRange(0, 3));
                        if (Free(c.x - 1, c.y - 1, 3, 3, 2))
                        {
                            Clear(Add(F(c), new Vector2(0.5f, 0.5f)), 5.0, 4.0);
                            AddLot(c.x - 1, c.y - 1, 3, 3, "cabin", toward);
                            Trail(c + toward.Mul(2), toward);
                            counts["cabin"]++;
                        }
                        break;
                    }
                case "stand":
                    {
                        var c2 = bse + a.Mul(7 + rng.RandiRange(0, 6)) + along.Mul(rng.RandiRange(-3, 3));
                        if (Free(c2.x, c2.y, 1, 1, 1) && tiles[Idx(c2.x, c2.y)] == Data.T_FOREST)
                        {
                            AddLot(c2.x, c2.y, 1, 1, "stand", toward);
                            counts["stand"]++;
                        }
                        break;
                    }
                case "ranger":
                    {
                        var c3 = bse + a.Mul(6);
                        if (Free(c3.x - 3, c3.y - 3, 6, 6, 3))
                        {
                            Clear(F(c3), 8.0, 7.0);
                            AddLot(c3.x - 3, c3.y - 2, 5, 4, "ranger", toward);
                            Trail(c3 + toward.Mul(3), toward);
                            counts["ranger"]++;
                        }
                        break;
                    }
                case "sawmill":
                    {
                        var c4 = bse + a.Mul(11);
                        int x0 = c4.x - 8, y0 = c4.y - 8;
                        if (Free(x0, y0, 16, 16, 2))
                        {
                            Clear(F(c4), 13.0, 11.0);
                            var m = RectAt(c4 + along.Mul(-3), 6, 9, a);
                            AddLot(m.x, m.y, m.width, m.height, "sawmill", toward);
                            var s = RectAt(c4 + along.Mul(5) + a.Mul(2), 4, 5, a);
                            AddLot(s.x, s.y, s.width, s.height, "mill_shed", toward);
                            Trail(c4 + toward.Mul(6), toward, 24);
                            counts["sawmill"]++;
                        }
                        break;
                    }
                case "campground":
                    {
                        var c5 = bse + a.Mul(10);
                        if (Free(c5.x - 7, c5.y - 7, 14, 14, 1))
                        {
                            Clear(F(c5), 10.0, 9.0);
                            int n = 0;
                            foreach (var o in new[] { new Vector2Int(-4, -4), new Vector2Int(3, -4), new Vector2Int(-4, 3), new Vector2Int(3, 3) })
                            {
                                var hc = c5 + o;
                                if (Free(hc.x, hc.y, 2, 2, 0, true) && n < 4)
                                {
                                    AddLot(hc.x, hc.y, 2, 2, "camp_hut", o.y < 0 ? new Vector2Int(-Math.Sign(o.x), 0) : new Vector2Int(0, 1));
                                    n++;
                                }
                            }
                            Trail(c5 + toward.Mul(6), toward, 24);
                            counts["campground"]++;
                        }
                        break;
                    }
                case "farmstead":
                    {
                        var c6 = bse + a.Mul(14);
                        if (Free(c6.x - 11, c6.y - 11, 22, 22, 1))
                        {
                            Clear(Add(F(c6), Mul(F(a), 2.0)), 15.0, 15.0);
                            var fh = RectAt(c6.Sub(a.Mul(4)) + along.Mul(-4), 4, 5, a);
                            AddLot(fh.x, fh.y, fh.width, fh.height, "farmhouse", toward);
                            var br = RectAt(c6.Sub(a.Mul(3)) + along.Mul(5), 5, 6, a);
                            AddLot(br.x, br.y, br.width, br.height, "barn", along.Mul(-1));
                            var fr = RectAt(c6 + a.Mul(6), 16, 8, a);
                            for (int y = fr.y; y < fr.y + fr.height; y++)
                                for (int x = fr.x; x < fr.x + fr.width; x++)
                                    if (Inside(x, y))
                                    {
                                        int t = tiles[Idx(x, y)];
                                        if (t == Data.T_MEADOW || t == Data.T_FOREST || t == Data.T_ROCK) tiles[Idx(x, y)] = Data.T_FIELD;
                                    }
                            Trail(c6.Sub(a.Mul(7)), toward, 20);
                            counts["farmstead"]++;
                        }
                        break;
                    }
            }
        }

        static RectInt RectAt(Vector2Int c, int l, int d, Vector2Int a)
        {
            int w = a.x == 0 ? l : d;
            int h = a.x == 0 ? d : l;
            return new RectInt(c.x - w / 2, c.y - h / 2, w, h);
        }
    }
}
