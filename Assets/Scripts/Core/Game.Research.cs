// game.gd: research (effects cache, unlocks, progress), building conversions and gutting.
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DayZero.Core
{
    public sealed partial class Game
    {
        sealed class FxCache { public int N; public object Ref; public Dictionary<string, double> Fx; public HashSet<string> Un; }
        readonly Dictionary<int, FxCache> _fxCache = new Dictionary<int, FxCache>();
        static readonly FxCache EmptyFx = new FxCache { Fx = new Dictionary<string, double>(), Un = new HashSet<string>() };

        public double Rx(string key) => Fx().Fx.TryGetValue(key, out var v) ? v : 0.0;

        public bool HasUnlock(string u)
        {
            if (string.IsNullOrEmpty(u)) return true;
            return Fx().Un.Contains(u);
        }

        public bool HasResearch(string rid) => C.Research.Done.Contains(rid);

        public bool LightsOn(int ci = -1)
        {
            if (ci < 0) ci = ViewCol;
            var c = ColonyAt(ci);
            if (c?.Research == null) return false;
            return c.Research.Done.Contains(Data.ELECTRICITY);
        }

        public Dictionary<int, int> LitLots()
        {
            var o = new Dictionary<int, int>();
            if (S == null) return o;
            var on = new Dictionary<int, bool>();
            for (int ci = 0; ci < S.Cols.Count; ci++) on[ci] = LightsOn(ci);
            foreach (var b in AllBases())
            {
                if (!on.GetOr(b.Col, false)) continue;
                foreach (int l in b.Claim) if (l >= 0) o[l] = b.Col;
            }
            foreach (var kv in S.Conv)
                if (on.GetOr(kv.Value.Col, false)) o[kv.Key] = kv.Value.Col;
            return o;
        }

        FxCache Fx()
        {
            if (C == null) return EmptyFx;
            var done = C.Research.Done;
            if (_fxCache.TryGetValue(CurCol, out var c) && c.N == done.Count && ReferenceEquals(c.Ref, done)) return c;
            var fx = new Dictionary<string, double>();
            var un = new HashSet<string>();
            foreach (var rid in done)
            {
                if (!(D.Research[rid] is JObject t)) continue;
                if (t["fx"] is JObject f) foreach (var kv in f) fx[kv.Key] = fx.GetOr(kv.Key, 0.0) + (double)kv.Value;
                if (t["unlock"] is JArray u) foreach (var x in u) un.Add((string)x);
            }
            c = new FxCache { N = done.Count, Ref = done, Fx = fx, Un = un };
            _fxCache[CurCol] = c;
            return c;
        }

        public string UnlockSourceName(string u)
        {
            foreach (var kv in D.Research)
            {
                var t = (JObject)kv.Value;
                if (t["unlock"] is JArray ul && ul.Any(x => (string)x == u))
                    return string.Format("{0} ({1} T{2})", (string)t["name"], (string)D.ResearchCatsName[(string)t["cat"]], (int)(double)t["tier"]);
            }
            return u;
        }

        static Dictionary<string, double> CostOf(JToken c)
        {
            var o = new Dictionary<string, double>();
            if (c is JObject jo) foreach (var kv in jo) o[kv.Key] = (double)kv.Value;
            return o;
        }

        public Dictionary<string, double> ResearchCost(string rid) => CostOf(D.Research[rid]["cost"]);
        public double ResearchHours(string rid) => (double)D.Research[rid]["hours"];
        public double ResearchProg(string rid) => C.Research.Prog.Get(rid, -1.0);

        public string ResearchState(string rid)
        {
            if (HasResearch(rid)) return "done";
            if (C.Research.Cur == rid) return "active";
            foreach (var r in (JArray)D.Research[rid]["requires"])
                if (!HasResearch((string)r)) return "locked";
            return "available";
        }

        public string CanResearch(string rid)
        {
            if (D.Research[rid] == null) return "Unknown research.";
            if (HasResearch(rid)) return "Done.";
            foreach (var rt in (JArray)D.Research[rid]["requires"])
            {
                string r = (string)rt;
                if (!HasResearch(r))
                {
                    var t = D.Research[r];
                    return string.Format("Needs {0} ({1} T{2}).", (string)t["name"], (string)D.ResearchCatsName[(string)t["cat"]], (int)(double)t["tier"]);
                }
            }
            if (CountJob("Scientist") == 0) return "Needs a Scientist.";
            if (C.Research.Cur == rid) return "Already researching.";
            if (ResearchProg(rid) < 0.0 && !CanAfford(ResearchCost(rid))) return "Need " + FmtCost(ResearchCost(rid)) + ".";
            return "";
        }

        public string StartResearch(string rid)
        {
            string e = CanResearch(rid);
            if (e != "") return e;
            if (ResearchProg(rid) < 0.0)
            {
                Pay(ResearchCost(rid));
                C.Research.Prog[rid] = 0.0;
            }
            C.Research.Cur = rid;
            AddLog(string.Format("Research started: {0} ({1:0}h of lab time).", (string)D.Research[rid]["name"], ResearchHours(rid)), "info");
            EmitChanged();
            return "";
        }

        public double ResearchRate()
        {
            int sci = C.People.Count(p => p.St == "base" && p.Job == "Scientist");
            if (sci == 0) return 0.0;
            int lab = C.Bases.Count == 0 ? 0 : C.Bases.Max(b => b.U("lab"));
            return Math.Sqrt(sci) * (1.0 + Rx("research_speed")) * (1.0 + 0.4 * lab) * (1.0 + Math.Min(0.48, 0.12 * ConvN("school")));
        }

        void StepResearch()
        {
            string rid = C.Research.Cur;
            if (rid == "" || D.Research[rid] == null) return;
            double r = ResearchRate();
            if (r <= 0.0) return;
            C.Research.Prog[rid] = ResearchProg(rid) + r;
            if (C.Research.Prog[rid] >= ResearchHours(rid))
            {
                C.Research.Done.Add(rid);
                C.Research.Prog.Remove(rid);
                C.Research.Cur = "";
                AddLog(string.Format("Research complete: {0}. {1}", (string)D.Research[rid]["name"], (string)D.Research[rid]["desc"]), "good", rid);
                if (rid.StartsWith("house") || rid.StartsWith("elec") || rid.StartsWith("craft")) RebuildSafe();
            }
        }

        // ---------------- conversions ----------------
        public const double CONV_EVERY_H = 2.0;
        public const string GUT_FIRST = "Gut it first: strip it to the shell (GUT on this sheet), then pick a use.";
        readonly Dictionary<int, (Dictionary<string, int> all, Dictionary<int, Dictionary<string, int>> per)> _convCache = new Dictionary<int, (Dictionary<string, int>, Dictionary<int, Dictionary<string, int>>)>();
        int _convCacheVer = -1;
        public int GutCount;

        public Conversion ConvOf(int l) => S.Conv.Get(l);
        public string ConvType(int l) => ConvOf(l)?.Type ?? "";

        public Base ConvZoneBase(int l, int ci = -1)
        {
            if (ci < 0) ci = CurCol;
            var col = ColonyAt(ci);
            if (col == null || l < 0 || l >= D.Lots.Count) return null;
            var door = D.NearestWalkable(D.LotDoor(l), 3);
            var c = D.LotCenter(l);
            bool inTerr = false;
            if (door.x >= 0)
            {
                int k = Ti(door);
                inTerr = k < _territory.Length && _territory[k] == ci + 1;
                if (ClaimAt(k) == ci)
                {
                    Base nb = null; double nd = 1000000000.0;
                    foreach (var b0 in col.Bases)
                    {
                        if (b0.Done < 1.0) continue;
                        double d0 = V.F(b0.Pos).DistTo(c);
                        if (d0 < nd) { nd = d0; nb = b0; }
                    }
                    if (nb != null) return nb;
                }
            }
            Base best = null; double bd = 1000000000.0;
            foreach (var b in col.Bases)
            {
                if (b.Done < 1.0) continue;
                double d = V.F(b.Pos).DistTo(c);
                double zr = BaseZoneR(b);
                bool ok = d <= zr || (door.x >= 0 && V.F(b.Pos).DistTo(V.F(door)) <= zr);
                if (!ok && door.x >= 0)
                {
                    var eb = EnclBaseAt(Ti(door));
                    if (eb != null && eb.Col == ci && eb.Id == b.Id) ok = true;
                }
                if (!ok && inTerr && d <= ENC_R) ok = true;
                if (ok && d < bd) { bd = d; best = b; }
            }
            return best;
        }

        public string ConvError(int l, int ci = -1)
        {
            string e = ConvPlaceError(l, ci);
            if (e != "") return e;
            if (ConvOf(l) == null && !IsGutted(l)) return GUT_FIRST;
            return "";
        }

        public string ConvPlaceError(int l, int ci = -1)
        {
            if (ci < 0) ci = CurCol;
            if (l < 0 || l >= D.Lots.Count) return "Tap a building.";
            string k = D.LotKind(l);
            if (k == "parking") return "That's a parking lot.";
            if (k == "farm" && D.Lots[l].Landmark == "") return "That's an open field.";
            if (D.Lots[l].Cells < 2) return "Too small to use.";
            if (LotOwnerBase(l) != null) return "That building is part of a camp.";
            if (_factionLotsDirty) RefreshFactionLots();
            if (_factionLots.ContainsKey(l)) return "A faction holds that building.";
            var cv = ConvOf(l);
            if (cv != null && cv.Col != ci) return string.Format("{0} uses that building.", ColonyAt(cv.Col)?.Name ?? "Another colony");
            if (ConvZoneBase(l, ci) == null) return "Only buildings inside one of your camps' safe zones can be converted.";
            return "";
        }

        (Dictionary<string, int> all, Dictionary<int, Dictionary<string, int>> per) ConvCounts(int ci)
        {
            if (_convCacheVer != S.ConvVer)
            {
                _convCache.Clear();
                _convCacheVer = S.ConvVer;
            }
            if (_convCache.TryGetValue(ci, out var cc)) return cc;
            var all = new Dictionary<string, int>();
            var per = new Dictionary<int, Dictionary<string, int>>();
            foreach (var kv in S.Conv)
            {
                var c = kv.Value;
                if (c.Col != ci) continue;
                all[c.Type] = all.GetOr(c.Type, 0) + 1;
                if (!per.ContainsKey(c.Bid)) per[c.Bid] = new Dictionary<string, int>();
                per[c.Bid][c.Type] = per[c.Bid].GetOr(c.Type, 0) + 1;
            }
            var o = (all, per);
            _convCache[ci] = o;
            return o;
        }

        public int ConvN(string type, int bid = -1, int ci = -1)
        {
            if (ci < 0) ci = CurCol;
            if (ci < 0 || S == null) return 0;
            var cc = ConvCounts(ci);
            if (bid < 0) return cc.all.GetOr(type, 0);
            return cc.per.TryGetValue(bid, out var d) ? d.GetOr(type, 0) : 0;
        }

        public List<int> ConvList(int ci = -1)
        {
            if (ci < 0) ci = CurCol;
            return S.Conv.Where(kv => kv.Value.Col == ci).Select(kv => kv.Key).ToList();
        }

        void ConvBalance(Base b, ODict<string, double> r, double agri, double wm)
        {
            int ci = b.Col, bid = b.Id;
            if (ConvN("hospital", bid, ci) + ConvN("farm", bid, ci) + ConvN("water", bid, ci) + ConvN("workshop", bid, ci) + ConvN("garage", bid, ci) + ConvN("armory", bid, ci) == 0) return;
            r["meds"] += 0.5 * ConvN("hospital", bid, ci);
            r["food"] += 5.0 * ConvN("farm", bid, ci) * agri;
            r["water"] += 4.0 * ConvN("water", bid, ci) * wm;
            r["materials"] += 1.5 * ConvN("workshop", bid, ci);
            r["fuel"] += 0.8 * ConvN("garage", bid, ci);
            int na = ConvN("armory", bid, ci);
            if (na > 0 && R("metal") > 3.0)
            {
                r["ammo"] += 1.2 * na; r["metal"] -= 0.5 * na; r["plastic"] -= 0.3 * na;
                if (_balUp.Has("metal")) { _balUp["metal"] += 0.5 * na; _balUp["plastic"] += 0.3 * na; }
            }
        }

        public double RepairCostMult() => 1.0 - Math.Min(0.4, 0.2 * ConvN("workshop"));

        public Dictionary<string, (double score, string why)> ConvNeeds()
        {
            var ppl = AlivePeople();
            int pop = Math.Max(1, ppl.Count);
            var tot = ResZero();
            foreach (var b in C.Bases)
                foreach (var kv in BaseBalance(b)) tot[kv.Key] = tot.Get(kv.Key, 0.0) + kv.Value;
            int beds = C.Bases.Sum(BaseBeds);
            int hurt = ppl.Count(p => p.Hp < 70.0), sick = ppl.Count(p => p.Inf >= 0.0 && p.Known);
            double cap = StorageCap();
            int full = Data.RESOURCES.Count(k => R(k) > cap * 0.8);
            int dmg = RepairQueue().Count;
            return new Dictionary<string, (double, string)>
            {
                ["hospital"] = (1.0 + 6.0 * (hurt + sick * 2) / pop + (R("meds") < 6.0 ? 4.0 : 0.0), hurt + sick > 0 ? "people are hurt" : "meds are low"),
                ["farm"] = (1.5 + (tot["food"] < 0.0 ? 8.0 : 0.0) + (R("food") < pop * 3.0 ? 5.0 : 0.0), "food is running short"),
                ["water"] = (1.0 + (tot["water"] < 0.0 ? 8.0 : 0.0) + (R("water") < pop * 3.0 ? 5.0 : 0.0), "water is running short"),
                ["barracks"] = (0.5 + (ppl.Count > beds ? 10.0 : 0.0) + (ppl.Count > beds * 0.85 ? 4.0 : 0.0), "people are sleeping on the floor"),
                ["storage"] = (0.5 + 6.0 * full, "the stores are overflowing"),
                ["workshop"] = (2.0 + (MatsTotal() < 60.0 ? 5.0 : 0.0) + Math.Min(4.0, dmg * 0.5), dmg > 2 ? "the walls need work" : "materials are low"),
                ["armory"] = (1.5 + (R("ammo") < 15.0 ? 5.0 : 0.0) + Math.Min(3.0, 0.4 * GuardsTotal()), R("ammo") < 15.0 ? "ammo is low" : "the guards need a reloading bench"),
                ["garage"] = (1.0 + (R("fuel") < 5.0 ? 4.0 : 0.0), "fuel is low"),
                ["school"] = (2.5 + (C.Research.Cur != "" ? 1.5 : 0.0), "things are steady enough to teach the kids"),
            };
        }

        public (string type, string why) ConvAiPick(int l)
        {
            var needs = ConvNeeds();
            string aff = Data.CONV_AFFINITY.TryGetValue(D.LotKind(l), out var a) ? a : "";
            string best = "school"; double bs = -1000000000.0;
            foreach (var t in Data.CONV_ORDER)
            {
                double sc = needs[t].score / (1.0 + 0.8 * ConvN(t)) + (t == aff ? 2.5 : 0.0);
                if (sc > bs + 0.0001) { bs = sc; best = t; }
            }
            return (best, needs[best].why);
        }

        string WhereName(int l) => (Data.LABEL_MAJOR.Contains(D.LotKind(l)) || Data.LABEL_MINOR.Contains(D.LotKind(l))) ? D.LotName(l) : TakeName(l);

        void ConvertLot(int l, string type, Base b, bool manual, string reason)
        {
            string was = ConvType(l);
            S.Conv[l] = new Conversion { Type = type, Col = CurCol, Bid = b?.Id ?? -1, Manual = manual ? 1 : 0, T = S.T };
            if (!S.Gut.Has(l)) S.Gut[l] = CurCol + 1;
            if (!Searched(l)) MarkSearched(l, CurCol + 1);
            S.Loot[l] = 0f;
            S.ConvVer++;
            var cd = Data.CONVERSIONS[type];
            string where = WhereName(l);
            if (manual)
            {
                if (was != "") AddLog(string.Format("{0}: the {1} is now {2}.", b?.Name ?? "Camp", Data.CONVERSIONS[was].Name, A(cd.Name)), "good");
                else AddLog(string.Format("{0}: survivors moved into {1} and set it up as {2}.", b?.Name ?? "Camp", where, A(cd.Name)), "good");
            }
            else AddLog(string.Format("Inside {0}'s safe zone, survivors turned {1} into {2} ({3}).", b?.Name ?? "camp", where, A(cd.Name), reason), "good");
            PlaySfx("hammer", D.LotCenter(l));
            EmitChanged();
        }

        void StripLot(int l)
        {
            var tab = D.LotLootTable(l);
            double left = S.Loot[l];
            foreach (var kv in tab) C.Res[kv.Key] = R(kv.Key) + (kv.Value[0] + kv.Value[1]) * 0.25 * left;
            MarkSearched(l, CurCol + 1);
        }

        public string ConvertBuilding(int l, string type)
        {
            if (!Data.CONVERSIONS.ContainsKey(type)) return "Unknown use.";
            if (l < 0 || l >= D.Lots.Count) return "Tap a building.";
            string e = ConvError(l);
            if (e != "") return e;
            if (ConvType(l) == type) return string.Format("It's already a {0}.", Data.CONVERSIONS[type].Name);
            var cc = ConvCost(type);
            if (!CanAfford(cc)) return "Need " + FmtCost(cc) + ".";
            Pay(cc);
            if (ConvOf(l) == null && !Searched(l) && S.Loot[l] > 0.15f) StripLot(l);
            ConvertLot(l, type, ConvZoneBase(l), true, "");
            return "";
        }

        public static Dictionary<string, double> ConvCost(string type) => Data.CONV_COSTS.TryGetValue(type, out var c) ? c : Data.CONV_COST;

        void StepConversions()
        {
            var cv = S.Conv;
            foreach (var kv in cv)
            {
                var c = kv.Value;
                if (c.Col != CurCol || BaseById(c.Bid) != null) continue;
                var nb = NearestOwnBase(D.LotCenter(kv.Key));
                c.Bid = nb?.Id ?? -1;
                S.ConvVer++;
            }
            if (!C.Bases.Any(b => CampAuto(b, "conv"))) return;
            if (S.T < C.Cd.Get("conv", -1000000000.0)) return;
            var srchC = S.SrchC; var srch = S.Srch;
            foreach (var b in C.Bases)
            {
                if (b.Done < 1.0) continue;
                if (!CampAuto(b, "conv")) continue;
                double lim = BaseZoneR(b) + 2.0;
                if (IsEnclosed(b)) lim = ENC_R;
                var cand = new List<(float d, int l)>();
                foreach (var e in BaseLotList(b))
                {
                    if (e.d > lim) break;
                    cand.Add(e);
                }
                if (IsEnclosed(b)) cand.AddRange(EnclLots(b));
                if (b.Main) foreach (int cl in ClaimedLots(CurCol)) cand.Add((0f, cl));
                foreach (var e in cand)
                {
                    int l = e.l;
                    if (cv.Has(l)) continue;
                    bool mine = (srch[l] >= 0f && srchC[l] == CurCol + 1) || S.Loot[l] < 0.25f || IsGutted(l);
                    if (!mine) continue;
                    if (ConvPlaceError(l) != "") continue;
                    if (!IsGutted(l))
                    {
                        if (Gutting(l) || GutsOf(CurCol) >= Data.GUT_MAX) continue;
                        if (GutError(l) == "") BeginGut(l, ConvZoneBase(l), false);
                        return;
                    }
                    var zb = ConvZoneBase(l);
                    var pick = ConvAiPick(l);
                    ConvertLot(l, pick.type, zb, false, pick.why);
                    C.Cd["conv"] = S.T + CONV_EVERY_H;
                    return;
                }
            }
        }

        // ---------------- gutting ----------------
        public bool IsGutted(int l) => S.Gut.Has(l);
        public int GuttedBy(int l) => S.Gut.Get(l, 0) - 1;
        public bool Gutting(int l) => S.Guts.Has(l);
        public double GutLeft(int l) => S.Guts.TryGetValue(l, out var j) ? Math.Max(0.0, j.Left) : 0.0;
        public int GutsOf(int ci) => S.Guts.Count(kv => kv.Value.Col == ci);

        public string GutError(int l, int ci = -1)
        {
            if (ci < 0) ci = CurCol;
            if (l < 0 || l >= D.Lots.Count) return "Tap a building.";
            string k = D.LotKind(l);
            if (k == "parking") return "That's a parking lot.";
            if (k == "farm" && D.Lots[l].Landmark == "") return "That's an open field.";
            if (D.Lots[l].Cells < 2) return "Too small to bother.";
            if (IsGutted(l)) return "Already gutted.";
            if (Gutting(l)) return "A crew is already gutting it.";
            if (LotOwnerBase(l) != null) return "That building is part of a camp.";
            if (ConvOf(l) != null) return "That building is in use.";
            if (_factionLotsDirty) RefreshFactionLots();
            if (_factionLots.ContainsKey(l)) return "A faction holds that building.";
            if (GutsOf(ci) >= Data.GUT_MAX) return string.Format("Your crews are already gutting {0} buildings.", Data.GUT_MAX);
            if (!GutReach(l, ci)) return string.Format("Too far out. Gut buildings in your safe zone, on claimed land or within {0} tiles of a camp.", (int)Data.GUT_R);
            return "";
        }

        bool GutReach(int l, int ci)
        {
            if (ConvZoneBase(l, ci) != null) return true;
            var c = D.LotCenter(l);
            foreach (var b in ColonyAt(ci)?.Bases ?? new List<Base>())
                if (b.Done >= 1.0 && V.F(b.Pos).DistTo(c) <= Data.GUT_R) return true;
            return false;
        }

        public string StartGut(int l)
        {
            string e = GutError(l);
            if (e != "") return e;
            var nb = ConvZoneBase(l) ?? NearestOwnBase(D.LotCenter(l));
            BeginGut(l, nb, true);
            return "";
        }

        void BeginGut(int l, Base b, bool manual)
        {
            S.Guts[l] = new GutJob { Col = CurCol, Left = Data.GUT_SEC, Total = Data.GUT_SEC, Bid = b?.Id ?? -1, Manual = manual ? 1 : 0 };
            if (manual) AddLog(string.Format("{0}: a crew is gutting {1}.", b?.Name ?? "Camp", WhereName(l)), "info");
            PlaySfx("hammer", D.LotCenter(l));
            EmitChanged();
        }

        void StepGuts(double rs)
        {
            var done = new List<int>();
            foreach (var kv in S.Guts)
            {
                kv.Value.Left -= rs;
                if (kv.Value.Left <= 0.0) done.Add(kv.Key);
            }
            foreach (int l in done) FinishGut(l);
        }

        void FinishGut(int l)
        {
            var j = S.Guts[l];
            S.Guts.Remove(l);
            int ci = j.Col;
            if (ci < 0 || ci >= S.Cols.Count || !ColAlive(S.Cols[ci])) return;
            int prev = CurCol, plog = LogCol;
            Use(ci); LogCol = ci;
            if (!Searched(l) && S.Loot[l] > 0.15f) StripLot(l);
            var y = D.GutYield(l);
            foreach (var kv in y) C.Res[kv.Key] = R(kv.Key) + kv.Value;
            S.Gut[l] = ci + 1;
            S.Loot[l] = 0f;
            if (!Searched(l)) MarkSearched(l, ci + 1);
            GutCount++;
            if (j.Manual == 1)
                AddLog(string.Format("Gutted {0} down to the shell: {1}.{2}", WhereName(l), FmtCost(y), ConvPlaceError(l) == "" ? " It can be turned into a camp building now." : ""), "good");
            PlaySfx("hammer", D.LotCenter(l));
            LogCol = plog;
            Use(prev >= 0 ? prev : ViewCol);
            EmitChanged();
        }
    }
}
