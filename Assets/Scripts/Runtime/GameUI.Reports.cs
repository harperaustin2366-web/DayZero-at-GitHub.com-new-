// E5 save slots screen, E6 morning report card, E7 memorial wall, E15 run summary.
// ReportScreens lays each screen out into a SideMenus display list (MenuCtx), so the game paints it with the UI kit
// (SideMenus.Flush) and the offline preview paints the same list to PNG. The GameUI part at the bottom is the glue:
// which screen is open, and the commands behind the buttons (Bootstrap load / save / new game / title).
using System;
using System.Collections.Generic;
using System.Linq;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed class ReportState
    {
        public string Open = "";            // "" | "saves" | "memorial"
        public string Confirm = "";         // two-tap confirm key ("save2", "load0", "del3")
        public SaveSlots.Info[] Slots;      // cached slot cards (re-read when the screen opens or after a change)
        public void Refresh() { Slots = null; Confirm = ""; }
    }

    /// <summary>Commands behind the report screens' buttons.</summary>
    public sealed class ReportActions
    {
        public bool InGame, CanSave;
        public Action<int> Load, Save, Delete;
        public Action Close, NewGame, MainMenu, Memorial, DismissReport;
    }

    public static class ReportScreens
    {
        static Color INK => SideMenus.INK; static Color INK2 => SideMenus.INK2; static Color MUTE => SideMenus.MUTE;
        static Color AMBER => SideMenus.AMBER; static Color GREEN => SideMenus.GREEN; static Color RED => SideMenus.RED;
        static readonly string[] RES_ORDER = { "food", "water", "meds", "ammo", "fuel", "mats" };

        static Rect Sheet(MenuCtx c, float maxW, float maxH)
        {
            float pw = Math.Min(maxW, c.W - 40f), ph = Math.Min(maxH, c.H - 32f);
            return new Rect((c.W - pw) / 2f, (c.H - ph) / 2f, pw, ph);
        }

        static string ResName(string k)
        {
            return (Data.RES_LABEL.TryGetValue(k, out var l) && !string.IsNullOrEmpty(l) ? l : k).ToLowerInvariant();
        }

        static string Plural(int n, string one, string many) => n == 1 ? one : many;

        // ---------------- E5 save slots ----------------
        public static void Saves(MenuCtx c, ReportState st, ReportActions a)
        {
            var g = c.G;
            if (st.Slots == null)
            {
                st.Slots = new SaveSlots.Info[SaveSlots.MANUAL + 1];
                for (int i = 0; i <= SaveSlots.MANUAL; i++) st.Slots[i] = SaveSlots.Read(g, i);
            }
            c.Dim();
            var inner = c.Panel(Sheet(c, 1080f, 660f), "SAVE GAMES");
            var L = new UiLayout(inner, 10f);
            var F = new UiLayout(L.Bottom(60f), 12f);
            c.Btn(F.Right(190f), "CLOSE", UiRole.Danger, a.Close, true, false, 22);
            c.Fit(F.Rest, a.CanSave ? "The game autosaves every dawn and when you leave the app." : a.InGame ? "Saving is not possible right now." : "Pick a save to load.", 17, INK2, false);
            int n = SaveSlots.MANUAL + 1;
            float rh = Math.Min(116f, (L.Rest.height - (n - 1) * 10f) / n);
            for (int i = 0; i < n; i++)
            {
                var row = L.Top(rh);
                var info = st.Slots[i];
                c.Plate(row, i, i == 0);
                var R = new UiLayout(UiLayout.Inset(row, 16f, 8f, 12f, 8f), 12f);
                int slot = i;
                float bh = Math.Min(64f, R.Rest.height);
                Rect B(float w) { var r = R.Right(w); return UiLayout.Center(r, w, bh); }
                // buttons right to left: DELETE, LOAD, SAVE (manual slots); LOAD only on AUTO
                if (i > 0)
                {
                    bool cd = st.Confirm == "del" + i;
                    c.Btn(B(150f), cd ? "SURE?" : "DELETE", UiRole.Danger, () => { if (st.Confirm == "del" + slot) { a.Delete(slot); st.Refresh(); } else st.Confirm = "del" + slot; }, info.Has, cd, 20);
                }
                bool cl = st.Confirm == "load" + i;
                c.Btn(B(170f), cl ? "CONFIRM" : "LOAD", UiRole.Primary, () =>
                {
                    if (!a.InGame || st.Confirm == "load" + slot) { st.Confirm = ""; a.Load(slot); }
                    else st.Confirm = "load" + slot;
                }, info.Has, cl, 20);
                if (i > 0)
                {
                    bool cs = st.Confirm == "save" + i;
                    c.Btn(B(170f), cs ? "OVERWRITE" : "SAVE HERE", UiRole.Accent, () =>
                    {
                        if (!info.Has || st.Confirm == "save" + slot) { a.Save(slot); st.Refresh(); }
                        else st.Confirm = "save" + slot;
                    }, a.CanSave, cs, 20);
                }
                var tx = UiLayout.Rows(R.Rest, 2f, 1.2f, 1f, 0.9f);
                c.Fit(tx[0], SaveSlots.SlotName(i) + (info.Has && info.Colony != "" ? "  -  " + info.Colony : ""), 22, i == 0 ? AMBER : INK, true);
                if (!info.Has) { c.Fit(tx[1], "Empty", 18, MUTE, false); continue; }
                string diff = info.Diff >= 0 && info.Diff < Game.DIFF_NAMES.Length ? Game.DIFF_NAMES[info.Diff] : "";
                c.Fit(tx[1], info.Day > 0
                    ? string.Format("Day {0}  -  {1} {2}{3}{4}", info.Day, info.Pop, Plural(info.Pop, "survivor", "survivors"), diff != "" ? "  -  " + diff : "", info.Expanded ? "  -  Expanded" : "")
                    : "Saved game", 18, INK, false);
                c.Fit(tx[2], SaveSlots.When(info.Time), 16, INK2, false);
            }
        }

        // ---------------- E7 memorial wall ----------------
        public static void Memorial(MenuCtx c, ReportActions a)
        {
            var log = RunStats.Instance.Log;
            var fallen = log?.Fallen ?? new List<FallenEntry>();
            c.Dim();
            var inner = c.Panel(Sheet(c, 1180f, 680f), "MEMORIAL WALL");
            var L = new UiLayout(inner, 10f);
            var F = new UiLayout(L.Bottom(60f), 12f);
            c.Btn(F.Right(190f), "CLOSE", UiRole.Danger, a.Close, true, false, 22);
            c.Fit(F.Rest, fallen.Count == 0 ? "" : string.Format("{0} {1} fallen. They will not be forgotten.", fallen.Count, Plural(fallen.Count, "survivor has", "survivors have")), 17, INK2, false);
            if (fallen.Count == 0)
            {
                var e = UiLayout.Center(L.Rest, L.Rest.width, 80f);
                var er = UiLayout.Rows(e, 6f, 1f, 1f);
                c.Fit(er[0], "No one has fallen yet.", 26, AMBER, true, MenuAlign.Center);
                c.Fit(er[1], "Keep it that way.", 19, INK2, false, MenuAlign.Center);
                return;
            }
            const float RH = 72f, GAP = 6f;
            float cw = c.Scroll("rp_memorial", L.Rest, fallen.Count * (RH + GAP) - GAP);
            for (int k = 0; k < fallen.Count; k++)
            {
                var f = fallen[fallen.Count - 1 - k];   // newest first
                var row = new Rect(0f, k * (RH + GAP), cw, RH);
                c.Plate(row, k);
                c.Box(new Rect(row.x + 3f, row.y + 6f, 4f, row.height - 12f), new Color(0.75f, 0.30f, 0.24f, 0.9f));
                var cols = UiLayout.Columns(UiLayout.Inset(row, 18f, 6f, 14f, 6f), 14f, 0.95f, 1.6f, 0.75f);
                var nm = UiLayout.Rows(cols[0], 0f, 1.15f, 0.85f);
                c.Fit(nm[0], f.Name, 21, INK, true);
                c.Fit(nm[1], f.Job, 16, INK2, false);
                c.Fit(cols[1], f.Cause, 18, INK2, false);
                var dd = UiLayout.Rows(cols[2], 0f, 1.15f, 0.85f);
                c.Fit(dd[0], "Day " + f.Day, 19, AMBER, true, MenuAlign.Right);
                c.Fit(dd[1], DaysText(f.Days), 15, INK2, false, MenuAlign.Right);
            }
            c.EndScroll();
        }

        static string DaysText(double d)
        {
            if (d < 1.0) return "with you under a day";
            int n = (int)Math.Floor(d);
            return string.Format("survived {0} {1}", n, Plural(n, "day", "days"));
        }

        // ---------------- E6 morning report ----------------
        public static void Morning(MenuCtx c, MorningReport r, ReportActions a)
        {
            const float LINE = 30f;
            var lines = new List<(string t, Color col)>();
            foreach (var d in r.Died.Take(3)) lines.Add((string.Format("Lost: {0} ({1})  -  {2}", d.Name, d.Job, d.Cause), RED));
            if (r.Died.Count > 3) lines.Add((string.Format("... and {0} more. See the memorial wall.", r.Died.Count - 3), RED));
            if (r.Joined.Count > 0)
                lines.Add(("Joined: " + string.Join(", ", r.Joined.Take(5)) + (r.Joined.Count > 5 ? string.Format(" and {0} more", r.Joined.Count - 5) : ""), GREEN));
            // the six biggest hauls, staples first on a tie; the rest summed up
            var got = r.Loot.Where(kv => kv.Value >= 1.0).OrderByDescending(kv => (int)kv.Value).ThenBy(kv => Array.IndexOf(RES_ORDER, kv.Key) < 0 ? 99 : Array.IndexOf(RES_ORDER, kv.Key)).ToList();
            var loot = got.Take(6).Select(kv => string.Format("{0} {1}", (int)kv.Value, ResName(kv.Key))).ToList();
            if (got.Count > 6) loot.Add(string.Format("{0} other", got.Skip(6).Sum(kv => (int)kv.Value)));
            lines.Add((loot.Count > 0 ? "Loot brought back: " + string.Join(", ", loot) : "No loot was brought back.", loot.Count > 0 ? INK : INK2));
            string def;
            if (r.Breaches == 0 && r.TowersLost == 0 && r.WallDmg < 1.0) def = "Defenses: the walls held, no damage.";
            else
            {
                var parts = new List<string>();
                if (r.Breaches > 0) parts.Add(string.Format("{0} {1} broken", r.Breaches, Plural(r.Breaches, "wall section", "wall sections")));
                if (r.TowersLost > 0) parts.Add(string.Format("{0} {1} destroyed", r.TowersLost, Plural(r.TowersLost, "tower", "towers")));
                if (r.WallDmg >= 1.0) parts.Add(string.Format("{0} damage to the walls", (int)r.WallDmg));
                def = "Defenses: " + string.Join(", ", parts) + ".";
            }
            lines.Add((def, r.Breaches > 0 || r.TowersLost > 0 ? RED : INK));
            if (r.Died.Count == 0 && r.Joined.Count == 0) lines.Insert(0, ("Everyone made it through the night.", GREEN));

            float pw = Math.Min(900f, c.W - 60f);
            float ph = 64f + (80f + 10f) + (44f + 10f) + lines.Count * (LINE + 10f) + (48f + 10f) + 6f;   // header + pad, button, chips, lines, threat
            ph = Math.Min(ph, c.H - 24f);
            var p = new Rect((c.W - pw) / 2f, (c.H - ph) / 2f, pw, ph);
            c.Dim();
            var inner = c.Panel(p, "MORNING REPORT  -  DAY " + r.Day);
            var L = new UiLayout(inner, 10f);
            c.Btn(L.Bottom(80f), "START THE DAY", UiRole.Accent, a.DismissReport, true, false, 28);
            int dp = r.PopNow - r.PopBefore;
            var chips = UiLayout.Columns(L.Top(44f), 8f, 1.35f, 1f, 1f, 1f);
            c.Chip(chips[0], string.Format("{0} SURVIVORS ({1}{2})", r.PopNow, dp >= 0 ? "+" : "", dp), dp < 0 ? UiRole.Danger : UiRole.Primary, 17);
            c.Chip(chips[1], r.Kills + " KILLED", UiRole.Accent, 17);
            c.Chip(chips[2], r.Died.Count + " LOST", r.Died.Count > 0 ? UiRole.Danger : UiRole.Neutral, 17);
            c.Chip(chips[3], r.Rescued + " RESCUED", r.Rescued > 0 ? UiRole.Primary : UiRole.Neutral, 17);
            foreach (var (t, col) in lines) c.Fit(L.Top(LINE), t, 19, col, false);
            var th = L.Top(48f);
            string threat = r.ThreatMajor
                ? string.Format("MAJOR ATTACK EXPECTED TONIGHT  -  about {0} zombies", r.Threat)
                : string.Format("TONIGHT: about {0} zombies expected{1}", r.Threat, r.ThreatKnown ? "" : " (estimate)");
            c.Chip(th, threat, r.ThreatMajor ? UiRole.Danger : UiRole.Neutral, 19);
        }

        // ---------------- E15 run summary ----------------
        public static void Summary(MenuCtx c, bool won, ReportActions a)
        {
            var g = c.G; var col = g.C; var rs = RunStats.Instance;
            int days = col != null && col.LostDay > 0 ? col.LostDay : g.Day();
            int best = rs.BestDay(out int bestKills);
            var series = rs.PopSeries(out int first);
            int peak = Math.Max(rs.Log?.Peak ?? 0, series.Count > 0 ? series.Max() : 0);
            c.Dim();
            var inner = c.Panel(Sheet(c, 1240f, 690f), won ? "YOU HELD OUT" : "YOUR COLONY HAS FALLEN");
            var L = new UiLayout(inner, 12f);
            var F = new UiLayout(L.Bottom(70f), 12f);
            c.Btn(F.Right(240f), "MAIN MENU", UiRole.Neutral, a.MainMenu, true, false, 24);
            c.Btn(F.Right(220f), "MEMORIAL", UiRole.Primary, a.Memorial, true, false, 22);
            c.Btn(F.Right(260f), "NEW GAME", UiRole.Accent, a.NewGame, true, false, 26);
            c.Fit(F.Rest, col != null && col.Name != "" ? col.Name : "", 19, INK2, false);

            var left = L.Left(Math.Min(400f, L.Rest.width * 0.36f));
            var tiles = new (string k, string v)[]
            {
                ("DAYS SURVIVED", days.ToString()),
                ("PEAK POPULATION", peak.ToString()),
                ("ZOMBIES KILLED", (col?.Stat("kills") ?? 0).ToString()),
                ("SURVIVORS RESCUED", (col?.Stat("rescued") ?? 0).ToString()),
                ("FALLEN", (rs.Log?.Fallen.Count ?? col?.Stat("dead") ?? 0).ToString()),
                ("BEST DAY", best > 0 ? string.Format("Day {0}  -  {1} kills", best, bestKills) : "-"),
            };
            var T = new UiLayout(left, 8f);
            float tH = (left.height - 8f * (tiles.Length - 1)) / tiles.Length;
            for (int i = 0; i < tiles.Length; i++)
            {
                var r = T.Top(tH);
                c.Plate(r, i);
                var rr = UiLayout.Columns(UiLayout.Inset(r, 14f, 4f, 14f, 4f), 8f, 1f, 1f);
                c.Fit(rr[0], tiles[i].k, 16, INK2, true);
                c.Fit(rr[1], tiles[i].v, 24, i == 5 ? AMBER : INK, true, MenuAlign.Right);
            }
            var gp = c.Panel(L.Rest, "POPULATION BY DAY");
            Graph(c, gp, series, first);
        }

        /// <summary>Bars with an amber step line along their tops (Box items only, so phone and preview draw the same).</summary>
        public static void Graph(MenuCtx c, Rect area, List<int> s, int firstDay)
        {
            if (s.Count < 2) { c.Fit(UiLayout.Center(area, area.width, 40f), "Not enough days to draw a graph.", 19, INK2, false, MenuAlign.Center); return; }
            var lab = new Rect(area.x, area.yMax - 26f, area.width, 26f);
            var plot = new Rect(area.x + 46f, area.y + 6f, area.width - 52f, area.height - 40f);
            // bucket to at most one bar per 6 px (long runs)
            int maxBars = Math.Max(2, (int)(plot.width / 6f));
            int per = (s.Count + maxBars - 1) / maxBars;
            var vals = new List<int>();
            for (int i = 0; i < s.Count; i += per) { int m = 0; for (int j = i; j < Math.Min(s.Count, i + per); j++) m = Math.Max(m, s[j]); vals.Add(m); }
            int top = Math.Max(1, vals.Max());
            int scale = top <= 5 ? 5 : top <= 10 ? 10 : (int)Math.Ceiling(top / 10.0) * 10;
            c.Box(new Rect(plot.x, plot.yMax, plot.width, 2f), new Color(0.85f, 0.80f, 0.66f, 0.6f));
            c.Box(new Rect(plot.x - 2f, plot.y, 2f, plot.height + 2f), new Color(0.85f, 0.80f, 0.66f, 0.6f));
            c.Box(new Rect(plot.x, plot.y, plot.width, 1f), new Color(0.85f, 0.80f, 0.66f, 0.18f));
            c.Box(new Rect(plot.x, plot.y + plot.height / 2f, plot.width, 1f), new Color(0.85f, 0.80f, 0.66f, 0.18f));
            c.Fit(new Rect(area.x, plot.y - 10f, 40f, 22f), scale.ToString(), 15, INK2, false, MenuAlign.Right);
            c.Fit(new Rect(area.x, plot.y + plot.height / 2f - 11f, 40f, 22f), (scale / 2).ToString(), 15, INK2, false, MenuAlign.Right);
            c.Fit(new Rect(area.x, plot.yMax - 12f, 40f, 22f), "0", 15, INK2, false, MenuAlign.Right);
            float bw = plot.width / vals.Count;
            float prevY = -1f;
            for (int i = 0; i < vals.Count; i++)
            {
                float hgt = vals[i] / (float)scale * plot.height;
                float x = plot.x + i * bw, y = plot.yMax - hgt;
                if (hgt > 0f) c.Box(new Rect(x + bw * 0.12f, y, Math.Max(1f, bw * 0.76f), hgt), new Color(0.50f, 0.58f, 0.30f, 0.55f));
                c.Box(new Rect(x, y - 1.5f, bw, 3f), AMBER);
                if (prevY >= 0f && Math.Abs(prevY - y) > 0.5f) c.Box(new Rect(x - 1.5f, Math.Min(prevY, y) - 1.5f, 3f, Math.Abs(prevY - y) + 3f), AMBER);
                prevY = y;
            }
            int last = firstDay + s.Count - 1;
            c.Fit(new Rect(plot.x, lab.y, 200f, lab.height), "Day " + firstDay, 15, INK2, false);
            c.Fit(new Rect(plot.xMax - 200f, lab.y, 200f, lab.height), "Day " + last, 15, INK2, false, MenuAlign.Right);
        }
    }

#if !DZ_PREVIEW
    public sealed partial class GameUI
    {
        readonly ReportState _rep = new ReportState();

        /// <summary>A morning report card is up (blocks map taps, like the other modal sheets).</summary>
        bool ReportsModal => _b.State == Bootstrap.Screen2.Playing && !_b.Picking && RunStats.Instance.Pending != null;

        ReportActions RepActions() => new ReportActions
        {
            InGame = _b.State == Bootstrap.Screen2.Playing,
            CanSave = _b.State == Bootstrap.Screen2.Playing && SaveSlots.CanSave(G),
            Load = slot => { _rep.Open = ""; _menuOpen = false; RunStats.Instance.Pending = null; _b.LoadSaved(SaveSlots.SlotPath(slot)); },
            Save = slot => _b.Toast(SaveSlots.Save(G, slot) ? "Saved to " + SaveSlots.SlotName(slot).ToLowerInvariant() + "." : "Save failed."),
            Delete = slot => { SaveSlots.Delete(G, slot); _b.Toast(SaveSlots.SlotName(slot) + " deleted."); },
            Close = () => { _rep.Open = ""; _rep.Refresh(); },
            NewGame = () => { _rep.Open = ""; _b.NewGame(G?.S != null ? G.S.Difficulty : 1); },
            MainMenu = () => { _rep.Open = ""; _b.ToTitle(); },
            Memorial = () => { _rep.Open = "memorial"; UiKit.ScrollTo("rp_memorial", 0f); },
            DismissReport = () => RunStats.Instance.Pending = null,
        };

        void OpenReportScreen(string which) { _rep.Open = which; _rep.Refresh(); if (which == "memorial") UiKit.ScrollTo("rp_memorial", 0f); }

        /// <summary>SAVE GAMES / MEMORIAL screens opened from the Menu tab or the title. True when one was drawn.</summary>
        bool DrawReportScreens(float w, float h)
        {
            if (_rep.Open == "") return false;
            var ctx = NewCtx(w, h, MapView.Instance);
            var a = RepActions();
            if (_rep.Open == "saves") ReportScreens.Saves(ctx, _rep, a);
            else if (_rep.Open == "memorial") ReportScreens.Memorial(ctx, a);
            else { _rep.Open = ""; return false; }
            SideMenus.Flush(ctx);
            return true;
        }

        /// <summary>Menu tab, Game column: SAVE GAMES and MEMORIAL (one row of two buttons).</summary>
        void DrawReportMenuButtons(UiLayout col)
        {
            var r = UiLayout.Columns(col.Top(62f), 12f, 1f, 1f);
            if (KBtn(r[0], "SAVE GAMES", UiRole.Primary)) OpenReportScreen("saves");
            if (KBtn(r[1], "MEMORIAL", UiRole.Neutral)) OpenReportScreen("memorial");
        }

        /// <summary>Morning report card (modal chain in DrawGame). True when drawn.</summary>
        bool DrawMorningReport(float w, float h)
        {
            var rep = RunStats.Instance.Pending;
            if (!ReportsModal || rep == null) return false;
            var ctx = NewCtx(w, h, MapView.Instance);
            ReportScreens.Morning(ctx, rep, RepActions());
            SideMenus.Flush(ctx);
            return true;
        }

        /// <summary>E15: the run summary replaces the old one-line game-over panel. True when drawn.</summary>
        bool DrawRunSummary(float w, float h)
        {
            if (G?.S == null) return false;
            if (DrawReportScreens(w, h)) return true;   // MEMORIAL opened from the summary
            var ctx = NewCtx(w, h, MapView.Instance);
            ReportScreens.Summary(ctx, _b.Won, RepActions());
            SideMenus.Flush(ctx);
            return true;
        }
    }
#endif
}
