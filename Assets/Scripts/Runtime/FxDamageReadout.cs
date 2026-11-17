// v0.15 graphics (idea #14): damage you can read. The player's own defences (towers, fences / gates, breaches,
// traps of the current colony, i.e. exactly what Game.RepairQueue repairs) show their condition on the map:
//   below ~80% HP  soot / burn decals on the wall and the ground
//   below ~50% HP  + electrical sparks bursting every few seconds and thin smoke wisps
//   below ~25% HP  + small flames and a pulsing amber wrench icon above it (billboard), so it obviously needs repair
// Fence tiles are grouped into 3x3 tile cells (worst tile wins) so a long damaged wall does not eat the budget.
// Up to 20 sites, 10 quads each = 200 quads in ONE dynamic mesh, "Sprites/Default" + one procedural 2x2 atlas
// (soft puff, soot blotch, flame, wrench). Each site's shown condition eases toward the real one, so marks fade in
// when something gets hit and fade out once it is repaired. NO new shader.
// Visual only: reads Game.S (Towers / Fences / Breaches / Traps) and HP maxima, never writes to the sim.
using System.Collections.Generic;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed class FxDamageReadout : MonoBehaviour
    {
        const int MAXS = 20, PER = 10, MAX = MAXS * PER;   // 200 quads
        const int CELL = 3;                                // fence tiles grouped per CELL x CELL
        const float SHOW = 0.8f;                           // condition below which a site shows anything
        static FxDamageReadout _inst;

        // atlas cells (2x2): 0 puff, 1 soot, 2 flame, 3 wrench
        static readonly int[] ROLE_CELL = { 1, 1, 0, 0, 0, 0, 0, 2, 2, 3 };
        // roles: 0 ground soot, 1 wall soot, 2-4 sparks, 5-6 smoke, 7-8 flames, 9 wrench

        struct Cand { public long Key; public Vector3 P; public float H, Frac, Score; }
        sealed class Site { public long Key; public Vector3 P; public float H, Target = 1f, Shown = 1f, Seed; public bool Seen; }

        readonly List<Site> _sites = new List<Site>(MAXS);
        readonly List<Cand> _cand = new List<Cand>(256);
        readonly Dictionary<long, int> _cellIdx = new Dictionary<long, int>();
        Mesh _mesh; Material _mat; Texture2D _tex; MeshRenderer _mr;
        readonly Vector3[] _v = new Vector3[MAX * 4];
        readonly Color[] _c = new Color[MAX * 4];
        int _frame = -10, _scanFrame = -1000;

        static readonly Color SOOT = new Color(0.07f, 0.055f, 0.045f, 1f);
        static readonly Color SPARK_A = new Color(1.00f, 0.97f, 0.75f, 1f);
        static readonly Color SPARK_B = new Color(0.55f, 0.80f, 1.00f, 1f);
        static readonly Color SMOKE = new Color(0.30f, 0.29f, 0.28f, 1f);
        static readonly Color FLAME_A = new Color(1.00f, 0.80f, 0.32f, 1f);
        static readonly Color FLAME_B = new Color(1.00f, 0.40f, 0.08f, 1f);
        static readonly Color AMBER = new Color(1.00f, 0.70f, 0.12f, 1f);

        /// <summary>Called from FxView.Apply every map frame.</summary>
        public static void Tick(Camera cam, Game g)
        {
            if (g?.S == null || cam == null) return;
            if (_inst == null)
            {
                var go = new GameObject("FxDamageReadout"); Object.DontDestroyOnLoad(go);
                _inst = go.AddComponent<FxDamageReadout>();
                if (!_inst.Init()) { Destroy(go); _inst = null; return; }
            }
            try { _inst.Run(cam, g); } catch (System.Exception) { /* visual only */ }
        }

        bool Init()
        {
            var sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("UI/Default");
            if (sh == null) return false;
            _tex = MakeAtlas(64);
            _mat = new Material(sh) { mainTexture = _tex, renderQueue = 3005, hideFlags = HideFlags.DontSave };
            _mesh = new Mesh { name = "FxDamageReadout", hideFlags = HideFlags.DontSave };
            _mesh.MarkDynamic();
            var uv = new Vector2[MAX * 4]; var tri = new int[MAX * 6];
            for (int i = 0; i < MAX; i++)
            {
                int cell = ROLE_CELL[i % PER];
                float u0 = (cell & 1) * 0.5f, v0 = (cell >> 1) * 0.5f, e = 0.5f / 128f;   // half-texel inset: no bleeding
                uv[i * 4] = new Vector2(u0 + e, v0 + e); uv[i * 4 + 1] = new Vector2(u0 + 0.5f - e, v0 + e);
                uv[i * 4 + 2] = new Vector2(u0 + 0.5f - e, v0 + 0.5f - e); uv[i * 4 + 3] = new Vector2(u0 + e, v0 + 0.5f - e);
                int b = i * 4, t = i * 6;
                tri[t] = b; tri[t + 1] = b + 2; tri[t + 2] = b + 1; tri[t + 3] = b; tri[t + 4] = b + 3; tri[t + 5] = b + 2;
            }
            _mesh.vertices = _v; _mesh.uv = uv; _mesh.colors = _c; _mesh.triangles = tri;
            _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
            gameObject.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _mr = gameObject.AddComponent<MeshRenderer>();
            _mr.sharedMaterial = _mat;
            _mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _mr.receiveShadows = false;
            return true;
        }

        static float H01(int a, int b)
        {
            unchecked
            {
                uint h = (uint)a * 0x9E3779B1u ^ (uint)b * 0x85EBCA77u;
                h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
                return (h & 0xFFFFFF) / 16777216f;
            }
        }

        static Vector3 W(int k, int w) => new Vector3(k % w + 0.5f, 0f, -(k / w + 0.5f));

        void AddCand(long key, Vector3 p, float h, float frac, Vector3 focus)
        {
            float d = new Vector2(p.x - focus.x, p.z - focus.z).magnitude;
            _cand.Add(new Cand { Key = key, P = p, H = h, Frac = frac, Score = frac + d / 120f });
        }

        /// <summary>Collect damaged own structures (current colony) and decide which sites to show.</summary>
        void Scan(Game g, Vector3 focus)
        {
            _cand.Clear(); _cellIdx.Clear();
            var S = g.S; var d = Data.Instance;
            if (d == null) return;
            int w = d.W, col = g.CurCol;
            if (S.Towers != null)
                foreach (var kv in S.Towers)
                {
                    var t = kv.Value;
                    if (t.Col != col || t.Tier < 0 || t.Tier >= Data.TOWERS.Length) continue;
                    float f = (float)(t.Hp / System.Math.Max(1.0, Data.TOWERS[t.Tier].Hp));
                    if (f < SHOW) AddCand(0L * 100000000L + kv.Key, W(kv.Key, w), 3.0f, f, focus);
                }
            if (S.Traps != null)
                foreach (var kv in S.Traps)
                {
                    var t = kv.Value;
                    if (t.Col != col || t.Kind < 0 || t.Kind >= Data.TRAPS.Length || S.Fences.Has(kv.Key)) continue;
                    float f = (float)(t.Hp / System.Math.Max(1.0, Game.TrapHpMax(t)));
                    if (f < SHOW) AddCand(1L * 100000000L + kv.Key, W(kv.Key, w), 0.4f, f, focus);
                }
            // fences, gates and breaches merged per CELL x CELL tile cell: the worst tile of the cell wins
            int cw = (w + CELL - 1) / CELL;
            if (S.Fences != null)
                foreach (var kv in S.Fences)
                {
                    var f0 = kv.Value;
                    if (f0.Col != col) continue;
                    float f = (float)(f0.Hp / System.Math.Max(1.0, g.FenceTileMax(kv.Key)));
                    if (f < SHOW) FenceCell(kv.Key, w, cw, f, g.IsGate(kv.Key) ? 1.7f : 1.3f, focus);
                }
            if (S.Breaches != null)
                foreach (var kv in S.Breaches)
                    if (kv.Value.Col == col && !S.Fences.Has(kv.Key)) FenceCell(kv.Key, w, cw, 0f, 0.7f, focus);

            _cand.Sort((a, b) => a.Score.CompareTo(b.Score));
            foreach (var s in _sites) s.Seen = false;
            int shown = 0;
            for (int i = 0; i < _cand.Count; i++)
            {
                var c = _cand[i];
                var s = Find(c.Key);
                if (s == null)
                {
                    if (shown >= MAXS || _sites.Count >= MAXS) continue;
                    s = new Site { Key = c.Key, Seed = H01((int)(c.Key % 100000000L), (int)(c.Key / 100000000L)) * 100f };
                    _sites.Add(s);
                }
                else if (shown >= MAXS) continue;
                s.P = c.P; s.H = c.H; s.Target = c.Frac; s.Seen = true; shown++;
            }
            foreach (var s in _sites) if (!s.Seen) s.Target = 1f;   // repaired, gone or out of budget: fade out
        }

        void FenceCell(int k, int w, int cw, float f, float h, Vector3 focus)
        {
            int x = k % w, y = k / w;
            long ck = 2L * 100000000L + (y / CELL) * cw + (x / CELL);
            if (_cellIdx.TryGetValue(ck, out int i))
            {
                if (f < _cand[i].Frac) { var c = _cand[i]; float dist = c.Score - c.Frac; c.Frac = f; c.P = W(k, w); c.H = h; c.Score = f + dist; _cand[i] = c; }
                return;
            }
            _cellIdx[ck] = _cand.Count;
            AddCand(ck, W(k, w), h, f, focus);
        }

        Site Find(long key) { for (int i = 0; i < _sites.Count; i++) if (_sites[i].Key == key) return _sites[i]; return null; }

        static float Band(float frac, float hi, float lo) => Mathf.Clamp01((hi - frac) / (hi - lo));   // 0 at hi, 1 at lo

        void Run(Camera cam, Game g)
        {
            _frame = Time.frameCount;
            var tr = cam.transform; var camR = tr.right; var camU = tr.up;
            var hR = new Vector3(camR.x, 0f, camR.z); if (hR.sqrMagnitude < 1e-4f) hR = Vector3.right; hR.Normalize();
            var toCam = new Vector3(tr.position.x, 0f, tr.position.z);
            // ground point the camera looks at (for picking the nearest damaged sites)
            var fwd = tr.forward; var focus = toCam;
            if (fwd.y < -0.05f) focus = tr.position + fwd * (-tr.position.y / fwd.y);
            if (Time.frameCount - _scanFrame >= 20) { _scanFrame = Time.frameCount; Scan(g, focus); }

            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            float t = Time.time;   // follows game pause / speed
            int q = 0;
            for (int i = _sites.Count - 1; i >= 0; i--)
            {
                var s = _sites[i];
                s.Shown = Mathf.MoveTowards(s.Shown, s.Target, dt * 0.6f);
                if (s.Target >= SHOW && s.Shown >= SHOW - 0.001f) _sites.RemoveAt(i);
            }
            for (int i = 0; i < _sites.Count && q + PER <= MAX; i++)
            {
                var s = _sites[i];
                float f = s.Shown, sd = s.Seed;
                float soot = Band(f, SHOW, 0.55f), mid = Band(f, 0.5f, 0.4f), low = Band(f, 0.25f, 0.17f);
                var p = s.P;
                var face = toCam - p; face.y = 0f; face = face.sqrMagnitude > 1e-4f ? face.normalized : Vector3.back;
                var front = p + face * 0.55f;                       // just in front of the wall, toward the camera
                float far = Mathf.Max(1f, Vector3.Distance(tr.position, p) / 40f);

                // 0 ground soot, 1 wall soot / burn
                var c = SOOT; c.a = soot * 0.55f;
                Flat(q++, p + Vector3.up * 0.04f, 0.9f + 0.5f * soot + s.H * 0.15f, sd, c);
                c.a = soot * 0.65f;
                Upright(q++, front + Vector3.up * 0.05f, hR, 0.55f + 0.15f * s.H, s.H * (0.45f + 0.4f * soot), c);

                // 2-4 sparks: a burst every few seconds from a point on the wall
                float per = 2.2f + 1.8f * Mathf.Repeat(sd * 0.37f, 1f);
                float ph = Mathf.Repeat(t / per + sd * 0.11f, 1f) * per;          // seconds into this cycle
                int cyc = Mathf.FloorToInt(t / per + sd * 0.11f);
                var src = front + Vector3.up * (s.H * (0.35f + 0.5f * H01(cyc, (int)sd))) + hR * ((H01((int)sd, cyc) - 0.5f) * 0.6f);
                for (int k = 0; k < 3; k++)
                {
                    float st = ph - k * 0.05f;
                    float a = st > 0f && st < 0.45f ? (1f - st / 0.45f) : 0f;
                    a *= 0.6f + 0.4f * Mathf.Sin(t * 47f + k * 2.1f + sd);              // flicker
                    float ang = (H01(cyc * 3 + k, (int)sd) - 0.5f) * 2.4f;
                    var vel = hR * Mathf.Sin(ang) * 1.6f + face * 0.6f + Vector3.up * (1.2f + 0.8f * H01(k, cyc));
                    float tt = Mathf.Max(0f, st);
                    var sp = src + vel * tt + Vector3.down * (4.5f * tt * tt);
                    var col = Color.Lerp(SPARK_A, SPARK_B, H01(k + 7, cyc) * 0.6f); col.a = Mathf.Clamp01(a) * mid;
                    Bill(q++, sp, camR, camU, (0.16f + 0.10f * (1f - tt / 0.45f)) * Mathf.Lerp(1f, far, 0.5f), col);
                }

                // 5-6 thin smoke wisps
                for (int k = 0; k < 2; k++)
                {
                    float sp = Mathf.Repeat(t * 0.12f + k * 0.5f + sd * 0.07f, 1f);
                    var wp = p + Vector3.up * (s.H * 0.7f + sp * 3.2f) + new Vector3(0.6f, 0f, 0.25f) * (sp * sp * 1.4f)
                           + hR * Mathf.Sin(sd + k * 2f + t * 0.7f) * 0.2f;
                    var col = SMOKE; col.a = mid * Mathf.Clamp01(sp / 0.12f) * (1f - sp) * (0.35f + 0.25f * low);
                    Bill(q++, wp, camR, camU, (0.5f + sp * 1.4f) * Mathf.Lerp(1f, far, 0.5f), col);
                }

                // 7-8 small flames at the base / wall
                for (int k = 0; k < 2; k++)
                {
                    float h = (0.55f + 0.25f * Mathf.Sin(t * (10f + k * 4.3f) + sd + k)) * Mathf.Clamp(s.H * 0.45f, 0.5f, 1.1f);
                    var col = Color.Lerp(FLAME_A, FLAME_B, 0.35f + 0.3f * Mathf.Sin(t * 5.3f + k + sd));
                    col.a = low * (0.8f + 0.2f * Mathf.Sin(t * 13f + k * 1.7f + sd));
                    var b = front + hR * ((k - 0.5f) * 0.45f) + Vector3.up * (k == 0 ? 0.02f : s.H * 0.4f);
                    Upright(q++, b, hR, h * 0.32f, h, col);
                }

                // 9 pulsing amber wrench above the structure (billboard, scaled with distance so it reads zoomed out)
                {
                    float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 4.2f + sd);
                    var col = AMBER; col.a = low * (0.65f + 0.35f * pulse);
                    float sz = (0.9f + 0.15f * pulse) * Mathf.Lerp(1f, far, 0.8f);
                    var wp = p + Vector3.up * (s.H + 0.8f + 0.15f * pulse + sz * 0.5f);
                    Bill(q++, wp, camR, camU, sz, col);
                }
            }
            for (; q < MAX; q++) { int b = q * 4; _v[b] = _v[b + 1] = _v[b + 2] = _v[b + 3] = Vector3.zero; _c[b] = _c[b + 1] = _c[b + 2] = _c[b + 3] = Color.clear; }
            _mesh.vertices = _v; _mesh.colors = _c;
        }

        void Flat(int q, Vector3 p, float r, float rot, Color c)
        {
            int b = q * 4;
            float a = rot;                                   // rotate per site so blotches don't all look the same
            var ax = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r; var az = new Vector3(-ax.z, 0f, ax.x);
            _v[b] = p - ax - az; _v[b + 1] = p + ax - az; _v[b + 2] = p + ax + az; _v[b + 3] = p - ax + az;
            _c[b] = _c[b + 1] = _c[b + 2] = _c[b + 3] = c;
        }

        void Bill(int q, Vector3 p, Vector3 r, Vector3 u, float sz, Color c)
        {
            int b = q * 4; r *= sz * 0.5f; u *= sz * 0.5f;
            _v[b] = p - r - u; _v[b + 1] = p + r - u; _v[b + 2] = p + r + u; _v[b + 3] = p - r + u;
            _c[b] = _c[b + 1] = _c[b + 2] = _c[b + 3] = c;
        }

        void Upright(int q, Vector3 basePt, Vector3 r, float hw, float h, Color c)
        {
            int b = q * 4; r *= hw;
            _v[b] = basePt - r; _v[b + 1] = basePt + r; _v[b + 2] = basePt + r + Vector3.up * h; _v[b + 3] = basePt - r + Vector3.up * h;
            _c[b] = _c[b + 1] = _c[b + 2] = _c[b + 3] = c;
        }

        void LateUpdate()
        {
            if (_mr != null) _mr.enabled = Time.frameCount - _frame <= 2 && Time.frameCount - FxView.Frame <= 2 && _sites.Count > 0;
        }

        static float Noise(float x, float y)
        {
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
            float fx = x - xi, fy = y - yi; fx = fx * fx * (3f - 2f * fx); fy = fy * fy * (3f - 2f * fy);
            float a = H01(xi, yi * 7919), b = H01(xi + 1, yi * 7919), c = H01(xi, (yi + 1) * 7919), d = H01(xi + 1, (yi + 1) * 7919);
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }

        static float SegDist(float px, float py, float ax, float ay, float bx, float by)
        {
            float vx = bx - ax, vy = by - ay, wx = px - ax, wy = py - ay;
            float t = Mathf.Clamp01((wx * vx + wy * vy) / (vx * vx + vy * vy));
            float dx = wx - vx * t, dy = wy - vy * t;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>2x2 atlas of n x n cells: 0 soft puff, 1 ragged soot blotch, 2 flame tongue, 3 wrench (white, dark outline).</summary>
        static Texture2D MakeAtlas(int n)
        {
            int N = n * 2;
            var t = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave };
            var px = new Color32[N * N];
            for (int cell = 0; cell < 4; cell++)
            {
                int ox = (cell & 1) * n, oy = (cell >> 1) * n;
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
                        float r = Mathf.Sqrt(u * u + v * v);
                        byte rgb = 255; float a = 0f;
                        switch (cell)
                        {
                            case 0:   // soft puff
                            {
                                float k = Mathf.Clamp01(1f - r); a = k * k * (3f - 2f * k);
                                break;
                            }
                            case 1:   // soot: noisy blotch, denser in the middle, ragged edge
                            {
                                float nz = Noise(x * 0.18f, y * 0.18f) * 0.6f + Noise(x * 0.45f + 31f, y * 0.45f) * 0.4f;
                                float k = Mathf.Clamp01((1f - r * (0.75f + 0.5f * nz)) * 2.2f);
                                a = k * (0.55f + 0.45f * nz);
                                break;
                            }
                            case 2:   // flame tongue: wide at the bottom, pointed top, hot core
                            {
                                float yy = (v + 1f) * 0.5f;                       // 0 bottom .. 1 top
                                float half = 0.85f * Mathf.Pow(1f - yy, 0.7f) * Mathf.Clamp01(yy * 6f + 0.3f);
                                float k = half > 0.001f ? Mathf.Clamp01(1f - Mathf.Abs(u) / half) : 0f;
                                a = Mathf.Clamp01(k * 1.6f) * Mathf.Clamp01((1f - yy) * 1.4f);
                                break;
                            }
                            default:  // wrench: diagonal handle + open-ended head, white with a dark outline
                            {
                                float handle = SegDist(u, v, -0.62f, -0.62f, 0.18f, 0.18f) - 0.13f;
                                float hx = u - 0.38f, hy = v - 0.38f;
                                float head = Mathf.Sqrt(hx * hx + hy * hy) - 0.33f;
                                // jaw: a slot cut from the head's centre out through its far corner
                                float jaw = SegDist(u, v, 0.40f, 0.40f, 0.9f, 0.9f) - 0.16f;
                                float sdf = Mathf.Max(Mathf.Min(handle, head), -jaw);
                                float px1 = 2f / n;
                                float fill = Mathf.Clamp01(0.5f - sdf / px1);
                                float outl = Mathf.Clamp01(0.5f - (sdf - 0.09f) / px1);
                                a = outl;
                                rgb = (byte)(255f * (outl > 0f ? fill / outl : 0f));
                                break;
                            }
                        }
                        px[(oy + y) * N + ox + x] = new Color32(rgb, rgb, rgb, (byte)(Mathf.Clamp01(a) * 255f));
                    }
            }
            t.SetPixels32(px); t.Apply(false, true);
            return t;
        }

        void OnDestroy() { if (_mesh != null) Destroy(_mesh); if (_mat != null) Destroy(_mat); if (_tex != null) Destroy(_tex); }
    }
}
