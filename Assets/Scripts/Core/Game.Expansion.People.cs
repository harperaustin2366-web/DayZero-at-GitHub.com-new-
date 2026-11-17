// Expansion: people. SYSTEMS_30 #6 Survivor Traits (traits only; stress #5 and fatigue #18 come later).
// Everything here runs only while Game.ExpansionRules is on, and draws from its own _prng, never the main Rng,
// so classic (v0.10.8a) runs stay bit-exact.
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace DayZero.Core
{
    public sealed partial class Game
    {
        public sealed class TraitDef { public string Id, Name, Desc; public double Weight; public Dictionary<string, double> Fx = new Dictionary<string, double>(); }

        static Dictionary<string, TraitDef> _traits;
        static List<string[]> _traitConflicts;
        public const double TRAIT_REVEAL_H = 24.0;

        GdRng _prng;
        GameState _prngFor;

        /// <summary>Trait table from Data/traits.json, loaded on first use.</summary>
        public static Dictionary<string, TraitDef> Traits
        {
            get { if (_traits == null) LoadTraits(); return _traits; }
        }

        static void LoadTraits()
        {
            _traits = new Dictionary<string, TraitDef>();
            _traitConflicts = new List<string[]>();
            JToken d = null;
            try { d = DataLoader.Load("Data/traits"); } catch (Exception) { d = null; }
            if (d is JObject o && o["traits"] is JObject t)
            {
                foreach (var kv in t)
                {
                    var td = new TraitDef { Id = kv.Key, Name = (string)kv.Value["name"] ?? kv.Key, Desc = (string)kv.Value["desc"] ?? "", Weight = (double?)kv.Value["weight"] ?? 0.0 };
                    if (kv.Value["fx"] is JObject fx) foreach (var f in fx) td.Fx[f.Key] = (double)f.Value;
                    _traits[kv.Key] = td;
                }
                if (o["conflicts"] is JArray ca) foreach (var c in ca) _traitConflicts.Add(c.Select(x => (string)x).ToArray());
            }
            if (_traits.Count == 0)
            {
                // Fallback when the data file is missing (headless tools): the simple effects only.
                void A(string id, string n, double w, string k = null, double v = 0) { var td = new TraitDef { Id = id, Name = n, Weight = w }; if (k != null) td.Fx[k] = v; _traits[id] = td; }
                A("sharpshooter", "Sharpshooter", 6, "hit_add", 0.12); A("ex_medic", "Ex-Medic", 5); A("green_thumb", "Green Thumb", 6, "farmer_mult", 0.30);
                A("tinkerer", "Tinkerer", 6, "engineer_mult", 0.25); A("insomniac", "Insomniac", 5); A("night_owl", "Night Owl", 4); A("coward", "Coward", 6);
                A("hoarder", "Hoarder", 5); A("tough", "Tough", 5, "max_hp", 120); A("glass_jaw", "Glass Jaw", 3, "max_hp", 80); A("quiet", "Quiet", 5);
                A("loudmouth", "Loudmouth", 3); A("leader", "Leader", 3); A("mechanic", "Mechanic", 4); A("dog_person", "Dog Person", 3); A("scout", "Scout", 4);
                A("hardened", "Hardened", 0, "hit_add", 0.08);
                _traitConflicts.Add(new[] { "coward", "leader" }); _traitConflicts.Add(new[] { "quiet", "loudmouth" }); _traitConflicts.Add(new[] { "tough", "glass_jaw" });
            }
        }

        GdRng PRng
        {
            get
            {
                if (_prng == null || !ReferenceEquals(_prngFor, S))
                {
                    _prng = new GdRng((long)(S.Seed ^ 0x7EA1) + (long)(S.T * 7.0));
                    _prngFor = S;
                }
                return _prng;
            }
        }

        public static bool HasTrait(Person p, string id) => p != null && p.Traits != null && p.Traits.Contains(id);

        /// <summary>Sum of one fx key over a person's traits (0 when none).</summary>
        public static double Tx(Person p, string key)
        {
            if (p?.Traits == null) return 0.0;
            double v = 0.0;
            foreach (var id in p.Traits) if (Traits.TryGetValue(id, out var td) && td.Fx.TryGetValue(key, out var x)) v += x;
            return v;
        }

        public static double MaxHp(Person p)
        {
            if (!ExpansionRules || p?.Traits == null) return 100.0;
            foreach (var id in p.Traits) if (Traits.TryGetValue(id, out var td) && td.Fx.TryGetValue("max_hp", out var x)) return x;
            return 100.0;
        }

        static bool TraitConflict(string a, string b) => _traitConflicts.Any(c => c.Length == 2 && ((c[0] == a && c[1] == b) || (c[0] == b && c[1] == a)));

        string PickTrait(List<string> have)
        {
            var pool = Traits.Values.Where(t => t.Weight > 0 && !have.Contains(t.Id) && !have.Any(h => TraitConflict(h, t.Id))).ToList();
            double tot = pool.Sum(t => t.Weight);
            if (tot <= 0) return null;
            double r = PRng.Randf() * tot;
            foreach (var t in pool) { r -= t.Weight; if (r < 0) return t.Id; }
            return pool[pool.Count - 1].Id;
        }

        /// <summary>SYSTEMS_30 #6: 25% none, 60% one, 15% two (never conflicting); one trait stays hidden for 24 h.</summary>
        public void RollTraits(Person p)
        {
            if (!ExpansionRules || p == null || p.Traits != null) return;
            if (AgeStage(p) != "adult") return;   // children roll when they grow up (PeopleHour)
            p.Traits = new List<string>();
            p.JoinT = S.T;
            double r = PRng.Randf();
            int n = r < 0.25 ? 0 : (r < 0.85 ? 1 : 2);
            for (int i = 0; i < n; i++) { var t = PickTrait(p.Traits); if (t != null) p.Traits.Add(t); }
            if (p.Traits.Count > 0) p.TraitHidden = p.Traits[(int)(PRng.Randf() * p.Traits.Count) % p.Traits.Count];
            double mh = MaxHp(p);
            if (mh > 100.0 && p.Hp >= 100.0) p.Hp = mh; else if (p.Hp > mh) p.Hp = mh;
        }

        /// <summary>Hourly: roll anyone without traits (grown-up children, old saves), reveal hidden traits after 24 h,
        /// then fatigue and shifts (#18) and stress, morale and breaking points (#5).</summary>
        void PeopleHour()
        {
            if (!ExpansionRules) return;
            foreach (var p in C.People)
            {
                if (p.Traits == null) RollTraits(p);
                RevealTraits(p);
            }
            if (C.Ppl == null) C.Ppl = new PeopleCfg();
            StepFatigue();
            StepStress();
        }

        void RevealTraits(Person p)
        {
            if (p.TraitHidden == null || S.T < p.JoinT + TRAIT_REVEAL_H) return;
            string id = p.TraitHidden;
            p.TraitHidden = null;
            if (Traits.TryGetValue(id, out var td)) AddLog(string.Format("{0} turns out to be {1}.", p.First, td.Name), "info");
        }
    
        // ---------------- #18 fatigue and night-watch shifts ----------------
        public const double FAT_DOZE = 80.0, FAT_TIRED = 60.0;

        /// <summary>Output multiplier from fatigue: 0.85 at 60+, 0.75 at 80+.</summary>
        public static double FatigueOutMult(Person p)
        {
            if (!ExpansionRules || p == null) return 1.0;
            return p.Fatigue >= FAT_DOZE ? 0.75 : (p.Fatigue >= FAT_TIRED ? 0.85 : 1.0);
        }

        /// <summary>Hit chance change from fatigue (added to the hit chance): -0.05 at 60+, -0.12 at 80+
        /// (the spec's -10% / -25%, taken relative to a typical 0.5 gun hit).</summary>
        public static double FatigueHitAdd(Person p)
        {
            if (!ExpansionRules || p == null) return 0.0;
            return p.Fatigue >= FAT_DOZE ? -0.12 : (p.Fatigue >= FAT_TIRED ? -0.05 : 0.0);
        }

        static bool WatchHour(int h) => h >= 18 || h < 6;

        /// <summary>Which shift (1..n) is on watch at hour h: 2 shifts 18-00 / 00-06; 3 shifts 18-22 / 22-02 / 02-06.</summary>
        static int ShiftAt(int h, int n)
        {
            if (n <= 1) return 1;
            if (n == 2) return h >= 18 ? 1 : 2;
            if (h >= 18 && h < 22) return 1;
            if (h >= 22 || h < 2) return 2;
            return 3;
        }

        public int PplShifts => Math.Max(1, Math.Min(3, C?.Ppl?.Shifts ?? 1));

        /// <summary>A guard whose shift is on (always true outside watch hours or with one shift).</summary>
        public bool OnShift(Person p)
        {
            if (!ExpansionRules || !p.Guard) return true;
            int h = Hour();
            int n = PplShifts;
            if (!WatchHour(h) || n <= 1) return true;
            return p.Shift == ShiftAt(h, n);
        }

        public bool Asleep(Person p) => ExpansionRules && (p.Collapsed || p.SleepEndT > S.T);

        /// <summary>CombatTick / tower crew: dozing, asleep or off-shift guards don't fight.</summary>
        public bool PplSkipFight(Person p)
        {
            if (!ExpansionRules) return false;
            if (p.Dozing || Asleep(p)) return true;
            return p.Guard && p.St == "base" && WatchHour(Hour()) && !OnShift(p);
        }

        /// <summary>AssignJobs: frozen, resting, asleep or collapsed people take no job.</summary>
        public bool PplNoWork(Person p)
        {
            if (!ExpansionRules) return false;
            return p.FreezeEndT > S.T || p.RestEndT > S.T || Asleep(p);
        }

        /// <summary>AutoGuards: extra priority (higher = picked later). Tired people and people who can't work go last.</summary>
        public double PplGuardPri(Person p)
        {
            if (!ExpansionRules) return 0.0;
            return p.Fatigue / 25.0 + (PplNoWork(p) ? 100.0 : 0.0);
        }

        /// <summary>BaseBalance: job output multiplier = fatigue x morale (0.8..1.2); 0 while frozen, resting or asleep.</summary>
        public double PplOutMult(Person p)
        {
            if (!ExpansionRules) return 1.0;
            if (PplNoWork(p)) return 0.0;
            return FatigueOutMult(p) * (0.8 + 0.4 * Math.Max(0.0, Math.Min(100.0, C.Morale)) / 100.0);
        }

        static readonly string[] OUT_KEYS = { "food", "water", "materials", "meds" };
        double[] PplSnap(ODict<string, double> r) => OUT_KEYS.Select(k => r.Get(k, 0.0)).ToArray();
        void PplScale(ODict<string, double> r, double[] pre, double m)
        {
            if (m == 1.0) return;
            for (int i = 0; i < OUT_KEYS.Length; i++)
            {
                double d = r.Get(OUT_KEYS[i], 0.0) - pre[i];
                if (d > 0.0) r[OUT_KEYS[i]] = pre[i] + d * m;
            }
        }

        /// <summary>Person id -> has a bed (people past a camp's bed count sleep rough).</summary>
        Dictionary<int, bool> BedMap()
        {
            var m = new Dictionary<int, bool>();
            foreach (var b in C.Bases)
            {
                int beds = BaseBeds(b), i = 0;
                foreach (var p in BasePeople(b.Id)) m[p.Id] = i++ < beds;
            }
            return m;
        }

        Dictionary<int, bool> _bedMap;
        bool HasBed(Person p) => _bedMap == null || !_bedMap.TryGetValue(p.Id, out var v) || v;

        void StepFatigue()
        {
            int h = Hour();
            int n = PplShifts;
            _bedMap = BedMap();
            bool raid = C.Nraid != null && C.Nraid.On;
            // shift roster: at 18:00 (or for new guards) split each camp's guards round-robin, most rested first
            foreach (var b in C.Bases)
            {
                var gs = BasePeople(b.Id).Where(p => p.Guard).ToList();
                if (h == 18 || gs.Any(p => p.Shift < 1 || p.Shift > n))
                {
                    if (C.Ppl.AutoRotate) gs = gs.OrderBy(p => p.Fatigue).ThenBy(p => p.Id).ToList();
                    for (int i = 0; i < gs.Count; i++) gs[i].Shift = n <= 1 ? 1 : (i % n) + 1;
                }
            }
            foreach (var p in C.People)
            {
                p.Dozing = false;
                if (p.St == "dead") continue;
                if (!p.Guard) p.Shift = 0;
                if (IsKid(p)) { p.Fatigue = 0.0; continue; }
                bool bed = HasBed(p);
                double d;
                if (Asleep(p))
                {
                    d = bed ? -10.0 : -5.0;
                    if (p.SleepEndT <= S.T + 1.0 && p.Collapsed) p.Collapsed = false;
                }
                else if (p.St != "base") d = p.Sq > 0 ? 4.0 : 3.0;
                else if (p.Guard && WatchHour(h) && OnShift(p))
                {
                    d = raid && C.Nraid.Bid == p.Base ? 4.0 : 3.0;   // spec +4 / +6, eased so a 12 h single watch tops out under 100
                    if (HasTrait(p, "insomniac")) d *= 0.5;
                    if (p.Fatigue >= FAT_DOZE && PRng.Randf() < 0.05) { p.Dozing = true; C.Ppl.Dozed++; }
                }
                else if (h >= 22 || h < 6 || (p.Guard && WatchHour(h))) d = bed ? -10.0 : -5.0;   // night sleep (off-shift guards too)
                else if (h >= 18 || p.FreezeEndT > S.T || p.RestEndT > S.T) d = -1.0;
                else d = (p.Job == "Farmer" || p.Job == "Engineer" || p.Job == "Scavenger") ? 3.0 : 2.0;
                p.Fatigue = Math.Max(0.0, Math.Min(100.0, p.Fatigue + d));
                if (p.Fatigue >= 100.0 && !Asleep(p))
                {
                    p.Collapsed = true; p.SleepEndT = S.T + 8.0; C.Ppl.Collapses++;
                    AddLog(string.Format("{0} collapsed from exhaustion and sleeps where they dropped.", p.First), "loss");
                }
                // a long watch is slept off in the morning
                if (h == 6 && p.Guard && p.Fatigue >= 40.0 && !Asleep(p)) p.SleepEndT = S.T + 6.0;
            }
        }

        // ---------------- #5 stress, morale and breaking points ----------------
        public void AddStress(Person p, double v)
        {
            if (p == null || p.St == "dead" || IsMinor(p)) return;
            if (v > 0.0 && HasTrait(p, "hardened")) v *= 0.8;
            p.Stress = Math.Max(0.0, Math.Min(100.0, p.Stress + v));
        }

        /// <summary>Hook in KillPerson: the camp sees a death (+12; partner, parent or child +35).</summary>
        void PplOnDeath(Person dead)
        {
            if (!ExpansionRules || C == null) return;
            if (C.Ppl == null) C.Ppl = new PeopleCfg();
            C.Ppl.DeathDay[dead.Base.ToString()] = Day();
            foreach (var p in C.People)
            {
                if (p == dead || p.St == "dead") continue;
                bool close = p.Id == dead.Partner || dead.Kids.Contains(p.Id) || dead.Parents.Contains(p.Id);
                if (close) AddStress(p, 35.0);
                else if (p.Base == dead.Base && p.St == "base") AddStress(p, 12.0);
            }
        }

        /// <summary>Hook in Turn: someone turned inside the camp (+15 to everyone there).</summary>
        void PplOnTurn(int bid)
        {
            if (!ExpansionRules) return;
            foreach (var p in AtBase(bid)) AddStress(p, 15.0);
        }

        void StepStress()
        {
            int h = Hour();
            int day = Day();
            var adults = C.People.Where(p => p.St != "dead" && !IsMinor(p)).ToList();
            // a breach in tonight's raid: +6 to that camp, once per raid
            if (C.Nraid != null && C.Nraid.On)
            {
                if (C.Ppl.RaidStart != C.Nraid.Start) { C.Ppl.RaidStart = C.Nraid.Start; C.Ppl.RaidBreaks = 0; }
                if (C.Nraid.Breaks > 0 && C.Ppl.RaidBreaks == 0)
                {
                    C.Ppl.RaidBreaks = C.Nraid.Breaks;
                    foreach (var p in AtBase(C.Nraid.Bid)) AddStress(p, 6.0);
                }
            }
            bool starving = R("food") <= 0.5;
            foreach (var p in adults)
            {
                bool bed = HasBed(p);
                double g = 0.0;
                if (starving) g += 1.0;
                if (!bed) g += 0.3;
                if (p.Fatigue >= FAT_DOZE) g += 0.5;
                if (p.Hp < 50.0 || (p.Inf >= 0.0 && p.Known)) g += 0.3;
                AddStress(p, g);
                double rel = 0.0;
                bool sleeping = Asleep(p) || (p.St == "base" && (h >= 22 || h < 6) && !(p.Guard && OnShift(p)));
                if (sleeping && bed) rel += 0.6;
                if (C.Morale > 70.0) rel += 0.2;
                rel += 0.1 * Math.Min(3, ConvN("school", p.Base));
                var pt = p.Partner != 0 ? PersonById(p.Partner) : null;
                if (pt != null && pt.St != "dead" && pt.Base == p.Base) rel += 0.1;
                if (h == 6)
                {
                    int dd = C.Ppl.DeathDay.Get(p.Base.ToString(), -99);
                    if (dd != day && dd != day - 1) rel += 3.0;
                }
                p.Stress = Math.Max(0.0, p.Stress - rel);
            }
            if (adults.Count > 0)
            {
                double target = 100.0 - adults.Average(p => p.Stress);
                C.Morale = Math.Max(0.0, Math.Min(100.0, C.Morale + (target - C.Morale) * 0.02));
            }
            // AUTO: medics and leaders talk to people at breaking point
            if (C.Ppl.AutoTalk)
                foreach (var p in adults)
                    if (p.Stress >= 80.0 && p.TalkDay != day && p.St == "base" && Talker(p) != null) DoTalk(p, Talker(p));
            // stash found again (or lost for good)
            foreach (var p in adults)
                if (p.Stash != null && S.T >= p.StashT)
                {
                    if (PRng.Randf() < 0.6)
                    {
                        foreach (var kv in p.Stash) C.Res.AddTo(kv.Key, kv.Value);
                        AddLog(string.Format("A search turned up {0}'s hidden stash. The supplies are back in storage.", p.First), "info");
                    }
                    p.Stash = null; p.StashT = 0.0;
                }
            // deserters slip away at night
            if (IsNight())
                foreach (var p in adults.Where(x => x.DesertT > 0.0 && x.St == "base").ToList()) Desert(p);
            if (h % 6 != 0) return;
            foreach (var p in adults)
            {
                if (p.St != "base" || p.Stress < 80.0 || p.DesertT > 0.0) continue;
                if (PRng.Randf() >= (p.Stress - 75.0) / 100.0) continue;
                Break(p);
            }
        }

        Person Talker(Person p)
        {
            var here = AtBase(p.Base).Where(x => x != p && !IsMinor(x) && !Asleep(x)).ToList();
            return here.FirstOrDefault(x => x.Job == "Medic" || HasTrait(x, "leader"))
                ?? here.Where(x => x.Stress < 50.0).OrderBy(x => x.Stress).ThenBy(x => x.Id).FirstOrDefault();   // the calmest one acts as camp leader
        }

        void DoTalk(Person p, Person by)
        {
            p.Stress = Math.Max(0.0, p.Stress - 15.0);
            p.TalkDay = Day();
            C.Ppl.Talks++;
            AddLog(string.Format("{0} sat down with {1} for an hour. {1} seems steadier.", by.First, p.First), "info");
        }

        void Break(Person p)
        {
            C.Ppl.Breaks++;
            string kind;
            if (HasTrait(p, "hoarder")) kind = "steal";
            else
            {
                var w = new List<(string k, double w)> { ("freeze", 30), ("steal", 20), ("desert", HasTrait(p, "coward") ? 40 : 20), ("snap", 15), ("steel", 15) };
                double r = PRng.Randf() * w.Sum(x => x.w);
                kind = w[w.Count - 1].k;
                foreach (var x in w) { r -= x.w; if (r < 0) { kind = x.k; break; } }
            }
            if (kind == "desert" && AlivePeople().Count <= 2) kind = "freeze";
            switch (kind)
            {
                case "freeze":
                    p.FreezeEndT = S.T + 24.0; C.Ppl.Freezes++;
                    AddLog(string.Format("{0} has shut down and refuses to work today.", p.First), "loss");
                    break;
                case "steal":
                    {
                        bool food = PRng.Randf() < 0.6 || R("meds") < 2.0;
                        string k = food ? "food" : "meds";
                        double amt = Math.Min(R(k), food ? 5 + (int)(PRng.Randf() * 11) : 2 + (int)(PRng.Randf() * 3));
                        if (amt > 0.0)
                        {
                            C.Res[k] = R(k) - amt;
                            if (p.Stash == null) p.Stash = new ODict<string, double>();
                            p.Stash.AddTo(k, amt);
                            p.StashT = S.T + 24.0 + PRng.Randf() * 48.0;
                        }
                        C.Ppl.Steals++;
                        AddLog(string.Format("Some {0} went missing from storage. {1} has been acting strange.", k, p.First), "loss");
                        break;
                    }
                case "desert":
                    p.DesertT = S.T;
                    if (IsNight()) Desert(p);
                    else AddLog(string.Format("{0} has been staring at the gate all day.", p.First), "loss");
                    return;   // the stress relief comes with leaving
                case "snap":
                    {
                        C.Ppl.Snaps++;
                        var others = AtBase(p.Base).Where(x => x != p).ToList();
                        if (p.Wpn != "" && IsNight() && (others.Count == 0 || PRng.Randf() < 0.5))
                        {
                            AddNoise(p.Base, 30f, "Gunshot");
                            AddLog(string.Format("{0} snapped and fired into the dark. Everything out there heard it.", p.First), "loss");
                        }
                        else if (others.Count > 0)
                        {
                            var v = others[(int)(PRng.Randf() * others.Count) % others.Count];
                            if (PRng.Randf() < 0.05) { KillPerson(v, string.Format("was killed by {0} in a fight", p.First)); }
                            else
                            {
                                v.Hp = Math.Max(5.0, v.Hp - 20.0);
                                AddLog(string.Format("{0} snapped and attacked {1}.", p.First, v.First), "loss");
                            }
                        }
                        break;
                    }
                case "steel":
                    p.Stress = 30.0; C.Ppl.Steeled++;
                    if (p.Traits == null) p.Traits = new List<string>();
                    if (!p.Traits.Contains("hardened")) p.Traits.Add("hardened");
                    AddLog(string.Format("{0} stared it down and came out harder. (Hardened)", p.First), "good");
                    return;
            }
            p.Stress = Math.Max(0.0, p.Stress - 15.0);
        }

        void Desert(Person p)
        {
            if (AlivePeople().Count <= 2) { p.DesertT = 0.0; return; }
            double food = Math.Min(3.0, R("food"));
            C.Res["food"] = R("food") - food;
            if (p.Wpn != "" && PRng.Randf() >= 0.25) { C.Weapons[p.Wpn] = C.Weapons.Get(p.Wpn, 0) + 1; p.Wpn = ""; }
            string gun = p.Wpn != "" ? " and a gun" : "";
            LeaveBld(p);
            _claimed.Remove(p.Lot);
            C.People.Remove(p);
            _peopleById.Remove(p.Id);
            C.Ppl.Deserted++;
            AddLog(string.Format("{0} ({1}) slipped out in the night with some food{2}.", p.Name, p.Job, gun), "loss");
        }

        /// <summary>DoActionInner: "ppl_talk:id" and "ppl_rest:id".</summary>
        string PplAction(string action)
        {
            if (!ExpansionRules) return "Not available.";
            if (C.Ppl == null) C.Ppl = new PeopleCfg();
            int c = action.IndexOf(':');
            if (c < 0 || !int.TryParse(action.Substring(c + 1), out int pid)) return "No such survivor.";
            var p = OwnPerson(pid);
            if (p == null || p.St == "dead") return "That survivor isn't in your colony.";
            if (action.StartsWith("ppl_talk"))
            {
                if (p.TalkDay == Day()) return string.Format("Someone already talked with {0} today.", p.First);
                if (p.St != "base") return string.Format("{0} is out right now.", p.First);
                var by = Talker(p);
                if (by == null) return string.Format("Nobody at camp can talk {0} down right now.", p.First);
                DoTalk(p, by);
                EmitChanged();
                return "";
            }
            if (action.StartsWith("ppl_rest"))
            {
                if (p.RestEndT > S.T) return string.Format("{0} is already resting.", p.First);
                p.RestEndT = S.T + 24.0;
                p.Stress = Math.Max(0.0, p.Stress - 25.0);
                if (p.Guard) { p.Guard = false; p.Shift = 0; }
                AddLog(string.Format("{0} takes a day off.", p.First), "info");
                EmitChanged();
                return "";
            }
            return "Unknown action.";
        }
    }
}
