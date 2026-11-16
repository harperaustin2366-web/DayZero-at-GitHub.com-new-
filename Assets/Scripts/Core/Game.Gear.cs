// game.gd: weapons, armour and gun parts (loadouts, crafting queue, gun noise) and
// vehicles: garage, repairs, fuel, driving squads, wrecks and towing.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DayZero.Core
{
    public sealed partial class Game
    {
        public const int CRAFT_MAX = 8;
        public static readonly string[] SLOTS = { "wpn", "vest", "helm" };
        readonly Dictionary<int, double> _gunNoise = new Dictionary<int, double>();
        public static readonly Dictionary<string, double> AMMO_CRAFT = Gd.D(("metal", 4), ("plastic", 2));

        public WeaponDef WeaponOf(Person p)
        {
            string w = p.Wpn ?? "";
            if (w == "" || !Data.WEAPONS.ContainsKey(w)) return Data.UNARMED;
            return Data.WEAPONS[w];
        }

        void GearInit(Colony c)
        {
            if (c.Kit == null) c.Kit = new ODict<string, int>();
            if (c.Craft == null) c.Craft = new List<CraftJob>();
            if (c.Weapons == null) c.Weapons = new ODict<string, int>();
            foreach (var g in Data.WEAPONS.Keys) if (!c.Weapons.Has(g)) c.Weapons[g] = 0;
            foreach (var k in Data.GPARTS.Keys.Concat(Data.ARMOR.Keys)) if (!c.Kit.Has(k)) c.Kit[k] = 0;
            foreach (var p in c.People) { p.Wpn ??= ""; p.Vest ??= ""; p.Helm ??= ""; }
        }

        public bool HasArmory() => ConvN("armory") > 0 || BestArmory() > 0;
        public bool HasWorkshop() => ConvN("workshop") > 0 || C.Bases.Any(b => b.U("workshop") > 0);
        public int ArmoryLevel() => Math.Max(BestArmory(), Math.Min(3, ConvN("armory")));

        public int GearTotal(string id) => Data.WEAPONS.ContainsKey(id) ? C.Weapons.Get(id, 0) : C.Kit.Get(id, 0);

        void GearAdd(string id, int n)
        {
            if (Data.WEAPONS.ContainsKey(id)) C.Weapons[id] = Math.Max(0, C.Weapons.Get(id, 0) + n);
            else C.Kit[id] = Math.Max(0, C.Kit.Get(id, 0) + n);
        }

        public static string SlotOf(string id)
        {
            if (Data.WEAPONS.ContainsKey(id)) return "wpn";
            if (Data.ARMOR.TryGetValue(id, out var a)) return a.Slot;
            return "";
        }

        public int GearWorn(string id)
        {
            string s = SlotOf(id);
            if (s == "") return 0;
            return C.People.Count(p => p.St != "dead" && p.Slot(s) == id);
        }

        public int GearFree(string id) => Math.Max(0, GearTotal(id) - GearWorn(id));

        public double ArmorOf(Person p)
        {
            double a = 0.0;
            foreach (var id in new[] { p.Vest ?? "", p.Helm ?? "" })
                if (Data.ARMOR.TryGetValue(id, out var ad)) a += ad.Armor;
            return Math.Min(Data.ARMOR_CAP, a);
        }

        void FixLoadouts()
        {
            foreach (var s in SLOTS)
            {
                var by = new ODict<string, List<Person>>();
                foreach (var p in C.People)
                {
                    string id = p.Slot(s);
                    if (id == "") continue;
                    if (SlotOf(id) != s) { p.SetSlot(s, ""); continue; }
                    if (!by.Has(id)) by[id] = new List<Person>();
                    by[id].Add(p);
                }
                foreach (var kv in by)
                {
                    int over = kv.Value.Count - GearTotal(kv.Key);
                    if (over <= 0) continue;
                    var arr = kv.Value;
                    Gd.StableSort(arr, (a, b) => GuardRank(b).CompareTo(GuardRank(a)));
                    for (int i = 0; i < over; i++) arr[i].SetSlot(s, "");
                }
            }
        }

        bool IsFighter(Person p) => p.Job == "Soldier" || p.Job == "Cop" || p.Sq > 0 || p.Guard;

        int GuardRank(Person p)
        {
            if (IsMinor(p)) return 99;
            int r = p.Sq > 0 ? 0 : 4;
            r += p.Job == "Soldier" ? 0 : p.Job == "Cop" ? 1 : p.Job == "Scavenger" ? 3 : (p.Guard ? 2 : 6);
            return r;
        }

        void Equip()
        {
            FixLoadouts();
            var ppl = C.People.Where(p => p.St != "dead" && IsFighter(p) && !IsMinor(p)).ToList();
            Gd.StableSort(ppl, (a, b) => GuardRank(a).CompareTo(GuardRank(b)));
            Deal(ppl, false);
        }

        void Deal(List<Person> ppl, bool allSlots)
        {
            var free = new Dictionary<string, int>();
            foreach (var id in Data.WEAPONS.Keys.Concat(Data.ARMOR.Keys)) free[id] = GearFree(id);
            foreach (var p in ppl)
                foreach (var s in SLOTS)
                {
                    if (p.Slot(s) != "" && !allSlots) continue;
                    var order = s == "wpn" ? Data.WEAPON_ORDER : Data.ARMOR_ORDER;
                    foreach (var id in order)
                        if (SlotOf(id) == s && free.GetOr(id, 0) > 0)
                        {
                            free[id]--; p.SetSlot(s, id);
                            break;
                        }
                }
        }

        public string AutoEquip()
        {
            if (!HasArmory()) return "Needs an Armory: convert a gutted building to an Armory first.";
            var ppl = new List<Person>();
            foreach (var p in C.People)
            {
                if (p.St == "dead") continue;
                foreach (var s in SLOTS) p.SetSlot(s, "");
                if (!IsMinor(p)) ppl.Add(p);
            }
            Gd.StableSort(ppl, (a, b) => GuardRank(a).CompareTo(GuardRank(b)));
            Deal(ppl, true);
            AddLog("The armory re-issued every weapon, vest and helmet.", "info");
            EmitChanged();
            return "";
        }

        public string EquipSlot(int pid, string slot, string id)
        {
            if (!SLOTS.Contains(slot)) return "Unknown slot.";
            if (!HasArmory()) return "Needs an Armory to change loadouts.";
            var p = OwnPerson(pid);
            if (p == null) return "That survivor isn't in your colony.";
            if (id != "")
            {
                if (SlotOf(id) != slot) return "That doesn't go there.";
                if (IsMinor(p)) return "Kids don't carry weapons or armour.";
                if (p.Slot(slot) != id && GearFree(id) < 1) return string.Format("No free {0} on the rack.", Lower(Data.GearDefOf(id).Name));
            }
            p.SetSlot(slot, id);
            EmitChanged();
            return "";
        }

        public int BestArmory() => C.Bases.Count == 0 ? 0 : C.Bases.Max(b => b.U("armory"));

        public Dictionary<string, double> CraftCost(string id) => Data.GearDefOf(id)?.Cost ?? new Dictionary<string, double>();

        public string CraftError(string id)
        {
            var d = Data.GearDefOf(id);
            if (d == null) return "Unknown item.";
            if (C.Craft.Count >= CRAFT_MAX) return string.Format("The queue is full ({0} jobs).", CRAFT_MAX);
            if (Data.GPARTS.ContainsKey(id))
            {
                if (!HasWorkshop()) return "Needs a Workshop (convert a gutted building, or a camp's Workshop upgrade).";
            }
            else
            {
                if (!HasArmory()) return "Needs an Armory.";
                if (ArmoryLevel() < (d.Armory ?? 1)) return string.Format("Needs an Armory level {0} (more Armories or the camp's Armory upgrade).", d.Armory ?? 1);
                if (d.Parts != null)
                    foreach (var kv in d.Parts)
                        if (C.Kit.Get(kv.Key, 0) < kv.Value) return string.Format("Need {0}.", PartsText(d.Parts));
            }
            if (!CanAfford(d.Cost ?? new Dictionary<string, double>())) return "Need " + FmtCost(d.Cost) + ".";
            return "";
        }

        static string PartsText(Dictionary<string, int> parts) => string.Join(", ", parts.Select(kv => string.Format("{0} {1}", kv.Value, Lower(Data.GPARTS[kv.Key].Name))));

        public string Craft(string id)
        {
            string e = CraftError(id);
            if (e != "") return e;
            var d = Data.GearDefOf(id);
            Pay(d.Cost ?? new Dictionary<string, double>());
            if (d.Parts != null) foreach (var kv in d.Parts) C.Kit[kv.Key] = C.Kit[kv.Key] - kv.Value;
            string kind = Data.GPARTS.ContainsKey(id) ? "part" : (Data.WEAPONS.ContainsKey(id) ? "gun" : "arm");
            C.Craft.Add(new CraftJob { Kind = kind, Id = id, End = -1.0, Hours = d.H ?? 3.0 });
            CraftStart();
            PlaySfx("hammer", new Vector2(-1, -1));
            EmitChanged();
            return "";
        }

        public string CraftCancel(int i)
        {
            var q = C.Craft;
            if (i < 0 || i >= q.Count) return "Nothing there.";
            var j = q[i];
            var d = Data.GearDefOf(j.Id);
            if (d?.Cost != null) foreach (var kv in d.Cost) C.Res[kv.Key] = R(kv.Key) + kv.Value;
            if (d?.Parts != null) foreach (var kv in d.Parts) C.Kit[kv.Key] = C.Kit.Get(kv.Key, 0) + kv.Value;
            q.RemoveAt(i);
            CraftStart();
            EmitChanged();
            return "";
        }

        public double CraftSpeed(string kind)
        {
            if (kind == "part")
            {
                int n = ConvN("workshop");
                foreach (var b in C.Bases) n += b.U("workshop");
                return 1.0 + 0.5 * Math.Max(0, n - 1);
            }
            return 1.0 + 0.5 * Math.Max(0, ArmoryLevel() - 1);
        }

        void CraftStart()
        {
            var q = C.Craft;
            if (q.Count == 0) return;
            var j = q[0];
            if (j.End < 0.0) j.End = S.T + j.Hours / CraftSpeed(j.Kind);
        }

        void StepCraft()
        {
            var q = C.Craft;
            if (q == null || q.Count == 0) return;
            int guard = 0;
            while (q.Count > 0 && q[0].End >= 0.0 && S.T >= q[0].End && guard < 20)
            {
                guard++;
                var j = q[0]; q.RemoveAt(0);
                GearAdd(j.Id, 1);
                AddLog(string.Format("{0} finished: {1}.", j.Kind == "part" ? "The workshop" : "The armory", A(Lower(Data.GearDefOf(j.Id).Name))), "good");
                if (q.Count > 0) q[0].End = S.T + q[0].Hours / CraftSpeed(q[0].Kind);
                EmitChanged();
            }
            if (q.Count > 0 && q[0].End < 0.0) CraftStart();
        }

        public double CraftProgress()
        {
            var q = C.Craft;
            if (q.Count == 0 || q[0].End < 0.0) return 0.0;
            double dur = q[0].Hours / CraftSpeed(q[0].Kind);
            return V.Clamp(1.0 - (q[0].End - S.T) / Math.Max(0.01, dur), 0.0, 1.0);
        }

        static readonly string[] GEAR_PARTS_LOOT = { "g_barrel", "g_receiver", "g_trigger", "g_mag", "g_stock", "g_plate" };

        void GearLoot(Person p, int l)
        {
            if (!Data.GEAR_DROPS.TryGetValue(D.LotKind(l), out var drops)) return;
            foreach (var kv in drops)
            {
                if (Rng.Randf() >= kv.Value * S.Loot[l]) continue;
                string id = kv.Key;
                if (id == "parts") id = GEAR_PARTS_LOOT[Rng.RandiRange(0, 5)];
                GearAdd(id, 1);
                AddLog(string.Format("{0} found {1} at {2}.", p.Name, A(Lower(Data.GearDefOf(id).Name)), D.LotName(l)), "good");
            }
        }

        void GunShot(WeaponDef w) => _gunNoise[CurCol] = _gunNoise.GetOr(CurCol, 0.0) + (w.Noise ?? 0);

        void StepNoise()
        {
            double n = _gunNoise.GetOr(CurCol, 0.0);
            _gunNoise[CurCol] = 0.0;
            if (n < 120.0 || MaxZombies <= 0) return;
            var mb = MainBase();
            if (mb == null) return;
            int k = Math.Min(4, (int)(n / 120.0));
            for (int i = 0; i < k; i++)
            {
                double a = Rng.Randf() * V.TAU;
                var t = V.I(V.F(mb.Pos).Add(V.F(Math.Cos(a), Math.Sin(a)).Mul(Rng.RandfRange(12.0, 18.0))));
                t = new Vector2Int(V.Clampi(t.x, 0, D.W - 1), V.Clampi(t.y, 0, D.H - 1));
                SpawnZombie(Ti(D.NearestWalkable(t)), 0);
            }
            if (k >= 2) AddLog(string.Format("The gunfire carried. More of the dead are drifting toward {0}.", mb.Name), "loss");
        }

        public double Blow(Person p, double dmg) => dmg * (1.0 - ArmorOf(p));

        public bool ArmorBlocks(Person p)
        {
            double a = ArmorOf(p);
            return a > 0.0 && Rng.Randf() < a;
        }

        void LoseGear(Person p)
        {
            foreach (var s in SLOTS)
            {
                string id = p.Slot(s);
                if (id != "") { GearAdd(id, -1); p.SetSlot(s, ""); }
            }
        }

        public string CraftAmmo()
        {
            if (BestArmory() < 1) return "Needs an Armory.";
            if (!CanAfford(AMMO_CRAFT)) return "Need " + FmtCost(AMMO_CRAFT) + ".";
            Pay(AMMO_CRAFT);
            C.Res["ammo"] = R("ammo") + 10.0;
            EmitChanged();
            return "";
        }

        // ---------------- vehicles ----------------
        public Dictionary<string, double> VehicleCost(Base b)
        {
            int n = b.Veh;
            return Gd.D(("metal", 12 + 10 * n), ("wire", 4 + 3 * n), ("elec", 4 + 2 * n), ("fuel", 5));
        }

        public string CanRebuildVehicle(Base b)
        {
            if (!HasGarage(b)) return "Needs a Garage.";
            if (GarageUsed(b) >= GarageCap(b)) return "The Garage is full. Upgrade it or convert another building to a Garage.";
            foreach (var part in Data.VEHICLE_PARTS)
                if (C.Parts.Get(part, 0) < 1) return string.Format("Missing a {0}. Scavenge gas stations, parking lots, warehouses.", Lower(Data.PART_LABEL[part]));
            if (!CanAfford(VehicleCost(b))) return "Need " + FmtCost(VehicleCost(b)) + ".";
            return "";
        }

        public string RebuildVehicle(Base b)
        {
            string e = CanRebuildVehicle(b);
            if (e != "") return e;
            Pay(VehicleCost(b));
            foreach (var part in Data.VEHICLE_PARTS) C.Parts[part] = C.Parts[part] - 1;
            string typ = new[] { "pickup", "truck", "humvee" }[Math.Min(GarageUsed(b), 2)];
            C.Vehs.Add(new Vehicle { Id = VehId(), Type = typ, Base = b.Id, Hp = 1.0, Fuel = 5.0, Sq = -1, St = "ok", End = 0.0, Drv = -1 });
            VehBump(); VehSyncParked();
            AddLog(string.Format("{0}: the {1} runs again! Assign it to a squad in GARAGE, or scavengers and task forces take it.", b.Name, Lower(Data.VehDef(typ).Name)), "major");
            PlaySfx("hammer", V.F(b.Pos));
            EmitChanged();
            return "";
        }

        int VehId()
        {
            int i = S.VehN;
            S.VehN = i + 1;
            return i;
        }

        void VehBump() => S.VehVer++;

        public Vehicle VehicleById(int vid) => C.Vehs.FirstOrDefault(v => v.Id == vid);
        public Wreck WreckById(int wid) => S.Wrecks.FirstOrDefault(w => w.Id == wid);

        public Wreck WreckNear(Vector2Int tile, double r = 1.3)
        {
            Wreck best = null; double bd = r;
            foreach (var w in S.Wrecks)
            {
                double d = V.F(w.Pos).Add(V.HALF).DistTo(V.F(tile).Add(V.HALF));
                if (d <= bd) { bd = d; best = w; }
            }
            return best;
        }

        public int GarageCap(Base b) => b == null ? 0 : 2 * ConvN("garage", b.Id) + b.U("garage");
        public bool HasGarage(Base b) => GarageCap(b) > 0;

        public int GarageUsed(Base b) => C.Vehs.Count(v => v.Base == b.Id) + C.Squads.Count(q => q.Base == b.Id && q.Towing != null);

        public Dictionary<string, double> VehRepairCost(Vehicle v)
        {
            var d = Data.VehDef(v.Type);
            double f = V.Clamp(1.0 - v.Hp, d.MinFix ?? 0.15, 1.0);
            var c = new Dictionary<string, double>();
            foreach (var kv in d.Fix) c[kv.Key] = (int)Math.Ceiling(kv.Value * f);
            return c;
        }

        public double VehRepairHours(Vehicle v)
        {
            var d = Data.VehDef(v.Type);
            int ng = Math.Max(1, ConvN("garage", v.Base));
            return Math.Max(1.0, d.Rh * V.Clamp(1.0 - v.Hp, 0.15, 1.0) / (1.0 + 0.5 * (ng - 1)) / (1.0 + Rx("repair_mult")));
        }

        public string VehRepairError(Vehicle v)
        {
            if (v == null) return "No such vehicle.";
            if (v.St == "out") return "It's out on the road.";
            if (v.St == "repair") return "Already being repaired.";
            if (v.Hp >= 0.999 && v.Need.Count == 0) return "Nothing to fix.";
            var b = BaseById(v.Base);
            if (!HasGarage(b)) return string.Format("Needs a Garage at {0}.", b?.Name ?? "its camp");
            int glv = Data.VehDef(v.Type).GarageLvl ?? 0;
            if ((b?.U("garage") ?? 0) < glv)
                return string.Format("A {0} needs a level {1} Garage upgrade at {2}.", Data.VehDef(v.Type).Name, glv, b?.Name ?? "its camp");
            foreach (var part in v.Need)
                if (C.Parts.Get(part, 0) < 1) return string.Format("Missing a {0}. Scavenge gas stations, parking lots, warehouses.", Lower(Data.PART_LABEL.TryGetValue(part, out var pl) ? pl : part));
            var c = VehRepairCost(v);
            if (!CanAfford(c)) return "Need " + FmtCost(c) + ".";
            return "";
        }

        public string RepairVehicle(int vid)
        {
            var v = VehicleById(vid);
            string e = VehRepairError(v);
            if (e != "") return e;
            Pay(VehRepairCost(v));
            foreach (var part in v.Need) C.Parts[part] = C.Parts[part] - 1;
            v.Need = new List<string>();
            v.St = "repair";
            v.End = S.T + VehRepairHours(v);
            VehBump();
            AddLog(string.Format("{0}: mechanics start on the {1} ({2:0}h).", BaseById(v.Base)?.Name ?? "Garage", Lower(Data.VehDef(v.Type).Name), v.End - S.T), "info");
            PlaySfx("hammer", V.F(BaseById(v.Base)?.Pos ?? new Vector2Int(0, 0)));
            EmitChanged();
            return "";
        }

        public string RefuelVehicle(int vid)
        {
            var v = VehicleById(vid);
            if (v == null) return "No such vehicle.";
            if (v.St == "out") return "It's out on the road.";
            double room = Data.VehDef(v.Type).Tank - v.Fuel;
            if (room < 0.5) return "The tank is full.";
            double amt = Math.Min(room, R("fuel"));
            if (amt < 0.5) return "No fuel in the stores. Scavenge gas stations, siphon wrecks or trade for it.";
            C.Res["fuel"] = R("fuel") - amt;
            v.Fuel += amt;
            VehBump();
            EmitChanged();
            return "";
        }

        public string AssignVehicle(int vid, int sid)
        {
            var v = VehicleById(vid);
            if (v == null) return "No such vehicle.";
            if (v.St == "out") return "It's out on the road.";
            if (sid < 0) { v.Sq = -1; VehBump(); EmitChanged(); return ""; }
            var q = SquadById(sid);
            if (q == null) return "No such squad.";
            if (q.Base != v.Base) return string.Format("{0} is based at another camp.", q.Name);
            foreach (var o in C.Vehs) if (o.Sq == sid && o != v) o.Sq = -1;
            v.Sq = sid;
            VehBump();
            EmitChanged();
            return "";
        }

        public static bool VehRuns(Vehicle v) => v.St == "ok" && v.Hp > 0.0 && v.Need.Count == 0;

        void StepVehicles()
        {
            bool ch = false;
            foreach (var v in C.Vehs)
                if (v.St == "repair" && S.T >= v.End)
                {
                    v.St = "ok"; v.Hp = 1.0; ch = true;
                    AddLog(string.Format("{0}: the {1} is fixed and ready to drive.", BaseById(v.Base)?.Name ?? "Garage", Lower(Data.VehDef(v.Type).Name)), "good");
                }
            VehSyncParked();
            if (ch) { VehBump(); EmitChanged(); }
        }

        void VehSyncParked()
        {
            foreach (var b in C.Bases) b.Veh = C.Vehs.Count(v => v.Base == b.Id && v.St != "out");
        }

        void VehFlags(List<Person> team, Vehicle v)
        {
            var d = Data.VehDef(v.Type);
            for (int i = 0; i < team.Count; i++)
            {
                var p = team[i];
                p.Veh = true; p.Vlead = i == 0;
                p.Vt = v.Type; p.Vspd = d.Speed; p.Vcargo = Data.VehCargoMult(v.Type); p.Varm = d.Armor * V.Clamp(v.Hp + 0.3, 0.3, 1.0);
            }
            v.Drv = team.Count > 0 ? team[0].Id : -1;
        }

        static void VehUnflag(Person p)
        {
            p.Veh = false; p.Vlead = false;
            p.Vt = null; p.Vspd = null; p.Vcargo = null; p.Varm = null; p.Vid = null;
        }

        public int VehiclesFree(Base b) => C.Vehs.Count(v => v.Base == b.Id && v.Sq < 0 && VehRuns(v) && v.Fuel >= 1.0);

        bool TakeVehicle(List<Person> team, Base b, Vector2Int? tgtArg = null)
        {
            var tgt = tgtArg ?? V.NONE;
            if (team.Count == 0 || b == null) return false;
            foreach (var v in C.Vehs)
            {
                if (v.Base != b.Id || v.Sq >= 0 || !VehRuns(v)) continue;
                var d = Data.VehDef(v.Type);
                if (team.Count > d.Seats) continue;
                double dist = tgt.x < 0 ? 8.0 : V.F(tgt).DistTo(V.F(b.Pos)) * 1.3;
                double need = Math.Max(0.5, 2.0 * dist * d.Use);
                if (v.Fuel < need) continue;
                v.Fuel -= need;
                v.St = "out";
                VehFlags(team, v);
                team[0].Vid = v.Id;
                VehBump(); VehSyncParked();
                return true;
            }
            return false;
        }

        void VehHome(Person p)
        {
            int vid = p.Vid ?? -1;
            if (vid >= 0)
            {
                var v = VehicleById(vid);
                if (v != null && v.St == "out" && v.Sq < 0)
                {
                    v.St = "ok"; v.Drv = -1; v.Base = p.Base;
                    VehBump(); VehSyncParked();
                }
            }
            VehUnflag(p);
            p.Tow = false;
        }

        public Vehicle SquadVehicle(Squad q) => q == null ? null : C.Vehs.FirstOrDefault(v => v.Sq == q.Id);

        bool VehDepart(Squad q, List<Person> mem, List<Vector2> path)
        {
            q.Vid = -1; q.Vret = false;
            var v = SquadVehicle(q);
            if (v == null || !VehRuns(v))
            {
                v = null;
                if (HasUnlock("squad_veh"))
                    foreach (var o in C.Vehs)
                        if (o.Base == q.Base && o.Sq < 0 && VehRuns(o)) { v = o; break; }
            }
            if (v == null || !VehRuns(v) || v.Base != q.Base) return false;
            var d = Data.VehDef(v.Type);
            if (mem.Count > d.Seats)
            {
                AddLog(string.Format("{0}: the {1} seats {2}, so the squad walks.", q.Name, Lower(d.Name), d.Seats), "info");
                return false;
            }
            double need = path.Count * d.Use;
            if (v.Fuel < need)
            {
                AddLog(string.Format("{0}: not enough fuel in the {1} for the way out, so the squad walks.", q.Name, Lower(d.Name)), "info");
                return false;
            }
            v.Fuel -= need;
            v.St = "out";
            VehFlags(mem, v);
            q.Vid = v.Id;
            VehBump(); VehSyncParked();
            return true;
        }

        void VehReturn(Squad q)
        {
            if (q.Vret || q.Vid < 0) return;
            q.Vret = true;
            var v = VehicleById(q.Vid);
            if (v == null || v.St != "out") return;
            var d = Data.VehDef(v.Type);
            double need = q.Route.Count * d.Use;
            if (v.Fuel >= need)
            {
                v.Fuel -= need;
                VehBump();
                return;
            }
            v.Fuel = 0.0;
            VehAbandon(q, v, "ran out of fuel");
        }

        void VehAbandon(Squad q, Vehicle v, string why)
        {
            var d = Data.VehDef(v.Type);
            var at = q.Tgt;
            foreach (var p in SquadMembers(q))
                if (p.St != "dead" && p.Veh)
                {
                    at = D.NearestWalkable(new Vector2Int((int)p.Pos.x, (int)p.Pos.y), 3);
                    if (at.x < 0) at = new Vector2Int((int)p.Pos.x, (int)p.Pos.y);
                    break;
                }
            S.Wrecks.Add(new Wreck { Id = VehId(), Type = v.Type, Pos = at, Hp = Math.Max(0.05, v.Hp), Need = new List<string>(v.Need), Fuel = v.Fuel, Own = CurCol });
            C.Vehs.Remove(v);
            q.Vid = -1;
            foreach (var p in SquadMembers(q)) VehUnflag(p);
            AddLog(string.Format("{0}: the {1} {2} near {3}. They walk home; it is marked on the map to tow back.", q.Name, Lower(d.Name), why, D.DistrictAt(at).Name), "loss");
            VehBump(); VehSyncParked();
        }

        void VehDamage(Squad q, double risk)
        {
            if (q.Vid < 0 || risk <= 0.0) return;
            var v = VehicleById(q.Vid);
            if (v == null || v.St != "out") return;
            var d = Data.VehDef(v.Type);
            v.Hp -= risk * 120.0 * (1.0 - d.Armor) / d.Hp;
            VehBump();
            if (v.Hp <= 0.0)
            {
                v.Hp = 0.05;
                v.Need = d.Need.Take(2).ToList();
                VehAbandon(q, v, "was wrecked");
            }
        }

        public double SquadVarm(Squad q)
        {
            if (q.Vid < 0) return 0.0;
            var v = VehicleById(q.Vid);
            if (v == null || v.St != "out") return 0.0;
            return Data.VehDef(v.Type).Armor;
        }

        void VehSquadHome(Squad q)
        {
            if (q.Vid >= 0)
            {
                var v = VehicleById(q.Vid);
                if (v != null && v.St == "out") { v.St = "ok"; v.Drv = -1; }
                q.Vid = -1; q.Vret = false;
            }
            if (q.Towing != null)
            {
                var w = q.Towing;
                q.Towing = null;
                int tn = C.Vehs.Count(ov => ov.Type == "tank");
                if (w.Type == "tank" && tn >= Data.TANK_CAP)
                {
                    w.Pos = (BaseById(q.Base)?.Pos ?? w.Pos).Add(2, 0);
                    S.Wrecks.Add(w);
                    w = null;
                }
                if (w != null) VehAddTowed(q, w);
            }
            foreach (var p in SquadMembers(q)) { VehUnflag(p); p.Tow = false; }
            VehBump(); VehSyncParked();
        }

        void VehAddTowed(Squad q, Wreck w)
        {
            var nv = new Vehicle { Id = w.Id, Type = w.Type, Base = q.Base, Hp = w.Hp, Need = new List<string>(w.Need), Fuel = w.Fuel, Sq = -1, St = "ok", End = 0.0, Drv = -1 };
            if (nv.Need.Count > 0 || nv.Hp < 0.35) nv.St = "wreck";
            C.Vehs.Add(nv);
            AddLog(string.Format("{0} towed a {1} wreck into {2}'s Garage. {3}", q.Name, Lower(Data.VehDef(w.Type).Name), BaseById(q.Base)?.Name ?? "camp", nv.St == "wreck" ? "Open GARAGE to repair it." : "It needs fuel."), "major");
        }

        public int TankCount() => C.Vehs.Count(v => v.Type == "tank") + C.Squads.Count(q => q.Towing != null && q.Towing.Type == "tank");

        public string TowError(int wid, int sid)
        {
            var w = WreckById(wid);
            if (w == null) return "That wreck is gone.";
            var q = SquadById(sid);
            if (q == null) return "No such squad.";
            string e = SquadReady(q);
            if (e != "") return e;
            var b = BaseById(q.Base);
            if (!HasGarage(b)) return string.Format("{0} has no Garage. Convert a gutted building to a Garage first.", b?.Name ?? "That camp");
            if (GarageUsed(b) >= GarageCap(b)) return string.Format("The Garage at {0} is full ({1}/{2}).", b.Name, GarageUsed(b), GarageCap(b));
            var d = Data.VehDef(w.Type);
            if (w.Type == "tank" && TankCount() >= Data.TANK_CAP) return string.Format("Your colony already has {0} tanks (the most it can keep).", Data.TANK_CAP);
            var v = SquadVehicle(q);
            var path = SquadRoute(q, w.Pos);
            if (path.Count < 2) return "No way to get there.";
            if (path.Count > SquadRange()) return string.Format("Too far: squads go up to {0} tiles of road from their base.", SquadRange());
            bool canDrive = v != null && VehRuns(v) && v.Fuel >= 2.0 * path.Count * Data.VehDef(v.Type).Use;
            if (canDrive) return "";
            if (d.Light && SquadMembers(q).Count >= 4) return "";
            if (d.Light) return string.Format("Needs a working, fuelled vehicle assigned to {0}, or a squad of 4 to push and drag it.", q.Name);
            return string.Format("Too heavy to drag by hand: assign a working vehicle with fuel for the round trip to {0}.", q.Name);
        }

        public string TowWreck(int wid, int sid)
        {
            string e = TowError(wid, sid);
            if (e != "") return e;
            var w = WreckById(wid);
            var q = SquadById(sid);
            var path = SquadRoute(q, w.Pos);
            q.Mode = "tow";
            q.TowId = wid;
            q.St = "out";
            bool drove = SquadDispatch(q, path, "sqt");
            foreach (var p in SquadMembers(q)) p.Tow = true;
            AddLog(string.Format("{0} heading out to tow the {1} wreck{2}.", q.Name, Lower(Data.VehDef(w.Type).Name), drove ? " by truck" : " (on foot, slow going)"), "info");
            PlaySfx("click", V.F(w.Pos));
            EmitChanged();
            return "";
        }

        void SquadTow(Person p)
        {
            var q = SquadById(p.Sq);
            if (q == null || q.St != "out" || q.Mode != "tow") { GoHome(p); return; }
            var w = WreckById(q.TowId);
            q.St = "return";
            if (w == null) AddLog(string.Format("{0} found the wreck already gone.", q.Name), "info");
            else
            {
                S.Wrecks.Remove(w);
                q.Towing = w;
                VehBump();
            }
            foreach (var m in SquadMembers(q))
                if (m.St != "dead") GoHome(m);
            EmitChanged();
        }

        /// <summary>v0.14.0a (UI only, reads the sim): how far a tow job has come. 0..0.5 while the squad heads out to
        /// the wreck, 0.5..1 while it brings the wreck home. at = where the squad is now. False when q isn't towing.</summary>
        public bool TowStatus(Squad q, out double frac, out string phase, out Vector2 at)
        {
            frac = 0.0; phase = ""; at = new Vector2(-1f, -1f);
            if (q == null || q.Mode != "tow" || (q.St != "out" && q.St != "return")) return false;
            var b = BaseById(q.Base);
            if (b == null) return false;
            Person lead = null;
            foreach (var p in SquadMembers(q)) { if (p.Vlead) { lead = p; break; } if (lead == null) lead = p; }
            if (lead == null) return false;
            at = lead.Pos;
            var home = V.F(b.Pos);
            if (q.Towing != null)
            {
                double full = Math.Max(1.0, V.F(q.Towing.Pos).DistTo(home));
                frac = 0.5 + 0.5 * V.Clamp(1.0 - lead.Pos.DistTo(home) / full, 0.0, 1.0);
                phase = "towing home";
                return true;
            }
            var w = WreckById(q.TowId);
            if (w == null) { phase = "returning"; frac = 0.5; return true; }
            double out0 = Math.Max(1.0, V.F(w.Pos).DistTo(home));
            frac = 0.5 * V.Clamp(1.0 - lead.Pos.DistTo(V.F(w.Pos)) / out0, 0.0, 1.0);
            phase = "heading to the wreck";
            return true;
        }

        /// <summary>v0.14.0a (UI only): the squad of this colony on its way to tow this wreck, if any.</summary>
        public Squad TowingSquad(int wid) => C?.Squads.FirstOrDefault(q => q.Mode == "tow" && q.TowId == wid && (q.St == "out" || q.St == "return"));

        public string SiphonWreck(int wid)
        {
            var w = WreckById(wid);
            if (w == null) return "That wreck is gone.";
            if (w.Fuel < 0.5) return "The tank is dry.";
            bool near = C.Bases.Any(b => V.F(b.Pos).DistTo(V.F(w.Pos)) <= AutoRadius() + 4.0);
            if (!near) return "Too far from your camps: only inside the auto-scavenge radius.";
            double amt = w.Fuel;
            C.Res["fuel"] = R("fuel") + amt;
            w.Fuel = 0.0;
            VehBump();
            AddLog(string.Format("Siphoned {0} fuel from a {1} wreck.", (int)amt, Lower(Data.VehDef(w.Type).Name)), "good");
            EmitChanged();
            return "";
        }

        public static string WreckInfo(Wreck w)
        {
            var d = Data.VehDef(w.Type);
            var nd = w.Need.Select(x => Lower(Data.PART_LABEL.TryGetValue(x, out var l) ? l : x)).ToList();
            return string.Format("Condition {0}% · needs {1} · fuel left {2} · {3} seats, cargo {4}, armour {5}%", (int)(w.Hp * 100.0),
                nd.Count > 0 ? string.Join(", ", nd) : "no parts", (int)w.Fuel, d.Seats, d.Cargo, (int)(d.Armor * 100.0));
        }

        public const int WRECKS_NEW = 40;
        public const int WRECKS_MIGRATE = 12;

        void SpawnWrecks(int n, int salt)
        {
            var r = new GdRng((long)(S.Seed ^ salt));
            int tries = 0, placed = 0;
            while (placed < n && tries < n * 60)
            {
                tries++;
                var t = new Vector2Int(r.RandiRange(2, D.W - 3), r.RandiRange(2, D.H0 - 3));
                if (!D.IsRoad(t) || WreckNear(t, 6.0) != null) continue;
                float roll = r.Randf();
                string typ = roll < 0.55f ? "pickup" : (roll < 0.85f ? "truck" : "humvee");
                AddWreck(typ, t, r);
                placed++;
            }
            int nf = n / 3;
            tries = 0;
            while (nf > 0 && tries < 6000)
            {
                tries++;
                var t3 = new Vector2Int(r.RandiRange(2, D.W - 3), r.RandiRange(2, D.H0 - 3));
                if (D.Tile(t3) != Data.T_DIRT || WreckNear(t3, 6.0) != null) continue;
                AddWreck(r.Randf() < 0.7f ? "pickup" : "truck", t3, r);
                nf--;
            }
            var cps = new List<int>();
            for (int l = 0; l < D.Lots.Count; l++) if (D.LotKind(l) == "checkpoint") cps.Add(l);
            int ntank = S.Wrecks.Count(w0 => w0.Type == "tank");
            foreach (int l in cps)
            {
                float roll = r.Randf();
                string typ = "";
                if (roll < 0.12f && ntank < Data.TANK_WRECKS_MAX) { typ = "tank"; ntank++; }
                else if (roll < 0.42f) typ = "apc";
                if (typ == "") continue;
                var t2 = D.NearestWalkable(D.LotDoor(l), 4);
                if (t2.x < 0 || WreckNear(t2, 3.0) != null) continue;
                AddWreck(typ, t2, r);
            }
            if (ntank == 0 && cps.Count > 0 && salt == 51829)
            {
                var t4 = D.NearestWalkable(D.LotDoor(cps[r.RandiRange(0, cps.Count - 1)]), 5);
                if (t4.x >= 0)
                {
                    var ex = WreckNear(t4, 3.0);
                    if (ex != null) S.Wrecks.Remove(ex);
                    AddWreck("tank", t4, r);
                }
            }
            if (salt == 51829) SpawnBandWrecks(n, salt);
        }

        static readonly (string rid, double share)[] BAND_WRECKS = { ("small_town", 0.2), ("farmland", 0.12) };

        void SpawnBandWrecks(int n, int salt)
        {
            var r = new GdRng((long)((S.Seed ^ salt) + 31239));
            foreach (var bw in BAND_WRECKS)
            {
                var Rr = D.REGIONS[bw.rid].Rect;
                int want = Math.Max(2, (int)(n * bw.share));
                int tries = 0;
                while (want > 0 && tries < 4000)
                {
                    tries++;
                    var t = new Vector2Int(r.RandiRange(Rr.x, Rr.x + Rr.width - 1), r.RandiRange(Rr.y, Rr.y + Rr.height - 1));
                    if (!(D.IsRoad(t) || D.Tile(t) == Data.T_DIRT) || WreckNear(t, 6.0) != null) continue;
                    float roll = r.Randf();
                    AddWreck(bw.rid == "farmland" ? (roll < 0.6f ? "pickup" : "truck") : (roll < 0.5f ? "pickup" : (roll < 0.85f ? "truck" : "humvee")), t, r);
                    want--;
                }
            }
        }

        void SeedBandOutbreaks(float[] z)
        {
            foreach (var rid in new[] { "small_town", "farmland" })
            {
                var Rr = D.REGIONS[rid].Rect;
                long h = ForestGen.H32(Rr.x, S.Seed, 176);
                var c = new Vector2Int(Rr.x + 40 + (int)(h % Math.Max(1, Rr.width - 80)), Rr.y + 40 + (int)((h >> 12) % Math.Max(1, Rr.height - 80)));
                int zn = rid == "small_town" ? 1 : 2;
                int best = -1; double bd = 1000000000.0;
                for (int si = 0; si < SW * SH; si++)
                {
                    if (SectZone[si] != zn || SectLand[si] < 0.35f) continue;
                    double d = new Vector2((si % SW) * SECT + SECT / 2, (si / SW) * SECT + SECT / 2).DistTo(V.F(c));
                    if (d < bd) { bd = d; best = si; }
                }
                if (best >= 0 && best < z.Length) z[best] = (float)Math.Max(z[best], 0.3 * D.REGIONS[rid].Zdens);
            }
        }

        void AddWreck(string typ, Vector2Int t, GdRng r)
        {
            var d = Data.VehDef(typ);
            var need = new List<string>();
            foreach (var part in d.Need) if (r.Randf() < 0.6f) need.Add(part);
            S.Wrecks.Add(new Wreck { Id = VehId(), Type = typ, Pos = t, Hp = V.Snapped(r.RandfRange(0.15, 0.55), 0.01), Need = need, Fuel = r.RandiRange(0, (int)(d.Tank * 0.2)), Own = -1 });
        }

        public List<Base> GarageBases() => C.Bases.Where(HasGarage).ToList();
    }
}
