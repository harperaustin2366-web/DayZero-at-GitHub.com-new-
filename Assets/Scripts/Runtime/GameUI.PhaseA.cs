// v0.12.0a "Defend the base" HUD (IMGUI, built in code): floating labels over converted buildings, "needs repair"
// markers and the red siren / attack banner with its countdown. v0.17: the ACTIONS dock, the building sheet, the
// BUILDINGS / DEFENSE panels, the placement bar and the wall / fence / tower sheets are laid out once in
// InWorldPanels.cs (UI kit, shared with the offline preview); this file feeds them the game and the map.
using System;
using System.Collections.Generic;
using System.Linq;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed partial class GameUI
    {
        enum Panel { None, Build, Defense }
        Panel _panel = Panel.None;
        Texture2D _redTex, _amberTex, _barBgTex, _barTex, _pillTex;
        GUIStyle _pill, _pillSmall;

        void EnsurePhaseAStyles()
        {
            if (_pill != null) return;
            _redTex = Solid(new Color(0.55f, 0.10f, 0.08f, 0.90f));
            _amberTex = Solid(new Color(0.45f, 0.30f, 0.06f, 0.92f));
            _barBgTex = Solid(new Color(0f, 0f, 0f, 0.6f));
            _barTex = Solid(new Color(0.95f, 0.70f, 0.28f, 1f));
            _pillTex = Solid(new Color(0.07f, 0.08f, 0.06f, 0.82f));
            _pill = new GUIStyle(_label) { fontSize = 17, fontStyle = FontStyle.Bold, wordWrap = false, alignment = TextAnchor.MiddleLeft };
            _pill.normal.textColor = INK;
            _pillSmall = new GUIStyle(_pill) { fontSize = 13 };
            _pillSmall.normal.textColor = AMBER;
        }

        void Icon(Rect r, string id, Color badge) => GUI.DrawTexture(r, Icons.Tex(id, badge), ScaleMode.ScaleToFit, true);

        Vector2 ToVirtual(Vector2 screenTopLeft) => new Vector2((screenTopLeft.x - _offX) / _scale, (screenTopLeft.y - _offY) / _scale);

        // ---------------- floating labels over converted buildings ----------------
        /// <summary>Label pill size: icon + name, wider with a progress line while the building is set up.</summary>
        public static Vector2 LabelSize(float textW, bool pending) => new Vector2(Mathf.Max(36f + textW + 14f, pending ? 196f : 0f), pending ? 46f : 34f);

        void DrawConvLabels(MapView map, float w, float h)
        {
            EnsurePhaseAStyles();
            if (map.Dist > 110f) return;
            int me = G.CurCol;
            var items = new List<(int l, Conversion cv, ConversionDef cd, Vector2 anchor, string name, float tw)>();
            var want = new List<Rect>();
            foreach (var kv in G.S.Conv)
            {
                if (kv.Value.Col != me) continue;
                if (!map.LabelAnchor(kv.Key, out var wp) || !map.TryScreen(wp, out var sp)) continue;
                var v = ToVirtual(sp);
                if (v.x < -60 || v.x > w + 60 || v.y < 40 || v.y > h - 40) continue;
                if (!Data.CONVERSIONS.TryGetValue(kv.Value.Type, out var cd)) continue;
                string name = cd.Name.ToUpperInvariant();
                float tw = _pill.CalcSize(new GUIContent(name)).x;
                var sz = LabelSize(tw, G.ConvPending(kv.Key));
                items.Add((kv.Key, kv.Value, cd, v, name, tw));
                want.Add(new Rect(v.x - sz.x / 2, v.y - sz.y - 8, sz.x, sz.y));
                if (items.Count >= 24) break;
            }
            var rects = HudLayout.Declutter(want, _blocks);
            for (int i = 0; i < items.Count; i++)
            {
                var r = rects[i];
                if (r.width <= 0f) continue;
                var it = items[i];
                bool pend = G.ConvPending(it.l);
                GUI.DrawTexture(r, _pillTex);
                GUI.DrawTexture(new Rect(r.x, r.yMax - 3, r.width, 3), Solid2(it.cd.Color));
                Icon(new Rect(r.x + 3, r.y + 3, 28, 28), it.cv.Type, it.cd.Color);
                Shadowed(new Rect(r.x + 36, r.y + 2, it.tw + 8, 30), it.name, _pill);
                if (pend)
                {
                    float pr = (float)G.ConvProgress(it.l);
                    GUI.Label(new Rect(r.x + 36, r.y + 27, 84, 16), "SETTING UP", _pillSmall);
                    var bar = new Rect(r.x + 124, r.y + 32, r.width - 132, 6);
                    GUI.DrawTexture(bar, _barBgTex);
                    GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * pr, bar.height), _barTex);
                }
                // leader line down to the roof
                float lx = Mathf.Clamp(it.anchor.x, r.x + 4, r.xMax - 4);
                GUI.DrawTexture(new Rect(lx - 1.5f, r.yMax, 3, Mathf.Max(4f, it.anchor.y - r.yMax)), _pillTex);
            }
        }

        readonly Dictionary<Color, Texture2D> _solids = new Dictionary<Color, Texture2D>();
        Texture2D Solid2(Color c) { if (!_solids.TryGetValue(c, out var t) || t == null) { t = Solid(c); _solids[c] = t; } return t; }

        void DrawRepairMarks(MapView map, float w, float h)
        {
            if (map.Dist > 90f) return;
            float pulse = 0.75f + 0.25f * Mathf.Sin(Time.unscaledTime * 5f);
            foreach (var wp in MapGen.RepairMarks(G))
            {
                if (!map.TryScreen(wp, out var sp)) continue;
                var v = ToVirtual(sp);
                if (v.x < 0 || v.x > w || v.y < 40 || v.y > h) continue;
                float s = 34f * pulse;
                Icon(new Rect(v.x - s / 2, v.y - s, s, s), "repair", new Color(0.85f, 0.28f, 0.18f));
            }
        }

        // ---------------- docks (v0.17: InWorldPanels.cs) ----------------
        /// <summary>What occupies the right edge this frame: the ACTIONS / BUILDINGS / DEFENSE dock, the collapsed
        /// ACTIONS tab, or nothing (a map tool is active). Mirrors DrawGame's DrawSidePanel / DrawActions choice.</summary>
        Docks.Right RightNow(MapView map)
        {
            if (map != null && map.ActiveTool != MapView.Tool.None) return Docks.Right.None;
            if (map != null && _panel != Panel.None) return Docks.Right.Dock;
            return _actionsOpen ? Docks.Right.Dock : Docks.Right.Tab;
        }

        Rect SheetArea(float w, float h, MapView map) => Docks.SheetArea(w, h, RightNow(map));

        KitPainter _kp;
        readonly InWorld.LootPick _lootPick = new InWorld.LootPick();

        PanelHost Host(float w, float h)
        {
            if (_kp == null) _kp = new KitPainter(this);
            return new PanelHost
            {
                G = G, P = _kp, W = w, H = h, Act = (a, args) => _b.Act(a, args), Toast = _b.Toast, Click = _b.PlayClick,
                Dirty = () => MapView.Instance?.DirtyDynamic(),
            };
        }

        /// <summary>IKitPaint on UiKit / UiWheel (IMGUI).</summary>
        sealed class KitPainter : IKitPaint
        {
            readonly GameUI _u;
            public KitPainter(GameUI u) { _u = u; }
            static TextAnchor A(MenuAlign al) => al == MenuAlign.Center ? TextAnchor.MiddleCenter : al == MenuAlign.Right ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft;
            public Rect Block(Rect r) => _u.Blk(r);
            public void Plate(Rect r) => UiKit.Panel(r);
            public void Header(Rect r, string text, int maxSize) => UiKit.Header(r, text, maxSize);
            public bool Button(Rect r, string text, UiRole role, bool selected = false, int maxSize = 20) => UiKit.Button(r, text, role, selected, maxSize);
            public void Fit(Rect r, string text, int maxSize, MenuAlign al, Color col, bool bold = true, int minSize = 11) => UiKit.FitLabel(r, text, maxSize, A(al), col, bold, minSize);
            public float Paragraph(Rect r, string text, int size, Color col)
            {
                GUI.BeginGroup(r);   // clip: a long text never spills under the next control
                float hh = UiKit.Paragraph(new Rect(0f, 0f, r.width, r.height), text, size, col);
                GUI.EndGroup();
                return hh;
            }
            public float ParaH(string text, int size, float w) => string.IsNullOrEmpty(text) ? 0f : Mathf.Ceil(_u.MMeasure(text, size, false, w));
            public void Bar(Rect r, float frac, UiRole role, string text) => UiKit.Bar(r, frac, role, text);
            public void Chip(Rect r, string text, UiRole role) => UiKit.Chip(r, text, role);
            public int WheelH(string id, Rect r, IList<string> items, int selected, int visible = 3, int maxSize = 20) => UiWheel.Horizontal(id, r, items, selected, visible, maxSize);
            public void Icon(Rect r, string id, Color badge) { if (Event.current.type == EventType.Repaint) _u.Icon(r, id, badge); }
            public void Fill(Rect r, Color c) { if (Event.current.type == EventType.Repaint) GUI.DrawTexture(r, _u.Solid2(c)); }
            public void Divider(Rect r) => UiKit.Divider(r);
            public void Label(Rect r, string text, int size, bool bold, MenuAlign al, bool wrap, Color c)
            {
                var st = _u.MLabel(size, bold, al, wrap);
                st.normal.textColor = c;
                GUI.Label(r, text, st);
            }
            public Rect BeginScroll(string id, Rect view, float contentH) => UiKit.BeginScroll(id, view, contentH);
            public void EndScroll() => UiKit.EndScroll();
        }

        // ---------------- siren / attack banner ----------------
        Rect TopLane(float w) => Docks.TopLane(w, VH, RightNow(MapView.Instance));

        void DrawAttackBanner(float w, float h, ref float y)
        {
            EnsurePhaseAStyles();
            var a = G.AttackStatus();
            if (a == null) return;
            var st = a.Value;
            bool siren = st.major || (Game.PhaseARules && G.HasSiren());
            if (st.phase == "warn" && !siren && st.hoursLeft > 2.0) return;   // a few stragglers: the phase line says it
            var r = Blk(Docks.Banner(TopLane(w), 620f, y, 70f));   // v0.17: centred between the stats panel and the right dock
            float pulse = 0.78f + 0.22f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * (st.phase == "attack" ? 4f : 2.2f)));
            var old = GUI.color; GUI.color = new Color(1f, 1f, 1f, pulse);
            GUI.DrawTexture(r, st.phase == "attack" || siren ? _redTex : _amberTex);
            GUI.color = old;
            Icon(new Rect(r.x + 8, r.y + 7, 56, 56), st.phase == "attack" ? "warn" : "siren", new Color(0.82f, 0.22f, 0.16f));
            string l1, l2;
            if (st.phase == "warn")
            {
                int mins = Mathf.Max(0, (int)Math.Round(st.hoursLeft * 60.0));
                l1 = string.Format("{0} - ~{1} ZOMBIES FROM THE {2}", siren ? "SIREN" : "MOVEMENT", st.n, st.dir.ToUpperInvariant());
                l2 = string.Format("At the walls in {0}h {1:00}m - guards to the line, patch the walls", mins / 60, mins % 60);
            }
            else
            {
                l1 = string.Format("UNDER ATTACK - {0} ZOMBIES", st.n);
                l2 = st.breaches > 0 ? string.Format("{0} BREACH{1} IN THE WALL - they're getting in!", st.breaches, st.breaches == 1 ? "" : "ES") : (G.RaidAtWallNow > 0 ? G.RaidAtWallNow + " clawing at the walls - guards firing" : "Closing in on the walls");
            }
            UiKit.FitLabel(new Rect(r.x + 74, r.y + 6, r.width - 84, 34), l1, 24, TextAnchor.MiddleLeft, new Color(1f, 0.93f, 0.85f), true, 13);
            UiKit.FitLabel(new Rect(r.x + 74, r.y + 40, r.width - 84, 24), l2, 16, TextAnchor.MiddleLeft, new Color(1f, 0.85f, 0.75f), false, 11);
            y += 76;
        }

        void DrawRepairChip(float w, ref float y)
        {
            if (!Game.PhaseARules) return;   // original rules: repairs are automatic, there is no REPAIR NOW / auto-repair
            int n = G.DamagedWallCount();
            if (n == 0) return;
            int br = G.BreachCount();
            double cost = 0; foreach (var it in G.RepairQueue()) cost += G.RepairCost(it) * 1.5;
            var r = Blk(Docks.Banner(TopLane(w), 560f, y, 56f));
            UiKit.Panel(r);
            Icon(new Rect(r.x + 8, r.y + 10, 36, 36), "repair", new Color(0.88f, 0.62f, 0.18f));
            string t = br > 0 ? string.Format("{0} BREACH{1} - {2} TO FIX", br, br == 1 ? "" : "ES", n) : string.Format("{0} WALL SECTION{1} DAMAGED", n, n == 1 ? "" : "S");
            float bw = Mathf.Min(190f, r.width * 0.4f);
            UiKit.FitLabel(new Rect(r.x + 52, r.y + 5, r.width - bw - 64, 24), t, 17, TextAnchor.MiddleLeft, br > 0 ? RED_INK : AMBER, true, 11);
            UiKit.FitLabel(new Rect(r.x + 52, r.y + 29, r.width - bw - 64, 20), G.C.Auto.Repair ? (G.MatsTotal() >= 2 ? "Survivors are patching them" : "Out of materials") : "Auto-repair is off", 14, TextAnchor.MiddleLeft, UiKit.InkDim, false, 10);
            if (UiKit.Button(new Rect(r.xMax - bw - 6, r.y + 5, bw, 46), string.Format("REPAIR NOW ({0:0})", Math.Ceiling(cost)), UiRole.Accent, false, 17))
            {
                var rr = _b.Act("repair_all");
                _b.Toast(rr == "" ? "Crews patched the walls." : rr);
                MapView.Instance?.DirtyDynamic();
            }
            y += 62;
        }

        // ---------------- ACTIONS dock (v0.17; GameUI.cs DrawGame calls this instead of DrawActions) ----------------
        void DrawActionsDock(float w, float h, MapView map)
        {
            if (_kp == null) _kp = new KitPainter(this);
            string label = InWorld.ActionsDock(_kp, w, h, _actionsOpen, out bool toggle);
            if (toggle) _actionsOpen = !_actionsOpen;
            switch (label)
            {
                case null: break;
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

        // ---------------- right-side panels ----------------
        bool DrawSidePanel(float w, MapView map)
        {
            if (map == null) return false;
            if (map.ActiveTool != MapView.Tool.None) return true;   // placement bar is drawn at the bottom instead
            if (_panel == Panel.Build) { DrawBuildPanel(w, map); return true; }
            if (_panel == Panel.Defense) { DrawDefensePanel(w, map); return true; }
            return false;
        }

        void DrawBuildPanel(float w, MapView map)
        {
            InWorld.BuildPanel(Host(w, VH), () => _panel = Panel.None,
                l => { map.SelectBuilding(l); map.FlyTo(Data.Instance.LotCenter(l)); _panel = Panel.None; });
        }

        void DrawDefensePanel(float w, MapView map)
        {
            var host = Host(w, VH);
            if (!Game.PhaseARules)
            {
                // DEFENSE under the original v0.10.8a rules: main.gd FENCES (corner-post tool + SUGGEST), gates, TOWERS,
                // TRAPS with the original costs and unlocks. No repair buttons or siren: survivors repair on their own.
                int ft = _fenceTier;
                InWorld.FencePanel(host, () => _panel = Panel.None, new[]
                {
                    new InWorld.DefTool { Label = "FENCE", Sub = "corner posts", Icon = "wall", Go = () => Start(map, MapView.Tool.Fence, ft) },   // v0.14.0a corner-post fence tool
                    new InWorld.DefTool { Label = "LOOP", Sub = "suggested", Icon = "wall", Go = () => Start(map, MapView.Tool.Loop, ft) },
                    new InWorld.DefTool { Label = "GATE", Icon = "gate", Go = () => Start(map, MapView.Tool.Gate, 0) },
                    new InWorld.DefTool { Label = "TOWER", Icon = "tower", Go = () => Start(map, MapView.Tool.Tower, _towerTier) },
                    new InWorld.DefTool { Label = "TRAP", Icon = "trap", Go = () => Start(map, MapView.Tool.Trap, 0, TrapIndex("spikes")) },
                });
                return;
            }
            InWorld.DefensePanel(host, () => _panel = Panel.None, new[]
            {
                new InWorld.DefTool { Label = "PALISADE", Sub = "wood wall", Icon = "wall", Go = () => Start(map, MapView.Tool.Wall, 0) },
                new InWorld.DefTool { Label = "SCRAP", Sub = "metal wall", Icon = "wall", Go = () => Start(map, MapView.Tool.Wall, 2) },
                new InWorld.DefTool { Label = "CONCRETE", Sub = "wall", Icon = "wall", Go = () => Start(map, MapView.Tool.Wall, 3) },
                new InWorld.DefTool { Label = "GATE", Icon = "gate", Go = () => Start(map, MapView.Tool.Gate, 0) },
                new InWorld.DefTool { Label = "TOWER", Sub = "watchtower", Icon = "tower", Go = () => Start(map, MapView.Tool.Tower, 0) },
                new InWorld.DefTool { Label = "UPGRADE", Sub = "a wall", Icon = "upgrade", Go = () => Start(map, MapView.Tool.Upgrade, 0) },
                new InWorld.DefTool { Label = "SPIKES", Sub = "spike pit", Icon = "trap", Go = () => Start(map, MapView.Tool.Trap, 0, TrapIndex("spikes")) },
                new InWorld.DefTool { Label = "WIRE", Sub = "barbed", Icon = "trap", Go = () => Start(map, MapView.Tool.Trap, 0, TrapIndex("barbed")) },
            });
        }

        // main.gd world.fence_tier / tower_tier: the last material picked on the placement bar (wood palisade / watchtower at first)
        int _fenceTier, _towerTier;

        static int TrapIndex(string id) { for (int i = 0; i < Data.TRAPS.Length; i++) if (Data.TRAPS[i].Id == id) return i; return 0; }

        void Start(MapView map, MapView.Tool t, int tier, int trap = 0)
        {
            map.StartTool(t, tier, trap);
            _b.Toast(t == MapView.Tool.Fence ? "Tap a glowing corner post, then the next one: the fence follows the curb. Close the loop and add a GATE." : t == MapView.Tool.Loop ? "The suggested fence loop around your camp. Pick the material, CONFIRM to build." : t == MapView.Tool.Wall ? (Game.PhaseARules ? "Tap the map to place the wall. ROTATE and +/- to shape it, CONFIRM to build." : "Tap the map to place the fence line. ROTATE and +/- to shape it, CONFIRM to build.") : t == MapView.Tool.Upgrade ? "Tap one of your walls to upgrade it." : t == MapView.Tool.Gate ? "Tap a wall to put a gate in it." : "Tap the map to place it.");
        }

        // ---------------- placement bar ----------------
        static ToolBarIn.Kind KindOf(MapView.Tool t)
        {
            switch (t)
            {
                case MapView.Tool.Fence: return ToolBarIn.Kind.Fence;
                case MapView.Tool.Loop: return ToolBarIn.Kind.Loop;
                case MapView.Tool.Wall: return ToolBarIn.Kind.Wall;
                case MapView.Tool.Gate: return ToolBarIn.Kind.Gate;
                case MapView.Tool.Tower: return ToolBarIn.Kind.Tower;
                case MapView.Tool.Trap: return ToolBarIn.Kind.Trap;
                default: return ToolBarIn.Kind.Upgrade;
            }
        }

        void DrawPlacementBar(float w, float h, MapView map)
        {
            var tb = new ToolBarIn
            {
                Tool = KindOf(map.ActiveTool), Info = map.ToolInfo, Cost = map.ToolCost, Err = map.ToolErr, Hint = map.FenceHint,
                PostsVisible = map.PostsVisible, CanUndo = map.CanUndoFence, CanClear = map.FenceChain.Count > 0 || map.FenceStart >= 0, Ok = map.ToolOk,
                WallTier = map.WallTier, TowerTier = map.TowerTier, TrapKind = map.TrapKind,
                Rotate = map.Rotate, Shorter = map.Shorter, Longer = map.Longer, Undo = map.FenceUndo, Clear = map.FenceClear, Cancel = map.CancelTool,
                SetFenceTier = t => { _fenceTier = t; map.SetWallTier(t); },
                SetWallTier = t => map.SetWallTier(t),
                SetTowerTier = t => { _towerTier = t; map.SetTowerTier(t); },
                SetTrapKind = k => map.SetTrapKind(k),
                Confirm = map.ConfirmTool,
            };
            InWorld.PlacementBar(Host(w, h), SheetArea(w, h, map), tb);
        }

        // ---------------- building (conversion) sheet ----------------
        void DrawBuildingSheet(float w, float h, MapView map)
        {
            int l = map.BuildingLot;
            var D = Data.Instance;
            if (l < 0 || l >= D.Lots.Count) return;
            InWorld.BuildingSheet(Host(w, h), SheetArea(w, h, map), l, map.BuildingType(l), _lootPick, () => map.ClearBuilding(),
                GutRowH(l), r => DrawGutRow(r, l));   // GameUI.Camp.cs: GUT row (main.gd _sheet_gut)
        }

        // ---------------- wall sheet ----------------
        void DrawWallSheet(float w, float h, MapView map)
        {
            var run = G.RunById(map.SelectedRun);
            if (run == null) { map.ClearBuilding(); return; }
            InWorld.WallSheet(Host(w, h), SheetArea(w, h, map), run, () => map.ClearBuilding());
        }

        // ---------------- fence-tile and tower sheets (original rules; main.gd _sheet_fence / _sheet_tower) ----------------
        void DrawFenceSheet(float w, float h, MapView map)
        {
            InWorld.FenceSheet(Host(w, h), SheetArea(w, h, map), map.SelectedFence, () => map.ClearBuilding(), () => Start(map, MapView.Tool.Wall, _fenceTier));
        }

        void DrawTowerSheet(float w, float h, MapView map)
        {
            InWorld.TowerSheet(Host(w, h), SheetArea(w, h, map), map.SelectedTower, () => map.ClearBuilding());
        }
    }
}
