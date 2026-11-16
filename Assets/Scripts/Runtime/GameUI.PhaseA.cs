// v0.12.0a "Defend the base" HUD (IMGUI, built in code): floating labels over converted buildings, the
// conversion sheet for a tapped building, BUILD (buildings) and DEFENSE (walls / gates / towers / traps /
// upgrades / repair / siren) panels, the placement bar for the DEFENSE tools, the wall sheet, "needs repair"
// markers and the red siren / attack banner with its countdown. Everything is read from and sent to the sim.
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
        int _convPage;
        Texture2D _redTex, _amberTex, _barBgTex, _barTex, _pillTex;
        GUIStyle _pill, _pillSmall, _banner, _bannerSmall, _cell, _cellSmall;

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
            _banner = new GUIStyle(_label) { fontSize = 24, fontStyle = FontStyle.Bold, wordWrap = false, alignment = TextAnchor.MiddleLeft };
            _banner.normal.textColor = new Color(1f, 0.93f, 0.85f);
            _bannerSmall = new GUIStyle(_label) { fontSize = 16, wordWrap = false, alignment = TextAnchor.MiddleLeft };
            _bannerSmall.normal.textColor = new Color(1f, 0.85f, 0.75f);
            _cell = new GUIStyle(_button) { fontSize = 15, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(46, 6, 2, 2), wordWrap = true };
            _cellSmall = new GUIStyle(_small) { fontSize = 13, wordWrap = true };
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

        // ---------------- siren / attack banner ----------------
        void DrawAttackBanner(float w, float h, ref float y)
        {
            EnsurePhaseAStyles();
            var a = G.AttackStatus();
            if (a == null) return;
            var st = a.Value;
            bool siren = st.major || (Game.PhaseARules && G.HasSiren());
            if (st.phase == "warn" && !siren && st.hoursLeft > 2.0) return;   // a few stragglers: the phase line says it
            float bw = Mathf.Min(620f, w - 720f + 300f), bx = w / 2 - bw / 2;
            bw = Mathf.Max(bw, 520f); bx = w / 2 - bw / 2;
            var r = Blk(new Rect(bx, y, bw, 70));
            float pulse = 0.78f + 0.22f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * (st.phase == "attack" ? 4f : 2.2f)));
            var old = GUI.color; GUI.color = new Color(1f, 1f, 1f, pulse);
            GUI.DrawTexture(r, st.phase == "attack" || siren ? _redTex : _amberTex);
            GUI.color = old;
            Icon(new Rect(r.x + 8, r.y + 7, 56, 56), st.phase == "attack" ? "warn" : "siren", new Color(0.82f, 0.22f, 0.16f));
            string l1, l2;
            if (st.phase == "warn")
            {
                int mins = Mathf.Max(0, (int)Math.Round(st.hoursLeft * 60.0));
                l1 = string.Format("{0} · ~{1} ZOMBIES FROM THE {2}", siren ? "SIREN" : "MOVEMENT", st.n, st.dir.ToUpperInvariant());
                l2 = string.Format("At the walls in {0}h {1:00}m — guards to the line, patch the walls", mins / 60, mins % 60);
            }
            else
            {
                l1 = string.Format("UNDER ATTACK · {0} ZOMBIES", st.n);
                l2 = st.breaches > 0 ? string.Format("{0} BREACH{1} IN THE WALL — they're getting in!", st.breaches, st.breaches == 1 ? "" : "ES") : (G.RaidAtWallNow > 0 ? G.RaidAtWallNow + " clawing at the walls · guards firing" : "Closing in on the walls");
            }
            Shadowed(new Rect(r.x + 74, r.y + 6, bw - 84, 34), l1, _banner);
            GUI.Label(new Rect(r.x + 74, r.y + 40, bw - 84, 24), l2, _bannerSmall);
            y += 76;
        }

        void DrawRepairChip(float w, ref float y)
        {
            if (!Game.PhaseARules) return;   // original rules: repairs are automatic, there is no REPAIR NOW / auto-repair
            int n = G.DamagedWallCount();
            if (n == 0) return;
            int br = G.BreachCount();
            double cost = 0; foreach (var it in G.RepairQueue()) cost += G.RepairCost(it) * 1.5;
            float bw = 520f, bx = w / 2 - bw / 2;
            var r = Blk(new Rect(bx, y, bw, 44));
            GUI.DrawTexture(r, _pillTex);
            Icon(new Rect(r.x + 6, r.y + 5, 34, 34), "repair", new Color(0.88f, 0.62f, 0.18f));
            string t = br > 0 ? string.Format("{0} BREACH{1} · {2} TO FIX", br, br == 1 ? "" : "ES", n) : string.Format("{0} WALL SECTION{1} DAMAGED", n, n == 1 ? "" : "S");
            var ts = new GUIStyle(_pill) { fontSize = 16 }; ts.normal.textColor = br > 0 ? RED_INK : AMBER;
            Shadowed(new Rect(r.x + 46, r.y + 2, bw - 230, 22), t, ts);
            GUI.Label(new Rect(r.x + 46, r.y + 22, bw - 230, 20), G.C.Auto.Repair ? (G.MatsTotal() >= 2 ? "Survivors are patching them" : "Out of materials") : "Auto-repair is off", _cellSmall);
            if (Btn(new Rect(r.xMax - 176, r.y + 4, 170, 36), string.Format("REPAIR NOW ({0:0})", Math.Ceiling(cost)), _amberButton))
            {
                var rr = _b.Act("repair_all");
                _b.Toast(rr == "" ? "Crews patched the walls." : rr);
                MapView.Instance?.DirtyDynamic();
            }
            y += 50;
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

        void PanelHeader(Rect p, string title)
        {
            var hdr = new GUIStyle(_title) { fontSize = 22, wordWrap = false };
            Shadowed(new Rect(p.x + 14, p.y + 8, p.width - 110, 30), title, hdr);
            if (Btn(new Rect(p.xMax - 92, p.y + 6, 84, 36), "CLOSE", _darkButton)) _panel = Panel.None;
        }

        void DrawBuildPanel(float w, MapView map)
        {
            EnsurePhaseAStyles();
            const float PW = 330f;
            var p = Blk(new Rect(w - PW - 8, 108, PW, 560));
            GUI.Box(p, GUIContent.none, _box); GUI.Box(p, GUIContent.none, _box);
            PanelHeader(p, "BUILDINGS");
            float y = p.y + 48, x = p.x + 12, bw = PW - 24;
            bool auto = G.C.Auto.Conv;
            if (Btn(new Rect(x, y, bw, 44), "AI AUTO-CONVERT: " + (auto ? "ON" : "OFF"), auto ? _button : _darkButton))
            {
                _b.Act("set_auto", "conv", !auto);
                _b.Toast(!auto ? "Survivors will convert scavenged buildings in the safe zone as the colony needs them." : "Auto-convert off: tap a scavenged building in the safe zone to convert it yourself.");
            }
            y += 50;
            GUI.Label(new Rect(x, y, bw, 40), "Tap a scavenged building inside the safe zone to convert it. Converted buildings work for every camp nearby.", _cellSmall);
            y += 44;
            var list = G.ConvList();
            const int PER = 6;
            int pages = Math.Max(1, (list.Count + PER - 1) / PER);
            _convPage = Mathf.Clamp(_convPage, 0, pages - 1);
            if (list.Count == 0) { GUI.Label(new Rect(x, y, bw, 40), "No converted buildings yet.", _small); y += 44; }
            foreach (int l in list.Skip(_convPage * PER).Take(PER))
            {
                var cv = G.ConvOf(l);
                if (cv == null || !Data.CONVERSIONS.TryGetValue(cv.Type, out var cd)) continue;
                var row = new Rect(x, y, bw, 46);
                if (GUI.Button(row, GUIContent.none, _darkButton)) { map.SelectBuilding(l); map.FlyTo(Data.Instance.LotCenter(l)); _panel = Panel.None; }
                Icon(new Rect(row.x + 4, row.y + 5, 36, 36), cv.Type, cd.Color);
                GUI.Label(new Rect(row.x + 46, row.y + 2, bw - 50, 22), cd.Name, _pill);
                string stt = G.ConvPending(l) ? string.Format("setting up · {0}%", (int)(G.ConvProgress(l) * 100)) : (cv.Manual == 1 ? "in use" : "in use (AI)");
                GUI.Label(new Rect(row.x + 46, row.y + 24, bw - 50, 20), stt, _cellSmall);
                y += 50;
            }
            if (pages > 1)
            {
                if (Btn(new Rect(x, y, 90, 36), "<")) _convPage--;
                GUI.Label(new Rect(x + 100, y + 6, 110, 26), (_convPage + 1) + "/" + pages, _small);
                if (Btn(new Rect(x + bw - 90, y, 90, 36), ">")) _convPage++;
                y += 42;
            }
            // siren upgrade lives here and in DEFENSE (Phase A only)
            if (Game.PhaseARules) DrawSirenRow(new Rect(x, p.yMax - 64, bw, 56));
        }

        void DrawSirenRow(Rect r)
        {
            // the siren is a Phase A facility: the original data has no "siren" upgrade (UpgradeCost would have nothing to show)
            if (!Game.PhaseARules || !Data.UPGRADES.ContainsKey("siren")) return;
            var mb = G.MainBase();
            if (mb == null) return;
            if (mb.U("siren") > 0)
            {
                Icon(new Rect(r.x, r.y + 8, 40, 40), "siren", new Color(0.82f, 0.22f, 0.16f));
                GUI.Label(new Rect(r.x + 48, r.y + 4, r.width - 48, 48), "Air-raid siren built: warnings come 3 hours earlier.", _cellSmall);
                return;
            }
            var cost = G.UpgradeCost(mb, "siren");
            string blk = G.UpgradeBlock(mb, "siren");
            GUI.enabled = blk == "" && G.CanAfford(cost);
            if (GUI.Button(r, "BUILD AIR-RAID SIREN\n" + Game.FmtCost(cost), _cell)) { var e = _b.Act("do_upgrade", mb.Id, "siren"); _b.Toast(e == "" ? "The siren is up: every attack is announced 3 hours earlier." : e); }
            GUI.enabled = true;
            Icon(new Rect(r.x + 4, r.y + 8, 38, 38), "siren", new Color(0.82f, 0.22f, 0.16f));
        }

        void DrawDefensePanel(float w, MapView map)
        {
            EnsurePhaseAStyles();
            if (!Game.PhaseARules) { DrawFencePanel(w, map); return; }
            const float PW = 360f;
            var p = Blk(new Rect(w - PW - 8, 108, PW, 600));
            GUI.Box(p, GUIContent.none, _box); GUI.Box(p, GUIContent.none, _box);
            PanelHeader(p, "DEFENSE");
            float y = p.y + 46, x = p.x + 12, bw = PW - 24;
            var ri = G.RaidInfo();
            string rl = ri == null ? "No attack expected yet. The dead come every night from the 2nd." : ri.On ? string.Format("Under attack: {0} zombies.", ri.Spawned) : ri.Warned ? string.Format("Attack tonight: about {0}.", ri.N) : string.Format("Next attack: tonight, about {0}.", G.RaidSize());
            GUI.Label(new Rect(x, y, bw, 36), rl, _cellSmall); y += 36;
            float cw = (bw - 8) / 2, ch = 54;
            void Tool(int col, string label, string icon, Action a)
            {
                var r = new Rect(x + col * (cw + 8), y, cw, ch);
                if (Btn(r, label, _cell)) a();
                Icon(new Rect(r.x + 4, r.y + 8, 38, 38), icon, new Color(0.42f, 0.48f, 0.23f));
            }
            Tool(0, "PALISADE\nWALL", "wall", () => Start(map, MapView.Tool.Wall, 0));
            Tool(1, "SCRAP-METAL\nWALL", "wall", () => Start(map, MapView.Tool.Wall, 2)); y += ch + 6;
            Tool(0, "CONCRETE\nWALL", "wall", () => Start(map, MapView.Tool.Wall, 3));
            Tool(1, "GATE", "gate", () => Start(map, MapView.Tool.Gate, 0)); y += ch + 6;
            Tool(0, "WATCHTOWER", "tower", () => Start(map, MapView.Tool.Tower, 0));
            Tool(1, "UPGRADE\nWALL", "upgrade", () => Start(map, MapView.Tool.Upgrade, 0)); y += ch + 6;
            Tool(0, "SPIKE PIT", "trap", () => Start(map, MapView.Tool.Trap, 0, TrapIndex("spikes")));
            Tool(1, "BARBED\nWIRE", "trap", () => Start(map, MapView.Tool.Trap, 0, TrapIndex("barbed"))); y += ch + 12;
            bool ar = G.C.Auto.Repair;
            if (Btn(new Rect(x, y, bw, 44), "AUTO-REPAIR: " + (ar ? "ON" : "OFF"), ar ? _button : _darkButton))
            {
                _b.Act("set_auto", "repair", !ar);
                _b.Toast(!ar ? "Survivors will patch damaged walls and rebuild breaches while there are materials." : "Auto-repair off: damaged walls are flagged and you repair them with REPAIR NOW.");
            }
            y += 50;
            int dmg = G.DamagedWallCount();
            GUI.enabled = dmg > 0;
            if (Btn(new Rect(x, y, bw, 44), dmg > 0 ? string.Format("REPAIR NOW ({0} section{1})", dmg, dmg == 1 ? "" : "s") : "WALLS ARE SOUND", _amberButton)) { var e = _b.Act("repair_all"); _b.Toast(e == "" ? "Crews patched the walls." : e); map.DirtyDynamic(); }
            GUI.enabled = true;
            y += 52;
            DrawSirenRow(new Rect(x, y, bw, 56));
        }

        /// <summary>DEFENSE under the original v0.10.8a rules: main.gd FENCES (line tool + SUGGEST), gates, TOWERS and
        /// TRAPS through build_fence / build_fence_segs / place_gate / place_tower / place_traps with the original
        /// costs and unlocks. No repair buttons or siren: survivors repair from the repair queue on their own.</summary>
        void DrawFencePanel(float w, MapView map)
        {
            const float PW = 360f;
            var p = Blk(new Rect(w - PW - 8, 108, PW, 560));
            GUI.Box(p, GUIContent.none, _box); GUI.Box(p, GUIContent.none, _box);
            PanelHeader(p, "DEFENSE");
            float y = p.y + 46, x = p.x + 12, bw = PW - 24;
            var ri = G.RaidInfo();
            string rl = ri == null ? "Hordes won't come for your camps until the city has fallen; until then the dead probe your fences at night." : ri.On ? string.Format("Under attack: {0} zombies.", ri.Spawned) : ri.Warned ? string.Format("Attack tonight: about {0}.", ri.N) : "Fence your camp in before the city falls.";
            GUI.Label(new Rect(x, y, bw, 40), rl, _cellSmall); y += 42;
            float cw = (bw - 8) / 2, ch = 54;
            void Tool(int col, string label, string icon, Action a)
            {
                var r = new Rect(x + col * (cw + 8), y, cw, ch);
                if (Btn(r, label, _cell)) a();
                Icon(new Rect(r.x + 4, r.y + 8, 38, 38), icon, new Color(0.42f, 0.48f, 0.23f));
            }
            int ft = _fenceTier;
            Tool(0, "FENCE\nLINE", "wall", () => Start(map, MapView.Tool.Wall, ft));
            Tool(1, "SUGGEST\nLOOP", "wall", () => Start(map, MapView.Tool.Loop, ft)); y += ch + 6;
            Tool(0, "GATE", "gate", () => Start(map, MapView.Tool.Gate, 0));
            Tool(1, "TOWER", "tower", () => Start(map, MapView.Tool.Tower, _towerTier)); y += ch + 6;
            Tool(0, "TRAP", "trap", () => Start(map, MapView.Tool.Trap, 0, TrapIndex("spikes"))); y += ch + 12;
            var mb = G.MainBase();
            int fences = 0, gates = 0;
            foreach (var kv in G.S.Fences) if (kv.Value.Col == G.CurCol) { fences++; if (G.IsGate(kv.Key)) gates++; }
            int towers = 0; foreach (var kv in G.S.Towers) if (kv.Value.Col == G.CurCol) towers++;
            string stat = string.Format("{0} fence tiles · {1} gate{2} · {3} tower{4} · {5} / {6} traps{7}", fences, gates, gates == 1 ? "" : "s", towers, towers == 1 ? "" : "s", G.TrapsOf(), Data.TRAP_MAX,
                mb != null && G.IsEnclosed(mb) ? " · camp fenced in" : "");
            var ss = new GUIStyle(_cellSmall) { fontSize = 14 }; ss.normal.textColor = AMBER;
            GUI.Label(new Rect(x, y, bw, 40), stat, ss); y += 44;
            GUI.Label(new Rect(x, y, bw, 110), "Repairs are automatic: survivors fix damaged fences, gates and towers from the stockpile, gates first. Tap a fence or tower on the map to upgrade it, make a gate or remove it. Draw a fence line over an old one in a better material to upgrade it.", _cellSmall);
        }

        // main.gd world.fence_tier / tower_tier: the last material picked on the placement bar (wood palisade / watchtower at first)
        int _fenceTier, _towerTier;

        static int TrapIndex(string id) { for (int i = 0; i < Data.TRAPS.Length; i++) if (Data.TRAPS[i].Id == id) return i; return 0; }

        void Start(MapView map, MapView.Tool t, int tier, int trap = 0)
        {
            map.StartTool(t, tier, trap);
            _b.Toast(t == MapView.Tool.Loop ? "The suggested fence loop around your camp. Pick the material, CONFIRM to build." : t == MapView.Tool.Wall ? (Game.PhaseARules ? "Tap the map to place the wall. ROTATE and +/- to shape it, CONFIRM to build." : "Tap the map to place the fence line. ROTATE and +/- to shape it, CONFIRM to build.") : t == MapView.Tool.Upgrade ? "Tap one of your walls to upgrade it." : t == MapView.Tool.Gate ? "Tap a wall to put a gate in it." : "Tap the map to place it.");
        }

        // ---------------- placement bar ----------------
        void DrawPlacementBar(float w, float h, MapView map)
        {
            EnsurePhaseAStyles();
            float pw = Mathf.Min(840f, w - 200f), ph = 168f, px = w / 2 - pw / 2, py = h - 62 - ph - 10;
            var p = Blk(new Rect(px, py, pw, ph));
            GUI.Box(p, GUIContent.none, _box); GUI.Box(p, GUIContent.none, _box);
            string icon = map.ActiveTool == MapView.Tool.Wall || map.ActiveTool == MapView.Tool.Loop ? "wall" : map.ActiveTool == MapView.Tool.Gate ? "gate" : map.ActiveTool == MapView.Tool.Tower ? "tower" : map.ActiveTool == MapView.Tool.Trap ? "trap" : "upgrade";
            Icon(new Rect(px + 10, py + 10, 46, 46), icon, new Color(0.42f, 0.48f, 0.23f));
            var tt = new GUIStyle(_pill) { fontSize = 18, wordWrap = false, clipping = TextClipping.Clip };
            Shadowed(new Rect(px + 64, py + 8, pw - 74, 26), map.ToolInfo, tt);
            var cs = new GUIStyle(_small) { wordWrap = false }; cs.normal.textColor = AMBER;
            GUI.Label(new Rect(px + 64, py + 34, pw - 74, 22), "Cost: " + map.ToolCost + (map.ActiveTool == MapView.Tool.Loop ? "" : "   ·   tap the map to move it"), cs);
            if (map.ToolErr != "") { var es = new GUIStyle(_warn) { fontSize = 15, wordWrap = false, clipping = TextClipping.Clip }; GUI.Label(new Rect(px + 64, py + 56, pw - 74, 22), map.ToolErr, es); }
            float by = py + ph - 74, bh = 64, x = px + 10;
            if (map.ActiveTool == MapView.Tool.Wall || map.ActiveTool == MapView.Tool.Loop)
            {
                if (map.ActiveTool == MapView.Tool.Wall)
                {
                    if (Btn(new Rect(x, by, 130, bh), "ROTATE")) map.Rotate(); x += 138;
                    if (Btn(new Rect(x, by, 70, bh), "−")) map.Shorter(); x += 76;
                    if (Btn(new Rect(x, by, 70, bh), "+")) map.Longer(); x += 78;
                }
                if (Game.PhaseARules)
                {
                    string[] tn = { "PALISADE", "", "SCRAP", "CONCRETE" };
                    int[] path = Game.WALL_PATH;
                    int cur = Array.IndexOf(path, map.WallTier);
                    if (Btn(new Rect(x, by, 140, bh), tn[map.WallTier < tn.Length ? map.WallTier : 0], _brownButton)) map.SetWallTier(path[(cur + 1) % path.Length]);
                    x += 148;
                }
                else
                {
                    // the original's five fence tiers (main.gd fence tier picker); locked ones still show, CONFIRM says what research they need
                    string[] tn = { "WOOD", "CHAIN-LINK", "SHEET METAL", "CONCRETE", "REINFORCED" };
                    int t = Mathf.Clamp(map.WallTier, 0, Data.FENCES.Length - 1);
                    if (Btn(new Rect(x, by, 190, bh), (t < tn.Length ? tn[t] : Data.FENCES[t].Name.ToUpperInvariant()) + (G.FenceTierUnlocked(t) ? "" : "\n(locked)"), _brownButton))
                    { _fenceTier = (t + 1) % Data.FENCES.Length; map.SetWallTier(_fenceTier); }
                    x += 198;
                }
            }
            else if (map.ActiveTool == MapView.Tool.Tower)
            {
                int t = Mathf.Clamp(map.TowerTier, 0, Data.TOWERS.Length - 1);
                if (Btn(new Rect(x, by, 230, bh), Data.TOWERS[t].Name.ToUpperInvariant() + (G.TowerTierUnlocked(t) ? "" : "\n(locked)"), _brownButton))
                { _towerTier = (t + 1) % Data.TOWERS.Length; map.SetTowerTier(_towerTier); }
                x += 238;
            }
            else if (map.ActiveTool == MapView.Tool.Trap)
            {
                if (Game.PhaseARules)
                {
                    string[] ids = { "spikes", "barbed", "deadfall", "tripwire" };
                    int cur = Array.IndexOf(ids, Data.TRAPS[map.TrapKind].Id);
                    if (Btn(new Rect(x, by, 230, bh), "TYPE: " + Data.TRAPS[map.TrapKind].Name.ToUpperInvariant(), _brownButton)) map.SetTrapKind(TrapIndex(ids[(cur + 1 + ids.Length) % ids.Length]));
                }
                else if (Btn(new Rect(x, by, 300, bh), "TYPE: " + Data.TRAPS[map.TrapKind].Name.ToUpperInvariant() + (G.TrapUnlocked(map.TrapKind) ? "" : " (locked)"), _brownButton))
                    map.SetTrapKind((map.TrapKind + 1) % Data.TRAPS.Length);   // all seven of the original's traps
                x += Game.PhaseARules ? 238 : 308;
            }
            if (Btn(new Rect(px + pw - 330, by, 150, bh), "CANCEL", _darkButton)) map.CancelTool();
            GUI.enabled = map.ToolOk;
            if (Btn(new Rect(px + pw - 170, by, 160, bh), "CONFIRM", _amberButton))
            {
                string e = map.ConfirmTool();
                _b.Toast(e == "" ? "Built." : e);
            }
            GUI.enabled = true;
        }

        // ---------------- building (conversion) sheet ----------------
        void DrawBuildingSheet(float w, float h, MapView map)
        {
            EnsurePhaseAStyles();
            int l = map.BuildingLot;
            var D = Data.Instance;
            if (l < 0 || l >= D.Lots.Count) return;
            var cv = G.ConvOf(l);
            string err = G.ConvError(l);
            bool canConv = err == "";
            float gutH = GutRowH(l);   // GameUI.Camp.cs: GUT row (main.gd _sheet_gut)
            float pw = Mathf.Min(980f, w - 120f), ph = (canConv || cv != null ? 300f : 150f) + gutH, px = w / 2 - pw / 2, py = h - 62 - ph - 10;
            var p = Blk(new Rect(px, py, pw, ph));
            GUI.Box(p, GUIContent.none, _box); GUI.Box(p, GUIContent.none, _box);
            var tt = new GUIStyle(_title) { fontSize = 24, wordWrap = false, clipping = TextClipping.Clip };
            string name = D.LotName(l);
            string type = map.BuildingType(l);
            Shadowed(new Rect(px + 16, py + 8, pw - 140, 32), (string.IsNullOrEmpty(name) ? type : name).ToUpperInvariant(), tt);
            if (Btn(new Rect(px + pw - 100, py + 8, 90, 38), "CLOSE", _darkButton)) { map.ClearBuilding(); return; }
            string status;
            if (cv != null && !Data.CONVERSIONS.ContainsKey(cv.Type)) status = type + " · " + Game.ConvNameOf(cv.Type);
            else if (cv != null)
            {
                var cd = Data.CONVERSIONS[cv.Type];
                status = G.ConvPending(l) ? string.Format("Being set up as {0} · {1}% · ready in about {2:0.0} h", cd.Name, (int)(G.ConvProgress(l) * 100), Math.Max(0.0, cv.Ready - G.S.T)) : cd.Name + ": " + cd.Desc;
            }
            else status = canConv ? type + " · scavenged · inside the safe zone. Pick a use:" : type + " · " + err;
            var ss = new GUIStyle(cv == null && !canConv ? _warn : _label) { fontSize = 16, wordWrap = true };
            // main.gd _sheet_lot: SEND TASK FORCE TO LOOT on any unsearched building with loot left (send_raid)
            bool loot = cv == null && !G.Searched(l) && G.S.Loot[l] > 0.15f;
            GUI.Label(new Rect(px + 16, py + 42, pw - 32 - (loot ? 300 : 0), 44), status, ss);
            if (loot && Btn(new Rect(px + pw - 306, py + 46, 290, 40), "SEND TASK FORCE TO LOOT", _amberButton))
            {
                var e = _b.Act("send_raid", l);
                _b.Toast(e == "" ? "A task force is heading there for supplies." : e);
            }
            DrawGutRow(new Rect(px + 16, py + ph - gutH - 8, pw - 32, gutH), l);
            if (!canConv && cv == null) return;
            if (cv != null && G.ConvPlaceError(l) != "") return;
            // grid of uses: 5 x 2
            float gy = py + 92, cw = (pw - 32 - 4 * 8) / 5, chh = 96;
            int i = 0;
            foreach (var t in Data.CONV_ORDER)
            {
                var cd = Data.CONVERSIONS[t];
                int col = i % 5, row = i / 5;
                var r = new Rect(px + 16 + col * (cw + 8), gy + row * (chh + 8), cw, chh);
                bool cur = cv != null && cv.Type == t;
                var cost = Game.ConvCost(t);
                GUI.enabled = !cur && G.CanAfford(cost);
                if (GUI.Button(r, GUIContent.none, cur ? _amberButton : _button))
                {
                    _b.PlayClick();
                    string e = _b.Act("convert_building", l, t);
                    _b.Toast(e != "" ? e : Game.PhaseARules ? string.Format("Survivors are setting it up as {0} (about {1:0} h).", cd.Name, G.ConvHours(t)) : string.Format("Survivors set it up as {0}.", cd.Name));
                    map.DirtyDynamic();
                }
                GUI.enabled = true;
                Icon(new Rect(r.x + 6, r.y + 6, 40, 40), t, cd.Color);
                var ns = new GUIStyle(_pill) { fontSize = 15, wordWrap = true };
                GUI.Label(new Rect(r.x + 50, r.y + 4, r.width - 54, 42), cd.Name, ns);
                var cs2 = new GUIStyle(_cellSmall) { fontSize = 12 }; cs2.normal.textColor = G.CanAfford(cost) ? new Color(0.85f, 0.82f, 0.7f) : RED_INK;
                GUI.Label(new Rect(r.x + 6, r.y + 48, r.width - 10, 46), Game.FmtCost(cost) + (Game.PhaseARules ? " · " + G.ConvHours(t, l).ToString("0") + " h" : ""), cs2);
                i++;
            }
        }

        // ---------------- wall sheet ----------------
        void DrawWallSheet(float w, float h, MapView map)
        {
            EnsurePhaseAStyles();
            var run = G.RunById(map.SelectedRun);
            if (run == null) { map.ClearBuilding(); return; }
            int tiles = 0, dmg = 0, br = 0;
            foreach (int k in G.RunTiles(run))
            {
                if (G.S.Fences.TryGetValue(k, out var f)) { tiles++; if (f.Hp < G.FenceTileMax(k) * 0.75) dmg++; }
                else if (G.S.Breaches.ContainsKey(k)) { tiles++; br++; }
            }
            float pw = 720f, ph = 168f, px = w / 2 - pw / 2, py = h - 62 - ph - 10;
            var p = Blk(new Rect(px, py, pw, ph));
            GUI.Box(p, GUIContent.none, _box); GUI.Box(p, GUIContent.none, _box);
            Icon(new Rect(px + 10, py + 10, 46, 46), "wall", new Color(0.42f, 0.48f, 0.23f));
            var tt = new GUIStyle(_title) { fontSize = 22, wordWrap = false };
            Shadowed(new Rect(px + 64, py + 8, pw - 170, 30), (run.Starter ? "CAMP " : "") + Data.FENCES[run.Tier].Name.ToUpperInvariant(), tt);
            if (Btn(new Rect(px + pw - 100, py + 8, 90, 38), "CLOSE", _darkButton)) { map.ClearBuilding(); return; }
            var st = new GUIStyle(_label) { fontSize = 16 }; if (br > 0) st.normal.textColor = RED_INK;
            GUI.Label(new Rect(px + 64, py + 40, pw - 80, 24), string.Format("{0} tiles · {1} damaged · {2} breach{3}", tiles, dmg, br, br == 1 ? "" : "es"), st);
            var up = G.WallUpgradePlan(run);
            float by = py + ph - 74;
            GUI.enabled = up.err == "" && G.CanAfford(up.cost);
            if (Btn(new Rect(px + 10, by, 300, 64), up.next >= 0 ? "UPGRADE TO " + Data.FENCES[up.next].Name.ToUpperInvariant() + "\n" + Game.FmtCost(up.cost) : "MAX LEVEL", _amberButton))
            { var e = _b.Act("upgrade_wall", run.Id); _b.Toast(e == "" ? "Wall upgraded." : e); map.DirtyDynamic(); }
            GUI.enabled = dmg + br > 0 && Game.PhaseARules;
            if (Game.PhaseARules && Btn(new Rect(px + 318, by, 190, 64), "REPAIR NOW")) { var e = _b.Act("repair_all"); _b.Toast(e == "" ? "Crews patched the walls." : e); map.DirtyDynamic(); }
            GUI.enabled = !run.Starter;
            if (Btn(new Rect(px + 516, by, 190, 64), "REMOVE", _darkButton)) { var e = _b.Act("remove_wall", run.Id); _b.Toast(e == "" ? "Wall torn down (half the materials back)." : e); map.ClearBuilding(); map.DirtyDynamic(); }
            GUI.enabled = true;
            if (up.err != "" && up.next >= 0) GUI.Label(new Rect(px + 64, py + 64, pw - 80, 22), up.err, new GUIStyle(_warn) { fontSize = 14 });
        }

        // ---------------- fence-tile and tower sheets (original rules; main.gd _sheet_fence / _sheet_tower) ----------------
        void DrawFenceSheet(float w, float h, MapView map)
        {
            EnsurePhaseAStyles();
            int k = map.SelectedFence;
            if (!G.S.Fences.TryGetValue(k, out var f)) { map.ClearBuilding(); return; }
            var D = Data.Instance;
            int tier = Mathf.Clamp(f.Tier, 0, Data.FENCES.Length - 1);
            var fd = Data.FENCES[tier];
            bool gate = G.IsGate(k);
            float pw = 760f, ph = 176f, px = w / 2 - pw / 2, py = h - 62 - ph - 10;
            var p = Blk(new Rect(px, py, pw, ph));
            GUI.Box(p, GUIContent.none, _box); GUI.Box(p, GUIContent.none, _box);
            Icon(new Rect(px + 10, py + 10, 46, 46), gate ? "gate" : "wall", new Color(0.42f, 0.48f, 0.23f));
            var tt = new GUIStyle(_title) { fontSize = 22, wordWrap = false, clipping = TextClipping.Clip };
            Shadowed(new Rect(px + 64, py + 8, pw - 170, 30), ((gate ? "Gate · " : "") + fd.Name).ToUpperInvariant(), tt);
            if (Btn(new Rect(px + pw - 100, py + 8, 90, 38), "CLOSE", _darkButton)) { map.ClearBuilding(); return; }
            var pos = new Vector2Int(k % D.W, k / D.W);
            var st = new GUIStyle(_label) { fontSize = 15, wordWrap = true };
            GUI.Label(new Rect(px + 64, py + 40, pw - 80, 44), string.Format("{0} · {1} / {2} hp. Survivors repair damage from the repair queue (gates first) while there are materials.", Game.StreetName(pos), (int)f.Hp, (int)G.FenceTileMax(k)), st);
            if (f.Col != G.CurCol) { GUI.Label(new Rect(px + 64, py + 88, pw - 80, 24), "Built by another colony. It blocks zombies for everyone, but only they can change it.", _cellSmall); return; }
            float by = py + ph - 70, bh = 60, x = px + 10;
            if (gate)
            {
                if (Btn(new Rect(x, by, 200, bh), "REMOVE GATE")) { var e = _b.Act("remove_gate", k); _b.Toast(e == "" ? "Gate taken out." : e); map.DirtyDynamic(); }
                x += 208;
            }
            else
            {
                string ge = G.CanGate(k);
                GUI.enabled = ge == "" && G.CanAfford(Data.GATE_COST);
                if (Btn(new Rect(x, by, 220, bh), ge == "" ? "MAKE IT A GATE\n" + Game.FmtCost(Data.GATE_COST) : "GATE: " + ge, _cell)) { var e = _b.Act("place_gate", k); _b.Toast(e == "" ? "Gate built." : e); map.DirtyDynamic(); }
                GUI.enabled = true;
                x += 228;
            }
            int se = G.SegAtTile(k);
            int nt = tier + 1;
            if (se >= 0)
            {
                if (nt < Data.FENCES.Length)
                {
                    var cost = G.FenceCost(nt, G.EdgeTiles(se).Count);
                    bool un = G.FenceTierUnlocked(nt);
                    GUI.enabled = un && G.CanAfford(cost);
                    if (Btn(new Rect(x, by, 250, bh), un ? "UPGRADE → " + Data.FENCES[nt].Name.ToUpperInvariant() + "\n" + Game.FmtCost(cost) : Data.FENCES[nt].Name + " needs\n" + G.UnlockSourceName(Data.FENCES[nt].Unlock), _amberButton))
                    { var e = _b.Act("build_fence_segs", new List<int> { se }, nt); _b.Toast(e == "" ? "Section upgraded." : e); map.DirtyDynamic(); }
                    GUI.enabled = true;
                    x += 258;
                }
                if (Btn(new Rect(px + pw - 220, by, 210, bh), "REMOVE SECTION (50%)", _darkButton)) { var e = _b.Act("remove_fence_seg", se); _b.Toast(e == "" ? "Section torn down (half the materials back)." : e); map.ClearBuilding(); map.DirtyDynamic(); }
            }
            else
            {
                if (Btn(new Rect(x, by, 200, bh), "FENCE TOOL")) { map.ClearBuilding(); Start(map, MapView.Tool.Wall, _fenceTier); }
                if (Btn(new Rect(px + pw - 190, by, 180, bh), "REMOVE (50%)", _darkButton)) { var e = _b.Act("remove_fence", new List<Vector2Int> { pos }); _b.Toast(e == "" ? "Fence torn down (half the materials back)." : e); map.ClearBuilding(); map.DirtyDynamic(); }
            }
        }

        void DrawTowerSheet(float w, float h, MapView map)
        {
            EnsurePhaseAStyles();
            int k = map.SelectedTower;
            if (!G.S.Towers.TryGetValue(k, out var t)) { map.ClearBuilding(); return; }
            var td = Data.TOWERS[Mathf.Clamp(t.Tier, 0, Data.TOWERS.Length - 1)];
            float pw = 720f, ph = 176f, px = w / 2 - pw / 2, py = h - 62 - ph - 10;
            var p = Blk(new Rect(px, py, pw, ph));
            GUI.Box(p, GUIContent.none, _box); GUI.Box(p, GUIContent.none, _box);
            Icon(new Rect(px + 10, py + 10, 46, 46), "tower", new Color(0.42f, 0.48f, 0.23f));
            var tt = new GUIStyle(_title) { fontSize = 22, wordWrap = false };
            Shadowed(new Rect(px + 64, py + 8, pw - 170, 30), td.Name.ToUpperInvariant(), tt);
            if (Btn(new Rect(px + pw - 100, py + 8, 90, 38), "CLOSE", _darkButton)) { map.ClearBuilding(); return; }
            var st = new GUIStyle(_label) { fontSize = 15, wordWrap = true };
            GUI.Label(new Rect(px + 64, py + 40, pw - 80, 44), string.Format("{0} / {1} hp · range {2:0} tiles · {3}", (int)t.Hp, (int)td.Hp, td.Range + G.Rx("tower_range"), td.Desc), st);
            if (t.Col != G.CurCol) { GUI.Label(new Rect(px + 64, py + 88, pw - 80, 24), "Belongs to another colony.", _cellSmall); return; }
            float by = py + ph - 70, bh = 60;
            int nt = t.Tier + 1;
            if (nt < Data.TOWERS.Length)
            {
                var cost = G.TowerCost(nt);
                bool un = G.TowerTierUnlocked(nt);
                GUI.enabled = un && G.CanAfford(cost);
                if (Btn(new Rect(px + 10, by, 360, bh), un ? "UPGRADE → " + Data.TOWERS[nt].Name.ToUpperInvariant() + "\n" + Game.FmtCost(cost) : Data.TOWERS[nt].Name + " needs\n" + G.UnlockSourceName(Data.TOWERS[nt].Unlock), _amberButton))
                { var e = _b.Act("upgrade_tower", k); _b.Toast(e == "" ? "Tower upgraded." : e); map.DirtyDynamic(); }
                GUI.enabled = true;
            }
            if (Btn(new Rect(px + pw - 170, by, 160, bh), "REMOVE", _darkButton)) { var e = _b.Act("remove_tower", k); _b.Toast(e == "" ? "Tower taken down." : e); map.ClearBuilding(); map.DirtyDynamic(); }
        }
    }
}
