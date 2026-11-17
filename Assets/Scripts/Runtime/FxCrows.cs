// v0.15 graphics (idea #11): birds and debris. ~6 flocks of 4-8 crows perch on rooftops / the street near the camera
// view; when any live zombie comes within ~4 tiles a flock flaps off (2-frame wing flap), circles up and away, and
// resettles somewhere else near the view ~20 s later. ~20 scraps of paper tumble along the streets with the same wind
// FxFires drifts its smoke with, respawning upwind off-screen. Day and night (the FxView overlay handles the tint).
// ONE dynamic mesh (cap 68 quads), "Sprites/Default" + a procedural 4-cell atlas (perched / wings up / wings down /
// paper). NO new shader. Visual only: reads Game.Zc/Zh/ZPos, Data lots / tiles and MapGen.LotH, never writes the sim,
// and uses its own System.Random (never the sim RNG).
using System.Collections.Generic;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed class FxCrows : MonoBehaviour
    {
        const int FLOCKS = 6, BIRDS = 8, PAPERS = 20, MAX = FLOCKS * BIRDS + PAPERS;   // 68 quads (< 150 cap)
        const float SCARE_R = 4f, AWAY_S = 20f, FLY_S = 6f, LAND_S = 2.5f, VIEW_R = 22f;
        static FxCrows _inst;

        sealed class Flock
        {
            public int N, State;          // 0 perched, 1 flying off, 2 away, 3 landing
            public float T;               // time in state
            public Vector3 Home, Away;    // perch (world) / flee direction (horizontal, unit)
            public readonly Vector3[] Off = new Vector3[BIRDS];
            public readonly float[] Ph = new float[BIRDS];
            public readonly bool[] Flip = new bool[BIRDS];
            public float Fade;            // fade-in after a quiet relocation
        }
        struct Paper { public Vector3 P; public float Spd, Ph, Rot, Sz; }

        readonly Flock[] _f = new Flock[FLOCKS];
        readonly Paper[] _p = new Paper[PAPERS];
        Mesh _mesh; Material _mat; Texture2D _tex; MeshRenderer _mr;
        readonly Vector3[] _v = new Vector3[MAX * 4];
        readonly Color[] _c = new Color[MAX * 4];
        readonly Vector2[] _uv = new Vector2[MAX * 4];
        readonly System.Random _rng = new System.Random(911);
        int _frame = -10, _scanFrame = -100;
        bool _seeded;
        Vector2 _focus;

        static readonly Color CROW = new Color(0.07f, 0.07f, 0.08f, 1f);
        static readonly Color PAPER = new Color(0.90f, 0.89f, 0.84f, 1f);
        static readonly Vector3 WIND = new Vector3(0.8f, 0f, 0.35f).normalized;   // same wind as FxFires smoke

        /// <summary>Called from FxView.Apply every map frame.</summary>
        public static void Tick(Camera cam, Game g)
        {
            if (g?.S == null || cam == null) return;
            if (_inst == null)
            {
                var go = new GameObject("FxCrows"); Object.DontDestroyOnLoad(go);
                _inst = go.AddComponent<FxCrows>();
                if (!_inst.Init()) { Destroy(go); _inst = null; return; }
            }
            try { _inst.Run(cam, g); } catch (System.Exception) { /* visual only */ }
        }

        bool Init()
        {
            var sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("UI/Default");
            if (sh == null) return false;
            _tex = MakeAtlas(32);
            _mat = new Material(sh) { mainTexture = _tex, renderQueue = 3005, hideFlags = HideFlags.DontSave };
            _mesh = new Mesh { name = "FxCrows", hideFlags = HideFlags.DontSave };
            _mesh.MarkDynamic();
            var tri = new int[MAX * 6];
            for (int i = 0; i < MAX; i++)
            {
                int b = i * 4, t = i * 6;
                tri[t] = b; tri[t + 1] = b + 2; tri[t + 2] = b + 1; tri[t + 3] = b; tri[t + 4] = b + 3; tri[t + 5] = b + 2;
            }
            _mesh.vertices = _v; _mesh.uv = _uv; _mesh.colors = _c; _mesh.triangles = tri;
            _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
            gameObject.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _mr = gameObject.AddComponent<MeshRenderer>();
            _mr.sharedMaterial = _mat;
            _mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _mr.receiveShadows = false;
            for (int i = 0; i < FLOCKS; i++) _f[i] = new Flock();
            return true;
        }

        float R01() => (float)_rng.NextDouble();

        /// <summary>Tile-space point where the camera's centre ray meets the ground.</summary>
        static Vector2 FocusOf(Camera cam)
        {
            var tr = cam.transform; var d = tr.forward; var o = tr.position;
            float t = d.y < -0.05f ? -o.y / d.y : 30f;
            var w = o + d * Mathf.Min(t, 200f);
            return new Vector2(w.x, -w.z);
        }

        Vector3 PickPerch(Vector2 near, float rMin, float rMax)
        {
            var d = Data.Instance; var gen = Game.YardProvider?.Target as MapGen;
            for (int tries = 0; tries < 12; tries++)
            {
                float a = R01() * 6.283f, r = Mathf.Lerp(rMin, rMax, R01());
                var p = near + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
                if (d != null && gen != null && gen.LotH != null && R01() < 0.6f)
                {
                    // rooftop: the lot under / nearest this point
                    for (int l = 0; l < d.Lots.Count && l < gen.LotH.Length; l++)
                    {
                        var L = d.Lots[l];
                        if (p.x < L.X || p.y < L.Y || p.x > L.X + L.W || p.y > L.Y + L.H || gen.LotH[l] < 0.8f) continue;
                        float x = L.X + L.W * (0.25f + R01() * 0.5f), y = L.Y + L.H * (0.25f + R01() * 0.5f);
                        return new Vector3(x, gen.LotH[l] + 0.02f, -y);
                    }
                }
                if (d != null && d.W > 0)
                {
                    int tx = Mathf.FloorToInt(p.x), ty = Mathf.FloorToInt(p.y);
                    if (tx < 0 || ty < 0 || tx >= d.W || ty * d.W + tx >= d.Tiles.Length) continue;
                }
                return new Vector3(p.x, 0.02f, -p.y);
            }
            return new Vector3(near.x, 0.02f, -near.y);
        }

        void Settle(Flock f, Vector3 home, bool fade)
        {
            f.Home = home; f.State = 0; f.T = R01() * 5f; f.Fade = fade ? 0f : 1f;
            f.N = 4 + _rng.Next(5);
            for (int i = 0; i < BIRDS; i++)
            {
                f.Off[i] = new Vector3((R01() - 0.5f) * 2.0f, 0f, (R01() - 0.5f) * 1.4f);
                f.Ph[i] = R01() * 50f; f.Flip[i] = R01() < 0.5f;
            }
        }

        bool Road(Vector2 p)
        {
            var d = Data.Instance; if (d == null || d.W <= 0) return true;
            int x = Mathf.FloorToInt(p.x), y = Mathf.FloorToInt(p.y), k = y * d.W + x;
            if (x < 0 || y < 0 || x >= d.W || k >= d.Tiles.Length) return false;
            int t = d.Tiles[k];
            return t == Data.T_ROAD || t == Data.T_AVE || t == Data.T_BRIDGE;
        }

        void SpawnPaper(int i, bool anywhere)
        {
            var side = new Vector2(-WIND.z, WIND.x);   // perpendicular to the wind, tile space (x, -z)
            var up = new Vector2(WIND.x, -WIND.z);
            Vector2 p = _focus;
            for (int tries = 0; tries < 8; tries++)
            {
                float along = anywhere ? (R01() * 2f - 1f) * VIEW_R : -VIEW_R * (0.9f + R01() * 0.2f);
                p = _focus + up * along + side * ((R01() * 2f - 1f) * VIEW_R);
                if (Road(p)) break;
            }
            _p[i] = new Paper { P = new Vector3(p.x, 0.05f, -p.y), Spd = 0.8f + R01() * 1.6f, Ph = R01() * 40f, Rot = R01() * 6.28f, Sz = 0.16f + R01() * 0.12f };
        }

        void Run(Camera cam, Game g)
        {
            _frame = Time.frameCount;
            float dt = Mathf.Min(Time.deltaTime, 0.1f), t = Time.time;
            _focus = FocusOf(cam);
            if (!_seeded)
            {
                _seeded = true;
                for (int i = 0; i < FLOCKS; i++) Settle(_f[i], PickPerch(_focus, 3f, VIEW_R * 0.8f), true);
                for (int i = 0; i < PAPERS; i++) SpawnPaper(i, true);
            }

            // zombie proximity scan (view only), every ~8 frames
            bool scan = Time.frameCount - _scanFrame >= 8;
            if (scan) _scanFrame = Time.frameCount;
            int nz = g.Zc.Count;
            for (int fi = 0; fi < FLOCKS; fi++)
            {
                var f = _f[fi];
                f.T += dt; f.Fade = Mathf.Min(1f, f.Fade + dt * 0.8f);
                var hp = new Vector2(f.Home.x, -f.Home.z);
                if (f.State == 0)
                {
                    if ((hp - _focus).sqrMagnitude > VIEW_R * VIEW_R * 2.2f) { Settle(f, PickPerch(_focus, 6f, VIEW_R * 0.8f), true); continue; }   // camera moved on
                    if (scan)
                        for (int i = 0; i < nz && i < g.Zh.Count; i++)
                        {
                            if (g.Zh[i] == 255) continue;
                            var zd = hp - g.ZPos(i);
                            if (zd.sqrMagnitude > SCARE_R * SCARE_R) continue;
                            if (zd.sqrMagnitude < 1e-3f) zd = new Vector2(1f, 0f);
                            zd.Normalize();
                            f.Away = new Vector3(zd.x, 0f, -zd.y); f.State = 1; f.T = 0f;
                            break;
                        }
                }
                else if (f.State == 1 && f.T > FLY_S) { f.State = 2; }
                else if (f.State == 2 && f.T > AWAY_S)
                {
                    Settle(f, PickPerch(_focus, 4f, VIEW_R * 0.8f), false);
                    f.State = 3; f.T = 0f;
                }
                else if (f.State == 3 && f.T > LAND_S) { f.State = 0; f.T = 0f; }
            }

            var tr = cam.transform; var camR = tr.right; var camU = tr.up;
            int q = 0;
            for (int fi = 0; fi < FLOCKS; fi++)
            {
                var f = _f[fi];
                float far = Mathf.Clamp(Vector3.Distance(tr.position, f.Home) / 40f, 1f, 2.2f);
                for (int i = 0; i < f.N; i++)
                {
                    float ph = f.Ph[i];
                    Vector3 p; int frame; float sz; float a = f.Fade;
                    if (f.State == 0)
                    {
                        // perched: an occasional hop / turn, otherwise still
                        float hop = Mathf.Max(0f, Mathf.Sin(t * 0.9f + ph) - 0.93f) * 3f;
                        if (Mathf.Repeat(t * 0.13f + ph, 1f) < dt * 0.13f) f.Flip[i] = !f.Flip[i];
                        p = f.Home + f.Off[i] + Vector3.up * hop;
                        frame = hop > 0.02f ? 1 : 0; sz = 0.32f * far;
                    }
                    else if (f.State == 3)
                    {
                        // landing: spiral down from above onto the new perch
                        float k = Mathf.Clamp01(f.T / LAND_S), e = 1f - k;
                        float ang = ph + t * 2.4f;
                        p = f.Home + f.Off[i] + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * (2.2f * e) + Vector3.up * (9f * e * e);
                        frame = k > 0.95f ? 0 : ((int)(t * 9f + ph) & 1) + 1; sz = Mathf.Lerp(0.5f, 0.32f, k) * far;
                        a = Mathf.Clamp01(k * 3f);
                    }
                    else
                    {
                        // flying off: burst up, then circle up and away; fade out once well clear
                        float ft = f.T;
                        float ang = ph + ft * (1.6f + (i & 1) * 0.5f);
                        float rad = 0.6f + Mathf.Min(ft, 4f) * 0.5f;
                        p = f.Home + f.Off[i] * (1f + ft * 0.3f) + f.Away * (ft * 2.2f)
                          + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * rad
                          + Vector3.up * (Mathf.Min(ft, 0.6f) * 3f + ft * 1.6f + 0.25f * Mathf.Sin(ft * 5f + ph));
                        frame = ((int)(t * 10f + ph * 3f) & 1) + 1; sz = 0.5f * far;
                        a = f.State == 2 ? 0f : Mathf.Clamp01((FLY_S - ft) / 2f);
                    }
                    var c = CROW; c.a = a;
                    Bill(q++, p + Vector3.up * sz * 0.4f, camR, camU, sz, c, frame, f.Flip[i]);
                }
            }

            // paper: drift downwind along the streets, tumbling; respawn upwind when it leaves the view
            var side = new Vector3(-WIND.z, 0f, WIND.x);
            for (int i = 0; i < PAPERS; i++)
            {
                var pp = _p[i];
                float gust = 0.55f + 0.45f * Mathf.Sin(t * 0.7f + pp.Ph) * Mathf.Sin(t * 0.31f + pp.Ph * 0.5f);
                pp.P += (WIND * pp.Spd * Mathf.Max(0.1f, gust) + side * Mathf.Sin(t * 1.3f + pp.Ph) * 0.3f) * dt;
                pp.Rot += dt * (2f + pp.Spd * 2.5f) * gust;
                _p[i] = pp;
                float h = 0.04f + Mathf.Abs(Mathf.Sin(t * 1.7f + pp.Ph)) * 0.5f * gust;
                var tp = new Vector2(pp.P.x, -pp.P.z);
                if ((tp - _focus).sqrMagnitude > VIEW_R * VIEW_R * 1.5f) { SpawnPaper(i, false); continue; }
                var ax = new Vector3(Mathf.Cos(pp.Rot), 0f, Mathf.Sin(pp.Rot));
                var ay = Quaternion.AngleAxis(Mathf.Sin(pp.Rot * 0.7f + pp.Ph) * 80f, ax) * Vector3.Cross(ax, Vector3.up);
                var c = PAPER; c.a = 0.9f;
                Quad(q++, pp.P + Vector3.up * h, ax * pp.Sz, ay * pp.Sz * 0.75f, c, 3, false);
            }

            for (; q < MAX; q++) { int b = q * 4; _v[b] = _v[b + 1] = _v[b + 2] = _v[b + 3] = Vector3.zero; _c[b] = _c[b + 1] = _c[b + 2] = _c[b + 3] = Color.clear; }
            _mesh.vertices = _v; _mesh.colors = _c; _mesh.uv = _uv;
        }

        void Bill(int q, Vector3 p, Vector3 r, Vector3 u, float sz, Color c, int cell, bool flip)
            => Quad(q, p, r * sz * 0.5f, u * sz * 0.5f, c, cell, flip);

        void Quad(int q, Vector3 p, Vector3 r, Vector3 u, Color c, int cell, bool flip)
        {
            int b = q * 4;
            _v[b] = p - r - u; _v[b + 1] = p + r - u; _v[b + 2] = p + r + u; _v[b + 3] = p - r + u;
            _c[b] = _c[b + 1] = _c[b + 2] = _c[b + 3] = c;
            float u0 = cell * 0.25f + 0.004f, u1 = cell * 0.25f + 0.246f;
            if (flip) { float s = u0; u0 = u1; u1 = s; }
            _uv[b] = new Vector2(u0, 0f); _uv[b + 1] = new Vector2(u1, 0f); _uv[b + 2] = new Vector2(u1, 1f); _uv[b + 3] = new Vector2(u0, 1f);
        }

        void LateUpdate()
        {
            if (_mr != null) _mr.enabled = Time.frameCount - _frame <= 2 && Time.frameCount - FxView.Frame <= 2;
        }

        /// <summary>4 cells of n x n, white silhouettes: crow perched, wings up, wings down, paper scrap.</summary>
        static Texture2D MakeAtlas(int n)
        {
            var t = new Texture2D(n * 4, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave };
            var px = new Color32[n * 4 * n];
            for (int cell = 0; cell < 4; cell++)
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;   // -1..1, v up
                        float a = 0f;
                        if (cell == 0)
                        {
                            // perched side view: body ellipse, head front-up, beak, tail back-down, legs
                            a = Mathf.Max(a, Ell(u, v, 0.0f, -0.15f, 0.45f, 0.30f));
                            a = Mathf.Max(a, Ell(u, v, 0.42f, 0.18f, 0.20f, 0.19f));
                            a = Mathf.Max(a, Tri(u, v, new Vector2(0.55f, 0.22f), new Vector2(0.80f, 0.14f), new Vector2(0.55f, 0.10f)));
                            a = Mathf.Max(a, Tri(u, v, new Vector2(-0.30f, -0.05f), new Vector2(-0.85f, -0.50f), new Vector2(-0.25f, -0.35f)));
                            if (Mathf.Abs(u - 0.05f) < 0.04f && v < -0.4f && v > -0.75f) a = 1f;
                        }
                        else if (cell == 3)
                        {
                            // paper scrap: rectangle with a torn corner and faint ruled lines
                            if (Mathf.Abs(u) < 0.85f && Mathf.Abs(v) < 0.65f && u + v < 1.1f)
                                a = Mathf.Abs(Mathf.Repeat(v * 5f, 1f) - 0.5f) < 0.06f ? 0.75f : 1f;
                        }
                        else
                        {
                            // flying, seen from below: body + head, wings up (cell 1) or down (cell 2)
                            a = Mathf.Max(a, Ell(u, v, 0f, 0f, 0.42f, 0.14f));
                            a = Mathf.Max(a, Ell(u, v, 0.45f, 0.02f, 0.14f, 0.12f));
                            float wy = cell == 1 ? 0.75f : -0.65f;
                            a = Mathf.Max(a, Tri(u, v, new Vector2(-0.18f, 0.05f), new Vector2(0.15f, 0.05f), new Vector2(-0.35f, wy)));
                            a = Mathf.Max(a, Tri(u, v, new Vector2(-0.18f, 0.0f), new Vector2(0.15f, 0.0f), new Vector2(0.05f, wy * 0.85f)));
                            a = Mathf.Max(a, Tri(u, v, new Vector2(-0.3f, 0.02f), new Vector2(-0.85f, 0.15f), new Vector2(-0.85f, -0.12f)));
                        }
                        px[y * n * 4 + cell * n + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(a) * 255f));
                    }
            t.SetPixels32(px); t.Apply(false, true);
            return t;
        }

        static float Ell(float u, float v, float cx, float cy, float rx, float ry)
        {
            float dx = (u - cx) / rx, dy = (v - cy) / ry;
            return Mathf.Clamp01((1f - (dx * dx + dy * dy)) * 4f);
        }

        static float Tri(float u, float v, Vector2 a, Vector2 b, Vector2 c)
        {
            var p = new Vector2(u, v);
            float d1 = Cr(p, a, b), d2 = Cr(p, b, c), d3 = Cr(p, c, a);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            return neg && pos ? 0f : 1f;
        }
        static float Cr(Vector2 p, Vector2 a, Vector2 b) => (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);

        void OnDestroy() { if (_mesh != null) Destroy(_mesh); if (_mat != null) Destroy(_mat); if (_tex != null) Destroy(_tex); }
    }
}
