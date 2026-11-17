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
        const float VH = 720f;          // virtual height (Godot viewport 1280x720)
        const float TAB_H = 52f;
        const int PAGE = 7;

        static readonly string[] RES_KEYS = { "food", "water", "meds", "ammo", "fuel" };
        static readonly string[] TABS = { "Colony", "Jobs", "Log", "Menu" };

        readonly Bootstrap _b;
        int _tab;
        int _jobPage, _logPage;
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
        public bool ModalOpen => _b.State != Bootstrap.Screen2.Playing || _menuOpen || SideOpen || _b.Pending.Count > 0 || _b.Ended;

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
            Shadowed(new Rect(60, 50, w - 120, 60), "DAY ZERO: OUTBREAK", _title);
            GUI.DrawTexture(new Rect(60, 92, 420, 3), _accentTex);
            GUI.Label(new Rect(60, 98, w - 120, 34), "SURVIVE. BUILD. DEFEND. EXPAND.", _tagSt);
            GUI.Label(new Rect(60, 132, w - 120, 30), "v" + Application.version + " · Unity port", _small);
            float y = 190;
            bool has = _b.HasSave();
            GUI.enabled = has;
            if (Btn(new Rect(60, y, 420, 76), has ? "Continue" : "Continue (no save)", _bigButton)) _b.LoadSaved();
            GUI.enabled = true;
            y += 110;
            GUI.Label(new Rect(60, y, 420, 34), "New game", _label);
            y += 40;
            for (int d = 0; d < Game.DIFF_NAMES.Length; d++)
            {
                if (Btn(new Rect(60 + d * 220, y, 200, 72), Game.DIFF_NAMES[d], _bigButton)) _b.NewGame(d);
            }
            DrawExpandedToggle(w, h);   // GameUI.Expansion.World.cs
            GUI.Label(new Rect(60, h - 90, w - 120, 60),
                "Starting a new game replaces the current save. You pick the building for your base on the map.", _small);
        }

        // ---------------- in game ----------------
        // Layout follows the Godot build: stats top-left, MENU top-centre, kills / rescued and
        // resources top-right, camera buttons down the left, collapsible ACTIONS panel on the right.
        void DrawGame(float w, float h)
        {
            if (G?.S == null || G.C == null) { Center(w, h, "DayZero", "No game running."); return; }
            var map = MapView.Instance;
            if (map != null && map.Ready) DrawMapLabels(map, w, h);
            if (map != null && map.Ready && !_b.Picking && G.C.Started) { DrawConvLabels(map, w, h); DrawRepairMarks(map, w, h); DrawWreckMarks(map, w, h); }
            if (!_b.Picking && G.C.Started) DrawDangerOverlay(map, w, h);   // GameUI.Expansion.World.cs
            if (map != null && !string.IsNullOrEmpty(map.RenderWarning))
                Shadowed(new Rect(12, h - 30, w - 24, 24), map.RenderWarning, _small);
            // v0.11.2a drew this every frame, so "Building the map 100%" never went away
            if (map == null || !map.Ready) Center(w, h, "Building the map", map != null ? string.Format("{0}%", (int)(map.Progress * 100f)) : "");

            if (_b.Picking)
            {
                // new game: choose the building for the base (no camp, no clock yet)
                if (Btn(Blk(new Rect(w / 2 - 80, 8, 160, 50)), "MENU", _darkButton)) _menuOpen = true;
                DrawCameraButtons(map);
                if (map != null && map.Ready && !ModalOpen) DrawStartPick(w, h, map);
                if (_menuOpen) { BeginAnim(ref _modalAnim, 3, null, 0f, 18f); DrawMenuOverlay(w, h); EndAnim(); }
                return;
            }
            // A sheet on top (side menu, menu overlay, decision, game over) gets the touches: IMGUI hands a press to
            // the first button drawn under it, so the HUD underneath must not see mouse events while one is up.
            bool sheet = SideOpen || _menuOpen || _b.Pending.Count > 0 || _b.Ended || G.S.Phase == "lost";
            var evType = Event.current.type;
            bool swallow = sheet && (evType == EventType.MouseDown || evType == EventType.MouseUp || evType == EventType.MouseDrag || evType == EventType.ScrollWheel);
            if (swallow) Event.current.type = EventType.Ignore;
            DrawStats();
            DrawExpWorldHud(w, h, map);   // GameUI.Expansion.World.cs
            if (Btn(Blk(new Rect(w / 2 - 80, 8, 160, 50)), "MENU", _darkButton)) _menuOpen = true;
            DrawTopRight(w);
            DrawCameraButtons(map);
            float by = 104f;
            DrawAttackBanner(w, h, ref by);
            DrawRepairChip(w, ref by);
            DrawResearchNote(w, ref by, map);
            DrawWarHud(w, ref by, map);   // GameUI.Factions.cs
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
            var ban = Blk(new Rect(w / 2 - 330, 66, 660, 78));
            GUI.Box(ban, GUIContent.none, _hudBox);
            var bs = _bannerS;
            Shadowed(new Rect(ban.x, ban.y + 6, ban.width, 40), "Tap any building to make it your base", _bannerT);
            GUI.Label(new Rect(ban.x, ban.y + 46, ban.width, 26), "Start anywhere: drag to look around, pinch to zoom, then tap the building you want", bs);
            if (map.SelectedLot < 0 || !map.SelectionInfo(out string type, out string size, out int floors, out string district)) return;
            string err = map.SelectError;
            float pw = 600, ph = err != "" ? 210 : 180, px = w / 2 - pw / 2, py = h - ph - 14;
            var panel = Blk(new Rect(px, py, pw, ph));
            GUI.Box(panel, GUIContent.none, _box);
            Shadowed(new Rect(px + 20, py + 10, pw - 40, 36), type.ToUpperInvariant(), _pickT);
            GUI.Label(new Rect(px + 20, py + 50, pw - 40, 28), string.Format("Size {0}  ·  {1} floor{2}  ·  {3}", size, floors, floors == 1 ? "" : "s", district), _label);
            float y = py + 82;
            if (err != "")
            {
                GUI.Label(new Rect(px + 20, y, pw - 40, 30), err, _pickErr);
                y += 32;
            }
            else
            {
                GUI.Label(new Rect(px + 20, y, pw - 40, 26), "Your people move in. Fence the block in from DEFENSE, then add a gate.", _small);
                y += 30;
            }
            GUI.enabled = err == "";
            if (Btn(new Rect(px + 20, y + 4, 270, 60), "CONFIRM", _amberButton)) _b.ConfirmStart();
            GUI.enabled = true;
            if (Btn(new Rect(px + pw - 290, y + 4, 270, 60), "CANCEL", _darkButton)) map.ClearSelection();
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

        void DrawStats()
        {
            var c = G.C;
            var alive = G.AlivePeople();
            int beds = 0, used = 0;
            foreach (var b in c.Bases) { int r = G.CampResidents(b.Id); used += r; beds += r + G.CampFree(b); }
            int dead = c.People.Count(p => p.St == "dead");
            int z = G.ZombieCount();
            var panel = Blk(new Rect(8, 8, 330, 190));
            GUI.Box(panel, GUIContent.none, _hudBox);
            float y = 14, x = 20;
            Shadowed(new Rect(x, y, 310, 28), "SURVIVORS: " + Col(OLIVE_INK, alive.Count.ToString()), _hud); y += 27;
            Shadowed(new Rect(x, y, 310, 28), "BEDS: " + Col(used > beds ? RED_INK : OLIVE_INK, used + "/" + beds), _hud); y += 27;
            Shadowed(new Rect(x, y, 310, 28), "ZOMBIES: " + Col(z > 0 ? RED_INK : OLIVE_INK, z.ToString()), _hud); y += 27;
            Shadowed(new Rect(x, y, 310, 28), "DEAD: " + dead, _hud); y += 27;
            string timeText = "TIME: " + G.ClockText() + (G.IsNight() ? "  " + Col(AMBER, "night") : "");
            DrawClockChip(new Rect(13, y + 1, 320, 27), x, timeText);
            Shadowed(new Rect(x, y, 310, 28), timeText, _hud); y += 30;
            GUI.DrawTexture(new Rect(x, y - 1, 306, 1), _lineTex);
            var ri = G.RaidInfo();
            bool alert = ri != null && (ri.On || ri.Warned);
            if (alert) DrawAlertPulse(new Rect(x + 284, y + 1, 22, 20), ri.On);
            GUI.Label(new Rect(x, y, alert ? 280 : 310, 22), PhaseText(), _phaseSt); y += 20;
            GUI.Label(new Rect(x, y, 310, 22), string.Format("{0}  ·  {1} survivors  ·  {2} kills", c.Name, alive.Count, c.Stat("kills")), _colonySt);
        }

        /// <summary>v0.14.0a: the TIME row sits on a soft chip tinted by the hour (warm day, orange dusk / dawn, blue
        /// night) with a sun / moon glyph at its right end.</summary>
        void DrawClockChip(Rect r, float textX, string timeText)
        {
            float hour = (float)(G.S.T % 24.0);
            var oldBg = GUI.backgroundColor;
            GUI.backgroundColor = UiSkin.SkyTint(hour);
            GUI.Box(r, GUIContent.none, _tintBox);
            GUI.backgroundColor = oldBg;
            bool night = hour >= 19.5f || hour < 5.5f;
            _gc.text = timeText;
            if (textX + _hud.CalcSize(_gc).x + 6f + 21f <= r.xMax - 4f)   // only where the text leaves room
                GUI.DrawTexture(new Rect(r.xMax - 26, r.y + 3, 21, 21), night ? Icons.GlyphTex("moon", MOON) : Icons.GlyphTex("sun", SUN));
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
                if (_chipText[i] == null || n != _chipInt[i]) { _chipInt[i] = n; _chipText[i] = CHIP_KEYS[i] + " " + n; }
            }
        }

        void DrawTopRight(float w)
        {
            var c = G.C;
            GUI.DrawTexture(new Rect(w - 360, 4, 356, 60), _scrimTex);
            Shadowed(new Rect(w - 330, 8, 320, 28), Col(AMBER, c.Stat("kills").ToString()) + " : ZOMBIES KILLED", _hudR);
            Shadowed(new Rect(w - 330, 34, 320, 28), Col(OLIVE_INK, c.Stat("rescued").ToString()) + " : RESCUED", _hudR);
            // resource chips, right-aligned: glyph + "name value"; a change flashes the text green / red for a moment
            if (Event.current.type == EventType.Layout || _chipText[0] == null) TickChips();
            float x = w - 8, now = Time.unscaledTime;
            var ink = _chip.normal.textColor;
            for (int i = CHIP_KEYS.Length - 1; i >= 0; i--)
            {
                _gc.text = _chipText[i];
                float cw = Mathf.Max(64f, _chip.CalcSize(_gc).x + 14f + 22f);
                x -= cw;
                var r = Blk(new Rect(x, 66, cw, 32));
                GUI.Box(r, GUIContent.none, _chip);
                GUI.DrawTexture(new Rect(r.x + 6, r.y + 5, 22, 22), Icons.GlyphTex(CHIP_KEYS[i], CHIP_COL[i]), ScaleMode.ScaleToFit, true);
                float f = _chipDir[i] == 0 ? 0f : Mathf.Clamp01(1f - (now - _chipFlashT[i]) / 0.9f);
                if (f > 0f) _chipTxt.normal.textColor = Color.Lerp(ink, _chipDir[i] > 0 ? new Color(0.62f, 0.92f, 0.45f) : new Color(1f, 0.45f, 0.35f), f);
                GUI.Label(new Rect(r.x + 22, r.y, r.width - 22, r.height), _gc, _chipTxt);
                _chipTxt.normal.textColor = ink;
                x -= 4;
            }
        }

        void DrawCameraButtons(MapView map)
        {
            // 76 wide + no word wrap: at 60 px "HOME" wrapped to "HOM/E" on phones (v0.11.2a)
            float x = 10, y = 214, bw = 76;
            var small = _camSmall;
            if (Btn(Blk(new Rect(x, y, bw, 54)), "HOME", small)) map?.Home(); y += 62;
            if (Btn(Blk(new Rect(x, y, bw, 54)), "+", _darkButton)) map?.ZoomIn(); y += 62;
            if (Btn(Blk(new Rect(x, y, bw, 54)), "-", _darkButton)) map?.ZoomOut(); y += 62;
            if (Btn(Blk(new Rect(x, y, bw, 54)), "MAP", small)) map?.Overview();
        }

        void DrawActions(float w, MapView map)
        {
            var panel = SideMenus.ActionsPanel(w);
            if (!_actionsOpen)
            {
                if (Btn(Blk(new Rect(w - 52, 300, 44, 64)), "<", _darkButton)) _actionsOpen = true;
                return;
            }
            if (Btn(Blk(new Rect(panel.x - 48, 300, 44, 64)), ">", _darkButton)) _actionsOpen = false;
            Blk(panel);
            GUI.Box(panel, GUIContent.none, _box);
            GUI.Label(new Rect(panel.x + 12, panel.y + 6, panel.width - 24, 24), "ACTIONS", _hdrAmber);
            GUI.DrawTexture(new Rect(panel.x + 10, panel.y + 27, 90, 3), _accentTex);
            for (int i = 0; i < SideMenus.ACTIONS.Length; i++)
            {
                var (label, style) = SideMenus.ACTIONS[i];
                if (!Btn(SideMenus.ActionButton(w, i), label, MButton(style, SideMenus.ActFont(i)))) continue;
                switch (label)
                {
                    case "BUILD": _panel = Panel.Build; map?.CancelTool(); break;
                    case "BUILD OUTPOST": StartOutpost(map); break;                         // GameUI.Camp.cs
                    case "CAMPS": SideMenus.CampsPage = 0; OpenSide("camps", map); break;   // main.gd _show_bases
                    case "GATHER": _tab = 1; _menuOpen = true; break;
                    case "CREW & JOBS": SideMenus.CrewPage = 0; OpenSide("crew", map); break; // main.gd _show_crew
                    case "DEFENSE": _panel = Panel.Defense; map?.ClearBuilding(); break;
                    case "SQUADS": _side.Camp = -1; OpenSide("squads", map); break;        // main.gd _pick_default_camp: the main camp
                    case "WAR": _side.Sub = ""; OpenSide("war", map); break;                // main.gd _show_war (GameUI.Factions.cs)
                    case "TASK FORCE": _side.Camp = -1; OpenSide("taskforce", map); break;
                    case "COLONY": _tab = 0; _menuOpen = true; break;
                    case "TRADE": OpenSide("trade", map); break;
                    case "ARMORY": OpenSide("armory", map); break;
                    case "GARAGE": OpenSide("garage", map); break;
                    case "RESEARCH": OpenSide("research", map); break;
                    case "LOG": _tab = 2; _menuOpen = true; break;
                }
            }
        }

        void DrawTimeControls(float w, float h)
        {
            float bw = 86, total = bw * 4 + 18, x = w / 2 - total / 2, y = h - 62;
            if (_b.Paused) DrawHalo(new Rect(x, y, bw + 16, 52), new Color(1f, 0.72f, 0.30f), 2.6f);   // paused: PLAY breathes amber
            if (Btn(Blk(new Rect(x, y, bw + 16, 52)), _b.Paused ? "PLAY" : "PAUSE", _darkButton)) _b.Paused = !_b.Paused; x += bw + 22;
            if (Btn(Blk(new Rect(x, y, bw, 52)), "x" + _b.Speed, _darkButton)) _b.Speed = _b.Speed >= 4 ? 1 : _b.Speed * 2; x += bw + 6;
            if (Btn(Blk(new Rect(x, y, bw, 52)), "+1h", _darkButton)) _b.Skip(1); x += bw + 6;
            if (Btn(Blk(new Rect(x, y, bw, 52)), "+6h", _darkButton)) _b.Skip(6);
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

        void DrawMapLabels(MapView map, float w, float h)
        {
            float d = map.Dist;
            int drawn = 0;
            foreach (var lb in map.Labels)
            {
                if (lb.Tier == 2 ? d < 70f : lb.Tier == 0 ? d > 150f : d > 75f) continue;
                if (!map.TryScreen(lb.Pos, out var sp)) continue;
                var v = new Vector2((sp.x - _offX) / _scale, (sp.y - _offY) / _scale);
                if (v.x < -100 || v.x > w + 100 || v.y < 0 || v.y > h) continue;
                var st = lb.Tier == 2 ? _mapDistrict : _mapLabel;
                var r = new Rect(v.x - 160, v.y - 14, 320, 28);
                GUI.Label(new Rect(r.x + 1.5f, r.y + 1.5f, r.width, r.height), lb.Text, lb.Tier == 2 ? _mapDistrictSh : _mapLabelSh);
                GUI.Label(r, lb.Text, st);
                if (++drawn >= 60) break;
            }
            // camps
            foreach (var b in G.C.Bases)
            {
                var p = new Vector3(b.Pos.x + 0.5f, 3.6f, -(b.Pos.y + 0.5f));
                if (!map.TryScreen(p, out var sp)) continue;
                var v = new Vector2((sp.x - _offX) / _scale, (sp.y - _offY) / _scale);
                string t = G.BaseLabel(b);
                var r = new Rect(v.x - 180, v.y - 16, 360, 30);
                GUI.Label(new Rect(r.x + 1.5f, r.y + 1.5f, r.width, r.height), t, _mapCampSh);
                GUI.Label(r, t, _mapCamp);
            }
        }

        void DrawMenuOverlay(float w, float h)
        {
            GUI.DrawTexture(new Rect(0, 0, w, h), _dimTex);
            var panel = new Rect(24, 24, w - 48, h - 48);
            GUI.Box(panel, GUIContent.none, _box);
            GUI.Box(panel, GUIContent.none, _box);   // double up for a darker sheet
            GUI.DrawTexture(new Rect(panel.x + 12, panel.y + TAB_H + 6, panel.width - 24, 1), _lineTex);
            for (int i = 0; i < TABS.Length; i++)
            {
                var r = new Rect(panel.x + 12 + i * 170, panel.y + 10, 160, TAB_H - 8);
                if (_tab == i) GUI.DrawTexture(new Rect(r.x - 6, r.yMax + 1, r.width + 12, 6), _accentTex);   // selected tab: glowing underline
                if (Btn(r, TABS[i].ToUpperInvariant(), _tab == i ? _amberButton : _button)) _tab = i;
            }
            if (Btn(new Rect(panel.xMax - 132, panel.y + 10, 120, TAB_H - 8), "CLOSE", _darkButton)) { _menuOpen = false; return; }
            var inner = new Rect(panel.x + 18, panel.y + TAB_H + 14, panel.width - 36, panel.height - TAB_H - 26);
            switch (_tab)
            {
                case 0: DrawColony(inner); break;
                case 1: DrawJobs(inner); break;
                case 2: DrawLog(inner); break;
                case 3: DrawMenu(inner); break;
            }
        }

        string PhaseText()
        {
            string ph = G.PhaseOf(Math.Max(0, G.CurCol));
            string t;
            switch (ph)
            {
                case "spread": t = string.Format("Infection {0}% of the city", (int)(G.SpreadPct() * 100.0)); break;
                case "fall": t = string.Format("THE FALL · hold {0:0.0} more days", G.HoldLeft() / 24.0); break;
                case "lost": t = "Overrun"; break;
                default: t = ph; break;
            }
            var ri = G.RaidInfo();
            if (ri != null && (ph == "spread" || ph == "fall"))
            {
                if (ri.On) t = string.Format("NIGHT ATTACK · {0} closing in", ri.Spawned);
                else if (ri.Warned) t = string.Format("ATTACK TONIGHT · about {0}", ri.N);
            }
            return t;
        }

        void DrawColony(Rect r)
        {
            var c = G.C;
            float y = r.y;
            GUI.Label(new Rect(r.x, y, r.width, 36), c.Name, _title); y += 44;
            var alive = G.AlivePeople();
            GUI.Label(new Rect(r.x, y, r.width, 30), string.Format("{0} survivors · {1} camp(s) · {2} zombies on the map · kills {3} · rescued {4}",
                alive.Count, c.Bases.Count, G.ZombieCount(), c.Stat("kills"), c.Stat("rescued")), _label);
            y += 36;
            var ri = G.RaidInfo();
            if (ri != null && (ri.On || ri.Warned))
            {
                GUI.Label(new Rect(r.x, y, r.width, 28), ri.On ? "Your camp is under attack. Guards are on the line." : "Scouts expect an attack tonight. Put Soldiers and Cops on guard.", _warn);
                y += 32;
            }
            float col = Mathf.Min(560f, r.width / 2 - 10);
            float yl = y, yr = y;
            GUI.Label(new Rect(r.x, yl, col, 28), "Camps", _label); yl += 30;
            foreach (var b in c.Bases.Take(6))
            {
                GUI.Label(new Rect(r.x + 10, yl, col - 10, 26), "• " + G.BaseLabel(b) + (b.Done < 1.0 ? string.Format(" (building {0}%)", (int)(b.Done * 100)) : ""), _small);
                yl += 26;
            }
            yl += 8;
            GUI.Label(new Rect(r.x, yl, col, 28), "Stockpile", _label); yl += 30;
            string res = string.Join("   ", c.Res.Where(kv => kv.Value >= 1.0).Select(kv => kv.Key + " " + (int)kv.Value));
            GUI.Label(new Rect(r.x + 10, yl, col - 10, r.yMax - yl), res, _small);

            float xr = r.x + col + 20;
            GUI.Label(new Rect(xr, yr, col, 28), "Jobs", _label); yr += 30;
            foreach (var j in Data.JOBS)
            {
                int n = G.CountJob(j);
                GUI.Label(new Rect(xr + 10, yr, col - 10, 26), string.Format("{0,-10} {1}", j, n), _small);
                yr += 25;
            }
            int kids = alive.Count(p => p.Job == "Child");
            if (kids > 0) { GUI.Label(new Rect(xr + 10, yr, col - 10, 26), "Children " + kids, _small); yr += 25; }
            yr += 8;
            if (Btn(new Rect(xr, yr, 260, 52), "Manage jobs")) _tab = 1;
        }

        void DrawJobs(Rect r)
        {
            var people = G.AlivePeople();
            int pages = Math.Max(1, (people.Count + PAGE - 1) / PAGE);
            _jobPage = Mathf.Clamp(_jobPage, 0, pages - 1);
            GUI.Label(new Rect(r.x, r.y, r.width - 360, 32), string.Format("Survivors ({0}) · tap < > to change a job", people.Count), _label);
            if (Btn(new Rect(r.xMax - 340, r.y - 4, 110, 44), "< Prev")) _jobPage = Math.Max(0, _jobPage - 1);
            GUI.Label(new Rect(r.xMax - 222, r.y + 4, 100, 32), string.Format("{0}/{1}", _jobPage + 1, pages), _label);
            if (Btn(new Rect(r.xMax - 110, r.y - 4, 110, 44), "Next >")) _jobPage = Math.Min(pages - 1, _jobPage + 1);

            float y = r.y + 50, rowH = (r.height - 50) / PAGE;
            foreach (var p in people.Skip(_jobPage * PAGE).Take(PAGE))
            {
                string info = string.Format("{0}  ·  {1}  ·  hp {2}{3}", p.Name, StateText(p), (int)p.Hp, p.Stage != null && p.Stage != "adult" ? "  ·  " + p.Stage : "");
                GUI.Label(new Rect(r.x, y + 4, r.width - 430, rowH - 4), info, _label);
                float bx = r.xMax - 420;
                if (Btn(new Rect(bx, y + 2, 64, rowH - 8), "<")) CycleJob(p, -1);
                GUI.Label(new Rect(bx + 70, y + 8, 200, rowH - 8), p.Job, _chip);
                if (Btn(new Rect(bx + 276, y + 2, 64, rowH - 8), ">")) CycleJob(p, 1);
                y += rowH;
            }
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

        void CycleJob(Person p, int dir)
        {
            // Step through Data.JOBS in the chosen direction; a job the survivor can't take
            // (JobBlock: a child, no free gear...) is skipped and its reason shown.
            var jobs = Data.JOBS;
            int n = jobs.Length;
            int i = Math.Max(0, Array.IndexOf(jobs, p.Job));
            string firstErr = null;
            for (int k = 1; k < n; k++)
            {
                string next = jobs[(((i + dir * k) % n) + n) % n];
                string err = _b.Act("set_job", p.Id, next);
                if (err == "")
                {
                    string desc;
                    string msg = p.Name + " is now a " + next + (Data.JOB_DESC.TryGetValue(next, out desc) ? ". " + desc : ".");
                    if (firstErr != null) msg += "\n(skipped: " + firstErr + ")";
                    _b.Toast(msg);
                    return;
                }
                if (firstErr == null) firstErr = err;
                if (err.StartsWith("That survivor") || err.StartsWith("Error") || err.StartsWith("Your colony") || err.StartsWith("No colony")) break;
            }
            _b.Toast(firstErr ?? "No other job available.");
        }

        void DrawLog(Rect r)
        {
            var log = G.S.Log.Where(e => e.Col < 0 || e.Col == G.CurCol).Reverse().ToList();
            const int per = 12;
            int pages = Math.Max(1, (log.Count + per - 1) / per);
            _logPage = Mathf.Clamp(_logPage, 0, pages - 1);
            GUI.Label(new Rect(r.x, r.y, 400, 32), "Log (newest first)", _label);
            if (Btn(new Rect(r.xMax - 340, r.y - 4, 110, 44), "< Newer")) _logPage = Math.Max(0, _logPage - 1);
            GUI.Label(new Rect(r.xMax - 222, r.y + 4, 100, 32), string.Format("{0}/{1}", _logPage + 1, pages), _label);
            if (Btn(new Rect(r.xMax - 110, r.y - 4, 110, 44), "Older >")) _logPage = Math.Min(pages - 1, _logPage + 1);
            float y = r.y + 48, rowH = (r.height - 48) / per;
            foreach (var e in log.Skip(_logPage * per).Take(per))
            {
                string when = string.Format("d{0} {1:00}:00", (int)(e.T / 24.0) + 1, (int)e.T % 24);
                var st = e.Kind == "death" || e.Kind == "siren" || e.Kind == "major" ? _warn : _small;
                GUI.Label(new Rect(r.x, y, r.width, rowH), when + "  " + e.Text, st);
                y += rowH;
            }
        }

        void DrawMenu(Rect r)
        {
            float y = r.y;
            if (Btn(new Rect(r.x, y, 300, 64), "Save now", _bigButton)) _b.SaveNow();
            if (Btn(new Rect(r.x + 320, y, 300, 64), "Load last save", _bigButton)) _b.LoadSaved();
            if (Btn(new Rect(r.x + 640, y, 300, 64), "Title screen", _bigButton)) _b.ToTitle();
            y += 90;
            var sfx = _b.Sfx;
            if (sfx != null)
            {
                GUI.Label(new Rect(r.x, y, 200, 36), "Sound", _label);
                if (Btn(new Rect(r.x + 200, y - 4, 160, 48), sfx.Enabled ? "On" : "Off")) { sfx.Enabled = !sfx.Enabled; sfx.SaveSettings(); }
                if (Btn(new Rect(r.x + 380, y - 4, 70, 48), "-")) { sfx.Volume = Math.Max(0.0, Math.Round(sfx.Volume - 0.1, 1)); sfx.SaveSettings(); }
                GUI.Label(new Rect(r.x + 460, y + 4, 120, 36), (int)(sfx.Volume * 100) + "%", _label);
                if (Btn(new Rect(r.x + 560, y - 4, 70, 48), "+")) { sfx.Volume = Math.Min(1.0, Math.Round(sfx.Volume + 0.1, 1)); sfx.SaveSettings(); }
                y += 70;
            }
            GUI.Label(new Rect(r.x, y, 200, 36), "Film look", _label);
            if (Btn(new Rect(r.x + 200, y - 4, 160, 48), FxView.Film ? "On" : "Off")) FxView.SetFilmLook(!FxView.Film);
            y += 70;
            GUI.Label(new Rect(r.x, y, r.width, 120),
                "The game autosaves every few minutes and when the app goes to the background. " +
                "Drag to pan the map, pinch to zoom, twist with two fingers to rotate. Survivors work on their own: set jobs, answer events and control time.", _small);
        }

        // ---------------- popups ----------------
        void DrawDecision(float w, float h, JObject ev)
        {
            var choices = ev["choices"] as JArray ?? new JArray();
            float pw = Mathf.Min(900f, w - 60), ph = Mathf.Min(h - 60, 230f + choices.Count * 84f);
            var p = new Rect((w - pw) / 2, (h - ph) / 2, pw, ph);
            GUI.DrawTexture(new Rect(0, 0, w, h), _sheetDimTex);
            GUI.Box(p, GUIContent.none, _hudBox);
            Shadowed(new Rect(p.x + 24, p.y + 16, pw - 48, 44), (string)ev["title"] ?? "Event", _title);
            GUI.DrawTexture(new Rect(p.x + 24, p.y + 60, Mathf.Min(320f, pw - 48), 3), _accentTex);
            GUI.Label(new Rect(p.x + 24, p.y + 66, pw - 48, 140), (string)ev["text"] ?? "", _label);
            float y = p.y + 210;
            for (int i = 0; i < choices.Count; i++)
            {
                var ch = choices[i] as JObject;
                string label = (string)ch?["label"] ?? ("Option " + (i + 1));
                string hint = (string)ch?["hint"];
                if (Btn(new Rect(p.x + 24, y, pw - 48, 76), string.IsNullOrEmpty(hint) ? label : label + "\n" + hint)) { _b.Answer(ev, i); return; }
                y += 84;
            }
        }

        void DrawGameOver(float w, float h)
        {
            float pw = Mathf.Min(760f, w - 60);
            var p = new Rect((w - pw) / 2, h / 2 - 170, pw, 340);
            GUI.DrawTexture(new Rect(0, 0, w, h), _sheetDimTex);
            GUI.Box(p, GUIContent.none, _hudBox);
            Shadowed(new Rect(p.x + 24, p.y + 20, pw - 48, 44), _b.Won ? "You held out" : "Your colony has fallen", _title);
            GUI.DrawTexture(new Rect(p.x + 24, p.y + 66, Mathf.Min(320f, pw - 48), 3), _accentTex);
            var c = G.C;
            GUI.Label(new Rect(p.x + 24, p.y + 80, pw - 48, 100), string.Format("Day {0}. {1} zombies killed, {2} survivors rescued.",
                G.Day(), c?.Stat("kills") ?? 0, c?.Stat("rescued") ?? 0), _label);
            if (Btn(new Rect(p.x + 24, p.y + 240, 300, 70), "New game", _bigButton)) _b.NewGame(1);
            if (Btn(new Rect(p.x + pw - 324, p.y + 240, 300, 70), "Title screen", _bigButton)) _b.ToTitle();
        }

        void DrawToasts(float w, float h)
        {
            if (_b.Toasts.Count == 0) return;
            float y = h - 16;
            for (int i = _b.Toasts.Count - 1; i >= 0; i--)
            {
                _gc.text = _b.Toasts[i];
                float th = Mathf.Max(40f, _small.CalcHeight(_gc, w * 0.6f) + 12f);
                y -= th + 6;
                var r = new Rect(w * 0.2f, y, w * 0.6f, th);
                GUI.Box(r, GUIContent.none, _box);
                GUI.DrawTexture(new Rect(r.x + 4, r.y + 8, 3, r.height - 16), _amberLineTex);
                GUI.Label(new Rect(r.x + 10, r.y + 6, r.width - 20, r.height - 8), _b.Toasts[i], _small);
            }
        }
    }
}
