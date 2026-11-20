// Rival colony leaders (EXPANDED rules only). Each faction leader is a person: a life before, someone they lost,
// values and fears, a temperament rolled from the run seed + faction id, emotions that move with what happens to
// their people (grief, dread, anger, pride, hope) and a memory of specific events with you and the other camps.
// Every game hour they look at their camp (food, building stock, walls, people, the dead nearby, who hurt them)
// and pick a goal with a utility score: hunker down, fortify, build, expand, scavenge, take people in, trade,
// ask for help, raid, fall back, or prepare for war. A held goal gets a stubbornness bonus so they don't flip
// every hour, and people are not perfect: fear can make them hide when it is not needed, grief can turn into
// revenge, grudges outlast the reason for them.
//
// Parity: nothing here runs unless Game.ExpansionRules is on. All randomness is a hash of (seed, faction id,
// game time, salt); the shared sim Rng is never drawn from here. Camp.Mind stays null in classic runs, and the
// save field is skipped when null, so classic saves and traces are unchanged.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DayZero.Core
{
    /// <summary>One thing a leader remembers, already written in their own words.</summary>
    public sealed class LeaderMemory
    {
        public double T;
        public string Who = "";    // "col:<index>" for a player colony, a camp id, or "" for the dead / the world
        public string Kind = "";   // gift, attacked, raided_us, we_raided, help, ignored, fell, ally, betrayed, built, lost...
        public string Text = "";
    }

    /// <summary>A faction leader's mind. Saved on the Camp (null = not met yet / classic rules).</summary>
    public sealed class LeaderMind
    {
        public int V = 1;
        // temperament, -1..1 (Stubborn and Temper 0..1)
        public double Bold, Greed, Trust, Build, Stubborn, Temper;
        // who they are
        public string Before = "", Lost = "", LostName = "", Values = "", Fear = "", Voice = "plain";
        // feelings, 0..1
        public double Grief, Dread, Anger, Pride, Hope = 0.4;
        public string AngryAt = "", ProudOf = "", ProudKind = "";   // ProudKind: beat (fought them off) or took (robbed them)
        // what they are doing
        public string Goal = "", Target = "", Plan = "", Project = "";
        public double GoalT, PlanT = -1000.0, Work, BuildWork, RaidT = -1000.0, AskT = -1000.0, AskUntil = -1.0, TradeT = -1000.0;
        public string AskRes = "";
        public int AskCol = -1, Walls;
        public List<string> Built = new List<string>();   // projects finished (see LbProject)
        // people
        public ODict<string, double> Op = new ODict<string, double>();     // opinion of other camps, -100..100
        public List<string> Allies = new List<string>();
        public List<double> Grudge = new List<double>(), Favour = new List<double>();   // per player colony
        public List<double> LastRels = new List<double>();
        public int LastPop;
        public double LastStr, Buried;
        public List<LeaderMemory> Mem = new List<LeaderMemory>();
    }

    public sealed partial class Game
    {
        // ---------------- who the leaders are ----------------
        sealed class Persona
        {
            public string Before, Lost, LostName, Values, Fear, Voice;
            public double Bold, Greed, Trust, Build;
        }

        static readonly Dictionary<string, Persona> PERSONAS = new Dictionary<string, Persona>
        {
            ["millbrook"] = new Persona
            {
                Before = "Ran the scale house at the grain co-op for thirty years and knew every farmer in the county by name.",
                Lost = "Her husband Walt went back for the neighbours' kids the first week and never came home.", LostName = "Walt",
                Values = "Nobody goes hungry. You share what you have.", Fear = "A winter with no seed left to plant.",
                Voice = "farmer", Bold = -0.4, Greed = -0.6, Trust = 0.4, Build = 0.7,
            },
            ["cedar_falls"] = new Persona
            {
                Before = "Shift supervisor at the hydro dam. Twenty years of keeping the power on for people who never thought about it.",
                Lost = "His daughter Kayla was on one of the evacuation buses that never reached the bridge.", LostName = "Kayla",
                Values = "Do the job in front of you. Keep the lights on.", Fear = "The turbines stopping and the dark coming in.",
                Voice = "engineer", Bold = -0.1, Greed = 0.2, Trust = 0.0, Build = 0.8,
            },
            ["fort_hale"] = new Persona
            {
                Before = "Guard staff sergeant on convoy duty. Got called up two days before it all went wrong.",
                Lost = "Her lieutenant and most of the company were overrun at the overpass checkpoint.", LostName = "Lieutenant Reyes",
                Values = "Chain of command. Nobody gets left behind.", Fear = "Losing discipline. A unit that breaks is a unit that dies.",
                Voice = "soldier", Bold = 0.4, Greed = 0.0, Trust = -0.2, Build = 0.3,
            },
            ["riverbend"] = new Persona
            {
                Before = "Long-haul trucker, with a few years inside for moving stolen goods.",
                Lost = "His brother Lenny got bitten on the docks. Duke was the one who had to end it.", LostName = "Lenny",
                Values = "Strength. His crew eats first.", Fear = "Looking weak. The weak get eaten.",
                Voice = "boss", Bold = 0.7, Greed = 0.7, Trust = -0.5, Build = -0.5,
            },
            ["pine_hollow"] = new Persona
            {
                Before = "Deacon at Pine Hollow church and owner of the hardware store on Main.",
                Lost = "His wife Grace and the whole youth group bus were lost on the highway out.", LostName = "Grace",
                Values = "The flock comes first. Keep the congregation pure.", Fear = "Strangers carrying the sickness inside the gate.",
                Voice = "preacher", Bold = 0.1, Greed = 0.0, Trust = -0.8, Build = 0.4,
            },
            ["scrapyard"] = new Persona
            {
                Before = "Ran a chop shop out of the salvage yard and rode with the Iron Saints.",
                Lost = "His old lady Dee and half the club went down at the summer rally.", LostName = "Dee",
                Values = "The crew is family. Nobody owns them.", Fear = "Being caged in. Walls that become a trap.",
                Voice = "biker", Bold = 0.8, Greed = 0.5, Trust = -0.3, Build = -0.7,
            },
            ["saint_anne"] = new Persona
            {
                Before = "Charge nurse in the emergency room at Saint Anne's General before she took her vows.",
                Lost = "The whole night shift on her ward turned in a single night. She was the only one who walked out.", LostName = "the night shift",
                Values = "Every life is worth saving.", Fear = "Having to choose who lives.",
                Voice = "nurse", Bold = -0.7, Greed = -0.8, Trust = 0.7, Build = 0.3,
            },
        };

        static readonly string[] GEN_BEFORE =
        {
            "Taught high school history and coached the wrestling team.", "Managed the night shift at a distribution warehouse.",
            "Was a paramedic for eleven years.", "Owned a small diner off the highway.", "Worked the line at the meat plant.",
            "Was a city bus driver who knew every street.", "Ran a hunting outfitter out of the back of a truck.",
        };
        static readonly string[] GEN_LOST = { "a brother", "their wife", "their husband", "their son", "their daughter", "their best friend", "both parents" };
        static readonly string[] GEN_VALUES = { "Keep your word.", "Family first.", "Nobody gets left behind.", "Only the strong make it.", "Trust is earned." };
        static readonly string[] GEN_FEAR = { "Being overrun at night.", "Running out of food.", "Being betrayed by someone they trusted.", "Losing another person." };

        // ---------------- hash rng (never the sim Rng) ----------------
        static ulong LbMix(ulong z)
        {
            z += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        static ulong LbStr(string s)
        {
            ulong h = 1469598103934665603UL;
            foreach (char ch in s ?? "") { h ^= ch; h *= 1099511628211UL; }
            return h;
        }

        /// <summary>0..1, fixed for (seed, camp, game hour, salt).</summary>
        double LbRoll(Camp c, int salt, double tq = -1.0)
        {
            long t = (long)Math.Floor((tq < 0.0 ? S.T : tq));
            ulong h = LbMix(LbMix((ulong)(uint)S.Seed ^ LbStr(c.Id)) ^ (ulong)t * 0x2545F4914F6CDD1DUL ^ (ulong)salt * 0x632BE59BD9B4E019UL);
            return (h >> 11) * (1.0 / 9007199254740992.0);
        }

        double LbJit(Camp c, int salt, double amp) => (LbMix((ulong)(uint)S.Seed * 31UL ^ LbStr(c.Id) ^ (ulong)salt * 977UL) >> 11) * (1.0 / 9007199254740992.0) * 2.0 * amp - amp;

        // ---------------- public read side (UI) ----------------
        /// <summary>The leader's mind, or null with classic rules / before the first hour.</summary>
        public LeaderMind LeaderOf(Camp c) => ExpansionRules && c != null ? c.Mind : null;

        public string LeaderTraitsText(Camp c)
        {
            var m = LeaderOf(c);
            if (m == null) return "";
            var t = new List<string>();
            t.Add(m.Bold > 0.3 ? "aggressive" : m.Bold < -0.3 ? "cautious" : "steady");
            t.Add(m.Greed > 0.3 ? "greedy" : m.Greed < -0.3 ? "generous" : "fair");
            t.Add(m.Trust > 0.3 ? "loyal" : m.Trust < -0.3 ? "paranoid" : "guarded");
            t.Add(m.Build > 0.3 ? "a builder" : m.Build < -0.3 ? "a raider at heart" : "practical");
            if (m.Stubborn > 0.6) t.Add("stubborn");
            if (m.Temper > 0.6) t.Add("holds grudges");
            return string.Join(", ", t);
        }

        public string LeaderPlanText(Camp c) => LeaderOf(c)?.Plan ?? "";

        public string LeaderBackstory(Camp c)
        {
            var m = LeaderOf(c);
            if (m == null) return "";
            return string.Format("{0} {1}", m.Before, m.Lost).Trim();
        }

        public string LeaderValuesText(Camp c)
        {
            var m = LeaderOf(c);
            if (m == null) return "";
            return string.Format("Believes: {0} Fears: {1}", m.Values, m.Fear);
        }

        /// <summary>How the leader feels right now, in plain words.</summary>
        public string LeaderFeelingText(Camp c)
        {
            var m = LeaderOf(c);
            if (m == null) return "";
            double mx = Math.Max(Math.Max(m.Grief, m.Dread), Math.Max(m.Anger, m.Pride));
            if (mx < 0.25) return m.Hope > 0.55 ? "Hopeful" : m.Hope < 0.25 ? "Worn down" : "Calm";
            if (mx == m.Grief) return m.Anger > 0.4 ? "Grieving and angry" : "Grieving";
            if (mx == m.Dread) return mx > 0.65 ? "Close to panic" : "Scared";
            if (mx == m.Anger) return "Angry";
            return "Proud";
        }

        /// <summary>Attitude toward a player colony: a short label plus the memory behind it (if any).</summary>
        public string LeaderMoodToYou(Camp c, int ci = -1)
        {
            var m = LeaderOf(c);
            if (m == null) return "";
            if (ci < 0) ci = Math.Max(0, ViewCol);
            double rel = CampRel(c, ci);
            double g = ci < m.Grudge.Count ? m.Grudge[ci] : 0.0, f = ci < m.Favour.Count ? m.Favour[ci] : 0.0;
            string lab;
            if (c.Att == "Raiders" && rel < 25.0) lab = g > 25.0 ? "Wants revenge on you" : c.Str > 40.0 ? "Sees you as easy pickings" : "Sizing you up";
            else if (rel >= 60.0) lab = "Trusts you like family";
            else if (rel >= 25.0) lab = f > 20.0 ? "Grateful to you" : "Friendly";
            else if (rel > 5.0) lab = "Warming to you";
            else if (rel > -5.0) lab = g > 15.0 ? "Hasn't forgotten" : "Wary of you";
            else if (rel > -25.0) lab = "Distrusts you";
            else if (rel > -60.0) lab = "Holds a grudge";
            else lab = "Wants you dead";
            var mem = m.Mem.LastOrDefault(x => x.Who == "col:" + ci);
            return mem != null ? string.Format("{0}. \"{1}\"", lab, mem.Text) : lab;
        }

        /// <summary>Newest first, in the leader's words.</summary>
        public List<string> LeaderMemories(Camp c, int n = 3)
        {
            var m = LeaderOf(c);
            var o = new List<string>();
            if (m == null) return o;
            for (int i = m.Mem.Count - 1; i >= 0 && o.Count < n; i--) o.Add(m.Mem[i].Text);
            return o;
        }

        // ---------------- creation ----------------
        LeaderMind LeaderNew(Camp c)
        {
            var m = new LeaderMind();
            PERSONAS.TryGetValue(c.Id ?? "", out var p);
            if (p == null)
            {
                int k = (int)(LbMix(LbStr(c.Id) ^ (ulong)(uint)S.Seed) % 997UL);
                string lost = GEN_LOST[k % GEN_LOST.Length];
                p = new Persona
                {
                    Before = GEN_BEFORE[k % GEN_BEFORE.Length], Lost = string.Format("Lost {0} in the first weeks.", lost), LostName = lost.Replace("their ", "").Replace("a ", "their "),
                    Values = GEN_VALUES[(k / 7) % GEN_VALUES.Length], Fear = GEN_FEAR[(k / 5) % GEN_FEAR.Length], Voice = c.Att == "Raiders" ? "boss" : "plain",
                    Bold = c.Att == "Raiders" ? 0.6 : 0.0, Greed = c.Att == "Raiders" ? 0.5 : 0.0, Trust = c.Att == "Hostile" ? -0.5 : 0.0, Build = c.Att == "Raiders" ? -0.4 : 0.2,
                };
            }
            m.Before = p.Before; m.Lost = p.Lost; m.LostName = p.LostName; m.Values = p.Values; m.Fear = p.Fear; m.Voice = p.Voice;
            // the same person in every run, a little different each time (seed + faction id)
            m.Bold = V.Clamp(p.Bold + LbJit(c, 1, 0.25), -1.0, 1.0);
            m.Greed = V.Clamp(p.Greed + LbJit(c, 2, 0.25), -1.0, 1.0);
            m.Trust = V.Clamp(p.Trust + LbJit(c, 3, 0.25), -1.0, 1.0);
            m.Build = V.Clamp(p.Build + LbJit(c, 4, 0.25), -1.0, 1.0);
            m.Stubborn = V.Clamp(0.5 + LbJit(c, 5, 0.45), 0.0, 1.0);
            m.Temper = V.Clamp(0.5 + (m.Bold * 0.3) + LbJit(c, 6, 0.4), 0.0, 1.0);
            m.Grief = 0.25;   // everyone lost someone
            foreach (var o in S.Camps)
                if (o != c) m.Op[o.Id] = V.Clamp(CampRelationFromData(c.Id, o.Id) + (o.Att == "Raiders" ? -20.0 : 0.0) + m.Trust * 10.0, -100.0, 100.0);
            m.LastPop = c.Pop; m.LastStr = c.Str;
            for (int ci = 0; ci < S.Cols.Count; ci++) { m.Grudge.Add(0.0); m.Favour.Add(0.0); m.LastRels.Add(CampRel(c, ci)); }
            m.GoalT = S.T;
            return m;
        }

        // ---------------- the hourly think ----------------
        struct LeaderCtx
        {
            public double FoodDays, Threat, Mats, Meds, Ammo, Defense, Inf;
            public int Horde, ClaimWant;
            public string Dir;
            public bool Night;
        }

        static readonly string[] LB_GOALS = { "hunker", "fortify", "build", "expand", "scavenge", "recruit", "trade", "ask_help", "raid", "retreat" };

        void StepLeaders()
        {
            if (S.Camps.Count == 0 || S.Phase == "pick") return;
            foreach (var c in S.Camps)
            {
                if (!c.Alive || !c.Fi.HasValue) continue;
                var m = c.Mind ??= LeaderNew(c);
                while (m.Grudge.Count < S.Cols.Count) { m.Grudge.Add(0.0); m.Favour.Add(0.0); m.LastRels.Add(CampRel(c, m.LastRels.Count)); }
                var x = LeaderSense(c, m);
                LeaderNotice(c, m, x);
                LeaderFeel(c, m, x);
                LeaderDecide(c, m, x);
                LeaderAct(c, m, x);
                m.LastPop = c.Pop; m.LastStr = c.Str;
                for (int ci = 0; ci < S.Cols.Count; ci++) m.LastRels[ci] = CampRel(c, ci);
            }
            if (Hour() == 9) LeaderDiplomacy();
            _factionLotsDirty |= _lbClaimsDirty; _lbClaimsDirty = false;
        }

        bool _lbClaimsDirty;

        LeaderCtx LeaderSense(Camp c, LeaderMind m)
        {
            var x = new LeaderCtx();
            var st = c.Stock;
            x.FoodDays = st.Get("food", 0.0) / Math.Max(0.1, c.Pop * 0.012 * 24.0);
            x.Mats = st.Get("materials", 0.0); x.Meds = st.Get("meds", 0.0); x.Ammo = st.Get("ammo", 0.0);
            x.Inf = InfAt(c.Pos);
            var cp = V.F(c.Pos).Add(V.HALF);
            var near = Zc.Count > 0 ? ZombiesNear(cp, FactionRadius(c), 80) : new List<int>();
            x.Horde = near.Count;
            if (near.Count > 0)
            {
                Vector2 sum = Vector2.zero;
                foreach (int i in near) sum += ZPos(i) - cp;
                x.Dir = sum.sqrMagnitude > 0.01f ? DirName(Math.Atan2(sum.y, sum.x)) : "";
            }
            if (string.IsNullOrEmpty(x.Dir)) x.Dir = DIR_NAMES[(int)(LbRoll(c, 11, Math.Floor(S.T / 72.0)) * 8) % 8];
            x.Threat = V.Clamp(Math.Max((x.Inf - 0.3) / 0.55, x.Horde / 35.0), 0.0, 1.0);
            if (S.Phase == "fall") x.Threat = Math.Max(x.Threat, 0.6);
            x.Defense = c.Str / Math.Max(1.0, c.Pop * 2.0);
            x.ClaimWant = Math.Min(7, 2 + c.Pop / 8);
            x.Night = IsNight();
            return x;
        }

        void LeaderRemember(LeaderMind m, string who, string kind, string text)
        {
            m.Mem.Add(new LeaderMemory { T = S.T, Who = who ?? "", Kind = kind, Text = text });
            if (m.Mem.Count > 14) m.Mem.RemoveAt(0);
        }

        static string LbCount(int n) => n <= 1 ? "one of ours" : n == 2 ? "two of ours" : n == 3 ? "three of ours" : n <= 5 ? "a handful of ours" : "too many of ours";

        static string LbRes(string r)
        {
            switch (r)
            {
                case "food": return "food";
                case "water": return "clean water";
                case "meds": return "medicine";
                case "ammo": return "ammunition";
                case "materials": return "building supplies";
                case "fuel": return "fuel";
                default: return r ?? "supplies";
            }
        }

        string LbColName(int ci) => ColonyAt(ci)?.Name is string n && n != "" ? n : "the newcomers";
        string LbWhoName(string who)
        {
            if (string.IsNullOrEmpty(who)) return "the dead";
            if (who.StartsWith("col:") && int.TryParse(who.Substring(4), out int ci)) return LbColName(ci);
            return CampById(who)?.Name ?? "them";
        }

        /// <summary>Changes since last hour that nobody told the brain about: deaths, squad attacks, new people.</summary>
        void LeaderNotice(Camp c, LeaderMind m, LeaderCtx x)
        {
            int attacker = -1;
            for (int ci = 0; ci < S.Cols.Count && ci < m.LastRels.Count; ci++)
                if (m.LastRels[ci] - CampRel(c, ci) >= 25.0) attacker = ci;
            int dead = m.LastPop - c.Pop;
            if (attacker >= 0)
            {
                m.Grudge[attacker] += 45.0;
                m.Anger = Math.Min(1.0, m.Anger + 0.45 * (0.5 + m.Temper));
                m.Dread = Math.Min(1.0, m.Dread + 0.15 * (1.0 - m.Temper));
                m.AngryAt = "col:" + attacker;
                string you = LbColName(attacker);
                LeaderRemember(m, "col:" + attacker, "attacked", dead > 0
                    ? string.Format("{0} came at us with guns. We buried {1} because of them.", you, LbCount(dead))
                    : string.Format("{0} came at us with guns. We drove them off, but I won't forget it.", you));
            }
            if (dead > 0)
            {
                m.Buried += dead;
                m.Grief = Math.Min(1.0, m.Grief + 0.14 * dead);
                if (attacker < 0)
                {
                    string why = x.Threat > 0.35 ? "The dead got {0} out past the fence." : "We lost {0}. A supply run that didn't come back.";
                    if (dead >= 1 && (m.Mem.Count == 0 || S.T - m.Mem[m.Mem.Count - 1].T > 6.0 || m.Mem[m.Mem.Count - 1].Kind != "lost"))
                        LeaderRemember(m, "", "lost", string.Format(why, LbCount(dead)));
                    // grief turns one of two ways
                    if (m.Temper > 0.55) m.Anger = Math.Min(1.0, m.Anger + 0.08 * dead);
                    else m.Dread = Math.Min(1.0, m.Dread + 0.1 * dead);
                }
            }
            else if (dead < 0) m.Hope = Math.Min(1.0, m.Hope + 0.05);
            if (c.Str - m.LastStr <= -3.0 && attacker < 0 && dead <= 0) m.Dread = Math.Min(1.0, m.Dread + 0.05);
        }

        void LeaderFeel(Camp c, LeaderMind m, LeaderCtx x)
        {
            double slow = 0.3 + 0.7 * m.Stubborn;   // stubborn people hang on to things
            m.Grief = Math.Max(0.0, m.Grief - 0.0025 * (1.2 - slow * 0.5));
            m.Anger = Math.Max(0.0, m.Anger - 0.003 * (1.3 - slow));
            m.Pride = Math.Max(0.0, m.Pride - 0.004);
            m.Buried *= 0.994;   // "this week"
            double target = x.Threat * (0.8 + 0.4 * (1.0 - (m.Bold + 1.0) * 0.5)) + m.Grief * 0.2;
            m.Dread += (V.Clamp(target, 0.0, 1.0) - m.Dread) * (target > m.Dread ? 0.25 : 0.05);
            double hopeT = V.Clamp(0.25 + x.FoodDays * 0.05 + m.Pride * 0.2 - m.Grief * 0.3 - m.Dread * 0.2 + m.Allies.Count * 0.08, 0.0, 1.0);
            m.Hope += (hopeT - m.Hope) * 0.03;
            if (m.Anger < 0.05) m.AngryAt = "";
            if (m.Pride < 0.05) m.ProudOf = "";
            for (int ci = 0; ci < m.Grudge.Count; ci++)
            {
                m.Grudge[ci] = Math.Max(0.0, m.Grudge[ci] - 0.06 * (1.3 - slow));
                m.Favour[ci] = Math.Max(0.0, m.Favour[ci] - 0.05);
            }
        }

        bool LbHostileTo(Camp c, int ci)
        {
            if (!ColAlive(ColonyAt(ci))) return false;
            if (c.Lord == ci) return false;
            bool h = c.Att == "Raiders" || CampRel(c, ci) <= (double)D.Camps["thresholds"]["hostile"];
            return h && S.T >= c.Truce.Get(ci.ToString(), -1.0);
        }

        int LbWarWith(Camp c)
        {
            for (int ci = 0; ci < S.Cols.Count; ci++)
            {
                var w = S.Cols[ci].War;
                if (ColAlive(S.Cols[ci]) && w != null && w.St == "prep" && w.Foe == c.Id) return ci;
            }
            return -1;
        }

        string LbWorstNeed(Camp c)
        {
            if (c.Stock.Get("food", 0.0) < c.Pop * 0.012 * 24.0 * 4.0) return "food";   // hunger first
            string best = ""; double bv = 1.0;
            foreach (var r in new[] { "food", "meds", "materials", "ammo" })
            {
                double want = r == "food" ? c.Pop * 0.3 * 6.0 : r == "meds" ? 8.0 : r == "materials" ? 40.0 : 20.0;
                double v = c.Stock.Get(r, 0.0) / want;
                if (v < bv) { bv = v; best = r; }
            }
            return best;
        }

        /// <summary>Score every option and keep the current goal unless something clearly beats it.</summary>
        void LeaderDecide(Camp c, LeaderMind m, LeaderCtx x)
        {
            int war = LbWarWith(c);
            if (war >= 0)
            {
                if (m.Goal != "war") { m.Goal = "war"; m.GoalT = S.T; m.Target = "col:" + war; m.Plan = ""; }
                return;
            }
            if (m.Goal == "war") m.Goal = "";
            double calm = 1.0 - x.Threat;
            double foodShort = V.Clamp(1.0 - x.FoodDays / 5.0, 0.0, 1.0);
            double cautious = (1.0 - m.Bold) * 0.5, bold = (1.0 + m.Bold) * 0.5, builder = (1.0 + m.Build) * 0.5;
            bool hasAlly = m.Allies.Any(a => CampById(a)?.Alive == true);
            bool friendYou = false;
            for (int ci = 0; ci < S.Cols.Count; ci++) if (ColAlive(S.Cols[ci]) && CampRel(c, ci) > -5.0 && c.Att != "Raiders") friendYou = true;
            var sc = new Dictionary<string, double>();
            sc["hunker"] = x.Threat * x.Threat * 1.6 + m.Dread * 0.5 * cautious + (x.Night ? 0.1 : 0.0) + m.Grief * 0.15 * cautious;
            sc["fortify"] = (x.Threat * 0.7 + V.Clamp(1.0 - x.Defense, 0.0, 1.0) * 0.4 + m.Grief * 0.25 + m.Dread * 0.2) * (x.Mats >= 10.0 ? 1.0 : 0.15);
            bool canBuild = m.Built.Count < LB_PROJECTS.Length;
            sc["build"] = canBuild && x.Mats >= 30.0 ? calm * 0.3 + builder * 0.35 + V.Clamp((x.Mats - 30.0) / 150.0, 0.0, 0.35) : 0.0;
            int claims = c.Claim?.Count ?? 1;
            sc["expand"] = claims < x.ClaimWant && c.Pop >= claims * 4 ? calm * 0.4 + m.Pride * 0.2 + bold * 0.1 - m.Grief * 0.2 : 0.0;
            sc["scavenge"] = (foodShort * 0.9 + calm * 0.25 + (x.Meds < 4.0 ? 0.2 : 0.0) + (x.Mats < 20.0 ? 0.15 : 0.0)) * (x.Night ? 0.4 : 1.0) * (1.0 - x.Threat * 0.6);
            sc["recruit"] = c.Pop < 32 ? calm * 0.3 * (x.FoodDays > 4.0 ? 1.0 : 0.2) * (c.Pop < 24 ? 1.0 : 0.4) + m.Hope * 0.12 : 0.0;
            sc["trade"] = (hasAlly || m.Op.Values.Any(v => v > 20.0)) && c.Surplus.Count > 0 ? 0.2 + (1.0 - m.Greed) * 0.1 + foodShort * 0.15 : 0.0;
            sc["ask_help"] = (hasAlly || friendYou) && S.T - m.AskT > 48.0 ? (foodShort > 0.7 ? 0.6 : 0.0) + (x.Threat > 0.6 && x.Defense < 0.5 ? 0.4 : 0.0) + (m.Trust + 1.0) * 0.1 - m.Greed * 0.05 - (c.Att == "Raiders" ? 0.4 : 0.0) : 0.0;
            double desperate = foodShort > 0.75 ? 0.45 : 0.0;
            double raider = Math.Max(0.0, -m.Build) * 0.35 + Math.Max(0.0, m.Greed) * 0.15;   // some people just take
            double raidCd = desperate > 0.0 || m.Anger > 0.5 ? 36.0 : 72.0;
            sc["raid"] = S.T - m.RaidT > raidCd && !x.Night ? (desperate + raider + m.Anger * 0.55 * m.Temper * 1.6 + (m.Greed + 1.0) * 0.05) * bold * (1.0 - x.Threat * 0.8) * (c.Pop >= 8 ? 1.0 : 0.2) : 0.0;
            sc["retreat"] = x.Threat > 0.75 && x.Defense < 0.45 && claims > 1 ? 0.6 + cautious * 0.4 : 0.0;
            // people aren't perfect: a little noise, more for the impulsive
            foreach (var k in LB_GOALS) sc[k] = sc[k] + (LbRoll(c, 20 + Array.IndexOf(LB_GOALS, k)) - 0.5) * 0.12 * (1.2 - m.Stubborn);
            // panic: a frightened, cautious leader hides even when it's not needed
            if (m.Dread > 0.6 && m.Bold < 0.0) sc["hunker"] += 0.3;
            // grief can harden into revenge
            if (m.Grief > 0.5 && m.Temper > 0.6 && m.AngryAt != "") sc["raid"] += 0.25;
            // protect the people first
            if (x.Threat > 0.6) { sc["scavenge"] *= 0.3; sc["expand"] *= 0.2; sc["recruit"] *= 0.3; sc["raid"] *= 0.4; }
            string cur = m.Goal;
            if (cur != "" && sc.ContainsKey(cur)) sc[cur] += 0.15 + 0.25 * m.Stubborn;   // hysteresis
            string best = cur; double bv = cur != "" && sc.ContainsKey(cur) ? sc[cur] : -1.0;
            foreach (var k in LB_GOALS) if (sc[k] > bv + 1e-9) { bv = sc[k]; best = k; }
            bool held = S.T - m.GoalT < 6.0;
            bool emergency = (best == "hunker" || best == "retreat" || best == "fortify") && x.Threat > 0.6;
            if (best != cur && (cur == "" || !held || emergency))
            {
                m.Goal = best; m.GoalT = S.T; m.Work = 0.0; m.Plan = "";
                m.Target = best == "raid" ? LbPickRaidTarget(c, m) : best == "trade" || best == "ask_help" ? LbPickFriend(c, m) : "";
                if (best == "raid" && m.Target == "") { m.Goal = "scavenge"; }
            }
        }

        string LbPickRaidTarget(Camp c, LeaderMind m)
        {
            // a grudge against a player colony comes first, if they are at odds
            if (m.AngryAt.StartsWith("col:") && int.TryParse(m.AngryAt.Substring(4), out int aci) && LbHostileTo(c, aci)) return m.AngryAt;
            string best = ""; double bv = double.MaxValue;
            foreach (var o in S.Camps)
            {
                if (o == c || !o.Alive || !o.Fi.HasValue || m.Allies.Contains(o.Id)) continue;
                double op = m.Op.Get(o.Id, 0.0);
                if (op > 10.0 && m.AngryAt != o.Id) continue;
                double d = V.F(o.Pos).DistTo(V.F(c.Pos));
                double v = op + o.Str * 0.6 + d * 0.15 - (m.AngryAt == o.Id ? 60.0 : 0.0) - o.Stock.Get("food", 0.0) * 0.05;
                if (o.Str > c.Str * 1.3 && m.AngryAt != o.Id) continue;   // not suicidal
                if (v < bv) { bv = v; best = o.Id; }
            }
            if (best == "")
                for (int ci = 0; ci < S.Cols.Count; ci++) if (LbHostileTo(c, ci)) return "col:" + ci;
            return best;
        }

        string LbPickFriend(Camp c, LeaderMind m)
        {
            string best = ""; double bv = 15.0;
            foreach (var o in S.Camps)
            {
                if (o == c || !o.Alive || !o.Fi.HasValue) continue;
                double v = m.Op.Get(o.Id, 0.0) + (m.Allies.Contains(o.Id) ? 30.0 : 0.0);
                if (v > bv) { bv = v; best = o.Id; }
            }
            return best;
        }

        // ---------------- doing it ----------------
        void LeaderAct(Camp c, LeaderMind m, LeaderCtx x)
        {
            var st = c.Stock;
            double strCap = 20.0 + c.Pop * 2.6;
            // what they built keeps paying: gardens and rain barrels, an infirmary shelf
            foreach (var b in m.Built)
                switch (b)
                {
                    case "garden": st["food"] = st.Get("food", 0.0) + 0.08; break;
                    case "barrels": st["water"] = st.Get("water", 0.0) + 0.1; st["food"] = st.Get("food", 0.0) + 0.02; break;
                    case "smokehouse": st["food"] = st.Get("food", 0.0) + 0.05; break;
                    case "sickroom": st["meds"] = st.Get("meds", 0.0) + 0.02; break;
                    case "workshop": st["materials"] = st.Get("materials", 0.0) + 0.08; break;
                }
            switch (m.Goal)
            {
                case "hunker":
                    c.NextScav = Math.Max(c.NextScav, S.T + 2.0);       // everyone home
                    c.NextAmbush = Math.Max(c.NextAmbush, S.T + 2.0);   // nobody out on the roads
                    if (c.Str < strCap) c.Str += 0.05;
                    break;
                case "fortify":
                    if (st.Get("materials", 0.0) >= 8.0 && c.Str < strCap)
                    {
                        st["materials"] -= 1.5; c.Str += 0.25; m.Work += 1.0;
                        if (m.Work >= 18.0)
                        {
                            m.Work = 0.0; m.Walls++;
                            LeaderRemember(m, "", "built", string.Format("We finished the {0} wall. People slept that night.", x.Dir));
                            m.Pride = Math.Min(1.0, m.Pride + 0.1);
                        }
                    }
                    break;
                case "build":
                    if (st.Get("materials", 0.0) >= 20.0)
                    {
                        if (m.Project == "") m.Project = LbProject(c, m, x);
                        if (m.Project == "") break;
                        st["materials"] -= 2.0; m.BuildWork += 1.0;
                        if (m.BuildWork >= 14.0)
                        {
                            m.BuildWork = 0.0; m.Built.Add(m.Project);
                            LeaderRemember(m, "", "built", string.Format(LbProjectDone(m.Project), x.Dir));
                            m.Project = ""; m.Pride = Math.Min(1.0, m.Pride + 0.12); m.Hope = Math.Min(1.0, m.Hope + 0.1);
                            c.Str += m.Built[m.Built.Count - 1] == "tower" ? 4.0 : 1.0;
                        }
                    }
                    break;
                case "expand":
                    m.Work += 1.0;
                    if (m.Work >= 12.0)
                    {
                        m.Work = 0.0;
                        int before = c.Claim?.Count ?? 1;
                        FactionGrowClaim(c, Math.Max(0, x.ClaimWant - Math.Min(2 + (int)(c.Pop / 9.0), 6)));
                        if ((c.Claim?.Count ?? 1) > before)
                        {
                            LeaderRemember(m, "", "expanded", "We cleared the building next door and made it ours.");
                            m.Pride = Math.Min(1.0, m.Pride + 0.08);
                        }
                    }
                    break;
                case "scavenge":
                    if (!x.Night) c.NextScav = Math.Min(c.NextScav, S.T + 2.0);
                    break;
                case "recruit":
                    m.Work += 1.0;
                    if (m.Work >= 16.0)
                    {
                        m.Work = 0.0;
                        if (LbRoll(c, 31) < 0.35 && st.Get("food", 0.0) > 10.0)
                        {
                            int n = LbRoll(c, 32) < 0.3 ? 2 : 1;
                            c.Pop += n; c.Str += 0.5 * n; st["food"] -= 4.0;
                            string[] who = { "a mother and her boy hiding in a laundromat", "an old mechanic who still had his tools", "two teenagers living on a rooftop", "a nurse who walked in out of the rain", "a man who had been alone since spring" };
                            LeaderRemember(m, "", "recruit", string.Format("We took in {0}.", who[(int)(LbRoll(c, 33) * who.Length) % who.Length]));
                            m.Hope = Math.Min(1.0, m.Hope + 0.08);
                        }
                    }
                    break;
                case "trade":
                    if (S.T - m.TradeT >= 24.0) LbTrade(c, m);
                    break;
                case "ask_help":
                    if (S.T - m.AskT > 48.0) LbAskHelp(c, m);
                    break;
                case "raid":
                    if (S.T - m.RaidT > 36.0 && !x.Night) LbRaid(c, m);
                    break;
                case "retreat":
                    c.NextScav = Math.Max(c.NextScav, S.T + 3.0);
                    c.NextAmbush = Math.Max(c.NextAmbush, S.T + 3.0);
                    if ((c.Claim?.Count ?? 1) > 1 && m.Work <= 0.0)
                    {
                        int far = -1; double fd = -1.0; var c0 = D.LotCenter(c.Lot);
                        foreach (int l in c.Claim) if (l != c.Lot && D.LotCenter(l).DistTo(c0) > fd) { fd = D.LotCenter(l).DistTo(c0); far = l; }
                        if (far >= 0)
                        {
                            c.Claim.Remove(far); _lbClaimsDirty = true;
                            LeaderRemember(m, "", "retreat", string.Format("We gave up the {0} building. It hurt, but buildings can be retaken.", x.Dir));
                        }
                        m.Work = 1.0;
                    }
                    if (c.Str < strCap) c.Str += 0.08;
                    break;
                case "war":
                    if (c.Str < strCap * 1.2) c.Str += 0.15;
                    c.NextScav = Math.Max(c.NextScav, S.T + 1.0);
                    break;
            }
            // feelings reach the roads: grief keeps a cautious camp's guns home, anger sends a bold one out early
            for (int ci = 0; ci < S.Cols.Count; ci++)
            {
                if (!LbHostileTo(c, ci)) continue;
                if (m.Grief > 0.55 && m.Bold < 0.2) c.NextRaid = Math.Max(c.NextRaid, S.T + 12.0);
                else if (m.AngryAt == "col:" + ci && m.Anger > 0.5 && m.Bold > 0.0 && m.Goal != "hunker") c.NextRaid = Math.Min(c.NextRaid, S.T + 6.0);
            }
            // a request that nobody answered
            if (m.AskUntil > 0.0 && S.T > m.AskUntil)
            {
                m.AskUntil = -1.0;
                if (m.AskCol >= 0 && m.AskCol < m.Grudge.Count)
                {
                    m.Grudge[m.AskCol] += m.Trust < 0.0 ? 10.0 : 4.0;
                    LeaderRemember(m, "col:" + m.AskCol, "ignored", string.Format("We asked {0} for help and heard nothing back.", LbColName(m.AskCol)));
                }
                m.AskCol = -1;
            }
            if (m.Plan == "" || S.T - m.PlanT >= 12.0) { m.Plan = LeaderVoice(c, m, x); m.PlanT = S.T; }
        }

        static readonly string[] LB_PROJECTS = { "garden", "sickroom", "workshop", "tower", "barrels", "smokehouse" };

        /// <summary>Next thing worth building, by what the camp is short of; "" when everything is built.</summary>
        string LbProject(Camp c, LeaderMind m, LeaderCtx x)
        {
            var order = new List<string>();
            string need = LbWorstNeed(c);
            if (need == "meds") order.Add("sickroom");
            if (need == "food") { order.Add("garden"); order.Add("smokehouse"); }
            if (need == "materials") order.Add("workshop");
            if (m.Dread > 0.3 || m.Walls == 0 && m.Bold < 0.0) order.Add("tower");
            int k = (int)(LbRoll(c, 61, Math.Floor(S.T / 48.0)) * LB_PROJECTS.Length);
            for (int i = 0; i < LB_PROJECTS.Length; i++) order.Add(LB_PROJECTS[(k + i) % LB_PROJECTS.Length]);
            return order.FirstOrDefault(p => !m.Built.Contains(p)) ?? "";
        }

        static string LbProjectName(string p)
        {
            switch (p)
            {
                case "garden": return "a rooftop garden";
                case "sickroom": return "a proper sick room";
                case "workshop": return "a workshop out of the old garage";
                case "tower": return "a lookout tower";
                case "barrels": return "rain barrels on every roof";
                case "smokehouse": return "a smokehouse for the winter";
                default: return "something that lasts";
            }
        }

        static string LbProjectDone(string p)
        {
            switch (p)
            {
                case "garden": return "The rooftop garden came up green. First fresh food in weeks.";
                case "sickroom": return "We finished the sick room. Nobody has to bleed on the floor anymore.";
                case "workshop": return "The workshop is running. We can fix what breaks now.";
                case "tower": return "The lookout tower is up. We see them coming from the {0} now.";
                case "barrels": return "Rain barrels on every roof. Nobody drinks from the ditch anymore.";
                case "smokehouse": return "The smokehouse is done. Meat that keeps through the winter.";
                default: return "We built something that will outlast this winter.";
            }
        }

        void LbTrade(Camp c, LeaderMind m)
        {
            m.TradeT = S.T;
            var o = CampById(m.Target);
            if (o == null || !o.Alive) { m.Target = LbPickFriend(c, m); m.Plan = ""; o = CampById(m.Target); if (o == null) return; }
            string give = c.Surplus.FirstOrDefault(r => c.Stock.Get(r, 0.0) > 30.0);
            string get = c.Needs.FirstOrDefault(r => o.Stock.Get(r, 0.0) > 25.0);
            if (give == null || get == null) return;
            double amt = 12.0 * (m.Greed > 0.3 ? 0.8 : 1.0);
            c.Stock[give] -= 12.0; o.Stock[give] = o.Stock.Get(give, 0.0) + 12.0;
            o.Stock[get] -= amt; c.Stock[get] = c.Stock.Get(get, 0.0) + amt;
            m.Op[o.Id] = V.Clamp(m.Op.Get(o.Id, 0.0) + 3.0, -100.0, 100.0);
            if (o.Mind != null)
            {
                o.Mind.Op[c.Id] = V.Clamp(o.Mind.Op.Get(c.Id, 0.0) + 3.0, -100.0, 100.0);
                if (!o.Mind.Mem.Any(e => e.Who == c.Id && e.Kind == "trade")) LeaderRemember(o.Mind, c.Id, "trade", string.Format("{0} trades fair. Their {1} for our {2}.", c.Name, LbRes(give), LbRes(get)));
            }
            if (!m.Mem.Any(e => e.Who == o.Id && e.Kind == "trade")) LeaderRemember(m, o.Id, "trade", string.Format("We swap our {0} with {1} for {2}. It works.", LbRes(give), o.Name, LbRes(get)));
        }

        void LbAskHelp(Camp c, LeaderMind m)
        {
            m.AskT = S.T;
            string need = LbWorstNeed(c);
            if (need == "") need = "food";
            m.AskRes = need;
            // allies first
            var ally = m.Allies.Select(CampById).FirstOrDefault(a => a != null && a.Alive && a.Stock.Get(need, 0.0) > 30.0);
            if (ally != null)
            {
                double amt = ally.Mind != null && ally.Mind.Greed > 0.4 ? 8.0 : 15.0;
                ally.Stock[need] -= amt; c.Stock[need] = c.Stock.Get(need, 0.0) + amt;
                m.Op[ally.Id] = V.Clamp(m.Op.Get(ally.Id, 0.0) + 10.0, -100.0, 100.0);
                LeaderRemember(m, ally.Id, "help", string.Format("When we ran short of {0}, {1} sent a wagon. That is what friends do.", LbRes(need), ally.Name));
                if (ally.Mind != null) LeaderRemember(ally.Mind, c.Id, "helped", string.Format("We sent {0} to {1} when they were in trouble.", LbRes(need), c.Name));
                m.Hope = Math.Min(1.0, m.Hope + 0.1);
                return;
            }
            // then the player colony that likes them best
            int bc = -1; double br = -6.0;
            for (int ci = 0; ci < S.Cols.Count; ci++)
                if (ColAlive(S.Cols[ci]) && CampRel(c, ci) > br) { br = CampRel(c, ci); bc = ci; }
            if (bc < 0 || c.Att == "Raiders") return;
            m.AskCol = bc; m.AskUntil = S.T + 36.0;
            int lc = LogCol; LogCol = bc;
            AddLog(string.Format("A runner from {0}: {1} says they are running out of {2}. A gift now would not be forgotten.", c.Name, c.Leader, LbRes(need)), "info");
            LogCol = lc;
        }

        void LbRaid(Camp c, LeaderMind m)
        {
            m.RaidT = S.T;
            string tgt = m.Target != "" ? m.Target : LbPickRaidTarget(c, m);
            if (tgt.StartsWith("col:"))
            {
                // their own raid on your camp (Game.Factions FactionRaid) does the rest, if they're still at odds
                if (int.TryParse(tgt.Substring(4), out int ci) && LbHostileTo(c, ci)) c.NextRaid = Math.Min(c.NextRaid, S.T);
                return;
            }
            var o = CampById(tgt);
            if (o == null || !o.Alive || o == c) return;
            var om = o.Mind;
            double atk = c.Str * (0.75 + 0.5 * LbRoll(c, 41)) * (1.0 + Math.Max(0.0, m.Bold) * 0.2);
            double dfn = o.Str * (0.85 + 0.4 * LbRoll(c, 42)) * (om != null && (om.Goal == "hunker" || om.Goal == "fortify" || om.Goal == "war") ? 1.25 : 1.0) + (om?.Walls ?? 0) * 2.0;
            bool win = atk > dfn;
            int lc = LogCol; LogCol = -1;
            if (win)
            {
                var took = new List<string>();
                foreach (var k in new[] { "food", "meds", "ammo", "materials" })
                {
                    double v = Math.Floor(o.Stock.Get(k, 0.0) * 0.15);
                    if (v < 1.0) continue;
                    o.Stock[k] -= v; c.Stock[k] = c.Stock.Get(k, 0.0) + v; took.Add(LbRes(k));
                }
                o.Str = Math.Max(4.0, o.Str - (3.0 + 3.0 * LbRoll(c, 43)));
                c.Str = Math.Max(4.0, c.Str - 1.0);
                bool killed = LbRoll(c, 44) < 0.35 && o.Pop > 3;
                if (killed) o.Pop--;
                m.Pride = Math.Min(1.0, m.Pride + 0.3); m.ProudOf = o.Id; m.ProudKind = "took";
                if (m.AngryAt == o.Id) m.Anger *= 0.5;
                LeaderRemember(m, o.Id, "we_raided", string.Format("We hit {0} and came home with their {1}.", o.Name, took.Count > 0 ? took[0] : "pride"));
                if (om != null)
                {
                    om.Anger = Math.Min(1.0, om.Anger + 0.4 * (0.5 + om.Temper)); om.AngryAt = c.Id;
                    om.Dread = Math.Min(1.0, om.Dread + 0.15);
                    LeaderRemember(om, c.Id, "raided_us", killed ? string.Format("{0} broke through, stripped our stores and killed one of ours.", c.Name) : string.Format("{0} broke through and stripped our stores.", c.Name));
                    if (killed) { om.LastPop = o.Pop; om.Buried += 1.0; om.Grief = Math.Min(1.0, om.Grief + 0.14); }
                }
                AddLog(string.Format("{0} raided {1} and drove off with their {2}.", c.Name, o.Name, took.Count > 0 ? string.Join(" and ", took.Take(2)) : "stores"), "info");
            }
            else
            {
                c.Str = Math.Max(4.0, c.Str - (3.0 + 2.0 * LbRoll(c, 45)));
                bool lostOne = LbRoll(c, 46) < 0.4 && c.Pop > 3;
                if (lostOne) { c.Pop--; m.LastPop = c.Pop; m.Buried += 1.0; m.Grief = Math.Min(1.0, m.Grief + 0.14); }
                m.Dread = Math.Min(1.0, m.Dread + 0.15); m.Pride *= 0.5;
                LeaderRemember(m, o.Id, "raid_failed", lostOne ? string.Format("We tried {0} and they were ready. One of ours didn't come back.", o.Name) : string.Format("We tried {0} and they were ready. We dragged our wounded home.", o.Name));
                if (om != null)
                {
                    om.Pride = Math.Min(1.0, om.Pride + 0.3); om.ProudOf = c.Id; om.ProudKind = "beat";
                    om.Anger = Math.Min(1.0, om.Anger + 0.2); om.AngryAt = c.Id;
                    LeaderRemember(om, c.Id, "beat_raid", string.Format("{0} came for us and we sent them running.", c.Name));
                }
                AddLog(string.Format("{0} tried to raid {1} and were beaten back.", c.Name, o.Name), "info");
            }
            LogCol = lc;
            m.Op[o.Id] = V.Clamp(m.Op.Get(o.Id, 0.0) - 15.0, -100.0, 100.0);
            if (om != null)
            {
                om.Op[c.Id] = V.Clamp(om.Op.Get(c.Id, 0.0) - 40.0, -100.0, 100.0);
                if (om.Allies.Remove(c.Id)) { m.Allies.Remove(o.Id); LeaderRemember(om, c.Id, "betrayed", string.Format("{0} were our allies. They broke faith.", c.Name)); }
            }
            m.Goal = ""; m.GoalT = S.T - 6.0;   // rethink next hour
        }

        /// <summary>Once a day: opinions drift, alliances form and break, feelings about you turn into relations.</summary>
        void LeaderDiplomacy()
        {
            var alive = S.Camps.Where(c => c.Alive && c.Fi.HasValue && c.Mind != null).ToList();
            foreach (var a in alive)
            {
                var m = a.Mind;
                foreach (var b in alive)
                {
                    if (a == b) continue;
                    double op = m.Op.Get(b.Id, 0.0);
                    double drift = 0.0;
                    double d = V.F(a.Pos).DistTo(V.F(b.Pos));
                    if (d < 70.0) drift -= 0.4 * (1.0 - m.Trust) * 0.5;          // too close for comfort
                    if (b.Att == "Raiders") drift -= 0.3;
                    if (m.Allies.Contains(b.Id)) drift += 0.5;
                    // shared enemies bring people together
                    foreach (var e in alive) if (e != a && e != b && m.Op.Get(e.Id, 0.0) < -30.0 && b.Mind.Op.Get(e.Id, 0.0) < -30.0) { drift += 0.6; break; }
                    if (drift > 0.0) drift *= m.Trust < -0.3 ? 0.5 : m.Trust > 0.3 ? 1.4 : 1.0;
                    m.Op[b.Id] = V.Clamp(op + drift, -100.0, 100.0);
                }
            }
            int lc = LogCol; LogCol = -1;
            foreach (var a in alive)
                foreach (var b in alive)
                {
                    if (string.CompareOrdinal(a.Id, b.Id) >= 0) continue;
                    var ma = a.Mind; var mb = b.Mind;
                    double oa = ma.Op.Get(b.Id, 0.0), ob = mb.Op.Get(a.Id, 0.0);
                    bool allied = ma.Allies.Contains(b.Id);
                    if (!allied && oa >= 50.0 && ob >= 50.0)
                    {
                        ma.Allies.Add(b.Id); mb.Allies.Add(a.Id);
                        LeaderRemember(ma, b.Id, "ally", string.Format("We shook hands with {0}. If one of us is hit, the other comes.", b.Name));
                        LeaderRemember(mb, a.Id, "ally", string.Format("We shook hands with {0}. If one of us is hit, the other comes.", a.Name));
                        AddLog(string.Format("{0} and {1} have agreed to stand together.", a.Name, b.Name), "info");
                    }
                    else if (allied && (oa < 20.0 || ob < 20.0))
                    {
                        ma.Allies.Remove(b.Id); mb.Allies.Remove(a.Id);
                        var who = oa < ob ? a : b; var other = who == a ? b : a;
                        LeaderRemember(who.Mind, other.Id, "split", string.Format("We are done with {0}. I don't trust them anymore.", other.Name));
                        LeaderRemember(other.Mind, who.Id, "betrayed", string.Format("{0} turned their back on us.", who.Name));
                        AddLog(string.Format("{0} and {1} have fallen out.", a.Name, b.Name), "info");
                    }
                }
            LogCol = lc;
            // feelings about you become relations, slowly
            foreach (var c in alive)
            {
                var m = c.Mind;
                for (int ci = 0; ci < S.Cols.Count && ci < m.Grudge.Count; ci++)
                {
                    if (!ColAlive(S.Cols[ci])) continue;
                    double net = m.Favour[ci] - m.Grudge[ci];
                    double d = V.Clamp(net * 0.06, -2.5, 2.0);
                    if (m.Trust < -0.4 && d > 0.0) d *= 0.5;   // paranoid people warm slowly
                    if (Math.Abs(d) >= 0.1) AddRel(c, d, ci);
                }
            }
        }

        // ---------------- what they say ----------------
        static readonly Dictionary<string, string> VOICE_US = new Dictionary<string, string>
        {
            ["farmer"] = "folks", ["engineer"] = "the crew", ["soldier"] = "my people", ["boss"] = "my boys", ["preacher"] = "the flock",
            ["biker"] = "the dogs", ["nurse"] = "our people", ["plain"] = "everyone",
        };

        static readonly Dictionary<string, string[]> VOICE_TAIL = new Dictionary<string, string[]>
        {
            ["farmer"] = new[] { "Nobody goes hungry on my watch.", "We'll get through this the way we got through the drought." },
            ["engineer"] = new[] { "Do the job in front of you.", "Keep the turbines turning and the lights stay on." },
            ["soldier"] = new[] { "Nobody gets left behind.", "That's an order, not a suggestion." },
            ["boss"] = new[] { "Anybody who doesn't like it can swim.", "Nobody takes what's ours." },
            ["preacher"] = new[] { "The Lord tests us. We will not fail Him.", "Strangers stay outside the gate." },
            ["biker"] = new[] { "Dogs look after dogs.", "Nobody cages us." },
            ["nurse"] = new[] { "Every life in here matters.", "We don't leave anyone in the cold." },
            ["plain"] = new[] { "We keep going.", "One more day." },
        };

        static readonly Dictionary<string, string[]> GOAL_LINES = new Dictionary<string, string[]>
        {
            ["hunker"] = new[] { "The dead are thick to the {dir}. Nobody goes out. {Us} stay inside until they drift off.", "I pulled everyone in off the streets. Doors barred, lights out, we wait them out.", "Too many of them out there. I'm not losing anyone else to a supply run." },
            ["hunker_panic"] = new[] { "I keep hearing them at the fence. Maybe it's nothing. Nobody goes out anyway.", "Call me a coward if you want. Everyone stays inside until I say so." },
            ["fortify"] = new[] { "Every spare hand is on the {dir} wall. It has to hold before the next big wave.", "Nobody leaves the walls until the {dir} fence is done.", "If the {dir} side goes, we all go. So the {dir} side gets fixed first." },
            ["build"] = new[] { "Quiet days are for building. We're putting in {project}.", "Working on {project} while it's calm. Something that lasts." },
            ["expand"] = new[] { "We need more room. The building next door gets cleared and made ours before someone else takes it.", "{Us} are pushing out a block. More roofs, more beds, more ground to grow on." },
            ["scavenge"] = new[] { "We're low on {need}. Teams go out at first light and come back before dark.", "Runners are going house to house for {need}. Nobody goes alone." },
            ["scavenge_calm"] = new[] { "It's quiet, so the runners are out picking the town clean before winter.", "Stores are good. We go out anyway. Next month might not be this kind." },
            ["recruit"] = new[] { "There are still people hiding out there. We're bringing in anyone who can carry their weight.", "Looking for survivors. A few more hands and this place could really stand." },
            ["trade"] = new[] { "We have more {spare} than we can use. Sending a cart to {friend}.", "Trading our {spare} with {friend}. Better than letting it rot." },
            ["ask_help"] = new[] { "We're running out of {need}. I swallowed my pride and sent word out.", "Things are bad. If nobody helps us with {need}, people here will start dying." },
            ["raid"] = new[] { "{foe} has full storerooms and we have empty bellies. We're going to take some of it.", "{foe} had this coming. We hit them when they least expect it." },
            ["raid_you"] = new[] { "{foe} keep getting fat while we go hungry. That changes soon.", "{foe} think their fences make them safe. We'll see." },
            ["raid_revenge"] = new[] { "{foe} killed our people. Now they find out what that costs.", "We owe {foe} for every grave out back. We're going to pay it." },
            ["retreat"] = new[] { "We can't hold the outer buildings. Everyone falls back to the main house.", "We're giving ground on the {dir} side. Buildings can be retaken. People can't." },
            ["war"] = new[] { "It's coming to a fight with {foe}. Everyone who can hold a gun drills every day.", "{foe} wouldn't bend. Fine. We train, we stock up, and we end it." },
        };

        string LeaderVoice(Camp c, LeaderMind m, LeaderCtx x)
        {
            int slot = (int)Math.Floor(S.T / 12.0);
            int Pick(int n, int salt) => (int)(LbRoll(c, salt, slot) * n) % Math.Max(1, n);
            var parts = new List<string>();
            // what is on their mind first
            double mx = Math.Max(Math.Max(m.Grief, m.Dread * 0.8), Math.Max(m.Anger, m.Pride));
            if (mx >= 0.3)
            {
                string[] o;
                if (mx == m.Grief)
                    o = m.Buried >= 1.0 ? new[] { string.Format("We buried {0} this week.", LbCount((int)Math.Round(m.Buried))), "Too many graves out back now." }
                                        : new[] { string.Format("I still think about {0} every night.", m.LostName), "Nobody here has slept right in a while." };
                else if (mx == m.Anger && m.AngryAt != "") o = new[] { string.Format("I haven't forgotten what {0} did.", LbWhoName(m.AngryAt)), string.Format("{0} will answer for what they did to us.", LbWhoName(m.AngryAt)) };
                else if (mx == m.Pride && m.ProudOf != "" && m.ProudKind == "beat") o = new[] { string.Format("We sent {0} running.", LbWhoName(m.ProudOf)), "We held. People here walk a little taller." };
                else if (mx == m.Pride && m.ProudOf != "") o = new[] { string.Format("{0} found out what happens to the soft ones.", LbWhoName(m.ProudOf)), string.Format("We took what we needed from {0}. Nobody here went hungry for it.", LbWhoName(m.ProudOf)) };
                else if (mx == m.Pride) o = new[] { "Things are finally coming together here.", "For the first time in a long time, this place feels like it might last." };
                else if (mx == m.Anger) o = new[] { "Everyone's angry and looking for someone to blame.", "My temper's short these days." };
                else o = new[] { "I don't like how many of them are out there.", "Everyone's jumpy." };
                parts.Add(o[Pick(o.Length, 51)]);
            }
            string key = m.Goal == "" ? "scavenge" : m.Goal;
            string needR = LbWorstNeed(c);
            if (key == "scavenge" && needR == "") key = "scavenge_calm";
            if (key == "hunker" && x.Threat < 0.3 && m.Dread > 0.5) key = "hunker_panic";
            string foe = "";
            if (key == "raid" || key == "war")
            {
                foe = LbWhoName(m.Target);
                if (m.Target.StartsWith("col:") && key == "raid") key = "raid_you";
                if (key.StartsWith("raid") && m.AngryAt == m.Target && m.Anger > 0.3) key = "raid_revenge";
            }
            var lines = GOAL_LINES.TryGetValue(key, out var gl) ? gl : GOAL_LINES["scavenge"];
            string s = lines[Pick(lines.Length, 52)];
            string us = VOICE_US.TryGetValue(m.Voice, out var u) ? u : "everyone";
            string spare = c.Surplus.FirstOrDefault(r => c.Stock.Get(r, 0.0) > 30.0) ?? (c.Surplus.Count > 0 ? c.Surplus[0] : "food");
            s = s.Replace("{dir}", x.Dir).Replace("{Us}", char.ToUpperInvariant(us[0]) + us.Substring(1)).Replace("{us}", us)
                 .Replace("{project}", LbProjectName(m.Project != "" ? m.Project : LbProject(c, m, x))).Replace("{need}", LbRes(needR == "" ? "food" : needR))
                 .Replace("{spare}", LbRes(spare)).Replace("{friend}", CampById(m.Target)?.Name ?? "the next camp over").Replace("{foe}", foe);
            parts.Add(s);
            if (LbRoll(c, 53, slot) < 0.35 && VOICE_TAIL.TryGetValue(m.Voice, out var tails)) parts.Add(tails[Pick(tails.Length, 54)]);
            return string.Join(" ", parts);
        }

        /// <summary>StepCamps (EXPANDED): how much a leader's care cuts the odds of the dead overrunning the camp
        /// this hour. 1 = no help. Barred doors, finished walls and a lookout tower all count.</summary>
        double LeaderGuard(Camp c)
        {
            var m = c.Mind;
            if (!ExpansionRules || m == null) return 1.0;
            double g = 1.0;
            if (m.Goal == "hunker" || m.Goal == "retreat") g *= 0.6;
            else if (m.Goal == "fortify" || m.Goal == "war") g *= 0.8;
            g *= Math.Max(0.5, 1.0 - 0.1 * m.Walls);
            if (m.Built.Contains("tower")) g *= 0.85;
            return g;
        }

        // ---------------- hooks from Game.Factions (EXPANDED only, no Rng) ----------------
        /// <summary>A player colony did something to/for this camp.</summary>
        void LeaderEvent(Camp c, int ci, string kind)
        {
            if (!ExpansionRules || c == null || !c.Alive || c.Mind == null) return;
            var m = c.Mind;
            if (ci < 0) ci = Math.Max(0, CurCol);
            while (m.Grudge.Count <= ci) { m.Grudge.Add(0.0); m.Favour.Add(0.0); m.LastRels.Add(CampRel(c, m.LastRels.Count)); }
            string you = LbColName(ci), who = "col:" + ci;
            switch (kind)
            {
                case "gift":
                    if (m.AskUntil > S.T && m.AskCol == ci && (m.AskRes == "food" || m.AskRes == ""))
                    {
                        m.Favour[ci] += 35.0; m.AskUntil = -1.0; m.AskCol = -1;
                        AddRel(c, 6.0, ci);
                        m.Hope = Math.Min(1.0, m.Hope + 0.15);
                        LeaderRemember(m, who, "gift", string.Format("When we were going hungry, {0} sent food. We won't forget that.", you));
                    }
                    else
                    {
                        m.Favour[ci] += m.Greed > 0.4 ? 6.0 : 10.0;
                        if (!m.Mem.Any(e => e.Who == who && e.Kind == "gift" && S.T - e.T < 72.0))
                            LeaderRemember(m, who, "gift", m.Trust < -0.4 ? string.Format("{0} sent food. I'd like to know what they want for it.", you) : string.Format("{0} sent us food for nothing. Decent of them.", you));
                    }
                    if (m.Grudge[ci] > 0.0) m.Grudge[ci] = Math.Max(0.0, m.Grudge[ci] - 8.0);
                    break;
                case "parley":
                    m.Favour[ci] += 12.0; m.Grudge[ci] = Math.Max(0.0, m.Grudge[ci] - 15.0);
                    LeaderRemember(m, who, "parley", string.Format("{0} came to talk instead of shoot. That counts for something.", you));
                    break;
                case "tribute":
                    m.Pride = Math.Min(1.0, m.Pride + 0.15); m.ProudOf = who; m.ProudKind = "took";
                    LeaderRemember(m, who, "tribute", string.Format("{0} paid us to stay away. They know who's stronger.", you));
                    break;
                case "pact":
                    m.Favour[ci] += 25.0; m.Hope = Math.Min(1.0, m.Hope + 0.1);
                    LeaderRemember(m, who, "pact", string.Format("We signed a pact with {0}. I hope they mean it.", you));
                    break;
                case "recruit":
                    m.LastPop = c.Pop;   // they walked, nobody died
                    LeaderRemember(m, who, "recruit", string.Format("Two of ours went over to {0}. I hope they're treated right.", you));
                    break;
                case "help_sent":
                    m.LastPop = c.Pop;
                    LeaderRemember(m, who, "help_sent", string.Format("We sent two fighters to hold the line with {0}.", you));
                    break;
                case "raid_won":
                    m.Pride = Math.Min(1.0, m.Pride + 0.2); m.ProudOf = who; m.ProudKind = "took";
                    LeaderRemember(m, who, "we_raided", string.Format("We hit {0} and came home loaded.", you));
                    break;
                case "raid_balked":
                    m.Dread = Math.Min(1.0, m.Dread + 0.05);
                    if (!m.Mem.Any(e => e.Who == who && e.Kind == "raid_balked" && S.T - e.T < 96.0))
                        LeaderRemember(m, who, "raid_balked", string.Format("We looked at {0}'s walls and walked away. Not worth the blood.", you));
                    break;
                case "ambush_won":
                    m.Pride = Math.Min(1.0, m.Pride + 0.1); m.ProudOf = who; m.ProudKind = "took";
                    break;
                case "ambush_lost":
                    m.Anger = Math.Min(1.0, m.Anger + 0.1); m.Grudge[ci] += 5.0;
                    if (!m.Mem.Any(e => e.Who == who && e.Kind == "ambush_lost" && S.T - e.T < 48.0))
                        LeaderRemember(m, who, "ambush_lost", string.Format("One of {0}'s people fought off our boys on the road.", you));
                    break;
            }
        }

        /// <summary>A camp went silent: every other leader takes it in.</summary>
        void LeaderCampFell(Camp fell)
        {
            if (!ExpansionRules) return;
            foreach (var o in S.Camps)
            {
                if (o == fell || !o.Alive || o.Mind == null) continue;
                var m = o.Mind;
                if (m.Target == fell.Id) { m.Goal = ""; m.Target = ""; m.Plan = ""; }   // rethink
                m.Dread = Math.Min(1.0, m.Dread + (m.Allies.Contains(fell.Id) ? 0.3 : 0.15));
                if (m.Allies.Contains(fell.Id)) { m.Grief = Math.Min(1.0, m.Grief + 0.25); m.Allies.Remove(fell.Id); }
                LeaderRemember(m, fell.Id, "fell", m.Op.Get(fell.Id, 0.0) > 20.0
                    ? string.Format("{0} went silent. They were good people.", fell.Name)
                    : string.Format("{0} went silent. If it can happen to them, it can happen to us.", fell.Name));
            }
        }
    }
}
