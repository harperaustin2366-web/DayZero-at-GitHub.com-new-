// DAY ZERO: OUTBREAK -- Unity port. v0.16 graphics: spreading overgrowth (visual only, no sim effect).
// The city decays as the days pass: moss/grass tint creeps over asphalt and sidewalks, real 3D vines climb
// the walls (wandering stems that branch, leaf clusters, ivy curtains, tendrils hanging off roof edges and
// window sills), and small grass tufts break through the paving. Overgrowth level runs
// from ~0.15 on day 1 to ~0.8 by day 60 and is quantised to 5-day steps; chunk meshes are post-processed
// when built (MapView.MakeChunk) and rebuilt only when the step changes. Everything is a deterministic
// hash of world position (no RNG, so the sim stays bit-exact). LOD1 chunks only get the tint (no extra verts).
using System.Collections.Generic;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public static class FxOvergrowth
    {
        public const int STEP_DAYS = 5;

        /// <summary>The building generator's old static ivy streaks/bands are replaced by the day-scaled 3D vines here.</summary>
        public static readonly bool ReplacesStaticIvy = true;

        /// <summary>5-day step index for a game day (rebuild key).</summary>
        public static int Step(int day) => Mathf.Max(0, day - 1) / STEP_DAYS;

        /// <summary>Overgrowth 0..1 for the start of a step: ~0.15 at day 1, ~0.8 by day 60, ~0.9 late.</summary>
        public static float LevelForStep(int step)
        {
            float day = 1f + step * STEP_DAYS;
            float t = Mathf.Clamp01((day - 1f) / 59f);
            float l = 0.15f + 0.65f * (1f - (1f - t) * (1f - t) * 0.6f - (1f - t) * 0.4f);   // eases out a little
            if (day > 60f) l += 0.1f * Mathf.Clamp01((day - 60f) / 60f);
            return Mathf.Clamp01(l);
        }

        public static float Level(int day) => LevelForStep(Step(day));

        // ---------- deterministic hash noise ----------
        static uint H(int x, int y, uint s)
        {
            unchecked
            {
                uint h = (uint)x * 0x8da6b343u ^ (uint)y * 0xd8163841u ^ s * 0xcb1ab31fu;
                h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
                return h;
            }
        }
        static float H01(int x, int y, uint s) => (H(x, y, s) & 0xffffff) / 16777216f;
        static float Noise(float x, float y, float cell, uint s)
        {
            float fx = x / cell, fy = y / cell;
            int ix = Mathf.FloorToInt(fx), iy = Mathf.FloorToInt(fy);
            float tx = fx - ix, ty = fy - iy;
            tx = tx * tx * (3f - 2f * tx); ty = ty * ty * (3f - 2f * ty);
            float a = H01(ix, iy, s), b = H01(ix + 1, iy, s), c = H01(ix, iy + 1, s), d = H01(ix + 1, iy + 1, s);
            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);
        }

        static readonly Color32 MOSS = new Color32(78, 92, 50, 255), MOSS_DRY = new Color32(96, 98, 58, 255);
        static readonly Color TUFT_A = Geo.Hex("#6a7a3e"), TUFT_B = Geo.Hex("#7d8448"), TUFT_DRY = Geo.Hex("#8a8450");

        const float GROUND_Y = 0.25f;
        /// <summary>Horizontal view direction of the default camera (MapView.YAW = 75 deg).</summary>
        static readonly Vector3 CAM_FWD = new Vector3(0.9659f, 0f, 0.2588f);

        /// <summary>Paved / bare ground colour (asphalt, sidewalk, curb, concrete, dirt): low saturation, not green.</summary>
        static bool Paved(Color32 c)
        {
            int mx = Mathf.Max(c.r, Mathf.Max(c.g, c.b)), mn = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
            return c.g <= c.r + 2 && mx - mn <= 38 && mx >= 40 && mx <= 175;
        }

        static long Key(Vector3 v) => ((long)Mathf.RoundToInt(v.x * 64f) & 0x1fffff) | (((long)Mathf.RoundToInt(v.z * 64f) & 0x1fffff) << 21) | (((long)Mathf.RoundToInt(v.y * 64f) & 0x1fffff) << 42);

        static bool Vertical(Vector3 a, Vector3 b, Vector3 c, out Vector3 n)
        {
            n = Vector3.Cross(b - a, c - a);
            float mag = n.magnitude;
            if (mag < 1e-6f || Mathf.Abs(n.y / mag) > 0.2f) return false;
            n /= mag;
            return true;
        }

        static Color32 Mix(Color32 a, Color32 b, float t)
        {
            t = Mathf.Clamp01(t);
            return new Color32((byte)(a.r + (b.r - a.r) * t), (byte)(a.g + (b.g - a.g) * t), (byte)(a.b + (b.b - a.b) * t), 255);
        }

        /// <summary>Post-processes one chunk mesh in place. full=true (LOD0) adds vines and grass tufts.</summary>
        public static void Apply(MeshData m, float level, bool full)
        {
            if (m == null || m.V.Count == 0 || level <= 0.01f) return;
            int n0 = m.V.Count, t0 = m.T.Count;
            var V = m.V; var C = m.C; var T = m.T;
            var paved = new bool[n0];

            // --- 1) ground: moss / grass creeping through cracks (patchy, two noise octaves) ---
            for (int i = 0; i < n0; i++)
            {
                var p = V[i];
                if (p.y > GROUND_Y || !Paved(C[i])) continue;
                paved[i] = true;
                float x = p.x, y = -p.z;
                float nz = Noise(x, y, 7f, 11u) * 0.6f + Noise(x, y, 2.3f, 12u) * 0.4f;
                float a = Mathf.Clamp01((nz - (1f - level) * 0.95f) * 2.6f);
                if (a <= 0f) continue;
                var tint = Noise(x, y, 11f, 13u) > 0.5f ? MOSS : MOSS_DRY;
                C[i] = Mix(C[i], tint, a * (0.35f + 0.35f * level));
            }

            // --- 2) walls: faint moss tint at each wall base; LOD0 grows real vine geometry on the walls ---
            var walls = FindWalls(m, n0, t0, level, full);
            int vineCap = 0;
            if (full && walls.Count > 0)
            {
                // phone budget: vines + tufts add ~10% to the chunk (vs. the pre-v0.16 chunk with static ivy). Walls
                // are taken in a stable order, so the same walls keep their vines on every rebuild and more join later.
                int cap = Mathf.Max(1000, (int)(n0 * 0.10f));
                // overgrowth clusters: a smooth field picks whole patches of buildings that nature takes first, so
                // vines read as a reclaimed block rather than a thin sprinkle; the patches widen as the days pass
                foreach (var w in walls)
                {
                    var mid = w.P0 + w.Along * (w.Len * 0.5f);
                    w.Pri = 0.75f * Noise(mid.x, mid.z, 6f, 61u) + 0.25f * H01(Qi(mid.x, 8f), Qi(mid.z, 8f), 62u);
                    if (w.Len < 0.6f) w.Pri += 2f;
                    // faces turned away from the default camera (MapView.YAW) are rarely seen: they come last
                    if (Vector3.Dot(w.N, CAM_FWD) > 0.1f) w.Pri += 0.4f;
                }
                walls.Sort((x, y) => x.Pri != y.Pri ? x.Pri.CompareTo(y.Pri) : x.Seed.CompareTo(y.Seed));
                int pick = Mathf.CeilToInt(walls.Count * (0.035f + 0.25f * level * Mathf.Sqrt(level)));
                var vb = new VineBuilder(m, level);
                int start = m.V.Count;
                for (int i = 0; i < pick && i < walls.Count; i++)
                {
                    var w = walls[i];
                    if (w.Len < 0.6f) break;
                    int left = cap - (m.V.Count - start);
                    if (left < 60) break;
                    vb.Grow(w, left, 1f - 0.7f * i / pick);
                }
                vineCap = m.V.Count - start;
            }

            // --- 3) grass tufts breaking through the paving (LOD0 only) ---
            if (!full) return;
            float dens = 0.04f + 0.30f * level * level;    // tufts per square tile
            int budget = Mathf.Clamp(((int)(n0 * 0.12f) - vineCap) / 16, 60, 900);
            for (int t = 0; t < t0 && budget > 0; t += 3)
            {
                int ia = T[t], ib = T[t + 1], ic = T[t + 2];
                if (!paved[ia] || !paved[ib] || !paved[ic]) continue;
                Vector3 a = V[ia], b = V[ib], c = V[ic];
                var cr = Vector3.Cross(b - a, c - a);
                float area = cr.magnitude * 0.5f;
                if (area < 0.02f || cr.y * cr.y < 0.8f * Vector3.Dot(cr, cr)) continue;
                var ctr = (a + b + c) / 3f;
                int hx = Mathf.FloorToInt(ctr.x * 8f), hy = Mathf.FloorToInt(-ctr.z * 8f);
                float want = area * dens;
                int cnt = (int)want + (H01(hx, hy, 31u) < want - (int)want ? 1 : 0);
                for (int k = 0; k < cnt && budget > 0; k++)
                {
                    float r1 = H01(hx + k * 7, hy, 32u), r2 = H01(hx, hy + k * 7, 33u);
                    if (r1 + r2 > 1f) { r1 = 1f - r1; r2 = 1f - r2; }
                    var p = a + (b - a) * r1 + (c - a) * r2;
                    // tufts cluster where the ground moss is: skip spots the crack noise leaves bare
                    float nz = Noise(p.x, -p.z, 7f, 11u) * 0.6f + Noise(p.x, -p.z, 2.3f, 12u) * 0.4f;
                    if (nz < (1f - level) * 0.8f) continue;
                    AddTuft(m, p, level, H(Mathf.FloorToInt(p.x * 50f), Mathf.FloorToInt(p.z * 50f), 34u));
                    budget--;
                }
            }
        }

        static Color32 C32(Color c) => new Color32((byte)(Mathf.Clamp01(c.r) * 255f), (byte)(Mathf.Clamp01(c.g) * 255f), (byte)(Mathf.Clamp01(c.b) * 255f), 255);

        // ======================= walls & vines =======================
        static readonly Color STEM_LO = Geo.Hex("#4a3c29"), STEM_HI = Geo.Hex("#3d4a2a");
        static readonly Color[] LEAF = { Geo.Hex("#4b682b"), Geo.Hex("#5f7d37"), Geo.Hex("#395223"), Geo.Hex("#738c42") };

        /// <summary>One wall face found in the chunk mesh: base corner, horizontal axis, outward normal, size.</summary>
        sealed class Wall
        {
            public Vector3 P0, Along, N;
            public float Len, Top;              // length along the base, height from the base to the top (parapet incl.)
            public uint Seed;
            public float Pri;                   // overgrowth priority (low = taken first)
            public List<Vector3> Sills;         // windows on this face: (u, sill y above base, half width)
        }

        struct Pane { public float x, y, z, w; public Pane(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; } }

        static int Qi(float v, float k) => Mathf.RoundToInt(v * k);

        /// <summary>Finds wall faces by their base edge: the bottom edge of the lowest quad of a stack of vertical
        /// quads (grime band, wall, parapet). Bases need not be on the ground (towers on podiums, raised lots).
        /// Tints the wall foot with moss on every LOD.</summary>
        static List<Wall> FindWalls(MeshData m, int n0, int t0, float level, bool full)
        {
            var V = m.V; var C = m.C; var T = m.T;
            var walls = new List<Wall>();
            var tops = new Dictionary<long, float>();
            var isTop = new HashSet<long>();
            // the map shader draws both sides, so triangle winding says nothing about which side is outside:
            // roofs (any non-vertical surface above the wall foot) are indexed to find the inside of each wall
            var roofs = full ? new Dictionary<long, List<int>>() : null;
            for (int t = 0; t < t0; t += 3)
            {
                Vector3 a = V[T[t]], b = V[T[t + 1]], c = V[T[t + 2]];
                if (!Vertical(a, b, c, out _))
                {
                    if (!full || Mathf.Min(a.y, Mathf.Min(b.y, c.y)) < 0.3f) continue;
                    var cr = Vector3.Cross(b - a, c - a);
                    if (cr.y * cr.y < 0.04f * Vector3.Dot(cr, cr)) continue;
                    int x0 = Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))), x1 = Mathf.FloorToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x)));
                    int z0 = Mathf.FloorToInt(Mathf.Min(a.z, Mathf.Min(b.z, c.z))), z1 = Mathf.FloorToInt(Mathf.Max(a.z, Mathf.Max(b.z, c.z)));
                    if ((x1 - x0 + 1) * (z1 - z0 + 1) > 400) continue;
                    for (int gx = x0; gx <= x1; gx++)
                        for (int gz = z0; gz <= z1; gz++)
                        {
                            long gk = ((long)gx << 32) ^ (uint)gz;
                            if (!roofs.TryGetValue(gk, out var rl)) roofs[gk] = rl = new List<int>();
                            rl.Add(t);
                        }
                    continue;
                }
                float minY = Mathf.Min(a.y, Mathf.Min(b.y, c.y)), maxY = Mathf.Max(a.y, Mathf.Max(b.y, c.y));
                if (maxY - minY < 0.05f) continue;
                foreach (var v in new[] { a, b, c })
                {
                    long k = Key(v);
                    if (v.y < minY + 0.005f) { if (!tops.TryGetValue(k, out float o) || o < maxY) tops[k] = maxY; }
                    else isTop.Add(k);
                }
            }
            // windows (thin vertical quads standing just off a wall), bucketed by plane
            Dictionary<long, List<Pane>> panes = full ? new Dictionary<long, List<Pane>>() : null;
            var seen = new HashSet<(long, long)>();
            var done = new bool[n0];
            for (int t = 0; t < t0; t += 3)
            {
                int ia = T[t], ib = T[t + 1], ic = T[t + 2];
                Vector3 a = V[ia], b = V[ib], c = V[ic];
                if (!Vertical(a, b, c, out var nrm)) continue;
                float minY = Mathf.Min(a.y, Mathf.Min(b.y, c.y)), maxY = Mathf.Max(a.y, Mathf.Max(b.y, c.y));
                float h = maxY - minY;
                if (h < 0.05f) continue;
                nrm.y = 0f; nrm = nrm.normalized;
                if (full && h > 0.25f && h < 0.8f && minY > 0.25f)
                {
                    var ctr = (a + b + c) / 3f;
                    float hw = Mathf.Max(Mathf.Abs(Vector3.Dot(a - ctr, new Vector3(-nrm.z, 0f, nrm.x))), Mathf.Abs(Vector3.Dot(b - ctr, new Vector3(-nrm.z, 0f, nrm.x))));
                    long pk = PlaneKey(nrm, ctr, out _);
                    if (!panes.TryGetValue(pk, out var lst)) panes[pk] = lst = new List<Pane>();
                    lst.Add(new Pane(ctr.x, minY, ctr.z, hw));
                }
                // bottom edge of this triangle?
                int[] ids = { ia, ib, ic };
                int e0 = -1, e1 = -1;
                for (int e = 0; e < 3; e++)
                    if (V[ids[e]].y < minY + 0.005f && V[ids[(e + 1) % 3]].y < minY + 0.005f) { e0 = ids[e]; e1 = ids[(e + 1) % 3]; }
                if (e0 < 0) continue;
                long k0 = Key(V[e0]), k1 = Key(V[e1]);
                if (isTop.Contains(k0) && isTop.Contains(k1)) continue;   // not the foot of the stack
                var top = a.y > minY + 0.005f ? a : b.y > minY + 0.005f ? b : c;
                for (int hop = 0; hop < 6 && tops.TryGetValue(Key(top), out float up) && up > top.y + 0.01f; hop++) top.y = up;
                float wh = top.y - minY;
                if (wh < (minY > 0.3f ? 1.5f : 0.8f)) continue;
                // faint moss at the wall foot (all LODs)
                foreach (int v in ids)
                {
                    if (v >= n0 || done[v] || V[v].y > minY + 0.16f) continue;
                    done[v] = true;
                    float k = Noise(V[v].x, -V[v].z, 5f, 21u);
                    C[v] = Mix(C[v], MOSS, (full ? 0.2f : 0.38f) * level * (0.5f + k));
                }
                if (!full) continue;
                Vector3 p0 = V[e0], p1 = V[e1];
                if (p1.x < p0.x - 1e-4f || (Mathf.Abs(p1.x - p0.x) <= 1e-4f && p1.z < p0.z)) { var tmp = p0; p0 = p1; p1 = tmp; }
                var key = (Key(p0), Key(p1));
                if (!seen.Add(key)) continue;
                var along = p1 - p0; along.y = 0f;
                float len = along.magnitude;
                if (len < (minY > 0.3f ? 1.0f : 0.35f)) continue;
                along /= len;
                p0.y = p1.y = minY;
                // outward = away from the roof this wall holds up
                var mid = (p0 + p1) * 0.5f;
                for (int pass = 0; pass < 2; pass++)
                {
                    float dd = pass == 0 ? 0.4f : 0.9f;
                    bool fwd = RoofAt(m, roofs, mid + nrm * dd, minY + 0.45f, minY + wh + 1.6f);
                    bool back = RoofAt(m, roofs, mid - nrm * dd, minY + 0.45f, minY + wh + 1.6f);
                    if (fwd == back) continue;
                    if (fwd) nrm = -nrm;
                    break;
                }
                walls.Add(new Wall
                {
                    P0 = p0, Along = along, N = nrm, Len = len, Top = wh,
                    Seed = H(Qi(p0.x + p1.x, 16f), Qi(p0.z + p1.z, 16f), (uint)Qi(minY, 16f) + 7u),
                });
            }
            // hand each wall the windows on its face
            if (full)
                foreach (var w in walls)
                {
                    long pk0 = PlaneKey(w.N, w.P0, out int dq);
                    for (int dd = -1; dd <= 1; dd++)
                    {
                        long pk = (pk0 & ~0xffffL) | (uint)((dq + dd) & 0xffff);
                        if (!panes.TryGetValue(pk, out var lst)) continue;
                        foreach (var pn in lst)
                        {
                            var c = new Vector3(pn.x, pn.y, pn.z) - w.P0;
                            float off = Vector3.Dot(c, w.N);
                            if (off < -0.004f || off > 0.07f) continue;
                            float u = Vector3.Dot(c, w.Along), ys = pn.y - w.P0.y;
                            if (u < 0.12f || u > w.Len - 0.12f || ys < 0.2f || ys > w.Top - 0.25f) continue;
                            if (w.Sills == null) w.Sills = new List<Vector3>();
                            bool merged = false;
                            for (int i = 0; i < w.Sills.Count; i++)
                            {
                                var s = w.Sills[i];
                                if (Mathf.Abs(s.x - u) < Mathf.Max(0.1f, s.z) && Mathf.Abs(s.y - ys) < 0.15f)
                                { w.Sills[i] = new Vector3(s.x, Mathf.Min(s.y, ys), Mathf.Max(s.z, pn.w)); merged = true; break; }
                            }
                            if (!merged) w.Sills.Add(new Vector3(u, ys, pn.w));
                        }
                    }
                }
            return walls;
        }

        /// <summary>Is there a roof-like (non-vertical) surface over world point p (xz) between heights y0 and y1?</summary>
        static bool RoofAt(MeshData m, Dictionary<long, List<int>> roofs, Vector3 p, float y0, float y1)
        {
            long gk = ((long)Mathf.FloorToInt(p.x) << 32) ^ (uint)Mathf.FloorToInt(p.z);
            if (!roofs.TryGetValue(gk, out var rl)) return false;
            var V = m.V; var T = m.T;
            foreach (int t in rl)
            {
                Vector3 a = V[T[t]], b = V[T[t + 1]], c = V[T[t + 2]];
                float d = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
                if (Mathf.Abs(d) < 1e-8f) continue;
                float l1 = ((b.z - c.z) * (p.x - c.x) + (c.x - b.x) * (p.z - c.z)) / d;
                float l2 = ((c.z - a.z) * (p.x - c.x) + (a.x - c.x) * (p.z - c.z)) / d;
                float l3 = 1f - l1 - l2;
                if (l1 < -1e-4f || l2 < -1e-4f || l3 < -1e-4f) continue;
                float y = a.y * l1 + b.y * l2 + c.y * l3;
                if (y >= y0 && y <= y1) return true;
            }
            return false;
        }

        /// <summary>Bucket key of a vertical plane (sign-free normal angle + distance from origin, 0.125 tile steps).</summary>
        static long PlaneKey(Vector3 n, Vector3 p, out int dq)
        {
            if (n.x < 0f || (n.x == 0f && n.z < 0f)) n = -n;
            int ang = Mathf.RoundToInt(Mathf.Atan2(n.z, n.x) * 40f);
            dq = Mathf.RoundToInt(Vector3.Dot(n, p) * 8f);
            return ((long)(ang & 0xffff) << 16) | (uint)(dq & 0xffff);
        }

        /// <summary>Builds the vine geometry of one wall: climbing stems with branches and leaves, an ivy curtain,
        /// tendrils hanging from the roof edge and from window sills. Stops cleanly when the vertex budget runs out.</summary>
        sealed class VineBuilder
        {
            const float OFF = 0.03f;          // stems stand this far off the wall (no z-fighting)
            readonly MeshData m; readonly float lv;
            int budget, start; Wall w;

            public VineBuilder(MeshData m, float level) { this.m = m; lv = level; }

            static float R(uint s, int k) => (H((int)(s & 0x7fffffff), k, 77u) & 0xffffff) / 16777216f;
            bool Room(int verts) => m.V.Count - start + verts <= budget;
            Vector3 P(float u, float y, float d) => w.P0 + w.Along * u + Vector3.up * y + w.N * d;
            Vector3 WDir(Vector2 d) => w.Along * d.x + Vector3.up * d.y;
            Color32 Sh(Color c, Vector3 n) => C32(Geo.Shade(c, n.normalized));
            void Tri2(int a, int b, int c) { var T = m.T; T.Add(a); T.Add(b); T.Add(c); T.Add(a); T.Add(c); T.Add(b); }
            static Vector2 Rot(Vector2 v, float a) { float c = Mathf.Cos(a), s = Mathf.Sin(a); return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c); }

            public void Grow(Wall wall, int left, float inten)
            {
                w = wall; start = m.V.Count;
                uint s = w.Seed;
                float len = w.Len;
                float li = lv * (0.4f + 0.6f * inten);
                budget = Mathf.Min(left, (int)(50 + 170 * li));
                // 1) climbing vines: count and height grow with the level
                int nv = 1 + Mathf.FloorToInt(Mathf.Min(len, 4f) * 0.55f * li * (0.5f + R(s, 1)));
                bool curtain = lv > 0.3f && len > 0.9f && R(s, 200) < (li - 0.25f) * 0.65f;
                if (curtain) { budget += 240; nv = Mathf.Max(1, nv - 1); }
                for (int k = 0; k < nv; k++)
                {
                    float u0 = (k + 0.15f + 0.7f * R(s, k * 8 + 2)) / nv * len;
                    float frac = Mathf.Min(1.1f, 0.05f + 1.15f * li * li + 0.45f * R(s, k * 8 + 3));
                    float hT = Mathf.Min(w.Top * frac, 0.5f + 4.5f * lv);
                    if (hT < 0.25f) continue;
                    if (hT > w.Top) hT = w.Top;
                    if (!Vine(u0, 0f, hT, H((int)s, k, 501u), 0f, true)) return;
                    // 2) the curtain grows after the first vine so even a tight budget shows a vine first
                    if (k == 0 && curtain)
                    {
                        float cw = Mathf.Min(len * 0.85f, 0.7f + 1.5f * lv * (0.5f + R(s, 201)));
                        float cu = cw * 0.5f + R(s, 202) * (len - cw);
                        float ch = Mathf.Min(w.Top - 0.05f, w.Top * (0.3f + 0.7f * lv * (0.6f + 0.4f * R(s, 203))));
                        if (!Curtain(cu, cw, ch, H((int)s, 3, 601u))) return;
                    }
                }
                // 3) tendrils hanging from the roof edge / parapet
                if (w.Top > 1.0f && R(s, 300) < li * 0.7f)
                {
                    int n = 1 + (R(s, 301) < lv - 0.3f ? 1 : 0);
                    for (int k = 0; k < n; k++)
                    {
                        float u = (0.1f + 0.8f * R(s, 310 + k)) * len;
                        float L = Mathf.Min(w.Top - 0.3f, (0.25f + 1.2f * lv) * (0.35f + 0.65f * R(s, 330 + k)));
                        if (!Tendril(u, w.Top, L, H((int)s, k, 701u), true)) return;
                    }
                }
                // 4) window sills: a leafy clump on the sill with a short tendril dropping off it
                if (w.Sills != null)
                {
                    int done = 0;
                    for (int i = 0; i < w.Sills.Count && done < 2; i++)
                    {
                        var sl = w.Sills[i];
                        if (R(s, 400 + i) > lv * 0.35f) continue;
                        float u = sl.x + (R(s, 450 + i) - 0.5f) * sl.z * 1.2f;
                        float L = (0.1f + 0.3f * lv) * (0.4f + 0.6f * R(s, 470 + i));
                        if (!Tendril(u, sl.y, L, H((int)s, i, 801u), false)) return;
                        done++;
                    }
                }
            }

            /// <summary>A climbing stem from (u0,y0) up to y1 along a wandering zig-zag, with leaves (dense low,
            /// thinning upward), 1-3 side branches, and a clump draped over the top when it reaches the roof edge.</summary>
            bool Vine(float u0, float y0, float y1, uint seed, float bias, bool main)
            {
                var nodes = new List<Vector2>();
                float u = u0, y = y0;
                nodes.Add(new Vector2(u, y));
                int sgn = (seed & 1) == 0 ? 1 : -1;
                float seg = main ? 0.22f : 0.18f;
                for (int i = 0; y < y1 - 0.01f && i < 48; i++)
                {
                    float r1 = R(seed, i * 3 + 1), r2 = R(seed, i * 3 + 2);
                    float dy = Mathf.Min(seg * (0.7f + 0.6f * r1), y1 - y);
                    y += dy;
                    u += sgn * (0.03f + 0.08f * r2) * (dy / seg) + bias * dy;
                    if (r1 < 0.7f) sgn = -sgn;
                    u = Mathf.Clamp(u, 0.05f, w.Len - 0.05f);
                    nodes.Add(new Vector2(u, y));
                }
                int n = nodes.Count;
                if (n < 2) return true;
                if (!Room(n * 2 + 6)) return false;
                int s0 = Stem(nodes, main ? 0.05f : 0.032f, main ? 0.02f : 0.014f, main ? 0f : 0.35f);
                // leaves: dense near the foot, thinning upward
                for (int j = 1; j < n; j++)
                {
                    float hf = (nodes[j].y - y0 * 0.5f) / Mathf.Max(0.6f, w.Top);
                    var tng = (nodes[j] - nodes[j - 1]).normalized;
                    int cnt = hf < 0.3f ? 2 : hf < 0.65f ? (R(seed, 200 + j) < 0.35f ? 2 : 1) : (R(seed, 200 + j) < 0.8f ? 1 : 0);
                    if (main && hf < 0.4f && R(seed, 260 + j) < 0.5f - hf)
                    {
                        if (!Room(6)) return false;
                        var bc = nodes[j] + new Vector2((R(seed, 270 + j) - 0.5f) * 0.12f, 0f);
                        Blob(P(bc.x, bc.y, OFF), 0.08f + 0.05f * R(seed, 280 + j), LeafCol(seed, 290 + j, hf), seed + (uint)j);
                    }
                    for (int q = 0; q < cnt; q++)
                    {
                        if (!Room(3)) return false;
                        float sd = ((j + q) & 1) == 0 ? 1f : -1f;
                        var d2 = Rot(tng, sd * (0.55f + 0.8f * R(seed, 300 + j * 4 + q)));
                        Leaf(s0 + j * 2 + (sd > 0f ? 1 : 0), m.V[s0 + j * 2 + (sd > 0f ? 1 : 0)], d2,
                             0.1f + 0.07f * R(seed, 320 + j * 4 + q), 0.02f + 0.08f * R(seed, 330 + j * 4 + q), LeafCol(seed, 340 + j * 4 + q, hf), sd);
                    }
                }
                // branches
                if (main && n >= 5)
                {
                    int nb = 1 + (lv > 0.4f && R(seed, 400) < 0.6f ? 1 : 0) + (lv > 0.65f && R(seed, 401) < 0.4f ? 1 : 0);
                    for (int b = 0; b < nb; b++)
                    {
                        int at = 1 + (int)(R(seed, 410 + b) * (n - 3));
                        float bl = (y1 - nodes[at].y) * (0.3f + 0.4f * R(seed, 420 + b));
                        if (bl < 0.25f) continue;
                        float bias2 = (R(seed, 430 + b) < 0.5f ? -1f : 1f) * (0.5f + 0.6f * R(seed, 440 + b));
                        if (!Vine(nodes[at].x, nodes[at].y, nodes[at].y + bl, H((int)seed, b, 551u), bias2, false)) return false;
                    }
                }
                // reached the roof edge: drape over the parapet
                if (main && y1 >= w.Top - 0.02f)
                {
                    if (!Room(20)) return false;
                    var tp = nodes[n - 1];
                    Blob(P(tp.x, w.Top - 0.07f, OFF), 0.13f, LEAF[0], seed + 91u);
                    CapBlob(tp.x, 0.14f, seed + 93u);
                    LeafFree(P(tp.x + 0.07f, w.Top - 0.05f, OFF + 0.02f), new Vector2(0.4f, -1f).normalized, 0.12f, 0.06f, LEAF[1], 1f);
                }
                return true;
            }

            /// <summary>A hanging tendril from (u, yTop) dropping L, with a clump at the top (over the roof edge
            /// when fromRoof, on the sill otherwise) and small leaves down its length.</summary>
            bool Tendril(float u, float yTop, float L, uint seed, bool fromRoof)
            {
                if (L < 0.08f) return true;
                var nodes = new List<Vector2>();
                float y = yTop - 0.02f, x = u;
                nodes.Add(new Vector2(x, y));
                for (int i = 0; y > yTop - L && y > 0.1f && i < 30; i++)
                {
                    y -= 0.11f + 0.05f * R(seed, i);
                    x += (R(seed, 40 + i) - 0.5f) * 0.07f;
                    x = Mathf.Clamp(x, 0.04f, w.Len - 0.04f);
                    nodes.Add(new Vector2(x, Mathf.Max(0.05f, y)));
                }
                int n = nodes.Count;
                if (n < 2) return true;
                if (!Room(n * 5 + 16)) return false;
                int s0 = Stem(nodes, 0.03f, 0.012f, 0.4f);
                for (int j = 1; j < n; j++)
                {
                    float sd = (j & 1) == 0 ? 1f : -1f;
                    var tng = (nodes[j] - nodes[j - 1]).normalized;
                    int vi = s0 + j * 2 + (sd > 0f ? 1 : 0);
                    Leaf(vi, m.V[vi], Rot(tng, sd * (0.5f + 0.6f * R(seed, 80 + j))), 0.08f + 0.05f * R(seed, 90 + j),
                         0.03f + 0.05f * R(seed, 100 + j), LeafCol(seed, 110 + j, 0.5f), sd);
                }
                float br = fromRoof ? 0.12f + 0.05f * R(seed, 120) : 0.08f + 0.03f * R(seed, 120);
                Blob(P(u, yTop - (fromRoof ? 0.06f : 0f), OFF + 0.01f), br, LeafCol(seed, 121, 0.3f), seed + 5u);
                if (fromRoof) CapBlob(u, br * 1.1f, seed + 7u);
                return true;
            }

            /// <summary>Ivy curtain: a bumpy low-poly leaf mass (grid pushed out of the wall by varying amounts,
            /// mottled in three greens) with a ragged top, a few holes, and single leaves sticking out of it.</summary>
            bool Curtain(float cu, float cw, float ch, uint seed)
            {
                const float step = 0.14f;
                int nu = Mathf.Max(3, Mathf.RoundToInt(cw / step));
                int nvMax = Mathf.Max(2, Mathf.CeilToInt(ch / step) + 1);
                var hc = new float[nu];
                for (int i = 0; i < nu; i++)
                {
                    float uc = cu - cw * 0.5f + (i + 0.5f) * cw / nu;
                    float e = (uc - cu) / (cw * 0.5f);
                    float env = Mathf.Sqrt(Mathf.Max(0f, 1f - e * e));
                    hc[i] = Mathf.Min(w.Top - 0.04f, ch * (0.2f + 0.8f * env) * (0.55f + 0.65f * Noise(uc * 2.5f, (seed & 255) + 0.5f, 1f, seed)));
                }
                var idx = new int[(nu + 1) * (nvMax + 1)];
                for (int i = 0; i < idx.Length; i++) idx[i] = -1;
                int Vert(int i, int j)
                {
                    int k = j * (nu + 1) + i;
                    if (idx[k] >= 0) return idx[k];
                    uint hk = H(i, j, seed);
                    float r0 = (hk & 255) / 255f, r1 = ((hk >> 8) & 255) / 255f, r2 = ((hk >> 16) & 255) / 255f;
                    bool rim = i == 0 || i == nu || j == 0;
                    float u = cu - cw * 0.5f + i * cw / nu + (rim ? 0f : (r1 - 0.5f) * step * 0.7f);
                    float y = j * step + (j == 0 ? 0f : (r2 - 0.5f) * step * 0.7f);
                    u = Mathf.Clamp(u, 0.03f, w.Len - 0.03f);
                    float d = OFF - 0.012f + (rim ? 0.01f : 0.025f + 0.07f * r0);
                    var p = P(u, Mathf.Min(y, w.Top - 0.02f), d);
                    var col = LEAF[r1 < 0.3f ? 2 : r1 < 0.62f ? 0 : r1 < 0.92f ? 1 : 3];
                    var nn = w.N + WDir(new Vector2(r2 - 0.5f, r1 - 0.3f)) * 1.1f;
                    idx[k] = m.V.Count;
                    m.V.Add(p); m.C.Add(Sh(Geo.Mul(col, 0.85f + 0.3f * r0), nn));
                    return idx[k];
                }
                for (int i = 0; i < nu; i++)
                {
                    int rows = Mathf.Min(nvMax, Mathf.RoundToInt(hc[i] / step));
                    for (int j = 0; j < rows; j++)
                    {
                        uint hk = H(i, j, seed + 11u);
                        if (j > 1 && j < rows - 1 && (hk & 255) < 14) continue;          // a few holes show the wall
                        if (!Room(8)) return false;
                        int a = Vert(i, j), b = Vert(i + 1, j), c = Vert(i + 1, j + 1), d = Vert(i, j + 1);
                        if (((hk >> 8) & 1) == 0) { Tri2(a, b, c); Tri2(a, c, d); } else { Tri2(a, b, d); Tri2(b, c, d); }
                        // single leaves poking out of the mass, and out of its ragged top and sides
                        bool edge = j == rows - 1 || i == 0 || i == nu - 1;
                        if ((edge && ((hk >> 9) & 3) != 0) || ((hk >> 11) & 7) == 0)
                        {
                            float r = ((hk >> 14) & 255) / 255f;
                            Vector2 dir = j == rows - 1 ? new Vector2((r - 0.5f) * 1.2f, 1f) : i == 0 ? new Vector2(-1f, 0.5f) : i == nu - 1 ? new Vector2(1f, 0.5f) : Rot(Vector2.up, (r - 0.5f) * 3f);
                            int vi = edge && j == rows - 1 ? d : a;
                            Leaf(vi, m.V[vi], dir.normalized, 0.09f + 0.06f * r, 0.04f + 0.07f * (((hk >> 22) & 255) / 255f), LeafCol(hk, 3, 0.3f), r < 0.5f ? 1f : -1f);
                        }
                    }
                }
                // a stem or two climbing on out of the top of the mass
                for (int k = 0; k < 2; k++)
                {
                    float u = cu + (R(seed, 900 + k) - 0.5f) * cw * 0.6f;
                    float y0 = ch * 0.45f, y1 = Mathf.Min(w.Top, ch + 0.3f + 0.6f * lv * R(seed, 910 + k));
                    if (y1 - y0 > 0.35f && !Vine(u, y0, y1, H((int)seed, k, 921u), 0f, false)) return false;
                }
                return true;
            }

            Color LeafCol(uint seed, int k, float hf)
            {
                float r = R(seed, k);
                if (r < 0.1f) return LEAF[3];
                if (r < 0.3f + 0.25f * (1f - Mathf.Clamp01(hf))) return LEAF[2];
                return r < 0.7f ? LEAF[0] : LEAF[1];
            }

            /// <summary>Tapered stem ribbon through wall-space nodes, OFF off the wall (2 verts per node). Returns the first vertex.</summary>
            int Stem(List<Vector2> nodes, float w0, float w1, float green)
            {
                int n = nodes.Count, i0 = m.V.Count;
                for (int j = 0; j < n; j++)
                {
                    var t = (nodes[Mathf.Min(n - 1, j + 1)] - nodes[Mathf.Max(0, j - 1)]).normalized;
                    var side = WDir(new Vector2(-t.y, t.x));
                    float f = j / (float)(n - 1);
                    float wd = Mathf.Lerp(w0, w1, f) * 0.5f;
                    var p = P(nodes[j].x, nodes[j].y, OFF - 0.004f + 0.012f * f);
                    var c = Sh(Geo.Lerp(STEM_LO, STEM_HI, Mathf.Clamp01(f * 0.8f + green)), w.N + Vector3.up * 0.2f);
                    m.V.Add(p - side * wd); m.V.Add(p + side * wd); m.C.Add(c); m.C.Add(c);
                }
                for (int j = 0; j < n - 1; j++)
                {
                    int a = i0 + j * 2;
                    Tri2(a, a + 1, a + 3); Tri2(a, a + 3, a + 2);
                }
                return i0;
            }

            /// <summary>One leaf on an existing vertex (the stalk): a diamond angled d (wall space), tip lifted 'lift'
            /// off the wall and rolled to one side so neighbouring leaves catch the light differently (3 new verts).</summary>
            void Leaf(int bi, Vector3 b, Vector2 d, float len, float lift, Color col, float roll)
            {
                var dir = WDir(d);
                var side = WDir(new Vector2(-d.y, d.x));
                float wid = len * 0.4f;
                var tip = b + dir * len + w.N * lift;
                var mid = b + dir * (len * 0.45f) + w.N * (lift * 0.55f);
                var s1 = mid + side * wid + w.N * (0.03f * roll);
                var s2 = mid - side * wid - w.N * (0.012f * roll);
                var nrm = w.N * 0.8f + Vector3.up * 0.35f + side * (0.5f * roll) - dir * (lift * 3f);
                int i = m.V.Count;
                m.V.Add(s1); m.V.Add(tip); m.V.Add(s2);
                m.C.Add(Sh(col, nrm)); m.C.Add(Sh(Geo.Mul(col, 1.18f), nrm)); m.C.Add(Sh(Geo.Mul(col, 0.88f), nrm));
                Tri2(bi, i, i + 1); Tri2(bi, i + 1, i + 2);
            }

            void LeafFree(Vector3 b, Vector2 d, float len, float lift, Color col, float roll)
            {
                int bi = m.V.Count;
                m.V.Add(b); m.C.Add(Sh(Geo.Mul(col, 0.7f), w.N));
                Leaf(bi, b, d, len, lift, col, roll);
            }

            /// <summary>Low-poly leaf clump on the wall: a 5-sided dome (6 verts) bulging out of the wall plane.</summary>
            void Blob(Vector3 c, float r, Color col, uint seed)
            {
                int i0 = m.V.Count;
                var ctrN = w.N + Vector3.up * 0.45f;
                m.V.Add(c + w.N * (r * 0.6f) + Vector3.up * (r * 0.1f)); m.C.Add(Sh(Geo.Mul(col, 1.12f), ctrN));
                float a0 = R(seed, 1) * 6.283f;
                for (int k = 0; k < 5; k++)
                {
                    float a = a0 + k * 1.2566f + (R(seed, 2 + k) - 0.5f) * 0.6f;
                    float rr = r * (0.75f + 0.45f * R(seed, 10 + k));
                    var dv = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                    var p = c + WDir(dv) * rr + w.N * (0.012f * R(seed, 20 + k));
                    m.V.Add(p); m.C.Add(Sh(Geo.Mul(col, 0.8f + 0.12f * dv.y), w.N + WDir(dv) * 0.8f + Vector3.up * 0.2f));
                }
                for (int k = 0; k < 5; k++) Tri2(i0, i0 + 1 + k, i0 + 1 + (k + 1) % 5);
            }

            /// <summary>Clump lying on the parapet cap, spilling over the outer edge (6 verts).</summary>
            void CapBlob(float u, float r, uint seed)
            {
                if (!Room(6)) return;
                var c = P(u, w.Top + 0.012f, -0.02f);
                var inward = -w.N;
                int i0 = m.V.Count;
                var col = LEAF[R(seed, 0) < 0.5f ? 0 : 1];
                m.V.Add(c + Vector3.up * (r * 0.45f) + w.N * 0.02f); m.C.Add(Sh(Geo.Mul(col, 1.1f), Vector3.up));
                float a0 = R(seed, 1) * 6.283f;
                for (int k = 0; k < 5; k++)
                {
                    float a = a0 + k * 1.2566f;
                    float rr = r * (0.75f + 0.4f * R(seed, 10 + k));
                    var dv = w.Along * Mathf.Cos(a) + inward * Mathf.Sin(a);
                    var p = c + dv * rr;
                    if (Vector3.Dot(dv, w.N) > 0.2f) p.y -= rr * 0.6f;      // droops over the outer edge
                    m.V.Add(p); m.C.Add(Sh(Geo.Mul(col, 0.85f), Vector3.up * 0.6f + dv));
                }
                for (int k = 0; k < 5; k++) Tri2(i0, i0 + 1 + k, i0 + 1 + (k + 1) % 5);
            }
        }

        /// <summary>A grass tuft: an irregular flat moss patch plus three short blades fanned upward.</summary>
        static void AddTuft(MeshData m, Vector3 p, float level, uint h)
        {
            float r = 0.12f + 0.22f * ((h & 255) / 255f) * (0.5f + level);
            float rot = ((h >> 8) & 255) / 255f * 6.283f;
            var col = ((h >> 16) & 3) == 0 ? TUFT_DRY : ((h >> 18) & 1) == 0 ? TUFT_A : TUFT_B;
            var flat = C32(Geo.Mul(col, 0.8f));
            float y = p.y + 0.012f;
            int i0 = m.V.Count;
            m.V.Add(new Vector3(p.x, y, p.z)); m.C.Add(flat);
            const int N = 6;
            for (int k = 0; k < N; k++)
            {
                float ang = rot + k * 6.283f / N;
                float rr = r * (0.6f + 0.4f * (((h >> (k * 3)) & 7) / 7f));
                m.V.Add(new Vector3(p.x + Mathf.Cos(ang) * rr, y, p.z + Mathf.Sin(ang) * rr)); m.C.Add(flat);
            }
            for (int k = 0; k < N; k++) { m.T.Add(i0); m.T.Add(i0 + 1 + (k + 1) % N); m.T.Add(i0 + 1 + k); }
            // blades
            float bh = 0.12f + 0.18f * level;
            var top = C32(Geo.Mul(col, 1.12f)); var bot = C32(Geo.Mul(col, 0.7f));
            for (int k = 0; k < 3; k++)
            {
                float ang = rot + k * 2.094f;
                var d = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
                var side = new Vector3(-d.z, 0f, d.x) * 0.035f;
                var basePt = new Vector3(p.x, y, p.z) + d * (r * 0.2f);
                var tip = basePt + d * (r * 0.5f) + Vector3.up * bh * (0.7f + 0.3f * (((h >> (20 + k)) & 1)));
                int i = m.V.Count;
                m.V.Add(basePt - side); m.V.Add(basePt + side); m.V.Add(tip);
                m.C.Add(bot); m.C.Add(bot); m.C.Add(top);
                m.T.Add(i); m.T.Add(i + 1); m.T.Add(i + 2); m.T.Add(i); m.T.Add(i + 2); m.T.Add(i + 1);
            }
        }
    }

#if !DZ_PREVIEW
    public sealed partial class MapView
    {
        int _ogStep = -1;
        bool _ogRebuilding;

        /// <summary>Overgrowth level for chunks built now (records the step they were built at).</summary>
        float OvergrowthLevel()
        {
            var g = G;
            int day = g?.S != null ? g.Day() : 1;
            if (!_ogRebuilding) _ogStep = FxOvergrowth.Step(day);
            return FxOvergrowth.Level(day);
        }

        /// <summary>Called each frame: when the 5-day overgrowth step changes, re-stream full-detail chunks
        /// and rebuild the far chunks one per frame.</summary>
        void UpdateOvergrowth()
        {
            var g = G;
            if (_ogRebuilding || !Ready || Building || g?.S == null || _ogStep < 0) return;
            if (FxOvergrowth.Step(g.Day()) == _ogStep) return;
            StartCoroutine(RebuildOvergrowth());
        }

        System.Collections.IEnumerator RebuildOvergrowth()
        {
            _ogRebuilding = true;
            _ogStep = FxOvergrowth.Step(G.Day());
            var lod1 = _lod1;
            foreach (var kv in _lod0) if (kv.Value != null) DestroyChunk(kv.Value);
            _lod0.Clear();
            for (int c = 0; c < lod1.Length; c++)
            {
                if (lod1 != _lod1 || Building || _root == null) break;   // a full rebuild took over
                if (lod1[c] != null) DestroyChunk(lod1[c]);
                var ch = _gen.BuildChunk(c % _ncx, c / _ncx, _buckets[c], 1);
                lod1[c] = MakeChunk("Chunk " + (c % _ncx) + "," + (c / _ncx) + " LOD1", ch);
                lod1[c].SetActive(!_lod0.ContainsKey(c));
                yield return null;
            }
            _ogRebuilding = false;
        }
    }
#endif
}
