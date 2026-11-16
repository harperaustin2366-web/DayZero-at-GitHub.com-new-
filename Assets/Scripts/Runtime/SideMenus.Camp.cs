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

        // ---------------- CAMPS & OUTPOSTS (main.gd _show_bases) ----------------
        static void Camps(MenuCtx c)
        {
            var g = c.G; var D = Data.Instance;
            var p = Frame(c, "CAMPS & OUTPOSTS");
            float x = p.x + 18, iw = p.width - 36, y = p.y + 66;
            string intro = string.Format("{0} / {1} outposts. Each camp has a safe zone the dead won't enter until the city falls.", g.Outposts(), Game.OUTPOST_MAX);
            c.Label(new Rect(x, y, iw, 24), intro, 15, INK2, false, false); y += 32f;
            const float RH = 74f, GAP = 8f;
            int per = Math.Max(1, (int)((p.yMax - 70f - y + GAP) / (RH + GAP)));
            var bases = g.C.Bases;
            int pages = Math.Max(1, (bases.Count + per - 1) / per);
            CampsPage = Math.Max(0, Math.Min(CampsPage, pages - 1));
            foreach (var b in bases.Skip(CampsPage * per).Take(per))
            {
                var r = new Rect(x, y, iw, RH);
                c.Box(r, new Color(0.12f, 0.13f, 0.10f, 0.95f));
                var bb = b;
                float bx = r.xMax - 10f;
                if (b.Done >= 1.0) { bx -= 150f; c.Button(new Rect(bx, r.y + 8, 150, 44), "FACILITIES", "sel", 16, () => OpenBase(c.St, bb.Id, true)); bx -= 8f; }
                bx -= 96f;
                c.Button(new Rect(bx, r.y + 8, 96, 44), "VIEW", "olive", 16, () => { PendingFly = new Vector2(bb.Pos.x + 0.5f, bb.Pos.y + 0.5f); c.St.Open = ""; });
                c.Label(new Rect(r.x + 12, r.y + 8, bx - r.x - 20, 28), g.BaseLabel(b), 19, b.Main ? GREEN : CAMP_BLUE, true, false);
                string info = b.Done < 1.0 ? string.Format("Under construction: {0}%", (int)(b.Done * 100))
                    : string.Format("{0} · {1} · {2}/{3} beds · defense {4}{5}{6}", D.DistrictAt(b.Pos).Name, g.BaseLevelDef(b).Name, g.BasePeople(b.Id).Count, g.BaseBeds(b), (int)g.Defense(b),
                        g.IsEnclosed(b) ? " · fenced" : "", g.IsSelfSustaining(b) ? " · SELF-SUSTAINING" : "");
                c.Label(new Rect(r.x + 12, r.y + 44, r.width - 24, 24), info, 15, b.Done < 1.0 ? AMBER : INK2, false, false);
                y += RH + GAP;
            }
            Pager(c, p, CampsPage, pages, v => CampsPage = v, "Tap BUILD OUTPOST in ACTIONS, then a building, to start a new outpost.");
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
            var p = Frame(c, g.BaseLabel(b));
            if (BaseFromList) c.Button(new Rect(p.xMax - 142 - 150, p.y + 10, 142, 48), "ALL CAMPS", "olive", 17, () => { c.St.Open = "camps"; });
            float x = p.x + 18, iw = p.width - 36, y = p.y + 66;
            var bal = CampBalanceText(g, b);
            string bt = (bal.ok ? "SELF-SUSTAINING  " : "Not self-sustaining yet  ") + bal.text;
            float bh = c.H_(bt, 15, false, iw);
            c.Label(new Rect(x, y, iw, bh), bt, 15, bal.ok ? GREEN : AMBER); y += bh + 2f;
            c.Label(new Rect(x, y, iw, 20), string.Format("Everything here is instant: pay the cost and it's built. Building slots used: {0} / {1}.", g.SlotsUsed(b), g.BaseSlots(b)), 13, INK2, false, false);
            y += 24f;
            float top = y;
            // the level card sits on page 1 only; measure it on a scratch list so every page splits the rows the same way
            float cardEnd = LevelCard(new MenuCtx { G = g, W = c.W, H = c.H, St = c.St, Measure = c.Measure, Act = c.Act, Toast = c.Toast }, b, x, iw, y) + 8f;
            if (BasePage == 0) y = LevelCard(c, b, x, iw, y) + 8f;

            // facility rows, paged
            const float BW = 240f;
            var rows = new List<(string uid, float h)>();
            foreach (var kv in Data.UPGRADES)
            {
                if (kv.Key == "radio" && !b.Main) continue;
                string desc = kv.Value.Desc ?? "";
                var cst = g.UpgradeCost(b, kv.Key);
                float bh0 = cst.Count > 0 ? BtnH(c, "UPGRADE\n" + Game.FmtCost(cst), 13, BW) : 52f;
                float h = Math.Max(bh0 + 4f, 24f + c.H_(desc, 14, false, iw - BW - 16f));
                if (kv.Key == "armory" && b.U("armory") > 0) h += 72f;
                rows.Add((kv.Key, h));
            }
            float avail0 = p.yMax - 70f - cardEnd, availN = p.yMax - 70f - top;
            var starts = new List<int> { 0 };
            float used = 0f, cap = avail0;
            for (int i = 0; i < rows.Count; i++)
            {
                if (used > 0f && used + rows[i].h > cap) { starts.Add(i); used = 0f; cap = availN; }
                used += rows[i].h + 6f;
            }
            int pages = starts.Count;
            BasePage = Math.Max(0, Math.Min(BasePage, pages - 1));
            if (BasePage > 0) y = top;
            int from = starts[BasePage], to = BasePage + 1 < pages ? starts[BasePage + 1] : rows.Count;
            for (int i = from; i < to; i++)
            {
                string uid = rows[i].uid;
                var u = Data.UPGRADES[uid];
                int lv = b.U(uid);
                float lw = iw - BW - 16f;
                c.Label(new Rect(x, y, lw, 24), string.Format("{0}  (level {1}/{2}){3}", u.Name, lv, u.Max, (u.Slot ?? true) ? "" : "  · no slot needed"), 18, lv > 0 ? GREEN : INK, true, false);
                string desc = u.Desc ?? "";
                float dh = c.H_(desc, 14, false, lw);
                if (dh > 0f) c.Label(new Rect(x, y + 24, lw, dh), desc, 14, INK2);
                var cost = g.UpgradeCost(b, uid);
                string blk = g.UpgradeBlock(b, uid);
                var br = new Rect(x + iw - BW, y + 2, BW, cost.Count > 0 ? BtnH(c, "UPGRADE\n" + Game.FmtCost(cost), 13, BW) : 52f);
                if (uid == "garage" && !b.GarageOk) c.Label(br, "No garage\nat this site", 14, RED, false, true, MenuAlign.Right);
                else if (cost.Count == 0) c.Label(new Rect(br.x, br.y + 12, br.width, 26), "MAX", 16, GREEN, true, false, MenuAlign.Right);
                else if (blk != "") c.Label(br, blk, 13, AMBER, false, true, MenuAlign.Right);
                else
                {
                    string uu = uid; int bid = b.Id;
                    c.Button(br, (lv == 0 ? "BUILD" : "UPGRADE") + "\n" + Game.FmtCost(cost), g.CanAfford(cost) ? "sel" : "olive", 13,
                        () => CampToast(c, "", "do_upgrade", bid, uu));
                }
                float ry = y + rows[i].h - 72f;
                if (uid == "armory" && lv > 0)
                {
                    // main.gd _armory_rows: arsenal, "10 AMMO" (craft_ammo); loadouts and gun assembly live in the ARMORY screen
                    var guns = new List<string>();
                    foreach (var gid in Data.WEAPON_ORDER)
                    {
                        int n = g.C.Weapons.Get(gid, 0);
                        if (n > 0) guns.Add(string.Format("{0} {1}", n, (Data.WEAPONS.TryGetValue(gid, out var wd) ? wd.Name : gid).ToLowerInvariant()));
                    }
                    c.Label(new Rect(x + 16, ry + 2, iw - 16, 20), "Arsenal: " + (guns.Count > 0 ? string.Join(", ", guns) : "no guns") + ". Loadouts, gun parts and assembly are in the ARMORY screen.", 14, AMBER, false, false);
                    c.Button(new Rect(x + 16, ry + 24, 240, 46), "10 AMMO · " + CampShortCost(Game.AMMO_CRAFT), "olive", 14, () => CampToast(c, "+10 ammo", "craft_ammo"));
                }
                y += rows[i].h + 6f;
            }
            Pager(c, p, BasePage, pages, v => BasePage = v, "");
        }

        /// <summary>main.gd _base_level_card: the camp's level, the path, the next level and UPGRADE (upgrade_base).</summary>
        static float LevelCard(MenuCtx c, Base b, float x, float iw, float y)
        {
            var g = c.G;
            var cur = g.BaseLevelDef(b);
            var arr = b.Main ? Data.BASE_LEVELS["main"] : Data.BASE_LEVELS["outpost"];
            int l = b.Lvl;
            string stats = string.Format("{0} beds ({1} living here) · {2} building slots · zone {3:0} tiles · storage {4} each · +{5} defense{6}",
                g.BaseBeds(b), g.BasePeople(b.Id).Count, g.BaseSlots(b), g.BaseZoneR(b), (int)g.StorageCap(), cur.Defense, g.IsEnclosed(b) ? string.Format(" · fenced: {0} tiles", g.EnclosedCount(b)) : "");
            string path = string.Join(" → ", arr.Select((a, i) => i == l ? "[" + a.Name + "]" : a.Name));
            float sh = c.H_(stats, 14, false, iw - 24f);
            bool next = l + 1 < arr.Length;
            string err = next ? g.CanUpgradeBase(b) : "";
            string nxt = "";
            if (next)
            {
                var nx = arr[l + 1];
                nxt = string.Format("Next: {0} · {1} beds · {2} slots · zone {3:0} · +{4} defense · storage {5}{6}", nx.Name, nx.Beds, nx.Slots, nx.Zone, nx.Defense, nx.Storage,
                    nx.MinPeople.HasValue ? string.Format(" · needs {0} people", nx.MinPeople.Value) : "");
            }
            const float UBW = 300f;
            string ubt = next ? "UPGRADE BASE\n" + Game.FmtCost(arr[l + 1].Cost) : "";
            float ubh = next ? BtnH(c, ubt, 12, UBW) : 0f;
            float nh = next ? Math.Max(ubh, c.H_(nxt, 14, false, iw - 24f - UBW - 10f)) : 0f;
            float eh = err != "" ? c.H_(err, 13, false, iw - 24f) + 2f : 0f;
            float h = 12f + 28f + sh + 4f + 22f + (next ? nh + 6f : 0f) + eh + 8f;
            var r = new Rect(x, y, iw, h);
            c.Box(r, new Color(0.13f, 0.15f, 0.12f, 0.97f));
            c.Box(new Rect(r.x, r.y, 3, r.height), new Color(0.57f, 0.42f, 0.17f));
            float cy = y + 10f, cx = x + 12f, cw = iw - 24f;
            c.Label(new Rect(cx, cy, cw, 28), b.Name + " · " + cur.Name, 20, AMBER, true, false); cy += 30f;
            c.Label(new Rect(cx, cy, cw, sh), stats, 14, INK); cy += sh + 4f;
            c.Label(new Rect(cx, cy, cw, 20), path, 14, INK2, false, false); cy += 24f;
            if (next)
            {
                c.Label(new Rect(cx, cy, cw - UBW - 10f, nh), nxt, 14, INK);
                int bid = b.Id;
                c.Button(new Rect(cx + cw - UBW, cy, UBW, ubh), ubt, err == "" ? "sel" : "olive", 12,
                    () => CampToast(c, "", "upgrade_base", bid));
                cy += nh + 6f;
                if (err != "") c.Label(new Rect(cx, cy, cw, eh), err, 13, RED);
            }
            else c.Label(new Rect(cx + cw - 300f, y + 10f, 300, 26), "Top level.", 16, GREEN, true, false, MenuAlign.Right);
            return y + h;
        }

        // ---------------- camp sheet (main.gd _sheet_base) ----------------
        public static Rect BaseSheetRect(float w, float h) { float pw = Math.Min(940f, w - 200f), ph = 236f; return new Rect(w / 2f - pw / 2f, h - 62f - ph - 10f, pw, ph); }

        public static void BaseSheet(MenuCtx c, int bid, Action close)
        {
            var g = c.G; var D = Data.Instance;
            var b = g.BaseById(bid);
            if (b == null) { close?.Invoke(); return; }
            var p = BaseSheetRect(c.W, c.H);
            c.Box(p, PANEL);
            c.Box(new Rect(p.x, p.y, p.width, 3), GREEN);
            float x = p.x + 16, iw = p.width - 32, y = p.y + 10;
            c.Label(new Rect(x, y, iw - 140, 30), g.BaseLabel(b), 22, GREEN, true, false);
            c.Button(new Rect(p.xMax - 126, p.y + 8, 112, 44), "CLOSE", "dark", 18, close);
            y += 44f;
            string dn = D.DistrictAt(b.Pos).Name;
            if (b.Done < 1.0)
            {
                c.Label(new Rect(x, y, iw, 24), string.Format("{0} · under construction {1}%. Engineers speed it up.", dn, (int)(b.Done * 100)), 16, INK2, false, false);
                return;
            }
            var ppl = g.BasePeople(b.Id);
            c.Label(new Rect(x, y, iw, 22), string.Format("{0} · {1} · {2}/{3} beds · defense {4}", dn, g.BaseLevelDef(b).Name, ppl.Count, g.BaseBeds(b), (int)g.Defense(b)), 16, INK2, false, false); y += 24f;
            c.Label(new Rect(x, y, iw, 20), g.IsEnclosed(b) ? string.Format("Fenced in: {0} tiles enclosed (+{1} defense)", g.EnclosedCount(b), (int)g.EnclosureDefense(b)) : "Not enclosed. Snap a closed fence loop around it (DEFENSE) to grow it.", 14, g.IsEnclosed(b) ? GREEN : INK2, false, false); y += 22f;
            var counts = new Dictionary<string, int>();
            foreach (var q in ppl) counts[q.Job] = counts.TryGetValue(q.Job, out var n0) ? n0 + 1 : 1;
            var parts = new List<string>();
            foreach (var j in Data.JOBS) if (counts.TryGetValue(j, out var n1)) parts.Add(n1 + " " + Data.JOB_SHORT[j]);
            if (counts.TryGetValue("Child", out var nk)) parts.Add(nk + " KID");
            var fac = new List<string>();
            foreach (var k in new[] { "medbay", "armory", "garage" }) if (b.U(k) > 0) fac.Add(Data.UPGRADES[k].Name + " L" + b.U(k));
            if (b.Veh > 0) fac.Add(b.Veh + " vehicle(s)");
            c.Label(new Rect(x, y, iw, 22), (parts.Count > 0 ? string.Join("  ", parts) : "Nobody assigned here yet.") + "   ·   Facilities: " + (fac.Count > 0 ? string.Join(", ", fac) : "none yet"), 15, INK, false, false); y += 24f;
            if (b.Site != "") { c.Label(new Rect(x, y, iw, 20), Game.SiteBonusText(b.Site), 14, AMBER, false, false); y += 22f; }
            var bal = CampBalanceText(g, b);
            c.Label(new Rect(x, y, iw, 20), (bal.ok ? "SELF-SUSTAINING  " : "") + bal.text, 14, bal.ok ? GREEN : AMBER, false, false);
            // buttons: FACILITIES · UPGRADE BASE · CREW / SQUADS (n) · TASK FORCE
            float by = p.yMax - 60f, gap = 8f;
            bool up = g.NextLevelCost(b).Count > 0 || b.Lvl == 0;
            int nb = up ? 5 : 4;
            float bw = (iw - gap * (nb - 1)) / nb, bx = x;
            void Btn(string t, string style, Action a) { c.Button(new Rect(bx, by, bw, 50), t, style, 16, a); bx += bw + gap; }
            Btn("FACILITIES", "sel", () => { close?.Invoke(); OpenBase(c.St, bid); });
            if (up) Btn("UPGRADE BASE", "olive", () => { close?.Invoke(); OpenBase(c.St, bid); });
            Btn("CREW", "olive", () => { close?.Invoke(); CrewPage = 0; c.St.Open = "crew"; });
            Btn(string.Format("SQUADS ({0})", g.CampSquads(bid).Count), "olive", () => { close?.Invoke(); c.St.Camp = bid; Open(c.St, "squads"); });
            Btn("TASK FORCE", "olive", () => { close?.Invoke(); c.St.Camp = bid; Open(c.St, "taskforce"); });
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

        /// <summary>Step to the next job the survivor can take (the original's job dropdown, as a tap-to-cycle button).</summary>
        static void CrewCycleJob(MenuCtx c, Person p, int dir)
        {
            var jobs = Data.JOBS; int n = jobs.Length;
            int i = Math.Max(0, Array.IndexOf(jobs, p.Job));
            string firstErr = null;
            for (int k = 1; k < n; k++)
            {
                string next = jobs[(((i + dir * k) % n) + n) % n];
                if (c.G.IsMinor(p) && Data.DANGER_JOBS.Contains(next)) continue;   // greyed out in the original's dropdown
                string err = CampAct(c, "set_job", p.Id, next);
                if (err == "") { c.Toast(p.Name + " is now a " + next + (Data.JOB_DESC.TryGetValue(next, out var d) ? ". " + d : ".")); return; }
                if (firstErr == null) firstErr = err;
            }
            c.Toast(firstErr ?? "No other job available.");
        }

        static void Crew(MenuCtx c)
        {
            var g = c.G;
            var p = Frame(c, "CREW & JOBS");
            float x = p.x + 18, iw = p.width - 36, y = p.y + 64;
            var alive = g.AlivePeople();
            var counts = new Dictionary<string, int>();
            foreach (var q in alive) counts[q.Job] = counts.TryGetValue(q.Job, out var n0) ? n0 + 1 : 1;
            var parts = Data.JOBS.Select(j => string.Format("{0} {1}", Data.JOB_SHORT[j], counts.TryGetValue(j, out var n) ? n : 0)).ToList();
            if (counts.TryGetValue("Child", out var nk) && nk > 0) parts.Add("KID " + nk);
            c.Label(new Rect(x, y, iw, 22), string.Join("  ·  ", parts), 15, INK2, false, false); y += 24f;
            var fc = g.FamilyCounts();
            if (fc.Values.Sum() > 0)
            {
                c.Label(new Rect(x, y, iw, 20), string.Format("Families: {0} couple{1} · {2} expecting · {3} bab{4} · {5} child{6} · {7} teen{8}", fc["couples"], fc["couples"] == 1 ? "" : "s",
                    fc["expecting"], fc["babies"], fc["babies"] == 1 ? "y" : "ies", fc["children"], fc["children"] == 1 ? "" : "ren", fc["teens"], fc["teens"] == 1 ? "" : "s"), 14, CAMP_FAMILY, false, false);
                y += 22f;
            }
            y = AutoPanel(c, x, iw, y + 2f) + 6f;

            // survivor rows: name · < job > · camp · STAY · GUARD · status (+ family line)
            var bases = g.C.Bases.Where(b => b.Done >= 1.0).ToList();
            var ppl = alive.Select((q, i) => (q, i)).OrderBy(t => { int j = Array.IndexOf(Data.JOBS, t.q.Job); return j < 0 ? 99 : j; }).ThenBy(t => t.i).Select(t => t.q).ToList();
            const float RH = 48f, GAP = 4f;
            var hs = ppl.Select(q => RH + (g.FamilyText(q) != "" ? 20f : 0f)).ToList();
            float avail = p.yMax - 66f - y;
            var starts = Pages(hs, avail, GAP);
            int pages = starts.Count;
            CrewPage = Math.Max(0, Math.Min(CrewPage, pages - 1));
            int from = starts[CrewPage], to = CrewPage + 1 < pages ? starts[CrewPage + 1] : ppl.Count;
            for (int i = from; i < to; i++)
            {
                var q = ppl[i];
                bool kid = g.IsKid(q), minor = g.IsMinor(q);
                float rx = x;
                c.Label(new Rect(rx, y + 12, 170, 24), q.Name, 17, Data.JOB_COLOR.TryGetValue(q.Job, out var jc) ? jc : INK, false, false); rx += 176f;
                if (kid) { c.Label(new Rect(rx, y + 12, 218, 24), "Child · too young to work", 15, INK2, false, false); rx += 222f; }
                else
                {
                    var qq = q;
                    c.Button(new Rect(rx, y, 46, RH - 2), "<", "olive", 18, () => CrewCycleJob(c, qq, -1)); rx += 48f;
                    c.Box(new Rect(rx, y, 122, RH - 2), new Color(0.07f, 0.08f, 0.06f, 0.82f));
                    c.Label(new Rect(rx, y + 12, 122, 24), q.Job, 16, INK, true, false, MenuAlign.Center); rx += 124f;
                    c.Button(new Rect(rx, y, 46, RH - 2), ">", "olive", 18, () => CrewCycleJob(c, qq, 1)); rx += 50f;
                }
                // camp (move_to_base): tap to move them to the next camp
                int bi = Math.Max(0, bases.FindIndex(b => b.Id == q.Base));
                string bn = bases.Count > 0 ? bases[bi].Name : "-";
                if (bases.Count > 1)
                {
                    var nb = bases[(bi + 1) % bases.Count]; var qm = q;
                    c.Button(new Rect(rx, y, 170, RH - 2), bn, "dark", 15, () => CampToast(c, string.Format("{0} is moving to {1}.", qm.First, nb.Name), "move_to_base", qm.Id, nb.Id));
                }
                else { c.Box(new Rect(rx, y, 170, RH - 2), new Color(0.07f, 0.08f, 0.06f, 0.6f)); c.Label(new Rect(rx + 6, y + 11, 158, 22), bn, 15, INK2, false, false, MenuAlign.Center); }
                rx += 176f;
                int pid = q.Id; bool hold = q.Hold, guard = q.Guard;
                c.Button(new Rect(rx, y, 92, RH - 2), (hold ? "[x] " : "[  ] ") + "STAY", hold ? "sel" : "dark", 14, () => CampToast(c, "", "set_hold", pid, !hold)); rx += 96f;
                c.Button(new Rect(rx, y, 104, RH - 2), (guard ? "[x] " : "[  ] ") + "GUARD", guard ? "sel" : "dark", 14, () => CampToast(c, "", "set_guard", pid, !guard), !minor); rx += 110f;
                var stt = CrewStatusText(g, q);
                c.Label(new Rect(rx, y + 11, x + iw - rx, 22), stt.text, 15, stt.col, false, false);
                string ft = g.FamilyText(q);
                if (ft != "") c.Label(new Rect(x + 30, y + RH, iw - 30, 20), ft, 14, CAMP_FAMILY, false, false);
                y += hs[i] + GAP;
            }
            Pager(c, p, CrewPage, pages, v => CrewPage = v, "Bites can be hidden: keep Medics at every camp.");
        }

        /// <summary>main.gd _auto_panel: the colony-wide auto toggles and the auto-scavenge radius slider (set_auto).</summary>
        static float AutoPanel(MenuCtx c, float x, float iw, float y)
        {
            var g = c.G;
            const string help = "Everyone works their job on their own. Scavengers search buildings inside this radius around every camp and outpost (searched ones get an X), fighters answer SOS calls inside it while there are beds. Beyond it, send a squad (SQUADS > SCAVENGE). Tick STAY to keep someone home. Searched buildings inside a safe zone get converted (Auto-convert); 1 in 5 people stand the night watch (Auto-guards, or tick GUARD).";
            float hh = c.H_(help, 13, false, iw - 24f);
            float h = 10f + 44f + 8f + 44f + 6f + hh + 10f;
            c.Box(new Rect(x, y, iw, h), new Color(0.10f, 0.13f, 0.10f, 0.97f));
            c.Box(new Rect(x, y, iw, 2), new Color(0.30f, 0.40f, 0.28f));
            float cx = x + 12f, cw = iw - 24f, cy = y + 10f, tw = (cw - 3 * 8f) / 4f;
            for (int i = 0; i < CREW_AUTO_KEYS.Length; i++)
            {
                string key = CREW_AUTO_KEYS[i].key; bool on = g.C.Auto.Get(key);
                c.Button(new Rect(cx + i * (tw + 8f), cy, tw, 44), CREW_AUTO_KEYS[i].label + (on ? ": ON" : ": OFF"), on ? "sel" : "dark", 16, () => CampToast(c, "", "set_auto", key, !on));
            }
            cy += 52f;
            double lo = Game.AUTO_R_MIN, hi = g.AutoRMax(), cur = g.AutoRadius();
            c.Label(new Rect(cx, cy + 9, 270, 24), string.Format("Auto radius: {0} tiles (max {1})", (int)cur, (int)hi), 15, AMBER, false, false);
            // the slider (step 2 like the original's HSlider): one tap target per value, filled up to the current radius
            var vals = new List<double>();
            for (double v = lo; v <= hi + 0.001; v += 2.0) vals.Add(v);
            if (vals.Count == 0 || vals[vals.Count - 1] < hi - 0.001) vals.Add(hi);
            float sx = cx + 280f, sw = cw - 280f, seg = sw / vals.Count;
            for (int i = 0; i < vals.Count; i++)
            {
                double v = vals[i];
                c.Button(new Rect(sx + i * seg, cy - 2, Math.Max(4f, seg - 2f), 44), "", v <= cur + 0.001 ? "sel" : "dark", 10, () => CampToast(c, "", "set_auto", "r", v));
            }
            cy += 50f;
            c.Label(new Rect(cx, cy, cw, hh), help, 13, INK2);
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

        public static Rect TrapSheetRect(float w, float h) { float pw = Math.Min(900f, w - 200f), ph = 220f; return new Rect(w / 2f - pw / 2f, h - 62f - ph - 10f, pw, ph); }

        public static void TrapSheet(MenuCtx c, int k, Action close, Action trapTool)
        {
            var g = c.G; var D = Data.Instance;
            if (!g.S.Traps.TryGetValue(k, out var t)) { close?.Invoke(); return; }
            var td = Data.TRAPS[t.Kind];
            var p = TrapSheetRect(c.W, c.H);
            c.Box(p, PANEL);
            c.Box(new Rect(p.x, p.y, p.width, 3), AMBER);
            float x = p.x + 16, iw = p.width - 32, y = p.y + 10;
            c.Label(new Rect(x, y, iw - 140, 30), "TRAP · " + td.Name, 22, AMBER, true, false);
            c.Button(new Rect(p.xMax - 126, p.y + 8, 112, 44), "CLOSE", "dark", 18, close);
            y += 46f;
            int lv = Game.TrapLvl(t);
            var pos = new Vector2Int(k % D.W, k / D.W);
            string l1 = string.Format("Level {0} / {1} · {2} · {3} · {4}", lv + 1, Data.TRAP_LEVELS, Game.StreetName(pos),
                t.Hp > 0.0 ? string.Format("{0} / {1} uses left", (int)t.Hp, (int)Game.TrapHpMax(t)) : "spent, waiting to be cleared and reset", td.Desc);
            float h1 = Math.Min(64f, c.H_(l1, 15, false, iw));
            c.Label(new Rect(x, y, iw, h1), l1, 15, t.Hp > 0.0 ? INK2 : AMBER); y += h1 + 2f;
            c.Label(new Rect(x, y, iw, 20), TrapStats(t.Kind, lv), 14, INK2, false, false);
            if (t.Col != g.CurCol) { c.Label(new Rect(x, y + 24, iw, 22), "Belongs to another colony.", 15, RED, false, false); return; }
            float by = p.yMax - 62f;
            if (lv + 1 < Data.TRAP_LEVELS)
            {
                bool locked = lv + 1 >= 2 && !g.HasUnlock(Data.TRAP_L3_UNLOCK);
                string txt = locked ? "Level 3 needs\n" + g.UnlockSourceName(Data.TRAP_L3_UNLOCK)
                    : string.Format("UPGRADE → Level {0}\n{1} · {2}", lv + 2, Game.FmtCost(g.TrapUpgradeCost(t.Kind, lv)), TrapStats(t.Kind, lv + 1, true));
                c.Button(new Rect(x, by, iw - 400f, 54), txt, "sel", 13, () => CampToast(c, "", "upgrade_trap", k), !locked);
            }
            else c.Label(new Rect(x, by + 14, iw - 400f, 26), "Top level.", 16, AMBER, true, false);
            c.Button(new Rect(p.xMax - 16 - 384, by, 184, 54), "TRAP TOOL", "olive", 17, trapTool);
            c.Button(new Rect(p.xMax - 16 - 192, by, 192, 54), "REMOVE", "brown", 17, () => { string e = CampAct(c, "remove_trap", k); c.Toast(e != "" ? e : "Trap taken up."); if (e == "") close?.Invoke(); });
        }

        // ---------------- trap-line toolbar (main.gd _render_trap_toolbar, _on_trap_changed) ----------------
        public static Rect TrapBarRect(float w, float h) { float pw = Math.Min(1060f, w - 200f), ph = 254f; return new Rect(w / 2f - pw / 2f, h - 62f - ph - 10f, pw, ph); }

        /// <summary>tiles/ok/anchor: the line drawn on the map; pick(kind), undo, clear, confirm, done: toolbar actions.</summary>
        public static void TrapBar(MenuCtx c, int kind, IList<Vector2Int> tiles, bool anchored, Action<int> pick, Action undo, Action clear, Action confirm, Action done)
        {
            var g = c.G;
            var p = TrapBarRect(c.W, c.H);
            c.Box(p, PANEL);
            c.Box(new Rect(p.x, p.y, p.width, 3), AMBER);
            float x = p.x + 12, iw = p.width - 24, y = p.y + 8;
            string hint = string.Format("Tap a spot, then the next one: traps fill the line between them. Tap the last spot again for a new line. Traps reach twice as far as fences. {0} / {1} placed.", g.TrapsOf(), Data.TRAP_MAX);
            float hh = Math.Min(40f, c.H_(hint, 14, false, iw - 120f));
            c.Label(new Rect(x, y, iw - 120f, hh), hint, 14, INK2);
            c.Button(new Rect(p.xMax - 12 - 110, y, 110, 44), "DONE", "dark", 16, done);
            y += Math.Max(hh, 44f) + 6f;
            int n = Data.TRAPS.Length;
            float tw = (iw - (n - 1) * 6f) / n;
            for (int i = 0; i < n; i++)
            {
                var td = Data.TRAPS[i];
                bool un = g.TrapUnlocked(i);
                int ii = i;
                string t = un ? td.Name + "\n" + CampShortCost(g.TrapCost(i)) : td.Name + "\nneeds " + g.UnlockSourceName(td.Unlock);
                c.Button(new Rect(x + i * (tw + 6f), y, tw, 76), t, kind == i ? "sel" : "olive", 12, () => pick(ii), un);
            }
            y += 82f;
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
            float by = p.yMax - 62f;
            c.Label(new Rect(x, y, iw - 360f, by + 54f - y), info, 14, AMBER);
            c.Button(new Rect(p.xMax - 12 - 360, by, 110, 54), "UNDO", "dark", 16, undo, tiles.Count > 0);
            c.Button(new Rect(p.xMax - 12 - 244, by, 110, 54), "CLEAR", "dark", 16, clear, tiles.Count > 0 || anchored);
            c.Button(new Rect(p.xMax - 12 - 128, by, 128, 54), "CONFIRM", "sel", 17, confirm, okAny);
        }

        // ---------------- BUILD OUTPOST bar (main.gd _start_outpost banner + CANCEL) ----------------
        public static Rect OutpostBarRect(float w, float h) { float pw = Math.Min(860f, w - 200f), ph = 120f; return new Rect(w / 2f - pw / 2f, h - 62f - ph - 10f, pw, ph); }

        public static void OutpostBar(MenuCtx c, Action cancel)
        {
            var g = c.G;
            var p = OutpostBarRect(c.W, c.H);
            c.Box(p, PANEL);
            c.Box(new Rect(p.x, p.y, p.width, 3), AMBER);
            float x = p.x + 16, iw = p.width - 32;
            c.Label(new Rect(x, p.y + 10, iw - 170, 30), "BUILD OUTPOST", 20, AMBER, true, false);
            string t = string.Format("Tap a building to take it over as an outpost ({0}, instant). Anywhere on the map. {1} / {2} outposts.", Game.FmtCost(Game.OUTPOST_COST), g.Outposts(), Game.OUTPOST_MAX);
            c.Label(new Rect(x, p.y + 42, iw - 170, p.height - 50), t, 15, g.CanAfford(Game.OUTPOST_COST) ? INK : RED);
            c.Button(new Rect(p.xMax - 16 - 150, p.y + p.height / 2 - 27, 150, 54), "CANCEL", "brown", 18, cancel);
        }

        // ---------------- building sheet: GUT (main.gd _sheet_gut) ----------------
        /// <summary>Height the GUT row needs on a building sheet (0 when it shows nothing).</summary>
        public static float GutRowHeight(Game g, int l)
        {
            if (l < 0 || g.ConvOf(l) != null) return 0f;
            if (g.IsGutted(l) || g.Gutting(l)) return 30f;
            if (g.GutError(l) == "") return 72f;
            return g.ConvPlaceError(l) == "" ? 30f : 0f;
        }

        public static void GutRow(MenuCtx c, Rect r, int l)
        {
            var g = c.G; var D = Data.Instance;
            if (r.height <= 0f) return;
            if (g.IsGutted(l))
            {
                int by = g.GuttedBy(l);
                c.Label(r, string.Format("GUTTED{0}: stripped to the shell (wood, drywall, wiring, fittings all taken out). Ready to become a camp building.", by == g.CurCol ? "" : " by " + (g.ColonyAt(by)?.Name ?? "someone")), 14, new Color(0.95f, 0.75f, 0.45f));
                return;
            }
            if (g.Gutting(l))
            {
                bool mine = g.S.Guts.TryGetValue(l, out var j) && j.Col == g.CurCol;
                c.Label(r, mine ? string.Format("A crew is gutting it: about {0} s left.", (int)Math.Ceiling(g.GutLeft(l))) : "Another colony is gutting it.", 14, AMBER);
                return;
            }
            string ge = g.GutError(l);
            if (ge == "")
            {
                float bw = Math.Min(r.width, 900f);
                c.Button(new Rect(r.x, r.y, bw, 48), string.Format("GUT IT ({0} s): strip it to the shell for {1}", (int)Data.GUT_SEC, CampShortCost(D.GutYield(l))), "olive", 14, () =>
                {
                    string e = CampAct(c, "gut", l);
                    c.Toast(e != "" ? e : "A crew is gutting it.");
                });
                if (g.ConvPlaceError(l) == "") c.Label(new Rect(r.x, r.y + 52, r.width, 20), "Buildings must be gutted before they can become a farm, workshop, garage, housing or any other camp building.", 13, INK2, false, false);
            }
            else if (g.ConvPlaceError(l) == "") c.Label(r, "Gut it before converting: " + ge, 13, RED);
        }
    }
}
