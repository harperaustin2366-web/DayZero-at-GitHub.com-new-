// Procedural 3D city map, generated entirely at runtime (no editor assets beyond
// Resources/Shaders/DayZeroMap.shader + DayZeroShadow.shader). v0.11.3a restyle toward
// dayzero-style-concept.png / art/ART_BIBLE.md; the meshes come from MapGen (pure C#, also run
// by the offline preview harness work/unity-verify/Preview so the look can be checked without a device):
//   * two levels of detail per 64x64-tile chunk: LOD1 (massing, coarse ground) for the whole map is
//     built behind the loading progress; LOD0 (windows, markings, props, street furniture) streams in
//     around the camera and is dropped again when the camera moves away
//   * vertex-coloured meshes with lighting baked in; one unlit material + per-pixel distance haze,
//     plus an alpha-blended ground-shadow mesh per chunk (stencil keeps overlaps from double-darkening)
//   * dynamic layer: survivor camps (palisade, watchtower, cabin, campfire, lit windows), markers on
//     converted / lit lots
//   * touch camera: one-finger pan, pinch zoom, two-finger twist; pitch 42, FOV 35
//   * v0.11.4a: start-site selection (tap a building -> highlight -> GameUI Confirm founds the camp
//     there via Bootstrap.ConfirmStart; the chunks around the yard are rebuilt so it forms around it)
//     and the crowd layer (Crowd.cs: procedural survivors / zombies batched into one dynamic mesh)
// World space: tile (x, y) -> Vector3(x, height, -y), 1 unit = 1 tile (~3.5 m).
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

    public sealed partial class MapView : MonoBehaviour
    {
        public static MapView Instance { get; private set; }

        const int CH = MapGen.CH;
        // keep in sync with work/unity-verify/Preview/Program.cs
        public const float FOV = 35f, PITCH = 42f, YAW = 75f;
        const float MIN_DIST = 7f, MAX_DIST = 200f, HOME_DIST = 24f, LOD0_MAX_DIST = 75f, PICK_DIST = 45f;

        public bool Ready { get; private set; }
        public bool Building { get; private set; }
        public float Progress { get; private set; }
        public readonly List<MapLabel> Labels = new List<MapLabel>();

        Camera _cam;
        Material _objMat, _shadowMat;
        /// <summary>Non-empty when the map is not drawn with DayZero/Map (GameUI shows it on screen).</summary>
        public string RenderWarning { get; private set; } = "";
        GameObject _root, _dyn;
        MapGen _gen;
        List<int>[] _buckets;
        int _ncx, _ncy;
        GameObject[] _lod1 = new GameObject[0];
        readonly Dictionary<int, GameObject> _lod0 = new Dictionary<int, GameObject>();
        string _builtKey = "";

        // camera state
        Vector3 _focus, _focusTarget;
        float _dist = HOME_DIST, _distTarget = HOME_DIST, _yaw = YAW;
        bool _animFocus;
        bool _drag; Vector2 _last;
        bool _pinch; float _pinchD, _pinchA; Vector2 _pinchMid;

        // dynamic layer
        string _dynKey = "";
        float _dynT;

        // start-site selection (phase "pick")
        public int SelectedLot { get; private set; } = -1;
        public Vector2Int SelectedTile { get; private set; } = new Vector2Int(-1, -1);
        public string SelectError { get; private set; } = "";
        GameObject _selGo;
        Material _glowMat;
        Vector2 _tapStart; float _tapT; bool _tapOk;
        Vector2 _pickFocus = new Vector2(-1f, -1f);

        // crowd layer
        Crowd _crowd;
        GameObject _crowdGo;
        Mesh _crowdMesh, _crowdShadowMesh;
        readonly MeshData _cm = new MeshData(), _cs = new MeshData();
        Game _sfxGame;
        public Crowd Crowd => _crowd;

        static readonly Color FOG = new Color(0x6e / 255f, 0x74 / 255f, 0x66 / 255f);

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
        public void Overview() { _distTarget = _distTarget > 100f ? HOME_DIST : 160f; }
        public float Dist => _dist;

        public void Home()
        {
            var p = HomeTile();
            _focusTarget = new Vector3(p.x, 0f, -p.y);
            _animFocus = true;
            _distTarget = Picking ? PICK_DIST : HOME_DIST;
            _yaw = YAW;
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
            var d = D;
            if (g?.C != null)
                foreach (var b in g.C.Bases)
                    if (b.Main) return b.Lot >= 0 && b.Lot < d.Lots.Count ? d.LotCenter(b.Lot) : new Vector2(b.Pos.x + 0.5f, b.Pos.y + 0.5f);
            if (g?.C != null && g.C.Bases.Count > 0) return new Vector2(g.C.Bases[0].Pos.x, g.C.Bases[0].Pos.y);
            if (Picking)
            {
                // no camp yet (v0.14.0a: no suggested site any more, Austin picks anywhere): open over the middle of
                // the city, a little zoomed out, so there are plenty of buildings to tap
                if (_pickFocus.x < 0f) _pickFocus = new Vector2(d.CityRect.x + d.CityRect.width * 0.5f, d.CityRect.y + d.CityRect.height * 0.5f);
                return _pickFocus;
            }
            return new Vector2(d.CityRect.x + d.CityRect.width * 0.5f, d.CityRect.y + d.CityRect.height * 0.5f);
        }

        // ================= frame =================
        void Update()
        {
            var b = Bootstrap.Instance;
            bool playing = b != null && b.State == Bootstrap.Screen2.Playing && b.Game?.S != null;
            if (_root != null) _root.SetActive(playing && Ready);
            if (_dyn != null) _dyn.SetActive(playing && Ready);
            FxTerritory.Tick(transform, playing ? b.Game : null, _gen, _dist, playing && Ready);   // v0.18 territory tint
            if (!playing) return;

            string key = D.WorldSeed + ":" + D.Lots.Count + ":" + D.W + "x" + D.H;
            if (key != _builtKey && !Building) StartCoroutine(Build(key));
            if (!Ready) return;

            EnsureCamera();
            HandleInput(b);
            UpdateCamera();
            FxView.Apply(_cam, b.Game, _dist);   // v0.15 day/night tint, haze, film look (visual only)
            StreamLod0();   // v0.17.1: LOD0 built on a worker thread, cached (no per-frame coroutine)
            _dynT -= Time.unscaledDeltaTime;
            if (_dynT <= 0f) { _dynT = 0.75f; UpdateDynamic(); }
            if (!Picking && SelectedLot >= 0) ClearSelection();
            UpdateTools();
            UpdateCrowd(b);
            UpdateOvergrowth();   // v0.16: rebuild chunks when the 5-day overgrowth step changes
        }

        /// <summary>Force a full rebuild on the next frame (new game / load may reuse the same world seed).</summary>
        public void Invalidate() { FxTerritory.Invalidate(); _builtKey = ""; _pickFocus = new Vector2(-1f, -1f); ClearSelection(); Game.YardProvider = null; CancelTool(); ClearBuilding(); }

        /// <summary>True while a new game waits for the player to pick the building for the camp.</summary>
        public bool Picking { get { var g = G; return g?.S != null && g.C != null && !g.C.Started && g.S.Phase == "pick"; } }

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
                _tapOk = false;   // two fingers: never a tap
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
                    _tapStart = t.position; _tapT = Time.unscaledTime; _tapOk = _drag;
                }
                else if (_drag && (t.phase == TouchPhase.Moved || t.phase == TouchPhase.Stationary))
                {
                    if ((t.position - _tapStart).magnitude > TapSlop()) _tapOk = false;
                    Pan(_last, t.position);
                    _last = t.position;
                }
                if (t.phase == TouchPhase.Ended)
                {
                    if (_tapOk && Time.unscaledTime - _tapT < 0.45f && (t.position - _tapStart).magnitude <= TapSlop()) OnTap(t.position);
                    _tapOk = false;
                }
                if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled) _drag = false;
                return;
            }
            _pinch = false;
            // mouse (editor / desktop)
            Vector2 mp = Input.mousePosition;
            if (Input.GetMouseButtonDown(0)) { _drag = ui == null || !ui.BlocksPointer(TopLeft(mp)); _last = mp; _tapStart = mp; _tapT = Time.unscaledTime; _tapOk = _drag; }
            else if (_drag && Input.GetMouseButton(0)) { if ((mp - _tapStart).magnitude > TapSlop()) _tapOk = false; Pan(_last, mp); _last = mp; }
            if (Input.GetMouseButtonUp(0))
            {
                if (_tapOk && Time.unscaledTime - _tapT < 0.45f && (mp - _tapStart).magnitude <= TapSlop()) OnTap(mp);
                _tapOk = false; _drag = false;
            }
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
            Shader.SetGlobalVector("_DZFogParams", new Vector4(_dist * 0.9f, _dist * 2.6f + 20f, 0.85f, 0f));
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

        /// <summary>Finger travel (pixels) below which a touch counts as a tap, not a pan (~1.6 mm).</summary>
        static float TapSlop() => Mathf.Max(12f, (UnityEngine.Screen.dpi > 0f ? UnityEngine.Screen.dpi : 320f) * 0.065f);

        // ================= start-site selection =================
        void OnTap(Vector2 screen)
        {
            if (!Picking) { OnPlayTap(screen); return; }
            if (_cam == null || _gen == null) return;
            var ray = _cam.ScreenPointToRay(new Vector3(screen.x, screen.y, 0f));
            int lot = _gen.PickLot(ray.origin, ray.direction);
            if (lot < 0) { ClearSelection(); Bootstrap.Instance?.Toast("Tap a building to make it your base."); return; }
            Select(lot);
        }

        /// <summary>Highlight a lot as the candidate camp site and ask the game whether it can start there.</summary>
        public void Select(int lot)
        {
            var g = G;
            if (g?.S == null || _gen == null) return;
            ClearSelection();
            SelectedLot = lot;
            SelectedTile = _gen.LotTile(lot);
            SelectError = SelectedTile.x >= 0 ? g.CanStartAnywhere(SelectedTile, Math.Max(0, g.CurCol)) : "Tap a building.";
            var ch = _gen.Highlight(lot, SelectError == "");
            _selGo = new GameObject("Selection");
            _selGo.transform.SetParent(transform, false);
            if (ch.Mesh.Count > 0) MakeObject("Highlight", ToMesh("Highlight", ch.Mesh), _objMat, _selGo.transform);
            if (_glowMat != null && ch.Shadow.Count > 0) MakeObject("Glow", ToMesh("Glow", ch.Shadow), _glowMat, _selGo.transform);
            Bootstrap.Instance?.PlayClick();
        }

        public void ClearSelection()
        {
            SelectedLot = -1; SelectedTile = new Vector2Int(-1, -1); SelectError = "";
            if (_selGo != null) { DestroyChunk(_selGo); _selGo = null; }
        }

        /// <summary>Type / size / floors / district of the selected lot for the GameUI panel.</summary>
        public bool SelectionInfo(out string type, out string size, out int floors, out string district)
        {
            type = size = district = ""; floors = 0;
            if (SelectedLot < 0 || _gen == null) return false;
            _gen.Describe(SelectedLot, out type, out float w, out float dd, out floors);
            size = string.Format("{0:0} x {1:0} m", w, dd);
            district = SelectedTile.x >= 0 ? D.DistrictAt(SelectedTile).Name : "";
            return true;
        }

        /// <summary>Called by Bootstrap after Game.StartAt succeeded: clear the yard around the new camp in the
        /// static chunks (LOD1 rebuilt now, LOD0 re-streams), refresh the dynamic layer and centre the camera.</summary>
        public void OnCampFounded()
        {
            ClearSelection();
            _pickFocus = new Vector2(-1f, -1f);
            var g = G;
            if (g?.S == null || _gen == null || _root == null) return;
            _gen.SetCamps(g);
            var rebuild = new HashSet<int>();
            foreach (var b in g.AllBases())
            {
                if (!_gen.Layout(b, out var lay)) continue;
                float r = lay.Yard.Hu * 1.5f + 12f;   // lots are bucketed by centre: reach neighbours that overlap the yard
                int cx0 = Mathf.Clamp(Mathf.FloorToInt((lay.Yard.C.x - r) / CH), 0, _ncx - 1), cx1 = Mathf.Clamp(Mathf.FloorToInt((lay.Yard.C.x + r) / CH), 0, _ncx - 1);
                int cy0 = Mathf.Clamp(Mathf.FloorToInt((lay.Yard.C.y - r) / CH), 0, _ncy - 1), cy1 = Mathf.Clamp(Mathf.FloorToInt((lay.Yard.C.y + r) / CH), 0, _ncy - 1);
                for (int cy = cy0; cy <= cy1; cy++) for (int cx = cx0; cx <= cx1; cx++) rebuild.Add(cy * _ncx + cx);
            }
            foreach (int c in rebuild)
            {
                if (c < 0 || c >= _lod1.Length) continue;
                if (_lod1[c] != null) DestroyChunk(_lod1[c]);
                var ch = _gen.BuildChunk(c % _ncx, c / _ncx, _buckets[c], 1);
                _lod1[c] = MakeChunk("Chunk " + (c % _ncx) + "," + (c / _ncx) + " LOD1", ch);
            }
            // drop all full-detail chunks (and the LOD0 cache): they re-stream with the yard cleared
            InvalidateLod0();
            SyncWorkerCamps();
            for (int c = 0; c < _lod1.Length; c++) if (_lod1[c] != null) _lod1[c].SetActive(true);
            _dynKey = "";
            _crowd?.Clear();
            Home();
            Debug.Log("[MapView] camp founded: rebuilt " + rebuild.Count + " LOD1 chunks around the yard");
        }

        // ================= crowd =================
        void UpdateCrowd(Bootstrap b)
        {
            var g = G;
            if (_crowd == null || g?.S == null || _cam == null) return;
            if (_sfxGame != g)
            {
                if (_sfxGame != null) _sfxGame.SfxRequested -= _crowd.OnSfx;
                g.SfxRequested += _crowd.OnSfx;
                _sfxGame = g;
            }
            bool running = !b.Paused && b.Pending.Count == 0 && !b.Ended && !Picking;
            float dt = running ? Mathf.Min(Time.unscaledDeltaTime, 0.1f) : 0f;
            var focus = new Vector2(_focus.x, -_focus.z);
            _crowd.Update(g, dt, focus, _dist);
            _crowd.Build(_cm, _cs, _cam.transform.position, _dist);
            if (_crowdGo == null)
            {
                _crowdGo = new GameObject("Crowd");
                _crowdGo.transform.SetParent(transform, false);
                _crowdMesh = new Mesh { name = "Crowd" }; _crowdMesh.MarkDynamic();
                var go = MakeObject("Figures", _crowdMesh, _objMat, _crowdGo.transform);
                if (_shadowMat != null)
                {
                    _crowdShadowMesh = new Mesh { name = "Crowd shadows" }; _crowdShadowMesh.MarkDynamic();
                    MakeObject("Blob shadows", _crowdShadowMesh, _shadowMat, _crowdGo.transform);
                }
            }
            _crowdGo.SetActive(Ready);
            Upload(_crowdMesh, _cm);
            if (_crowdShadowMesh != null) Upload(_crowdShadowMesh, _cs);
        }

        static readonly Bounds CROWD_BOUNDS = new Bounds(Vector3.zero, new Vector3(1e5f, 1e3f, 1e5f));

        static void Upload(Mesh m, MeshData md)
        {
            m.Clear(false);
            m.indexFormat = md.V.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            m.SetVertices(md.V);
            m.SetColors(md.C);
            m.SetTriangles(md.T, 0, false);
            m.bounds = CROWD_BOUNDS;   // figures are spread over the view; skip per-frame bounds and never cull
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
            yield return null;

            _gen = new MapGen(d);
            Game.YardProvider = _gen.YardFor;   // v0.12.0a: the sim's starter palisade follows the drawn yard
            G?.EnsureWallsNow();
            _gen.SetCamps(G);
            StartWorkerGen(d);
            if (_crowd != null && _sfxGame != null) _sfxGame.SfxRequested -= _crowd.OnSfx;
            _sfxGame = null;
            _crowd = new Crowd(_gen);
            _pickFocus = new Vector2(-1f, -1f);
            MakeSkirt(d);
            Progress = 0.05f;
            yield return null;

            // --- lots bucketed per chunk ---
            _ncx = (d.W + CH - 1) / CH; _ncy = (d.H + CH - 1) / CH;
            _buckets = new List<int>[_ncx * _ncy];
            for (int i = 0; i < _buckets.Length; i++) _buckets[i] = new List<int>();
            for (int l = 0; l < d.Lots.Count; l++)
            {
                var L = d.Lots[l];
                int cx = Mathf.Clamp((L.X + L.W / 2) / CH, 0, _ncx - 1), cy = Mathf.Clamp((L.Y + L.H / 2) / CH, 0, _ncy - 1);
                _buckets[cy * _ncx + cx].Add(l);
            }

            // --- far LOD for the whole map ---
            _lod1 = new GameObject[_ncx * _ncy];
            int done = 0, total = _ncx * _ncy;
            sw.Restart();
            for (int cy = 0; cy < _ncy; cy++)
                for (int cx = 0; cx < _ncx; cx++)
                {
                    var ch = _gen.BuildChunk(cx, cy, _buckets[cy * _ncx + cx], 1);
                    _lod1[cy * _ncx + cx] = MakeChunk("Chunk " + cx + "," + cy + " LOD1", ch);
                    done++;
                    if (sw.ElapsedMilliseconds > 14) { Progress = 0.05f + 0.75f * done / total; sw.Restart(); yield return null; }
                }

            BuildLabels(d);
            var h = HomeTile();
            _focus = _focusTarget = new Vector3(h.x, 0f, -h.y);
            _dist = _distTarget = Picking ? PICK_DIST : HOME_DIST; _yaw = YAW;

            // --- full detail around the camp before the map is shown ---
            Progress = 0.85f;
            yield return null;
            foreach (int c in WantedLod0())
            {
                if (_lod0.ContainsKey(c)) continue;
                MakeLod0(c);
                Progress = Mathf.Min(0.99f, Progress + 0.03f);
                yield return null;
            }
            _builtKey = key;
            _dynKey = "";
            Building = false; Ready = true; Progress = 1f;
            Debug.Log("[MapView] built " + total + " chunks (LOD1) + " + _lod0.Count + " LOD0 for " + d.Lots.Count + " lots");
        }

        void Clear()
        {
            foreach (var c in _lod1) if (c != null) Destroy(c);
            foreach (var c in _lod0.Values) if (c != null) Destroy(c);
            _lod1 = new GameObject[0]; _lod0.Clear();
            _lod0Ver++; _lod0Seen.Clear(); _lod0Verts.Clear();
            if (_root != null) Destroy(_root);
            if (_dyn != null) Destroy(_dyn);
            ClearSelection();
            if (_crowdGo != null) { Destroy(_crowdGo); _crowdGo = null; }
            if (_crowdMesh != null) { Destroy(_crowdMesh); _crowdMesh = null; }
            if (_crowdShadowMesh != null) { Destroy(_crowdShadowMesh); _crowdShadowMesh = null; }
            Labels.Clear();
        }

        GameObject MakeChunk(string name, MapGen.Chunk ch, bool overgrown = false)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_root.transform, false);
            if (!overgrown) FxOvergrowth.Apply(ch.Mesh, OvergrowthLevel(), name.EndsWith("LOD0") && GameSettings.Vines);   // v0.16 spreading overgrowth (visual only)
            if (ch.Mesh.Count > 0) MakeObject("Mesh", ToMesh(name, ch.Mesh), _objMat, go.transform);
            if (_shadowMat != null && ch.Shadow != null && ch.Shadow.Count > 0) MakeObject("Shadows", ToMesh(name + " shadows", ch.Shadow), _shadowMat, go.transform);
            return go;
        }

        static Mesh ToMesh(string name, MeshData md)
        {
            var m = new Mesh { name = name };
            if (md.V.Count > 65000) m.indexFormat = IndexFormat.UInt32;
            m.SetVertices(md.V);
            m.SetColors(md.C);
            m.SetTriangles(md.T, 0);
            m.RecalculateBounds();
            m.UploadMeshData(true);
            return m;
        }

        void MakeLod0(int c)
        {
            int cx = c % _ncx, cy = c / _ncx;
            var ch = _gen.BuildChunk(cx, cy, _buckets[c], 0);
            AddLod0(c, ch, false);
            if (c < _lod1.Length && _lod1[c] != null) _lod1[c].SetActive(false);
        }

        void AddLod0(int c, MapGen.Chunk ch, bool overgrown)
        {
            int cx = c % _ncx, cy = c / _ncx;
            _lod0[c] = MakeChunk("Chunk " + cx + "," + cy + " LOD0", ch, overgrown);
            _lod0Verts[c] = ch.Mesh.V.Count + (ch.Shadow != null ? ch.Shadow.V.Count : 0);
            _lod0Seen[c] = Time.frameCount;
        }

        // ---------------- LOD0 streaming (v0.17.1 perf) ----------------
        // Full-detail chunks are built off the main thread by a second, private MapGen (+ FxOvergrowth vines) and
        // only turned into meshes here, at most one upload per frame. Chunks that leave the view are hidden, not
        // destroyed (LRU cache), so panning back is free; they are dropped from view only outside the wanted
        // area + one chunk ring. The cache is invalidated wherever chunks were force-rebuilt before (new map,
        // camp founded -> SetCamps/ruins, overgrowth step). Output is bit-identical to a main-thread build
        // (checked by the preview harness: --chunkbench).
        const int LOD0_CACHE = 24;
        static int LOD0_ACTIVE_MAX => GameSettings.Lod0Active;   // v0.18 graphics quality (12 HIGH, 8 LOW)
        const long LOD0_CACHE_VERTS = 3000000;
        readonly Dictionary<int, int> _lod0Seen = new Dictionary<int, int>();    // chunk -> frame last shown
        readonly Dictionary<int, int> _lod0Verts = new Dictionary<int, int>();
        int _lod0Ver;                         // bumped on invalidation: in-flight results of an older version are discarded
        MapGen _wgen; System.Threading.Tasks.Task<MapGen> _wgenTask; bool _wgenCamps, _wgenFailed;
        System.Threading.Tasks.Task<MapGen.Chunk> _job; int _jobC = -1, _jobVer;
        readonly List<int> _want = new List<int>(32), _keepRing = new List<int>(32), _pre = new List<int>(32), _lodTmp = new List<int>(32);
        Vector3 _sortP, _prevFocus; Vector3 _panDir;
        Comparison<int> _byDist;

        void StartWorkerGen(Data d)
        {
            _lod0Ver++;
            _wgen = null; _wgenCamps = false; _wgenFailed = false; _job = null;
            _wgenTask = System.Threading.Tasks.Task.Run(() => new MapGen(d));   // ~0.5 s desktop: keep it off the loading frame
        }

        /// <summary>Drop every cached full-detail chunk (callers re-show LOD1) and discard any chunk being built.</summary>
        void InvalidateLod0()
        {
            _lod0Ver++;
            foreach (var kv in _lod0)
            {
                if (kv.Value == null) continue;
                if (kv.Value.activeSelf && kv.Key < _lod1.Length && _lod1[kv.Key] != null) _lod1[kv.Key].SetActive(true);   // no hole until it re-streams
                DestroyChunk(kv.Value);
            }
            _lod0.Clear(); _lod0Seen.Clear(); _lod0Verts.Clear();
        }

        /// <summary>Give the worker generator the same camps / ruins as _gen (call right after _gen.SetCamps).</summary>
        void SyncWorkerCamps()
        {
            if (_wgen == null) { _wgenCamps = false; return; }   // applied when the worker generator is ready
            try { _job?.Wait(); } catch (Exception) { }
            _job = null;
            _wgen.SetCamps(G); _wgenCamps = true;
        }

        bool WorkerReady()
        {
            if (_wgenFailed) return false;
            if (_wgen == null)
            {
                if (_wgenTask == null || !_wgenTask.IsCompleted) return false;
                if (_wgenTask.Status != System.Threading.Tasks.TaskStatus.RanToCompletion)
                { _wgenFailed = true; Debug.LogWarning("[MapView] worker MapGen failed, LOD0 builds on the main thread: " + _wgenTask.Exception); return false; }
                _wgen = _wgenTask.Result; _wgenTask = null;
            }
            if (!_wgenCamps && _job == null) { _wgen.SetCamps(G); _wgenCamps = true; }
            return _wgenCamps;
        }

        /// <summary>Chunks within reach of point p (nearest first, at most cap) into o; empty when zoomed out past maxDist.</summary>
        void WantedLod0(List<int> o, Vector3 shift, float extra, int cap, float maxDist)
        {
            o.Clear();
            if (_dist > maxDist || _ncx == 0) return;
            var fwd = Quaternion.Euler(0f, _yaw, 0f) * Vector3.forward;
            var p = _focus + fwd * (_dist * 0.45f) + shift;
            float r = CH * 0.72f + _dist * 1.25f + extra;
            int cx0 = Mathf.Max(0, Mathf.FloorToInt((p.x - r) / CH)), cx1 = Mathf.Min(_ncx - 1, Mathf.FloorToInt((p.x + r) / CH));
            int cy0 = Mathf.Max(0, Mathf.FloorToInt((-p.z - r) / CH)), cy1 = Mathf.Min(_ncy - 1, Mathf.FloorToInt((-p.z + r) / CH));
            for (int cy = cy0; cy <= cy1; cy++)
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    float x = Mathf.Clamp(p.x, cx * CH, cx * CH + CH), z = Mathf.Clamp(-p.z, cy * CH, cy * CH + CH);
                    float dx = x - p.x, dz = z + p.z;
                    if (dx * dx + dz * dz < r * r) o.Add(cy * _ncx + cx);
                }
            // nearest first
            _sortP = p;
            if (_byDist == null) _byDist = (a, b) => DistTo(a, _sortP).CompareTo(DistTo(b, _sortP));
            o.Sort(_byDist);
            if (o.Count > cap) o.RemoveRange(cap, o.Count - cap);
        }

        /// <summary>Chunks that should be at full detail: those near the point the camera looks at
        /// (shifted forward, the view extends further up the screen), only when zoomed in.</summary>
        List<int> WantedLod0() { var o = new List<int>(); WantedLod0(o, Vector3.zero, 0f, 9, LOD0_MAX_DIST); return o; }

        float DistTo(int c, Vector3 p)
        {
            float x = (c % _ncx) * CH + CH * 0.5f, z = -((c / _ncx) * CH + CH * 0.5f);
            return (x - p.x) * (x - p.x) + (z - p.z) * (z - p.z);
        }

        /// <summary>Per frame: upload a finished worker chunk, show / hide cached chunks, start the next build
        /// (wanted first, then one ring ahead in the pan direction) and evict the least recently shown.</summary>
        void StreamLod0()
        {
            if (_root == null || _buckets == null) return;
            int frame = Time.frameCount;
            bool uploaded = false;
            if (_job != null && _job.IsCompleted)
            {
                var job = _job; _job = null;
                if (job.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                {
                    if (_jobVer == _lod0Ver && !_lod0.ContainsKey(_jobC)) { AddLod0(_jobC, job.Result, true); _lod0[_jobC].SetActive(false); uploaded = true; }
                }
                else Debug.LogWarning("[MapView] LOD0 chunk build failed: " + job.Exception);
            }

            // pan direction (smoothed), for prefetch
            var mv = _focus - _prevFocus; _prevFocus = _focus; mv.y = 0f;
            _panDir = Vector3.Lerp(_panDir, mv, 0.2f);

            WantedLod0(_want, Vector3.zero, 0f, GameSettings.Lod0Want, LOD0_MAX_DIST);
            WantedLod0(_keepRing, Vector3.zero, CH, 25, LOD0_MAX_DIST * 1.2f);   // hysteresis: one chunk ring further, a bit further out
            // show wanted chunks; keep already-shown ones inside the ring while under the active budget
            int active = 0;
            for (int i = 0; i < _want.Count; i++) if (_lod0.ContainsKey(_want[i])) active++;
            _lodTmp.Clear(); foreach (var kv in _lod0) _lodTmp.Add(kv.Key);
            for (int i = 0; i < _lodTmp.Count; i++)
            {
                int c = _lodTmp[i]; var go = _lod0[c];
                if (go == null) continue;
                bool on = _want.Contains(c);
                if (!on && go.activeSelf && _keepRing.Contains(c) && active < LOD0_ACTIVE_MAX) { on = true; active++; }
                if (on) _lod0Seen[c] = frame;
                if (go.activeSelf != on)
                {
                    go.SetActive(on);
                    if (c < _lod1.Length && _lod1[c] != null) _lod1[c].SetActive(!on);
                }
            }

            // next build
            if (_job == null)
            {
                int next = -1;
                for (int i = 0; i < _want.Count && next < 0; i++) if (!_lod0.ContainsKey(_want[i])) next = _want[i];
                if (WorkerReady())
                {
                    if (next < 0 && _panDir.sqrMagnitude > 0.0004f)
                    {
                        WantedLod0(_pre, _panDir.normalized * CH, 0f, GameSettings.Lod0Want, LOD0_MAX_DIST);
                        for (int i = 0; i < _pre.Count && next < 0; i++) if (!_lod0.ContainsKey(_pre[i])) next = _pre[i];
                    }
                    if (next >= 0)
                    {
                        var gen = _wgen; var lots = _buckets[next]; int cx = next % _ncx, cy = next / _ncx; float og = OvergrowthLevel(); bool vn = GameSettings.Vines;
                        _jobC = next; _jobVer = _lod0Ver;
                        _job = System.Threading.Tasks.Task.Run(() => { var ch = gen.BuildChunk(cx, cy, lots, 0); FxOvergrowth.Apply(ch.Mesh, og, vn); return ch; });
                    }
                }
                else if (next >= 0 && !uploaded && (_wgenFailed || _wgenTask != null)) MakeLod0(next);   // worker not up (yet): old synchronous path
            }

            // evict least recently shown hidden chunks over the count / vertex budget
            for (int guard = 0; guard < 4; guard++)
            {
                long verts = 0; foreach (var kv in _lod0Verts) verts += kv.Value;
                if (_lod0.Count <= LOD0_CACHE && verts <= LOD0_CACHE_VERTS) break;
                int victim = -1, oldest = int.MaxValue;
                foreach (var kv in _lod0)
                    if (kv.Value != null && !kv.Value.activeSelf && _lod0Seen.TryGetValue(kv.Key, out int f) && f < oldest) { oldest = f; victim = kv.Key; }
                if (victim < 0) break;
                DestroyChunk(_lod0[victim]); _lod0.Remove(victim); _lod0Seen.Remove(victim); _lod0Verts.Remove(victim);
            }
        }

        static void DestroyChunk(GameObject go)
        {
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>()) if (mf.sharedMesh != null) Destroy(mf.sharedMesh);
            Destroy(go);
        }

        void EnsureMaterials()
        {
            if (_objMat != null) return;
            var sh = Resources.Load<Shader>("Shaders/DayZeroMap");
            if (sh == null) sh = Shader.Find("DayZero/Map");
            var ss = Resources.Load<Shader>("Shaders/DayZeroShadow");
            if (ss == null) ss = Shader.Find("DayZero/MapShadow");
            if (ss != null && ss.isSupported)
            {
                _shadowMat = new Material(ss) { name = "MapShadows" };
                // selection glow: same alpha decal shader, drawn just before the shadows so the stencil lets it win
                _glowMat = new Material(ss) { name = "SelectionGlow", renderQueue = 2009 };
            }
            if (sh != null && sh.isSupported)
            {
                _objMat = new Material(sh) { name = "MapObjects" };
                RenderWarning = "";
                Debug.Log("[MapView] using shader DayZero/Map");
                return;
            }
            string why = sh == null ? "missing" : "unsupported on this GPU";
            // Every mesh is vertex-coloured, so any shader that multiplies vertex colour works (no haze).
            var ic = Shader.Find("Hidden/Internal-Colored");
            if (ic != null && ic.isSupported)
            {
                _objMat = new Material(ic) { name = "MapObjects (fallback)" };
                _objMat.SetInt("_SrcBlend", (int)BlendMode.One);
                _objMat.SetInt("_DstBlend", (int)BlendMode.Zero);
                _objMat.SetInt("_Cull", (int)CullMode.Off);
                _objMat.SetInt("_ZWrite", 1);
                _objMat.SetInt("_ZTest", (int)CompareFunction.LessEqual);
                RenderWarning = "Map shader " + why + ": fallback Hidden/Internal-Colored (no haze)";
            }
            else
            {
                var sp = Shader.Find("Sprites/Default");
                if (sp == null) sp = Shader.Find("UI/Default");
                if (sp != null) { _objMat = new Material(sp) { name = "MapObjects (fallback)", renderQueue = 2000 }; RenderWarning = "Map shader " + why + ": fallback " + sp.name + " (no depth, no haze)"; }
                else { _objMat = new Material(Shader.Find("Standard")); RenderWarning = "Map shader " + why + ": no vertex-colour fallback found"; }
            }
            _shadowMat = null; _glowMat = null;
            Debug.LogWarning("[MapView] " + RenderWarning);
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
            if (mat != null && mat == _shadowMat) r.enabled = GameSettings.Shadows;   // v0.18 settings
            return go;
        }

        /// <summary>Flat haze-coloured border around the map so the edge fades out. Split into 64-unit cells.</summary>
        void MakeSkirt(Data d)
        {
            var g = new Geo(new MeshData(), null);
            float W = d.W, H = d.H, pad = 384f, y = -0.06f, step = 64f;
            Color c = new Color(0.30f, 0.32f, 0.26f);
            for (float z0 = -pad; z0 < H + pad; z0 += step)
                for (float x0 = -pad; x0 < W + pad; x0 += step)
                {
                    float x1 = Mathf.Min(x0 + step, W + pad), z1 = Mathf.Min(z0 + step, H + pad);
                    if (x0 >= 0f && x1 <= W && z0 >= 0f && z1 <= H) continue;
                    AddSkirtRect(g, x0, x1, z0, z1, W, H, y, c);
                }
            if (g.M.Count > 0) MakeObject("Ground Skirt", ToMesh("Ground Skirt", g.M), _objMat, _root.transform);
        }

        static void Rect2(Geo g, float x0, float x1, float y0, float y1, float h, Color c)
            => g.Flat(new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1), h, c, c, c, c);

        static void AddSkirtRect(Geo g, float x0, float x1, float y0, float y1, float W, float H, float h, Color c)
        {
            float ix0 = Mathf.Max(x0, 0f), ix1 = Mathf.Min(x1, W), iy0 = Mathf.Max(y0, 0f), iy1 = Mathf.Min(y1, H);
            if (ix0 >= ix1 || iy0 >= iy1) { Rect2(g, x0, x1, y0, y1, h, c); return; }
            if (y0 < iy0) Rect2(g, x0, x1, y0, iy0, h, c);
            if (iy1 < y1) Rect2(g, x0, x1, iy1, y1, h, c);
            if (x0 < ix0) Rect2(g, x0, ix0, iy0, iy1, h, c);
            if (ix1 < x1) Rect2(g, ix1, x1, iy0, iy1, h, c);
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
                Labels.Add(new MapLabel { Pos = new Vector3(c.x, _gen.LotH[ll.Lot] + 0.6f, -c.y), Text = ll.Name, Tier = Mathf.Clamp(ll.Tier, 0, 1) });
            }
        }

        // ---------------- dynamic layer: camps, markers, lit windows ----------------
        void UpdateDynamic()
        {
            var g = G;
            if (g?.S == null || _gen == null) return;
            var bases = g.AllBases();
            var lit = g.LitLots();
            var conv = g.S.Conv;
            var sb = new System.Text.StringBuilder();
            foreach (var b in bases) sb.Append(b.Id).Append('@').Append(b.Pos.x).Append(',').Append(b.Pos.y).Append(b.Main ? 'M' : 'b').Append(b.Col).Append(';');
            sb.Append('|');
            foreach (var kv in conv) sb.Append(kv.Key).Append(':').Append(kv.Value.Col).Append(';');
            sb.Append('|');
            foreach (var kv in lit) sb.Append(kv.Key).Append(':').Append(kv.Value).Append(';');
            // v0.12.0a: walls (tier + hp in quarters), breaches, towers, traps, conversions being set up
            var S = g.S;
            sb.Append('|').Append(S.Walls?.Count ?? 0).Append('w');
            foreach (var kv in S.Fences) sb.Append(kv.Key).Append(kv.Value.Tier).Append((int)(kv.Value.Hp / Math.Max(1.0, g.FenceTileMax(kv.Key)) * 4.0)).Append(S.Gates.ContainsKey(kv.Key) ? 'g' : ',');
            sb.Append('|');
            if (S.Breaches != null) foreach (var kv in S.Breaches) sb.Append(kv.Key).Append(',');
            sb.Append('|');
            foreach (var kv in S.Towers) sb.Append(kv.Key).Append(kv.Value.Tier).Append(',');
            foreach (var kv in S.Traps) sb.Append(kv.Key).Append(kv.Value.Kind).Append(kv.Value.Hp > 0 ? 'a' : 'x').Append(',');
            sb.Append('|');
            foreach (var kv in conv) sb.Append(kv.Value.Type).Append(kv.Value.Ready > S.T ? (int)(g.ConvProgress(kv.Key) * 5.0) : 9).Append(',');
            sb.Append('|').Append(FxCampGrowth.Key(g));   // v0.16 idea #12: camps grow with their facilities
            string key = sb.ToString();
            if (key == _dynKey) return;
            _dynKey = key;

            if (_dyn != null) DestroyChunk(_dyn);
            _dyn = new GameObject("Map Dynamic");
            _dyn.transform.SetParent(transform, false);
            var ch = _gen.BuildDynamic(g);
            FxCampGrowth.Build(_gen, g, ch);
            if (ch.Mesh.Count > 0) MakeObject("Dynamic", ToMesh("Dynamic", ch.Mesh), _objMat, _dyn.transform);
            if (_shadowMat != null && ch.Shadow.Count > 0) MakeObject("Dynamic Shadows", ToMesh("Dynamic Shadows", ch.Shadow), _shadowMat, _dyn.transform);
        }
    }
}
