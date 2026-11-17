// DAY ZERO: OUTBREAK -- Unity port. v0.16 graphics: spreading overgrowth (visual only, no sim effect).
// The city decays as the days pass: moss/grass tint creeps over asphalt and sidewalks, vine streaks climb
// the bottoms of walls and fences, and small grass tufts break through the paving. Overgrowth level runs
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
        static readonly Color VINE_LO = Geo.Hex("#3e4a2c"), VINE_HI = Geo.Hex("#5e6e3a"), VINE_LEAF = Geo.Hex("#6f7a44");
        static readonly Color TUFT_A = Geo.Hex("#6a7a3e"), TUFT_B = Geo.Hex("#7d8448"), TUFT_DRY = Geo.Hex("#8a8450");

        const float GROUND_Y = 0.25f;

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
            var done = new bool[n0];
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

            // --- 2) walls: green grime at the base; LOD0 adds vine streaks climbing the wall ---
            var edges = full ? new HashSet<long>() : null;
            int vineBudget = full ? 2400 : 0;   // vine quads per chunk (phone cap)
            // walls come as stacked quads (grime band at the base, then the wall): map each vertical
            // quad's bottom corner to its top so a ground band can find the full wall height
            var tops = new Dictionary<long, float>();
            for (int t = 0; t < t0; t += 3)
            {
                Vector3 a = V[T[t]], b = V[T[t + 1]], c = V[T[t + 2]];
                if (!Vertical(a, b, c, out _)) continue;
                float minY = Mathf.Min(a.y, Mathf.Min(b.y, c.y)), maxY = Mathf.Max(a.y, Mathf.Max(b.y, c.y));
                if (maxY - minY < 0.05f) continue;
                foreach (var v in new[] { a, b, c })
                    if (v.y < minY + 0.005f) { long k = Key(v); if (!tops.TryGetValue(k, out float o) || o < maxY) tops[k] = maxY; }
            }
            for (int t = 0; t < t0; t += 3)
            {
                int ia = T[t], ib = T[t + 1], ic = T[t + 2];
                Vector3 a = V[ia], b = V[ib], c = V[ic];
                float minY = Mathf.Min(a.y, Mathf.Min(b.y, c.y)), maxY = Mathf.Max(a.y, Mathf.Max(b.y, c.y));
                if (minY > 0.15f || maxY < 0.2f) continue;
                if (!Vertical(a, b, c, out var nrm)) continue;
                // follow the stack up from a bottom corner to the real wall top
                var top = a.y > minY + 0.005f ? a : b.y > minY + 0.005f ? b : c;
                for (int hop = 0; hop < 4 && tops.TryGetValue(Key(top), out float up) && up > top.y + 0.01f; hop++) top.y = up;
                maxY = top.y;
                if (maxY < 0.8f) continue;
                float wh = maxY - minY;
                int[] ids = { ia, ib, ic };
                foreach (int v in ids)
                {
                    if (done[v] || V[v].y > 0.15f) continue;
                    done[v] = true;
                    float k = Noise(V[v].x, -V[v].z, 5f, 21u);
                    C[v] = Mix(C[v], MOSS, (full ? 0.25f : 0.45f) * level * (0.5f + k));
                }
                if (!full || vineBudget <= 0) continue;
                // bottom edge of this wall triangle
                for (int e = 0; e < 3; e++)
                {
                    int p0 = ids[e], p1 = ids[(e + 1) % 3];
                    if (V[p0].y > 0.15f || V[p1].y > 0.15f) continue;
                    long key = p0 < p1 ? ((long)p0 << 32) | (uint)p1 : ((long)p1 << 32) | (uint)p0;
                    if (!edges.Add(key)) continue;
                    vineBudget -= AddVines(m, V[p0], V[p1], nrm, wh, level);
                }
            }

            // --- 3) grass tufts breaking through the paving (LOD0 only) ---
            if (!full) return;
            float dens = 0.04f + 0.30f * level * level;    // tufts per square tile
            int budget = 900;
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

        static void Quad(MeshData m, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color32 ca, Color32 cb)
        {
            int i = m.V.Count;
            m.V.Add(a); m.V.Add(b); m.V.Add(c); m.V.Add(d);
            m.C.Add(ca); m.C.Add(ca); m.C.Add(cb); m.C.Add(cb);
            // both windings: thin overlays read from either side and the map material may cull back faces
            m.T.Add(i); m.T.Add(i + 1); m.T.Add(i + 2); m.T.Add(i); m.T.Add(i + 2); m.T.Add(i + 3);
            m.T.Add(i); m.T.Add(i + 2); m.T.Add(i + 1); m.T.Add(i); m.T.Add(i + 3); m.T.Add(i + 2);
        }

        static Color32 C32(Color c) => new Color32((byte)(Mathf.Clamp01(c.r) * 255f), (byte)(Mathf.Clamp01(c.g) * 255f), (byte)(Mathf.Clamp01(c.b) * 255f), 255);

        /// <summary>Vine streaks along one wall bottom edge. Returns the quads added.</summary>
        static int AddVines(MeshData m, Vector3 p0, Vector3 p1, Vector3 nrm, float wallH, float level)
        {
            var along = p1 - p0; along.y = 0f;
            float len = along.magnitude;
            if (len < 0.3f) return 0;
            along /= len;
            var off = nrm * 0.025f;
            int slots = Mathf.Max(1, Mathf.RoundToInt(len / 0.45f));
            int added = 0;
            float maxH = Mathf.Min(wallH - 0.05f, 0.4f + 3.2f * level);
            for (int s = 0; s < slots; s++)
            {
                var mid = p0 + along * (len * (s + 0.5f) / slots);
                int hx = Mathf.FloorToInt(mid.x * 10f), hy = Mathf.FloorToInt(mid.z * 10f);
                float r = H01(hx, hy, 41u);
                if (r > 0.25f + 0.7f * level) continue;          // coverage grows with level
                float h = maxH * (0.25f + 0.75f * H01(hx, hy, 42u)) * Mathf.Clamp01(0.4f + level);
                if (h < 0.2f) continue;
                float w = 0.12f + 0.22f * H01(hx, hy, 43u);
                float lean = (H01(hx, hy, 44u) - 0.5f) * 0.3f;
                var lo = Geo.Shade(VINE_LO, nrm); var hi = Geo.Shade(H01(hx, hy, 45u) > 0.5f ? VINE_HI : VINE_LEAF, nrm);
                var b0 = mid - along * w + off; var b1 = mid + along * w + off;
                b0.y = b1.y = Mathf.Min(p0.y, p1.y);
                var topC = mid + along * lean + off; topC.y = b0.y + h;
                var t0 = topC - along * (w * 0.35f); var t1 = topC + along * (w * 0.35f);
                Quad(m, b0, b1, t1, t0, C32(lo), C32(hi));
                added++;
                // a side runner: short branch at mid height
                if (level > 0.45f && H01(hx, hy, 46u) < level - 0.3f)
                {
                    var c0 = (b0 + t0) * 0.5f; var dir = (H01(hx, hy, 47u) > 0.5f ? along : -along);
                    var c1 = c0 + dir * (0.25f + 0.3f * level) + Vector3.up * 0.25f;
                    Quad(m, c0, c0 + Vector3.up * 0.08f, c1 + Vector3.up * 0.05f, c1, C32(lo), C32(hi));
                    added++;
                }
            }
            return added;
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
