// v0.15 graphics quick wins: time of day + weather -> one set of render parameters (FxSkyState) that MapView /
// FxView push into the shader globals of DayZero/MapFx, DayZero/MapShadow and the Fx* shaders.
// Pure C# (UnityEngine value types only) so the offline preview (work/unity-verify/FxPreview) evaluates the SAME
// curves as the game. Visual only: reads the sim clock (Game.S.T), never writes to the sim.
//
// Conventions: world = (tile x, height, -tile y); SunDir points TOWARD the sun (or the moon at night). The noon
// sun matches the direction baked into the vertex colours and the static ground shadows (Geo.SUN), so midday
// looks exactly like v0.14; morning / evening rotate away from it and drop toward the horizon.
//
// Encoding rule for every shader global: an all-zero vector means "no change" (v0.14 look), so a frame where
// the globals were never set (or a device that drops them) can never turn the map black or white.
using System;
using UnityEngine;

namespace DayZero.Runtime
{
    public enum FxWeatherKind { Clear = 0, Rain = 1, Storm = 2, Fog = 3, Snow = 4 }

    /// <summary>Weather the view draws. The sim has no weather yet (v0.14 Core): until Game exposes
    /// CurrentWeather / CurrentSeason this local value (default Clear) is used; settings / preview can cycle it.</summary>
    public static class FxWeather
    {
        public static FxWeatherKind Current { get; set; } = FxWeatherKind.Clear;
        public const int Count = 5;
        public static FxWeatherKind Cycle() { Current = (FxWeatherKind)(((int)Current + 1) % Count); return Current; }
        public static string Name(FxWeatherKind k) => k == FxWeatherKind.Clear ? "Clear" : k == FxWeatherKind.Rain ? "Rain" : k == FxWeatherKind.Storm ? "Storm" : k == FxWeatherKind.Fog ? "Fog" : "Snow";
    }

    public struct FxSkyState
    {
        public float Hour;
        public Vector3 SunDir;        // toward the sun (moon at night), normalised
        public float SunElev;         // degrees above the horizon of SunDir
        public float Day;             // 0 night .. 1 full day (sun up)
        public Color Amb;             // multiplier on the baked vertex colour (1,1,1 = v0.14)
        public Color SunCol;          // extra light on faces turned toward SunDir (golden hour, moon)
        public Color Fog;             // haze / clear colour
        public float FogStartMul, FogEndMul, FogK;
        public float Lights;          // 0..1 strength of fires / lamps / lit windows as light sources
        public float BakedFade;       // 0..1 how much the static (noon) ground shadows are faded out
        public float LongAlpha;       // opacity of the rotating long shadows (FxShadows)
        public float Wet, Snow, Mist, Precip;   // weather amounts 0..1
        public float Grade;           // colour grade strength (0 when the setting is off)
        public FxWeatherKind Weather;
    }

    public static class FxSky
    {
        public const float SUNRISE = 5f, SUNSET = 21f, NOON = 13f, MAX_ELEV = 52f;
        // baked noon light (Geo.SUN) projected on the ground plane
        static readonly Vector2 NOON_AZ = new Vector2(0.103f, 0.634f).normalized;

        struct Key { public float H; public Color Amb, Sun, Fog; public float Lights, Fade, Long; }

        static Color C(float r, float g, float b) => new Color(r, g, b, 1f);

        // keyframes over the 24 h clock (the sim: night 21:00-05:00, IsNight)
        static readonly Key[] KEYS =
        {
            new Key { H = 0.0f,  Amb = C(0.25f, 0.30f, 0.47f), Sun = C(0.05f, 0.07f, 0.13f), Fog = C(0.085f, 0.10f, 0.15f), Lights = 1.00f, Fade = 0.70f, Long = 0.16f },
            new Key { H = 4.3f,  Amb = C(0.25f, 0.30f, 0.47f), Sun = C(0.05f, 0.07f, 0.13f), Fog = C(0.085f, 0.10f, 0.15f), Lights = 1.00f, Fade = 0.70f, Long = 0.16f },
            new Key { H = 5.6f,  Amb = C(0.60f, 0.52f, 0.60f), Sun = C(0.42f, 0.22f, 0.16f), Fog = C(0.46f, 0.40f, 0.42f), Lights = 0.45f, Fade = 0.75f, Long = 0.30f },
            new Key { H = 7.5f,  Amb = C(0.90f, 0.87f, 0.82f), Sun = C(0.14f, 0.09f, 0.03f), Fog = C(0.42f, 0.44f, 0.40f), Lights = 0.06f, Fade = 0.70f, Long = 0.34f },
            new Key { H = 13.0f, Amb = C(1.00f, 1.00f, 1.00f), Sun = C(0.00f, 0.00f, 0.00f), Fog = C(0.431f, 0.455f, 0.40f), Lights = 0.00f, Fade = 0.70f, Long = 0.36f },
            new Key { H = 17.5f, Amb = C(0.97f, 0.93f, 0.86f), Sun = C(0.10f, 0.06f, 0.01f), Fog = C(0.46f, 0.45f, 0.38f), Lights = 0.04f, Fade = 0.70f, Long = 0.36f },
            new Key { H = 19.4f, Amb = C(0.86f, 0.70f, 0.56f), Sun = C(0.55f, 0.30f, 0.07f), Fog = C(0.56f, 0.44f, 0.31f), Lights = 0.28f, Fade = 0.75f, Long = 0.40f },
            new Key { H = 20.6f, Amb = C(0.62f, 0.46f, 0.47f), Sun = C(0.50f, 0.20f, 0.06f), Fog = C(0.42f, 0.29f, 0.26f), Lights = 0.65f, Fade = 0.78f, Long = 0.34f },
            new Key { H = 21.6f, Amb = C(0.30f, 0.32f, 0.48f), Sun = C(0.06f, 0.07f, 0.13f), Fog = C(0.11f, 0.12f, 0.18f), Lights = 1.00f, Fade = 0.70f, Long = 0.16f },
            new Key { H = 24.0f, Amb = C(0.25f, 0.30f, 0.47f), Sun = C(0.05f, 0.07f, 0.13f), Fog = C(0.085f, 0.10f, 0.15f), Lights = 1.00f, Fade = 0.70f, Long = 0.16f },
        };

        static float Smooth(float t) { t = Mathf.Clamp01(t); return t * t * (3f - 2f * t); }

        /// <summary>Render parameters for clock hour (0..24, fractional), the weather and the grade setting.</summary>
        public static FxSkyState Eval(float hour, FxWeatherKind w, bool grade)
        {
            hour = ((hour % 24f) + 24f) % 24f;
            int i = 0;
            while (i < KEYS.Length - 2 && hour >= KEYS[i + 1].H) i++;
            var a = KEYS[i]; var b = KEYS[i + 1];
            float t = Smooth((hour - a.H) / Mathf.Max(1e-3f, b.H - a.H));
            var s = new FxSkyState
            {
                Hour = hour, Weather = w,
                Amb = Color.Lerp(a.Amb, b.Amb, t), SunCol = Color.Lerp(a.Sun, b.Sun, t), Fog = Color.Lerp(a.Fog, b.Fog, t),
                Lights = Mathf.Lerp(a.Lights, b.Lights, t), BakedFade = Mathf.Lerp(a.Fade, b.Fade, t), LongAlpha = Mathf.Lerp(a.Long, b.Long, t),
                FogStartMul = 1f, FogEndMul = 1f, FogK = 0.85f, Grade = grade ? 1f : 0f,
            };

            // sun: rises 05:00, 52 deg at 13:00 (the baked direction), sets 21:00; 15 deg of azimuth per hour.
            // at night a fixed high moon (pale, faint shadows).
            if (hour > SUNRISE && hour < SUNSET)
            {
                float e = MAX_ELEV * Mathf.Sin(Mathf.PI * (hour - SUNRISE) / (SUNSET - SUNRISE));
                s.SunElev = Mathf.Max(4f, e);
                s.Day = Smooth((hour - SUNRISE) / 1.2f) * Smooth((SUNSET - hour) / 1.2f);
                float az = (hour - NOON) * 15f * Mathf.PI / 180f;
                s.SunDir = Dir(Rot(NOON_AZ, az), s.SunElev);
            }
            else
            {
                s.SunElev = 38f; s.Day = 0f;
                s.SunDir = Dir(Rot(NOON_AZ, -35f * Mathf.PI / 180f), s.SunElev);
            }

            // weather
            switch (w)
            {
                case FxWeatherKind.Rain:
                    s.Amb = Mul(s.Amb, 0.80f, 0.82f, 0.86f); s.SunCol = s.SunCol * 0.3f;
                    s.Fog = Color.Lerp(s.Fog, Mul(C(0.36f, 0.40f, 0.42f), Bright(s)), 0.6f);
                    s.FogStartMul = 0.6f; s.FogEndMul = 0.8f; s.Wet = 1f; s.Precip = 1f; s.LongAlpha *= 0.35f; s.BakedFade = Mathf.Max(s.BakedFade, 0.85f);
                    break;
                case FxWeatherKind.Storm:
                    s.Amb = Mul(s.Amb, 0.62f, 0.66f, 0.74f); s.SunCol = s.SunCol * 0.1f;
                    s.Fog = Color.Lerp(s.Fog, Mul(C(0.26f, 0.29f, 0.32f), Bright(s)), 0.75f);
                    s.FogStartMul = 0.45f; s.FogEndMul = 0.7f; s.FogK = 0.9f; s.Wet = 1f; s.Precip = 1.6f; s.LongAlpha *= 0.12f; s.BakedFade = Mathf.Max(s.BakedFade, 0.9f);
                    s.Lights = Mathf.Max(s.Lights, 0.25f);
                    break;
                case FxWeatherKind.Fog:
                    s.Amb = Mul(s.Amb, 0.92f, 0.94f, 0.95f); s.SunCol = s.SunCol * 0.4f;
                    s.Fog = Color.Lerp(s.Fog, Mul(C(0.60f, 0.63f, 0.58f), Bright(s)), 0.7f);
                    s.FogStartMul = 0.25f; s.FogEndMul = 0.55f; s.FogK = 0.95f; s.Mist = 1f; s.LongAlpha *= 0.4f; s.BakedFade = Mathf.Max(s.BakedFade, 0.8f);
                    break;
                case FxWeatherKind.Snow:
                    s.Amb = Mul(s.Amb, 1.00f, 1.02f, 1.07f); s.SunCol = s.SunCol * 0.6f;
                    s.Fog = Color.Lerp(s.Fog, Mul(C(0.66f, 0.69f, 0.71f), Bright(s)), 0.6f);
                    s.FogStartMul = 0.7f; s.FogEndMul = 0.85f; s.Snow = 1f; s.Precip = 0.8f; s.LongAlpha *= 0.6f;
                    break;
            }
            return s;
        }

        /// <summary>0 at night .. 1 at noon, used to dim weather colours with the time of day.</summary>
        static float Bright(FxSkyState s) => 0.22f + 0.78f * (s.Amb.r * 0.3f + s.Amb.g * 0.59f + s.Amb.b * 0.11f);
        static Color Mul(Color c, float k) => new Color(c.r * k, c.g * k, c.b * k, 1f);
        static Color Mul(Color c, float r, float g, float b) => new Color(c.r * r, c.g * g, c.b * b, 1f);
        static Vector2 Rot(Vector2 v, float a) => new Vector2(v.x * Mathf.Cos(a) + v.y * Mathf.Sin(a), -v.x * Mathf.Sin(a) + v.y * Mathf.Cos(a));
        static Vector3 Dir(Vector2 az, float elevDeg)
        {
            float e = elevDeg * Mathf.PI / 180f;
            return new Vector3(az.x * Mathf.Cos(e), Mathf.Sin(e), az.y * Mathf.Cos(e)).normalized;
        }

        /// <summary>Ground offset (tile space) of a point at height 1 projected along the light (shadow direction x length),
        /// capped at 5 heights so a low sun does not throw a shadow across half the map.</summary>
        public static Vector2 ShadowOffset(Vector3 sunDir)
        {
            float y = Mathf.Max(0.19f, sunDir.y);
            var o = new Vector2(-sunDir.x / y, sunDir.z / y);
            float m = o.magnitude;
            return m > 5f ? o * (5f / m) : o;
        }
    }
}
