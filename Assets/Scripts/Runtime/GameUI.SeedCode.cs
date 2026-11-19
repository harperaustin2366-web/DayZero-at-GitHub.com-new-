// GameUI glue for E13 challenge codes (SeedCode.cs) and E18 auto-assign (AutoAssign.cs):
//   title screen   ENTER CHALLENGE CODE button, which opens the keypad sheet (SeedCodeUi.Entry)
//   Menu tab       strip under the cards: the current run's code with a COPY button
//   run summary    the code chip in the sheet header
//   Jobs tab       AUTO-ASSIGN bar above the list, LOCK toggle on every row (locks kept per run in PlayerPrefs)
using System;
using System.Collections.Generic;
using System.Linq;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed partial class GameUI
    {
        readonly SeedEntryState _seedEntry = new SeedEntryState();

        // ---------------- E13 challenge codes ----------------
        /// <summary>Title screen: the keypad sheet while it is open. True when drawn (the title is skipped).</summary>
        bool DrawSeedEntry(float w, float h)
        {
            if (!_seedEntry.Open) return false;
            var ctx = NewCtx(w, h, MapView.Instance);
            SeedCodeUi.Entry(ctx, _seedEntry, StartFromCode, null);
            SideMenus.Flush(ctx);
            return true;
        }

        void StartFromCode(int seed, int difficulty, bool expanded, CustomRules custom)
        {
            Game.ExpansionRules = expanded;   // NewRun copies it into the save (the title toggle is left as it was)
            CustomGame.Pending = custom;      // E14 multipliers of a custom code (null = normal game); ApplyNewRun consumes it
            _b.NewGame(difficulty, seed);
            _b.Toast("Challenge code " + SeedCode.Pretty(SeedCode.Encode(seed, difficulty, expanded, custom)) + ": " + SeedCode.Describe(difficulty, expanded, custom) + ".");
        }

        /// <summary>Title screen: ENTER CHALLENGE CODE under the ACHIEVEMENTS / CUSTOM GAME card (same column as
        /// DrawTitleExtras), level with the TUTORIAL toggle.</summary>
        void DrawSeedCodeTitle(float w, float h)
        {
            float x = 40f + Mathf.Min(w - 80f, 720f) + 20f;
            float cw = Mathf.Min(360f, w - 40f - x);
            if (cw < 220f) { x = Mathf.Max(40f, w - 300f); cw = 260f; }
            if (KBtn(new Rect(x, 528f, cw, 60f), "ENTER CHALLENGE CODE", UiRole.Primary, false, 20)) { _seedEntry.Reset(); _seedEntry.Open = true; }
            UiKit.FitLabel(new Rect(x + 4f, 592f, cw - 8f, 24f), "Play a friend's city and settings", 15, TextAnchor.MiddleLeft, UiKit.InkDim, false);
        }

        /// <summary>Menu tab: a strip under the GAME / SETTINGS cards with the run's code, COPY and a hint (content = the
        /// tab's content rect; the cards take its top 430 px, or the strip sits on their bottom edge when the UI is scaled up).</summary>
        void DrawSeedCodeStrip(Rect content)
        {
            string code = SeedCode.ForGame(G);
            if (code == "") return;
            float y = content.y + Mathf.Min(content.height, 430f) + 10f;
            if (y + 64f > content.yMax) y = content.yMax - 64f;
            var strip = new Rect(content.x, y, content.width, 64f);
            UiKit.Panel(strip);
            var r = UiLayout.Columns(UiLayout.Inset(strip, 10f, 6f, 10f, 6f), 12f, -360f, -130f, 1f);
            UiKit.Chip(r[0], "CHALLENGE CODE  " + SeedCode.Pretty(code), UiRole.Accent, 19);
            UiKit.FitLabel(r[2], "Friends type it on the title screen (ENTER CHALLENGE CODE) to play this city.", 16, TextAnchor.MiddleLeft, UiKit.InkDim, false);
            if (KBtn(r[1], "COPY", UiRole.Neutral, false, 20))
            {
                try { GUIUtility.systemCopyBuffer = code; _b.Toast("Code " + SeedCode.Pretty(code) + " copied. Friends enter it on the title screen (ENTER CODE)."); }
                catch (Exception) { _b.Toast("Your code is " + SeedCode.Pretty(code) + "."); }
            }
        }

        /// <summary>Run summary: code chip in the header (drawn into the summary's display list before Flush).</summary>
        void DrawSeedSummary(MenuCtx ctx) => SeedCodeUi.SummaryChip(ctx, G);

        // ---------------- E18 auto-assign ----------------
        HashSet<int> _jobLocks = new HashSet<int>();
        string _jobLockKey = "";
        string _autoMsg = "";

        string JobLockKey() => G?.S == null ? "" : "dz_joblock_" + G.S.Seed + "_" + Math.Max(0, G.CurCol);

        HashSet<int> JobLocks()
        {
            string k = JobLockKey();
            if (k != _jobLockKey)
            {
                _jobLockKey = k; _jobLocks = new HashSet<int>(); _autoMsg = "";
                if (k != "")
                    foreach (var s in PlayerPrefs.GetString(k, "").Split(','))
                        if (int.TryParse(s, out int id)) _jobLocks.Add(id);
            }
            return _jobLocks;
        }

        void SaveJobLocks()
        {
            if (_jobLockKey == "") return;
            PlayerPrefs.SetString(_jobLockKey, string.Join(",", _jobLocks.OrderBy(i => i)));
            PlayerPrefs.Save();
        }

        /// <summary>Jobs tab: AUTO-ASSIGN button and a one-line explanation / last result. Returns the list area.</summary>
        Rect DrawAutoAssignBar(Rect area)
        {
            var locks = JobLocks();
            var L = new UiLayout(area, 8f);
            var bar = UiLayout.Columns(L.Top(60f), 12f, -270f, 1f);
            if (KBtn(bar[0], "AUTO-ASSIGN", UiRole.Accent, false, 24)) RunAutoAssign(locks);
            int nl = G.AlivePeople().Count(p => locks.Contains(p.Id));
            string hint = _autoMsg != "" ? _autoMsg
                : "Guards for tonight, enough water and food, medics for the hurt, the rest scavenge and build." + (nl > 0 ? string.Format(" {0} locked survivor{1} kept.", nl, nl == 1 ? "" : "s") : " LOCK a survivor to keep their job.");
            UiKit.Paragraph(bar[1], hint, 16, _autoMsg != "" ? UiKit.Ink : UiKit.InkDim);
            return L.Rest;
        }

        void RunAutoAssign(HashSet<int> locks)
        {
            var plan = AutoAssign.Plan(G, locks, out string summary);
            int done = 0; string err = "";
            foreach (var ch in plan)
            {
                string e = _b.Act("set_job", ch.P.Id, ch.To);
                if (e == "") done++; else if (err == "") err = e;
            }
            _autoMsg = done == 0 && err == "" ? "Jobs already fit: " + summary + "." : string.Format("{0} job{1} changed. Now {2}.", done, done == 1 ? "" : "s", summary);
            if (err != "") _autoMsg += " " + err;
            _b.Toast(_autoMsg);
        }

        /// <summary>Jobs row: LOCK toggle (auto-assign leaves a locked survivor alone; the wheel still works).</summary>
        void DrawJobLock(Rect r, Person p)
        {
            if (G.IsKid(p) || Array.IndexOf(Data.JOBS, p.Job) < 0) return;
            var locks = JobLocks();
            bool on = locks.Contains(p.Id);
            if (KBtn(UiLayout.Center(r, r.width, Mathf.Min(64f, r.height)), on ? "LOCKED" : "LOCK", on ? UiRole.Accent : UiRole.Neutral, on, 17))
            {
                if (on) locks.Remove(p.Id); else locks.Add(p.Id);
                SaveJobLocks();
            }
        }
    }
}
