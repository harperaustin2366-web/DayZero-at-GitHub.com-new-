// v0.15 graphics: falling weather drawn over the live map, NO new shader.
//  - rain / storm: slanted streaks, snow: drifting flakes, storm: an occasional brief white lightning flash
//  - fog is the existing FxView haze (FxSky fog multipliers), nothing extra here
//  - screen-space quads pushed with GL in OnGUI (Repaint) through ONE "Sprites/Default" material (the shader
//    MapView / FxLights already use), drawn behind the game UI (GUI.depth 999: in front of the FxOverlay tint)
//  - at most 300 particles (phones), count scaled by Game.WeatherIntensity
// Kind = FxWeather.Current. When the sim weather (Game.CurrentWeather, expansion rules) changes it is copied
// into FxWeather.Current, so a manual FxWeather.Cycle() still sticks until the next sim change.
// Visual only: reads the sim, never writes to it; uses UnityEngine.Random (not the sim RNG).
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed class FxWeatherView : MonoBehaviour
    {
        const int MAX = 300;
        static FxWeatherView _inst;
        static int _lastSim = -1;
        internal static float Intensity;      // 0..1
        internal static int Frame = -10;

        Material _mat;
        Texture2D _white;
        readonly Vector2[] _p = new Vector2[MAX];   // normalised screen pos (0..1, y down)
        readonly float[] _s = new float[MAX];        // per-particle speed / size factor
        float _flash, _nextBolt = 8f;
        FxWeatherKind _kind;

        /// <summary>Called from FxView.Apply every map frame.</summary>
        public static void Tick(Game g)
        {
            if (g?.S != null)
            {
                int sim = (int)g.CurrentWeather;
                if (sim != _lastSim) { _lastSim = sim; if (Game.ExpansionRules) FxWeather.Current = (FxWeatherKind)Mathf.Clamp(sim, 0, 4); }
            }
            var k = FxWeather.Current;
            float simI = g?.S != null && Game.ExpansionRules ? g.WeatherIntensity : 0f;
            // manual / non-expansion weather has no sim intensity: use the kind's default strength
            float def = k == FxWeatherKind.Rain ? 0.6f : k == FxWeatherKind.Storm ? 1f : k == FxWeatherKind.Snow ? 0.8f : 0f;
            Intensity = simI > 0.01f ? simI : def;
            Frame = Time.frameCount;
            if (_inst == null && (k == FxWeatherKind.Rain || k == FxWeatherKind.Storm || k == FxWeatherKind.Snow))
            {
                var go = new GameObject("FxWeatherView"); Object.DontDestroyOnLoad(go);
                _inst = go.AddComponent<FxWeatherView>();
            }
        }

        void Awake()
        {
            for (int i = 0; i < MAX; i++) { _p[i] = new Vector2(Random.value, Random.value); _s[i] = 0.6f + Random.value * 0.8f; }
        }

        bool Ready()
        {
            if (_mat != null) return true;
            var sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("UI/Default");
            if (sh == null) return false;
            _white = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave };
            _white.SetPixel(0, 0, Color.white); _white.Apply();
            _mat = new Material(sh) { mainTexture = _white, hideFlags = HideFlags.DontSave };
            return true;
        }

        void Update()
        {
            if (Time.frameCount - Frame > 2) return;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            _kind = FxWeather.Current;
            bool snow = _kind == FxWeatherKind.Snow, storm = _kind == FxWeatherKind.Storm;
            float t = Time.unscaledTime;
            float aspect = Screen.height > 0 ? (float)Screen.width / Screen.height : 1.7f;
            for (int i = 0; i < MAX; i++)
            {
                var p = _p[i];
                if (snow) { p.y += dt * 0.09f * _s[i]; p.x += dt * (0.02f * Mathf.Sin(t * 1.3f + i) + 0.01f) / aspect; }
                else { float v = (storm ? 1.9f : 1.4f) * _s[i]; p.y += dt * v; p.x += dt * v * (storm ? 0.32f : 0.14f) / aspect; }
                if (p.y > 1.05f) { p.y -= 1.1f; p.x = Random.value; }
                if (p.x > 1.02f) p.x -= 1.04f; else if (p.x < -0.02f) p.x += 1.04f;
                _p[i] = p;
            }
            // lightning: a double pulse every 7-18 s during a storm, more often at full strength
            if (storm)
            {
                _nextBolt -= dt;
                if (_nextBolt <= 0f) { _flash = 0.45f; _nextBolt = Mathf.Lerp(18f, 7f, Intensity) * (0.6f + Random.value * 0.8f); }
            }
            _flash = Mathf.Max(0f, _flash - dt);
        }

        void OnGUI()
        {
            if (Event.current.type != EventType.Repaint) return;
            if (Time.frameCount - Frame > 2 || Time.frameCount - FxView.Frame > 2) return;   // map not live
            var k = FxWeather.Current;
            bool snow = k == FxWeatherKind.Snow, storm = k == FxWeatherKind.Storm;
            if (!(snow || storm || k == FxWeatherKind.Rain) || Intensity <= 0.01f) return;
            if (!Ready()) return;
            GUI.depth = 999;
            float W = Screen.width, H = Screen.height, u = H / 720f;   // u: resolution-independent pixel unit
            int n = Mathf.Clamp(Mathf.RoundToInt(MAX * Mathf.Clamp01(Intensity)), 0, MAX);
            if (!snow && !storm) n = Mathf.Min(n, 200);

            GL.PushMatrix();
            GL.LoadPixelMatrix();             // (0,0) bottom-left
            _mat.SetPass(0);
            GL.Begin(GL.QUADS);
            if (snow)
            {
                for (int i = 0; i < n; i++)
                {
                    float x = _p[i].x * W, y = (1f - _p[i].y) * H, r = (1.4f + 1.6f * _s[i]) * u;
                    GL.Color(new Color(0.95f, 0.97f, 1f, 0.55f + 0.3f * (_s[i] - 0.6f)));
                    GL.Vertex3(x - r, y - r, 0); GL.Vertex3(x - r, y + r, 0); GL.Vertex3(x + r, y + r, 0); GL.Vertex3(x + r, y - r, 0);
                }
            }
            else
            {
                float slant = storm ? 0.32f : 0.14f, len = (storm ? 34f : 26f) * u, w = 0.9f * u;
                var dir = new Vector2(slant, -1f).normalized;   // screen motion direction (y up)
                var side = new Vector2(-dir.y, dir.x) * w;
                var col = storm ? new Color(0.78f, 0.84f, 0.95f, 0.30f) : new Color(0.75f, 0.82f, 0.92f, 0.26f);
                var tail = new Color(col.r, col.g, col.b, 0f);
                for (int i = 0; i < n; i++)
                {
                    float x = _p[i].x * W, y = (1f - _p[i].y) * H, l = len * _s[i];
                    float hx = x, hy = y, tx = x - dir.x * l, ty = y - dir.y * l;
                    GL.Color(tail); GL.Vertex3(tx - side.x, ty - side.y, 0); GL.Vertex3(tx + side.x, ty + side.y, 0);
                    GL.Color(col); GL.Vertex3(hx + side.x, hy + side.y, 0); GL.Vertex3(hx - side.x, hy - side.y, 0);
                }
            }
            GL.End();
            GL.PopMatrix();

            if (storm && _flash > 0f)
            {
                // two quick pulses: 0.45 -> 0.30 bright, a short gap, then a softer second strike
                float f = _flash > 0.30f ? (_flash - 0.30f) / 0.15f : _flash > 0.22f ? 0f : _flash / 0.22f * 0.6f;
                var old = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, 0.5f * Mathf.Clamp01(f));
                GUI.DrawTexture(new Rect(0, 0, W, H), _white);
                GUI.color = old;
            }
        }

        void OnDestroy() { if (_mat != null) Destroy(_mat); if (_white != null) Destroy(_white); if (_inst == this) _inst = null; }
    }
}
