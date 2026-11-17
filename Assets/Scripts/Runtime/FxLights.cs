// v0.15 graphics: night light pools. Up to 16 flat warm glow quads on the ground around camp fires, watchtowers,
// faction camp fires and lit lots (doors), the 16 nearest the camera, with a gentle flicker. Strength follows
// FxSky.Lights (0 by day). NO new shader: one dynamic mesh drawn with "Sprites/Default" (the same fallback MapView
// already uses, always shipped by Unity) and a procedural radial-gradient texture.
// Visual only: reads the sim (Game.S, LitLots, AllBases) and the map layout (MapGen), never writes to either.
using System.Collections.Generic;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed class FxLights : MonoBehaviour
    {
        const int MAX = 16;
        const float Y = 0.07f;                         // just above the ground plane
        static FxLights _inst;

        struct Src { public Vector2 P; public float R; public float K; public float Seed; }

        readonly List<Src> _all = new List<Src>();
        readonly List<Src> _near = new List<Src>(MAX);
        Mesh _mesh; Material _mat; Texture2D _tex; MeshRenderer _mr;
        readonly Vector3[] _v = new Vector3[MAX * 4];
        readonly Color[] _c = new Color[MAX * 4];
        float _scanT; int _frame = -10; float _str;
        Vector2 _focus;

        static readonly Color WARM = new Color(1.00f, 0.55f, 0.20f, 1f);
        static readonly Color LAMP = new Color(1.00f, 0.72f, 0.38f, 1f);

        /// <summary>Called from FxView.Apply every map frame. strength = FxSky light-pool strength (0..1).</summary>
        public static void Tick(Camera cam, Game g, float strength)
        {
            if (strength <= 0.01f || g?.S == null || cam == null) { if (_inst != null) _inst._str = 0f; return; }
            if (_inst == null)
            {
                var go = new GameObject("FxLights"); Object.DontDestroyOnLoad(go);
                _inst = go.AddComponent<FxLights>();
                if (!_inst.Init()) { Destroy(go); _inst = null; return; }
            }
            _inst.Run(cam, g, strength);
        }

        bool Init()
        {
            var sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("UI/Default");
            if (sh == null) return false;
            _tex = MakeGlow(64);
            _mat = new Material(sh) { mainTexture = _tex, renderQueue = 3000, hideFlags = HideFlags.DontSave };
            _mesh = new Mesh { name = "FxLightPools", hideFlags = HideFlags.DontSave };
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

        void Run(Camera cam, Game g, float strength)
        {
            _frame = Time.frameCount; _str = strength;
            // camera focus = where the view ray meets the ground
            var tr = cam.transform; var f = tr.forward;
            Vector3 hit = f.y < -0.05f ? tr.position + f * (-tr.position.y / f.y) : tr.position;
            var focus = new Vector2(hit.x, -hit.z);
            _scanT -= Time.unscaledDeltaTime;
            if (_scanT <= 0f || (focus - _focus).sqrMagnitude > 9f)
            {
                if (_scanT <= 0f) { _scanT = 1.0f; Scan(g); }
                _focus = focus; PickNearest();
            }

            float t = Time.unscaledTime;
            for (int i = 0; i < MAX; i++)
            {
                int b = i * 4;
                if (i >= _near.Count) { _v[b] = _v[b + 1] = _v[b + 2] = _v[b + 3] = Vector3.zero; _c[b] = _c[b + 1] = _c[b + 2] = _c[b + 3] = Color.clear; continue; }
                var s = _near[i];
                float fl = 0.86f + 0.08f * Mathf.Sin(t * 7.3f + s.Seed) + 0.06f * Mathf.Sin(t * 13.1f + s.Seed * 2.7f);
                float r = s.R * (0.97f + 0.03f * fl);
                var col = s.K > 0.9f ? WARM : LAMP;
                col.a = Mathf.Clamp01(strength * s.K * fl * 0.55f);
                var p = new Vector3(s.P.x, Y, -s.P.y);
                _v[b] = p + new Vector3(-r, 0, -r); _v[b + 1] = p + new Vector3(r, 0, -r);
                _v[b + 2] = p + new Vector3(r, 0, r); _v[b + 3] = p + new Vector3(-r, 0, r);
                _c[b] = _c[b + 1] = _c[b + 2] = _c[b + 3] = col;
            }
            _mesh.vertices = _v; _mesh.colors = _c;
        }

        void LateUpdate()
        {
            // hide outside the live map (menus, loading) and by day
            if (_mr != null) _mr.enabled = Time.frameCount - _frame <= 2 && Time.frameCount - FxView.Frame <= 2 && _str > 0.01f;
        }

        void Scan(Game g)
        {
            _all.Clear();
            var gen = Game.YardProvider?.Target as MapGen;   // the live map's generator (set by MapView)
            var S = g.S;
            try
            {
                foreach (var b in g.AllBases())
                {
                    if (gen != null && gen.Layout(b, out var lay))
                    {
                        Add(lay.Fire, 2.3f, 1f);
                        if (lay.Main) Add(lay.Tower, 1.3f, 0.7f);
                    }
                    else Add(new Vector2(b.Pos.x + 0.5f, b.Pos.y + 0.5f), 2.0f, 1f);
                }
                if (S.Camps != null && gen != null)
                    foreach (var c in S.Camps)
                        if (c.Alive && c.Lot >= 0 && gen.FactionLayoutOf(c, out var fl)) Add(fl.Fire, 2.1f, 1f);
                var d = Data.Instance;
                if (S.Towers != null && d != null)
                    foreach (var kv in S.Towers) Add(new Vector2(kv.Key % d.W + 0.5f, kv.Key / d.W + 0.5f), 1.3f, 0.7f);
                if (d != null)
                    foreach (var kv in g.LitLots())
                    {
                        if (kv.Key < 0 || kv.Key >= d.Lots.Count) continue;
                        var L = d.Lots[kv.Key];
                        Add(new Vector2(L.DoorX + 0.5f, L.DoorY + 0.5f), 1.6f, 0.6f);
                    }
            }
            catch (System.Exception) { /* visual only: a half-built map never breaks the frame */ }
        }

        void Add(Vector2 p, float r, float k)
        {
            for (int i = 0; i < _all.Count; i++) if ((_all[i].P - p).sqrMagnitude < 0.5f) return;   // no stacked pools
            _all.Add(new Src { P = p, R = r, K = k, Seed = (p.x * 12.9898f + p.y * 78.233f) % 6.2832f });
        }

        void PickNearest()
        {
            _near.Clear();
            var f = _focus;
            _all.Sort((a, b) => (a.P - f).sqrMagnitude.CompareTo((b.P - f).sqrMagnitude));
            for (int i = 0; i < _all.Count && _near.Count < MAX; i++) _near.Add(_all[i]);
        }

        /// <summary>White with a smooth radial alpha falloff (bright core, soft edge, clear corners).</summary>
        static Texture2D MakeGlow(int n)
        {
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
                    float k = Mathf.Clamp01(1f - Mathf.Sqrt(u * u + v * v)); k = k * k * (3f - 2f * k);
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(k * 255f));
                }
            t.SetPixels32(px); t.Apply(false, true);
            return t;
        }

        void OnDestroy() { if (_mesh != null) Destroy(_mesh); if (_mat != null) Destroy(_mat); if (_tex != null) Destroy(_tex); }
    }
}
