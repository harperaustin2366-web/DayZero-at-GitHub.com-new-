// Expansion UI: people (SYSTEMS_30 #5 Stress, #6 Traits, #18 Fatigue). Adds to the CREW & JOBS side menu
// (SideMenus.Camp.cs Crew, three one-line hooks): a line under each survivor row with trait chips, a stress face,
// a fatigue bar and a STRESS tag; a survivor sheet with TALK / REST DAY; and a NIGHT WATCH screen (shifts,
// guards, morale, AUTO TALK). Everything is drawn only while Game.ExpansionRules is on. Buttons send the normal
// actions ("ppl_talk:<id>", "ppl_rest:<id>") through Bootstrap.Act -> Game.DoAction; no rules live here.
using System;
using System.Collections.Generic;
using System.Linq;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public static partial class SideMenus
    {
        /// <summary>Survivor sheet shown inside CREW (-1 = none) and the NIGHT WATCH screen flag.</summary>
        public static int PplDetail = -1;
        public static bool PplWatch;
        public static int PplWatchPage;

        static readonly Color PPL_CALM = new Color(0.52f, 0.78f, 0.42f);
        static readonly Color PPL_TENSE = new Color(0.86f, 0.80f, 0.36f);
        static readonly Color PPL_STRESSED = new Color(0.95f, 0.58f, 0.24f);
        static readonly Color PPL_BREAK = new Color(0.92f, 0.32f, 0.26f);
        static readonly Color PPL_CHIP = new Color(0.18f, 0.22f, 0.16f, 0.95f);
        static readonly Color PPL_CHIP_HID = new Color(0.22f, 0.20f, 0.14f, 0.95f);
        static readonly Color PPL_FACE_INK = new Color(0.08f, 0.07f, 0.05f, 1f);
        const float PPL_LINE = 56f;

        static bool PplOn(MenuCtx c) => Game.ExpansionRules && c?.G?.C != null;

        public static (string name, Color col) StressBand(double s)
            => s >= 80.0 ? ("BREAKING", PPL_BREAK) : s >= 60.0 ? ("STRESSED", PPL_STRESSED) : s >= 40.0 ? ("TENSE", PPL_TENSE) : ("CALM", PPL_CALM);

        static Color FatigueCol(double f) => f >= Game.FAT_DOZE ? PPL_BREAK : f >= Game.FAT_TIRED ? PPL_STRESSED : PPL_CALM;

        /// <summary>Hook for the CREW row heights: the extra line under adult rows.</summary>
        public static float PplRowH(Game g, Person q) => Game.ExpansionRules && g != null && !g.IsMinor(q) ? PPL_LINE : 0f;

        static UiRole StressRole(double s) => s >= 60.0 ? UiRole.Danger : s >= 40.0 ? UiRole.Accent : UiRole.Primary;
        static UiRole FatigueRole(double f) => f >= Game.FAT_DOZE ? UiRole.Danger : f >= Game.FAT_TIRED ? UiRole.Accent : UiRole.Primary;

        /// <summary>A small procedural face (no glyphs: the phone font lacks them), 22 px, mouth by stress band.</summary>
        static void StressFace(MenuCtx c, float x, float y, double s)
        {
            var b = StressBand(s);
            c.Box(new Rect(x, y, 22, 22), b.col);
            c.Box(new Rect(x + 5, y + 6, 4, 4), PPL_FACE_INK);
            c.Box(new Rect(x + 13, y + 6, 4, 4), PPL_FACE_INK);
            if (s >= 80.0) c.Box(new Rect(x + 7, y + 13, 8, 6), PPL_FACE_INK);                              // open mouth
            else if (s >= 60.0) { c.Box(new Rect(x + 5, y + 16, 12, 2), PPL_FACE_INK); c.Box(new Rect(x + 5, y + 14, 2, 2), PPL_FACE_INK); c.Box(new Rect(x + 15, y + 14, 2, 2), PPL_FACE_INK); }   // frown
            else if (s >= 40.0) c.Box(new Rect(x + 6, y + 15, 10, 2), PPL_FACE_INK);                           // flat
            else { c.Box(new Rect(x + 7, y + 16, 8, 2), PPL_FACE_INK); c.Box(new Rect(x + 5, y + 14, 2, 2), PPL_FACE_INK); c.Box(new Rect(x + 15, y + 14, 2, 2), PPL_FACE_INK); }   // smile
        }

        /// <summary>Trait chips; the still-hidden trait shows "?". Returns the x after the last chip.</summary>
        static float TraitChips(MenuCtx c, Person q, float x, float y, float maxX)
        {
            if (q.Traits == null || q.Traits.Count == 0)
            {
                c.Fit(new Rect(x, y, 110, 30), "no traits", 15, INK2, false);
                return x + 112f;
            }
            foreach (var id in q.Traits)
            {
                bool hid = id == q.TraitHidden;
                string t = hid ? "?" : (Game.Traits.TryGetValue(id, out var td) ? td.Name : id).ToUpperInvariant();
                float w = Math.Max(48f, t.Length * 10.5f + 30f);
                if (x + w > maxX) break;
                c.Chip(new Rect(x, y, w, 30), t, hid ? UiRole.Accent : UiRole.Primary, 15);
                x += w + 6f;
            }
            return x;
        }

        /// <summary>Hook in CREW: the line under a survivor row (traits, stress face, fatigue bar, STRESS tag, TALK / REST).</summary>
        public static void PplRow(MenuCtx c, Person q, float x, float iw, float y)
        {
            if (!PplOn(c) || c.G.IsMinor(q)) return;
            int pid = q.Id;
            var R = new UiLayout(new Rect(x, y + 2f, iw, PPL_LINE - 4f), 10f);
            c.Btn(R.Right(190f), "TALK / REST", UiRole.Neutral, () => { PplDetail = pid; PplWatch = false; }, true, false, 17);
            var mid = R.Rest;
            float cy = mid.y + (mid.height - 30f) / 2f;
            float rx = TraitChips(c, q, mid.x, cy, mid.x + Math.Min(380f, mid.width * 0.42f));
            rx = Math.Max(rx + 8f, mid.x + Math.Min(390f, mid.width * 0.43f));
            StressFace(c, rx, cy + 4f, q.Stress); rx += 30f;
            var b = StressBand(q.Stress);
            c.Fit(new Rect(rx, cy, 110f, 30f), b.name, 16, b.col, true); rx += 116f;
            float bw = Math.Max(120f, Math.Min(220f, mid.xMax - rx - (q.Stress >= 80.0 ? 136f : 0f)));
            c.KBar(new Rect(rx, cy + 1f, bw, 28f), (float)(q.Fatigue / 100.0), FatigueRole(q.Fatigue), "FATIGUE " + (int)q.Fatigue); rx += bw + 10f;
            if (q.Stress >= 80.0) c.Chip(new Rect(rx, cy, 126f, 30f), "STRESS " + (int)q.Stress, UiRole.Danger, 15);
        }

        /// <summary>Hook at the top of CREW: the survivor sheet / NIGHT WATCH screen in place of the list. True when one
        /// of those took over (the NIGHT WATCH button sits in CREW's top bar).</summary>
        public static bool PplCrewModal(MenuCtx c)
        {
            if (!PplOn(c)) { PplDetail = -1; PplWatch = false; return false; }
            if (PplDetail >= 0)
            {
                var q = c.G.AlivePeople().FirstOrDefault(x => x.Id == PplDetail);
                if (q == null) PplDetail = -1;
                else { PersonSheet(c, q); return true; }
            }
            if (PplWatch) { WatchScreen(c); return true; }
            return false;
        }

        static string Hrs(double h) => h >= 1.0 ? string.Format("{0:0}h", Math.Ceiling(h)) : "<1h";

        static void PersonSheet(MenuCtx c, Person q)
        {
            var g = c.G;
            var L = Frame(c, "SURVIVOR · " + q.Name, () => PplDetail = -1);
            int pid = q.Id;
            double t = g.S.T;
            bool talked = q.TalkDay == g.Day(), away = q.St != "base", resting = q.RestEndT > t;
            string hint = talked ? "Someone already talked with them today." : away ? "Talking needs them at camp." : "TALK: a Medic, a Leader or the calmest person sits with them (-15 stress, once a day).";
            hint += resting ? "  Already resting." : "  REST DAY: -25 stress, no job and no guard duty for 24h.";
            var F = new UiLayout(L.Bottom(BTN_H), 12f);
            c.Btn(F.Left(260f), "TALK", UiRole.Accent, () => PplDo(c, "ppl_talk:" + pid, "They talked it through."), !talked && !away, false, 22);
            c.Btn(F.Left(260f), "REST DAY", UiRole.Primary, () => PplDo(c, "ppl_rest:" + pid, "Off duty for a day: stress eases, no work, no watch."), !resting, false, 22);
            c.Para(F.Rest, hint, 15, INK2);

            var stt = CrewStatusText(g, q);
            c.Fit(L.Top(30f), string.Format("{0}  ·  {1}", q.Job, stt.text), 21, INK, true);
            // two columns: traits on the left, stress / fatigue / notes on the right
            var cols = UiLayout.Columns(L.Rest, 16f, 1.1f, 1f);
            var T = new UiLayout(cols[0], 8f);
            c.Header(T.Top(40f), "TRAITS", 20);
            var tv = T.Rest; float tw = tv.width - 12f;
            var trs = new List<(string nm, string ds, bool hid, float dh)>();
            if (q.Traits != null)
                foreach (var id in q.Traits)
                {
                    bool hid = id == q.TraitHidden;
                    Game.Traits.TryGetValue(id, out var td);
                    string nm = hid ? "?" : (td?.Name ?? id);
                    string ds = hid ? string.Format("Still getting to know them. Shows in about {0}.", Hrs(q.JoinT + Game.TRAIT_REVEAL_H - g.S.T)) : (td?.Desc ?? "");
                    trs.Add((nm, ds, hid, Math.Max(30f, c.H_(ds, 15, false, tw - 190f))));
                }
            c.Scroll("ppl_traits_" + pid, tv, trs.Count == 0 ? 30f : Total(trs.Select(r => r.dh + 12f).ToList(), 6f));
            if (trs.Count == 0) c.Fit(new Rect(0, 0, tw, 30f), "Nothing stands out about them yet.", 16, INK2, false);
            float y = 0f;
            for (int i = 0; i < trs.Count; i++)
            {
                var tr = trs[i];
                c.Plate(new Rect(0, y, tw, tr.dh + 12f), i);
                c.Chip(new Rect(8f, y + 6f, 166f, 30f), tr.nm.ToUpperInvariant(), tr.hid ? UiRole.Accent : UiRole.Primary, 15);
                c.Para(new Rect(184f, y + 8f, tw - 192f, tr.dh), tr.ds, 15, INK2);
                y += tr.dh + 12f + 6f;
            }
            c.EndScroll();

            var S = new UiLayout(cols[1], 8f);
            c.Header(S.Top(40f), "STRESS AND FATIGUE", 20);
            var b = StressBand(q.Stress);
            var sr = new UiLayout(S.Top(34f), 10f);
            StressFace(c, sr.Rest.x, sr.Rest.y + 6f, q.Stress); sr.Left(24f);
            c.Fit(sr.Left(300f), string.Format("STRESS {0}  ·  {1}", (int)q.Stress, b.name), 17, b.col, true);
            c.KBar(sr.Rest, (float)(q.Stress / 100.0), StressRole(q.Stress), ((int)q.Stress).ToString());
            var fr = new UiLayout(S.Top(34f), 10f);
            fr.Left(24f);
            c.Fit(fr.Left(300f), string.Format("FATIGUE {0}{1}", (int)q.Fatigue, q.Fatigue >= Game.FAT_DOZE ? "  ·  EXHAUSTED" : q.Fatigue >= Game.FAT_TIRED ? "  ·  TIRED" : ""), 17, FatigueCol(q.Fatigue), true);
            c.KBar(fr.Rest, (float)(q.Fatigue / 100.0), FatigueRole(q.Fatigue), ((int)q.Fatigue).ToString());
            var notes = new List<string>();
            if (q.Collapsed) notes.Add("Collapsed from exhaustion.");
            else if (q.SleepEndT > t) notes.Add("Asleep for about " + Hrs(q.SleepEndT - t) + ".");
            if (q.RestEndT > t) notes.Add("On a rest day for " + Hrs(q.RestEndT - t) + ".");
            if (q.FreezeEndT > t) notes.Add("Shut down: refuses to work for " + Hrs(q.FreezeEndT - t) + ".");
            if (q.Dozing) notes.Add("Dozed off on watch.");
            if (q.Guard && q.Shift > 0) notes.Add(string.Format("Night watch: shift {0} ({1}).", q.Shift, ShiftHours(q.Shift, g.PplShifts)));
            if (q.Stress >= 80.0) notes.Add("At breaking point: they may freeze, steal, run or snap. A talk helps.");
            if (notes.Count > 0) { string nt = string.Join("  ", notes); c.Para(S.Top(Math.Min(S.Rest.height, c.H_(nt, 16, false, S.Rest.width))), nt, 16, q.Stress >= 80.0 ? AMBER : INK2); }
        }

        static void PplDo(MenuCtx c, string action, string ok)
        {
            string e = c.Act(action, new object[0]) ?? "";
            c.Toast(e != "" ? e : ok);
        }

        static string ShiftHours(int s, int n)
        {
            if (n <= 1) return "18-06";
            if (n == 2) return s == 1 ? "18-00" : "00-06";
            return s == 1 ? "18-22" : s == 2 ? "22-02" : "02-06";
        }

        static readonly List<string> SHIFT_ITEMS = new List<string> { "1 SHIFT", "2 SHIFTS", "3 SHIFTS" };

        static void WatchScreen(MenuCtx c)
        {
            var g = c.G; var col = g.C;
            if (col.Ppl == null) col.Ppl = new PeopleCfg();
            var cfg = col.Ppl;
            var L = Frame(c, "NIGHT WATCH", () => PplWatch = false);
            Footer(c, L, "Tired guards doze off and miss shots. More shifts, more rest. The rota is set at 18:00, most rested first.");

            // shifts (a wheel, the original's 1 / 2 / 3 buttons) + morale + auto talk
            int n = g.PplShifts;
            var r1 = new UiLayout(L.Top(BTN_H), 12f);
            c.Fit(r1.Left(110f), "SHIFTS", 20, INK, true);
            c.Wheel("ppl_shifts", r1.Left(390f), SHIFT_ITEMS, Math.Max(0, Math.Min(2, n - 1)), k => { cfg.Shifts = k + 1; }, true, true, 3, 19);
            string sh = n <= 1 ? "One watch, 18:00-06:00. Guards get tired fast." : n == 2 ? "18:00-00:00 and 00:00-06:00." : "18:00-22:00, 22:00-02:00 and 02:00-06:00.";
            c.Fit(r1.Rest, sh, 17, INK2, false);
            double m = Math.Max(0.0, Math.Min(100.0, col.Morale));
            var mc = m >= 60.0 ? PPL_CALM : m >= 40.0 ? PPL_TENSE : m >= 20.0 ? PPL_STRESSED : PPL_BREAK;
            var r2 = new UiLayout(L.Top(BTN_H), 12f);
            c.Fit(r2.Left(200f), string.Format("MORALE {0}%", (int)Math.Round(m)), 20, mc, true);
            c.KBar(UiLayout.Center(r2.Left(290f), 290f, 30f), (float)(m / 100.0), m >= 60.0 ? UiRole.Primary : m >= 40.0 ? UiRole.Accent : UiRole.Danger, (int)Math.Round(m) + "%");
            bool at = cfg.AutoTalk;
            c.Btn(r2.Left(250f), (at ? "[x] " : "[  ] ") + "AUTO TALK", at ? UiRole.Accent : UiRole.Neutral, () => { cfg.AutoTalk = !at; }, true, at, 19);
            c.Fit(r2.Rest, "Medics and Leaders talk to anyone at breaking point.", 16, INK2, false);

            // guards
            var guards = g.AlivePeople().Where(q => q.Guard && !g.IsMinor(q)).OrderBy(q => q.Base).ThenBy(q => q.Shift).ThenBy(q => q.Id).ToList();
            c.Header(L.Top(40f), string.Format("GUARDS ({0})", guards.Count), 20);
            var view = L.Rest;
            if (guards.Count == 0) { c.Fit(new Rect(view.x, view.y, view.width, 30f), "Nobody is on guard. Tick GUARD in CREW or turn on Auto-guards.", 18, AMBER, false); return; }
            const float RH = 56f;
            float cw = c.Scroll("ppl_guards", view, guards.Count * (RH + 6f) - 6f);
            float y = 0f; int k2 = 0;
            foreach (var q in guards)
            {
                var bs = col.Bases.FirstOrDefault(bb => bb.Id == q.Base);
                var row = new Rect(0, y, cw, RH);
                c.Plate(row, k2++);
                var cc = UiLayout.Columns(UiLayout.Inset(row, 14f, 8f, 10f, 8f), 12f, 1.3f, 1f, 1f, 1.3f, 0.9f);
                c.Fit(cc[0], q.Name, 18, INK, true);
                c.Fit(cc[1], bs != null ? bs.Name : "-", 16, INK2, false);
                c.Fit(cc[2], q.Shift > 0 ? string.Format("SHIFT {0}  {1}", q.Shift, ShiftHours(q.Shift, n)) : "SHIFT -", 16, AMBER, true);
                c.KBar(UiLayout.Center(cc[3], cc[3].width, 28f), (float)(q.Fatigue / 100.0), FatigueRole(q.Fatigue), "FATIGUE " + (int)q.Fatigue);
                string st = q.Dozing ? "DOZING" : g.Asleep(q) ? "ASLEEP" : q.St != "base" ? "AWAY" : !(g.Hour() >= 18 || g.Hour() < 6) ? "DAYTIME" : g.OnShift(q) ? "ON WATCH" : "OFF SHIFT";
                c.Fit(cc[4], st, 16, q.Dozing ? PPL_BREAK : st == "ON WATCH" ? GREEN : INK2, true, MenuAlign.Right);
                y += RH + 6f;
            }
            c.EndScroll();
        }
    }
}
