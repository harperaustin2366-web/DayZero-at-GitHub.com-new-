// Expansion world UI (design/SYSTEMS_30.md #2 weather/seasons, #3 noise, #23 district danger).
// Title: EXPANDED ON/OFF toggle (PlayerPrefs "expanded", default 1) -> Game.ExpansionRules before NewRun.
// In game (only while Game.ExpansionRules): season / weather chip, noise gauge with a sources popup,
// DANGER map overlay (D1..D5 per district), and Game.CurrentWeather pushed into FxWeather.Current.
// View only: reads the sim, never writes to it.
using System.Collections.Generic;
using System.Linq;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed partial class GameUI
    {
        public const string PREF_EXPANDED = "expanded";
        static readonly string[] WEATHER_WORD = { "CLEAR", "RAIN", "STORM", "FOG", "SNOW" };
        static readonly string[] NOISE_WORD = { "QUIET", "HEARD", "LOUD", "BEACON" };
        static readonly Color[] NOISE_COL =
        {
            new Color(0.45f, 0.80f, 0.35f), new Color(0.92f, 0.85f, 0.30f), new Color(0.95f, 0.58f, 0.22f), new Color(0.95f, 0.30f, 0.25f),
        };
        static readonly Color[] DANGER_COL =
        {
            Color.white, new Color(0.45f, 0.82f, 0.40f), new Color(0.75f, 0.85f, 0.35f), new Color(0.95f, 0.85f, 0.30f),
            new Color(0.97f, 0.58f, 0.22f), new Color(0.97f, 0.30f, 0.25f),
        };

        bool _dangerOn, _noisePopup, _expStyled;
        Texture2D _xBarBg, _xWhite;
        GUIStyle _xChip, _xCap, _xDanger, _xDangerSh, _xRow, _xDangerBtn;

        void EnsureExpStyles()
        {
            if (_expStyled) return;
            _expStyled = true;
            _xBarBg = Solid(new Color(0.05f, 0.06f, 0.05f, 0.85f));
            _xWhite = Solid(Color.white);
            _xChip = new GUIStyle(_chip) { fontSize = 16, fontStyle = FontStyle.Bold };
            _xCap = new GUIStyle(_small) { fontSize = 14, wordWrap = false, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
            _xDanger = new GUIStyle(_mapLabel) { fontSize = 17, fontStyle = FontStyle.Bold };
            _xDangerSh = new GUIStyle(_labelShadow) { fontSize = 17, fontStyle = FontStyle.Bold };
            _xRow = new GUIStyle(_label) { fontSize = 18, wordWrap = false };
            _xDangerBtn = new GUIStyle(_camSmall); _xDangerBtn.normal.textColor = _xDangerBtn.hover.textColor = AMBER;
        }

        static bool ExpandedPref => PlayerPrefs.GetInt(PREF_EXPANDED, 1) != 0;

        /// <summary>Title screen: EXPANDED toggle under the difficulty buttons. Keeps Game.ExpansionRules in step with the
        /// preference so the next NewRun starts with it (Continue restores the flag from the save).</summary>
        void DrawExpandedToggle(float w, float h)
        {
            EnsureExpStyles();
            bool on = ExpandedPref;
            Game.ExpansionRules = on;
            float y = 424;
            if (KBtn(new Rect(60, y, 300, 72), on ? "EXPANDED: ON" : "EXPANDED: OFF", on ? UiRole.Accent : UiRole.Neutral, on, 22))
            {
                on = !on;
                PlayerPrefs.SetInt(PREF_EXPANDED, on ? 1 : 0);
                PlayerPrefs.Save();
                Game.ExpansionRules = on;
            }
            float tw = Mathf.Min(w - 80f, 760f) - 336f;
            UiKit.FitLabel(new Rect(376, y + 8, tw, 26), on ? "Seasons, weather, noise, district danger, traits and stress" : "Classic = original rules", 16, TextAnchor.MiddleLeft, UiKit.Ink, false);
            UiKit.FitLabel(new Rect(376, y + 36, tw, 26), on ? "Classic = original rules (tap to switch off)" : "Tap to turn the expanded rules on", 15, TextAnchor.MiddleLeft, UiKit.InkDim, false);
        }

        /// <summary>Map layer: coloured D1..D5 tag under each district name while the DANGER overlay is on (hidden where it
        /// would run under the HUD or another label).</summary>
        void DrawDangerOverlay(MapView map, float w, float h)
        {
            if (!Game.ExpansionRules || !_dangerOn || map == null || !map.Ready || G?.S == null) return;
            if (Event.current.type != EventType.Repaint) return;
            EnsureExpStyles();
            var ds = Data.Instance.Districts;
            var dd = G.S.DistrictDanger;
            for (int i = 0; i < ds.Count; i++)
            {
                var c = ds[i].C;
                if (c == null || c.Length < 2) continue;
                if (!map.TryScreen(new Vector3((float)c[0], 2f, -(float)c[1]), out var sp)) continue;
                var v = ToVirtual(sp);
                if (v.x < -100 || v.x > w + 100 || v.y < 0 || v.y > h) continue;
                int lv = dd != null && i < dd.Length && dd[i] > 0 ? Mathf.Clamp(dd[i], 1, 5) : G.DistrictDangerAt((int)c[0], (int)c[1]);
                string t = Game.DANGER_LABEL[lv];
                _gc.text = t;
                float tw = _xDanger.CalcSize(_gc).x + 20f;
                var r = new Rect(v.x - tw / 2, v.y + 14, tw, 26);
                if (!_lp.Place(r)) continue;
                var oc = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.6f * oc.a);
                GUI.DrawTexture(r, _xWhite);
                GUI.color = new Color(DANGER_COL[lv].r, DANGER_COL[lv].g, DANGER_COL[lv].b, oc.a);
                GUI.DrawTexture(new Rect(r.x, r.y, 4, r.height), _xWhite);
                GUI.color = oc;
                UiKit.FitLabel(new Rect(r.x + 6, r.y, r.width - 8, r.height), t, 17, TextAnchor.MiddleCenter, DANGER_COL[lv]);
            }
        }

        static readonly string[] WEATHER_ICON = { "sun", "water", "water", null, null };

        /// <summary>HUD (next to the stats card): season / weather row and the noise gauge (tap: sources popup), the DANGER
        /// toggle at the foot of the camera column; also drives the weather visuals from the sim.</summary>
        void DrawExpWorldHud(float w, float h, MapView map)
        {
            if (!Game.ExpansionRules || G?.S == null) return;
            EnsureExpStyles();
            int wi = Mathf.Clamp((int)G.CurrentWeather, 0, 4);
            FxWeather.Current = (FxWeatherKind)wi;   // enums share order (Clear, Rain, Storm, Fog, Snow)
            var f = _hf;
            UiKit.Panel(Blk(f.World));

            // season / weather
            var sr = f.SeasonRow;
            UiKit.RowPlate(sr, 0);
            string icon = WEATHER_ICON[wi];
            if (icon == "sun" && G.IsNight()) icon = "moon";
            float tx = sr.x + 10f;
            if (icon != null)
            {
                if (Event.current.type == EventType.Repaint)
                    GUI.DrawTexture(new Rect(sr.x + 5f, sr.y + 3f, sr.height - 6f, sr.height - 6f), Icons.Tex(icon, icon == "sun" ? new Color(0.80f, 0.56f, 0.18f) : icon == "moon" ? new Color(0.30f, 0.38f, 0.62f) : new Color(0.30f, 0.50f, 0.72f)), ScaleMode.ScaleToFit, true);
                tx = sr.x + sr.height + 2f;
            }
            UiKit.FitLabel(new Rect(tx, sr.y, sr.xMax - tx - 8f, sr.height), G.SeasonLabel + "  -  " + WEATHER_WORD[wi], 17, TextAnchor.MiddleLeft, UiKit.Ink, true, 10);

            // noise gauge (main camp)
            var mb = G.MainBase();
            var gr = f.NoiseRow;
            if (mb != null)
            {
                float noise = Mathf.Clamp(mb.Noise, 0f, 100f);
                int band = G.NoiseBand(mb.Id);
                var cols = UiLayout.Columns(gr, 6f, -56f, 1f, -64f);
                UiKit.FitLabel(cols[0], "NOISE", 14, TextAnchor.MiddleLeft, UiKit.InkDim, true, 9);
                UiKit.Bar(UiLayout.Inset(cols[1], 0f, 5f, 0f, 5f), noise / 100f, band == 0 ? UiRole.Primary : band == 3 ? UiRole.Danger : UiRole.Accent);
                UiKit.FitLabel(cols[2], NOISE_WORD[band], 14, TextAnchor.MiddleRight, NOISE_COL[band], true, 9);
                if (GUI.Button(f.World, GUIContent.none, GUIStyle.none)) { _noisePopup = !_noisePopup; _b.PlayClick(); }
                if (_noisePopup) DrawNoisePopup(new Rect(f.World.x, f.World.yMax + 6f, Mathf.Max(f.World.width, 300f), 0), mb, noise, band);
            }
            else UiKit.FitLabel(gr, "NOISE  -", 14, TextAnchor.MiddleLeft, UiKit.InkMute, true, 9);

            // DANGER overlay toggle, at the foot of the camera column
            if (map != null && f.Cam.Length > 4 && KBtn(Blk(f.Cam[4]), "DANGER", _dangerOn ? UiRole.Accent : UiRole.Neutral, _dangerOn, 16))
                _dangerOn = !_dangerOn;
        }

        void DrawNoisePopup(Rect at, Base mb, float noise, int band)
        {
            var src = G.NoiseSources(mb.Id);
            var rows = src == null ? new List<KeyValuePair<string, float>>() : src.Where(kv => kv.Value >= 0.05f).OrderByDescending(kv => kv.Value).Take(8).ToList();
            float ph = UiKit.HeaderH + 4f + 8f + 26f + 6f + 30f * Mathf.Max(1, rows.Count) + 28f + UiKit.PanelPad;
            var r = Blk(new Rect(at.x, at.y, at.width, ph));
            var ci = UiKit.Panel(r, string.Format("Noise {0} / 100  {1}", (int)noise, NOISE_WORD[band]), 19);
            var L = new UiLayout(ci, 4f);
            UiKit.FitLabel(L.Top(26f), "This hour, at " + mb.Name, 15, TextAnchor.MiddleLeft, UiKit.InkDim, false);
            UiKit.FitLabel(L.Bottom(24f), "Tap the gauge to close", 14, TextAnchor.MiddleLeft, UiKit.InkMute, false);
            if (rows.Count == 0) UiKit.FitLabel(L.Top(26f), "No noise made this hour.", 17, TextAnchor.MiddleLeft, UiKit.Ink, false);
            int i = 0;
            foreach (var kv in rows)
            {
                var row = L.Top(26f);
                UiKit.RowPlate(row, i++);
                var c = UiLayout.Columns(UiLayout.Inset(row, 8f, 0f, 8f, 0f), 6f, 1f, -60f);
                UiKit.FitLabel(c[0], kv.Key, 16, TextAnchor.MiddleLeft, UiKit.Ink, false);
                UiKit.FitLabel(c[1], "+" + kv.Value.ToString("0.#"), 16, TextAnchor.MiddleRight, UiKit.Amber);
            }
        }
    }
}
