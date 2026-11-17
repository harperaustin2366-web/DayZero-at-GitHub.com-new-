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
            float y = 430;
            if (Btn(new Rect(60, y, 300, 64), on ? "EXPANDED: ON" : "EXPANDED: OFF", on ? _amberButton : _darkButton))
            {
                on = !on;
                PlayerPrefs.SetInt(PREF_EXPANDED, on ? 1 : 0);
                PlayerPrefs.Save();
                Game.ExpansionRules = on;
            }
            GUI.Label(new Rect(376, y + 4, w - 440, 28), on ? "Seasons, weather, noise, district danger, traits and stress" : "Classic = original rules", _small);
            GUI.Label(new Rect(376, y + 32, w - 440, 28), on ? "Classic = original rules (tap to switch off)" : "Tap to turn the expanded rules on", _small);
        }

        /// <summary>Map layer: coloured D1..D5 label under each district name while the DANGER overlay is on.</summary>
        void DrawDangerOverlay(MapView map, float w, float h)
        {
            if (!Game.ExpansionRules || !_dangerOn || map == null || !map.Ready || G?.S == null) return;
            EnsureExpStyles();
            var ds = Data.Instance.Districts;
            var dd = G.S.DistrictDanger;
            for (int i = 0; i < ds.Count; i++)
            {
                var c = ds[i].C;
                if (c == null || c.Length < 2) continue;
                if (!map.TryScreen(new Vector3((float)c[0], 2f, -(float)c[1]), out var sp)) continue;
                var v = new Vector2((sp.x - _offX) / _scale, (sp.y - _offY) / _scale);
                if (v.x < -100 || v.x > w + 100 || v.y < 0 || v.y > h) continue;
                int lv = dd != null && i < dd.Length && dd[i] > 0 ? Mathf.Clamp(dd[i], 1, 5) : G.DistrictDangerAt((int)c[0], (int)c[1]);
                string t = Game.DANGER_LABEL[lv];
                var r = new Rect(v.x - 90, v.y + 12, 180, 26);
                var oc = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.55f * oc.a);
                _gc.text = t;
                float tw = _xDanger.CalcSize(_gc).x + 16f;
                GUI.DrawTexture(new Rect(v.x - tw / 2, r.y + 1, tw, 24), _xWhite);
                GUI.color = new Color(DANGER_COL[lv].r, DANGER_COL[lv].g, DANGER_COL[lv].b, oc.a);
                GUI.DrawTexture(new Rect(v.x - tw / 2, r.y + 1, 4, 24), _xWhite);
                GUI.color = oc;
                _xDanger.normal.textColor = DANGER_COL[lv];
                GUI.Label(new Rect(r.x + 1.5f, r.y + 1.5f, r.width, r.height), t, _xDangerSh);
                GUI.Label(r, t, _xDanger);
            }
        }

        /// <summary>HUD (after the stats panel): season / weather chip, noise gauge + popup, DANGER button; also drives
        /// the weather visuals from the sim.</summary>
        void DrawExpWorldHud(float w, float h, MapView map)
        {
            if (!Game.ExpansionRules || G?.S == null) return;
            EnsureExpStyles();
            int wi = Mathf.Clamp((int)G.CurrentWeather, 0, 4);
            FxWeather.Current = (FxWeatherKind)wi;   // enums share order (Clear, Rain, Storm, Fog, Snow)

            float x = 346, cw = Mathf.Clamp(w / 2 - 88 - x, 150f, 300f);
            // season / weather chip
            string chip = G.SeasonLabel + " · " + WEATHER_WORD[wi];
            var cr = Blk(new Rect(x, 8, cw, 30));
            GUI.Box(cr, GUIContent.none, _chip);
            GUI.Label(cr, chip, _xChip);

            // noise gauge (main camp)
            var mb = G.MainBase();
            if (mb != null)
            {
                float noise = Mathf.Clamp(mb.Noise, 0f, 100f);
                int band = G.NoiseBand(mb.Id);
                var gr = Blk(new Rect(x, 42, cw, 30));
                GUI.Box(gr, GUIContent.none, _chip);
                GUI.Label(new Rect(gr.x + 8, gr.y, 62, gr.height), "NOISE", _xCap);
                var bar = new Rect(gr.x + 66, gr.y + 9, gr.width - 66 - 70, 12);
                GUI.DrawTexture(bar, _xBarBg);
                var oc = GUI.color;
                GUI.color = new Color(NOISE_COL[band].r, NOISE_COL[band].g, NOISE_COL[band].b, oc.a);
                GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * noise / 100f, bar.height), _xWhite);
                GUI.color = oc;
                _xCap.normal.textColor = NOISE_COL[band];
                GUI.Label(new Rect(bar.xMax + 6, gr.y, 66, gr.height), NOISE_WORD[band], _xCap);
                _xCap.normal.textColor = _small.normal.textColor;
                if (GUI.Button(gr, GUIContent.none, GUIStyle.none)) { _noisePopup = !_noisePopup; _b.PlayClick(); }
                if (_noisePopup) DrawNoisePopup(new Rect(x, 76, Mathf.Max(cw, 280f), 0), mb, noise, band);
            }

            // DANGER overlay toggle, under the camera buttons
            if (map != null && Btn(Blk(new Rect(10, 462, 76, 54)), _dangerOn ? "DANGER\nON" : "DANGER", _dangerOn ? _xDangerBtn : _camSmall))
                _dangerOn = !_dangerOn;
        }

        void DrawNoisePopup(Rect at, Base mb, float noise, int band)
        {
            var src = G.NoiseSources(mb.Id);
            var rows = src == null ? new List<KeyValuePair<string, float>>() : src.Where(kv => kv.Value >= 0.05f).OrderByDescending(kv => kv.Value).Take(8).ToList();
            float ph = 76 + 26 * Mathf.Max(1, rows.Count) + 12;
            var r = Blk(new Rect(at.x, at.y, at.width, ph));
            GUI.Box(r, GUIContent.none, _hudBox);
            GUI.Label(new Rect(r.x + 12, r.y + 6, r.width - 24, 24), string.Format("NOISE {0} / 100  {1}", (int)noise, NOISE_WORD[band]), _hdrAmber);
            GUI.Label(new Rect(r.x + 12, r.y + 30, r.width - 24, 22), "This hour, at " + mb.Name, _small);
            float y = r.y + 58;
            if (rows.Count == 0) GUI.Label(new Rect(r.x + 12, y, r.width - 24, 24), "No noise made this hour.", _xRow);
            foreach (var kv in rows)
            {
                GUI.Label(new Rect(r.x + 12, y, r.width - 90, 24), kv.Key, _xRow);
                GUI.Label(new Rect(r.xMax - 78, y, 66, 24), "+" + kv.Value.ToString("0.#"), _xRow);
                y += 26;
            }
            GUI.Label(new Rect(r.x + 12, r.yMax - 22, r.width - 24, 20), "Tap the gauge to close", _small);
        }
    }
}
