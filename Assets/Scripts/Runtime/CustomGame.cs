// E14 custom game: four rule multipliers chosen on the title's CUSTOM screen (zombie count, loot amount, raid strength,
// day length). They live in the save as GameState.Custom (Core CustomRules; null = a normal game) and are read by the
// sim at the few points listed in PORT_NOTES.md. Every multiplier at 1.0 is exactly the original game.
// This file holds the choices, the short text code (for seed codes / sharing) and the screen layout (a SideMenus
// display list, so the game paints it with the UI kit and the offline preview paints the same list to PNG).
using System;
using System.Collections.Generic;
using System.Globalization;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    /// <summary>What the CUSTOM screen is showing (the choices themselves are CustomGame.Draft).</summary>
    public sealed class CustomGameState
    {
        public bool Open;
        public int Difficulty = 1;
    }

    public static class CustomGame
    {
        public static readonly double[] ZOMBIE_STEPS = { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 2.5, 3.0 };
        public static readonly double[] LOOT_STEPS = { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0 };
        public static readonly double[] RAID_STEPS = { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 2.5, 3.0 };
        public static readonly double[] DAY_STEPS = { 0.5, 0.75, 1.0, 1.5, 2.0, 3.0 };

        /// <summary>What the CUSTOM screen is editing (kept between visits while the app runs).</summary>
        public static CustomRules Draft = new CustomRules();

        /// <summary>Rules the next NewGame starts with (null = normal game). Set by START on the CUSTOM screen, consumed
        /// by ApplyNewRun. A seed code that carries multipliers can set this before calling Bootstrap.NewGame.</summary>
        public static CustomRules Pending;

        static Game _g;
        /// <summary>Remember the running game (Bootstrap.Update via Achievements.Tick), so Current needs no Bootstrap.</summary>
        public static void Track(Game g) { _g = g; }

        /// <summary>The running game's multipliers (all 1.0 for a normal game or no game). A copy: changing it changes nothing.</summary>
        public static CustomRules Current => Of(_g);
        public static CustomRules Of(Game g) => g?.S?.Custom != null ? g.S.Custom.Clone() : new CustomRules();
        public static bool IsCustom(Game g) => g?.S?.Custom != null && !g.S.Custom.IsDefault;

        /// <summary>Right after Game.NewRun: write the pending rules into the new state (null when they are all 1.0).</summary>
        public static void ApplyNewRun(Game g)
        {
            var p = Pending; Pending = null;
            if (g?.S == null) return;
            g.S.Custom = p != null && !p.IsDefault ? p.Clone() : null;
        }

        // ---------------- text code ----------------
        /// <summary>"Z150 L100 R200 D100" style code without spaces: Z150L100R200D100 (percent). Normal game = "".</summary>
        public static string Code(CustomRules r)
        {
            if (r == null || r.IsDefault) return "";
            int P(double v) => (int)Math.Round(v * 100.0);
            return string.Format(CultureInfo.InvariantCulture, "Z{0}L{1}R{2}D{3}", P(r.Zombies), P(r.Loot), P(r.Raids), P(r.DayLen)) + (r.NoExile ? "X0" : "");
        }

        /// <summary>Parse a Code() string (case-insensitive, any order, missing parts = 100). Empty = normal game.</summary>
        public static bool TryParse(string code, out CustomRules r)
        {
            r = new CustomRules();
            if (string.IsNullOrWhiteSpace(code)) return true;
            string s = code.Trim().ToUpperInvariant();
            int i = 0;
            while (i < s.Length)
            {
                char k = s[i++];
                int j = i;
                while (j < s.Length && char.IsDigit(s[j])) j++;
                if (j == i || !int.TryParse(s.Substring(i, j - i), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pc)) return false;
                double v = Math.Max(10, Math.Min(500, pc)) / 100.0;
                if (k == 'X') r.NoExile = pc == 0;   // v0.19 EXILE option: X0 = off
                else if (k == 'Z') r.Zombies = v; else if (k == 'L') r.Loot = v; else if (k == 'R') r.Raids = v; else if (k == 'D') r.DayLen = v; else return false;
                i = j;
            }
            return true;
        }

        public static string Mul(double v) => "x" + v.ToString(v == Math.Floor(v) ? "0.0" : "0.0#", CultureInfo.InvariantCulture);

        /// <summary>One line for save cards / summaries: "Custom: zombies x2.0, loot x0.5". Empty for a normal game.</summary>
        public static string Summary(CustomRules r)
        {
            if (r == null || r.IsDefault) return "";
            var p = new List<string>();
            if (r.Zombies != 1.0) p.Add("zombies " + Mul(r.Zombies));
            if (r.Loot != 1.0) p.Add("loot " + Mul(r.Loot));
            if (r.Raids != 1.0) p.Add("raids " + Mul(r.Raids));
            if (r.DayLen != 1.0) p.Add("day length " + Mul(r.DayLen));
            if (r.NoExile) p.Add("no exile");
            return "Custom: " + string.Join(", ", p);
        }

        static int StepIndex(double[] steps, double v)
        {
            int best = 0;
            for (int i = 1; i < steps.Length; i++) if (Math.Abs(steps[i] - v) < Math.Abs(steps[best] - v)) best = i;
            return best;
        }
        public static double Step(double[] steps, double v, int dir) => steps[Math.Max(0, Math.Min(steps.Length - 1, StepIndex(steps, v) + dir))];

        // ---------------- screen ----------------
        static Color INK => SideMenus.INK; static Color INK2 => SideMenus.INK2; static Color MUTE => SideMenus.MUTE;
        static Color AMBER => SideMenus.AMBER; static Color GREEN => SideMenus.GREEN; static Color RED => SideMenus.RED;

        static string ZombieNote(double v) => v == 1.0 ? "Normal city crowds" : v < 1.0 ? "Fewer dead on the streets" : "More dead on every street";
        static string LootNote(double v) => v == 1.0 ? "Normal finds per building" : v < 1.0 ? "Scavengers bring back less" : "Scavengers bring back more";
        static string RaidNote(double v) => v == 1.0 ? "Normal night raids and hordes" : v < 1.0 ? "Smaller raids and hordes" : "Bigger raids and hordes";
        static string DayNote(double v) => v == 1.0 ? "Normal clock" : v < 1.0 ? "Days pass faster in real time" : "Days last longer in real time";

        /// <summary>The CUSTOM screen: difficulty, four steppers, RESET / BACK / START. start(difficulty) begins the game.</summary>
        public static void Screen(MenuCtx c, CustomGameState st, Action<int> start, Action back)
        {
            var d = Draft;
            c.Dim();
            float pw = Math.Min(1120f, c.W - 40f), ph = Math.Min(680f, c.H - 24f);
            var inner = c.Panel(new Rect((c.W - pw) / 2f, (c.H - ph) / 2f, pw, ph), "CUSTOM GAME");
            var L = new UiLayout(inner, 8f);
            var F = new UiLayout(L.Bottom(70f), 12f);
            c.Btn(F.Right(250f), "START", UiRole.Accent, () => start(st.Difficulty), true, false, 26);
            c.Btn(F.Left(170f), "BACK", UiRole.Danger, back, true, false, 22);
            c.Btn(F.Left(170f), "RESET", UiRole.Neutral, () => Draft = new CustomRules(), !d.IsDefault, false, 22);
            c.Fit(F.Rest, d.IsDefault ? "All x1.0 is the normal game." : "Saved with the game.", 16, INK2, false, MenuAlign.Center);

            // difficulty row
            var dr = L.Top(64f);
            var dc = new UiLayout(dr, 12f);
            c.Fit(dc.Left(220f), "Difficulty", 21, INK, true);
            var db = UiLayout.Columns(dc.Rest, 10f, 1f, 1f, 1f);
            for (int i = 0; i < Game.DIFF_NAMES.Length && i < db.Length; i++)
            {
                int di = i;
                c.Btn(db[i], Game.DIFF_NAMES[i].ToUpperInvariant(), st.Difficulty == i ? UiRole.Accent : UiRole.Primary, () => st.Difficulty = di, true, st.Difficulty == i, 22);
            }
            L.Top(2f);
            var rows = UiLayout.Rows(L.Rest, 8f, 1f, 1f, 1f, 1f, 1f);
            Stepper(c, rows[0], 0, "Zombie count", d.Zombies, ZombieNote(d.Zombies), ZOMBIE_STEPS, v => Draft.Zombies = v);
            Stepper(c, rows[1], 1, "Loot amount", d.Loot, LootNote(d.Loot), LOOT_STEPS, v => Draft.Loot = v);
            Stepper(c, rows[2], 2, "Raid strength", d.Raids, RaidNote(d.Raids), RAID_STEPS, v => Draft.Raids = v);
            Stepper(c, rows[3], 3, "Day length", d.DayLen, DayNote(d.DayLen), DAY_STEPS, v => Draft.DayLen = v);
            ExileRow(c, rows[4], 4, !d.NoExile);
        }

        /// <summary>v0.19 EXILE on / off (expanded rules: a camp that turns on you can vote you out; Core/Popularity.cs).</summary>
        static void ExileRow(MenuCtx c, Rect row, int idx, bool on)
        {
            c.Plate(row, idx, !on);
            var R = new UiLayout(UiLayout.Inset(row, 16f, 6f, 10f, 6f), 12f);
            float bh = Math.Min(76f, R.Rest.height);
            Rect B(Rect r, float w) => UiLayout.Center(r, w, bh);
            var off = B(R.Right(110f), 110f);
            var onB = B(R.Right(110f), 110f);
            c.Btn(onB, "ON", on ? UiRole.Accent : UiRole.Primary, () => Draft.NoExile = false, true, on, 24);
            c.Btn(off, "OFF", !on ? UiRole.Accent : UiRole.Primary, () => Draft.NoExile = true, true, !on, 24);
            var tx = UiLayout.Rows(R.Rest, 2f, 1.15f, 0.85f);
            c.Fit(tx[0], "Exile", 23, INK, true);
            c.Fit(tx[1], on ? "Expanded rules: lose your people's support and they can vote you out" : "Your people never vote you out", 17, INK2, false);
        }

        static void Stepper(MenuCtx c, Rect row, int idx, string name, double v, string note, double[] steps, Action<double> set)
        {
            c.Plate(row, idx, v != 1.0);
            var R = new UiLayout(UiLayout.Inset(row, 16f, 6f, 10f, 6f), 12f);
            float bh = Math.Min(76f, R.Rest.height);
            Rect B(Rect r, float w) => UiLayout.Center(r, w, bh);
            var plus = B(R.Right(110f), 110f);
            var val = R.Right(130f);
            var minus = B(R.Right(110f), 110f);
            int si = StepIndex(steps, v);
            c.Btn(minus, "-", UiRole.Primary, () => set(Step(steps, v, -1)), si > 0, false, 34);
            c.Btn(plus, "+", UiRole.Primary, () => set(Step(steps, v, 1)), si < steps.Length - 1, false, 34);
            var vr = UiLayout.Rows(val, 0f, 1.3f, 0.7f);
            c.Fit(vr[0], Mul(v), 30, v == 1.0 ? INK : v > 1.0 ? AMBER : GREEN, true, MenuAlign.Center);
            c.Fit(vr[1], v == 1.0 ? "normal" : "custom", 14, MUTE, false, MenuAlign.Center);
            var tx = UiLayout.Rows(R.Rest, 2f, 1.15f, 0.85f);
            c.Fit(tx[0], name, 23, INK, true);
            c.Fit(tx[1], note, 17, INK2, false);
        }
    }
}
