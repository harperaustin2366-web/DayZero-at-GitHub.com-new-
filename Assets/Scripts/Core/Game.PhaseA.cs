// v0.12.0a "Defend the base" (Phase A): straight wall runs placed by the player (rasterised onto the sim's
// fence tiles), the starter palisade around a new camp, breaches that survivors rebuild, wall upgrades
// (wood -> scrap metal -> concrete), the air-raid siren and raid sizing. Builds on Game.Defense.cs
// (fences, gates, towers, traps, repairs) and Game.Factions.cs (night raids) instead of replacing them.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DayZero.Core
{
    /// <summary>Camp yard geometry in tile space (the palisade square drawn around a camp).</summary>
    public struct YardGeo
    {
        public Vector2 C, U, V;   // centre, axes (V points from the gate side to the back)
        public float Half;
        public Vector2 Gate;      // centre of the gate opening (on the -V side)
    }

    public sealed partial class Game
    {
        /// <summary>Set by the view (MapView / preview harness) so the sim's starter palisade sits exactly where the
        /// palisade is drawn. Headless (smoke test) it is null and a yard is estimated from the lot.</summary>
        public static Func<Base, YardGeo?> YardProvider;
        /// <summary>Headless runs (smoke test) may raise the starter palisade of an old save from the estimated yard.</summary>
        public static bool AllowYardFallback;
        /// <summary>v0.13.0a: the Phase A rule changes (starter palisade, breaches, yard beds, guard-behind-wall bonus,
        /// raid sizing, siren lead, conversion set-up time, convert-after-scavenge) are dormant. The game plays by the
        /// original v0.10.8a rules; set true only to experiment.</summary>
        public static bool PhaseARules = false;
        /// <summary>SYSTEMS_30 expansion rules (traits, stress, fatigue...). Off = classic v0.10.8a.</summary>
        public static bool ExpansionRules;

        public const int WALL_MAX_LEN = 24;            // tiles per placed run
        public static readonly int[] WALL_PATH = { 0, 2, 3 };   // wood palisade -> scrap metal -> concrete
        public const double SIREN_LEAD_H = 3.0;
        public static double RAID_WALL_HIT => PhaseARules ? 0.6 : 1.1;   // was 1.1: a night horde chews through a wood palisade from ~day 12, scrap metal from ~day 20

        // ---------------- geometry ----------------
        /// <summary>Tiles under a straight line (sampled every 0.2 tile, so the set is 4-connected and zombies,
        /// which only step N/S/E/W, can't slip through a diagonal).</summary>
        public List<int> LineTiles(Vector2 a, Vector2 b)
        {
            var o = new List<int>();
            var seen = new HashSet<int>();
            float len = (b - a).magnitude;
            int n = Math.Max(1, (int)Math.Ceiling(len / 0.2f));
            int lastX = int.MinValue, lastY = int.MinValue;
            for (int i = 0; i <= n; i++)
            {
                var p = Vector2.Lerp(a, b, (float)i / n);
                int x = Mathf.FloorToInt(p.x), y = Mathf.FloorToInt(p.y);
                if (lastX != int.MinValue && x != lastX && y != lastY)
                {
                    // diagonal step: add the corner tile the line passes closest to
                    int cx = lastX, cy = y;
                    var mid = new Vector2(lastX + 0.5f, y + 0.5f); var alt = new Vector2(x + 0.5f, lastY + 0.5f);
                    if (DistToSeg(alt, a, b) < DistToSeg(mid, a, b)) { cx = x; cy = lastY; }
                    var cq = new Vector2Int(cx, cy);
                    if (D.InMap(cq) && seen.Add(Ti(cq))) o.Add(Ti(cq));
                }
                lastX = x; lastY = y;
                var q = new Vector2Int(x, y);
                if (!D.InMap(q)) continue;
                int k = Ti(q);
                if (seen.Add(k)) o.Add(k);
            }
            return o;
        }

        static float DistToSeg(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a; float l2 = ab.sqrMagnitude;
            float t = l2 > 0f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / l2) : 0f;
            return (p - (a + ab * t)).magnitude;
        }

        public List<int> RunTiles(WallRun r) => LineTiles(r.A, r.B);

        /// <summary>An intact fence tile (or a building) on the straight line between two points.</summary>
        public bool WallBetween(Vector2 a, Vector2 b)
        {
            int ka = Ti(V.I(a)), kb = Ti(V.I(b));
            foreach (int k in LineTiles(a, b))
            {
                if (k == ka || k == kb) continue;
                if (S.Fences.Has(k)) return true;
            }
            return false;
        }

        /// <summary>Recompute safe zones after a base level change made outside UpgradeBase (tools / previews).</summary>
        public void RefreshSafeZones() => RebuildSafe();

        public WallRun RunById(int id) => S.Walls.FirstOrDefault(w => w.Id == id);

        /// <summary>The wall run a tile belongs to (own colony first), or null.</summary>
        public WallRun RunAtTile(int k)
        {
            WallRun best = null;
            foreach (var r in S.Walls)
            {
                if (!RunTiles(r).Contains(k)) continue;
                if (r.Col == CurCol) return r;
                best ??= r;
            }
            return best;
        }

        /// <summary>Nearest own wall run to a point (tile space), within maxD tiles.</summary>
        public WallRun RunNear(Vector2 p, float maxD = 1.2f)
        {
            WallRun best = null; float bd = maxD;
            foreach (var r in S.Walls)
            {
                if (r.Col != CurCol) continue;
                float d = DistToSeg(p, r.A, r.B);
                if (d < bd) { bd = d; best = r; }
            }
            return best;
        }

        // ---------------- yards & the starter palisade ----------------
        readonly Dictionary<int, (int lot, int n)> _yardLots = new Dictionary<int, (int, int)>();

        /// <summary>Other buildings whose centre is inside a camp's palisade yard (the view clears them into the
        /// yard; they count as sheds and outbuildings, i.e. beds, instead of conversion targets).</summary>
        public int YardLotCount(Base b)
        {
            if (b == null || S?.Walls == null || !S.Walls.Any(w => w.Starter && w.Bid == b.Id)) return 0;
            if (_yardLots.TryGetValue(b.Id, out var c) && c.lot == b.Lot) return c.n;
            var y = YardOf(b);
            int n = 0;
            var ct = V.I(y.C); int R = (int)Math.Ceiling(y.Half) + 2;
            var seen = new HashSet<int>();
            for (int dy = -R; dy <= R; dy++)
                for (int dx = -R; dx <= R; dx++)
                {
                    var q = ct.Add(dx, dy);
                    if (!D.InMap(q)) continue;
                    int l = D.LotAt(q);
                    if (l < 0 || l == b.Lot || !seen.Add(l) || D.Lots[l].Cells < 2) continue;
                    var dd = D.LotCenter(l) - y.C;
                    if (Math.Abs(Vector2.Dot(dd, y.U)) < y.Half + 0.2f && Math.Abs(Vector2.Dot(dd, y.V)) < y.Half + 0.2f) n++;
                }
            _yardLots[b.Id] = (b.Lot, n);
            return n;
        }

        /// <summary>Lot centre inside a camp yard (the view clears those buildings for the palisade yard).</summary>
        public bool InCampYard(int l)
        {
            if (l < 0 || l >= D.Lots.Count || S?.Walls == null) return false;
            var c = D.LotCenter(l);
            foreach (var b in AllBases())
            {
                if (!S.Walls.Any(w => w.Starter && w.Bid == b.Id)) continue;
                var y = YardOf(b);
                var dd = c - y.C;
                if (Math.Abs(Vector2.Dot(dd, y.U)) < y.Half + 0.2f && Math.Abs(Vector2.Dot(dd, y.V)) < y.Half + 0.2f) return true;
            }
            return false;
        }

        public YardGeo YardOf(Base b)
        {
            var y = YardProvider?.Invoke(b);
            if (y.HasValue) return y.Value;
            // headless estimate (mirrors MapGen.CampRect: building box + 1.5 tiles, city axes)
            double th = D.RgAngle * Math.PI / 180.0;
            var gu = new Vector2((float)Math.Cos(th), (float)Math.Sin(th)); var gv = new Vector2(-gu.y, gu.x);
            Vector2 c; float half;
            if (b.Lot >= 0 && b.Lot < D.Lots.Count)
            {
                var L = D.Lots[b.Lot];
                c = D.LotCenter(b.Lot);
                half = Math.Max(L.W, L.H) * 0.42f + (b.Main ? 1.5f : 0.9f);
            }
            else { c = V.F(b.Pos).Add(V.HALF); half = b.Main ? 3f : 2f; }
            // gate faces the base door
            var toDoor = (V.F(b.Pos).Add(V.HALF) - c);
            Vector2 vv = gv;
            float best = -9f;
            foreach (var ax in new[] { gu, -gu, gv, -gv }) { float dd = Vector2.Dot(ax, -toDoor); if (dd > best) { best = dd; vv = ax; } }
            var uu = new Vector2(vv.y, -vv.x);
            return new YardGeo { C = c, U = uu, V = vv, Half = half, Gate = c - vv * half };
        }

        /// <summary>Raise the free wooden palisade (with its gate) around a new camp, along the yard drawn around it.
        /// Tiles that are not open ground (neighbouring buildings, water) are left: the dead can't cross them anyway.</summary>
        public int FoundPalisade(Base b)
        {
            if (b == null) return 0;
            var y = YardOf(b);
            var corners = new[] { y.C - y.U * y.Half - y.V * y.Half, y.C + y.U * y.Half - y.V * y.Half, y.C + y.U * y.Half + y.V * y.Half, y.C - y.U * y.Half + y.V * y.Half };
            int n = 0;
            double hp = FenceMaxHp(0);
            int gateK = -1; float gd = 9f;
            for (int s = 0; s < 4; s++)
            {
                var a = corners[s]; var e = corners[(s + 1) % 4];
                var run = new WallRun { Id = NewId(), A = a, B = e, Tier = 0, Col = b.Col, Bid = b.Id, Starter = true };
                if (s == 0)
                {
                    float len = (e - a).magnitude;
                    run.GapT = Mathf.Clamp01(Vector2.Dot(y.Gate - a, (e - a) / Math.Max(0.01f, len)) / Math.Max(0.01f, len));
                    run.GapW = 0.5f;
                }
                S.Walls.Add(run);
                foreach (int k in LineTiles(a, e))
                {
                    var p = Tp(k);
                    if (!D.Walkable(p) || S.Towers.Has(k)) continue;
                    bool door = false;
                    foreach (var ob in AllBases()) if (ob.Pos.Same(p)) door = true;
                    if (S.Fences.TryGetValue(k, out var ef) && ef.Col != b.Col) continue;
                    if (!door) { S.Fences[k] = new Fence { Tier = 0, Hp = hp, Col = b.Col }; n++; }
                    if (s == 0)
                    {
                        float dg = (V.F(p).Add(V.HALF) - y.Gate).magnitude;
                        if (!door && dg < gd) { gd = dg; gateK = k; }
                    }
                }
            }
            if (gateK >= 0) { S.Gates[gateK] = b.Col; S.Fences[gateK].Hp = FenceTileMax(gateK); }
            S.WallsInit = true;
            FencesChanged();
            return n;
        }

        /// <summary>Older saves (before v0.12.0a) get the starter palisade on load.</summary>
        void EnsureWalls()
        {
            S.Walls ??= new List<WallRun>();
            S.Breaches ??= new ODict<int, Breach>();
            if (S.WallsInit || !PhaseARules) return;
            if (YardProvider == null && !AllowYardFallback) return;   // the view calls EnsureWallsNow once its map exists
            int prev = CurCol;
            for (int ci = 0; ci < S.Cols.Count; ci++)
            {
                var c = S.Cols[ci];
                if (!c.Started || !ColAlive(c)) continue;
                Use(ci);
                foreach (var b in c.Bases.Where(b => b.Main).ToList()) FoundPalisade(b);
            }
            S.WallsInit = true;
            Use(prev >= 0 ? prev : ViewCol);
        }

        /// <summary>Called by the view after building the map (so the yard geometry is known): older saves get
        /// their starter palisade here.</summary>
        public void EnsureWallsNow()
        {
            if (S == null || S.WallsInit || S.Cols.Count == 0) return;
            EnsureWalls();
            EmitChanged();
        }

        // ---------------- placing walls ----------------
        /// <summary>Per tile of a planned run: 0 builds, 1 natural wall (building / water: nothing to build),
        /// 2 can't build (with the reason in Err), 3 already at this tier or better.</summary>
        public sealed class WallPlan
        {
            public List<int> Tiles = new List<int>();
            public List<int> State = new List<int>();
            public List<int> Build = new List<int>();
            public Dictionary<string, double> Cost = new Dictionary<string, double>();
            public string Err = "";
            public bool CanAfford;
        }

        public WallPlan PlanWall(Vector2 a, Vector2 b, int tier)
        {
            var pl = new WallPlan();
            if ((b - a).magnitude > WALL_MAX_LEN + 0.01f) { pl.Err = string.Format("Too long ({0} tiles max per wall).", WALL_MAX_LEN); }
            foreach (int k in LineTiles(a, b))
            {
                var p = Tp(k);
                pl.Tiles.Add(k);
                if (!D.Walkable(p)) { pl.State.Add(1); continue; }
                if (S.Fences.TryGetValue(k, out var f) && f.Col == CurCol && f.Tier >= tier) { pl.State.Add(3); continue; }
                string e = FenceTileError(p, tier);
                if (e == "" && ZombiesNear(V.F(p).Add(V.HALF), 0.8, 1).Count > 0) e = "Zombies in the way.";
                if (e == "") { pl.State.Add(0); pl.Build.Add(k); }
                else { pl.State.Add(2); if (pl.Err == "") pl.Err = e; }
            }
            pl.Cost = FenceCost(tier, pl.Build.Count);
            pl.CanAfford = CanAfford(pl.Cost);
            if (pl.Build.Count == 0 && pl.Err == "") pl.Err = pl.State.Any(s => s == 3) ? "Already walled." : "Nothing to build here.";
            if (!FenceTierUnlocked(tier)) pl.Err = string.Format("Research {0} first.", UnlockSourceName(Data.FENCES[tier].Unlock));
            return pl;
        }

        public string PlaceWall(Vector2 a, Vector2 b, int tier)
        {
            if (tier < 0 || tier >= Data.FENCES.Length) return "Unknown wall.";
            if (!FenceTierUnlocked(tier)) return string.Format("Research {0} first.", UnlockSourceName(Data.FENCES[tier].Unlock));
            if ((b - a).magnitude < 0.9f) return "Drag the wall out a little longer.";
            var pl = PlanWall(a, b, tier);
            if ((b - a).magnitude > WALL_MAX_LEN + 0.01f) return pl.Err;
            if (pl.Build.Count == 0) return pl.Err;
            if (!pl.CanAfford) return "Need " + FmtCost(pl.Cost) + ".";
            Pay(pl.Cost);
            double hp = FenceMaxHp(tier);
            foreach (int k in pl.Build)
            {
                S.Fences[k] = new Fence { Tier = tier, Hp = hp, Col = CurCol };
                S.Breaches.Remove(k);
            }
            var nb = NearestOwnBase((a + b) * 0.5f);
            S.Walls.Add(new WallRun { Id = NewId(), A = a, B = b, Tier = tier, Col = CurCol, Bid = nb?.Id ?? -1 });
            FencesChanged();
            PlaySfx("hammer", (a + b) * 0.5f);
            AddLog(string.Format("Built a {0}-tile {1}.", pl.Build.Count, Lower(Data.FENCES[tier].Name)), "info");
            EmitChanged();
            return "";
        }

        public int NextWallTier(int tier)
        {
            foreach (int t in WALL_PATH) if (t > tier) return t;
            return -1;
        }

        /// <summary>Upgrade cost of a run (every fence tile and breach on it rebuilt at the next tier).</summary>
        public (int next, List<int> tiles, Dictionary<string, double> cost, string err) WallUpgradePlan(WallRun r)
        {
            if (r == null) return (-1, new List<int>(), new Dictionary<string, double>(), "No wall there.");
            if (r.Col != CurCol) return (-1, new List<int>(), new Dictionary<string, double>(), "That wall isn't yours.");
            int nt = NextWallTier(r.Tier);
            if (nt < 0) return (-1, new List<int>(), new Dictionary<string, double>(), "Already concrete: the strongest wall you can build.");
            var tiles = new List<int>();
            foreach (int k in RunTiles(r))
            {
                bool mine = (S.Fences.TryGetValue(k, out var f) && f.Col == CurCol && f.Tier < nt) || (S.Breaches.TryGetValue(k, out var br) && br.Col == CurCol && !S.Fences.Has(k));
                if (mine) tiles.Add(k);
            }
            var cost = FenceCost(nt, tiles.Count);
            string err = !FenceTierUnlocked(nt) ? string.Format("Research {0} first.", UnlockSourceName(Data.FENCES[nt].Unlock)) : tiles.Count == 0 ? "Nothing on this wall to upgrade." : "";
            return (nt, tiles, cost, err);
        }

        public string UpgradeWall(int runId)
        {
            var r = RunById(runId);
            var up = WallUpgradePlan(r);
            if (up.err != "") return up.err;
            if (!CanAfford(up.cost)) return "Need " + FmtCost(up.cost) + ".";
            Pay(up.cost);
            double hp = FenceMaxHp(up.next);
            foreach (int k in up.tiles)
            {
                bool gate = IsGate(k) || (S.Breaches.TryGetValue(k, out var br) && br.Gate);
                S.Fences[k] = new Fence { Tier = up.next, Hp = hp, Col = CurCol };
                if (gate) { S.Gates[k] = CurCol; S.Fences[k].Hp = FenceTileMax(k); }
                S.Breaches.Remove(k);
            }
            r.Tier = up.next;
            // every other run sharing these tiles shows the new material too
            FencesChanged();
            PlaySfx("hammer", (r.A + r.B) * 0.5f);
            AddLog(string.Format("Upgraded {0} wall tiles to {1}.", up.tiles.Count, Lower(Data.FENCES[up.next].Name)), "good");
            EmitChanged();
            return "";
        }

        public string RemoveWall(int runId)
        {
            var r = RunById(runId);
            if (r == null) return "No wall there.";
            if (r.Col != CurCol) return "That wall isn't yours.";
            var keys = RunTiles(r).Where(k => S.Fences.TryGetValue(k, out var f) && f.Col == CurCol && !TileInOtherRun(k, r)).ToList();
            foreach (int k in RunTiles(r)) if (!TileInOtherRun(k, r)) S.Breaches.Remove(k);
            S.Walls.Remove(r);
            if (keys.Count > 0) RemoveFenceKeys(keys);
            else { FencesChanged(); EmitChanged(); }
            return "";
        }

        bool TileInOtherRun(int k, WallRun r)
        {
            foreach (var o in S.Walls) if (o != r && o.Col == r.Col && RunTiles(o).Contains(k)) return true;
            return false;
        }

        // ---------------- damage, breaches, repairs ----------------
        /// <summary>Called by HitBlocker when a fence tile hits 0 hp: remember it so it can be rebuilt.</summary>
        void RecordBreach(int k, Fence f, bool gate)
        {
            if (!PhaseARules) return;
            S.Breaches[k] = new Breach { Tier = f.Tier, Col = f.Col, T = S.T, Gate = gate };
        }

        public int BreachCount(int ci = -1)
        {
            if (ci < 0) ci = CurCol;
            int n = 0;
            foreach (var kv in S.Breaches) if (kv.Value.Col == ci && !S.Fences.Has(kv.Key)) n++;
            return n;
        }

        /// <summary>Wall tiles below 50% plus breaches (the "needs repair" flag in the HUD).</summary>
        public int DamagedWallCount(int ci = -1)
        {
            if (ci < 0) ci = CurCol;
            int n = 0;
            foreach (var kv in S.Fences) if (kv.Value.Col == ci && kv.Value.Hp < FenceTileMax(kv.Key) * 0.5) n++;
            return n + BreachCount(ci);
        }

        double BreachCostMats(Breach br) => MatSum(FenceCost(V.Clampi(br.Tier, 0, Data.FENCES.Length - 1), 1)) * RepairCostMult() / (1.0 + Rx("repair_mult"));

        /// <summary>Rebuild a breach (a survivor finished the job, or the player paid): false if the tile is
        /// blocked by zombies or the materials ran out.</summary>
        bool RebuildBreach(int k)
        {
            if (!S.Breaches.TryGetValue(k, out var br) || S.Fences.Has(k)) { S.Breaches.Remove(k); return false; }
            if (ZombiesNear(V.F(Tp(k)).Add(V.HALF), 0.7, 1).Count > 0) return false;
            int tier = V.Clampi(br.Tier, 0, Data.FENCES.Length - 1);
            if (!PayMatsFlex(BreachCostMats(br), Data.FENCES[tier].Cost)) return false;
            S.Fences[k] = new Fence { Tier = tier, Hp = FenceMaxHp(tier), Col = br.Col };
            if (br.Gate) { S.Gates[k] = br.Col; S.Fences[k].Hp = FenceTileMax(k); }
            S.Breaches.Remove(k);
            FencesChanged(Tp(k));
            return true;
        }

        /// <summary>Manual "repair now": patch every damaged wall tile, gate, tower and breach of the colony that
        /// the stockpile can pay for, at 1.5x the cost survivors would spend (no hauling, done at once).</summary>
        public (int fixedN, double spent, string err) RepairAllNow()
        {
            int n = 0; double spent = 0.0;
            double m0 = MatsTotal();
            foreach (var it in RepairQueue())
            {
                double cost = RepairCost(it) * 1.5;
                if (MatsTotal() + 0.0001 < cost) continue;
                bool ok = false;
                switch (it.kind)
                {
                    case "breach":
                        if (S.Breaches.TryGetValue(it.k, out var br))
                        {
                            int tier = V.Clampi(br.Tier, 0, Data.FENCES.Length - 1);
                            if (ZombiesNear(V.F(Tp(it.k)).Add(V.HALF), 0.7, 1).Count > 0) break;
                            if (!PayMatsFlex(cost, Data.FENCES[tier].Cost)) break;
                            S.Fences[it.k] = new Fence { Tier = tier, Hp = FenceMaxHp(tier), Col = br.Col };
                            if (br.Gate) { S.Gates[it.k] = br.Col; S.Fences[it.k].Hp = FenceTileMax(it.k); }
                            S.Breaches.Remove(it.k);
                            ok = true;
                        }
                        break;
                    case "fence": case "gate":
                        if (S.Fences.TryGetValue(it.k, out var f) && PayMatsFlex(cost, Data.FENCES[V.Clampi(f.Tier, 0, Data.FENCES.Length - 1)].Cost)) { f.Hp = FenceTileMax(it.k); ok = true; }
                        break;
                    case "tower":
                        if (S.Towers.TryGetValue(it.k, out var t) && PayMatsFlex(cost, Data.TOWERS[t.Tier].Cost)) { t.Hp = Data.TOWERS[t.Tier].Hp; ok = true; }
                        break;
                    case "trap":
                        if (S.Traps.TryGetValue(it.k, out var tr) && PayMatsFlex(cost, Data.TRAPS[tr.Kind].Cost)) { tr.Hp = TrapHpMax(tr); ok = true; }
                        break;
                }
                if (ok) n++;
            }
            spent = m0 - MatsTotal();
            if (n == 0) return (0, 0.0, RepairQueue().Count == 0 ? "Nothing needs repair." : "Not enough materials (or the dead are standing in the breach).");
            FencesChanged();
            PlaySfx("hammer", MainBase() != null ? V.F(MainBase().Pos) : Vector2.zero);
            AddLog(string.Format("Crews patched {0} wall section{1} for {2:0} materials.", n, n == 1 ? "" : "s", spent), "good");
            EmitChanged();
            return (n, spent, "");
        }

        // ---------------- raids & the siren ----------------
        public bool HasSiren(int ci = -1)
        {
            var c = ci < 0 ? C : ColonyAt(ci);
            return c != null && c.Bases.Any(b => b.U("siren") > 0);
        }

        /// <summary>Hours before the raid reaches the wall when the lookouts raise the alarm.</summary>
        double RaidWarnLead() => Math.Max(RAID_WARN_H, RAID_TRAVEL_H) + (PhaseARules && HasSiren() ? SIREN_LEAD_H : 0.0);

        /// <summary>HUD state of the next / current attack on the viewed colony: phase "warn" (countdown),
        /// "attack" (at the walls), or null.</summary>
        public (string phase, int n, double hoursLeft, bool major, string dir, int atWall, int breaches)? AttackStatus(int ci = -1)
        {
            if (ci < 0) ci = ViewCol;
            var c = ColonyAt(ci);
            if (c == null || !c.Started || !ColAlive(c)) return null;
            var R0 = c.Nraid;
            if (R0 == null) return null;
            int prev = CurCol;
            int br = 0;
            foreach (var kv in S.Breaches) if (kv.Value.Col == ci && !S.Fences.Has(kv.Key)) br++;
            if (R0.On)
            {
                if (R0.Eta > 0.0 && S.T < R0.Eta - 0.25) return ("warn", R0.Spawned, R0.Eta - S.T, R0.Major, DirName(R0.Dir), R0.AtWall, br);
                return ("attack", R0.Spawned, 0.0, R0.Major, DirName(R0.Dir), R0.AtWall, br);
            }
            if (R0.Warned && R0.NextT > 0.0) return ("warn", R0.N, Math.Max(0.0, (R0.Eta > 0.0 ? R0.Eta : R0.NextT) - S.T), R0.Major, DirName(R0.Dir), 0, br);
            return null;
        }

        /// <summary>Raiders within a few tiles of the attacked camp's wall line right now.</summary>
        void CountRaidAtWall(Raid R0)
        {
            if (!R0.On) return;
            var b = BaseById(R0.Bid) ?? MainBase();
            if (b == null) return;
            var y = YardOf(b);
            float lim = y.Half + 2.2f;
            int n = 0;
            for (int i = 0; i < Zc.Count; i++)
            {
                if (Zh[i] != 3) continue;
                var d = ZPos(i) - y.C;
                if (Mathf.Abs(Vector2.Dot(d, y.U)) < lim && Mathf.Abs(Vector2.Dot(d, y.V)) < lim) n++;
            }
            R0.AtWall = Math.Max(R0.AtWall, n);
            _raidAtWallNow = n;
        }
        int _raidAtWallNow;
        public int RaidAtWallNow => _raidAtWallNow;
    }
}
