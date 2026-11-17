// v0.15 graphics: gunfire light. Crowd.cs already draws the muzzle-flash geometry and the tracer on the
// shooting survivor (Crowd.OnSfx / MuzzleFx); this adds what that hard-edged mesh cannot: a soft glow that
// pops at the gun for ~0.12 s plus a warm splash of light on the ground around the shooter (most visible at
// night), and a faint tracer streak that lingers a little longer than Crowd's 0.06 s one, fired in the
// a random bearing (the sim event carries no target; Crowd's own tracer already aims at the nearest zombie).
// Driven by the sim's own shot event, Game.SfxRequested("gunshot" / "rifle", tilePos); NO new shader: one
// dynamic mesh with "Sprites/Default" (as FxLights). Visual only, UnityEngine.Random (never the sim RNG).
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed class FxMuzzle : MonoBehaviour
    {
        const int MAX = 24;                       // live pops (quads per pop: glow, ground splash, tracer)
        const float LIFE = 0.12f, TRACE_LIFE = 0.10f;
        static readonly Color GLOW = new Color(1f, 0.82f, 0.45f, 1f), SPLASH = new Color(1f, 0.62f, 0.25f, 1f), TRACE = new Color(1f, 0.88f, 0.55f, 1f);

        static FxMuzzle _inst;
        static Game _hooked;
        struct Pop { public Vector2 P, Dir; public float T; public bool Rifle; }
        readonly Pop[] _pops = new Pop[MAX];
        int _next, _frame = -10;
        Mesh _mesh; Material _mat; Texture2D _tex; MeshRenderer _mr;
        readonly Vector3[] _v = new Vector3[MAX * 12];
        readonly Color[] _c = new Color[MAX * 12];
        Camera _cam;

        /// <summary>Called from FxView.Apply every map frame: (re)subscribes to the live game's shot event.</summary>
        public static void Tick(Camera cam, Game g)
        {
            if (_inst == null)
            {
                var go = new GameObject("FxMuzzle"); Object.DontDestroyOnLoad(go);
                _inst = go.AddComponent<FxMuzzle>();
                if (!_inst.Init()) { Destroy(go); _inst = null; return; }
            }
            if (!ReferenceEquals(_hooked, g))
            {
                if (_hooked != null) _hooked.SfxRequested -= OnSfx;
                _hooked = g;
                if (g != null) g.SfxRequested += OnSfx;
            }
            _inst._cam = cam; _inst._frame = Time.frameCount;
        }

        static void OnSfx(string name, Vector2 at)
        {
            if (_inst == null || at.x < 0f || (name != "gunshot" && name != "rifle")) return;
            if (Time.frameCount - _inst._frame > 2) return;
            var cam = _inst._cam;
            if (cam != null)
            {
                var vp = cam.WorldToViewportPoint(Geo.W(at, 1f));
                if (vp.z <= 0f || vp.x < -0.2f || vp.x > 1.2f || vp.y < -0.2f || vp.y > 1.2f) return;   // off screen
            }
            float a = Random.value * 6.2832f;
            _inst._pops[_inst._next] = new Pop { P = at, Dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a)), T = Mathf.Max(LIFE, TRACE_LIFE), Rifle = name == "rifle" };
            _inst._next = (_inst._next + 1) % MAX;
        }

        bool Init()
        {
            var sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("UI/Default");
            if (sh == null) return false;
            _tex = MakeGlow(32);
            _mat = new Material(sh) { mainTexture = _tex, renderQueue = 3010, hideFlags = HideFlags.DontSave };
            _mesh = new Mesh { name = "FxMuzzle", hideFlags = HideFlags.DontSave };
            _mesh.MarkDynamic();
            var uv = new Vector2[MAX * 12]; var tri = new int[MAX * 18];
            for (int q = 0; q < MAX * 3; q++)
            {
                int b = q * 4, t = q * 6;
                bool tracer = q % 3 == 2;   // tracer quads sample the glow's bright middle row so they stay solid
                uv[b] = new Vector2(0, tracer ? 0.5f : 0); uv[b + 1] = new Vector2(1, tracer ? 0.5f : 0); uv[b + 2] = new Vector2(1, tracer ? 0.5f : 1); uv[b + 3] = new Vector2(0, tracer ? 0.5f : 1);
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

        void LateUpdate()
        {
            bool live = Time.frameCount - _frame <= 2 && Time.frameCount - FxView.Frame <= 2 && _cam != null;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            bool any = false;
            var camPos = _cam != null ? _cam.transform.position : Vector3.zero;
            float night = Mathf.Clamp01(FxView.Tint.a / 0.45f);   // ground splash reads strongest in the dark
            for (int i = 0; i < MAX; i++)
            {
                ref var p = ref _pops[i];
                int b = i * 12;
                if (p.T > 0f) p.T -= dt;
                if (p.T <= 0f || !live) { for (int k = 0; k < 12; k++) { _v[b + k] = Vector3.zero; _c[b + k] = Color.clear; } continue; }
                any = true;
                float age = Mathf.Max(LIFE, TRACE_LIFE) - p.T;
                float g = Mathf.Clamp01(1f - age / LIFE); g *= g;
                var muzzle = Geo.W(p.P, 1.05f * MapGen.MT);
                // 1) camera-facing glow
                var toCam = (camPos - muzzle).normalized;
                var r = Vector3.Cross(Vector3.up, toCam).normalized; if (r.sqrMagnitude < 0.01f) r = Vector3.right;
                var u = Vector3.Cross(toCam, r);
                float s = (p.Rifle ? 0.75f : 0.6f) * (0.7f + 0.3f * g);
                _v[b] = muzzle - r * s - u * s; _v[b + 1] = muzzle + r * s - u * s; _v[b + 2] = muzzle + r * s + u * s; _v[b + 3] = muzzle - r * s + u * s;
                var cg = GLOW; cg.a = 0.85f * g;
                _c[b] = _c[b + 1] = _c[b + 2] = _c[b + 3] = cg;
                // 2) ground light splash
                var gp = Geo.W(p.P, 0.06f); float gr = (p.Rifle ? 1.9f : 1.5f);
                _v[b + 4] = gp + new Vector3(-gr, 0, -gr); _v[b + 5] = gp + new Vector3(gr, 0, -gr); _v[b + 6] = gp + new Vector3(gr, 0, gr); _v[b + 7] = gp + new Vector3(-gr, 0, gr);
                var cs = SPLASH; cs.a = (0.18f + 0.32f * night) * g;
                _c[b + 4] = _c[b + 5] = _c[b + 6] = _c[b + 7] = cs;
                // 3) tracer streak: a short bright dash flying out along Dir
                float tk = Mathf.Clamp01(age / TRACE_LIFE);
                if (tk < 1f)
                {
                    var d3 = new Vector3(p.Dir.x, 0f, -p.Dir.y);
                    float range = p.Rifle ? 9f : 6f;
                    var h = muzzle + d3 * (0.4f + range * tk); var tl = muzzle + d3 * (0.4f + range * Mathf.Max(0f, tk - 0.35f));
                    var side = Vector3.Cross(d3, (camPos - h).normalized).normalized * 0.05f;
                    _v[b + 8] = tl - side; _v[b + 9] = tl + side; _v[b + 10] = h + side; _v[b + 11] = h - side;
                    var ct = TRACE; ct.a = 0.8f * (1f - tk);
                    var ctt = ct; ctt.a = 0f;
                    _c[b + 8] = _c[b + 9] = ctt; _c[b + 10] = _c[b + 11] = ct;
                }
                else { for (int k = 8; k < 12; k++) { _v[b + k] = Vector3.zero; _c[b + k] = Color.clear; } }
            }
            if (_mr != null) _mr.enabled = any;
            if (any || _wasAny) { _mesh.vertices = _v; _mesh.colors = _c; }
            _wasAny = any;
        }
        bool _wasAny;

        static Texture2D MakeGlow(int n)
        {
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                    float k = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy)); k = k * k;
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(k * 255f));
                }
            t.SetPixels32(px); t.Apply(false, true);
            return t;
        }

        void OnDestroy()
        {
            if (_hooked != null) { _hooked.SfxRequested -= OnSfx; _hooked = null; }
            if (_mat != null) Destroy(_mat); if (_tex != null) Destroy(_tex); if (_mesh != null) Destroy(_mesh);
            if (_inst == this) _inst = null;
        }
    }
}
