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
        public string LeaderCamp = "";   // faction sheet showing the LEADER page for this camp id (EXPANDED)
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
        struct SheetBtn { public string T, Style, Sub; public Action Click; public bool On; }

        /// <summary>Lays out a bottom sheet: title row, wrapped lines, rows of equal-width buttons.</summary>
        static void Sheet(MenuCtx c, string title, Color titleCol, List<SheetLine> lines, List<(string label, List<SheetBtn> btns)> rows, Action close, float pw)
        {
            var area = SheetRect(c.W, c.H, pw, 100f);
            float iw = area.width - 24f;
            var hs = lines.Select(l => Math.Max(l.Size + 8f, c.H_(l.T, l.Size, false, iw))).ToList();
            float h = 24f + 48f + 8f + hs.Sum(x => x + 8f) + rows.Count * (BTN_H + 8f) + 4f;
            float maxH = c.H - 62f - 10f - 110f;
            var p = SheetRect(c.W, c.H, pw, Math.Min(h, maxH));
            var L = SheetFrame(c, p, title, close);
            // button rows from the bottom up
            for (int ri = rows.Count - 1; ri >= 0; ri--)
            {
                var row = rows[ri];
                var R = new UiLayout(L.Bottom(BTN_H), 8f);
                if (!string.IsNullOrEmpty(row.label)) c.Fit(R.Left(Math.Min(300f, R.Rest.width * 0.34f)), row.label, 16, INK2, false);
                var bc = UiLayout.Even(R.Rest, Math.Max(1, row.btns.Count), 8f);
                for (int i = 0; i < row.btns.Count; i++)
                {
                    var b = row.btns[i];
                    c.Btn(bc[i], b.T, MenuCtx.StyleRole(b.Style), b.Click, b.On, false, 18, b.Sub);
                }
            }
            for (int i = 0; i < lines.Count; i++)
            {
                if (hs[i] > L.Rest.height + 1f) break;   // too tall: drop the tail rather than draw under the buttons
                c.Para(L.Top(hs[i]), lines[i].T, lines[i].Size, lines[i].Col);
            }
        }

        public static float SheetWidth(float w) => Math.Min(900f, w - 230f);

        // ---------------- faction camp sheet (main.gd _sheet_camp / _faction_body) ----------------
        public static void FactionSheet(MenuCtx c, string cid, Action close) { FactionSheetBody(c, cid, close); Flush(c); }

        static void FactionSheetBody(MenuCtx c, string cid, Action close)
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
            var mind = g.LeaderOf(cp);   // EXPANDED: the leader's mind (LeaderBrain.cs), null otherwise
            if (mind != null && c.St.LeaderCamp == cid) { LeaderPage(c, cp, mind, tcol, close, pw); return; }
            lines.Add(new SheetLine { T = string.Format("{0}{1} · led by {2} · {3} people · strength {4} · {5}", att, pact ? " (pact)" : "", cp.Leader, cp.Pop, (int)cp.Str, Data.Instance.DistrictAt(cp.Pos).Name), Size = 15, Col = AttColor(att) });
            if (mind != null && !string.IsNullOrEmpty(mind.Plan))
                lines.Add(new SheetLine { T = string.Format("{0} ({1}): \"{2}\"", cp.Leader, Lower(g.LeaderFeelingText(cp)), mind.Plan), Size = 15, Col = INK });
            else if (!string.IsNullOrEmpty(cp.Desc)) lines.Add(new SheetLine { T = cp.Desc, Size = 14, Col = INK });
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
            if (hostile) lines.Add(new SheetLine { T = "They ambush people out near their turf, raid your nearest camp and batter its fences. Pay them off, talk (Diplomacy T5) or fight them with a squad.", Size = 14, Col = REDN });

            void Res(string e) { if (!string.IsNullOrEmpty(e)) c.Toast(e); }   // main.gd _res_toast: only failures toast
            var r1 = new List<SheetBtn>();
            if (att != "Hostile")
                r1.Add(new SheetBtn { T = "TRADE", Style = "sel", On = true, Click = () => { c.St.Open = "trade"; c.St.Tab = "camps"; c.St.Barter = cid; c.St.BPage = 0; close?.Invoke(); } });
            r1.Add(new SheetBtn { T = "GIFT 15 FOOD", Style = "olive", On = true, Click = () => Res(c.Act("camp_gift", new object[] { cid })) });
            if (att == "Friendly")
                r1.Add(new SheetBtn { T = string.Format("RECRUIT {0}", 2 + (int)g.Rx("recruit_n")), Style = "sel", On = true, Click = () => Res(c.Act("camp_recruit", new object[] { cid })) });
            if (mind != null) r1.Add(new SheetBtn { T = "LEADER", Sub = "who they are", Style = "sel", On = true, Click = () => c.St.LeaderCamp = cid });
            rows.Add((null, r1));
            var r2 = new List<SheetBtn>();
            if (hostile) r2.Add(new SheetBtn { T = "PAY OFF", Sub = "25 food, 10 ammo", Style = "olive", On = true, Click = () => Res(c.Act("camp_tribute", new object[] { cid })) });
            if (att == "Hostile" && cp.Att != "Raiders")
            {
                bool un = g.HasUnlock("parley");
                r2.Add(new SheetBtn { T = "PEACE TALKS", Sub = un ? "20 food, 6 meds" : "needs Diplomacy T5", Style = "sel", On = un, Click = () => Res(c.Act("camp_parley", new object[] { cid })) });
            }
            if (g.HasUnlock("pact") && att == "Friendly" && !pact)
                r2.Add(new SheetBtn { T = "SIGN A PACT", Sub = "30 food", Style = "olive", On = true, Click = () => Res(c.Act("camp_pact", new object[] { cid })) });
            if (r2.Count > 0) rows.Add((null, r2));
            var idle = g.C.Squads.Where(q => q.St == "idle").Take(3).ToList();
            if (idle.Count > 0)
            {
                var r3 = new List<SheetBtn>();
                foreach (var q in idle)
                {
                    int sid = q.Id; string qn = q.Name;
                    r3.Add(new SheetBtn { T = q.Name.Replace("Squad ", "").ToUpperInvariant(), Sub = string.Format("power {0}", (int)g.SquadAttackPower(q)), Style = "brown", On = true, Click = () =>
                    {
                        string e = c.Act("squad_attack", new object[] { sid, cid });
                        c.Toast(e != "" ? e : qn + " is moving out.");
                    } });
                }
                rows.Add((string.Format("Attack (their power {0}):", (int)g.FactionPower(cp)), r3));
            }
            Sheet(c, cp.Name, tcol, lines, rows, close, pw);
        }

        /// <summary>EXPANDED: the LEADER page of a faction sheet. Who the leader is, what they are doing and why, how they
        /// feel about you and what they remember. All text comes from Game.Leader* (LeaderBrain.cs); plain ASCII.</summary>
        static void LeaderPage(MenuCtx c, Camp cp, LeaderMind m, Color tcol, Action close, float pw)
        {
            var g = c.G;
            var lines = new List<SheetLine>();
            lines.Add(new SheetLine { T = string.Format("LEADER: {0}  -  {1}", cp.Leader, g.LeaderTraitsText(cp)), Size = 17, Col = GOLD });
            lines.Add(new SheetLine { T = string.Format("Right now ({0}): \"{1}\"", Lower(g.LeaderFeelingText(cp)), m.Plan), Size = 16, Col = INK });
            string att = g.CampAttitude(cp);
            lines.Add(new SheetLine { T = "Toward you: " + g.LeaderMoodToYou(cp, g.ViewCol), Size = 15, Col = AttColor(att) });
            lines.Add(new SheetLine { T = "Before: " + g.LeaderBackstory(cp), Size = 14, Col = INK2 });
            lines.Add(new SheetLine { T = g.LeaderValuesText(cp), Size = 14, Col = INK2 });
            var mem = g.LeaderMemories(cp, 3);
            if (mem.Count > 0) lines.Add(new SheetLine { T = "Remembers: " + string.Join("  ", mem.Select(x => "\"" + x + "\"")), Size = 14, Col = INK2 });
            var allies = m.Allies.Select(a => g.CampById(a)).Where(a => a != null && a.Alive).Select(a => a.Name).ToList();
            if (allies.Count > 0) lines.Add(new SheetLine { T = "Stands with: " + string.Join(", ", allies), Size = 14, Col = GREEN });
            var rows = new List<(string, List<SheetBtn>)>
            {
                (null, new List<SheetBtn> { new SheetBtn { T = "BACK TO CAMP", Style = "sel", On = true, Click = () => c.St.LeaderCamp = "" } }),
            };
            Sheet(c, cp.Name, tcol, lines, rows, () => { c.St.LeaderCamp = ""; close?.Invoke(); }, pw);
        }

        // ---------------- WAR (main.gd _show_war / _war_army_section / _show_war_report) ----------------
        /// <summary>Blocks of a scroll list: height, then a draw at y (content coordinates).</summary>
        sealed class Blocks
        {
            public readonly List<(float h, Action<float> draw)> L = new List<(float, Action<float>)>();
            public void Add(float h, Action<float> draw) => L.Add((h, draw));
            public void Draw(MenuCtx c, string id, Rect view, float gap)
            {
                c.Scroll(id, view, Total(L.Select(b => b.h).ToList(), gap));
                float y = 0f;
                foreach (var b in L) { b.draw(y); y += b.h + gap; }
                c.EndScroll();
            }
        }

        static void War(MenuCtx c)
        {
            var g = c.G; var st = c.St;
            var w = g.WarOf(g.ViewCol);
            if (w == null) { st.Open = ""; return; }
            if (st.Sub == "report" && w.Report != null && w.Report.Id > 0) { WarReportView(c, w.Report); return; }
            if (st.Sub == "report") st.Sub = "";
            bool hasRep = w.Report != null && w.Report.Id > 0;
            var L = Frame(c, "WAR");
            if (hasRep) Footer(c, L, "", new BarBtn("LAST BATTLE REPORT", UiRole.Primary, () => { st.Sub = "report"; st.WRPage = 0; }, true, 320f));
            var view = L.Rest;
            float iw = view.width - 12f;
            string stt = w.St ?? "none", lord = w.Lord ?? "";
            var B = new Blocks();
            void P(string t, int size, Color col, float x0 = 0f) { float hh = Math.Max(size + 8f, c.H_(t, size, false, iw - x0 * 2f)); B.Add(hh, y => c.Para(new Rect(x0, y, iw - x0 * 2f, hh), t, size, col)); }
            void T(string t, int size, Color col) => B.Add(size + 12f, y => c.Fit(new Rect(0, y, iw, size + 12f), t, size, col, true));
            P("Rival camps may demand you bend the knee (12% a day at dawn). Bend and they take 45% of everything you produce. Refuse and you get 10 days to raise an army of grown-ups; then the battle runs itself. Each soldier rolls 2 dice, +1 hand weapon, +2 pistol, +3 shotgun or rifle, +1 armour (2-6 dice). Highest single die wins a duel, ties re-roll. A side that is badly beaten may surrender (25% each round).", 15, INK2);
            if (stt == "demand")
                T(string.Format("{0} is waiting for your answer: bend the knee or go to war.", g.CampById(w.Foe)?.Name ?? "A rival camp"), 19, REDN);
            if (lord != "" && stt != "prep")
            {
                var lc = g.CampById(lord);
                var paid = w.Paid.Where(kv => kv.Value >= 1.0).Select(kv => new KeyValuePair<string, double>(kv.Key, Math.Floor(kv.Value))).ToList();
                string t1 = string.Format("They take 45% of what your camps produce and your scavengers bring home. Taken since dawn: {0}.", paid.Count > 0 ? Game.FmtCost(paid) : "nothing yet");
                var est = lc != null ? g.RivalArmyEstimate(lc) : (0, 0.0);
                string t2 = string.Format("{0} can field about {1} fighters averaging {2:0.0} dice.", lc?.Name ?? "?", est.Item1, est.Item2);
                float h1 = c.H_(t1, 16, false, iw - 36f), h2 = c.H_(t2, 15, false, iw - 36f);
                float ch = 12f + 32f + h1 + 4f + h2 + 10f + BTN_H + 12f;
                B.Add(ch, y =>
                {
                    Card(c, new Rect(0, y, iw, ch), 0, REDN, true);
                    float yy = y + 12f;
                    c.Fit(new Rect(18f, yy, iw - 36f, 30f), string.Format("You are a vassal of {0}", lc?.Name ?? "?"), 22, REDN, true); yy += 32f;
                    c.Para(new Rect(18f, yy, iw - 36f, h1), t1, 16, INK); yy += h1 + 4f;
                    c.Para(new Rect(18f, yy, iw - 36f, h2), t2, 15, GOLD); yy += h2 + 10f;
                    c.Btn(new Rect(18f, yy, Math.Min(560f, iw - 36f), BTN_H), "DECLARE WAR TO BREAK FREE", UiRole.Danger, () =>
                    {
                        string e = c.Act("war_declare", new object[0]);
                        c.Toast(e != "" ? e : "War declared. You have 10 days.");
                    }, true, false, 20, "10 days to prepare");
                });
            }
            var vs = g.WarVassals(g.ViewCol);
            if (vs.Count > 0)
            {
                B.Add(40f, y => c.Header(new Rect(0, y, iw, 40f), "Camps that answer to you", 20));
                foreach (var vc in vs) P(string.Format("{0} · {1} people · pays you 45% of its scavenging every dawn", vc.Name, vc.Pop), 16, GREEN);
            }
            if (stt == "prep") WarArmy(c, w, iw, B);
            else if (stt == "none" && lord == "" && vs.Count == 0)
                P("No war. " + (w.CoolT > g.S.T ? string.Format("Rival camps leave you be for {0:0.0} more days.", (w.CoolT - g.S.T) / 24.0) : "Keep an eye on the camps under COLONIES."), 18, INK);
            B.Draw(c, "war_" + stt, view, ROW_GAP);
        }

        static void WarArmy(MenuCtx c, WarState w, float iw, Blocks B)
        {
            var g = c.G;
            var foe = g.CampById(w.Foe);
            var kit = g.WarKit(w);
            int tot = kit.Sum(k => k.Dice);
            var est = foe != null ? g.RivalArmyEstimate(foe) : (0, 0.0);
            float tw = iw - 36f;
            var sw = g.C.Weapons;
            var lines = new List<(string t, int size, Color col)>
            {
                (string.Format("Them: about {0} fighters, {1}, averaging {2:0.0} dice (~{3} dice in all).", est.Item1, foe != null ? g.RivalStrengthWord(foe) : "?", est.Item2, (int)Math.Round(est.Item1 * est.Item2)), 16, GOLD),
                (string.Format("You: {0} soldiers, {1} dice in all{2}.", kit.Count, tot, kit.Count > 0 ? string.Format(", {0:0.0} each", (double)tot / kit.Count) : ""), 16, kit.Count > 0 ? GREEN : REDN),
                (string.Format("Armoury: {0} rifles, {1} shotguns, {2} pistols · {3} spare hand weapons · {4} vests · {5} ammo (guns use it each round; without ammo a gun is a club). Soldiers, Cops and Scavengers carry their own hand weapon; Soldiers and Cops wear their own armour.",
                    sw.Get("rifle", 0), sw.Get("shotgun", 0), sw.Get("pistol", 0), w.Melee, w.Vests, (int)g.C.Res.Get("ammo", 0.0)), 14, INK2),
            };
            var lh = lines.Select(l => Math.Max(l.size + 6f, c.H_(l.t, l.size, false, tw))).ToList();
            float ch = 12f + 34f + lh.Sum(h => h + 4f) + 8f + BTN_H + 12f;
            var acts = new[] { ("ENLIST ALL ABLE", "war_enlist_all", UiRole.Accent, (string)null), ("CLEAR ARMY", "war_clear", UiRole.Danger, (string)null),
                ("HAND WEAPON", "craft_melee", UiRole.Primary, Game.FmtCost(Game.MELEE_COST)), ("VEST", "craft_vest", UiRole.Primary, Game.FmtCost(Game.VEST_COST)) };
            B.Add(ch, y =>
            {
                Card(c, new Rect(0, y, iw, ch), 0, REDN, true);
                float yy = y + 12f;
                c.Fit(new Rect(18f, yy, tw, 32f), string.Format("WAR with {0} · battle in {1:0.0} days", foe?.Name ?? "?", g.WarDaysLeft(w)), 23, REDN, true); yy += 34f;
                for (int i = 0; i < lines.Count; i++) { c.Para(new Rect(18f, yy, tw, lh[i]), lines[i].t, lines[i].size, lines[i].col); yy += lh[i] + 4f; }
                yy += 8f;
                var bc = UiLayout.Even(new Rect(18f, yy, tw, BTN_H), 4, 10f);
                for (int i = 0; i < acts.Length; i++)
                {
                    string act = acts[i].Item2;
                    c.Btn(bc[i], acts[i].Item1, acts[i].Item3, () => { string e = c.Act(act, new object[0]); if (e != "") c.Toast(e); }, true, false, 18, acts[i].Item4);
                }
            });
            // "Grown-ups who can fight", enlisted first (stable)
            var by = kit.ToDictionary(k => k.Pid);
            var ppl = g.AlivePeople();
            int minors = ppl.Count(q => g.CanEnlist(q) != "");
            var able = ppl.Where(q => g.CanEnlist(q) == "").Select((q, i) => (q, i)).OrderByDescending(t => by.ContainsKey(t.q.Id) ? 1 : 0).ThenBy(t => t.i).Select(t => t.q).ToList();
            string hdr = "Grown-ups who can fight" + (minors > 0 ? string.Format("  ·  {0} children and teens stay behind the walls", minors) : "");
            B.Add(40f, y => c.Header(new Rect(0, y, iw, 40f), hdr, 20));
            if (able.Count == 0) B.Add(30f, y => c.Fit(new Rect(0, y, iw, 30f), "Nobody grown-up enough to fight.", 17, REDN, false));
            int k2 = 0;
            foreach (var q in able)
            {
                bool on = by.ContainsKey(q.Id);
                string txt = string.Format("{0} ({1}) · hp {2}", q.Name, q.Job, (int)q.Hp);
                if (on) { var k = by[q.Id]; txt += string.Format(" · {0}{1} · {2} dice", Game.WAR_GEAR_NAME.TryGetValue(k.Gear ?? "", out var gn) ? gn : "", k.Armor ? " + armour" : "", k.Dice); }
                int pid = q.Id; bool en = on; int idx = k2++;
                B.Add(BTN_H + 12f, y =>
                {
                    var row = new Rect(0, y, iw, BTN_H + 12f);
                    Card(c, row, idx, on ? GREEN : new Color(0.36f, 0.36f, 0.32f), on);
                    c.Fit(new Rect(18f, y + 6f, iw - 240f, BTN_H), txt, 18, on ? GREEN : INK, on);
                    c.Btn(new Rect(iw - 200f, y + 6f, 190f, BTN_H), on ? "ENLISTED" : "ENLIST", on ? UiRole.Accent : UiRole.Primary, () =>
                    {
                        string e = c.Act("war_enlist", new object[] { pid, !en });
                        if (e != "") c.Toast(e);
                    }, true, on, 19);
                });
            }
        }

        static void WarReportView(MenuCtx c, WarReport r)
        {
            var st = c.St;
            var L = Frame(c, string.Format(r.Won ? "VICTORY over {0}" : "DEFEAT by {0}", r.Foe), () => st.Sub = "");
            string sum = Plain(r.Summary ?? "");
            c.Para(L.Top(c.H_(sum, 19, false, L.Rest.width)), sum, 19, r.Won ? GREEN : REDN);
            c.Fit(L.Top(26f), string.Format("Day {0} · {1} rounds · {2} · {3} ammo spent", r.Day, r.Rounds, r.Casualties, r.Ammo), 17, GOLD, false);
            var view = L.Rest;
            float iw = view.width - 12f;
            var all = r.Head.Select(t => (Plain(t), 15, INK2)).Concat(r.Lines.Select(t => { string s = Plain(t); bool hd = t.StartsWith("—"); return (s, hd ? 17 : 15, hd ? GOLD : INK); })).ToList();
            var hs = all.Select(a => Math.Max(22f, c.H_(a.Item1, a.Item2, false, iw))).ToList();
            c.Scroll("war_report", view, Total(hs, 3f));
            float y = 0f;
            for (int i = 0; i < all.Count; i++) { c.Para(new Rect(0, y, iw, hs[i]), all[i].Item1, all[i].Item2, all[i].Item3); y += hs[i] + 3f; }
            c.EndScroll();
        }

        /// <summary>The "_war" demand decision (main.gd _show_decision): the full text and both answers.</summary>
        public static void WarDemand(MenuCtx c, JObject ev, Action<int> pick)
        {
            c.Dim();
            var choices = ev["choices"] as JArray ?? new JArray();
            float pw = Math.Min(1000f, c.W - 60f), iw = pw - 24f;
            string title = Plain((string)ev["title"] ?? "WAR"), text = Plain((string)ev["text"] ?? "");
            float tx = c.H_(text, 17, false, iw);
            var hints = new List<(string label, string hint, float hh)>();
            for (int i = 0; i < choices.Count; i++)
            {
                var ch = choices[i] as JObject;
                string label = Plain((string)ch?["label"] ?? ("Option " + (i + 1))), hint = Plain((string)ch?["hint"]);
                hints.Add((label, hint, string.IsNullOrEmpty(hint) ? 0f : c.H_(hint, 15, false, iw)));
            }
            float ph = Math.Min(c.H - 24f, 24f + 48f + 10f + tx + 14f + hints.Sum(hh => BTN_H + 4f + hh.hh + 10f));
            var p = new Rect((c.W - pw) / 2f, (c.H - ph) / 2f, pw, ph);
            var L = new UiLayout(c.Panel(p), 10f);
            c.Header(L.Top(44f), title, 24);
            c.Para(L.Top(Math.Min(tx, L.Rest.height)), text, 17, INK);
            for (int i = 0; i < hints.Count; i++)
            {
                int ii = i;
                c.Btn(L.Top(BTN_H), hints[i].label, i == 0 ? UiRole.Primary : UiRole.Danger, () => pick(ii), true, false, 20);
                if (hints[i].hh > 0f) c.Para(L.Top(Math.Min(hints[i].hh, L.Rest.height)), hints[i].hint, 15, INK2);
            }
            Flush(c);
        }

        /// <summary>WAR status line under the top bar (main.gd lbl_war / _refresh_war_hud); tap opens WAR.</summary>
        public static Rect WarHudRect(float w, float y) => new Rect(w / 2 - 350, y, 700, 40);
    }
}
