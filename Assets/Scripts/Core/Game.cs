// Port of scripts/core/game.gd (Godot autoload "G", extends Node) - core: state, colonies, clock,
// start of a run, logging, resources and the per-frame / per-hour drivers.
//
// Engine-agnostic: Game is a plain C# class. Drive it with Process(deltaSeconds) from a
// MonoBehaviour (or Step/TickHour directly in tests). Signals became C# events; everything
// that needed the scene tree, files or the network goes through ISaveStore / INet / events.
//
// GDScript kept the current colony's sub-dictionaries aliased into S (S.res, S.people...).
// Here they live on Colony and are reached through C (= S.Cols[CurCol]); _use(ci) just
// switches CurCol. COL_VALS (morale, next_*_t, won_shown) are read/written on C directly,
// which is what the GDScript copy-in/copy-out amounted to.
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DayZero.Core
{
    public sealed partial class Game
    {
        // ---------------- signals ----------------
        public event Action Changed;
        public event Action<LogEntry> LogAdded;
        public event Action<JObject> DecisionAdded;
        public event Action<string> PhaseChanged;
        public event Action<bool> GameOver;
        public event Action<string, Vector2> SfxRequested;
        public event Action HourTicked;
        public event Action<int, bool> ColonyOver;

        void EmitChanged() => Changed?.Invoke();
        void PlaySfx(string name, Vector2 at) => SfxRequested?.Invoke(name, at);
        void EmitDecision(JObject ev) => DecisionAdded?.Invoke(ev);

        // ---------------- hosts ----------------
        /// <summary>Multiplayer layer (net.gd). Null in single player.</summary>
        public INet Net;
        /// <summary>Where saves go (user://). Defaults to files under SaveRoot.</summary>
        public ISaveStore Store;

        // ---------------- constants ----------------
        public const string SAVE_PATH = "dayzero_v4.json";
        public const string MP_SAVE_PATH = "dayzero_v4_mp.json";
        public const int SAVE_VERSION = 18;   // 18: expansion weather/seasons/district danger (17 still loads)
        /// <summary>Format tag written into C# saves (they are JSON, not Godot store_var).</summary>
        public const string SAVE_FORMAT = "dayzero-unity-json-1";
        public const int MAX_COLONIES = 11;
        public static readonly Color[] COL_COLORS =
        {
            new Color(0.25f, 0.85f, 0.35f, 1), new Color(1.0f, 0.62f, 0.15f, 1), new Color(0.75f, 0.45f, 1.0f, 1), new Color(0.3f, 0.85f, 1.0f, 1),
            new Color(1.0f, 0.35f, 0.55f, 1), new Color(0.95f, 0.9f, 0.25f, 1), new Color(0.2f, 0.55f, 1.0f, 1), new Color(0.6f, 1.0f, 0.75f, 1),
            new Color(1.0f, 0.55f, 0.85f, 1), new Color(0.75f, 0.55f, 0.3f, 1), new Color(0.92f, 0.92f, 0.95f, 1),
        };
        public const double ACTIVE_R = 46.0;
        public const int FLOW_R = 80;
        public const double JUMP_HOURS = 44.0;
        public const double FALL_SHARE = 0.99;
        public const double SEC_PER_HOUR = 50.0;
        public const double DAY_REAL_SEC = 900.0;
        public const double NIGHT_REAL_SEC = 300.0;
        public const int SECT = 8;
        public const double SAFE_R = 7.0;
        public const int OUTPOST_MAX = 6;
        public const double HOLD_HOURS = 240.0;
        public const double MOVE_MULT = 2.0;
        public const double HUMAN_MOVE_MULT = 12.0;
        public const double HUMAN_SPEED = 6.0 * HUMAN_MOVE_MULT;
        public const double Z_SPEED = 2.4 * MOVE_MULT;
        public const double Z_FAST = 4.6 * MOVE_MULT;
        public const double GUARD_RANGE = 5.0;
        public static readonly Vector2Int[] DIRS = { new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1) };
        public static readonly string[] DIFF_NAMES = { "Easy", "Normal", "Hard" };
        public static readonly double[] DIFF_SPREAD = { 0.7, 1.0, 1.4 };
        public const double WILD_Z = 0.35;
        public const double WILD_SPREAD = 0.45;
        public const int NO_BLD = Person.NO_BLD;
        public const string NO_BEDS_MSG = "No free beds anywhere — convert more housing.";

        // ---------------- state ----------------
        public GameState S;
        public bool Running;
        public double TestSpeed = 1.0;
        public GdRng Rng = new GdRng();
        public GdRng Frng = new GdRng();
        public GdRng Wrng = new GdRng();
        /// <summary>Godot's global RNG (Math::rand): Array.shuffle() draws from it, not from rng. Godot randomizes it at
        /// startup; tests seed it (seed(n)) to reproduce a run.</summary>
        public GdRng GRng = new GdRng((ulong)DateTime.UtcNow.Ticks);
        public int MaxZombies = 1100;
        public int CurCol = -1;
        public int ViewCol = 0;
        public int LogCol = -1;
        public string Actor = "";
        public bool IsClient;
        public bool Mp;
        public bool AutosaveOn = true;
        /// <summary>GDScript decided this from the running scene (res://tests/ -> 2 s per hour).
        /// Set true for tests / fast-forward; false uses the day/night real-time clock.</summary>
        public bool FastClock;
        public byte[] Active = new byte[0];

        // zombies: current tile, next tile, progress, mode (0 wander, 1 horde, 3 night raid, 255 dead)
        public List<int> Zc = new List<int>(), Zn = new List<int>();
        public List<float> Zp = new List<float>();
        public List<byte> Zh = new List<byte>();

        public int SW, SH;
        public byte[] SectZone;
        public float[] SectDens, SectLand, SectWild;
        public byte[] SectProt = new byte[0];
        public byte[] Safe;
        public int[] Flow;
        public AStarGrid Astar;
        float[] _wInf = new float[0];
        public int[] BucketHead;
        public List<int> BucketNext = new List<int>();
        double _accHour, _accCombat;
        readonly Dictionary<int, Person> _peopleById = new Dictionary<int, Person>();
        readonly Dictionary<int, int> _claimed = new Dictionary<int, int>();
        readonly Dictionary<int, List<(float d, int l)>> _baseLots = new Dictionary<int, List<(float d, int l)>>();
        bool _flowDirty = true;
        int _logN;

        static Data D => Data.Instance;

        /// <summary>The current colony (GDScript: the refs aliased into S by _use).</summary>
        public Colony C => (S != null && CurCol >= 0 && CurCol < S.Cols.Count) ? S.Cols[CurCol] : null;
        double R(string k) => C.Res.Get(k, 0.0);

        // ---------------- _ready ----------------
        public Game()
        {
            Forest.Register();
            SW = (int)Math.Ceiling(D.W / (double)SECT);
            SH = (int)Math.Ceiling(D.H / (double)SECT);
            SectLand = new float[SW * SH]; SectWild = new float[SW * SH];
            SectZone = new byte[SW * SH]; SectDens = new float[SW * SH];
            Astar = new AStarGrid(new RectInt(0, 0, D.W, D.H));
            Coarse = new AStarGrid(new RectInt(0, 0, SW, SH));
            WorldTiles(new RectInt(0, 0, D.W, D.H));
            BucketHead = new int[D.W * D.H];
            Array.Fill(BucketHead, -1);
            Safe = new byte[D.W * D.H];
            Flow = new int[D.W * D.H];
            D.WorldChanged += OnWorldChanged;
        }

        void WorldTiles(RectInt r)
        {
            var s0 = new Vector2Int(r.x / SECT, r.y / SECT);
            var s1 = new Vector2Int(Math.Min(SW - 1, (r.x + r.width - 1) / SECT), Math.Min(SH - 1, (r.y + r.height - 1) / SECT));
            int W = D.W;
            byte[] tl = D.Tiles;
            var cr = D.CityRect;
            int[] zl = new int[3];
            for (int sy = s0.y; sy <= s1.y; sy++)
                for (int sx = s0.x; sx <= s1.x; sx++)
                {
                    int n = 0, land = 0, wild = 0;
                    zl[0] = zl[1] = zl[2] = 0;
                    for (int y = sy * SECT; y < Math.Min(D.H, (sy + 1) * SECT); y++)
                        for (int x = sx * SECT; x < Math.Min(D.W, (sx + 1) * SECT); x++)
                        {
                            n++;
                            int t = tl[y * W + x];
                            bool open = t != Data.T_WATER && t != Data.T_BLD && t != Data.T_VOID;
                            var p = new Vector2Int(x, y);
                            if (open) { land++; zl[ZoneOf(p)]++; }
                            if (!Gd.Has(cr, p)) wild++;
                            Astar.SetSolid(p, !open);
                        }
                    SectLand[sy * SW + sx] = (float)(land / Math.Max(1.0, n));
                    SectWild[sy * SW + sx] = (float)(wild / Math.Max(1.0, n));
                    int z = 255;
                    if (land > 0) z = (zl[0] >= zl[1] && zl[0] >= zl[2]) ? 0 : (zl[1] >= zl[2] ? 1 : 2);
                    SectZone[sy * SW + sx] = (byte)z;
                    SectDens[sy * SW + sx] = z < 3 ? (float)(new[] { 1.0, D.REGIONS["small_town"].Zdens, D.REGIONS["farmland"].Zdens }[z]) : 0f;
                }
        }

        int ZoneOf(Vector2Int p)
        {
            if (p.y < D.H0) return 0;
            string r = D.RegionAt(p);
            return r == "small_town" ? 1 : (r == "farmland" ? 2 : 0);
        }

        void OnWorldChanged()
        {
            int W = D.W, H = D.H0, o = Data.OFF;
            foreach (var r in new[] { new RectInt(0, 0, W, o), new RectInt(0, H - o, W, o), new RectInt(0, o, o, H - 2 * o), new RectInt(W - o, o, o, H - 2 * o), new RectInt(0, H, W, D.H - H) })
                WorldTiles(r);
            _coarseReady = false;
            _pathCache.Clear(); _pathOrder.Clear();
            _baseLots.Clear();
            _factionLotsDirty = true;
        }

        public bool SectIsWild(int i) => i >= 0 && i < SectWild.Length && SectWild[i] > 0.5f;

        // ---------------- new run / colonies ----------------
        public void NewRun(int difficulty = 1, int seedValue = -1, int ncols = 1)
        {
            if (seedValue < 0) seedValue = (int)(new GdRng((ulong)DateTime.UtcNow.Ticks).Randi() & 2147483647);
            D.SetWorldSeed(seedValue);
            Rng.SetSeed(seedValue);
            Frng.SetSeed(seedValue ^ 24301);
            Wrng.SetSeed(seedValue ^ 14866);
            var loot = new float[D.Lots.Count]; Array.Fill(loot, 1f);
            S = new GameState
            {
                Ver = SAVE_VERSION, Seed = seedValue, T = 6.0, Phase = "pick", Difficulty = difficulty,
                Ctrl = new byte[SW * SH], CtrlH = new float[SW * SH], Scout = new float[SW * SH],
                Loot = loot, Zinf = new float[SW * SH], Zcorner = new Vector2Int(0, 0), Srch = SrchNew(), SrchC = SrchCNew(),
                FallT = -1.0, NextHordeT = 0.0, NextGroupT = 36.0, NextRaidT = 90.0, NextJumpT = 72.0,
                Mode = ncols > 1 ? "separate" : "solo", VehN = 1, VehVer = 1,
            };
            S.ExpRules = ExpansionRules;
            Zc.Clear(); Zn.Clear(); Zp.Clear(); Zh.Clear();
            _peopleById.Clear(); _claimed.Clear(); _baseLots.Clear(); _fxCache.Clear(); _encl.Clear(); _enclVer = -1; _claimVer = -1; _claimSig = 0;
            _seedCache.Clear(); _tileSegVer = -1;
            _factionLotsDirty = true;
            _convCacheVer = -1; _flowBuilt = false; _hourQ.Clear();
            CurCol = -1;
            EnsureRoutine(S);
            for (int i = 0; i < Math.Max(1, ncols); i++) AddColony("");
            Use(0);
            SpawnWrecks(WRECKS_NEW, 51829);
            if (ExpansionRules) EnsureLotWrecks();   // Expansion vehicles: parked / abandoned map cars are towable wrecks
            ViewCol = 0;
            _wInf = new float[SW * SH];
            ResetWeights();
            RebuildSafe();
            Active = new byte[SW * SH];
            RebuildBuckets();
            Running = false;
            EmitChanged();
        }

        Colony NewColonyDict(string cname)
        {
            int i = S.Cols.Count;
            var c = new Colony
            {
                Id = i, Name = cname != "" ? cname : string.Format("Colony {0}", i + 1),
                Res = StartRes(), Auto = AutoNew(), Kit = KitNew(),
                Morale = 70.0, NextArrivalT = S.T + 30.0, NextEventT = S.T + EVENT_FIRST_H,
                Nraid = RaidNew(), StartT = S.T, War = WarNew(),
            };
            foreach (var w in new[] { "pipe", "pistol", "hunting", "shotgun", "rifle" }) c.Weapons[w] = 0;
            foreach (var p in new[] { "battery", "tires", "alternator", "fuel_pump" }) c.Parts[p] = 0;
            foreach (var s in new[] { "kills", "dead", "rescued", "turned" }) c.Stats[s] = 0;
            return c;
        }

        static ODict<string, int> KitNew()
        {
            var k = new ODict<string, int>();
            foreach (var id in Data.GPARTS.Keys.Concat(Data.ARMOR.Keys)) k[id] = 0;
            return k;
        }

        public int AddColony(string cname)
        {
            if (S.Cols.Count >= MAX_COLONIES) return -1;
            S.Cols.Add(NewColonyDict(cname));
            return S.Cols.Count - 1;
        }

        public Colony ColonyAt(int ci) => (S == null || ci < 0 || ci >= S.Cols.Count) ? null : S.Cols[ci];
        public Color ColColor(int ci) => COL_COLORS[V.Clampi(ci, 0, COL_COLORS.Length - 1)];
        public static bool ColAlive(Colony c) => c != null && c.Started && !c.Lost && c.Merged < 0;

        public void Use(int ci)
        {
            if (S == null || ci < 0 || ci >= S.Cols.Count) return;
            CurCol = ci;
        }

        void EachCol(Action f)
        {
            for (int i = 0; i < S.Cols.Count; i++)
            {
                if (!ColAlive(S.Cols[i])) continue;
                Use(i);
                LogCol = i;
                f();
            }
            LogCol = -1;
            Use(ViewCol);
        }

        public List<Base> AllBases()
        {
            var o = new List<Base>();
            if (S == null) return o;
            foreach (var c in S.Cols)
            {
                if (c.Lost || c.Merged >= 0) continue;
                o.AddRange(c.Bases);
            }
            return o;
        }

        public Base BaseAny(int bid)
        {
            foreach (var c in S.Cols)
                foreach (var b in c.Bases)
                    if (b.Id == bid) return b;
            return null;
        }

        public List<Person> AllPeople()
        {
            var o = new List<Person>();
            foreach (var c in S.Cols) o.AddRange(c.People);
            return o;
        }

        public string PhaseOf(int ci)
        {
            var c = ColonyAt(ci);
            if (c == null) return S?.Phase ?? "pick";
            if (c.Lost) return "lost";
            if (!c.Started) return "pick";
            return S.Phase;
        }

        public string PlayerNameOfCol(int ci)
        {
            foreach (var kv in S.Players)
                if (kv.Value.Col == ci) return kv.Value.Name;
            return ColonyAt(ci)?.Name ?? "";
        }

        public string CanStartAt(Vector2Int tile, int ci) => StartError(tile, ci, false);

        /// <summary>v0.14.0a (Austin: "pick wherever you want to start"): the player's own pick. Any building the
        /// player can tap works unless another faction or colony holds it: no size limits, and a building with no
        /// door on the street gets the nearest open tile as its gate. The game's own suggestions (SuggestStart,
        /// AssignSpawns) keep the original rule, so hands-off runs start where they always did.</summary>
        public string CanStartAnywhere(Vector2Int tile, int ci) => StartError(tile, ci, true);

        string StartError(Vector2Int tile, int ci, bool anywhere)
        {
            tile = WildSite(tile, ci);
            var c = ColonyAt(ci);
            if (c == null) return "No such colony.";
            if (c.Started) return "Your camp is already set up.";
            string te = anywhere ? StartTakeError(tile, ci) : TakeError(tile, ci);
            if (te != "") return te;
            var p = anywhere ? StartDoor(tile) : TakeDoor(tile);
            foreach (var b in AllBases())
            {
                double d = V.F(b.Pos).DistTo(V.F(p));
                if (d < SAFE_R * 2.0 + 2.0) return "Too close to another camp's safe zone.";
                if (S.Mode == "separate" && d < 70.0) return "Too close to the other colony (70 tiles minimum in Separate mode).";
            }
            if (S.Phase != "pick" && InfAt(p) > 0.3) return "That part of the city is already overrun.";
            return "";
        }

        /// <summary>TakeError for the player's start pick: buildings only, never one another faction or colony holds.</summary>
        string StartTakeError(Vector2Int tile, int ci)
        {
            if (TakeError(tile, ci) == "") return "";
            int l = D.BuildingAt(tile);
            if (l < 0) return TakeError(tile, ci);
            var L = D.Lots[l];
            if (L.Kind == "parking") return "That's a parking lot. Tap a building.";
            var fo = FactionOfLot(l);
            if (fo != null) return string.Format("{0} holds that building.", fo.Name);
            var ob = LotOwnerBase(l);
            if (ob != null) return ob.Col == ci ? string.Format("That building is already part of {0}.", ob.Name) : string.Format("That building belongs to {0}.", ColonyAt(ob.Col)?.Name ?? "another colony");
            if (StartDoor(tile).x < 0) return "No open ground next to that building.";
            return "";
        }

        /// <summary>The camp's gate tile for a start pick: the building's street door, else the nearest open tile.</summary>
        Vector2Int StartDoor(Vector2Int tile)
        {
            var p = TakeDoor(tile);
            if (p.x >= 0) return p;
            int l = D.BuildingAt(tile);
            if (l < 0) return p;
            var lc = D.LotCenter(l);
            return D.NearestWalkable(new Vector2Int((int)lc.x, (int)lc.y), 14);
        }

        const int SPAWN_CELL = 16;
        List<(Vector2Int t, bool lm)> _spawnCands = new List<(Vector2Int, bool)>();
        int _spawnCandsSeed = -1;

        List<(Vector2Int t, bool lm)> SpawnCandidates()
        {
            int sd = S.Seed;
            if (_spawnCandsSeed == sd && _spawnCands.Count > 0) return _spawnCands;
            _spawnCandsSeed = sd;
            _spawnCands = new List<(Vector2Int, bool)>();
            var seen = new Dictionary<Vector2Int, int>();
            var lm = new HashSet<int>(D.Landmarks);
            for (int l = 0; l < D.Lots.Count; l++)
            {
                var L = D.Lots[l];
                var c = new Vector2Int(L.X + L.W / 2, L.Y + L.H / 2);
                var cell = new Vector2Int(c.x / SPAWN_CELL, c.y / SPAWN_CELL);
                bool isLm = lm.Contains(l);
                if (seen.ContainsKey(cell) && !isLm) continue;
                if (c.x < 12 || c.y < 12 || c.x > D.W - 12 || c.y > D.H0 - 12) continue;
                if (D.BuildingAt(c) != l || D.LotTakeable(l) != "") continue;
                if (seen.TryGetValue(cell, out int si)) { _spawnCands[si] = (c, true); continue; }
                seen[cell] = _spawnCands.Count;
                _spawnCands.Add((c, isLm));
            }
            return _spawnCands;
        }

        public List<Vector2Int> SuggestSpawns(int n, int ci = 0)
        {
            var cands = SpawnCandidates();
            var chosen = new List<Vector2Int>();
            var pts = new List<Vector2>();
            foreach (var b in AllBases()) pts.Add(V.F(b.Pos));
            for (int i = 0; i < n; i++)
            {
                int best = -1; double bs = -1.0;
                for (int j = 0; j < cands.Count; j++)
                {
                    var t = cands[j].t;
                    double md = 1000000000.0;
                    if (pts.Count == 0) md = 2000.0 - V.F(t).DistTo(V.F(Gd.Center(D.CityRect)));
                    else
                    {
                        foreach (var q in pts) md = Math.Min(md, V.F(t).Dist2To(q));
                        md = Math.Sqrt(md);
                    }
                    if (cands[j].lm) md += 8.0;
                    if (md > bs && TakeError(t, ci) == "") { bs = md; best = j; }
                }
                if (best < 0) break;
                chosen.Add(cands[best].t);
                pts.Add(V.F(cands[best].t));
            }
            return chosen;
        }

        public void AssignSpawns()
        {
            var need = S.Cols.Where(c => !c.Started && c.Merged < 0).ToList();
            if (need.Count == 0) return;
            var sp = SuggestSpawns(need.Count, need[0].Id);
            for (int i = 0; i < need.Count; i++)
                if (i < sp.Count) need[i].Spawn = sp[i];
        }

        public Vector2Int SuggestStart(int ci)
        {
            var c = ColonyAt(ci);
            if (c == null || c.Started) return V.NONE;
            Vector2Int? sp = c.Spawn;
            if (sp.HasValue && CanStartAt(sp.Value, ci) == "") return sp.Value;
            var best = V.NONE; double bd = 1e18;
            var others = new List<Vector2>();
            foreach (var oc in S.Cols)
                if (oc.Id != ci && !oc.Started && oc.Spawn.HasValue) others.Add(V.F(oc.Spawn.Value));
            foreach (var cd in SpawnCandidates())
            {
                var t = cd.t;
                double d = sp.HasValue ? V.F(t).Dist2To(V.F(sp.Value)) : 0.0;
                if (!sp.HasValue)
                {
                    double md = 1e18;
                    foreach (var b in AllBases()) md = Math.Min(md, V.F(t).Dist2To(V.F(b.Pos)));
                    d = -md;
                }
                if (d >= bd) continue;
                bool clash = false;
                foreach (var q in others)
                    if (V.F(t).DistTo(q) < 40.0) { clash = true; break; }
                if (clash || CanStartAt(t, ci) != "") continue;
                bd = d; best = t;
            }
            if (best.x >= 0) c.Spawn = best;
            return best;
        }

        public string StartAt(Vector2Int tile)
        {
            int ci = CurCol;
            tile = WildSite(tile, ci);
            string e = CanStartAnywhere(tile, ci);   // v0.14.0a: the player's pick may be any building (see CanStartAnywhere)
            if (e != "") return e;
            var p = StartDoor(tile);
            int lot = D.BuildingAt(tile);
            bool first = true;
            foreach (var c in S.Cols) if (c.Started) first = false;
            var col = S.Cols[ci];
            col.Started = true;
            col.StartT = S.T;
            var b = AddBase(p, true, lot);
            b.Done = 1.0;
            if (PhaseARules) FoundPalisade(b);   // v0.12.0a (dormant): the drawn palisade is a real (wood, breakable) wall
            if (first)
            {
                var corners = new List<Vector2Int>();
                var cr = D.CityRect;
                foreach (var cc in new[] { new Vector2Int(cr.x + 4, cr.y + 4), new Vector2Int(cr.x + cr.width - 5, cr.y + 4), new Vector2Int(cr.x + 4, cr.y + cr.height - 5), new Vector2Int(cr.x + cr.width - 5, cr.y + cr.height - 5) })
                    corners.Add(LandNear(cc));
                GRng.Shuffle(corners);
                var pick = corners[0];
                foreach (var c in corners)
                    if (V.F(c).DistTo(V.F(p)) >= 200.0) { pick = c; break; }
                S.Zcorner = pick;
                var cs = new Vector2Int(V.Clampi(pick.x / SECT, 0, SW - 1), V.Clampi(pick.y / SECT, 0, SH - 1));
                for (int dy = -1; dy < 2; dy++)
                    for (int dx = -1; dx < 2; dx++)
                    {
                        var q = cs.Add(dx, dy);
                        if (q.x >= 0 && q.y >= 0 && q.x < SW && q.y < SH)
                            S.Zinf[q.y * SW + q.x] = (dx == 0 && dy == 0) ? 0.75f : 0.35f;
                    }
                SeedBandOutbreaks(S.Zinf);
            }
            var crew = new List<string> { "Civilian", "Civilian", "Scavenger", "Scavenger", "Soldier", "Medic", "Engineer", "Farmer" };
            int lm = D.LandmarkNear(p);
            if (lm >= 0)
            {
                string site = D.Lots[lm].Landmark;
                if (D.Sites[site] is JObject sd)
                {
                    foreach (var r in (JArray)sd["crew"]) crew.Add(MapRole((string)r));
                    foreach (var k in Data.RESOURCES)
                        if (sd[k] != null) C.Res[k] = R(k) + (double)sd[k] * 0.5;
                    if (sd["materials"] != null)
                        AddMats((double)sd["materials"] * 0.5, Data.LOOT_MIX.TryGetValue(D.LotKind(lm), out var mix) ? mix : Data.MAT_MIX);
                }
            }
            foreach (var r in crew)
            {
                var np = AddPerson(r, b.Id, V.F(p));
                if (np.Job == "Soldier" || np.Job == "Cop") { C.Weapons.AddTo("pistol", 1); C.Kit.AddTo("soft_vest", 1); }
            }
            if (b.Site == "police")
            {
                C.Weapons.AddTo("pistol", 2); C.Weapons.AddTo("shotgun", 1); C.Kit.AddTo("soft_vest", 2); C.Kit.AddTo("helmet", 1);
            }
            Equip();
            RebuildSafe();
            for (int i = 0; i < (first ? 40 : 12); i++) SpawnGroup(true, p);
            if (first) PlaceCamps(p);
            S.Phase = "spread";
            RebuildSafe();
            UpdateActive();
            SpawnDotsHourly();
            RebuildBuckets();
            int prevLc = LogCol;
            LogCol = ci;
            if (Mp) AddLog(string.Format("{0} took over {1} in {2}. The outbreak started in the {3} corner of the city.", col.Name, TakeName(lot), D.DistrictAt(p).Name, CornerName()), "major");
            else AddLog(string.Format("You set up camp in {0}, {1}. The outbreak started in the {2} corner of the city.", TakeName(lot), D.DistrictAt(p).Name, CornerName()), "major");
            if (first) AddLog("Hordes won't come for your camps until the whole city has fallen, but from the second night on the dead probe your walls after dark, more of them every night. Use the days to expand, fence in and post guards.", "info");
            LogCol = prevLc;
            Running = true;
            PhaseChanged?.Invoke(S.Phase);
            EmitChanged();
            return "";
        }

        Vector2Int LandNear(Vector2Int c)
        {
            var best = c; double bd = 1000000000.0;
            for (int sy = 0; sy < SH; sy++)
                for (int sx = 0; sx < SW; sx++)
                {
                    if (SectLand[sy * SW + sx] < 0.35f || SectIsWild(sy * SW + sx)) continue;
                    var q = new Vector2Int(sx * SECT + SECT / 2, sy * SECT + SECT / 2);
                    double d = V.F(q).DistTo(V.F(c));
                    if (d < bd) { bd = d; best = q; }
                }
            return best;
        }

        string MapRole(string r)
        {
            if (r == "Soldier" && Rng.Randf() < 0.4f) return "Cop";
            return Data.JOBS.Contains(r) ? r : "Civilian";
        }

        public string CornerName()
        {
            var c = S.Zcorner;
            return (c.y < D.H0 / 2 ? "north" : "south") + (c.x < D.W / 2 ? "west" : "east");
        }

        // ---------------- clock & lookups ----------------
        public int Day() => (int)(S.T / 24.0) + 1;
        public int Hour() => (int)S.T % 24;
        public string ClockText() => string.Format("Day {0}  {1:00}:{2:00}", Day(), Hour(), (int)((S.T % 1.0) * 60.0) / 10 * 10);
        public bool IsNight() => Hour() >= 21 || Hour() < 5;
        public int Ti(Vector2Int p) => p.y * D.W + p.x;
        public Vector2Int Tp(int k) => new Vector2Int(k % D.W, k / D.W);
        public int SectOf(Vector2Int p) => V.Clampi(p.y / SECT, 0, SH - 1) * SW + V.Clampi(p.x / SECT, 0, SW - 1);
        public double InfAt(Vector2Int p) => S.Zinf[SectOf(p)];
        public int ZombieCount() => Zc.Count;
        public List<Person> AlivePeople() => C.People.Where(p => p.St != "dead").ToList();
        public Person PersonById(int pid) => _peopleById.TryGetValue(pid, out var p) ? p : null;
        public Base BaseById(int bid)
        {
            if (C == null) return null;
            foreach (var b in C.Bases) if (b.Id == bid) return b;
            return null;
        }
        public Base MainBase()
        {
            if (C == null) return null;
            foreach (var b in C.Bases) if (b.Main) return b;
            return null;
        }
        public List<Person> BasePeople(int bid) => C.People.Where(p => p.St != "dead" && p.Base == bid).ToList();
        public List<Person> AtBase(int bid) => C.People.Where(p => p.St == "base" && p.Base == bid).ToList();
        public int CountJob(string job) => C.People.Count(p => p.St != "dead" && p.Job == job);
        public int Outposts() => C.Bases.Count(b => !b.Main);
        public double SpreadPct()
        {
            double tot = 0, n = 0;
            for (int i = 0; i < SW * SH; i++)
                if (SectLand[i] > 0.2f && SectProt[i] == 0 && SectWild[i] < 0.5f) { tot += Math.Min(1.0, S.Zinf[i] / 0.9); n += 1; }
            return tot / Math.Max(1.0, n);
        }

        public void AddLog(string text, string kind = "info", string rid = null)
        {
            _logN++;
            var e = new LogEntry { T = S?.T ?? 0.0, Text = text, Kind = kind, Col = LogCol, N = _logN, Rid = rid };
            if (Mp && Actor != "" && LogCol >= 0 && !text.StartsWith(Actor)) e.Text = string.Format("[{0}] {1}", Actor, text);
            S.Log.Add(e);
            if (S.Log.Count > 80) S.Log.RemoveAt(0);
            LogAdded?.Invoke(e);
        }

        int NewId()
        {
            int i = S.NextId;
            S.NextId = i + 1;
            return i;
        }

        Person AddPerson(string job, int bid, Vector2 pos, string pname = "")
        {
            var p = new Person
            {
                Id = NewId(), Name = pname != "" ? pname : D.RandomName(Rng), Job = job, Base = bid,
                Pos = pos, Col = CurCol, Inb = NO_BLD, Home = NO_BLD, Work = "", Wl = -1,
            };
            FamInit(p);
            C.People.Add(p);
            _peopleById[p.Id] = p;
            _rtNeed[CurCol] = true;
            if (ExpansionRules) RollTraits(p);   // Expansion: people
            return p;
        }

        Base AddBase(Vector2Int p, bool isMain, int lot = -1)
        {
            var up = new ODict<string, int>();
            foreach (var k in Data.UPGRADES.Keys) up[k] = 0;
            var b = new Base
            {
                Id = NewId(), Pos = p, Main = isMain, Done = 1.0, Veh = 0, Up = up, Col = CurCol, Lvl = 0,
                Name = isMain ? "Main Camp" : "Outpost " + new[] { "A", "B", "C", "D", "E", "F", "G", "H" }[Math.Min(7, Outposts())],
                Lot = lot, Claim = lot >= 0 ? new List<int> { lot } : new List<int>(),
            };
            if (lot >= 0) S.Loot[lot] = 0f;
            int lm = D.LandmarkNear(p);
            if (lot >= 0 && D.Lots[lot].Landmark != "") lm = lot;
            b.Site = lm >= 0 ? D.Lots[lm].Landmark : "";
            b.GarageOk = b.Site == "police" || b.Site == "warehouse" || NearKind(p, Data.GARAGE_KINDS, 7.0);
            StartingFacilities(b);
            C.Bases.Add(b);
            _baseLots.Remove(b.Id);
            _flowDirty = true;
            return b;
        }

        public Base LotOwnerBase(int l)
        {
            if (l < 0) return null;
            foreach (var b in AllBases())
                if (b.Claim.Contains(l)) return b;
            return null;
        }

        public string TakeName(int l)
        {
            if (l < 0) return "a building";
            string nm = D.LotName(l);
            switch (D.Lots[l].Kind)
            {
                case "house": return "a house";
                case "apartment": return "an apartment block";
                case "office": return "an office building";
                case "factory": return "a factory";
            }
            return nm;
        }

        public string TakeError(Vector2Int tile, int ci)
        {
            int l = D.BuildingAt(tile);
            if (l < 0)
            {
                if (!D.InMap(tile) || D.Tile(tile) == Data.T_WATER) return "That's water. Tap a building to take it over.";
                if (D.IsWild(tile)) return "No cabin, homestead or farm close enough here. Tap near a building in the woods.";
                if (D.Tile(tile) == Data.T_PARK) return "That's a park. Tap a building to take it over.";
                if (D.LotAt(tile) >= 0) return "That's open ground. Tap a building to take it over.";
                return "That's a street. Tap a building to take it over.";
            }
            string e = D.LotTakeable(l);
            if (e != "") return e;
            var fo = FactionOfLot(l);
            if (fo != null) return string.Format("{0} holds that building.", fo.Name);
            var ob = LotOwnerBase(l);
            if (ob != null)
            {
                if (ob.Col == ci) return string.Format("That building is already part of {0}.", ob.Name);
                return string.Format("That building belongs to {0}.", ColonyAt(ob.Col)?.Name ?? "another colony");
            }
            if (TakeDoor(tile).x < 0) return "No way into that building from the street.";
            return "";
        }

        const int WILD_SNAP = 14;
        public Vector2Int WildSite(Vector2Int tile, int ci = -1)
        {
            if (ci < 0) ci = CurCol;
            if (!D.IsWild(tile) || D.BuildingAt(tile) >= 0) return tile;
            int t = D.Tile(tile);
            if (t == Data.T_WATER) return tile;
            var best = V.NONE; double bd = 1000000000.0;
            for (int dy = -WILD_SNAP; dy < WILD_SNAP + 1; dy++)
                for (int dx = -WILD_SNAP; dx < WILD_SNAP + 1; dx++)
                {
                    var q = tile.Add(dx, dy);
                    int l = D.BuildingAt(q);
                    if (l < 0 || l < D.CityLots) continue;
                    double d = dx * dx + dy * dy;
                    if (d >= bd || d > WILD_SNAP * WILD_SNAP) continue;
                    if (TakeError(q, ci) != "") continue;
                    bd = d; best = q;
                }
            return best.x >= 0 ? best : tile;
        }

        public Vector2Int TakeDoor(Vector2Int tile)
        {
            int l = D.BuildingAt(tile);
            if (l < 0) return D.NearestWalkable(tile);
            return D.NearestWalkable(D.LotDoor(l), 4);
        }

        public Vector2Int BuildingTileNear(Vector2Int p, int r = 12, int ci = -1)
        {
            if (ci < 0) ci = CurCol;
            for (int rr = 0; rr < r + 1; rr++)
                for (int dy = -rr; dy < rr + 1; dy++)
                    for (int dx = -rr; dx < rr + 1; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != rr) continue;
                        var q = p.Add(dx, dy);
                        if (TakeError(q, ci) == "") return q;
                    }
            return V.NONE;
        }

        public int ClaimTarget(Base b)
        {
            int nfac = 0;
            foreach (var kv in b.Up) if (kv.Value > 0) nfac++;
            int l = b.Lvl;
            return b.Main ? (1 + 2 * l + nfac / 2) : (1 + l + nfac / 3);
        }

        void GrowClaim(Base b)
        {
            if (b.Lot < 0) return;
            var claim = b.Claim;
            int want = ClaimTarget(b);
            if (claim.Count >= want) return;
            var owned = new HashSet<int>();
            foreach (var ob in AllBases()) foreach (int l in ob.Claim) owned.Add(l);
            var c0 = D.LotCenter(b.Lot);
            int r = (int)(BaseZoneR(b) + 3.0);
            var cand = new ODict<int, float>();
            for (int dy = -r; dy < r + 1; dy++)
                for (int dx = -r; dx < r + 1; dx++)
                {
                    var q = new Vector2Int((int)c0.x + dx, (int)c0.y + dy);
                    int l = D.BuildingAt(q);
                    if (l < 0 || owned.Contains(l) || cand.Has(l)) continue;
                    if (D.LotTakeable(l) != "" || _factionLots.ContainsKey(l) || S.Conv.Has(l)) continue;
                    cand[l] = D.LotCenter(l).DistTo(c0);
                }
            var arr = cand.KeyList();
            Gd.StableSort(arr, (x, y) => cand[x].CompareTo(cand[y]));
            foreach (int l in arr)
            {
                if (claim.Count >= want) break;
                if (cand[l] > BaseZoneR(b) + 3.0) break;
                claim.Add(l);
                S.Loot[l] = 0f;
            }
            _seedCache.Remove(b.Id);
        }

        bool NearKind(Vector2Int p, IList<string> kinds, double r)
        {
            for (int dy = -(int)r; dy < (int)r + 1; dy++)
                for (int dx = -(int)r; dx < (int)r + 1; dx++)
                {
                    var q = p.Add(dx, dy);
                    int l = D.LotAt(q);
                    if (l >= 0 && kinds.Contains(D.LotKind(l)) && V.Len(dx, dy) <= r) return true;
                }
            return false;
        }

        void StartingFacilities(Base b)
        {
            var p = b.Pos;
            switch (b.Site)
            {
                case "hospital": b.Up["medbay"] = 1; break;
                case "police": b.Up["armory"] = 1; break;
                case "warehouse": b.Up["garage"] = 1; break;
                case "farmhouse": b.Up["garden"] = 1; b.Up["water"] = 1; break;
                case "high_school": b.Up["walls"] = 1; break;
            }
            if (b.U("garage") == 0 && NearKind(p, new[] { "gas_station" }, 6.0)) b.Up["garage"] = 1;
            if (b.U("medbay") == 0 && NearKind(p, new[] { "clinic", "pharmacy" }, 4.0)) b.Up["medbay"] = 1;
            if (b.U("armory") == 0 && NearKind(p, new[] { "gun_shop", "checkpoint" }, 4.0)) b.Up["armory"] = 1;
        }

        public string BaseLabel(Base b)
        {
            string s = b.Name;
            if (b.Site != "" && D.Sites[b.Site] != null) s += " (" + (string)D.Sites[b.Site]["short"] + ")";
            return s;
        }

        public static string SiteBonusText(string site) => RateText(SiteBonusText0(site));
        static string SiteBonusText0(string site)
        {
            switch (site)
            {
                case "hospital": return "Hospital: starts with a Med Bay, +1 meds/day";
                case "police": return "Precinct: starts with an Armory, +1.5 ammo/day, +defense";
                case "high_school": return "School: starts with Walls, +2 food/day from the cafeteria";
                case "warehouse": return "Warehouse: starts with a Garage, +2 materials/day";
                case "farmhouse": return "Farm: starts with a Garden + Water Collectors, +6 food/day";
            }
            return "";
        }

        public double Defense(Base b, bool withAlly = true)
        {
            double d = 10.0 + 12.0 * b.U("walls") + BaseLevelDef(b).Defense;
            if (b.Site == "police" || b.Site == "high_school") d += 8.0;
            d += Rx("defense_add");
            if (IsNight()) d += Rx("night_def");
            d += EnclosureDefense(b);
            d += 4.0 * TowersOfBase(b.Id).Count;
            d += 3.0 * Math.Min(3, GatesNear(b));
            foreach (var p in AtBase(b.Id))
            {
                if (p.Job == "Soldier") d += 5.0;
                else if (p.Job == "Cop") d += 3.5;
                else d += 1.0;
            }
            if (withAlly && Rx("ally_def") > 0.0 && S.Mode == "separate")
            {
                double best = 0.0;
                int me = CurCol;
                for (int i = 0; i < S.Cols.Count; i++)
                {
                    if (i == me || !ColAlive(S.Cols[i])) continue;
                    foreach (var ob in S.Cols[i].Bases)
                        if (ob.Main) best = Math.Max(best, 10.0 + 12.0 * ob.U("walls") + BaseLevelDef(ob).Defense);
                }
                d += best * Rx("ally_def");
            }
            return d;
        }

        // ---------------- frame / hour drivers ----------------
        public double SecPerHour()
        {
            if (FastClock) return 2.0;
            double sph = IsNight() ? NIGHT_REAL_SEC / 8.0 : DAY_REAL_SEC / 16.0;
            double dl = CustomRules.D(S);   // E14 custom game: real seconds per game hour (1.0 = original)
            return dl == 1.0 ? sph : sph * dl;
        }

        /// <summary>GDScript _process(delta). Call once per frame with real seconds.</summary>
        public void Process(double delta)
        {
            if (IsClient)
            {
                ClientInterp(delta);
                if (S != null && S.Guts.Count > 0 && (Net?.SimRunning() ?? Running))
                    foreach (var kv in S.Guts) kv.Value.Left = Math.Max(0.0, kv.Value.Left - delta);
                return;
            }
            if (!Running || S == null || S.Phase == "pick" || S.Phase == "lost") return;
            Step(delta);
            if (AutosaveOn) AutosaveTick(delta);
        }

        /// <summary>Advance the simulation by a number of in-game hours (headless driver for
        /// tests, fast-forward and servers). Runs Step in 0.2 h slices, the largest slice Step
        /// accepts, so it simulates exactly like frames would at maximum speed.</summary>
        public void Advance(double hours)
        {
            if (S == null || S.Phase == "pick" || S.Phase == "lost") return;
            double left = hours;
            while (left > 1e-9 && S.Phase != "lost")
            {
                double dh = Math.Min(0.2, left);
                double mult = FastClock ? TestSpeed : 1.0;
                Step(dh * SecPerHour() / mult);
                left -= dh;
            }
        }

        public void Step(double delta)
        {
            double sph = SecPerHour();
            double mult = FastClock ? TestSpeed : 1.0;
            double dh = Math.Min(delta * mult / sph, 0.2);
            if (dh <= 0.0) return;
            RefreshRefs();
            _rtBudget = RT_PATH_BUDGET;
            S.T += dh;
            if (S.Guts.Count > 0) StepGuts(CustomRules.D(S) == 1.0 ? dh * sph : dh * sph / CustomRules.D(S));   // E14: gutting keeps its game-time length
            MoveZombies(dh); Dbg("MoveZombies");
            RebuildBuckets();
            _openHumans.Clear();
            EachCol(() => MovePeople(dh)); Dbg("MovePeople");
            if (ExpansionRules) EachCol(() => StepCarRuns(dh));   // Expansion vehicles: scavenging parties in cars
            _accCombat += dh;
            if (_accCombat >= 0.25)
            {
                _accCombat -= 0.25;
                EachCol(CombatTick); Dbg("CombatTick");
            }
            FlushDead();
            _accHour += dh;
            if (_accHour >= 1.0)
            {
                _accHour -= 1.0;
                if (S.Cols.Count > STAGGER_COLS) TickHourStaggered();
                else TickHour();
                FlushDead();
            }
            else if (_hourQ.Count > 0)
            {
                StepHourQ();
                FlushDead();
            }
        }

        public void TickHour()
        {
            FlushHourQ();
            TickHourPre();
            EachCol(HourCol);
            TickHourPost();
        }

        const int STAGGER_COLS = 4;
        readonly List<int> _hourQ = new List<int>();
        void TickHourStaggered()
        {
            FlushHourQ();
            TickHourPre();
            for (int i = 0; i < S.Cols.Count; i++)
                if (ColAlive(S.Cols[i])) _hourQ.Add(i);
            TickHourPost();
        }

        void StepHourQ()
        {
            if (_hourQ.Count == 0) return;
            int i = _hourQ[0]; _hourQ.RemoveAt(0);
            if (i < S.Cols.Count && ColAlive(S.Cols[i]))
            {
                Use(i); LogCol = i;
                HourCol();
                LogCol = -1;
                Use(ViewCol);
            }
        }

        void FlushHourQ() { while (_hourQ.Count > 0) StepHourQ(); }

        /// <summary>Parity debugging (work/parity): called after each hourly sub-step with its name. Null in the game.</summary>
        public Action<string> DbgHook;
        public static bool DbgV;
        void Dbg(string n) => DbgHook?.Invoke(n);

        void TickHourPre()
        {
            _rtNeed.Clear(); _slotCache.Clear(); _insideDirty = true;
            UpdateZoneLive(); Dbg("UpdateZoneLive");
            Spread(); Dbg("Spread");
            UpdateWeights(); Dbg("UpdateWeights");
            UpdateActive(); Dbg("UpdateActive");
            SpawnDotsHourly(); Dbg("SpawnDotsHourly");
        }

        void HourCol()
        {
            Economy(); Dbg("Economy");
            Medical(); Dbg("Medical");
            StepCraft(); Dbg("StepCraft");
            StepVehicles(); Dbg("StepVehicles");
            StepNoise(); Dbg("StepNoise");
            Equip(); Dbg("Equip");
            AssignJobs(); Dbg("AssignJobs");
            AutoRescue(); Dbg("AutoRescue");
            StepResearch(); Dbg("StepResearch");
            StepTrade(); Dbg("StepTrade");
            StepBases(); Dbg("StepBases");
            StepSquads(); Dbg("StepSquads");
            StepFieldSquads(); Dbg("StepFieldSquads");
            StepArrivals(); Dbg("StepArrivals");
            PatchGaps(); Dbg("PatchGaps");
            StepConversions(); Dbg("StepConversions");
            AutoGuards(); Dbg("AutoGuards");
            PostGuards(); Dbg("PostGuards");
            StepNightRaid(); Dbg("StepNightRaid");
            StepFamily(); Dbg("StepFamily");
            if (ExpansionRules) { PeopleHour(); Dbg("PeopleHour"); }   // Expansion: people
            MaybeEvents(); Dbg("MaybeEvents");
            StepWar(); Dbg("StepWar");
        }

        void TickHourPost()
        {
            StepControl(); Dbg("StepControl");
            if (ExpansionRules) { StepExpWorld(); Dbg("StepExpWorld"); }   // Expansion: weather, seasons, district danger
            StepGroups(); Dbg("StepGroups");
            StepCamps(); Dbg("StepCamps");
            StepFactions(); Dbg("StepFactions");
            RegrowLoot(); Dbg("RegrowLoot");
            StepPhase(); Dbg("StepPhase");
            StepDefenseGlobal(); Dbg("StepDefenseGlobal");
            if (ExpansionRules) { StepSupport(); Dbg("StepSupport"); }   // v0.19 leader popularity / camp vote / exile (Popularity.cs)
            Use(ViewCol);
            if (_flowDirty && (S.Phase == "fall" || RaidOnAny())) BuildFlow();
            HourTicked?.Invoke();
            EmitChanged(); Dbg("EmitChanged");
        }

        // ---------------- resources ----------------
        public bool CanAfford(IEnumerable<KeyValuePair<string, double>> cost)
        {
            foreach (var kv in cost)
                if (R(kv.Key) < kv.Value) return false;
            return true;
        }

        public void Pay(IEnumerable<KeyValuePair<string, double>> cost)
        {
            foreach (var kv in cost) C.Res[kv.Key] = R(kv.Key) - kv.Value;
        }

        public static string FmtCost(IEnumerable<KeyValuePair<string, double>> cost)
        {
            var parts = new List<string>();
            foreach (var kv in cost)
                parts.Add(string.Format("{0} {1}", (int)Math.Ceiling(kv.Value - 0.001), (Data.RES_LABEL.TryGetValue(kv.Key, out var lab) ? lab : kv.Key).ToLowerInvariant()));
            return string.Join(", ", parts);
        }

        static ODict<string, double> StartRes()
        {
            var r = new ODict<string, double> { ["food"] = 40.0, ["water"] = 40.0, ["ammo"] = 20.0, ["meds"] = 6.0, ["fuel"] = 10.0 };
            foreach (var m in Data.MATS) r[m] = 0.0;
            foreach (var kv in START_MATS) r[kv.Key] = kv.Value;
            return r;
        }

        static readonly Dictionary<string, double> START_MATS = Gd.D(("wood", 46), ("nails", 14), ("metal", 4), ("cloth", 2));

        static ODict<string, double> ResZero()
        {
            var r = new ODict<string, double> { ["materials"] = 0.0 };
            foreach (var k in Data.RESOURCES) r[k] = 0.0;
            return r;
        }

        static void SplitInto(ODict<string, double> r, double amount, IEnumerable<KeyValuePair<string, double>> mix)
        {
            foreach (var m in mix) r[m.Key] = r.Get(m.Key, 0.0) + amount * m.Value;
        }

        public double MatsTotal(ODict<string, double> res = null)
        {
            if (res == null || res.Count == 0) res = C.Res;
            double t = 0;
            foreach (var m in Data.MATS) t += res.Get(m, 0.0);
            return t;
        }

        public void SetMats(double v, int ci = -1)
        {
            var r = ci < 0 ? C.Res : ColonyAt(ci).Res;
            foreach (var m in Data.MATS) r[m] = v;
        }

        public void AddEachMat(double v, int ci = -1)
        {
            var r = ci < 0 ? C.Res : ColonyAt(ci).Res;
            foreach (var m in Data.MATS) r[m] = r.Get(m, 0.0) + v;
        }

        public void AddMats(double amount, IEnumerable<KeyValuePair<string, double>> mix = null)
        {
            if (amount <= 0.0) return;
            SplitInto(C.Res, amount, mix ?? Data.MAT_MIX);
        }

        public double TakeMats(double amount)
        {
            double tot = MatsTotal();
            if (tot <= 0.0 || amount <= 0.0) return 0.0;
            double f = Math.Min(1.0, amount / tot);
            foreach (var m in Data.MATS) C.Res[m] = Math.Max(0.0, R(m) * (1.0 - f));
            return tot * f;
        }

        public void AddRes(string k, double v)
        {
            if (k == "materials")
            {
                if (v >= 0.0) AddMats(v);
                else TakeMats(-v);
                return;
            }
            if (!C.Res.Has(k)) return;
            C.Res[k] = C.Res[k] + v;
        }

        public static double MatSum(IEnumerable<KeyValuePair<string, double>> cost)
        {
            double t = 0;
            foreach (var kv in cost)
                if (Data.MATS.Contains(kv.Key) || kv.Key == "materials") t += kv.Value;
            return t;
        }

        public bool PayMatsFlex(double amount, IEnumerable<KeyValuePair<string, double>> recipe)
        {
            if (amount <= 0.0) return true;
            double rs = MatSum(recipe);
            if (rs > 0.0)
            {
                var c = new Dictionary<string, double>();
                foreach (var kv in recipe)
                    if (Data.MATS.Contains(kv.Key)) c[kv.Key] = kv.Value * amount / rs;
                if (CanAfford(c)) { Pay(c); return true; }
            }
            if (MatsTotal() + 0.0001 < amount) return false;
            TakeMats(amount);
            return true;
        }

        void FixRes(ODict<string, double> res)
        {
            if (res.Has("materials"))
            {
                double m = res["materials"];
                res.Remove("materials");
                foreach (var k in Data.MATS) if (!res.Has(k)) res[k] = 0.0;
                if (m > 0.0) SplitInto(res, m, Data.MAT_MIX);
            }
            foreach (var k in Data.RESOURCES) if (!res.Has(k)) res[k] = 0.0;
        }

        public Vector2 SxCenter(int i) => new Vector2((i % SW) * SECT + SECT / 2, (i / SW) * SECT + SECT / 2);
        public int SectorOwner(int i) => S.Ctrl[i] - 1;

        static readonly string[] STREETS = { "Elm", "Oak", "Main", "Pine", "Maple", "Cedar", "Birch", "Ash", "Walnut", "Spruce" };
        public static string StreetName(Vector2Int p) => string.Format("{0} St & {1} Ave", STREETS[((p.y / 7) % 10 + 10) % 10], (p.x / 8) + 1);

        static string A(string noun) => ("aeiou".IndexOf(char.ToLowerInvariant(noun.Length > 0 ? noun[0] : 'x')) >= 0 ? "an " : "a ") + noun;
        static string Lower(string s) => (s ?? "").ToLowerInvariant();
        static string ResLabel(string k) => Data.RES_LABEL.TryGetValue(k, out var l) ? l : k;
    }
}
