// Typed replacements for the untyped Dictionaries game.gd keeps in its state `S`.
// Field names are the GDScript keys in PascalCase; the save writer uses a snake_case naming
// strategy, so the JSON keys read like the original ("next_arrival_t", "garage_ok"...).
// Keys GDScript tested with has()/get(k, default) are nullable or carry the default value.
// Small fixed-shape arrays (fences [tier, hp, col], towers, traps, conversions, guts, craft jobs)
// became small classes.
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DayZero.Core
{
    public sealed class Person
    {
        public const int NO_BLD = -999999;
        public int Id;
        public string Name = "";
        public string Job = "Civilian";
        public int Base;
        public double Hp = 100.0;
        public double Inf = -1.0;
        public bool Known;
        public string St = "base";      // base | move | work | dead
        public string Goal = "";
        public Vector2Int Tgt = new Vector2Int(-1, -1);
        public int Lot = -1;
        public Vector2 Pos;
        public ODict<string, double> Carry = new ODict<string, double>();
        public double Wt, Cd;
        public int Kills;
        public int Tf;                   // group id, or -1000000 - lot for a supply run
        public int Pi;
        public double Seg;
        public string Wpn = "", Vest = "", Helm = "";
        public bool Veh, Vlead;
        public int Col;
        public int Sq;
        public int Inb = NO_BLD, Home = NO_BLD;
        public string Work = "";
        public int Wl = -1;
        [JsonIgnore] public List<Vector2> Path = new List<Vector2>();

        // family (game.gd _fam_init)
        public string Sex;
        public double Born = -1.0;
        public string Stage;
        public ODict<string, double> Rel = new ODict<string, double>();
        public int Partner;
        public List<int> Parents = new List<int>();
        public List<string> Pnames = new List<string>();
        public List<int> Kids = new List<int>();
        public double Preg = -1.0;
        public int PregBy;
        public double LastBirth = -1000000.0;
        public string Lost = "";

        // optional keys
        public bool Guard;
        public bool? Gauto;
        public Vector2? Post;
        public bool Hold;
        public int? Trav;
        public bool Resume;
        public string Vt;
        public double? Vspd, Vcargo, Varm;
        public int? Vid;
        public bool Tow;

        // Expansion: people (SYSTEMS_30 #5 Stress, #6 Traits, #18 Fatigue). Only touched while Game.ExpansionRules
        // is on; every field defaults to 0 / null so it is left out of the save and a classic run never sees it.
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public List<string> Traits;   // null = not rolled yet
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public string TraitHidden;    // shown as "?" until JoinT + 24 h
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public double JoinT;
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public double Stress;         // 0..100
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public double Fatigue;        // 0..100
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public int TalkDay;           // Day() of the last TALK
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public double RestEndT;       // REST DAY until
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public double FreezeEndT;     // break: refuses work until
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public double SleepEndT;      // sleeping off a watch / collapse until
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public bool Collapsed;
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public int Shift;             // night-watch shift 1..3 (0 = none)
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public bool Dozing;           // dozed off on watch this hour
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public ODict<string, double> Stash;   // stolen goods (break: Steal)
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public double StashT;         // when the stash may be found
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public double DesertT;        // break: Desert, leaves at this hour
        // end Expansion: people

        // client-side interpolation (net clients only, never saved)
        [JsonIgnore] public Vector2? CFrom, CTo;
        [JsonIgnore] public double CLt, CLd;

        public string Slot(string s) => s == "wpn" ? Wpn : (s == "vest" ? Vest : (s == "helm" ? Helm : ""));
        public void SetSlot(string s, string id)
        {
            if (s == "wpn") Wpn = id; else if (s == "vest") Vest = id; else if (s == "helm") Helm = id;
        }
        public string First => (Name ?? "?").Split(' ')[0];
    }

    public sealed class Base
    {
        public int Id;
        public Vector2Int Pos;
        public bool Main;
        public double Done = 1.0;
        public int Veh;
        public ODict<string, int> Up = new ODict<string, int>();
        public int Col;
        public int Lvl;
        public string Name = "";
        public int Lot = -1;
        public List<int> Claim = new List<int>();
        public string Site = "";
        public bool GarageOk;
        public ODict<string, bool> Auto;   // per-camp overrides (null = use colony default)
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public float Noise;   // expansion #3 noise meter 0..100
        public int U(string k) => Up.Get(k, 0);
    }

    public sealed class ResearchState
    {
        public List<string> Done = new List<string>();
        public string Cur = "";
        public ODict<string, double> Prog = new ODict<string, double>();
    }

    public sealed class AutoCfg
    {
        public double R = 26.0;
        public bool Scav = true, Rescue = true, Conv = true, Guards = true;
        public bool Repair = true;   // v0.12.0a: survivors patch walls / breaches on their own
        public bool Get(string k) => k == "scav" ? Scav : k == "rescue" ? Rescue : k == "conv" ? Conv : k == "guards" ? Guards : k == "repair" ? Repair : true;
    }

    public sealed class CraftJob
    {
        public string Kind, Id;
        public double End = -1.0, Hours = 3.0;
    }

    public sealed class Vehicle
    {
        public int Id;
        public string Type = "pickup";
        public int Base;
        public double Hp = 1.0;
        public List<string> Need = new List<string>();
        public double Fuel;
        public int Sq = -1;
        public string St = "ok";   // ok | out | repair | wreck
        public double End;
        public int Drv = -1;
    }

    public sealed class Wreck
    {
        public int Id;
        public string Type = "pickup";
        public Vector2Int Pos;
        public double Hp;
        public List<string> Need = new List<string>();
        public double Fuel;
        public int Own = -1;
    }

    public sealed class Squad
    {
        public int Id;
        public string Name = "";
        public List<int> Members = new List<int>();
        public int Base;
        public string St = "idle";   // idle | out | return | search | attack | travel | field
        public Vector2Int Tgt = new Vector2Int(-1, -1);
        public List<int> Route = new List<int>();
        public int Kills, Cleared;
        public double? Kit;
        public int Vid = -1;
        public bool Vret;
        public string Mode = "";
        public int TowId = -1;
        public Wreck Towing;
        public string Dest, From;
        public int Hops = 1;
        public double? Tarr;
        public string CurMap;
        public Vector2Int? At;
        public string Foe = "";
        public double? SearchEnd;
        public int Searched;
        public double KitOr1 => Kit ?? 1.0;
    }

    public sealed class Raid
    {
        public double NextT = -1.0;
        public int N;
        public int Bid = -1;
        public double Dir;
        public bool Warned, On;
        public int Spawned, Breaks, Nights, LastDay;
        public double Start = -1.0;
        public double Dmg;
        // v0.12.0a: expected arrival at the wall (kept after launch for the HUD countdown), major attack flag,
        // most raiders seen at the wall at once
        public double Eta = -1.0;
        public bool Major;
        public int AtWall;
    }

    public sealed class Fighter
    {
        public int Pid;
        public string Name = "", Job = "", Gear = "";
        public bool Armor;
        public int Dice;
        public string State = "up";
    }

    public sealed class WarReport
    {
        public int Id;
        public string Foe;
        public bool Won;
        public int Day;
        public string Summary, Casualties;
        public List<string> Head = new List<string>();
        public List<string> Lines = new List<string>();
        public int Rounds;
        public string Surrender;
        public int Ammo;
    }

    public sealed class WarState
    {
        public string St = "none";  // none | demand | prep
        public string Foe = "", Lord = "";
        public double CoolT = -1.0, BattleT = -1.0;
        public List<int> Army = new List<int>();
        public int Melee, Vests;
        public ODict<string, double> Paid = new ODict<string, double>();
        public WarReport Report;
        public int Rid, DawnDay, Rolls, Hits;
        public int Uid = -1;
    }

    public sealed class TradeGood
    {
        public string M = "ignore";
        public int Lvl;
    }

    public sealed class TradeCfg
    {
        public ODict<string, TradeGood> Goods = new ODict<string, TradeGood>();
        public double Credit, Next;
        public string Last = "";
    }

    public sealed class Colony
    {
        public int Id;
        public string Name = "";
        public ODict<string, double> Res = new ODict<string, double>();
        public List<Base> Bases = new List<Base>();
        public List<Person> People = new List<Person>();
        public ResearchState Research = new ResearchState();
        public List<Squad> Squads = new List<Squad>();
        public ODict<string, double> Cd = new ODict<string, double>();
        public List<string> Pacts = new List<string>();
        public AutoCfg Auto = new AutoCfg();
        public ODict<string, int> Weapons = new ODict<string, int>();
        public ODict<string, int> Kit = new ODict<string, int>();
        public List<CraftJob> Craft = new List<CraftJob>();
        public List<Vehicle> Vehs = new List<Vehicle>();
        public ODict<string, int> Parts = new ODict<string, int>();
        public ODict<string, int> Stats = new ODict<string, int>();
        public double Morale = 70.0;
        public double NextArrivalT, NextEventT;
        public bool WonShown;
        public bool Started, Lost, Won;
        public int LostDay;
        public int Merged = -1;
        public Raid Nraid = new Raid();
        public double StartT;
        public WarState War = new WarState();
        public TradeCfg Trade;
        public Vector2Int? Spawn;
        public bool Away;           // set by the network layer when the player is offline
        // Expansion: people (night watch shifts, AUTO talk, breach / death bookkeeping); null in classic runs
        public PeopleCfg Ppl;
        // v0.19 leader popularity (Core/Popularity.cs): support, mutiny clock, camp vote, last exile; null in classic runs
        public LeaderStanding Lead;
        // end Expansion: people
        public int Stat(string k) => Stats.Get(k, 0);
        public void AddStat(string k, int v) => Stats[k] = Stats.Get(k, 0) + v;
    }

    // Expansion: people
    /// <summary>SYSTEMS_30 #18 / #5 colony settings and bookkeeping (Colony.Ppl, created on the first expansion hour).</summary>
    public sealed class PeopleCfg
    {
        public int Shifts = 1;                 // night watch: 1, 2 or 3 shifts
        public bool AutoRotate = true;         // most rested guards take the watch
        public bool AutoTalk = true;           // AUTO: Leaders (and Medics) talk to strained people
        public int RaidBreaks;                 // breaks of the current night raid already turned into Stress
        public double RaidStart = -1.0;
        public ODict<string, int> DeathDay = new ODict<string, int>();   // base id -> last Day() someone there died
        public int Breaks, Deserted, Steals, Snaps, Steeled, Freezes, Dozed, Collapses, Talks;   // stats (balance checks, COLONY)
    }
    // end Expansion: people

    public sealed class Group
    {
        public int Id, Lot;
        public Vector2Int Pos;
        public int N;
        public string Label = "";
        public string St = "wait";
        public bool Tf;
    }

    public sealed class Camp
    {
        public string Id = "", Name = "", Leader = "";
        public int Pop;
        public string Att = "Neutral";
        public double Rel;
        public int Lot;
        public Vector2Int Pos;
        public bool Alive = true;
        public List<string> Needs = new List<string>(), Surplus = new List<string>();
        public string Desc = "";
        // faction (game.gd _faction_init)
        public int? Fi;
        public string Color;
        public double Str = 30.0;
        public ODict<string, double> Stock = new ODict<string, double>();
        public List<double> Rels;
        public List<int> Claim;
        public double NextScav, NextGrow, NextGift, NextRaid, NextAmbush, NextHelp;
        public ODict<string, double> Truce = new ODict<string, double>();
        public int Searched;
        public int Lord = -1;
        public ODict<string, double> Owed = new ODict<string, double>();
        public int? DefeatedBy;
        public LeaderMind Mind;   // EXPANDED: the leader's mind (LeaderBrain.cs); null in classic runs, skipped in saves when null
        public List<string> List(string which) => which == "needs" ? Needs : (which == "surplus" ? Surplus : new List<string>());
    }

    public sealed class Fence { public int Tier; public double Hp; public int Col; }
    public sealed class FenceSeg { public int Tier; public int Col; }
    public sealed class Tower { public int Tier; public double Hp; public int Col; public int Bid; }
    public sealed class Trap { public int Kind; public double Hp; public int Col; public int Lvl; }
    public sealed class Conversion
    {
        public string Type; public int Col; public int Bid; public int Manual; public double T;
        public double Ready;     // v0.12.0a: > 0 = still being set up until this game hour (0 = in use)
        public string Prev;      // use it had before a change of use (kept working until Ready)
    }

    /// <summary>v0.12.0a: a straight wall the player placed (or the starter palisade). The sim still works on
    /// fence tiles (S.Fences); the run is the line those tiles were rasterised from, so the view can draw a
    /// straight wall at any angle and map each tile's hp onto it.</summary>
    public sealed class WallRun
    {
        public int Id; public Vector2 A, B; public int Tier; public int Col; public int Bid = -1;
        public bool Starter;
        public float GapT = -1f, GapW;   // opening along the run (0..1 at A..B, half-width in tiles) where the camp gate stands
    }

    /// <summary>v0.12.0a: a fence tile the dead broke through; survivors rebuild it (or the player pays to).</summary>
    public sealed class Breach { public int Tier; public int Col; public double T; public bool Gate; }
    public sealed class GutJob { public int Col; public double Left, Total; public int Bid; public int Manual = 1; }
    public sealed class PlayerInfo { public string Name = ""; public int Col; }

    public sealed class LogEntry
    {
        public double T;
        public string Text = "", Kind = "info";
        public int Col, N;
        public string Rid;   // research id for "Research complete" entries
    }

    /// <summary>The global part of game.gd `S`. The per-colony refs GDScript aliased into S
    /// (res, bases, people, research...) live on Colony; Game exposes the current one as C.</summary>
    public sealed class GameState
    {
        public int Ver;
        public int Seed;
        public double T = 6.0;
        public string Phase = "pick";
        public int Difficulty = 1;
        public List<Group> Groups = new List<Group>();
        public List<Camp> Camps = new List<Camp>();
        public ODict<int, Fence> Fences = new ODict<int, Fence>();
        public ODict<int, FenceSeg> Fsegs = new ODict<int, FenceSeg>();
        public ODict<int, Tower> Towers = new ODict<int, Tower>();
        public ODict<int, Trap> Traps = new ODict<int, Trap>();
        public ODict<int, int> Gates = new ODict<int, int>();
        public ODict<int, Conversion> Conv = new ODict<int, Conversion>();
        public ODict<int, int> Gut = new ODict<int, int>();
        public ODict<int, GutJob> Guts = new ODict<int, GutJob>();
        public int FenceVer = 1, NextId = 1, ConvVer = 1;
        // v0.12.0a defenses: placed wall runs, breaches waiting for repair; WallsInit is false in older saves
        public List<WallRun> Walls = new List<WallRun>();
        public ODict<int, Breach> Breaches = new ODict<int, Breach>();
        public bool WallsInit;
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public bool ExpRules;   // run started with Game.ExpansionRules
        // Expansion world (SYSTEMS_30 #2 weather/seasons, #23 district danger); untouched while ExpRules is off
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public int Season, SeasonDay, Weather;
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public double WeatherEndT;
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public int[] DistrictDanger;
        public byte[] Ctrl;
        public float[] CtrlH, Scout, Loot, Zinf, Srch;
        public byte[] SrchC;
        public Vector2Int Zcorner;
        public double FallT = -1.0, NextHordeT, NextGroupT = 36.0, NextRaidT = 90.0, NextJumpT = 72.0;
        public List<LogEntry> Log = new List<LogEntry>();
        public List<Colony> Cols = new List<Colony>();
        public ODict<string, PlayerInfo> Players = new ODict<string, PlayerInfo>();
        public string Mode = "solo";
        public List<Wreck> Wrecks = new List<Wreck>();
        public int VehN = 1, VehVer = 1;
        public int NextUid = 1;
        // E5-E15 run log (Runtime/RunStats.cs: daily population / kills, memorial wall, morning report); UI only, missing = new
        [JsonProperty("runlog", NullValueHandling = NullValueHandling.Ignore)] public Newtonsoft.Json.Linq.JObject RunLog;
        // v0.19 survivor minds (Core/SurvivorMind.cs TalkLog): TALK history, names seen; UI only, never read by the sim, missing = new
        [JsonProperty("talk", NullValueHandling = NullValueHandling.Ignore)] public Newtonsoft.Json.Linq.JObject Talk;
        // E14 custom game multipliers (Runtime/CustomGame.cs); null = a normal game, every multiplier 1.0
        [JsonProperty("custom", NullValueHandling = NullValueHandling.Ignore)] public CustomRules Custom;
        // zombies (only filled while saving / loading)
        public int[] Zc, Zn;
        public float[] Zp;
        public byte[] Zh;
    }

    /// <summary>E14 custom game: rule multipliers chosen on New Game (1.0 = original behaviour). Read at the few sim points
    /// listed in PORT_NOTES.md; with a null GameState.Custom every helper returns exactly 1.0, so the sim stays bit-exact.</summary>
    public sealed class CustomRules
    {
        public double Zombies = 1.0, Loot = 1.0, Raids = 1.0, DayLen = 1.0;
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public bool NoExile;   // v0.19: EXILE option off (expanded rules: no camp vote / exile)
        [JsonIgnore] public bool IsDefault => Zombies == 1.0 && Loot == 1.0 && Raids == 1.0 && DayLen == 1.0 && !NoExile;
        public CustomRules Clone() => (CustomRules)MemberwiseClone();
        static double Ok(double v) => double.IsNaN(v) || v <= 0.0 ? 1.0 : v;
        public static double Z(GameState s) => s?.Custom == null ? 1.0 : Ok(s.Custom.Zombies);
        public static double L(GameState s) => s?.Custom == null ? 1.0 : Ok(s.Custom.Loot);
        public static double R(GameState s) => s?.Custom == null ? 1.0 : Ok(s.Custom.Raids);
        public static double D(GameState s) => s?.Custom == null ? 1.0 : Ok(s.Custom.DayLen);
    }

    /// <summary>Per-person daily routine state (game.gd _rt entries; runtime only).</summary>
    public sealed class Routine
    {
        public string Code = "";
        public Vector2Int Tt = new Vector2Int(-1, -1);
        public List<Vector2> Path = new List<Vector2>();
        public int Pi;
        public double Seg;
        public bool Arr;
        public int Ek = Person.NO_BLD;
        public int Hk = Person.NO_BLD;
        public Vector2Int Hd;
        public int Wk = Person.NO_BLD;
        public Vector2Int Wa = new Vector2Int(-9, -9);
        public Vector2 Ws;
        public double J, Leave;
        public bool Danger;
        public int Bid = -1;
        public bool? Set;
    }

    /// <summary>Result of fence_preview / seg_preview / trap_preview.</summary>
    public sealed class BuildPreview
    {
        public List<bool> Ok = new List<bool>();
        public int N, Edges;
        public Dictionary<string, double> Cost = new Dictionary<string, double>();
        public string Err = "";
    }

    public sealed class TravelInfo
    {
        public string Err = "";
        public int Hops;
        public double Hours, Fuel;
        public string Veh = "";
    }

    public sealed class SquadRisk { public int Sectors, N; public double Inf, Loss, Power; }
    public sealed class Quote { public double Price; public Camp Camp; }
}
