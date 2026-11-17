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

        /// <summary>Hourly: roll anyone without traits (grown-up children, old saves), reveal hidden traits after 24 h.</summary>
        void PeopleHour()
        {
            if (!ExpansionRules) return;
            foreach (var p in C.People)
            {
                if (p.Traits == null) RollTraits(p);
                RevealTraits(p);
            }
        }

        void RevealTraits(Person p)
        {
            if (p.TraitHidden == null || S.T < p.JoinT + TRAIT_REVEAL_H) return;
            string id = p.TraitHidden;
            p.TraitHidden = null;
            if (Traits.TryGetValue(id, out var td)) AddLog(string.Format("{0} turns out to be {1}.", p.First, td.Name), "info");
        }
    }
}
