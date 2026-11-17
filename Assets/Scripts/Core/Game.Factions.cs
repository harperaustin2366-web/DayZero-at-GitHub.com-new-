// game.gd: other survivor camps / factions (placement, relations, trade, scavenging, ambushes,
// raids on your camps, gifts, pacts), automatic trading, night raids by the dead, and the war system.
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DayZero.Core
{
    public sealed partial class Game
    {
        // ---------------- camps ----------------
        readonly Dictionary<int, int> _factionLots = new Dictionary<int, int>();
        bool _factionLotsDirty = true;
        public int TradeVisits;

        static JObject CampsData => (JObject)D.Camps["camps"];

        double CampRelationFromData(string from, string to)
        {
            var rel = CampsData[from]?["relations"] as JObject;
            return rel?[to] != null ? (double)rel[to] : 0.0;
        }

        void PlaceCamps(Vector2Int player)
        {
            var placed = new List<Vector2Int>();
            int fi = 0;
            foreach (var kv in CampsData)
            {
                string cid = kv.Key;
                var c = (JObject)kv.Value;
                string home = (string)c["district"] ?? cid;
                int best = -1;
                for (int pass = 0; pass < 2; pass++)
                {
                    var cand = new List<int>();
                    for (int l = 0; l < D.Lots.Count; l++)
                    {
                        var L = D.Lots[l];
                        if (pass == 0 && D.Districts[L.District].Id != home) continue;
                        if (L.Cells < 6 || D.LotTakeable(l) != "") continue;
                        if (L.Landmark != "") continue;
                        var door = V.F(D.LotDoor(l));
                        bool ok = true;
                        foreach (var b in AllBases()) if (door.DistTo(V.F(b.Pos)) < 45.0f) ok = false;
                        if (door.DistTo(V.F(player)) < 45.0f) ok = false;
                        if (door.DistTo(V.F(S.Zcorner)) < 50.0f) ok = false;
                        foreach (var q in placed) if (door.DistTo(V.F(q)) < 40.0f) ok = false;
                        if (ok && pass == 1 && Rng.Randf() > 0.02f) continue;
                        if (ok) cand.Add(l);
                    }
                    if (cand.Count > 0)
                    {
                        best = cand[Rng.RandiRange(0, cand.Count - 1)];
                        break;
                    }
                }
                if (best < 0) continue;
                string att = (string)c["attitude"];
                var e = new Camp
                {
                    Id = cid, Name = (string)c["name"], Leader = (string)c["leader"], Pop = (int)(double)c["pop"], Att = att,
                    Rel = (double)D.Camps["attitudes"][att]["base"], Lot = best, Pos = D.LotDoor(best), Alive = true,
                    Needs = c["needs"].ToObject<List<string>>(), Surplus = c["surplus"].ToObject<List<string>>(), Desc = (string)c["desc"],
                };
                S.Camps.Add(e);
                FactionInit(e, fi);
                placed.Add(e.Pos);
                fi++;
            }
            _factionLotsDirty = true;
        }

        public Camp CampById(string cid) => S.Camps.FirstOrDefault(c => c.Id == cid);

        public string CampAttitude(Camp c)
        {
            if (c.Att == "Raiders") return "Raiders";
            var th = D.Camps["thresholds"];
            double rv = CampRel(c);
            if (rv >= (double)th["friendly"]) return "Friendly";
            if (rv <= (double)th["hostile"]) return "Hostile";
            return "Neutral";
        }

        public double CampPrice(Camp c, string res)
        {
            var prices = (JObject)D.Camps["prices"];
            double p = prices[res] != null ? (double)prices[res] : 1.2;
            if (CampLists(c, "needs", res)) p *= (double)D.Camps["need_mult"];
            if (CampLists(c, "surplus", res)) p *= (double)D.Camps["surplus_mult"];
            return p;
        }

        public static bool CampLists(Camp c, string which, string res)
        {
            var arr = c.List(which);
            return arr.Contains(res) || (Data.MATS.Contains(res) && arr.Contains("materials"));
        }

        public string CampTrade(string cid, string give, string want)
        {
            var c = CampById(cid);
            if (c == null || !c.Alive) return "They're gone.";
            if (CampAttitude(c) == "Hostile") return string.Format("{0} won't trade with you.", c.Name);
            if (R(give) < 10.0) return string.Format("You need 10 {0}.", Lower(ResLabel(give)));
            double value = 10.0 * CampPrice(c, give);
            double amount = Math.Floor(value / CampPrice(c, want) * (CampAttitude(c) == "Raiders" ? 0.85 : 1.0) * (1.0 + Rx("trade_rate")));
            if (amount < 1.0) return "Not worth it to them.";
            C.Res[give] = R(give) - 10.0;
            C.Res[want] = R(want) + amount;
            AddRel(c, 1.0 * (1.0 + Rx("rel_gain")));
            AddLog(string.Format("Traded 10 {0} to {1} for {2} {3}.", Lower(ResLabel(give)), c.Name, (int)amount, Lower(ResLabel(want))), "good");
            EmitChanged();
            return "";
        }

        // ---------------- automatic trade ----------------
        public const double TRADE_EVERY = 6.0;
        public const double TRADE_LOT = 10.0;
        public const int TRADE_LOTS_MAX = 6;
        public const int TRADE_LVL_MAX = 990;
        public const int GEAR_LOTS_MAX = 3;
        public static readonly Dictionary<string, int> TRADE_STEP = new Dictionary<string, int> { ["food"] = 10, ["water"] = 10, ["ammo"] = 5, ["meds"] = 5, ["fuel"] = 5, ["wood"] = 10, ["nails"] = 5, ["metal"] = 10, ["plastic"] = 5, ["cloth"] = 5, ["wire"] = 5, ["drywall"] = 5, ["elec"] = 5, ["furniture"] = 5 };
        public static readonly Dictionary<string, int> TRADE_LVL0 = new Dictionary<string, int> { ["food"] = 120, ["water"] = 120, ["ammo"] = 40, ["meds"] = 20, ["fuel"] = 30, ["wood"] = 40, ["nails"] = 20, ["metal"] = 30, ["plastic"] = 20, ["cloth"] = 20, ["wire"] = 15, ["drywall"] = 15, ["elec"] = 10, ["furniture"] = 15 };
        public static readonly string[] TRADE_MODES = { "ignore", "import", "export" };

        static TradeCfg TradeNew()
        {
            var t = new TradeCfg();
            foreach (var r in Data.RESOURCES) t.Goods[r] = new TradeGood { M = "ignore", Lvl = TRADE_LVL0.GetOr(r, 50) };
            foreach (var r in Data.GEAR_TRADE) t.Goods[r] = new TradeGood { M = "ignore", Lvl = 2 };
            return t;
        }

        public TradeCfg TradeCfgOf(int ci = -1)
        {
            if (ci < 0) ci = CurCol;
            if (S == null || ci < 0 || ci >= S.Cols.Count) return TradeNew();
            var c = S.Cols[ci];
            if (c.Trade == null) c.Trade = TradeNew();
            var t = c.Trade;
            if (t.Goods == null) t.Goods = TradeNew().Goods;
            foreach (var r in Data.RESOURCES) if (!t.Goods.Has(r)) t.Goods[r] = new TradeGood { M = "ignore", Lvl = TRADE_LVL0.GetOr(r, 50) };
            foreach (var r in Data.GEAR_TRADE) if (!t.Goods.Has(r)) t.Goods[r] = new TradeGood { M = "ignore", Lvl = 2 };
            return t;
        }

        public string SetTrade(string res, string mode, int lvl, int ci = -1)
        {
            if (!(Data.RESOURCES.Contains(res) || Data.GEAR_TRADE.Contains(res))) return "Unknown good.";
            if (!TRADE_MODES.Contains(mode)) return "Pick import, export or ignore.";
            var g = TradeCfgOf(ci).Goods[res];
            g.M = mode;
            g.Lvl = V.Clampi(lvl, 0, TRADE_LVL_MAX);
            EmitChanged();
            return "";
        }

        public List<Camp> TradePartners() => S.Camps.Where(c => c.Alive && CampAttitude(c) != "Hostile").ToList();

        public Quote TradeQuote(string res, bool selling)
        {
            double best = -1.0; Camp bc = null;
            foreach (var c in TradePartners())
            {
                double mult = (CampAttitude(c) == "Raiders" ? 0.85 : 1.0) * (1.0 + Rx("trade_rate"));
                double p = selling ? CampPrice(c, res) : CampPrice(c, res) / mult;
                if (bc == null || (selling && p > best) || (!selling && p < best)) { best = p; bc = c; }
            }
            return new Quote { Price = best, Camp = bc };
        }

        public static string GoodLabel(string r) => Data.RES_LABEL.TryGetValue(r, out var l) ? l : (Data.GearDefOf(r)?.Name ?? r);

        void StepTrade()
        {
            var t = TradeCfgOf();
            if (S.T < t.Next) return;
            t.Next = S.T + TRADE_EVERY;
            bool any = Data.RESOURCES.Concat(Data.GEAR_TRADE).Any(r => t.Goods[r].M != "ignore");
            if (!any) return;
            if (TradePartners().Count == 0)
            {
                t.Last = "No traders will come: every camp around is gone or hostile.";
                return;
            }
            var sold = new ODict<string, double>(); var bought = new ODict<string, double>(); var met = new ODict<string, Camp>();
            foreach (var r in Data.RESOURCES)
            {
                var g = t.Goods[r];
                if (g.M != "export") continue;
                int lots = 0;
                while (lots < TRADE_LOTS_MAX && R(r) - TRADE_LOT >= g.Lvl - 0.001)
                {
                    var q = TradeQuote(r, true);
                    if (q.Camp == null) break;
                    C.Res[r] = R(r) - TRADE_LOT;
                    t.Credit += TRADE_LOT * q.Price;
                    sold[r] = sold.Get(r, 0.0) + TRADE_LOT;
                    met[q.Camp.Id] = q.Camp;
                    lots++;
                }
            }
            var imp = Data.RESOURCES.Where(r => t.Goods[r].M == "import" && R(r) < t.Goods[r].Lvl).ToList();
            Gd.StableSort(imp, (a, b) => (R(a) / Math.Max(1.0, t.Goods[a].Lvl)).CompareTo(R(b) / Math.Max(1.0, t.Goods[b].Lvl)));
            var shortL = new List<string>();
            foreach (var r in imp)
            {
                var g3 = t.Goods[r];
                int lots2 = 0;
                while (lots2 < TRADE_LOTS_MAX && R(r) < g3.Lvl - 0.001)
                {
                    var q2 = TradeQuote(r, false);
                    if (q2.Camp == null) break;
                    double want = Math.Min(TRADE_LOT, Math.Ceiling(g3.Lvl - R(r)));
                    double unit = q2.Price;
                    double amt = Math.Min(want, Math.Floor(t.Credit / unit + 0.0001));
                    if (amt < 1.0) { shortL.Add(r); break; }
                    t.Credit = Math.Max(0.0, t.Credit - amt * unit);
                    C.Res[r] = R(r) + amt;
                    bought[r] = bought.Get(r, 0.0) + amt;
                    met[q2.Camp.Id] = q2.Camp;
                    lots2++;
                }
            }
            TradeGear(t, sold, bought, met, shortL);
            foreach (var kv in met) AddRel(kv.Value, 1.0 * (1.0 + Rx("rel_gain")));
            TradeVisits++;
            if (sold.Count == 0 && bought.Count == 0)
            {
                t.Last = string.Format("Traders came by; nothing to sell{0}.", shortL.Count > 0 ? " and no credit for " + string.Join(", ", shortL.Select(x => Lower(GoodLabel(x)))) : "");
                return;
            }
            var names = met.Select(kv => kv.Value.Name).ToList();
            var parts = new List<string>();
            if (sold.Count > 0) parts.Add("sold " + string.Join(", ", sold.Select(kv => string.Format("{0} {1}", (int)kv.Value, Lower(GoodLabel(kv.Key))))));
            if (bought.Count > 0) parts.Add("bought " + string.Join(", ", bought.Select(kv => string.Format("{0} {1}", (int)kv.Value, Lower(GoodLabel(kv.Key))))));
            t.Last = string.Format("Traders ({0}) {1}.", string.Join(", ", names), string.Join("; ", parts));
            if (shortL.Count > 0) t.Last += " Not enough credit for " + string.Join(", ", shortL.Select(x => Lower(GoodLabel(x)))) + ".";
            AddLog("Traded: " + t.Last, "info");
            EmitChanged();
        }

        public Quote GearQuote(string r, bool selling)
        {
            Camp bc = null; double best = -1.0;
            double val = Data.GearDefOf(r)?.Val ?? 10.0;
            foreach (var c in TradePartners())
            {
                double mult = (CampAttitude(c) == "Raiders" ? 0.85 : 1.0) * (1.0 + Rx("trade_rate"));
                double p = selling ? val * 0.8 : val * 1.15 / mult;
                if (bc == null || (selling && p > best) || (!selling && p < best)) { best = p; bc = c; }
            }
            return new Quote { Price = best, Camp = bc };
        }

        void TradeGear(TradeCfg t, ODict<string, double> sold, ODict<string, double> bought, ODict<string, Camp> met, List<string> shortL)
        {
            foreach (var r in Data.GEAR_TRADE)
            {
                var g = t.Goods[r];
                if (g.M != "export") continue;
                int n = 0;
                while (n < GEAR_LOTS_MAX && GearFree(r) > 0 && GearTotal(r) - 1 >= g.Lvl)
                {
                    var q = GearQuote(r, true);
                    if (q.Camp == null) break;
                    GearAdd(r, -1); t.Credit += q.Price;
                    sold[r] = sold.Get(r, 0.0) + 1.0; met[q.Camp.Id] = q.Camp; n++;
                }
            }
            foreach (var r in Data.GEAR_TRADE)
            {
                var g2 = t.Goods[r];
                if (g2.M != "import") continue;
                int n2 = 0;
                while (n2 < GEAR_LOTS_MAX && GearTotal(r) < g2.Lvl)
                {
                    var q2 = GearQuote(r, false);
                    if (q2.Camp == null) break;
                    if (t.Credit < q2.Price) { shortL.Add(r); break; }
                    t.Credit -= q2.Price; GearAdd(r, 1);
                    bought[r] = bought.Get(r, 0.0) + 1.0; met[q2.Camp.Id] = q2.Camp; n2++;
                }
            }
        }

        public string CampGift(string cid)
        {
            var c = CampById(cid);
            if (c == null || !c.Alive) return "They're gone.";
            if (R("food") < 15.0) return "You need 15 food.";
            C.Res["food"] = R("food") - 15.0;
            AddRel(c, 9.0 * (1.0 + Rx("rel_gain")));
            AddLog(string.Format("Sent 15 food to {0} as a gift.", c.Name), "info");
            EmitChanged();
            return "";
        }

        public string CampRecruit(string cid)
        {
            var c = CampById(cid);
            if (c == null || !c.Alive) return "They're gone.";
            if (CampAttitude(c) != "Friendly") return "They need to trust you first (Friendly).";
            if (c.Pop < 6) return "They can't spare anyone.";
            if (R("food") < 20.0) return "Costs 20 food.";
            if (BedsFree() <= 0) return NO_BEDS_MSG;
            C.Res["food"] = R("food") - 20.0;
            c.Pop -= 2;
            AddRel(c, -6.0);
            var mb = MainBase();
            var arr = new List<Person>();
            int n = BedRoom(2 + (int)Rx("recruit_n"));
            for (int i = 0; i < n; i++)
            {
                var p = AddPerson(D.RandomJob(Rng), mb.Id, V.F(c.Pos).Add(V.HALF));
                Go(p, mb.Pos, "home");
                arr.Add(p);
            }
            AddLog(string.Format("Two people from {0} are walking over to join you.", c.Name), "good");
            PlaceArrivals(arr, mb.Id);
            EmitChanged();
            return "";
        }

        public string CampPact(string cid)
        {
            if (!HasUnlock("pact")) return string.Format("Research {0} first.", UnlockSourceName("pact"));
            var c = CampById(cid);
            if (c == null || !c.Alive) return "They're gone.";
            if (CampAttitude(c) != "Friendly") return "They need to be Friendly first.";
            if (C.Pacts.Contains(cid)) return "You already have a pact.";
            if (R("food") < 30.0) return "A pact costs 30 food.";
            C.Res["food"] = R("food") - 30.0;
            C.Pacts.Add(cid);
            AddLog(string.Format("Pact signed with {0}: they'll harass hordes heading for your camps and share supplies.", c.Name), "major");
            EmitChanged();
            return "";
        }

        void StepCamps()
        {
            foreach (var c in S.Camps)
            {
                if (!c.Alive) continue;
                double tough = 1.0 - Math.Min(0.6, c.Str / 100.0);
                if ((InfAt(c.Pos) > 0.85 && Rng.Randf() < 0.02 * tough) || (S.Phase == "fall" && Rng.Randf() < 0.01 * tough))
                {
                    c.Alive = false;
                    C?.AddStat("dead", c.Pop);
                    AddLog(string.Format("{0} went silent. The dead took {1}.", c.Name, D.DistrictAt(c.Pos).Name), "loss");
                    for (int i = 0; i < Math.Min(c.Pop, 12); i++) SpawnZombie(Ti(c.Pos), 0);
                    _factionLotsDirty = true;
                }
            }
            // GDScript kept a disabled ("if false and ...") legacy raider block here; not ported.
        }

        // ---------------- factions ----------------
        void RefreshFactionLots()
        {
            _factionLots.Clear();
            for (int i = 0; i < S.Camps.Count; i++)
            {
                var c = S.Camps[i];
                if (!c.Alive) continue;
                foreach (int l in c.Claim ?? new List<int> { c.Lot }) _factionLots[l] = i;
            }
            _factionLotsDirty = false;
        }

        public Camp FactionOfLot(int l)
        {
            if (S == null) return null;
            if (_factionLotsDirty) RefreshFactionLots();
            return _factionLots.TryGetValue(l, out int i) ? S.Camps[i] : null;
        }

        public static Color FactionColor(Camp c) => Gd.Hex(c.Color ?? "#c9b26a");

        public double CampRel(Camp c, int ci = -1)
        {
            if (ci < 0) ci = Math.Max(0, CurCol);
            var r = c.Rels;
            if (r != null && ci < r.Count) return r[ci];
            return c.Rel;
        }

        public void AddRel(Camp c, double v, int ci = -1)
        {
            if (ci < 0) ci = Math.Max(0, CurCol);
            if (c.Rels == null) c.Rels = new List<double> { c.Rel, c.Rel, c.Rel, c.Rel };
            while (c.Rels.Count <= ci) c.Rels.Add(c.Rel);
            c.Rels[ci] = V.Clamp(c.Rels[ci] + v, -100.0, 100.0);
        }

        public bool FactionHostile(Camp c) => c.Alive && (c.Att == "Raiders" || CampAttitude(c) == "Hostile") && S.T >= c.Truce.Get(CurCol.ToString(), -1.0);
        public static double FactionRadius(Camp c) => Math.Min(34.0, 16.0 + c.Pop * 0.5);

        static readonly string[] FACTION_COLORS = { "#d9c46a", "#6ab0d9", "#d96a9a", "#e05040", "#a070e0", "#e0e0e0", "#70d0a0" };

        void FactionInit(Camp c, int fi)
        {
            var cd = CampsData[c.Id] as JObject;
            c.Fi = fi;
            c.Color = (string)cd?["color"] ?? FACTION_COLORS[fi % 7];
            c.Str = cd?["strength"] != null ? (double)cd["strength"] : 30.0;
            c.Stock = new ODict<string, double>();
            if (cd?["stock"] is JObject st) foreach (var kv in st) c.Stock[kv.Key] = (double)kv.Value;
            else c.Stock["food"] = 60.0;
            c.Rels = new List<double> { c.Rel, c.Rel, c.Rel, c.Rel };
            c.Claim = new List<int> { c.Lot };
            c.NextScav = S.T + Rng.RandfRange(2.0, 8.0);
            c.NextGrow = S.T + Rng.RandfRange(36.0, 60.0);
            c.NextGift = S.T + Rng.RandfRange(48.0, 90.0);
            c.NextRaid = S.T + Rng.RandfRange(80.0, 130.0);
            c.NextAmbush = S.T + Rng.RandfRange(20.0, 40.0);
            c.NextHelp = 0.0;
            c.Truce = new ODict<string, double>();
            c.Searched = 0;
            FactionGrowClaim(c);
        }

        void FactionGrowClaim(Camp c)
        {
            int want = 2 + (int)(c.Pop / 9.0);
            var claim = c.Claim;
            if (claim.Count >= Math.Min(want, 6)) return;
            if (_factionLotsDirty) RefreshFactionLots();
            var c0 = D.LotCenter(c.Lot);
            int best = -1; double bd = 9.0;
            for (int dy = -9; dy < 10; dy++)
                for (int dx = -9; dx < 10; dx++)
                {
                    int l = D.BuildingAt(new Vector2Int((int)c0.x + dx, (int)c0.y + dy));
                    if (l < 0 || _factionLots.ContainsKey(l) || LotOwnerBase(l) != null) continue;
                    if (D.LotTakeable(l) != "") continue;
                    double d = D.LotCenter(l).DistTo(c0);
                    if (d < bd) { bd = d; best = l; }
                }
            if (best >= 0)
            {
                claim.Add(best);
                S.Loot[best] = 0f;
                _factionLotsDirty = true;
            }
        }

        void StepFactions()
        {
            if (S.Camps.Count == 0 || S.Phase == "pick") return;
            if (_factionLotsDirty) RefreshFactionLots();
            foreach (var c in S.Camps)
            {
                if (!c.Alive || !c.Fi.HasValue) continue;
                var st = c.Stock;
                st["food"] = Math.Max(-20.0, st.Get("food", 0.0) - c.Pop * 0.012);
                if (S.T >= c.NextScav) FactionScavenge(c);
                if (S.T >= c.NextGrow)
                {
                    c.NextGrow = S.T + Rng.RandfRange(40.0, 60.0);
                    if (st["food"] > c.Pop * 1.2 && S.Phase != "fall")
                    {
                        c.Pop = c.Pop + 1 + (Rng.Randf() < 0.4f ? 1 : 0);
                        c.Str += 1.5;
                        st["food"] = st["food"] - 6.0;
                        FactionGrowClaim(c);
                    }
                    else if (st["food"] <= 0.0 && c.Pop > 4)
                    {
                        c.Pop--;
                        c.Str = Math.Max(4.0, c.Str - 1.0);
                    }
                }
                for (int ci = 0; ci < S.Cols.Count; ci++)
                {
                    if (!ColAlive(S.Cols[ci])) continue;
                    Use(ci);
                    LogCol = ci;
                    if (FactionHostile(c))
                    {
                        FactionAmbush(c);
                        FactionRaid(c);
                    }
                    else if (CampAttitude(c) == "Friendly") FactionFriendly(c);
                    LogCol = -1;
                }
                Use(ViewCol);
            }
        }

        void FactionScavenge(Camp c)
        {
            c.NextScav = S.T + Rng.RandfRange(5.0, 8.0);
            if (IsNight()) return;
            int n = 1 + (int)(c.Pop / 10.0);
            double r = FactionRadius(c);
            var cp = V.F(c.Pos);
            var cand = new List<int>();
            for (int t = 0; t < 40; t++)
            {
                if (cand.Count >= n) break;
                double ang = Rng.Randf() * V.TAU;
                var q = V.I(cp.Add(V.F(Math.Cos(ang), Math.Sin(ang)).Mul(Rng.RandfRange(4.0, r))));
                int l = D.BuildingAt(q);
                if (l < 0 || Searched(l) || S.Loot[l] < 0.2f || _factionLots.ContainsKey(l) || cand.Contains(l)) continue;
                if (LotOwnerBase(l) != null || _claimed.ContainsKey(l)) continue;
                if (InfAt(D.LotDoor(l)) > 0.8) continue;
                cand.Add(l);
            }
            var st = c.Stock;
            foreach (int l in cand)
            {
                var tab = D.LotLootTable(l);
                foreach (var kv in tab)
                {
                    double gain = (kv.Value[0] + kv.Value[1]) * 0.5 * S.Loot[l] * 0.7;
                    st[kv.Key] = st.Get(kv.Key, 0.0) + gain;
                    VassalCampShare(c, kv.Key, gain);
                }
                MarkSearched(l, 100 + (c.Fi ?? 0));
                c.Searched++;
            }
            if (InfAt(c.Pos) > 0.5 && Rng.Randf() < 0.05f) c.Pop = Math.Max(2, c.Pop - 1);
        }

        void FactionAmbush(Camp c)
        {
            if (S.T < c.NextAmbush) return;
            double r = FactionRadius(c) + (c.Att == "Raiders" ? 34.0 : 24.0);
            var cp = V.F(c.Pos);
            double deter = Math.Min(0.7, Rx("raid_deter") / 60.0);
            foreach (var p in C.People.ToList())
            {
                if (!(p.St == "move" || p.St == "work") || p.Sq > 0) continue;
                if (p.Pos.DistTo(cp) > r) continue;
                double chance = (c.Att == "Raiders" ? 0.06 : 0.035) * (1.0 - deter);
                if (Rng.Randf() > chance) continue;
                c.NextAmbush = S.T + Rng.RandfRange(10.0, 20.0);
                var carry = p.Carry;
                if (carry.Count > 0 && Rng.Randf() < 0.6f)
                {
                    foreach (var kv in carry) c.Stock[kv.Key] = c.Stock.Get(kv.Key, 0.0) + kv.Value;
                    p.Carry = new ODict<string, double>();
                    AddLog(string.Format("{0} jumped {1} near {2} and took everything they were carrying.", c.Name, p.Name, D.DistrictAt(V.I(p.Pos)).Name), "loss");
                }
                else
                {
                    double win = p.Job == "Soldier" ? 0.7 : p.Job == "Cop" ? 0.65 : p.Job == "Scavenger" ? 0.45 : 0.35;
                    if (p.Wpn != "") win += 0.1 + (Data.GunHit(WeaponOf(p)) - 0.68) * 0.3;
                    win += ArmorOf(p) * 0.15;
                    if (Rng.Randf() < win)
                    {
                        c.Str = Math.Max(3.0, c.Str - 1.0);
                        AddLog(string.Format("{0} fought off a {1} ambush near {2}.", p.Name, c.Name, D.DistrictAt(V.I(p.Pos)).Name), "info");
                    }
                    else
                    {
                        p.Hp -= 35.0;
                        if (p.Hp <= 0.0 || Rng.Randf() < 0.15f) KillPerson(p, string.Format("was shot by {0}", c.Name));
                        else
                        {
                            AddLog(string.Format("{0} was hurt in a {1} ambush and is limping home.", p.Name, c.Name), "loss");
                            GoHome(p);
                        }
                    }
                }
                PlaySfx("gunshot", p.Pos);
                return;
            }
        }

        void FactionRaid(Camp c)
        {
            if (S.T < c.NextRaid) return;
            c.NextRaid = S.T + Rng.RandfRange(70.0, 120.0) * (c.Att == "Raiders" ? 1.0 : 1.4);
            Base target = null; double bd = 160.0;
            foreach (var b in C.Bases)
            {
                if (b.Done < 1.0) continue;
                double d = V.F(b.Pos).DistTo(V.F(c.Pos));
                if (d < bd) { bd = d; target = b; }
            }
            if (target == null) return;
            double force = c.Str * (c.Att == "Raiders" ? 0.6 : 0.45);
            if (Defense(target) + Rx("raid_deter") > 18.0 + force)
            {
                AddLog(string.Format("{0} sized up {1} and thought better of it.", c.Name, target.Name), "info");
                return;
            }
            var lost = new ODict<string, double>();
            foreach (var k in new[] { "food", "ammo" })
            {
                double v = Math.Floor(R(k) * 0.12);
                C.Res[k] = R(k) - v; lost[k] = v;
                c.Stock[k] = c.Stock.Get(k, 0.0) + v;
            }
            double mv = Math.Floor(TakeMats(MatsTotal() * 0.12));
            if (mv > 0.0)
            {
                lost["materials"] = mv;
                c.Stock["materials"] = c.Stock.Get("materials", 0.0) + mv;
            }
            int hit = 0;
            var bp = V.F(target.Pos);
            foreach (int k in S.Fences.KeyList())
            {
                if (hit >= 8) break;
                var f = S.Fences[k];
                if (f.Col != CurCol) continue;
                if (V.F(Tp(k)).DistTo(bp) > 30.0f) continue;
                f.Hp = Math.Max(1.0, f.Hp - Rng.RandfRange(40.0, 90.0));
                hit++;
            }
            AddLog(string.Format("{0} raided {1} and took {2}{3}.", c.Name, target.Name, FmtCost(lost), hit > 0 ? string.Format(" They tore at {0} fence sections.", hit) : ""), "loss");
            PlaySfx("alarm", V.F(target.Pos));
        }

        void FactionFriendly(Camp c)
        {
            if (S.T >= c.NextGift)
            {
                c.NextGift = S.T + Rng.RandfRange(60.0, 100.0);
                string res = c.Surplus.Count > 0 ? c.Surplus[0] : "food";
                double amt = Rng.RandfRange(6.0, 12.0) * (1.0 + Rx("trade_rate") * 2.0);
                AddRes(res, amt);
                AddLog(string.Format("{0} sent over a cart with {1} {2}.", c.Name, (int)amt, Lower(ResLabel(res))), "good");
            }
            if (C.Pacts.Contains(c.Id) && S.Phase == "fall" && S.T >= c.NextHelp && c.Pop > 8)
            {
                c.NextHelp = S.T + 120.0;
                var mb = MainBase();
                if (mb == null) return;
                if (BedsFree() <= 0) return;
                c.Pop -= 2;
                var aarr = new List<Person>();
                int n = BedRoom(2);
                for (int i = 0; i < n; i++)
                {
                    var np = AddPerson(i == 0 ? "Soldier" : "Cop", mb.Id, V.F(c.Pos).Add(V.HALF));
                    Go(np, mb.Pos, "home");
                    aarr.Add(np);
                }
                AddLog(string.Format("{0} sent two fighters to help you hold the line.", c.Name), "good");
                PlaceArrivals(aarr, mb.Id);
            }
        }

        public string CampParley(string cid)
        {
            var c = CampById(cid);
            if (c == null || !c.Alive) return "They're gone.";
            if (!HasUnlock("parley")) return string.Format("Research {0} first.", UnlockSourceName("parley"));
            if (c.Att == "Raiders") return "Raiders don't talk. Pay them off or fight them.";
            if (S.T < C.Cd.Get("parley_" + cid, -1000000000.0)) return "They need time. Try again later.";
            if (R("food") < 20.0 || R("meds") < 6.0) return "Bring 20 food and 6 meds.";
            C.Res["food"] = R("food") - 20.0; C.Res["meds"] = R("meds") - 6.0;
            C.Cd["parley_" + cid] = S.T + 48.0;
            AddRel(c, 18.0 * (1.0 + Rx("rel_gain")));
            AddLog(string.Format("Talks with {0} went well. Relations are now {1}.", c.Name, CampAttitude(c)), "good");
            EmitChanged();
            return "";
        }

        public string CampTribute(string cid)
        {
            var c = CampById(cid);
            if (c == null || !c.Alive) return "They're gone.";
            if (!FactionHostile(c)) return "They aren't threatening you.";
            if (R("food") < 25.0 || R("ammo") < 10.0) return "They want 25 food and 10 ammo.";
            C.Res["food"] = R("food") - 25.0; C.Res["ammo"] = R("ammo") - 10.0;
            c.Stock["food"] = c.Stock.Get("food", 0.0) + 25.0;
            c.Truce[CurCol.ToString()] = S.T + 120.0;
            AddLog(string.Format("Paid {0} off. They'll leave you alone for 5 days.", c.Name), "info");
            EmitChanged();
            return "";
        }

        // ---------------- night raids by the dead ----------------
        public const double NIGHT_ATTACK_H = 21.0;
        public const double RAID_WARN_H = 4.0;
        public const double RAID_TRAVEL_H = 4.0;
        public const double RAID_FAR_MIN = 60.0;
        public const double RAID_FAR_MAX = 120.0;
        public const int RAID_MAJOR = 15;
        public const int RAID_END_H = 6;
        public const int RAID_CAP = 140;
        static readonly double[] RAID_DIFF = { 0.75, 1.0, 1.35 };
        public const double Z_RAID = 24.0;
        public const double Z_RAID_HIT = 6.0 / Z_RAID;
        bool _flowBuilt;

        static Raid RaidNew() => new Raid();

        public int RaidSize(int ci = -1)
        {
            if (ci < 0) ci = CurCol;
            var c = ColonyAt(ci);
            double d = Math.Max(0.0, (S.T - c.StartT) / 24.0);
            // v0.12.0a: a visibly growing horde (normal: day 1 ~5, day 10 ~28, day 20 ~67, day 30 ~120)
            double n = (PhaseARules ? 3.0 + 1.7 * d + 0.075 * d * d : 2.0 + 1.6 * d + 0.06 * d * d) * RAID_DIFF[V.Clampi(S.Difficulty, 0, 2)];
            foreach (var cid in c.Pacts)
            {
                var cp = CampById(cid);
                if (cp != null && cp.Alive) { n *= 0.8; break; }
            }
            if (ExpansionRules) n *= ExpRaidMul();   // Expansion: summer x1.05, winter x0.85
            return V.Clampi((int)Gd.Round(n), 3, RAID_CAP);
        }

        public bool RaidOnAny() => S.Cols.Any(c => ColAlive(c) && c.Nraid.On);

        public Raid RaidInfo(int ci = -1)
        {
            if (ci < 0) ci = ViewCol;
            var R0 = ColonyAt(ci)?.Nraid;
            if (R0 == null || (R0.NextT < 0.0 && !R0.On)) return null;
            return R0;
        }

        static readonly string[] DIR_NAMES = { "east", "southeast", "south", "southwest", "west", "northwest", "north", "northeast" };
        public static string DirName(double a) => DIR_NAMES[(int)Gd.Round(V.Fposmod(a, V.TAU) / (V.TAU / 8.0)) % 8];

        double RaidDir(Base b)
        {
            var gs = new List<int>();
            foreach (var kv in S.Gates)
                if (kv.Value == CurCol && OnLineOf(b, kv.Key)) gs.Add(kv.Key);
            if (gs.Count > 0 && Rng.Randf() < 0.6f)
            {
                int g = gs[Rng.RandiRange(0, gs.Count - 1)];
                return V.F(Tp(g)).Sub(V.F(b.Pos)).Ang();
            }
            return Rng.Randf() * V.TAU;
        }

        public double RaidLineDist(Base b, Vector2Int p)
        {
            var line = _enclEdge.GetOr(b.Id, new int[0]);
            if (line.Length == 0) return V.F(p).DistTo(V.F(b.Pos)) - BaseZoneR(b);
            int stp = Math.Max(1, line.Length / 400);
            double d = 1000000000.0;
            for (int i = 0; i < line.Length; i += stp) d = Math.Min(d, V.F(p).DistTo(V.F(Tp(line[i]))));
            return d - stp * 0.5;
        }

        double RaidReach(Base b)
        {
            if (b == null) return 0.0;
            return Math.Max(_enclRad.GetOr(b.Id, 0.0), BaseZoneR(b)) + RAID_FAR_MAX * 1.6 + 30.0;
        }

        void RaidDmg(int k, double dmg)
        {
            int oc = S.Fences.Has(k) ? FenceOwner(k) : TowerOwner(k);
            if (oc < 0) return;
            var R0 = ColonyAt(oc)?.Nraid;
            if (R0 != null && R0.On) R0.Dmg += dmg;
        }

        void RaidBreak(int ci)
        {
            var R0 = ColonyAt(ci)?.Nraid;
            if (R0 != null && R0.On) R0.Breaks++;
        }

        void StepNightRaid()
        {
            var c = S.Cols[CurCol];
            if (c.Nraid == null) c.Nraid = RaidNew();
            var R0 = c.Nraid;
            int h = Hour();
            if (R0.On)
            {
                CountRaidAtWall(R0);
                if (h >= RAID_END_H && h < 12) EndNightRaid(R0);
                return;
            }
            double dsurv = (S.T - c.StartT) / 24.0;
            if (h >= 12 && h < 19 && R0.LastDay != Day() && dsurv >= 0.9 && R0.NextT < 0.0)
            {
                R0.LastDay = Day();
                var tgt = MainBase();
                var outs = C.Bases.Where(b => !b.Main && b.Done >= 1.0 && BasePeople(b.Id).Count > 0).ToList();
                if (outs.Count > 0 && Rng.Randf() < 0.3f) tgt = outs[Rng.RandiRange(0, outs.Count - 1)];
                if (tgt == null) return;
                R0.Bid = tgt.Id;
                R0.N = RaidSize();
                R0.Dir = RaidDir(tgt);
                R0.NextT = Math.Floor(S.T / 24.0) * 24.0 + NIGHT_ATTACK_H + Rng.RandfRange(0.0, 2.0);
                R0.Eta = R0.NextT;
                R0.Major = R0.N >= RAID_MAJOR;
                R0.Warned = false;
            }
            if (R0.NextT < 0.0) return;
            if (!R0.Warned && S.T >= R0.NextT - RaidWarnLead())
            {
                R0.Warned = true;
                var tb = BaseById(R0.Bid) ?? MainBase();
                if (tb == null) { R0.NextT = -1.0; return; }
                R0.Bid = tb.Id;
                AutoGuards(true);
                PostGuards(true);
                int hh = (int)(R0.NextT % 24.0);
                if (R0.N >= RAID_MAJOR || (PhaseARules && HasSiren()))
                {
                    AddLog(string.Format("SIREN · Lookouts spotted about {0} of the dead moving on {1} from the {2}. Expect them at the wall around {3:00}:00. Guards to the line.", R0.N, tb.Name, DirName(R0.Dir), hh), "siren");
                    if (PhaseARules) PlaySfx("siren", V.F(tb.Pos));
                }
                else
                    AddLog(string.Format("Movement at dusk: a few of the dead are drifting toward {0} from the {1}. The night watch is ready.", tb.Name, DirName(R0.Dir)), "info");
            }
            if (S.T >= R0.NextT - RAID_TRAVEL_H) LaunchNightRaid(R0);
        }

        void LaunchNightRaid(Raid R0)
        {
            var b = BaseById(R0.Bid) ?? MainBase();
            R0.NextT = -1.0;
            if (b == null) return;
            if (_flowDirty || !_flowBuilt) BuildFlow();
            int n = R0.N;
            int made = 0;
            var line = _enclEdge.GetOr(b.Id, new int[0]);
            bool walled = line.Length > 0;
            var ectr = _enclCtr.GetOr(b.Id, V.F(b.Pos));
            double zr = BaseZoneR(b);
            for (int pass = 0; pass < 3; pass++)
            {
                double spread = pass == 0 ? 0.6 : Math.PI;
                double rmax = RAID_FAR_MAX + (pass == 2 ? 40.0 : 0.0);
                for (int t = 0; t < n * 16; t++)
                {
                    if (made >= n) break;
                    double ang = R0.Dir + Rng.RandfRange(-spread, spread);
                    Vector2Int q;
                    if (walled)
                    {
                        int lk = line[Rng.RandiRange(0, line.Length - 1)];
                        var lp = V.F(Tp(lk)).Add(V.HALF);
                        var outv = lp.Sub(ectr).Norm();
                        if (outv.x == 0f && outv.y == 0f) continue;
                        if (Math.Abs(V.Wrapf(outv.Ang() - R0.Dir, -Math.PI, Math.PI)) > spread) continue;
                        q = V.I(lp.Add(outv.Mul(Rng.RandfRange(RAID_FAR_MIN + 2.0, rmax))));
                    }
                    else q = V.I(V.F(b.Pos).Add(V.F(Math.Cos(ang), Math.Sin(ang)).Mul(zr + Rng.RandfRange(RAID_FAR_MIN + 2.0, rmax))));
                    if (!D.InMap(q)) continue;
                    int k = Ti(q);
                    if (!SpawnOk(k) || Flow[k] >= 99999) continue;
                    if (RaidLineDist(b, q) < RAID_FAR_MIN) continue;
                    if (Zc.Count >= MaxZombies + 150)
                    {
                        int j = Rng.RandiRange(0, Zc.Count - 1);
                        if (Zh[j] == 255 || Zh[j] == 3) continue;
                        Zc[j] = k; Zn[j] = k; Zp[j] = 0f; Zh[j] = 3;
                        if (SpawnProbeOn) SpawnProbe.Add((k, 3, S.T, ProtectedNow(k)));
                    }
                    else SpawnZ(k, 3);
                    made++;
                }
                if (made >= n) break;
            }
            R0.On = true; R0.Spawned = made; R0.Breaks = 0; R0.Dmg = 0.0; R0.Nights++; R0.Start = S.T; R0.AtWall = 0;
            bool major = made >= RAID_MAJOR;
            AddLog(string.Format("{0} {1} of the dead are on the move toward {2} from the {3}, still a long way out. They'll be at the wall after dark.", major ? "They're coming:" : "Contact:", made, b.Name, DirName(R0.Dir)), major ? "death" : "loss");
            PlaySfx("groan", V.F(b.Pos));
            EmitChanged();
        }

        void EndNightRaid(Raid R0)
        {
            var b = BaseById(R0.Bid);
            int left = 0, gone = 0;
            var c = b != null ? V.F(b.Pos) : new Vector2(-9999, -9999);
            double reach = RaidReach(b);
            for (int i = 0; i < Zc.Count; i++)
            {
                if (Zh[i] == 3 && ZPos(i).DistTo(c) < reach)
                {
                    left++;
                    if (Safe[Zc[i]] == 0) { KillZ(i, false); gone++; }
                    else Zh[i] = 0;
                }
            }
            R0.On = false;
            int killed = Math.Max(0, R0.Spawned - left);
            int br = R0.Breaks;
            int inside = left - gone;
            AddLog(string.Format("Dawn at {0}. {1} of the {2} that came in the night are down{3}{4}.", b?.Name ?? "camp", killed, R0.Spawned,
                br > 0 ? string.Format(", {0} fence tile{1} broke", br, br == 1 ? "" : "s") : ", the line held",
                inside > 0 ? string.Format(", {0} still prowl inside the safe zone", inside) : ""), br == 0 && inside == 0 ? "major" : "loss");
            if (R0.Dmg >= 1.0 && br == 0) AddLog(string.Format("The walls took {0} damage. Survivors will patch them up while there are materials.", (int)R0.Dmg), "info");
            EmitChanged();
        }

        // ---------------- war ----------------
        public const double WAR_CHANCE = 0.12;
        public const int WAR_DAWN = 6;
        public const int WAR_MIN_DAY = 3;
        public const double WAR_PREP_H = 240.0;
        public const double WAR_COOLDOWN_H = 240.0;
        public const double WAR_TRIBUTE = 0.45;
        public const double WAR_WEAK = 0.4;
        public const double WAR_SURRENDER = 0.25;
        public const int WAR_ARMY_MAX = 20;
        public const int WAR_MAX_ROUNDS = 40;
        public const double WAR_FALL = 0.35;
        public const double WAR_FALL_ARMOR = 0.2;
        public const double WAR_LOOT = 0.4;
        public static readonly Dictionary<string, double> MELEE_COST = Gd.D(("wood", 2), ("nails", 1), ("metal", 1));
        public static readonly Dictionary<string, double> VEST_COST = Gd.D(("cloth", 6), ("metal", 4), ("plastic", 2));
        public static readonly Dictionary<string, int> WAR_TIER = new Dictionary<string, int> { [""] = 0, ["melee"] = 1, ["pipe"] = 2, ["pistol"] = 2, ["hunting"] = 3, ["shotgun"] = 3, ["rifle"] = 3 };
        public static readonly Dictionary<string, string> WAR_GEAR_NAME = new Dictionary<string, string> { [""] = "bare hands", ["melee"] = "hand weapon", ["pipe"] = "pipe pistol", ["pistol"] = "pistol", ["hunting"] = "hunting rifle", ["shotgun"] = "shotgun", ["rifle"] = "AR-pattern rifle" };
        static readonly string[] WAR_OWN_MELEE = { "Soldier", "Cop", "Scavenger" };

        static WarState WarNew() => new WarState();

        public WarState WarOf(int ci = -1)
        {
            var c = ColonyAt(ci < 0 ? CurCol : ci);
            if (c == null) return null;
            if (c.War == null) c.War = WarNew();
            return c.War;
        }

        bool CampOk(string cid)
        {
            var c = CampById(cid);
            return c != null && c.Alive;
        }

        public static int WarGearDice(string gear, bool armor) => V.Clampi(2 + WAR_TIER.GetOr(gear ?? "", 0) + (armor ? 1 : 0), 2, 6);

        public string CanEnlist(Person p)
        {
            if (p == null || p.St == "dead") return "Nobody there.";
            if (IsMinor(p)) return string.Format("{0} is too young to fight.", p.First);
            return "";
        }

        List<(Camp c, double wt)> WarCandidates()
        {
            var o = new List<(Camp, double)>();
            foreach (var c in S.Camps)
            {
                if (!c.Alive || !c.Fi.HasValue) continue;
                if (c.Lord >= 0) continue;
                if (C.Pacts.Contains(c.Id)) continue;
                double wt = 0.0;
                if (c.Att == "Raiders") wt = 3.0;
                else
                {
                    string a = CampAttitude(c);
                    if (a == "Hostile") wt = 2.0;
                    else if (a == "Neutral") wt = 1.0;
                }
                if (wt > 0.0) o.Add((c, wt));
            }
            return o;
        }

        public bool WarCanTrigger()
        {
            var w = WarOf();
            if (w == null || !(S.Phase == "spread" || S.Phase == "fall")) return false;
            if (Day() < WAR_MIN_DAY) return false;
            if (w.St != "none" || w.Lord != "") return false;
            if (S.T < w.CoolT) return false;
            return WarCandidates().Count > 0;
        }

        void StepWar()
        {
            var w = WarOf();
            if (w == null) return;
            if (w.Lord != "" && !CampOk(w.Lord))
            {
                AddLog(string.Format("{0} is gone. Nobody takes a cut of your work any more.", CampById(w.Lord)?.Name ?? "Your overlord"), "good");
                w.Lord = "";
                if (w.St == "prep") { w.St = "none"; w.Foe = ""; w.Army = new List<int>(); }
            }
            if ((w.St == "demand" || w.St == "prep") && !CampOk(w.Foe))
            {
                AddLog(string.Format("{0} went silent. The war is over before it began.", CampById(w.Foe)?.Name ?? "The rival camp"), "info");
                w.St = "none"; w.Foe = ""; w.Army = new List<int>(); w.CoolT = S.T + WAR_COOLDOWN_H;
            }
            if (Hour() == WAR_DAWN && w.DawnDay != Day())
            {
                w.DawnDay = Day();
                WarDawn(w);
            }
            if (w.St == "prep" && S.T >= w.BattleT) WarBattle(w);
        }

        void WarDawn(WarState w)
        {
            var paid = new ODict<string, double>();
            foreach (var kv in w.Paid) if (kv.Value >= 1.0) paid[kv.Key] = Math.Floor(kv.Value);
            if (paid.Count > 0 && w.Lord != "")
                AddLog(string.Format("{0} collected their 45%: {1}.", CampById(w.Lord)?.Name ?? "Your overlord", FmtCost(paid)), "loss");
            w.Paid = new ODict<string, double>();
            foreach (var c in S.Camps)
            {
                if (c.Lord != CurCol) continue;
                if (!c.Alive) { c.Lord = -1; continue; }
                var got = new ODict<string, double>();
                foreach (var kv in c.Owed)
                    if ((C.Res.Has(kv.Key) || kv.Key == "materials") && kv.Value > 0.0)
                    {
                        AddRes(kv.Key, kv.Value);
                        if (kv.Value >= 1.0) got[kv.Key] = Math.Floor(kv.Value);
                    }
                c.Owed = new ODict<string, double>();
                if (got.Count > 0) AddLog(string.Format("{0} handed over their 45%: {1}.", c.Name, FmtCost(got)), "good");
            }
            if (!WarCanTrigger()) return;
            w.Rolls++;
            if (Wrng.Randf() >= WAR_CHANCE) return;
            w.Hits++;
            WarDemand(w);
        }

        public JObject ForceWarDemand(string cid = "")
        {
            var w = WarOf();
            if (w == null || w.St != "none") return null;
            return WarDemand(w, cid);
        }

        Camp WarPickCamp()
        {
            var cand = WarCandidates();
            if (cand.Count == 0) return null;
            double tot = cand.Sum(e => e.wt);
            double r = Wrng.Randf() * tot;
            foreach (var e in cand)
            {
                r -= e.wt;
                if (r <= 0.0) return e.c;
            }
            return cand[cand.Count - 1].c;
        }

        JObject WarDemand(WarState w, string cid = "")
        {
            var c = cid != "" ? CampById(cid) : WarPickCamp();
            if (c == null || !c.Alive) return null;
            w.St = "demand"; w.Foe = c.Id;
            var est = RivalArmyEstimate(c);
            int mine = AlivePeople().Count(p => !IsMinor(p));
            string text = string.Format("Riders from {0} stopped at your gate. {1} wants your camp to bend the knee: from now on they take 45% of everything you produce.\n\n{2}: {3} people, {4}. They can field about {5} fighters averaging {6:0.0} dice each.\nYou have {7} grown-ups who could fight (children and teens stay out of it).\n\nBend the knee and they own you, but nobody dies today. Refuse and you have 10 days to build an army and arm it with whatever you have, from hand weapons to guns.",
                c.Name, c.Leader, c.Name, c.Pop, RivalStrengthWord(c), est.n, est.avg, mine);
            var ev = new JObject
            {
                ["id"] = "_war", ["title"] = string.Format("WAR: {0} demands you bend the knee", c.Name), ["text"] = text, ["col"] = CurCol,
                ["choices"] = new JArray
                {
                    new JObject { ["label"] = "Bend the knee", ["hint"] = "They take 45% of what you produce every day. Your stockpile stays yours.", ["effects"] = new JObject { ["war"] = "submit", ["camp"] = c.Id, ["text"] = string.Format("You bent the knee to {0}.", c.Name) } },
                    new JObject { ["label"] = "Refuse and go to war", ["hint"] = "10 days to enlist and arm an army (ARMY under ⚔ WAR). Then the battle.", ["effects"] = new JObject { ["war"] = "fight", ["camp"] = c.Id, ["text"] = string.Format("War with {0}. Battle in 10 days.", c.Name) } },
                },
            };
            S.NextUid++;
            ev["uid"] = S.NextUid;
            w.Uid = S.NextUid;
            AddLog(string.Format("{0} demands that you bend the knee.", c.Name), "major");
            EmitDecision(ev);
            return ev;
        }

        public string RivalStrengthWord(Camp c)
        {
            double f = RivalPower(c);
            if (f >= 0.75) return "heavily armed";
            if (f >= 0.45) return "well armed";
            if (f >= 0.25) return "armed";
            return "poorly armed";
        }

        static double RivalPower(Camp c) => V.Clamp(c.Str / 60.0 + (c.Att == "Raiders" ? 0.15 : 0.0), 0.1, 1.0);
        public static int RivalArmySize(Camp c) => V.Clampi((int)Gd.Round(c.Pop * 0.45), 3, 18);

        public (int n, double avg) RivalArmyEstimate(Camp c)
        {
            double f = RivalPower(c);
            double gun = Math.Min(0.9, f * 0.7);
            double eTier = gun * (2.0 + f) + (1.0 - gun) * 0.8;
            double eArm = f * 0.6;
            return (RivalArmySize(c), V.Clamp(2.0 + eTier + eArm, 2.0, 6.0));
        }

        static readonly string[] RIVAL_NAMES = { "Ash", "Boone", "Cutter", "Dell", "Fox", "Grady", "Hale", "Jett", "Knox", "Lyle", "Mace", "Nash", "Orrin", "Pike", "Quill", "Reno", "Slade", "Tate", "Vance", "Wade" };

        List<Fighter> RivalArmy(Camp c)
        {
            double f = RivalPower(c);
            double gun = Math.Min(0.9, f * 0.7);
            var o = new List<Fighter>();
            int n = RivalArmySize(c);
            for (int i = 0; i < n; i++)
            {
                string g = "";
                if (Wrng.Randf() < gun) g = Wrng.Randf() < f ? "rifle" : "pistol";
                else if (Wrng.Randf() < 0.8f) g = "melee";
                bool arm = Wrng.Randf() < f * 0.6;
                o.Add(new Fighter { Pid = 0, Name = RIVAL_NAMES[i % RIVAL_NAMES.Length], Gear = g, Armor = arm, Dice = WarGearDice(g, arm), State = "up" });
            }
            return o;
        }

        void WarChoice(string kind, string cid)
        {
            var w = WarOf();
            if (w == null || w.St != "demand" || w.Foe != cid) return;
            var c = CampById(cid);
            if (c == null || !c.Alive) { w.St = "none"; w.Foe = ""; return; }
            if (kind == "submit")
            {
                w.St = "none"; w.Foe = ""; w.Lord = cid; w.Paid = new ODict<string, double>();
                WarTruce(c, true);
                AddLog(string.Format("You bent the knee to {0}. They take 45% of everything you produce.", c.Name), "loss");
            }
            else WarStartPrep(w, c);
        }

        void WarTruce(Camp c, bool on)
        {
            if (c.Truce == null) c.Truce = new ODict<string, double>();
            c.Truce[CurCol.ToString()] = on ? 1000000000000.0 : -1.0;
        }

        void WarStartPrep(WarState w, Camp c)
        {
            w.St = "prep"; w.Foe = c.Id; w.BattleT = S.T + WAR_PREP_H;
            w.Army = new List<int>();
            WarTruce(c, false);
            AddRel(c, -20.0);
            foreach (var p in AlivePeople())
                if ((p.Job == "Soldier" || p.Job == "Cop") && CanEnlist(p) == "" && w.Army.Count < WAR_ARMY_MAX) w.Army.Add(p.Id);
            AddLog(string.Format("War with {0}. The battle comes in 10 days: enlist your army and arm it.", c.Name), "major");
        }

        public string WarDeclare()
        {
            var w = WarOf();
            if (w.Lord == "") return "You don't answer to anyone.";
            if (w.St != "none") return "You're already at war.";
            var c = CampById(w.Lord);
            if (c == null || !c.Alive) return "They're gone.";
            WarStartPrep(w, c);
            EmitChanged();
            return "";
        }

        public string WarEnlist(int pid, bool on = true)
        {
            var w = WarOf();
            if (w.St != "prep") return "You're not at war.";
            var p = OwnPerson(pid);
            if (p == null) return "That survivor isn't in your colony.";
            if (!on) { w.Army.Remove(pid); EmitChanged(); return ""; }
            string e = CanEnlist(p);
            if (e != "") return e;
            if (w.Army.Contains(pid)) return "";
            if (w.Army.Count >= WAR_ARMY_MAX) return string.Format("An army has {0} soldiers at most.", WAR_ARMY_MAX);
            w.Army.Add(pid);
            EmitChanged();
            return "";
        }

        public string WarEnlistAll()
        {
            var w = WarOf();
            if (w.St != "prep") return "You're not at war.";
            foreach (var p in AlivePeople())
            {
                if (w.Army.Count >= WAR_ARMY_MAX) break;
                if (CanEnlist(p) == "" && !w.Army.Contains(p.Id)) w.Army.Add(p.Id);
            }
            EmitChanged();
            return "";
        }

        public string WarClear()
        {
            var w = WarOf();
            if (w.St != "prep") return "You're not at war.";
            w.Army = new List<int>();
            EmitChanged();
            return "";
        }

        public string CraftWarGear(string kind)
        {
            var w = WarOf();
            var cost = kind == "melee" ? MELEE_COST : VEST_COST;
            if (!CanAfford(cost)) return string.Format("Need {0}.", FmtCost(cost));
            Pay(cost);
            if (kind == "melee") w.Melee++;
            else GearAdd("soft_vest", 1);
            AddLog(string.Format("Made a {0} for the fight.", kind == "melee" ? "hand weapon" : "vest of scrap armour"), "info");
            EmitChanged();
            return "";
        }

        public List<Person> WarArmyPeople(WarState w = null)
        {
            w ??= WarOf();
            var o = new List<Person>();
            foreach (int pid in w.Army)
            {
                var p = PersonById(pid);
                if (p == null || p.Col != CurCol || CanEnlist(p) != "") continue;
                o.Add(p);
            }
            return o;
        }

        public List<Fighter> WarKit(WarState w = null)
        {
            w ??= WarOf();
            var ppl = WarArmyPeople(w);
            var pri = new Dictionary<string, int> { ["Soldier"] = 0, ["Cop"] = 1, ["Scavenger"] = 2 };
            Gd.StableSort(ppl, (a, b) =>
            {
                int pa = pri.GetOr(a.Job, 3), pb = pri.GetOr(b.Job, 3);
                return pa != pb ? pa.CompareTo(pb) : a.Id.CompareTo(b.Id);
            });
            var guns = C.Weapons.Duplicate();
            int melee = w.Melee;
            int vests = w.Vests;
            foreach (var aid in Data.ARMOR.Keys) vests += GearFree(aid);
            var o = new List<Fighter>();
            foreach (var p in ppl)
            {
                string g = "";
                foreach (var wid in Data.WEAPON_ORDER)
                    if (guns.Get(wid, 0) > 0) { guns[wid] = guns[wid] - 1; g = wid; break; }
                if (g == "")
                {
                    if (WAR_OWN_MELEE.Contains(p.Job)) g = "melee";
                    else if (melee > 0) { melee--; g = "melee"; }
                }
                bool arm = false;
                if (ArmorOf(p) > 0.0) arm = true;
                else if (vests > 0) { vests--; arm = true; }
                o.Add(new Fighter { Pid = p.Id, Name = p.Name, Job = p.Job, Gear = g, Armor = arm, Dice = WarGearDice(g, arm), State = "up" });
            }
            return o;
        }

        public double WarDaysLeft(WarState w = null)
        {
            w ??= WarOf();
            return Math.Max(0.0, w.BattleT - S.T) / 24.0;
        }

        public string WarStatusText(int ci = -1)
        {
            var w = WarOf(ci);
            if (w == null) return "";
            var parts = new List<string>();
            if (w.St == "demand") parts.Add(string.Format("⚔ {0} demands you bend the knee", CampById(w.Foe)?.Name ?? "A rival camp"));
            else if (w.St == "prep") parts.Add(string.Format("⚔ WAR with {0} · battle in {1:0.0} days · {2} enlisted", CampById(w.Foe)?.Name ?? "?", WarDaysLeft(w), WarArmyPeople(w).Count));
            if (w.Lord != "" && w.St != "prep") parts.Add(string.Format("⚑ Vassal of {0} · they take 45%", CampById(w.Lord)?.Name ?? "?"));
            int nv = WarVassals(ci).Count;
            if (nv > 0 && parts.Count == 0) parts.Add(string.Format("★ {0} vassal camp{1} pay{2} you 45%", nv, nv == 1 ? "" : "s", nv == 1 ? "s" : ""));
            return string.Join(" · ", parts);
        }

        public List<Camp> WarVassals(int ci = -1)
        {
            if (ci < 0) ci = CurCol;
            return S.Camps.Where(c => c.Alive && c.Lord == ci).ToList();
        }

        void WarTaxOutput(ODict<string, double> tot)
        {
            var w = WarOf();
            if (w == null || w.Lord == "") return;
            var c = CampById(w.Lord);
            if (c == null || !c.Alive) return;
            var o = new ODict<string, double>();
            foreach (var b in C.Bases)
            {
                var r = BaseBalance(b);
                foreach (var kv in r) o[kv.Key] = o.Get(kv.Key, 0.0) + kv.Value + _balUp.Get(kv.Key, 0.0);
            }
            foreach (var kv in o)
            {
                string k = kv.Key;
                if (!tot.Has(k)) continue;
                double share = WAR_TRIBUTE * Math.Max(0.0, kv.Value) / 24.0;
                double take = V.Clamp(share, 0.0, Math.Max(0.0, R(k) + tot[k] / 24.0));
                if (take <= 0.0) continue;
                tot[k] = tot[k] - take * 24.0;
                c.Stock[k] = c.Stock.Get(k, 0.0) + take;
                w.Paid[k] = w.Paid.Get(k, 0.0) + take;
            }
        }

        void WarTax(ODict<string, double> haul)
        {
            var w = WarOf();
            if (w == null || w.Lord == "") return;
            var c = CampById(w.Lord);
            if (c == null || !c.Alive) return;
            foreach (var kv in haul)
            {
                if (!C.Res.Has(kv.Key)) continue;
                double take = V.Clamp(WAR_TRIBUTE * kv.Value, 0.0, Math.Max(0.0, C.Res[kv.Key]));
                C.Res[kv.Key] = C.Res[kv.Key] - take;
                c.Stock[kv.Key] = c.Stock.Get(kv.Key, 0.0) + take;
                w.Paid[kv.Key] = w.Paid.Get(kv.Key, 0.0) + take;
            }
        }

        void VassalCampShare(Camp c, string k, double gain)
        {
            int lc = c.Lord;
            if (lc < 0 || gain <= 0.0) return;
            if (!ColAlive(ColonyAt(lc))) { c.Lord = -1; return; }
            double share = gain * WAR_TRIBUTE;
            c.Stock[k] = c.Stock.Get(k, 0.0) - share;
            if (c.Owed == null) c.Owed = new ODict<string, double>();
            c.Owed[k] = c.Owed.Get(k, 0.0) + share;
        }

        static List<int> Roll(int n, GdRng r)
        {
            var d = new List<int>();
            for (int i = 0; i < n; i++) d.Add(r.RandiRange(1, 6));
            d.Sort(); d.Reverse();
            return d;
        }

        static string Dstr(List<int> d) => string.Join(" ", d);
        static bool WarWeak(int up, int start, int otherUp) => up > 0 && (up < WAR_WEAK * start || otherUp >= 2 * up);

        public sealed class BattleResult
        {
            public string Winner = "", Surrender = "";
            public List<string> Lines = new List<string>();
            public int Rounds;
            public double AmmoUsed;
            public int WeakChecks, Surrenders;
        }

        public BattleResult WarBattleSim(List<Fighter> us, List<Fighter> them, GdRng r, double ammo = 1000000000.0, string themName = "them")
        {
            var res = new BattleResult();
            var lines = res.Lines;
            int[] start = { us.Count, them.Count };
            var sides = new[] { us, them };
            int rnd = 0;
            while (rnd < WAR_MAX_ROUNDS)
            {
                var up = new[] { new List<Fighter>(), new List<Fighter>() };
                for (int si = 0; si < 2; si++)
                {
                    foreach (var s in sides[si]) if (s.State == "up") up[si].Add(s);
                    // GDScript sort_custom (unstable) by dice desc; stable here.
                    Gd.StableSort(up[si], (a, b) => b.Dice.CompareTo(a.Dice));
                }
                if (up[0].Count == 0 || up[1].Count == 0) break;
                rnd++;
                lines.Add(string.Format("— Round {0}: {1} of yours against {2} of {3} —", rnd, up[0].Count, up[1].Count, themName));
                int pairs = Math.Min(up[0].Count, up[1].Count);
                for (int i = 0; i < pairs; i++)
                {
                    var a = up[0][i]; var b = up[1][i];
                    int na = a.Dice, nb = b.Dice;
                    if (WAR_TIER.GetOr(a.Gear ?? "", 0) >= 2)
                    {
                        double cost = Data.WEAPONS.TryGetValue(a.Gear, out var wd) ? wd.Ammo : 1.0;
                        if (ammo - res.AmmoUsed >= cost) res.AmmoUsed += cost;
                        else na = WarGearDice("melee", a.Armor);
                    }
                    string help = "";
                    if (up[0].Count > pairs + i) { na = Math.Min(6, na + 1); help = " (backed up)"; }
                    if (up[1].Count > pairs + i) { nb = Math.Min(6, nb + 1); help = " (they had backup)"; }
                    List<int> da, db;
                    int tries = 0;
                    while (true)
                    {
                        da = Roll(na, r); db = Roll(nb, r);
                        tries++;
                        if (da[0] != db[0] || tries >= 8) break;
                    }
                    bool aWins = da[0] > db[0] || (da[0] == db[0] && (na > nb || (na == nb && r.Randf() < 0.5f)));
                    var loser = aWins ? b : a;
                    bool fell = r.Randf() < (loser.Armor ? WAR_FALL_ARMOR : WAR_FALL);
                    loser.State = fell ? "fell" : "wounded";
                    lines.Add(string.Format("{0} [{1}] {2}  vs  {3} [{4}] {5}{6}{7} → {8} {9}.",
                        a.Name.Split(' ')[0], WAR_GEAR_NAME.GetOr(a.Gear ?? "", ""), Dstr(da), b.Name, WAR_GEAR_NAME.GetOr(b.Gear ?? "", ""), Dstr(db),
                        help, tries > 1 ? " (re-rolled a tie)" : "", loser == a ? a.Name.Split(' ')[0] : b.Name, fell ? "fell" : "was wounded and pulled back"));
                }
                int[] nUp = { 0, 0 };
                for (int si = 0; si < 2; si++) foreach (var s in sides[si]) if (s.State == "up") nUp[si]++;
                if (nUp[0] == 0 || nUp[1] == 0) break;
                var order = r.Randf() < 0.5f ? new[] { 1, 0 } : new[] { 0, 1 };
                int gave = -1;
                foreach (int si in order)
                {
                    if (WarWeak(nUp[si], start[si], nUp[1 - si]))
                    {
                        res.WeakChecks++;
                        if (r.Randf() < WAR_SURRENDER) { gave = si; break; }
                    }
                }
                if (gave >= 0)
                {
                    res.Surrenders = 1;
                    res.Surrender = gave == 0 ? "us" : "them";
                    lines.Add(gave == 0 ? "Your side laid down its weapons." : string.Format("{0} threw down their weapons and surrendered.", themName));
                    break;
                }
            }
            res.Rounds = rnd;
            if (res.Surrender != "") res.Winner = res.Surrender == "us" ? "them" : "us";
            else
            {
                int n0 = us.Count(s => s.State == "up"), n1 = them.Count(s => s.State == "up");
                res.Winner = n0 > n1 ? "us" : "them";
            }
            return res;
        }

        void WarBattle(WarState w)
        {
            var c = CampById(w.Foe);
            if (c == null || !c.Alive) { w.St = "none"; w.Foe = ""; w.Army = new List<int>(); return; }
            var us = WarKit(w);
            bool calledUp = false;
            if (us.Count == 0)
            {
                foreach (var p in AlivePeople())
                    if (CanEnlist(p) == "" && w.Army.Count < WAR_ARMY_MAX) w.Army.Add(p.Id);
                us = WarKit(w);
                calledUp = true;
            }
            var them = RivalArmy(c);
            string nm = c.Name;
            var res = new BattleResult { Winner = "them", Surrender = "us", Lines = new List<string> { "Nobody was left to fight." } };
            if (us.Count > 0) res = WarBattleSim(us, them, Wrng, R("ammo"), nm);
            C.Res["ammo"] = Math.Max(0.0, R("ammo") - res.AmmoUsed);
            bool won = res.Winner == "us";
            var fell = new List<Person>(); var hurt = new List<Person>();
            foreach (var s in us)
            {
                var p = PersonById(s.Pid);
                if (p == null) continue;
                if (s.State == "fell") fell.Add(p);
                else if (s.State == "wounded") hurt.Add(p);
            }
            foreach (var p in hurt) p.Hp = Math.Max(15.0, p.Hp - 45.0);
            foreach (var p in fell) KillPerson(p, string.Format("fell fighting {0}", nm));
            int theirFell = them.Count(s => s.State == "fell"), theirHurt = them.Count(s => s.State == "wounded");
            c.Pop = Math.Max(2, c.Pop - theirFell);
            c.Str = Math.Max(4.0, c.Str - 2.0 * theirFell - 0.5 * theirHurt);
            string summary;
            bool wasLord = w.Lord == c.Id;
            if (won)
            {
                var loot = new ODict<string, double>();
                foreach (var k in Data.RESOURCES.Concat(new[] { "materials" }))
                {
                    double v = Math.Floor(Math.Max(0.0, c.Stock.Get(k, 0.0)) * WAR_LOOT);
                    if (v >= 1.0)
                    {
                        c.Stock[k] = c.Stock[k] - v;
                        AddRes(k, v);
                        loot[k] = v;
                    }
                }
                if (wasLord) { w.Lord = ""; w.Paid = new ODict<string, double>(); }
                bool broke = res.Surrender == "them" || theirFell + theirHurt >= (int)Math.Ceiling(them.Count / 2.0);
                if (broke && c.Lord < 0)
                {
                    c.Lord = CurCol; c.Owed = new ODict<string, double>();
                    WarTruce(c, true);
                    summary = string.Format("Victory. {0} bent the knee: they hand over {1} now and 45% of what they scavenge every day.", nm, loot.Count > 0 ? FmtCost(loot) : "what little they have");
                }
                else
                {
                    c.Truce[CurCol.ToString()] = S.T + WAR_COOLDOWN_H;
                    summary = string.Format("Victory. {0} pulled back and paid {1}.", nm, loot.Count > 0 ? FmtCost(loot) : "what little they had");
                }
                if (wasLord) summary += " You are free of them.";
                AddRel(c, -10.0);
            }
            else
            {
                var lost = new ODict<string, int>();
                foreach (var wid in Data.WEAPON_ORDER)
                {
                    int n = C.Weapons.Get(wid, 0);
                    int l = (int)Math.Ceiling(n / 3.0);
                    if (l > 0) { C.Weapons[wid] = n - l; lost[wid] = l; }
                }
                int lm = (int)Math.Ceiling(w.Melee / 2.0), lv = (int)Math.Ceiling(w.Vests / 2.0);
                w.Melee -= lm; w.Vests -= lv;
                int ng = lost.Sum(kv => kv.Value);
                c.Str += 1.5 * ng;
                var lt = new List<string>();
                foreach (var kv in lost) lt.Add(string.Format("{0} {1}{2}", kv.Value, Lower(Data.WEAPONS[kv.Key].Name), kv.Value > 1 ? "s" : ""));
                if (lm > 0) lt.Add(string.Format("{0} hand weapon{1}", lm, lm > 1 ? "s" : ""));
                if (lv > 0) lt.Add(string.Format("{0} vest{1}", lv, lv > 1 ? "s" : ""));
                w.Lord = c.Id; w.Paid = new ODict<string, double>();
                WarTruce(c, true);
                summary = string.Format("Defeat. {0} owns your camp now and takes 45% of what you produce.{1}", nm, lt.Count > 0 ? " They carried off " + string.Join(", ", lt) + "." : "");
                Equip();
            }
            string cas = string.Format("Your losses: {0} fell, {1} wounded. Theirs: {2} fell, {3} wounded.", fell.Count, hurt.Count, theirFell, theirHurt);
            w.Rid++;
            var head = new List<string>();
            if (calledUp) head.Add("Nobody had been enlisted, so every grown-up who could stand was called to the line.");
            head.Add(string.Format("Your army: {0} soldiers ({1} dice). {2}: {3} fighters ({4} dice).", us.Count, us.Sum(s => s.Dice), nm, them.Count, them.Sum(s => s.Dice)));
            head.Add("Each duel: both sides roll their dice, the highest single die wins, ties re-roll.");
            w.Report = new WarReport { Id = w.Rid, Foe = nm, Won = won, Day = Day(), Summary = summary, Casualties = cas, Head = head, Lines = res.Lines.Take(400).ToList(), Rounds = res.Rounds, Surrender = res.Surrender, Ammo = (int)res.AmmoUsed };
            w.St = "none"; w.Foe = ""; w.Army = new List<int>(); w.CoolT = S.T + WAR_COOLDOWN_H; w.BattleT = -1.0;
            AddLog(string.Format("The battle with {0} is over. {1} {2}", nm, summary, cas), won ? "good" : "loss");
            PlaySfx("alarm", V.F(MainBase()?.Pos ?? V.NONE));
            EmitChanged();
        }
    }
}
