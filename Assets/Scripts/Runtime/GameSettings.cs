// v0.18 (batch B1, phone feel): player settings that are not part of the simulation.
//   E1 graphics quality preset (LOW / MEDIUM / HIGH) + individual toggles: vines / overgrowth, weather fx, crows and
//      paper, fires and smoke, shadows and light pools, film look. The preset also sets how many full-detail map
//      chunks stream in and the frame cap (LOW: fewer chunks, at most 8 active, 30 fps).
//   E2 battery saver: 30 fps cap; with no touch for 20 s while the game is paused (or a decision / the title screen
//      is up) it drops to 15 fps until the next touch.
//   E3 UI scale (0.8x .. 1.4x) on top of the screen fit; GameUI's virtual height reads UiScale, so every screen and
//      the touch mapping scale together. A change is applied at the start of the next frame (never between two
//      IMGUI events of the same frame).
//   E4 haptics: short vibrations on zombie hits on survivors, the raid siren, a fence / gate broken through and a
//      survivor death. Read-only subscriptions to Game.SfxRequested / Game.LogAdded plus an HP comparison between
//      frames, so the simulation and its RNG order are untouched.
// Everything persists in PlayerPrefs ("set_*" keys; film look keeps FxView's own "filmlook" key).
// The pure part (fields, presets, limits) also compiles in the offline preview (DZ_PREVIEW); the Unity driver,
// persistence and the MapView apply hooks do not.
using System;
using System.Collections.Generic;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public static class GameSettings
    {
        public const int LOW = 0, MEDIUM = 1, HIGH = 2;
        public static readonly string[] QUALITY_NAMES = { "LOW", "MEDIUM", "HIGH" };
        public const float UI_MIN = 0.8f, UI_MAX = 1.3f, UI_STEP = 0.1f;
        public const float IDLE_SECONDS = 20f;

        public static int Quality = HIGH;
        public static bool Vines = true, Weather = true, Crows = true, Fires = true, Shadows = true, Film = true;
        public static bool BatterySaver, Haptics = true;
        /// <summary>Applied UI scale (GameUI reads this). Change it with SetUiScale.</summary>
        public static float UiScale { get; private set; } = 1f;
        static float _pendingUi = -1f;
        /// <summary>Bumped on every change (UI / apply hooks compare it).</summary>
        public static int Version;

        // ---- limits the rest of the game reads (all cheap, safe to read from the LOD0 worker thread) ----
        /// <summary>Full-detail (LOD0) chunks wanted around the view.</summary>
        public static int Lod0Want => Quality == LOW ? 4 : Quality == MEDIUM ? 7 : 9;
        /// <summary>Most full-detail chunks kept shown at once (wanted + hysteresis ring).</summary>
        public static int Lod0Active => Quality == LOW ? 8 : Quality == MEDIUM ? 10 : 12;
        /// <summary>Frame cap without the idle drop.</summary>
        public static int BaseFps => BatterySaver || Quality == LOW ? 30 : 60;

        /// <summary>True when the individual toggles are exactly what preset q sets.</summary>
        public static bool MatchesPreset(int q)
        {
            Preset(q, out bool v, out bool w, out bool c, out bool f, out bool s, out bool fl);
            return v == Vines && w == Weather && c == Crows && f == Fires && s == Shadows && fl == Film;
        }

        public static void Preset(int q, out bool vines, out bool weather, out bool crows, out bool fires, out bool shadows, out bool film)
        {
            vines = q >= MEDIUM; weather = true; crows = q >= HIGH; fires = q >= MEDIUM; shadows = q >= MEDIUM; film = q >= HIGH;
        }

        public static void SetQuality(int q)
        {
            Quality = Mathf.Clamp(q, LOW, HIGH);
            Preset(Quality, out Vines, out Weather, out Crows, out Fires, out Shadows, out bool film);
            SetFilm(film, false);
            Changed();
        }

        public static void SetFilm(bool on, bool notify = true)
        {
            Film = on;
#if !DZ_PREVIEW
            FxView.SetFilmLook(on);
#endif
            if (notify) Changed();
        }

        public static void SetUiScale(float s) { _pendingUi = Mathf.Clamp((float)Math.Round(s * 10f) / 10f, UI_MIN, UI_MAX); }
        public static float UiScaleShown => _pendingUi > 0f ? _pendingUi : UiScale;
        /// <summary>Moves a pending UI scale into UiScale (called once per frame before any OnGUI).</summary>
        public static bool ApplyPendingUi()
        {
            if (_pendingUi <= 0f) return false;
            bool ch = Mathf.Abs(_pendingUi - UiScale) > 0.001f;
            UiScale = _pendingUi; _pendingUi = -1f;
            if (ch) Changed();
            return ch;
        }

        public static void Changed()
        {
            Version++;
#if !DZ_PREVIEW
            Save();
#endif
        }

        public static string UiScaleText(float s) => s.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "x";

#if !DZ_PREVIEW
        static bool _loaded;

        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                Quality = Mathf.Clamp(PlayerPrefs.GetInt("set_quality", HIGH), LOW, HIGH);
                Vines = PlayerPrefs.GetInt("set_vines", 1) != 0;
                Weather = PlayerPrefs.GetInt("set_weather", 1) != 0;
                Crows = PlayerPrefs.GetInt("set_crows", 1) != 0;
                Fires = PlayerPrefs.GetInt("set_fires", 1) != 0;
                Shadows = PlayerPrefs.GetInt("set_shadows", 1) != 0;
                Film = PlayerPrefs.GetInt(FxView.PREF_FILM, 1) != 0;
                BatterySaver = PlayerPrefs.GetInt("set_battery", 0) != 0;
                Haptics = PlayerPrefs.GetInt("set_haptics", 1) != 0;
                UiScale = Mathf.Clamp(PlayerPrefs.GetFloat("set_uiscale", 1f), UI_MIN, UI_MAX);
            }
            catch (Exception e) { Debug.LogWarning("[GameSettings] load failed: " + e.Message); }
            Version++;
        }

        static void Save()
        {
            try
            {
                PlayerPrefs.SetInt("set_quality", Quality);
                PlayerPrefs.SetInt("set_vines", Vines ? 1 : 0);
                PlayerPrefs.SetInt("set_weather", Weather ? 1 : 0);
                PlayerPrefs.SetInt("set_crows", Crows ? 1 : 0);
                PlayerPrefs.SetInt("set_fires", Fires ? 1 : 0);
                PlayerPrefs.SetInt("set_shadows", Shadows ? 1 : 0);
                PlayerPrefs.SetInt("set_battery", BatterySaver ? 1 : 0);
                PlayerPrefs.SetInt("set_haptics", Haptics ? 1 : 0);
                PlayerPrefs.SetFloat("set_uiscale", UiScale);
                PlayerPrefs.Save();
            }
            catch (Exception e) { Debug.LogWarning("[GameSettings] save failed: " + e.Message); }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            Load();
            if (GameSettingsDriver.Instance != null) return;
            var go = new GameObject("GameSettings");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<GameSettingsDriver>();
        }
#endif
    }

#if !DZ_PREVIEW
    /// <summary>Per-frame side of the settings: frame cap / battery idle drop, pending UI scale, applying graphics
    /// toggles to the map, and haptics. Visual / device only: never writes to the sim.</summary>
    [DefaultExecutionOrder(-50)]
    public sealed class GameSettingsDriver : MonoBehaviour
    {
        public static GameSettingsDriver Instance { get; private set; }
        float _lastTouch;
        int _appliedVer = -1, _appliedVinesShadows = -1;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
            _lastTouch = Time.unscaledTime;
        }

        void Update()
        {
            GameSettings.Load();
            GameSettings.ApplyPendingUi();

            // ---- E2 frame cap ----
            if (Input.touchCount > 0 || Input.GetMouseButton(0) || Input.anyKeyDown) _lastTouch = Time.unscaledTime;
            var b = Bootstrap.Instance;
            bool paused = b == null || b.State != Bootstrap.Screen2.Playing || b.Paused || b.Pending.Count > 0 || b.Ended;
            int fps = GameSettings.BaseFps;
            if (GameSettings.BatterySaver && paused && Time.unscaledTime - _lastTouch > GameSettings.IDLE_SECONDS) fps = 15;
            if (Application.targetFrameRate != fps) Application.targetFrameRate = fps;
            if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;   // targetFrameRate is ignored while vSync is on

            // ---- E1 apply map-side toggles when they change ----
            if (_appliedVer != GameSettings.Version)
            {
                _appliedVer = GameSettings.Version;
                int vs = (GameSettings.Vines ? 1 : 0) | (GameSettings.Shadows ? 2 : 0) | (GameSettings.Quality << 2);
                var map = MapView.Instance;
                if (map != null && vs != _appliedVinesShadows)
                {
                    bool vinesChanged = _appliedVinesShadows >= 0 && ((vs ^ _appliedVinesShadows) & 1) != 0;
                    _appliedVinesShadows = vs;
                    map.ApplySettings(vinesChanged);
                }
            }

            HookGame(b?.Game);
        }

        // ---------------- E4 haptics ----------------
        Game _hooked;
        int _hitFrame = -100;
        float _lastBuzz = -10f, _lastHitBuzz = -10f;
        readonly Dictionary<int, double> _hp = new Dictionary<int, double>();
        int _hpCol = -1;

        void HookGame(Game g)
        {
            if (ReferenceEquals(g, _hooked)) return;
            if (_hooked != null) { _hooked.SfxRequested -= OnSfx; _hooked.LogAdded -= OnLog; }
            _hooked = g;
            _hp.Clear(); _hpCol = -1;
            if (g != null) { g.SfxRequested += OnSfx; g.LogAdded += OnLog; }
        }

        void OnDestroy() { HookGame(null); if (Instance == this) Instance = null; }

        void OnSfx(string name, Vector2 at)
        {
            if (name == "hit" || name == "scream" || name == "groan") _hitFrame = Time.frameCount;
        }

        void OnLog(LogEntry e)
        {
            var g = _hooked;
            if (e == null || g?.S == null || !GameSettings.Haptics) return;
            if (e.Col != Mathf.Max(0, g.ViewCol)) return;   // only the colony the player is looking after
            if (e.Kind == "death") Buzz(140, 255, true);
            else if (e.Kind == "siren") Buzz(220, 200, true);
            else if (e.Kind == "loss" && e.Text != null && e.Text.Contains("broke through the")) Buzz(110, 230, true);
        }

        void LateUpdate()
        {
            // zombie hits on survivors: a survivor of the viewed colony lost a chunk of HP in the same frame the sim
            // played a melee hit / scream / groan (starvation and healing never trip it)
            var g = _hooked;
            if (g?.S == null || g.S.Cols == null) { _hp.Clear(); return; }
            int col = Mathf.Max(0, g.ViewCol);
            if (col >= g.S.Cols.Count) return;
            if (col != _hpCol) { _hp.Clear(); _hpCol = col; }
            bool hitNow = Time.frameCount - _hitFrame <= 1, hurt = false;
            var people = g.S.Cols[col].People;
            for (int i = 0; i < people.Count; i++)
            {
                var p = people[i];
                if (p == null) continue;
                if (_hp.TryGetValue(p.Id, out double old) && p.Hp < old - 4.0) hurt = true;
                _hp[p.Id] = p.Hp;
            }
            if (_hp.Count > people.Count + 64) _hp.Clear();   // dead / gone survivors: start over now and then
            if (hurt && hitNow && GameSettings.Haptics && Time.unscaledTime - _lastHitBuzz > 0.6f)
            {
                _lastHitBuzz = Time.unscaledTime;
                Buzz(35, 120, false);
            }
        }

        void Buzz(int ms, int amplitude, bool important)
        {
            float now = Time.unscaledTime;
            if (now - _lastBuzz < (important ? 0.15f : 0.35f)) return;
            _lastBuzz = now;
            Haptic.Vibrate(ms, amplitude);
        }
    }

    /// <summary>Short vibration. Android: VibrationEffect.createOneShot (API 26+) or vibrate(ms); elsewhere nothing.</summary>
    public static class Haptic
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        static AndroidJavaObject _vib;
        static int _sdk = -1;
        static bool _failed;
#endif

        public static void Vibrate(int ms, int amplitude = -1)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_failed) return;
            try
            {
                if (_vib == null)
                {
                    using (var up = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                    using (var act = up.GetStatic<AndroidJavaObject>("currentActivity"))
                        _vib = act.Call<AndroidJavaObject>("getSystemService", "vibrator");
                    using (var ver = new AndroidJavaClass("android.os.Build$VERSION")) _sdk = ver.GetStatic<int>("SDK_INT");
                }
                if (_vib == null) { _failed = true; return; }
                if (_sdk >= 26)
                {
                    using (var ve = new AndroidJavaClass("android.os.VibrationEffect"))
                    using (var eff = ve.CallStatic<AndroidJavaObject>("createOneShot", (long)ms, amplitude <= 0 ? -1 : Mathf.Clamp(amplitude, 1, 255)))
                        _vib.Call("vibrate", eff);
                }
                else _vib.Call("vibrate", (long)ms);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Haptic] " + e.Message);
                _failed = true;
                Handheld.Vibrate();   // also makes Unity add the VIBRATE permission to the manifest
            }
#endif
        }
    }

    // MapView side of the graphics toggles (vines need the full-detail chunks re-streamed; shadow meshes are only
    // switched on / off). The tiny reads in MapView.cs / FxView.cs (GameSettings.Vines / Shadows / Lod0* / Fires /
    // Crows / Weather) do the rest.
    public sealed partial class MapView
    {
        public void ApplySettings(bool vinesChanged)
        {
            try
            {
                bool on = GameSettings.Shadows;
                if (_shadowMat != null)
                    foreach (var r in GetComponentsInChildren<MeshRenderer>(true))
                        if (r != null && r.sharedMaterial == _shadowMat) r.enabled = on;
                if (vinesChanged && Ready && !Building) InvalidateLod0();
            }
            catch (Exception e) { Debug.LogWarning("[GameSettings] apply: " + e.Message); }
        }
    }
#endif
}
