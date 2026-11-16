// Faction camp sheet (diplomacy) and the WAR screen, ported from the Godot original
// (main.gd _sheet_camp / _faction_body, _show_war / _war_army_section / _show_war_report,
// _show_decision for the "_war" demand). Same display-list approach as RESEARCH / TRADE (SideMenus.cs): GameUI
// draws it with IMGUI, the offline preview paints it to PNG. Every button sends the original's player action
// (camp_gift, camp_recruit, camp_tribute, camp_parley, camp_pact, squad_attack, war_enlist, war_enlist_all,
// war_clear, war_declare, craft_melee, craft_vest) through Bootstrap.Act -> Game.DoAction; no rules live here.
using System;
using System.Collections.Generic;
using System.Linq;
using DayZero.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed partial class MenuCtx
    {
        /// <summary>Close the menus, fly to a faction camp and open its sheet (main.gd COLONIES / VIEW).</summary>
        public Action<string> ViewCamp;
    }

    public sealed partial class SideMenuState
    {
        public int WPage, WRPage;        // WAR: army list page, battle report page
    }

    public static partial class SideMenus
    {
        static readonly Color GOLD = new Color(0.914f, 0.769f, 0.416f);    // main.gd C_GOLD #e9c46a
        static readonly Color REDN = new Color(1.0f, 0.42f, 0.36f);         // C_REDN, lifted a little for contrast on the dark panel

        /// <summary>The phone font has no ⚔ ⚑ ★ ⚠ ✓ glyphs: drop them from texts the sim writes.</summary>
        public static string Plain(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            foreach (var g in new[] { "⚔ ", "⚑ ", "★ ", "⚠ ", " ✓", "⚔", "⚑", "★", "⚠", "✓" }) s = s.Replace(g, "");
            return s;
        }

        static Color Lighten(Color c, float f) => new Color(c.r + (1f - c.r) * f, c.g + (1f - c.g) * f, c.b + (1f - c.b) * f, 1f);

        struct SheetLine { public string T; public int Size; public Color Col; }
        struct SheetBtn { public string T, Style; public Action Click; public bool On; }

        /// <summary>Lays out a bottom sheet: title row, wrapped lines, rows of equal-width buttons.</summary>
        static void Sheet(MenuCtx c, string title, Color titleCol, List<SheetLine> lines, List<(string label, List<SheetBtn> btns)> rows, Action close, float pw)
        {
            float iw = pw - 32f;
            float h = 10f + 44f;
            var hs = lines.Select(l => Math.Max(l.Size + 6f, c.H_(l.T, l.Size, false, iw))).ToList();
            foreach (var lh in hs) h += lh + 4f;
            h += rows.Count * 56f + 8f;
            float maxH = c.H - 62f - 10f - 110f;
            var p = new Rect(c.W / 2f - pw / 2f, c.H - 62f - 10f - Math.Min(h, maxH), pw, Math.Min(h, maxH));
            c.Box(p, PANEL);
            c.Box(new Rect(p.x, p.y, p.width, 3), titleCol);
            float x = p.x + 16, y = p.y + 10;
            c.Label(new Rect(x, y, iw - 140, 32), title, 22, titleCol, true, false);
            c.Button(new Rect(p.xMax - 126, p.y + 8, 112, 44), "CLOSE", "dark", 18, close);
            y += 46f;
            float rowsTop = p.yMax - 8f - rows.Count * 56f;
            for (int i = 0; i < lines.Count; i++)
            {
                if (y + hs[i] > rowsTop + 2f) break;   // too tall: drop the tail rather than draw under the buttons
                c.Label(new Rect(x, y, iw, hs[i]), lines[i].T, lines[i].Size, lines[i].Col);
                y += hs[i] + 4f;
            }
            y = rowsTop;
            foreach (var row in rows)
            {
                float bx = x, avail = iw;
                if (!string.IsNullOrEmpty(row.label))
                {
                    float lw = Math.Min(260f, iw * 0.32f);
                    c.Label(new Rect(bx, y + 4, lw, 46), row.label, 14, INK2);
                    bx += lw + 6f; avail -= lw + 6f;
                }
                int n = Math.Max(1, row.btns.Count);
                float bw = (avail - (n - 1) * 6f) / n;
                foreach (var b in row.btns)
                {
                    int sz = b.T.Length > 24 ? 14 : b.T.Length > 18 ? 15 : 16;
                    c.Button(new Rect(bx, y, bw, 50), b.T, b.Style, sz, b.Click, b.On);
                    bx += bw + 6f;
                }
                y += 56f;
            }
        }

        public static float SheetWidth(float w) => Math.Min(860f, w - 230f);

        // ---------------- faction camp sheet (main.gd _sheet_camp / _faction_body) ----------------
        public static void FactionSheet(MenuCtx c, string cid, Action close)
        {
            var g = c.G;
            var cp = g.CampById(cid);
            if (cp == null) { close?.Invoke(); return; }
            float pw = SheetWidth(c.W);
            var lines = new List<SheetLine>();
            var rows = new List<(string, List<SheetBtn>)>();
            Color tcol = Lighten(Game.FactionColor(cp), 0.15f);
            if (!cp.Alive)
            {
                lines.Add(new SheetLine { T = "Gone. Their buildings stand empty.", Size = 16, Col = INK2 });
                Sheet(c, cp.Name, tcol, lines, rows, close, pw);
                return;
            }
            string att = g.CampAttitude(cp);
            bool hostile = g.FactionHostile(cp);
            bool pact = g.C.Pacts.Contains(cp.Id);
            lines.Add(new SheetLine { T = string.Format("{0}{1} · led by {2} · {3} people · strength {4} · {5}", att, pact ? " (pact)" : "", cp.Leader, cp.Pop, (int)cp.Str, Data.Instance.DistrictAt(cp.Pos).Name), Size = 15, Col = AttColor(att) });
            if (!string.IsNullOrEmpty(cp.Desc)) lines.Add(new SheetLine { T = cp.Desc, Size = 14, Col = INK });
            double tr = cp.Truce.Get(g.CurCol.ToString(), -1.0);
            string rel = string.Format("Relations {0:+0;-0;+0}", (int)g.CampRel(cp));
            if (tr > g.S.T) rel += string.Format(" · paid off for {0:0}h more", tr - g.S.T);
            lines.Add(new SheetLine { T = string.Format("{0} · needs {1} · spare {2} · {3} buildings searched", rel, cp.Needs.Count > 0 ? string.Join(", ", cp.Needs) : "nothing",
                cp.Surplus.Count > 0 ? string.Join(", ", cp.Surplus) : "nothing", cp.Searched), Size = 14, Col = GOLD });
            if (cp.Lord == g.ViewCol) lines.Add(new SheetLine { T = "They answer to you and hand over 45% of what they scavenge every dawn.", Size = 14, Col = GREEN });
            else if (cp.Lord >= 0) lines.Add(new SheetLine { T = string.Format("They answer to {0}.", g.ColonyAt(cp.Lord)?.Name ?? "another colony"), Size = 14, Col = INK2 });
            var wv = g.WarOf(g.ViewCol);
            if (wv != null && wv.Lord == cp.Id) lines.Add(new SheetLine { T = "You are their vassal: they take 45% of what you produce. Break free under WAR.", Size = 14, Col = REDN });
            if (wv != null && wv.St == "prep" && wv.Foe == cp.Id) lines.Add(new SheetLine { T = string.Format("At war with you · battle in {0:0.0} days.", g.WarDaysLeft(wv)), Size = 14, Col = REDN });
            if (hostile) lines.Add(new SheetLine { T = "They ambush people out near their turf, raid your nearest camp and batter its fences. Pay them off, talk (Diplomacy T5) or fight them with a squad.", Size = 13, Col = REDN });

            void Res(string e) { if (!string.IsNullOrEmpty(e)) c.Toast(e); }   // main.gd _res_toast: only failures toast
            var r1 = new List<SheetBtn>();
            if (att != "Hostile")
                r1.Add(new SheetBtn { T = "TRADE", Style = "sel", On = true, Click = () => { c.St.Open = "trade"; c.St.Tab = "camps"; c.St.Barter = cid; c.St.BPage = 0; close?.Invoke(); } });
            r1.Add(new SheetBtn { T = "GIFT 15 FOOD", Style = "olive", On = true, Click = () => Res(c.Act("camp_gift", new object[] { cid })) });
            if (att == "Friendly")
                r1.Add(new SheetBtn { T = string.Format("RECRUIT {0}", 2 + (int)g.Rx("recruit_n")), Style = "sel", On = true, Click = () => Res(c.Act("camp_recruit", new object[] { cid })) });
            rows.Add((null, r1));
            var r2 = new List<SheetBtn>();
            if (hostile) r2.Add(new SheetBtn { T = "PAY OFF (25 food, 10 ammo)", Style = "olive", On = true, Click = () => Res(c.Act("camp_tribute", new object[] { cid })) });
            if (att == "Hostile" && cp.Att != "Raiders")
            {
                bool un = g.HasUnlock("parley");
                r2.Add(new SheetBtn { T = un ? "PEACE TALKS (20 food, 6 meds)" : "PEACE TALKS (Diplomacy T5)", Style = "sel", On = un, Click = () => Res(c.Act("camp_parley", new object[] { cid })) });
            }
            if (g.HasUnlock("pact") && att == "Friendly" && !pact)
                r2.Add(new SheetBtn { T = "SIGN A PACT (30 food)", Style = "olive", On = true, Click = () => Res(c.Act("camp_pact", new object[] { cid })) });
            if (r2.Count > 0) rows.Add((null, r2));
            var idle = g.C.Squads.Where(q => q.St == "idle").Take(3).ToList();
            if (idle.Count > 0)
            {
                var r3 = new List<SheetBtn>();
                foreach (var q in idle)
                {
                    int sid = q.Id; string qn = q.Name;
                    r3.Add(new SheetBtn { T = string.Format("{0} ({1})", q.Name.Replace("Squad ", ""), (int)g.SquadAttackPower(q)), Style = "brown", On = true, Click = () =>
                    {
                        string e = c.Act("squad_attack", new object[] { sid, cid });
                        c.Toast(e != "" ? e : qn + " is moving out.");
                    } });
                }
                rows.Add((string.Format("Attack (their power {0}):", (int)g.FactionPower(cp)), r3));
            }
            Sheet(c, cp.Name, tcol, lines, rows, close, pw);
        }

        // ---------------- WAR (main.gd _show_war / _war_army_section / _show_war_report) ----------------
        static void War(MenuCtx c)
        {
            var g = c.G; var st = c.St;
            var w = g.WarOf(g.ViewCol);
            if (w == null) { st.Open = ""; return; }
            if (st.Sub == "report" && w.Report != null && w.Report.Id > 0) { WarReportView(c, w.Report); return; }
            if (st.Sub == "report") st.Sub = "";
            var p = Frame(c, "WAR");
            float x = p.x + 18, iw = p.width - 36, y = p.y + 66, bottom = p.yMax - 70f;
            string stt = w.St ?? "none", lord = w.Lord ?? "";
            const string intro = "Rival camps may demand you bend the knee (12% a day at dawn). Bend and they take 45% of everything you produce. Refuse and you get 10 days to raise an army of grown-ups; then the battle runs itself. Each soldier rolls 2 dice, +1 hand weapon, +2 pistol, +3 shotgun or rifle, +1 armour (2-6 dice). Highest single die wins a duel, ties re-roll. A side that is badly beaten may surrender (25% each round).";
            float ih = c.H_(intro, 14, false, iw);
            c.Label(new Rect(x, y, iw, ih), intro, 14, INK2); y += ih + 8f;
            void L(string t, int size, Color col, bool bold = false, float pad = 4f)
            {
                float hh = Math.Max(size + 6f, c.H_(t, size, bold, iw - 24f));
                c.Label(new Rect(x + 12, y, iw - 24f, hh), t, size, col, bold); y += hh + pad;
            }
            if (stt == "demand")
                L(string.Format("{0} is waiting for your answer: bend the knee or go to war.", g.CampById(w.Foe)?.Name ?? "A rival camp"), 17, REDN, true);
            if (lord != "" && stt != "prep")
            {
                var lc = g.CampById(lord);
                float top = y;
                c.Box(new Rect(x, y, iw, 10), new Color(0.15f, 0.11f, 0.10f));   // card background, grown below
                int cardItem = c.Items.Count - 1;
                y += 8f;
                L(string.Format("You are a vassal of {0}", lc?.Name ?? "?"), 19, REDN, true);
                var paid = w.Paid.Where(kv => kv.Value >= 1.0).Select(kv => new KeyValuePair<string, double>(kv.Key, Math.Floor(kv.Value))).ToList();
                L(string.Format("They take 45% of what your camps produce and your scavengers bring home. Taken since dawn: {0}.", paid.Count > 0 ? Game.FmtCost(paid) : "nothing yet"), 15, INK);
                var est = lc != null ? g.RivalArmyEstimate(lc) : (0, 0.0);
                L(string.Format("{0} can field about {1} fighters averaging {2:0.0} dice.", lc?.Name ?? "?", est.Item1, est.Item2), 14, GOLD);
                c.Button(new Rect(x + 12, y, Math.Min(620f, iw - 24f), 52), "DECLARE WAR TO BREAK FREE (10 days to prepare)", "brown", 17, () =>
                {
                    string e = c.Act("war_declare", new object[0]);
                    c.Toast(e != "" ? e : "War declared. You have 10 days.");
                });
                y += 60f;
                c.Items[cardItem].R = new Rect(x, top, iw, y - top);
                y += 6f;
            }
            var vs = g.WarVassals(g.ViewCol);
            if (vs.Count > 0)
            {
                c.Label(new Rect(x, y, iw, 26), "Camps that answer to you", 18, AMBER, true, false); y += 30f;
                foreach (var vc in vs) L(string.Format("{0} · {1} people · pays you 45% of its scavenging every dawn", vc.Name, vc.Pop), 15, GREEN);
            }
            bool hasRep = w.Report != null && w.Report.Id > 0;
            if (stt == "prep") WarArmy(c, w, x, iw, y, hasRep ? bottom - 0f : bottom, p);
            else if (stt == "none" && lord == "" && vs.Count == 0)
                L("No war. " + (w.CoolT > g.S.T ? string.Format("Rival camps leave you be for {0:0.0} more days.", (w.CoolT - g.S.T) / 24.0) : "Keep an eye on the camps under COLONIES."), 16, INK);
            if (hasRep)
                c.Button(new Rect(p.x + 18, p.yMax - 60, 300, 50), "LAST BATTLE REPORT", "olive", 18, () => { st.Sub = "report"; st.WRPage = 0; });
        }

        static void WarArmy(MenuCtx c, WarState w, float x, float iw, float y, float bottom, Rect p)
        {
            var g = c.G; var st = c.St;
            var foe = g.CampById(w.Foe);
            var kit = g.WarKit(w);
            int tot = kit.Sum(k => k.Dice);
            var est = foe != null ? g.RivalArmyEstimate(foe) : (0, 0.0);
            float top = y;
            c.Box(new Rect(x, y, iw, 10), new Color(0.15f, 0.11f, 0.10f));
            int cardItem = c.Items.Count - 1;
            y += 8f;
            void L(string t, int size, Color col, bool bold = false)
            {
                float hh = Math.Max(size + 6f, c.H_(t, size, bold, iw - 24f));
                c.Label(new Rect(x + 12, y, iw - 24f, hh), t, size, col, bold); y += hh + 3f;
            }
            L(string.Format("WAR with {0} · battle in {1:0.0} days", foe?.Name ?? "?", g.WarDaysLeft(w)), 20, REDN, true);
            L(string.Format("Them: about {0} fighters, {1}, averaging {2:0.0} dice (~{3} dice in all).", est.Item1, foe != null ? g.RivalStrengthWord(foe) : "?", est.Item2, (int)Math.Round(est.Item1 * est.Item2)), 15, GOLD);
            L(string.Format("You: {0} soldiers, {1} dice in all{2}.", kit.Count, tot, kit.Count > 0 ? string.Format(", {0:0.0} each", (double)tot / kit.Count) : ""), 15, kit.Count > 0 ? GREEN : REDN);
            var sw = g.C.Weapons;
            L(string.Format("Armoury: {0} rifles, {1} shotguns, {2} pistols · {3} spare hand weapons · {4} vests · {5} ammo (guns use it each round; without ammo a gun is a club). Soldiers, Cops and Scavengers carry their own hand weapon; Soldiers and Cops wear their own armour.",
                sw.Get("rifle", 0), sw.Get("shotgun", 0), sw.Get("pistol", 0), w.Melee, w.Vests, (int)g.C.Res.Get("ammo", 0.0)), 13, INK2);
            var acts = new[] { ("ENLIST ALL ABLE", "war_enlist_all", "sel"), ("CLEAR ARMY", "war_clear", "olive"),
                ("HAND WEAPON (" + Game.FmtCost(Game.MELEE_COST) + ")", "craft_melee", "sel"), ("VEST (" + Game.FmtCost(Game.VEST_COST) + ")", "craft_vest", "sel") };
            float bw = (iw - 24f - 3 * 6f) / 4f, bx = x + 12;
            foreach (var a in acts)
            {
                string act = a.Item2;
                c.Button(new Rect(bx, y + 2, bw, 50), a.Item1, a.Item3, a.Item1.Length > 22 ? 13 : 15, () => { string e = c.Act(act, new object[0]); if (e != "") c.Toast(e); });
                bx += bw + 6f;
            }
            y += 60f;
            c.Items[cardItem].R = new Rect(x, top, iw, y - top);
            y += 6f;
            // "Grown-ups who can fight", enlisted first (stable)
            var by = kit.ToDictionary(k => k.Pid);
            var ppl = g.AlivePeople();
            int minors = ppl.Count(q => g.CanEnlist(q) != "");
            var able = ppl.Where(q => g.CanEnlist(q) == "").Select((q, i) => (q, i)).OrderByDescending(t => by.ContainsKey(t.q.Id) ? 1 : 0).ThenBy(t => t.i).Select(t => t.q).ToList();
            string hdr = "Grown-ups who can fight" + (minors > 0 ? string.Format("  ·  {0} children and teens stay behind the walls", minors) : "");
            c.Label(new Rect(x, y, iw, 24), hdr, 16, AMBER, true, false); y += 28f;
            const float RH = 54f, GAP = 4f;
            int per = Math.Max(1, (int)((bottom - y + GAP) / (RH + GAP)));
            int pages = Math.Max(1, (able.Count + per - 1) / per);
            st.WPage = Math.Max(0, Math.Min(pages - 1, st.WPage));
            foreach (var q in able.Skip(st.WPage * per).Take(per))
            {
                bool on = by.ContainsKey(q.Id);
                string txt = string.Format("{0} ({1}) · hp {2}", q.Name, q.Job, (int)q.Hp);
                if (on) { var k = by[q.Id]; txt += string.Format(" · {0}{1} · {2} dice", Game.WAR_GEAR_NAME.TryGetValue(k.Gear ?? "", out var gn) ? gn : "", k.Armor ? " + armour" : "", k.Dice); }
                c.Box(new Rect(x, y, iw, RH), on ? new Color(0.12f, 0.16f, 0.11f) : new Color(0.12f, 0.13f, 0.12f));
                c.Label(new Rect(x + 12, y + 3, iw - 180f, RH - 6), txt, 15, on ? GREEN : INK);
                int pid = q.Id; bool en = on;
                c.Button(new Rect(x + iw - 150f, y + 3, 142, RH - 6), on ? "ENLISTED" : "ENLIST", on ? "sel" : "olive", 16, () =>
                {
                    string e = c.Act("war_enlist", new object[] { pid, !en });
                    if (e != "") c.Toast(e);
                });
                y += RH + GAP;
            }
            if (able.Count == 0) c.Label(new Rect(x, y, iw, 24), "Nobody grown-up enough to fight.", 15, REDN, false, false);
            if (pages > 1)
            {
                float py = p.yMax - 60;
                c.Button(new Rect(p.xMax - 392, py, 130, 50), "< PREV", "olive", 18, () => st.WPage--, st.WPage > 0);
                c.Label(new Rect(p.xMax - 258, py + 12, 112, 28), string.Format("{0} / {1}", st.WPage + 1, pages), 18, INK, true, false, MenuAlign.Center);
                c.Button(new Rect(p.xMax - 144, py, 130, 50), "NEXT >", "olive", 18, () => st.WPage++, st.WPage < pages - 1);
            }
        }

        static void WarReportView(MenuCtx c, WarReport r)
        {
            var st = c.St;
            var p = Frame(c, string.Format(r.Won ? "VICTORY over {0}" : "DEFEAT by {0}", r.Foe));
            c.Button(new Rect(p.xMax - 284, p.y + 10, 130, 48), "< BACK", "olive", 18, () => st.Sub = "");
            float x = p.x + 18, iw = p.width - 36, y = p.y + 66;
            string sum = Plain(r.Summary ?? "");
            float sh = c.H_(sum, 18, false, iw);
            c.Label(new Rect(x, y, iw, sh), sum, 18, r.Won ? GREEN : REDN); y += sh + 4f;
            c.Label(new Rect(x, y, iw, 22), string.Format("Day {0} · {1} rounds · {2} · {3} ammo spent", r.Day, r.Rounds, r.Casualties, r.Ammo), 14, GOLD, false, false); y += 28f;
            var all = r.Head.Select(t => (Plain(t), 14, INK2)).Concat(r.Lines.Select(t => { string s = Plain(t); bool hd = t.StartsWith("—"); return (s, hd ? 15 : 14, hd ? GOLD : INK); })).ToList();
            var hs = all.Select(a => Math.Max(20f, c.H_(a.Item1, a.Item2, false, iw))).ToList();
            var pages = Pages(hs, p.yMax - 70f - y, 2f);
            st.WRPage = Math.Max(0, Math.Min(pages.Count - 1, st.WRPage));
            int from = pages[st.WRPage], to = st.WRPage + 1 < pages.Count ? pages[st.WRPage + 1] : all.Count;
            for (int i = from; i < to; i++) { c.Label(new Rect(x, y, iw, hs[i]), all[i].Item1, all[i].Item2, all[i].Item3); y += hs[i] + 2f; }
            Pager(c, p, st.WRPage, pages.Count, v => st.WRPage = v, "");
        }

        /// <summary>The "_war" demand decision (main.gd _show_decision): the full text and both answers.</summary>
        public static void WarDemand(MenuCtx c, JObject ev, Action<int> pick)
        {
            c.Box(new Rect(0, 0, c.W, c.H), DIM);
            var choices = ev["choices"] as JArray ?? new JArray();
            float pw = Math.Min(980f, c.W - 60f), iw = pw - 48f;
            string title = Plain((string)ev["title"] ?? "WAR"), text = Plain((string)ev["text"] ?? "");
            float th = c.H_(title, 24, true, iw), tx = c.H_(text, 16, false, iw);
            float ph = Math.Min(c.H - 24f, 20f + th + 10f + tx + 14f + choices.Count * 84f + 10f);
            var p = new Rect((c.W - pw) / 2f, (c.H - ph) / 2f, pw, ph);
            c.Box(p, PANEL);
            c.Box(new Rect(p.x, p.y, pw, 3), REDN);
            float x = p.x + 24, y = p.y + 18;
            c.Label(new Rect(x, y, iw, th), title, 24, REDN, true); y += th + 10f;
            c.Label(new Rect(x, y, iw, tx), text, 16, INK); y += tx + 14f;
            for (int i = 0; i < choices.Count; i++)
            {
                var ch = choices[i] as JObject;
                string label = Plain((string)ch?["label"] ?? ("Option " + (i + 1))), hint = Plain((string)ch?["hint"]);
                int ii = i;
                c.Button(new Rect(x, y, iw, 76), string.IsNullOrEmpty(hint) ? label : label + "\n" + hint, i == 0 ? "olive" : "brown", 17, () => pick(ii));
                y += 84f;
            }
        }

        /// <summary>WAR status line under the top bar (main.gd lbl_war / _refresh_war_hud); tap opens WAR.</summary>
        public static Rect WarHudRect(float w, float y) => new Rect(w / 2 - 350, y, 700, 40);
    }
}
