// Procedural 3D city map, generated entirely at runtime from the ported map data (no editor
// assets beyond Resources/Shaders/DayZeroMap.shader). Port of the look of view3d.gd, restyled as
// a ruined, overgrown low-poly city:
//   * ground: one textured plane, texture painted from Data.Tiles (2 px per tile) with grass,
//     dirt patches, water, cracked/weedy asphalt, paving, fields
//   * buildings: one box (or gable house) per lot, oriented by Data.LotAngle like Godot's _quad,
//     height by kind like Godot's _lot_height, desaturated palette, window bands, roof clutter;
//     some burnt (dark, lower, rubble) or collapsed (rubble heap)
//   * trees, wrecked cars, rubble; combined into one mesh per 64x64-tile chunk, vertex colours,
//     lighting baked into the colours; one unlit material + distance haze
//   * dynamic layer: camp marker, converted lots (green roof caps) and lit lots (yellow / red)
//   * touch camera: one-finger pan, pinch zoom, two-finger twist; Godot FOV 40, pitch 52
// World space: tile (x, y) -> Vector3(x, height, -y), 1 unit = 1 tile.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using DayZero.Core;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace DayZero.Runtime
{
    public sealed class MapLabel
    {
        public Vector3 Pos;
        public string Text;
        public int Tier;          // 0 landmark, 1 minor landmark, 2 district
    }

    public sealed class MapView : MonoBehaviour
    {
        public static MapView Instance { get; private set; }

        const int CH = 64;              // chunk size in tiles (Godot CH)
        const float FL = 0.4f;          // floor height (Godot FL)
        const float FOV = 40f, PITCH = 52f;
        const float MIN_DIST = 14f, MAX_DIST = 230f;

        public bool Ready { get; private set; }
        public bool Building { get; private set; }
        public float Progress { get; private set; }
        public readonly List<MapLabel> Labels = new List<MapLabel>();

        Camera _cam;
        Light _sun;
        Material _objMat, _groundMat;
        Texture2D _groundTex;
        GameObject _root, _dyn;
        readonly List<GameObject> _chunks = new List<GameObject>();
        readonly List<Vector3> _chunkCenters = new List<Vector3>();
        string _builtKey = "";
        float[] _lotH = new float[0];

        // camera state
        Vector3 _focus, _focusTarget;
        float _dist = 55f, _distTarget = 55f, _yaw = 30f;
        bool _animFocus;
        bool _drag; Vector2 _last;
        bool _pinch; float _pinchD, _pinchA; Vector2 _pinchMid;

        // dynamic layer
        string _dynKey = "";
        float _dynT;

        static readonly Color FOG = new Color(0.78f, 0.74f, 0.66f);
        static readonly Vector3 SUN = new Vector3(-0.55f, 0.72f, -0.42f).normalized;

        void Awake()
        {
            Instance = this;
            Input.simulateMouseWithTouches = false;
        }

        Data D => Data.Instance;
        Game G => Bootstrap.Instance != null ? Bootstrap.Instance.Game : null;

        // ================= public API (GameUI) =================
        public void ZoomIn() { _distTarget = Mathf.Clamp(_distTarget / 1.4f, MIN_DIST, MAX_DIST); }
        public void ZoomOut() { _distTarget = Mathf.Clamp(_distTarget * 1.4f, MIN_DIST, MAX_DIST); }
        public void Overview() { _distTarget = _distTarget > 150f ? 55f : MAX_DIST; }
        public float Dist => _dist;

        public void Home()
        {
            var p = HomeTile();
            _focusTarget = new Vector3(p.x, 0f, -p.y);
            _animFocus = true;
            _distTarget = 55f;
        }

        /// <summary>World -> IMGUI screen position (top-left origin). False when behind the camera or off screen.</summary>
        public bool TryScreen(Vector3 world, out Vector2 gui)
        {
            gui = Vector2.zero;
            if (_cam == null) return false;
            var s = _cam.WorldToScreenPoint(world);
            if (s.z <= 0.1f) return false;
            gui = new Vector2(s.x, UnityEngine.Screen.height - s.y);
            return gui.x > -200 && gui.x < UnityEngine.Screen.width + 200 && gui.y > -60 && gui.y < UnityEngine.Screen.height + 60;
        }

        Vector2 HomeTile()
        {
            var g = G;
            if (g?.C != null)
                foreach (var b in g.C.Bases) if (b.Main) return new Vector2(b.Pos.x + 0.5f, b.Pos.y + 0.5f);
            if (g?.C != null && g.C.Bases.Count > 0) return new Vector2(g.C.Bases[0].Pos.x, g.C.Bases[0].Pos.y);
            var d = D;
            return new Vector2(d.CityRect.x + d.CityRect.width * 0.5f, d.CityRect.y + d.CityRect.height * 0.5f);
        }

        // ================= frame =================
        void Update()
        {
            var b = Bootstrap.Instance;
            bool playing = b != null && b.State == Bootstrap.Screen2.Playing && b.Game?.S != null;
            if (_root != null) _root.SetActive(playing && Ready);
            if (_dyn != null) _dyn.SetActive(playing && Ready);
            if (!playing) return;

            string key = D.WorldSeed + ":" + D.Lots.Count + ":" + D.W + "x" + D.H;
            if (key != _builtKey && !Building) StartCoroutine(Build(key));
            if (!Ready) return;

            EnsureCamera();
            HandleInput(b);
            UpdateCamera();
            CullChunks();
            _dynT -= Time.unscaledDeltaTime;
            if (_dynT <= 0f) { _dynT = 0.75f; UpdateDynamic(); }
        }

        void EnsureCamera()
        {
            if (_cam == null) _cam = Camera.main != null ? Camera.main : (Camera.allCamerasCount > 0 ? Camera.allCameras[0] : null);
            if (_cam == null)
            {
                var go = new GameObject("Map Camera"); go.tag = "MainCamera";
                _cam = go.AddComponent<Camera>(); DontDestroyOnLoad(go);
            }
            _cam.orthographic = false;
            _cam.fieldOfView = FOV;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = FOG;
            if (_sun == null)
            {
                var lg = new GameObject("Sun");
                lg.transform.SetParent(transform, false);
                _sun = lg.AddComponent<Light>();
                _sun.type = LightType.Directional;
                _sun.color = new Color(1f, 0.88f, 0.70f);
                _sun.intensity = 1.1f;
                _sun.shadows = LightShadows.None;
                lg.transform.rotation = Quaternion.LookRotation(-SUN);
                RenderSettings.ambientLight = new Color(0.55f, 0.53f, 0.50f);
            }
        }

        // ================= input =================
        static Vector2 TopLeft(Vector2 p) => new Vector2(p.x, UnityEngine.Screen.height - p.y);

        void HandleInput(Bootstrap b)
        {
            var ui = b.UI;
            if (ui != null && ui.ModalOpen) { _drag = false; _pinch = false; return; }
            int tc = Input.touchCount;
            if (tc >= 2)
            {
                var t0 = Input.GetTouch(0); var t1 = Input.GetTouch(1);
                Vector2 mid = (t0.position + t1.position) * 0.5f;
                float d = Mathf.Max(1f, Vector2.Distance(t0.position, t1.position));
                float a = Mathf.Atan2(t1.position.y - t0.position.y, t1.position.x - t0.position.x) * Mathf.Rad2Deg;
                if (!_pinch)
                {
                    // start a pinch only if the gesture did not begin on the HUD
                    bool blocked = ui != null && (ui.BlocksPointer(TopLeft(t0.position)) || ui.BlocksPointer(TopLeft(t1.position)));
                    if (blocked && !_drag) return;
                    _pinch = true; _drag = false;
                    _pinchD = d; _pinchA = a; _pinchMid = mid;
                    return;
                }
                _distTarget = _dist = Mathf.Clamp(_dist * (_pinchD / d), MIN_DIST, MAX_DIST);
                _yaw += Mathf.DeltaAngle(a, _pinchA);
                PlaceCamera();
                Pan(_pinchMid, mid);
                _pinchD = d; _pinchA = a; _pinchMid = mid;
                return;
            }
            if (tc == 1)
            {
                var t = Input.GetTouch(0);
                if (_pinch) { _pinch = false; _drag = true; _last = t.position; return; } // second finger lifted
                if (t.phase == TouchPhase.Began)
                {
                    _drag = ui == null || !ui.BlocksPointer(TopLeft(t.position));
                    _last = t.position;
                }
                else if (_drag && (t.phase == TouchPhase.Moved || t.phase == TouchPhase.Stationary))
                {
                    Pan(_last, t.position);
                    _last = t.position;
                }
                if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled) _drag = false;
                return;
            }
            _pinch = false;
            // mouse (editor / desktop)
            Vector2 mp = Input.mousePosition;
            if (Input.GetMouseButtonDown(0)) { _drag = ui == null || !ui.BlocksPointer(TopLeft(mp)); _last = mp; }
            else if (_drag && Input.GetMouseButton(0)) { Pan(_last, mp); _last = mp; }
            if (Input.GetMouseButtonUp(0)) _drag = false;
            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) > 0.01f) _distTarget = Mathf.Clamp(_distTarget * Mathf.Pow(0.88f, wheel), MIN_DIST, MAX_DIST);
            if (Input.touchCount == 0 && !Input.GetMouseButton(0)) _drag = false;
        }

        bool Ground(Vector2 screen, out Vector3 hit)
        {
            hit = Vector3.zero;
            var ray = _cam.ScreenPointToRay(new Vector3(screen.x, screen.y, 0f));
            var plane = new Plane(Vector3.up, Vector3.zero);
            if (!plane.Raycast(ray, out float t) || t > 5000f) return false;
            hit = ray.GetPoint(t);
            return true;
        }

        void Pan(Vector2 from, Vector2 to)
        {
            if ((from - to).sqrMagnitude < 0.01f) return;
            if (!Ground(from, out var a) || !Ground(to, out var c)) return;
            _focus += a - c;
            _focus = ClampFocus(_focus);
            _focusTarget = _focus;
            _animFocus = false;
            PlaceCamera();
        }

        Vector3 ClampFocus(Vector3 f)
        {
            var d = D;
            f.x = Mathf.Clamp(f.x, 0f, d.W);
            f.z = Mathf.Clamp(f.z, -d.H, 0f);
            f.y = 0f;
            return f;
        }

        void UpdateCamera()
        {
            float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime * 10f);
            _dist = Mathf.Lerp(_dist, _distTarget, k);
            if (_animFocus)
            {
                _focus = Vector3.Lerp(_focus, _focusTarget, k);
                if ((_focus - _focusTarget).sqrMagnitude < 0.01f) { _focus = _focusTarget; _animFocus = false; }
            }
            _focus = ClampFocus(_focus);
            PlaceCamera();
            Shader.SetGlobalColor("_DZFogColor", FOG);
            Shader.SetGlobalVector("_DZFogParams", new Vector4(_dist * 1.05f, _dist * 3.4f + 40f, 0.9f, 0f));
        }

        void PlaceCamera()
        {
            if (_cam == null) return;
            var rot = Quaternion.Euler(PITCH, _yaw, 0f);
            _cam.transform.rotation = rot;
            _cam.transform.position = _focus - rot * Vector3.forward * _dist;
            _cam.nearClipPlane = Mathf.Max(0.3f, _dist * 0.05f);
            _cam.farClipPlane = _dist * 4.5f + 250f;
        }

        void CullChunks()
        {
            float r = _dist * 3.2f + CH * 1.5f, r2 = r * r;
            for (int i = 0; i < _chunks.Count; i++)
            {
                var c = _chunkCenters[i];
                float dx = c.x - _focus.x, dz = c.z - _focus.z;
                bool on = dx * dx + dz * dz < r2;
                if (_chunks[i].activeSelf != on) _chunks[i].SetActive(on);
            }
        }

        // ================= build =================
        IEnumerator Build(string key)
        {
            Building = true; Ready = false; Progress = 0f;
            var sw = Stopwatch.StartNew();
            Clear();
            _root = new GameObject("Map");
            _root.transform.SetParent(transform, false);
            EnsureMaterials();
            var d = D;

            // --- lot heights ---
            _lotH = new float[d.Lots.Count];
            for (int l = 0; l < d.Lots.Count; l++) _lotH[l] = LotHeight(l);

            // --- ground texture, painted in row bands across frames ---
            int tw = d.W * 2, th = d.H * 2;
            var px = new Color32[tw * th];
            for (int ty = 0; ty < d.H; ty++)
            {
                PaintRow(d, ty, px, tw, th);
                if (sw.ElapsedMilliseconds > 14) { Progress = 0.25f * ty / d.H; sw.Restart(); yield return null; }
            }
            _groundTex = new Texture2D(tw, th, TextureFormat.RGBA32, true) { name = "MapGround", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, anisoLevel = 2 };
            _groundTex.SetPixels32(px);
            _groundTex.Apply(true, true);   // mipmaps, then drop the CPU copy
            px = null;
            _groundMat.mainTexture = _groundTex;
            MakeGround(d);
            yield return null;

            // --- lots bucketed per chunk ---
            int ncx = (d.W + CH - 1) / CH, ncy = (d.H + CH - 1) / CH;
            var buckets = new List<int>[ncx * ncy];
            for (int i = 0; i < buckets.Length; i++) buckets[i] = new List<int>();
            for (int l = 0; l < d.Lots.Count; l++)
            {
                var L = d.Lots[l];
                int cx = Mathf.Clamp((L.X + L.W / 2) / CH, 0, ncx - 1), cy = Mathf.Clamp((L.Y + L.H / 2) / CH, 0, ncy - 1);
                buckets[cy * ncx + cx].Add(l);
            }

            int done = 0, total = ncx * ncy;
            for (int cy = 0; cy < ncy; cy++)
                for (int cx = 0; cx < ncx; cx++)
                {
                    BuildChunk(d, cx, cy, buckets[cy * ncx + cx]);
                    done++;
                    if (sw.ElapsedMilliseconds > 14) { Progress = 0.25f + 0.75f * done / total; sw.Restart(); yield return null; }
                }

            BuildLabels(d);
            _builtKey = key;
            _dynKey = "";
            Building = false; Ready = true; Progress = 1f;
            var h = HomeTile();
            _focus = _focusTarget = new Vector3(h.x, 0f, -h.y);
            _dist = _distTarget = 55f;
            Debug.Log("[MapView] built " + _chunks.Count + " chunks for " + d.Lots.Count + " lots");
        }

        void Clear()
        {
            foreach (var c in _chunks) if (c != null) Destroy(c);
            _chunks.Clear(); _chunkCenters.Clear();
            if (_root != null) Destroy(_root);
            if (_dyn != null) Destroy(_dyn);
            if (_groundTex != null) Destroy(_groundTex);
            Labels.Clear();
        }

        void EnsureMaterials()
        {
            if (_objMat != null) return;
            var sh = Resources.Load<Shader>("Shaders/DayZeroMap");
            if (sh != null && sh.isSupported)
            {
                _objMat = new Material(sh) { name = "MapObjects" };
                _groundMat = new Material(sh) { name = "MapGround" };
                return;
            }
            Debug.LogWarning("[MapView] DayZero/Map shader missing or unsupported; using built-in fallbacks (no haze).");
            var ic = Shader.Find("Hidden/Internal-Colored");
            _objMat = new Material(ic);
            _objMat.SetInt("_SrcBlend", (int)BlendMode.One);
            _objMat.SetInt("_DstBlend", (int)BlendMode.Zero);
            _objMat.SetInt("_Cull", (int)CullMode.Off);
            _objMat.SetInt("_ZWrite", 1);
            _objMat.SetInt("_ZTest", (int)CompareFunction.LessEqual);
            var sp = Shader.Find("Sprites/Default");
            _groundMat = new Material(sp != null ? sp : ic) { renderQueue = 1000 };
        }

        GameObject MakeObject(string name, Mesh m, Material mat, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = m;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return go;
        }

        void MakeGround(Data d)
        {
            var m = new Mesh { name = "Ground" };
            float W = d.W, H = d.H, pad = 400f;
            // the centre quad carries the texture; a skirt of void colour runs out into the haze
            m.SetVertices(new List<Vector3> {
                new Vector3(0, 0, 0), new Vector3(W, 0, 0), new Vector3(W, 0, -H), new Vector3(0, 0, -H),
                new Vector3(-pad, -0.01f, pad), new Vector3(W + pad, -0.01f, pad), new Vector3(W + pad, -0.01f, -H - pad), new Vector3(-pad, -0.01f, -H - pad) });
            m.SetUVs(0, new List<Vector2> {
                new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0), new Vector2(0, 0),
                new Vector2(0.0005f, 0.0005f), new Vector2(0.0005f, 0.0005f), new Vector2(0.0005f, 0.0005f), new Vector2(0.0005f, 0.0005f) });
            var white = new Color32(255, 255, 255, 255);
            var edge = (Color32)new Color(0.62f, 0.66f, 0.56f);
            m.SetColors(new List<Color32> { white, white, white, white, edge, edge, edge, edge });
            m.SetTriangles(new List<int> { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 }, 0);
            m.RecalculateBounds();
            MakeObject("Ground", m, _groundMat, _root.transform);
        }

        // ---------------- hashing / noise ----------------
        /// <summary>Godot view3d._hf (64-bit int maths like GDScript).</summary>
        static float Hf(long a, long b)
        {
            unchecked
            {
                long h = (a * 73856093L) ^ (b * 19349663L);
                h = (h ^ (h >> 13)) * 1274126177L;
                return (h & 65535) / 65535f;
            }
        }

        static float Noise(float x, float y, float cell, int salt)
        {
            float fx = x / cell, fy = y / cell;
            int ix = Mathf.FloorToInt(fx), iy = Mathf.FloorToInt(fy);
            float tx = fx - ix, ty = fy - iy;
            tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty);
            float a = Hf(ix * 131 + salt, iy), b = Hf((ix + 1) * 131 + salt, iy);
            float c = Hf(ix * 131 + salt, iy + 1), e = Hf((ix + 1) * 131 + salt, iy + 1);
            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, e, tx), ty);
        }

        static Color Hex(string s) { ColorUtility.TryParseHtmlString(s, out var c); return c; }

        // ---------------- ground painting ----------------
        static readonly Color C_GRASS = new Color(0.40f, 0.47f, 0.27f);
        static readonly Color C_GRASS_DRY = new Color(0.52f, 0.52f, 0.34f);
        static readonly Color C_FOREST = new Color(0.30f, 0.40f, 0.25f);
        static readonly Color C_DIRT = new Color(0.52f, 0.44f, 0.33f);
        static readonly Color C_ASPHALT = new Color(0.27f, 0.27f, 0.27f);
        static readonly Color C_AVE = new Color(0.31f, 0.31f, 0.30f);
        static readonly Color C_WEED = new Color(0.42f, 0.48f, 0.28f);
        static readonly Color C_WATER = new Color(0.20f, 0.32f, 0.31f);
        static readonly Color C_PAVE = new Color(0.62f, 0.60f, 0.55f);
        static readonly Color C_CONCRETE = new Color(0.55f, 0.53f, 0.49f);
        static readonly Color C_PIER = new Color(0.45f, 0.37f, 0.28f);
        static readonly Color C_FIELD = new Color(0.66f, 0.60f, 0.38f);
        static readonly Color C_ROCK = new Color(0.50f, 0.49f, 0.46f);
        static readonly Color C_VOID = new Color(0.20f, 0.23f, 0.20f);

        void PaintRow(Data d, int ty, Color32[] px, int tw, int th)
        {
            for (int tx = 0; tx < d.W; tx++)
            {
                int i = ty * d.W + tx;
                int t = d.Tiles[i];
                for (int sy = 0; sy < 2; sy++)
                    for (int sx = 0; sx < 2; sx++)
                    {
                        float x = tx * 2 + sx, y = ty * 2 + sy;
                        float j = Hf((long)x * 7 + 3, (long)y * 13 + 1);      // per-pixel jitter
                        Color c = GroundColor(d, t, i, tx, ty, x, y, j);
                        int row = th - 1 - (int)y;
                        px[row * tw + (int)x] = c;
                    }
            }
        }

        Color GroundColor(Data d, int t, int i, int tx, int ty, float x, float y, float j)
        {
            float n = Noise(x, y, 14f, 11);              // dirt / dry patches
            float n2 = Noise(x, y, 5f, 29);
            Color grass = Color.Lerp(C_GRASS, C_GRASS_DRY, Mathf.Clamp01(n * 1.3f - 0.35f));
            if (n2 > 0.72f) grass = Color.Lerp(grass, C_DIRT, (n2 - 0.72f) * 2.6f);
            switch (t)
            {
                case Data.T_WATER:
                    return Color.Lerp(C_WATER, C_WATER * 1.12f, n2) * (0.97f + j * 0.05f);
                case Data.T_ROAD:
                case Data.T_AVE:
                {
                    Color c = (t == Data.T_AVE ? C_AVE : C_ASPHALT) * (0.94f + j * 0.1f);
                    float crack = Hf((long)x * 31 + 5, (long)y * 17 + 9);
                    if (crack > 0.93f) c *= 0.62f;                                    // cracks / potholes
                    if (Noise(x, y, 6f, 41) > 0.68f && j > 0.45f) c = Color.Lerp(c, C_WEED, 0.75f); // weeds through the asphalt
                    if (n > 0.78f) c = Color.Lerp(c, C_DIRT, 0.35f);                   // silt / mud
                    return c;
                }
                case Data.T_PARK:
                case Data.T_MEADOW:
                    return grass * (0.95f + j * 0.08f);
                case Data.T_FOREST:
                    return Color.Lerp(C_FOREST, grass, n * 0.5f) * (0.93f + j * 0.1f);
                case Data.T_DIRT:
                    return Color.Lerp(C_DIRT, grass, n2 * 0.35f) * (0.95f + j * 0.08f);
                case Data.T_ROCK:
                    return C_ROCK * (0.9f + j * 0.15f);
                case Data.T_FIELD:
                    return Color.Lerp(C_FIELD, C_DIRT, ((int)(x / 2) % 3 == 0 ? 0.35f : 0f) + n2 * 0.2f);
                case Data.T_PIER:
                    return C_PIER * (0.9f + j * 0.15f);
                case Data.T_PLAZA:
                {
                    Color c = C_PAVE * (0.95f + j * 0.08f);
                    if (n2 > 0.66f) c = Color.Lerp(c, C_WEED, 0.5f);
                    return c;
                }
                case Data.T_BRIDGE:
                    return C_CONCRETE * (0.95f + j * 0.06f);
                case Data.T_BLD:
                {
                    int l = d.LotId[i];
                    string k = l >= 0 && l < d.Lots.Count ? d.Lots[l].Kind : "";
                    if (k == "house" || k == "farmhouse" || k == "cabin") return grass * (0.9f + j * 0.1f);
                    if (k == "farm") return Color.Lerp(C_FIELD, C_DIRT, n2 * 0.4f);
                    if (k == "parking")
                    {
                        Color p = C_AVE * (0.95f + j * 0.08f);
                        if (((tx + ty) & 3) == 0 && j > 0.5f) p *= 1.25f;           // faded bay lines
                        if (n2 > 0.6f) p = Color.Lerp(p, C_WEED, 0.6f);
                        return p;
                    }
                    Color cc = C_CONCRETE * (0.93f + j * 0.1f);
                    if (n2 > 0.7f) cc = Color.Lerp(cc, C_WEED, 0.55f);
                    return cc;
                }
                default:
                    return C_VOID;
            }
        }

        // ---------------- lot heights (Godot _lot_height) ----------------
        float LotHeight(int l)
        {
            var d = D;
            var L = d.Lots[l];
            float r = Hf(l, 1);
            string dk = d.Districts.Count > 0 ? d.Districts[Mathf.Clamp(L.District, 0, d.Districts.Count - 1)].Kind : "";
            switch (L.Kind)
            {
                case "house": return FL * 1.2f + (r > 0.55f ? FL : 0f);
                case "apartment": return FL * (3 + (int)(r * 2.99f)) + 0.08f;
                case "office":
                {
                    int fl = 3 + (int)(r * 3.99f);
                    if (dk == "downtown") fl += 2 + (int)(Hf(l, 2) * 5f);
                    else if (dk == "midtown") fl += 1;
                    return FL * fl + 0.08f;
                }
                case "factory": return 0.8f + r * 0.4f;
                case "parking": case "farm": return 0f;
                case "hospital": return FL * 5 + 0.1f;
                case "police": case "fire_station": return FL * 3;
                case "school": return FL * 3;
                case "church": return 1.2f;
                case "supermarket": case "warehouse": return 0.95f;
                case "gas_station": return 0.6f;
                case "checkpoint": return 0.8f;
                case "cabin": return 0.55f;
                case "shack": return 0.45f;
                case "farmhouse": return 0.95f;
                case "barn": return 0.95f;
                case "ranger": return 0.62f;
                case "camp_hut": return 0.7f;
                case "stand": return 1.55f;
                default: return 0.6f;
            }
        }

        static readonly Dictionary<string, Color[]> PAL = new Dictionary<string, Color[]>
        {
            ["house"] = new[] { Hex("#a99a83"), Hex("#9c8f7a"), Hex("#b5a58a"), Hex("#8d8576"), Hex("#7f8a86"), Hex("#9a8f7d"), Hex("#6f7f88") },
            ["house_roof"] = new[] { Hex("#5d5148"), Hex("#6a5a4c"), Hex("#4f5558"), Hex("#6e5a48"), Hex("#5a6266") },
            ["apartment"] = new[] { Hex("#9a8a78"), Hex("#8f7b68"), Hex("#a39a8a"), Hex("#7f8a86"), Hex("#8e6a55"), Hex("#b0a48e"), Hex("#8a8f8c") },
            ["office"] = new[] { Hex("#8d9093"), Hex("#a5a7a3"), Hex("#6f7d86"), Hex("#b8b2a5"), Hex("#7c8580") },
            ["factory"] = new[] { Hex("#8a6a4f"), Hex("#7a7f7d"), Hex("#6f5e4c"), Hex("#94785a"), Hex("#6c7470") },
            ["roof"] = new[] { Hex("#6b6660"), Hex("#57524c"), Hex("#7a7264"), Hex("#5f6a6e"), Hex("#6d5444"), Hex("#807a70") },
        };

        static Color Pal(string k, int l, int salt)
        {
            var arr = PAL.TryGetValue(k, out var a) ? a : PAL["apartment"];
            return arr[(int)(Hf(l, salt) * arr.Length) % arr.Length];
        }

        static Color Desat(Color c, float amt)
        {
            float g = c.r * 0.3f + c.g * 0.59f + c.b * 0.11f;
            return Color.Lerp(c, new Color(g, g, g), amt);
        }

        static readonly Color MOSS = new Color(0.33f, 0.42f, 0.22f);
        static readonly Color CHAR = new Color(0.16f, 0.15f, 0.14f);

        // ---------------- chunk ----------------
        void BuildChunk(Data d, int cx, int cy, List<int> lots)
        {
            var mb = new MB();
            foreach (int l in lots) AddLot(d, mb, l);
            AddScatter(d, mb, cx, cy);
            if (mb.V.Count == 0) return;
            var m = mb.ToMesh("Chunk " + cx + "," + cy);
            var go = MakeObject("Chunk " + cx + "," + cy, m, _objMat, _root.transform);
            _chunks.Add(go);
            _chunkCenters.Add(new Vector3(cx * CH + CH * 0.5f, 0f, -(cy * CH + CH * 0.5f)));
        }

        /// <summary>Oriented lot footprint in tile space (Godot _quad, pad 0.45-0.13 tiles).</summary>
        Vector2[] LotQuad(Data d, int l, float extraInset)
        {
            var L = d.Lots[l];
            double th = d.LotAngle(l) * Math.PI / 180.0;
            float cu = (float)Math.Cos(th), su = (float)Math.Sin(th);
            float u0 = 1e9f, u1 = -1e9f, v0 = 1e9f, v1 = -1e9f;
            for (int y = L.Y; y < L.Y + L.H; y++)
                for (int x = L.X; x < L.X + L.W; x++)
                {
                    if (x < 0 || y < 0 || x >= d.W || y >= d.H || d.LotId[y * d.W + x] != l) continue;
                    float px = x + 0.5f, py = y + 0.5f;
                    float u = px * cu + py * su, v = -px * su + py * cu;
                    u0 = Mathf.Min(u0, u); u1 = Mathf.Max(u1, u); v0 = Mathf.Min(v0, v); v1 = Mathf.Max(v1, v);
                }
            if (u0 > u1) return null;
            float pad = 0.32f - extraInset;
            u0 -= pad; u1 += pad; v0 -= pad; v1 += pad;
            if (u1 - u0 < 0.2f || v1 - v0 < 0.2f) return null;
            var q = new Vector2[4];
            var uv = new[] { new Vector2(u0, v0), new Vector2(u1, v0), new Vector2(u1, v1), new Vector2(u0, v1) };
            for (int i = 0; i < 4; i++) q[i] = new Vector2(uv[i].x * cu - uv[i].y * su, uv[i].x * su + uv[i].y * cu);
            return q;
        }

        static Vector2[] Shrink(Vector2[] q, float f)
        {
            var c = (q[0] + q[1] + q[2] + q[3]) * 0.25f;
            var o = new Vector2[4];
            for (int i = 0; i < 4; i++) o[i] = c + (q[i] - c) * f;
            return o;
        }

        static Vector2[] RectQuad(Vector2 c, float hu, float hv, float th)
        {
            float cu = Mathf.Cos(th), su = Mathf.Sin(th);
            var U = new Vector2(cu, su); var V = new Vector2(-su, cu);
            return new[] { c - U * hu - V * hv, c + U * hu - V * hv, c + U * hu + V * hv, c - U * hu + V * hv };
        }

        void AddLot(Data d, MB mb, int l)
        {
            var L = d.Lots[l];
            float h = _lotH[l];
            float ang = (float)(d.LotAngle(l) * Math.PI / 180.0);
            if (h <= 0f)
            {
                // parking / farm: no building; wrecks on parking lots
                if (L.Kind == "parking")
                {
                    var q0 = LotQuad(d, l, 0.3f);
                    if (q0 == null) return;
                    int n = 1 + (int)(Hf(l, 21) * 3.5f);
                    for (int k = 0; k < n; k++)
                    {
                        Vector2 p = Lerp4(q0, Hf(l, 30 + k), Hf(l, 40 + k));
                        AddCar(mb, p, ang + (Hf(l, 50 + k) > 0.5f ? 0f : Mathf.PI * 0.5f) + (Hf(l, 60 + k) - 0.5f) * 0.4f, l * 7 + k);
                    }
                }
                return;
            }
            var q = LotQuad(d, l, 0f);
            if (q == null) return;

            string k2 = L.Kind;
            string palKey = PAL.ContainsKey(k2) ? k2 : (k2 == "house" || k2 == "cabin" || k2 == "farmhouse" || k2 == "shack" ? "house" : "apartment");
            Color wall = Desat(Pal(palKey, l, 3), 0.25f);
            Color roof = Desat(Pal(k2 == "house" ? "house_roof" : "roof", l, 4), 0.2f);
            bool landmark = !string.IsNullOrEmpty(L.Landmark);
            float ruin = Hf(l, 7);
            bool burnt = !landmark && ruin < 0.09f;
            bool collapsed = !landmark && !burnt && ruin < 0.15f;
            bool broken = !landmark && !burnt && !collapsed && ruin < 0.27f && h > 0.9f;
            bool mossy = Hf(l, 8) < 0.4f;
            bool gable = k2 == "house" || k2 == "farmhouse" || k2 == "cabin" || k2 == "barn" || k2 == "church" || k2 == "shack";

            if (collapsed)
            {
                Color rub = Desat(wall * 0.75f, 0.3f);
                var heap = Shrink(q, 0.8f);
                mb.Prism(heap, 0f, Mathf.Min(0.35f, h * 0.35f), rub * 0.9f, rub, Shrink(heap, 0.6f), Mathf.Min(0.5f, h * 0.45f), MOSS, 0.35f);
                AddRubble(mb, q, l, 7 + (int)(Hf(l, 9) * 6), rub, 0.35f);
                if (Hf(l, 10) < 0.6f) AddTree(mb, Lerp4(q, Hf(l, 11), Hf(l, 12)), l, 0.7f);
                return;
            }

            float bh = h;
            if (burnt)
            {
                bh = Mathf.Max(0.3f, h * 0.55f);
                wall = Color.Lerp(wall, CHAR, 0.65f);
                roof = CHAR * 0.9f;
                gable = false;
            }

            if (broken)
            {
                // half-collapsed: one half stands, the other is sheared off low with rubble spilling out
                var a = new[] { q[0], (q[0] + q[1]) * 0.5f, (q[3] + q[2]) * 0.5f, q[3] };
                var b = new[] { (q[0] + q[1]) * 0.5f, q[1], q[2], (q[3] + q[2]) * 0.5f };
                if (Hf(l, 15) < 0.5f) { var t = a; a = b; b = t; }
                float hb = bh * (0.25f + Hf(l, 16) * 0.3f);
                mb.Box(a, 0f, bh, wall, roof, MOSS, 0.5f);
                mb.Box(b, 0f, hb, wall * 0.9f, Color.Lerp(roof, CHAR, 0.4f), MOSS, 0.5f);
                Color glassB = Color.Lerp(wall, new Color(0.13f, 0.14f, 0.15f), 0.75f);
                for (int f = 0; f * FL + FL * 0.72f < bh - 0.04f; f++) mb.WallBand(a, f * FL + FL * 0.38f, f * FL + FL * 0.72f, glassB, 0.1f);
                AddRubble(mb, b, l, 6 + (int)(Hf(l, 9) * 5), wall * 0.8f, 0.3f);
                if (Hf(l, 17) < 0.5f) AddTree(mb, Lerp4(b, 0.5f, 0.5f), l + 3, 0.6f);
                return;
            }

            // body (houses get a gable roof on top of the walls)
            float wallTop = gable ? bh * 0.62f : bh;
            mb.Box(q, 0f, wallTop, wall, gable ? wall : roof, mossy ? MOSS : (Color?)null, mossy ? 0.45f : 0f);
            if (gable) mb.Gable(q, wallTop, bh, roof, wall);
            else if (!burnt)
            {
                // roof clutter: AC units / stair housings like the Godot roofs
                int n = 1 + (int)(Hf(l, 13) * 2.5f);
                Color rc = Color.Lerp(roof, Color.white, 0.35f);
                for (int k = 0; k < n; k++)
                {
                    var p = Lerp4(q, 0.2f + 0.6f * Hf(l, 14 + k), 0.2f + 0.6f * Hf(l, 18 + k));
                    float s = 0.12f + Hf(l, 22 + k) * 0.18f;
                    mb.Box(RectQuad(p, s, s * (0.7f + Hf(l, 26 + k) * 0.6f), ang), bh, bh + 0.08f + Hf(l, 30 + k) * 0.14f, rc, rc * 1.08f, null, 0f);
                }
                if (mossy || Hf(l, 19) < 0.35f)
                {
                    // overgrown roof: low green mats
                    var p = Lerp4(q, 0.25f + 0.5f * Hf(l, 34), 0.25f + 0.5f * Hf(l, 35));
                    float s = 0.2f + Hf(l, 36) * 0.35f;
                    mb.Box(RectQuad(p, s, s * 0.8f, ang + Hf(l, 37)), bh, bh + 0.035f, MOSS, MOSS * 1.1f, null, 0f);
                }
            }

            // window bands, one per floor on every wall
            int floors = Mathf.Max(1, Mathf.RoundToInt(wallTop / FL));
            if (floors >= 1 && wallTop > 0.3f)
            {
                Color glass = burnt ? CHAR * 0.6f : Color.Lerp(wall, new Color(0.15f, 0.17f, 0.19f), 0.7f);
                for (int f = 0; f < floors; f++)
                {
                    float y0 = f * FL + FL * 0.38f, y1 = f * FL + FL * 0.72f;
                    if (y1 > wallTop - 0.04f) break;
                    mb.WallBand(q, y0, y1, glass, 0.12f);
                }
            }

            if (burnt) AddRubble(mb, q, l, 3 + (int)(Hf(l, 9) * 4), CHAR * 1.4f, 0.22f);
        }

        static Vector2 Lerp4(Vector2[] q, float u, float v)
        {
            var a = Vector2.Lerp(q[0], q[1], u);
            var b = Vector2.Lerp(q[3], q[2], u);
            return Vector2.Lerp(a, b, v);
        }

        void AddRubble(MB mb, Vector2[] q, int l, int n, Color c, float maxH)
        {
            var big = Shrink(q, 1.15f);
            for (int k = 0; k < n; k++)
            {
                var p = Lerp4(big, Hf(l, 100 + k), Hf(l, 120 + k));
                float s = 0.08f + Hf(l, 140 + k) * 0.18f;
                float hh = 0.05f + Hf(l, 160 + k) * maxH;
                Color cc = c * (0.8f + Hf(l, 180 + k) * 0.4f);
                mb.Box(RectQuad(p, s, s * 0.7f, Hf(l, 200 + k) * 6.28f), 0f, hh, cc, cc * 1.1f, null, 0f);
            }
        }

        static readonly Color[] CAR_COLS = { Hex("#6d5a4a"), Hex("#5a6266"), Hex("#7a6a52"), Hex("#4f5a52"), Hex("#80766a"), Hex("#5b4a44"), Hex("#6c6f73") };

        void AddCar(MB mb, Vector2 p, float th, int salt)
        {
            Color body = Desat(CAR_COLS[(int)(Hf(salt, 1) * CAR_COLS.Length) % CAR_COLS.Length], 0.2f);
            if (Hf(salt, 2) < 0.6f) body = Color.Lerp(body, new Color(0.55f, 0.32f, 0.18f), 0.6f);    // rust
            if (Hf(salt, 3) < 0.15f) body = Color.Lerp(body, CHAR, 0.7f);                           // burnt out
            float L = 0.42f, Wd = 0.2f;
            mb.Box(RectQuad(p, L, Wd, th), 0.03f, 0.2f, body, body * 1.05f, null, 0f);
            var cab = p + new Vector2(Mathf.Cos(th), Mathf.Sin(th)) * (-0.05f);
            mb.Box(RectQuad(cab, L * 0.55f, Wd * 0.9f, th), 0.2f, 0.32f, body * 0.85f, Color.Lerp(body, new Color(0.2f, 0.22f, 0.24f), 0.5f), null, 0f);
        }

        static readonly Color[] TREE_COLS = { new Color(0.29f, 0.52f, 0.36f), new Color(0.36f, 0.55f, 0.32f), new Color(0.42f, 0.50f, 0.27f), new Color(0.26f, 0.44f, 0.33f), new Color(0.48f, 0.52f, 0.30f) };
        static readonly Color TRUNK = new Color(0.36f, 0.29f, 0.22f);

        void AddTree(MB mb, Vector2 p, int salt, float scale)
        {
            float r = Hf(salt, 71);
            float s = scale * (0.75f + r * 0.6f);
            bool dead = Hf(salt, 72) < 0.08f;
            Color c = dead ? new Color(0.45f, 0.40f, 0.33f) : TREE_COLS[(int)(Hf(salt, 73) * TREE_COLS.Length) % TREE_COLS.Length];
            float rot = Hf(salt, 74) * 1.57f;
            mb.Box(RectQuad(p, 0.05f * s, 0.05f * s, rot), 0f, 0.35f * s, TRUNK, TRUNK, null, 0f);
            if (dead) { mb.Cone(p, 0.12f * s, 0.3f * s, 1.0f * s, c, rot); return; }
            if (Hf(salt, 75) < 0.4f)
            {
                // broadleaf: squat double cone
                mb.Cone(p, 0.42f * s, 0.25f * s, 0.95f * s, c * 0.92f, rot);
                mb.Cone(p, 0.30f * s, 0.6f * s, 1.15f * s, c * 1.05f, rot + 0.6f);
            }
            else
            {
                // pine: two stacked cones
                mb.Cone(p, 0.38f * s, 0.25f * s, 1.05f * s, c * 0.92f, rot);
                mb.Cone(p, 0.26f * s, 0.75f * s, 1.55f * s, c * 1.05f, rot + 0.7f);
            }
        }

        void AddScatter(Data d, MB mb, int cx, int cy)
        {
            int x0 = cx * CH, y0 = cy * CH, x1 = Mathf.Min(d.W, x0 + CH), y1 = Mathf.Min(d.H, y0 + CH);
            float cityAng = (float)(d.RgAngle * Math.PI / 180.0);
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    int t = d.Tiles[y * d.W + x];
                    float r = Hf(x * 3 + 17, y * 5 + 3);
                    float jx = Hf(x, y * 7 + 1) * 0.8f + 0.1f, jy = Hf(x * 11 + 2, y) * 0.8f + 0.1f;
                    var p = new Vector2(x + jx, y + jy);
                    int salt = y * 4099 + x;
                    switch (t)
                    {
                        case Data.T_PARK: if (r < 0.16f) AddTree(mb, p, salt, 1f); break;
                        case Data.T_FOREST: if (r < 0.055f) AddTree(mb, p, salt, 1.25f); break;
                        case Data.T_MEADOW: if (r < 0.02f) AddTree(mb, p, salt, 1.1f); break;
                        case Data.T_FIELD: if (r < 0.004f) AddTree(mb, p, salt, 1.1f); break;
                        case Data.T_PLAZA: if (r < 0.015f) AddTree(mb, p, salt, 0.8f); break;
                        case Data.T_ROAD:
                        case Data.T_AVE:
                        case Data.T_BRIDGE:
                            if (r < 0.011f)
                                AddCar(mb, p, cityAng + (Hf(x, y) > 0.5f ? 0f : Mathf.PI * 0.5f) + (Hf(x + 5, y) - 0.5f) * 0.6f, salt);
                            else if (t != Data.T_BRIDGE && r < 0.016f) AddTree(mb, p, salt, 0.65f);       // saplings in the cracks
                            else if (r < 0.019f)
                            {
                                // a short run of concrete jersey barriers, some rust-streaked, a few knocked askew
                                for (int k = 0; k < 3; k++)
                                {
                                    var bp = p + new Vector2(Mathf.Cos(cityAng), Mathf.Sin(cityAng)) * (k * 0.5f - 0.5f);
                                    Color bc = Hf(salt, 80 + k) < 0.3f ? new Color(0.62f, 0.42f, 0.30f) : new Color(0.66f, 0.64f, 0.60f);
                                    mb.Box(RectQuad(bp, 0.22f, 0.07f, cityAng + (Hf(salt, 90 + k) - 0.5f) * 0.7f), 0f, 0.2f, bc, bc * 1.05f, null, 0f);
                                }
                            }
                            else if (r < 0.026f) mb.Box(RectQuad(p, 0.1f, 0.07f, r * 300f), 0f, 0.06f + jx * 0.1f, new Color(0.45f, 0.42f, 0.38f), new Color(0.5f, 0.47f, 0.42f), null, 0f);
                            break;
                    }
                }
        }

        // ---------------- labels ----------------
        void BuildLabels(Data d)
        {
            Labels.Clear();
            foreach (var ds in d.Districts)
            {
                if (ds.C == null || ds.C.Length < 2 || string.IsNullOrEmpty(ds.Name)) continue;
                Labels.Add(new MapLabel { Pos = new Vector3((float)ds.C[0], 2f, -(float)ds.C[1]), Text = ds.Name.ToUpperInvariant(), Tier = 2 });
            }
            foreach (var ll in d.LabelLots)
            {
                if (ll.Lot < 0 || ll.Lot >= d.Lots.Count) continue;
                var c = d.LotCenter(ll.Lot);
                Labels.Add(new MapLabel { Pos = new Vector3(c.x, _lotH[ll.Lot] + 0.6f, -c.y), Text = ll.Name, Tier = Mathf.Clamp(ll.Tier, 0, 1) });
            }
        }

        // ---------------- dynamic layer: camps, converted / lit lots ----------------
        void UpdateDynamic()
        {
            var g = G;
            if (g?.S == null) return;
            var d = D;
            var bases = g.AllBases();
            var lit = g.LitLots();
            var conv = g.S.Conv;
            var sb = new System.Text.StringBuilder();
            foreach (var b in bases) sb.Append(b.Id).Append('@').Append(b.Pos.x).Append(',').Append(b.Pos.y).Append(b.Main ? 'M' : 'b').Append(b.Col).Append(';');
            sb.Append('|');
            foreach (var kv in conv) sb.Append(kv.Key).Append(':').Append(kv.Value.Col).Append(';');
            sb.Append('|');
            foreach (var kv in lit) sb.Append(kv.Key).Append(':').Append(kv.Value).Append(';');
            string key = sb.ToString();
            if (key == _dynKey) return;
            _dynKey = key;

            if (_dyn != null) Destroy(_dyn);
            _dyn = new GameObject("Map Dynamic");
            _dyn.transform.SetParent(transform, false);
            var mb = new MB();
            int me = g.CurCol;

            var green = new Color(0.42f, 0.78f, 0.36f);
            var yellow = new Color(0.98f, 0.82f, 0.36f);
            var red = new Color(0.86f, 0.30f, 0.24f);
            foreach (var kv in conv) Cap(d, mb, kv.Key, green);
            foreach (var kv in lit) if (!conv.ContainsKey(kv.Key)) Cap(d, mb, kv.Key, kv.Value == me ? yellow : red);

            foreach (var b in bases)
            {
                var p = new Vector2(b.Pos.x + 0.5f, b.Pos.y + 0.5f);
                Color fc = b.Col == me ? new Color(0.88f, 0.70f, 0.30f) : new Color(0.75f, 0.35f, 0.28f);
                float pole = b.Main ? 3.2f : 2.2f;
                float top = b.Lot >= 0 && b.Lot < _lotH.Length ? _lotH[b.Lot] : 0f;
                mb.Box(RectQuad(p, 0.05f, 0.05f, 0f), top, top + pole, new Color(0.25f, 0.24f, 0.22f), new Color(0.3f, 0.3f, 0.28f), null, 0f);
                mb.Box(RectQuad(p + new Vector2(0.45f, 0f), 0.42f, 0.03f, 0f), top + pole - 0.6f, top + pole - 0.05f, fc, fc, null, 0f);
                float half = b.Main ? 3.0f : 2.0f;
                bool inBld = top > 0.3f;
                if (inBld && b.Lot < d.Lots.Count) { var L = d.Lots[b.Lot]; half = Mathf.Max(half, Mathf.Max(L.W, L.H) * 0.5f + 0.9f); p = d.LotCenter(b.Lot); }
                AddCampYard(mb, p, half, (float)(d.RgAngle * Math.PI / 180.0), b.Id, b.Main, inBld);
            }
            if (mb.V.Count > 0) MakeObject("Dynamic", mb.ToMesh("Dynamic"), _objMat, _dyn.transform);
        }

        static readonly Color WOOD = new Color(0.40f, 0.29f, 0.19f);
        static readonly Color AMBER = new Color(1.0f, 0.72f, 0.30f);

        /// <summary>Wooden palisade square with a watchtower, a hut with lit windows and a campfire.</summary>
        void AddCampYard(MB mb, Vector2 p, float half, float th, int salt, bool main, bool inBuilding)
        {
            var U = new Vector2(Mathf.Cos(th), Mathf.Sin(th)); var V = new Vector2(-Mathf.Sin(th), Mathf.Cos(th));
            int per = main ? 10 : 7;
            for (int side = 0; side < 4; side++)
            {
                Vector2 dirA = side % 2 == 0 ? U : V, dirN = side % 2 == 0 ? V : U;
                float sg = side < 2 ? 1f : -1f;
                for (int k = 0; k < per; k++)
                {
                    if (side == 0 && (k == per / 2 || k == per / 2 - 1)) continue;   // gate gap
                    float t = (k + 0.5f) / per * 2f - 1f;
                    var pp = p + dirN * (half * sg) + dirA * (t * half);
                    float hh = 0.55f + Hf(salt * 31 + side * 17, k) * 0.12f;
                    mb.Box(RectQuad(pp, half / per, 0.06f, th + (side % 2 == 0 ? 0f : Mathf.PI * 0.5f)), 0f, hh, WOOD, WOOD * 1.15f, null, 0f);
                }
            }
            if (inBuilding)
            {
                // camp is inside a building: fire in the yard by the gate, tower at the corner
                var gp = p - U * half * 0.0f + V * (half - 0.6f);
                mb.Box(RectQuad(gp, 0.45f, 0.45f, th + 0.785f), 0.005f, 0.012f, AMBER * 0.75f, Color.Lerp(AMBER, new Color(0.5f, 0.45f, 0.3f), 0.35f), null, 0f);
                mb.Cone(gp, 0.13f, 0.012f, 0.3f, new Color(1f, 0.6f, 0.2f), 0.3f);
            }
            else
            {
            // hut with warm lit windows
            var hp = p - U * half * 0.35f + V * half * 0.25f;
            var hq = RectQuad(hp, half * 0.42f, half * 0.28f, th);
            mb.Box(hq, 0f, 0.45f, WOOD * 1.1f, WOOD, null, 0f);
            mb.Gable(hq, 0.45f, 0.75f, WOOD * 0.8f, WOOD * 1.1f);
            mb.WallBand(hq, 0.16f, 0.32f, AMBER, half * 0.18f);
            // campfire glow: amber disc + flame
            var fp = p + U * half * 0.25f - V * half * 0.2f;
            mb.Box(RectQuad(fp, 0.5f, 0.5f, th + 0.785f), 0.005f, 0.012f, AMBER * 0.75f, Color.Lerp(AMBER, new Color(0.5f, 0.45f, 0.3f), 0.35f), null, 0f);
            mb.Cone(fp, 0.14f, 0.012f, 0.32f, new Color(1f, 0.6f, 0.2f), 0.3f);
            // crates / barrels
            for (int k = 0; k < 3; k++)
            {
                var cp = p + U * half * (0.55f - k * 0.18f) + V * half * 0.6f;
                mb.Box(RectQuad(cp, 0.1f, 0.1f, th + k), 0f, 0.2f, WOOD * 1.3f, WOOD * 1.4f, null, 0f);
            }
            }
            if (!main) return;
            // watchtower in the back corner
            var tp = p + U * half * 0.7f + V * half * 0.7f;
            for (int k = 0; k < 4; k++)
            {
                float a = th + 0.785f + k * Mathf.PI * 0.5f;
                var lp = tp + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 0.3f;
                mb.Box(RectQuad(lp, 0.04f, 0.04f, th), 0f, 1.6f, WOOD, WOOD, null, 0f);
            }
            var tq = RectQuad(tp, 0.32f, 0.32f, th);
            mb.Box(tq, 1.5f, 1.75f, WOOD * 1.1f, WOOD * 1.2f, null, 0f);
            mb.Cone(tp, 0.42f, 2.05f, 2.4f, WOOD * 0.8f, th + 0.785f);
        }

        void Cap(Data d, MB mb, int l, Color c)
        {
            if (l < 0 || l >= d.Lots.Count) return;
            var q = LotQuad(d, l, 0.04f);
            if (q == null) return;
            float h = _lotH[l];
            mb.Box(q, h + 0.01f, h + 0.06f, c * 0.85f, c, null, 0f);
        }

        // ================= mesh builder =================
        sealed class MB
        {
            public readonly List<Vector3> V = new List<Vector3>(4096);
            public readonly List<Color32> C = new List<Color32>(4096);
            public readonly List<int> T = new List<int>(6144);

            static Vector3 W(Vector2 p, float h) => new Vector3(p.x, h, -p.y);

            static Color Lit(Color c, Vector3 n)
            {
                float k = n.y > 0.9f ? 1.0f : 0.62f + 0.42f * Mathf.Max(0f, Vector3.Dot(n, SUN)) + 0.1f * Mathf.Max(0f, n.y);
                return new Color(Mathf.Clamp01(c.r * k * 1.02f), Mathf.Clamp01(c.g * k), Mathf.Clamp01(c.b * k * 0.96f), 1f);
            }

            static Vector3 Normal(Vector3 a, Vector3 b, Vector3 c, Vector3 outward)
            {
                var n = Vector3.Cross(b - a, c - a).normalized;
                if (Vector3.Dot(n, outward) < 0f) n = -n;
                return n;
            }

            public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color ca, Color cb, Color cc, Color cd)
            {
                int i = V.Count;
                V.Add(a); V.Add(b); V.Add(c); V.Add(d);
                C.Add(ca); C.Add(cb); C.Add(cc); C.Add(cd);
                T.Add(i); T.Add(i + 1); T.Add(i + 2); T.Add(i); T.Add(i + 2); T.Add(i + 3);
            }

            public void Tri(Vector3 a, Vector3 b, Vector3 c, Color col)
            {
                int i = V.Count;
                V.Add(a); V.Add(b); V.Add(c);
                Color32 k = col; C.Add(k); C.Add(k); C.Add(k);
                T.Add(i); T.Add(i + 1); T.Add(i + 2);
            }

            /// <summary>Box over a 4-corner footprint (tile space). Bottom of walls darkened (fake AO) and optionally tinted (moss).</summary>
            public void Box(Vector2[] q, float y0, float y1, Color wall, Color top, Color? baseTint, float tintAmt)
            {
                var ctr = (q[0] + q[1] + q[2] + q[3]) * 0.25f;
                var cw = W(ctr, (y0 + y1) * 0.5f);
                for (int i = 0; i < 4; i++)
                {
                    Vector2 a = q[i], b = q[(i + 1) & 3];
                    var A0 = W(a, y0); var B0 = W(b, y0); var B1 = W(b, y1); var A1 = W(a, y1);
                    var n = Normal(A0, B0, B1, (A0 + B1) * 0.5f - cw);
                    Color lc = Lit(wall, n);
                    Color lb = lc * 0.78f; lb.a = 1f;
                    if (baseTint.HasValue && tintAmt > 0f) lb = Color.Lerp(lb, Lit(baseTint.Value, n), tintAmt);
                    Quad(A0, B0, B1, A1, lb, lb, lc, lc);
                }
                Color tc = Lit(top, Vector3.up);
                Quad(W(q[0], y1), W(q[1], y1), W(q[2], y1), W(q[3], y1), tc, tc, tc, tc);
            }

            /// <summary>Frustum: base quad q at y0, top quad qt at y1 (used for rubble heaps).</summary>
            public void Prism(Vector2[] q, float y0, float y1, Color wall, Color top, Vector2[] qt, float yt, Color tint, float tintAmt)
            {
                Box(q, y0, y1, wall, top, tint, tintAmt);
                var ctr = (q[0] + q[1] + q[2] + q[3]) * 0.25f;
                var cw = W(ctr, y1);
                for (int i = 0; i < 4; i++)
                {
                    var A0 = W(q[i], y1); var B0 = W(q[(i + 1) & 3], y1); var B1 = W(qt[(i + 1) & 3], yt); var A1 = W(qt[i], yt);
                    var n = Normal(A0, B0, B1, (A0 + B1) * 0.5f - cw + Vector3.up * 0.01f);
                    Color c = Lit(Color.Lerp(top, tint, tintAmt * 0.5f), n);
                    Quad(A0, B0, B1, A1, c, c, c, c);
                }
                Color tc = Lit(top, Vector3.up);
                Quad(W(qt[0], yt), W(qt[1], yt), W(qt[2], yt), W(qt[3], yt), tc, tc, tc, tc);
            }

            /// <summary>Gable roof from y0 (eaves) to y1 (ridge), ridge along the footprint's long side.</summary>
            public void Gable(Vector2[] q, float y0, float y1, Color roof, Color wall)
            {
                Vector2[] r = q;
                if ((q[1] - q[0]).sqrMagnitude < (q[2] - q[1]).sqrMagnitude) r = new[] { q[1], q[2], q[3], q[0] };
                Vector2 m0 = (r[3] + r[0]) * 0.5f, m1 = (r[1] + r[2]) * 0.5f;
                var ctr = (q[0] + q[1] + q[2] + q[3]) * 0.25f;
                var cw = W(ctr, y0);
                var R0 = W(m0, y1); var R1 = W(m1, y1);
                var a = W(r[0], y0); var b = W(r[1], y0); var c = W(r[2], y0); var d = W(r[3], y0);
                var n1 = Normal(a, b, R1, (a + R1) * 0.5f - cw);
                Color c1 = Lit(roof, n1);
                Quad(a, b, R1, R0, c1, c1, c1, c1);
                var n2 = Normal(c, d, R0, (c + R0) * 0.5f - cw);
                Color c2 = Lit(roof, n2);
                Quad(c, d, R0, R1, c2, c2, c2, c2);
                Tri(d, a, R0, Lit(wall, Normal(d, a, R0, (d + a) * 0.5f - cw)));
                Tri(b, c, R1, Lit(wall, Normal(b, c, R1, (b + c) * 0.5f - cw)));
            }

            /// <summary>Dark window strip on every wall between y0 and y1, inset from the corners.</summary>
            public void WallBand(Vector2[] q, float y0, float y1, Color glass, float margin)
            {
                var ctr = (q[0] + q[1] + q[2] + q[3]) * 0.25f;
                for (int i = 0; i < 4; i++)
                {
                    Vector2 a = q[i], b = q[(i + 1) & 3];
                    float len = (b - a).magnitude;
                    if (len < margin * 2f + 0.2f) continue;
                    Vector2 dir = (b - a) / len;
                    Vector2 mid = (a + b) * 0.5f;
                    Vector2 outv = (mid - ctr).normalized * 0.012f;
                    Vector2 a2 = a + dir * margin + outv, b2 = b - dir * margin + outv;
                    var A0 = W(a2, y0); var B0 = W(b2, y0); var B1 = W(b2, y1); var A1 = W(a2, y1);
                    var n = new Vector3(outv.x, 0f, -outv.y).normalized;
                    Color c = Lit(glass, n);
                    Quad(A0, B0, B1, A1, c, c, c, c);
                }
            }

            /// <summary>Four-sided cone (low-poly tree tier).</summary>
            public void Cone(Vector2 p, float rad, float y0, float y1, Color col, float rot)
            {
                var apex = W(p, y1);
                var cw = W(p, y0);
                var pts = new Vector3[4];
                for (int i = 0; i < 4; i++)
                {
                    float a = rot + i * Mathf.PI * 0.5f;
                    pts[i] = W(p + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rad, y0);
                }
                for (int i = 0; i < 4; i++)
                {
                    var A = pts[i]; var B = pts[(i + 1) & 3];
                    var n = Normal(A, B, apex, (A + B) * 0.5f - cw + Vector3.up * 0.2f);
                    Tri(A, B, apex, Lit(col, n));
                }
            }

            public Mesh ToMesh(string name)
            {
                var m = new Mesh { name = name };
                if (V.Count > 65000) m.indexFormat = IndexFormat.UInt32;
                m.SetVertices(V);
                m.SetColors(C);
                m.SetTriangles(T, 0);
                m.RecalculateBounds();
                m.UploadMeshData(true);
                return m;
            }
        }
    }
}
