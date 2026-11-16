// v0.12.0a "Defend the base": play-mode taps on the map. With a DEFENSE tool active a tap moves the
// placement ghost (wall segment / gate / watchtower / trap / wall upgrade), which GameUI's placement bar
// rotates, lengthens and confirms; with no tool a tap selects a building (conversion sheet) or a wall run
// (upgrade / repair sheet). All checks and costs come from the sim (Game.PlanWall, CanGate, CanTower,
// CanTrap, WallUpgradePlan), all changes go through Bootstrap.Act -> Game.DoAction.
using System;
using System.Collections.Generic;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed partial class MapView
    {
        /// <summary>Wall..Upgrade: DEFENSE placement tools. Loop: the original's SUGGEST fence loop. Squad / Scav / Drone /
        /// Strike: map targets for SQUADS (CLEAR ROAD, SCAVENGE, DRONE SCOUT, GUNSHIP STRIKE), as main.gd world.mode.</summary>
        public enum Tool { None, Wall, Gate, Tower, Trap, Upgrade, Loop, Squad, Scav, Drone, Strike, Fence }
        public bool TargetTool => ActiveTool == Tool.Squad || ActiveTool == Tool.Scav || ActiveTool == Tool.Drone || ActiveTool == Tool.Strike;
        public int TowerTier { get; private set; }
        public int SquadId { get; private set; } = -1;
        public Vector2Int Target { get; private set; } = new Vector2Int(-1, -1);
        public TargetInfo TargetText { get; private set; } = new TargetInfo();
        List<int> _loopEdges = new List<int>();

        public Tool ActiveTool { get; private set; } = Tool.None;
        public int WallTier { get; private set; }
        public int TrapKind { get; private set; }
        public float WallLen { get; private set; } = 6f;
        public int WallDir { get; private set; }          // 0 along the street grid, 1 diagonal, 2 across, 3 other diagonal
        public Vector2 GhostAt { get; private set; } = new Vector2(-1f, -1f);
        public bool ToolOk { get; private set; }
        public string ToolErr { get; private set; } = "";
        public string ToolCost { get; private set; } = "";
        public string ToolInfo { get; private set; } = "";
        int _toolKey = -1;            // gate tile / run id the tool aims at
        GameObject _ghostGo;
        float _ghostT;
        string _ghostSig = "";

        public int BuildingLot { get; private set; } = -1;   // building tapped in play mode (conversion sheet)
        public int SelectedRun { get; private set; } = -1;   // wall run tapped in play mode
        public int SelectedFence { get; private set; } = -1; // fence tile tapped (original rules: fence sheet)
        public int SelectedTower { get; private set; } = -1; // tower tile tapped (tower sheet)
        public int SelectedGroup { get; private set; } = -1; // survivor group tapped (SOS sheet)
        GameObject _bselGo;

        bool Playing => G?.S != null && G.C != null && G.C.Started && !Picking;

        // ---------------- tools ----------------
        public void StartTool(Tool t, int tier = 0, int trapKind = 0)
        {
            ClearBuilding();
            ResetCampTools();   // MapView.Camp.cs
            ActiveTool = t; WallTier = tier; TrapKind = trapKind;
            if (t == Tool.Tower) TowerTier = tier;
            if (t == Tool.Loop) { var mb = G?.MainBase(); _loopEdges = mb != null ? G.SuggestLoop(mb, tier) : new List<int>(); }
            if (t == Tool.Fence) FenceToolStart();   // MapView.Fence.cs: corner posts
            // start the ghost just outside the camp gate, or at the screen centre when the camera looks elsewhere
            var c = new Vector2(_focus.x, -_focus.z);
            var g = G; var b = g?.MainBase();
            if (b != null && (c - HomeTile()).magnitude < 6f)
            {
                var y = g.YardOf(b);
                c = y.C - y.V * (y.Half + 3f);
                WallDir = 0;
            }
            GhostAt = c;
            _ghostSig = "";
            RefreshGhost();
        }

        public void SetWallTier(int t)
        {
            WallTier = t;
            if (ActiveTool == Tool.Loop) { var mb = G?.MainBase(); _loopEdges = mb != null ? G.SuggestLoop(mb, t) : new List<int>(); }
            RefreshGhost();
        }
        public void SetTowerTier(int t) { TowerTier = t; RefreshGhost(); }

        /// <summary>SQUADS / air support: pick a target on the map (main.gd _start_squad_target / _start_air).
        /// kind: clear | scav | drone | strike.</summary>
        public void StartTarget(string kind, int sid)
        {
            ClearBuilding();
            if (_ghostGo != null) { DestroyChunk(_ghostGo); _ghostGo = null; }
            ActiveTool = kind == "scav" ? Tool.Scav : kind == "drone" ? Tool.Drone : kind == "strike" ? Tool.Strike : Tool.Squad;
            SquadId = sid; Target = new Vector2Int(-1, -1); GhostAt = new Vector2(-1f, -1f);
            var g = G;
            var q = g?.SquadById(sid);
            var b = q != null ? g.BaseById(q.Base) : g?.MainBase();
            if (b != null) FlyTo(new Vector2(b.Pos.x + 0.5f, b.Pos.y + 0.5f));
            if (ActiveTool == Tool.Scav || ActiveTool == Tool.Squad) _distTarget = Mathf.Max(_distTarget, 55f);
            _ghostSig = "";
            RefreshGhost();
        }

        string TargetKind => ActiveTool == Tool.Scav ? "scav" : ActiveTool == Tool.Drone ? "drone" : ActiveTool == Tool.Strike ? "strike" : "clear";
        public void SetTrapKind(int k) { TrapKind = k; RefreshGhost(); }
        public void Rotate() { WallDir = (WallDir + 1) & 3; RefreshGhost(); }
        public void Longer() { WallLen = Mathf.Min(Game.WALL_MAX_LEN, WallLen + 2f); RefreshGhost(); }
        public void Shorter() { WallLen = Mathf.Max(2f, WallLen - 2f); RefreshGhost(); }

        public void CancelTool()
        {
            ActiveTool = Tool.None; _toolKey = -1; ToolErr = ToolCost = ToolInfo = ""; ToolOk = false;
            ResetCampTools();   // MapView.Camp.cs: outpost mode, trap line
            SquadId = -1; Target = new Vector2Int(-1, -1); TargetText = new TargetInfo(); _loopEdges = new List<int>();
            FenceToolReset();   // MapView.Fence.cs
            if (_ghostGo != null) { DestroyChunk(_ghostGo); _ghostGo = null; }
            _ghostSig = "";
        }

        Vector2 WallAxis()
        {
            double th = D.RgAngle * Math.PI / 180.0;
            var u = new Vector2((float)Math.Cos(th), (float)Math.Sin(th)); var v = new Vector2(-u.y, u.x);
            switch (WallDir & 3)
            {
                case 1: return (u + v).normalized;
                case 2: return v;
                case 3: return (v - u).normalized;
                default: return u;
            }
        }

        public void WallEnds(out Vector2 a, out Vector2 b)
        {
            var ax = WallAxis();
            a = GhostAt - ax * (WallLen * 0.5f); b = GhostAt + ax * (WallLen * 0.5f);
        }

        /// <summary>Re-check the tool target against the sim and rebuild the ghost mesh if anything changed.</summary>
        public void RefreshGhost()
        {
            var g = G;
            if (ActiveTool == Tool.None || g?.S == null || _gen == null) return;
            var tiles = new List<int>(); var state = new List<int>();
            var kind = MapGen.GhostKind.Wall;
            Vector2 a = GhostAt, b = GhostAt;
            int tier = WallTier;
            var tile = new Vector2Int(Mathf.FloorToInt(GhostAt.x), Mathf.FloorToInt(GhostAt.y));
            int key = D.InMap(tile) ? tile.y * D.W + tile.x : -1;
            switch (ActiveTool)
            {
                case Tool.Wall:
                {
                    WallEnds(out a, out b);
                    var pl = g.PlanWall(a, b, WallTier);
                    tiles = pl.Tiles; state = pl.State;
                    ToolOk = pl.Build.Count > 0 && pl.CanAfford && pl.Err == "";
                    ToolErr = pl.Err != "" ? pl.Err : (!pl.CanAfford && pl.Build.Count > 0 ? "Need " + Game.FmtCost(pl.Cost) + "." : "");
                    if (pl.Build.Count > 0 && pl.Err != "" && pl.CanAfford) { ToolOk = true; ToolErr = "Some tiles are skipped: " + pl.Err; }
                    ToolCost = pl.Build.Count > 0 ? Game.FmtCost(pl.Cost) : "-";
                    ToolInfo = string.Format("{0} · {1:0} tiles long · {2} to build", Data.FENCES[WallTier].Name, WallLen, pl.Build.Count);
                    break;
                }
                case Tool.Gate:
                {
                    kind = MapGen.GhostKind.Gate;
                    // v0.14.0a: the reach grows with the camera distance so a gate can be dropped on a fence when zoomed out
                    float reach = Mathf.Max(1.6f, _dist * 0.045f); int ri = Mathf.CeilToInt(reach);
                    int best = -1; float bd = reach;
                    for (int dy = -ri; dy <= ri; dy++)
                        for (int dx = -ri; dx <= ri; dx++)
                        {
                            var q = new Vector2Int(tile.x + dx, tile.y + dy);
                            if (!D.InMap(q)) continue;
                            int k = q.y * D.W + q.x;
                            if (!g.S.Fences.ContainsKey(k)) continue;
                            float dd = (new Vector2(q.x + 0.5f, q.y + 0.5f) - GhostAt).magnitude;
                            if (dd < bd) { bd = dd; best = k; }
                        }
                    _toolKey = best;
                    if (best >= 0) { a = new Vector2(best % D.W + 0.5f, best / D.W + 0.5f); tiles.Add(best); }
                    ToolErr = best < 0 ? "Tap a wall to put a gate in it." : g.CanGate(best);
                    if (ToolErr == "" && !g.CanAfford(Data.GATE_COST)) ToolErr = "Need " + Game.FmtCost(Data.GATE_COST) + ".";
                    ToolOk = ToolErr == ""; state.Add(ToolOk ? 0 : 2);
                    ToolCost = Game.FmtCost(Data.GATE_COST);
                    ToolInfo = "Reinforced gate: twice the wall's hp, survivors come and go";
                    break;
                }
                case Tool.Tower:
                {
                    kind = MapGen.GhostKind.Tower;
                    a = new Vector2(tile.x + 0.5f, tile.y + 0.5f);
                    if (key >= 0) tiles.Add(key);
                    var td = Data.TOWERS[TowerTier];
                    ToolErr = g.CanTower(tile, TowerTier);
                    var cost = g.TowerCost(TowerTier);
                    if (ToolErr == "" && !g.CanAfford(cost)) ToolErr = "Need " + Game.FmtCost(cost) + ".";
                    ToolOk = ToolErr == ""; state.Add(ToolOk ? 0 : 2);
                    ToolCost = Game.FmtCost(cost);
                    ToolInfo = string.Format("{0}: range {1:0.#}, {2}", td.Name, td.Range + g.Rx("tower_range"), td.Ammo > 0.0 ? string.Format("{0:0.##} ammo per shot", td.Ammo) : "no ammo") + (td.Crew ? ", one survivor crews it" : ", needs power");
                    break;
                }
                case Tool.Trap:
                {
                    kind = MapGen.GhostKind.Trap;
                    if (!Game.PhaseARules) { if (!TrapLineGhost(tiles, state, ref a)) return; b = a; break; }   // original trap line (MapView.Camp.cs)
                    a = new Vector2(tile.x + 0.5f, tile.y + 0.5f);
                    if (key >= 0) tiles.Add(key);
                    ToolErr = g.CanTrap(tile, TrapKind);
                    var cost = g.TrapCost(TrapKind);
                    if (ToolErr == "" && !g.CanAfford(cost)) ToolErr = "Need " + Game.FmtCost(cost) + ".";
                    ToolOk = ToolErr == ""; state.Add(ToolOk ? 0 : 2);
                    ToolCost = Game.FmtCost(cost);
                    ToolInfo = Data.TRAPS[TrapKind].Name + ": " + Data.TRAPS[TrapKind].Desc;
                    break;
                }
                case Tool.Loop:
                {
                    // the original's SUGGEST: a closed ring of road-graph fence sections around the main camp
                    var mb = g.MainBase();
                    var pv = g.SegPreview(_loopEdges, WallTier);
                    for (int i = 0; i < _loopEdges.Count; i++)
                        foreach (int k in g.EdgeTiles(_loopEdges[i]))
                        {
                            if (!D.Walkable(new Vector2Int(k % D.W, k / D.W))) continue;
                            tiles.Add(k);
                            state.Add(g.S.Fences.TryGetValue(k, out var f0) && f0.Col == g.CurCol && f0.Tier >= WallTier ? 3 : i < pv.Ok.Count && pv.Ok[i] ? 0 : 2);
                        }
                    if (tiles.Count > 0) { a = new Vector2(tiles[0] % D.W + 0.5f, tiles[0] / D.W + 0.5f); b = a; }
                    ToolErr = _loopEdges.Count == 0 ? string.Format("No closed loop fits around {0} yet. Clear more ground with a squad, or place sections by hand.", mb?.Name ?? "your camp")
                        : !g.FenceTierUnlocked(WallTier) ? string.Format("Research {0} first.", g.UnlockSourceName(Data.FENCES[WallTier].Unlock))
                        : pv.N == 0 ? (pv.Err != "" ? pv.Err : "Already fenced.")
                        : !g.CanAfford(pv.Cost) ? "Need " + Game.FmtCost(pv.Cost) + "." : pv.Err != "" ? "Some sections are skipped: " + pv.Err : "";
                    ToolOk = _loopEdges.Count > 0 && pv.N > 0 && g.CanAfford(pv.Cost) && g.FenceTierUnlocked(WallTier);
                    ToolCost = pv.N > 0 ? Game.FmtCost(pv.Cost) : "-";
                    ToolInfo = string.Format("Suggested loop around {0}: {1} · {2} sections · {3} tiles to build", mb?.Name ?? "camp", Data.FENCES[WallTier].Name, pv.Edges, pv.N);
                    kind = MapGen.GhostKind.Route;
                    break;
                }
                case Tool.Fence:
                {
                    kind = MapGen.GhostKind.Fence;
                    FenceGhost(tiles, state);   // MapView.Fence.cs
                    a = b = new Vector2(-1f, -1f);
                    break;
                }
                case Tool.Squad: case Tool.Scav: case Tool.Drone: case Tool.Strike:
                {
                    kind = MapGen.GhostKind.Route;
                    var ti = SideMenus.SquadTarget(g, TargetKind, SquadId, Target);
                    TargetText = ti;
                    ToolOk = ti.Ok; ToolErr = ti.Ok ? "" : ti.Detail; ToolInfo = ti.Title; ToolCost = ti.Detail;
                    if (Target.x >= 0) { a = new Vector2(Target.x + 0.5f, Target.y + 0.5f); b = a; }
                    var seen = new HashSet<int>();
                    int range = g.SquadRange();
                    for (int i = 0; i < ti.Path.Count; i++)
                    {
                        var pt = V.I(ti.Path[i]);
                        if (!D.InMap(pt)) continue;
                        int k = pt.y * D.W + pt.x;
                        if (seen.Add(k)) { tiles.Add(k); state.Add(i < range ? 0 : 2); }
                    }
                    foreach (int l in ti.Search)
                    {
                        var dr = D.LotDoor(l);
                        if (!D.InMap(dr)) continue;
                        int k = dr.y * D.W + dr.x;
                        if (seen.Add(k)) { tiles.Add(k); state.Add(3); }
                    }
                    if (ti.Radius > 0f && Target.x >= 0)
                    {
                        // ring of tiles at the drone / strike radius
                        int n = Mathf.Max(24, (int)(ti.Radius * 6.3f));
                        for (int i = 0; i < n; i++)
                        {
                            float an = i * Mathf.PI * 2f / n;
                            var q = new Vector2Int(Mathf.FloorToInt(Target.x + 0.5f + Mathf.Cos(an) * ti.Radius), Mathf.FloorToInt(Target.y + 0.5f + Mathf.Sin(an) * ti.Radius));
                            if (!D.InMap(q)) continue;
                            int k = q.y * D.W + q.x;
                            if (seen.Add(k)) { tiles.Add(k); state.Add(ti.Ok ? 3 : 2); }
                        }
                    }
                    if (Target.x < 0 && tiles.Count == 0) { if (_ghostGo != null) { DestroyChunk(_ghostGo); _ghostGo = null; } _ghostSig = ""; return; }
                    break;
                }
                case Tool.Upgrade:
                {
                    kind = MapGen.GhostKind.Upgrade;
                    var run = g.RunNear(GhostAt, 1.6f);
                    _toolKey = run?.Id ?? -1;
                    var up = g.WallUpgradePlan(run);
                    if (run != null) { a = run.A; b = run.B; tiles = up.tiles; foreach (var _ in up.tiles) state.Add(up.err == "" ? 0 : 2); tier = up.next >= 0 ? up.next : run.Tier; }
                    ToolErr = run == null ? "Tap one of your walls." : up.err;
                    if (ToolErr == "" && !g.CanAfford(up.cost)) ToolErr = "Need " + Game.FmtCost(up.cost) + ".";
                    ToolOk = ToolErr == "";
                    ToolCost = up.next >= 0 ? Game.FmtCost(up.cost) : "-";
                    ToolInfo = run == null ? "Upgrade: wood palisade → scrap metal → concrete" : string.Format("{0} → {1} ({2} tiles)", Data.FENCES[run.Tier].Name, up.next >= 0 ? Data.FENCES[up.next].Name : "max", up.tiles.Count);
                    break;
                }
            }
            // rebuild the ghost mesh only when what it shows changed
            var sb = new System.Text.StringBuilder();
            sb.Append((int)ActiveTool).Append(':').Append(a.x.ToString("0.00")).Append(',').Append(a.y.ToString("0.00")).Append(':').Append(b.x.ToString("0.00")).Append(',').Append(b.y.ToString("0.00")).Append(':').Append(tier).Append(ToolOk ? 'y' : 'n');
            for (int i = 0; i < tiles.Count; i++) sb.Append(tiles[i]).Append(state.Count > i ? state[i] : 0);
            string sig = sb.ToString();
            if (sig == _ghostSig && _ghostGo != null) return;
            _ghostSig = sig;
            if (_ghostGo != null) DestroyChunk(_ghostGo);
            var ch = _gen.Ghost(kind, a, b, tiles, state, tier, ToolOk, TrapKind);
            _ghostGo = new GameObject("Placement ghost");
            _ghostGo.transform.SetParent(transform, false);
            if (ch.Mesh.Count > 0) MakeObject("Ghost", ToMesh("Ghost", ch.Mesh), _objMat, _ghostGo.transform);
            if (_glowMat != null && ch.Shadow.Count > 0) MakeObject("Ghost tiles", ToMesh("Ghost tiles", ch.Shadow), _glowMat, _ghostGo.transform);
        }

        /// <summary>CONFIRM on the placement bar. Returns the sim's error ("" = built).</summary>
        public string ConfirmTool()
        {
            var bs = Bootstrap.Instance; var g = G;
            if (bs == null || g == null) return "No game.";
            string e;
            switch (ActiveTool)
            {
                case Tool.Wall:
                    WallEnds(out var a, out var b);
                    if (Game.PhaseARules) e = bs.Act("place_wall", a, b, WallTier);
                    else
                    {
                        // original rules: the ghost line is laid as fence tiles (main.gd fence tool -> build_fence)
                        var pl = g.PlanWall(a, b, WallTier);
                        var tl = new List<Vector2Int>();
                        foreach (int k in pl.Build) tl.Add(new Vector2Int(k % D.W, k / D.W));
                        e = tl.Count > 0 ? bs.Act("build_fence", tl, WallTier) : (pl.Err != "" ? pl.Err : "Nothing to build here.");
                    }
                    break;
                case Tool.Fence: e = FenceConfirm(); break;   // MapView.Fence.cs
                case Tool.Loop: e = _loopEdges.Count > 0 ? bs.Act("build_fence_segs", new List<int>(_loopEdges), WallTier) : ToolErr; if (e == "") { CancelTool(); return e; } break;
                case Tool.Gate: e = _toolKey >= 0 ? bs.Act("place_gate", _toolKey) : "Tap a wall to put a gate in it."; break;
                case Tool.Tower: e = bs.Act("place_tower", new Vector2Int(Mathf.FloorToInt(GhostAt.x), Mathf.FloorToInt(GhostAt.y)), TowerTier); break;
                case Tool.Trap:
                    {
                        if (!Game.PhaseARules) { e = TrapConfirm(); break; }   // original: place_traps with the whole line
                        var tp = new Vector2Int(Mathf.FloorToInt(GhostAt.x), Mathf.FloorToInt(GhostAt.y));
                        e = Game.PhaseARules ? bs.Act("place_trap", tp, TrapKind) : bs.Act("place_traps", new List<Vector2Int> { tp }, TrapKind);
                        break;
                    }
                case Tool.Squad: case Tool.Scav: case Tool.Drone: case Tool.Strike:
                    {
                        if (Target.x < 0) return "Tap a target first.";
                        var ak = ActiveTool;
                        e = ak == Tool.Squad ? bs.Act("send_squad", SquadId, Target) : ak == Tool.Scav ? bs.Act("send_squad_scav", SquadId, Target)
                          : ak == Tool.Drone ? bs.Act("drone_scout", Target) : bs.Act("air_strike", Target);
                        if (e == "") { CancelTool(); return e; }
                        break;
                    }
                case Tool.Upgrade: e = _toolKey >= 0 ? bs.Act("upgrade_wall", _toolKey) : "Tap one of your walls."; break;
                default: e = ""; break;
            }
            _ghostSig = "";
            _dynKey = "";
            RefreshGhost();
            return e;
        }

        void UpdateTools()
        {
            if (!Playing) { if (ActiveTool != Tool.None) CancelTool(); if (BuildingLot >= 0) ClearBuilding(); return; }
            if (ActiveTool == Tool.None) return;
            if (ActiveTool == Tool.Fence) UpdatePosts();   // MapView.Fence.cs: corner-post pins around the camera
            _ghostT -= Time.unscaledDeltaTime;
            if (_ghostT <= 0f) { _ghostT = TargetTool || ActiveTool == Tool.Loop ? 1.5f : 0.3f; RefreshGhost(); }
        }

        // ---------------- play-mode taps ----------------
        void OnPlayTap(Vector2 screen)
        {
            if (_cam == null || _gen == null || G?.S == null) return;
            var ray = _cam.ScreenPointToRay(new Vector3(screen.x, screen.y, 0f));
            Vector2 gp = new Vector2(-1f, -1f);
            if (Ground(screen, out var hit)) gp = new Vector2(hit.x, -hit.z);
            if (TargetTool)
            {
                // a building counts as its own tile (the route goes to its door), else the ground under the finger
                int tl = ActiveTool == Tool.Scav || ActiveTool == Tool.Squad ? _gen.PickLot(ray.origin, ray.direction) : -1;
                Vector2Int t;
                if (tl >= 0) { var lc = D.LotCenter(tl); t = new Vector2Int(Mathf.FloorToInt(lc.x), Mathf.FloorToInt(lc.y)); if (D.BuildingAt(t) != tl) t = D.LotDoor(tl); }
                else if (gp.x >= 0f) t = new Vector2Int(Mathf.FloorToInt(gp.x), Mathf.FloorToInt(gp.y));
                else return;
                Target = t; GhostAt = new Vector2(t.x + 0.5f, t.y + 0.5f);
                RefreshGhost();
                Bootstrap.Instance?.PlayClick();
                return;
            }
            if (ActiveTool == Tool.Loop) return;   // the suggested loop doesn't move
            if (ActiveTool == Tool.Fence) { FenceTap(screen, gp); return; }   // MapView.Fence.cs
            if (TrapLine) { if (gp.x >= 0f) TrapTapTile(new Vector2Int(Mathf.FloorToInt(gp.x), Mathf.FloorToInt(gp.y))); return; }   // MapView.Camp.cs
            if (ActiveTool != Tool.None)
            {
                if (gp.x < 0f) return;
                GhostAt = gp;
                RefreshGhost();
                Bootstrap.Instance?.PlayClick();
                return;
            }
            if (CampTap(ray, gp)) return;   // MapView.Camp.cs: BUILD OUTPOST, own camps, traps
            // survivor groups waiting for rescue (SOS sheet) stand on the street: they win over the building behind them
            if (gp.x >= 0f && G.S.Groups != null)
            {
                Group bestG = null; float bd = 1.6f;
                foreach (var gr in G.S.Groups)
                {
                    if (gr.St != "wait") continue;
                    float dd = (new Vector2(gr.Pos.x + 0.5f, gr.Pos.y + 0.5f) - gp).magnitude;
                    if (dd < bd) { bd = dd; bestG = gr; }
                }
                if (bestG != null) { ClearBuilding(); SelectedGroup = bestG.Id; Bootstrap.Instance?.PlayClick(); return; }
            }
            if (TrySelectWreck(gp)) return;   // main.gd _select_at: a wreck on the road wins over the lot behind it
            if (TapCampNear(gp)) return;   // faction camp sheet (MapView.Factions.cs)
            int lot = _gen.PickLot(ray.origin, ray.direction);
            if (TapCampLot(lot)) return;
            if (lot >= 0 && !G.InCampYard(lot)) { SelectBuilding(lot); return; }
            if (gp.x >= 0f)
            {
                var tt = new Vector2Int(Mathf.FloorToInt(gp.x), Mathf.FloorToInt(gp.y));
                int tk = NearestKey(tt, k => G.S.Towers.ContainsKey(k));
                if (tk >= 0) { ClearBuilding(); SelectedTower = tk; Bootstrap.Instance?.PlayClick(); return; }
                if (!Game.PhaseARules)
                {
                    int fk = NearestKey(tt, k => G.S.Fences.ContainsKey(k));
                    if (fk >= 0) { ClearBuilding(); SelectedFence = fk; Bootstrap.Instance?.PlayClick(); return; }
                }
            }
            var run = gp.x >= 0f && Game.PhaseARules ? G.RunNear(gp, 1.0f) : null;
            if (run != null) { ClearBuilding(); SelectedRun = run.Id; Bootstrap.Instance?.PlayClick(); return; }
            ClearBuilding();
        }

        public void SelectBuilding(int lot)
        {
            ClearBuilding();
            BuildingLot = lot;
            var g = G;
            bool ok = g != null && g.ConvPlaceError(lot) == "";
            var ch = _gen.Highlight(lot, ok);
            _bselGo = new GameObject("Building selection");
            _bselGo.transform.SetParent(transform, false);
            if (ch.Mesh.Count > 0) MakeObject("Highlight", ToMesh("Highlight", ch.Mesh), _objMat, _bselGo.transform);
            if (_glowMat != null && ch.Shadow.Count > 0) MakeObject("Glow", ToMesh("Glow", ch.Shadow), _glowMat, _bselGo.transform);
            Bootstrap.Instance?.PlayClick();
        }

        /// <summary>Tile key within one tile of t that passes the test (t itself first), or -1.</summary>
        int NearestKey(Vector2Int t, Func<int, bool> ok)
        {
            for (int r = 0; r <= 1; r++)
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                        var q = new Vector2Int(t.x + dx, t.y + dy);
                        if (!D.InMap(q)) continue;
                        int k = q.y * D.W + q.x;
                        if (ok(k)) return k;
                    }
            return -1;
        }

        public void SelectGroup(int gid) { ClearBuilding(); SelectedGroup = gid; }

        public void ClearBuilding()
        {
            BuildingLot = -1; SelectedRun = -1; SelectedFence = -1; SelectedTower = -1; SelectedGroup = -1; SelectedWreck = -1;
            ClearCampSel();   // MapView.Camp.cs
            SelectedCamp = "";   // MapView.Factions.cs
            if (_bselGo != null) { DestroyChunk(_bselGo); _bselGo = null; }
        }

        /// <summary>Building info for the conversion sheet.</summary>
        public string BuildingType(int lot)
        {
            if (_gen == null) return "Building";
            _gen.Describe(lot, out string type, out _, out _, out _);
            return type;
        }

        /// <summary>Floating label anchors of converted buildings (world space).</summary>
        public bool LabelAnchor(int lot, out Vector3 w)
        {
            w = default;
            return _gen != null && _gen.LabelAnchor(lot, out w);
        }

        /// <summary>Glide the camera to a tile-space point.</summary>
        public void FlyTo(Vector2 p)
        {
            _focusTarget = new Vector3(p.x, 0f, -p.y);
            _animFocus = true;
            if (_distTarget > 40f) _distTarget = 30f;
        }

        /// <summary>Force the dynamic layer (walls, conversions) to rebuild now.</summary>
        public void DirtyDynamic() { _dynKey = ""; _dynT = 0f; }

        // ================= v0.14.0a: FENCE tool with corner posts =================
        // Austin: "make snapping points on all four corners of roads where you can draw fencing from each snapping point
        // when you zoom out of the map a little bit". The snap points are the original game's fence posts (Data road
        // graph: a post on every curb corner around every crossing in the city, a 4-tile grid in the wild). Tap a post,
        // then another: the sections between them (Data.RgPath, along the curbs and across the road at crossings) join
        // the chain, and the second post becomes the next start. UNDO / CLEAR, CONFIRM builds the chain with the
        // original build_fence_segs (original costs and hp). Posts show while the camera is within POST_MAX_DIST.
        public const float POST_MAX_DIST = 115f;
        public readonly List<int> FenceChain = new List<int>();
        public List<bool> FenceOk { get; private set; } = new List<bool>();
        readonly List<int> _fenceHist = new List<int>();
        public int FenceStart { get; private set; } = -1;
        public string FenceHint { get; private set; } = "";
        readonly Dictionary<int, bool> _postOk = new Dictionary<int, bool>();
        int _postOkVer = -1;
        GameObject _postsGo;
        string _postsKey = "";

        public bool PostsVisible => ActiveTool == Tool.Fence && _dist <= POST_MAX_DIST;

        void FenceToolStart()
        {
            FenceChain.Clear(); _fenceHist.Clear(); FenceOk = new List<bool>(); FenceStart = -1;
            _postOk.Clear(); _postsKey = "";
            FenceHint = "Tap a corner post to start the fence.";
            // posts read best a little zoomed out: a few blocks in view
            if (_distTarget < 40f) _distTarget = 44f;
        }

        void FenceToolReset()
        {
            FenceChain.Clear(); _fenceHist.Clear(); FenceOk = new List<bool>(); FenceStart = -1; FenceHint = "";
            if (_postsGo != null) { DestroyChunk(_postsGo); _postsGo = null; }
            _postsKey = "";
        }

        bool PostOk(int n)
        {
            var g = G;
            if (g?.S == null) return false;
            if (_postOkVer != g.S.FenceVer) { _postOk.Clear(); _postOkVer = g.S.FenceVer; }
            if (_postOk.TryGetValue(n, out bool v)) return v;
            var p = D.RgPos(n);
            v = g.InTerritory(new Vector2Int((int)p.x, (int)p.y), g.CurCol);
            _postOk[n] = v;
            return v;
        }

        static float PostScale(float dist) => Mathf.Clamp(dist / 26f, 0.8f, 4.2f);

        void UpdatePosts()
        {
            var g = G;
            if (!PostsVisible || g?.S == null || _gen == null)
            {
                if (_postsGo != null) { DestroyChunk(_postsGo); _postsGo = null; }
                _postsKey = "";
                return;
            }
            var f = new Vector2(_focus.x, -_focus.z);
            float R = Mathf.Clamp(_dist * 1.25f, 22f, 130f);
            const float Q = 4f;
            string key = Mathf.RoundToInt(f.x / Q) + "," + Mathf.RoundToInt(f.y / Q) + "," + Mathf.RoundToInt(_dist / 5f) + "," + FenceStart + "," + FenceChain.Count + "," + (FenceChain.Count > 0 ? FenceChain[FenceChain.Count - 1] : -1)
                + "," + WallTier + "," + g.S.FenceVer + "," + (FenceOk.Count > 0 && FenceOk[FenceOk.Count - 1] ? 1 : 0);
            if (key == _postsKey && _postsGo != null) return;
            _postsKey = key;
            var nodes = new List<int>(); var ok = new List<bool>();
            foreach (int n in D.RgNodesIn(new Rect(f.x - R, f.y - R, R * 2f, R * 2f), 2500))
            {
                if (D.RgAdj[n].Count == 0) continue;
                nodes.Add(n); ok.Add(PostOk(n));
            }
            if (_postsGo != null) DestroyChunk(_postsGo);
            var ch = _gen.FencePosts(nodes, ok, FenceStart, FenceChain, FenceOk, PostScale(_dist), WallTier);
            _postsGo = new GameObject("Fence posts");
            _postsGo.transform.SetParent(transform, false);
            if (ch.Mesh.Count > 0) MakeObject("Posts", ToMesh("Posts", ch.Mesh), _objMat, _postsGo.transform);
            if (_glowMat != null && ch.Shadow.Count > 0) MakeObject("Post glow", ToMesh("Post glow", ch.Shadow), _glowMat, _postsGo.transform);
        }

        /// <summary>The corner post under the finger: nearest on screen within ~a fingertip, among posts near the ground hit.</summary>
        int PostAt(Vector2 screen, Vector2 gp)
        {
            if (gp.x < 0f) return -1;
            float R = Mathf.Max(2.5f, _dist * 0.09f);
            var tap = new Vector2(screen.x, UnityEngine.Screen.height - screen.y);
            float lim = Mathf.Max(52f, (UnityEngine.Screen.dpi > 0f ? UnityEngine.Screen.dpi : 320f) * 0.32f);
            int best = -1; float bd = lim;
            foreach (int n in D.RgNodesIn(new Rect(gp.x - R, gp.y - R, R * 2f, R * 2f), 600))
            {
                if (D.RgAdj[n].Count == 0) continue;
                if (!TryScreen(Geo.W(D.RgPos(n), 0.3f), out var sp)) continue;
                float dd = (sp - tap).magnitude;
                if (dd < bd) { bd = dd; best = n; }
            }
            if (best < 0) return -1;
            int twin = D.SnapPost(D.RgPos(best), 0.5f);   // a seam post next to a city corner is the same corner
            return twin >= 0 ? twin : best;
        }

        void FenceTap(Vector2 screen, Vector2 gp)
        {
            var bs = Bootstrap.Instance;
            if (!PostsVisible) { bs?.Toast("Zoom in a little to see the corner posts."); return; }
            int n = PostAt(screen, gp);
            if (n < 0) { bs?.Toast("Tap one of the glowing corner posts."); return; }
            if (FenceStart < 0) { FenceStart = n; FenceHint = "Now tap another corner post. The fence follows the curb between them."; }
            else if (n == FenceStart) { FenceStart = -1; FenceHint = FenceChain.Count > 0 ? "Tap a corner post to carry on, or CONFIRM." : "Tap a corner post to start the fence."; }
            else
            {
                var path = D.RgPath(FenceStart, n);
                if (path.Count == 0) FenceHint = "Those posts are too far apart. Tap posts closer together.";
                else
                {
                    var add = new List<int>();
                    foreach (int e in path) if (!FenceChain.Contains(e) && !add.Contains(e)) add.Add(e);
                    if (FenceChain.Count + add.Count > Game.SEG_MAX_EDGES) FenceHint = string.Format("That's as long as one fence goes ({0} sections). CONFIRM it first.", Game.SEG_MAX_EDGES);
                    else if (add.Count > 0) { _fenceHist.Add(FenceChain.Count); FenceChain.AddRange(add); FenceHint = "Keep tapping posts to carry the fence on. Close the loop back at your first post."; }
                }
                FenceStart = n;
            }
            bs?.PlayClick();
            _postsKey = ""; _ghostSig = "";
            RefreshGhost();
        }

        public bool CanUndoFence => _fenceHist.Count > 0 || FenceStart >= 0;

        public void FenceUndo()
        {
            if (_fenceHist.Count == 0) { FenceStart = -1; _postsKey = ""; RefreshGhost(); return; }
            int n = _fenceHist[_fenceHist.Count - 1];
            _fenceHist.RemoveAt(_fenceHist.Count - 1);
            // the start goes back to the post where the removed stretch began (original world_view.gd fence_undo)
            if (n > 0)
            {
                int last = FenceChain[n - 1];
                int first = FenceChain[n];
                int sa = D.RgEa[first], sb = D.RgEb[first];
                FenceStart = sa == D.RgEa[last] || sa == D.RgEb[last] ? sa : sb == D.RgEa[last] || sb == D.RgEb[last] ? sb : D.RgEb[last];
            }
            else FenceStart = -1;
            FenceChain.RemoveRange(n, FenceChain.Count - n);
            _postsKey = ""; _ghostSig = "";
            RefreshGhost();
        }

        public void FenceClear()
        {
            FenceChain.Clear(); _fenceHist.Clear(); FenceOk = new List<bool>(); FenceStart = -1;
            FenceHint = "Tap a corner post to start the fence.";
            _postsKey = ""; _ghostSig = "";
            RefreshGhost();
        }

        void FenceGhost(List<int> tiles, List<int> state)
        {
            var g = G;
            var pv = g.SegPreview(FenceChain, WallTier);
            FenceOk = pv.Ok;
            var seen = new HashSet<int>();
            for (int i = 0; i < FenceChain.Count; i++)
                foreach (int k in g.EdgeTiles(FenceChain[i]))
                {
                    if (!D.Walkable(new Vector2Int(k % D.W, k / D.W)) || !seen.Add(k)) continue;
                    tiles.Add(k);
                    state.Add(g.S.Fences.TryGetValue(k, out var f0) && f0.Col == g.CurCol && f0.Tier >= WallTier ? 3 : i < pv.Ok.Count && pv.Ok[i] ? 0 : 2);
                }
            bool unlocked = g.FenceTierUnlocked(WallTier);
            ToolErr = FenceChain.Count == 0 ? "" : !unlocked ? string.Format("Research {0} first.", g.UnlockSourceName(Data.FENCES[WallTier].Unlock))
                : pv.N == 0 ? (pv.Err != "" ? pv.Err : "Already fenced.")
                : !g.CanAfford(pv.Cost) ? "Need " + Game.FmtCost(pv.Cost) + "." : pv.Err != "" ? "Some sections are skipped: " + pv.Err : "";
            ToolOk = FenceChain.Count > 0 && pv.N > 0 && unlocked && g.CanAfford(pv.Cost);
            ToolCost = pv.N > 0 ? Game.FmtCost(pv.Cost) : "-";
            ToolInfo = FenceChain.Count == 0 ? string.Format("{0} fence: tap corner posts", Data.FENCES[WallTier].Name) : string.Format("{0} fence · {1} section{2} · {3} tiles to build", Data.FENCES[WallTier].Name, pv.Edges, pv.Edges == 1 ? "" : "s", pv.N);
        }

        string FenceConfirm()
        {
            var bs = Bootstrap.Instance;
            if (FenceChain.Count == 0) return "Tap two corner posts first.";
            string e = bs.Act("build_fence_segs", new List<int>(FenceChain), WallTier);
            if (e == "")
            {
                // keep the tool and the last post: the next tap carries the fence on from where it ended
                FenceChain.Clear(); _fenceHist.Clear(); FenceOk = new List<bool>();
                FenceHint = "Built. Carry on from the amber post, or put a GATE in it so your people can get in and out.";
                _postsKey = "";
            }
            return e;
        }
    }
}
