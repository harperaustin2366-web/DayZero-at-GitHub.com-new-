// game.gd: squads - forming, sweeps, scavenging expeditions, travel between maps,
// specials (drone, helicopter, air strike) and squad attacks on faction camps.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DayZero.Core
{
    public sealed partial class Game
    {
        public const int SQUAD_MAX = 4;
        public const int SQUAD_SIZE = 8;
        public const int SQUAD_RANGE = 80;
        public const int SQUAD_SIZE_BASE = 4;
        public const double HOP_HOURS = 3.0;
        public const double HOP_WALK_MULT = 4.0;
        public const double HOP_FUEL = 60.0;
        public double TravelRisk = 0.14;
        static readonly Dictionary<string, string[]> MAP_ADJ = new Dictionary<string, string[]>
        { ["world"] = new[] { "small_town", "farmland" }, ["small_town"] = new[] { "world" }, ["farmland"] = new[] { "world" } };
        public static readonly Dictionary<string, string> MAP_NAME = new Dictionary<string, string>
        { ["world"] = "City / Forest", ["city"] = "City", ["forest"] = "Forest", ["small_town"] = "Small Town", ["farmland"] = "Farmland" };
        readonly Dictionary<string, Vector2Int> _entryCache = new Dictionary<string, Vector2Int>();

        public Squad SquadById(int sid) => C?.Squads.FirstOrDefault(q => q.Id == sid);

        public List<Person> SquadMembers(Squad q)
        {
            var o = new List<Person>();
            foreach (int pid in q.Members)
            {
                var p = PersonById(pid);
                if (p != null && p.St != "dead") o.Add(p);
            }
            return o;
        }

        public List<Squad> CampSquads(int bid) => C.Squads.Where(q => q.Base == bid).ToList();

        public string FormSquad(List<int> pids, string sname = "", int bid = -1)
        {
            if (bid >= 0 && BaseById(bid) == null) return "That camp isn't yours.";
            if (pids.Count < 2) return "A squad needs at least 2 people.";
            if (pids.Count > SquadSizeMax()) return string.Format("A squad has {0} people at most (Scouting research adds more).", SquadSizeMax());
            var mem = new List<Person>();
            foreach (int pid in pids)
            {
                var p = OwnPerson(pid);
                if (p == null) return "That survivor isn't in your colony.";
                if (p.Sq > 0) return string.Format("{0} is already in a squad.", p.Name);
                if (p.St != "base") return string.Format("{0} is out right now.", p.Name);
                if (p.Inf >= 0.0 && p.Known) return string.Format("{0} is in quarantine.", p.Name);
                if (IsMinor(p)) return string.Format("{0} is too young for a squad.", p.Name);
                if (bid < 0) bid = p.Base;
                if (p.Base != bid) return string.Format("{0} lives at {1}, not {2}.", p.Name, BaseById(p.Base)?.Name ?? "another camp", BaseById(bid)?.Name ?? "this camp");
                mem.Add(p);
            }
            if (BaseById(bid) == null) return "That camp isn't yours.";
            if (CampSquads(bid).Count >= SQUAD_MAX) return string.Format("{0} can field {1} squads at most.", BaseById(bid).Name, SQUAD_MAX);
            int sid = NewId();
            var names = new[] { "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Ghost", "Hammer" };
            var q = new Squad { Id = sid, Name = sname != "" ? sname : "Squad " + names[sid % names.Length], Base = bid, St = "idle" };
            foreach (var p in mem) { p.Sq = sid; q.Members.Add(p.Id); }
            C.Squads.Add(q);
            Equip();
            AddLog(string.Format("{0} formed: {1}.", q.Name, string.Join(", ", mem.Select(x => x.First))), "info");
            EmitChanged();
            return "";
        }

        public string DisbandSquad(int sid)
        {
            var q = SquadById(sid);
            if (q == null) return "No such squad.";
            if (q.St != "idle") return "Recall them first.";
            foreach (var p in SquadMembers(q)) p.Sq = 0;
            C.Squads.Remove(q);
            Equip();
            EmitChanged();
            return "";
        }

        public List<Vector2> SquadRoute(Squad q, Vector2Int tile)
        {
            var b = BaseById(q.Base);
            if (b == null) return new List<Vector2>();
            var t = D.NearestWalkable(tile, 4);
            if (t.x < 0) return new List<Vector2>();
            return Math.Abs(t.x - b.Pos.x) + Math.Abs(t.y - b.Pos.y) <= LONG_PATH ? Astar.GetPointPath(b.Pos, t) : FindPath(b.Pos, t);
        }

        public SquadRisk SquadRiskOf(Squad q, List<Vector2> path)
        {
            var secs = new ODict<int, bool>();
            foreach (var pt in path) secs[SectOf(V.I(pt))] = true;
            double tot = 0.0, expLoss = 0.0;
            double power = SquadPower(q);
            foreach (var s in secs) { tot += S.Zinf[s.Key]; expLoss += S.Zinf[s.Key] * 0.22 * (1.0 - power); }
            return new SquadRisk { Sectors = secs.Count, Inf = tot / Math.Max(1.0, secs.Count), Loss = expLoss, N = SquadMembers(q).Count, Power = power };
        }

        double SquadPower(Squad q)
        {
            double pw = 0.0;
            foreach (var p in SquadMembers(q))
            {
                pw += 0.05;
                if (p.Wpn != "") pw += 0.08 * (0.6 + 0.6 * Math.Max(0.0, Data.GunHit(WeaponOf(p)) - 0.3) / 0.5);
                pw += ArmorOf(p) * 0.1;
                if (p.Job == "Soldier") pw += 0.05;
                else if (p.Job == "Cop") pw += 0.03;
            }
            pw += Rx("squad_power") + Rx("squad_safe") + Rx("recon") + 0.1 * q.KitOr1 - 0.05;
            return V.Clamp(pw, 0.0, 0.85);
        }

        public string SendSquad(int sid, Vector2Int tile)
        {
            var q = SquadById(sid);
            if (q == null) return "No such squad.";
            if (q.St != "idle") return string.Format("{0} is already out.", q.Name);
            var mem = SquadMembers(q);
            if (mem.Count < 2) return string.Format("Not enough people left in {0}.", q.Name);
            foreach (var p in mem)
            {
                if (p.St != "base") return string.Format("{0} isn't back at camp yet.", p.Name);
                if (p.Hp < 40.0) return string.Format("{0} is too hurt to go.", p.Name);
            }
            var path = SquadRoute(q, tile);
            if (path.Count < 2) return "No way to get there.";
            if (path.Count > SquadRange()) return string.Format("Too far: squads go up to {0} tiles of road from their base.", SquadRange());
            double kit = Math.Min(R("ammo"), 3.0 * mem.Count);
            C.Res["ammo"] = R("ammo") - kit;
            q.Kit = kit / Math.Max(1.0, 3.0 * mem.Count);
            bool drove = VehDepart(q, mem, path);
            q.Route = path.Select(pt => Ti(V.I(pt))).ToList();
            q.Tgt = V.I(path[path.Count - 1]);
            q.St = "out";
            foreach (var p in mem) { p.Path = path; p.Pi = 0; p.Seg = 0.0; p.Tgt = q.Tgt; p.Goal = "sq"; p.St = "move"; }
            AddLog(string.Format("{0} is moving out to clear {1} tiles of road toward {2}{3}.", q.Name, path.Count, D.DistrictAt(q.Tgt).Name, drove ? " by truck" : ""), "info");
            PlaySfx("click", V.F(q.Tgt));
            EmitChanged();
            return "";
        }

        public string RecallSquad(int sid)
        {
            var q = SquadById(sid);
            if (q == null) return "No such squad.";
            if (q.St == "idle") return "They're already home.";
            if (q.St == "travel") return string.Format("{0} is on the road between maps.", q.Name);
            if (q.St == "field") return string.Format("{0} has no camp in this map yet: set up an outpost here or SEND TO MAP.", q.Name);
            q.St = "return";
            foreach (var p in SquadMembers(q))
                if (new[] { "sq", "sqs", "sqw", "sqa", "sqt" }.Contains(p.Goal)) GoHome(p);
            EmitChanged();
            return "";
        }

        // ---------------- maps ----------------
        public string Rgroup(Vector2Int p)
        {
            if (p.y < D.H0) return "world";
            string r = D.RegionAt(p);
            return r != "" ? r : "world";
        }

        public static string MapGroup(string rid) => (rid == "city" || rid == "forest" || rid == "" || rid == "world") ? "world" : rid;

        public int MapHops(string a, string b)
        {
            if (a == b) return 0;
            var seen = new Dictionary<string, int> { [a] = 0 };
            var q = new List<string> { a };
            while (q.Count > 0)
            {
                string c = q[0]; q.RemoveAt(0);
                foreach (var n in MAP_ADJ.GetOr(c, new string[0]))
                {
                    if (seen.ContainsKey(n)) continue;
                    seen[n] = seen[c] + 1;
                    if (n == b) return seen[n];
                    q.Add(n);
                }
            }
            return -1;
        }

        public Vector2Int RegionEntry(string g, string fromG = "world")
        {
            string key = string.Format("{0}:{1}:{2}", D.WorldSeed, g, fromG);
            if (_entryCache.TryGetValue(key, out var e0)) return e0;
            Vector2Int c; RectInt rect;
            if (g == "world")
            {
                string src = D.REGIONS.ContainsKey(fromG) ? fromG : "small_town";
                var br = D.REGIONS[src].Rect;
                int cx = br.x + br.width / 2;
                if (src == "small_town") cx = V.Clampi(cx, D.CityRect.x + 4, D.CityRect.x + D.CityRect.width - 5);
                c = new Vector2Int(cx, D.H0 - 2); rect = new RectInt(0, 0, D.W, D.H0);
            }
            else
            {
                rect = D.REGIONS[g].Rect;
                c = new Vector2Int(rect.x + rect.width / 2, rect.y);
            }
            var e = FindEntry(c, rect);
            _entryCache[key] = e;
            return e;
        }

        Vector2Int FindEntry(Vector2Int c, RectInt rect)
        {
            var fb = V.NONE;
            for (int r = 0; r < 160; r++)
            {
                for (int dy = -r; dy < r + 1; dy++)
                    for (int dx = -r; dx < r + 1; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                        var q = c.Add(dx, dy);
                        if (!Gd.Has(rect, q) || !D.Walkable(q)) continue;
                        int t = D.Tile(q);
                        if (t == Data.T_ROAD || t == Data.T_AVE) return q;
                        if (fb.x < 0) fb = q;
                    }
                if (fb.x >= 0 && r > 40) return fb;
            }
            return fb.x >= 0 ? fb : c;
        }

        public string SquadMap(Squad q)
        {
            if (q.CurMap != null) return q.CurMap;
            foreach (var p in SquadMembers(q)) return Rgroup(new Vector2Int((int)p.Pos.x, (int)p.Pos.y));
            var b = BaseById(q.Base);
            return b != null ? Rgroup(b.Pos) : "world";
        }

        public Base CampInMap(string g, Vector2Int? nearArg = null)
        {
            var near = nearArg ?? V.NONE;
            Base best = null; double bd = 1e18;
            foreach (var b in C.Bases)
            {
                if (b.Done < 1.0 || Rgroup(b.Pos) != g) continue;
                double d = near.x < 0 ? 0.0 : V.F(b.Pos).Dist2To(V.F(near));
                if (d < bd) { bd = d; best = b; }
            }
            return best;
        }

        public bool SquadInMap(string g) => C.Squads.Any(q => q.St != "travel" && SquadMap(q) == g && SquadMembers(q).Count > 0);

        Vehicle TravelVehicle(Squad q, List<Person> mem)
        {
            var v = SquadVehicle(q);
            if (v == null || !VehRuns(v) || v.Base != q.Base || (v.St != "ok" && v.St != "out")) return null;
            if (mem.Count > Data.VehDef(v.Type).Seats) return null;
            return v;
        }

        public TravelInfo TravelInfoOf(int sid, string dest)
        {
            var o = new TravelInfo();
            var q = SquadById(sid);
            if (q == null) { o.Err = "No such squad."; return o; }
            if (D.REGIONS.TryGetValue(dest, out var rd) && rd.Locked) { o.Err = string.Format("{0}: coming soon.", rd.Name ?? dest); return o; }
            string g = MapGroup(dest);
            if (!MAP_ADJ.ContainsKey(g)) { o.Err = "Pick a map."; return o; }
            if (q.St != "idle" && q.St != "field") { o.Err = string.Format("{0} is already out.", q.Name); return o; }
            var mem = SquadMembers(q);
            if (mem.Count == 0) { o.Err = string.Format("Nobody left in {0}.", q.Name); return o; }
            foreach (var p in mem)
            {
                if (q.St == "idle" && p.St != "base") { o.Err = string.Format("{0} isn't back at camp yet.", p.Name); return o; }
                if (p.Hp < 40.0) { o.Err = string.Format("{0} is too hurt to travel.", p.Name); return o; }
            }
            string from = SquadMap(q);
            if (from == g)
            {
                o.Err = g == "world" ? string.Format("City and Forest are one map: send {0} on a normal run.", q.Name) : string.Format("{0} is already in {1}.", q.Name, MAP_NAME.GetOr(g, g));
                return o;
            }
            int hops = MapHops(from, g);
            if (hops < 0) { o.Err = "No road between those maps."; return o; }
            o.Hops = hops;
            var v = TravelVehicle(q, mem);
            if (v != null)
            {
                var d = Data.VehDef(v.Type);
                o.Veh = d.Name;
                o.Fuel = HOP_FUEL * d.Use * hops;
                o.Hours = HOP_HOURS * hops;
                if (v.Fuel < o.Fuel)
                    o.Err = string.Format("Not enough fuel: the {0} needs {1:0} fuel for {2} hop{3} (has {4:0}). Refuel it in the Garage, or unassign it to walk.", Lower(d.Name), o.Fuel, hops, hops != 1 ? "s" : "", v.Fuel);
            }
            else o.Hours = HOP_HOURS * HOP_WALK_MULT * hops;
            return o;
        }

        public string TravelError(int sid, string dest) => TravelInfoOf(sid, dest).Err;

        public string SendSquadMap(int sid, string dest)
        {
            var info = TravelInfoOf(sid, dest);
            if (info.Err != "") return info.Err;
            var q = SquadById(sid);
            var mem = SquadMembers(q);
            string g = MapGroup(dest);
            string from = SquadMap(q);
            var v = TravelVehicle(q, mem);
            q.Vid = -1; q.Vret = false;
            if (v != null)
            {
                v.Fuel -= info.Fuel;
                v.St = "out";
                VehFlags(mem, v);
                q.Vid = v.Id;
                VehBump(); VehSyncParked();
            }
            q.St = "travel"; q.Dest = g; q.From = from; q.Hops = info.Hops; q.Tarr = S.T + info.Hours;
            q.CurMap = null; q.At = null;
            foreach (var p in mem)
            {
                LeaveBld(p);
                if (_rt.TryGetValue(p.Id, out var r)) r.Code = "";
                p.Path = new List<Vector2>(); p.Pi = 0; p.Seg = 0.0;
                p.Trav = null;
                p.St = "work"; p.Goal = "hop"; p.Wt = info.Hours;
            }
            _rtNeed[CurCol] = true;
            AddLog(string.Format("{0} set off for {1} {2} ({3:0} h, {4} hop{5}{6}).", q.Name, MAP_NAME.GetOr(g, g), v != null ? "in the " + Lower(info.Veh) : "on foot", info.Hours, info.Hops, info.Hops != 1 ? "s" : "", v != null ? string.Format(", {0:0} fuel", info.Fuel) : ""), "info");
            EmitChanged();
            return "";
        }

        void SquadHopDone(Person p)
        {
            var q = SquadById(p.Sq);
            if (q == null || q.St != "travel") { p.St = "base"; p.Goal = ""; return; }
            SquadArrive(q);
        }

        void SquadArrive(Squad q)
        {
            string g = q.Dest ?? "world";
            var entry = RegionEntry(g, q.From ?? "world");
            var mem = SquadMembers(q);
            double power = SquadPower(q);
            double varm = SquadVarm(q);
            bool walk = q.Vid < 0;
            for (int h = 0; h < q.Hops; h++)
            {
                double risk = TravelRisk * (1.0 - power) * (1.0 - varm) * (walk ? 1.4 : 1.0);
                if (mem.Count > 0 && Rng.Randf() < risk)
                {
                    var vic = mem[Rng.RandiRange(0, mem.Count - 1)];
                    if (Rng.Randf() < 0.3f)
                    {
                        KillPerson(vic, string.Format("died in an ambush on the road to {0}", MAP_NAME.GetOr(g, g)));
                        mem.Remove(vic);
                    }
                    else Bite(vic, " in a road ambush");
                    VehDamage(q, 0.4);
                }
            }
            q.Tarr = null;
            q.CurMap = g;
            int i = 0;
            foreach (var p in mem)
            {
                var off = new Vector2((i % 3) - 1, (i / 3) % 3 - 1).Mul(0.3);
                p.Pos = V.F(entry).Add(V.HALF).Add(off);
                p.Path = new List<Vector2>(); p.Wt = 0.0;
                i++;
            }
            var b = CampInMap(g, entry);
            if (mem.Count == 0) return;
            if (b != null) SquadJoin(q, b);
            else
            {
                q.St = "field"; q.At = entry;
                foreach (var p in mem) { p.St = "work"; p.Goal = "hold"; p.Wt = 1000000.0; p.Tgt = entry; }
                AddLog(string.Format("{0} reached {1} at the entry road. No camp of yours here yet: take over a building as an outpost while they hold the spot.", q.Name, MAP_NAME.GetOr(g, g)), "major");
            }
            _rtNeed[CurCol] = true;
            EmitChanged();
        }

        void SquadJoin(Squad q, Base b)
        {
            q.Base = b.Id; q.St = "return"; q.At = null;
            var v = VehicleById(q.Vid);
            if (v != null) v.Base = b.Id;
            foreach (var ov in C.Vehs) if (ov.Sq == q.Id) ov.Base = b.Id;
            foreach (var p in SquadMembers(q))
            {
                p.Base = b.Id; p.Trav = null;
                if (!Go(p, b.Pos, "home")) { p.St = "base"; p.Goal = ""; p.Pos = V.F(b.Pos).Add(V.HALF); }
            }
            AddLog(string.Format("{0} arrived in {1} and moved into {2}.", q.Name, MAP_NAME.GetOr(q.CurMap ?? "world", "the map"), b.Name), "good");
            _rtNeed[CurCol] = true;
            VehSyncParked();
        }

        void StepFieldSquads()
        {
            foreach (var q in C.Squads.ToList())
            {
                if (q.St != "field") continue;
                var b = CampInMap(q.CurMap ?? "world", q.At ?? V.NONE);
                if (b != null) SquadJoin(q, b);
            }
        }

        void SquadSweep(Person p)
        {
            var q = SquadById(p.Sq);
            if (q == null || q.St != "out") return;
            q.St = "return";
            var mem = SquadMembers(q);
            double power = SquadPower(q);
            var secs = new ODict<int, bool>();
            foreach (int k in q.Route) secs[SectOf(Tp(k))] = true;
            var losses = new List<string>();
            int kills = 0;
            double vrisk = 0.0;
            foreach (var kv in secs)
            {
                int s = kv.Key;
                double v = S.Zinf[s];
                if (S.Ctrl[s] == CurCol + 1 && v < 0.1) continue;
                kills += (int)(v * SectLand[s] * 12.0);
                double risk = v * 0.22 * (1.0 - power) * (S.Scout[s] > 0f ? 0.6 : 1.0) * (1.0 - SquadVarm(q));
                vrisk += v * 0.22;
                if (Rng.Randf() < risk && mem.Count > 0)
                {
                    var vic = mem[Rng.RandiRange(0, mem.Count - 1)];
                    if (Rng.Randf() < 0.4f)
                    {
                        losses.Add(vic.First + " (killed)");
                        KillPerson(vic, string.Format("fell clearing the road in {0}", D.DistrictAt(V.I(SxCenter(s))).Name));
                        mem.Remove(vic);
                    }
                    else
                    {
                        losses.Add(vic.First + " (bitten)");
                        Bite(vic, " clearing a road");
                    }
                }
                S.Zinf[s] = Math.Min(S.Zinf[s], 0.03f);
                S.Ctrl[s] = (byte)(CurCol + 1);
                S.CtrlH[s] = (float)(96.0 + Rx("clear_hold"));
            }
            for (int i = 0; i < Zc.Count; i++)
            {
                if (Zh[i] == 255) continue;
                if (secs.Has(SectOf(Tp(Zc[i])))) { KillZ(i); kills++; }
            }
            VehDamage(q, vrisk);
            q.Kills += kills;
            q.Cleared += secs.Count;
            C.AddStat("kills", kills);
            RebuildSafe();
            AddLog(string.Format("{0} cleared {1} blocks around {2} ({3} zombies down){4}. Fence it before the dead drift back.", q.Name, secs.Count, D.DistrictAt(q.Tgt).Name, kills, losses.Count > 0 ? ". Losses: " + string.Join(", ", losses) : ", no losses"), losses.Count == 0 ? "good" : "loss");
            PlaySfx("rifle", p.Pos);
            foreach (var m in mem) GoHome(m);
            EmitChanged();
        }

        void StepSquads()
        {
            foreach (var q in C.Squads.ToList())
            {
                var mem = SquadMembers(q);
                if (mem.Count == 0)
                {
                    AddLog(string.Format("{0} was wiped out.", q.Name), "death");
                    if (q.Towing != null) { q.Towing.Pos = q.Tgt; S.Wrecks.Add(q.Towing); }
                    var lv = VehicleById(q.Vid);
                    if (lv != null && lv.St == "out")
                    {
                        lv.St = "ok"; C.Vehs.Remove(lv);
                        S.Wrecks.Add(new Wreck { Id = lv.Id, Type = lv.Type, Pos = q.Tgt, Hp = Math.Max(0.05, lv.Hp * 0.5), Need = new List<string>(), Fuel = lv.Fuel, Own = CurCol });
                    }
                    foreach (var ov in C.Vehs) if (ov.Sq == q.Id) ov.Sq = -1;
                    VehBump();
                    C.Squads.Remove(q);
                    continue;
                }
                q.Members = q.Members.Where(pid => PersonById(pid) != null).ToList();
                if (q.St != "idle" && q.St != "travel" && q.St != "field")
                {
                    if (mem.All(p => p.St == "base"))
                    {
                        q.St = "idle";
                        VehSquadHome(q);
                    }
                }
            }
        }

        string CooldownOk(string key)
        {
            double t = C.Cd.Get(key, -1000000000.0);
            if (S.T < t) return string.Format("Ready again in {0:0}h.", t - S.T);
            return "";
        }

        public string DroneScout(Vector2Int tile)
        {
            if (!HasUnlock("drone")) return string.Format("Research {0} first.", UnlockSourceName("drone"));
            string e = CooldownOk("drone");
            if (e != "") return e;
            if (R("fuel") < 3.0) return "Needs 3 fuel.";
            C.Res["fuel"] = R("fuel") - 3.0;
            C.Cd["drone"] = S.T + 8.0;
            var c = new Vector2Int(V.Clampi(tile.x / SECT, 0, SW - 1), V.Clampi(tile.y / SECT, 0, SH - 1));
            for (int dy = -2; dy < 3; dy++)
                for (int dx = -2; dx < 3; dx++)
                {
                    var q = c.Add(dx, dy);
                    if (q.x >= 0 && q.y >= 0 && q.x < SW && q.y < SH) S.Scout[q.y * SW + q.x] = 36f;
                }
            string found = "";
            if (Rng.Randf() < 0.6f)
            {
                var g = SpawnGroup(true, tile);
                if (g != null) found = string.Format(" It spotted {0}.", Lower(g.Label));
            }
            AddLog(string.Format("The drone mapped the streets around {0}: squads there fight smarter for a day and a half.{1}", D.DistrictAt(tile).Name, found), "good");
            EmitChanged();
            return "";
        }

        public string HeliRescue(int gid)
        {
            if (!HasUnlock("heli")) return string.Format("Research {0} first.", UnlockSourceName("heli"));
            var g = GroupById(gid);
            if (g == null || g.St != "wait") return "They're gone.";
            string e = CooldownOk("heli");
            if (e != "") return e;
            if (R("fuel") < 12.0) return "Needs 12 fuel.";
            if (BedRoom(g.N) < g.N) return BedsFree() <= 0 ? NO_BEDS_MSG : string.Format("Only {0} free beds across your camps for {1} people. Convert more housing.", BedsFree(), g.N);
            C.Res["fuel"] = R("fuel") - 12.0;
            C.Cd["heli"] = S.T + 18.0;
            var mb = MainBase();
            g.St = "saved"; S.Groups.Remove(g); C.AddStat("rescued", g.N);
            var harr = new List<Person>();
            for (int i = 0; i < g.N; i++)
            {
                var np = AddPerson(D.RandomJob(Rng), mb.Id, V.F(mb.Pos).Add(V.HALF));
                if (Rng.Randf() < 0.12f) np.Inf = Rng.RandfRange(10.0, 30.0);
                harr.Add(np);
            }
            AddLog(string.Format("The helicopter lifted {0} ({1} people) straight to {2}.", Lower(g.Label), g.N, mb.Name), "good");
            PlaceArrivals(harr, mb.Id);
            PlaySfx("click", V.F(g.Pos));
            EmitChanged();
            return "";
        }

        public string AirStrike(Vector2Int tile)
        {
            if (!HasUnlock("air_strike")) return string.Format("Research {0} first.", UnlockSourceName("air_strike"));
            string e = CooldownOk("air");
            if (e != "") return e;
            var cost = Gd.D(("fuel", 20.0), ("ammo", 30.0));
            if (!CanAfford(cost)) return "Needs 20 fuel and 30 ammo.";
            Pay(cost);
            C.Cd["air"] = S.T + 24.0;
            int n = 0;
            foreach (int i in ZombiesNear(V.F(tile).Add(V.HALF), 7.0, 400)) { KillZ(i); n++; }
            foreach (var d in new[] { new Vector2Int(0, 0) }.Concat(DIRS))
            {
                int s = SectOf(tile + d.Mul(SECT));
                S.Zinf[s] = (float)Math.Max(0.0, S.Zinf[s] - 0.4);
            }
            C.AddStat("kills", n);
            AddLog(string.Format("Gunship run over {0}: {1} zombies down.", D.DistrictAt(tile).Name, n), "good");
            PlaySfx("rifle", V.F(tile));
            EmitChanged();
            return "";
        }

        // ---------------- squad scavenging ----------------
        public int SquadSizeMax() => Math.Min(SQUAD_SIZE, SQUAD_SIZE_BASE + (int)Rx("squad_size"));
        public int SquadRange() => SQUAD_RANGE + (int)Rx("squad_range");
        public double SquadSearchR() => 7.0 + Rx("search_r");
        public int SquadSearchN(Squad q) => Math.Min(14, SquadMembers(q).Count + 1 + (int)Rx("search_n"));

        public List<int> SquadScavTargets(Squad q, Vector2Int tile)
        {
            var c = V.F(tile);
            double r = SquadSearchR();
            var o = new List<(float d, int l)>();
            var seen = new HashSet<int>();
            for (int dy = -(int)r; dy < (int)r + 1; dy++)
                for (int dx = -(int)r; dx < (int)r + 1; dx++)
                {
                    if (dx * dx + dy * dy > r * r) continue;
                    int l = D.BuildingAt(tile.Add(dx, dy));
                    if (l < 0 || seen.Contains(l)) continue;
                    seen.Add(l);
                    if (Searched(l) || S.Loot[l] < 0.2f || _factionLots.ContainsKey(l) || LotOwnerBase(l) != null) continue;
                    o.Add((D.LotCenter(l).DistTo(c), l));
                }
            Gd.StableSort(o, (a, b) => a.d.CompareTo(b.d));
            return o.Take(SquadSearchN(q)).Select(e => e.l).ToList();
        }

        string SquadReady(Squad q)
        {
            if (q == null) return "No such squad.";
            if (q.St != "idle") return string.Format("{0} is already out.", q.Name);
            var mem = SquadMembers(q);
            if (mem.Count < 2) return string.Format("Not enough people left in {0}.", q.Name);
            foreach (var p in mem)
            {
                if (p.St != "base") return string.Format("{0} isn't back at camp yet.", p.Name);
                if (p.Hp < 40.0) return string.Format("{0} is too hurt to go.", p.Name);
            }
            return "";
        }

        bool SquadDispatch(Squad q, List<Vector2> path, string goal)
        {
            var mem = SquadMembers(q);
            double kit = Math.Min(R("ammo"), 3.0 * mem.Count);
            C.Res["ammo"] = R("ammo") - kit;
            q.Kit = kit / Math.Max(1.0, 3.0 * mem.Count);
            bool drove = VehDepart(q, mem, path);
            q.Route = path.Select(pt => Ti(V.I(pt))).ToList();
            q.Tgt = V.I(path[path.Count - 1]);
            foreach (var p in mem) { p.Path = path; p.Pi = 0; p.Seg = 0.0; p.Tgt = q.Tgt; p.Goal = goal; p.St = "move"; }
            return drove;
        }

        public string SendSquadScav(int sid, Vector2Int tile)
        {
            var q = SquadById(sid);
            if (q == null) return "No such squad.";
            if (!HasUnlock("squad_scav")) return string.Format("Research {0} first.", UnlockSourceName("squad_scav"));
            string e = SquadReady(q);
            if (e != "") return e;
            int l = D.BuildingAt(tile);
            var goalT = l >= 0 ? D.LotDoor(l) : tile;
            var path = SquadRoute(q, goalT);
            if (path.Count < 2) return "No way to get there.";
            if (path.Count > SquadRange()) return string.Format("Too far: squads go up to {0} tiles of road from their base.", SquadRange());
            var tg = SquadScavTargets(q, V.I(path[path.Count - 1]));
            if (tg.Count == 0) return "Nothing left to search around there.";
            q.Mode = "scav";
            q.St = "out";
            bool drove = SquadDispatch(q, path, "sqs");
            AddLog(string.Format("{0} is heading out to search {1} buildings around {2}{3}.", q.Name, tg.Count, D.DistrictAt(q.Tgt).Name, drove ? " by truck" : ""), "info");
            PlaySfx("click", V.F(q.Tgt));
            EmitChanged();
            return "";
        }

        void SquadSearch(Person p)
        {
            var q = SquadById(p.Sq);
            if (q == null) return;
            if (q.St == "search")
            {
                p.Goal = "sqw"; p.St = "work"; p.Wt = Math.Max(0.2, (q.SearchEnd ?? S.T) - S.T);
                return;
            }
            if (q.St != "out") return;
            var mem = SquadMembers(q);
            var tg = SquadScavTargets(q, q.Tgt);
            double power = SquadPower(q);
            var losses = new List<string>();
            int found = 0, i = 0;
            double vrisk = 0.0;
            foreach (int l in tg)
            {
                var carrier = mem.Count > 0 ? mem[i % mem.Count] : p;
                i++;
                TakeLoot(carrier, l, 0.8 * (1.0 + 0.15 * q.KitOr1));
                double risk = (D.LotDanger(l) * 0.5 + InfAt(D.LotDoor(l)) * 0.12) * (1.0 - power) * (S.Scout[SectOf(D.LotDoor(l))] > 0f ? 0.7 : 1.0) * (1.0 - SquadVarm(q));
                vrisk += InfAt(D.LotDoor(l)) * 0.12;
                if (Rng.Randf() < risk && mem.Count > 0)
                {
                    var vic = mem[Rng.RandiRange(0, mem.Count - 1)];
                    if (Rng.Randf() < 0.3f)
                    {
                        losses.Add(vic.First + " (killed)");
                        KillPerson(vic, string.Format("was killed searching {0}", D.LotName(l)));
                        mem.Remove(vic);
                    }
                    else
                    {
                        losses.Add(vic.First + " (bitten)");
                        Bite(vic, " searching a building");
                    }
                }
                if (mem.Count > 0 && Rng.Randf() < 0.05 * (1.0 + Rx("find_people")))
                {
                    int n = BedRoom(Rng.RandiRange(1, 3));
                    var farr = new List<Person>();
                    for (int k = 0; k < n; k++)
                    {
                        var np = AddPerson(D.RandomJob(Rng), q.Base, p.Pos);
                        Go(np, HomePos(np), "home");
                        farr.Add(np);
                    }
                    PlaceArrivals(farr, q.Base);
                    found += n;
                    C.AddStat("rescued", n);
                }
            }
            int c = SectOf(q.Tgt);
            for (int dy = -1; dy < 2; dy++)
                for (int dx = -1; dx < 2; dx++)
                {
                    int sx = (c % SW) + dx, sy = (c / SW) + dy;
                    if (sx >= 0 && sy >= 0 && sx < SW && sy < SH) S.Scout[sy * SW + sx] = Math.Max(S.Scout[sy * SW + sx], 48f);
                }
            VehDamage(q, vrisk);
            double hours = tg.Count * 0.6 / (1.0 + Rx("search_speed")) / Math.Max(1.0, mem.Count * 0.5);
            q.St = "search";
            q.SearchEnd = S.T + hours;
            q.Searched += tg.Count;
            foreach (var m in mem)
                if (m.St == "work" || m == p) { m.Goal = "sqw"; m.St = "work"; m.Wt = hours; }
            AddLog(string.Format("{0} searched {1} buildings around {2}{3}{4}.", q.Name, tg.Count, D.DistrictAt(q.Tgt).Name,
                found > 0 ? string.Format(", found {0} survivors", found) : "", losses.Count > 0 ? ". Losses: " + string.Join(", ", losses) : ""), losses.Count == 0 ? "good" : "loss");
            EmitChanged();
        }

        // ---------------- attacks on faction camps ----------------
        public double FactionPower(Camp c) => c.Str * 0.35 + c.Pop * 0.05;

        public double SquadAttackPower(Squad q)
        {
            double pw = 0.0;
            foreach (var p in SquadMembers(q))
            {
                var w = WeaponOf(p);
                pw += 1.0 + Data.GunHit(w) * 1.2 * (p.Wpn != "" ? 1.0 : 0.4) * (1.0 + 0.25 * (w.Multi - 1)) + ArmorOf(p) * 0.8;
                if (p.Job == "Soldier") pw += 0.5;
                else if (p.Job == "Cop") pw += 0.3;
            }
            return pw * (1.0 + Rx("squad_power")) * (0.6 + 0.4 * q.KitOr1);
        }

        public string SquadAttack(int sid, string cid)
        {
            var c = CampById(cid);
            if (c == null || !c.Alive) return "They're gone.";
            var q = SquadById(sid);
            string e = SquadReady(q);
            if (e != "") return e;
            var path = SquadRoute(q, c.Pos);
            if (path.Count < 2) return "No way to get there.";
            if (path.Count > SquadRange()) return string.Format("Too far: squads go up to {0} tiles of road from their base.", SquadRange());
            q.Mode = "attack";
            q.Foe = cid;
            q.St = "attack";
            SquadDispatch(q, path, "sqa");
            AddRel(c, -15.0);
            AddLog(string.Format("{0} is moving on {1}.", q.Name, c.Name), "major");
            EmitChanged();
            return "";
        }

        void SquadBattle(Person p)
        {
            var q = SquadById(p.Sq);
            if (q == null || q.St != "attack") return;
            q.St = "return";
            var c = CampById(q.Foe ?? "");
            var mem = SquadMembers(q);
            if (c == null || !c.Alive)
            {
                foreach (var m in mem) GoHome(m);
                return;
            }
            double atk = SquadAttackPower(q);
            double dfn = FactionPower(c);
            bool win = Rng.Randf() < atk / Math.Max(0.1, atk + dfn);
            var losses = new List<string>();
            int nloss = win ? Rng.RandiRange(0, 1) : Rng.RandiRange(1, 2);
            for (int k = 0; k < nloss; k++)
            {
                if (mem.Count == 0) break;
                var vic = mem[Rng.RandiRange(0, mem.Count - 1)];
                if (Rng.Randf() < 0.5f)
                {
                    losses.Add(vic.First + " (killed)");
                    KillPerson(vic, string.Format("was killed attacking {0}", c.Name));
                    mem.Remove(vic);
                }
                else
                {
                    vic.Hp = Math.Max(5.0, vic.Hp - 45.0);
                    losses.Add(vic.First + " (wounded)");
                }
            }
            AddRel(c, -40.0);
            foreach (var o in S.Camps)
                if (o != c && o.Alive && CampRelationFromData(o.Id, c.Id) > 20) AddRel(o, -10.0);
            string lossTxt = losses.Count > 0 ? ". Losses: " + string.Join(", ", losses) : "";
            if (win)
            {
                double dmg = Rng.RandfRange(10.0, 18.0) * atk / Math.Max(1.0, dfn);
                c.Str -= dmg;
                c.Pop = Math.Max(0, c.Pop - Rng.RandiRange(2, 4));
                var taken = new Dictionary<string, double>();
                foreach (var k in new[] { "food", "ammo", "meds", "materials", "fuel" })
                {
                    double v = Math.Floor(c.Stock.Get(k, 0.0) * 0.3);
                    if (v > 0.0)
                    {
                        c.Stock[k] = c.Stock[k] - v; taken[k] = v;
                        if (mem.Count > 0) mem[0].Carry[k] = mem[0].Carry.Get(k, 0.0) + v;
                    }
                }
                if (c.Str <= 6.0 || c.Pop <= 3)
                {
                    c.Alive = false;
                    c.DefeatedBy = CurCol;
                    _factionLotsDirty = true;
                    int joined = 0;
                    if (c.Att != "Raiders")
                    {
                        var jarr = new List<Person>();
                        int nj = BedRoom(Math.Min(3, c.Pop));
                        for (int k = 0; k < nj; k++)
                        {
                            var np = AddPerson(D.RandomJob(Rng), q.Base, V.F(c.Pos).Add(V.HALF));
                            Go(np, HomePos(np), "home");
                            jarr.Add(np);
                            joined++;
                        }
                        PlaceArrivals(jarr, q.Base);
                    }
                    AddLog(string.Format("{0} broke {1}. Their camp is empty now{2}{3}.", q.Name, c.Name, joined > 0 ? string.Format(", {0} of them joined you", joined) : "", lossTxt), "major");
                }
                else AddLog(string.Format("{0} hit {1} hard and came away with {2}{3}.", q.Name, c.Name, taken.Count > 0 ? FmtCost(taken) : "nothing", lossTxt), "good");
            }
            else
            {
                c.Str -= Rng.RandfRange(2.0, 5.0);
                AddLog(string.Format("{0} was driven off by {1}{2}.", q.Name, c.Name, lossTxt), "loss");
            }
            PlaySfx("rifle", p.Pos);
            foreach (var m in mem) GoHome(m);
            EmitChanged();
        }
    }
}
