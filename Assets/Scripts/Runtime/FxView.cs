// v0.15 graphics: pushes FxSky (time of day) into what the v0.14 map already renders with, with NO new shaders:
//  - distance haze: the DayZero/Map + DayZero/MapShadow globals _DZFogColor / _DZFogParams (same encoding MapView uses)
//  - camera clear colour, RenderSettings fog colour / distances / ambient (harmless for the unlit map shader)
//  - an overall time-of-day tint (golden hour orange, night deep blue ~45%) and an optional "film look"
//    (slight green-grey wash + procedural vignette) drawn as a full-screen IMGUI overlay BEHIND the game UI
//    (GUI.depth 1000), so it needs no material / shader of its own and can never be stripped from the build.
// Visual only: reads the sim clock (Game.S.T), never writes to the sim. Film look toggled by PlayerPrefs "filmlook" (default 1).
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public static class FxView
    {
        public const string PREF_FILM = "filmlook";
        static readonly Color NIGHT = new Color(0.04f, 0.07f, 0.22f, 1f);
        static readonly Color GOLD = new Color(1.00f, 0.55f, 0.18f, 1f);
        static readonly Color FILM = new Color(0.40f, 0.46f, 0.38f, 1f);

        internal static Color Tint;          // rgb + alpha of the time-of-day wash
        internal static bool Film = true;
        internal static int Frame = -10;     // last frame Apply ran (overlay only draws while the map is live)
        static FxOverlay _ov;
        static int _filmCheckFrame = -1000;

        /// <summary>Hour of day (fractional) from the sim clock; 13:00 when no game is running.</summary>
        public static float HourOf(Game g) => g?.S == null ? 13f : (float)(((g.S.T % 24.0) + 24.0) % 24.0);

        /// <summary>Called every frame from MapView.Update after the camera is placed. camDist = MapView zoom distance.</summary>
        public static void Apply(Camera cam, Game g, float camDist)
        {
            var s = FxSky.Eval(HourOf(g), FxWeather.Current, false);

            // haze: same start / end encoding as MapView.UpdateCamera, scaled by the weather multipliers
            float start = camDist * 0.9f * s.FogStartMul, end = (camDist * 2.6f + 20f) * s.FogEndMul;
            Shader.SetGlobalColor("_DZFogColor", s.Fog);
            Shader.SetGlobalVector("_DZFogParams", new Vector4(start, Mathf.Max(start + 1f, end), s.FogK, 0f));
            RenderSettings.fogColor = s.Fog;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = start;
            RenderSettings.fogEndDistance = Mathf.Max(start + 1f, end);
            RenderSettings.ambientLight = s.Amb;
            if (cam != null) cam.backgroundColor = s.Fog;

            // tint: night = how far the ambient drops below noon; golden = warm sun light while it is not night
            float lum = s.Amb.r * 0.3f + s.Amb.g * 0.59f + s.Amb.b * 0.11f;
            float night = Mathf.Clamp01((1f - lum) / 0.7f);
            float golden = Mathf.Clamp01((s.SunCol.r - s.SunCol.b) * 2.2f) * (1f - night);
            float a = Mathf.Max(night * 0.45f, golden * 0.20f);
            var c = Color.Lerp(GOLD, NIGHT, night / Mathf.Max(1e-3f, night + golden));
            Tint = new Color(c.r, c.g, c.b, a);

            if (Time.frameCount - _filmCheckFrame > 30) { _filmCheckFrame = Time.frameCount; Film = PlayerPrefs.GetInt(PREF_FILM, 1) != 0; }
            Frame = Time.frameCount;
            FxLights.Tick(cam, g, s.Lights);   // night light pools around fires / towers / lit lots
            if (_ov == null)
            {
                var go = new GameObject("FxOverlay"); Object.DontDestroyOnLoad(go);
                _ov = go.AddComponent<FxOverlay>();
            }
        }

        public static void SetFilmLook(bool on) { PlayerPrefs.SetInt(PREF_FILM, on ? 1 : 0); Film = on; _filmCheckFrame = Time.frameCount; }

        internal static Color FilmWash => new Color(FILM.r, FILM.g, FILM.b, 0.07f);
    }

    /// <summary>Full-screen time-of-day tint + film look, drawn first (behind the game UI) with plain IMGUI textures.</summary>
    public sealed class FxOverlay : MonoBehaviour
    {
        Texture2D _white, _vig;

        void OnGUI()
        {
            if (Event.current.type != EventType.Repaint) return;
            if (Time.frameCount - FxView.Frame > 2) return;   // map not live (menus, loading): draw nothing
            GUI.depth = 1000;                                  // higher depth = behind GameUI windows
            if (_white == null) { _white = new Texture2D(1, 1, TextureFormat.RGBA32, false); _white.SetPixel(0, 0, Color.white); _white.Apply(); _white.hideFlags = HideFlags.DontSave; }
            var r = new Rect(0, 0, Screen.width, Screen.height);
            var old = GUI.color;
            if (FxView.Tint.a > 0.004f) { GUI.color = FxView.Tint; GUI.DrawTexture(r, _white); }
            if (FxView.Film)
            {
                GUI.color = FxView.FilmWash; GUI.DrawTexture(r, _white);
                if (_vig == null) _vig = MakeVignette(128);
                GUI.color = Color.white; GUI.DrawTexture(r, _vig, ScaleMode.StretchToFill, true);
            }
            GUI.color = old;
        }

        /// <summary>Black with alpha rising toward the edges (clear centre, ~55% in the corners).</summary>
        static Texture2D MakeVignette(int n)
        {
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
                    float d = Mathf.Sqrt(u * u * 0.85f + v * v) / 1.25f;          // landscape: a touch wider
                    float k = Mathf.Clamp01((d - 0.45f) / 0.55f); k = k * k * (3f - 2f * k);
                    px[y * n + x] = new Color32(0, 0, 0, (byte)(k * 140f));
                }
            t.SetPixels32(px); t.Apply(false, true);
            return t;
        }

        void OnDestroy() { if (_white != null) Destroy(_white); if (_vig != null) Destroy(_vig); }
    }
}
