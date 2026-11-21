// v0.19.3 Expansion vehicles (Austin): every car on the map is a real wreck, mechanics, 4-slot garages, armour
// plating and scavenging parties in a car. Everything here runs only while Game.ExpansionRules is on (the hooks
// in Game.Gear.cs / Game.Colonists.cs / Game.cs are gated), so classic parity is untouched.
//  - Map cars: parking-lot cars and the cars abandoned on the roads are S.Wrecks (EnsureLotWrecks), the renderer
//    draws no decorative cars any more. Some are burnt-out shells (Wreck.Burnt): beyond repair, strip for scrap.
//  - Repair needs a Mechanic (new job; an Engineer stands in at half speed) at the vehicle's camp. Work advances
//    one mechanic-hour per hour per mechanic; with nobody there it waits ("Needs a mechanic").
//  - Garage space: 4 vehicles per Garage (each Garage conversion and each Garage upgrade level counts as one).
//  - Armour plating: +2 levels per vehicle (metal), +0.12 armour each, a little slower and thirstier.
//  - Car scavenging party (CarRun): picks the nearest unlooted building on the roads, crew searches it, loot goes in
//    the cargo, next nearest, ... until full / fuel low / danger, then home to unload. ~10 real minutes at x1.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DayZero.Core
{
    public sealed partial class Game
    {
        public const int GARAGE_SLOTS = 4;
        public const int PLATE_MAX = 2;
        public const double PLATE_ARMOR = 0.12, PLATE_SPEED = 0.07, PLATE_FUEL = 0.15;
        public const double CAR_CARGO_UNITS = 8.0;      // cargo capacity = VehicleDef.Cargo * this (pickup 96, truck 240)
        public const double CAR_LOOT_MULT = 0.38;       // per building, on top of the vehicle's cargo multiplier
        public const double CAR_DRIVE_MULT = 0.55;      // cars crawl through the streets (still ~2x walking)
        public const double CAR_RUN_MAX_H = 14.0;       // safety cap: head home after this many game hours
        public const int LOT_WRECKS_ROAD = 70;          // extra abandoned cars on the roads (replace the old decorative lane cars)

        // ---------------- garage space ----------------
        int ExpGarageCap(Base b) => GARAGE_SLOTS * (ConvN("garage", b.Id) + b.U("garage"));

        // ---------------- armour plating ----------------
        public double VehArmor(Vehicle v)
        {
            var d = Data.VehDef(v.Type);
            if (!ExpansionRules || v.Plate <= 0) return d.Armor;
            return Math.Min(0.85, d.Armor + PLATE_ARMOR * v.Plate);
        }

        public double VehSpeed(Vehicle v)
        {
            var d = Data.VehDef(v.Type);
            return !ExpansionRules || v.Plate <= 0 ? d.Speed : d.Speed * (1.0 - PLATE_SPEED * v.Plate);
        }

        public double VehUse(Vehicle v)
        {
            var d = Data.VehDef(v.Type);
            return !ExpansionRules || v.Plate <= 0 ? d.Use : d.Use * (1.0 + PLATE_FUEL * v.Plate);
        }

        public static Dictionary<string, double> PlateCost(Vehicle v)
        {
            int n = v.Plate;
            bool heavy = !Data.VehDef(v.Type).Light;
            return Gd.D(("metal", (n == 0 ? 14 : 22) + (heavy ? 8 : 0)), ("nails", n == 0 ? 4 : 6), ("wire", n == 0 ? 2 : 4));
        }

        public string PlateError(Vehicle v)
        {
            if (!ExpansionRules) return "Armour plating needs the expanded rules.";
            if (v == null) return "No such vehicle.";
            if (v.Plate >= PLATE_MAX) return "Fully plated.";
            if (v.St == "out") return "It's out on the road.";
            var b = BaseById(v.Base);
            if (!HasGarage(b)) return string.Format("Needs a Garage at {0}.", b?.Name ?? "its camp");
            if (!CanAfford(PlateCost(v))) return "Need " + FmtCost(PlateCost(v)) + ".";
            return "";
        }

        public string PlateVehicle(int vid)
        {
            var v = VehicleById(vid);
            string e = PlateError(v);
            if (e != "") return e;
            Pay(PlateCost(v));
            v.Plate++;
            VehBump();
            AddLog(string.Format("{0}: scrap-metal plating bolted onto the {1} (armour {2}%).", BaseById(v.Base)?.Name ?? "Garage", Lower(Data.VehDef(v.Type).Name), (int)Math.Round(VehArmor(v) * 100.0)), "good");
            PlaySfx("hammer", V.F(BaseById(v.Base)?.Pos ?? new Vector2Int(0, 0)));
            EmitChanged();
            return "";
        }

        // ---------------- mechanics ----------------
        static bool CanWrench(Person p) => p.Job == "Mechanic" || p.Job == "Engineer";

        /// <summary>Mechanic-hours per game hour at a camp: 1 per Mechanic, 0.5 per Engineer, among the people there now
        /// (not hurt, not in quarantine, not in a squad trip).</summary>
        public double MechRate(int bid)
        {
            double r = 0.0;
            foreach (var p in C.People)
            {
                if (p.St != "base" || p.Base != bid || !CanWrench(p) || p.Hp < 30.0 || (p.Inf >= 0.0 && p.Known)) continue;
                r += p.Job == "Mechanic" ? 1.0 : 0.5;
            }
            return Math.Min(3.0, r);
        }

        public int MechanicsLiving(int bid, bool mechOnly = false) =>
            C.People.Count(p => p.Base == bid && p.St != "dead" && (mechOnly ? p.Job == "Mechanic" : CanWrench(p)));

        string ExpRepairError(Vehicle v)
        {
            if (MechanicsLiving(v.Base) == 0)
                return string.Format("Needs a mechanic: make someone at {0} a Mechanic in CREW & JOBS (an Engineer works at half speed).", BaseById(v.Base)?.Name ?? "its camp");
            return "";
        }

        /// <summary>Called from StepVehicles (hourly): share each camp's mechanic-hours among its cars in repair.</summary>
        void ExpStepRepairs()
        {
            foreach (var b in C.Bases)
            {
                var rep = C.Vehs.Where(v => v.Base == b.Id && v.St == "repair").OrderBy(v => v.Id).ToList();
                if (rep.Count == 0) continue;
                double rate = MechRate(b.Id);
                if (rate <= 0.0) continue;
                // mechanics finish one car before starting the next: the first car gets up to 2 mechanic-hours, the rest the remainder
                foreach (var v in rep)
                {
                    if (rate <= 0.0) break;
                    double take = Math.Min(rate, Math.Min(2.0, Math.Max(0.0, v.End - v.Prog)));
                    v.Prog += take; rate -= take;
                }
                VehBump();
            }
        }

        /// <summary>UI: repair progress 0..1 and the status line ("2 mechanics on it, 3h left" / "Needs a mechanic").</summary>
        public double RepairFrac(Vehicle v) => v.St != "repair" ? 0.0 : !ExpansionRules ? 1.0 - V.Clamp((v.End - S.T) / Math.Max(1.0, VehRepairHours(v)), 0.0, 1.0) : V.Clamp(v.Prog / Math.Max(0.01, v.End), 0.0, 1.0);

        public string RepairStatus(Vehicle v)
        {
            if (v.St != "repair") return "";
            if (!ExpansionRules) return string.Format("in repair ({0:0}h left)", Math.Max(0.0, v.End - S.T));
            double rate = MechRate(v.Base);
            if (rate <= 0.0) return MechanicsLiving(v.Base) == 0 ? "repair stalled: needs a mechanic" : "repair waiting: the mechanic is away";
            double left = Math.Max(0.0, v.End - v.Prog);
            return string.Format("in repair {0}% ({1:0.#}h left at {2:0.#} mechanic-h/h)", (int)(RepairFrac(v) * 100.0), left / Math.Min(rate, 2.0), rate);
        }

        // ---------------- map cars become wrecks ----------------
        /// <summary>Once per run (new run or an older expanded save): a wreck for every parking-lot car and the cars
        /// abandoned in the street lanes, a share of them burnt-out shells. Uses its own RNG.</summary>
        void EnsureLotWrecks()
        {
            if (!ExpansionRules || S == null || S.LotWrecks) return;
            S.LotWrecks = true;
            var r = new GdRng((long)(S.Seed ^ 0x5A17C4));
            // parking lots: 1-2 cars nose-in on the stalls
            for (int l = 0; l < D.Lots.Count; l++)
            {
                if (D.LotKind(l) != "parking") continue;
                var L = D.Lots[l];
                var tiles = new List<Vector2Int>();
                for (int y = L.Y; y < L.Y + L.H; y++)
                    for (int x = L.X; x < L.X + L.W; x++)
                        if (D.LotAt(new Vector2Int(x, y)) == l) tiles.Add(new Vector2Int(x, y));
                if (tiles.Count == 0) continue;
                int n = Math.Min(tiles.Count, 1 + r.RandiRange(0, 1));
                float a0 = (float)(D.LotAngle(l) * Math.PI / 180.0) + Mathf.PI * 0.5f;
                for (int k = 0, tries = 0; k < n && tries < 30; tries++)
                {
                    var t = tiles[r.RandiRange(0, tiles.Count - 1)];
                    if (WreckNear(t, 1.4) != null) continue;
                    string typ = r.Randf() < 0.65f ? "pickup" : "truck";
                    AddWreck(typ, t, r);
                    var w = S.Wrecks[S.Wrecks.Count - 1];
                    w.Ang = a0 + (r.Randf() < 0.5f ? 0f : Mathf.PI) + (r.Randf() - 0.5f) * 0.3f;
                    if (r.Randf() < 0.1f) Burn(w);
                    k++;
                }
            }
            // abandoned in the road lanes (and on bridges): mostly civilian, some burnt out
            int placed = 0;
            for (int tries = 0; placed < LOT_WRECKS_ROAD && tries < LOT_WRECKS_ROAD * 80; tries++)
            {
                var t = new Vector2Int(r.RandiRange(2, D.W - 3), r.RandiRange(2, D.H - 3));
                if (!D.IsRoad(t) || WreckNear(t, 5.0) != null) continue;
                float roll = r.Randf();
                AddWreck(roll < 0.55f ? "pickup" : roll < 0.92f ? "truck" : "humvee", t, r);
                var w = S.Wrecks[S.Wrecks.Count - 1];
                w.Ang = RoadAng(t, r);
                if (r.Randf() < 0.22f) Burn(w);
                placed++;
            }
            // the original wrecks get a facing along their road too (look only)
            foreach (var w in S.Wrecks)
                if (w.Ang == null && D.IsRoad(w.Pos)) w.Ang = RoadAng(w.Pos, r);
            VehBump();
        }

        static void Burn(Wreck w)
        {
            w.Burnt = true; w.Fuel = 0.0; w.Hp = 0.0;
            w.Need = Data.VehDef(w.Type).Need.ToList();
        }

        /// <summary>The road's direction at t (radians): the city grid is turned by RgAngle, the rest is axis aligned.</summary>
        float RoadAng(Vector2Int t, GdRng r)
        {
            float a0 = (t.x >= D.CityRect.x && t.y >= D.CityRect.y && t.x < D.CityRect.x + D.CityRect.width && t.y < D.CityRect.y + D.CityRect.height) ? (float)(D.RgAngle * Math.PI / 180.0) : 0f;
            float best = a0; int bs = -1;
            foreach (float a in new[] { a0, a0 + Mathf.PI * 0.5f })
            {
                int s = 0;
                for (int k = 1; k <= 3; k++)
                    for (int sg = -1; sg <= 1; sg += 2)
                    {
                        var q = new Vector2Int(Mathf.RoundToInt(t.x + Mathf.Cos(a) * k * sg), Mathf.RoundToInt(t.y + Mathf.Sin(a) * k * sg));
                        if (D.IsRoad(q)) s++;
                    }
                if (s > bs) { bs = s; best = a; }
            }
            return best + (r.Randf() < 0.5f ? 0f : Mathf.PI) + (r.Randf() - 0.5f) * (r.Randf() < 0.2f ? 1.2f : 0.3f);
        }

        public string StripWreck(int wid)
        {
            var w = WreckById(wid);
            if (w == null) return "That wreck is gone.";
            if (!w.Burnt) return "It can still be fixed: tow it to a Garage instead.";
            bool near = C.Bases.Any(b => V.F(b.Pos).DistTo(V.F(w.Pos)) <= AutoRadius() + 4.0);
            if (!near) return "Too far from your camps: only inside the auto-scavenge radius.";
            int metal = 4 + (int)((uint)(w.Id * 2654435761u) % 5u) + (Data.VehDef(w.Type).Light ? 0 : 4);
            AddRes("metal", metal);
            S.Wrecks.Remove(w);
            VehBump();
            AddLog(string.Format("Stripped a burnt-out {0} for {1} metal.", Lower(Data.VehDef(w.Type).Name), metal), "good");
            PlaySfx("hammer", V.F(w.Pos));
            EmitChanged();
            return "";
        }

        // ---------------- scavenging party in a car ----------------
        public CarRun CarRunById(int id) => C?.Runs?.FirstOrDefault(x => x.Id == id);
        public CarRun VehRun(int vid) => C?.Runs?.FirstOrDefault(x => x.Vid == vid);
        public double CarCap(Vehicle v) => Data.VehDef(v.Type).Cargo * CAR_CARGO_UNITS;
        public static double CargoSum(CarRun run) => run.Cargo.Values.Sum();

        /// <summary>Real seconds one game hour takes at x1 right now (day 56.25 s, night 37.5 s, E14 day length).</summary>
        double RealSecPerHourX1()
        {
            double sph = IsNight() ? NIGHT_REAL_SEC / 8.0 : DAY_REAL_SEC / 16.0;
            double dl = CustomRules.D(S);
            return dl == 1.0 ? sph : sph * dl;
        }

        /// <summary>Who could crew a car from this vehicle's camp now (adults at camp, not in a squad, fit).</summary>
        public List<Person> CarCrewPool(Vehicle v) => v == null ? new List<Person>() :
            C.People.Where(p => p.St == "base" && p.Base == v.Base && p.Sq == 0 && !IsMinor(p) && !(p.Inf >= 0.0 && p.Known) && p.Hp >= 40.0).ToList();

        public string CarRunError(int vid, List<int> crew)
        {
            if (!ExpansionRules) return "Scavenging parties need the expanded rules.";
            var v = VehicleById(vid);
            if (v == null) return "No such vehicle.";
            var d = Data.VehDef(v.Type);
            if (v.St == "out") return "It's out on the road.";
            if (v.St == "repair" || !VehRuns(v)) return string.Format("The {0} doesn't run: repair it first.", Lower(d.Name));
            var b = BaseById(v.Base);
            if (b == null) return "That camp isn't yours.";
            if (v.Fuel < 6.0) return string.Format("The {0} needs fuel first (REFUEL).", Lower(d.Name));
            if (crew == null || crew.Count == 0) return "Pick a crew: at least 1 person.";
            if (crew.Count > d.Seats) return string.Format("The {0} seats {1}.", Lower(d.Name), d.Seats);
            var pool = CarCrewPool(v);
            foreach (int pid in crew)
            {
                var p = OwnPerson(pid);
                if (p == null) return "That survivor isn't in your colony.";
                if (!pool.Contains(p))
                {
                    if (p.Sq > 0) return string.Format("{0} is in a squad.", p.Name);
                    if (p.St != "base") return string.Format("{0} is out right now.", p.Name);
                    if (p.Base != v.Base) return string.Format("{0} lives at another camp.", p.Name);
                    if (IsMinor(p)) return string.Format("{0} is too young.", p.Name);
                    return string.Format("{0} isn't fit to go.", p.Name);
                }
            }
            return "";
        }

        public string StartCarRun(int vid, List<int> crew)
        {
            crew = crew?.Distinct().ToList();
            string e = CarRunError(vid, crew);
            if (e != "") return e;
            var v = VehicleById(vid);
            var b = BaseById(v.Base);
            var d = Data.VehDef(v.Type);
            C.Runs ??= new List<CarRun>();
            var run = new CarRun { Id = NewId(), Vid = v.Id, Base = b.Id, Crew = crew.ToList(), Cap = CarCap(v), T0 = S.T, Pos = V.F(b.Pos).Add(V.HALF), St = "drive" };
            if (!CarNextStop(run, v))
            {
                return "Nothing left to loot within reach of the roads.";
            }
            C.Runs.Add(run);
            v.St = "out"; v.Drv = crew[0];
            foreach (int pid in crew)
            {
                var p = PersonById(pid);
                LeaveBld(p);
                _claimed.Remove(p.Lot); p.Lot = -1;
                p.Path = new List<Vector2>();
                p.St = "work"; p.Goal = "crun"; p.Wt = 1000000.0;
                CarBoard(p, v, true);
            }
            VehBump(); VehSyncParked();
            AddLog(string.Format("Scavenging party out in the {0}: {1}. First stop {2}, {3}.", Lower(d.Name), string.Join(", ", crew.Select(id => PersonById(id).First)),
                D.LotName(run.Lot), D.DistrictAt(D.LotDoor(run.Lot)).Name), "info");
            PlaySfx("click", run.Pos);
            EmitChanged();
            return "";
        }

        void CarBoard(Person p, Vehicle v, bool inCar)
        {
            p.Veh = inCar; p.Vlead = false; p.Vt = v.Type; p.Vspd = VehSpeed(v); p.Vcargo = 1.0; p.Varm = VehArmor(v) * V.Clamp(v.Hp + 0.3, 0.3, 1.0); p.Vid = v.Id;
        }

        public string RecallCarRun(int rid)
        {
            var run = CarRunById(rid);
            if (run == null) return "They're already home.";
            if (run.St == "home") return "Already heading home.";
            CarHeadHome(run, VehicleById(run.Vid), "recalled");
            EmitChanged();
            return "";
        }

        /// <summary>Nearest-first next building: unlooted, within road reach of the camp, not a faction's or a camp's, not
        /// in a D4-D5 district; the first stop prefers buildings beyond the foot scavengers' radius.</summary>
        string _carWhy = "";
        bool CarNextStop(CarRun run, Vehicle v)
        {
            _carWhy = "nothing left nearby";
            var b = BaseById(run.Base);
            if (b == null) return false;
            var bp = V.F(b.Pos);
            double range = SquadRange(), ar = AutoRadius();
            var cand = new List<(double s, int l)>();
            for (int l = 0; l < D.Lots.Count; l++)
            {
                if (l >= S.Loot.Length || S.Loot[l] < 0.35f || run.Done.Contains(l)) continue;
                var door = D.LotDoor(l);
                double db = V.F(door).DistTo(bp);
                if (db > range) continue;
                if (_factionLots.ContainsKey(l) || LotOwnerBase(l) != null || _claimed.ContainsKey(l)) continue;
                if (DistrictDangerAt(door.x, door.y) >= 4 || InfAt(door) > 0.9) continue;
                double s = V.F(door).DistTo(run.Pos);
                if (run.Stops == 0 && db <= ar) s += 30.0;
                cand.Add((s, l));
            }
            Gd.StableSort(cand, (x, y) => x.s.CompareTo(y.s));
            int tries = 0;
            foreach (var c in cand)
            {
                if (++tries > 6) break;
                var door = D.LotDoor(c.l);
                var park = NearRoad(door, 4);
                if (park.x < 0) { run.Done.Add(c.l); continue; }
                var path = CarPath(run.Pos, park);
                if (path.Count < 1) { run.Done.Add(c.l); continue; }
                double need = (path.Count + 1.3 * V.F(park).DistTo(bp)) * VehUse(v) + 1.0;
                if (v.Fuel < need) { _carWhy = "fuel low"; return false; }
                run.Lot = c.l; run.Park = V.F(park).Add(V.HALF);
                run.Path = path; run.Pi = 0; run.Seg = 0.0; run.St = "drive";
                return true;
            }
            return false;
        }

        static bool Same(Vector2Int a, Vector2Int b) => a.x == b.x && a.y == b.y;

        Vector2Int NearRoad(Vector2Int t, int maxr)
        {
            if (D.IsRoad(t) && !Astar.IsSolid(t)) return t;
            for (int r = 1; r <= maxr; r++)
            {
                Vector2Int best = V.NONE; int bd = int.MaxValue;
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                        var q = t.Add(dx, dy);
                        if (!D.IsRoad(q) || Astar.IsSolid(q)) continue;
                        int dd = dx * dx + dy * dy;
                        if (dd < bd) { bd = dd; best = q; }
                    }
                if (best.x >= 0) return best;
            }
            return V.NONE;
        }

        /// <summary>A driving route: off the yard onto the nearest road (normal path, through the gate), then on road
        /// tiles only (RoadAStar), then the last bit off-road (to a camp). Falls back to the normal path.</summary>
        List<Vector2> CarPath(Vector2 from, Vector2Int to)
        {
            var s = D.NearestWalkable(V.I(from), 4);
            if (s.x < 0) return new List<Vector2>();
            var rs = NearRoad(s, 12);
            var rt = NearRoad(to, 6);
            var o = new List<Vector2>();
            if (rs.x >= 0 && rt.x >= 0)
            {
                var mid = RoadAStar(rs, rt);
                if (mid != null)
                {
                    if (!Same(rs, s)) { var a = FindPath(s, rs); if (a.Count == 0) goto fallback; o.AddRange(a); }
                    if (o.Count > 0 && mid.Count > 0 && Same(V.I(o[o.Count - 1]), mid[0])) o.RemoveAt(o.Count - 1);
                    foreach (var t in mid) o.Add(V.F(t));
                    if (!Same(rt, to)) { var c = FindPath(rt, to); if (c.Count == 0) goto fallback; if (c.Count > 0) c.RemoveAt(0); o.AddRange(c); }
                    return o;
                }
            }
        fallback:
            return new List<Vector2>(FindPath(s, to));
        }

        // small binary-heap A* over road tiles (4-neighbour), boxed around the two ends
        int[] _raG, _raFrom; byte[] _raSt; int _raW, _raH, _raX0, _raY0;
        List<Vector2Int> RoadAStar(Vector2Int a, Vector2Int b)
        {
            if (Same(a, b)) return new List<Vector2Int> { a };
            int m = 50;
            int x0 = Math.Max(0, Math.Min(a.x, b.x) - m), y0 = Math.Max(0, Math.Min(a.y, b.y) - m);
            int x1 = Math.Min(D.W - 1, Math.Max(a.x, b.x) + m), y1 = Math.Min(D.H - 1, Math.Max(a.y, b.y) + m);
            int w = x1 - x0 + 1, h = y1 - y0 + 1, n = w * h;
            if (_raG == null || _raG.Length < n) { _raG = new int[n]; _raFrom = new int[n]; _raSt = new byte[n]; }
            Array.Clear(_raSt, 0, n);
            _raW = w; _raH = h; _raX0 = x0; _raY0 = y0;
            int Id(int x, int y) => (y - y0) * w + (x - x0);
            var heap = new List<(int f, int id)>();
            void Push(int f, int id)
            {
                heap.Add((f, id)); int i = heap.Count - 1;
                while (i > 0) { int p = (i - 1) / 2; if (heap[p].f <= heap[i].f) break; var tmp = heap[p]; heap[p] = heap[i]; heap[i] = tmp; i = p; }
            }
            (int f, int id) Pop()
            {
                var top = heap[0]; var last = heap[heap.Count - 1]; heap.RemoveAt(heap.Count - 1);
                if (heap.Count > 0)
                {
                    heap[0] = last; int i = 0;
                    while (true)
                    {
                        int l = 2 * i + 1, r = l + 1, s = i;
                        if (l < heap.Count && heap[l].f < heap[s].f) s = l;
                        if (r < heap.Count && heap[r].f < heap[s].f) s = r;
                        if (s == i) break;
                        var tmp = heap[s]; heap[s] = heap[i]; heap[i] = tmp; i = s;
                    }
                }
                return top;
            }
            int ia = Id(a.x, a.y), ib = Id(b.x, b.y);
            _raG[ia] = 0; _raFrom[ia] = -1; _raSt[ia] = 1;
            Push(Math.Abs(a.x - b.x) + Math.Abs(a.y - b.y), ia);
            int[] dx = { 1, -1, 0, 0 }, dy = { 0, 0, 1, -1 };
            int expanded = 0;
            while (heap.Count > 0 && expanded < 60000)
            {
                var (f, id) = Pop();
                if (_raSt[id] == 2) continue;
                _raSt[id] = 2; expanded++;
                if (id == ib) break;
                int cx = id % w + x0, cy = id / w + y0;
                for (int k = 0; k < 4; k++)
                {
                    int nx = cx + dx[k], ny = cy + dy[k];
                    if (nx < x0 || ny < y0 || nx > x1 || ny > y1) continue;
                    var q = new Vector2Int(nx, ny);
                    if (!D.IsRoad(q) || Astar.IsSolid(q) || Astar.IsBlocked(q)) continue;
                    int nid = Id(nx, ny);
                    if (_raSt[nid] == 2) continue;
                    int g2 = _raG[id] + 1;
                    if (_raSt[nid] == 1 && _raG[nid] <= g2) continue;
                    _raG[nid] = g2; _raFrom[nid] = id; _raSt[nid] = 1;
                    Push(g2 + Math.Abs(nx - b.x) + Math.Abs(ny - b.y), nid);
                }
            }
            if (_raSt[ib] != 2) return null;
            var o = new List<Vector2Int>();
            for (int c = ib; c >= 0; c = _raFrom[c]) o.Add(new Vector2Int(c % w + x0, c / w + y0));
            o.Reverse();
            return o;
        }

        double CarStopHours(int crew) => crew <= 1 ? 1.3 : crew == 2 ? 1.1 : crew == 3 ? 0.95 : 0.85;

        /// <summary>Every frame step (Game.Step, own colony and the others): move the cars, run the stops.</summary>
        void StepCarRuns(double dh)
        {
            if (C.Runs == null || C.Runs.Count == 0) return;
            foreach (var run in C.Runs.ToList())
            {
                var v = VehicleById(run.Vid);
                // crew that died or were pulled away by something else leave the party
                for (int i = run.Crew.Count - 1; i >= 0; i--)
                {
                    var p = PersonById(run.Crew[i]);
                    if (p == null || p.St == "dead" || p.Goal != "crun")
                    {
                        if (p != null && p.St != "dead") VehUnflag(p);
                        run.Crew.RemoveAt(i);
                    }
                }
                if (v == null || run.Crew.Count == 0) { CarLost(run, v, v == null ? "is gone" : "lost its whole crew"); continue; }
                run.RealSec += dh * RealSecPerHourX1();
                if (run.St == "stop") { CarStopTick(run, v); continue; }
                // driving
                double spd = HUMAN_SPEED * VehSpeed(v) * (1.0 + Rx("veh_speed")) * CAR_DRIVE_MULT;
                run.Seg += dh * spd;
                double tiles = 0.0;
                while (run.Seg >= 1.0 && run.Pi < run.Path.Count - 1) { run.Seg -= 1.0; run.Pi++; tiles += 1.0; }
                v.Fuel = Math.Max(0.0, v.Fuel - tiles * VehUse(v));
                run.FuelUsed += tiles * VehUse(v);
                Vector2 np;
                if (run.Path.Count < 2 || run.Pi >= run.Path.Count - 1) np = run.Path.Count > 0 ? run.Path[run.Path.Count - 1].Add(V.HALF) : run.Pos;
                else np = run.Path[run.Pi].LerpTo(run.Path[run.Pi + 1], (float)run.Seg).Add(V.HALF);
                var dv = np - run.Pos;
                if (dv.sqrMagnitude > 1e-6f) run.Dir = dv.normalized;
                run.Pos = np;
                foreach (int pid in run.Crew) { var p = PersonById(pid); if (p != null) p.Pos = run.Pos; }
                CarRam(run, v);
                if (!C.Runs.Contains(run)) continue;
                if (run.Path.Count < 2 || run.Pi >= run.Path.Count - 1)
                {
                    if (run.St == "home") { CarUnload(run, v); continue; }
                    // parked at the curb: the crew gets out and goes in
                    run.St = "stop"; run.StopT0 = S.T; run.StopT1 = S.T + CarStopHours(run.Crew.Count) / (1.0 + Rx("search_speed"));
                    foreach (int pid in run.Crew) { var p = PersonById(pid); if (p != null) p.Veh = false; }
                    CarNoise(run);
                    VehBump();
                }
            }
        }

        /// <summary>A stop in progress: crew walk car -> door, search, walk back. At the end the building is looted.</summary>
        void CarStopTick(CarRun run, Vehicle v)
        {
            var door = V.F(D.LotDoor(run.Lot)).Add(V.HALF);
            double span = Math.Max(0.01, run.StopT1 - run.StopT0);
            double ph = V.Clamp((S.T - run.StopT0) / span, 0.0, 1.0);
            int i = 0;
            foreach (int pid in run.Crew)
            {
                var p = PersonById(pid);
                if (p == null) continue;
                // walk out (0-0.2), inside / at the door (0.2-0.8), walk back (0.8-1); everyone a little apart
                double k = ph < 0.2 ? ph / 0.2 : ph < 0.8 ? 1.0 : 1.0 - (ph - 0.8) / 0.2;
                var off = new Vector2(Mathf.Cos(i * 2.3f + run.Id), Mathf.Sin(i * 2.3f + run.Id)) * 0.35f;
                p.Pos = Vector2.Lerp(run.Park, door + off, (float)k);
                i++;
            }
            run.Pos = run.Park;
            if (S.T < run.StopT1) return;
            CarSearch(run, v);
            if (!C.Runs.Contains(run)) return;
            run.Crew.RemoveAll(pid => PersonById(pid) == null || PersonById(pid).St == "dead");
            if (run.Crew.Count == 0) return;   // StepCarRuns abandons the car next step
            foreach (int pid in run.Crew) { var p = PersonById(pid); if (p != null) { p.Veh = true; p.Pos = run.Pos; } }
            string why = CarStopReason(run, v);
            if (why == "" && !CarNextStop(run, v)) why = _carWhy;
            if (why != "") CarHeadHome(run, v, why);
            VehBump();
        }

        string CarStopReason(CarRun run, Vehicle v)
        {
            if (CargoSum(run) >= run.Cap * 0.97) return "full";
            var b = BaseById(run.Base);
            double home = b != null ? 1.4 * V.F(b.Pos).DistTo(run.Pos) : 30.0;
            if (v.Fuel < home * VehUse(v) + 3.0) return "fuel low";
            if (run.Crew.Any(pid => (PersonById(pid)?.Hp ?? 0.0) < 35.0) || v.Hp < 0.25) return "too hurt to go on";
            if (ZombiesNear(run.Pos, 5.0, 12).Count >= 8) return "too many of the dead around";
            if (S.T - run.T0 > CAR_RUN_MAX_H) return "out too long";
            return "";
        }

        GdRng CarRng(CarRun run, int salt) => new GdRng((long)S.Seed ^ ((long)run.Id * 7919L) ^ ((long)(run.Stops + 1) * 104729L) ^ (long)salt);

        void CarSearch(CarRun run, Vehicle v)
        {
            int l = run.Lot;
            var r = CarRng(run, 11);
            var crew = run.Crew.Select(PersonById).Where(p => p != null).ToList();
            var carrier = crew[0];
            var keep = carrier.Carry; carrier.Carry = new ODict<string, double>();
            bool pv = carrier.Veh; carrier.Veh = true; double? pc = carrier.Vcargo; carrier.Vcargo = Data.VehCargoMult(v.Type);
            TakeLoot(carrier, l, CAR_LOOT_MULT * (1.0 + 0.2 * (crew.Count - 1)));
            carrier.Veh = pv; carrier.Vcargo = pc;
            double got = 0.0, room = Math.Max(0.0, run.Cap - CargoSum(run)), tot = carrier.Carry.Values.Sum();
            double f = tot > room && tot > 0.0 ? room / tot : 1.0;
            foreach (var kv in carrier.Carry) { double a = kv.Value * f; run.Cargo[kv.Key] = run.Cargo.Get(kv.Key, 0.0) + a; got += a; }
            carrier.Carry = keep;
            run.Done.Add(l); run.Stops++;
            // the search itself: the same risk a squad takes, less with guns and armour on
            double pw = 0.0;
            foreach (var p in crew) pw += 0.06 + (p.Wpn != "" ? 0.08 : 0.0) + ArmorOf(p) * 0.1 + (p.Job == "Soldier" || p.Job == "Cop" ? 0.04 : 0.0);
            pw = V.Clamp(pw, 0.0, 0.8);
            double risk = (D.LotDanger(l) * 0.4 + InfAt(D.LotDoor(l)) * 0.1) * (1.0 - pw);
            if (r.Randf() < risk)
            {
                var vic = crew[r.RandiRange(0, crew.Count - 1)];
                if (r.Randf() < 0.25f) { KillPerson(vic, string.Format("was killed searching {0}", D.LotName(l))); run.Kills++; }
                else { Bite(vic, " searching a building"); run.Bites++; }
            }
            if (ExpansionRules && run.Stops % 4 == 0 && got > 0.0)
                AddLog(string.Format("Scavenging party: {0} searched, the {1} is {2}% full.", D.LotName(l), Lower(Data.VehDef(v.Type).Name), (int)(100.0 * CargoSum(run) / Math.Max(1.0, run.Cap))), "info");
        }

        /// <summary>Engine noise at a stop draws a few of the dead from the blocks around (fewer when it's quiet).</summary>
        void CarNoise(CarRun run)
        {
            if (MaxZombies <= 0) return;
            var r = CarRng(run, 23);
            if (r.Randf() > 0.4f * (1.0 - Math.Min(0.6, Rx("quiet")))) return;
            int k = 1 + (r.Randf() < 0.35f ? 1 : 0);
            for (int i = 0; i < k; i++)
            {
                double a = r.Randf() * V.TAU;
                var t = V.I(run.Pos.Add(V.F(Math.Cos(a), Math.Sin(a)).Mul(r.RandfRange(5.0, 9.0))));
                t = new Vector2Int(V.Clampi(t.x, 0, D.W - 1), V.Clampi(t.y, 0, D.H - 1));
                var wt = D.NearestWalkable(t, 4);
                if (wt.x >= 0) SpawnZombie(Ti(wt), 0);
            }
        }

        /// <summary>Zombies in the car's way: it rams them. Damage to the car and the crew is cut by its armour.</summary>
        void CarRam(CarRun run, Vehicle v)
        {
            var near = ZombiesNear(run.Pos, 0.8, 3);
            if (near.Count == 0) return;
            double arm = VehArmor(v);
            var r = CarRng(run, 31 + run.Rams);
            foreach (int zi in near)
            {
                run.Rams++;
                KillZ(zi);
                v.Hp -= 0.02 * (1.0 - arm);
                if (r.Randf() < 0.12 * (1.0 - arm) && run.Crew.Count > 0)
                {
                    var p = PersonById(run.Crew[r.RandiRange(0, run.Crew.Count - 1)]);
                    if (p != null)
                    {
                        if (r.Randf() < 0.25 * (1.0 - arm)) { Bite(p, " through a car window"); run.Bites++; }
                        else p.Hp = Math.Max(5.0, p.Hp - 12.0 * (1.0 - arm));
                    }
                }
            }
            VehBump();
            if (v.Hp <= 0.0) CarWrecked(run, v);
        }

        void CarHeadHome(CarRun run, Vehicle v, string why)
        {
            var b = BaseById(run.Base);
            run.Why = why;
            run.St = "home";
            foreach (int pid in run.Crew) { var p = PersonById(pid); if (p != null) { p.Veh = true; p.Pos = run.Pos; } }
            run.Path = b != null ? CarPath(run.Pos, b.Pos) : new List<Vector2>();
            run.Pi = 0; run.Seg = 0.0;
            if (v != null && ExpansionRules)
                AddLog(string.Format("Scavenging party heading home ({0}): the {1} is {2}% full after {3} buildings.", why, Lower(Data.VehDef(v.Type).Name),
                    (int)(100.0 * CargoSum(run) / Math.Max(1.0, run.Cap)), run.Stops), "info");
        }

        void CarUnload(CarRun run, Vehicle v)
        {
            var b = BaseById(run.Base);
            var parts = new List<string>();
            double tot = 0.0;
            foreach (var kv in run.Cargo.OrderByDescending(x => x.Value))
            {
                AddRes(kv.Key, kv.Value); tot += kv.Value;
                if (kv.Value >= 1.0 && parts.Count < 6) parts.Add(string.Format("{0} {1}", (int)kv.Value, Lower(ResLabel(kv.Key))));
            }
            foreach (int pid in run.Crew)
            {
                var p = PersonById(pid);
                if (p == null) continue;
                VehUnflag(p);
                p.St = "base"; p.Goal = ""; p.Wt = 0.0;
                if (b != null) p.Pos = V.F(b.Pos).Add(V.HALF);
            }
            if (v != null) { v.St = "ok"; v.Drv = -1; }
            C.Runs.Remove(run);
            C.AddStat("car_runs", 1);
            VehBump(); VehSyncParked();
            AddLog(string.Format("The scavenging party is back at {0} ({1}) with {2} supplies from {3} buildings: {4}.", b?.Name ?? "camp", run.Why, (int)tot, run.Stops,
                parts.Count > 0 ? string.Join(", ", parts) : "nothing much"), "major");
            EmitChanged();
        }

        void CarWrecked(CarRun run, Vehicle v)
        {
            var at = D.NearestWalkable(V.I(run.Pos), 3);
            if (at.x < 0) at = V.I(run.Pos);
            S.Wrecks.Add(new Wreck { Id = v.Id, Type = v.Type, Pos = at, Hp = 0.1, Need = Data.VehDef(v.Type).Need.Take(2).ToList(), Fuel = v.Fuel, Own = CurCol, Ang = Mathf.Atan2(run.Dir.y, run.Dir.x) });
            C.Vehs.Remove(v);
            // the crew walks home with what they can carry
            double share = run.Crew.Count > 0 ? 0.25 / run.Crew.Count : 0.0;
            foreach (int pid in run.Crew)
            {
                var p = PersonById(pid);
                if (p == null) continue;
                VehUnflag(p);
                foreach (var kv in run.Cargo) p.Carry[kv.Key] = p.Carry.Get(kv.Key, 0.0) + kv.Value * share;
                p.Goal = "";
                GoHome(p);
            }
            C.Runs.Remove(run);
            VehBump(); VehSyncParked();
            AddLog(string.Format("The scavenging party's {0} was wrecked near {1}. The crew walks home with what they can carry; tow it back later.", Lower(Data.VehDef(v.Type).Name), D.DistrictAt(at).Name), "loss");
            EmitChanged();
        }

        void CarLost(CarRun run, Vehicle v, string why)
        {
            if (v != null)
            {
                var at = D.NearestWalkable(V.I(run.Pos), 3);
                if (at.x < 0) at = V.I(run.Pos);
                S.Wrecks.Add(new Wreck { Id = v.Id, Type = v.Type, Pos = at, Hp = Math.Max(0.05, v.Hp * 0.5), Need = new List<string>(), Fuel = v.Fuel, Own = CurCol, Ang = Mathf.Atan2(run.Dir.y, run.Dir.x) });
                C.Vehs.Remove(v);
            }
            C.Runs.Remove(run);
            VehBump(); VehSyncParked();
            AddLog(string.Format("The scavenging party {0}. The car stands abandoned on the road.", why), "death");
            EmitChanged();
        }

        /// <summary>UI: one line about a run ("stop 5 at Houses, cargo 62%, 6.1 min").</summary>
        public string CarRunLine(CarRun run)
        {
            var v = VehicleById(run.Vid);
            string what = run.St == "stop" ? "searching " + D.LotName(run.Lot) : run.St == "home" ? "driving home (" + run.Why + ")" : "driving to " + D.LotName(run.Lot);
            return string.Format("{0} · cargo {1}% · {2} buildings · fuel {3} · {4:0.0} min", what, (int)(100.0 * CargoSum(run) / Math.Max(1.0, run.Cap)), run.Stops, (int)(v?.Fuel ?? 0.0), run.RealSec / 60.0);
        }
    }
}
