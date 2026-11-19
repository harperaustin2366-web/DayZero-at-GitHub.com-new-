// Minimal runtime UI, built entirely in code with IMGUI (no prefabs, fonts or scene objects
// needed). Laid out on a 1280x720-high virtual canvas like the Godot project and scaled to the
// screen, inside the device safe area.
//
// Covers: title screen (new game by difficulty / continue), top bar (clock, phase, resources,
// population, morale, pause, speed, skip), tabs for Colony, Jobs, Log and Menu (save / load /
// sound), decision popups and the game-over screen.
using System;
using System.Collections.Generic;
using System.Linq;
using DayZero.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed partial class GameUI
    {
        static float VH => 720f / GameSettings.UiScale;   // virtual height (Godot viewport 1280x720); v0.18 UI scale (GameSettings.cs)
        const float TAB_H = 52f;

        static readonly string[] RES_KEYS = { "food", "water", "meds", "ammo", "fuel" };
        static readonly string[] TABS = { "Colony", "Jobs", "Log", "Menu" };

        readonly Bootstrap _b;
        int _tab;
        bool _styled;
        GUIStyle _label, _small, _title, _button, _bigButton, _box, _chip, _warn;
        GUIStyle _amberButton, _brownButton, _darkButton, _hud, _hudR, _shadow, _mapLabel, _mapDistrict, _mapCamp, _labelShadow;
        Texture2D _bgTex, _panelTex, _chipTex, _dimTex;
        bool _menuOpen, _actionsOpen = true;
        // v0.14.0a skin (UiSkin.cs): cached textures / styles, nothing built per frame
        Texture2D _glowTex, _scrimTex, _accentTex, _lineTex, _amberLineTex, _sheetDimTex;
        GUIStyle _hudBox, _tintBox, _camSmall, _hdrAmber, _phaseSt, _colonySt, _tagSt, _bannerT, _bannerS, _pickT, _pickErr;
        GUIStyle _mapLabelSh, _mapDistrictSh, _mapCampSh, _chipTxt;
        readonly GUIContent _gc = new GUIContent();
        readonly Dictionary<GUIStyle, GUIStyle> _shadowOf = new Dictionary<GUIStyle, GUIStyle>();

        public GameUI(Bootstrap b) { _b = b; }

        Game G => _b.Game;


        // ---------------- styles ----------------
        // Dark translucent panels, olive buttons, amber accents (approved style concept).
        static readonly Color INK = new Color(0.93f, 0.90f, 0.80f);
        static readonly Color AMBER = new Color(0.95f, 0.70f, 0.28f);
        static readonly Color OLIVE_INK = new Color(0.72f, 0.80f, 0.48f);
        static readonly Color RED_INK = new Color(0.95f, 0.42f, 0.32f);
        static readonly Color SUN = new Color(1f, 0.80f, 0.36f), MOON = new Color(0.72f, 0.80f, 0.98f), RED_GLYPH = new Color(0.95f, 0.35f, 0.28f);
        // resource chips (v0.14.0a): key, glyph, glyph colour
        static readonly string[] CHIP_KEYS = { "food", "water", "meds", "ammo", "fuel", "mats", "morale" };
        static readonly Color[] CHIP_COL =
        {
            new Color(0.86f, 0.42f, 0.28f), new Color(0.40f, 0.66f, 0.90f), new Color(0.92f, 0.36f, 0.34f), new Color(0.90f, 0.72f, 0.34f),
            new Color(0.62f, 0.70f, 0.36f), new Color(0.78f, 0.58f, 0.38f), new Color(0.95f, 0.55f, 0.48f),
        };

        static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, c); t.Apply();
            t.hideFlags = HideFlags.DontSave;
            return t;
        }

        GUIStyle MakeButton(Color normal, Color hover, Color active, int size)
        {
            // v0.14.0a: rounded 9-slice textures (gradient, keyline, top highlight, drop shadow), pressed = darker with an
            // amber inner glow; same colours, padding and text as before. Textures are cached per colour in UiSkin.
            var st = new GUIStyle(GUI.skin.button) { fontSize = size, wordWrap = true, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            UiSkin.SkinButton(st, normal, hover, active);
            st.normal.textColor = st.hover.textColor = st.active.textColor = st.focused.textColor = st.onNormal.textColor = INK;
            st.onHover.textColor = st.onActive.textColor = INK;
            st.padding = new RectOffset(8, 8, 4, 4);
            return st;
        }

        void EnsureStyles()
        {
            if (_styled) return;
            _styled = true;
            _bgTex = UiSkin.ToTex(UiSkin.Backdrop());
            _panelTex = Solid(new Color(0.09f, 0.10f, 0.08f, 0.80f));
            _chipTex = Solid(new Color(0.07f, 0.08f, 0.06f, 0.82f));
            _dimTex = Solid(new Color(0f, 0f, 0f, 0.45f));

            _label = new GUIStyle(GUI.skin.label) { fontSize = 20, wordWrap = true };
            _label.normal.textColor = INK;
            _small = new GUIStyle(_label) { fontSize = 16 };
            _small.normal.textColor = new Color(0.78f, 0.76f, 0.68f);
            _title = new GUIStyle(_label) { fontSize = 30, fontStyle = FontStyle.Bold };
            _title.normal.textColor = AMBER;
            _warn = new GUIStyle(_label) { fontSize = 18, fontStyle = FontStyle.Bold };
            _warn.normal.textColor = RED_INK;
            _button = MakeButton(new Color(0.34f, 0.38f, 0.22f, 0.94f), new Color(0.42f, 0.46f, 0.27f, 0.97f), new Color(0.70f, 0.50f, 0.18f, 1f), 20);
            _amberButton = MakeButton(new Color(0.72f, 0.52f, 0.17f, 0.95f), new Color(0.80f, 0.59f, 0.21f, 0.97f), new Color(0.55f, 0.38f, 0.12f, 1f), 20);
            _brownButton = MakeButton(new Color(0.52f, 0.33f, 0.18f, 0.95f), new Color(0.60f, 0.39f, 0.22f, 0.97f), new Color(0.70f, 0.50f, 0.18f, 1f), 20);
            _darkButton = MakeButton(new Color(0.09f, 0.10f, 0.08f, 0.82f), new Color(0.18f, 0.20f, 0.14f, 0.9f), new Color(0.70f, 0.50f, 0.18f, 1f), 22);
            _bigButton = new GUIStyle(_button) { fontSize = 26 };
            _box = new GUIStyle(GUI.skin.box);
            UiSkin.Apply(_box, UiSkin.Panel());
            _hudBox = new GUIStyle(_box);
            UiSkin.Apply(_hudBox, UiSkin.HudPanel());
            _tintBox = new GUIStyle(_box);
            UiSkin.Apply(_tintBox, UiSkin.TintChip());
            _chip = new GUIStyle(_label) { fontSize = 18, alignment = TextAnchor.MiddleCenter, wordWrap = false };
            UiSkin.Apply(_chip, UiSkin.Chip());
            _chipTxt = new GUIStyle(_chip);
            _chipTxt.normal.background = null; _chipTxt.overflow = new RectOffset(0, 0, 0, 0);
            _glowTex = UiSkin.ToTex(UiSkin.Glow());
            _scrimTex = UiSkin.ToTex(UiSkin.ScrimRight());
            _accentTex = UiSkin.ToTex(UiSkin.AccentBar());
            _lineTex = Solid(new Color(0.85f, 0.66f, 0.32f, 0.35f));
            _amberLineTex = Solid(new Color(0.95f, 0.70f, 0.28f, 0.9f));
            _sheetDimTex = Solid(new Color(0.04f, 0.045f, 0.035f, 0.80f));
            _xWhiteTex = Solid(Color.white);
            _hud = new GUIStyle(_label) { fontSize = 21, fontStyle = FontStyle.Bold, wordWrap = false, richText = true };
            _hudR = new GUIStyle(_hud) { alignment = TextAnchor.UpperRight };
            _shadow = new GUIStyle(_hud); _shadow.normal.textColor = new Color(0, 0, 0, 0.75f);
            _mapLabel = new GUIStyle(_label) { fontSize = 16, alignment = TextAnchor.MiddleCenter, wordWrap = false };
            _mapLabel.normal.textColor = new Color(0.96f, 0.93f, 0.84f);
            _mapDistrict = new GUIStyle(_mapLabel) { fontSize = 22, fontStyle = FontStyle.Bold };
            _mapDistrict.normal.textColor = new Color(0.98f, 0.80f, 0.45f);
            _mapCamp = new GUIStyle(_mapLabel) { fontSize = 18, fontStyle = FontStyle.Bold };
            _mapCamp.normal.textColor = AMBER;
            _labelShadow = new GUIStyle(_mapLabel); _labelShadow.normal.textColor = new Color(0, 0, 0, 0.8f);
            _mapLabelSh = new GUIStyle(_labelShadow) { fontSize = _mapLabel.fontSize, fontStyle = _mapLabel.fontStyle };
            _mapDistrictSh = new GUIStyle(_labelShadow) { fontSize = _mapDistrict.fontSize, fontStyle = _mapDistrict.fontStyle };
            _mapCampSh = new GUIStyle(_labelShadow) { fontSize = _mapCamp.fontSize, fontStyle = FontStyle.Bold };
            _camSmall = new GUIStyle(_darkButton) { fontSize = 15, wordWrap = false, clipping = TextClipping.Overflow };
            _hdrAmber = new GUIStyle(_small) { fontStyle = FontStyle.Bold }; _hdrAmber.normal.textColor = AMBER;
            _phaseSt = new GUIStyle(_small) { wordWrap = false }; _phaseSt.normal.textColor = AMBER;
            _colonySt = new GUIStyle(_small) { wordWrap = false }; _colonySt.normal.textColor = OLIVE_INK;
            _tagSt = new GUIStyle(_label) { fontSize = 22, fontStyle = FontStyle.Bold }; _tagSt.normal.textColor = OLIVE_INK;
            _bannerT = new GUIStyle(_title) { fontSize = 28, alignment = TextAnchor.MiddleCenter, wordWrap = false };
            _bannerS = new GUIStyle(_small) { alignment = TextAnchor.MiddleCenter, wordWrap = false };
            _pickT = new GUIStyle(_title) { fontSize = 26, wordWrap = false };
            _pickErr = new GUIStyle(_warn) { wordWrap = true };
        }

        // ---------------- map input hooks ----------------
        // Rects of HUD drawn this frame (virtual coords), so a touch on a button doesn't pan the map.
        readonly List<Rect> _blocks = new List<Rect>();
        float _offX, _offY, _scale = 1f;

        /// <summary>True when a modal (menu, decision, game over) covers the map or we are not in a game.</summary>
        public bool ModalOpen => _b.State != Bootstrap.Screen2.Playing || _menuOpen || SideOpen || _b.Pending.Count > 0 || _b.Ended || ReportsModal;

        /// <summary>Screen position (top-left origin, pixels) is over a HUD element.</summary>
        public bool BlocksPointer(Vector2 screenTopLeft)
        {
            if (ModalOpen) return true;
            var v = new Vector2((screenTopLeft.x - _offX) / _scale, (screenTopLeft.y - _offY) / _scale);
            for (int i = 0; i < _blocks.Count; i++) if (_blocks[i].Contains(v)) return true;
            return false;
        }

        Rect Blk(Rect r) { _blocks.Add(r); return r; }

        // ---------------- entry ----------------
        public void Draw()
        {
            EnsureStyles();
            if (Event.current.type == EventType.Layout) _blocks.Clear();
            var safe = UnityEngine.Screen.safeArea;
            if (safe.width <= 0 || safe.height <= 0) safe = new Rect(0, 0, UnityEngine.Screen.width, UnityEngine.Screen.height);
            var map = MapView.Instance;
            bool mapShown = _b.State == Bootstrap.Screen2.Playing && map != null && map.Ready;
            if (!mapShown) GUI.DrawTexture(new Rect(0, 0, UnityEngine.Screen.width, UnityEngine.Screen.height), _bgTex);

            float s = Mathf.Max(0.35f, safe.height / VH);
            // IMGUI's origin is top-left; safeArea's y is measured from the bottom.
            float top = UnityEngine.Screen.height - safe.yMax;
            _offX = safe.x; _offY = top; _scale = s;
            GUI.matrix = Matrix4x4.TRS(new Vector3(safe.x, top, 0f), Quaternion.identity, new Vector3(s, s, 1f));
            float w = safe.width / s, h = VH;

            switch (_b.State)
            {
                case Bootstrap.Screen2.Loading: Center(w, h, "DayZero", "Building the city…"); break;
                case Bootstrap.Screen2.Failed: DrawFailed(w, h); break;
                case Bootstrap.Screen2.Title: DrawTitle(w, h); break;
                case Bootstrap.Screen2.Playing: DrawGame(w, h); break;
            }
            DrawToasts(w, h);
            DrawAchToast(w, h);   // GameUI.Achievements.cs (E12)
            GUI.matrix = Matrix4x4.identity;
        }

        bool Btn(Rect r, string text, GUIStyle st = null)
        {
            // v0.14.0a press feedback: while a finger holds this button it sinks a little (inset 1.5 / 2 px) over a soft
            // amber halo; the pressed texture itself is darker with an inner glow. Paint only: hit-testing keeps the full rect.
            var ev = Event.current;
            if (ev.type == EventType.Repaint && GUIUtility.hotControl != 0 && GUI.enabled && r.Contains(ev.mousePosition))
            {
                var oc = GUI.color;
                GUI.color = new Color(1f, 0.72f, 0.30f, 0.30f * oc.a);
                GUI.DrawTexture(new Rect(r.x - 10f, r.y - 10f, r.width + 20f, r.height + 20f), _glowTex);
                GUI.color = oc;
                r = new Rect(r.x + 1.5f, r.y + 2f, r.width - 3f, r.height - 4f);
            }
            if (!GUI.Button(r, text, st ?? _button)) return false;
            _b.PlayClick();
            return true;
        }

        void Center(float w, float h, string title, string sub)
        {
            GUI.Label(new Rect(w / 2 - 300, h / 2 - 60, 600, 50), title, _title);
            GUI.Label(new Rect(w / 2 - 300, h / 2, 600, 40), sub, _label);
        }

        void DrawFailed(float w, float h)
        {
            GUI.Box(new Rect(40, 40, w - 80, h - 80), GUIContent.none, _box);
            GUI.Label(new Rect(70, 60, w - 140, 50), "Something went wrong", _title);
            GUI.Label(new Rect(70, 120, w - 140, h - 280), _b.Fatal, _label);
            if (_b.Game != null && Btn(new Rect(70, h - 140, 320, 64), "Back to title", _bigButton)) _b.ToTitle();
        }

        void DrawTitle(float w, float h)
        {
            if (DrawReportScreens(w, h)) return;   // GameUI.Reports.cs: SAVE GAMES opened from the title
            if (DrawAchCustomScreens(w, h)) return;   // GameUI.Achievements.cs / GameUI.CustomGame.cs (E12, E14)
            UiKit.FitLabel(new Rect(60, 44, w - 120, 58), "DAY ZERO: OUTBREAK", 46, TextAnchor.MiddleLeft, UiKit.Amber);
            GUI.DrawTexture(new Rect(60, 104, 420, 3), _accentTex);
            UiKit.FitLabel(new Rect(60, 110, w - 120, 32), "SURVIVE. BUILD. DEFEND. EXPAND.", 22, TextAnchor.MiddleLeft, OLIVE_INK);
            UiKit.FitLabel(new Rect(60, 142, w - 120, 24), "v" + Application.version + "  -  Unity port", 15, TextAnchor.MiddleLeft, UiKit.InkDim, false);
            var panel = new Rect(40, 176, Mathf.Min(w - 80, 720f), 340f);
            UiKit.Panel(panel);
            bool has = _b.HasSave();
            if (KBtn(new Rect(60, 190, 420, 72), has ? "CONTINUE" : "CONTINUE (NO SAVE)", has ? UiRole.Accent : UiRole.Disabled, false, 26)) _b.LoadSaved();
            if (KBtn(new Rect(496, 190, Mathf.Min(244f, panel.xMax - 512f), 72), "LOAD GAME", UiRole.Primary, false, 24)) OpenReportScreen("saves");
            UiKit.Header(new Rect(56, 276, panel.width - 32, 40), "New game", 20);
            for (int d = 0; d < Game.DIFF_NAMES.Length; d++)
                if (KBtn(new Rect(60 + d * 220, 326, 200, 72), Game.DIFF_NAMES[d].ToUpperInvariant(), UiRole.Primary, false, 24)) _b.NewGame(d);
            DrawExpandedToggle(w, h);   // GameUI.Expansion.World.cs
            DrawTutorialToggle(w, h);   // Tutorial.cs (E11)
            DrawTitleExtras(w, h);      // GameUI.Achievements.cs: ACHIEVEMENTS, CUSTOM GAME (E12, E14)
            UiKit.FitLabel(new Rect(60, h - 80, w - 120, 28),
                "Starting a new game replaces the current save. You pick the building for your base on the map.", 16, TextAnchor.MiddleLeft, UiKit.InkDim, false);
        }

        // ---------------- in game ----------------
        // v0.17: every HUD rect comes from HudFrame (non-overlapping docks): stats top-left, season / noise next to
        // it, MENU top-centre, kills / rescued and the resource chips top-right, camera column down the left, one
        // segmented time control at the bottom, collapsible ACTIONS panel on the right. All drawn with the UiKit.
        HudFrame _hf;

        bool KBtn(Rect r, string text, UiRole role = UiRole.Primary, bool selected = false, int maxSize = 22)
        {
            if (!UiKit.Button(r, text, role, selected, maxSize)) return false;
            _b.PlayClick();
            return true;
        }

        // single-line width measure + wrapped-height measure (same font as UiKit's labels)
        GUIStyle _kitMeasure, _kitPara;
        float TextW(string t, int size, bool bold = true)
        {
            if (_kitMeasure == null) _kitMeasure = new GUIStyle(GUI.skin.label) { wordWrap = false, padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0) };
            _kitMeasure.fontSize = size; _kitMeasure.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            _gc.text = t ?? "";
            return _kitMeasure.CalcSize(_gc).x;
        }
        float ParaH(string t, int size, float width)
        {
            if (_kitPara == null) _kitPara = new GUIStyle(GUI.skin.label) { wordWrap = true, padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0) };
            _kitPara.fontSize = size; _kitPara.fontStyle = FontStyle.Normal;
            _gc.text = t ?? "";
            return _kitPara.CalcHeight(_gc, width);
        }

        void DrawGame(float w, float h)
        {
            if (G?.S == null || G.C == null) { Center(w, h, "DayZero", "No game running."); return; }
            _hf = HudFrame.Make(w, h, Game.ExpansionRules && !_b.Picking);
            var map = MapView.Instance;
            if (map != null && map.Ready) DrawMapLabels(map, w, h);
            if (map != null && map.Ready && !_b.Picking && G.C.Started) { DrawConvLabels(map, w, h); DrawRepairMarks(map, w, h); DrawWreckMarks(map, w, h); }
            if (!_b.Picking && G.C.Started) DrawDangerOverlay(map, w, h);   // GameUI.Expansion.World.cs
            if (map != null && !string.IsNullOrEmpty(map.RenderWarning))
                UiKit.FitLabel(new Rect(110, h - 30, w - 220, 24), map.RenderWarning, 15, TextAnchor.MiddleLeft, UiKit.InkDim, false);
            // v0.11.2a drew this every frame, so "Building the map 100%" never went away
            if (map == null || !map.Ready) Center(w, h, "Building the map", map != null ? string.Format("{0}%", (int)(map.Progress * 100f)) : "");

            if (_b.Picking)
            {
                // new game: choose the building for the base (no camp, no clock yet)
                if (KBtn(Blk(_hf.Menu), "MENU", UiRole.Neutral)) _menuOpen = true;
                DrawCameraButtons(map);
                if (map != null && map.Ready && !ModalOpen) DrawStartPick(w, h, map);
                DrawB1Hud(w, h, map);   // GameUI.Alerts.cs (E11 tutorial, E16, E19)
                if (_menuOpen) { BeginAnim(ref _modalAnim, 3, null, 0f, 18f); DrawMenuOverlay(w, h); EndAnim(); }
                return;
            }
            // A sheet on top (side menu, menu overlay, decision, game over) gets the touches: IMGUI hands a press to
            // the first button drawn under it, so the HUD underneath must not see mouse events while one is up.
            bool sheet = SideOpen || _menuOpen || _b.Pending.Count > 0 || _b.Ended || G.S.Phase == "lost" || ReportsModal;
            var evType = Event.current.type;
            bool swallow = sheet && (evType == EventType.MouseDown || evType == EventType.MouseUp || evType == EventType.MouseDrag || evType == EventType.ScrollWheel);
            if (swallow) Event.current.type = EventType.Ignore;
            DrawStats();
            DrawExpWorldHud(w, h, map);   // GameUI.Expansion.World.cs
            if (KBtn(Blk(_hf.Menu), "MENU", UiRole.Neutral)) _menuOpen = true;
            DrawTopRight(w);
            DrawCameraButtons(map);
            float by = _hf.BannerY;
            DrawAttackBanner(w, h, ref by);
            DrawRepairChip(w, ref by);
            DrawResearchNote(w, ref by, map);
            DrawWarHud(w, ref by, map);   // GameUI.Factions.cs
            DrawTutorialBanner(w, ref by, map);   // Tutorial.cs (E11)
            BeginAnim(ref _sideAnim, _panel != Panel.None ? 1 + (int)_panel : _actionsOpen ? 1 : 0, null, 28f, 0f);
            bool sidePanel = DrawSidePanel(w, map);
            EndAnim();
            if (!sidePanel) DrawActionsDock(w, h, map);   // v0.17 kit dock (GameUI.PhaseA.cs)
            if (map != null && !ModalOpen)
            {
                BeginAnim(ref _sheetAnim, SheetKey(map), map.SelectedCamp, 0f, 22f);
                if (DrawCampSheets(w, h, map)) { }   // GameUI.Camp.cs: outpost bar, trap line, camp / trap sheets
                else if (map.TargetTool) DrawTargetBar(w, h, map);
                else if (map.ActiveTool != MapView.Tool.None) DrawPlacementBar(w, h, map);
                else if (map.BuildingLot >= 0) DrawBuildingSheet(w, h, map);
                else if (map.SelectedGroup >= 0) DrawGroupSheet(w, h, map);
                else if (map.SelectedCamp != "") DrawFactionSheet(w, h, map);   // GameUI.Factions.cs
                else if (map.SelectedWreck >= 0) DrawWreckSheet(w, h, map);
                else if (map.SelectedFence >= 0) DrawFenceSheet(w, h, map);
                else if (map.SelectedTower >= 0) DrawTowerSheet(w, h, map);
                else if (map.SelectedRun >= 0) DrawWallSheet(w, h, map);
                EndAnim();
            }
            DrawTimeControls(w, h);
            DrawB1Hud(w, h, map);   // GameUI.Alerts.cs (E16 speed drop, E17 alerts, E19 long-press card)
            if (swallow) Event.current.type = evType;

            // modal sheets fade and slide in (v0.14.0a); a new decision or another side menu restarts the animation
            bool over = _b.Ended || G.S.Phase == "lost";
            int mk = over ? 1 : _b.Pending.Count > 0 ? 2 : _menuOpen ? 3 : SideOpen ? 4 : 0;
            BeginAnim(ref _modalAnim, mk, mk == 2 ? (object)_b.Pending[0] : mk == 4 ? _side.Open : null, 0f, 18f);
            if (over) DrawGameOver(w, h);
            else if (_b.Pending.Count > 0 && DrawWarDemand(w, h, _b.Pending[0])) { }   // GameUI.Factions.cs
            else if (_b.Pending.Count > 0) DrawDecision(w, h, _b.Pending[0]);
            else if (_menuOpen) DrawMenuOverlay(w, h);
            else if (SideOpen) DrawSideMenu(w, h);
            else if (!DrawDaySting(w, h)) DrawMorningReport(w, h);   // v0.19 day sting first (GameUI.DaySting.cs), then the E6 report
            EndAnim();
        }

        // ---------------- v0.14.0a open animations ----------------
        // Paint-only: a sheet that just opened starts slightly offset and translucent and eases in over ~0.2 s via
        // GUI.matrix / GUI.color (IMGUI maps touches through the same matrix, so taps still land on what is drawn).
        struct Anim { public int Key; public object Obj; public float T0; }
        Anim _modalAnim, _sideAnim, _sheetAnim;
        Matrix4x4 _animM; Color _animC; bool _animOn;
        const float ANIM_S = 0.22f;

        void BeginAnim(ref Anim a, int key, object obj, float dx, float dy)
        {
            float now = Time.unscaledTime;
            if (key != a.Key || !ReferenceEquals(obj, a.Obj)) { a.Key = key; a.Obj = obj; a.T0 = now; }
            _animM = GUI.matrix; _animC = GUI.color; _animOn = true;
            if (key == 0) return;
            float t = Mathf.Clamp01((now - a.T0) / ANIM_S);
            if (t >= 1f) return;
            float k = 1f - (1f - t) * (1f - t) * (1f - t);   // ease-out cubic
            GUI.matrix = _animM * Matrix4x4.Translate(new Vector3(dx * (1f - k), dy * (1f - k), 0f));
            GUI.color = new Color(_animC.r, _animC.g, _animC.b, _animC.a * (0.25f + 0.75f * k));
        }

        void EndAnim()
        {
            if (!_animOn) return;
            GUI.matrix = _animM; GUI.color = _animC; _animOn = false;
        }

        static int SheetKey(MapView map)
        {
            if (map.TargetTool) return 2;
            if (map.ActiveTool != MapView.Tool.None) return 3 + (int)map.ActiveTool;
            if (map.BuildingLot >= 0) return 100000 + map.BuildingLot;
            if (map.SelectedGroup >= 0) return 200000 + map.SelectedGroup;
            if (map.SelectedCamp != "") return 50;
            if (map.SelectedWreck >= 0) return 300000 + map.SelectedWreck;
            if (map.SelectedFence >= 0) return 400000 + map.SelectedFence;
            if (map.SelectedTower >= 0) return 500000 + map.SelectedTower;
            if (map.SelectedRun >= 0) return 600000 + map.SelectedRun;
            return 1;
        }

        /// <summary>Start-site selection: banner, then the selected building's info with Confirm / Cancel.</summary>
        void DrawStartPick(float w, float h, MapView map)
        {
            float bw = Mathf.Clamp(w - 600f, 460f, 680f);
            var ban = Blk(new Rect(w / 2 - bw / 2, _hf.Menu.yMax + 8f, bw, 84f));
            var bc = UiKit.Panel(ban);
            var br = UiLayout.Rows(bc, 2f, 1.3f, 1f);
            UiKit.FitLabel(br[0], "TAP ANY BUILDING TO MAKE IT YOUR BASE", 24, TextAnchor.MiddleCenter, UiKit.Amber);
            UiKit.FitLabel(br[1], "Drag to look around, pinch to zoom, then tap the building you want", 16, TextAnchor.MiddleCenter, UiKit.InkDim, false);
            if (map.SelectedLot < 0 || !map.SelectionInfo(out string type, out string size, out int floors, out string district)) return;
            string err = map.SelectError;
            float pw = Mathf.Min(640f, w - 220f), ph = 236f;
            var panel = Blk(new Rect(w / 2 - pw / 2, h - ph - 12f, pw, ph));
            var pc = UiKit.Panel(panel, type);
            var L = new UiLayout(pc, 6f);
            UiKit.FitLabel(L.Top(28f), string.Format("Size {0}  -  {1} floor{2}  -  {3}", size, floors, floors == 1 ? "" : "s", district), 19, TextAnchor.MiddleLeft, UiKit.Ink, false);
            if (err != "") UiKit.FitLabel(L.Top(26f), err, 17, TextAnchor.MiddleLeft, UiKit.Bad);
            else UiKit.FitLabel(L.Top(26f), "Your people move in. Fence the block in from DEFENSE, then add a gate.", 16, TextAnchor.MiddleLeft, UiKit.InkDim, false);
            var bb = UiLayout.Columns(L.Bottom(58f), 12f, 1f, 1f);
            if (KBtn(bb[0], "CONFIRM", err == "" ? UiRole.Accent : UiRole.Disabled)) _b.ConfirmStart();
            if (KBtn(bb[1], "CANCEL", UiRole.Neutral)) map.ClearSelection();
        }

        void Shadowed(Rect r, string text, GUIStyle st)
        {
            // shadow styles are cached per source style (v0.14.0a: was a new GUIStyle per call, every frame); callers
            // that build a style per frame would grow the cache, so it is simply reset when it gets large
            if (!_shadowOf.TryGetValue(st, out var sh))
            {
                if (_shadowOf.Count > 48) _shadowOf.Clear();
                sh = new GUIStyle(st); sh.normal.textColor = new Color(0, 0, 0, 0.75f); sh.richText = false;
                _shadowOf[st] = sh;
            }
            GUI.Label(new Rect(r.x + 1.5f, r.y + 1.5f, r.width, r.height), StripTags(text), sh);
            GUI.Label(r, text, st);
        }

        static string StripTags(string s) => s.IndexOf('<') < 0 ? s : System.Text.RegularExpressions.Regex.Replace(s, "<.*?>", "");
        static string Col(Color c, string s) => "<color=#" + ColorUtility.ToHtmlStringRGB(c) + ">" + s + "</color>";

        /// <summary>Top-left stats card: four stat tiles, the clock row (tinted by the hour), the phase line and the
        /// colony line.</summary>
        void DrawStats()
        {
            var c = G.C; var f = _hf;
            var alive = G.AlivePeople();
            int beds = 0, used = 0;
            foreach (var b in c.Bases) { int r = G.CampResidents(b.Id); used += r; beds += r + G.CampFree(b); }
            int dead = c.People.Count(p => p.St == "dead");
            int z = G.ZombieCount();
            UiKit.Panel(Blk(f.Stats));
            StatTile(f.StatCell[0], "SURVIVORS", alive.Count.ToString(), OLIVE_INK);
            StatTile(f.StatCell[1], "BEDS", used + "/" + beds, used > beds ? RED_INK : OLIVE_INK);
            StatTile(f.StatCell[2], "ZOMBIES", z.ToString(), z > 0 ? RED_INK : OLIVE_INK);
            StatTile(f.StatCell[3], "DEAD", dead.ToString(), UiKit.Ink);
            DrawClockChip(f.TimeRow);
            var ri = G.RaidInfo();
            bool alert = ri != null && (ri.On || ri.Warned);
            var pr = f.PhaseRow;
            if (alert) DrawAlertPulse(new Rect(pr.xMax - 22f, pr.y + 1f, 22f, 22f), ri.On);
            UiKit.FitLabel(new Rect(pr.x, pr.y, pr.width - (alert ? 28f : 0f), pr.height), PhaseText(), 17, TextAnchor.MiddleLeft, alert ? RED_INK : AMBER, true, 11);
            UiKit.FitLabel(f.ColonyRow, string.Format("{0}  -  {1} survivors  -  {2} kills", c.Name, alive.Count, c.Stat("kills")), 15, TextAnchor.MiddleLeft, OLIVE_INK, false, 11);
        }

        void StatTile(Rect r, string label, string value, Color vc)
        {
            UiKit.RowPlate(r, 0);
            var rows = UiLayout.Rows(UiLayout.Inset(r, 9f, 3f, 6f, 2f), 0f, -13f, 1f);
            UiKit.FitLabel(rows[0], label, 12, TextAnchor.MiddleLeft, UiKit.InkMute, true, 9, false);
            UiKit.FitLabel(rows[1], value, 22, TextAnchor.MiddleLeft, vc, true, 12);
        }

        /// <summary>The clock row sits on a soft plate tinted by the hour (warm day, orange dusk / dawn, blue night)
        /// with a drawn sun / moon badge at its left.</summary>
        void DrawClockChip(Rect r)
        {
            float hour = (float)(G.S.T % 24.0);
            var oldBg = GUI.backgroundColor;
            GUI.backgroundColor = UiSkin.SkyTint(hour);
            GUI.Box(r, GUIContent.none, _tintBox);
            GUI.backgroundColor = oldBg;
            bool night = hour >= 19.5f || hour < 5.5f;
            if (Event.current.type == EventType.Repaint)
                GUI.DrawTexture(new Rect(r.x + 4f, r.y + 3f, r.height - 6f, r.height - 6f), night ? Icons.Tex("moon", new Color(0.30f, 0.38f, 0.62f)) : Icons.Tex("sun", new Color(0.80f, 0.56f, 0.18f)), ScaleMode.ScaleToFit, true);
            float tx = r.x + r.height + 4f;
            bool isNight = G.IsNight();
            float nw = isNight ? 66f : 0f;
            UiKit.FitLabel(new Rect(tx, r.y, r.xMax - tx - nw - 6f, r.height), G.ClockText().ToUpperInvariant(), 21, TextAnchor.MiddleLeft, UiKit.Ink, true, 12);
            if (isNight) UiKit.FitLabel(new Rect(r.xMax - nw - 6f, r.y, nw, r.height), "NIGHT", 15, TextAnchor.MiddleRight, AMBER, true, 10);
        }

        /// <summary>v0.14.0a: pulsing siren glyph with a red halo (attack warned: slow, attack on: fast).</summary>
        void DrawAlertPulse(Rect r, bool on)
        {
            float p = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * (on ? 7f : 3.5f));
            var oc = GUI.color;
            GUI.color = new Color(1f, 0.28f, 0.2f, (0.35f + 0.45f * p) * oc.a);
            float g = 10f + 6f * p;
            GUI.DrawTexture(new Rect(r.x - g, r.y - g, r.width + g * 2f, r.height + g * 2f), _glowTex);
            GUI.color = oc;
            GUI.DrawTexture(r, Icons.GlyphTex("siren", RED_GLYPH), ScaleMode.ScaleToFit, true);
        }

        // ticking resource counters: shown value eases toward the real one; text is rebuilt only when the shown
        // integer changes, so a steady HUD allocates nothing
        readonly float[] _chipShown = new float[7];
        readonly int[] _chipInt = new int[7];
        readonly string[] _chipText = new string[7];
        readonly float[] _chipFlashT = new float[7];
        readonly int[] _chipDir = new int[7];
        object _chipState;
        float _chipLastT;

        void TickChips()
        {
            var c = G.C;
            float now = Time.unscaledTime, dt = Mathf.Clamp(now - _chipLastT, 0f, 0.25f);
            _chipLastT = now;
            bool snap = !ReferenceEquals(_chipState, G.S);
            _chipState = G.S;
            float ease = 1f - Mathf.Exp(-dt * 7f);
            for (int i = 0; i < CHIP_KEYS.Length; i++)
            {
                float target = i < 5 ? (float)c.Res.Get(CHIP_KEYS[i], 0.0) : i == 5 ? (float)G.MatsTotal() : (float)c.Morale;
                if (snap) { _chipShown[i] = target; _chipDir[i] = 0; }
                else if ((int)target != (int)_chipShown[i] && Mathf.Abs(target - _chipShown[i]) >= 1f)
                {
                    int dir = target > _chipShown[i] ? 1 : -1;
                    if (dir != _chipDir[i] || now - _chipFlashT[i] > 0.6f) { _chipFlashT[i] = now; _chipDir[i] = dir; }
                }
                float v = _chipShown[i] + (target - _chipShown[i]) * ease;
                if (Mathf.Abs(target - v) < 0.5f) v = target;
                _chipShown[i] = v;
                int n = (int)v;
                if (_chipText[i] == null || n != _chipInt[i]) { _chipInt[i] = n; _chipText[i] = n.ToString(); }
            }
        }

        /// <summary>Top right: ZOMBIES KILLED / RESCUED counters, then the resource chips (drawn badge icon, value,
        /// name when there is room); a change flashes the value green / red for a moment.</summary>
        void DrawTopRight(float w)
        {
            var c = G.C; var f = _hf;
            UiKit.Panel(Blk(f.Counters));
            Counter(f.CounterHalf[0], c.Stat("kills").ToString(), "ZOMBIES KILLED", UiKit.Amber);
            Counter(f.CounterHalf[1], c.Stat("rescued").ToString(), "RESCUED", OLIVE_INK);
            if (Event.current.type == EventType.Layout || _chipText[0] == null) TickChips();
            float now = Time.unscaledTime;
            for (int i = 0; i < CHIP_KEYS.Length; i++)
            {
                var r = Blk(f.Chips[i]);
                UiKit.Chip(r, "", UiRole.Neutral);
                float s = r.height - 8f;
                if (Event.current.type == EventType.Repaint)
                    GUI.DrawTexture(new Rect(r.x + 9f, r.y + 4f, s, s), Icons.Tex(CHIP_KEYS[i], CHIP_COL[i]), ScaleMode.ScaleToFit, true);
                var ink = UiKit.Ink;
                float fl = _chipDir[i] == 0 ? 0f : Mathf.Clamp01(1f - (now - _chipFlashT[i]) / 0.9f);
                if (fl > 0f) ink = Color.Lerp(ink, _chipDir[i] > 0 ? new Color(0.62f, 0.92f, 0.45f) : new Color(1f, 0.45f, 0.35f), fl);
                float tx = r.x + 9f + s + 6f, tw = r.xMax - tx - 6f;
                if (f.ChipNames)
                {
                    float vw = Mathf.Min(TextW(_chipText[i], 19) + 4f, tw * 0.62f);
                    UiKit.FitLabel(new Rect(tx, r.y, vw, r.height), _chipText[i], 19, TextAnchor.MiddleLeft, ink, true, 11);
                    UiKit.FitLabel(new Rect(tx + vw + 3f, r.y + 1f, tw - vw - 3f, r.height), CHIP_KEYS[i], 13, TextAnchor.MiddleRight, UiKit.InkMute, false, 9, false);
                }
                else UiKit.FitLabel(new Rect(tx, r.y, tw, r.height), _chipText[i], 19, TextAnchor.MiddleLeft, ink, true, 11);
            }
        }

        void Counter(Rect r, string value, string label, Color vc)
        {
            float vw = Mathf.Clamp(TextW(value, 24) + 6f, 26f, r.width * 0.45f);
            UiKit.FitLabel(new Rect(r.x, r.y, vw, r.height), value, 24, TextAnchor.MiddleLeft, vc, true, 12);
            UiKit.FitLabel(new Rect(r.x + vw + 4f, r.y + 2f, r.width - vw - 4f, r.height - 2f), label, 15, TextAnchor.MiddleLeft, UiKit.InkDim, true, 10);
        }

        static readonly string[] CAM_LABELS = { "HOME", "+", "-", "MAP" };

        void DrawCameraButtons(MapView map)
        {
            var f = _hf;
            for (int i = 0; i < 4; i++)
            {
                if (!KBtn(Blk(f.Cam[i]), CAM_LABELS[i], UiRole.Neutral, false, i == 1 || i == 2 ? 30 : 18)) continue;
                switch (i)
                {
                    case 0: map?.Home(); break;
                    case 1: map?.ZoomIn(); break;
                    case 2: map?.ZoomOut(); break;
                    default: map?.Overview(); break;
                }
            }
        }

        /// <summary>One segmented control: PLAY / PAUSE (amber and breathing while paused), speed, +1h, +6h.</summary>
        void DrawTimeControls(float w, float h)
        {
            var f = _hf;
            UiKit.Panel(Blk(f.TimeBar));
            if (_b.Paused) DrawHalo(UiLayout.Inset(f.TimeSeg[0], 6f), new Color(1f, 0.72f, 0.30f), 2.6f);
            if (KBtn(f.TimeSeg[0], _b.Paused ? "PLAY" : "PAUSE", _b.Paused ? UiRole.Accent : UiRole.Neutral)) _b.Paused = !_b.Paused;
            if (KBtn(f.TimeSeg[1], "x" + _b.Speed, UiRole.Neutral, _b.Speed > 1)) _b.Speed = _b.Speed >= 4 ? 1 : _b.Speed * 2;
            if (KBtn(f.TimeSeg[2], "+1h", UiRole.Neutral)) _b.Skip(1);
            if (KBtn(f.TimeSeg[3], "+6h", UiRole.Neutral)) _b.Skip(6);
        }

        /// <summary>Soft pulsing halo behind a control (v0.14.0a).</summary>
        void DrawHalo(Rect r, Color c, float speed)
        {
            float p = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * speed);
            var oc = GUI.color;
            GUI.color = new Color(c.r, c.g, c.b, (0.18f + 0.30f * p) * oc.a);
            float g = 12f + 6f * p;
            GUI.DrawTexture(new Rect(r.x - g, r.y - g, r.width + g * 2f, r.height + g * 2f), _glowTex);
            GUI.color = oc;
        }

        // ---------------- map labels ----------------
        // v0.17: one LabelPlacer per repaint. Labels are placed in priority order (own camps, faction camps, SOS,
        // districts, landmarks) and a label is shown only where it overlaps no HUD rect and no label placed before
        // it, so map text never runs under the HUD or into other text.
        readonly LabelPlacer _lp = new LabelPlacer();

        void BeginLabels()
        {
            _lp.Clear();
            if (!_b.Picking) _hf.Avoid(_lp.Avoid);
            else { _lp.Avoid.Add(_hf.Menu); foreach (var r in _hf.Cam) _lp.Avoid.Add(r); }
            _lp.Avoid.AddRange(_blocks);   // everything else on the HUD (sheets, banners, ACTIONS) from the layout pass
        }

        void DrawMapLabels(MapView map, float w, float h)
        {
            if (Event.current.type != EventType.Repaint) return;   // paint only; taps go to the map
            BeginLabels();
            // own camps first: a name tag lifted above the roof so it clears the colony name painted on the wall
            foreach (var b in G.C.Bases)
            {
                var p = new Vector3(b.Pos.x + 0.5f, 3.6f, -(b.Pos.y + 0.5f));
                if (!map.TryScreen(p, out var sp)) continue;
                var v = ToVirtual(sp);
                string t = G.BaseLabel(b);
                float tw = Mathf.Min(300f, TextW(t, 17) + 30f);
                var r = new Rect(v.x - tw / 2f, v.y - 46f - 30f, tw, 30f);
                if (!_lp.PlaceNudged(ref r, 24f)) continue;
                var oc = GUI.color;
                GUI.color = new Color(0.95f, 0.70f, 0.28f, 0.85f * oc.a);
                GUI.DrawTexture(new Rect(v.x - 1f, r.yMax, 2f, Mathf.Max(0f, v.y - 6f - r.yMax)), _xWhiteTex);
                GUI.color = oc;
                UiKit.Chip(r, t, UiRole.Accent, 17);
            }
            if (!_b.Picking && G.C.Started) PlanMarks(map, w, h);   // GameUI.WorldMarks.cs: reserve marker labels
            float d = map.Dist;
            int drawn = 0;
            for (int pass = 0; pass < 2; pass++)
                foreach (var lb in map.Labels)
                {
                    if ((lb.Tier == 2) != (pass == 0)) continue;
                    if (lb.Tier == 2 ? d < 70f : lb.Tier == 0 ? d > 150f : d > 75f) continue;
                    if (!map.TryScreen(lb.Pos, out var sp)) continue;
                    var v = ToVirtual(sp);
                    if (v.x < -100 || v.x > w + 100 || v.y < 0 || v.y > h) continue;
                    var st = lb.Tier == 2 ? _mapDistrict : _mapLabel;
                    _gc.text = lb.Text;
                    float tw = st.CalcSize(_gc).x + 4f, th = lb.Tier == 2 ? 28f : 22f;
                    var r = new Rect(v.x - tw / 2f, v.y - th / 2f, tw, th);
                    if (!_lp.Place(r)) continue;
                    GUI.Label(new Rect(r.x + 1.5f, r.y + 1.5f, r.width, r.height), lb.Text, lb.Tier == 2 ? _mapDistrictSh : _mapLabelSh);
                    GUI.Label(r, lb.Text, st);
                    if (++drawn >= 60) break;
                }
        }

        Texture2D _xWhiteTex;

        // ---------------- tabbed screens (COLONY / JOBS / LOG / MENU) ----------------
        int _lastTab = -1;

        void DrawMenuOverlay(float w, float h)
        {
            if (DrawReportScreens(w, h)) return;   // GameUI.Reports.cs: SAVE GAMES / MEMORIAL
            if (DrawAchCustomScreens(w, h)) return;   // GameUI.Achievements.cs (E12)
            GUI.DrawTexture(new Rect(0, 0, w, h), _dimTex);
            var f = MenuFrame.Make(w, h);
            UiKit.Panel(f.TabPlate);
            for (int i = 0; i < TABS.Length; i++)
                if (KBtn(f.Tabs[i], TABS[i].ToUpperInvariant(), _tab == i ? UiRole.Accent : UiRole.Primary, _tab == i)) _tab = i;
            if (KBtn(f.Close, "CLOSE", UiRole.Danger)) { _menuOpen = false; _lastTab = -1; return; }
            if (_tab != _lastTab) { _lastTab = _tab; UiKit.ScrollTo("ui_jobs", 0f); UiKit.ScrollTo("ui_log", 0f); }
            switch (_tab)
            {
                case 0: DrawColony(f.Content); break;
                case 1: DrawJobs(f.Content); break;
                case 2: DrawLog(f.Content); break;
                case 3: DrawMenu(f.Content); break;
            }
        }

        string PhaseText()
        {
            string ph = G.PhaseOf(Math.Max(0, G.CurCol));
            string t;
            switch (ph)
            {
                case "spread": t = string.Format("Infection {0}% of the city", (int)(G.SpreadPct() * 100.0)); break;
                case "fall": t = string.Format("THE FALL - hold {0:0.0} more days", G.HoldLeft() / 24.0); break;
                case "lost": t = "Overrun"; break;
                default: t = ph; break;
            }
            var ri = G.RaidInfo();
            if (ri != null && (ph == "spread" || ph == "fall"))
            {
                if (ri.On) t = string.Format("NIGHT ATTACK - {0} closing in", ri.Spawned);
                else if (ri.Warned) t = string.Format("ATTACK TONIGHT - about {0}", ri.N);
            }
            return t;
        }

        static readonly HashSet<string> ICON_RES = new HashSet<string> { "food", "water", "meds", "ammo", "fuel", "mats", "morale" };

        Color ResCol(string key)
        {
            int i = Array.IndexOf(CHIP_KEYS, key);
            return i >= 0 ? CHIP_COL[i] : new Color(0.62f, 0.60f, 0.50f);
        }

        /// <summary>COLONY: summary card with stat chips, an attack warning strip, then CAMPS / STOCKPILE / JOBS cards.</summary>
        void DrawColony(Rect r)
        {
            var c = G.C;
            var alive = G.AlivePeople();
            var L = new UiLayout(r, 10f);
            var top = UiKit.Panel(L.Top(104f), c.Name);
            int z = G.ZombieCount();
            var cr = UiLayout.Even(new Rect(top.x, top.y, top.width, Mathf.Min(38f, top.height)), 6, 8f);
            UiKit.Chip(cr[0], alive.Count + " SURVIVORS", UiRole.Primary, 17);
            UiKit.Chip(cr[1], c.Bases.Count + (c.Bases.Count == 1 ? " CAMP" : " CAMPS"), UiRole.Primary, 17);
            UiKit.Chip(cr[2], z + " ZOMBIES", z > 0 ? UiRole.Danger : UiRole.Neutral, 17);
            UiKit.Chip(cr[3], c.Stat("kills") + " KILLS", UiRole.Accent, 17);
            UiKit.Chip(cr[4], c.Stat("rescued") + " RESCUED", UiRole.Primary, 17);
            UiKit.Chip(cr[5], "DAY " + G.Day(), UiRole.Neutral, 17);
            var ri = G.RaidInfo();
            if (ri != null && (ri.On || ri.Warned))
                UiKit.Chip(L.Top(42f), ri.On ? "Your camp is under attack. Guards are on the line." : "Scouts expect an attack tonight. Put Soldiers and Cops on guard.", UiRole.Danger, 18);
            var cols = UiLayout.Columns(L.Rest, 10f, 1.2f, 1f, 0.85f);

            // camps
            var cc = UiKit.Panel(cols[0], "Camps (" + c.Bases.Count + ")");
            var cl = UiKit.BeginList("ui_camps", cc, c.Bases.Count, 62f, 6f);
            for (int i = cl.First; i < cl.Last; i++)
            {
                var b = c.Bases[i];
                var row = cl.Row(i);
                UiKit.RowPlate(row, i);
                var rr = UiLayout.Rows(UiLayout.Inset(row, 12f, 5f, 10f, 6f), 3f, 1f, 1f);
                UiKit.FitLabel(rr[0], G.BaseLabel(b), 19, TextAnchor.MiddleLeft, UiKit.Ink);
                if (b.Done < 1.0) UiKit.Bar(rr[1], (float)b.Done, UiRole.Accent, string.Format("BUILDING {0}%", (int)(b.Done * 100)));
                else
                {
                    int res = G.CampResidents(b.Id), free = G.CampFree(b);
                    UiKit.FitLabel(rr[1], string.Format("{0} living here  -  {1} bed{2} free", res, free, free == 1 ? "" : "s"), 15, TextAnchor.MiddleLeft, UiKit.InkDim, false);
                }
            }
            UiKit.EndList();

            // stockpile (every resource the colony holds, two columns)
            var sc = UiKit.Panel(cols[1], "Stockpile");
            var res2 = c.Res.Where(kv => kv.Value >= 1.0).ToList();
            int rowsN = (res2.Count + 1) / 2;
            float cellH = 44f, gap = 6f;
            var win = UiKit.BeginScroll("ui_stock", sc, Mathf.Max(0f, rowsN * (cellH + gap) - gap));
            var area = new Rect(0f, 0f, sc.width - 10f, rowsN * (cellH + gap));
            for (int i = 0; i < res2.Count; i++)
            {
                var cell = UiLayout.Cell(area, 2, i, cellH, gap);
                if (cell.yMax < win.y || cell.y > win.yMax) continue;
                UiKit.RowPlate(cell, i / 2);
                string key = res2[i].Key;
                float ix = cell.x + 8f;
                if (ICON_RES.Contains(key))
                {
                    if (Event.current.type == EventType.Repaint) GUI.DrawTexture(new Rect(ix, cell.y + 7f, 30f, 30f), Icons.Tex(key, ResCol(key)), ScaleMode.ScaleToFit, true);
                    ix += 36f;
                }
                else ix += 4f;
                var parts = UiLayout.Columns(new Rect(ix, cell.y, cell.xMax - ix - 10f, cell.height), 6f, 1f, -58f);
                UiKit.FitLabel(parts[0], key, 16, TextAnchor.MiddleLeft, UiKit.InkDim, true, 10);
                UiKit.FitLabel(parts[1], ((int)res2[i].Value).ToString(), 19, TextAnchor.MiddleRight, UiKit.Ink, true, 11);
            }
            if (res2.Count == 0) UiKit.FitLabel(new Rect(0f, 0f, area.width, 30f), "Nothing stored yet.", 16, TextAnchor.MiddleLeft, UiKit.InkDim, false);
            UiKit.EndScroll();

            // jobs
            var jc = UiKit.Panel(cols[2], "Jobs");
            var JL = new UiLayout(jc, 8f);
            if (KBtn(JL.Bottom(58f), "MANAGE JOBS", UiRole.Accent)) _tab = 1;
            int kids = alive.Count(p => p.Job == "Child");
            int nj = Data.JOBS.Length + (kids > 0 ? 1 : 0);
            var jl = UiKit.BeginList("ui_jobcount", JL.Rest, nj, 30f, 3f);
            for (int i = jl.First; i < jl.Last; i++)
            {
                var row = jl.Row(i);
                UiKit.RowPlate(row, i);
                string name = i < Data.JOBS.Length ? Data.JOBS[i] : "Children";
                int n = i < Data.JOBS.Length ? G.CountJob(Data.JOBS[i]) : kids;
                var parts = UiLayout.Columns(UiLayout.Inset(row, 12f, 2f, 12f, 2f), 6f, 1f, -48f);
                UiKit.FitLabel(parts[0], name, 17, TextAnchor.MiddleLeft, n > 0 ? UiKit.Ink : UiKit.InkMute, false);
                UiKit.FitLabel(parts[1], n.ToString(), 19, TextAnchor.MiddleRight, n > 0 ? UiKit.Amber : UiKit.InkMute);
            }
            UiKit.EndList();
        }

        /// <summary>JOBS: every survivor in one scroll list; name, HP bar, status / stage / trait chips, and a
        /// scroll-wheel job picker per row (stable id per survivor).</summary>
        void DrawJobs(Rect r)
        {
            var people = G.AlivePeople();
            var c = UiKit.Panel(r, string.Format("Survivors ({0})  -  drag the wheel to change a job", people.Count));
            var lst = UiKit.BeginList("ui_jobs", c, people.Count, 104f, 6f);
            for (int i = lst.First; i < lst.Last; i++) JobRow(lst.Row(i), i, people[i]);
            UiKit.EndList();
        }

        void JobRow(Rect row, int i, Person p)
        {
            UiKit.RowPlate(row, i);
            var parts = UiLayout.Columns(UiLayout.Inset(row, 14f, 8f, 8f, 8f), 14f, 1f, -230f);
            var info = UiLayout.Rows(parts[0], 8f, 1.15f, 1f);
            var top = UiLayout.Columns(info[0], 14f, 1f, -210f);
            UiKit.FitLabel(top[0], p.Name, 24, TextAnchor.MiddleLeft, UiKit.Ink, true, 14);
            int hp = (int)p.Hp;
            UiKit.Bar(UiLayout.Inset(top[1], 0f, 4f, 0f, 4f), (float)(p.Hp / 100.0), hp >= 60 ? UiRole.Primary : hp >= 30 ? UiRole.Accent : UiRole.Danger, "HP " + hp);
            // chips flow left to right while they fit
            var cr = info[1];
            float x = cr.x, ch = cr.height - 2f, cy = cr.y + 1f;
            bool busy = p.St == "work" || p.St == "move";
            ChipFlow(ref x, cr.xMax, cy, ch, StateText(p).ToUpperInvariant(), busy ? UiRole.Accent : UiRole.Primary);
            if (p.Stage != null && p.Stage != "adult") ChipFlow(ref x, cr.xMax, cy, ch, p.Stage.ToUpperInvariant(), UiRole.Neutral);
            if (Game.ExpansionRules && p.Traits != null)
                foreach (var id in p.Traits)
                {
                    bool hid = id == p.TraitHidden;
                    string t = hid ? "?" : (Game.Traits.TryGetValue(id, out var td) ? td.Name : id).ToUpperInvariant();
                    if (!ChipFlow(ref x, cr.xMax, cy, ch, t, UiRole.Neutral)) break;
                }
            // job wheel
            int cur = Array.IndexOf(Data.JOBS, p.Job);
            if (cur < 0) { UiKit.Chip(UiLayout.Center(parts[1], parts[1].width, 44f), p.Job.ToUpperInvariant(), UiRole.Neutral, 18); return; }
            int pick = UiWheel.Vertical("job_" + p.Id, parts[1], Data.JOBS, cur, 3, 20);
            if (pick >= 0 && pick != cur) SetJob(p, Data.JOBS[pick]);
        }

        /// <summary>A chip sized to its text at x (advanced past it); false when it would not fit before maxX.</summary>
        bool ChipFlow(ref float x, float maxX, float y, float h, string text, UiRole role)
        {
            float cw = Mathf.Clamp(TextW(text, 15) + 30f, 52f, 220f);
            if (x + cw > maxX) return false;
            UiKit.Chip(new Rect(x, y, cw, h), text, role, 15);
            x += cw + 8f;
            return true;
        }

        static string StateText(Person p)
        {
            switch (p.St)
            {
                case "base": return "at camp";
                case "move": return "travelling";
                case "work": return string.IsNullOrEmpty(p.Goal) ? "working" : "on " + p.Goal;
                default: return p.St;
            }
        }

        /// <summary>The wheel landed on a job: same set_job call as before; a job the survivor can't take (a child,
        /// no free gear...) shows its reason and the wheel rolls back.</summary>
        void SetJob(Person p, string job)
        {
            string err = _b.Act("set_job", p.Id, job);
            if (err == "")
            {
                string desc;
                _b.Toast(p.Name + " is now a " + job + (Data.JOB_DESC.TryGetValue(job, out desc) ? ". " + desc : "."));
            }
            else _b.Toast(err);
        }

        // log rows: wrapped text, heights cached until the log or the width changes
        readonly List<float> _logH = new List<float>();
        int _logN = -1; float _logW = -1f; object _logTop;

        /// <summary>LOG: newest first in one scroll list; time chip + wrapped text, deaths / sirens / major events in red.</summary>
        void DrawLog(Rect r)
        {
            var log = G.S.Log.Where(e => e.Col < 0 || e.Col == G.CurCol).Reverse().ToList();
            var c = UiKit.Panel(r, string.Format("Log ({0})  -  newest first", log.Count));
            float rowW = c.width - 10f, textW = rowW - 12f - 118f - 12f - 10f;
            object topE = log.Count > 0 ? log[0] : null;
            if (log.Count != _logN || Mathf.Abs(textW - _logW) > 0.5f || !ReferenceEquals(topE, _logTop))
            {
                _logN = log.Count; _logW = textW; _logTop = topE; _logH.Clear();
                foreach (var e in log) _logH.Add(Mathf.Max(46f, ParaH(e.Text, 17, textW) + 18f));
            }
            float total = 0f; foreach (var hh in _logH) total += hh + 6f;
            var win = UiKit.BeginScroll("ui_log", c, Mathf.Max(0f, total - 6f));
            float y = 0f;
            for (int i = 0; i < log.Count && i < _logH.Count; i++)
            {
                float rh = _logH[i];
                if (y + rh >= win.y && y <= win.yMax)
                {
                    var e = log[i];
                    bool bad = e.Kind == "death" || e.Kind == "siren" || e.Kind == "major";
                    var row = new Rect(0f, y, rowW, rh);
                    UiKit.RowPlate(row, i);
                    string when = string.Format("D{0} {1:00}:00", (int)(e.T / 24.0) + 1, (int)e.T % 24);
                    UiKit.Chip(new Rect(row.x + 12f, row.y + 8f, 118f, 30f), when, bad ? UiRole.Danger : UiRole.Neutral, 15);
                    UiKit.Paragraph(new Rect(row.x + 142f, row.y + 9f, textW, rh - 14f), e.Text, 17, bad ? new Color(1f, 0.62f, 0.52f) : UiKit.InkDim);
                }
                y += rh + 6f;
            }
            if (log.Count == 0) UiKit.FitLabel(new Rect(0f, 0f, rowW, 32f), "Nothing has happened yet.", 17, TextAnchor.MiddleLeft, UiKit.InkDim, false);
            UiKit.EndScroll();
        }

        static readonly string[] VOL_ITEMS = { "0%", "10%", "20%", "30%", "40%", "50%", "60%", "70%", "80%", "90%", "100%" };

        /// <summary>MENU: GAME card (save / load / title) and SETTINGS card (sound, volume wheel, film look, help).</summary>
        void DrawMenu(Rect r)
        {
            if (DrawSettingsSheet(r)) return;   // v0.18 SETTINGS sheet (GameUI.Settings.cs)
            var cols = UiLayout.Columns(new Rect(r.x, r.y, r.width, Mathf.Min(r.height, 500f)), 10f, 1f, 1.25f);
            var gc = UiKit.Panel(cols[0], "Game");
            var G2 = new UiLayout(gc, 12f);
            if (KBtn(G2.Top(62f), "SAVE NOW", UiRole.Accent)) _b.SaveNow();
            if (KBtn(G2.Top(62f), "LOAD LAST SAVE")) _b.LoadSaved();
            if (KBtn(G2.Top(62f), "TITLE SCREEN", UiRole.Danger)) _b.ToTitle();
            DrawReportMenuButtons(G2);   // GameUI.Reports.cs: SAVE GAMES, MEMORIAL
            DrawAchMenuButton(G2);       // GameUI.Achievements.cs (E12)
            G2.Top(2f);
            UiKit.Paragraph(G2.Rest, "The game autosaves every few minutes and when the app goes to the background.", 16);

            var sc = UiKit.Panel(cols[1], "Settings");
            var S = new UiLayout(sc, 12f);
            var sfx = _b.Sfx;
            if (sfx != null)
            {
                var a = UiLayout.Columns(S.Top(62f), 12f, 1f, -220f);
                UiKit.FitLabel(a[0], "SOUND", 20, TextAnchor.MiddleLeft, UiKit.Ink);
                if (KBtn(a[1], sfx.Enabled ? "ON" : "OFF", sfx.Enabled ? UiRole.Primary : UiRole.Neutral, sfx.Enabled)) { sfx.Enabled = !sfx.Enabled; sfx.SaveSettings(); }
                var v = UiLayout.Columns(S.Top(62f), 12f, 1f, -340f);
                UiKit.FitLabel(v[0], "VOLUME", 20, TextAnchor.MiddleLeft, UiKit.Ink);
                int cur = Mathf.Clamp((int)Math.Round(sfx.Volume * 10.0), 0, 10);
                int pick = UiWheel.Horizontal("ui_volume", v[1], VOL_ITEMS, cur, 5, 18);
                if (pick >= 0 && pick != cur) { sfx.Volume = Math.Round(pick / 10.0, 1); sfx.SaveSettings(); }
            }
            if (KBtn(S.Top(62f), "MORE SETTINGS", UiRole.Accent)) OpenSettings();   // graphics, film look, battery, vibration, UI size
            var tc = UiLayout.Columns(S.Top(62f), 12f, 1f, -220f);   // v0.18 territory tint (FxTerritory); MERGE: move into the SETTINGS sheet
            UiKit.FitLabel(tc[0], "TERRITORY COLORS", 20, TextAnchor.MiddleLeft, UiKit.Ink);
            if (KBtn(tc[1], FxTerritory.Enabled ? "ON" : "OFF", FxTerritory.Enabled ? UiRole.Primary : UiRole.Neutral, FxTerritory.Enabled)) FxTerritory.Enabled = !FxTerritory.Enabled;
            UiKit.Divider(S.Top(8f));
            UiKit.Paragraph(S.Rest, "Drag to pan the map, pinch to zoom, twist with two fingers to rotate. Survivors work on their own: set jobs, answer events and control time.", 16);
        }

        // ---------------- popups ----------------
        object _decLast;

        /// <summary>Event decision: titled panel, wrapped text, one large button per choice (label + hint line);
        /// many choices scroll instead of running off the panel.</summary>
        void DrawDecision(float w, float h, JObject ev)
        {
            var choices = ev["choices"] as JArray ?? new JArray();
            if (!ReferenceEquals(ev, _decLast)) { _decLast = ev; UiKit.ScrollTo("ui_decision", 0f); }
            GUI.DrawTexture(new Rect(0, 0, w, h), _sheetDimTex);
            float pw = Mathf.Min(900f, w - 60f), textW = pw - 2f * UiKit.PanelPad - 4f;
            string text = (string)ev["text"] ?? "";
            float th = Mathf.Ceil(ParaH(text, 19, textW)) + 4f;
            const float CH = 66f, CG = 8f;
            float listH = Mathf.Max(0f, choices.Count * (CH + CG) - CG);
            float chrome = UiKit.HeaderH + 4f + 8f + UiKit.PanelPad;
            float ph = Mathf.Min(h - 40f, chrome + th + 14f + listH + 8f);
            var p = new Rect((w - pw) / 2f, (h - ph) / 2f, pw, ph);
            var ci = UiKit.Panel(p, (string)ev["title"] ?? "Event", 24);
            float maxText = Mathf.Max(40f, ci.height - 14f - Mathf.Min(listH, CH * 2f + CG));
            var L = new UiLayout(ci, 14f);
            UiKit.Paragraph(L.Top(Mathf.Min(th, maxText)), text, 19, UiKit.Ink);
            var view = L.Rest;
            int picked = -1;
            UiKit.BeginScroll("ui_decision", view, listH);
            float bw = view.width - (listH > view.height + 0.5f ? 12f : 0f);
            for (int i = 0; i < choices.Count; i++)
            {
                var ch = choices[i] as JObject;
                string label = (string)ch?["label"] ?? ("Option " + (i + 1));
                string hint = (string)ch?["hint"];
                var br = new Rect(0f, i * (CH + CG), bw, CH);
                if (KBtn(br, "", UiRole.Primary)) picked = i;
                var face = UiKit.ButtonFace(br, false);
                if (string.IsNullOrEmpty(hint)) UiKit.FitLabel(face, label, 22, TextAnchor.MiddleCenter, UiKit.Ink, true, 12);
                else
                {
                    var rr = UiLayout.Rows(UiLayout.Inset(face, 10f, 4f, 10f, 3f), 0f, 1.15f, 0.85f);
                    UiKit.FitLabel(rr[0], label, 21, TextAnchor.MiddleCenter, UiKit.Ink, true, 12);
                    UiKit.FitLabel(rr[1], hint, 15, TextAnchor.MiddleCenter, UiKit.InkDim, false, 10, false);
                }
            }
            UiKit.EndScroll();
            if (picked >= 0) _b.Answer(ev, picked);
        }

        void DrawGameOver(float w, float h)
        {
            if (DrawRunSummary(w, h)) return;   // GameUI.Reports.cs (E15)
            float pw = Mathf.Min(760f, w - 60f);
            var p = new Rect((w - pw) / 2f, h / 2f - 150f, pw, 300f);
            GUI.DrawTexture(new Rect(0, 0, w, h), _sheetDimTex);
            var ci = UiKit.Panel(p, _b.Won ? "You held out" : "Your colony has fallen", 26);
            var L = new UiLayout(ci, 12f);
            var bb = UiLayout.Columns(L.Bottom(66f), 14f, 1f, 1f);
            var c = G.C;
            UiKit.Paragraph(L.Rest, string.Format("Day {0}. {1} zombies killed, {2} survivors rescued.",
                G.Day(), c?.Stat("kills") ?? 0, c?.Stat("rescued") ?? 0), 20, UiKit.Ink);
            if (KBtn(bb[0], "NEW GAME", UiRole.Accent)) _b.NewGame(1);
            if (KBtn(bb[1], "TITLE SCREEN", UiRole.Neutral)) _b.ToTitle();
        }

        void DrawToasts(float w, float h)
        {
            if (_b.Toasts.Count == 0) return;
            bool hud = _b.State == Bootstrap.Screen2.Playing && _hf != null && !_b.Picking;
            float y = hud ? _hf.TimeBar.y - 8f : h - 16f;
            // centred in the sheet lane (right of the camera column, left of the right dock), so a toast never covers ACTIONS
            float lo = 10f, hi = w - 10f;
            if (hud) { var sa = SheetArea(w, h, MapView.Instance); lo = sa.x; hi = sa.xMax; }   // GameUI.PhaseA.cs docks
            float tw = Mathf.Min(Mathf.Min(760f, w * 0.56f), hi - lo), tx = lo + (hi - lo - tw) / 2f;
            for (int i = _b.Toasts.Count - 1; i >= 0; i--)
            {
                float th = Mathf.Max(48f, Mathf.Ceil(ParaH(_b.Toasts[i], 17, tw - 40f)) + 22f);
                y -= th + 6f;
                var r = new Rect(tx, y, tw, th);
                UiKit.Panel(r);
                GUI.DrawTexture(new Rect(r.x + 8f, r.y + 9f, 4f, r.height - 18f), _amberLineTex);
                UiKit.Paragraph(UiLayout.Inset(r, 22f, 11f, 16f, 8f), _b.Toasts[i], 17, UiKit.Ink);
            }
        }
    }
}
