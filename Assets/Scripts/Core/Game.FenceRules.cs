// v0.19.2 (Austin's video 21:37: "Fix the gaps in the fencing and gate placement, and if I add on to my fence line
// expanding it, expand my territory color. Same for AI settlements."):
//  * Gate placement (EXPANDED rules): a gate goes in a straight stretch of a fence run, never at a corner / T-junction,
//    never where the run steps diagonally across a tile corner, never against another gate, and only where people can
//    actually walk straight through (open ground on both sides). GateSpotError says why a tile won't take a gate;
//    SnapGate finds the nearest tile that will (the GATE tool and "make it a gate" snap to it).
//  * Territory owner per tile for the overlay: the camp enclosures (RebuildEnclosures) merged with the colony's claimed
//    land (RebuildClaims: every pocket closed by its own fence, so extending the fence line outward claims the new
//    pocket and tearing it down gives it back). Both are rebuilt only when the fences change (S.FenceVer); ClaimGen
//    tells the overlay when to redraw. Classic rules keep the original gate check; the claims were already sim state.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace DayZero.Core
{
    public sealed partial class Game
    {
        /// <summary>Bumped every time the claimed-land grid is recomputed with a different fence layout.</summary>
        public int ClaimGen;

        /// <summary>Colony whose territory tile k is (camp enclosure or fenced pocket it claimed), or -1.</summary>
        public int TerritoryOwnerAt(int k)
        {
            var b = EnclBaseAt(k);
            if (b != null) return b.Col;
            return ClaimAt(k);
        }

        /// <summary>Tiles of colony ci's territory (enclosures + claims, each tile once).</summary>
        public int TerritoryTiles(int ci)
        {
            int n = 0, N = D.W * D.H;
            for (int k = 0; k < N; k++) if (TerritoryOwnerAt(k) == ci) n++;
            return n;
        }

        // ---------------- diagonal steps ----------------
        /// <summary>EXPANDED: the tiles a section's line was sampled through (Data.RgEdgeTiles) can join only by a corner where the
        /// line steps diagonally. Movement is 4-way so nothing walked through, but the drawn fence showed a hole there and the
        /// line was not a closed wall for anything that moves diagonally. Each such step gets the corner tile the line passes
        /// closest to, so every fence section is a 4-connected wall. (Data.RgEdgeTiles itself also builds the road graph,
        /// so it stays as it was.) Older fences get the extra tiles from PatchGaps like any other hole in a section.</summary>
        List<int> FillDiagonalSteps(int e, List<int> t)
        {
            if (t.Count < 2) return t;
            int W = D.W;
            var a = D.RgPos(D.RgEa[e]); var b = D.RgPos(D.RgEb[e]);
            var o = new List<int>(t.Count + 8);
            o.Add(t[0]);
            for (int i = 1; i < t.Count; i++)
            {
                int k0 = t[i - 1], k1 = t[i];
                int x0 = k0 % W, y0 = k0 / W, x1 = k1 % W, y1 = k1 / W;
                if (x0 != x1 && y0 != y1)
                {
                    var c1 = new Vector2(x1 + 0.5f, y0 + 0.5f); var c2 = new Vector2(x0 + 0.5f, y1 + 0.5f);
                    int kc = DistToSeg(c1, a, b) <= DistToSeg(c2, a, b) ? y0 * W + x1 : y1 * W + x0;
                    if (!o.Contains(kc)) o.Add(kc);
                }
                if (!o.Contains(k1)) o.Add(k1);
            }
            return o;
        }

        // ---------------- fence segments per tile ----------------
        readonly Dictionary<int, List<int>> _tileSegs = new Dictionary<int, List<int>>();
        int _tileSegsVer = -1;

        /// <summary>Every fenced road-graph section whose line crosses tile k (a corner tile is on two or more).</summary>
        public List<int> SegsAtTile(int k)
        {
            if (_tileSegsVer != S.FenceVer || !ReferenceEquals(_tileSegsSrc, S))
            {
                _tileSegsVer = S.FenceVer; _tileSegsSrc = S;
                _tileSegs.Clear();
                foreach (var kv in S.Fsegs)
                    foreach (int t in EdgeTiles(kv.Key))
                    {
                        if (!_tileSegs.TryGetValue(t, out var l)) _tileSegs[t] = l = new List<int>(2);
                        if (!l.Contains(kv.Key)) l.Add(kv.Key);
                    }
            }
            return _tileSegs.TryGetValue(k, out var o) ? o : _noSegs;
        }
        GameState _tileSegsSrc;
        static readonly List<int> _noSegs = new List<int>();

        bool OpenGround(int x, int y)
        {
            var q = new Vector2Int(x, y);
            if (!D.InMap(q) || !D.Walkable(q)) return false;
            int k = Ti(q);
            return !S.Fences.Has(k) && !S.Towers.Has(k);
        }

        bool WallLike(int x, int y)
        {
            var q = new Vector2Int(x, y);
            if (!D.InMap(q)) return true;
            if (!D.Walkable(q)) return true;
            int k = Ti(q);
            return S.Fences.Has(k) || S.Towers.Has(k);
        }

        /// <summary>Why tile k can't take a gate under the EXPANDED placement rules ("" = a good spot).</summary>
        public string GateSpotError(int k)
        {
            var p = Tp(k);
            if (!D.InMap(p) || !D.Walkable(p)) return "Gates go on the street or open ground.";
            foreach (var b in AllBases()) if (b.Pos.Same(p)) return "That's a camp door.";
            // people walk straight through: open ground on two opposite sides
            bool ns = OpenGround(p.x, p.y - 1) && OpenGround(p.x, p.y + 1);
            bool ew = OpenGround(p.x - 1, p.y) && OpenGround(p.x + 1, p.y);
            if (!ns && !ew) return "No way through here: put the gate where the fence runs straight.";
            // no gate next to a gate
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var q = new Vector2Int(p.x + dx, p.y + dy);
                    if (D.InMap(q) && S.Gates.Has(Ti(q)) && S.Fences.Has(Ti(q))) return "Right next to another gate.";
                }
            var segs = SegsAtTile(k);
            var c = V.F(p).Add(V.HALF);
            if (segs.Count > 0)
            {
                // corner / T-junction: the tile is on two sections that don't run on in a straight line, or holds a post
                // where sections turn
                Vector2 d0 = Vector2.zero; bool has0 = false;
                foreach (int e in segs)
                {
                    var a = D.RgPos(D.RgEa[e]); var bb = D.RgPos(D.RgEb[e]);
                    var dir = (bb - a); if (dir.sqrMagnitude < 1e-4f) continue; dir = dir / dir.magnitude;
                    if (!has0) { d0 = dir; has0 = true; }
                    else if (Mathf.Abs(Vector2.Dot(d0, dir)) < 0.96f) return "That's a corner. Gates go in a straight stretch.";
                    foreach (int nd in new[] { D.RgEa[e], D.RgEb[e] })
                    {
                        var np = D.RgPos(nd);
                        if (Mathf.FloorToInt(np.x) != p.x || Mathf.FloorToInt(np.y) != p.y) continue;
                        int fenced = 0; bool turn = false;
                        foreach (int e2 in D.RgAdj[nd])
                        {
                            if (!S.Fsegs.Has(e2)) continue;
                            fenced++;
                            var o = D.RgPos(D.RgOther(e2, nd)) - np; if (o.sqrMagnitude < 1e-4f) continue;
                            if (Mathf.Abs(Vector2.Dot(o / o.magnitude, d0)) < 0.96f) turn = true;
                        }
                        if (turn || fenced > 2) return "That's a corner post. Gates go in a straight stretch.";
                    }
                }
                // flush: the run carries on (fence or a wall) on both sides of the gate
                if (has0)
                {
                    var a0 = D.RgPos(D.RgEa[segs[0]]);
                    var on = a0 + d0 * Vector2.Dot(c - a0, d0);   // the tile centre projected onto the fence line
                    foreach (float sgn in new[] { -1f, 1f })
                    {
                        var q = on + d0 * (0.95f * sgn);
                        int qx = Mathf.FloorToInt(q.x), qy = Mathf.FloorToInt(q.y);
                        if (qx == p.x && qy == p.y) q = on + d0 * (1.45f * sgn);
                        qx = Mathf.FloorToInt(q.x); qy = Mathf.FloorToInt(q.y);
                        if (!WallLike(qx, qy)) return "The fence ends here: a gate needs fence on both sides.";
                    }
                }
                return "";
            }
            // loose fence tile: straight when two opposite neighbours are wall and the other pair is open
            bool wallNs = WallLike(p.x, p.y - 1) && WallLike(p.x, p.y + 1);
            bool wallEw = WallLike(p.x - 1, p.y) && WallLike(p.x + 1, p.y);
            if (!((wallNs && ew) || (wallEw && ns))) return "That's a corner. Gates go in a straight stretch.";
            return "";
        }

        /// <summary>Nearest tile of your own fence within reach of p that can take a gate, or -1 with the reason of the
        /// nearest fence tile (or a hint when there is no fence there at all).</summary>
        public int SnapGate(Vector2 p, float reach, out string why)
        {
            why = "Tap a fence to put a gate in it.";
            int ri = Mathf.CeilToInt(reach);
            int tx = Mathf.FloorToInt(p.x), ty = Mathf.FloorToInt(p.y);
            var cand = new List<(float d, int k)>();
            for (int dy = -ri; dy <= ri; dy++)
                for (int dx = -ri; dx <= ri; dx++)
                {
                    var q = new Vector2Int(tx + dx, ty + dy);
                    if (!D.InMap(q)) continue;
                    int k = Ti(q);
                    if (!S.Fences.Has(k)) continue;
                    float dd = (V.F(q).Add(V.HALF) - p).magnitude;
                    if (dd <= reach) cand.Add((dd, k));
                }
            cand.Sort((a, b) => a.d != b.d ? a.d.CompareTo(b.d) : a.k.CompareTo(b.k));
            string first = null;
            foreach (var c in cand)
            {
                string e = CanGate(c.k);
                if (e == "") return c.k;
                if (first == null) first = e;
            }
            if (first != null) why = first;
            return -1;
        }
    }
}
