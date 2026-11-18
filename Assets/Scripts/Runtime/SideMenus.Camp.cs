// CAMPS, camp FACILITIES / UPGRADE BASE, CREW & JOBS, the camp sheet, the trap sheet, the trap-line toolbar, the
// BUILD OUTPOST bar and the building sheet's GUT row, ported from the Godot original (main.gd _show_bases,
// _show_base_modal, _base_level_card, _armory_rows, _show_crew, _auto_panel, _status_text, _sheet_base, _sheet_trap,
// _trap_stats, _render_trap_toolbar, _start_outpost, _sheet_gut). Pure layout into the same display list as the other
// side menus; every button sends the original's player action (upgrade_base, do_upgrade, craft_ammo, build_outpost,
// gut, place_traps, upgrade_trap, remove_trap, set_job, move_to_base, set_hold, set_guard, set_auto) through
// Bootstrap.Act -> Game.DoAction. No rules live here.
using System;
using System.Collections.Generic;
using System.Linq;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public static partial class SideMenus
    {
        static readonly Color CAMP_BLUE = new Color(0.56f, 0.82f, 1f);
        static readonly Color CAMP_FAMILY = new Color(0.96f, 0.84f, 0.63f);

        /// <summary>Which camp the FACILITIES screen shows, the pages of the camp screens, and a camera move the
        /// map should make once the menu has closed (VIEW on the camp list).</summary>
        public static int CampBid = -1;
        public static int CrewPage, CampsPage, BasePage;
        public static bool BaseFromList;
        public static Vector2? PendingFly;

        /// <summary>Font size of an ACTIONS button: long labels (BUILD OUTPOST) step down so they stay on one line.</summary>
        public static int ActFont(int i) => ACTIONS[i].label.Length > 11 ? 16 : 19;

        /// <summary>Opens the FACILITIES / UPGRADE BASE screen of one camp.</summary>
        public static void OpenBase(SideMenuState st, int bid, bool fromList = false)
        {
            CampBid = bid; BasePage = 0; BaseFromList = fromList;
            st.Open = "base";
        }

        /// <summary>The CAMPS / FACILITIES / CREW screens. True when one of them is open.</summary>
        public static bool CampMenus(MenuCtx c)
        {
            switch (c.St.Open)
            {
                case "crew": Crew(c); return true;
                case "camps": Camps(c); return true;
                case "base": BaseModal(c); return true;
            }
            return false;
        }

        static string CampAct(MenuCtx c, string a, params object[] args) => c.Act(a, args) ?? "";
        static void CampToast(MenuCtx c, string ok, string a, params object[] args) { string e = CampAct(c, a, args); if (e != "" || ok != "") c.Toast(e != "" ? e : ok); }

        /// <summary>Height a wrapped button label needs (bold, 8 px side padding, 7 px top / bottom), at least 52.</summary>
        static float BtnH(MenuCtx c, string t, int size, float w) => Math.Max(52f, c.H_(t, size, true, w - 16f) + 14f);

        static string CampShortCost(IEnumerable<KeyValuePair<string, double>> cost)
            => string.Join(" ", cost.Select(kv => string.Format("{0} {1}", (int)Math.Ceiling(kv.Value - 0.001),
                (Data.RES_SHORT.TryGetValue(kv.Key, out var s) ? s : Data.RES_LABEL.TryGetValue(kv.Key, out var l) ? l : kv.Key).ToLowerInvariant())));

        // ---------------- bottom sheets on the map ----------------
        /// <summary>A bottom sheet's rect: above the time controls, right of the camera buttons and left of the ACTIONS
        /// panel, so a sheet never covers either (v0.17: the old sheets ran under ACTIONS).</summary>
        /// <summary>Set by GameUI.DrawDockedSheet while a sheet lays out: the dock's sheet area (left of ACTIONS,
        /// above PLAY / speed). When set, every sheet docks into it.</summary>
        public static Rect? DockArea;

        public static Rect SheetRect(float w, float h, float pw, float ph)
        {
            if (DockArea.HasValue)
            {
                var a = DockArea.Value;
                float dw = Math.Min(pw, a.width), dh = Math.Min(ph, a.height);
                return new Rect((float)Math.Floor(a.x + (a.width - dw) * 0.5f + 0.5f), a.yMax - dh, dw, dh);
            }
            float l = 150f, r = Math.Max(l + 420f, ActionsPanel(w).x - 12f);
            pw = Math.Min(pw, r - l);
            return new Rect(l + (r - l - pw) / 2f, h - 62f - ph - 10f, pw, ph);
        }

        /// <summary>Kit sheet: plate, header strip and CLOSE (or another right-hand button). Returns the layout below.</summary>
        static UiLayout SheetFrame(MenuCtx c, Rect p, string title, Action close, string closeLabel = "CLOSE", UiRole closeRole = UiRole.Danger)
        {
            var L = new UiLayout(c.Panel(p), 8f);
            var T = new UiLayout(L.Top(48f), 10f);
            if (close != null) c.Btn(T.Right(140f), closeLabel, closeRole, close);
            c.Header(new Rect(T.Rest.x, T.Rest.y + 2f, T.Rest.width, 42f), title, 22);
            return L;
        }

        // ---------------- CAMPS & OUTPOSTS (main.gd _show_bases) ----------------
        static void Camps(MenuCtx c)
        {
            var g = c.G; var D = Data.Instance;
            var L = Frame(c, "CAMPS & OUTPOSTS");
            Footer(c, L, "Tap BUILD OUTPOST in ACTIONS, then a building, to start a new outpost.");
            var top = new UiLayout(L.Top(36f), 10f);
            c.Chip(top.Left(230f), string.Format("{0} / {1} OUTPOSTS", g.Outposts(), Game.OUTPOST_MAX), UiRole.Accent, 17);
            c.Fit(top.Rest, "Each camp has a safe zone the dead won't enter until the city falls.", 17, INK2, false);
            var bases = g.C.Bases;
            const float RH = 84f;
            float cw = c.Scroll("camps_list", L.Rest, Math.Max(0f, bases.Count * (RH + ROW_GAP) - ROW_GAP));
            float y = 0f; int k = 0;
            foreach (var b in bases)
            {
                var card = new Rect(0, y, cw, RH);
                Card(c, card, k++, b.Main ? GREEN : CAMP_BLUE, b.Main);
                var bb = b;
                var cols = UiLayout.Columns(UiLayout.Inset(card, 18f, 8f, 10f, 8f), 10f, 1f, -130f, -200f);
                var ln = UiLayout.Rows(cols[0], 2f, 1.2f, 1f);
                c.Fit(ln[0], g.BaseLabel(b), 21, b.Main ? GREEN : CAMP_BLUE, true);
                string info = b.Done < 1.0 ? string.Format("Under construction: {0}%", (int)(b.Done * 100))
                    : string.Format("{0} · {1} · {2}/{3} beds · defense {4}{5}{6}", D.DistrictAt(b.Pos).Name, g.BaseLevelDef(b).Name, g.BasePeople(b.Id).Count, g.BaseBeds(b), (int)g.Defense(b),
                        g.IsEnclosed(b) ? " · fenced" : "", g.IsSelfSustaining(b) ? " · SELF-SUSTAINING" : "");
                c.Fit(ln[1], info, 16, b.Done < 1.0 ? AMBER : INK2, false);
                c.Btn(UiLayout.Center(cols[1], cols[1].width, BTN_H), "VIEW", UiRole.Neutral, () => { PendingFly = new Vector2(bb.Pos.x + 0.5f, bb.Pos.y + 0.5f); c.St.Open = ""; });
                if (b.Done >= 1.0) c.Btn(UiLayout.Center(cols[2], cols[2].width, BTN_H), "FACILITIES", UiRole.Accent, () => OpenBase(c.St, bb.Id, true));
                y += RH + ROW_GAP;
            }
            c.EndScroll();
        }

        // ---------------- FACILITIES / UPGRADE BASE (main.gd _show_base_modal, _base_level_card, _armory_rows) ----------------
        static (bool ok, string text) CampBalanceText(Game g, Base b)
        {
            var r = g.BaseBalance(b);
            double mats = r.Get("materials", 0.0) + Game.MatSum(r);
            bool ok = true;
            var parts = new List<string>();
            foreach (var k in new[] { "food", "water", "meds", "ammo", "materials" })
            {
                double v = k == "materials" ? mats : r.Get(k, 0.0);
                if (v < -0.01) ok = false;
                parts.Add(string.Format("{0} {1}{2:0.0}", Data.RES_LABEL.TryGetValue(k, out var l) ? l : k, v >= 0.0 ? "+" : "", v));
            }
            return (ok, "Per day: " + string.Join("  ", parts));
        }

        static void BaseModal(MenuCtx c)
        {
            var g = c.G;
            var b = g.BaseById(CampBid);
            if (b == null) b = g.MainBase();
            if (b == null) { c.St.Open = ""; return; }
            CampBid = b.Id;
            var L = BaseFromList ? Frame(c, g.BaseLabel(b), null, new BarBtn("ALL CAMPS", UiRole.Neutral, () => { c.St.Open = "camps"; }, true, 180f)) : Frame(c, g.BaseLabel(b));
            Footer(c, L, string.Format("Everything here is instant: pay the cost and it's built. Building slots used: {0} / {1}.", g.SlotsUsed(b), g.BaseSlots(b)));
            var bal = CampBalanceText(g, b);
            var top = new UiLayout(L.Top(36f), 10f);
            c.Chip(top.Left(250f), bal.ok ? "SELF-SUSTAINING" : "NOT SELF-SUSTAINING", bal.ok ? UiRole.Primary : UiRole.Accent, 16);
            c.Fit(top.Rest, bal.text, 17, bal.ok ? GREEN : AMBER, false);

            // one scroll list: the level card, then a row per facility
            var view = L.Rest;
            float cw = view.width - 12f;
            const float BW = 300f;
            float lw = cw - BW - 46f;
            var rows = new List<(string uid, float h, float dh, float ah, string ars)>();
            foreach (var kv in Data.UPGRADES)
            {
                if (kv.Key == "radio" && !b.Main) continue;
                string desc = kv.Value.Desc ?? "";
                var cst0 = g.UpgradeCost(b, kv.Key);
                if (cst0.Count > 0) desc += (desc != "" ? "\n" : "") + "Cost: " + Game.FmtCost(cst0);
                float dh = c.H_(desc, 15, false, lw);
                float h = Math.Max(BTN_H + 20f, 12f + 30f + dh + 12f);
                float ah = 0f; string ars = null;
                if (kv.Key == "armory" && b.U("armory") > 0)
                {
                    var guns = new List<string>();
                    foreach (var gid in Data.WEAPON_ORDER)
                    {
                        int n = g.C.Weapons.Get(gid, 0);
                        if (n > 0) guns.Add(string.Format("{0} {1}", n, (Data.WEAPONS.TryGetValue(gid, out var wd) ? wd.Name : gid).ToLowerInvariant()));
                    }
                    ars = "Arsenal: " + (guns.Count > 0 ? string.Join(", ", guns) : "no guns") + ". Loadouts, gun parts and assembly are in the ARMORY screen.";
                    ah = c.H_(ars, 15, false, cw - 36f) + 6f + BTN_H + 10f;
                    h += ah;
                }
                rows.Add((kv.Key, h, dh, ah, ars));
            }
            float cardH = LevelCardH(c, b, cw);
            c.Scroll("base_" + b.Id, view, cardH + ROW_GAP + Total(rows.Select(r => r.h).ToList(), ROW_GAP));
            float y = LevelCard(c, b, 0f, cw, 0f) + ROW_GAP;
            for (int i = 0; i < rows.Count; i++)
            {
                string uid = rows[i].uid;
                var u = Data.UPGRADES[uid];
                int lv = b.U(uid);
                var card = new Rect(0, y, cw, rows[i].h);
                Card(c, card, i, lv > 0 ? GREEN : new Color(0.36f, 0.36f, 0.32f));
                float tx = 18f, ty = y + 10f;
                c.Fit(new Rect(tx, ty, lw, 28f), string.Format("{0}  (level {1}/{2}){3}", u.Name, lv, u.Max, (u.Slot ?? true) ? "" : "  · no slot needed"), 20, lv > 0 ? GREEN : INK, true);
                var cost = g.UpgradeCost(b, uid);
                string desc = (u.Desc ?? "") + (cost.Count > 0 ? ((u.Desc ?? "") != "" ? "\n" : "") + "Cost: " + Game.FmtCost(cost) : "");
                if (rows[i].dh > 0f) c.Para(new Rect(tx, ty + 30f, lw, rows[i].dh), desc, 15, INK2);
                string blk = g.UpgradeBlock(b, uid);
                float mainH = rows[i].h - rows[i].ah;
                var br = new Rect(cw - BW - 12f, y + (mainH - BTN_H) / 2f, BW, BTN_H);
                if (uid == "garage" && !b.GarageOk) c.Fit(br, "No garage at this site", 17, RED, true, MenuAlign.Center);
                else if (cost.Count == 0) c.Chip(UiLayout.Center(br, BW, 36f), "MAX LEVEL", UiRole.Primary, 17);
                else if (blk != "") { float bh = Math.Min(mainH - 12f, c.H_(blk, 14, false, BW)); c.Para(new Rect(br.x, y + (mainH - bh) / 2f, BW, bh), blk, 14, AMBER); }
                else
                {
                    string uu = uid; int bid = b.Id;
                    c.Btn(br, lv == 0 ? "BUILD" : "UPGRADE", g.CanAfford(cost) ? UiRole.Accent : UiRole.Primary, () => CampToast(c, "", "do_upgrade", bid, uu), true, false, 21);
                }
                if (rows[i].ars != null)
                {
                    // main.gd _armory_rows: arsenal, "10 AMMO" (craft_ammo); loadouts and gun assembly live in the ARMORY screen
                    float ay = y + mainH;
                    float ph = rows[i].ah - 6f - BTN_H - 10f;
                    c.Para(new Rect(tx, ay, cw - 36f, ph), rows[i].ars, 15, AMBER);
                    c.Btn(new Rect(tx, ay + ph + 6f, 280f, BTN_H), "10 AMMO", UiRole.Primary, () => CampToast(c, "+10 ammo", "craft_ammo"), true, false, 19, CampShortCost(Game.AMMO_CRAFT));
                }
                y += rows[i].h + ROW_GAP;
            }
            c.EndScroll();
        }

        static (string stats, string path, string nxt, string err, string cost, bool next) LevelTexts(Game g, Base b)
        {
            var cur = g.BaseLevelDef(b);
            var arr = b.Main ? Data.BASE_LEVELS["main"] : Data.BASE_LEVELS["outpost"];
            int l = b.Lvl;
            string stats = string.Format("{0} beds ({1} living here) · {2} building slots · zone {3:0} tiles · storage {4} each · +{5} defense{6}",
                g.BaseBeds(b), g.BasePeople(b.Id).Count, g.BaseSlots(b), g.BaseZoneR(b), (int)g.StorageCap(), cur.Defense, g.IsEnclosed(b) ? string.Format(" · fenced: {0} tiles", g.EnclosedCount(b)) : "");
            string path = string.Join("  >  ", arr.Select((a, i) => i == l ? "[" + a.Name + "]" : a.Name));
            bool next = l + 1 < arr.Length;
            string err = next ? g.CanUpgradeBase(b) : "";
            string nxt = "", cost = "";
            if (next)
            {
                var nx = arr[l + 1];
                nxt = string.Format("Next: {0} · {1} beds · {2} slots · zone {3:0} · +{4} defense · storage {5}{6}", nx.Name, nx.Beds, nx.Slots, nx.Zone, nx.Defense, nx.Storage,
                    nx.MinPeople.HasValue ? string.Format(" · needs {0} people", nx.MinPeople.Value) : "");
                cost = Game.FmtCost(nx.Cost);
                nxt += "\nCost: " + cost;
            }
            return (stats, path, nxt, err, cost, next);
        }

        const float UBW = 320f;
        static float LevelCardH(MenuCtx c, Base b, float iw)
        {
            var t = LevelTexts(c.G, b);
            float cw = iw - 36f;
            float h = 12f + 32f + c.H_(t.stats, 15, false, cw) + 4f + 26f;
            if (t.next) h += Math.Max(BTN_H, c.H_(t.nxt, 15, false, cw - UBW - 12f)) + 8f + (t.err != "" ? c.H_(t.err, 14, false, cw) + 4f : 0f);
            return h + 12f;
        }

        /// <summary>main.gd _base_level_card: the camp's level, the path, the next level and UPGRADE BASE (upgrade_base).</summary>
        static float LevelCard(MenuCtx c, Base b, float x, float iw, float y)
        {
            var g = c.G;
            var t = LevelTexts(g, b);
            float h = LevelCardH(c, b, iw);
            var r = new Rect(x, y, iw, h);
            Card(c, r, 0, AMBER, true);
            float cx = x + 18f, cw = iw - 36f, cy = y + 12f;
            c.Fit(new Rect(cx, cy, cw - (t.next ? 0f : 220f), 30f), b.Name + " · " + g.BaseLevelDef(b).Name, 22, AMBER, true);
            if (!t.next) c.Chip(new Rect(cx + cw - 200f, cy - 2f, 200f, 34f), "TOP LEVEL", UiRole.Primary, 17);
            cy += 32f;
            float sh = c.H_(t.stats, 15, false, cw);
            c.Para(new Rect(cx, cy, cw, sh), t.stats, 15, INK); cy += sh + 4f;
            c.Fit(new Rect(cx, cy, cw, 24f), t.path, 16, INK2, false); cy += 26f;
            if (t.next)
            {
                float nh = Math.Max(BTN_H, c.H_(t.nxt, 15, false, cw - UBW - 12f));
                c.Para(new Rect(cx, cy, cw - UBW - 12f, nh), t.nxt, 15, INK);
                int bid = b.Id;
                c.Btn(new Rect(cx + cw - UBW, cy, UBW, BTN_H), "UPGRADE BASE", t.err == "" ? UiRole.Accent : UiRole.Neutral, () => CampToast(c, "", "upgrade_base", bid), true, false, 21);
                cy += nh + 8f;
                if (t.err != "") c.Para(new Rect(cx, cy, cw, c.H_(t.err, 14, false, cw)), t.err, 14, RED);
            }
            return y + h;
        }

        // ---------------- camp sheet (main.gd _sheet_base) ----------------
        public static Rect BaseSheetRect(float w, float h) => SheetRect(w, h, 960f, 276f);

        public static void BaseSheet(MenuCtx c, int bid, Action close) { BaseSheetBody(c, bid, close); Flush(c); }

        static void BaseSheetBody(MenuCtx c, int bid, Action close)
        {
            var g = c.G; var D = Data.Instance;
            var b = g.BaseById(bid);
            if (b == null) { close?.Invoke(); return; }
            var L = SheetFrame(c, BaseSheetRect(c.W, c.H), g.BaseLabel(b), close);
            string dn = D.DistrictAt(b.Pos).Name;
            if (b.Done < 1.0)
            {
                c.Fit(L.Top(26f), string.Format("{0} · under construction {1}%. Engineers speed it up.", dn, (int)(b.Done * 100)), 17, INK2, false);
                return;
            }
            // buttons: FACILITIES · UPGRADE BASE · CREW / SQUADS (n) · TASK FORCE
            bool up = g.NextLevelCost(b).Count > 0 || b.Lvl == 0;
            var acts = new List<(string t, UiRole role, Action a)>
            {
                ("FACILITIES", UiRole.Accent, () => { close?.Invoke(); OpenBase(c.St, bid); }),
            };
            if (up) acts.Add(("UPGRADE BASE", UiRole.Primary, () => { close?.Invoke(); OpenBase(c.St, bid); }));
            acts.Add(("CREW", UiRole.Primary, () => { close?.Invoke(); CrewPage = 0; c.St.Open = "crew"; }));
            acts.Add((string.Format("SQUADS ({0})", g.CampSquads(bid).Count), UiRole.Primary, () => { close?.Invoke(); c.St.Camp = bid; Open(c.St, "squads"); }));
            acts.Add(("TASK FORCE", UiRole.Primary, () => { close?.Invoke(); c.St.Camp = bid; Open(c.St, "taskforce"); }));
            var bc = UiLayout.Columns(L.Bottom(BTN_H), 8f, acts.Select(a => a.t.Length + 6f).ToArray());
            for (int i = 0; i < acts.Count; i++) c.Btn(bc[i], acts[i].t, acts[i].role, acts[i].a, true, false, 18);
            var ppl = g.BasePeople(b.Id);
            c.Fit(L.Top(22f), string.Format("{0} · {1} · {2}/{3} beds · defense {4}", dn, g.BaseLevelDef(b).Name, ppl.Count, g.BaseBeds(b), (int)g.Defense(b)), 17, INK2, false);
            c.Fit(L.Top(20f), g.IsEnclosed(b) ? string.Format("Fenced in: {0} tiles enclosed (+{1} defense)", g.EnclosedCount(b), (int)g.EnclosureDefense(b)) : "Not enclosed. Snap a closed fence loop around it (DEFENSE) to grow it.", 15, g.IsEnclosed(b) ? GREEN : INK2, false);
            var counts = new Dictionary<string, int>();
            foreach (var q in ppl) counts[q.Job] = counts.TryGetValue(q.Job, out var n0) ? n0 + 1 : 1;
            var parts = new List<string>();
            foreach (var j in Data.JOBS) if (counts.TryGetValue(j, out var n1)) parts.Add(n1 + " " + Data.JOB_SHORT[j]);
            if (counts.TryGetValue("Child", out var nk)) parts.Add(nk + " KID");
            var fac = new List<string>();
            foreach (var k in new[] { "medbay", "armory", "garage" }) if (b.U(k) > 0) fac.Add(Data.UPGRADES[k].Name + " L" + b.U(k));
            if (b.Veh > 0) fac.Add(b.Veh + " vehicle(s)");
            c.Fit(L.Top(20f), (parts.Count > 0 ? string.Join("  ", parts) : "Nobody assigned here yet.") + "   ·   Facilities: " + (fac.Count > 0 ? string.Join(", ", fac) : "none yet"), 15, INK, false);
            if (b.Site != "") c.Fit(L.Top(20f), Game.SiteBonusText(b.Site), 15, AMBER, false);
            var bal = CampBalanceText(g, b);
            c.Fit(L.Top(20f), (bal.ok ? "SELF-SUSTAINING  " : "") + bal.text, 15, bal.ok ? GREEN : AMBER, false);
        }

        // ---------------- CREW & JOBS (main.gd _show_crew, _auto_panel, _status_text) ----------------
        static readonly (string key, string label)[] CREW_AUTO_KEYS = { ("scav", "Auto-scavenge"), ("rescue", "Auto-rescue SOS"), ("conv", "Auto-convert"), ("guards", "Auto-guards") };

        public static (string text, Color col) CrewStatusText(Game g, Person p)
        {
            if (p.Inf >= 0.0 && p.Known) return ("Infected · quarantine", new Color(1f, 0.63f, 0.25f));
            if (p.St == "base" && p.Guard && g.OnDuty()) return ("On the night watch", new Color(0.56f, 0.72f, 1f));
            string hp = p.Hp >= 95.0 ? "" : string.Format(" · hurt {0}%", (int)p.Hp);
            switch (p.St)
            {
                case "base": return ("At camp" + hp, INK2);
                case "move":
                    switch (p.Goal)
                    {
                        case "scav": return ("Out scavenging" + hp, AMBER);
                        case "home":
                            if (p.Trav.HasValue) return (string.Format("Moving to {0} (no bed at the first camp)", g.BaseById(p.Trav.Value)?.Name ?? "another camp") + hp, new Color(0.56f, 0.82f, 1f));
                            return ("Heading home" + hp, INK2);
                        case "tf": return ("Task force" + hp, AMBER);
                        case "hunt": return ("Hunting zombies" + hp, AMBER);
                        case "repair": return ("Repairing a fence", AMBER);
                        case "sq": return ("Squad: clearing road" + hp, AMBER);
                        case "build": return ("Building outpost", AMBER);
                    }
                    break;
                case "work":
                    switch (p.Goal)
                    {
                        case "scav": return ("Looting" + hp, AMBER);
                        case "build": return ("Building outpost", AMBER);
                        case "repair": return ("Repairing", AMBER);
                        case "sq": return ("Squad: sweeping the area" + hp, AMBER);
                    }
                    break;
            }
            return ("Busy" + hp, INK2);
        }

        /// <summary>The jobs a survivor can be given (the original's job dropdown; minors never get a danger job).</summary>
        static List<string> CrewJobs(Game g, Person p)
        {
            var o = Data.JOBS.Where(j => !(g.IsMinor(p) && Data.DANGER_JOBS.Contains(j))).ToList();
            if (!o.Contains(p.Job)) o.Insert(0, p.Job);
            return o;
        }

        /// <summary>Job wheel landed on another job: set_job (an error toasts and the wheel springs back).</summary>
        static void CrewSetJob(MenuCtx c, Person p, string job)
        {
            if (job == p.Job) return;
            string err = CampAct(c, "set_job", p.Id, job);
            if (err == "") c.Toast(p.Name + " is now a " + job + (Data.JOB_DESC.TryGetValue(job, out var d) ? ". " + d : "."));
            else c.Toast(err);
        }

        const float CREW_L1 = 60f, CREW_L2 = 26f;

        static void Crew(MenuCtx c)
        {
            var g = c.G;
            if (PplCrewModal(c)) return;   // Expansion: survivor sheet / NIGHT WATCH (GameUI.Expansion.People.cs)
            var L = PplOn(c) ? Frame(c, "CREW & JOBS", null, new BarBtn("NIGHT WATCH", UiRole.Primary, () => { PplWatch = true; PplWatchPage = 0; }, true, 210f)) : Frame(c, "CREW & JOBS");
            Footer(c, L, "Drag a job or camp wheel to change it. Bites can be hidden: keep Medics at every camp.");
            var alive = g.AlivePeople();
            var counts = new Dictionary<string, int>();
            foreach (var q in alive) counts[q.Job] = counts.TryGetValue(q.Job, out var n0) ? n0 + 1 : 1;
            var parts = Data.JOBS.Select(j => string.Format("{0} {1}", Data.JOB_SHORT[j], counts.TryGetValue(j, out var n) ? n : 0)).ToList();
            if (counts.TryGetValue("Child", out var nk) && nk > 0) parts.Add("KID " + nk);
            c.Fit(L.Top(26f), string.Join("  ·  ", parts), 17, INK2, true);
            var fc = g.FamilyCounts();
            if (fc.Values.Sum() > 0)
                c.Fit(L.Top(22f), string.Format("Families: {0} couple{1} · {2} expecting · {3} bab{4} · {5} child{6} · {7} teen{8}", fc["couples"], fc["couples"] == 1 ? "" : "s",
                    fc["expecting"], fc["babies"], fc["babies"] == 1 ? "y" : "ies", fc["children"], fc["children"] == 1 ? "" : "ren", fc["teens"], fc["teens"] == 1 ? "" : "s"), 16, CAMP_FAMILY, false);

            // one scroll list: the colony-wide auto settings, then a row per survivor
            var view = L.Rest;
            float cw = view.width - 12f;
            float autoH = AutoPanelH(c, cw);
            var bases = g.C.Bases.Where(b => b.Done >= 1.0).ToList();
            var ppl = alive.Select((q, i) => (q, i)).OrderBy(t => { int j = Array.IndexOf(Data.JOBS, t.q.Job); return j < 0 ? 99 : j; }).ThenBy(t => t.i).Select(t => t.q).ToList();
            var hs = ppl.Select(q => 8f + CREW_L1 + CREW_L2 + PplRowH(g, q) + 8f).ToList();
            c.Scroll("crew_list", view, autoH + ROW_GAP + Total(hs, ROW_GAP));
            float y = AutoPanel(c, 0f, cw, 0f) + ROW_GAP;
            var baseNames = bases.Select(b => b.Name).ToList();
            float nameW = cw >= 1240f ? 250f : 210f, jobW = 330f, campW = bases.Count > 1 ? 300f : 220f;
            for (int i = 0; i < ppl.Count; i++)
            {
                var q = ppl[i];
                bool kid = g.IsKid(q), minor = g.IsMinor(q);
                var card = new Rect(0, y, cw, hs[i]);
                Color jc = Data.JOB_COLOR.TryGetValue(q.Job, out var jcc) ? jcc : INK;
                Card(c, card, i, jc);
                var R1 = new UiLayout(new Rect(18f, y + 8f, cw - 28f, CREW_L1), 10f);
                c.Fit(R1.Left(nameW), q.Name, 20, jc, true);
                var jr = R1.Left(jobW);
                if (kid) c.Fit(jr, "Child · too young to work", 17, INK2, false, MenuAlign.Center);
                else
                {
                    var qq = q; var jobs = CrewJobs(g, q);
                    c.Wheel("crew_job_" + q.Id, jr, jobs, Math.Max(0, jobs.IndexOf(q.Job)), k => CrewSetJob(c, qq, jobs[k]), true, true, 3, 18);
                }
                // camp (move_to_base): a wheel of the camps when there are several
                var cr = R1.Left(campW);
                int bi = Math.Max(0, bases.FindIndex(b => b.Id == q.Base));
                if (bases.Count > 1)
                {
                    var qm = q;
                    c.Wheel("crew_camp_" + q.Id, cr, baseNames, bi, k => { var nb = bases[k]; CampToast(c, string.Format("{0} is moving to {1}.", qm.First, nb.Name), "move_to_base", qm.Id, nb.Id); }, true, true, 3, 17);
                }
                else c.Chip(UiLayout.Center(cr, cr.width, 34f), bases.Count > 0 ? bases[bi].Name : "-", UiRole.Neutral, 16);
                int pid = q.Id; bool hold = q.Hold, guard = q.Guard;
                var tg = UiLayout.Columns(R1.Rest, 10f, 1f, 1.1f);
                c.Btn(tg[0], (hold ? "[x] " : "[  ] ") + "STAY", hold ? UiRole.Accent : UiRole.Neutral, () => CampToast(c, "", "set_hold", pid, !hold), true, hold, 17);
                c.Btn(tg[1], (guard ? "[x] " : "[  ] ") + "GUARD", guard ? UiRole.Accent : UiRole.Neutral, () => CampToast(c, "", "set_guard", pid, !guard), !minor, guard, 17);
                // line 2: status · family
                var stt = CrewStatusText(g, q);
                string ft = g.FamilyText(q);
                var R2 = UiLayout.Columns(new Rect(18f, y + 8f + CREW_L1, cw - 28f, CREW_L2), 12f, 1f, ft != "" ? 1.2f : 0.001f);
                c.Fit(R2[0], stt.text, 16, stt.col, false);
                if (ft != "") c.Fit(R2[1], ft, 15, CAMP_FAMILY, false, MenuAlign.Right);
                PplRow(c, q, 18f, cw - 28f, y + 8f + CREW_L1 + CREW_L2);   // Expansion: traits, stress, fatigue
                y += hs[i] + ROW_GAP;
            }
            c.EndScroll();
        }

        const string AUTO_HELP = "Everyone works their job on their own. Scavengers search buildings inside this radius around every camp and outpost (searched ones get an X), fighters answer SOS calls inside it while there are beds. Beyond it, send a squad (SQUADS > SCAVENGE). Tick STAY to keep someone home. Searched buildings inside a safe zone get converted (Auto-convert); 1 in 5 people stand the night watch (Auto-guards, or tick GUARD).";

        static float AutoPanelH(MenuCtx c, float iw) => 12f + BTN_H + 10f + BTN_H + 10f + c.H_(AUTO_HELP, 15, false, iw - 36f) + 12f;

        /// <summary>main.gd _auto_panel: the colony-wide auto toggles and the auto-scavenge radius (set_auto). The
        /// original's HSlider (step 2) is a wheel of radii.</summary>
        static float AutoPanel(MenuCtx c, float x, float iw, float y)
        {
            var g = c.G;
            float h = AutoPanelH(c, iw);
            Card(c, new Rect(x, y, iw, h), 0, GREEN, true);
            float cx = x + 18f, cw = iw - 36f, cy = y + 12f;
            var tg = UiLayout.Even(new Rect(cx, cy, cw, BTN_H), CREW_AUTO_KEYS.Length, 10f);
            for (int i = 0; i < CREW_AUTO_KEYS.Length; i++)
            {
                string key = CREW_AUTO_KEYS[i].key; bool on = g.C.Auto.Get(key);
                c.Btn(tg[i], CREW_AUTO_KEYS[i].label + (on ? ": ON" : ": OFF"), on ? UiRole.Primary : UiRole.Neutral, () => CampToast(c, "", "set_auto", key, !on), true, on, 18);
            }
            cy += BTN_H + 10f;
            double lo = Game.AUTO_R_MIN, hi = g.AutoRMax(), cur = g.AutoRadius();
            var vals = new List<double>();
            for (double v = lo; v <= hi + 0.001; v += 2.0) vals.Add(v);
            if (vals.Count == 0 || vals[vals.Count - 1] < hi - 0.001) vals.Add(hi);
            int sel = 0;
            for (int i = 0; i < vals.Count; i++) if (vals[i] <= cur + 0.001) sel = i;
            var R = new UiLayout(new Rect(cx, cy, cw, BTN_H), 12f);
            c.Fit(R.Left(220f), "AUTO RADIUS", 19, AMBER, true);
            var names = vals.Select(v => string.Format("{0} tiles", (int)v)).ToList();
            c.Wheel("crew_auto_r", R.Left(Math.Min(480f, R.Rest.width * 0.6f)), names, sel, k => CampToast(c, "", "set_auto", "r", vals[k]), true, true, 3, 19);
            c.Fit(R.Rest, string.Format("now {0} tiles · max {1}", (int)cur, (int)hi), 17, INK2, false);
            cy += BTN_H + 10f;
            float hh = c.H_(AUTO_HELP, 15, false, cw);
            c.Para(new Rect(cx, cy, cw, hh), AUTO_HELP, 15, INK2);
            return y + h;
        }

        // ---------------- trap sheet (main.gd _sheet_trap, _trap_stats) ----------------
        public static string TrapStats(int kind, int lv, bool shortForm = false)
        {
            var td = Data.TRAPS[kind];
            var parts = new List<string> { string.Format("{0} uses", (int)Game.TrapHpLvl(kind, lv)) };
            double kc = Game.TrapKill(kind, lv);
            if (kc > 0.0) parts.Add(string.Format("{0}% stop", (int)Math.Round(kc * 100.0)));
            double sl = Game.TrapSlow(kind, lv);
            if (sl > 0.0 && !shortForm) parts.Add(string.Format("holds ~{0} min", (int)Math.Round(sl / Game.Z_SPEED * 60.0)));
            double r = Game.TrapRadius(kind, lv);
            if (r > 0.0) parts.Add(td.Fx == "lure" ? string.Format("draws from {0:0} tiles", r) : td.Fx == "fire" ? string.Format("burns {0:0.0} tiles around", r) : string.Format("guards answer within {0:0} tiles", r));
            return string.Join(" · ", parts);
        }

        public static Rect TrapSheetRect(float w, float h) => SheetRect(w, h, 920f, 232f);

        public static void TrapSheet(MenuCtx c, int k, Action close, Action trapTool) { TrapSheetBody(c, k, close, trapTool); Flush(c); }

        static void TrapSheetBody(MenuCtx c, int k, Action close, Action trapTool)
        {
            var g = c.G; var D = Data.Instance;
            if (!g.S.Traps.TryGetValue(k, out var t)) { close?.Invoke(); return; }
            var td = Data.TRAPS[t.Kind];
            var L = SheetFrame(c, TrapSheetRect(c.W, c.H), "TRAP · " + td.Name, close);
            int lv = Game.TrapLvl(t);
            var pos = new Vector2Int(k % D.W, k / D.W);
            bool mine = t.Col == g.CurCol;
            if (mine)
            {
                var B = new UiLayout(L.Bottom(BTN_H), 10f);
                c.Btn(B.Right(170f), "REMOVE", UiRole.Danger, () => { string e = CampAct(c, "remove_trap", k); c.Toast(e != "" ? e : "Trap taken up."); if (e == "") close?.Invoke(); });
                c.Btn(B.Right(190f), "TRAP TOOL", UiRole.Primary, trapTool);
                if (lv + 1 < Data.TRAP_LEVELS)
                {
                    bool locked = lv + 1 >= 2 && !g.HasUnlock(Data.TRAP_L3_UNLOCK);
                    if (locked) c.Btn(B.Rest, "LEVEL 3 LOCKED", UiRole.Disabled, null, false, false, 18, "needs " + g.UnlockSourceName(Data.TRAP_L3_UNLOCK));
                    else c.Btn(B.Rest, string.Format("UPGRADE TO LEVEL {0}", lv + 2), UiRole.Accent, () => CampToast(c, "", "upgrade_trap", k), true, false, 18,
                        Game.FmtCost(g.TrapUpgradeCost(t.Kind, lv)) + " · " + TrapStats(t.Kind, lv + 1, true));
                }
                else c.Chip(UiLayout.Center(B.Rest, 200f, 36f), "TOP LEVEL", UiRole.Primary, 17);
            }
            string l1 = string.Format("Level {0} / {1} · {2} · {3} · {4}", lv + 1, Data.TRAP_LEVELS, Game.StreetName(pos),
                t.Hp > 0.0 ? string.Format("{0} / {1} uses left", (int)t.Hp, (int)Game.TrapHpMax(t)) : "spent, waiting to be cleared and reset", td.Desc);
            float h1 = Math.Min(Math.Max(20f, L.Rest.height - 30f), c.H_(l1, 15, false, L.Rest.width));
            c.Para(L.Top(h1), l1, 15, t.Hp > 0.0 ? INK2 : AMBER);
            c.Fit(L.Top(22f), TrapStats(t.Kind, lv), 15, INK2, false);
            if (!mine) c.Fit(L.Top(24f), "Belongs to another colony.", 16, RED, true);
        }

        // ---------------- trap-line toolbar (main.gd _render_trap_toolbar, _on_trap_changed) ----------------
        /// <summary>Trap kinds per row on the trap bar: all on one row when the sheet is wide, else rows of 4.</summary>
        static int TrapCols(float sheetW) => sheetW >= 1000f ? Data.TRAPS.Length : 4;
        public static Rect TrapBarRect(float w, float h)
        {
            var r = SheetRect(w, h, 1080f, 100f);
            int rows = (Data.TRAPS.Length + TrapCols(r.width) - 1) / TrapCols(r.width);
            return SheetRect(w, h, 1080f, 24f + BTN_H + 8f + rows * (60f + 8f) + 30f + BTN_H + 8f);
        }

        /// <summary>tiles/ok/anchor: the line drawn on the map; pick(kind), undo, clear, confirm, done: toolbar actions.</summary>
        public static void TrapBar(MenuCtx c, int kind, IList<Vector2Int> tiles, bool anchored, Action<int> pick, Action undo, Action clear, Action confirm, Action done)
        {
            var g = c.G;
            var L = new UiLayout(c.Panel(TrapBarRect(c.W, c.H)), 8f);
            var T = new UiLayout(L.Top(BTN_H), 10f);
            c.Btn(T.Right(130f), "DONE", UiRole.Neutral, done);
            string hint = string.Format("Tap a spot, then the next one: traps fill the line between them. Tap the last spot again for a new line. Traps reach twice as far as fences. {0} / {1} placed.", g.TrapsOf(), Data.TRAP_MAX);
            c.Para(T.Rest, hint, 14, INK2);
            int n = Data.TRAPS.Length;
            int tc = TrapCols(TrapBarRect(c.W, c.H).width), trows = (n + tc - 1) / tc;
            var kr = L.Top(trows * 68f - 8f);
            for (int i = 0; i < n; i++)
            {
                var kc = UiLayout.Cell(kr, tc, i, 60f, 8f);
                var td = Data.TRAPS[i];
                bool un = g.TrapUnlocked(i);
                int ii = i;
                c.Btn(kc, td.Name, kind == i ? UiRole.Accent : UiRole.Primary, () => pick(ii), un, kind == i, 17, un ? CampShortCost(g.TrapCost(i)) : "needs " + g.UnlockSourceName(td.Unlock));
            }
            string info;
            bool okAny = false;
            if (tiles.Count == 0) info = Data.TRAPS[kind].Desc + (anchored ? "  Now tap the next corner or spot." : "");
            else
            {
                var pv = g.TrapPreview(tiles.ToList(), kind);
                okAny = pv.N > 0;
                info = string.Format("{0} trap{1} · {2}", pv.N, pv.N == 1 ? "" : "s", pv.N > 0 ? Game.FmtCost(pv.Cost) : "nothing to place");
                if (pv.N < tiles.Count && pv.Err != "") info += " · red: " + pv.Err;
                if (pv.N > 0 && !g.CanAfford(pv.Cost)) info += " · not enough materials";
            }
            info += "  ·  " + TrapStats(kind, 0, true);
            var B = new UiLayout(L.Bottom(BTN_H), 10f);
            c.Btn(B.Right(160f), "CONFIRM", UiRole.Accent, confirm, okAny);
            c.Btn(B.Right(130f), "CLEAR", UiRole.Danger, clear, tiles.Count > 0 || anchored);
            c.Btn(B.Right(130f), "UNDO", UiRole.Neutral, undo, tiles.Count > 0);
            var ir = new Rect(B.Rest.x, L.Rest.y, B.Rest.width, B.Rest.yMax - L.Rest.y);
            c.Para(ir, info, 15, AMBER);
            Flush(c);
        }

        // ---------------- BUILD OUTPOST bar (main.gd _start_outpost banner + CANCEL) ----------------
        public static Rect OutpostBarRect(float w, float h) => SheetRect(w, h, 880f, 132f);

        public static void OutpostBar(MenuCtx c, Action cancel)
        {
            var g = c.G;
            var L = new UiLayout(c.Panel(OutpostBarRect(c.W, c.H)), 8f);
            var cols = new UiLayout(L.Rest, 14f);
            c.Btn(UiLayout.Center(cols.Right(170f), 170f, BTN_H), "CANCEL", UiRole.Danger, cancel);
            var R = new UiLayout(cols.Rest, 6f);
            c.Header(R.Top(42f), "BUILD OUTPOST", 22);
            string t = string.Format("Tap a building to take it over as an outpost ({0}, instant). Anywhere on the map. {1} / {2} outposts.", Game.FmtCost(Game.OUTPOST_COST), g.Outposts(), Game.OUTPOST_MAX);
            c.Para(R.Rest, t, 15, g.CanAfford(Game.OUTPOST_COST) ? INK : RED);
            Flush(c);
        }

        // ---------------- building sheet: GUT (main.gd _sheet_gut) ----------------
        /// <summary>Height the GUT row needs on a building sheet (0 when it shows nothing).</summary>
        public static float GutRowHeight(Game g, int l)
        {
            if (l < 0 || g.ConvOf(l) != null) return 0f;
            if (g.IsGutted(l) || g.Gutting(l)) return 30f;
            if (g.GutError(l) == "") return 80f;
            return g.ConvPlaceError(l) == "" ? 30f : 0f;
        }

        public static void GutRow(MenuCtx c, Rect r, int l) { GutRowBody(c, r, l); Flush(c); }

        static void GutRowBody(MenuCtx c, Rect r, int l)
        {
            var g = c.G; var D = Data.Instance;
            if (r.height <= 0f) return;
            if (g.IsGutted(l))
            {
                int by = g.GuttedBy(l);
                c.Para(r, string.Format("GUTTED{0}: stripped to the shell (wood, drywall, wiring, fittings all taken out). Ready to become a camp building.", by == g.CurCol ? "" : " by " + (g.ColonyAt(by)?.Name ?? "someone")), 14, new Color(0.95f, 0.75f, 0.45f));
                return;
            }
            if (g.Gutting(l))
            {
                bool mine = g.S.Guts.TryGetValue(l, out var j) && j.Col == g.CurCol;
                c.Fit(r, mine ? string.Format("A crew is gutting it: about {0} s left.", (int)Math.Ceiling(g.GutLeft(l))) : "Another colony is gutting it.", 15, AMBER, false);
                return;
            }
            string ge = g.GutError(l);
            if (ge == "")
            {
                float bw = Math.Min(r.width, 640f);
                c.Btn(new Rect(r.x, r.y, bw, BTN_H), string.Format("GUT IT ({0} s)", (int)Data.GUT_SEC), UiRole.Primary, () =>
                {
                    string e = CampAct(c, "gut", l);
                    c.Toast(e != "" ? e : "A crew is gutting it.");
                }, true, false, 19, "strip it to the shell for " + CampShortCost(D.GutYield(l)));
                if (g.ConvPlaceError(l) == "") c.Fit(new Rect(r.x, r.y + BTN_H + 2f, r.width, 20f), "Buildings must be gutted before they can become a farm, workshop, garage, housing or any other camp building.", 14, INK2, false);
            }
            else if (g.ConvPlaceError(l) == "") c.Fit(r, "Gut it before converting: " + ge, 14, RED, false);
        }
    }
}
