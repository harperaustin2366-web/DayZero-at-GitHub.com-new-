// game.gd: fences (tiles and road-graph segments), towers, enclosures, territory reach,
// claimed land, gates, traps, repairs, base levels/slots/beds.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DayZero.Core
{
    public sealed partial class Game
    {
        public const int ENC_R = 70;
        public const int FENCE_MAX_TILES = 160;
        public const double FENCE_REACH_MULT = 3.0;
        public const double TRAP_REACH_MULT = 2.0;
        public const double TERRITORY_PAD = 8.0;
        public const int ENC_BOX_PAD = 14;
        public const int ENC_MIN = 20;
        public const int SEG_MAX_EDGES = 48;
        public const int TRAP_BIT = 16;

        readonly Dictionary<int, int[]> _encl = new Dictionary<int, int[]>();
        readonly Dictionary<int, int> _enclTier = new Dictionary<int, int>();
        int _enclVer = -1;
        byte[] _territory = new byte[0];

        ODict<int, Fence> _F = new ODict<int, Fence>();
        ODict<int, Tower> _T = new ODict<int, Tower>();
        ODict<int, Trap> _TR = new ODict<int, Trap>();

        public bool Blocked(int k) => _F.Has(k) || _T.Has(k);

        void RefreshRefs()
        {
            _F = S.Fences; _T = S.Towers; _TR = S.Traps;
            SyncFenceBlocks();
            if (!_lureDirty && ReferenceEquals(_TR, _lureSrc)) return;
            _lureDirty = false; _lureSrc = _TR;
            _lures.Clear();
            foreach (var kv in _TR)
            {
                var t = kv.Value;
                if (t.Hp > 0.0 && t.Kind < Data.TRAPS.Length && (Data.TRAPS[t.Kind].Fx ?? "") == "lure")
                    _lures.Add((V.F(Tp(kv.Key)).Add(V.HALF), TrapRadius(t.Kind, TrapLvl(t))));
            }
        }

        // ---------------- v0.14.0a: fences block people, gates let them through ----------------
        // Austin's rule change: survivors, squads and vehicles path around fence tiles and can only cross a fence
        // line at a gate (the zombies already had to break fences and gates). The A* grid gets a fence layer
        // (AStarGrid.SetBlocked); it is synced here whenever the fences change (FencesChanged -> RebuildSafe ->
        // RefreshRefs) or a save is loaded. With no fences nothing is blocked and paths are the original's.
        readonly HashSet<int> _pathBlocks = new HashSet<int>();
        object _blockSrc;
        int _blockVer = -1;

        void SyncFenceBlocks()
        {
            if (S == null || Astar == null) return;
            if (ReferenceEquals(_blockSrc, S) && _blockVer == S.FenceVer && _pathBlocks.Count == CountPathBlocks()) return;
            _blockSrc = S; _blockVer = S.FenceVer;
            var want = new HashSet<int>();
            foreach (var kv in S.Fences) if (!S.Gates.Has(kv.Key)) want.Add(kv.Key);
            bool changed = false;
            foreach (int k in _pathBlocks) if (!want.Contains(k)) { Astar.SetBlocked(Tp(k), false); changed = true; }
            foreach (int k in want) if (!_pathBlocks.Contains(k)) { Astar.SetBlocked(Tp(k), true); changed = true; }
            if (!changed) return;
            _pathBlocks.Clear(); foreach (int k in want) _pathBlocks.Add(k);
            _pathCache.Clear(); _pathOrder.Clear();
            // survivors who were waiting at a closed fence try their route again (a gate may have gone in)
            foreach (int id in _rtFenced) if (_rt.TryGetValue(id, out var rr)) rr.Code = "";
            _rtFenced.Clear();
        }

        /// <summary>Survivors whose daily route is cut by a fence with no gate (Game.Family.cs Routine waits for them).</summary>
        readonly HashSet<int> _rtFenced = new HashSet<int>();

        int CountPathBlocks()
        {
            int n = 0;
            foreach (var kv in S.Fences) if (!S.Gates.Has(kv.Key)) n++;
            return n;
        }

        /// <summary>True when fences are what keeps a route from a to b closed (no gate on the way): the caller then
        /// keeps the survivor where they are instead of walking them through the fence.</summary>
        public bool FenceBlocksRoute(Vector2Int a, Vector2Int b) => _pathBlocks.Count > 0 && !a.Same(b);

        /// <summary>Is this tile part of a fence line that people can't walk through (gates excluded)?</summary>
        public bool PathBlocked(int k) => _pathBlocks.Contains(k);

        public int FenceOwner(int k) => S.Fences.TryGetValue(k, out var f) ? f.Col : -1;
        public int TowerOwner(int k) => S.Towers.TryGetValue(k, out var t) ? t.Col : -1;
        public bool FenceTierUnlocked(int t) => t <= 0 || HasUnlock(Data.FENCES[t].Unlock);
        public bool TowerTierUnlocked(int t) => HasUnlock(Data.TOWERS[t].Unlock);
        public double FenceMaxHp(int tier) => Data.FENCES[tier].Hp * (1.0 + Rx("fence_hp"));

        public Dictionary<string, double> FenceCost(int tier, int n)
        {
            var o = new Dictionary<string, double>();
            double disc = 1.0 - Math.Min(0.5, Rx("craft_disc"));
            foreach (var kv in Data.FENCES[tier].Cost) o[kv.Key] = Math.Ceiling(kv.Value * n * disc);
            return o;
        }

        public double BaseZoneR(Base b) => BaseLevelDef(b).Zone;

        public BaseLevelDef BaseLevelDef(Base b)
        {
            var arr = b.Main ? Data.BASE_LEVELS["main"] : Data.BASE_LEVELS["outpost"];
            return arr[V.Clampi(b.Lvl, 0, arr.Length - 1)];
        }

        public bool InTerritory(Vector2Int p, int ci)
        {
            if (!D.InMap(p)) return false;
            int k = Ti(p);
            if (k < _territory.Length && _territory[k] == ci + 1) return true;
            var c = ColonyAt(ci);
            if (c == null) return false;
            foreach (var b in c.Bases)
                if (V.F(b.Pos).DistTo(V.F(p)) <= (BaseZoneR(b) + TERRITORY_PAD) * FENCE_REACH_MULT) return true;
            if (k < _reach.Length && (_reach[k] & (1 << ci)) != 0) return true;
            int s = SectOf(p);
            if (S.Ctrl[s] == ci + 1) return true;
            foreach (var d in DIRS)
            {
                var q = p + d;
                if (D.InMap(q) && _territory[Ti(q)] == ci + 1) return true;
            }
            return false;
        }

        string FenceTileError(Vector2Int p, int tier)
        {
            if (!D.InMap(p)) return "Off the map.";
            if (!D.Walkable(p)) return "Fences go on streets and open ground.";
            int k = Ti(p);
            if (S.Towers.Has(k)) return "A tower stands there.";
            foreach (var b in AllBases()) if (b.Pos.Same(p)) return "That's a camp gate.";
            if (S.Fences.TryGetValue(k, out var f))
            {
                if (f.Col != CurCol) return string.Format("That fence belongs to {0}.", ColonyAt(f.Col)?.Name ?? "another colony");
                if (f.Tier >= tier) return "Already fenced.";
            }
            if (!InTerritory(p, CurCol)) return "Outside your territory. Clear the area with a squad first.";
            return "";
        }

        public BuildPreview FencePreview(List<Vector2Int> tiles, int tier)
        {
            var pv = new BuildPreview();
            var seen = new HashSet<Vector2Int>();
            foreach (var p in tiles)
            {
                if (seen.Contains(p)) { pv.Ok.Add(false); continue; }
                seen.Add(p);
                string e = FenceTileError(p, tier);
                if (e == "" && ZombiesNear(V.F(p).Add(V.HALF), 0.8, 1).Count > 0) e = "Zombies in the way.";
                pv.Ok.Add(e == "");
                if (e == "") pv.N++;
                else if (pv.Err == "") pv.Err = e;
            }
            pv.Cost = FenceCost(tier, pv.N);
            return pv;
        }

        public string BuildFence(List<Vector2Int> tiles, int tier)
        {
            if (tier < 0 || tier >= Data.FENCES.Length) return "Unknown fence.";
            if (!FenceTierUnlocked(tier)) return string.Format("Research {0} first.", Data.FENCES[tier].Name);
            if (tiles.Count == 0) return "Draw a line first.";
            if (tiles.Count > FENCE_MAX_TILES) return string.Format("That line is too long ({0} tiles max).", FENCE_MAX_TILES);
            var pv = FencePreview(tiles, tier);
            if (pv.N == 0) return pv.Err != "" ? pv.Err : "Nothing to build.";
            if (!CanAfford(pv.Cost)) return "Need " + FmtCost(pv.Cost) + ".";
            Pay(pv.Cost);
            double hp = FenceMaxHp(tier);
            for (int i = 0; i < tiles.Count; i++)
            {
                if (!pv.Ok[i]) continue;
                S.Fences[Ti(tiles[i])] = new Fence { Tier = tier, Hp = hp, Col = CurCol };
            }
            FencesChanged();
            PlaySfx("hammer", V.F(tiles[0]));
            AddLog(string.Format("Built {0} tiles of {1}.", pv.N, Lower(Data.FENCES[tier].Name)), "info");
            EmitChanged();
            return "";
        }

        /// <summary>remove_fence: tiles are Vector2Int positions (GDScript also accepted raw tile keys).</summary>
        public string RemoveFence(List<Vector2Int> tiles) => RemoveFenceKeys(tiles.Select(Ti).ToList());

        public string RemoveFenceKeys(List<int> keys)
        {
            int n = 0;
            foreach (int k in keys)
            {
                int se = SegAtTile(k);
                if (se >= 0 && S.Fsegs.Has(se))
                {
                    string e0 = RemoveFenceSeg(se);
                    if (e0 != "") return e0;
                    n++;
                    continue;
                }
                if (!S.Fences.Has(k)) continue;
                if (FenceOwner(k) != CurCol) return string.Format("That fence belongs to {0}.", ColonyAt(FenceOwner(k))?.Name ?? "another colony");
                var f = S.Fences[k];
                foreach (var kv in Data.FENCES[f.Tier].Cost) C.Res[kv.Key] = R(kv.Key) + Math.Floor(kv.Value * 0.5);
                S.Fences.Remove(k);
                S.Gates.Remove(k);
                n++;
            }
            if (n == 0) return "No fence there.";
            FencesChanged();
            EmitChanged();
            return "";
        }

        // ---------------- fence segments on the road graph ----------------
        readonly Dictionary<int, List<int>> _edgeCache = new Dictionary<int, List<int>>();
        readonly Dictionary<int, int> _tileSeg = new Dictionary<int, int>();
        int _tileSegVer = -1;

        public List<int> EdgeTiles(int e)
        {
            if (_edgeCache.TryGetValue(e, out var t)) return t;
            t = D.RgEdgeTiles(e);
            _edgeCache[e] = t;
            return t;
        }

        public int SegAtTile(int k)
        {
            if (_tileSegVer != S.FenceVer)
            {
                _tileSegVer = S.FenceVer;
                _tileSeg.Clear();
                foreach (var kv in S.Fsegs)
                    foreach (int t in EdgeTiles(kv.Key))
                        if (!_tileSeg.ContainsKey(t)) _tileSeg[t] = kv.Key;
            }
            return _tileSeg.TryGetValue(k, out int s) ? s : -1;
        }

        public (bool ok, string err, List<int> build, bool dup) EdgeCheck(int e, int tier)
        {
            var build = new List<int>();
            if (e < 0 || e >= D.RgEdges()) return (false, "No fence post there.", build, false);
            if (S.Fsegs.TryGetValue(e, out var sg))
            {
                if (sg.Col != CurCol) return (false, string.Format("That fence belongs to {0}.", ColonyAt(sg.Col)?.Name ?? "another colony"), build, false);
                if (sg.Tier >= tier) return (false, "Already fenced.", build, true);
            }
            foreach (int k in EdgeTiles(e))
            {
                var p = Tp(k);
                if (!D.Walkable(p)) continue;
                if (S.Towers.Has(k))
                {
                    if (TowerOwner(k) == CurCol) continue;
                    return (false, "Another colony's tower is in the way.", build, false);
                }
                if (S.Fences.TryGetValue(k, out var f))
                {
                    if (f.Col != CurCol) return (false, string.Format("That fence belongs to {0}.", ColonyAt(f.Col)?.Name ?? "another colony"), build, false);
                    if (f.Tier >= tier) continue;
                }
                if (!InTerritory(p, CurCol)) return (false, "Outside your territory. Clear the area with a squad first.", build, false);
                if (ZombiesNear(V.F(p).Add(V.HALF), 0.8, 1).Count > 0) return (false, "Zombies in the way.", build, false);
                build.Add(k);
            }
            return (true, "", build, false);
        }

        public BuildPreview SegPreview(List<int> edges, int tier)
        {
            var pv = new BuildPreview();
            var tiles = new HashSet<int>();
            var seen = new HashSet<int>();
            foreach (int ei in edges)
            {
                if (seen.Contains(ei)) { pv.Ok.Add(false); continue; }
                seen.Add(ei);
                var r = EdgeCheck(ei, tier);
                pv.Ok.Add(r.ok);
                if (r.ok)
                {
                    pv.Edges++;
                    foreach (int k in r.build) tiles.Add(k);
                }
                else if (pv.Err == "") pv.Err = r.err;
            }
            pv.N = tiles.Count;
            pv.Cost = FenceCost(tier, tiles.Count);
            return pv;
        }

        public string BuildFenceSegs(List<int> edges, int tier)
        {
            if (tier < 0 || tier >= Data.FENCES.Length) return "Unknown fence.";
            if (!FenceTierUnlocked(tier)) return string.Format("Research {0} first.", Data.FENCES[tier].Name);
            if (edges.Count == 0) return "Tap two fence posts first.";
            if (edges.Count > SEG_MAX_EDGES) return string.Format("That fence is too long ({0} sections max).", SEG_MAX_EDGES);
            var pv = SegPreview(edges, tier);
            if (pv.Edges == 0) return pv.Err != "" ? pv.Err : "Nothing to build.";
            if (!CanAfford(pv.Cost)) return "Need " + FmtCost(pv.Cost) + ".";
            Pay(pv.Cost);
            double hp = FenceMaxHp(tier);
            var first = V.NONE;
            for (int i = 0; i < edges.Count; i++)
            {
                if (!pv.Ok[i]) continue;
                int e = edges[i];
                var r = EdgeCheck(e, tier);
                S.Fsegs[e] = new FenceSeg { Tier = tier, Col = CurCol };
                foreach (int k in r.build)
                {
                    S.Fences[k] = new Fence { Tier = tier, Hp = hp * (IsGate(k) ? Data.GATE_HP_MULT : 1.0), Col = CurCol };
                    if (first.x < 0) first = Tp(k);
                }
            }
            FencesChanged();
            PlaySfx("hammer", first.x >= 0 ? V.F(first) : V.F(V.I(D.RgPos(D.RgEa[edges[0]]))));
            AddLog(string.Format("Built {0} fence section{1} of {2} ({3} tiles).", pv.Edges, pv.Edges == 1 ? "" : "s", Lower(Data.FENCES[tier].Name), pv.N), "info");
            EmitChanged();
            return "";
        }

        public string RemoveFenceSeg(int e)
        {
            if (!S.Fsegs.TryGetValue(e, out var sg)) return "No fence there.";
            if (sg.Col != CurCol) return string.Format("That fence belongs to {0}.", ColonyAt(sg.Col)?.Name ?? "another colony");
            var keep = new HashSet<int>();
            foreach (var kv in S.Fsegs)
            {
                if (kv.Key == e) continue;
                foreach (int k in EdgeTiles(kv.Key)) keep.Add(k);
            }
            foreach (int k in EdgeTiles(e))
            {
                if (keep.Contains(k) || !S.Fences.Has(k) || FenceOwner(k) != CurCol) continue;
                foreach (var kv in Data.FENCES[S.Fences[k].Tier].Cost) C.Res[kv.Key] = R(kv.Key) + kv.Value * 0.5;
                S.Fences.Remove(k);
                S.Gates.Remove(k);
            }
            S.Fsegs.Remove(e);
            FencesChanged();
            EmitChanged();
            return "";
        }

        /// <summary>suggest_loop: a closed ring of road-graph edges around the camp, or empty.</summary>
        public List<int> SuggestLoop(Base b, int tier = 0)
        {
            if (b == null || D.RgCount() == 0) return new List<int>();
            bool wild = D.IsWild(b.Pos);
            double th = wild ? 0.0 : D.RgAngle * Math.PI / 180.0;
            float cu = (float)Math.Cos(th), su = (float)Math.Sin(th);
            var c = V.F(b.Pos).Add(V.HALF);
            float u0 = c.x * cu + c.y * su, v0 = -c.x * su + c.y * cu;
            float hb = wild ? 32f : 24f;
            var near = D.RgNodesIn(new Rect(c.x - hb, c.y - hb, hb * 2f, hb * 2f));
            var us = new List<float>(); var vs = new List<float>();
            var nuv = new ODict<int, Vector2>();
            foreach (int n in near)
            {
                if (wild && n < D.CityPosts) continue;
                var p = D.RgPos(n);
                float u = p.x * cu + p.y * su, v = -p.x * su + p.y * cu;
                nuv[n] = new Vector2(u, v);
                if (!us.Any(x => Math.Abs(x - u) < 0.3f)) us.Add(u);
                if (!vs.Any(x => Math.Abs(x - v) < 0.3f)) vs.Add(v);
            }
            var left = us.Where(u => u < u0 - 0.2f).ToList(); var right = us.Where(u => u > u0 + 0.2f).ToList();
            var top = vs.Where(v => v < v0 - 0.2f).ToList(); var bot = vs.Where(v => v > v0 + 0.2f).ToList();
            Gd.StableSort(left, (x, y) => y.CompareTo(x)); Gd.StableSort(right, (x, y) => x.CompareTo(y));
            Gd.StableSort(top, (x, y) => y.CompareTo(x)); Gd.StableSort(bot, (x, y) => x.CompareTo(y));
            var combos = new List<(float area, float l, float r, float t, float b)>();
            int nl = wild ? 5 : 3;
            for (int il = 0; il < Math.Min(nl, left.Count); il++)
                for (int ir = 0; ir < Math.Min(nl, right.Count); ir++)
                    for (int it = 0; it < Math.Min(nl, top.Count); it++)
                        for (int ib = 0; ib < Math.Min(nl, bot.Count); ib++)
                            combos.Add(((right[ir] - left[il]) * (bot[ib] - top[it]), left[il], right[ir], top[it], bot[ib]));
            Gd.StableSort(combos, (x, y) => x.area.CompareTo(y.area));
            int tried = 0;
            foreach (var cb in combos)
            {
                if (tried >= (wild ? 48 : 24)) break;
                var corners = new List<int>();
                foreach (var uv in new[] { new Vector2(cb.l, cb.t), new Vector2(cb.r, cb.t), new Vector2(cb.r, cb.b), new Vector2(cb.l, cb.b) })
                {
                    int found = -1;
                    foreach (var kv in nuv)
                        if (kv.Value.DistTo(uv) < 0.45f) { found = kv.Key; break; }
                    corners.Add(found);
                }
                if (corners.Contains(-1)) continue;
                tried++;
                var chain = new List<int>();
                bool ok = true;
                for (int i = 0; i < 4; i++)
                {
                    var path = D.RgPath(corners[i], corners[(i + 1) % 4], 60.0);
                    if (path.Count == 0) { ok = false; break; }
                    chain.AddRange(path);
                }
                if (!ok) continue;
                var pv = SegPreview(chain, tier);
                bool allOk = true;
                for (int i = 0; i < chain.Count; i++)
                    if (!pv.Ok[i] && !S.Fsegs.Has(chain[i])) { allOk = false; break; }
                if (!allOk) continue;
                if (TryEncloses(b, chain, tier)) return chain;
            }
            return SuggestLoopAny(b, tier);
        }

        bool TryEncloses(Base b, List<int> chain, int tier)
        {
            var added = new List<int>();
            foreach (int e in chain)
                foreach (int k in EdgeTiles(e))
                    if (D.Walkable(Tp(k)) && !S.Fences.Has(k) && !S.Towers.Has(k))
                    {
                        S.Fences[k] = new Fence { Tier = tier, Hp = 1.0, Col = CurCol };
                        added.Add(k);
                    }
            var r = FloodEnclosure(b);
            foreach (int k in added) S.Fences.Remove(k);
            return r.all.Length >= ENC_MIN;
        }

        public Dictionary<string, object> SuggestDbg = new Dictionary<string, object>();
        void Sdbg(string k) => SuggestDbg[k] = (SuggestDbg.TryGetValue(k, out var v) ? (int)v : 0) + 1;

        List<int> SuggestLoopAny(Base b, int tier = 0)
        {
            var c = V.F(b.Pos).Add(V.HALF);
            double ra = D.RgAngle * Math.PI / 180.0;
            var angs = D.IsWild(b.Pos) ? new[] { 0.0, ra } : new[] { ra, 0.0 };
            var sizes = new[] { new Vector2(8, 8), new Vector2(11, 8), new Vector2(8, 11), new Vector2(12, 12), new Vector2(15, 11), new Vector2(11, 15), new Vector2(16, 16), new Vector2(20, 15), new Vector2(15, 20), new Vector2(22, 22), new Vector2(28, 22), new Vector2(22, 28) };
            int tried = 0;
            var seen = new HashSet<string>();
            var shifts = new[] { new Vector2(0, 0), new Vector2(0.5f, 0), new Vector2(-0.5f, 0), new Vector2(0, 0.5f), new Vector2(0, -0.5f) };
            var ctrs = new List<Vector2> { c };
            if (b.Lot >= 0)
            {
                var lc = D.LotCenter(b.Lot);
                if (lc.DistTo(c) > 1.5f) ctrs = new List<Vector2> { lc, c };
            }
            foreach (var sz in sizes)
                foreach (var sh in shifts)
                    foreach (double th in angs)
                        foreach (var cc in ctrs)
                        {
                            c = cc;
                            if (tried >= 80) return new List<int>();
                            float cu = (float)Math.Cos(th), su = (float)Math.Sin(th);
                            var corners = new List<int>();
                            foreach (var sg in new[] { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) })
                            {
                                var o = new Vector2((sg.x + sh.x) * sz.x, (sg.y + sh.y) * sz.y);
                                var tgt = c.Add(new Vector2(o.x * cu - o.y * su, o.x * su + o.y * cu));
                                int n = D.SnapPost(tgt, 4.5f);
                                if (n < 0) n = D.SnapPost(tgt, 7.0f);
                                if (n < 0 || corners.Contains(n)) break;
                                corners.Add(n);
                            }
                            var r = TryLoopCorners(b, corners, tier, seen);
                            if (r.chain == null) { tried += r.tried; continue; }
                            return r.chain;
                        }
            return new List<int>();
        }

        (List<int> chain, int tried) TryLoopCorners(Base b, List<int> corners, int tier, HashSet<string> seen)
        {
            if (corners.Count < 4) { Sdbg("corners"); return (null, 0); }
            string key = "[" + string.Join(", ", corners) + "]";
            if (seen.Contains(key)) return (null, 0);
            seen.Add(key);
            var chain = new List<int>();
            bool ok = true;
            for (int i = 0; i < 4; i++)
            {
                var path = D.RgPath(corners[i], corners[(i + 1) % 4], 90.0);
                if (path.Count == 0) { ok = false; break; }
                foreach (int e in path) if (!chain.Contains(e)) chain.Add(e);
            }
            if (!ok || chain.Count > SEG_MAX_EDGES) { Sdbg(!ok ? "path" : "long"); return (null, 1); }
            int gk = Ti(b.Pos);
            foreach (int e in chain)
                if (EdgeTiles(e).Contains(gk)) { Sdbg("over the gate"); return (null, 1); }
            var pv = SegPreview(chain, tier);
            for (int i = 0; i < chain.Count; i++)
                if (!pv.Ok[i] && !S.Fsegs.Has(chain[i])) { Sdbg("preview: " + pv.Err); return (null, 1); }
            if (TryEncloses(b, chain, tier)) return (chain, 0);
            Sdbg("not enclosing");
            if (SuggestDbg.TryGetValue("not enclosing", out var ne) && (int)ne == 1) SuggestDbg["chain"] = chain;
            return (null, 1);
        }

        void PatchGaps()
        {
            if (S.Fsegs.Count == 0 || (CountJob("Engineer") == 0 && CountJob("Civilian") == 0)) return;
            int n = 0;
            var last = V.NONE;
            foreach (var kv in S.Fsegs)
            {
                var sg = kv.Value;
                if (sg.Col != CurCol) continue;
                foreach (int k in EdgeTiles(kv.Key))
                {
                    if (S.Fences.Has(k) || S.Towers.Has(k)) continue;
                    var p = Tp(k);
                    if (!D.Walkable(p)) continue;
                    if (ZombiesNear(V.F(p).Add(V.HALF), 2.0, 1).Count > 0) continue;
                    var cost = FenceCost(sg.Tier, 1);
                    if (!CanAfford(cost)) goto done;
                    Pay(cost);
                    S.Fences[k] = new Fence { Tier = sg.Tier, Hp = FenceMaxHp(sg.Tier) * 0.5 * (IsGate(k) ? Data.GATE_HP_MULT : 1.0), Col = CurCol };
                    last = p;
                    n++;
                    if (n >= 2) break;
                }
                if (n >= 2) break;
            }
            if (n > 0)
            {
                FencesChanged(last);
                AddLog(string.Format("{0} patched a hole in the fence on {1}.", CountJob("Engineer") > 0 ? "Engineers" : "Volunteers", StreetName(last)), "info");
            }
            return;
        done:
            // GDScript returned straight out of the loop when it ran out of materials (no log, no rebuild).
            return;
        }

        List<int> _enclOnly = new List<int>();
        void FencesChanged(Vector2Int? nearArg = null)
        {
            var near = nearArg ?? V.NONE;
            bool wasSynced = _enclVer == S.FenceVer;
            S.FenceVer++;
            _flowDirty = true;
            _enclOnly = new List<int>();
            if (near.x >= 0 && wasSynced)
            {
                int oc = FenceOwner(Ti(near));
                foreach (var b in AllBases())
                {
                    var bx = _enclBox.TryGetValue(b.Id, out var r) ? r : new RectInt(0, 0, 0, 0);
                    if (Gd.Has(Gd.Grow(bx, 2), near) || V.F(b.Pos).DistTo(V.F(near)) <= ENC_R * 1.45 || b.Col == oc)
                        _enclOnly.Add(b.Id);
                }
                if (_enclOnly.Count == 0) _enclVer = S.FenceVer;
            }
            RebuildSafe();
        }

        // ---------------- enclosures ----------------
        readonly Dictionary<int, RectInt> _enclBox = new Dictionary<int, RectInt>();
        readonly Dictionary<int, int[]> _enclEdge = new Dictionary<int, int[]>();
        readonly Dictionary<int, int[]> _enclInner = new Dictionary<int, int[]>();
        readonly Dictionary<int, Vector2> _enclCtr = new Dictionary<int, Vector2>();
        readonly Dictionary<int, double> _enclRad = new Dictionary<int, double>();
        readonly Dictionary<int, int[]> _enclSects = new Dictionary<int, int[]>();
        readonly Dictionary<int, List<(float d, int l)>> _enclLots = new Dictionary<int, List<(float d, int l)>>();
        int[] _enclOwner = new int[0];
        public int EnclGen;
        public double EnclMs;
        int[] _reach = new int[0];
        int _reachVer = -1;

        void RebuildEnclosures()
        {
            long t0 = Gd.TicksMsec();
            _enclVer = S.FenceVer;
            var only = _enclOnly;
            _enclOnly = new List<int>();
            if (_enclOwner.Length != D.W * D.H) { _enclOwner = new int[D.W * D.H]; only = new List<int>(); }
            var bases = AllBases();
            var live = new HashSet<int>(bases.Select(b => b.Id));
            if (only.Count == 0)
            {
                _encl.Clear(); _enclTier.Clear(); _enclEdge.Clear(); _enclInner.Clear(); _enclCtr.Clear();
                _enclRad.Clear(); _enclSects.Clear(); _enclLots.Clear(); _enclBox.Clear();
                Array.Clear(_enclOwner, 0, _enclOwner.Length);
            }
            else
            {
                foreach (int bid in only)
                    if (_encl.TryGetValue(bid, out var old) && old != null)
                        foreach (int k in old)
                            if (_enclOwner[k] == bid + 1) _enclOwner[k] = 0;
            }
            foreach (int bid in _encl.Keys.ToList())
                if (!live.Contains(bid)) _encl.Remove(bid);
            var boxes = ColonyWallBoxes();
            foreach (var b in bases)
            {
                if (only.Count > 0 && !only.Contains(b.Id) && _encl.ContainsKey(b.Id)) continue;
                int shared = -1;
                foreach (var sd in EnclSeeds(b))
                {
                    int sk = Ti(sd);
                    int o = _enclOwner[sk] - 1;
                    if (o >= 0 && o != b.Id && live.Contains(o) && (BaseAny(o)?.Col ?? -1) == b.Col && _encl.ContainsKey(o)) { shared = o; break; }
                }
                if (shared >= 0)
                {
                    _encl[b.Id] = _encl[shared];
                    _enclTier[b.Id] = _enclTier.GetOr(shared, 0);
                    _enclEdge[b.Id] = _enclEdge.GetOr(shared, new int[0]);
                    _enclInner[b.Id] = _enclInner.GetOr(shared, new int[0]);
                    _enclCtr[b.Id] = _enclCtr.GetOr(shared, V.F(b.Pos));
                    _enclSects[b.Id] = _enclSects.GetOr(shared, new int[0]);
                    _enclBox[b.Id] = _enclBox.GetOr(shared, new RectInt(0, 0, 0, 0));
                    _enclRad[b.Id] = EnclRadius(b);
                    _enclLots.Remove(b.Id);
                    continue;
                }
                var r = FloodEnclosure(b, boxes.TryGetValue(b.Col, out var bx) ? bx : new RectInt(0, 0, 0, 0));
                _encl[b.Id] = r.all;
                _enclTier[b.Id] = r.tier;
                _enclEdge[b.Id] = r.edge;
                _enclInner[b.Id] = r.inner;
                _enclLots.Remove(b.Id);
                var tiles = r.all;
                double sx = 0, sy = 0;
                var sects = new List<int>();
                var sectSet = new HashSet<int>();
                int W = D.W;
                foreach (int k in tiles)
                {
                    _enclOwner[k] = b.Id + 1;
                    int x = k % W, y = k / W;
                    sx += x; sy += y;
                    int si = (y / SECT) * SW + x / SECT;
                    if (sectSet.Add(si)) sects.Add(si);
                }
                _enclCtr[b.Id] = tiles.Length > 0 ? new Vector2((float)(sx / Math.Max(1.0, tiles.Length)) + 0.5f, (float)(sy / Math.Max(1.0, tiles.Length)) + 0.5f) : V.F(b.Pos);
                _enclSects[b.Id] = sects.ToArray();
                _enclRad[b.Id] = EnclRadius(b);
            }
            EnclMs = Gd.TicksMsec() - t0;
            EnclGen++;
        }

        double EnclRadius(Base b)
        {
            double r = 0.0;
            var c = V.F(b.Pos);
            if (_enclEdge.TryGetValue(b.Id, out var e))
                foreach (int k in e) r = Math.Max(r, V.F(Tp(k)).DistTo(c));
            return r;
        }

        List<int> GateWalls()
        {
            var o = new List<int>();
            if (S.Gates.Count == 0) return o;
            foreach (var kv in S.Gates)
            {
                if (S.Fences.Has(kv.Key) || S.Towers.Has(kv.Key)) continue;
                if (SegAtTile(kv.Key) >= 0) o.Add(kv.Key);
            }
            return o;
        }

        int GateTier(int k)
        {
            if (S.Fences.TryGetValue(k, out var f)) return f.Tier;
            int se = SegAtTile(k);
            if (se >= 0 && S.Fsegs.TryGetValue(se, out var sg)) return sg.Tier;
            return 0;
        }

        Dictionary<int, RectInt> ColonyWallBoxes()
        {
            var o = new Dictionary<int, RectInt>();
            int W = D.W;
            void Add(int k, int ci)
            {
                var p = new Vector2Int(k % W, k / W);
                o[ci] = o.TryGetValue(ci, out var r) ? V.Expand(r, p) : new RectInt(p.x, p.y, 1, 1);
            }
            foreach (var kv in S.Fences) Add(kv.Key, kv.Value.Col);
            foreach (var kv in S.Towers) Add(kv.Key, kv.Value.Col);
            foreach (int k2 in GateWalls()) Add(k2, S.Gates[k2]);
            foreach (int ci in o.Keys.ToList())
                o[ci] = Gd.Grow(V.Resize(o[ci], 1, 1), ENC_BOX_PAD);
            return o;
        }

        readonly Dictionary<int, List<Vector2Int>> _seedCache = new Dictionary<int, List<Vector2Int>>();

        List<Vector2Int> EnclSeeds(Base b)
        {
            if (_seedCache.TryGetValue(b.Id, out var c)) return c;
            var o = new List<Vector2Int> { b.Pos };
            int l = b.Lot;
            if (l >= 0)
            {
                var L = D.Lots[l];
                var seen = new HashSet<Vector2Int>();
                for (int y = L.Y; y < L.Y + L.H; y++)
                    for (int x = L.X; x < L.X + L.W; x++)
                    {
                        if (D.LotId[y * D.W + x] != l) continue;
                        foreach (var d in DIRS)
                        {
                            var q = new Vector2Int(x, y) + d;
                            if (D.InMap(q) && D.Walkable(q) && !seen.Contains(q)) { seen.Add(q); o.Add(q); }
                        }
                    }
            }
            _seedCache[b.Id] = o;
            return o;
        }

        /// <summary>Flood-fills from the camp's seed tiles inside the colony's wall box. A region counts
        /// as enclosed when it never reaches the box edge. Returns tiles, min wall tier, wall tiles
        /// touched, and the inner tiles that touch a wall.</summary>
        (int[] all, int tier, int[] edge, int[] inner) FloodEnclosure(Base b, RectInt? wboxArg = null)
        {
            var r = FloodEnclosureCore(b, wboxArg, null);
            if (r.all.Length > 0 || !S.Walls.Any(w => w.Starter)) return r;
            // P2: a player wall ring that cuts across the starter palisade can fail to close; retry once with the starter posts open
            var open = new HashSet<int>();
            foreach (var w in S.Walls) if (w.Starter) foreach (int k in LineTiles(w.A, w.B)) open.Add(k);
            return FloodEnclosureCore(b, wboxArg, open);
        }

        (int[] all, int tier, int[] edge, int[] inner) FloodEnclosureCore(Base b, RectInt? wboxArg, HashSet<int> open)
        {
            var empty = (new int[0], -1, new int[0], new int[0]);
            bool scratch = !wboxArg.HasValue;
            RectInt wbox = scratch ? (ColonyWallBoxes().TryGetValue(b.Col, out var wb) ? wb : new RectInt(0, 0, 0, 0)) : wboxArg.Value;
            if (wbox.width <= 0) return empty;
            var c = b.Pos;
            var box = V.Intersection(V.Merge(wbox, new RectInt(c.x - 24, c.y - 24, 49, 49)), new RectInt(0, 0, D.W, D.H));
            if (!scratch) _enclBox[b.Id] = box;
            int x0 = box.x, y0 = box.y, bw = box.width, bh = box.height;
            if (bw < 3 || bh < 3) return empty;
            int W = D.W;
            var g = new byte[bw * bh];
            byte[] tl = D.Tiles;
            for (int yy = 0; yy < bh; yy++)
            {
                int row = (y0 + yy) * W + x0, lr = yy * bw;
                for (int xx = 0; xx < bw; xx++)
                {
                    int t = tl[row + xx];
                    if (t == Data.T_WATER || t == Data.T_BLD || t == Data.T_VOID) g[lr + xx] = 1;
                }
            }
            foreach (var kv in S.Fences)
            {
                int k = kv.Key, px = k % W - x0, py = k / W - y0;
                if (open != null && open.Contains(k)) continue;
                if (px >= 0 && py >= 0 && px < bw && py < bh) g[py * bw + px] = (byte)(2 + V.Clampi(kv.Value.Tier, 0, 4));
            }
            foreach (int k in GateWalls())
            {
                if (open != null && open.Contains(k)) continue;
                int px = k % W - x0, py = k / W - y0;
                if (px >= 0 && py >= 0 && px < bw && py < bh) g[py * bw + px] = (byte)(2 + V.Clampi(GateTier(k), 0, 4));
            }
            foreach (var kv in S.Towers)
            {
                int k = kv.Key, px = k % W - x0, py = k / W - y0;
                if (px >= 0 && py >= 0 && px < bw && py < bh) g[py * bw + px] = 7;
            }
            var all = new List<int>();
            var edge = new ODict<int, bool>();
            var inner = new List<int>();
            int minTier = 99;
            int[] offs = { 1, -1, bw, -bw };
            foreach (var sp in EnclSeeds(b))
            {
                int lx = sp.x - x0, ly = sp.y - y0;
                if (lx <= 0 || ly <= 0 || lx >= bw - 1 || ly >= bh - 1) continue;
                int li0 = ly * bw + lx;
                if (g[li0] != 0) continue;
                var q = new List<int> { li0 };
                g[li0] = 9;
                int head = 0, mt = 99;
                bool leak = false;
                var regEdge = new ODict<int, bool>();
                var regInner = new List<int>();
                while (head < q.Count)
                {
                    int li = q[head]; head++;
                    int x = li % bw, y = li / bw;
                    if (x == 0 || y == 0 || x == bw - 1 || y == bh - 1) { leak = true; continue; }
                    bool touches = false;
                    foreach (int o in offs)
                    {
                        int nj = li + o;
                        int v = g[nj];
                        if (v == 0) { g[nj] = 9; q.Add(nj); }
                        else if (v >= 2 && v <= 7)
                        {
                            touches = true;
                            if (v <= 6) mt = Math.Min(mt, v - 2);
                            if (!leak) regEdge[nj] = true;
                        }
                    }
                    if (touches && !leak) regInner.Add(li);
                }
                if (leak) continue;
                foreach (int li in q) all.Add((y0 + li / bw) * W + x0 + li % bw);
                foreach (var kv in regEdge) edge[(y0 + kv.Key / bw) * W + x0 + kv.Key % bw] = true;
                foreach (int li in regInner) inner.Add((y0 + li / bw) * W + x0 + li % bw);
                minTier = Math.Min(minTier, mt);
            }
            if (all.Count < ENC_MIN) return empty;
            return (all.ToArray(), minTier < 99 ? minTier : 0, edge.KeyList().ToArray(), inner.ToArray());
        }

        public Base EnclBaseAt(int k)
        {
            if (k < 0 || k >= _enclOwner.Length || _enclOwner[k] == 0) return null;
            return BaseAny(_enclOwner[k] - 1);
        }

        public List<(float d, int l)> EnclLots(Base b)
        {
            if (_enclLots.TryGetValue(b.Id, out var c)) return c;
            var o = new List<(float d, int l)>();
            if (IsEnclosed(b))
            {
                var cpos = V.F(b.Pos);
                var ids = new HashSet<int>();
                foreach (int k in _encl[b.Id]) ids.Add(_enclOwner[k]);
                for (int l = 0; l < D.Lots.Count; l++)
                {
                    var d = D.LotDoor(l);
                    if (!D.InMap(d)) continue;
                    int ow = _enclOwner[Ti(d)];
                    if (ow == 0 || !ids.Contains(ow)) continue;
                    o.Add((D.LotCenter(l).DistTo(cpos), l));
                }
                Gd.StableSort(o, (a, bb) => a.d.CompareTo(bb.d));
            }
            _enclLots[b.Id] = o;
            return o;
        }

        void RebuildReach()
        {
            _reachVer = _enclVer;
            if (_reach.Length != D.W * D.H) _reach = new int[D.W * D.H];
            Array.Clear(_reach, 0, _reach.Length);
            var done = new HashSet<string>();
            foreach (var b in AllBases())
            {
                if (!_encl.TryGetValue(b.Id, out var e) || e == null || e.Length == 0) continue;
                int ci = b.Col;
                string key = ci + ":" + e[0];
                if (done.Contains(key)) continue;
                done.Add(key);
                int R = (int)Math.Ceiling((BaseZoneR(b) + TERRITORY_PAD) * FENCE_REACH_MULT);
                DilateLine(b, e, R, 1 << ci);
                DilateLine(b, e, (int)Math.Ceiling(R * TRAP_REACH_MULT), 1 << (ci + TRAP_BIT));
            }
        }

        void DilateLine(Base b, int[] e, int R, int bit)
        {
            int W = D.W, H = D.H;
            var t0 = Tp(e[0]);
            var bx = new RectInt(t0.x, t0.y, 1, 1);
            var edges = _enclEdge.GetOr(b.Id, new int[0]);
            foreach (int k in edges) bx = V.Expand(bx, Tp(k));
            bx = V.Resize(bx, 1, 1);
            bx = V.Intersection(Gd.Grow(bx, R), new RectInt(0, 0, W, H));
            int bw = bx.width, bh = bx.height, x0 = bx.x, y0 = bx.y;
            var m = new int[bw * bh];
            foreach (int k in edges)
            {
                int px = k % W - x0, py = k / W - y0;
                if (px >= 0 && py >= 0 && px < bw && py < bh) m[py * bw + px] = 1;
            }
            var rowd = new byte[bw * bh];
            var pre = new int[Math.Max(bw, bh) + 1];
            for (int y = 0; y < bh; y++)
            {
                pre[0] = 0;
                int o = y * bw;
                for (int x = 0; x < bw; x++) pre[x + 1] = pre[x] + m[o + x];
                for (int x = 0; x < bw; x++)
                    if (pre[Math.Min(bw, x + R + 1)] - pre[Math.Max(0, x - R)] > 0) rowd[o + x] = 1;
            }
            for (int x = 0; x < bw; x++)
            {
                pre[0] = 0;
                for (int y = 0; y < bh; y++) pre[y + 1] = pre[y] + rowd[y * bw + x];
                for (int y = 0; y < bh; y++)
                    if (pre[Math.Min(bh, y + R + 1)] - pre[Math.Max(0, y - R)] > 0)
                    {
                        int k2 = (y0 + y) * W + x0 + x;
                        _reach[k2] |= bit;
                    }
            }
        }

        public int EnclosedCount(Base b) => _encl.TryGetValue(b.Id, out var e) && e != null ? e.Length : 0;
        public bool IsEnclosed(Base b) => EnclosedCount(b) > 0;

        public double EnclosureDefense(Base b)
        {
            if (!IsEnclosed(b)) return 0.0;
            return Data.FENCE_DEFENSE[V.Clampi(_enclTier.GetOr(b.Id, 0), 0, Data.FENCE_DEFENSE.Length - 1)] + Math.Min(12.0, EnclosedCount(b) / 40.0);
        }

        public int BaseBeds(Base b)
        {
            double n = BaseLevelDef(b).Beds + 10.0 * b.U("barracks") + Rx("housing_add");
            n += Math.Min(30.0, EnclosedCount(b) / 10.0);
            n += 8.0 * ConvN("barracks", b.Id, b.Col);
            if (PhaseARules) n += 3.0 * Math.Min(4, YardLotCount(b));   // v0.12.0a: sheds and outbuildings inside the palisade yard
            return (int)(n * (1.0 + Rx("housing_mult")));
        }

        public int BaseSlots(Base b) => BaseLevelDef(b).Slots + (int)Rx("slots_add") + Math.Min(3, EnclosedCount(b) / 120);

        public int SlotsUsed(Base b)
        {
            int n = 0;
            foreach (var kv in b.Up)
                if (kv.Value > 0 && Data.UPGRADES.TryGetValue(kv.Key, out var u) && (u.Slot ?? true)) n++;
            return n;
        }

        public double StorageCap()
        {
            double t = 0;
            foreach (var b in C.Bases) t += BaseLevelDef(b).Storage;
            t += 150.0 * ConvN("storage");
            return Math.Max(100.0, t) * (1.0 + Rx("storage_mult"));
        }

        public Dictionary<string, double> NextLevelCost(Base b)
        {
            var arr = b.Main ? Data.BASE_LEVELS["main"] : Data.BASE_LEVELS["outpost"];
            if (b.Lvl + 1 >= arr.Length) return new Dictionary<string, double>();
            return arr[b.Lvl + 1].Cost;
        }

        public string CanUpgradeBase(Base b)
        {
            var arr = b.Main ? Data.BASE_LEVELS["main"] : Data.BASE_LEVELS["outpost"];
            int l = b.Lvl;
            if (b.Done < 1.0) return "Finish building it first.";
            if (l + 1 >= arr.Length) return "Already at the top level.";
            var nx = arr[l + 1];
            if (!HasUnlock(nx.Unlock)) return string.Format("Research {0} first.", UnlockSourceName(nx.Unlock));
            if (BasePeople(b.Id).Count < (nx.MinPeople ?? 0)) return string.Format("Needs {0} people living here.", nx.MinPeople ?? 0);
            if (!CanAfford(nx.Cost)) return "Need " + FmtCost(nx.Cost) + ".";
            return "";
        }

        public string UpgradeBase(Base b)
        {
            string e = CanUpgradeBase(b);
            if (e != "") return e;
            var arr = b.Main ? Data.BASE_LEVELS["main"] : Data.BASE_LEVELS["outpost"];
            var nx = arr[b.Lvl + 1];
            Pay(nx.Cost);
            b.Lvl++;
            GrowClaim(b);
            RebuildSafe();
            _baseLots.Remove(b.Id);
            PlaySfx("hammer", V.F(b.Pos));
            AddLog(string.Format("{0} grew into a {1}: {2} beds, {3} building slots, bigger zone.", b.Name, nx.Name, BaseBeds(b), BaseSlots(b)), "major");
            EmitChanged();
            return "";
        }

        // ---------------- towers ----------------
        public string CanTower(Vector2Int p, int tier)
        {
            if (tier < 0 || tier >= Data.TOWERS.Length) return "Unknown tower.";
            if (!D.InMap(p) || !D.Walkable(p)) return "Towers go on open ground next to a fence.";
            int k = Ti(p);
            if (S.Towers.Has(k)) return "Something is already there.";
            bool onLine = false;
            if (S.Fences.Has(k))
            {
                if (FenceOwner(k) != CurCol) return "That fence isn't yours: towers must touch your own fence.";
                onLine = true;
            }
            foreach (var b in AllBases()) if (b.Pos.Same(p)) return "That's a camp gate.";
            bool touching = onLine, other = false;
            for (int dy = -1; dy < 2; dy++)
                for (int dx = -1; dx < 2; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var q = p.Add(dx, dy);
                    if (!D.InMap(q)) continue;
                    int o = FenceOwner(Ti(q));
                    if (o == CurCol) touching = true;
                    else if (o >= 0) other = true;
                }
            if (!TowerTierUnlocked(tier)) return string.Format("Research {0} first.", UnlockSourceName(Data.TOWERS[tier].Unlock));
            if (!touching) return !other ? "Towers must touch one of your own fences." : "That fence isn't yours: towers must touch your own fence.";
            return "";
        }

        public Dictionary<string, double> TowerCost(int tier)
        {
            var o = new Dictionary<string, double>();
            double disc = 1.0 - Math.Min(0.5, Rx("craft_disc"));
            foreach (var kv in Data.TOWERS[tier].Cost) o[kv.Key] = Math.Ceiling(kv.Value * disc);
            return o;
        }

        public string PlaceTower(Vector2Int p, int tier)
        {
            string e = CanTower(p, tier);
            if (e != "") return e;
            var cost = TowerCost(tier);
            if (!CanAfford(cost)) return "Need " + FmtCost(cost) + ".";
            Pay(cost);
            var b = NearestOwnBase(V.F(p));
            S.Fences.Remove(Ti(p));
            S.Gates.Remove(Ti(p));
            S.Towers[Ti(p)] = new Tower { Tier = tier, Hp = Data.TOWERS[tier].Hp, Col = CurCol, Bid = b?.Id ?? -1 };
            FencesChanged();
            PlaySfx("hammer", V.F(p));
            AddLog(string.Format("A {0} went up on the fence line.", Lower(Data.TOWERS[tier].Name)), "good");
            EmitChanged();
            return "";
        }

        public string UpgradeTower(int k)
        {
            if (!S.Towers.TryGetValue(k, out var t)) return "No tower there.";
            if (t.Col != CurCol) return string.Format("That tower belongs to {0}.", ColonyAt(t.Col)?.Name ?? "another colony");
            int nt = t.Tier + 1;
            if (nt >= Data.TOWERS.Length) return "Already the best tower.";
            if (!TowerTierUnlocked(nt)) return string.Format("Research {0} first.", Data.TOWERS[nt].Name);
            var cost = TowerCost(nt);
            if (!CanAfford(cost)) return "Need " + FmtCost(cost) + ".";
            Pay(cost);
            t.Tier = nt; t.Hp = Data.TOWERS[nt].Hp;
            PlaySfx("hammer", V.F(Tp(k)));
            AddLog(string.Format("Tower upgraded to a {0}.", Lower(Data.TOWERS[nt].Name)), "good");
            EmitChanged();
            return "";
        }

        public string RemoveTower(int k)
        {
            if (!S.Towers.Has(k)) return "No tower there.";
            if (TowerOwner(k) != CurCol) return string.Format("That tower belongs to {0}.", ColonyAt(TowerOwner(k))?.Name ?? "another colony");
            S.Towers.Remove(k);
            int se = SegAtTile(k);
            if (se >= 0 && S.Fsegs.TryGetValue(se, out var sg) && sg.Col == CurCol)
                S.Fences[k] = new Fence { Tier = sg.Tier, Hp = FenceMaxHp(sg.Tier), Col = CurCol };
            FencesChanged();
            EmitChanged();
            return "";
        }

        public Vector2Int TowerSpotNear(Vector2Int t, int tier, int r = 2)
        {
            if (CanTower(t, tier) == "") return t;
            var best = V.NONE; double bd = 99.0;
            for (int dy = -r; dy < r + 1; dy++)
                for (int dx = -r; dx < r + 1; dx++)
                {
                    var q = t.Add(dx, dy);
                    double d = V.Len(dx, dy);
                    if (d < bd && d <= r + 0.01 && CanTower(q, tier) == "") { bd = d; best = q; }
                }
            return best;
        }

        public List<int> TowersOfBase(int bid) => S.Towers.Where(kv => kv.Value.Bid == bid).Select(kv => kv.Key).ToList();

        Base NearestOwnBase(Vector2 p)
        {
            Base best = null; double bd = 1000000000.0;
            foreach (var b in C.Bases)
            {
                double d = V.F(b.Pos).DistTo(p);
                if (d < bd) { bd = d; best = b; }
            }
            return best;
        }

        void TowerCombat()
        {
            double power = Rx("power");
            var crewUsed = new Dictionary<int, int>();
            foreach (int k in S.Towers.KeyList())
            {
                if (!S.Towers.TryGetValue(k, out var t)) continue;
                if (t.Col != CurCol) continue;
                var td = Data.TOWERS[t.Tier];
                var p = V.F(Tp(k)).Add(V.HALF);
                if (td.Crew)
                {
                    var b = BaseById(t.Bid);
                    if (b == null) { b = NearestOwnBase(p); t.Bid = b?.Id ?? -1; }
                    if (b == null) continue;
                    int used = crewUsed.GetOr(b.Id, 0);
                    if (AtBase(b.Id).Count <= used) continue;
                    crewUsed[b.Id] = used + 1;
                }
                if ((td.Power ?? 0) > 0)
                {
                    if (power < td.Power.Value) continue;
                    power -= td.Power.Value;
                }
                var near = ZombiesNear(p, td.Range + Rx("tower_range"), 8);
                if (near.Count == 0) continue;
                double ammo = td.Ammo;
                if (ammo > 0.0)
                {
                    if (R("ammo") < ammo) continue;
                    C.Res["ammo"] = R("ammo") - ammo;
                }
                PlaySfx(t.Tier >= 1 ? "rifle" : "hit", p);
                for (int i = 0; i < Math.Min(td.Shots, near.Count); i++)
                    if (Rng.Randf() < td.Hit + Rx("tower_dmg")) KillZ(near[i]);
            }
        }

        void HitBlocker(int k, double dmg)
        {
            RaidDmg(k, dmg);
            if (S.Fences.TryGetValue(k, out var f))
            {
                f.Hp -= dmg;
                if (f.Hp <= 0.0)
                {
                    int oc = f.Col;
                    RecordBreach(k, f, IsGate(k));
                    S.Fences.Remove(k);
                    int lc = LogCol;
                    LogCol = oc;
                    AddLog(string.Format("Zombies broke through the {0} on {1}!", IsGate(k) ? "gate" : "fence", StreetName(Tp(k))), "loss");
                    LogCol = lc;
                    RaidBreak(oc);
                    PlaySfx("alarm", V.F(Tp(k)));
                    FencesChanged(Tp(k));
                    EmitChanged();
                }
            }
            else if (S.Towers.TryGetValue(k, out var t))
            {
                t.Hp -= dmg;
                if (t.Hp <= 0.0)
                {
                    int oc2 = t.Col;
                    S.Towers.Remove(k);
                    int lc2 = LogCol;
                    LogCol = oc2;
                    AddLog(string.Format("A tower on {0} was torn down by the dead.", StreetName(Tp(k))), "loss");
                    LogCol = lc2;
                    FencesChanged(Tp(k));
                    EmitChanged();
                }
            }
        }

        // ---------------- claimed land (fenced pockets) ----------------
        byte[] _claim = new byte[0];
        readonly Dictionary<int, List<int>> _claimTiles = new Dictionary<int, List<int>>();
        readonly ODict<int, int> _claimLots = new ODict<int, int>();
        int _claimVer = -1;
        public double ClaimMs;
        long _claimSig;

        public int ClaimAt(int k) => (k < 0 || k >= _claim.Length) ? -1 : _claim[k] - 1;
        public int ClaimLot(int l) => _claimLots.Get(l, -1);
        public List<int> ClaimedLots(int ci) => _claimLots.Where(kv => kv.Value == ci).Select(kv => kv.Key).ToList();
        public int ClaimedCount(int ci) => _claimTiles.TryGetValue(ci, out var t) ? t.Count : 0;

        void RebuildClaims()
        {
            long t0 = Gd.TicksMsec();
            _claimVer = S.FenceVer;
            int W = D.W, H = D.H;
            var gw = GateWalls();
            long sig = 17;
            unchecked
            {
                foreach (var kv in S.Fences) sig = sig * 31 + kv.Key * 3 + kv.Value.Col;
                sig = sig * 31 + 7;
                foreach (var kv in S.Towers) sig = sig * 31 + kv.Key * 5 + kv.Value.Col;
                sig = sig * 31 + 11;
                foreach (int k in gw) sig = sig * 31 + k;
            }
            if (sig == _claimSig && _claim.Length == W * H) return;
            _claimSig = sig;
            if (_claim.Length != W * H) _claim = new byte[W * H];
            Array.Clear(_claim, 0, _claim.Length);
            _claimTiles.Clear(); _claimLots.Clear();
            var boxes = ColonyWallBoxes();
            byte[] tl = D.Tiles;
            foreach (var bkv in boxes)
            {
                int ci = bkv.Key;
                var box = V.Intersection(bkv.Value, new RectInt(0, 0, W, H));
                int bw = box.width, bh = box.height;
                if (bw < 3 || bh < 3) continue;
                int x0 = box.x, y0 = box.y;
                var g = new byte[bw * bh];
                for (int yy = 0; yy < bh; yy++)
                {
                    int row = (y0 + yy) * W + x0, lr = yy * bw;
                    for (int xx = 0; xx < bw; xx++)
                    {
                        int t = tl[row + xx];
                        if (t == Data.T_WATER || t == Data.T_BLD || t == Data.T_VOID) g[lr + xx] = 1;
                    }
                }
                void Wall(int k, int owner)
                {
                    int px = k % W - x0, py = k / W - y0;
                    if (px >= 0 && py >= 0 && px < bw && py < bh) g[py * bw + px] = (byte)(owner == ci ? 2 : 3);
                }
                foreach (var kv in S.Fences) Wall(kv.Key, kv.Value.Col);
                foreach (var kv in S.Towers) Wall(kv.Key, kv.Value.Col);
                foreach (int k in gw) Wall(k, S.Gates[k]);
                var q = new List<int>();
                for (int xx = 0; xx < bw; xx++)
                    foreach (int yy in new[] { 0, bh - 1 })
                    {
                        int li = yy * bw + xx;
                        if (g[li] == 0) { g[li] = 9; q.Add(li); }
                    }
                for (int yy = 1; yy < bh - 1; yy++)
                    foreach (int xx in new[] { 0, bw - 1 })
                    {
                        int li2 = yy * bw + xx;
                        if (g[li2] == 0) { g[li2] = 9; q.Add(li2); }
                    }
                int head = 0;
                while (head < q.Count)
                {
                    int li3 = q[head]; head++;
                    int x = li3 % bw, y = li3 / bw;
                    if (x > 0 && g[li3 - 1] == 0) { g[li3 - 1] = 9; q.Add(li3 - 1); }
                    if (x < bw - 1 && g[li3 + 1] == 0) { g[li3 + 1] = 9; q.Add(li3 + 1); }
                    if (y > 0 && g[li3 - bw] == 0) { g[li3 - bw] = 9; q.Add(li3 - bw); }
                    if (y < bh - 1 && g[li3 + bw] == 0) { g[li3 + bw] = 9; q.Add(li3 + bw); }
                }
                var tiles = new List<int>();
                for (int start = 0; start < bw * bh; start++)
                {
                    if (g[start] != 0) continue;
                    var reg = new List<int> { start };
                    g[start] = 8;
                    int h2 = 0;
                    bool own = false;
                    while (h2 < reg.Count)
                    {
                        int li4 = reg[h2]; h2++;
                        foreach (int nj in new[] { li4 - 1, li4 + 1, li4 - bw, li4 + bw })
                        {
                            int v = g[nj];
                            if (v == 0) { g[nj] = 8; reg.Add(nj); }
                            else if (v == 2) own = true;
                        }
                    }
                    if (!own || reg.Count < ENC_MIN) continue;
                    foreach (int li5 in reg)
                    {
                        int k2 = (y0 + li5 / bw) * W + x0 + li5 % bw;
                        if (_claim[k2] == 0) { _claim[k2] = (byte)(ci + 1); tiles.Add(k2); }
                    }
                }
                if (tiles.Count > 0) _claimTiles[ci] = tiles;
            }
            if (_claimTiles.Count > 0)
            {
                var cb = new RectInt(0, 0, 0, 0);
                foreach (var kv in _claimTiles)
                    foreach (int k3 in kv.Value)
                    {
                        var pp = new Vector2Int(k3 % W, k3 / W);
                        cb = cb.width == 0 ? new RectInt(pp.x, pp.y, 1, 1) : V.Expand(cb, pp);
                    }
                cb = Gd.Grow(cb, 4);
                for (int l = 0; l < D.Lots.Count; l++)
                {
                    var dr = D.LotDoor(l);
                    if (!Gd.Has(cb, dr) || !D.InMap(dr)) continue;
                    int o2 = _claim[dr.y * W + dr.x];
                    if (o2 == 0)
                    {
                        var nw = D.NearestWalkable(dr, 2);
                        if (nw.x >= 0) o2 = _claim[nw.y * W + nw.x];
                    }
                    if (o2 > 0) _claimLots[l] = o2 - 1;
                }
            }
            ClaimMs = Gd.TicksMsec() - t0;
        }

        // ---------------- gates ----------------
        public bool IsGate(int k) => S.Gates.Has(k);

        public double FenceTileMax(int k)
        {
            if (!S.Fences.TryGetValue(k, out var f)) return 1.0;
            return FenceMaxHp(f.Tier) * (IsGate(k) ? Data.GATE_HP_MULT : 1.0);
        }

        public bool OnLineOf(Base b, int k)
        {
            if (V.F(Tp(k)).DistTo(V.F(b.Pos)) <= ENC_R) return true;
            if (!_enclEdge.TryGetValue(b.Id, out var e) || e == null || e.Length == 0) return false;
            return EdgeSet(b).Contains(k);
        }

        readonly Dictionary<string, HashSet<int>> _edgeSets = new Dictionary<string, HashSet<int>>();
        HashSet<int> EdgeSet(Base b)
        {
            string key = b.Id + ":" + _enclVer;
            if (_edgeSets.TryGetValue(key, out var hs)) return hs;
            if (_edgeSets.Count > 16) _edgeSets.Clear();
            var d = new HashSet<int>(_enclEdge.GetOr(b.Id, new int[0]));
            _edgeSets[key] = d;
            return d;
        }

        Base LineBase(int k)
        {
            foreach (var b in C.Bases)
                if (IsEnclosed(b) && EdgeSet(b).Contains(k)) return b;
            var b2 = NearestOwnBase(V.F(Tp(k)));
            if (b2 == null || V.F(b2.Pos).DistTo(V.F(Tp(k))) > ENC_R) return null;
            return b2;
        }

        public int GatesNear(Base b)
        {
            int n = 0, ci = b.Col;
            foreach (var kv in S.Gates)
                if (kv.Value == ci && S.Fences.Has(kv.Key) && OnLineOf(b, kv.Key)) n++;
            return n;
        }

        public string CanGate(int k)
        {
            if (!S.Fences.Has(k)) return "Gates go in your fence line.";
            if (FenceOwner(k) != CurCol) return "That fence isn't yours.";
            if (IsGate(k)) return "That's already a gate.";
            var b = LineBase(k);
            if (b == null) return "Too far from your camps.";
            if (GatesNear(b) >= 4) return "Four gates per camp at most.";
            return "";
        }

        public string PlaceGate(int k)
        {
            string e = CanGate(k);
            if (e != "") return e;
            if (!CanAfford(Data.GATE_COST)) return "Need " + FmtCost(Data.GATE_COST) + ".";
            Pay(Data.GATE_COST);
            S.Gates[k] = CurCol;
            S.Fences[k].Hp = FenceTileMax(k);
            FencesChanged(Tp(k));
            PlaySfx("hammer", V.F(Tp(k)));
            AddLog(string.Format("A reinforced gate went into the fence on {0}.", StreetName(Tp(k))), "good");
            EmitChanged();
            return "";
        }

        public string RemoveGate(int k)
        {
            if (!IsGate(k)) return "No gate there.";
            if (S.Gates[k] != CurCol) return "That gate isn't yours.";
            S.Gates.Remove(k);
            if (S.Fences.TryGetValue(k, out var f)) f.Hp = Math.Min(f.Hp, FenceMaxHp(f.Tier));
            FencesChanged(Tp(k));
            EmitChanged();
            return "";
        }

        void StepDefenseGlobal()
        {
            if (Hour() == RAID_END_H + 1)
            {
                bool any = false;
                foreach (var c in S.Cols) if (c.Nraid.On && ColAlive(c)) any = true;
                if (!any)
                    for (int i = 0; i < Zh.Count; i++) if (Zh[i] == 3) Zh[i] = 0;
            }
            if (S.Gates.Count == 0) return;
            foreach (int k in S.Gates.KeyList())
                if (!S.Fences.Has(k) && SegAtTile(k) < 0) S.Gates.Remove(k);
        }

        // ---------------- traps ----------------
        public bool TrapUnlocked(int kind)
        {
            string u = Data.TRAPS[kind].Unlock ?? "";
            return u == "" || HasUnlock(u);
        }

        public Dictionary<string, double> TrapCost(int kind)
        {
            var o = new Dictionary<string, double>();
            double disc = 1.0 - Math.Min(0.5, Rx("craft_disc"));
            foreach (var kv in Data.TRAPS[kind].Cost) o[kv.Key] = Math.Ceiling(kv.Value * disc);
            return o;
        }

        public static int TrapLvl(Trap t) => V.Clampi(t.Lvl, 0, Data.TRAP_LEVELS - 1);
        public static double TrapHpLvl(int kind, int lvl) => Gd.Round(Data.TRAPS[kind].Hp * (1.0 + 0.5 * lvl));
        public static double TrapHpMax(Trap t) => TrapHpLvl(t.Kind, TrapLvl(t));
        public static double TrapKill(int kind, int lvl)
        {
            double k = Data.TRAPS[kind].Kill;
            return k > 0.0 ? Math.Min(0.95, k + 0.1 * lvl) : 0.0;
        }
        public static double TrapSlow(int kind, int lvl) => Data.TRAPS[kind].Slow * (1.0 + 0.3 * lvl);
        public static double TrapRadius(int kind, int lvl)
        {
            var td = Data.TRAPS[kind];
            switch (td.Fx ?? "")
            {
                case "lure": return (td.R ?? 0) + 2.0 * lvl;
                case "fire": return (td.R ?? 0) + 0.5 * lvl;
                case "alarm": return (td.R ?? 0) + 1.0 * lvl;
            }
            return 0.0;
        }

        public Dictionary<string, double> TrapUpgradeCost(int kind, int lvl)
        {
            var o = new Dictionary<string, double>();
            foreach (var kv in TrapCost(kind)) o[kv.Key] = kv.Value * (lvl + 1);
            return o;
        }

        public string CanUpgradeTrap(int k)
        {
            if (!S.Traps.TryGetValue(k, out var t)) return "No trap there.";
            if (t.Col != CurCol) return "That trap isn't yours.";
            int lv = TrapLvl(t);
            if (lv >= Data.TRAP_LEVELS - 1) return string.Format("Already at Level {0}.", Data.TRAP_LEVELS);
            if (lv + 1 >= 2 && !HasUnlock(Data.TRAP_L3_UNLOCK)) return string.Format("Level 3 needs {0}.", UnlockSourceName(Data.TRAP_L3_UNLOCK));
            var cost = TrapUpgradeCost(t.Kind, lv);
            if (!CanAfford(cost)) return "Need " + FmtCost(cost) + ".";
            return "";
        }

        public string UpgradeTrap(int k)
        {
            string e = CanUpgradeTrap(k);
            if (e != "") return e;
            var t = S.Traps[k];
            int lv = TrapLvl(t) + 1;
            Pay(TrapUpgradeCost(t.Kind, lv - 1));
            t.Lvl = lv;
            t.Hp = TrapHpMax(t);
            _lureDirty = true; RefreshRefs();
            PlaySfx("hammer", V.F(Tp(k)));
            AddLog(string.Format("Upgraded the {0} on {1} to Level {2}.", Lower(Data.TRAPS[t.Kind].Name), StreetName(Tp(k)), lv + 1), "info");
            EmitChanged();
            return "";
        }

        public int TrapsOf(int ci = -1)
        {
            if (ci < 0) ci = CurCol;
            return S.Traps.Count(kv => kv.Value.Col == ci);
        }

        public double FenceReachR(Base b) => (BaseZoneR(b) + TERRITORY_PAD) * FENCE_REACH_MULT;
        public double TrapReachR(Base b) => FenceReachR(b) * TRAP_REACH_MULT;

        public bool InTrapRange(Vector2Int p, int ci)
        {
            if (!D.InMap(p)) return false;
            if (InTerritory(p, ci)) return true;
            var c = ColonyAt(ci);
            if (c == null) return false;
            foreach (var b in c.Bases)
                if (V.F(b.Pos).DistTo(V.F(p)) <= TrapReachR(b)) return true;
            int k = Ti(p);
            return k < _reach.Length && (_reach[k] & (1 << (ci + TRAP_BIT))) != 0;
        }

        public string CanTrap(Vector2Int p, int kind)
        {
            if (kind < 0 || kind >= Data.TRAPS.Length) return "Unknown trap.";
            if (!TrapUnlocked(kind)) return string.Format("Research {0} first.", UnlockSourceName(Data.TRAPS[kind].Unlock));
            if (!D.InMap(p) || !D.Walkable(p)) return "Traps go on streets and open ground.";
            int k = Ti(p);
            if (S.Fences.Has(k) || S.Towers.Has(k)) return "A fence or tower stands there.";
            if (S.Traps.Has(k)) return "There's already a trap there.";
            foreach (var b in AllBases()) if (b.Pos.Same(p)) return "That's a camp gate. Your own people walk there.";
            if (!InTrapRange(p, CurCol)) return "Outside your territory (traps reach twice as far as fences).";
            if (TrapsOf() >= Data.TRAP_MAX) return string.Format("{0} traps at most.", Data.TRAP_MAX);
            return "";
        }

        public BuildPreview TrapPreview(List<Vector2Int> tiles, int kind)
        {
            var pv = new BuildPreview();
            var seen = new HashSet<int>();
            int room = Data.TRAP_MAX - TrapsOf();
            foreach (var p in tiles)
            {
                string e = D.InMap(p) ? CanTrap(p, kind) : "Off the map.";
                if (e == "" && seen.Contains(Ti(p))) e = "Already in this line.";
                if (e == "" && pv.N >= room) e = string.Format("{0} traps at most.", Data.TRAP_MAX);
                if (e == "") { pv.N++; seen.Add(Ti(p)); }
                else if (pv.Err == "") pv.Err = e;
                pv.Ok.Add(e == "");
            }
            if (kind >= 0 && kind < Data.TRAPS.Length)
                foreach (var kv in TrapCost(kind)) pv.Cost[kv.Key] = kv.Value * pv.N;
            return pv;
        }

        public string PlaceTraps(List<Vector2Int> tiles, int kind)
        {
            if (tiles.Count == 0) return "Mark a trap line first.";
            if (kind < 0 || kind >= Data.TRAPS.Length) return "Unknown trap.";
            if (!TrapUnlocked(kind)) return string.Format("Research {0} first.", UnlockSourceName(Data.TRAPS[kind].Unlock));
            var pv = TrapPreview(tiles, kind);
            if (pv.N == 0) return pv.Err != "" ? pv.Err : "Nothing to place.";
            if (!CanAfford(pv.Cost)) return "Need " + FmtCost(pv.Cost) + ".";
            Pay(pv.Cost);
            var first = V.NONE;
            for (int i = 0; i < tiles.Count; i++)
            {
                if (!pv.Ok[i]) continue;
                var p = tiles[i];
                if (first.x < 0) first = p;
                S.Traps[Ti(p)] = new Trap { Kind = kind, Hp = Data.TRAPS[kind].Hp, Col = CurCol, Lvl = 0 };
            }
            _lureDirty = true; RefreshRefs();
            PlaySfx("hammer", V.F(first));
            string nm = Lower(Data.TRAPS[kind].Name);
            AddLog(pv.N > 1 ? string.Format("Set {0} {1}{2} along {3}.", pv.N, nm, pv.N == 1 ? "" : "s", StreetName(first)) : string.Format("Set a {0} on {1}.", nm, StreetName(first)), "info");
            EmitChanged();
            return "";
        }

        public string PlaceTrap(Vector2Int p, int kind)
        {
            string e = CanTrap(p, kind);
            if (e != "") return e;
            var cost = TrapCost(kind);
            if (!CanAfford(cost)) return "Need " + FmtCost(cost) + ".";
            Pay(cost);
            S.Traps[Ti(p)] = new Trap { Kind = kind, Hp = Data.TRAPS[kind].Hp, Col = CurCol, Lvl = 0 };
            _lureDirty = true; RefreshRefs();
            PlaySfx("hammer", V.F(p));
            AddLog(string.Format("Set a {0} on {1}.", Lower(Data.TRAPS[kind].Name), StreetName(p)), "info");
            EmitChanged();
            return "";
        }

        public string RemoveTrap(int k)
        {
            if (!S.Traps.TryGetValue(k, out var t)) return "No trap there.";
            if (t.Col != CurCol) return "That trap isn't yours.";
            var td = Data.TRAPS[t.Kind];
            double frac = t.Hp / TrapHpMax(t);
            int lv = TrapLvl(t);
            foreach (var kv in td.Cost) C.Res[kv.Key] = R(kv.Key) + Math.Floor(kv.Value * (1.0 + 0.5 * lv * (lv + 1)) * 0.5 * frac);
            S.Traps.Remove(k);
            _lureDirty = true; RefreshRefs();
            EmitChanged();
            return "";
        }

        public int TrapHits;
        readonly Dictionary<int, double> _alarmT = new Dictionary<int, double>();
        readonly List<(Vector2 pos, double r)> _lures = new List<(Vector2, double)>();
        bool _lureDirty = true;
        object _lureSrc;

        void TrapHit(int i, int k)
        {
            var t = _TR[k];
            if (t.Hp <= 0.0) return;
            var td = Data.TRAPS[t.Kind];
            int kind = t.Kind, lv = TrapLvl(t);
            t.Hp = Math.Max(0.0, t.Hp - (td.Wear ?? 1.0));
            double sl = TrapSlow(kind, lv);
            if (sl > 0.0) Zp[i] = (float)-sl;
            int oc = t.Col;
            string fx = td.Fx ?? "";
            TrapHits++;
            if (Rng.Randf() < TrapKill(kind, lv))
            {
                TrapKillZ(i, oc);
                PlaySfx("hit", V.F(Tp(k)));
            }
            if (fx == "fire")
            {
                foreach (int j in ZombiesNear(V.F(Tp(k)).Add(V.HALF), TrapRadius(kind, lv), 12))
                    if (j != i && Zh[j] != 255 && Rng.Randf() < 0.45 + 0.1 * lv) TrapKillZ(j, oc);
            }
            else if (fx == "alarm")
            {
                foreach (int j in ZombiesNear(V.F(Tp(k)).Add(V.HALF), TrapRadius(kind, lv), 8))
                    if (j != i && Zh[j] != 255 && Rng.Randf() < 0.15 + 0.1 * lv) TrapKillZ(j, oc);
                if (S.T - _alarmT.GetOr(k, -99.0) >= 1.0)
                {
                    _alarmT[k] = S.T;
                    PlaySfx("alarm", V.F(Tp(k)));
                    int lc0 = LogCol;
                    LogCol = oc;
                    AddLog(string.Format("Bells on the tripwire at {0}: the dead are coming in that way.", StreetName(Tp(k))), "info");
                    LogCol = lc0;
                }
            }
            if (t.Hp <= 0.0)
            {
                int lc = LogCol;
                LogCol = oc;
                if (fx == "lure" || fx == "alarm" || fx == "fire" || td.Id == "deadfall")
                    AddLog(string.Format("The {0} on {1} is spent. It needs resetting.", Lower(td.Name), StreetName(Tp(k))), "loss");
                else AddLog(string.Format("The {0} on {1} is clogged with bodies. It needs clearing out.", Lower(td.Name), StreetName(Tp(k))), "loss");
                LogCol = lc;
                if (fx == "lure") { _lureDirty = true; RefreshRefs(); }
            }
        }

        void TrapKillZ(int i, int oc)
        {
            KillZ(i, false);
            ColonyAt(oc)?.AddStat("kills", 1);
        }

        int LureStep(int i, Vector2Int c, int mode, int cs)
        {
            var cp = V.F(c).Add(V.HALF);
            var best = new Vector2(-1, -1); double bd = 1000000000.0;
            foreach (var L in _lures)
            {
                double d = L.pos.Dist2To(cp);
                if (d < L.r * L.r && d < bd && d > 0.3) { bd = d; best = L.pos; }
            }
            if (best.x < 0f) return -1;
            int bk = -1; double bdd = bd;
            foreach (var d in DIRS)
            {
                var q = c + d;
                if (!D.InMap(q)) continue;
                int k = Ti(q);
                if (!ZAllowed(k, mode, cs) || Blocked(k)) continue;
                double dd = V.F(q).Add(V.HALF).Dist2To(best);
                if (dd < bdd) { bdd = dd; bk = k; }
            }
            return bk;
        }

        void RepairTrap(Person p, int k)
        {
            var t = S.Traps[k];
            var td = Data.TRAPS[t.Kind];
            double miss = 1.0 - t.Hp / TrapHpMax(t);
            double cost = MatSum(td.Cost) * miss * RepairCostMult();
            if (PayMatsFlex(cost, td.Cost))
            {
                t.Hp = TrapHpMax(t);
                PlaySfx("hammer", p.Pos);
                if ((td.Fx ?? "") == "lure") { _lureDirty = true; RefreshRefs(); }
            }
        }

        /// <summary>Damaged fences/towers/traps of the current colony, most urgent first:
        /// (priority, "fence"|"gate"|"tower"|"trap", tile key, hp fraction).</summary>
        public List<(double pri, string kind, int k, double frac)> RepairQueue(Base b = null)
        {
            var o = new List<(double, string, int, double)>();
            var R0 = C.Nraid;
            var hot = new Vector2(-9999, -9999);
            if (R0.On || R0.Warned)
            {
                var tb = BaseById(R0.Bid);
                if (tb != null) hot = V.F(tb.Pos);
            }
            var near = b != null ? V.F(b.Pos) : new Vector2(-1, -1);
            foreach (var kv in S.Fences)
            {
                var f = kv.Value;
                if (f.Col != CurCol) continue;
                bool g = IsGate(kv.Key);
                double frac = f.Hp / FenceTileMax(kv.Key);
                if (frac >= (g ? 0.9 : 0.75)) continue;
                var p = V.F(Tp(kv.Key));
                if (near.x >= 0f && p.DistTo(near) > 40f) continue;
                o.Add(((1.0 - frac) * (g ? 3.0 : 1.5) + (p.DistTo(hot) < 40f ? 1.0 : 0.0), g ? "gate" : "fence", kv.Key, frac));
            }
            foreach (var kv in S.Breaches)
            {
                var br = kv.Value;
                if (br.Col != CurCol || S.Fences.Has(kv.Key)) continue;
                var pb = V.F(Tp(kv.Key));
                if (near.x >= 0f && pb.DistTo(near) > 40f) continue;
                o.Add((4.0 + (br.Gate ? 1.0 : 0.0) + (pb.DistTo(hot) < 40f ? 1.0 : 0.0), "breach", kv.Key, 0.0));
            }
            foreach (var kv in S.Towers)
            {
                var t = kv.Value;
                if (t.Col != CurCol) continue;
                double frac2 = t.Hp / Data.TOWERS[t.Tier].Hp;
                if (frac2 >= 0.8) continue;
                var p2 = V.F(Tp(kv.Key));
                if (near.x >= 0f && p2.DistTo(near) > 40f) continue;
                o.Add(((1.0 - frac2) * 2.2 + (p2.DistTo(hot) < 40f ? 1.0 : 0.0), "tower", kv.Key, frac2));
            }
            foreach (var kv in S.Traps)
            {
                var tr = kv.Value;
                if (tr.Col != CurCol) continue;
                double frac3 = tr.Hp / TrapHpMax(tr);
                if (frac3 >= 0.5) continue;
                var p3 = V.F(Tp(kv.Key));
                if (near.x >= 0f && p3.DistTo(near) > 40f) continue;
                o.Add(((1.0 - frac3) * 1.0, "trap", kv.Key, frac3));
            }
            Gd.StableSort(o, (x, y) => y.Item1.CompareTo(x.Item1));
            return o;
        }

        public double RepairCost((double pri, string kind, int k, double frac) it)
        {
            int k = it.k;
            double m = RepairCostMult() / (1.0 + Rx("repair_mult"));
            switch (it.kind)
            {
                case "fence":
                case "gate":
                    if (!S.Fences.TryGetValue(k, out var f)) return 0.0;
                    return (FenceTileMax(k) - f.Hp) / 30.0 * m;
                case "tower":
                    if (!S.Towers.TryGetValue(k, out var t)) return 0.0;
                    return (Data.TOWERS[t.Tier].Hp - t.Hp) / 30.0 * m;
                case "trap":
                    if (!S.Traps.TryGetValue(k, out var tr)) return 0.0;
                    return MatSum(Data.TRAPS[tr.Kind].Cost) * (1.0 - tr.Hp / TrapHpMax(tr)) * RepairCostMult();
                case "breach":
                    if (!S.Breaches.TryGetValue(k, out var br)) return 0.0;
                    return BreachCostMats(br);
            }
            return 0.0;
        }

        bool RepairUrgent(Base b)
        {
            if (PhaseARules && !C.Auto.Repair) return false;
            var q = RepairQueue(b);
            return q.Count > 0 && q[0].frac < 0.5 && MatsTotal() >= 2.0;
        }
    }
}
