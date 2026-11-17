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
        const float PPL_LINE = 38f;

        static bool PplOn(MenuCtx c) => Game.ExpansionRules && c?.G?.C != null;

        public static (string name, Color col) StressBand(double s)
            => s >= 80.0 ? ("BREAKING", PPL_BREAK) : s >= 60.0 ? ("STRESSED", PPL_STRESSED) : s >= 40.0 ? ("TENSE", PPL_TENSE) : ("CALM", PPL_CALM);

        static Color FatigueCol(double f) => f >= Game.FAT_DOZE ? PPL_BREAK : f >= Game.FAT_TIRED ? PPL_STRESSED : PPL_CALM;

        /// <summary>Hook for the CREW row heights: the extra line under adult rows.</summary>
        public static float PplRowH(Game g, Person q) => Game.ExpansionRules && g != null && !g.IsMinor(q) ? PPL_LINE : 0f;

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
                c.Label(new Rect(x, y + 3, 90, 20), "no traits", 13, INK2, false, false);
                return x + 92f;
            }
            foreach (var id in q.Traits)
            {
                bool hid = id == q.TraitHidden;
                string t = hid ? "?" : (Game.Traits.TryGetValue(id, out var td) ? td.Name : id).ToUpperInvariant();
                float w = Math.Max(30f, t.Length * 8.6f + 16f);
                if (x + w > maxX) break;
                c.Box(new Rect(x, y, w, 24), hid ? PPL_CHIP_HID : PPL_CHIP);
                c.Box(new Rect(x, y, 2, 24), hid ? AMBER : GREEN);
                c.Label(new Rect(x + 2, y + 4, w - 2, 18), t, 13, hid ? AMBER : INK, true, false, MenuAlign.Center);
                x += w + 6f;
            }
            return x;
        }

        /// <summary>Hook in CREW: the line under a survivor row (traits, stress face, fatigue bar, STRESS tag, DETAILS).</summary>
        public static void PplRow(MenuCtx c, Person q, float x, float iw, float y)
        {
            if (!PplOn(c) || c.G.IsMinor(q)) return;
            float cy = y + 4f, rx = x + 30f, right = x + iw;
            int pid = q.Id;
            c.Button(new Rect(right - 132f, y + 1f, 132f, PPL_LINE - 4f), "TALK / REST", "dark", 14, () => { PplDetail = pid; PplWatch = false; });
            rx = TraitChips(c, q, rx, cy + 2f, x + 30f + 330f);
            rx = Math.Max(rx + 8f, x + 370f);
            StressFace(c, rx, cy + 3f, q.Stress); rx += 28f;
            var b = StressBand(q.Stress);
            c.Label(new Rect(rx, cy + 5f, 96, 20), b.name, 13, b.col, true, false); rx += 98f;
            c.Label(new Rect(rx, cy + 5f, 70, 20), "FATIGUE", 12, INK2, false, false); rx += 62f;
            c.Bar(new Rect(rx, cy + 11f, 90f, 6f), (float)(q.Fatigue / 100.0), FatigueCol(q.Fatigue)); rx += 100f;
            if (q.Stress >= 80.0)
            {
                c.Box(new Rect(rx, cy + 1f, 104f, 26f), new Color(0.55f, 0.36f, 0.08f, 0.95f));
                c.Label(new Rect(rx, cy + 5f, 104f, 20f), "STRESS " + (int)q.Stress, 14, INK, true, false, MenuAlign.Center);
            }
        }

        /// <summary>Hook at the top of CREW (after its frame): the NIGHT WATCH button, or the survivor sheet / NIGHT WATCH
        /// screen in place of the list. True when one of those took over.</summary>
        public static bool PplCrewModal(MenuCtx c, Rect p)
        {
            if (!PplOn(c)) { PplDetail = -1; PplWatch = false; return false; }
            if (PplDetail >= 0)
            {
                var q = c.G.AlivePeople().FirstOrDefault(x => x.Id == PplDetail);
                if (q == null) PplDetail = -1;
                else { PersonSheet(c, p, q); return true; }
            }
            if (PplWatch) { WatchScreen(c, p); return true; }
            c.Button(new Rect(p.xMax - 142f - 196f, p.y + 10f, 186f, 48f), "NIGHT WATCH", "olive", 18, () => { PplWatch = true; PplWatchPage = 0; });
            return false;
        }

        static Rect PplCover(MenuCtx c, Rect p, string title, Action back)
        {
            c.Box(new Rect(p.x + 3, p.y + 3, p.width - 6, p.height - 6), PANEL);
            c.Box(new Rect(p.x, p.y, p.width, 3), AMBER);
            c.Label(new Rect(p.x + 18, p.y + 12, p.width - 340, 38), title, 28, AMBER, true, false);
            c.Button(new Rect(p.xMax - 142, p.y + 10, 128, 48), "BACK", "dark", 20, back);
            return p;
        }

        static string Hrs(double h) => h >= 1.0 ? string.Format("{0:0}h", Math.Ceiling(h)) : "<1h";

        static void PersonSheet(MenuCtx c, Rect p, Person q)
        {
            var g = c.G;
            PplCover(c, p, "SURVIVOR · " + q.Name.ToUpperInvariant(), () => PplDetail = -1);
            float x = p.x + 18, iw = p.width - 36, y = p.y + 66;
            var stt = CrewStatusText(g, q);
            c.Label(new Rect(x, y, iw, 24), string.Format("{0}  ·  {1}", q.Job, stt.text), 17, INK, true, false); y += 32f;

            // traits
            c.Label(new Rect(x, y, iw, 22), "TRAITS", 15, INK2, true, false); y += 26f;
            if (q.Traits == null || q.Traits.Count == 0) { c.Label(new Rect(x + 12, y, iw - 12, 22), "Nothing stands out about them yet.", 15, INK2, false, false); y += 26f; }
            else foreach (var id in q.Traits)
                {
                    bool hid = id == q.TraitHidden;
                    Game.Traits.TryGetValue(id, out var td);
                    string nm = hid ? "?" : (td?.Name ?? id);
                    string ds = hid ? string.Format("Still getting to know them. Shows in about {0}.", Hrs(q.JoinT + Game.TRAIT_REVEAL_H - g.S.T)) : (td?.Desc ?? "");
                    c.Box(new Rect(x + 12, y, 150, 26), hid ? PPL_CHIP_HID : PPL_CHIP);
                    c.Label(new Rect(x + 12, y + 4, 150, 20), nm.ToUpperInvariant(), 14, hid ? AMBER : INK, true, false, MenuAlign.Center);
                    float dh = Math.Max(26f, c.H_(ds, 14, false, iw - 190f));
                    c.Label(new Rect(x + 176, y + 4, iw - 190f, dh), ds, 14, INK2);
                    y += dh + 6f;
                }
            y += 6f;

            // stress and fatigue
            var b = StressBand(q.Stress);
            StressFace(c, x, y + 1f, q.Stress);
            c.Label(new Rect(x + 32, y + 2, 300, 22), string.Format("STRESS {0}  ·  {1}", (int)q.Stress, b.name), 16, b.col, true, false);
            c.Bar(new Rect(x + 340, y + 8, 260, 10), (float)(q.Stress / 100.0), b.col); y += 32f;
            c.Label(new Rect(x + 32, y + 2, 300, 22), string.Format("FATIGUE {0}{1}", (int)q.Fatigue, q.Fatigue >= Game.FAT_DOZE ? "  ·  EXHAUSTED" : q.Fatigue >= Game.FAT_TIRED ? "  ·  TIRED" : ""), 16, FatigueCol(q.Fatigue), true, false);
            c.Bar(new Rect(x + 340, y + 8, 260, 10), (float)(q.Fatigue / 100.0), FatigueCol(q.Fatigue)); y += 32f;

            var notes = new List<string>();
            double t = g.S.T;
            if (q.Collapsed) notes.Add("Collapsed from exhaustion.");
            else if (q.SleepEndT > t) notes.Add("Asleep for about " + Hrs(q.SleepEndT - t) + ".");
            if (q.RestEndT > t) notes.Add("On a rest day for " + Hrs(q.RestEndT - t) + ".");
            if (q.FreezeEndT > t) notes.Add("Shut down: refuses to work for " + Hrs(q.FreezeEndT - t) + ".");
            if (q.Dozing) notes.Add("Dozed off on watch.");
            if (q.Guard && q.Shift > 0) notes.Add(string.Format("Night watch: shift {0} ({1}).", q.Shift, ShiftHours(q.Shift, g.PplShifts)));
            if (q.Stress >= 80.0) notes.Add("At breaking point: they may freeze, steal, run or snap. A talk helps.");
            if (notes.Count > 0) { string nt = string.Join("  ", notes); float nh = c.H_(nt, 15, false, iw); c.Label(new Rect(x, y, iw, nh), nt, 15, q.Stress >= 80.0 ? AMBER : INK2); y += nh + 8f; }
            y += 6f;

            // actions
            int pid = q.Id;
            bool talked = q.TalkDay == g.Day(), away = q.St != "base", resting = q.RestEndT > t;
            c.Button(new Rect(x, y, 260, 56), "TALK", "amber", 20, () => PplDo(c, "ppl_talk:" + pid, "They talked it through."), !talked && !away);
            c.Button(new Rect(x + 276, y, 260, 56), "REST DAY", "olive", 20, () => PplDo(c, "ppl_rest:" + pid, "Off duty for a day: stress eases, no work, no watch."), !resting);
            y += 62f;
            string hint = talked ? "Someone already talked with them today." : away ? "Talking needs them at camp." : "TALK: a Medic, a Leader or the calmest person sits with them (-15 stress, once a day).";
            hint += resting ? "  Already resting." : "  REST DAY: -25 stress, no job and no guard duty for 24h.";
            c.Label(new Rect(x, y, iw, c.H_(hint, 14, false, iw)), hint, 14, INK2);
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

        static void WatchScreen(MenuCtx c, Rect p)
        {
            var g = c.G; var col = g.C;
            if (col.Ppl == null) col.Ppl = new PeopleCfg();
            var cfg = col.Ppl;
            PplCover(c, p, "NIGHT WATCH", () => PplWatch = false);
            float x = p.x + 18, iw = p.width - 36, y = p.y + 66;

            // shifts
            c.Label(new Rect(x, y + 14, 120, 24), "SHIFTS", 17, INK, true, false);
            int n = g.PplShifts;
            for (int k = 1; k <= 3; k++)
            {
                int kk = k;
                c.Button(new Rect(x + 120 + (k - 1) * 84, y, 76, 52), k.ToString(), n == k ? "sel" : "dark", 22, () => { cfg.Shifts = kk; });
            }
            string sh = n <= 1 ? "One watch, 18:00-06:00. Guards get tired fast." : n == 2 ? "18:00-00:00 and 00:00-06:00." : "18:00-22:00, 22:00-02:00 and 02:00-06:00.";
            c.Label(new Rect(x + 382, y + 4, iw - 382, 44), sh + " The rota is set at 18:00, most rested first.", 14, INK2);
            y += 62f;

            // morale + auto talk
            double m = Math.Max(0.0, Math.Min(100.0, col.Morale));
            var mc = m >= 60.0 ? PPL_CALM : m >= 40.0 ? PPL_TENSE : m >= 20.0 ? PPL_STRESSED : PPL_BREAK;
            c.Label(new Rect(x, y + 14, 220, 24), string.Format("MORALE {0}%", (int)Math.Round(m)), 17, mc, true, false);
            c.Bar(new Rect(x + 200, y + 20, 240, 12), (float)(m / 100.0), mc);
            bool at = cfg.AutoTalk;
            c.Button(new Rect(x + 470, y, 230, 52), (at ? "[x] " : "[  ] ") + "AUTO TALK", at ? "sel" : "dark", 18, () => { cfg.AutoTalk = !at; });
            c.Label(new Rect(x + 716, y + 2, iw - 716, 50), "Medics and Leaders talk to anyone at breaking point.", 14, INK2);
            y += 64f;

            // guards
            var guards = g.AlivePeople().Where(q => q.Guard && !g.IsMinor(q)).OrderBy(q => q.Base).ThenBy(q => q.Shift).ThenBy(q => q.Id).ToList();
            c.Box(new Rect(x, y, iw, 2), new Color(0.30f, 0.40f, 0.28f)); y += 8f;
            c.Label(new Rect(x, y, iw, 22), string.Format("GUARDS ({0})", guards.Count), 15, INK2, true, false); y += 28f;
            if (guards.Count == 0) { c.Label(new Rect(x + 12, y, iw - 12, 24), "Nobody is on guard. Tick GUARD in CREW or turn on Auto-guards.", 15, AMBER, false, false); return; }
            const float RH = 40f;
            int per = Math.Max(1, (int)((p.yMax - 66f - y) / RH));
            int pages = (guards.Count + per - 1) / per;
            PplWatchPage = Math.Max(0, Math.Min(PplWatchPage, pages - 1));
            foreach (var q in guards.Skip(PplWatchPage * per).Take(per))
            {
                var bs = col.Bases.FirstOrDefault(bb => bb.Id == q.Base);
                c.Box(new Rect(x, y, iw, RH - 4), new Color(0.10f, 0.13f, 0.10f, 0.9f));
                c.Label(new Rect(x + 10, y + 8, 200, 22), q.Name, 16, INK, true, false);
                c.Label(new Rect(x + 214, y + 9, 160, 22), bs != null ? bs.Name : "-", 14, INK2, false, false);
                c.Label(new Rect(x + 380, y + 9, 170, 22), q.Shift > 0 ? string.Format("SHIFT {0}  {1}", q.Shift, ShiftHours(q.Shift, n)) : "SHIFT -", 14, AMBER, true, false);
                c.Label(new Rect(x + 556, y + 9, 70, 22), "FATIGUE", 12, INK2, false, false);
                c.Bar(new Rect(x + 618, y + 15, 110, 6), (float)(q.Fatigue / 100.0), FatigueCol(q.Fatigue));
                c.Label(new Rect(x + 734, y + 9, 40, 22), ((int)q.Fatigue).ToString(), 14, FatigueCol(q.Fatigue), true, false);
                string st = q.Dozing ? "DOZING" : g.Asleep(q) ? "ASLEEP" : q.St != "base" ? "AWAY" : !(g.Hour() >= 18 || g.Hour() < 6) ? "DAYTIME" : g.OnShift(q) ? "ON WATCH" : "OFF SHIFT";
                c.Label(new Rect(x + 780, y + 9, iw - 790, 22), st, 14, q.Dozing ? PPL_BREAK : st == "ON WATCH" ? GREEN : INK2, true, false);
                y += RH;
            }
            Pager(c, p, PplWatchPage, pages, v => PplWatchPage = v, "Tired guards doze off and miss shots. More shifts, more rest.");
        }
    }
}
