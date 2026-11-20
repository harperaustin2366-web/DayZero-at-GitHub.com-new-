// RESEARCH and TRADE side menus, ported from the Godot original (main.gd _show_research, _show_trade_list,
// _render_trade_rows, _trade_set, _show_barter), plus the display list every side menu is laid out into.
// Pure C#: each menu is laid out into a flat display list (kit panels, headers, fitted labels, paragraphs,
// role buttons, chips, bars, row plates, scroll lists and UiWheel pickers) that SideMenus.Paint draws with the
// v0.17 UI kit (UiKit / UiWheel) and the offline preview paints to PNG, so the previews show the exact layout
// the phone gets. Every button sends the same player action as the original (start_research, set_trade,
// camp_trade) through Bootstrap.Act -> Game.DoAction; no rules live here.
// v0.17: lists scroll (no Prev / Next pages), amounts and cycles are UiWheel drums (no < > arrows), labels fit on
// one line (FitLabel), buttons carry a role (Primary olive, Accent amber main action, Danger rust, Neutral).
using System;
using System.Collections.Generic;
using System.Linq;
using DayZero.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DayZero.Runtime
{
    // Box / Label / Button / Bar are the original kinds (GameUI.DrawItems still knows them); the rest are kit items
    // that only SideMenus.Paint (and the preview) draw. Every side-menu entry point paints and clears its own list.
    public enum MenuKind { Box, Label, Button, Bar, Dim, Panel, Header, Chip, Plate, Divider, Wheel, Scroll, EndScroll }
    public enum MenuAlign { Left, Center, Right }

    public sealed class MenuItem
    {
        public MenuKind Kind;
        public Rect R;
        public string Text = "";
        public string Sub;               // buttons: second (smaller) line, e.g. a cost
        public int Size = 16, Min = 11;
        public bool Bold, Wrap = true, On = true, Sel;
        public MenuAlign Align;
        public Color Col, Fill;
        public string Style = "olive";   // legacy buttons: olive | amber | dark | brown | sel
        public UiRole Role = UiRole.Primary;
        public float Frac;               // bars
        public Action Click;
        // wheels / scroll lists
        public string Id;
        public IList<string> List;
        public int Index, Visible = 3;
        public bool Hz;
        public Action<int> Pick;
        public float ContentH, Jump = -1f;
    }

    /// <summary>What the side menus remember between frames (open menu, tree, tab, barter camp).</summary>
    public sealed partial class SideMenuState
    {
        public string Open = "";         // "" | "research" | "trade" | "squads" | "taskforce" | ...
        public string Cat = "farming";
        public int RPage;
        public bool RJump = true;        // scroll to the first unfinished project of the tree on next layout
        public string Tab = "goods";     // goods | gear | camps
        public int TPage, CPage, BPage;
        public string Barter = "";       // camp id while the barter grid is shown
        // SQUADS / TASK FORCE (main.gd sq_camp, _form_pick)
        public int Camp = -1;            // camp the squad / task force screens are about (-1 = main camp)
        public string Sub = "";          // SQUADS sub-screen: "" | "form" | "map"
        public int SqPage, TfPage, FPage, MapSid = -1;
        public readonly HashSet<int> Pick = new HashSet<int>();
        public readonly ArmoryMenuState Arm = new ArmoryMenuState();   // ARMORY / GARAGE (SideMenus.Armory.cs)
    }

    public sealed partial class MenuCtx
    {
        public Game G;
        public float W, H;
        /// <summary>Union of the kit plates painted by Flush (for map-tap blocking).</summary>
        public Rect Painted;
        public SideMenuState St;
        /// <summary>Height of text wrapped to a width: (text, font size, bold, width) -> height.</summary>
        public Func<string, int, bool, float, float> Measure;
        public Func<string, object[], string> Act;
        public Action<string> Toast;
        /// <summary>Hand over to the map to pick a target: (clear | scav | drone | strike, squad id).</summary>
        public Action<string, int> Target;
        /// <summary>Close the menu, fly to a survivor group and open its SOS sheet.</summary>
        public Action<int> ViewGroup;
        public readonly List<MenuItem> Items = new List<MenuItem>();

        MenuItem Add(MenuItem it) { Items.Add(it); return it; }

        // ---- original kinds ----
        public void Box(Rect r, Color fill) => Add(new MenuItem { Kind = MenuKind.Box, R = r, Fill = fill });
        /// <summary>wrap = paragraph (word wrapped), otherwise a one-line fitted label (shrinks, then "...").</summary>
        public void Label(Rect r, string t, int size, Color col, bool bold = false, bool wrap = true, MenuAlign al = MenuAlign.Left)
            => Add(new MenuItem { Kind = MenuKind.Label, R = r, Text = t, Size = size, Col = col, Bold = bold, Wrap = wrap, Align = al, Min = Math.Min(11, size) });
        /// <summary>Legacy styled button: the style maps onto a kit role.</summary>
        public void Button(Rect r, string t, string style, int size, Action click, bool on = true)
            => Add(new MenuItem { Kind = MenuKind.Button, R = r, Text = t, Style = style, Size = size, Click = click, On = on, Role = StyleRole(style) });
        public void Bar(Rect r, float frac, Color col) => Add(new MenuItem { Kind = MenuKind.Bar, R = r, Frac = Clamp01(frac), Col = col, Role = ColRole(col) });
        public float H_(string t, int size, bool bold, float w) => string.IsNullOrEmpty(t) ? 0f : (float)Math.Ceiling(Measure(t, size, bold, w));

        // ---- kit kinds ----
        public void Dim() => Add(new MenuItem { Kind = MenuKind.Dim, R = new Rect(0, 0, W, H) });
        /// <summary>Kit plate (+ header strip when titled). Returns the content rect (UiKit.Panel's padding).</summary>
        public Rect Panel(Rect r, string title = null)
        {
            Add(new MenuItem { Kind = MenuKind.Panel, R = r, Text = title });
            return string.IsNullOrEmpty(title) ? UiLayout.Inset(r, 12f) : UiLayout.Inset(r, 12f, 52f, 12f, 12f);
        }
        public void Header(Rect r, string t, int size = 22) => Add(new MenuItem { Kind = MenuKind.Header, R = r, Text = t, Size = size });
        /// <summary>One-line fitted label (never wraps, never spills).</summary>
        public void Fit(Rect r, string t, int size, Color col, bool bold = true, MenuAlign al = MenuAlign.Left, int min = 11)
            => Add(new MenuItem { Kind = MenuKind.Label, R = r, Text = t, Size = size, Col = col, Bold = bold, Wrap = false, Align = al, Min = Math.Min(min, size) });
        /// <summary>Word-wrapped body text (measure its height with H_ first).</summary>
        public void Para(Rect r, string t, int size, Color col) => Add(new MenuItem { Kind = MenuKind.Label, R = r, Text = t, Size = size, Col = col, Wrap = true });
        /// <summary>Kit button. sub = a smaller second line (a cost); off = drawn grey, no taps.</summary>
        public void Btn(Rect r, string t, UiRole role, Action click, bool on = true, bool sel = false, int size = 20, string sub = null)
            => Add(new MenuItem { Kind = MenuKind.Button, R = r, Text = t, Sub = sub, Role = role, Click = click, On = on, Sel = sel, Size = size, Style = role == UiRole.Accent ? "sel" : role == UiRole.Danger ? "brown" : role == UiRole.Neutral ? "dark" : "olive" });
        public void Chip(Rect r, string t, UiRole role = UiRole.Neutral, int size = 16) => Add(new MenuItem { Kind = MenuKind.Chip, R = r, Text = t, Role = role, Size = size });
        public void Plate(Rect r, int index, bool hi = false) => Add(new MenuItem { Kind = MenuKind.Plate, R = r, Index = index, Sel = hi });
        public void Divider(Rect r) => Add(new MenuItem { Kind = MenuKind.Divider, R = r });
        public void KBar(Rect r, float frac, UiRole role, string text = null) => Add(new MenuItem { Kind = MenuKind.Bar, R = r, Frac = Clamp01(frac), Role = role, Text = text, Col = new Color(0, 0, 0, 0) });
        /// <summary>UiWheel drum picker. pick(i) runs once when the drum lands on another item; stable id per row.</summary>
        public void Wheel(string id, Rect r, IList<string> items, int sel, Action<int> pick, bool hz, bool on = true, int visible = 3, int size = 20)
            => Add(new MenuItem { Kind = MenuKind.Wheel, Id = id, R = r, List = items, Index = sel, Pick = pick, Hz = hz, On = on, Visible = visible, Size = size });
        /// <summary>Touch-scrolled clipped list (UiKit.BeginScroll): items until EndScroll use content coordinates
        /// (x 0..view.width-12, y 0..contentH). jump &gt;= 0 scrolls there (e.g. the first unfinished project).</summary>
        public float Scroll(string id, Rect view, float contentH, float jump = -1f)
        {
            Add(new MenuItem { Kind = MenuKind.Scroll, Id = id, R = view, ContentH = contentH, Jump = jump });
            return view.width - 12f;
        }
        public void EndScroll() => Add(new MenuItem { Kind = MenuKind.EndScroll });

        static float Clamp01(float f) => Math.Max(0f, Math.Min(1f, f));
        public static UiRole StyleRole(string s) => s == "sel" || s == "amber" ? UiRole.Accent : s == "brown" ? UiRole.Danger : s == "dark" ? UiRole.Neutral : UiRole.Primary;
        static UiRole ColRole(Color c) => c.r > 0.85f && c.g < 0.6f ? UiRole.Danger : c.g > c.r ? UiRole.Primary : UiRole.Accent;
    }

    public static partial class SideMenus
    {
        public static readonly Color INK = new Color(0.93f, 0.92f, 0.81f);
        public static readonly Color INK2 = new Color(0.82f, 0.78f, 0.68f);
        public static readonly Color MUTE = new Color(0.60f, 0.58f, 0.50f);
        public static readonly Color AMBER = new Color(0.96f, 0.72f, 0.30f);
        public static readonly Color GREEN = new Color(0.62f, 0.84f, 0.42f);
        public static readonly Color RED = new Color(0.98f, 0.50f, 0.40f);
        static readonly Color DIM = new Color(0f, 0f, 0f, 0.55f);
        static readonly Color PANEL = new Color(0.07f, 0.08f, 0.06f, 0.97f);

        // shared kit metrics (720 px virtual height): touch targets 52-56 px, row gap 8
        public const float BTN_H = 56f, ROW_GAP = 8f, TOP_H = 56f, FOOT_H = 56f;

        // ---------------- ACTIONS panel (right side) ----------------
        /// <summary>ACTIONS buttons in the original's order (main.gd:258: BUILD OUTPOST, FENCES, TOWERS, TRAPS, SQUADS,
        /// WAR, TASK FORCE, CREW & JOBS, CAMPS, TRADE, ARMORY, GARAGE, RESEARCH, LOG), limited to the menus the port
        /// has: DEFENSE stands in for FENCES / TOWERS / TRAPS, so SQUADS and TASK FORCE follow it. Shared by GameUI
        /// and the preview.</summary>
        public static readonly (string label, string style)[] ACTIONS =
        {
            ("BUILD OUTPOST", "olive"), ("BUILD", "olive"), ("CAMPS", "sel"), ("GATHER", "brown"), ("DEFENSE", "olive"), ("SQUADS", "olive"), ("WAR", "olive"), ("TASK FORCE", "olive"),
            ("CREW & JOBS", "olive"), ("COLONY", "olive"), ("TRADE", "olive"), ("ARMORY", "olive"), ("GARAGE", "olive"), ("RESEARCH", "olive"), ("LOG", "olive"),
        };
        public const float ACT_W = 200f, ACT_Y = 108f, ACT_HDR = 34f, ACT_BH = 44f, ACT_GAP = 5f;
        /// <summary>One column while every 44 px button fits above y = 712 on a 720 px screen; past that (the original has 15)
        /// two columns, read top to bottom then left to right, so touch targets never shrink below 44 px.</summary>
        static int ActCols => ACT_Y + ACT_HDR + 10f + ACTIONS.Length * (ACT_BH + ACT_GAP) - ACT_GAP <= 712f ? 1 : 2;
        static int ActRows => (ACTIONS.Length + ActCols - 1) / ActCols;
        const float ACT_CW2 = 168f;   // column width with two columns
        public static Rect ActionsPanel(float w)
        {
            float pw = ActCols == 1 ? ACT_W : 20f + 2f * ACT_CW2 + 8f, ph = ACT_HDR + ActRows * (ACT_BH + ACT_GAP) - ACT_GAP + 10;
            // the wider two-column panel sits on the bottom edge (y 712), below the centred banners / research note
            return new Rect(w - pw - 8, ActCols == 1 ? ACT_Y : 712f - ph, pw, ph);
        }
        public static Rect ActionButton(float w, int i)
        {
            var p = ActionsPanel(w);
            float cw = ActCols == 1 ? ACT_W - 20 : ACT_CW2;
            int col = i / ActRows, row = i % ActRows;
            return new Rect(p.x + 10 + col * (cw + 8f), p.y + ACT_HDR + row * (ACT_BH + ACT_GAP), cw, ACT_BH);
        }

        /// <summary>"Research complete" note under the top bar (main.gd show_research_note), tap opens RESEARCH.</summary>
        public static Rect ResearchNoteRect(float w, float y) => new Rect(w / 2 - 350, y, 700, 50);

        public static void Open(SideMenuState st, string which)
        {
            st.Open = which;
            if (which == "research") st.RJump = true;
            if (which == "trade") st.Barter = "";
            if (which == "squads" || which == "taskforce") { st.Sub = ""; st.Pick.Clear(); }
            if (which == "armory" || which == "garage") st.Arm.PickPid = -1;
        }

        public static void Build(MenuCtx c)
        {
            if (c.G?.S == null || c.G.C == null) return;
            if (SideMenus.CampMenus(c)) { }   // SideMenus.Camp.cs: camps, base (facilities / upgrade base), crew
            else if (c.St.Open == "research") Research(c);
            else if (c.St.Open == "trade") Trade(c);
            else if (c.St.Open == "squads") Squads(c);
            else if (c.St.Open == "taskforce") TaskForce(c);
            else if (c.St.Open == "war") War(c);
            else if (c.St.Open == "armory") Armory(c);
            else if (c.St.Open == "garage") Garage(c);
            Flush(c);
        }

        // ---------------- shared screen chrome ----------------
        /// <summary>A button in a screen's top bar or footer: label, role, action, width (0 = auto from the label).</summary>
        public struct BarBtn
        {
            public string T; public UiRole Role; public Action Click; public bool On; public float W;
            public BarBtn(string t, UiRole role, Action click, bool on = true, float w = 0f) { T = t; Role = role; Click = click; On = on; W = w; }
        }

        static float AutoW(string t, float min = 150f) => Math.Max(min, Math.Min(320f, 26f + t.Length * 13.5f));

        /// <summary>Full-screen side menu: dim, kit plate, a top bar (header strip, extra buttons, BACK, CLOSE). Returns the
        /// layout of the space under the top bar, which the screen docks its parts off (so nothing can overlap).</summary>
        static UiLayout Frame(MenuCtx c, string title, Action back = null, params BarBtn[] extra)
        {
            c.Dim();
            float pw = Math.Min(1320f, c.W - 16f);
            var p = new Rect((c.W - pw) / 2f, 8f, pw, c.H - 16f);
            var inner = c.Panel(p);
            var L = new UiLayout(inner, 10f);
            var T = new UiLayout(L.Top(TOP_H), 10f);
            c.Btn(T.Right(150f), "CLOSE", UiRole.Danger, () => { c.St.Open = ""; c.St.Barter = ""; c.St.Sub = ""; });
            if (back != null) c.Btn(T.Right(150f), "BACK", UiRole.Neutral, back);
            foreach (var b in extra) c.Btn(T.Right(b.W > 0f ? b.W : AutoW(b.T)), b.T, b.Role, b.Click, b.On);
            var hr = T.Rest;
            c.Header(new Rect(hr.x, hr.y + 6f, hr.width, 44f), title, 24);
            return L;
        }

        /// <summary>Footer strip: buttons on the right, a fitted hint on the left.</summary>
        static void Footer(MenuCtx c, UiLayout L, string hint, params BarBtn[] btns)
        {
            if (string.IsNullOrEmpty(hint) && btns.Length == 0) return;
            var F = new UiLayout(L.Bottom(btns.Length > 0 ? FOOT_H : 30f), 10f);
            foreach (var b in btns) c.Btn(F.Right(b.W > 0f ? b.W : AutoW(b.T)), b.T, b.Role, b.Click, b.On);
            if (!string.IsNullOrEmpty(hint)) c.Fit(F.Rest, hint, 16, INK2, false, MenuAlign.Left, 11);
        }

        /// <summary>Row backing: kit row plate plus a 4 px state stripe on the left.</summary>
        static void Card(MenuCtx c, Rect r, int i, Color stripe, bool hi = false)
        {
            c.Plate(r, i, hi);
            if (stripe.a > 0f) c.Box(new Rect(r.x + 3f, r.y + 4f, 4f, r.height - 8f), stripe);
        }

        /// <summary>Sum of row heights plus gaps (content height of a scroll list).</summary>
        static float Total(IList<float> hs, float gap) { float t = 0f; for (int i = 0; i < hs.Count; i++) t += hs[i] + (i > 0 ? gap : 0f); return t; }

        static readonly Dictionary<string, List<string>> _lists = new Dictionary<string, List<string>>();
        /// <summary>Cached wheel labels for 0..max in steps (plus an off-step current value).</summary>
        static List<string> Steps(int step, int max, int cur, out List<int> vals)
        {
            vals = new List<int>();
            for (int v = 0; v <= max; v += Math.Max(1, step)) vals.Add(v);
            if (!vals.Contains(cur)) { vals.Add(cur); vals.Sort(); }
            string k = step + ":" + max + ":" + cur;
            if (!_lists.TryGetValue(k, out var l))
            {
                if (_lists.Count > 256) _lists.Clear();
                l = vals.Select(v => v.ToString()).ToList();
                _lists[k] = l;
            }
            return l;
        }

        // ---------------- painting (game only; the preview paints the same list to PNG) ----------------
        /// <summary>Draw the display list with the UI kit and clear it (GameUI.DrawItems then has nothing left).</summary>
        public static void Flush(MenuCtx c)
        {
            // bounds of every kit plate, so the host can block map taps under the sheet after the list is cleared
            foreach (var it in c.Items)
            {
                if (it.Kind != MenuKind.Panel) continue;
                c.Painted = c.Painted.width <= 0f ? it.R : Rect.MinMaxRect(Math.Min(c.Painted.xMin, it.R.xMin), Math.Min(c.Painted.yMin, it.R.yMin), Math.Max(c.Painted.xMax, it.R.xMax), Math.Max(c.Painted.yMax, it.R.yMax));
            }
#if !DZ_PREVIEW
            Paint(c.Items);
            c.Items.Clear();
#endif
        }

#if !DZ_PREVIEW
        static Texture2D _white;
        static GUIStyle _para;
        static readonly GUIContent _gc = new GUIContent();

        static void Paint(List<MenuItem> items)
        {
            if (_white == null) { _white = new Texture2D(1, 1); _white.SetPixel(0, 0, Color.white); _white.Apply(); }
            if (_para == null) _para = new GUIStyle(GUI.skin.label) { wordWrap = true, richText = false, clipping = TextClipping.Clip, padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0) };
            var oc = GUI.color;
            int depth = 0;
            int n = items.Count;
            for (int i = 0; i < n && i < items.Count; i++)
            {
                var it = items[i];
                switch (it.Kind)
                {
                    case MenuKind.Dim:
                        if (Event.current.type == EventType.Repaint) { GUI.color = new Color(0f, 0f, 0f, 0.5f * oc.a); GUI.DrawTexture(it.R, _white); GUI.color = oc; }
                        break;
                    case MenuKind.Box:
                        if (Event.current.type == EventType.Repaint) { GUI.color = new Color(it.Fill.r, it.Fill.g, it.Fill.b, it.Fill.a * oc.a); GUI.DrawTexture(it.R, _white); GUI.color = oc; }
                        break;
                    case MenuKind.Panel: UiKit.Panel(it.R, it.Text); break;
                    case MenuKind.Header: UiKit.Header(it.R, it.Text, it.Size); break;
                    case MenuKind.Chip: UiKit.Chip(it.R, it.Text, it.Role, it.Size); break;
                    case MenuKind.Plate: UiKit.RowPlate(it.R, it.Index, it.Sel); break;
                    case MenuKind.Divider: UiKit.Divider(it.R); break;
                    case MenuKind.Bar: UiKit.Bar(it.R, it.Frac, it.Role, it.Text); break;
                    case MenuKind.Label:
                        if (!it.Wrap) UiKit.FitLabel(it.R, it.Text, it.Size, Anchor(it.Align, false), it.Col, it.Bold, it.Min);
                        else if (Event.current.type == EventType.Repaint && !string.IsNullOrEmpty(it.Text))
                        {
                            _para.fontSize = it.Size; _para.fontStyle = it.Bold ? FontStyle.Bold : FontStyle.Normal; _para.alignment = Anchor(it.Align, true);
                            _para.normal.textColor = it.Col; _gc.text = it.Text;
                            GUI.Label(it.R, _gc, _para);
                        }
                        break;
                    case MenuKind.Button:
                        {
                            var role = it.On ? it.Role : UiRole.Disabled;
                            bool hit = UiKit.Button(it.R, it.Sub != null ? "" : it.Text, role, it.Sel, it.Size);
                            if (it.Sub != null)
                            {
                                var f = UiKit.ButtonFace(it.R, false);
                                var col = it.On ? UiKit.Ink : UiKit.InkMute;
                                UiKit.FitLabel(new Rect(f.x + 4f, f.y + 3f, f.width - 8f, f.height * 0.56f - 3f), it.Text, it.Size, TextAnchor.MiddleCenter, col, true, 11, it.On);
                                UiKit.FitLabel(new Rect(f.x + 4f, f.y + f.height * 0.56f - 2f, f.width - 8f, f.height * 0.44f - 1f), it.Sub, Math.Max(12, it.Size - 5), TextAnchor.MiddleCenter, it.On ? UiKit.InkDim : UiKit.InkMute, false, 10, it.On);
                            }
                            if (hit && it.On) it.Click?.Invoke();
                            break;
                        }
                    case MenuKind.Wheel:
                        {
                            bool en = GUI.enabled; GUI.enabled = en && it.On;
                            int r = it.Hz ? UiWheel.Horizontal(it.Id, it.R, it.List, it.Index, it.Visible, it.Size) : UiWheel.Vertical(it.Id, it.R, it.List, it.Index, it.Visible, it.Size);
                            GUI.enabled = en;
                            if (r >= 0 && r != it.Index && it.On) it.Pick?.Invoke(r);
                            break;
                        }
                    case MenuKind.Scroll:
                        if (it.Jump >= 0f) UiKit.ScrollTo(it.Id, Math.Max(0f, Math.Min(it.Jump, it.ContentH - it.R.height)));
                        UiKit.BeginScroll(it.Id, it.R, it.ContentH); depth++;
                        break;
                    case MenuKind.EndScroll:
                        if (depth > 0) { UiKit.EndScroll(); depth--; }
                        break;
                }
            }
            while (depth-- > 0) UiKit.EndScroll();
            GUI.color = oc;
        }

        static TextAnchor Anchor(MenuAlign a, bool upper) => a == MenuAlign.Center ? (upper ? TextAnchor.UpperCenter : TextAnchor.MiddleCenter)
            : a == MenuAlign.Right ? (upper ? TextAnchor.UpperRight : TextAnchor.MiddleRight) : (upper ? TextAnchor.UpperLeft : TextAnchor.MiddleLeft);
#endif

        static string CatName(string cat) => (string)Data.Instance.ResearchCatsName[cat] ?? cat;

        // ---------------- RESEARCH (main.gd _show_research) ----------------
        static void Research(MenuCtx c)
        {
            var g = c.G; var D = Data.Instance; var st = c.St;
            if (!D.ResearchByCat.ContainsKey(st.Cat)) st.Cat = D.ResearchCats[0];
            var L = Frame(c, "RESEARCH");
            var rids = D.ResearchByCat[st.Cat];
            int done = rids.Count(g.HasResearch);
            Footer(c, L, string.Format("{0}: {1} of {2} done  ·  the cost is paid at START, progress is kept if you switch", CatName(st.Cat), done, rids.Count));

            // the trees: a column of tabs on the left
            var nav = L.Left(c.W >= 1500f ? 280f : 244f);
            int nc = D.ResearchCats.Count;
            var navH = Enumerable.Repeat(BTN_H, nc).ToList();
            float nw = c.Scroll("rs_nav", nav, Total(navH, 6f));
            for (int i = 0; i < nc; i++)
            {
                string cat = D.ResearchCats[i];
                var list = D.ResearchByCat[cat];
                int n = list.Count(g.HasResearch);
                string cc = cat;
                c.Btn(new Rect(0, i * (BTN_H + 6f), nw, BTN_H), string.Format("{0}  {1}/{2}", CatName(cat), n, list.Count), st.Cat == cat ? UiRole.Accent : UiRole.Primary,
                    () => { st.Cat = cc; st.RJump = true; }, true, st.Cat == cat, 19);
            }
            c.EndScroll();

            // current project + rate
            var R = new UiLayout(L.Rest, 10f);
            var sr = R.Top(68f);
            c.Plate(sr, 0, true);
            var sc = UiLayout.Columns(UiLayout.Inset(sr, 14f, 6f, 10f, 6f), 12f, 1f, -440f);
            var chips = UiLayout.Columns(UiLayout.Center(sc[1], sc[1].width, 34f), 8f, 1f, 1.1f, 1f);
            double rate = g.ResearchRate();
            int sci = g.CountJob("Scientist");
            c.Chip(chips[0], string.Format("{0} SCIENTIST{1}", sci, sci == 1 ? "" : "S"), sci > 0 ? UiRole.Primary : UiRole.Danger);
            c.Chip(chips[1], string.Format("{0:0.00} LAB-H / H", rate), UiRole.Neutral);
            c.Chip(chips[2], string.Format("POWER {0}", (int)g.Rx("power")), UiRole.Neutral);
            string cur = g.C.Research.Cur ?? "";
            var lines = UiLayout.Rows(sc[0], 4f, 1f, 1f);
            if (cur != "" && D.Research[cur] != null)
            {
                var r = D.Research[cur];
                double pr = Math.Max(0.0, g.ResearchProg(cur)), hrs = g.ResearchHours(cur);
                c.Fit(lines[0], string.Format("Researching: {0}  ({1} T{2})", (string)r["name"], CatName((string)r["cat"]), (int)(double)r["tier"]), 20, AMBER, true);
                c.KBar(lines[1], (float)(pr / Math.Max(0.001, hrs)), UiRole.Accent, string.Format("{0:0} / {1:0}h  ·  {2}", pr, hrs,
                    rate > 0.0 ? string.Format("done in ~{0:0}h", (hrs - pr) / rate) : "no Scientist at a camp!"));
            }
            else
            {
                c.Fit(lines[0], "No project running. Pick one below.", 20, AMBER, true);
                c.Fit(lines[1], "Scientists at a camp do the lab time; power speeds them up.", 16, INK2, false);
            }

            string note = st.Cat == "scouting" ? "Scouting: bigger squads, longer range, faster searching and a wider auto-scavenge radius. T1 unlocks squad scavenging runs."
                : st.Cat == "aviation" ? "Aviation is the endgame: every tier is slow and expensive and the first one needs Electricity T8, Vehicles T10 and Crafting T10." : "";
            if (note != "") { float nh = c.H_(note, 15, false, R.Rest.width); c.Para(R.Top(nh), note, 15, AMBER); }

            // project cards (one scroll list)
            var view = R.Rest;
            const float BTN_W = 160f, PAD = 10f;
            float cw = view.width - 12f, tw = cw - BTN_W - 44f;
            var hs = new List<float>(); var rows = new List<(string rid, string state, string meta, bool need, float dh, float mh)>();
            foreach (var rid in rids)
            {
                var r = D.Research[rid];
                string stt = g.ResearchState(rid);
                string meta = string.Format("{0} · {1:0}h lab time", Game.FmtCost(g.ResearchCost(rid)), g.ResearchHours(rid));
                var need = new List<string>();
                foreach (var q in (JArray)r["requires"])
                {
                    string qs = (string)q;
                    if (!g.HasResearch(qs)) { var t = D.Research[qs]; need.Add(string.Format("{0} T{1} {2}", CatName((string)t["cat"]), (int)(double)t["tier"], (string)t["name"])); }
                }
                if (need.Count > 0) meta += " · needs " + string.Join(", ", need);
                if (g.ResearchProg(rid) >= 0.0 && stt != "active") meta += string.Format(" · paid, {0:0}h done", g.ResearchProg(rid));
                float dh = c.H_(Game.RateText((string)r["desc"]), 15, false, tw), mh = c.H_(meta, 14, false, tw);
                rows.Add((rid, stt, meta, need.Count > 0, dh, mh));
                hs.Add(Math.Max(80f, PAD + 28f + dh + 4f + mh + PAD));
            }
            float jump = -1f;
            if (st.RJump)
            {
                st.RJump = false;
                int first = rows.FindIndex(q => q.state == "active");
                if (first < 0) first = rows.FindIndex(q => q.state != "done");
                jump = 0f;
                for (int i = 0; i < first; i++) jump += hs[i] + ROW_GAP;
            }
            c.Scroll("rs_" + st.Cat, view, Total(hs, ROW_GAP), jump);
            float y = 0f;
            for (int i = 0; i < rows.Count; i++)
            {
                var q = rows[i]; var r = D.Research[q.rid];
                var card = new Rect(0, y, cw, hs[i]);
                Color accent = q.state == "done" ? GREEN : q.state == "active" ? AMBER : q.state == "available" ? new Color(0.62f, 0.66f, 0.36f) : new Color(0.36f, 0.36f, 0.32f);
                Card(c, card, i, accent, q.state == "active");
                Color nc2 = q.state == "done" ? GREEN : q.state == "active" ? AMBER : q.state == "available" ? INK : INK2;
                float tx = card.x + 18f, ty = card.y + PAD - 2f;
                c.Fit(new Rect(tx, ty, tw, 28f), string.Format("T{0}  {1}", (int)(double)r["tier"], (string)r["name"]), 20, nc2, true); ty += 30f;
                c.Para(new Rect(tx, ty, tw, q.dh), Game.RateText((string)r["desc"]), 15, q.state != "locked" ? INK : INK2); ty += q.dh + 4f;
                c.Para(new Rect(tx, ty, tw, q.mh), q.meta, 14, q.need ? RED : INK2);
                var br = new Rect(card.xMax - BTN_W - 12f, card.y + (card.height - BTN_H) / 2f, BTN_W, BTN_H);
                var chip = new Rect(br.x, card.y + (card.height - 36f) / 2f, BTN_W, 36f);
                switch (q.state)
                {
                    case "done": c.Chip(chip, "DONE", UiRole.Primary, 18); break;
                    case "active": c.Chip(chip, string.Format("{0}%", (int)(100.0 * Math.Max(0.0, g.ResearchProg(q.rid)) / g.ResearchHours(q.rid))), UiRole.Accent, 20); break;
                    case "locked": c.Chip(chip, "LOCKED", UiRole.Disabled, 16); break;
                    default:
                        string e = g.CanResearch(q.rid), rid = q.rid;
                        // a blocked START stays tappable (Neutral): it toasts why, as the original does
                        c.Btn(br, g.ResearchProg(rid) >= 0.0 ? "SWITCH" : "START", e == "" ? UiRole.Accent : UiRole.Neutral, () =>
                        {
                            string e2 = c.Act("start_research", new object[] { rid });
                            if (e2 != "") c.Toast(e2);
                        }, true, false, 20);
                        break;
                }
                y += hs[i] + ROW_GAP;
            }
            c.EndScroll();
        }

        // ---------------- TRADE (main.gd _show_trade_list / _render_trade_rows / _show_barter) ----------------
        static readonly Dictionary<string, string> MODE_LABEL = new Dictionary<string, string> { ["import"] = "IMPORT", ["export"] = "EXPORT", ["ignore"] = "IGNORE" };
        static readonly string[] MODES = { "import", "export", "ignore" };
        public const int GEAR_WHEEL_MAX = 40;   // gear levels on the wheel (0..40, one piece at a time); a higher saved level is kept on it

        static void Trade(MenuCtx c)
        {
            var g = c.G; var st = c.St;
            if (st.Barter != "") { Barter(c); return; }
            var L = Frame(c, "TRADE");
            var t = g.TradeCfgOf();
            var partners = g.TradePartners();

            // tabs (the original lists goods, then gear, in one scroll; barter sits on each camp's sheet)
            var tabs = new[] { ("goods", "SUPPLIES"), ("gear", "GUNS, PARTS & ARMOUR"), ("camps", string.Format("PARTNER CAMPS ({0})", partners.Count)) };
            var tr = UiLayout.Columns(L.Top(BTN_H), 10f, 1f, 1.3f, 1.1f);
            for (int i = 0; i < tabs.Length; i++)
            {
                string id = tabs[i].Item1;
                c.Btn(tr[i], tabs[i].Item2, st.Tab == id ? UiRole.Accent : UiRole.Primary, () => { st.Tab = id; }, true, st.Tab == id, 19);
            }
            // credit and the next visit
            double nxt = Math.Max(0.0, t.Next - g.S.T);
            var info = new UiLayout(L.Top(36f), 10f);
            c.Chip(info.Left(220f), string.Format("TRADE CREDIT {0}", (int)t.Credit), UiRole.Accent, 17);
            c.Chip(info.Left(250f), partners.Count > 0 ? string.Format("{0} CAMP{1} TRADING", partners.Count, partners.Count == 1 ? "" : "S") : "NOBODY TRADING", partners.Count > 0 ? UiRole.Primary : UiRole.Danger, 17);
            if (partners.Count > 0) c.Chip(info.Left(220f), string.Format("NEXT VISIT {0:0}H", nxt), UiRole.Neutral, 17);

            if (st.Tab == "camps") { Camps(c, L); return; }
            bool gear = st.Tab == "gear";
            Footer(c, L, gear ? "One piece at a time, every 6 hours. Worn gear is never sold." : "Traders come every 6 hours: up to 6 lots of 10 per good.");
            var goods = gear ? Data.GEAR_TRADE : Data.RESOURCES;
            var view = L.Rest;
            float cw = view.width - 12f;
            const string intro = "Set each good once and the traders do the rest: IMPORT keeps your stock topped up to the level, EXPORT sells whatever is above it, IGNORE leaves it alone. Exports earn trade credit, imports spend it, at each camp's own prices. Drag a level wheel to set it.";
            float ih = c.H_(intro, 15, false, cw);
            string last = string.IsNullOrEmpty(t.Last) ? "" : "Last visit: " + t.Last;
            float lh = last != "" ? c.H_(last, 16, false, cw) + 6f : 0f;
            const float RH = 72f;
            float contentH = ih + 10f + lh + goods.Length * (RH + ROW_GAP) - ROW_GAP;
            c.Scroll("tr_" + st.Tab, view, contentH);
            c.Para(new Rect(0, 0, cw, ih), intro, 15, INK2);
            if (last != "") c.Para(new Rect(0, ih + 6f, cw, lh - 6f), last, 16, AMBER);
            float y = ih + 10f + lh;
            int k = 0;
            foreach (var r in goods)
            {
                var gd = t.Goods[r];
                string m = gd.M;
                var card = new Rect(0, y, cw, RH);
                Card(c, card, k++, m == "import" ? GREEN : m == "export" ? AMBER : new Color(0.36f, 0.36f, 0.32f), m != "ignore");
                int stock = gear ? g.GearTotal(r) : (int)g.C.Res.Get(r, 0.0);
                var cols = UiLayout.Columns(UiLayout.Inset(card, 18f, 8f, 10f, 8f), 10f, -200f, -3f * 108f - 12f, -250f, 1f);
                var nm = UiLayout.Rows(cols[0], 2f, 1.2f, 1f);
                c.Fit(nm[0], Game.GoodLabel(r), 19, INK, true);
                c.Fit(nm[1], stock + " in stock", 15, INK2, false);
                var mb = UiLayout.Columns(cols[1], 6f, 1f, 1f, 1f);
                for (int i = 0; i < MODES.Length; i++)
                {
                    string md = MODES[i], rr = r; int lv = gd.Lvl;
                    c.Btn(mb[i], MODE_LABEL[md], m == md ? (md == "export" ? UiRole.Accent : UiRole.Primary) : UiRole.Neutral, () => SetTrade(c, rr, md, lv), true, m == md, 17);
                }
                int step = gear ? 1 : Game.TRADE_STEP.TryGetValue(r, out var sv) ? sv : 10;
                var lvls = Steps(step, gear ? Math.Max(GEAR_WHEEL_MAX, gd.Lvl) : Game.TRADE_LVL_MAX, gd.Lvl, out var vals);
                string r2 = r, m2 = m;
                c.Wheel("trlv_" + r, cols[2], lvls, vals.IndexOf(gd.Lvl), i => SetTrade(c, r2, m2, vals[i]), true, true, 3, 20);
                string hint;
                if (m == "import")
                {
                    var q = gear ? g.GearQuote(r, false) : g.TradeQuote(r, false);
                    hint = q.Camp != null ? string.Format("keep {0} · buys at {1:0.0} from {2}", gd.Lvl, q.Price, q.Camp.Name) : string.Format("keep {0} · nobody sells", gd.Lvl);
                }
                else if (m == "export")
                {
                    var q2 = gear ? g.GearQuote(r, true) : g.TradeQuote(r, true);
                    hint = q2.Camp != null ? string.Format("sell above {0} · {1:0.0} each at {2}", gd.Lvl, q2.Price, q2.Camp.Name) : string.Format("sell above {0} · nobody buys", gd.Lvl);
                }
                else hint = "not traded";
                float hh = Math.Min(cols[3].height, c.H_(hint, 15, false, cols[3].width));
                c.Para(new Rect(cols[3].x, cols[3].y + (cols[3].height - hh) / 2f, cols[3].width, hh), hint, 15, INK2);
                y += RH + ROW_GAP;
            }
            c.EndScroll();
        }

        static void SetTrade(MenuCtx c, string r, string mode, int lvl)
        {
            lvl = Math.Max(0, Math.Min(Game.TRADE_LVL_MAX, lvl));
            string e = c.Act("set_trade", new object[] { r, mode, lvl });
            if (e != "") c.Toast(e);
        }

        static Color AttColor(string att)
        {
            try
            {
                var hex = (string)Data.Instance.Camps["attitudes"]?[att]?["color"];
                if (!string.IsNullOrEmpty(hex))
                {
                    hex = hex.TrimStart('#');
                    float r = Convert.ToInt32(hex.Substring(0, 2), 16) / 255f, gg = Convert.ToInt32(hex.Substring(2, 2), 16) / 255f, b = Convert.ToInt32(hex.Substring(4, 2), 16) / 255f;
                    return new Color(r + (1f - r) * 0.25f, gg + (1f - gg) * 0.25f, b + (1f - b) * 0.25f, 1f);   // lightened(0.25) as in main.gd
                }
            }
            catch { }
            return INK;
        }

        static void Camps(MenuCtx c, UiLayout L)
        {
            var g = c.G; var st = c.St;
            Footer(c, L, "Hostile camps don't trade. Standing orders use the best price among the rest.");
            var camps = g.S.Camps.Where(cp => cp.Alive).ToList();
            var view = L.Rest;
            if (camps.Count == 0) { c.Fit(new Rect(view.x, view.y, view.width, 30), "Every camp around is gone.", 18, INK2, false); return; }
            const float RH = 80f;
            float cw = c.Scroll("tr_camps", view, camps.Count * (RH + ROW_GAP) - ROW_GAP);
            float y = 0f; int k = 0;
            foreach (var cp in camps)
            {
                string att = g.CampAttitude(cp);
                bool pact = g.C.Pacts.Contains(cp.Id);
                var card = new Rect(0, y, cw, RH);
                Card(c, card, k++, AttColor(att));
                var cols = UiLayout.Columns(UiLayout.Inset(card, 18f, 8f, 10f, 8f), 10f, 1f, -130f, -200f);
                var ln = UiLayout.Rows(cols[0], 2f, 1.2f, 1f);
                c.Fit(ln[0], string.Format("{0}  ·  {1}{2}  ·  {3} people", cp.Name, att, pact ? " (pact)" : "", cp.Pop), 19, AttColor(att), true);
                c.Fit(ln[1], string.Format("Relations {0:+0;-0;0} · needs {1} · spare {2}", (int)g.CampRel(cp), cp.Needs.Count > 0 ? string.Join(", ", cp.Needs) : "nothing", cp.Surplus.Count > 0 ? string.Join(", ", cp.Surplus) : "nothing"), 15, INK2, false);
                var bv = UiLayout.Center(cols[1], cols[1].width, BTN_H); var bb = UiLayout.Center(cols[2], cols[2].width, BTN_H);
                if (c.ViewCamp != null) { string vid = cp.Id; c.Btn(bv, "VIEW", UiRole.Neutral, () => c.ViewCamp(vid)); }   // faction camp sheet (main.gd COLONIES VIEW)
                if (att == "Hostile") c.Chip(UiLayout.Center(cols[2], cols[2].width, 36f), "WON'T TRADE", UiRole.Danger, 16);
                else { string cid = cp.Id; c.Btn(bb, "BARTER NOW", UiRole.Accent, () => { st.Barter = cid; st.BPage = 0; }); }
                y += RH + ROW_GAP;
            }
            c.EndScroll();
        }

        /// <summary>main.gd _show_barter: one-off swaps of 10 of a good with one camp (camp_trade).</summary>
        static void Barter(MenuCtx c)
        {
            var g = c.G; var st = c.St;
            var cp = g.CampById(st.Barter);
            if (cp == null || !cp.Alive || g.CampAttitude(cp) == "Hostile") { st.Barter = ""; Trade(c); return; }
            var L = Frame(c, "BARTER  ·  " + cp.Name, () => st.Barter = "");
            Footer(c, L, string.Format("{0} · relations {1:+0;-0;0} · they pay more for what they need", g.CampAttitude(cp), (int)g.CampRel(cp)));
            c.Fit(L.Top(28f), "Give 10 of something, get the amount shown. Tap a deal to make it.", 17, INK, false);
            var have = string.Join(" · ", Data.RESOURCES.Select(r => Lower(Game.GoodLabel(r)) + " " + (int)g.C.Res.Get(r, 0.0)));
            float hh = c.H_("You have: " + have, 15, false, L.Rest.width);
            c.Para(L.Top(hh), "You have: " + have, 15, AMBER);
            var deals = new List<(string give, string want, int amt)>();
            foreach (var give in Data.RESOURCES)
                foreach (var want in Data.RESOURCES)
                {
                    if (give == want) continue;
                    if (!(Game.CampLists(cp, "surplus", want) || Game.CampLists(cp, "needs", give))) continue;
                    double amt = Math.Floor(10.0 * g.CampPrice(cp, give) / g.CampPrice(cp, want));
                    if (amt < 1.0) continue;
                    deals.Add((give, want, (int)amt));
                }
            var view = L.Rest;
            if (deals.Count == 0) { c.Fit(new Rect(view.x, view.y, view.width, 30), "They have nothing you can swap for right now.", 18, INK2, false); return; }
            int cols = view.width > 1250f ? 4 : 3;
            int rows = (deals.Count + cols - 1) / cols;
            float cw = c.Scroll("tr_barter_" + cp.Id, view, rows * (BTN_H + ROW_GAP) - ROW_GAP);
            for (int k = 0; k < deals.Count; k++)
            {
                var r = UiLayout.Cell(new Rect(0, 0, cw, 0), cols, k, BTN_H, ROW_GAP);
                var dd = deals[k]; string cid = cp.Id;
                c.Btn(r, string.Format("10 {0}", Game.GoodLabel(dd.give)), UiRole.Primary, () =>
                {
                    string e = c.Act("camp_trade", new object[] { cid, dd.give, dd.want });
                    c.Toast(e != "" ? e : "Deal.");
                }, true, false, 18, string.Format("for {0} {1}", dd.amt, Game.GoodLabel(dd.want)));
            }
            c.EndScroll();
        }

        static string Lower(string s) => string.IsNullOrEmpty(s) ? s : s.ToLowerInvariant();
    }
}
