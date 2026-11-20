// v0.17 stage B: the in-world panels on the UI kit, written once against IKitPaint so the game (IMGUI, UiKit /
// UiWheel, GameUI.PhaseA.cs) and the offline preview (display list) draw exactly the same layout.
//   Docks     where everything sits: the right dock (ACTIONS / BUILDINGS / DEFENSE), the collapsed ACTIONS tab, the
//             bottom sheet area (left of the right dock, right of the camera buttons, above PLAY / speed) and the
//             top banner lane. Sheets docked into the area can never overlap the right dock or that HUD.
//   InWorld   ACTIONS dock, building sheet, placement bar (fence / wall / tower / trap tools), BUILDINGS and DEFENSE
//             panels, wall / fence / tower sheets, and the kit renderer for SideMenus display lists.
// Pure C# (also compiled by the offline preview). Every button sends the same action / callback as before.
using System;
using System.Collections.Generic;
using System.Linq;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    /// <summary>What an in-world panel draws with (UiKit in the game, a display list in the preview).</summary>
    public interface IKitPaint
    {
        /// <summary>Register r as HUD (map taps under it are swallowed). Returns r.</summary>
        Rect Block(Rect r);
        void Plate(Rect r);
        void Header(Rect r, string text, int maxSize);
        bool Button(Rect r, string text, UiRole role, bool selected = false, int maxSize = 20);
        void Fit(Rect r, string text, int maxSize, MenuAlign al, Color col, bool bold = true, int minSize = 11);
        /// <summary>Wrapped body text clipped to r. Returns the height the text needs.</summary>
        float Paragraph(Rect r, string text, int size, Color col);
        float ParaH(string text, int size, float w);
        void Bar(Rect r, float frac, UiRole role, string text);
        void Chip(Rect r, string text, UiRole role);
        int WheelH(string id, Rect r, IList<string> items, int selected, int visible = 3, int maxSize = 20);
        void Icon(Rect r, string id, Color badge);
        void Fill(Rect r, Color c);
        void Divider(Rect r);
        /// <summary>Legacy wrapped label of a SideMenus display list.</summary>
        void Label(Rect r, string text, int size, bool bold, MenuAlign al, bool wrap, Color c);
        /// <summary>Clipped, touch-scrolled view; draw in content coordinates until EndScroll. Returns the visible window.</summary>
        Rect BeginScroll(string id, Rect view, float contentH);
        void EndScroll();
    }

    /// <summary>Everything an in-world panel needs from the game besides the painter.</summary>
    public sealed class PanelHost
    {
        public Game G;
        public IKitPaint P;
        public float W, H;
        public Func<string, object[], string> Act;
        public Action<string> Toast;
        public Action Click;
        public Action Dirty;
        public string Do(string a, params object[] args) => Act(a, args);
    }

    /// <summary>Screen docks shared by every in-world panel.</summary>
    public static class Docks
    {
        public enum Right { None, Tab, Dock }
        public const float TopY = 108f, LeftX = 104f, SheetTop = 206f, BottomHud = 76f, TabW = 56f, TabH = 96f, LaneX = 392f;
        public static float DockW(float w) => w >= 1500f ? 384f : 344f;
        /// <summary>Right dock column: ACTIONS, BUILDINGS or DEFENSE (below the resource chips, down to the bottom edge).</summary>
        public static Rect RightDock(float w, float h) => new Rect(w - 8f - DockW(w), TopY, DockW(w), h - 10f - TopY);
        /// <summary>The collapsed ACTIONS tab on the right edge.</summary>
        public static Rect Tab(float w, float h) => new Rect(w - 8f - TabW, 300f, TabW, TabH);
        static float RightEdge(float w, float h, Right rs) => rs == Right.Dock ? RightDock(w, h).x - 10f : rs == Right.Tab ? w - 8f - TabW - 10f : w - 10f;
        /// <summary>Where bottom sheets / bars live: right of the camera buttons, left of the right dock (or tab), above
        /// PLAY / speed, below the stats panel.</summary>
        public static Rect SheetArea(float w, float h, Right rs) => new Rect(LeftX, SheetTop, RightEdge(w, h, rs) - LeftX, h - BottomHud - SheetTop);
        /// <summary>A sheet of (at most) wantW x wantH docked to the bottom centre of area.</summary>
        public static Rect Sheet(Rect area, float wantW, float wantH)
        {
            float pw = Mathf.Min(wantW, area.width), ph = Mathf.Min(wantH, area.height);
            return new Rect(Mathf.Floor(area.x + (area.width - pw) * 0.5f + 0.5f), area.yMax - ph, pw, ph);
        }
        /// <summary>Top banner lane (attack banner, repair chip, research note, war line): between the stats panel and
        /// the right dock, so banners never run under ACTIONS.</summary>
        public static Rect TopLane(float w, float h, Right rs) { float x1 = RightEdge(w, h, rs); return new Rect(LaneX, TopY - 4f, Mathf.Max(200f, x1 - LaneX), 300f); }
        /// <summary>A banner of (at most) want px centred in the lane.</summary>
        public static Rect Banner(Rect lane, float want, float y, float hgt) { float bw = Mathf.Min(want, lane.width); return new Rect(Mathf.Floor(lane.x + (lane.width - bw) * 0.5f + 0.5f), y, bw, hgt); }
    }

    /// <summary>Inputs of the placement bar (the active map tool).</summary>
    public sealed class ToolBarIn
    {
        public enum Kind { Fence, Loop, Wall, Gate, Tower, Trap, Upgrade }
        public Kind Tool;
        public string Info = "", Cost = "", Err = "", Hint = "";
        public bool PostsVisible, CanUndo, CanClear, Ok;
        public int WallTier, TowerTier, TrapKind;
        public Action Rotate, Shorter, Longer, Undo, Clear, Cancel;
        public Action<int> SetFenceTier, SetWallTier, SetTowerTier, SetTrapKind;
        public Func<string> Confirm;
    }

    public static class InWorld
    {
        public static Color Ink => UiSkin.KitInk;
        public static Color Dim => UiSkin.KitInkDim;
        public static readonly Color Amber = new Color(0.96f, 0.72f, 0.30f, 1f);
        public static readonly Color Warn = new Color(0.98f, 0.52f, 0.40f, 1f);
        public static readonly Color Good = new Color(0.66f, 0.86f, 0.46f, 1f);
        static readonly Color Olive = new Color(0.42f, 0.48f, 0.23f);
        public const float BH = 56f, GAP = 8f, HDR = 44f;

        // ---------------- frame ----------------
        /// <summary>Plate + header strip (+ an optional button at the header's right). Returns the content rect.</summary>
        public static Rect Frame(IKitPaint p, Rect r, string title, string btn, UiRole btnRole, float btnW, out bool pressed, Color? titleCol = null)
        {
            p.Plate(r);
            pressed = false;
            float hw = r.width - 8f - (btn != null ? btnW + 6f : 0f);
            p.Header(new Rect(r.x + 4f, r.y + 4f, hw, HDR), title, 22);
            if (btn != null) pressed = p.Button(new Rect(r.xMax - 4f - btnW, r.y + 4f, btnW, HDR + 2f), btn, btnRole, false, 18);
            return new Rect(r.x + 12f, r.y + 4f + HDR + 10f, r.width - 24f, r.height - HDR - 26f);
        }
        public const float FrameExtra = HDR + 26f;   // frame height minus content height

        /// <summary>Button with a title line and a smaller sub line (cost): "UPGRADE TO CONCRETE" / "40 mats".</summary>
        public static bool Button2(IKitPaint p, Rect r, string title, string sub, UiRole role, bool sel = false)
        {
            if (string.IsNullOrEmpty(sub)) return p.Button(r, title, role, sel, 18);
            bool c = p.Button(r, "", role, sel);
            bool en = role != UiRole.Disabled;
            var f = new Rect(r.x + 8f, r.y + 3f, r.width - 16f, r.height - UiSkin.KitLip - 6f);
            var rows = UiLayout.Rows(f, 0f, 1.15f, 1f);
            p.Fit(rows[0], title, 18, MenuAlign.Center, en ? Ink : UiSkin.KitInkMute, true, 10);
            p.Fit(rows[1], sub, 14, MenuAlign.Center, en ? new Color(1f, 0.9f, 0.7f, 0.95f) : UiSkin.KitInkMute, false, 9);
            return c;
        }

        /// <summary>Button with an icon on the left and a fitted label (tool grids, siren).</summary>
        public static bool IconButton(IKitPaint p, Rect r, string icon, Color badge, string text, UiRole role, string sub = null, bool sel = false)
        {
            bool c = p.Button(r, "", role, sel);
            float isz = Mathf.Min(40f, r.height - UiSkin.KitLip - 10f);
            var face = new Rect(r.x, r.y, r.width, r.height - UiSkin.KitLip);
            p.Icon(new Rect(r.x + 8f, face.y + (face.height - isz) * 0.5f, isz, isz), icon, badge);
            var tr = new Rect(r.x + 14f + isz, face.y + 2f, r.width - isz - 22f, face.height - 4f);
            bool en = role != UiRole.Disabled;
            if (string.IsNullOrEmpty(sub)) p.Fit(tr, text, 18, MenuAlign.Left, en ? Ink : UiSkin.KitInkMute, true, 10);
            else
            {
                var rows = UiLayout.Rows(tr, 0f, 1.15f, 1f);
                p.Fit(rows[0], text, 17, MenuAlign.Left, en ? Ink : UiSkin.KitInkMute, true, 10);
                p.Fit(rows[1], sub, 13, MenuAlign.Left, en ? new Color(1f, 0.88f, 0.66f, 0.95f) : UiSkin.KitInkMute, false, 9);
            }
            return c;
        }

        // ---------------- ACTIONS dock ----------------
        public const int ACT_COLS = 2;
        public static int ActRows => (SideMenus.ACTIONS.Length + ACT_COLS - 1) / ACT_COLS;
        /// <summary>The ACTIONS panel: bottom of the right dock, header + 2-column grid of 56 px buttons.</summary>
        public static Rect ActionsRect(float w, float h)
        {
            var d = Docks.RightDock(w, h);
            float ph = FrameExtra + ActRows * BH + (ActRows - 1) * 6f;
            ph = Mathf.Min(ph, d.height);
            return new Rect(d.x, d.yMax - ph, d.width, ph);
        }

        static UiRole ActRole(string style) => style == "sel" ? UiRole.Accent : style == "brown" ? UiRole.Neutral : UiRole.Primary;

        /// <summary>Draws the dock (open) or the tab (closed). Returns the label of the action tapped, or null.
        /// toggle is set when the user collapsed / expanded the dock.</summary>
        public static string ActionsDock(IKitPaint p, float w, float h, bool open, out bool toggle)
        {
            toggle = false;
            if (!open)
            {
                var t = p.Block(Docks.Tab(w, h));
                if (p.Button(t, "<", UiRole.Neutral, false, 26)) toggle = true;
                return null;
            }
            var r = p.Block(ActionsRect(w, h));
            var c = Frame(p, r, "ACTIONS", ">", UiRole.Neutral, 64f, out bool hide);
            if (hide) toggle = true;
            string hit = null;
            int rows = ActRows;
            float bh = Mathf.Min(BH, (c.height - (rows - 1) * 6f) / rows);
            for (int i = 0; i < SideMenus.ACTIONS.Length; i++)
            {
                var (label, style) = SideMenus.ACTIONS[i];
                int col = i / rows, row = i % rows;   // read top to bottom, then left to right (original order)
                var cell = UiLayout.Cell(new Rect(c.x, c.y, c.width, c.height), ACT_COLS, row * ACT_COLS + col, bh, 6f);
                cell = new Rect(cell.x, c.y + row * (bh + 6f), cell.width, bh);
                if (p.Button(cell, label, ActRole(style), false, 19)) hit = label;
            }
            return hit;
        }

        // ---------------- building (conversion) sheet ----------------
        public sealed class LootPick { public int From; }   // index into "nearest" + own camps (persists across frames)

        /// <summary>main.gd _sheet_lot / conversion sheet for a tapped building. gutRowH / gutRow draw the GUT row
        /// (SideMenus.GutRow). Returns false when the sheet closed itself.</summary>
        public static bool BuildingSheet(PanelHost H, Rect area, int l, string type, LootPick lp, Action close, float gutRowH, Action<Rect> gutRow)
        {
            var g = H.G; var p = H.P; var D = Data.Instance;
            var cv = g.ConvOf(l);
            string err = g.ConvError(l);
            bool canConv = err == "";
            string name = D.LotName(l);
            string title = (string.IsNullOrEmpty(name) ? type : name).ToUpperInvariant();
            string status; bool warn = false;
            float prog = -1f; string progT = null;
            if (cv != null && !Data.CONVERSIONS.ContainsKey(cv.Type)) status = type + " - " + Game.ConvNameOf(cv.Type);
            else if (cv != null)
            {
                var cd = Data.CONVERSIONS[cv.Type];
                if (g.ConvPending(l))
                {
                    status = string.Format("Being set up as {0}.", cd.Name);
                    prog = (float)g.ConvProgress(l);
                    progT = string.Format("SETTING UP  {0}%  -  READY IN {1:0.0} H", (int)(prog * 100), Math.Max(0.0, cv.Ready - g.S.T));
                }
                else status = cd.Name + ": " + Game.RateText(cd.Desc);
            }
            else if (canConv) status = type + " - scavenged, inside the safe zone. Pick a use:";
            else { status = type + " - " + err; warn = true; }
            // GUT row: while our own crew guts it the row becomes a progress bar here
            bool gutMine = cv == null && g.Gutting(l) && g.S.Guts.TryGetValue(l, out var gj) && gj.Col == g.CurCol;
            if (gutMine)
            {
                double left = g.GutLeft(l);
                prog = 1f - Mathf.Clamp01((float)(left / Math.Max(1.0, Data.GUT_SEC)));
                progT = string.Format("GUTTING  {0} S LEFT", (int)Math.Ceiling(left));
                gutRowH = 0f;
            }
            bool loot = cv == null && !g.Searched(l) && g.S.Loot[l] > 0.15f;
            bool grid = (canConv || cv != null) && !(cv != null && g.ConvPlaceError(l) != "");

            float wantW = 1000f;
            var probe = Docks.Sheet(area, wantW, 10f);
            float iw = probe.width - 24f;
            float paraH = Mathf.Min(p.ParaH(status, 17, iw), 66f);
            int cols = iw >= 900f ? 4 : iw >= 520f ? 3 : 2;
            int gridRows = (Data.CONV_ORDER.Length + cols - 1) / cols;
            float cellH = 62f;
            float need = FrameExtra + paraH + (prog >= 0f ? 30f + GAP : 0f) + (loot ? BH + GAP : 0f) + (gutRowH > 0f ? gutRowH + GAP : 0f)
                         + (grid ? gridRows * cellH + (gridRows - 1) * 6f + GAP : 0f);
            if (grid && need > area.height)   // squeeze the use grid before anything else
            {
                float over = need - area.height;
                cellH = Mathf.Max(BH, cellH - over / gridRows);
                need = area.height;
            }
            var r = p.Block(Docks.Sheet(area, wantW, need));
            var c = Frame(p, r, title, "CLOSE", UiRole.Danger, 120f, out bool shut);
            if (shut) { close(); return false; }
            var L = new UiLayout(c, GAP);
            p.Paragraph(L.Top(paraH), status, 17, warn ? Warn : Dim);
            if (prog >= 0f) p.Bar(L.Top(30f), prog, UiRole.Accent, progT);
            if (loot)
            {
                var row = L.Top(BH);
                var camps = g.C.Bases.Where(b => b.Done >= 1.0).ToList();
                int bid = -1;
                Rect btn = row;
                if (camps.Count > 1)
                {
                    var items = new List<string> { "NEAREST CAMP" };
                    foreach (var b in camps) items.Add(g.BaseLabel(b).ToUpperInvariant());
                    var parts = UiLayout.Columns(row, GAP, 1.1f, 1f);
                    lp.From = Mathf.Clamp(lp.From, 0, items.Count - 1);
                    lp.From = p.WheelH("loot_from", parts[0], items, lp.From, 3, 17);
                    if (lp.From > 0) bid = camps[lp.From - 1].Id;
                    btn = parts[1];
                }
                if (p.Button(btn, "SEND TASK FORCE TO LOOT", UiRole.Accent, false, 20))
                {
                    var e = bid >= 0 ? H.Do("send_raid", l, bid) : H.Do("send_raid", l);
                    H.Toast(e == "" ? "A task force is heading there for supplies." : e);
                }
            }
            if (gutRowH > 0f) gutRow(L.Top(gutRowH));
            if (!grid) return true;
            var gr = L.Rest;
            int i = 0;
            foreach (var t in Data.CONV_ORDER)
            {
                var cd = Data.CONVERSIONS[t];
                var cell = UiLayout.Cell(gr, cols, i, cellH, 6f);
                bool cur = cv != null && cv.Type == t;
                var cost = Game.ConvCost(t);
                bool afford = g.CanAfford(cost);
                string sub = ShortCost(cost) + (Game.PhaseARules ? " - " + g.ConvHours(t, l).ToString("0") + " h" : "");
                if (IconButton(p, cell, t, cd.Color, cd.Name, cur ? UiRole.Accent : afford ? UiRole.Primary : UiRole.Disabled, sub, cur) && !cur)
                {
                    H.Click?.Invoke();
                    string e = H.Do("convert_building", l, t);
                    H.Toast(e != "" ? e : Game.PhaseARules ? string.Format("Survivors are setting it up as {0} (about {1:0} h).", cd.Name, g.ConvHours(t)) : string.Format("Survivors set it up as {0}.", cd.Name));
                    H.Dirty?.Invoke();
                }
                i++;
            }
            return true;
        }

        /// <summary>"6 cloth 4 plast 2 elec": the short resource names (as SideMenus.Camp's cost lines).</summary>
        public static string ShortCost(IEnumerable<KeyValuePair<string, double>> cost)
            => string.Join(" ", cost.Select(kv => string.Format("{0} {1}", (int)Math.Ceiling(kv.Value - 0.001),
                (Data.RES_SHORT.TryGetValue(kv.Key, out var s) ? s : Data.RES_LABEL.TryGetValue(kv.Key, out var l) ? l : kv.Key).ToLowerInvariant())));

        // ---------------- placement bar ----------------
        public static readonly string[] FenceNames = { "WOOD", "CHAIN-LINK", "SHEET METAL", "CONCRETE", "REINFORCED" };
        static readonly string[] WallNames = { "PALISADE", "", "SCRAP METAL", "CONCRETE" };

        static string FenceName(int t) => t < FenceNames.Length ? FenceNames[t] : Data.FENCES[t].Name.ToUpperInvariant();

        public static void PlacementBar(PanelHost H, Rect area, ToolBarIn tb)
        {
            var g = H.G; var p = H.P;
            var r = p.Block(Docks.Sheet(area, 980f, FrameExtra + 24f + 22f + BH + 4f + GAP * 3f));
            string icon = tb.Tool == ToolBarIn.Kind.Wall || tb.Tool == ToolBarIn.Kind.Loop || tb.Tool == ToolBarIn.Kind.Fence ? "wall" : tb.Tool == ToolBarIn.Kind.Gate ? "gate" : tb.Tool == ToolBarIn.Kind.Tower ? "tower" : tb.Tool == ToolBarIn.Kind.Trap ? "trap" : "upgrade";
            var c = Frame(p, r, tb.Info, null, UiRole.Neutral, 0f, out _);
            var L = new UiLayout(c, GAP);
            var top = L.Top(24f + 22f + GAP);
            p.Icon(new Rect(top.x, top.y, 46f, 46f), icon, Olive);
            var tl = new Rect(top.x + 56f, top.y, top.width - 56f, 24f);
            bool fenceTool = tb.Tool == ToolBarIn.Kind.Fence;
            // the picked material / type is locked: say so next to the cost (the wheel shows names only)
            string locked = "";
            bool origFence = fenceTool || (!Game.PhaseARules && (tb.Tool == ToolBarIn.Kind.Loop || tb.Tool == ToolBarIn.Kind.Wall));
            if (origFence) { int t = Mathf.Clamp(tb.WallTier, 0, Data.FENCES.Length - 1); if (!g.FenceTierUnlocked(t)) locked = FenceName(t) + " LOCKED: " + g.UnlockSourceName(Data.FENCES[t].Unlock); }
            else if (tb.Tool == ToolBarIn.Kind.Tower) { int t = Mathf.Clamp(tb.TowerTier, 0, Data.TOWERS.Length - 1); if (!g.TowerTierUnlocked(t)) locked = Data.TOWERS[t].Name.ToUpperInvariant() + " LOCKED: " + g.UnlockSourceName(Data.TOWERS[t].Unlock); }
            else if (tb.Tool == ToolBarIn.Kind.Trap && !Game.PhaseARules && tb.TrapKind >= 0 && tb.TrapKind < Data.TRAPS.Length && !g.TrapUnlocked(tb.TrapKind)) locked = Data.TRAPS[tb.TrapKind].Name.ToUpperInvariant() + " LOCKED";
            var cl = locked != "" ? UiLayout.Columns(tl, GAP, 1f, 1f) : new[] { tl };
            p.Fit(cl[0], "Cost: " + tb.Cost + (tb.Tool == ToolBarIn.Kind.Loop || fenceTool ? "" : "   -   tap the map to move it"), 17, MenuAlign.Left, Amber, true, 11);
            if (locked != "") p.Fit(cl[1], locked, 16, MenuAlign.Right, Warn, true, 10);
            var hl = new Rect(tl.x, tl.yMax + 4f, tl.width, 22f);
            if (tb.Err != "") p.Fit(hl, tb.Err, 16, MenuAlign.Left, Warn, false, 10);
            else if (fenceTool) p.Fit(hl, tb.PostsVisible ? tb.Hint : "Zoom in a little to see the corner posts.", 16, MenuAlign.Left, Dim, false, 10);
            var row = L.Top(BH + 4f);
            // fixed left controls, flexible gap, CANCEL + CONFIRM on the right
            var sizes = new List<float>();
            var draw = new List<Action<Rect>>();
            void Add(float px, Action<Rect> a) { sizes.Add(-px); draw.Add(a); }
            float wheelW = Mathf.Clamp(row.width * 0.34f, 240f, 360f);
            if (origFence)
            {
                if (tb.Tool == ToolBarIn.Kind.Wall) { Add(120f, q => { if (p.Button(q, "ROTATE", UiRole.Primary)) tb.Rotate(); }); Add(64f, q => { if (p.Button(q, "-", UiRole.Primary, false, 26)) tb.Shorter(); }); Add(64f, q => { if (p.Button(q, "+", UiRole.Primary, false, 26)) tb.Longer(); }); }
                // the original's five fence tiers; locked ones still show, CONFIRM says what research they need
                Add(wheelW, q =>
                {
                    int t = Mathf.Clamp(tb.WallTier, 0, Data.FENCES.Length - 1);
                    var items = new List<string>();
                    for (int k = 0; k < Data.FENCES.Length; k++) items.Add(FenceName(k));
                    int n = p.WheelH("fence_tier", q, items, t, 3, 19);
                    if (n != t) tb.SetFenceTier(n);
                });
                if (fenceTool)
                {
                    Add(104f, q => { if (p.Button(q, "UNDO", tb.CanUndo ? UiRole.Primary : UiRole.Disabled)) tb.Undo(); });
                    Add(104f, q => { if (p.Button(q, "CLEAR", tb.CanClear ? UiRole.Neutral : UiRole.Disabled)) tb.Clear(); });
                }
            }
            else if (tb.Tool == ToolBarIn.Kind.Wall || tb.Tool == ToolBarIn.Kind.Loop)
            {
                if (tb.Tool == ToolBarIn.Kind.Wall) { Add(120f, q => { if (p.Button(q, "ROTATE", UiRole.Primary)) tb.Rotate(); }); Add(64f, q => { if (p.Button(q, "-", UiRole.Primary, false, 26)) tb.Shorter(); }); Add(64f, q => { if (p.Button(q, "+", UiRole.Primary, false, 26)) tb.Longer(); }); }
                Add(wheelW, q =>
                {
                    int[] path = Game.WALL_PATH;
                    int cur = Mathf.Max(0, Array.IndexOf(path, tb.WallTier));
                    var items = path.Select(t => t < WallNames.Length ? WallNames[t] : Data.FENCES[t].Name.ToUpperInvariant()).ToList();
                    int n = p.WheelH("wall_tier", q, items, cur, 3, 19);
                    if (n != cur) tb.SetWallTier(path[n]);
                });
            }
            else if (tb.Tool == ToolBarIn.Kind.Tower)
            {
                Add(wheelW, q =>
                {
                    int t = Mathf.Clamp(tb.TowerTier, 0, Data.TOWERS.Length - 1);
                    var items = new List<string>();
                    for (int k = 0; k < Data.TOWERS.Length; k++) items.Add(Data.TOWERS[k].Name.ToUpperInvariant());
                    int n = p.WheelH("tower_tier", q, items, t, 3, 19);
                    if (n != t) tb.SetTowerTier(n);
                });
            }
            else if (tb.Tool == ToolBarIn.Kind.Trap)
            {
                Add(wheelW, q =>
                {
                    var idx = new List<int>();
                    if (Game.PhaseARules) { foreach (var id in new[] { "spikes", "barbed", "deadfall", "tripwire" }) for (int k = 0; k < Data.TRAPS.Length; k++) if (Data.TRAPS[k].Id == id) idx.Add(k); }
                    else for (int k = 0; k < Data.TRAPS.Length; k++) idx.Add(k);   // all seven of the original's traps
                    var items = idx.Select(k => Data.TRAPS[k].Name.ToUpperInvariant()).ToList();
                    int cur = Mathf.Max(0, idx.IndexOf(tb.TrapKind));
                    int n = p.WheelH("trap_kind", q, items, cur, 3, 19);
                    if (n != cur) tb.SetTrapKind(idx[n]);
                });
            }
            sizes.Add(1f); draw.Add(null);
            Add(140f, q => { if (p.Button(q, "CANCEL", UiRole.Neutral)) tb.Cancel(); });
            Add(170f, q =>
            {
                if (p.Button(q, "CONFIRM", tb.Ok ? UiRole.Accent : UiRole.Disabled, false, 22))
                {
                    string e = tb.Confirm();
                    H.Toast(e == "" ? "Built." : e);
                }
            });
            var cells = UiLayout.Columns(row, GAP, sizes.ToArray());
            for (int i = 0; i < cells.Length; i++) draw[i]?.Invoke(cells[i]);
        }

        // ---------------- BUILDINGS / DEFENSE (right dock) ----------------
        /// <summary>Right-dock panel frame with CLOSE. Returns the content rect; closed is set when CLOSE was tapped.</summary>
        public static Rect DockFrame(IKitPaint p, float w, float h, string title, out bool closed)
        {
            var r = p.Block(Docks.RightDock(w, h));
            return Frame(p, r, title, "CLOSE", UiRole.Danger, 110f, out closed);
        }

        /// <summary>BUILDINGS: AI auto-convert, the converted buildings (scroll list), the siren (Phase A).</summary>
        public static void BuildPanel(PanelHost H, Action close, Action<int> select)
        {
            var g = H.G; var p = H.P;
            var c = DockFrame(p, H.W, H.H, "BUILDINGS", out bool shut);
            if (shut) { close(); return; }
            var L = new UiLayout(c, GAP);
            bool auto = g.C.Auto.Conv;
            if (p.Button(L.Top(BH), "AI AUTO-CONVERT: " + (auto ? "ON" : "OFF"), auto ? UiRole.Primary : UiRole.Neutral, auto, 18))
            {
                H.Do("set_auto", "conv", !auto);
                H.Toast(!auto ? "Survivors will convert scavenged buildings in the safe zone as the colony needs them." : "Auto-convert off: tap a scavenged building in the safe zone to convert it yourself.");
            }
            const string help = "Tap a scavenged building inside the safe zone to convert it. Converted buildings work for every camp nearby.";
            p.Paragraph(L.Top(Mathf.Min(64f, p.ParaH(help, 15, c.width))), help, 15, Dim);
            if (Game.PhaseARules && Data.UPGRADES.ContainsKey("siren") && g.MainBase() != null) SirenRow(H, L.Bottom(64f));
            p.Fit(L.Top(22f), "CONVERTED BUILDINGS", 15, MenuAlign.Left, Amber, true, 10);
            var list = g.ConvList();
            var view = L.Rest;
            if (list.Count == 0) { p.Fit(new Rect(view.x, view.y, view.width, 26f), "No converted buildings yet.", 16, MenuAlign.Left, Dim, false); return; }
            const float RH = 58f, RG = 6f;
            var win = p.BeginScroll("conv_list", view, list.Count * (RH + RG) - RG);
            float rw = view.width - 10f;
            for (int i = 0; i < list.Count; i++)
            {
                var row = new Rect(0f, i * (RH + RG), rw, RH);
                if (row.yMax < win.y || row.y > win.yMax) continue;
                int l = list[i];
                var cv = g.ConvOf(l);
                if (cv == null || !Data.CONVERSIONS.TryGetValue(cv.Type, out var cd)) continue;
                string stt = g.ConvPending(l) ? string.Format("setting up - {0}%", (int)(g.ConvProgress(l) * 100)) : (cv.Manual == 1 ? "in use" : "in use (AI)");
                if (IconButton(p, row, cv.Type, cd.Color, cd.Name, UiRole.Neutral, stt)) select(l);
            }
            p.EndScroll();
        }

        public static void SirenRow(PanelHost H, Rect r)
        {
            var g = H.G; var p = H.P;
            // the siren is a Phase A facility: the original data has no "siren" upgrade
            if (!Game.PhaseARules || !Data.UPGRADES.ContainsKey("siren")) return;
            var mb = g.MainBase();
            if (mb == null) return;
            var red = new Color(0.82f, 0.22f, 0.16f);
            if (mb.U("siren") > 0)
            {
                p.Icon(new Rect(r.x, r.y + (r.height - 40f) * 0.5f, 40f, 40f), "siren", red);
                p.Paragraph(new Rect(r.x + 50f, r.y + 6f, r.width - 50f, r.height - 6f), "Air-raid siren built: warnings come 3 hours earlier.", 15, Dim);
                return;
            }
            var cost = g.UpgradeCost(mb, "siren");
            string blk = g.UpgradeBlock(mb, "siren");
            bool on = blk == "" && g.CanAfford(cost);
            if (IconButton(p, r, "siren", red, "BUILD AIR-RAID SIREN", on ? UiRole.Primary : UiRole.Disabled, Game.FmtCost(cost)))
            { var e = H.Do("do_upgrade", mb.Id, "siren"); H.Toast(e == "" ? "The siren is up: every attack is announced 3 hours earlier." : e); }
        }

        /// <summary>One tool of the DEFENSE grid.</summary>
        public struct DefTool { public string Label, Sub, Icon; public Action Go; }

        /// <summary>DEFENSE (Phase A): walls / gate / tower / upgrade / traps, AUTO-REPAIR, REPAIR NOW, siren.</summary>
        public static void DefensePanel(PanelHost H, Action close, IList<DefTool> tools)
        {
            var g = H.G; var p = H.P;
            var c = DockFrame(p, H.W, H.H, "DEFENSE", out bool shut);
            if (shut) { close(); return; }
            var L = new UiLayout(c, GAP);
            var ri = g.RaidInfo();
            string rl = ri == null ? "No attack expected yet. The dead come every night from the 2nd." : ri.On ? string.Format("Under attack: {0} zombies.", ri.Spawned) : ri.Warned ? string.Format("Attack tonight: about {0}.", ri.N) : string.Format("Next attack: tonight, about {0}.", g.RaidSize());
            p.Paragraph(L.Top(Mathf.Min(44f, p.ParaH(rl, 15, c.width))), rl, 15, Amber);
            ToolGrid(p, L, tools);
            bool ar = g.C.Auto.Repair;
            if (p.Button(L.Top(BH), "AUTO-REPAIR: " + (ar ? "ON" : "OFF"), ar ? UiRole.Primary : UiRole.Neutral, ar, 18))
            {
                H.Do("set_auto", "repair", !ar);
                H.Toast(!ar ? "Survivors will patch damaged walls and rebuild breaches while there are materials." : "Auto-repair off: damaged walls are flagged and you repair them with REPAIR NOW.");
            }
            int dmg = g.DamagedWallCount();
            if (p.Button(L.Top(BH), dmg > 0 ? string.Format("REPAIR NOW ({0} SECTION{1})", dmg, dmg == 1 ? "" : "S") : "WALLS ARE SOUND", dmg > 0 ? UiRole.Accent : UiRole.Disabled, false, 18) && dmg > 0)
            { var e = H.Do("repair_all"); H.Toast(e == "" ? "Crews patched the walls." : e); H.Dirty?.Invoke(); }
            if (L.Rest.height >= 56f) SirenRow(H, L.Top(Mathf.Min(64f, L.Rest.height)));
        }

        static void ToolGrid(IKitPaint p, UiLayout L, IList<DefTool> tools)
        {
            int rows = (tools.Count + 1) / 2;
            var grid = L.Top(rows * BH + (rows - 1) * 6f);
            for (int i = 0; i < tools.Count; i++)
                if (IconButton(p, UiLayout.Cell(grid, 2, i, BH, 6f), tools[i].Icon, Olive, tools[i].Label, UiRole.Primary, tools[i].Sub)) tools[i].Go();
        }

        /// <summary>DEFENSE under the original v0.10.8a rules (main.gd FENCES / gates / TOWERS / TRAPS).</summary>
        public static void FencePanel(PanelHost H, Action close, IList<DefTool> tools)
        {
            var g = H.G; var p = H.P;
            var c = DockFrame(p, H.W, H.H, "DEFENSE", out bool shut);
            if (shut) { close(); return; }
            var L = new UiLayout(c, GAP);
            var ri = g.RaidInfo();
            string rl = ri == null ? "Hordes won't come for your camps until the city has fallen; until then the dead probe your fences at night." : ri.On ? string.Format("Under attack: {0} zombies.", ri.Spawned) : ri.Warned ? string.Format("Attack tonight: about {0}.", ri.N) : "Fence your camp in before the city falls.";
            p.Paragraph(L.Top(Mathf.Min(64f, p.ParaH(rl, 15, c.width))), rl, 15, Amber);
            ToolGrid(p, L, tools);
            var mb = g.MainBase();
            int fences = 0, gates = 0;
            foreach (var kv in g.S.Fences) if (kv.Value.Col == g.CurCol) { fences++; if (g.IsGate(kv.Key)) gates++; }
            int towers = 0; foreach (var kv in g.S.Towers) if (kv.Value.Col == g.CurCol) towers++;
            var c1 = UiLayout.Columns(L.Top(32f), 6f, 1f, 1f);
            var c2 = UiLayout.Columns(L.Top(32f), 6f, 1f, 1f);
            p.Chip(c1[0], fences + " FENCE TILES", UiRole.Primary);
            p.Chip(c1[1], gates + (gates == 1 ? " GATE" : " GATES"), UiRole.Neutral);
            p.Chip(c2[0], towers + (towers == 1 ? " TOWER" : " TOWERS"), UiRole.Neutral);
            p.Chip(c2[1], g.TrapsOf() + " / " + Data.TRAP_MAX + " TRAPS", UiRole.Neutral);
            if (mb != null && g.IsEnclosed(mb)) p.Fit(L.Top(22f), "CAMP FENCED IN", 15, MenuAlign.Left, Good, true, 10);
            p.Paragraph(L.Rest, "Fences stop everyone: survivors, squads and vehicles only get in and out through a GATE, the dead have to break through. Draw from corner post to corner post along the curbs, close the loop, then put a gate in it. Repairs are automatic, gates first. Tap a fence to upgrade it, make it a gate or remove it.", 15, Dim);
        }

        // ---------------- wall / fence / tower sheets ----------------
        public static void WallSheet(PanelHost H, Rect area, WallRun run, Action close)
        {
            var g = H.G; var p = H.P;
            int tiles = 0, dmg = 0, br = 0;
            foreach (int k in g.RunTiles(run))
            {
                if (g.S.Fences.TryGetValue(k, out var f)) { tiles++; if (f.Hp < g.FenceTileMax(k) * 0.75) dmg++; }
                else if (g.S.Breaches.ContainsKey(k)) { tiles++; br++; }
            }
            var up = g.WallUpgradePlan(run);
            bool errLine = up.err != "" && up.next >= 0;
            var r = p.Block(Docks.Sheet(area, 820f, FrameExtra + 26f + (errLine ? 24f : 0f) + BH + 8f + GAP * 2f));
            var c = Frame(p, r, (run.Starter ? "CAMP " : "") + Data.FENCES[run.Tier].Name.ToUpperInvariant(), "CLOSE", UiRole.Danger, 120f, out bool shut);
            if (shut) { close(); return; }
            var L = new UiLayout(c, GAP);
            p.Fit(L.Top(26f), string.Format("{0} tiles - {1} damaged - {2} breach{3}", tiles, dmg, br, br == 1 ? "" : "es"), 17, MenuAlign.Left, br > 0 ? Warn : Ink, false);
            if (errLine) p.Fit(L.Top(24f), up.err, 15, MenuAlign.Left, Warn, false, 10);
            var row = L.Bottom(BH + 8f);
            var cols = Game.PhaseARules ? UiLayout.Columns(row, GAP, 1.5f, 1f, 1f) : UiLayout.Columns(row, GAP, 1.5f, 1f);
            bool canUp = up.err == "" && g.CanAfford(up.cost);
            if (Button2(p, cols[0], up.next >= 0 ? "UPGRADE TO " + Data.FENCES[up.next].Name.ToUpperInvariant() : "MAX LEVEL", up.next >= 0 ? Game.FmtCost(up.cost) : null, canUp ? UiRole.Accent : UiRole.Disabled) && canUp)
            { var e = H.Do("upgrade_wall", run.Id); H.Toast(e == "" ? "Wall upgraded." : e); H.Dirty?.Invoke(); }
            int ri = 1;
            if (Game.PhaseARules)
            {
                bool rep = dmg + br > 0;
                if (p.Button(cols[ri], "REPAIR NOW", rep ? UiRole.Primary : UiRole.Disabled) && rep) { var e = H.Do("repair_all"); H.Toast(e == "" ? "Crews patched the walls." : e); H.Dirty?.Invoke(); }
                ri++;
            }
            if (p.Button(cols[ri], "REMOVE", run.Starter ? UiRole.Disabled : UiRole.Danger) && !run.Starter)
            { var e = H.Do("remove_wall", run.Id); H.Toast(e == "" ? "Wall torn down (half the materials back)." : e); close(); H.Dirty?.Invoke(); }
        }

        /// <summary>main.gd _sheet_fence. startFenceTool: FENCE TOOL (sections not in a segment).</summary>
        public static void FenceSheet(PanelHost H, Rect area, int k, Action close, Action startFenceTool)
        {
            var g = H.G; var p = H.P; var D = Data.Instance;
            if (!g.S.Fences.TryGetValue(k, out var f)) { close(); return; }
            int tier = Mathf.Clamp(f.Tier, 0, Data.FENCES.Length - 1);
            var fd = Data.FENCES[tier];
            bool gate = g.IsGate(k);
            var pos = new Vector2Int(k % D.W, k / D.W);
            string info = string.Format("{0} - {1} / {2} hp. Survivors repair damage from the repair queue (gates first) while there are materials.", Game.StreetName(pos), (int)f.Hp, (int)g.FenceTileMax(k));
            bool mine = f.Col == g.CurCol;
            var probe = Docks.Sheet(area, 860f, 10f);
            float ph = Mathf.Min(48f, p.ParaH(info, 16, probe.width - 24f));
            var r = p.Block(Docks.Sheet(area, 860f, FrameExtra + ph + GAP + (mine ? BH + 8f : 26f)));
            var c = Frame(p, r, ((gate ? "Gate - " : "") + fd.Name).ToUpperInvariant(), "CLOSE", UiRole.Danger, 120f, out bool shut);
            if (shut) { close(); return; }
            var L = new UiLayout(c, GAP);
            p.Paragraph(L.Top(ph), info, 16, Dim);
            if (!mine) { p.Fit(L.Top(24f), "Built by another colony. It blocks zombies for everyone, but only they can change it.", 15, MenuAlign.Left, Amber, false, 10); return; }
            var row = L.Bottom(BH + 8f);
            int se = g.SegAtTile(k);
            int nt = tier + 1;
            bool upCol = se >= 0 && nt < Data.FENCES.Length;
            var cols = UiLayout.Columns(row, GAP, upCol ? new[] { 1f, 1.2f, 1f } : new[] { 1f, 1f });
            if (gate)
            {
                if (p.Button(cols[0], "REMOVE GATE", UiRole.Neutral)) { var e = H.Do("remove_gate", k); H.Toast(e == "" ? "Gate taken out." : e); H.Dirty?.Invoke(); }
            }
            else
            {
                string ge = g.CanGate(k);
                // v0.19.2: a tile that can't take a gate (corner, end of the run, diagonal step) snaps to the nearest one along
                // the fence that can (Game.SnapGate); when none can, the button says why
                int gk = k;
                if (ge != "") { int sk = g.SnapGate(new Vector2(pos.x + 0.5f, pos.y + 0.5f), 2.5f, out _); if (sk >= 0) gk = sk; }
                bool snapped = gk != k;
                bool on = (ge == "" || snapped) && g.CanAfford(Data.GATE_COST);
                string gl = ge == "" ? "MAKE IT A GATE" : snapped ? "GATE NEXT TO IT" : "GATE: " + ge;
                if (Button2(p, cols[0], gl, ge == "" || snapped ? Game.FmtCost(Data.GATE_COST) : null, on ? UiRole.Primary : UiRole.Disabled) && on)
                { var e = H.Do("place_gate", gk); H.Toast(e == "" ? (snapped ? "Gate built on the nearest straight stretch (" + ge.TrimEnd('.') + " here)." : "Gate built.") : e); H.Dirty?.Invoke(); }
            }
            if (se >= 0)
            {
                if (upCol)
                {
                    var cost = g.FenceCost(nt, g.EdgeTiles(se).Count);
                    bool un = g.FenceTierUnlocked(nt);
                    bool on = un && g.CanAfford(cost);
                    if (Button2(p, cols[1], un ? "UPGRADE TO " + Data.FENCES[nt].Name.ToUpperInvariant() : Data.FENCES[nt].Name.ToUpperInvariant() + " NEEDS", un ? Game.FmtCost(cost) : g.UnlockSourceName(Data.FENCES[nt].Unlock), on ? UiRole.Accent : UiRole.Disabled) && on)
                    { var e = H.Do("build_fence_segs", new List<int> { se }, nt); H.Toast(e == "" ? "Section upgraded." : e); H.Dirty?.Invoke(); }
                }
                if (p.Button(cols[cols.Length - 1], "REMOVE SECTION (50%)", UiRole.Danger, false, 18)) { var e = H.Do("remove_fence_seg", se); H.Toast(e == "" ? "Section torn down (half the materials back)." : e); close(); H.Dirty?.Invoke(); }
            }
            else
            {
                var two = UiLayout.Columns(cols[1], GAP, 1f, 1f);
                if (p.Button(two[0], "FENCE TOOL", UiRole.Primary)) { close(); startFenceTool(); }
                if (p.Button(two[1], "REMOVE (50%)", UiRole.Danger, false, 18)) { var e = H.Do("remove_fence", new List<Vector2Int> { pos }); H.Toast(e == "" ? "Fence torn down (half the materials back)." : e); close(); H.Dirty?.Invoke(); }
            }
        }

        public static void TowerSheet(PanelHost H, Rect area, int k, Action close)
        {
            var g = H.G; var p = H.P;
            if (!g.S.Towers.TryGetValue(k, out var t)) { close(); return; }
            var td = Data.TOWERS[Mathf.Clamp(t.Tier, 0, Data.TOWERS.Length - 1)];
            string info = string.Format("{0} / {1} hp - range {2:0} tiles - {3}", (int)t.Hp, (int)td.Hp, td.Range + g.Rx("tower_range"), td.Desc);
            bool mine = t.Col == g.CurCol;
            var probe = Docks.Sheet(area, 820f, 10f);
            float ph = Mathf.Min(48f, p.ParaH(info, 16, probe.width - 24f));
            var r = p.Block(Docks.Sheet(area, 820f, FrameExtra + ph + GAP + (mine ? BH + 8f : 26f)));
            var c = Frame(p, r, td.Name.ToUpperInvariant(), "CLOSE", UiRole.Danger, 120f, out bool shut);
            if (shut) { close(); return; }
            var L = new UiLayout(c, GAP);
            p.Paragraph(L.Top(ph), info, 16, Dim);
            if (!mine) { p.Fit(L.Top(24f), "Belongs to another colony.", 15, MenuAlign.Left, Amber, false, 10); return; }
            var cols = UiLayout.Columns(L.Bottom(BH + 8f), GAP, 1.6f, 1f);
            int nt = t.Tier + 1;
            if (nt < Data.TOWERS.Length)
            {
                var cost = g.TowerCost(nt);
                bool un = g.TowerTierUnlocked(nt);
                bool on = un && g.CanAfford(cost);
                if (Button2(p, cols[0], un ? "UPGRADE TO " + Data.TOWERS[nt].Name.ToUpperInvariant() : Data.TOWERS[nt].Name.ToUpperInvariant() + " NEEDS", un ? Game.FmtCost(cost) : g.UnlockSourceName(Data.TOWERS[nt].Unlock), on ? UiRole.Accent : UiRole.Disabled) && on)
                { var e = H.Do("upgrade_tower", k); H.Toast(e == "" ? "Tower upgraded." : e); H.Dirty?.Invoke(); }
            }
            if (p.Button(cols[1], "REMOVE", UiRole.Danger)) { var e = H.Do("remove_tower", k); H.Toast(e == "" ? "Tower taken down." : e); close(); H.Dirty?.Invoke(); }
        }

        // ---------------- SideMenus display lists on the kit ----------------
        static bool IsPanelFill(Color c) => c.a >= 0.9f && c.r < 0.2f && c.g < 0.2f && c.b < 0.2f;

        /// <summary>Bounding box of a display list's panels (the sheet itself), for docking.</summary>
        public static Rect Bounds(List<MenuItem> items)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (var it in items)
            {
                if (it.Kind != MenuKind.Box || !IsPanelFill(it.Fill)) continue;
                x0 = Mathf.Min(x0, it.R.x); y0 = Mathf.Min(y0, it.R.y); x1 = Mathf.Max(x1, it.R.xMax); y1 = Mathf.Max(y1, it.R.yMax);
            }
            return x0 > x1 ? new Rect(0f, 0f, 0f, 0f) : new Rect(x0, y0, x1 - x0, y1 - y0);
        }

        static UiRole Role(MenuItem it)
        {
            if (!it.On) return UiRole.Disabled;
            string t = it.Text ?? "";
            if (t == "CLOSE") return UiRole.Danger;
            switch (it.Style)
            {
                case "amber": case "sel": return UiRole.Accent;
                case "dark": case "brown": return UiRole.Neutral;
                default: return UiRole.Primary;
            }
        }

        /// <summary>Paints a SideMenus display list with the kit: dark panels become textured plates with a header
        /// strip under their title, buttons become kit buttons (CLOSE rust, selected tabs amber, off = grey), one-line
        /// labels fit instead of clipping, bars sit in recessed wells. Clicks run the items' own callbacks.</summary>
        public static void RenderItems(IKitPaint p, List<MenuItem> items)
        {
            var plates = new List<Rect>();
            foreach (var it in items)
            {
                switch (it.Kind)
                {
                    case MenuKind.Box:
                        if (IsPanelFill(it.Fill) && it.R.width >= 120f && it.R.height >= 60f) { p.Plate(it.R); plates.Add(it.R); }
                        else if (it.R.height <= 4f && plates.Count > 0 && Mathf.Abs(it.R.y - plates[plates.Count - 1].y) < 1f) { }   // accent rule on a plate: the plate has its own
                        else p.Fill(it.R, it.Fill);
                        break;
                    case MenuKind.Bar:
                        {
                            var role = it.Col.r > 0.8f && it.Col.g < 0.6f ? UiRole.Danger : it.Col.g > it.Col.r ? UiRole.Primary : UiRole.Accent;
                            var r = it.R.height < 14f ? new Rect(it.R.x, it.R.y - (14f - it.R.height) * 0.5f, it.R.width, 14f) : it.R;
                            p.Bar(r, it.Frac, role, null);
                            break;
                        }
                    case MenuKind.Label:
                        {
                            // a panel title (big amber / coloured bold line at the top of a plate) sits on a header strip
                            Rect pl = default; bool title = false;
                            if (it.Bold && !it.Wrap && it.Size >= 20)
                                foreach (var q in plates) if (it.R.y - q.y < 24f && it.R.y >= q.y && it.R.x >= q.x && it.R.x < q.x + 40f) { pl = q; title = true; }
                            if (title)
                            {
                                var hr = new Rect(pl.x + 4f, pl.y + 4f, Mathf.Min(it.R.xMax + 6f, pl.xMax - 4f) - pl.x - 4f, Mathf.Max(HDR, it.R.yMax - pl.y + 2f));
                                p.Header(hr, "", 22);
                                p.Fit(new Rect(hr.x + 14f, hr.y + 2f, hr.width - 24f, hr.height - 6f), (it.Text ?? "").ToUpperInvariant(), Mathf.Min(it.Size, 24), MenuAlign.Left, it.Col, true, 12);
                            }
                            else if (!it.Wrap || (it.Size * 1.4f > it.R.height && (it.Text ?? "").IndexOf('\n') < 0))
                                p.Fit(it.R, it.Text, it.Size, it.Align, it.Col, it.Bold, Mathf.Min(11, it.Size));
                            else p.Label(it.R, it.Text, it.Size, it.Bold, it.Align, true, it.Col);
                            break;
                        }
                    case MenuKind.Button:
                        {
                            string t = it.Text ?? "";
                            var role = Role(it);
                            bool sel = it.Style == "sel";
                            bool hit;
                            int nl = t.IndexOf('\n');
                            if (nl >= 0) hit = Button2(p, it.R, t.Substring(0, nl), t.Substring(nl + 1).Replace('\n', ' '), role, sel);
                            else hit = p.Button(it.R, t, role, sel, Mathf.Max(it.Size, 14));
                            if (hit && it.On) it.Click?.Invoke();
                            break;
                        }
                }
            }
        }
    }
}
