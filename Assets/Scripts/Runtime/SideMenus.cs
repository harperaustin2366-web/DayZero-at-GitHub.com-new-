// RESEARCH and TRADE side menus, ported from the Godot original (main.gd _show_research, _show_trade_list,
// _render_trade_rows, _trade_set, _show_barter). Pure C#: each menu is laid out into a flat display list
// (boxes, labels, buttons, bars) that GameUI draws with IMGUI and the offline preview paints to PNG, so the
// previews show the exact layout the phone gets. Every button sends the same player action as the original
// (start_research, set_trade, camp_trade) through Bootstrap.Act -> Game.DoAction; no rules live here.
using System;
using System.Collections.Generic;
using System.Linq;
using DayZero.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DayZero.Runtime
{
    public enum MenuKind { Box, Label, Button, Bar }
    public enum MenuAlign { Left, Center, Right }

    public sealed class MenuItem
    {
        public MenuKind Kind;
        public Rect R;
        public string Text = "";
        public int Size = 16;
        public bool Bold, Wrap = true, On = true;
        public MenuAlign Align;
        public Color Col, Fill;
        public string Style = "olive";   // buttons: olive | amber | dark | brown | sel (selected / primary: dark amber)
        public float Frac;               // bars
        public Action Click;
    }

    /// <summary>What the side menus remember between frames (open menu, tree, page, tab, barter camp).</summary>
    public sealed partial class SideMenuState
    {
        public string Open = "";         // "" | "research" | "trade" | "squads" | "taskforce"
        public string Cat = "farming";
        public int RPage;
        public bool RJump = true;        // jump to the first unfinished project of the tree on next layout
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

        public void Box(Rect r, Color fill) => Items.Add(new MenuItem { Kind = MenuKind.Box, R = r, Fill = fill });
        public void Label(Rect r, string t, int size, Color col, bool bold = false, bool wrap = true, MenuAlign al = MenuAlign.Left)
            => Items.Add(new MenuItem { Kind = MenuKind.Label, R = r, Text = t, Size = size, Col = col, Bold = bold, Wrap = wrap, Align = al });
        public void Button(Rect r, string t, string style, int size, Action click, bool on = true)
            => Items.Add(new MenuItem { Kind = MenuKind.Button, R = r, Text = t, Style = style, Size = size, Click = click, On = on });
        public void Bar(Rect r, float frac, Color col) => Items.Add(new MenuItem { Kind = MenuKind.Bar, R = r, Frac = Math.Max(0f, Math.Min(1f, frac)), Col = col });
        public float H_(string t, int size, bool bold, float w) => string.IsNullOrEmpty(t) ? 0f : (float)Math.Ceiling(Measure(t, size, bold, w));
    }

    public static partial class SideMenus
    {
        public static readonly Color INK = new Color(0.93f, 0.90f, 0.80f);
        public static readonly Color INK2 = new Color(0.78f, 0.76f, 0.68f);
        public static readonly Color AMBER = new Color(0.95f, 0.70f, 0.28f);
        public static readonly Color GREEN = new Color(0.62f, 0.84f, 0.42f);
        public static readonly Color RED = new Color(0.98f, 0.50f, 0.40f);
        static readonly Color DIM = new Color(0f, 0f, 0f, 0.55f);
        static readonly Color PANEL = new Color(0.07f, 0.08f, 0.06f, 0.97f);

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
        }

        static Rect Frame(MenuCtx c, string title)
        {
            c.Box(new Rect(0, 0, c.W, c.H), DIM);
            float pw = Math.Min(1040f, c.W - 32f), ph = c.H - 24f;
            var p = new Rect((c.W - pw) / 2f, 12f, pw, ph);
            c.Box(p, PANEL);
            c.Box(new Rect(p.x, p.y, pw, 3), AMBER);
            c.Label(new Rect(p.x + 18, p.y + 12, pw - 200, 38), title, 28, AMBER, true, false);
            c.Button(new Rect(p.xMax - 142, p.y + 10, 128, 48), "CLOSE", "dark", 20, () => { c.St.Open = ""; c.St.Barter = ""; c.St.Sub = ""; });
            return p;
        }

        static void Pager(MenuCtx c, Rect p, int page, int pages, Action<int> set, string left)
        {
            float y = p.yMax - 60;
            if (!string.IsNullOrEmpty(left)) c.Label(new Rect(p.x + 18, y + 12, p.width - 420, 28), left, 15, INK2, false, false);
            if (pages <= 1) return;
            c.Button(new Rect(p.xMax - 392, y, 130, 50), "< PREV", "olive", 18, () => set(page - 1), page > 0);
            c.Label(new Rect(p.xMax - 258, y + 12, 112, 28), string.Format("{0} / {1}", page + 1, pages), 18, INK, true, false, MenuAlign.Center);
            c.Button(new Rect(p.xMax - 144, y, 130, 50), "NEXT >", "olive", 18, () => set(page + 1), page < pages - 1);
        }

        /// <summary>Greedy pages over variable-height rows: start index of each page.</summary>
        static List<int> Pages(IList<float> hs, float avail, float gap)
        {
            var o = new List<int> { 0 };
            float used = 0f;
            for (int i = 0; i < hs.Count; i++)
            {
                if (used > 0f && used + hs[i] > avail) { o.Add(i); used = 0f; }
                used += hs[i] + gap;
            }
            return o;
        }

        static string CatName(string cat) => (string)Data.Instance.ResearchCatsName[cat] ?? cat;

        // ---------------- RESEARCH (main.gd _show_research) ----------------
        static void Research(MenuCtx c)
        {
            var g = c.G; var D = Data.Instance; var st = c.St;
            if (!D.ResearchByCat.ContainsKey(st.Cat)) st.Cat = D.ResearchCats[0];
            var p = Frame(c, "RESEARCH");
            float x = p.x + 18, iw = p.width - 36, y = p.y + 66;

            // current project + rate
            string cur = g.C.Research.Cur ?? "";
            double rate = g.ResearchRate();
            float rw = 300f, lw = iw - rw - 16f;
            c.Label(new Rect(x + iw - rw, y, rw, 44), string.Format("{0} Scientists · {1:0.00} lab-h/h · {2} power", g.CountJob("Scientist"), rate, (int)g.Rx("power")), 15, INK2, false, true, MenuAlign.Right);
            if (cur != "" && D.Research[cur] != null)
            {
                var r = D.Research[cur];
                double pr = Math.Max(0.0, g.ResearchProg(cur)), hrs = g.ResearchHours(cur);
                string s = string.Format("Researching: {0} ({1} T{2}) · {3:0} / {4:0}h · {5}", (string)r["name"], CatName((string)r["cat"]), (int)(double)r["tier"], pr, hrs,
                    rate > 0.0 ? string.Format("done in ~{0:0}h", (hrs - pr) / rate) : "no Scientist at a camp!");
                float sh = Math.Max(22f, c.H_(s, 17, true, lw));
                c.Label(new Rect(x, y, lw, sh), s, 17, AMBER, true);
                c.Bar(new Rect(x, y + sh + 4, lw, 12), (float)(pr / Math.Max(0.001, hrs)), GREEN);
                y += Math.Max(sh + 26f, 44f);
            }
            else { c.Label(new Rect(x, y, lw, 44), "No project running. Pick one below.", 17, AMBER, true); y += 44f; }

            // the 9 trees, 5 per row
            int cols = 5;
            float cw = (iw - (cols - 1) * 8f) / cols, ch = 48f;
            for (int i = 0; i < D.ResearchCats.Count; i++)
            {
                string cat = D.ResearchCats[i];
                var list = D.ResearchByCat[cat];
                int n = list.Count(g.HasResearch);
                var r = new Rect(x + (i % cols) * (cw + 8f), y + (i / cols) * (ch + 8f), cw, ch);
                string cc = cat;
                c.Button(r, string.Format("{0}  {1}/{2}", CatName(cat), n, list.Count), st.Cat == cat ? "sel" : "dark", 17, () => { st.Cat = cc; st.RJump = true; });
            }
            y += ((D.ResearchCats.Count + cols - 1) / cols) * (ch + 8f) + 4f;

            string note = st.Cat == "scouting" ? "Scouting: bigger squads, longer range, faster searching and a wider auto-scavenge radius. T1 unlocks squad scavenging runs."
                : st.Cat == "aviation" ? "Aviation is the endgame: every tier is slow and expensive and the first one needs Electricity T8, Vehicles T10 and Crafting T10." : "";
            if (note != "") { float nh = c.H_(note, 15, false, iw); c.Label(new Rect(x, y, iw, nh), note, 15, AMBER); y += nh + 6f; }

            // project cards
            var rids = D.ResearchByCat[st.Cat];
            const float BTN_W = 140f, PAD = 8f;
            float tw = iw - BTN_W - 34f;
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
                float dh = c.H_((string)r["desc"], 14, false, tw), mh = c.H_(meta, 13, false, tw);
                rows.Add((rid, stt, meta, need.Count > 0, dh, mh));
                hs.Add(Math.Max(68f, PAD + 24f + dh + 2f + mh + PAD));
            }
            float top = y, avail = p.yMax - 70f - top;
            var pages = Pages(hs, avail, 6f);
            if (st.RJump)
            {
                st.RJump = false;
                int first = rows.FindIndex(q => q.state == "active");
                if (first < 0) first = rows.FindIndex(q => q.state != "done");
                st.RPage = 0;
                if (first >= 0) for (int pg = 0; pg < pages.Count; pg++) if (pages[pg] <= first) st.RPage = pg;
            }
            st.RPage = Math.Max(0, Math.Min(pages.Count - 1, st.RPage));
            int from = pages[st.RPage], to = st.RPage + 1 < pages.Count ? pages[st.RPage + 1] : rows.Count;
            for (int i = from; i < to; i++)
            {
                var q = rows[i]; var r = D.Research[q.rid];
                var card = new Rect(x, y, iw, hs[i]);
                Color fill = q.state == "done" ? new Color(0.12f, 0.17f, 0.11f) : q.state == "active" ? new Color(0.20f, 0.17f, 0.08f) : q.state == "available" ? new Color(0.15f, 0.16f, 0.14f) : new Color(0.11f, 0.11f, 0.11f);
                Color accent = q.state == "done" ? GREEN : q.state == "active" ? AMBER : q.state == "available" ? new Color(0.55f, 0.62f, 0.36f) : new Color(0.30f, 0.30f, 0.28f);
                c.Box(card, fill);
                c.Box(new Rect(card.x, card.y, 4, card.height), accent);
                Color nc = q.state == "done" ? GREEN : q.state == "active" ? AMBER : q.state == "available" ? INK : INK2;
                float ty = card.y + PAD - 2f;
                c.Label(new Rect(card.x + 16, ty, tw, 26), string.Format("T{0}  {1}", (int)(double)r["tier"], (string)r["name"]), 18, nc, true, false); ty += 26f;
                c.Label(new Rect(card.x + 16, ty, tw, q.dh), (string)r["desc"], 14, q.state != "locked" ? INK : INK2); ty += q.dh + 2f;
                c.Label(new Rect(card.x + 16, ty, tw, q.mh), q.meta, 13, q.need ? RED : INK2);
                var br = new Rect(card.xMax - BTN_W - 10, card.y + (card.height - 52f) / 2f, BTN_W, 52);
                switch (q.state)
                {
                    case "done": c.Label(br, "DONE", 18, GREEN, true, false, MenuAlign.Center); break;
                    case "active": c.Label(br, string.Format("{0}%", (int)(100.0 * Math.Max(0.0, g.ResearchProg(q.rid)) / g.ResearchHours(q.rid))), 22, AMBER, true, false, MenuAlign.Center); break;
                    case "locked": c.Label(br, "LOCKED", 16, INK2, true, false, MenuAlign.Center); break;
                    default:
                        string e = g.CanResearch(q.rid), rid = q.rid;
                        c.Button(br, g.ResearchProg(rid) >= 0.0 ? "SWITCH" : "START", e == "" ? "sel" : "dark", 19, () =>
                        {
                            string e2 = c.Act("start_research", new object[] { rid });
                            if (e2 != "") c.Toast(e2);
                        });
                        break;
                }
                y += hs[i] + 6f;
            }
            int done = rids.Count(g.HasResearch);
            Pager(c, p, st.RPage, pages.Count, v => st.RPage = v, string.Format("{0}: {1} of {2} done · cost is paid at START, progress is kept if you switch", CatName(st.Cat), done, rids.Count));
        }

        // ---------------- TRADE (main.gd _show_trade_list / _render_trade_rows / _show_barter) ----------------
        static readonly Dictionary<string, string> MODE_LABEL = new Dictionary<string, string> { ["import"] = "IMPORT", ["export"] = "EXPORT", ["ignore"] = "IGNORE" };
        static readonly string[] MODES = { "import", "export", "ignore" };

        static void Trade(MenuCtx c)
        {
            var g = c.G; var st = c.St;
            var p = Frame(c, st.Barter != "" ? "BARTER" : "TRADE");
            if (st.Barter != "") { Barter(c, p); return; }
            float x = p.x + 18, iw = p.width - 36, y = p.y + 66;

            const string intro = "Set each good once and the traders do the rest: IMPORT keeps your stock topped up to the level, EXPORT sells whatever is above it, IGNORE leaves it alone. Exports earn trade credit, imports spend it, at each camp's own prices.";
            float ih = c.H_(intro, 14, false, iw);
            c.Label(new Rect(x, y, iw, ih), intro, 14, INK2); y += ih + 6f;

            var t = g.TradeCfgOf();
            var partners = g.TradePartners();
            double nxt = Math.Max(0.0, t.Next - g.S.T);
            string head = string.Format("Trade credit {0} · {1}", (int)t.Credit, partners.Count > 0
                ? string.Format("traders from {0} camp{1} · next visit in {2:0}h", partners.Count, partners.Count == 1 ? "" : "s", nxt)
                : "no camp will trade with you right now");
            if (!string.IsNullOrEmpty(t.Last)) head += "\nLast visit: " + t.Last;
            float hh = c.H_(head, 15, false, iw);
            c.Label(new Rect(x, y, iw, hh), head, 15, AMBER); y += hh + 8f;

            // tabs (the original lists goods, then gear, in one scroll; barter sits on each camp's sheet)
            int alive = g.S.Camps.Count(cp => cp.Alive);
            var tabs = new[] { ("goods", "SUPPLIES"), ("gear", "GUNS, PARTS & ARMOUR"), ("camps", string.Format("PARTNER CAMPS ({0})", partners.Count)) };
            float tw = (iw - 16f) / 3f;
            for (int i = 0; i < tabs.Length; i++)
            {
                string id = tabs[i].Item1;
                c.Button(new Rect(x + i * (tw + 8f), y, tw, 48), tabs[i].Item2, st.Tab == id ? "sel" : "olive", 17, () => { st.Tab = id; });
            }
            y += 56f;

            if (st.Tab == "camps") { Camps(c, p, x, iw, y); return; }
            bool gear = st.Tab == "gear";
            if (gear) { c.Label(new Rect(x, y, iw, 22), "One at a time; worn gear is never sold.", 15, AMBER, false, false); y += 26f; }
            var goods = gear ? Data.GEAR_TRADE : Data.RESOURCES;
            const float RH = 58f, GAP = 6f;
            int per = Math.Max(1, (int)((p.yMax - 70f - y + GAP) / (RH + GAP)));
            int pages = (goods.Length + per - 1) / per;
            st.TPage = Math.Max(0, Math.Min(pages - 1, st.TPage));
            foreach (var r in goods.Skip(st.TPage * per).Take(per))
            {
                var gd = t.Goods[r];
                string m = gd.M;
                var card = new Rect(x, y, iw, RH);
                c.Box(card, m == "import" ? new Color(0.12f, 0.16f, 0.11f) : m == "export" ? new Color(0.19f, 0.15f, 0.09f) : new Color(0.12f, 0.13f, 0.13f));
                c.Box(new Rect(card.x, card.y, 4, RH), m == "import" ? GREEN : m == "export" ? AMBER : new Color(0.30f, 0.30f, 0.28f));
                int stock = gear ? g.GearTotal(r) : (int)g.C.Res.Get(r, 0.0);
                c.Label(new Rect(card.x + 14, card.y + 6, 176, 24), Game.GoodLabel(r), 17, INK, true, false);
                c.Label(new Rect(card.x + 14, card.y + 31, 176, 20), stock + " in stock", 14, INK2, false, false);
                float bx = card.x + 196;
                foreach (var mode in MODES)
                {
                    string md = mode, rr = r; int lv = gd.Lvl;
                    c.Button(new Rect(bx, card.y + 6, 98, 46), MODE_LABEL[mode], m == mode ? "sel" : "dark", 16, () => SetTrade(c, rr, md, lv));
                    bx += 104;
                }
                int step = gear ? 1 : Game.TRADE_STEP.TryGetValue(r, out var sv) ? sv : 10;
                string r2 = r, m2 = m; int l2 = gd.Lvl;
                bx += 4;
                c.Button(new Rect(bx, card.y + 6, 52, 46), "-", "olive", 26, () => SetTrade(c, r2, m2, l2 - step)); bx += 56;
                c.Label(new Rect(bx, card.y + 6, 62, 46), gd.Lvl.ToString(), 20, AMBER, true, false, MenuAlign.Center); bx += 66;
                c.Button(new Rect(bx, card.y + 6, 52, 46), "+", "olive", 26, () => SetTrade(c, r2, m2, l2 + step)); bx += 64;
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
                c.Label(new Rect(bx, card.y + 4, card.xMax - bx - 10, RH - 8), hint, 14, INK2);
                y += RH + GAP;
            }
            Pager(c, p, st.TPage, pages, v => st.TPage = v, gear ? "Traders buy and sell 1 piece at a time, every 6 hours." : "Traders come every 6 hours: up to 6 lots of 10 per good.");
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

        static void Camps(MenuCtx c, Rect p, float x, float iw, float y)
        {
            var g = c.G; var st = c.St;
            var camps = g.S.Camps.Where(cp => cp.Alive).ToList();
            if (camps.Count == 0) { c.Label(new Rect(x, y, iw, 30), "Every camp around is gone.", 17, INK2); return; }
            const float RH = 70f, GAP = 6f;
            int per = Math.Max(1, (int)((p.yMax - 70f - y + GAP) / (RH + GAP)));
            int pages = (camps.Count + per - 1) / per;
            st.CPage = Math.Max(0, Math.Min(pages - 1, st.CPage));
            foreach (var cp in camps.Skip(st.CPage * per).Take(per))
            {
                string att = g.CampAttitude(cp);
                bool pact = g.C.Pacts.Contains(cp.Id);
                var card = new Rect(x, y, iw, RH);
                c.Box(card, new Color(0.13f, 0.14f, 0.12f));
                c.Box(new Rect(card.x, card.y, 4, RH), AttColor(att));
                float tw = iw - (c.ViewCamp != null ? 312f : 200f);
                c.Label(new Rect(card.x + 14, card.y + 6, tw, 26), string.Format("{0}  ·  {1}{2}  ·  {3} people", cp.Name, att, pact ? " (pact)" : "", cp.Pop), 17, AttColor(att), true, false);
                string line = string.Format("Relations {0:+0;-0;0} · needs {1} · spare {2}", (int)g.CampRel(cp), cp.Needs.Count > 0 ? string.Join(", ", cp.Needs) : "nothing", cp.Surplus.Count > 0 ? string.Join(", ", cp.Surplus) : "nothing");
                c.Label(new Rect(card.x + 14, card.y + 34, tw, 32), line, 14, INK2);
                var br = new Rect(card.xMax - 176, card.y + 10, 164, 50);
                if (att == "Hostile") c.Label(br, "WON'T TRADE", 16, RED, true, false, MenuAlign.Center);
                else { string cid = cp.Id; c.Button(br, "BARTER NOW", "olive", 18, () => { st.Barter = cid; st.BPage = 0; }); }
                if (c.ViewCamp != null) { string vid = cp.Id; c.Button(new Rect(br.x - 112, br.y, 104, 50), "VIEW", "dark", 18, () => c.ViewCamp(vid)); }   // faction camp sheet (main.gd COLONIES VIEW)
                y += RH + GAP;
            }
            Pager(c, p, st.CPage, pages, v => st.CPage = v, "Hostile camps don't trade. Standing orders use the best price among the rest.");
        }

        /// <summary>main.gd _show_barter: one-off swaps of 10 of a good with one camp (camp_trade).</summary>
        static void Barter(MenuCtx c, Rect p)
        {
            var g = c.G; var st = c.St;
            var cp = g.CampById(st.Barter);
            if (cp == null || !cp.Alive || g.CampAttitude(cp) == "Hostile") { st.Barter = ""; Trade(c); return; }
            float x = p.x + 18, iw = p.width - 36, y = p.y + 66;
            c.Button(new Rect(p.xMax - 284, p.y + 10, 130, 48), "< BACK", "olive", 18, () => st.Barter = "");
            c.Label(new Rect(x + 140, p.y + 16, p.width - 460, 34), cp.Name.ToUpperInvariant(), 22, INK, true, false);
            const string txt = "Give 10 of something on the left, get the amount shown. They pay more for what they need.";
            c.Label(new Rect(x, y, iw, 24), txt, 15, INK2, false, false); y += 28f;
            var have = string.Join(" · ", Data.RESOURCES.Select(r => Lower(Game.GoodLabel(r)) + " " + (int)g.C.Res.Get(r, 0.0)));
            float hh = c.H_("You have: " + have, 14, false, iw);
            c.Label(new Rect(x, y, iw, hh), "You have: " + have, 14, AMBER); y += hh + 8f;
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
            if (deals.Count == 0) { c.Label(new Rect(x, y, iw, 30), "They have nothing you can swap for right now.", 17, INK2); return; }
            const int COLS = 4; const float BH = 52f, GAP = 8f;
            float bw = (iw - (COLS - 1) * GAP) / COLS;
            int rowsPer = Math.Max(1, (int)((p.yMax - 70f - y + GAP) / (BH + GAP)));
            int per = rowsPer * COLS, pages = (deals.Count + per - 1) / per;
            st.BPage = Math.Max(0, Math.Min(pages - 1, st.BPage));
            int k = 0;
            foreach (var d in deals.Skip(st.BPage * per).Take(per))
            {
                var r = new Rect(x + (k % COLS) * (bw + GAP), y + (k / COLS) * (BH + GAP), bw, BH);
                var dd = d; string cid = cp.Id;
                c.Button(r, string.Format("10 {0} → {1} {2}", Game.GoodLabel(d.give), d.amt, Game.GoodLabel(d.want)), "dark", 16, () =>
                {
                    string e = c.Act("camp_trade", new object[] { cid, dd.give, dd.want });
                    c.Toast(e != "" ? e : "Deal.");
                });
                k++;
            }
            Pager(c, p, st.BPage, pages, v => st.BPage = v, string.Format("{0} · relations {1:+0;-0;0}", g.CampAttitude(cp), (int)g.CampRel(cp)));
        }

        static string Lower(string s) => string.IsNullOrEmpty(s) ? s : s.ToLowerInvariant();
    }
}
