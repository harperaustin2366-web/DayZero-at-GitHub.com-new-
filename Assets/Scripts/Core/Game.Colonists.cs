// game.gd: colonists and jobs - economy, medicine, deaths and bites, moving people around,
// scavenging runs, combat, outposts and facilities, survivor groups, rescues, auto settings.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DayZero.Core
{
    public sealed partial class Game
    {
        ODict<string, double> _balUp = new ODict<string, double>();

        // ---------------- economy ----------------
        public ODict<string, double> BaseBalance(Base b)
        {
            var r = ResZero();
            var ppl = BasePeople(b.Id);
            int guards = 0;
            var up = ResZero();
            _balUp = up;
            foreach (var p in ppl)
            {
                string st = AgeStage(p);
                double eat = st == "baby" ? 0.4 : (st == "child" ? 0.7 : 1.0);
                r["food"] -= eat; r["water"] -= eat; r["meds"] -= 0.08;
                up["food"] += eat; up["water"] += eat; up["meds"] += 0.08;
                if (p.Job == "Soldier" || p.Job == "Cop") guards++;
            }
            r["ammo"] -= 0.4 * guards;
            r["cloth"] -= 0.03 * ppl.Count; r["wood"] -= 0.03 * ppl.Count;
            up["ammo"] += 0.4 * guards; up["cloth"] += 0.03 * ppl.Count; up["wood"] += 0.03 * ppl.Count;
            double agri = 1.0 + Rx("food_mult");
            double wm = 1.0 + Rx("water_mult");
            r["water"] += (b.Main ? 5.0 : 2.5) * wm + Rx("water_flat");
            r["water"] += 5.0 * b.U("water") * wm;
            r["food"] += 5.0 * b.U("garden") * agri;
            r["meds"] += Rx("meds_flat");
            r["materials"] += Rx("mats_flat");
            switch (b.Site)
            {
                case "hospital": r["meds"] += 1.0; break;
                case "police": r["ammo"] += 1.5; break;
                case "high_school": r["food"] += 2.0; break;
                case "warehouse": r["materials"] += 2.0; break;
                case "farmhouse": r["food"] += 6.0 * agri; r["water"] += 2.0; break;
            }
            ConvBalance(b, r, agri, wm);
            var here = AtBase(b.Id);
            if ((b.U("workshop") > 0 || b.U("armory") > 0) && here.Count > 0) r["ammo"] += Rx("ammo_daily");
            foreach (var p in here)
            {
                switch (p.Job)
                {
                    case "Farmer":
                        double f = 4.0 * (1.0 + 0.5 * b.U("garden")) * agri;
                        f *= 1.0 + Rx("farmer_mult");
                        if (b.Site == "farmhouse") f *= 1.5;
                        r["food"] += f;
                        break;
                    case "Civilian":
                        r["water"] += 1.6 * wm; r["food"] += 0.6; r["materials"] += 1.0;
                        break;
                    case "Engineer":
                        r["materials"] += (1.5 + (b.U("workshop") > 0 ? 2.0 : 0.0)) * (1.0 + Rx("salvage_mult"));
                        break;
                    case "Medic":
                        r["meds"] += 0.6 * b.U("medbay");
                        break;
                    case "Scavenger":
                        r["materials"] += 0.5;
                        break;
                }
            }
            if (b.U("armory") > 0 && R("ammo") < 80.0 && R("metal") > 3.0 && here.Count > 0)
            {
                r["ammo"] += 3.0 * b.U("armory");
                r["metal"] -= 1.2 * b.U("armory"); r["plastic"] -= 0.8 * b.U("armory");
                up["metal"] += 1.2 * b.U("armory"); up["plastic"] += 0.8 * b.U("armory");
            }
            return r;
        }

        public bool IsSelfSustaining(Base b)
        {
            if (BasePeople(b.Id).Count == 0) return false;
            var r = BaseBalance(b);
            foreach (var k in new[] { "food", "water", "ammo", "meds" })
                if (r[k] < -0.01) return false;
            if (r["materials"] + MatSum(r) < -0.01) return false;
            return true;
        }

        void Economy()
        {
            var tot = ResZero();
            foreach (var b in C.Bases)
                foreach (var kv in BaseBalance(b)) tot[kv.Key] = tot.Get(kv.Key, 0.0) + kv.Value;
            if (Hour() == 12)
            {
                double drop = Rx("supply_drop");
                if (drop > 0.0)
                {
                    foreach (var k in new[] { "food", "water", "ammo", "meds", "materials" }) tot[k] += drop * 24.0;
                    if (Rng.Randf() < 0.3f) AddLog(string.Format("A supply drop landed by {0}.", MainBase()?.Name ?? "camp"), "good");
                }
                double aid = Rx("camp_aid");
                if (aid > 0.0)
                    foreach (var c in S.Camps)
                        if (c.Alive && (CampAttitude(c) == "Friendly" || C.Pacts.Contains(c.Id)))
                        {
                            string res = c.Surplus.Count > 0 ? c.Surplus[0] : "food";
                            if (tot.Has(res)) tot[res] += aid * 24.0;
                        }
            }
            WarTaxOutput(tot);
            double gm = tot.Get("materials", 0.0);
            tot.Remove("materials");
            if (gm > 0.0)
                foreach (var m in Data.MAT_MIX) tot[m.Key] = tot[m.Key] + gm * m.Value;
            else if (gm < 0.0)
            {
                double mt = MatsTotal();
                if (mt > 0.0)
                    foreach (var m in Data.MATS) tot[m] = tot[m] + gm * R(m) / mt;
            }
            double cap = StorageCap();
            foreach (var kv in tot)
            {
                string k = kv.Key;
                if (!C.Res.Has(k)) C.Res[k] = 0.0;
                double v = kv.Value / 24.0;
                if (k == "food" || k == "water") C.Res[k] = C.Res[k] + v;
                else C.Res[k] = Math.Max(0.0, C.Res[k] + v);
                if (C.Res[k] > cap) C.Res[k] = Math.Max(cap, C.Res[k] - Math.Max(v, 0.0) - cap * 0.01);
            }
            int over = 0;
            foreach (var b in C.Bases) over += Math.Max(0, BasePeople(b.Id).Count - BaseBeds(b));
            if (over > 0)
            {
                C.Morale = Math.Max(0.0, C.Morale - 0.04 * over);
                if (Hour() == 9 && Rng.Randf() < 0.3f) AddLog(string.Format("{0} people have no bed. Upgrade a base or build barracks.", over), "loss");
            }
            bool starving = false;
            foreach (var k in new[] { "food", "water" })
                if (C.Res[k] < 0.0) { C.Res[k] = 0.0; starving = true; }
            if (starving)
            {
                C.Morale = Math.Max(0.0, C.Morale - 1.0);
                foreach (var p in AlivePeople())
                {
                    p.Hp -= 1.2;
                    if (p.Hp <= 0.0) KillPerson(p, "starved");
                }
                if (Hour() == 12) AddLog("People are going hungry. Send scavengers out or plant gardens.", "loss");
            }
            else C.Morale = Math.Min(100.0, C.Morale + 0.15 + Rx("morale_add") + 0.05 * Math.Min(3, ConvN("school")) + WallsMorale());
        }

        public double WallsMorale()
        {
            double m = 0.0;
            foreach (var b in C.Bases)
                if (IsEnclosed(b)) m += 0.05 + Math.Min(0.05, EnclosedCount(b) / 40000.0);
            return Math.Min(0.2, m);
        }

        void Medical()
        {
            double med2 = 1.0 + Rx("heal_mult");
            foreach (var b in C.Bases)
            {
                var here = AtBase(b.Id);
                int medics = here.Count(p => p.Job == "Medic");
                double infMult = 1.0 + 0.6 * b.U("medbay");
                if (b.Site == "hospital") infMult *= 1.3;
                double heal = (0.6 + 1.5 * b.U("medbay") + 2.5 * medics * infMult + 2.0 * ConvN("hospital", b.Id)) * med2;
                double detect = Math.Min(0.8, 0.07 * medics * infMult + 0.03 * b.U("medbay") + (medics > 0 ? Rx("detect_add") : 0.0));
                if (BasePeople(b.Id).Count > BaseBeds(b)) heal *= 0.6;
                foreach (var p in here)
                {
                    if (p.Hp < 100.0) p.Hp = Math.Min(100.0, p.Hp + heal);
                    if (p.Inf >= 0.0 && !p.Known && medics > 0 && Rng.Randf() < detect)
                    {
                        p.Known = true;
                        AddLog(string.Format("A medic found a hidden bite on {0}. Treating it.", p.Name), "major");
                    }
                }
            }
            foreach (var p in C.People.ToList())
            {
                if (p.St == "dead" || p.Inf < 0.0) continue;
                p.Inf = Math.Max(0.0, p.Inf - 1.0);
                if (p.Known && p.St == "base")
                {
                    if (R("meds") >= 2.0 && p.Inf > 1.0)
                    {
                        C.Res["meds"] = R("meds") - 2.0;
                        int mbLvl = BaseById(p.Base)?.U("medbay") ?? 0;
                        double cure = (mbLvl == 0 ? 0.3 : 0.45 + 0.12 * mbLvl) + Rx("cure_add") + Math.Min(0.12, 0.06 * ConvN("hospital"));
                        if (Rng.Randf() < cure)
                        {
                            p.Inf = -1.0; p.Known = false; p.Hp = Math.Max(30.0, p.Hp - 30.0);
                            AddLog(string.Format("{0} pulled through. The infection is gone.", p.Name), "good");
                            continue;
                        }
                        p.Inf = 0.0;
                    }
                    if (p.Inf <= 0.0) KillPerson(p, "died of the infection in quarantine");
                    continue;
                }
                if (p.Inf <= 0.0) Turn(p);
            }
            FlushDead();
        }

        void Turn(Person p)
        {
            var pos = p.Pos;
            bool inside = p.St == "base";
            KillPerson(p, "turned", false);
            C.AddStat("turned", 1);
            int k = Ti(D.NearestWalkable(V.I(pos)));
            bool rose = inside ? false : SpawnZombie(k, 0);
            if (inside)
            {
                AddLog(string.Format("{0} was hiding a bite and turned inside {1}! The others put them down before anyone else got hurt.", p.Name, BaseById(p.Base)?.Name ?? "camp"), "death");
                PlaySfx("alarm", pos);
            }
            else if (!rose) AddLog(string.Format("{0} turned inside the wall. The watch put them down.", p.Name), "loss");
        }

        void KillPerson(Person p, string cause, bool logIt = true)
        {
            if (p.St == "dead") return;
            if (p.St != "base") LoseGear(p);
            LeaveBld(p);
            p.St = "dead";
            p.Path = new List<Vector2>();
            _claimed.Remove(p.Lot);
            FamilyOnDeath(p);
            C.AddStat("dead", 1);
            if (logIt) AddLog(string.Format("{0} ({1}) {2}.", p.Name, p.Job, cause), "death");
            C.People.Remove(p);
            _peopleById.Remove(p.Id);
            if (AlivePeople().Count == 0 && CurCol >= 0)
            {
                var c = S.Cols[CurCol];
                if (c.Started && !c.Lost)
                {
                    c.Lost = true;
                    c.LostDay = Day();
                    AddLog(string.Format("Nobody is left{0}.", !Mp ? "" : " in " + c.Name), "death");
                    RebuildSafe();
                    _flowDirty = true;
                    ColonyOver?.Invoke(CurCol, false);
                    bool anyAlive = S.Cols.Any(ColAlive);
                    if (!anyAlive)
                    {
                        S.Phase = "lost";
                        Running = false;
                        if (S.Cols.Count == 1) GameOver?.Invoke(false);
                    }
                }
            }
        }

        void Bite(Person p, string src = "")
        {
            if (p.Inf >= 0.0) return;
            p.Inf = Rng.RandfRange(16.0, 40.0);
            p.Hp = Math.Max(5.0, p.Hp - 25.0);
            if (Rng.Randf() < 0.3f)
            {
                p.Known = true;
                AddLog(string.Format("{0} was bitten{1} and told everyone.", p.Name, src), "loss");
            }
            else p.Known = false;
        }

        public void SetJob(Person p, string job)
        {
            p.Job = job;
            EmitChanged();
        }

        public void MoveToBase(Person p, int bid)
        {
            var b = BaseById(bid);
            if (b == null || b.Done < 1.0) return;
            if (Rgroup(b.Pos) != Rgroup(new Vector2Int((int)p.Pos.x, (int)p.Pos.y))) return;
            p.Base = bid;
            if (p.St == "base") Go(p, b.Pos, "home");
            EmitChanged();
        }

        bool Go(Person p, Vector2Int tgt, string goal)
        {
            var from = new Vector2Int((int)p.Pos.x, (int)p.Pos.y);
            from = D.NearestWalkable(from, 4);
            if (from.x < 0) return false;
            var path = FindPath(from, tgt);
            if (path.Count == 0) return false;
            LeaveBld(p);
            if (_rt.TryGetValue(p.Id, out var r)) r.Code = "";
            path = FromHere(path, p.Pos);
            p.Path = path;
            p.Pi = 0;
            p.Seg = 0.0;
            p.Tgt = tgt;
            p.Goal = goal;
            p.St = "move";
            return true;
        }

        Vector2Int HomePos(Person p)
        {
            var b = BaseById(p.Base);
            if (b == null)
            {
                b = MainBase();
                if (b == null) return new Vector2Int((int)p.Pos.x, (int)p.Pos.y);
                p.Base = b.Id;
            }
            return b.Pos;
        }

        void MovePeople(double dh)
        {
            _openHumans.Clear();
            if (CurCol >= 0 && _rtNeed.GetOr(CurCol, true)) AssignRoutines();
            foreach (var p in C.People.ToList())
            {
                if (p.St == "dead") continue;
                p.Cd = Math.Max(0.0, p.Cd - dh);
                if (p.St == "move")
                {
                    var path = p.Path;
                    if (path.Count < 2) { Arrive(p); continue; }
                    double step = dh * HUMAN_SPEED * (p.Job == "Scavenger" ? 1.15 : 1.0) * (p.Hp / 200.0 + 0.5)
                        * (p.Veh ? (p.Vspd ?? 1.9) * (1.0 + Rx("veh_speed")) : 1.0)
                        * (p.Tow ? (p.Veh ? 0.7 : 0.5) : 1.0);
                    p.Seg += step;
                    while (p.Seg >= 1.0 && p.Pi < path.Count - 1)
                    {
                        p.Seg -= 1.0;
                        p.Pi++;
                    }
                    if (p.Pi >= path.Count - 1)
                    {
                        p.Pos = path[path.Count - 1].Add(V.HALF);
                        Arrive(p);
                    }
                    else
                    {
                        p.Pos = path[p.Pi].LerpTo(path[p.Pi + 1], (float)p.Seg).Add(V.HALF);
                        if (p.Goal == "sq")
                        {
                            long hj = ((long)p.Id * 2654435761L) & 65535;
                            p.Pos = p.Pos.Add(V.F(((hj & 255) / 255.0 - 0.5) * 1.1, (((hj >> 8) & 255) / 255.0 - 0.5) * 1.1));
                        }
                    }
                }
                else if (p.St == "work")
                {
                    p.Wt -= dh;
                    if (p.Wt <= 0.0) FinishWork(p);
                }
                if (p.St == "move" || p.St == "work")
                {
                    int k = Ti(new Vector2Int((int)p.Pos.x, (int)p.Pos.y));
                    if (k >= 0 && k < Safe.Length && Safe[k] == 0)
                    {
                        _openHumans.Add(p);
                        if (p.Cd <= 0.0) CheckEncounter(p);
                    }
                }
                else if (p.St == "base")
                {
                    var b = BaseById(p.Base);
                    if (b != null && b.Done >= 1.0 && Routine(p, dh)) { }
                    else if (b != null && p.Guard && OnDuty() && p.Post.HasValue)
                    {
                        var wob2 = V.F(Math.Sin(S.T * 1.7 + p.Id), Math.Cos(S.T * 1.3 + p.Id)).Mul(0.18);
                        p.Pos = p.Post.Value.Add(wob2);
                    }
                    else if (b != null)
                    {
                        long h = ((long)p.Id * 2654435761L) & 65535;
                        double spread = b.Main ? (4.4 + 1.8 * b.Lvl) : (3.2 + 1.6 * b.Lvl);
                        var wob = V.F(Math.Sin(S.T * 0.9 + p.Id), Math.Cos(S.T * 0.7 + p.Id * 1.3)).Mul(0.35);
                        p.Pos = V.F(b.Pos).Add(V.HALF).Add(V.F(((h & 255) / 255.0 - 0.5) * spread, (((h >> 8) & 255) / 255.0 - 0.5) * spread)).Add(wob);
                    }
                }
            }
        }

        void Arrive(Person p)
        {
            p.Path = new List<Vector2>();
            switch (p.Goal)
            {
                case "home":
                    p.St = "base"; p.Goal = "";
                    VehHome(p);
                    var hq = SquadById(p.Sq);
                    if (hq != null && hq.St != "idle" && hq.St != "out")
                    {
                        bool allIn = SquadMembers(hq).All(m => m.St == "base");
                        if (allIn) VehSquadHome(hq);
                    }
                    p.Trav = null;
                    Deposit(p);
                    break;
                case "scav": p.St = "work"; p.Wt = 1.5 / (1.0 + Rx("search_speed")); break;
                case "tf": p.St = "work"; p.Wt = 1.0; break;
                case "sq": case "sqa": p.St = "work"; p.Wt = 1.0; break;
                case "sqs": p.St = "work"; p.Wt = 0.3; break;
                case "sqt": p.St = "work"; p.Wt = 0.5; break;
                case "repair": case "build": p.St = "work"; p.Wt = 2.0; break;
                case "hunt": case "patrol": p.St = "work"; p.Wt = 1.0; break;
                default: p.St = "base"; break;
            }
        }

        void Deposit(Person p)
        {
            var c = p.Carry;
            if (c.Count == 0) return;
            var parts = new List<string>();
            foreach (var kv in c)
            {
                AddRes(kv.Key, kv.Value);
                if (kv.Value >= 1.0) parts.Add(string.Format("{0} {1}", (int)kv.Value, Lower(ResLabel(kv.Key))));
            }
            WarTax(c);
            p.Carry = new ODict<string, double>();
            if (parts.Count > 0 && Rng.Randf() < 0.35f)
            {
                var hb = BaseById(p.Base);
                AddLog(string.Format("{0} brought back {1}{2}.", p.Name, string.Join(", ", parts), hb != null && C.Bases.Count > 1 ? " to " + hb.Name : ""), "good");
            }
        }

        void FinishWork(Person p)
        {
            switch (p.Goal)
            {
                case "scav":
                    int l = p.Lot;
                    if (l >= 0)
                    {
                        double mult = p.Job == "Scavenger" ? 0.95 : 0.6;
                        TakeLoot(p, l, mult);
                        MaybeFindPeople(p, l, 0.035);
                    }
                    _claimed.Remove(p.Lot);
                    p.Lot = -1;
                    break;
                case "tf": ResolveTf(p); break;
                case "sq":
                    SquadSweep(p);
                    if (p.St != "base" && p.Goal == "home") return;
                    break;
                case "sqs":
                    SquadSearch(p);
                    if (p.St == "work" || p.Goal == "home") return;
                    break;
                case "sqt": SquadTow(p); return;
                case "hop": SquadHopDone(p); return;
                case "hold": p.Wt = 1000000.0; return;
                case "sqw":
                    var qq = SquadById(p.Sq);
                    if (qq != null && qq.St == "search") qq.St = "return";
                    break;
                case "sqa":
                    SquadBattle(p);
                    if (p.Goal == "home" || p.St == "dead") return;
                    break;
                case "repair":
                    int k = Ti(p.Tgt);
                    double eff = (1.0 + Rx("repair_mult")) / RepairCostMult();
                    if (S.Traps.Has(k) && !S.Fences.Has(k) && !S.Towers.Has(k)) RepairTrap(p, k);
                    else if (S.Fences.TryGetValue(k, out var f))
                    {
                        double mx = FenceTileMax(k);
                        double cost = (mx - f.Hp) / (30.0 * eff);
                        if (PayMatsFlex(cost, Data.FENCES[V.Clampi(f.Tier, 0, Data.FENCES.Length - 1)].Cost))
                        {
                            f.Hp = mx;
                            PlaySfx("hammer", p.Pos);
                        }
                    }
                    else if (S.Towers.TryGetValue(k, out var t))
                    {
                        double mx2 = Data.TOWERS[t.Tier].Hp;
                        double cost2 = (mx2 - t.Hp) / (30.0 * eff);
                        if (PayMatsFlex(cost2, Data.TOWERS[t.Tier].Cost))
                        {
                            t.Hp = mx2;
                            PlaySfx("hammer", p.Pos);
                        }
                    }
                    _claimed.Remove(-1000 - k);
                    break;
                case "build":
                    _claimed.Remove(p.Lot);
                    p.Lot = -1;
                    break;
            }
            GoHome(p);
        }

        void GoHome(Person p)
        {
            if (p.Veh && p.Sq > 0)
            {
                var gq = SquadById(p.Sq);
                if (gq != null && gq.Vid >= 0) VehReturn(gq);
            }
            if (!Go(p, HomePos(p), "home"))
            {
                p.St = "base"; p.Goal = "";
                Deposit(p);
            }
        }

        void TakeLoot(Person p, int l, double mult)
        {
            if (p.Veh) mult *= (p.Vcargo ?? 1.6) * (1.0 + Rx("veh_loot"));
            if (Data.PART_DROPS.TryGetValue(D.LotKind(l), out var drops))
                foreach (var kv in drops)
                    if (Rng.Randf() < kv.Value * S.Loot[l])
                    {
                        C.Parts.AddTo(kv.Key, 1);
                        AddLog(string.Format("{0} found a {1} at {2}.", p.Name, Lower(Data.PART_LABEL[kv.Key]), D.LotName(l)), "good");
                    }
            GearLoot(p, l);
            var tab = D.LotLootTable(l);
            double left = S.Loot[l];
            foreach (var kv in tab)
            {
                double v = Rng.RandfRange(kv.Value[0], kv.Value[1]) * left * mult;
                if (v > 0.0) p.Carry[kv.Key] = p.Carry.Get(kv.Key, 0.0) + v;
            }
            MarkSearched(l, CurCol + 1);
        }

        void CheckEncounter(Person p)
        {
            var near = ZombiesNear(p.Pos, 0.75, 4);
            if (near.Count == 0) return;
            p.Cd = 0.35;
            bool sq = p.Sq > 0 && p.Goal == "sq";
            double engage = 0.65 * (1.0 - Math.Min(0.6, Rx("quiet"))) * (p.Veh ? (0.45 - Math.Min(0.3, Rx("veh_safe"))) : 1.0);
            if (sq) engage = 0.9;
            if (Rng.Randf() > engage) return;
            double win = p.Job == "Soldier" ? 0.93 : p.Job == "Cop" ? 0.87 : p.Job == "Scavenger" ? 0.74 : 0.6;
            foreach (var q in _openHumans)
                if (q != p && q.Pos.DistTo(p.Pos) < 2.0f) win += 0.05;
            if (sq) win += 0.08 + Rx("squad_power") + (S.Scout[SectOf(V.I(p.Pos))] > 0f ? 0.06 : 0.0);
            var wd = WeaponOf(p);
            if ((p.Job == "Soldier" || p.Job == "Cop" || sq || p.Wpn != "") && wd.Ammo > 0.0 && R("ammo") >= wd.Ammo)
            {
                C.Res["ammo"] = R("ammo") - wd.Ammo;
                win += (Data.GunHit(wd) - 0.7) * 0.5;
                GunShot(wd);
                PlaySfx("gunshot", p.Pos);
            }
            else
            {
                win -= 0.1;
                PlaySfx("hit", p.Pos);
            }
            if (Rng.Randf() < Math.Min(0.97, win))
            {
                KillZ(near[0]);
                p.Kills++;
                return;
            }
            double arm = ArmorOf(p);
            p.Hp -= Blow(p, 30.0);
            double shield = Rx("recon") + (sq ? Rx("squad_safe") : 0.0) + (sq && p.Veh ? (p.Varm ?? 0.1) : 0.0) + arm * 0.8;
            if (p.Hp > 0.0 && Rng.Randf() < Math.Min(0.6 + arm * 0.4, shield))
            {
                PlaySfx("hit", p.Pos);
                return;
            }
            if (Rng.Randf() < 0.4f || p.Hp <= 0.0)
            {
                var pos = p.Pos;
                KillPerson(p, string.Format("was killed by zombies near {0}", D.DistrictAt(V.I(pos)).Name));
                SpawnZombie(Ti(D.NearestWalkable(V.I(pos))), 0);
                PlaySfx("scream", pos);
            }
            else
            {
                Bite(p, " out on a run");
                PlaySfx("groan", p.Pos);
            }
        }

        void AssignJobs()
        {
            bool night = IsNight();
            foreach (var b in C.Bases)
            {
                if (b.Done < 1.0) continue;
                var here = AtBase(b.Id);
                int guardsOut = here.Count(p => p.Goal == "hunt");
                int engs = here.Count(p => p.Job == "Engineer");
                bool urgent = RepairUrgent(b);
                foreach (var p in here)
                {
                    if (p.Inf >= 0.0 && p.Known) continue;
                    if (p.Hp < 40.0) continue;
                    if (p.Sq > 0) continue;
                    if (p.Hold) continue;
                    if (IsKid(p)) continue;
                    if (p.Guard && (Hour() >= 18 || Hour() < 6)) continue;
                    bool autoScav = CampAuto(b, "scav");
                    if (urgent && (p.Job == "Civilian" || p.Job == "Farmer" || p.Job == "Scientist" || p.Job == "Scavenger") && Rng.Randf() < 0.25f && EngineerTask(p, b)) continue;
                    switch (p.Job)
                    {
                        case "Scavenger":
                            if (!night && autoScav) SendScav(p, b, AutoRadius());
                            break;
                        case "Civilian":
                            if (engs == 0 && Rng.Randf() < 0.3f && EngineerTask(p, b)) continue;
                            if (IsMinor(p)) continue;
                            if (!night && autoScav && Rng.Randf() < 0.35f) SendScav(p, b, Math.Min(20.0, AutoRadius()));
                            break;
                        case "Engineer":
                            EngineerTask(p, b);
                            break;
                        case "Soldier":
                        case "Cop":
                            if (R("ammo") > 12.0 && guardsOut < 2 && Rng.Randf() < 0.5f && SendHunt(p, b)) guardsOut++;
                            break;
                    }
                }
            }
        }

        List<(float d, int l)> BaseLotList(Base b)
        {
            if (_baseLots.TryGetValue(b.Id, out var c)) return c;
            var arr = new List<(float d, int l)>();
            var cp = V.F(b.Pos);
            for (int l = 0; l < D.Lots.Count; l++)
            {
                float d = D.LotCenter(l).DistTo(cp);
                if (d <= AUTO_R_CAP + 2.0) arr.Add((d, l));
            }
            Gd.StableSort(arr, (a, bb) => a.d.CompareTo(bb.d));
            _baseLots[b.Id] = arr;
            return arr;
        }

        bool SendScav(Person p, Base b, double r)
        {
            bool drive = VehiclesFree(b) > 0 && p.Job == "Scavenger";
            r = Math.Min(r, AutoRadius());
            int best = -1; double bs = 1000000000.0;
            float[] srch = S.Srch;
            var cand = new List<(float d, int l)>();
            foreach (var e in BaseLotList(b))
            {
                if (e.d > r) break;
                cand.Add(e);
            }
            if (IsEnclosed(b)) cand.AddRange(EnclLots(b));
            foreach (var e in cand)
            {
                int l = e.l;
                if (srch[l] >= 0f || S.Loot[l] < 0.2f || _claimed.ContainsKey(l) || _factionLots.ContainsKey(l)) continue;
                var door = D.LotDoor(l);
                double inf = InfAt(door);
                if (inf > 0.95) continue;
                double s = e.d + 70.0 * inf + 12.0 * (1.0 - S.Loot[l]) - (D.Scavenge[D.LotKind(l)] != null ? 8.0 : 0.0);
                if (s < bs) { bs = s; best = l; }
            }
            if (best < 0) return false;
            if (!Go(p, D.LotDoor(best), "scav"))
            {
                S.Loot[best] = 0f;
                return false;
            }
            p.Lot = best;
            _claimed[best] = p.Id;
            if (drive) TakeVehicle(new List<Person> { p }, b, D.LotDoor(best));
            return true;
        }

        bool SendHunt(Person p, Base b)
        {
            var near = ZombiesNear(V.F(b.Pos).Add(V.HALF), 11.0, 8);
            if (near.Count == 0 || near.Count > 6) return false;
            var zt = V.I(ZPos(near[0]));
            zt = D.NearestWalkable(zt, 3);
            if (zt.x < 0) return false;
            return Go(p, zt, "hunt");
        }

        bool EngineerTask(Person p, Base b)
        {
            foreach (var ob in C.Bases)
                if (ob.Done < 1.0 && V.F(ob.Pos).DistTo(V.F(b.Pos)) < 60.0 && !_claimed.ContainsKey(-ob.Id))
                    if (Go(p, ob.Pos, "build"))
                    {
                        p.Lot = -ob.Id;
                        _claimed[-ob.Id] = p.Id;
                        return true;
                    }
            int bestk = -1;
            foreach (var it in RepairQueue(b))
            {
                int kk = it.k;
                if (_claimed.ContainsKey(-1000 - kk)) continue;
                if (MatsTotal() < Math.Max(0.5, RepairCost(it))) continue;
                bestk = kk;
                break;
            }
            if (bestk >= 0)
            {
                var tt = V.NONE;
                var fp = Tp(bestk);
                foreach (var d in new[] { new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1), new Vector2Int(1, 1), new Vector2Int(-1, -1), new Vector2Int(1, -1), new Vector2Int(-1, 1) })
                {
                    var q = fp + d;
                    if (D.InMap(q) && D.Walkable(q) && Safe[Ti(q)] == 1 && !Blocked(Ti(q))) { tt = q; break; }
                }
                if (tt.x < 0) tt = D.NearestWalkable(fp, 2);
                if (Go(p, tt, "repair"))
                {
                    p.Tgt = Tp(bestk);
                    _claimed[-1000 - bestk] = p.Id;
                    return true;
                }
            }
            return false;
        }

        void CombatTick()
        {
            double towerBonus = 3.0;
            foreach (var p in C.People.ToList())
            {
                if (p.St == "dead") continue;
                bool onSq = p.Sq > 0 && p.Goal == "sq" && (p.St == "move" || p.St == "work");
                bool duty = p.St == "base" && p.Guard && OnDuty() && p.Post.HasValue;
                bool guard = p.Job == "Soldier" || p.Job == "Cop" || onSq || (p.Guard && p.St == "base") || (p.St == "base" && p.Wpn != "" && !IsMinor(p));
                bool at = p.St == "base" || (p.St == "work" && (p.Goal == "hunt" || p.Goal == "patrol")) || (p.St == "move" && p.Goal == "hunt") || onSq;
                if (!at) continue;
                var b = BaseById(p.Base);
                double rngR = 1.3;
                var wd = guard ? WeaponOf(p) : Data.UNARMED;
                if (guard)
                {
                    if (wd.Ammo > 0.0 && R("ammo") < wd.Ammo) wd = Data.UNARMED;
                    rngR = wd.Range + ((b != null && b.U("tower") > 0 && p.St == "base" && wd.Ammo > 0.0) ? towerBonus : 0.0) + (duty ? 1.5 : 0.0);
                }
                var near = ZombiesNear(p.Pos, rngR, 6);
                if (near.Count == 0) continue;
                double hit = 0.3;
                if (guard)
                {
                    hit = Data.GunHit(wd) + (p.Job == "Soldier" ? 0.06 : 0.0) + Rx("hit_add") + (onSq ? Rx("squad_power") * 0.5 : 0.0) + (duty ? 0.04 : 0.0) + Math.Min(0.09, 0.03 * ConvN("armory"));
                    if (wd.Ammo > 0.0)
                    {
                        C.Res["ammo"] = R("ammo") - wd.Ammo;
                        GunShot(wd);
                        PlaySfx(p.Wpn == "rifle" || p.Wpn == "hunting" ? "rifle" : "gunshot", p.Pos);
                    }
                    else PlaySfx("hit", p.Pos);
                }
                for (int k = 0; k < Math.Min(wd.Multi, near.Count); k++)
                    if (Rng.Randf() < hit)
                    {
                        KillZ(near[k]);
                        p.Kills++;
                    }
            }
            TowerCombat();
            GuardPostFights();
            foreach (var b in C.Bases.ToList())
            {
                var near = ZombiesNear(V.F(b.Pos).Add(V.HALF), 2.2, 10);
                if (near.Count == 0) continue;
                var here = AtBase(b.Id);
                if (here.Count == 0) continue;
                double atk = 0.28 * 30.0 / (30.0 + Defense(b));
                foreach (int zi in near)
                {
                    if (Rng.Randf() < atk)
                    {
                        var v = here[Rng.RandiRange(0, here.Count - 1)];
                        if (v.St == "dead") continue;
                        if (ArmorBlocks(v))
                        {
                            v.Hp = Math.Max(5.0, v.Hp - 8.0);
                            continue;
                        }
                        if (Rng.Randf() < 0.35f)
                        {
                            KillPerson(v, string.Format("was dragged down at {0}", b.Name));
                            PlaySfx("scream", V.F(b.Pos));
                        }
                        else Bite(v, " defending " + b.Name);
                        here = AtBase(b.Id);
                        if (here.Count == 0) break;
                    }
                }
            }
        }

        // ---------------- outposts & facilities ----------------
        public string CanOutpost(Vector2Int p)
        {
            p = WildSite(p, CurCol);
            if (Outposts() >= OUTPOST_MAX) return string.Format("You can run {0} outposts at most.", OUTPOST_MAX);
            string te = TakeError(p, CurCol);
            if (te != "") return te;
            var q = TakeDoor(p);
            if (!(ColonyAt(CurCol)?.Started ?? true)) return "Set up your main camp first.";
            string om = Rgroup(q);
            if (om != "world" && CampInMap(om) == null && !SquadInMap(om))
                return string.Format("None of your people are in {0}. SEND TO MAP a squad there first.", MAP_NAME.GetOr(om, om));
            foreach (var b in AllBases())
                if (V.F(b.Pos).DistTo(V.F(q)) < 14.0) return "Too close to another camp.";
            if (InfAt(q) > 0.8) return "That area is crawling with the dead.";
            return "";
        }

        public static readonly Dictionary<string, double> OUTPOST_COST = Gd.D(("wood", 15), ("nails", 8), ("cloth", 7), ("food", 10));

        public string BuildOutpost(Vector2Int p)
        {
            p = WildSite(p, CurCol);
            string e = CanOutpost(p);
            if (e != "") return e;
            if (!CanAfford(OUTPOST_COST)) return "Need " + FmtCost(OUTPOST_COST) + ".";
            Pay(OUTPOST_COST);
            int lot = D.BuildingAt(p);
            var b = AddBase(TakeDoor(p), false, lot);
            RebuildSafe();
            string extra = "";
            var fac = new List<string>();
            foreach (var k in new[] { "medbay", "armory", "garage" })
                if (b.U(k) > 0) fac.Add(Data.UPGRADES[k].Name);
            if (fac.Count > 0) extra = " The site came with a basic " + string.Join(" and ", fac) + ".";
            AddLog(string.Format("{0} took over {1} in {2}. Move people there from Crew & Jobs.{3}", b.Name, TakeName(lot), D.DistrictAt(b.Pos).Name, extra), "good");
            PlaySfx("hammer", V.F(b.Pos));
            EmitChanged();
            return "";
        }

        void StepBases()
        {
            foreach (var b in C.Bases)
            {
                if (b.Done >= 1.0) continue;
                int eng = C.People.Count(p => p.St == "work" && p.Goal == "build" && p.Lot == -b.Id);
                b.Done = Math.Min(1.0, b.Done + 1.0 / 36.0 + eng / 8.0);
                if (b.Done >= 1.0)
                {
                    AddLog(string.Format("{0} is up and running. Move people there from the Crew screen.", b.Name), "good");
                    foreach (var p in C.People)
                        if (p.Goal == "build" && p.Lot == -b.Id) p.Lot = -1;
                    _claimed.Remove(-b.Id);
                    _flowDirty = true;
                }
            }
        }

        public Dictionary<string, double> UpgradeCost(Base b, string uid)
        {
            var u = Data.UPGRADES[uid];
            int lvl = b.U(uid);
            if (lvl >= u.Max) return new Dictionary<string, double>();
            return u.Cost[lvl];
        }

        public string UpgradeBlock(Base b, string uid)
        {
            var u = Data.UPGRADES[uid];
            int lvl = b.U(uid);
            if (lvl >= u.Max) return "Already at max.";
            if (uid == "radio" && !b.Main) return "The radio goes in the main camp.";
            if (uid == "garage" && !b.GarageOk) return "No garage here. Needs a gas station, warehouse, precinct or factory nearby.";
            if (u.Unlock != null && !HasUnlock(u.Unlock)) return string.Format("Research {0} first.", UnlockSourceName(u.Unlock));
            if (lvl == 0 && (u.Slot ?? true) && SlotsUsed(b) >= BaseSlots(b)) return string.Format("No free building slot ({0}/{1}). Upgrade the base or fence in more ground.", SlotsUsed(b), BaseSlots(b));
            if (lvl + 1 >= 3 && b.Lvl < 1) return string.Format("Level 3 needs the base upgraded to {0} first.", b.Main ? "Settlement" : "Fortified Outpost");
            return "";
        }

        public string DoUpgrade(Base b, string uid)
        {
            if (b.Done < 1.0) return "Finish building it first.";
            var cost = UpgradeCost(b, uid);
            string blk = UpgradeBlock(b, uid);
            if (blk != "") return blk;
            if (!CanAfford(cost)) return "Need " + FmtCost(cost) + ".";
            Pay(cost);
            b.Up[uid] = b.U(uid) + 1;
            GrowClaim(b);
            PlaySfx("hammer", V.F(b.Pos));
            AddLog(string.Format("{0}: {1} built.", b.Name, Data.UPGRADES[uid].Name), "good");
            EmitChanged();
            return "";
        }

        public bool HasRadio() => C.Bases.Any(b => b.U("radio") > 0);

        // ---------------- survivor groups & rescues ----------------
        public static readonly string[] GROUP_LABELS = { "Family in an attic", "Office workers on the 9th floor", "Church basement", "Daycare staff and kids",
            "Neighbors in a garage", "Nurses in a stairwell", "Truckers at a depot", "Hotel staff on the roof", "Bus of stranded commuters",
            "Students in the library", "Mechanic's garage crew", "Shoppers in a storeroom", "Diner cooks", "Retirees in a care home" };

        Group SpawnGroup(bool nearPlayer = true, Vector2Int? anchorArg = null)
        {
            var anchor = anchorArg ?? V.NONE;
            if (anchor.x < 0)
            {
                var mains = AllBases().Where(b => b.Main).Select(b => b.Pos).ToList();
                if (mains.Count > 0) anchor = mains[Rng.RandiRange(0, mains.Count - 1)];
            }
            for (int t = 0; t < 60; t++)
            {
                int l;
                if (nearPlayer && anchor.x >= 0)
                {
                    var q = anchor.Add(Rng.RandiRange(-50, 50), Rng.RandiRange(-50, 50));
                    l = D.LotAt(q);
                    if (l < 0) continue;
                }
                else l = Rng.RandiRange(0, D.Lots.Count - 1);
                var door = D.LotDoor(l);
                if (InfAt(door) > 0.3) continue;
                if (Safe[Ti(door)] == 1) continue;
                if (anchor.x >= 0)
                {
                    double d = V.F(door).DistTo(V.F(anchor));
                    if (d < 10.0) continue;
                    if (nearPlayer && d > 50.0) continue;
                }
                var g = new Group { Id = NewId(), Lot = l, Pos = door, N = Rng.RandiRange(2, 6), Label = GROUP_LABELS[Rng.RandiRange(0, GROUP_LABELS.Length - 1)], St = "wait", Tf = false };
                S.Groups.Add(g);
                return g;
            }
            return null;
        }

        public Group GroupById(int gid) => S.Groups.FirstOrDefault(g => g.Id == gid);

        void StepGroups()
        {
            foreach (var g in S.Groups.ToList())
            {
                if (g.St != "wait") continue;
                double inf = InfAt(g.Pos);
                int zn = ZombiesNear(V.F(g.Pos).Add(V.HALF), 2.5, 6).Count;
                if ((inf > 0.65 && Rng.Randf() < 0.05f) || (zn >= 4 && Rng.Randf() < 0.15f))
                {
                    g.St = "gone";
                    C.AddStat("dead", g.N);
                    AddLog(string.Format("Lost contact with {0} in {1}. {2} more of the dead.", Lower(g.Label), D.DistrictAt(g.Pos).Name, g.N), "loss");
                    for (int i = 0; i < g.N; i++) SpawnZombie(Ti(g.Pos), 0);
                    S.Groups.Remove(g);
                }
            }
            if (S.Phase == "spread" && S.T >= S.NextGroupT)
            {
                int ncol = S.Cols.Count(ColAlive);
                Use(ViewCol);
                S.NextGroupT = S.T + Rng.RandfRange(24.0, 44.0) / Math.Max(1.0, ncol) / (1.0 + Rx("group_rate"));
                var g = SpawnGroup(true);
                if (g != null)
                    AddLog(string.Format("Radio chatter: {0} holding out in {1}. Tap them on the map to send a rescue.", Lower(g.Label), D.DistrictAt(g.Pos).Name), "major");
            }
        }

        void StepArrivals()
        {
            if (S.T >= C.NextArrivalT && S.Phase == "spread")
            {
                C.NextArrivalT = S.T + Rng.RandfRange(30.0, 60.0) * (HasRadio() ? 0.6 : 1.0) / (1.0 + Rx("group_rate"));
                var mb = MainBase();
                if (mb == null) return;
                if (BedsFree() > 0)
                {
                    var p = AddPerson(D.RandomJob(Rng), mb.Id, V.F(mb.Pos));
                    AddLog(string.Format("A stranger, {0} ({1}), walked up to the gate and joined.", p.Name, p.Job), "good");
                    PlaceArrivals(new List<Person> { p }, mb.Id);
                }
                else Refuse(1, string.Format("A stranger came to the gate, but there was no bed to offer. {0}", NO_BEDS_MSG));
            }
        }

        public string SendRescue(int gid, bool auto = false, int bid = -1)
        {
            var g = GroupById(gid);
            if (g == null || g.St != "wait") return "They're gone.";
            if (g.Tf) return "A team is already on the way.";
            if (BedsFree() <= 0) return NO_BEDS_MSG;
            if (bid >= 0 && BaseById(bid) == null) return "That camp isn't yours.";
            var team = PickTeam(V.F(g.Pos), 3, bid);
            if (team.Count == 0) return bid >= 0 ? string.Format("Nobody fit is at {0} right now.", BaseById(bid).Name) : "Nobody fit is at a camp right now.";
            var tb = BaseById(team[0].Base);
            bool drove = TakeVehicle(team, tb, g.Pos);
            foreach (var p in team)
            {
                if (!Go(p, g.Pos, "tf")) continue;
                p.Tf = gid;
            }
            g.Tf = true;
            AddLog(string.Format("{0} ({1}) heading out for {2}{3}.", auto ? "Auto-rescue" : "Task force", string.Join(", ", team.Select(x => x.First)), Lower(g.Label), drove ? " by truck" : ""), "info");
            PlaySfx("click", V.F(g.Pos));
            EmitChanged();
            return "";
        }

        public string SendRaid(int l, int bid = -1)
        {
            if (S.Loot[l] < 0.15f || Searched(l)) return "Already searched.";
            if (bid >= 0 && BaseById(bid) == null) return "That camp isn't yours.";
            var team = PickTeam(D.LotCenter(l), 3, bid);
            if (team.Count == 0) return "Nobody fit is at a camp right now.";
            TakeVehicle(team, BaseById(team[0].Base), D.LotDoor(l));
            foreach (var p in team)
                if (Go(p, D.LotDoor(l), "tf")) p.Tf = -1000000 - l;
            AddLog(string.Format("Task force heading to {0} for supplies.", D.LotName(l)), "info");
            EmitChanged();
            return "";
        }

        List<Person> PickTeam(Vector2 target, int n, int bid = -1)
        {
            var cands = new List<(double pri, Person p)>();
            foreach (var b in C.Bases)
            {
                if (b.Done < 1.0) continue;
                if (bid >= 0 && b.Id != bid) continue;
                foreach (var p in AtBase(b.Id))
                {
                    if (p.Hp < 50.0 || (p.Inf >= 0.0 && p.Known)) continue;
                    if (p.Sq > 0 || p.Hold) continue;
                    if (IsMinor(p)) continue;
                    int pri = p.Job == "Soldier" ? 0 : p.Job == "Cop" ? 1 : p.Job == "Scavenger" ? 2 : p.Job == "Civilian" ? 3 : 9;
                    if (pri >= 9) continue;
                    cands.Add((pri * 1000.0 + V.F(b.Pos).DistTo(target), p));
                }
            }
            Gd.StableSort(cands, (a, b) => a.pri.CompareTo(b.pri));
            return cands.Take(n).Select(c => c.p).ToList();
        }

        void ResolveTf(Person p)
        {
            int tid = p.Tf;
            p.Tf = 0;
            if (tid <= -1000000)
            {
                int l = -tid - 1000000;
                TakeLoot(p, l, 1.0);
                return;
            }
            var g = GroupById(tid);
            if (g == null || g.St != "wait") return;
            int take = BedRoom(g.N);
            if (take <= 0)
            {
                g.Tf = false;
                Refuse(g.N, string.Format("The team reached {0}, but there are no free beds, so they stayed put. {1}", Lower(g.Label), NO_BEDS_MSG));
                return;
            }
            if (take < g.N)
            {
                g.N -= take;
                g.Tf = false;
                C.AddStat("rescued", take);
                var nm = new List<string>();
                var arr2 = new List<Person>();
                for (int i = 0; i < take; i++)
                {
                    var np2 = AddPerson(D.RandomJob(Rng), p.Base, V.F(g.Pos).Add(V.HALF));
                    nm.Add(np2.Job);
                    if (Rng.Randf() < 0.12f) np2.Inf = Rng.RandfRange(10.0, 30.0);
                    Go(np2, HomePos(np2), "home");
                    arr2.Add(np2);
                }
                C.AddStat("refused", g.N);
                AddLog(string.Format("Rescued {0} of {1} ({2}); {3} stayed behind: no free beds. {4}", take, Lower(g.Label), string.Join(", ", nm), g.N, NO_BEDS_MSG), "good");
                PlaceArrivals(arr2, p.Base);
                return;
            }
            g.St = "saved";
            S.Groups.Remove(g);
            C.AddStat("rescued", g.N);
            var names = new List<string>();
            var arr = new List<Person>();
            for (int i = 0; i < g.N; i++)
            {
                var np = AddPerson(D.RandomJob(Rng), p.Base, V.F(g.Pos).Add(V.HALF));
                names.Add(np.Job);
                if (Rng.Randf() < 0.12f) np.Inf = Rng.RandfRange(10.0, 30.0);
                Go(np, HomePos(np), "home");
                arr.Add(np);
            }
            AddLog(string.Format("Rescued {0}: {1} people ({2}) are heading to camp.", Lower(g.Label), g.N, string.Join(", ", names)), "good");
            PlaceArrivals(arr, p.Base);
            PlaySfx("click", V.F(g.Pos));
        }

        // ---------------- searched lots & loot regrowth ----------------
        public const double AUTO_R_MIN = 12.0;
        public const double AUTO_R_BASE = 30.0;
        public const double AUTO_R_DEFAULT = 26.0;
        public const double AUTO_R_CAP = 72.0;
        public const double REGROW_H = 240.0;

        float[] SrchNew() { var a = new float[D.Lots.Count]; Array.Fill(a, -1f); return a; }
        byte[] SrchCNew() => new byte[D.Lots.Count];
        static AutoCfg AutoNew() => new AutoCfg { R = AUTO_R_DEFAULT, Scav = true, Rescue = true, Conv = true, Guards = true };

        public bool Searched(int l) => l >= 0 && l < S.Srch.Length && S.Srch[l] >= 0f;
        public int SearchedBy(int l) => Searched(l) ? S.SrchC[l] : 0;

        public void MarkSearched(int l, int who)
        {
            if (l < 0 || l >= D.Lots.Count) return;
            S.Srch[l] = (float)S.T;
            S.SrchC[l] = (byte)V.Clampi(who, 0, 255);
            S.Loot[l] = Math.Min(S.Loot[l], 0.1f);
        }

        public int SearchedCount(int who = -1)
        {
            int n = 0;
            for (int i = 0; i < S.Srch.Length; i++)
                if (S.Srch[i] >= 0f && (who < 0 || S.SrchC[i] == who)) n++;
            return n;
        }

        void RegrowLoot()
        {
            if (Hour() != 3) return;
            float[] a = S.Srch;
            double t = S.T;
            for (int i = 0; i < a.Length; i++)
                if (a[i] >= 0f && t - a[i] > REGROW_H && !S.Conv.Has(i) && !S.Gut.Has(i))
                {
                    a[i] = -1f;
                    S.SrchC[i] = 0;
                    S.Loot[i] = Math.Max(S.Loot[i], 0.6f);
                }
        }

        void MaybeFindPeople(Person p, int l, double chance)
        {
            if (Rng.Randf() > chance * (1.0 + Rx("find_people"))) return;
            int want = Rng.RandiRange(1, 2);
            int n = BedRoom(want);
            if (n <= 0)
            {
                Refuse(want, string.Format("{0} found people hiding in {1}, but there are no free beds to bring them home to. {2}", p.Name, D.LotName(l), NO_BEDS_MSG));
                return;
            }
            var names = new List<string>();
            var arr = new List<Person>();
            for (int i = 0; i < n; i++)
            {
                var np = AddPerson(D.RandomJob(Rng), p.Base, p.Pos);
                if (Rng.Randf() < 0.1f) np.Inf = Rng.RandfRange(10.0, 30.0);
                Go(np, HomePos(np), "home");
                names.Add(np.First);
                arr.Add(np);
            }
            C.AddStat("rescued", n);
            AddLog(string.Format("{0} found {1} hiding in {2} and brought them along.", p.Name, string.Join(" and ", names), D.LotName(l)), "good");
            PlaceArrivals(arr, p.Base);
        }

        public double AutoRMax() => Math.Min(AUTO_R_CAP, AUTO_R_BASE + Rx("auto_r") + Rx("scav_range"));
        public double AutoRadius() => V.Clamp(C.Auto.R, AUTO_R_MIN, AutoRMax());

        public bool InAutoZone(Vector2 p)
        {
            double r = AutoRadius();
            foreach (var b in C.Bases)
                if (b.Done >= 1.0 && V.F(b.Pos).DistTo(p) <= r) return true;
            var pi = new Vector2Int((int)p.x, (int)p.y);
            if (D.InMap(pi))
            {
                var eb = EnclBaseAt(Ti(pi));
                if (eb == null)
                {
                    int bl = D.BuildingAt(pi);
                    if (bl >= 0)
                    {
                        var dq = D.NearestWalkable(D.LotDoor(bl), 3);
                        if (D.InMap(dq)) eb = EnclBaseAt(Ti(dq));
                    }
                }
                if (eb != null && eb.Col == CurCol) return true;
            }
            return false;
        }

        public static readonly string[] CAMP_AUTO_KEYS = { "scav", "rescue", "conv" };

        public bool CampAuto(Base b, string key)
        {
            bool dflt = C.Auto.Get(key);
            if (CurCol >= 0 && CurCol < S.Cols.Count && S.Cols[CurCol].Away) return dflt;
            if (b.Auto != null && b.Auto.TryGetValue(key, out bool v)) return v;
            return dflt;
        }

        /// <summary>set_auto(key, v, bid). v: number for "r", bool for the others.</summary>
        public string SetAuto(string key, object v, int bid = -1)
        {
            if (bid >= 0)
            {
                var b = BaseById(bid);
                if (b == null) return "That camp isn't yours.";
                if (!CAMP_AUTO_KEYS.Contains(key)) return "That setting is for the whole colony.";
                if (b.Auto == null) b.Auto = new ODict<string, bool>();
                b.Auto[key] = Convert.ToBoolean(v);
                EmitChanged();
                return "";
            }
            if (CAMP_AUTO_KEYS.Contains(key))
                foreach (var b in C.Bases) b.Auto?.Remove(key);
            switch (key)
            {
                case "r": C.Auto.R = V.Clamp(Convert.ToDouble(v), AUTO_R_MIN, AutoRMax()); break;
                case "scav": C.Auto.Scav = Convert.ToBoolean(v); break;
                case "rescue": C.Auto.Rescue = Convert.ToBoolean(v); break;
                case "conv": C.Auto.Conv = Convert.ToBoolean(v); break;
                case "guards": C.Auto.Guards = Convert.ToBoolean(v); break;
                default: return "Unknown setting.";
            }
            EmitChanged();
            return "";
        }

        void AutoRescue()
        {
            if (!C.Bases.Any(b => b.Done >= 1.0 && CampAuto(b, "rescue"))) return;
            foreach (var b in C.Bases.ToList())
            {
                if (b.Done < 1.0 || !CampAuto(b, "rescue")) continue;
                string key = b.Main ? "auto_rescue" : "auto_rescue_" + b.Id;
                if (S.T < C.Cd.Get(key, -1000000000.0)) continue;
                int room = BedsFree();
                if (room <= 0) return;
                int outN = C.People.Count(p => p.Goal == "tf" && p.St != "base");
                if (outN >= 6) return;
                Group best = null; double bd = 1000000000.0;
                foreach (var g in S.Groups)
                {
                    if (g.St != "wait" || g.Tf) continue;
                    if (InfAt(g.Pos) > 0.6) continue;
                    var gp = V.F(g.Pos);
                    if (!InAutoZone(gp)) continue;
                    if (g.N > room) continue;
                    if (NearestCampId(gp) != b.Id) continue;
                    double d = V.F(b.Pos).DistTo(gp);
                    if (d < bd) { bd = d; best = g; }
                }
                if (best == null) continue;
                if (PickTeam(V.F(best.Pos), 3, b.Id).Count < 2) continue;
                if (SendRescue(best.Id, true, b.Id) == "") C.Cd[key] = S.T + 6.0;
            }
        }
    }
}
