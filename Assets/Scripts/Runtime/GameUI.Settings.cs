// v0.18 (batch B1, phone feel): the SETTINGS sheet, opened from MENU > Settings > MORE SETTINGS. Built with the
// v0.17 UI kit (UiKit panels / buttons / bar). Left card GRAPHICS: quality preset LOW / MEDIUM / HIGH and six
// toggles; right card PHONE: battery saver, haptics, screen shake (v0.19), UI scale stepper (0.8x .. 1.4x) and BACK.
// The layout is a pure plan (SettingsPlan.Make) so the offline preview lays out the very same rects; GameUI draws
// the plan and runs the tapped action. State lives in GameSettings (GameSettings.cs).
using System.Collections.Generic;
using UnityEngine;

namespace DayZero.Runtime
{
    public enum SetKind { Panel, Label, Hint, Button, Bar, Para }

    public enum SetAct
    {
        None, QLow, QMedium, QHigh, Vines, Weather, Crows, Fires, Shadows, Film, Battery, Haptics, Shake, UiDown, UiUp, Back,
    }

    public struct SetItem
    {
        public SetKind Kind; public Rect R; public string Text; public UiRole Role; public bool Sel; public SetAct Act; public float Frac; public int Size;
    }

    public static class SettingsPlan
    {
        const float ROW = 56f, GAP = 10f;

        static void Add(List<SetItem> o, SetKind k, Rect r, string t, UiRole role = UiRole.Primary, bool sel = false, SetAct a = SetAct.None, int size = 20, float frac = 0f)
            => o.Add(new SetItem { Kind = k, R = r, Text = t, Role = role, Sel = sel, Act = a, Size = size, Frac = frac });

        const float PAD = 12f, HEAD = 40f;   // = UiKit.PanelPad / UiKit.HeaderH (UiKit is not in the offline preview)
        static Rect Inner(Rect panel) => UiLayout.Inset(panel, PAD, HEAD + 4f + 8f, PAD, PAD);   // same content rect as UiKit.Panel(r, title)

        static void Toggle(List<SetItem> o, Rect r, string name, bool on, SetAct a)
            => Add(o, SetKind.Button, r, name + (on ? ": ON" : ": OFF"), on ? UiRole.Primary : UiRole.Neutral, on, a, 19);

        /// <summary>Every rect of the sheet inside the menu content area r, from the current GameSettings.</summary>
        public static List<SetItem> Make(Rect r, List<SetItem> o = null)
        {
            o = o ?? new List<SetItem>(32);
            o.Clear();
            var cols = UiLayout.Columns(new Rect(r.x, r.y, r.width, Mathf.Min(r.height, 470f)), GAP, 1.35f, 1f);

            // ---- GRAPHICS ----
            Add(o, SetKind.Panel, cols[0], "Graphics");
            var L = new UiLayout(Inner(cols[0]), GAP);
            var q = UiLayout.Columns(L.Top(ROW), GAP, -130f, 1f, 1f, 1f);
            Add(o, SetKind.Label, q[0], "QUALITY");
            bool custom = !GameSettings.MatchesPreset(GameSettings.Quality);
            for (int i = 0; i < 3; i++)
            {
                bool sel = GameSettings.Quality == i;
                Add(o, SetKind.Button, q[i + 1], GameSettings.QUALITY_NAMES[i], sel ? UiRole.Accent : UiRole.Primary, sel && !custom, SetAct.QLow + i, 20);
            }
            string hint = custom ? "Custom: the toggles below differ from the " + GameSettings.QUALITY_NAMES[GameSettings.Quality] + " preset."
                : GameSettings.Quality == GameSettings.LOW ? "Low: less map detail, 30 fps, fewer effects. Easiest on the battery."
                : GameSettings.Quality == GameSettings.MEDIUM ? "Medium: full map detail, no crows or film look."
                : "High: everything on.";
            Add(o, SetKind.Hint, L.Top(24f), hint, size: 16);
            var g = L.Top(ROW * 3f + GAP * 2f);
            var rows = UiLayout.Rows(g, GAP, 1f, 1f, 1f);
            var r0 = UiLayout.Columns(rows[0], GAP, 1f, 1f); var r1 = UiLayout.Columns(rows[1], GAP, 1f, 1f); var r2 = UiLayout.Columns(rows[2], GAP, 1f, 1f);
            Toggle(o, r0[0], "VINES", GameSettings.Vines, SetAct.Vines);
            Toggle(o, r0[1], "WEATHER", GameSettings.Weather, SetAct.Weather);
            Toggle(o, r1[0], "CROWS, PAPER", GameSettings.Crows, SetAct.Crows);
            Toggle(o, r1[1], "FIRES, SMOKE", GameSettings.Fires, SetAct.Fires);
            Toggle(o, r2[0], "SHADOWS, LIGHTS", GameSettings.Shadows, SetAct.Shadows);
            Toggle(o, r2[1], "FILM LOOK", GameSettings.Film, SetAct.Film);
            if (L.Rest.height >= 30f) Add(o, SetKind.Para, L.Rest, "Vines change when the map detail streams back in.", size: 15);

            // ---- PHONE ----
            Add(o, SetKind.Panel, cols[1], "Phone");
            var P = new UiLayout(Inner(cols[1]), GAP);
            var back = P.Bottom(58f);
            var b = UiLayout.Columns(P.Top(ROW), GAP, 1f, -170f);
            Add(o, SetKind.Label, b[0], "BATTERY SAVER");
            Add(o, SetKind.Button, b[1], GameSettings.BatterySaver ? "ON" : "OFF", GameSettings.BatterySaver ? UiRole.Primary : UiRole.Neutral, GameSettings.BatterySaver, SetAct.Battery);
            var h = UiLayout.Columns(P.Top(ROW), GAP, 1f, -170f);
            Add(o, SetKind.Label, h[0], "VIBRATION");
            Add(o, SetKind.Button, h[1], GameSettings.Haptics ? "ON" : "OFF", GameSettings.Haptics ? UiRole.Primary : UiRole.Neutral, GameSettings.Haptics, SetAct.Haptics);
            var sk = UiLayout.Columns(P.Top(ROW), GAP, 1f, -170f);   // v0.19 E8
            Add(o, SetKind.Label, sk[0], "SCREEN SHAKE");
            Add(o, SetKind.Button, sk[1], GameSettings.ScreenShake ? "ON" : "OFF", GameSettings.ScreenShake ? UiRole.Primary : UiRole.Neutral, GameSettings.ScreenShake, SetAct.Shake);
            var u = UiLayout.Columns(P.Top(ROW), GAP, -120f, -62f, 1f, -62f);
            float s = GameSettings.UiScaleShown;
            Add(o, SetKind.Label, u[0], "UI SIZE");
            Add(o, SetKind.Button, u[1], "-", s > GameSettings.UI_MIN + 0.01f ? UiRole.Primary : UiRole.Disabled, false, SetAct.UiDown, 26);
            Add(o, SetKind.Bar, UiLayout.Inset(u[2], 0f, 8f, 0f, 8f), GameSettings.UiScaleText(s), frac: (s - GameSettings.UI_MIN) / (GameSettings.UI_MAX - GameSettings.UI_MIN));
            Add(o, SetKind.Button, u[3], "+", s < GameSettings.UI_MAX - 0.01f ? UiRole.Primary : UiRole.Disabled, false, SetAct.UiUp, 26);
            if (P.Rest.height >= 30f)
                Add(o, SetKind.Para, P.Rest, "Battery saver caps the game at 30 fps and drops to 15 fps after 20 s without a touch while paused. Vibration: hits on survivors, the siren, broken fences, deaths. Screen shake: also the slow-motion beat on a broken gate.", size: 15);
            Add(o, SetKind.Button, back, "BACK", UiRole.Accent, false, SetAct.Back, 22);
            return o;
        }

        /// <summary>Runs a tapped action against GameSettings. Returns true for BACK.</summary>
        public static bool Run(SetAct a)
        {
            switch (a)
            {
                case SetAct.QLow: case SetAct.QMedium: case SetAct.QHigh: GameSettings.SetQuality(a - SetAct.QLow); break;
                case SetAct.Vines: GameSettings.Vines = !GameSettings.Vines; GameSettings.Changed(); break;
                case SetAct.Weather: GameSettings.Weather = !GameSettings.Weather; GameSettings.Changed(); break;
                case SetAct.Crows: GameSettings.Crows = !GameSettings.Crows; GameSettings.Changed(); break;
                case SetAct.Fires: GameSettings.Fires = !GameSettings.Fires; GameSettings.Changed(); break;
                case SetAct.Shadows: GameSettings.Shadows = !GameSettings.Shadows; GameSettings.Changed(); break;
                case SetAct.Film: GameSettings.SetFilm(!GameSettings.Film); break;
                case SetAct.Shake: GameSettings.ScreenShake = !GameSettings.ScreenShake; GameSettings.Changed(); break;
                case SetAct.Battery: GameSettings.BatterySaver = !GameSettings.BatterySaver; GameSettings.Changed(); break;
                case SetAct.Haptics:
                    GameSettings.Haptics = !GameSettings.Haptics; GameSettings.Changed();
#if !DZ_PREVIEW
                    if (GameSettings.Haptics) Haptic.Vibrate(60, 180);   // a taste of it
#endif
                    break;
                case SetAct.UiDown: GameSettings.SetUiScale(GameSettings.UiScaleShown - GameSettings.UI_STEP); break;
                case SetAct.UiUp: GameSettings.SetUiScale(GameSettings.UiScaleShown + GameSettings.UI_STEP); break;
                case SetAct.Back: return true;
            }
            return false;
        }
    }

#if !DZ_PREVIEW
    public sealed partial class GameUI
    {
        bool _settingsOpen;
        int _settingsFrame = -100;
        readonly List<SetItem> _setItems = new List<SetItem>(32);

        void OpenSettings() { _settingsOpen = true; _settingsFrame = Time.frameCount; }

        /// <summary>Called first in the MENU tab: draws the settings sheet when it is open (true = the tab is taken).
        /// Leaving the tab or closing the menu closes the sheet (the frame gap is noticed on the next draw).</summary>
        bool DrawSettingsSheet(Rect r)
        {
            if (!_settingsOpen) return false;
            if (Time.frameCount - _settingsFrame > 2) { _settingsOpen = false; return false; }
            _settingsFrame = Time.frameCount;
            SettingsPlan.Make(r, _setItems);
            SetAct hit = SetAct.None;
            for (int i = 0; i < _setItems.Count; i++)
            {
                var it = _setItems[i];
                switch (it.Kind)
                {
                    case SetKind.Panel: UiKit.Panel(it.R, it.Text); break;
                    case SetKind.Label: UiKit.FitLabel(it.R, it.Text, 20, TextAnchor.MiddleLeft, UiKit.Ink); break;
                    case SetKind.Hint: UiKit.FitLabel(it.R, it.Text, it.Size, TextAnchor.MiddleLeft, UiKit.InkDim, false, 11, false); break;
                    case SetKind.Para: UiKit.Paragraph(it.R, it.Text, it.Size); break;
                    case SetKind.Bar: UiKit.Bar(it.R, it.Frac, UiRole.Accent, it.Text); break;
                    case SetKind.Button:
                        if (KBtn(it.R, it.Text, it.Role, it.Sel, it.Size) && it.Role != UiRole.Disabled) hit = it.Act;
                        break;
                }
            }
            if (hit != SetAct.None && SettingsPlan.Run(hit)) _settingsOpen = false;
            return true;
        }
    }
#endif
}
