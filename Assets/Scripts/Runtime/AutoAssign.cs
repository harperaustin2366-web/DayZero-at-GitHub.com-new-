// E18 AUTO-ASSIGN for the Jobs tab. One tap plans a sensible job for every unlocked survivor, camp by camp:
//   1. guards (Soldier / Cop) scaled to tonight's attack and the camp's walls and towers
//   2. a medic when people are hurt or known bitten (two for many wounded)
//   3. Civilians until the camp's water balance is not negative, then Farmers until food is not negative
//      (measured with the game's own BaseBalance, so seasons, gardens, research and fatigue all count)
//   4. a Scientist while research runs, Engineers for repairs, everyone else Scavenger (Civilian or Engineer for the young,
//      the hurt, the exhausted and, with EXPANDED, a visible Coward)
// With EXPANDED rules visible traits (not the hidden one) and fatigue steer who gets which job.
// Runs only when the player taps the button: no RNG, nothing scheduled, so hands-off parity is untouched. The plan is
// applied through the normal set_job action (GameUI.SeedCode.cs), so its rules (minors, children) still hold.
// Locked survivors (per run, kept in PlayerPrefs by the GameUI glue) are never changed, but their jobs count.
using System;
using System.Collections.Generic;
using System.Linq;
using DayZero.Core;

namespace DayZero.Runtime
{
    public static class AutoAssign
    {
        public struct Change { public Person P; public string From, To; }

        static readonly string[] GUARD = { "Soldier", "Cop" };

        /// <summary>Plans the jobs. Returns only the survivors whose job changes; summary = one line for a toast.</summary>
        public static List<Change> Plan(Game g, ICollection<int> locked, out string summary)
        {
            var changes = new List<Change>();
            summary = "";
            if (g?.C == null) return changes;
            var want = new Dictionary<int, string>();
            var tally = new Dictionary<string, int>();
            foreach (var b in g.C.Bases.OrderBy(x => x.Main ? 0 : 1).ThenBy(x => x.Id))
                PlanBase(g, b, locked, want);
            foreach (var p in g.C.People.OrderBy(x => x.Id))
            {
                if (!want.TryGetValue(p.Id, out var job)) continue;
                tally[job] = tally.TryGetValue(job, out var n) ? n + 1 : 1;
                if (job != p.Job) changes.Add(new Change { P = p, From = p.Job, To = job });
            }
            var parts = new List<string>();
            foreach (var j in Data.JOBS)
                if (tally.TryGetValue(j, out var n) && n > 0) parts.Add(n + " " + (n == 1 ? j : Plural(j)));
            summary = parts.Count == 0 ? "" : string.Join(", ", parts);
            return changes;
        }

        static string Plural(string job) => job == "Civilian" ? "Civilians" : job + "s";

        static bool Danger(string job) => Data.DANGER_JOBS.Contains(job);

        static void PlanBase(Game g, Base b, ICollection<int> locked, Dictionary<int, string> want)
        {
            if (b.Done < 1.0) return;
            var all = g.BasePeople(b.Id);
            if (all.Count == 0) return;
            var cands = all.Where(p => !locked.Contains(p.Id) && !g.IsKid(p) && p.Sq <= 0 && Data.JOBS.Contains(p.Job))
                .OrderBy(p => p.Id).ToList();
            if (cands.Count == 0) return;
            var fixedP = all.Where(p => !cands.Contains(p)).ToList();
            var free = new List<Person>(cands);
            var plan = new Dictionary<int, string>();

            int FixedCount(params string[] jobs) => fixedP.Count(p => jobs.Contains(p.Job));
            Person Take(string job)
            {
                Person best = null; double bs = double.MinValue;
                foreach (var p in free)
                {
                    if (g.JobBlock(p, job) != "") continue;
                    double s = Score(g, p, job);
                    if (s > bs) { bs = s; best = p; }
                }
                if (best != null) { free.Remove(best); plan[best.Id] = job; }
                return best;
            }

            // 1) guards: about one per 5 attackers, less what walls and towers hold; outposts keep one when 3+ live there
            var ri = g.RaidInfo();
            bool target = ri != null && (ri.On || ri.Warned) ? ri.Bid == b.Id || (ri.Bid < 0 && b.Main) : b.Main;
            int guards;
            if (target)
            {
                int n = ri != null && (ri.On || ri.Warned) ? ri.N : SafeRaidSize(g);
                double credit = g.TowersOfBase(b.Id).Count * 1.0 + g.EnclosureDefense(b) / 12.0;
                guards = (int)Math.Ceiling(n / 5.0 - credit);
                guards = Math.Max(guards, all.Count >= 3 ? 1 : 0);
            }
            else guards = all.Count >= 3 ? 1 : 0;
            int adults = cands.Count(p => g.JobBlock(p, "Soldier") == "");
            guards = Math.Min(guards - FixedCount(GUARD), (int)Math.Ceiling(adults * 0.45));
            bool ammoOk = g.C.Res.Get("ammo", 0.0) >= 40.0;
            for (int i = 0; i < guards; i++)
            {
                // Soldiers hit harder but burn ammo; Cops when ammo is short. A guard keeps the one of the two they had.
                var p = Take("Soldier");
                if (p == null) break;
                string job = p.Job == "Cop" || p.Job == "Soldier" ? p.Job : ammoOk ? "Soldier" : "Cop";
                if (!ammoOk && job == "Soldier" && Game.Tx(Visible(p), "hit_add") <= 0.0) job = "Cop";
                plan[p.Id] = job;
            }

            // 2) medics: one screens a camp of 6+ for hidden bites anyway; one for any wounded; two for many wounded
            int wounded = all.Count(p => p.Hp < Game.MaxHp(p) * 0.6 || (p.Known && p.Inf >= 0.0));
            int medics = (wounded > 0 || all.Count >= 6 ? 1 : 0) + (wounded >= 3 && cands.Count >= 8 ? 1 : 0);
            medics -= FixedCount("Medic");
            for (int i = 0; i < medics && free.Count > 0; i++) Take("Medic");

            // 3) people who should not scavenge (too young, hurt, exhausted, a visible Coward) take safe work first, so
            //    their Civilian water counts below; then water, then food, measured with BaseBalance on a trial plan
            foreach (var p in free.ToList())
                if (Unfit(g, p)) { free.Remove(p); plan[p.Id] = Score(g, p, "Engineer") > Score(g, p, "Civilian") + 1.0 ? "Engineer" : "Civilian"; }
            // water then food, measured with BaseBalance on a trial assignment
            int cap = Math.Max(1, (int)Math.Ceiling(cands.Count * 0.6));
            int civs = 0, farms = 0;
            double need = all.Sum(p => { string st = g.AgeStage(p); return st == "baby" ? 0.4 : st == "child" ? 0.7 : 1.0; });
            double margin = Stock(g, "water") < need * 48.0 ? 0.1 * need : 0.0;
            double last = double.MinValue;
            while (free.Count > 0 && civs < cap)
            {
                double w = Trial(g, b, cands, plan, "water");
                if (w >= margin || w <= last + 0.05) break;   // balanced, or the last Civilian did not help
                last = w;
                if (Take("Civilian") == null) break;
                civs++;
            }
            margin = Stock(g, "food") < need * 48.0 ? 0.1 * need : 0.0;
            last = double.MinValue;
            while (free.Count > 0 && farms + civs < cap)
            {
                double f = Trial(g, b, cands, plan, "food");
                if (f >= margin || f <= last + 0.25) break;   // winter: a farmer that adds almost nothing is wasted
                last = f;
                if (Take("Farmer") == null) break;
                farms++;
            }

            // 4) research, repairs, then the rest
            if (b.Main && free.Count >= 2 && !string.IsNullOrEmpty(g.C.Research?.Cur) && FixedCount("Scientist") == 0) Take("Scientist");
            int eng = 0;
            if (free.Count >= 2)
            {
                int dmg = SafeRepairs(g, b);
                eng = (b.Main ? 1 : 0) + (dmg >= 6 ? 1 : 0) + (free.Count >= 8 ? 1 : 0) - FixedCount("Engineer");
            }
            for (int i = 0; i < eng && free.Count > 1; i++) Take("Engineer");
            foreach (var p in free) plan[p.Id] = "Scavenger";
            free.Clear();
            foreach (var kv in plan) want[kv.Key] = kv.Value;
        }

        /// <summary>Food or water balance of the camp per hour if the plan were in place: planned people work their
        /// planned job, everyone still unplanned counts as a Scavenger (no food or water). Jobs and states are put
        /// back before returning; BaseBalance draws no random numbers.</summary>
        static double Trial(Game g, Base b, List<Person> cands, Dictionary<int, string> plan, string key)
        {
            var oldJ = new string[cands.Count]; var oldS = new string[cands.Count];
            try
            {
                for (int i = 0; i < cands.Count; i++)
                {
                    var p = cands[i];
                    oldJ[i] = p.Job; oldS[i] = p.St;
                    p.Job = plan.TryGetValue(p.Id, out var j) ? j : "Scavenger";
                    if (p.Job == "Farmer" || p.Job == "Civilian") p.St = "base";   // they would be home working
                }
                return g.BaseBalance(b).Get(key, 0.0);
            }
            finally
            {
                for (int i = 0; i < cands.Count; i++) if (oldJ[i] != null) { cands[i].Job = oldJ[i]; cands[i].St = oldS[i]; }
                g.BaseBalance(b);   // leave the balance cache as the real jobs give it
            }
        }

        static bool Unfit(Game g, Person p) => g.JobBlock(p, "Scavenger") != "" || p.Hp < Game.MaxHp(p) * 0.4
            || (Game.ExpansionRules && (p.Fatigue >= 70.0 || Game.HasTrait(Visible(p), "coward")));

        static double Stock(Game g, string k) => g.C.Res.Get(k, 0.0);

        static int SafeRaidSize(Game g)
        {
            try { return g.RaidSize(); } catch (Exception) { return 3 + g.Day(); }
        }

        static int SafeRepairs(Game g, Base b)
        {
            try { return g.RepairQueue(b).Count(r => r.frac < 0.7); } catch (Exception) { return 0; }
        }

        /// <summary>The person as far as the player can see: the trait still hidden ("?") is left out of the choice.</summary>
        static Person Visible(Person p)
        {
            if (p.Traits == null || string.IsNullOrEmpty(p.TraitHidden)) return p;
            return new Person { Traits = p.Traits.Where(t => t != p.TraitHidden).ToList() };
        }

        static double T(Person v, string id) => Game.HasTrait(v, id) ? 1.0 : 0.0;

        /// <summary>How well a survivor suits a job. Keeping the current job is worth a little (less churn); ties go to
        /// the lower id (Take scans in id order and needs a strictly better score).</summary>
        public static double Score(Game g, Person p, string job)
        {
            double s = p.Job == job ? 2.0 : 0.0;
            if (p.Job == "Soldier" && job == "Cop" || p.Job == "Cop" && job == "Soldier") s += 1.5;
            double hp = p.Hp / Math.Max(1.0, Game.MaxHp(p));
            bool exp = Game.ExpansionRules;
            var v = exp ? Visible(p) : null;
            double fat = exp ? p.Fatigue : 0.0;
            switch (job)
            {
                case "Soldier":
                case "Cop":
                    s += Math.Min(3.0, p.Kills / 10.0) + 3.0 * (hp - 0.5);
                    if (exp)
                    {
                        s += 4.0 * T(v, "sharpshooter") + 3.0 * T(v, "hardened") + 2.0 * T(v, "night_owl") + 1.5 * T(v, "insomniac")
                           + 1.5 * T(v, "tough") + 1.0 * T(v, "leader") - 6.0 * T(v, "coward") - 2.0 * T(v, "glass_jaw");
                        s -= fat / 15.0;
                        if (g.PplNoWork(p)) s -= 4.0;
                    }
                    break;
                case "Scavenger":
                    s += 2.0 * (hp - 0.5);
                    if (exp) s += 4.0 * T(v, "scout") + 2.0 * T(v, "quiet") + 1.0 * T(v, "hoarder") + 1.0 * T(v, "tough")
                               - 3.0 * T(v, "loudmouth") - 4.0 * T(v, "coward") - 1.5 * T(v, "glass_jaw") - fat / 15.0;
                    break;
                case "Farmer":
                    if (exp) s += 5.0 * T(v, "green_thumb") + fat / 40.0;
                    break;
                case "Medic":
                    if (exp) s += 6.0 * T(v, "ex_medic");
                    break;
                case "Engineer":
                    if (exp) s += 5.0 * T(v, "tinkerer") + 3.0 * T(v, "mechanic");
                    break;
                case "Civilian":
                    s += 1.5 * (1.0 - hp);
                    if (exp) s += fat / 20.0;
                    break;
            }
            return s;
        }
    }
}
