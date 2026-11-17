// v0.15 graphics (idea #7): burning buildings with smoke columns you can see from across the map.
// 6-10 lots burn at a time, picked deterministically from hash(seed, lot, epoch) and re-picked every 3 game days,
// weighted toward high district danger (Game.DistrictDangerAt) and lots that still hold loot (S.Loot) -- smoke on
// the horizon marks danger and loot. Each fire = 1 warm ground light pool (night only), 3 flickering flame
// billboards at the roof line and 16 rising, wind-drifted smoke puffs (grow with height and with camera distance so
// columns still read when zoomed out). Total cap 200 quads in ONE dynamic mesh drawn with "Sprites/Default" (the
// same always-shipped fallback FxLights uses) and a procedural soft-puff texture. NO new shader.
// Visual only: reads the sim (Game.S, DistrictDangerAt, Loot) and MapGen.LotH, never writes to either.
using System.Collections.Generic;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed class FxFires : MonoBehaviour
    {
        const int MAXF = 10, PUFFS = 16, FLAMES = 3, PER = 1 + FLAMES + PUFFS, MAX = MAXF * PER;   // 200 quads
        const int EPOCH_DAYS = 3;
        const float COL_H = 16f;                       // smoke column height in tiles above the roof
        static FxFires _inst;

        struct Fire { public Vector3 P; public float Top, Seed, Size; }

        readonly List<Fire> _fires = new List<Fire>(MAXF);
        Mesh _mesh; Material _mat; Texture2D _tex; MeshRenderer _mr;
        readonly Vector3[] _v = new Vector3[MAX * 4];
        readonly Color[] _c = new Color[MAX * 4];
        int _frame = -10, _epoch = int.MinValue, _seed, _lots = -1;
        object _gen;

        static readonly Color FLAME_A = new Color(1.00f, 0.78f, 0.30f, 1f);
        static readonly Color FLAME_B = new Color(1.00f, 0.38f, 0.08f, 1f);
        static readonly Color POOL = new Color(1.00f, 0.50f, 0.16f, 1f);
        static readonly Color SMOKE = new Color(0.20f, 0.19f, 0.18f, 1f);
        static readonly Color SMOKE_HI = new Color(0.42f, 0.40f, 0.37f, 1f);

        /// <summary>Called from FxView.Apply every map frame. lights = FxSky light strength (0 by day).</summary>
        public static void Tick(Camera cam, Game g, float lights)
        {
            if (g?.S == null || cam == null) return;
            if (_inst == null)
            {
                var go = new GameObject("FxFires"); Object.DontDestroyOnLoad(go);
                _inst = go.AddComponent<FxFires>();
                if (!_inst.Init()) { Destroy(go); _inst = null; return; }
            }
            try { _inst.Run(cam, g, lights); } catch (System.Exception) { /* visual only */ }
        }

        bool Init()
        {
            var sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("UI/Default");
            if (sh == null) return false;
            _tex = MakePuff(64);
            _mat = new Material(sh) { mainTexture = _tex, renderQueue = 3010, hideFlags = HideFlags.DontSave };
            _mesh = new Mesh { name = "FxFires", hideFlags = HideFlags.DontSave };
            _mesh.MarkDynamic();
            var uv = new Vector2[MAX * 4]; var tri = new int[MAX * 6];
            for (int i = 0; i < MAX; i++)
            {
                uv[i * 4] = new Vector2(0, 0); uv[i * 4 + 1] = new Vector2(1, 0); uv[i * 4 + 2] = new Vector2(1, 1); uv[i * 4 + 3] = new Vector2(0, 1);
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

        static float H01(int a, int b, int c)
        {
            unchecked
            {
                uint h = (uint)a * 0x9E3779B1u ^ (uint)b * 0x85EBCA77u ^ (uint)c * 0xC2B2AE3Du;
                h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
                return (h & 0xFFFFFF) / 16777216f;
            }
        }

        void Pick(Game g, MapGen gen)
        {
            _fires.Clear();
            var d = Data.Instance; var S = g.S;
            if (d == null || gen == null || gen.LotH == null) return;
            var skip = new HashSet<int>();
            foreach (var b in g.AllBases()) skip.Add(b.Lot);
            if (S.Camps != null) foreach (var c in S.Camps) skip.Add(c.Lot);
            int n = d.Lots.Count;
            var cand = new List<KeyValuePair<float, int>>();
            for (int l = 0; l < n && l < gen.LotH.Length; l++)
            {
                if (skip.Contains(l) || gen.LotH[l] < 1.0f) continue;
                var L = d.Lots[l];
                float danger = (g.DistrictDangerAt(L.DoorX, L.DoorY) - 1) / 4f;
                float loot = S.Loot != null && l < S.Loot.Length ? Mathf.Clamp01(S.Loot[l]) : 0.5f;
                float score = H01(_seed, l, _epoch) * 0.55f + danger * 0.30f + loot * 0.15f;
                cand.Add(new KeyValuePair<float, int>(score, l));
            }
            cand.Sort((a, b) => b.Key.CompareTo(a.Key));
            int want = 6 + (int)(H01(_seed, _epoch, 77) * 4.999f);
            for (int i = 0; i < cand.Count && _fires.Count < want; i++)
            {
                int l = cand[i].Value; var L = d.Lots[l];
                var p = new Vector3(L.X + L.W * 0.5f, 0f, -(L.Y + L.H * 0.5f));
                bool close = false;
                foreach (var f in _fires) if ((f.P - p).sqrMagnitude < 144f) { close = true; break; }   // >= 12 tiles apart
                if (close) continue;
                _fires.Add(new Fire { P = p, Top = gen.LotH[l], Seed = H01(_seed, l, 3) * 100f, Size = Mathf.Clamp(Mathf.Min(L.W, L.H) * 0.35f, 0.6f, 1.6f) });
            }
        }

        void Run(Camera cam, Game g, float lights)
        {
            _frame = Time.frameCount;
            var gen = Game.YardProvider?.Target as MapGen;
            int epoch = (g.Day() - 1) / EPOCH_DAYS;
            int lots = Data.Instance != null ? Data.Instance.Lots.Count : 0;
            if (epoch != _epoch || g.S.Seed != _seed || !ReferenceEquals(gen, _gen) || lots != _lots)
            { _epoch = epoch; _seed = g.S.Seed; _gen = gen; _lots = lots; Pick(g, gen); }

            var tr = cam.transform; var camR = tr.right; var camU = tr.up;
            var hR = new Vector3(camR.x, 0f, camR.z); if (hR.sqrMagnitude < 1e-4f) hR = Vector3.right; hR.Normalize();
            float t = Time.time;   // follows game pause / speed
            var wind = new Vector3(0.8f, 0f, 0.35f);
            int q = 0;
            for (int i = 0; i < _fires.Count; i++)
            {
                var f = _fires[i];
                float s = f.Seed;
                float fl = 0.80f + 0.12f * Mathf.Sin(t * 9.1f + s) + 0.08f * Mathf.Sin(t * 17.3f + s * 2.3f);
                var roof = f.P + Vector3.up * f.Top;
                float far = Mathf.Max(1f, Vector3.Distance(tr.position, roof) / 45f);   // keep columns readable zoomed out

                // ground light pool (night only)
                var pc = POOL; pc.a = Mathf.Clamp01(lights * 0.6f * fl);
                Flat(q++, f.P + Vector3.up * 0.08f, 3.2f + f.Size, pc);

                // smoke puffs, oldest (highest) first so lower puffs draw over them
                for (int k = 0; k < PUFFS; k++)
                {
                    float ph = Mathf.Repeat(t * 0.045f + (PUFFS - 1 - k) / (float)PUFFS + s * 0.013f, 1f);
                    float rise = ph * COL_H;
                    var p = roof + Vector3.up * (0.4f + rise) + wind * (rise * rise / COL_H * 0.55f)
                          + new Vector3(Mathf.Sin(s + k * 1.7f + t * 0.3f), 0f, Mathf.Cos(s * 1.3f + k * 2.1f)) * (0.15f + ph * 0.8f);
                    float sz = (0.6f + ph * 3.4f) * f.Size * Mathf.Lerp(1f, far, 0.6f + ph * 0.4f);
                    var c = Color.Lerp(SMOKE, SMOKE_HI, ph * 0.8f);
                    c = Color.Lerp(c, FLAME_B, lights * (1f - ph) * (1f - ph) * 0.35f);   // underside lit by the fire at night
                    c.a = Mathf.Clamp01(ph / 0.08f) * (1f - ph) * (1f - ph) * 0.85f;
                    Bill(q++, p, camR, camU, sz, c);
                }

                // flames at the roof line (vertical billboards, flickering)
                for (int k = 0; k < FLAMES; k++)
                {
                    float o = (k - 1) * 0.45f * f.Size;
                    float h = (0.9f + 0.35f * Mathf.Sin(t * (11f + k * 3.7f) + s + k)) * f.Size * Mathf.Lerp(1f, far, 0.5f);
                    var c = Color.Lerp(FLAME_A, FLAME_B, 0.3f + 0.4f * Mathf.Sin(t * 6.1f + k + s) * 0.5f + 0.2f * k);
                    c.a = Mathf.Clamp01(0.75f * fl + 0.25f * lights);
                    var b = roof + hR * o - Vector3.up * 0.15f;
                    Upright(q++, b, hR, h * 0.55f, h, c);
                }
            }
            for (; q < MAX; q++) { int b = q * 4; _v[b] = _v[b + 1] = _v[b + 2] = _v[b + 3] = Vector3.zero; _c[b] = _c[b + 1] = _c[b + 2] = _c[b + 3] = Color.clear; }
            _mesh.vertices = _v; _mesh.colors = _c;
        }

        void Flat(int q, Vector3 p, float r, Color c)
        {
            int b = q * 4;
            _v[b] = p + new Vector3(-r, 0, -r); _v[b + 1] = p + new Vector3(r, 0, -r);
            _v[b + 2] = p + new Vector3(r, 0, r); _v[b + 3] = p + new Vector3(-r, 0, r);
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
            if (_mr != null) _mr.enabled = Time.frameCount - _frame <= 2 && Time.frameCount - FxView.Frame <= 2 && _fires.Count > 0;
        }

        /// <summary>White soft puff: radial falloff broken up by a few lumps so smoke reads as billows, not discs.</summary>
        static Texture2D MakePuff(int n)
        {
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave };
            var px = new Color32[n * n];
            var lumps = new Vector3[] { new Vector3(-0.30f, -0.15f, 0.55f), new Vector3(0.28f, -0.10f, 0.55f), new Vector3(0.0f, 0.25f, 0.6f), new Vector3(0.0f, -0.05f, 0.75f) };
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
                    float k = 0f;
                    foreach (var l in lumps)
                    {
                        float dx = u - l.x, dy = v - l.y;
                        float e = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy) / l.z);
                        k = Mathf.Max(k, e * e * (3f - 2f * e));
                    }
                    float edge = Mathf.Clamp01((1f - Mathf.Sqrt(u * u + v * v)) * 3f);
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(k * edge * 255f));
                }
            t.SetPixels32(px); t.Apply(false, true);
            return t;
        }

        void OnDestroy() { if (_mesh != null) Destroy(_mesh); if (_mat != null) Destroy(_mat); if (_tex != null) Destroy(_tex); }
    }
}
