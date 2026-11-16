// Map side of the camp / outpost / trap UI (main.gd + world_view.gd): BUILD OUTPOST mode (tap a building ->
// build_outpost), tapping one of your camps (camp sheet) or a trap (trap sheet), and the original's trap-LINE tool
// (world_view.gd trap_tap_tile / line_tiles / _trap_add / trap_undo: tap a spot, then the next, traps fill the line
// every 2 tiles; CONFIRM sends them all with place_traps). Used under the original rules (PhaseARules=false);
// the Phase A single-trap tool is unchanged.
using System;
using System.Collections.Generic;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed partial class MapView
    {
        public int SelectedBase { get; private set; } = -1;   // own camp tapped (camp sheet)
        public int SelectedTrap { get; private set; } = -1;   // trap tile tapped (trap sheet)
        public bool OutpostMode { get; private set; }

        // trap line (world_view.gd trap_tiles / trap_hist / trap_anchor)
        const int TRAP_STEP = 2;
        public readonly List<Vector2Int> TrapTiles = new List<Vector2Int>();
        readonly List<int> _trapHist = new List<int>();
        public Vector2Int TrapAnchor { get; private set; } = new Vector2Int(-1, -1);
        public bool TrapLine => ActiveTool == Tool.Trap && !Game.PhaseARules;

        void ClearCampSel() { SelectedBase = -1; SelectedTrap = -1; }

        void ResetCampTools()
        {
            OutpostMode = false;
            TrapTiles.Clear(); _trapHist.Clear(); TrapAnchor = new Vector2Int(-1, -1);
        }

        // ---------------- BUILD OUTPOST ----------------
        public void StartOutpost()
        {
            CancelTool();
            ClearBuilding();
            OutpostMode = true;
        }

        public void EndOutpost() => OutpostMode = false;

        /// <summary>A tile of the building (main.gd _building_tap returns a tile the building stands on).</summary>
        Vector2Int LotTile(int lot)
        {
            var lc = D.LotCenter(lot);
            var t = new Vector2Int(Mathf.FloorToInt(lc.x), Mathf.FloorToInt(lc.y));
            if (D.BuildingAt(t) == lot) return t;
            for (int r = 1; r <= 6; r++)
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                        var q = new Vector2Int(t.x + dx, t.y + dy);
                        if (D.InMap(q) && D.BuildingAt(q) == lot) return q;
                    }
            return D.LotDoor(lot);
        }

        /// <summary>Play-mode tap hook (before buildings): outpost mode, own camps, traps. True when handled.</summary>
        bool CampTap(Ray ray, Vector2 gp)
        {
            var g = G; var bs = Bootstrap.Instance;
            if (g?.S == null || bs == null) return false;
            if (OutpostMode)
            {
                int lot = _gen.PickLot(ray.origin, ray.direction);
                Vector2Int t;
                if (lot >= 0) t = LotTile(lot);
                else if (gp.x >= 0f) t = new Vector2Int(Mathf.FloorToInt(gp.x), Mathf.FloorToInt(gp.y));
                else return true;
                string e = bs.Act("build_outpost", t);
                if (e != "") bs.Toast(e);
                else { EndOutpost(); bs.Toast("Outpost started. Engineers speed up construction; move people there from CREW & JOBS."); DirtyDynamic(); }
                return true;
            }
            if (ActiveTool != Tool.None) return false;
            // main.gd _select_at: a camp (its buildings, or within ~1.3 tiles of its gate) wins
            Base hitB = null;
            int pl = _gen.PickLot(ray.origin, ray.direction);
            if (pl >= 0) hitB = g.LotOwnerBase(pl);
            if (hitB == null && gp.x >= 0f)
            {
                float bd = 1.3f;
                foreach (var b in g.C.Bases)
                {
                    float d = (new Vector2(b.Pos.x + 0.5f, b.Pos.y + 0.5f) - gp).magnitude;
                    if (d < bd) { bd = d; hitB = b; }
                }
            }
            if (hitB != null && hitB.Col == g.CurCol) { ClearBuilding(); SelectedBase = hitB.Id; bs.PlayClick(); return true; }
            if (gp.x >= 0f && pl < 0)
            {
                var tt = new Vector2Int(Mathf.FloorToInt(gp.x), Mathf.FloorToInt(gp.y));
                if (D.InMap(tt) && !g.S.Towers.ContainsKey(tt.y * D.W + tt.x))
                {
                    int tk = NearestKey(tt, k => g.S.Traps.ContainsKey(k));
                    if (tk >= 0 && !g.S.Fences.ContainsKey(tk)) { ClearBuilding(); SelectedTrap = tk; bs.PlayClick(); return true; }
                }
            }
            return false;
        }

        // ---------------- trap line ----------------
        static List<Vector2Int> LineTiles(Vector2Int a, Vector2Int b)
        {
            var o = new List<Vector2Int>();
            int n = Math.Max(Math.Abs(b.x - a.x), Math.Abs(b.y - a.y));
            if (n == 0) { o.Add(a); return o; }
            for (int i = 0; i <= n; i++)
            {
                double t = (double)i / n;
                o.Add(new Vector2Int((int)Math.Round(a.x + (b.x - a.x) * t, MidpointRounding.AwayFromZero), (int)Math.Round(a.y + (b.y - a.y) * t, MidpointRounding.AwayFromZero)));
            }
            return o;
        }

        void TrapAdd(List<Vector2Int> tiles)
        {
            var add = new List<Vector2Int>();
            var have = new HashSet<Vector2Int>(TrapTiles);
            foreach (var p in tiles)
            {
                if (!D.InMap(p) || !D.Walkable(p) || have.Contains(p)) continue;
                bool close = false;
                int from = Math.Max(0, TrapTiles.Count - 6);
                foreach (var q in add) if (Math.Max(Math.Abs(q.x - p.x), Math.Abs(q.y - p.y)) < TRAP_STEP) { close = true; break; }
                if (!close) for (int i = from; i < TrapTiles.Count; i++) { var q = TrapTiles[i]; if (Math.Max(Math.Abs(q.x - p.x), Math.Abs(q.y - p.y)) < TRAP_STEP) { close = true; break; } }
                if (close) continue;
                add.Add(p); have.Add(p);
            }
            if (add.Count == 0) return;
            int room = Data.TRAP_MAX - TrapTiles.Count;
            if (room <= 0) { Bootstrap.Instance?.Toast(string.Format("That's {0} traps already. Confirm them first.", Data.TRAP_MAX)); return; }
            if (add.Count > room) add = add.GetRange(0, room);
            _trapHist.Add(TrapTiles.Count);
            TrapTiles.AddRange(add);
        }

        /// <summary>world_view.gd trap_tap_tile.</summary>
        public void TrapTapTile(Vector2Int t)
        {
            if (!D.InMap(t)) return;
            if (TrapAnchor == t) TrapAnchor = new Vector2Int(-1, -1);
            else if (TrapAnchor.x >= 0) { TrapAdd(LineTiles(TrapAnchor, t)); TrapAnchor = t; }
            else
            {
                TrapAdd(new List<Vector2Int> { t });
                TrapAnchor = t;
                if (TrapTiles.Count == 1) Bootstrap.Instance?.Toast("Tap another spot for a line of traps, or tap this one again to start a new line.");
            }
            _ghostSig = ""; RefreshGhost();
        }

        public void TrapUndo()
        {
            if (_trapHist.Count == 0) return;
            int n = _trapHist[_trapHist.Count - 1];
            _trapHist.RemoveAt(_trapHist.Count - 1);
            TrapTiles.RemoveRange(n, TrapTiles.Count - n);
            TrapAnchor = n > 0 ? TrapTiles[n - 1] : new Vector2Int(-1, -1);
            _ghostSig = ""; RefreshGhost();
        }

        public void TrapClear()
        {
            TrapTiles.Clear(); _trapHist.Clear(); TrapAnchor = new Vector2Int(-1, -1);
            _ghostSig = ""; RefreshGhost();
        }

        /// <summary>main.gd _confirm_traps: place_traps with the whole line, then a fresh line.</summary>
        string TrapConfirm()
        {
            if (TrapTiles.Count == 0) return "Tap two corners or spots first.";
            string e = Bootstrap.Instance.Act("place_traps", new List<Vector2Int>(TrapTiles), TrapKind);
            if (e == "") { TrapTiles.Clear(); _trapHist.Clear(); TrapAnchor = new Vector2Int(-1, -1); }
            return e;
        }

        /// <summary>Ghost of the trap line: green tiles get a trap, red ones are skipped; the frame marks the anchor.</summary>
        bool TrapLineGhost(List<int> tiles, List<int> state, ref Vector2 a)
        {
            var g = G;
            var pv = g.TrapPreview(new List<Vector2Int>(TrapTiles), TrapKind);
            for (int i = 0; i < TrapTiles.Count; i++)
            {
                var t = TrapTiles[i];
                tiles.Add(t.y * D.W + t.x);
                state.Add(i < pv.Ok.Count && pv.Ok[i] ? 0 : 2);
            }
            var cost = pv.Cost;
            ToolErr = TrapTiles.Count == 0 ? "" : pv.N == 0 ? pv.Err : !g.CanAfford(cost) ? "Need " + Game.FmtCost(cost) + "." : pv.Err != "" ? "Some spots are skipped: " + pv.Err : "";
            ToolOk = pv.N > 0 && g.CanAfford(cost);
            ToolCost = pv.N > 0 ? Game.FmtCost(cost) : "-";
            ToolInfo = string.Format("{0}: {1} trap{2}", Data.TRAPS[TrapKind].Name, pv.N, pv.N == 1 ? "" : "s");
            var at = TrapAnchor.x >= 0 ? TrapAnchor : TrapTiles.Count > 0 ? TrapTiles[TrapTiles.Count - 1] : new Vector2Int(-1, -1);
            if (at.x < 0) { if (_ghostGo != null) { DestroyChunk(_ghostGo); _ghostGo = null; } _ghostSig = ""; return false; }
            a = new Vector2(at.x + 0.5f, at.y + 0.5f);
            return true;
        }
    }
}
