// v0.19 (batch B2, game feel) E9 day-start sting: at dawn (RunStats.DAWN_H) a 2 s title card "DAY 14" with one line
// under it fades in and out over the map, with a short two-note cue made from the existing "hammer" clip at low
// pitch (Sfx.PlayTone; no new audio). Subtitle: during the Fall the days left to hold out; before it (no countdown
// exists, the Fall comes when the infection takes the whole city) the season and weather with the expanded rules,
// else the infection share. It sits in the modal chain just before the morning report card (GameUI.Reports), so
// the sting plays first and the report comes after it. Visual / audio only; the sting is not a modal (no taps taken).
// DayStingPlan is pure (offline preview lays out the same rects); the GameUI part draws it.
using System;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public static class DayStingPlan
    {
        public const float DUR = 2.0f, FADE_IN = 0.35f, FADE_OUT = 0.5f;

        /// <summary>Card opacity t seconds into the sting (0 before / after).</summary>
        public static float Alpha(float t)
        {
            if (t < 0f || t >= DUR) return 0f;
            if (t < FADE_IN) { float u = t / FADE_IN; return u * u * (3f - 2f * u); }
            if (t > DUR - FADE_OUT) { float u = (DUR - t) / FADE_OUT; return u * u * (3f - 2f * u); }
            return 1f;
        }

        /// <summary>Title slides up a little while it fades in (pixels).</summary>
        public static float Rise(float t) => t < FADE_IN ? (1f - t / FADE_IN) * 14f : 0f;

        public static string Title(int day) => "DAY " + day;

        public static string Subtitle(Game g)
        {
            if (g?.S == null) return "";
            string ph = g.PhaseOf(Math.Max(0, g.CurCol));
            if (ph == "fall")
            {
                int d = (int)Math.Ceiling(g.HoldLeft() / 24.0 - 1e-6);
                return d <= 1 ? "Last day to hold out" : d + " days to hold out";
            }
            if (Game.ExpansionRules)
            {
                var se = g.CurrentSeason.ToString();
                var we = g.CurrentWeather;
                return string.Format("{0}, day {1} of {2}  -  {3}", se, g.CurrentSeasonDay, Game.SEASON_DAYS,
                    we == ExpWeather.Clear ? "clear skies" : we.ToString().ToLowerInvariant());
            }
            int pct = (int)(g.SpreadPct() * 100.0);
            return pct < 1 ? "The outbreak is spreading" : string.Format("Infection {0}% of the city", pct);
        }

        /// <summary>Layout in GameUI virtual pixels: a dark band across the upper middle with two thin rules.</summary>
        public static void Layout(float w, float h, float t, out Rect band, out Rect title, out Rect sub, out Rect rule1, out Rect rule2)
        {
            float bh = Mathf.Clamp(h * 0.26f, 150f, 210f);
            float cy = h * 0.36f;
            band = new Rect(0f, cy - bh * 0.5f, w, bh);
            float rise = Rise(t);
            float rw = Mathf.Min(560f, w * 0.5f);
            title = new Rect(w * 0.5f - 400f, band.y + bh * 0.10f + rise, 800f, bh * 0.52f);
            sub = new Rect(w * 0.5f - 400f, band.y + bh * 0.62f + rise * 0.5f, 800f, bh * 0.26f);
            rule1 = new Rect(w * 0.5f - rw * 0.5f, band.y + 10f, rw, 2f);
            rule2 = new Rect(w * 0.5f - rw * 0.5f, band.yMax - 12f, rw, 2f);
        }
    }

#if !DZ_PREVIEW
    public sealed partial class GameUI
    {
        Game _stingGame;
        long _stingDawn = long.MinValue;
        int _stingDay = -1;
        float _stingT0 = -1f;
        bool _stingSnd1, _stingSnd2;
        string _stingSub = "";

        static long StingDawnIndex(double t) => (long)Math.Floor((t - RunStats.DAWN_H) / 24.0);

        /// <summary>Watches for dawn of the viewed run; a new dawn queues the sting (not on load / new game).</summary>
        void TrackDaySting()
        {
            var g = G;
            if (g?.S == null) return;
            long di = StingDawnIndex(g.S.T);
            if (!ReferenceEquals(g, _stingGame) || di < _stingDawn) { _stingGame = g; _stingDawn = di; _stingDay = -1; return; }
            if (di == _stingDawn) return;
            _stingDawn = di;
            string ph = g.PhaseOf(Math.Max(0, g.CurCol));
            if (g.Day() < 2 || (ph != "spread" && ph != "fall")) return;
            _stingDay = g.Day(); _stingT0 = -1f; _stingSnd1 = _stingSnd2 = false;
            _stingSub = DayStingPlan.Subtitle(g);
        }

        /// <summary>Modal chain, just before the morning report. True while the sting is on screen (the report waits).</summary>
        bool DrawDaySting(float w, float h)
        {
            TrackDaySting();
            var g = G;
            if (_stingDay < 0 || g?.S == null) return false;
            // shown late (a decision or a menu was up): only while it is still that morning
            if (_stingT0 < 0f && (g.Day() != _stingDay || g.Hour() >= 12)) { _stingDay = -1; return false; }
            float now = Time.unscaledTime;
            if (_stingT0 < 0f) _stingT0 = now;
            float t = now - _stingT0;
            if (t >= DayStingPlan.DUR) { _stingDay = -1; return false; }
            var sfx = _b != null ? _b.Sfx : null;
            if (!_stingSnd1) { _stingSnd1 = true; sfx?.PlayTone("hammer", 0.50f, -2f); }
            if (!_stingSnd2 && t >= 0.34f) { _stingSnd2 = true; sfx?.PlayTone("hammer", 0.42f, -4f); }
            if (Event.current.type != EventType.Repaint) return true;
            float a = DayStingPlan.Alpha(t);
            DayStingPlan.Layout(w, h, t, out var band, out var title, out var sub, out var r1, out var r2);
            var oc = GUI.color;
            GUI.color = new Color(0.05f, 0.05f, 0.045f, 0.62f * a);
            GUI.DrawTexture(band, Texture2D.whiteTexture);
            var am = UiKit.Amber;
            GUI.color = new Color(am.r, am.g, am.b, 0.85f * a);
            GUI.DrawTexture(r1, Texture2D.whiteTexture);
            GUI.DrawTexture(r2, Texture2D.whiteTexture);
            GUI.color = new Color(1f, 1f, 1f, a);
            UiKit.FitLabel(title, DayStingPlan.Title(_stingDay), 84, TextAnchor.MiddleCenter, UiKit.Ink, true, 40);
            UiKit.FitLabel(sub, _stingSub, 28, TextAnchor.MiddleCenter, am, true, 16);
            GUI.color = oc;
            return true;
        }
    }
#endif
}
