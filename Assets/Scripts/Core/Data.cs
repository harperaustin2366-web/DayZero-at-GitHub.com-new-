// Port of scripts/core/data.gd (Godot autoload "Data", extends Node).
// The original is a Node only because it's an autoload; it has no _process/_ready logic,
// so this is a plain class with a lazily-created singleton (Data.Instance).
// Construction loads JSON via Resources.Load, so create it on the main thread.
//
// GDScript -> C# conventions used here:
//  * GDScript `float` (64-bit) -> double; Godot Vector2/Rect2 (32-bit) -> UnityEngine.Vector2/Rect.
//  * Vector2i -> Vector2Int, Rect2i -> RectInt (half-open bounds via Gd.Has).
//  * PackedByteArray/PackedInt32Array -> byte[] / int[] (fixed size) or List<T> (when appended to).
//  * Untyped Dictionaries from JSON stay as Newtonsoft JObject/JArray (sites, research, scavenge,
//    events, camps); hand-written const tables become typed defs (see DataTypes.cs).
//  * signals world_changed / world_changing -> C# events WorldChanged / WorldChanging.
//  * Godot Dictionary iteration is insertion-ordered; C# Dictionary is in practice too when nothing
//    is removed (we never remove from the iterated ones), which keeps road-graph edge order identical.
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DayZero.Core
{
    public sealed class Data
    {
        static Data _instance;
        public static Data Instance => _instance ??= new Data();

        /// <summary>Factory for the forest generator (forest.gd not ported yet). If null,
        /// SetWorldSeed skips the forest pass and logs a warning.</summary>
        public static Func<Data, IForestGen> ForestGenFactory;

        // ---------------- tile types ----------------
        public const int T_WATER = 0;
        public const int T_ROAD = 1;
        public const int T_AVE = 2;
        public const int T_PARK = 3;
        public const int T_BLD = 4;
        public const int T_PIER = 5;
        public const int T_PLAZA = 6;
        public const int T_BRIDGE = 7;

        public const int T_FOREST = 8;
        public const int T_MEADOW = 9;
        public const int T_DIRT = 10;
        public const int T_ROCK = 11;
        public const int T_FIELD = 12;

        public const int T_VOID = 13;

        public const int OFF = 128;

        public const int BAND_H = 272;
        public const int BAND_VOID = 8;

        public Dictionary<string, RegionDef> REGIONS = new Dictionary<string, RegionDef>();
        public int H0 = 0;
        public int RegionLots0 = 0;
        public Dictionary<string, int> RegionCounts = new Dictionary<string, int>();

        // signals
        public event Action WorldChanged;
        public event Action WorldChanging;

        public static readonly string[] JOBS = { "Scavenger", "Civilian", "Farmer", "Medic", "Soldier", "Cop", "Engineer", "Scientist" };
        public static readonly int[] JOB_WEIGHTS = { 14, 34, 10, 9, 8, 8, 10, 7 };
        public static readonly Dictionary<string, string> JOB_DESC = new Dictionary<string, string>
        {
            ["Scavenger"] = "Goes out by day to loot nearby buildings. Best haul, picks safe routes.",
            ["Civilian"] = "Hauls water, does chores and makes short supply runs.",
            ["Farmer"] = "Grows food at the camp. Much better with a Garden.",
            ["Medic"] = "Heals the wounded and screens everyone for hidden bites.",
            ["Soldier"] = "Guards the camp, hunts nearby zombies. Uses ammo, hits hard.",
            ["Cop"] = "Guards and patrols. Cheaper on ammo than soldiers, keeps order.",
            ["Engineer"] = "Builds outposts, repairs fences and towers, runs the workshop.",
            ["Scientist"] = "Runs research projects.",
            ["Child"] = "Too young to work. Eats, sleeps and keeps everyone going.",
        };
        public static readonly Dictionary<string, string> JOB_SHORT = new Dictionary<string, string>
        { ["Scavenger"] = "SCV", ["Civilian"] = "CIV", ["Farmer"] = "FRM", ["Medic"] = "MED", ["Soldier"] = "SOL", ["Cop"] = "COP", ["Engineer"] = "ENG", ["Scientist"] = "SCI", ["Child"] = "KID" };
        public static readonly Dictionary<string, Color> JOB_COLOR = new Dictionary<string, Color>
        {
            ["Scavenger"] = Gd.Hex("#c9b26a"), ["Civilian"] = Gd.Hex("#7fd46a"), ["Farmer"] = Gd.Hex("#a6d65a"), ["Medic"] = Gd.Hex("#f2f2f2"),
            ["Soldier"] = Gd.Hex("#5fbf3f"), ["Cop"] = Gd.Hex("#5aa8ff"), ["Engineer"] = Gd.Hex("#f0a040"), ["Scientist"] = Gd.Hex("#7fe0d0"),
            ["Child"] = Gd.Hex("#f5d6a0"),
        };

        public static readonly string[] DANGER_JOBS = { "Scavenger", "Soldier", "Cop" };

        public static readonly string[] FIRST_F = { "Ana", "Carla", "Fatima", "Hana", "Jada", "Lena", "Nadia", "Priya", "Rosa", "Tess", "Uma", "Ximena",
            "Zoe", "Bea", "Faye", "Ivy", "Kira", "Mae", "Olive", "Sofia", "Vera", "Yara", "Lydia", "June", "Mira", "Nora" };
        public static readonly string[] FIRST_EITHER = { "Quinn", "Sam", "Dana", "Sasha", "Wren" };
        public static readonly string[] BABY_F = { "Rose", "Hope", "Grace", "Lily", "Ellie", "Clara", "Maggie", "Judith", "Ruby", "Hazel", "Iris", "Willa",
            "Faith", "Daisy", "Eve", "Georgia", "Ada", "Pearl", "Sadie", "Holly" };
        public static readonly string[] BABY_M = { "Carl", "Noah", "Eli", "Jesse", "Luke", "Henry", "Abel", "Silas", "Caleb", "Jonah", "Gus", "Milo",
            "Amos", "Reid", "Asher", "Toby", "Beau", "Levi", "Finn", "Hollis" };

        public string SexOf(string fullName, IRng rng)
        {
            string f = fullName.Split(' ')[0];
            if (FIRST_F.Contains(f)) return "f";
            if (FIRST_EITHER.Contains(f) || !FIRST.Contains(f)) return rng.Randf() < 0.5f ? "f" : "m";
            return "m";
        }

        public static readonly string[] BASIC = { "food", "water", "ammo", "meds", "fuel" };
        public static readonly string[] MATS = { "wood", "nails", "metal", "plastic", "cloth", "wire", "copper", "drywall", "elec", "furniture" };
        public static readonly string[] RESOURCES = { "food", "water", "ammo", "meds", "fuel", "wood", "nails", "metal", "plastic", "cloth", "wire", "copper", "drywall", "elec", "furniture" };
        public static readonly Dictionary<string, string> RES_LABEL = new Dictionary<string, string>
        {
            ["food"] = "Food", ["water"] = "Water", ["ammo"] = "Ammo", ["meds"] = "Meds", ["materials"] = "Mats", ["fuel"] = "Fuel",
            ["wood"] = "Wood", ["nails"] = "Nails & screws", ["metal"] = "Metal", ["plastic"] = "Plastic", ["cloth"] = "Cloth", ["wire"] = "Wire", ["copper"] = "Copper wire",
            ["drywall"] = "Drywall", ["elec"] = "Electrical parts", ["furniture"] = "Furniture",
        };
        public static readonly Dictionary<string, string> RES_SHORT = new Dictionary<string, string>
        { ["wood"] = "Wood", ["nails"] = "Nails", ["metal"] = "Metal", ["plastic"] = "Plast", ["cloth"] = "Cloth", ["wire"] = "Wire", ["copper"] = "Copper", ["drywall"] = "Dryw", ["elec"] = "Elec", ["furniture"] = "Furn" };

        public static readonly Dictionary<string, double> MAT_MIX = Gd.D(("wood", 0.26), ("nails", 0.12), ("metal", 0.18), ("plastic", 0.1), ("cloth", 0.1), ("wire", 0.05), ("copper", 0.03), ("drywall", 0.06), ("elec", 0.05), ("furniture", 0.05));

        public static readonly Dictionary<string, Dictionary<string, double>> LOOT_MIX = new Dictionary<string, Dictionary<string, double>>
        {
            ["house"] = Gd.D(("wood", 0.25), ("nails", 0.1), ("cloth", 0.2), ("plastic", 0.15), ("furniture", 0.15), ("elec", 0.05), ("wire", 0.02), ("copper", 0.03), ("metal", 0.05)),
            ["apartment"] = Gd.D(("wood", 0.2), ("nails", 0.1), ("cloth", 0.2), ("plastic", 0.15), ("furniture", 0.15), ("elec", 0.08), ("wire", 0.03), ("copper", 0.04), ("metal", 0.05)),
            ["office"] = Gd.D(("plastic", 0.2), ("elec", 0.2), ("wire", 0.07), ("copper", 0.08), ("furniture", 0.2), ("metal", 0.1), ("drywall", 0.1), ("nails", 0.05)),
            ["factory"] = Gd.D(("metal", 0.4), ("nails", 0.15), ("wire", 0.08), ("copper", 0.07), ("elec", 0.1), ("plastic", 0.15), ("wood", 0.05)),
            ["parking"] = Gd.D(("metal", 0.5), ("plastic", 0.2), ("wire", 0.1), ("copper", 0.1), ("elec", 0.1)),
            ["hardware"] = Gd.D(("nails", 0.25), ("wood", 0.25), ("metal", 0.15), ("wire", 0.08), ("copper", 0.07), ("elec", 0.1), ("plastic", 0.1)),
            ["warehouse"] = Gd.D(("wood", 0.3), ("metal", 0.25), ("plastic", 0.2), ("nails", 0.1), ("wire", 0.1), ("cloth", 0.05)),
            ["gun_shop"] = Gd.D(("metal", 0.7), ("plastic", 0.2), ("cloth", 0.1)),
            ["police"] = Gd.D(("metal", 0.3), ("elec", 0.2), ("wire", 0.07), ("copper", 0.08), ("cloth", 0.2), ("plastic", 0.15)),
            ["school"] = Gd.D(("furniture", 0.3), ("wood", 0.2), ("plastic", 0.2), ("elec", 0.1), ("cloth", 0.1), ("drywall", 0.1)),
            ["checkpoint"] = Gd.D(("metal", 0.35), ("wire", 0.25), ("cloth", 0.15), ("nails", 0.1), ("wood", 0.15)),
            ["fire_station"] = Gd.D(("metal", 0.3), ("cloth", 0.2), ("wire", 0.08), ("copper", 0.07), ("elec", 0.15), ("plastic", 0.1), ("nails", 0.1)),
            ["cabin"] = Gd.D(("wood", 0.4), ("nails", 0.15), ("cloth", 0.2), ("furniture", 0.15), ("metal", 0.1)),
            ["shack"] = Gd.D(("wood", 0.5), ("nails", 0.2), ("cloth", 0.1), ("metal", 0.2)),
            ["farmhouse"] = Gd.D(("wood", 0.35), ("nails", 0.15), ("cloth", 0.15), ("furniture", 0.1), ("metal", 0.15), ("wire", 0.1)),
            ["barn"] = Gd.D(("wood", 0.45), ("nails", 0.2), ("metal", 0.15), ("wire", 0.15), ("cloth", 0.05)),
            ["ranger"] = Gd.D(("wood", 0.25), ("metal", 0.2), ("wire", 0.08), ("copper", 0.07), ("elec", 0.15), ("cloth", 0.15), ("nails", 0.1)),
            ["sawmill"] = Gd.D(("wood", 0.65), ("nails", 0.15), ("metal", 0.15), ("wire", 0.05)),
            ["mill_shed"] = Gd.D(("wood", 0.6), ("nails", 0.2), ("metal", 0.2)),

            ["shop"] = Gd.D(("plastic", 0.25), ("cloth", 0.2), ("furniture", 0.15), ("wood", 0.15), ("metal", 0.1), ("elec", 0.15)),
            ["church"] = Gd.D(("wood", 0.45), ("furniture", 0.25), ("cloth", 0.15), ("nails", 0.1), ("metal", 0.05)),
            ["silo"] = Gd.D(("metal", 0.6), ("wire", 0.15), ("nails", 0.1), ("wood", 0.15)),
            ["grain_elevator"] = Gd.D(("metal", 0.45), ("wood", 0.2), ("wire", 0.1), ("copper", 0.05), ("elec", 0.1), ("nails", 0.1)),
        };

        public static readonly Dictionary<string, double> GUT_YIELD = Gd.D(("wood", 0.55), ("drywall", 0.45), ("elec", 0.18), ("wire", 0.12), ("copper", 0.14), ("furniture", 0.3), ("nails", 0.2), ("metal", 0.15));
        public static readonly Dictionary<string, double> GUT_KIND_MULT = Gd.D(("house", 1.0), ("apartment", 1.1), ("office", 1.15), ("factory", 0.9), ("school", 1.1), ("hospital", 1.2), ("warehouse", 0.8),
            ("cabin", 1.0), ("shack", 0.7), ("barn", 0.8), ("farmhouse", 1.0), ("sawmill", 1.0),
            ("shop", 1.0), ("church", 1.1), ("silo", 0.5), ("grain_elevator", 0.7));
        public const double GUT_SEC = 30.0;
        public const int GUT_MAX = 3;
        public const double GUT_R = 40.0;

        public static readonly string[] FIRST = { "Ana", "Ben", "Carla", "Dmitri", "Eli", "Fatima", "Gabe", "Hana", "Isaac", "Jada", "Kurt", "Lena",
            "Marcus", "Nadia", "Owen", "Priya", "Quinn", "Rosa", "Sam", "Tess", "Uma", "Victor", "Wade", "Ximena", "Yusuf",
            "Zoe", "Abe", "Bea", "Cole", "Dana", "Earl", "Faye", "Glen", "Hector", "Ivy", "Joel", "Kira", "Luis", "Mae",
            "Nate", "Olive", "Pete", "Ray", "Sofia", "Troy", "Vera", "Will", "Yara", "Ezra", "Lydia", "Hank", "June",
            "Dale", "Mira", "Rick", "Sasha", "Theo", "Wren", "Cal", "Nora" };
        public static readonly string[] LAST = { "Alvarez", "Brooks", "Chen", "Dixon", "Ellis", "Ford", "Grimes", "Hale", "Ivers", "Jensen", "Kowalski",
            "Lee", "Moreno", "Nash", "Ortiz", "Price", "Quint", "Reyes", "Stone", "Tran", "Underwood", "Vance", "Walsh",
            "Young", "Zamora", "Baker", "Carter", "Diaz", "Greene", "Hughes", "Kim", "Lopez", "Morgan", "Novak", "Patel",
            "Rhee", "Shaw", "Turner", "Webb", "Monroe" };

        static Dictionary<string, int[]> R(params (string k, int lo, int hi)[] kv)
        {
            var d = new Dictionary<string, int[]>();
            foreach (var (k, lo, hi) in kv) d[k] = new[] { lo, hi };
            return d;
        }

        public static readonly Dictionary<string, Dictionary<string, int[]>> GENERIC_LOOT = new Dictionary<string, Dictionary<string, int[]>>
        {
            ["house"] = R(("food", 3, 8), ("water", 2, 6), ("meds", 0, 2), ("materials", 2, 6), ("ammo", 0, 3)),
            ["apartment"] = R(("food", 4, 10), ("water", 2, 7), ("meds", 0, 2), ("materials", 2, 8), ("ammo", 0, 2)),
            ["office"] = R(("food", 1, 5), ("water", 3, 8), ("materials", 4, 12), ("meds", 0, 1)),
            ["factory"] = R(("materials", 8, 18), ("fuel", 1, 5), ("food", 0, 3)),
            ["parking"] = R(("fuel", 2, 6), ("materials", 1, 3)),

            ["cabin"] = R(("food", 3, 9), ("water", 2, 6), ("meds", 0, 2), ("materials", 2, 6), ("ammo", 0, 4), ("fuel", 0, 2)),
            ["shack"] = R(("food", 1, 5), ("water", 1, 4), ("materials", 3, 7), ("ammo", 0, 2)),
            ["farmhouse"] = R(("food", 10, 22), ("water", 4, 10), ("meds", 0, 3), ("materials", 3, 8), ("ammo", 1, 6), ("fuel", 1, 4)),
            ["barn"] = R(("food", 6, 14), ("materials", 6, 14), ("fuel", 2, 8)),
            ["ranger"] = R(("food", 4, 10), ("water", 6, 14), ("meds", 3, 8), ("ammo", 4, 12), ("materials", 3, 8), ("fuel", 2, 6)),
            ["camp_hut"] = R(("food", 2, 7), ("water", 3, 8), ("meds", 0, 2), ("fuel", 1, 3)),
            ["stand"] = R(("ammo", 1, 5), ("food", 0, 2)),
            ["sawmill"] = R(("materials", 16, 34), ("fuel", 3, 9), ("food", 0, 3)),
            ["mill_shed"] = R(("materials", 8, 18), ("fuel", 2, 6)),

            ["shop"] = R(("food", 2, 7), ("water", 2, 6), ("meds", 0, 2), ("materials", 3, 8)),
        };
        public static readonly Dictionary<string, string> KIND_LABEL = new Dictionary<string, string>
        {
            ["house"] = "Houses", ["apartment"] = "Apartments", ["office"] = "Offices", ["factory"] = "Factory", ["parking"] = "Parking lot",
            ["farm"] = "Miller Farm", ["school"] = "Lincoln High School", ["police"] = "5th Street Precinct", ["hospital"] = "St. Mary's Hospital",
            ["warehouse"] = "Harlan Freight Warehouse", ["fire_station"] = "Fire Station",
            ["cabin"] = "Cabin", ["shack"] = "Homestead", ["farmhouse"] = "Farmhouse", ["barn"] = "Barn", ["ranger"] = "Ranger Station",
            ["camp_hut"] = "Campground Cabin", ["stand"] = "Hunting Stand", ["sawmill"] = "Old Sawmill", ["mill_shed"] = "Sawmill Shed",
            ["shop"] = "Shop", ["silo"] = "Silo", ["grain_elevator"] = "Grain Elevator",
        };

        public static readonly Dictionary<string, string[]> WILD_NAMES = new Dictionary<string, string[]>
        {
            ["farmhouse"] = new[] { "Hollister Farm", "Cooper Homestead", "Dunmore Farm", "Aldridge Farm", "Whitcomb Place", "Bramble Farm", "Corliss Farm", "Larkin Farm" },
            ["ranger"] = new[] { "Elk Creek Ranger Station", "Pine Ridge Ranger Station", "Bear Hollow Ranger Station" },
            ["sawmill"] = new[] { "Harlan Lumber Co. Sawmill", "Kettle River Mill" },
            ["camp_hut"] = new[] { "Lakeside Campground", "Fern Gully Campground", "Cedar Flats Campground" },
            ["cabin"] = new[] { "Hunting Cabin", "Cabin", "Old Cabin", "Trapper's Cabin", "Summer Cabin" },
            ["shack"] = new[] { "Homestead", "Old Homestead", "Settler's Shack" },
        };

        static List<Dictionary<string, double>> L(params Dictionary<string, double>[] c) => c.ToList();

        public static readonly Dictionary<string, UpgradeDef> UPGRADES = new Dictionary<string, UpgradeDef>
        {
            ["medbay"] = new UpgradeDef { Name = "Med Bay", Max = 3, Cost = L(Gd.D(("cloth", 8), ("plastic", 5), ("furniture", 4), ("wood", 3), ("meds", 4)), Gd.D(("cloth", 12), ("plastic", 8), ("furniture", 6), ("elec", 4), ("wood", 5), ("meds", 8)), Gd.D(("cloth", 18), ("plastic", 12), ("elec", 10), ("metal", 10), ("furniture", 10), ("meds", 12), ("fuel", 5))),
                Desc = "Heals the wounded and treats infection (better cure odds each level). Medics here brew 0.6 meds/day per level." },
            ["armory"] = new UpgradeDef { Name = "Armory", Max = 3, Cost = L(Gd.D(("wood", 10), ("metal", 10), ("nails", 5)), Gd.D(("metal", 20), ("wood", 10), ("nails", 10), ("ammo", 10)), Gd.D(("metal", 40), ("wire", 10), ("elec", 10), ("copper", 4), ("nails", 10), ("fuel", 8))),
                Desc = "Crafts guns and ammo. L1 pistols + reloading, L2 shotguns, L3 rifles. Auto-reloads 3 ammo/day per level from metal and plastic while ammo is low." },
            ["garage"] = new UpgradeDef { Name = "Garage", Max = 3, Cost = L(Gd.D(("metal", 15), ("wood", 8), ("nails", 7), ("fuel", 5)), Gd.D(("metal", 22), ("elec", 8), ("copper", 8), ("nails", 7), ("fuel", 10)), Gd.D(("metal", 35), ("elec", 15), ("copper", 10), ("plastic", 10), ("fuel", 15))),
                Desc = "Rebuild vehicles from scavenged parts (battery, tires, alternator, fuel pump). One vehicle per level. Needs a site with a garage: gas station, warehouse, precinct or factory." },
            ["garden"] = new UpgradeDef { Name = "Garden", Max = 2, Cost = L(Gd.D(("wood", 8), ("nails", 4)), Gd.D(("wood", 16), ("nails", 6), ("plastic", 8), ("water", 10))), Desc = "+5 food/day per level. Farmers here produce 50% more per level." },
            ["water"] = new UpgradeDef { Name = "Water Collectors", Max = 3, Cost = L(Gd.D(("plastic", 8), ("wood", 5), ("nails", 2)), Gd.D(("plastic", 14), ("metal", 10), ("wire", 6)), Gd.D(("metal", 20), ("plastic", 15), ("elec", 10), ("copper", 5), ("fuel", 5))), Desc = "Rain barrels, filters, then a pump: +5 water/day per level." },
            ["workshop"] = new UpgradeDef { Name = "Workshop", Max = 1, Cost = L(Gd.D(("wood", 10), ("metal", 10), ("nails", 5), ("elec", 5), ("copper", 3), ("fuel", 5))), Desc = "Engineers here salvage 2 more materials/day each (mixed items). Enables ammo reloading research." },
            ["walls"] = new UpgradeDef { Name = "Compound walls", Slot = false, Max = 3, Cost = L(Gd.D(("wood", 12), ("nails", 4)), Gd.D(("wood", 20), ("metal", 12), ("nails", 8)), Gd.D(("metal", 40), ("wood", 15), ("nails", 15))), Desc = "Fewer bites when zombies reach the camp. Each level +12 defense." },
            ["tower"] = new UpgradeDef { Name = "Lookout post", Slot = false, Max = 1, Cost = L(Gd.D(("wood", 18), ("nails", 7))), Desc = "Guards at the camp shoot from further away (+3 tiles)." },
            ["barracks"] = new UpgradeDef { Name = "Barracks", Max = 3, Unlock = "barracks", Cost = L(Gd.D(("wood", 12), ("furniture", 10), ("cloth", 8), ("nails", 5)), Gd.D(("wood", 20), ("furniture", 18), ("cloth", 12), ("nails", 10)), Gd.D(("wood", 30), ("furniture", 25), ("cloth", 15), ("drywall", 10), ("nails", 10), ("fuel", 5))), Desc = "Bunks: +10 beds per level." },
            ["lab"] = new UpgradeDef { Name = "Research Lab", Max = 2, Cost = L(Gd.D(("elec", 10), ("furniture", 8), ("copper", 6), ("plastic", 6)), Gd.D(("elec", 25), ("copper", 15), ("metal", 15), ("plastic", 15), ("fuel", 10))), Desc = "Scientists work 40% faster per level (best lab counts)." },
            ["siren"] = new UpgradeDef { Name = "Air-raid siren", Slot = false, Max = 1, Cost = L(Gd.D(("metal", 6), ("elec", 3), ("copper", 2), ("wire", 2))), Desc = "A hand-crank siren on a mast. Lookouts sound it 3 hours before an attack (every attack, not just the big ones) and the guards man the wall at once." },
            ["radio"] = new UpgradeDef { Name = "Radio", Slot = false, Max = 1, Cost = L(Gd.D(("elec", 8), ("copper", 5), ("metal", 2), ("fuel", 8))), Desc = "Strangers find you more often. Main camp only." },
        };

        static Dictionary<string, int> P(params (string k, int v)[] kv)
        {
            var d = new Dictionary<string, int>();
            foreach (var (k, v) in kv) d[k] = v;
            return d;
        }

        public static readonly Dictionary<string, WeaponDef> WEAPONS = new Dictionary<string, WeaponDef>
        {
            ["pipe"] = new WeaponDef { Name = "Pipe pistol", Armory = 1, Cost = Gd.D(("nails", 1), ("wood", 1)), Parts = P(("g_barrel", 1), ("g_trigger", 1)), H = 3.0, Dmg = 22, Hit = 0.58, Range = 3.5, Noise = 5, Ammo = 0.6, Multi = 1, Val = 14 },
            ["pistol"] = new WeaponDef { Name = "Pistol", Armory = 1, Cost = Gd.D(("plastic", 1)), Parts = P(("g_barrel", 1), ("g_receiver", 1), ("g_trigger", 1), ("g_mag", 1)), H = 4.0, Dmg = 30, Hit = 0.68, Range = 4.5, Noise = 6, Ammo = 0.5, Multi = 1, Val = 30 },
            ["hunting"] = new WeaponDef { Name = "Hunting rifle", Armory = 2, Cost = Gd.D(("nails", 1)), Parts = P(("g_barrel", 1), ("g_receiver", 1), ("g_stock", 1), ("g_trigger", 1)), H = 5.0, Dmg = 60, Hit = 0.82, Range = 8.5, Noise = 8, Ammo = 1.0, Multi = 1, Val = 42 },
            ["shotgun"] = new WeaponDef { Name = "Shotgun", Armory = 2, Cost = Gd.D(("nails", 1)), Parts = P(("g_barrel", 2), ("g_receiver", 1), ("g_stock", 1), ("g_trigger", 1)), H = 5.0, Dmg = 50, Hit = 0.8, Range = 3.5, Noise = 9, Ammo = 1.0, Multi = 2, Val = 45 },
            ["rifle"] = new WeaponDef { Name = "AR-pattern rifle", Armory = 3, Cost = Gd.D(("plastic", 2)), Parts = P(("g_barrel", 1), ("g_receiver", 1), ("g_stock", 1), ("g_trigger", 1), ("g_mag", 2)), H = 6.0, Dmg = 40, Hit = 0.86, Range = 7.0, Noise = 8, Ammo = 1.2, Multi = 2, Val = 70 },
        };
        public static readonly string[] WEAPON_ORDER = { "rifle", "hunting", "shotgun", "pistol", "pipe" };

        /// <summary>gun_hit(w: Dictionary). Defaults mirror w.get("hit", 0.45) / w.get("dmg", 35).</summary>
        public static double GunHit(WeaponDef w) => (w.Hit ?? 0.45) + ((w.Dmg ?? 35) - 35.0) * 0.003;

        public static readonly Dictionary<string, GPartDef> GPARTS = new Dictionary<string, GPartDef>
        {
            ["g_barrel"] = new GPartDef { Name = "Barrel", Cost = Gd.D(("metal", 4)), H = 3.0, Val = 8 },
            ["g_receiver"] = new GPartDef { Name = "Receiver", Cost = Gd.D(("metal", 5), ("nails", 2)), H = 4.0, Val = 12 },
            ["g_stock"] = new GPartDef { Name = "Stock", Cost = Gd.D(("wood", 3), ("nails", 1)), H = 2.0, Val = 4 },
            ["g_trigger"] = new GPartDef { Name = "Trigger group", Cost = Gd.D(("metal", 2), ("nails", 2), ("wire", 1)), H = 3.0, Val = 8 },
            ["g_mag"] = new GPartDef { Name = "Magazine", Cost = Gd.D(("metal", 2), ("plastic", 2)), H = 2.0, Val = 5 },
            ["g_plate"] = new GPartDef { Name = "Armor plate", Cost = Gd.D(("metal", 6), ("plastic", 1)), H = 3.0, Val = 9 },
            ["g_carrier"] = new GPartDef { Name = "Vest carrier", Cost = Gd.D(("cloth", 6), ("plastic", 1)), H = 3.0, Val = 8 },
            ["g_shell"] = new GPartDef { Name = "Helmet shell", Cost = Gd.D(("plastic", 3), ("metal", 2), ("cloth", 1)), H = 3.0, Val = 8 },
        };
        public static readonly string[] GPART_ORDER = { "g_barrel", "g_receiver", "g_stock", "g_trigger", "g_mag", "g_plate", "g_carrier", "g_shell" };

        public static readonly Dictionary<string, ArmorDef> ARMOR = new Dictionary<string, ArmorDef>
        {
            ["soft_vest"] = new ArmorDef { Name = "Soft vest", Slot = "vest", Armor = 0.25, Armory = 1, Cost = Gd.D(("cloth", 2)), Parts = P(("g_carrier", 1)), H = 2.0, Val = 20 },
            ["plate_vest"] = new ArmorDef { Name = "Plate carrier", Slot = "vest", Armor = 0.45, Armory = 2, Cost = Gd.D(("nails", 1)), Parts = P(("g_carrier", 1), ("g_plate", 2)), H = 3.0, Val = 45 },
            ["helmet"] = new ArmorDef { Name = "Helmet", Slot = "helm", Armor = 0.15, Armory = 1, Cost = Gd.D(("cloth", 1)), Parts = P(("g_shell", 1)), H = 2.0, Val = 18 },
        };
        public static readonly string[] ARMOR_ORDER = { "plate_vest", "soft_vest", "helmet" };
        public const double ARMOR_CAP = 0.6;

        public static readonly string[] GEAR_TRADE = { "g_barrel", "g_receiver", "g_stock", "g_trigger", "g_mag", "g_plate", "g_carrier", "g_shell", "pipe", "pistol", "hunting", "shotgun", "rifle", "soft_vest", "plate_vest", "helmet" };

        /// <summary>gear_def(id). Returns null where GDScript returned {} (callers used .is_empty()).</summary>
        public static GearDef GearDefOf(string id)
        {
            if (WEAPONS.TryGetValue(id, out var w)) return w;
            if (ARMOR.TryGetValue(id, out var a)) return a;
            return GPARTS.TryGetValue(id, out var g) ? g : null;
        }

        public static readonly Dictionary<string, Dictionary<string, double>> GEAR_DROPS = new Dictionary<string, Dictionary<string, double>>
        {
            ["gun_shop"] = Gd.D(("parts", 0.7), ("pistol", 0.25), ("hunting", 0.2), ("shotgun", 0.18), ("rifle", 0.06)),
            ["police"] = Gd.D(("parts", 0.5), ("pistol", 0.3), ("shotgun", 0.12), ("soft_vest", 0.25), ("helmet", 0.1)),
            ["checkpoint"] = Gd.D(("parts", 0.55), ("rifle", 0.15), ("plate_vest", 0.15), ("helmet", 0.25)),
            ["house"] = Gd.D(("parts", 0.02), ("hunting", 0.02)),
            ["apartment"] = Gd.D(("parts", 0.02)),
        };
        // GDScript UNARMED had no "dmg" key; GunHit treats Dmg == null as 35.
        public static readonly WeaponDef UNARMED = new WeaponDef { Name = "Bat", Hit = 0.45, Range = 1.3, Ammo = 0.0, Multi = 1 };

        public static readonly string[] PARTS = { "battery", "tires", "alternator", "fuel_pump" };
        public static readonly Dictionary<string, string> PART_LABEL = new Dictionary<string, string>
        { ["battery"] = "Battery", ["tires"] = "Tires", ["alternator"] = "Alternator", ["fuel_pump"] = "Fuel pump", ["gun_parts"] = "Gun parts" };
        public static readonly string[] VEHICLE_PARTS = { "battery", "tires", "alternator", "fuel_pump" };
        public static readonly string[] VEHICLE_NAMES = { "Pickup truck", "Cargo van", "Armored truck" };
        public static readonly Dictionary<string, Dictionary<string, double>> PART_DROPS = new Dictionary<string, Dictionary<string, double>>
        {
            ["gas_station"] = Gd.D(("tires", 0.3), ("battery", 0.3), ("fuel_pump", 0.25), ("alternator", 0.2)),
            ["parking"] = Gd.D(("battery", 0.25), ("tires", 0.3), ("alternator", 0.15), ("fuel_pump", 0.12)),
            ["factory"] = Gd.D(("alternator", 0.2), ("fuel_pump", 0.18), ("battery", 0.15), ("tires", 0.1)),
            ["warehouse"] = Gd.D(("alternator", 0.25), ("fuel_pump", 0.2), ("battery", 0.2), ("tires", 0.2)),
            ["hardware"] = Gd.D(("battery", 0.2), ("alternator", 0.1)),
            ["fire_station"] = Gd.D(("tires", 0.25), ("battery", 0.25), ("fuel_pump", 0.12)),
            ["police"] = Gd.D(("battery", 0.1)),
            ["checkpoint"] = Gd.D(("tires", 0.15)),
            ["house"] = Gd.D(("battery", 0.03), ("tires", 0.03)),
            ["apartment"] = Gd.D(("battery", 0.02)),
        };
        public static readonly string[] GARAGE_KINDS = { "gas_station", "warehouse", "police", "factory", "fire_station" };

        public static readonly Dictionary<string, VehicleDef> VEHICLES = new Dictionary<string, VehicleDef>
        {
            ["pickup"] = new VehicleDef { Name = "Pickup truck", Seats = 3, Cargo = 12, Speed = 2.0, Tank = 40, Use = 0.1, Hp = 100, Armor = 0.1, Light = true, Rh = 6.0,
                Fix = Gd.D(("metal", 8), ("wire", 2), ("copper", 1), ("elec", 2), ("plastic", 2)), Need = new[] { "battery", "tires" }, Size = new Vector3(0.52f, 0.24f, 0.26f), Color = Gd.Hex("#7d6a4f") },
            ["truck"] = new VehicleDef { Name = "Cargo truck", Seats = 3, Cargo = 30, Speed = 1.6, Tank = 70, Use = 0.18, Hp = 140, Armor = 0.12, Light = false, Rh = 9.0,
                Fix = Gd.D(("metal", 14), ("wire", 3), ("copper", 2), ("elec", 3), ("plastic", 3)), Need = new[] { "battery", "tires", "alternator" }, Size = new Vector3(0.72f, 0.34f, 0.3f), Color = Gd.Hex("#5b6460") },
            ["humvee"] = new VehicleDef { Name = "Humvee", Seats = 4, Cargo = 8, Speed = 2.1, Tank = 50, Use = 0.14, Hp = 160, Armor = 0.3, Light = true, Rh = 10.0,
                Fix = Gd.D(("metal", 16), ("wire", 3), ("copper", 2), ("elec", 4), ("plastic", 2)), Need = new[] { "battery", "tires", "fuel_pump" }, Size = new Vector3(0.56f, 0.26f, 0.32f), Color = Gd.Hex("#6b6b4a") },
            ["apc"] = new VehicleDef { Name = "APC", Seats = 10, Cargo = 16, Speed = 1.7, Tank = 90, Use = 0.26, Hp = 260, Armor = 0.5, Light = false, Rh = 20.0, Mil = true, GarageLvl = 2, MinFix = 0.35,
                Fix = Gd.D(("metal", 45), ("wire", 8), ("copper", 8), ("elec", 14), ("plastic", 6)), Need = new[] { "battery", "tires", "alternator", "fuel_pump" }, Size = new Vector3(0.74f, 0.32f, 0.36f), Color = Gd.Hex("#55603f") },
            ["tank"] = new VehicleDef { Name = "Tank", Seats = 4, Cargo = 4, Speed = 1.2, Tank = 120, Use = 0.45, Hp = 400, Armor = 0.7, Light = false, Rh = 36.0, Mil = true, GarageLvl = 3, MinFix = 0.6,
                Fix = Gd.D(("metal", 120), ("wire", 15), ("copper", 30), ("elec", 35), ("plastic", 6)), Need = new[] { "battery", "alternator", "fuel_pump" }, Size = new Vector3(0.8f, 0.3f, 0.44f), Color = Gd.Hex("#4c5638") },
        };
        public static readonly string[] VEHICLE_ORDER = { "pickup", "truck", "humvee", "apc", "tank" };
        public const int TANK_CAP = 2;
        public const int TANK_WRECKS_MAX = 2;

        public static readonly string[] EVENT_ART_TYPES = { "raid", "trader", "survivors", "sickness", "war", "family", "scavenge", "storm", "horde", "death" };
        public static readonly Dictionary<string, string> EVENT_TYPE = new Dictionary<string, string>
        {
            ["looters_gate"] = "raid", ["radio_plea"] = "survivors", ["horde_passing"] = "horde", ["refugees_gate"] = "survivors",
            ["pharmacy_truck"] = "scavenge", ["trader"] = "trader", ["sick_child"] = "sickness", ["wanderer"] = "survivors", ["storm"] = "storm",
            ["blight"] = "storm", ["brawl"] = "family", ["dog"] = "family", ["fire"] = "storm", ["_fall"] = "horde", ["_war"] = "war",
        };
        public static readonly string[][] EVENT_KEYWORDS =
        {
            new[] { "war", "war" }, new[] { "demand", "war" }, new[] { "trader", "trader" }, new[] { "horde", "horde" }, new[] { "fever", "sickness" }, new[] { "sick", "sickness" },
            new[] { "storm", "storm" }, new[] { "blight", "storm" }, new[] { "fire", "storm" }, new[] { "baby", "family" }, new[] { "born", "family" }, new[] { "partner", "family" },
            new[] { "grave", "death" }, new[] { "funeral", "death" }, new[] { "died", "death" }, new[] { "dead", "death" }, new[] { "gate", "survivors" }, new[] { "stranger", "survivors" },
            new[] { "radio", "survivors" }, new[] { "loot", "raid" }, new[] { "raid", "raid" }, new[] { "bandit", "raid" },
        };

        public static string EventType(JObject ev)
        {
            if (ev["art"] != null && EVENT_ART_TYPES.Contains((string)ev["art"])) return (string)ev["art"];
            string id = (string)ev["id"] ?? "";
            if (EVENT_TYPE.TryGetValue(id, out var t0)) return t0;
            string t = (((string)ev["title"] ?? "") + " " + ((string)ev["text"] ?? "")).ToLowerInvariant();
            foreach (var kw in EVENT_KEYWORDS)
                if (t.Contains(kw[0])) return kw[1];
            return "scavenge";
        }

        /// <summary>Returns the ORIGINAL Godot res:// path; map it to a Unity Resources path when art is imported.</summary>
        public static string EventArtPath(string typ) =>
            string.Format("res://assets/gen/events/ev_{0}.jpg", EVENT_ART_TYPES.Contains(typ) ? typ : "scavenge");

        public static VehicleDef VehDef(string t) => VEHICLES.TryGetValue(t, out var v) ? v : VEHICLES["pickup"];

        public static double VehCargoMult(string t) => 1.0 + (double)VehDef(t).Cargo / 20.0;

        public static readonly FenceDef[] FENCES =
        {
            new FenceDef { Id = "wood", Name = "Wood palisade", Hp = 90, Cost = Gd.D(("wood", 1), ("nails", 0.25)), Color = Gd.Hex("#8b6a3e"), Unlock = "" },
            new FenceDef { Id = "chain", Name = "Chain-link fence", Hp = 160, Cost = Gd.D(("metal", 1), ("wire", 1)), Color = Gd.Hex("#b4bcc0"), Unlock = "fence_1" },
            new FenceDef { Id = "sheet", Name = "Scrap-metal wall", Hp = 260, Cost = Gd.D(("metal", 1), ("nails", 0.5), ("wood", 0.5)), Color = Gd.Hex("#7d8b96"), Unlock = "" },
            new FenceDef { Id = "concrete", Name = "Concrete wall", Hp = 420, Cost = Gd.D(("metal", 2), ("drywall", 1), ("nails", 1)), Color = Gd.Hex("#cfcbc0"), Unlock = "" },
            new FenceDef { Id = "rconcrete", Name = "Reinforced concrete", Hp = 600, Cost = Gd.D(("metal", 4), ("wire", 1), ("nails", 1), ("fuel", 1)), Color = Gd.Hex("#e8e4d8"), Unlock = "fence_4" },
        };

        public static readonly int[] FENCE_DEFENSE = { 8, 14, 22, 32, 44 };

        public static readonly TowerDef[] TOWERS =
        {
            new TowerDef { Id = "watch", Name = "Watchtower", Hp = 120, Range = 6.5, Hit = 0.6, Shots = 1, Ammo = 0.0, Crew = true, Cost = Gd.D(("wood", 14), ("nails", 6)), Unlock = "",
                Desc = "Crossbows and a lookout. Needs one survivor at its base to man it. No ammo." },
            new TowerDef { Id = "mg", Name = "MG nest", Hp = 200, Range = 7.0, Hit = 0.55, Shots = 3, Ammo = 0.35, Crew = true, Cost = Gd.D(("wood", 15), ("metal", 20), ("nails", 10), ("ammo", 20)), Unlock = "tower_1",
                Desc = "Sandbagged machine gun. Manned, burns ammo, hits several at once." },
            new TowerDef { Id = "rifle", Name = "Rifle tower", Hp = 260, Range = 10.0, Hit = 0.8, Shots = 2, Ammo = 0.5, Crew = true, Cost = Gd.D(("metal", 40), ("wood", 10), ("nails", 10), ("ammo", 10)), Unlock = "tower_2",
                Desc = "Tall steel tower with marksmen. Long range." },
            new TowerDef { Id = "auto", Name = "Auto-turret", Hp = 300, Range = 8.5, Hit = 0.75, Shots = 3, Ammo = 0.3, Crew = false, Power = 2, Cost = Gd.D(("metal", 40), ("elec", 20), ("copper", 15), ("plastic", 5), ("fuel", 15)), Unlock = "tower_3",
                Desc = "Motion-tracking turret. No crew, uses ammo and 2 power." },
        };

        public static readonly Dictionary<string, BaseLevelDef[]> BASE_LEVELS = new Dictionary<string, BaseLevelDef[]>
        {
            ["main"] = new[]
            {
                new BaseLevelDef { Name = "Camp", Beds = 16, Slots = 4, Storage = 220, Defense = 0, Zone = 7.0, Cost = new Dictionary<string, double>(), Unlock = "" },
                new BaseLevelDef { Name = "Settlement", Beds = 28, Slots = 6, Storage = 400, Defense = 8, Zone = 9.0, Cost = Gd.D(("wood", 25), ("nails", 10), ("metal", 10), ("cloth", 8), ("furniture", 7), ("food", 20)), Unlock = "lvl_main_1", MinPeople = 8 },
                new BaseLevelDef { Name = "Town", Beds = 45, Slots = 8, Storage = 700, Defense = 16, Zone = 11.0, Cost = Gd.D(("wood", 50), ("metal", 35), ("nails", 20), ("drywall", 15), ("furniture", 10), ("elec", 10), ("copper", 6), ("food", 40), ("fuel", 10)), Unlock = "lvl_main_2", MinPeople = 14 },
                new BaseLevelDef { Name = "Stronghold", Beds = 70, Slots = 10, Storage = 1100, Defense = 28, Zone = 13.0, Cost = Gd.D(("metal", 100), ("wood", 70), ("nails", 40), ("wire", 25), ("elec", 20), ("copper", 12), ("drywall", 25), ("fuel", 30), ("ammo", 20)), Unlock = "lvl_main_3", MinPeople = 20 },
            },
            ["outpost"] = new[]
            {
                new BaseLevelDef { Name = "Outpost", Beds = 10, Slots = 3, Storage = 100, Defense = 0, Zone = 6.0, Cost = new Dictionary<string, double>(), Unlock = "" },
                new BaseLevelDef { Name = "Fortified Outpost", Beds = 18, Slots = 4, Storage = 200, Defense = 8, Zone = 7.5, Cost = Gd.D(("wood", 25), ("nails", 10), ("metal", 10), ("wire", 5)), Unlock = "lvl_out_1", MinPeople = 4 },
                new BaseLevelDef { Name = "Garrison", Beds = 28, Slots = 6, Storage = 350, Defense = 16, Zone = 9.0, Cost = Gd.D(("wood", 40), ("metal", 35), ("nails", 20), ("wire", 15), ("fuel", 10)), Unlock = "lvl_out_2", MinPeople = 8 },
            },
        };

        public static readonly Dictionary<string, ConversionDef> CONVERSIONS = new Dictionary<string, ConversionDef>
        {
            ["hospital"] = new ConversionDef { Name = "Hospital", Tag = "HOSPITAL", Icon = "✚", Color = Gd.Hex("#ff8a8a"), Hours = 10, Desc = "+0.5 meds/day, the wounded at its camp heal 2 hp/h faster, +6% cure odds." },
            ["farm"] = new ConversionDef { Name = "Greenhouse Farm", Tag = "FARM", Icon = "☼", Color = Gd.Hex("#9be36a"), Hours = 12, Desc = "Planters on the roof and crop rows in the lot: +5 food/day." },
            ["water"] = new ConversionDef { Name = "Water Station", Tag = "WATER", Icon = "◇", Color = Gd.Hex("#74c8ff"), Hours = 8, Desc = "Rain catch and filtered tanks: +4 water/day." },
            ["barracks"] = new ConversionDef { Name = "Barracks", Tag = "BARRACKS", Icon = "≡", Color = Gd.Hex("#d6b88a"), Hours = 8, Desc = "Bunks for survivors: +8 beds at its camp." },
            ["storage"] = new ConversionDef { Name = "Storage", Tag = "STORAGE", Icon = "▣", Color = Gd.Hex("#c8b48c"), Hours = 6, Desc = "+150 storage for every resource." },
            ["workshop"] = new ConversionDef { Name = "Workshop", Tag = "WORKSHOP", Icon = "⚒", Color = Gd.Hex("#f0b860"), Hours = 10, Desc = "+1.5 materials/day (mixed items), repairs cost 20% less (up to 40%)." },
            ["armory"] = new ConversionDef { Name = "Armory", Tag = "ARMORY", Icon = "⚔", Color = Gd.Hex("#9fb4ff"), Hours = 10, Desc = "Reloading bench: turns 0.5 metal + 0.3 plastic into 1.2 ammo/day, guards hit 3% more often (up to 9%)." },
            ["garage"] = new ConversionDef { Name = "Garage", Tag = "GARAGE", Icon = "⚡", Color = Gd.Hex("#f0d060"), Hours = 8, Desc = "Siphons and stores fuel: +0.8 fuel/day, room for 2 vehicles." },
            ["school"] = new ConversionDef { Name = "School", Tag = "SCHOOL", Icon = "★", Color = Gd.Hex("#ffe08a"), Hours = 10, Desc = "Lessons and a library: research 12% faster (up to 48%), +morale." },
            ["kitchen"] = new ConversionDef { Name = "Kitchen", Tag = "KITCHEN", Icon = "♨", Color = Gd.Hex("#ffb070"), Hours = 6, Desc = "A mess hall with real stoves: its camp eats 12% less food (up to 24%), +morale." },
        };
        public static readonly string[] CONV_ORDER = { "hospital", "farm", "water", "barracks", "storage", "workshop", "armory", "garage", "school", "kitchen" };

        public static readonly Dictionary<string, string> CONV_AFFINITY = new Dictionary<string, string>
        {
            ["hospital"] = "hospital", ["clinic"] = "hospital", ["pharmacy"] = "hospital", ["church"] = "hospital",
            ["supermarket"] = "farm", ["farm"] = "farm", ["gas_station"] = "garage", ["fire_station"] = "garage", ["parking"] = "garage",
            ["gun_shop"] = "armory", ["police"] = "armory", ["checkpoint"] = "armory", ["hardware"] = "workshop", ["factory"] = "workshop",
            ["warehouse"] = "storage", ["school"] = "school", ["shop"] = "kitchen", ["office"] = "school", ["apartment"] = "barracks", ["house"] = "barracks",
        };
        public static readonly Dictionary<string, double> CONV_COST = Gd.D(("wood", 8), ("nails", 4), ("furniture", 3));
        public static readonly Dictionary<string, Dictionary<string, double>> CONV_COSTS = new Dictionary<string, Dictionary<string, double>>
        {
            ["hospital"] = Gd.D(("cloth", 6), ("plastic", 4), ("furniture", 3), ("elec", 2), ("copper", 1)),
            ["farm"] = Gd.D(("wood", 8), ("nails", 3), ("plastic", 4)),
            ["water"] = Gd.D(("plastic", 8), ("metal", 4), ("wire", 3)),
            ["barracks"] = Gd.D(("furniture", 6), ("cloth", 6), ("wood", 3)),
            ["storage"] = Gd.D(("wood", 8), ("nails", 4), ("metal", 3)),
            ["workshop"] = Gd.D(("metal", 6), ("elec", 4), ("copper", 3), ("wood", 2)),
            ["armory"] = Gd.D(("metal", 8), ("wood", 4), ("nails", 3)),
            ["garage"] = Gd.D(("metal", 6), ("elec", 3), ("copper", 3), ("plastic", 3)),
            ["school"] = Gd.D(("furniture", 6), ("wood", 4), ("elec", 2), ("cloth", 3)),
            ["kitchen"] = Gd.D(("metal", 4), ("wood", 4), ("furniture", 3), ("plastic", 2)),
        };

        public static readonly TrapDef[] TRAPS =
        {
            new TrapDef { Id = "spikes", Name = "Spike pit", Hp = 6, Kill = 0.75, Slow = 0.0, Cost = Gd.D(("wood", 6), ("nails", 2)), Unlock = "",
                Desc = "Sharpened stakes in a shallow pit. Kills most of what falls in, clogs after ~6." },
            new TrapDef { Id = "wire", Name = "Razor wire", Hp = 10, Kill = 0.35, Slow = 0.9, Cost = Gd.D(("wire", 8), ("metal", 4), ("fuel", 1)), Unlock = "fence_1",
                Desc = "Coils that tangle and cut. Holds the dead in place for the guards, wears out after ~10." },
            new TrapDef { Id = "lure", Name = "Noise lure", Hp = 12, Kill = 0.0, Slow = 2.2, Cost = Gd.D(("metal", 3), ("elec", 2), ("copper", 1), ("plastic", 1), ("fuel", 1)), Unlock = "", Fx = "lure", R = 7.0, Wear = 0.25,
                Desc = "Tin cans and a wind-up radio on a post. Draws wandering dead off their path and keeps them milling around it." },
            new TrapDef { Id = "tripwire", Name = "Tripwire alarm", Hp = 14, Kill = 0.2, Slow = 0.4, Cost = Gd.D(("wire", 3), ("metal", 2)), Unlock = "", Fx = "alarm", R = 4.0,
                Desc = "Fishing line strung to a string of bells. Rings when something walks through, and the nearest guards come to put it down." },
            new TrapDef { Id = "barbed", Name = "Barbed wire tangle", Hp = 16, Kill = 0.15, Slow = 1.4, Cost = Gd.D(("wire", 7), ("wood", 3)), Unlock = "",
                Desc = "Loose barbed wire staked low across the ground. Snags legs and holds the dead for a long time; outlasts razor wire." },
            new TrapDef { Id = "fire", Name = "Fire trench", Hp = 4, Kill = 0.65, Slow = 0.3, Cost = Gd.D(("wood", 4), ("cloth", 2), ("fuel", 4)), Unlock = "fence_2", Fx = "fire", R = 1.5,
                Desc = "A fuel-soaked trench with a flare on a pull line. Catches whatever walks in and the ones beside it; burns out fast." },
            new TrapDef { Id = "deadfall", Name = "Log deadfall", Hp = 3, Kill = 0.9, Slow = 0.0, Cost = Gd.D(("wood", 8), ("nails", 2)), Unlock = "",
                Desc = "A heavy log rigged over the street on a trigger stick. Almost never misses, but it needs resetting after a few drops." },
        };
        public const int TRAP_LEVELS = 3;
        public const string TRAP_L3_UNLOCK = "fence_2";
        public static readonly Dictionary<string, double> GATE_COST = Gd.D(("metal", 8), ("wood", 4), ("nails", 3));
        public const double GATE_HP_MULT = 2.0;
        public const int TRAP_MAX = 40;

        // ---------------- loaded JSON (kept dynamic, see header) ----------------
        public JObject Sites = new JObject();
        public JObject Research = new JObject();                 // research.json "techs"
        public List<string> ResearchCats = new List<string>();
        public JObject ResearchCatsName = new JObject();
        public JObject ResearchV03Map = new JObject();
        public Dictionary<string, List<string>> ResearchByCat = new Dictionary<string, List<string>>();
        public JObject Scavenge = new JObject();
        public JArray Events = new JArray();
        public JObject Camps = new JObject();

        // ---------------- world state ----------------
        public int W = 0;
        public int H = 0;
        public int TS = 16;
        public byte[] Tiles = new byte[0];
        public int[] LotId = new int[0];
        public byte[] DistrictMap = new byte[0];
        public List<Lot> Lots = new List<Lot>();
        public List<District> Districts = new List<District>();
        public List<int> Landmarks = new List<int>();
        public Vector2Int Chunks = new Vector2Int(2, 2);
        public Vector2Int ChunkPx = new Vector2Int(1600, 1040);
        public int OverviewScale = 4;

        public int CW = 0;
        public int CHt = 0;
        public RectInt CityRect = new RectInt();
        public int CityLots = 0;
        public Vector2Int ChunkOff = new Vector2Int(2, 2);
        public int WorldSeed = -1;
        byte[] _cityTiles = new byte[0];
        int[] _cityLotId = new int[0];
        List<Lot> _cityLotsArr = new List<Lot>();
        public Dictionary<string, int> ForestCounts = new Dictionary<string, int>();
        public long ForestMs = 0;
        public List<long> ForestTiming = new List<long>();

        /// <summary>GDScript _init().</summary>
        public Data()
        {
            Sites = (JObject)DataLoader.Load("Data/fob_sites");
            var rj = (JObject)DataLoader.Load("Data/research");
            Research = (JObject)rj["techs"];
            ResearchCats = rj["cats"].ToObject<List<string>>();
            ResearchCatsName = (JObject)rj["cat_names"];
            ResearchV03Map = (JObject)rj["v03_map"];
            ResearchItems();
            foreach (var c in ResearchCats) ResearchByCat[c] = new List<string>();
            foreach (var kv in Research) ResearchByCat[(string)kv.Value["cat"]].Add(kv.Key);
            // GDScript sort_custom is unstable; equal tiers keep JSON order here (stable).
            foreach (var c in ResearchCats)
                Gd.StableSort(ResearchByCat[c], (a, b) => ((int)(double)Research[a]["tier"]).CompareTo((int)(double)Research[b]["tier"]));
            Scavenge = (JObject)DataLoader.Load("Data/scavenge");
            Events = DataLoader.Load("Data/events") as JArray ?? new JArray();
            Camps = (JObject)DataLoader.Load("Data/camps");
            LoadCity();
            AddFireStations();
            LoadRoadgraph();
            _cityTiles = (byte[])Tiles.Clone(); _cityLotId = (int[])LotId.Clone();
            _cityLotsArr = Lots.Select(l => l.Clone()).ToList();
            RgSaveCity();
            BuildLabels();
        }

        void LoadCity()
        {
            var c = (JObject)DataLoader.Load("Map/city");
            CW = (int)c["W"]; CHt = (int)c["H"]; TS = (int)c["TS"];
            W = CW + 2 * OFF; H0 = CHt + 2 * OFF; H = H0 + BAND_H;
            CityRect = new RectInt(OFF, OFF, CW, CHt);
            InitRegions();
            Chunks = new Vector2Int((int)c["chunks"][0], (int)c["chunks"][1]);
            ChunkPx = new Vector2Int((int)c["chunk_px"][0], (int)c["chunk_px"][1]);
            ChunkOff = new Vector2Int(OFF * TS / ChunkPx.x, OFF * TS / ChunkPx.y);
            byte[] ct = Convert.FromBase64String((string)c["tiles"]);
            byte[] cd = Convert.FromBase64String((string)c["district_map"]);
            int[] clid;
            if (c["lot_id32"] != null)
            {
                clid = Gd.ToInt32Array(Convert.FromBase64String((string)c["lot_id32"]));
            }
            else
            {
                byte[] raw = Convert.FromBase64String((string)c["lot_id"]);
                clid = new int[CW * CHt];
                for (int i = 0; i < CW * CHt; i++) clid[i] = BitConverter.ToInt16(raw, i * 2); // decode_s16 (LE)
            }

            // Godot did this with Image fill/blit; equivalent direct array ops:
            Tiles = new byte[W * H];
            for (int i = 0; i < Tiles.Length; i++) Tiles[i] = (byte)(i / W >= H0 ? T_VOID : T_FOREST);
            for (int y = 0; y < CHt; y++) Buffer.BlockCopy(ct, y * CW, Tiles, (y + OFF) * W + OFF, CW);
            // RGBA8 image filled with Color(1,1,1,1) == 0xFFFFFFFF == -1 per int32.
            LotId = new int[W * H];
            for (int i = 0; i < LotId.Length; i++) LotId[i] = -1;
            for (int y = 0; y < CHt; y++) Array.Copy(clid, y * CW, LotId, (y + OFF) * W + OFF, CW);
            OverviewScale = c["overview_scale"] != null ? (int)c["overview_scale"] : 4;
            Lots = new List<Lot>();
            foreach (JArray la in (JArray)c["lots"])
            {
                var L = Lot.FromJArray(la);
                L.X += OFF; L.Y += OFF; L.DoorX += OFF; L.DoorY += OFF;
                Lots.Add(L);
            }
            CityLots = Lots.Count;
            Districts = new List<District>();
            foreach (JObject d in (JArray)c["districts"])
            {
                Districts.Add(new District
                {
                    Id = (string)d["id"], Name = (string)d["name"], Kind = (string)d["kind"], Region = (string)d["region"],
                    C = new[] { (double)d["c"][0] + OFF, (double)d["c"][1] + OFF },
                });
            }
            Landmarks = ((JArray)c["landmarks"]).Select(t => (int)(double)t).ToList();

            int nd = Districts.Count;
            Districts.Add(new District { Id = "north_woods", Name = "North Woods", Kind = "forest", C = new[] { OFF + CW * 0.5, OFF * 0.45 } });
            Districts.Add(new District { Id = "south_hollow", Name = "South Hollow", Kind = "forest", C = new[] { OFF + CW * 0.5, H0 - OFF * 0.45 } });
            Districts.Add(new District { Id = "west_shore", Name = "West Shore", Kind = "forest", C = new[] { OFF * 0.3, OFF + CHt * 0.5 } });
            Districts.Add(new District { Id = "east_ridge", Name = "East Ridge", Kind = "forest", C = new[] { W - OFF * 0.45, OFF + CHt * 0.5 } });

            int dtown = Districts.Count;
            RectInt rt0 = REGIONS["small_town"].Rect; RectInt rf0 = REGIONS["farmland"].Rect;
            Districts.Add(new District { Id = "small_town", Name = "Small Town", Kind = "town", Region = "small_town", C = new double[] { Gd.Center(rt0).x, Gd.Center(rt0).y } });
            Districts.Add(new District { Id = "farmland", Name = "Farmland", Kind = "farmland", Region = "farmland", C = new double[] { Gd.Center(rf0).x, Gd.Center(rf0).y } });
            int xsplit = (Gd.EndX(rt0) + rf0.x) / 2;
            var dd = new byte[W * H];
            for (int y = 0; y < H; y++)
            {
                if (y >= H0)
                {
                    for (int x = 0; x < W; x++) dd[y * W + x] = (byte)(x < xsplit ? dtown : dtown + 1);
                    continue;
                }
                for (int x = 0; x < W; x++)
                {
                    int up = OFF - y, dn = y - (OFF + CHt - 1), lf = OFF - x, rt = x - (OFF + CW - 1);
                    int m = Math.Max(Math.Max(up, dn), Math.Max(lf, rt));
                    if (m <= 0) continue;
                    dd[y * W + x] = (byte)(nd + (m == up ? 0 : (m == dn ? 1 : (m == lf ? 2 : 3))));
                }
            }
            for (int y = 0; y < CHt; y++) Buffer.BlockCopy(cd, y * CW, dd, (y + OFF) * W + OFF, CW);
            DistrictMap = dd;
        }

        public bool InCity(Vector2Int p) => Gd.Has(CityRect, p);

        void InitRegions()
        {
            int y0 = H0 + BAND_VOID;
            int rh = BAND_H - 2 * BAND_VOID;
            var town = new RectInt(BAND_VOID, y0, 400, rh);
            var farm = new RectInt(W - BAND_VOID - 440, y0, 440, rh);
            REGIONS = new Dictionary<string, RegionDef>
            {
                ["city"] = new RegionDef { Id = "city", Name = "The City", Rect = CityRect, Locked = false, Zdens = 1.0 },
                ["forest"] = new RegionDef { Id = "forest", Name = "The Forest", Rect = new RectInt(0, 0, W, H0), Locked = false, Zdens = 1.0 },
                ["small_town"] = new RegionDef { Id = "small_town", Name = "Small Town", Rect = town, Locked = false, Zdens = 0.8 },
                ["farmland"] = new RegionDef { Id = "farmland", Name = "Farmland", Rect = farm, Locked = false, Zdens = 0.4 },
                ["military"] = new RegionDef { Id = "military", Name = "Military Base", Rect = new RectInt(), Locked = true, Zdens = 1.0 },
                ["highway"] = new RegionDef { Id = "highway", Name = "Highway", Rect = new RectInt(), Locked = true, Zdens = 1.0 },
            };
        }

        public string RegionAt(Vector2Int p)
        {
            if (!InMap(p)) return "";
            if (p.y < H0) return Gd.Has(CityRect, p) ? "city" : "forest";
            foreach (var rid in new[] { "small_town", "farmland" })
                if (Gd.Has(REGIONS[rid].Rect, p)) return rid;
            return "";
        }

        public double RegionZdens(Vector2Int p)
        {
            string r = RegionAt(p);
            return r != "" ? REGIONS[r].Zdens : 0.0;
        }

        public bool InBand(Vector2Int p) => p.y >= H0 && InMap(p);

        public bool IsWild(Vector2Int p) => InMap(p) && !Gd.Has(CityRect, p);

        public Vector2Int CityTile(Vector2Int p) => p + new Vector2Int(OFF, OFF);

        public void SetWorldSeed(int s)
        {
            if (s == WorldSeed) return;
            long t0 = Gd.TicksMsec();
            WorldChanging?.Invoke();
            WorldSeed = s;
            Tiles = (byte[])_cityTiles.Clone(); LotId = (int[])_cityLotId.Clone();
            Lots = _cityLotsArr.Select(l => l.Clone()).ToList();
            _lotQuads.Clear();
            List<long> fTiming = new List<long>();
            if (ForestGenFactory != null)
            {
                var f = ForestGenFactory(this);
                f.Generate(s);
                ForestCounts = new Dictionary<string, int>(f.Counts);
                fTiming = f.Timing;
            }
            else
            {
                Debug.LogWarning("Data.SetWorldSeed: ForestGenFactory not set (forest.gd not ported); skipping forest pass.");
                ForestCounts = new Dictionary<string, int>();
            }
            RegionLots0 = Lots.Count;
            var rgen = new Regions(this);
            rgen.Generate(s);
            RegionCounts = new Dictionary<string, int>(rgen.Counts);
            long tg = Gd.TicksMsec();
            RgRestoreCity();
            ForestGraph();
            SeamGraph();
            ForestTiming = new List<long>(fTiming) { Gd.TicksMsec() - tg };
            BuildLabels();
            ForestMs = Gd.TicksMsec() - t0;
            WorldChanged?.Invoke();
        }

        // ---------------- road graph ----------------
        public const int RG_CELL = 16;
        public List<double> RgX = new List<double>();
        public List<double> RgY = new List<double>();
        public List<int> RgEa = new List<int>();
        public List<int> RgEb = new List<int>();
        public List<byte> RgEt = new List<byte>();
        // PackedInt32Array per node. GDScript had to copy/append/reassign because packed arrays are
        // value-like; in C# the List is a reference so we append in place.
        public List<List<int>> RgAdj = new List<List<int>>();
        public Dictionary<Vector2Int, List<int>> RgCells = new Dictionary<Vector2Int, List<int>>();
        public Dictionary<Vector2Int, List<int>> RgEcells = new Dictionary<Vector2Int, List<int>>();
        public double RgAngle = 30.0;
        public JArray RgCheck = new JArray();

        void LoadRoadgraph()
        {
            var c = DataLoader.Load("Map/roadgraph") as JObject;
            if (c == null || c["nodes"] == null) return;
            double q = (double)c["q"];
            RgAngle = c["angle"] != null ? (double)c["angle"] : 30.0;
            RgCheck = c["check"] as JArray ?? new JArray();
            int[] nb = Gd.ToInt32Array(Convert.FromBase64String((string)c["nodes"]));
            int n = nb.Length / 2;
            RgX = new List<double>(new double[n]); RgY = new List<double>(new double[n]);
            RgAdj = new List<List<int>>(n);
            var adjTmp = new List<int>[n];
            for (int i = 0; i < n; i++)
            {
                RgX[i] = (double)nb[i * 2] / q + OFF;
                RgY[i] = (double)nb[i * 2 + 1] / q + OFF;
                adjTmp[i] = new List<int>();
                var cell = new Vector2Int((int)RgX[i] / RG_CELL, (int)RgY[i] / RG_CELL);
                if (!RgCells.ContainsKey(cell)) RgCells[cell] = new List<int>();
                RgCells[cell].Add(i);
            }
            int[] eb = Gd.ToInt32Array(Convert.FromBase64String((string)c["edges"]));
            int m = eb.Length / 3;
            RgEa = new List<int>(new int[m]); RgEb = new List<int>(new int[m]); RgEt = new List<byte>(new byte[m]);
            for (int e = 0; e < m; e++)
            {
                int a = eb[e * 3], b = eb[e * 3 + 1];
                RgEa[e] = a; RgEb[e] = b; RgEt[e] = (byte)eb[e * 3 + 2];
                adjTmp[a].Add(e); adjTmp[b].Add(e);
                if (RgEt[e] == 1)
                {
                    Vector2 mid = (RgPos(a) + RgPos(b)) * 0.5f;
                    var cell2 = new Vector2Int((int)mid.x / RG_CELL, (int)mid.y / RG_CELL);
                    if (!RgEcells.ContainsKey(cell2)) RgEcells[cell2] = new List<int>();
                    RgEcells[cell2].Add(e);
                }
            }
            for (int i = 0; i < n; i++) RgAdj.Add(adjTmp[i]);
        }

        public const int FG = 4;
        public int CityPosts = 0;
        public int CityEdges = 0;

        sealed class RgSnapshot
        {
            public List<double> X, Y; public List<int> Ea, Eb; public List<byte> Et;
            public List<List<int>> Adj; public Dictionary<Vector2Int, List<int>> Cells, Ecells;
        }
        RgSnapshot _rgC;

        void RgSaveCity()
        {
            CityPosts = RgX.Count; CityEdges = RgEa.Count;
            _rgC = new RgSnapshot
            {
                X = new List<double>(RgX), Y = new List<double>(RgY), Ea = new List<int>(RgEa), Eb = new List<int>(RgEb),
                Et = new List<byte>(RgEt), Adj = DeepPackedArr(RgAdj), Cells = DeepPackedDict(RgCells), Ecells = DeepPackedDict(RgEcells),
            };
        }

        void RgRestoreCity()
        {
            if (_rgC == null) return;
            RgX = new List<double>(_rgC.X); RgY = new List<double>(_rgC.Y); RgEa = new List<int>(_rgC.Ea); RgEb = new List<int>(_rgC.Eb);
            RgEt = new List<byte>(_rgC.Et); RgAdj = DeepPackedArr(_rgC.Adj); RgCells = DeepPackedDict(_rgC.Cells);
            RgEcells = DeepPackedDict(_rgC.Ecells);
        }

        static List<List<int>> DeepPackedArr(List<List<int>> a) => a.Select(x => new List<int>(x)).ToList();

        static Dictionary<Vector2Int, List<int>> DeepPackedDict(Dictionary<Vector2Int, List<int>> d)
        {
            var o = new Dictionary<Vector2Int, List<int>>();
            foreach (var kv in d) o[kv.Key] = new List<int>(kv.Value);
            return o;
        }

        bool FgOpen(int x, int y)
        {
            if (x < 0 || y < 0 || x >= W || y >= H) return false;
            int t = Tiles[y * W + x];
            return t != T_WATER && t != T_BLD && t != T_VOID;
        }

        void ForestGraph()
        {
            var gid = new Dictionary<Vector2Int, int>();
            int gw = W / FG + 1;
            for (int gy = 0; gy < H / FG + 1; gy++)
                for (int gx = 0; gx < gw; gx++)
                {
                    int x = gx * FG + 2, y = gy * FG + 2;
                    if (x >= W || y >= H) continue;
                    if (Gd.Has(Gd.Grow(CityRect, 1), new Vector2Int(x, y))) continue;
                    if (!FgOpen(x, y)) continue;
                    gid[new Vector2Int(gx, gy)] = RgAddPost(x + 0.5, y + 0.5);
                }
            RectInt cr0 = CityRect;
            var offs = new[] { new Vector2Int(1, 0), new Vector2Int(0, 1), new Vector2Int(1, 1), new Vector2Int(1, -1) };
            foreach (var kv in gid)
            {
                int a = kv.Value;
                Vector2Int gv = kv.Key;
                int x0 = gv.x * FG + 2, y0 = gv.y * FG + 2;
                foreach (var o in offs)
                {
                    if (!gid.TryGetValue(gv + o, out int h)) continue;
                    bool ok = true;
                    for (int i = 1; i < FG; i++)
                    {
                        int x = x0 + o.x * i, y = y0 + o.y * i;
                        int t = Tiles[y * W + x];
                        if (t == T_WATER || t == T_BLD || t == T_VOID || Gd.Has(cr0, new Vector2Int(x, y))) { ok = false; break; }
                    }
                    if (!ok) continue;
                    int e = RgEa.Count;
                    RgEa.Add(a); RgEb.Add(h); RgEt.Add(0);
                    RgAdj[a].Add(e);
                    RgAdj[h].Add(e);
                }
            }

            Rect cr = Gd.ToRect(CityRect);
            foreach (var kv in gid)
            {
                int a = kv.Value;
                Vector2 p = RgPos(a);
                double dx = Math.Max(0.0, Math.Max(cr.x - p.x, p.x - cr.xMax));
                double dy = Math.Max(0.0, Math.Max(cr.y - p.y, p.y - cr.yMax));
                if (Math.Max(dx, dy) > 6.0) continue;
                var near = new List<(double d, int n)>();
                foreach (int n in RgNodesIn(new Rect(p - new Vector2(7, 7), new Vector2(14, 14))))
                {
                    if (n >= CityPosts) continue;
                    near.Add((Vector2.Distance(RgPos(n), p), n));
                }
                Gd.StableSort(near, (u, v) => u.d.CompareTo(v.d)); // GDScript: unstable sort on distance only
                int made = 0;
                foreach (var e in near)
                {
                    if (made >= 2 || e.d > 7.0) break;
                    if (RgTryEdge(a, e.n, true)) made += 1;
                }
            }
        }

        public const double SEAM_BAND = 9.0;
        public const double SEAM_R = 8.5;
        public const double SEAM_GAP = 3.6;
        public const double SEAM_TWIN = 2.6;
        public const int SEAM_LINKS = 5;
        public int SeamPosts = 0;
        public int SeamEdges = 0;
        public int SeamFirstPost = 0;

        public double BoundaryDist(Vector2 p)
        {
            Rect cr = Gd.ToRect(CityRect);
            if (Gd.Has(cr, p))
                return -Math.Min(Math.Min(p.x - cr.x, cr.xMax - p.x), Math.Min(p.y - cr.y, cr.yMax - p.y));
            double dx = Math.Max(0.0, Math.Max(cr.x - p.x, p.x - cr.xMax));
            double dy = Math.Max(0.0, Math.Max(cr.y - p.y, p.y - cr.yMax));
            return Math.Max(dx, dy);
        }

        bool RgLinked(int a, int b)
        {
            foreach (int e in RgAdj[a])
                if (RgOther(e, a) == b) return true;
            return false;
        }

        void SeamGraph()
        {
            SeamFirstPost = RgX.Count;
            int e0 = RgEa.Count;

            var gp = new Dictionary<Vector2Int, int>();
            RectInt cr = CityRect;
            int band = (int)SEAM_BAND - 2;
            for (int gy = 0; gy < H / FG + 1; gy++)
                for (int gx = 0; gx < W / FG + 1; gx++)
                {
                    int x = gx * FG + 2, y = gy * FG + 2;
                    var q = new Vector2Int(x, y);
                    if (!Gd.Has(cr, q)) continue;
                    if (x - cr.x > band && Gd.EndX(cr) - 1 - x > band && y - cr.y > band && Gd.EndY(cr) - 1 - y > band) continue;
                    if (!FgOpen(x, y)) continue;
                    var c = new Vector2(x + 0.5f, y + 0.5f);
                    if (RgNodeNear(c, SEAM_GAP) >= 0) continue;
                    gp[new Vector2Int(gx, gy)] = RgAddPost(c.x, c.y);
                }
            SeamPosts = gp.Count;

            var cand = new List<int>();
            Rect crf = Gd.Grow(Gd.ToRect(cr), (float)SEAM_BAND);
            foreach (var kv in RgCells)
                foreach (int i in kv.Value)
                {
                    Vector2 p = RgPos(i);
                    if (!Gd.Has(crf, p) || Math.Abs(BoundaryDist(p)) > SEAM_BAND) continue;
                    cand.Add(i);
                }
            cand.Sort();
            float sr = (float)SEAM_R;
            foreach (int a in cand)
            {
                Vector2 pa = RgPos(a);
                bool aCity = a < CityPosts;
                bool aGap = a >= SeamFirstPost;
                var near = new List<(double d, int b)>();
                foreach (int b in RgNodesIn(new Rect(pa - new Vector2(sr, sr), new Vector2(sr, sr) * 2.0f)))
                {
                    if (b == a) continue;
                    bool bCity = b < CityPosts;
                    bool bGap = b >= SeamFirstPost;
                    if (aCity == bCity && !aGap && !bGap) continue;
                    double d = Vector2.Distance(RgPos(b), pa);
                    if (d > SEAM_R) continue;
                    near.Add((d, b));
                }
                near.Sort((u, v) => u.d != v.d ? u.d.CompareTo(v.d) : u.b.CompareTo(v.b));
                int made = 0;
                foreach (var nb in near)
                {
                    int b = nb.b;
                    bool twin = nb.d < SEAM_TWIN && aCity != (b < CityPosts);
                    if (made >= SEAM_LINKS && !twin) break;
                    if (RgLinked(a, b)) { made += 1; continue; }
                    if (RgTryEdge(a, b, true)) made += 1;
                }
            }
            SeamEdges = RgEa.Count - e0;
        }

        public int SnapPost(Vector2 p, float r)
        {
            int n = -1; double bd0 = r;
            foreach (int i in RgNodesIn(new Rect(p - new Vector2(r, r), new Vector2(r, r) * 2.0f)))
            {
                if (RgAdj[i].Count == 0) continue;
                double d = Vector2.Distance(RgPos(i), p);
                if (d < bd0) { bd0 = d; n = i; }
            }
            if (n < 0 || n < CityPosts) return n;
            int best = -1; double bd = SEAM_TWIN;
            float tw = (float)SEAM_TWIN;
            foreach (int i in RgNodesIn(new Rect(RgPos(n) - new Vector2(tw, tw), new Vector2(tw, tw) * 2.0f)))
            {
                if (i >= CityPosts) continue;
                double d = Vector2.Distance(RgPos(i), RgPos(n));
                if (d < bd) { bd = d; best = i; }
            }
            return best >= 0 ? best : n;
        }

        int RgAddPost(double x, double y)
        {
            int i = RgX.Count;
            RgX.Add(x); RgY.Add(y); RgAdj.Add(new List<int>());
            var cell = new Vector2Int((int)x / RG_CELL, (int)y / RG_CELL);
            if (!RgCells.ContainsKey(cell)) RgCells[cell] = new List<int>();
            RgCells[cell].Add(i);
            return i;
        }

        bool RgTryEdge(int a, int b, bool link)
        {
            int e = RgEa.Count;
            RgEa.Add(a); RgEb.Add(b); RgEt.Add(0);
            foreach (int k in RgEdgeTiles(e))
            {
                int t = Tiles[k];
                if (t == T_WATER || t == T_BLD || t == T_VOID || (!link && Gd.Has(CityRect, new Vector2Int(k % W, k / W))))
                {
                    RgEa.RemoveAt(e); RgEb.RemoveAt(e); RgEt.RemoveAt(e); // resize(e)
                    return false;
                }
            }
            RgAdj[a].Add(e);
            RgAdj[b].Add(e);
            return true;
        }

        public int RgCount() => RgX.Count;
        public int RgEdges() => RgEa.Count;
        public Vector2 RgPos(int i) => new Vector2((float)RgX[i], (float)RgY[i]);
        public int RgOther(int e, int n) => RgEa[e] == n ? RgEb[e] : RgEa[e];
        public double RgLen(int e) => Vector2.Distance(RgPos(RgEa[e]), RgPos(RgEb[e]));

        public List<int> RgNodesIn(Rect r, int cap = 2000)
        {
            var o = new List<int>();
            var c0 = new Vector2Int((int)Math.Max(0.0, r.x) / RG_CELL, (int)Math.Max(0.0, r.y) / RG_CELL);
            var c1 = new Vector2Int((int)Math.Max(0.0, r.xMax) / RG_CELL, (int)Math.Max(0.0, r.yMax) / RG_CELL);
            for (int cy = c0.y; cy < c1.y + 1; cy++)
                for (int cx = c0.x; cx < c1.x + 1; cx++)
                {
                    if (!RgCells.TryGetValue(new Vector2Int(cx, cy), out var arr)) continue;
                    foreach (int i in arr)
                    {
                        if (Gd.Has(r, RgPos(i)))
                        {
                            o.Add(i);
                            if (o.Count >= cap) return o;
                        }
                    }
                }
            return o;
        }

        public int RgNodeNear(Vector2 p, double r)
        {
            int best = -1; double bd = r;
            float rf = (float)r;
            foreach (int i in RgNodesIn(new Rect(p - new Vector2(rf, rf), new Vector2(rf, rf) * 2.0f)))
            {
                double d = Vector2.Distance(RgPos(i), p);
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        public int RgCrossingNear(Vector2 p, double r)
        {
            int best = -1; double bd = r;
            var c = new Vector2Int((int)p.x / RG_CELL, (int)p.y / RG_CELL);
            for (int cy = c.y - 1; cy < c.y + 2; cy++)
                for (int cx = c.x - 1; cx < c.x + 2; cx++)
                {
                    if (!RgEcells.TryGetValue(new Vector2Int(cx, cy), out var arr)) continue;
                    foreach (int e in arr)
                    {
                        Vector2 a = RgPos(RgEa[e]), b = RgPos(RgEb[e]);
                        double d = Vector2.Distance(Gd.ClosestPointToSegment(p, a, b), p);
                        Vector2 ab = b - a;
                        double t = Vector2.Dot(p - a, ab) / Math.Max(0.0001, ab.sqrMagnitude);
                        if (t < 0.0 || t > 1.0) continue;
                        if (d < bd) { bd = d; best = e; }
                    }
                }
            return best;
        }

        public List<int> RgPath(int a, int b, double maxLen = 140.0)
        {
            var o = new List<int>();
            if (a < 0 || b < 0 || a == b) return o;
            if (Vector2.Distance(RgPos(a), RgPos(b)) > maxLen) return o;
            var dist = new Dictionary<int, double> { [a] = 0.0 };
            var prev = new Dictionary<int, int>();
            var open = new List<int> { a };
            var done = new HashSet<int>();
            int guard = 0;
            while (open.Count > 0 && guard < 6000)
            {
                guard += 1;
                int bi = 0;
                for (int k = 0; k < open.Count; k++)
                    if (dist[open[k]] < dist[open[bi]]) bi = k;
                int n = open[bi];
                open.RemoveAt(bi);
                if (done.Contains(n)) continue;
                done.Add(n);
                if (n == b) break;
                foreach (int e in RgAdj[n])
                {
                    int m = RgOther(e, n);
                    if (done.Contains(m)) continue;
                    double nd = dist[n] + RgLen(e);
                    if (nd > maxLen) continue;
                    if (Vector2.Distance(RgPos(m), RgPos(b)) + nd > maxLen * 1.05) continue;
                    if (!dist.ContainsKey(m) || nd < dist[m] - 0.0001)
                    {
                        dist[m] = nd; prev[m] = e;
                        open.Add(m);
                    }
                }
            }
            if (!prev.ContainsKey(b)) return o;
            int cur = b;
            while (cur != a)
            {
                int e = prev[cur];
                o.Add(e);
                cur = RgOther(e, cur);
            }
            o.Reverse();
            return o;
        }

        public List<int> RgEdgeTiles(int e)
        {
            var o = new List<int>();
            double ax = RgX[RgEa[e]], ay = RgY[RgEa[e]];
            double bx = RgX[RgEb[e]], by = RgY[RgEb[e]];
            double dx = bx - ax, dy = by - ay;
            int n = (int)Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dy)) * 2.0) + 1;
            int last = -1;
            for (int s = 0; s < n + 1; s++)
            {
                double t = (double)s / n;
                int x = (int)Math.Floor(ax + dx * t), y = (int)Math.Floor(ay + dy * t);
                if (x < 0 || y < 0 || x >= W || y >= H) continue;
                int k = y * W + x;
                if (k != last) { o.Add(k); last = k; }
            }
            return o;
        }

        // ---------------- lots ----------------
        public const int TAKE_MIN_CELLS = 2;
        public const int TAKE_MAX_CELLS = 120;

        public string LotTakeable(int l)
        {
            if (l < 0 || l >= Lots.Count) return "Tap a building.";
            Lot L = Lots[l];
            string k = L.Kind;
            if (k == "parking") return "That's a parking lot. Tap a building.";
            if (k == "farm" && L.Landmark == "") return "That's an open field. Tap a building.";
            if (L.Cells < TAKE_MIN_CELLS) return "That building is too small for a camp.";
            if (L.Cells > TAKE_MAX_CELLS) return "That building is too big to hold.";
            return "";
        }

        public int BuildingAt(Vector2Int p)
        {
            if (!InMap(p) || Tile(p) != T_BLD) return -1;
            return LotAt(p);
        }

        readonly Dictionary<int, Vector2[]> _lotQuads = new Dictionary<int, Vector2[]>();

        public Vector2[] LotQuad(int l)
        {
            if (_lotQuads.TryGetValue(l, out var cached)) return cached;
            Lot L = Lots[l];
            double th = LotAngle(l) * Math.PI / 180.0;
            double cu = Math.Cos(th), su = Math.Sin(th);
            double u0 = 1000000000.0, u1 = -1000000000.0, v0 = 1000000000.0, v1 = -1000000000.0;
            for (int y = L.Y; y < L.Y + L.H; y++)
                for (int x = L.X; x < L.X + L.W; x++)
                {
                    if (LotId[y * W + x] != l) continue;
                    double px = (x + 0.5) * TS, py = (y + 0.5) * TS;
                    double u = px * cu + py * su, v = -px * su + py * cu;
                    u0 = Math.Min(u0, u); u1 = Math.Max(u1, u); v0 = Math.Min(v0, v); v1 = Math.Max(v1, v);
                }
            double pad = TS * 0.45;
            u0 -= pad; u1 += pad; v0 -= pad; v1 += pad;
            var q = new Vector2[4];
            var uvs = new[] { (u0, v0), (u1, v0), (u1, v1), (u0, v1) };
            for (int i = 0; i < 4; i++)
            {
                // Godot built Vector2(u, v) (float32) first, then rotated in float32; we rotate in double.
                double ux = uvs[i].Item1, uy = uvs[i].Item2;
                q[i] = new Vector2((float)(ux * cu - uy * su), (float)(ux * su + uy * cu));
            }
            _lotQuads[l] = q;
            return q;
        }

        public double LotAngle(int l) => l < CityLots ? RgAngle : 0.0;

        public int Idx(Vector2Int p) => p.y * W + p.x;

        public bool InMap(Vector2Int p) => p.x >= 0 && p.y >= 0 && p.x < W && p.y < H;

        public int Tile(Vector2Int p)
        {
            if (!InMap(p)) return T_WATER;
            return Tiles[p.y * W + p.x];
        }

        public bool Walkable(Vector2Int p)
        {
            int t = Tile(p);
            return t != T_WATER && t != T_BLD && t != T_VOID;
        }

        public bool IsRoad(Vector2Int p)
        {
            int t = Tile(p);
            return t == T_ROAD || t == T_AVE || t == T_BRIDGE;
        }

        public bool IsLand(Vector2Int p)
        {
            int t = Tile(p);
            return t != T_WATER && t != T_VOID;
        }

        public District DistrictAt(Vector2Int p)
        {
            if (!InMap(p)) return Districts[0];
            return Districts[DistrictMap[p.y * W + p.x]];
        }

        public int LotAt(Vector2Int p)
        {
            if (!InMap(p)) return -1;
            return LotId[p.y * W + p.x];
        }

        public string LotKind(int l) => Lots[l].Kind;

        public Vector2Int LotDoor(int l) => new Vector2Int(Lots[l].DoorX, Lots[l].DoorY);

        public Vector2 LotCenter(int l)
        {
            Lot L = Lots[l];
            return new Vector2((float)(L.X + L.W * 0.5), (float)(L.Y + L.H * 0.5));
        }

        public static readonly Dictionary<string, string[]> LANDMARK_NAMES = new Dictionary<string, string[]>
        {
            ["hospital"] = new[] { "St. Mary's Hospital", "Mercy General", "County Medical Center", "Harbor Hospital", "Providence Memorial", "Northside Clinic Hospital" },
            ["police"] = new[] { "5th Street Precinct", "12th Precinct", "Harbor Precinct", "Central Station", "Eastside Precinct", "North Precinct" },
            ["school"] = new[] { "Lincoln High School", "Roosevelt High", "Westbrook Academy", "Kingsgate High", "Pine Hollow High", "Bayside High" },
            ["warehouse"] = new[] { "Harlan Freight Warehouse", "Dockside Storage", "Atlas Distribution", "Flats Cold Storage", "Bayside Logistics", "Union Freight" },
            ["farm"] = new[] { "Miller Farm", "Greene Acres", "Hollow Creek Farm", "Old Orchard Farm" },
        };

        public string LotName(int l)
        {
            string k = LotKind(l);
            if (WILD_NAMES.TryGetValue(k, out var wn) && l >= CityLots)
            {
                // GDScript `%` truncates (can go negative with world_seed = -1) and Array[-i] wraps; Gd.At mirrors that.
                int i = (k == "cabin" || k == "shack") ? (l * 7 + WorldSeed) % wn.Length : (l % wn.Length);
                return Gd.At(wn, i);
            }
            if (k == "fire_station") return string.Format("Fire Station No. {0}", Lots[l].District + 1);
            if (LANDMARK_NAMES.TryGetValue(k, out var nm) && Lots[l].Landmark != "")
                return Landmarks.Contains(l) ? nm[Landmarks.IndexOf(l) % nm.Length] : nm[0];
            if (Scavenge[k] is JObject sk)
            {
                var names = sk["names"] as JArray;
                if (names != null && names.Count > 0)
                    return (string)names[l % names.Count];
                return (string)sk["name"];
            }
            return KIND_LABEL.TryGetValue(k, out var lab) ? lab : Gd.Capitalize(k);
        }

        public static readonly Dictionary<string, Dictionary<string, double>> RESEARCH_MIX = new Dictionary<string, Dictionary<string, double>>
        {
            ["farming"] = Gd.D(("wood", 0.5), ("nails", 0.2), ("plastic", 0.3)),
            ["crafting"] = Gd.D(("metal", 0.5), ("nails", 0.3), ("wood", 0.2)),
            ["housing"] = Gd.D(("wood", 0.4), ("drywall", 0.3), ("furniture", 0.2), ("nails", 0.1)),
            ["plumbing"] = Gd.D(("plastic", 0.5), ("metal", 0.4), ("nails", 0.1)),
            ["electricity"] = Gd.D(("copper", 0.4), ("elec", 0.4), ("plastic", 0.2)),
            ["diplomacy"] = Gd.D(("cloth", 0.4), ("furniture", 0.3), ("wood", 0.3)),
            ["vehicles"] = Gd.D(("metal", 0.5), ("elec", 0.2), ("copper", 0.1), ("plastic", 0.2)),
            ["aviation"] = Gd.D(("metal", 0.4), ("cloth", 0.2), ("elec", 0.2), ("copper", 0.2)),
            ["scouting"] = Gd.D(("cloth", 0.4), ("wood", 0.3), ("metal", 0.3)),
        };

        public const string ELECTRICITY = "elec_02";

        /// <summary>Expands generic "materials" research costs into specific items (mutates Research in place).</summary>
        void ResearchItems()
        {
            foreach (var kv in Research)
            {
                var t = (JObject)kv.Value;
                var c = (JObject)t["cost"];
                if (c["materials"] != null)
                {
                    double m = (double)c["materials"];
                    c.Remove("materials");
                    var mix = RESEARCH_MIX.TryGetValue((string)t["cat"], out var rm) ? rm : MAT_MIX;
                    foreach (var mk in mix)
                        c[mk.Key] = (c[mk.Key] != null ? (double)c[mk.Key] : 0.0) + Math.Max(1.0, Gd.Round(m * mk.Value));
                }
            }
            if (Research[ELECTRICITY] is JObject e)
            {
                e["name"] = "Electricity";
                e["desc"] = "Generators and salvaged copper wire hooked into the building wiring: lights come on inside your buildings at night. Research +4% faster. +1 power.";
                var unlock = (JArray)e["unlock"];
                if (!unlock.Any(x => (string)x == "lights")) unlock.Add("lights");
            }
        }

        readonly Dictionary<string, LootTable> _lootCache = new Dictionary<string, LootTable>();

        public LootTable LotLootTable(int l)
        {
            string k = LotKind(l);
            if (_lootCache.TryGetValue(k, out var hit)) return hit;
            var raw = new Dictionary<string, double[]>();
            if (Scavenge[k] is JObject sk)
            {
                foreach (var p in (JObject)sk["loot"]) raw[p.Key] = new[] { (double)p.Value[0], (double)p.Value[1] };
            }
            else
            {
                var g = GENERIC_LOOT.TryGetValue(k, out var gl) ? gl : GENERIC_LOOT["house"];
                foreach (var p in g) raw[p.Key] = new double[] { p.Value[0], p.Value[1] };
            }
            var o = ExpandMats(raw, k);
            _lootCache[k] = o;
            return o;
        }

        public LootTable ExpandMats(Dictionary<string, double[]> raw, string kind)
        {
            var o = new LootTable();
            foreach (var r in raw)
            {
                if (r.Key != "materials") { o[r.Key] = r.Value; continue; }
                var mix = LOOT_MIX.TryGetValue(kind, out var lm) ? lm : MAT_MIX;
                double[] rr = r.Value;
                foreach (var m in mix)
                    o[m.Key] = new[] { rr[0] * m.Value, rr[1] * m.Value };
            }
            return o;
        }

        public Dictionary<string, double> GutYield(int l)
        {
            double cells = Lots[l].Cells;
            double km = GUT_KIND_MULT.TryGetValue(LotKind(l), out var v) ? v : 1.0;
            double n = Math.Min(Math.Max(cells, 4.0), 60.0) * km;
            var o = new Dictionary<string, double>();
            foreach (var m in GUT_YIELD) o[m.Key] = Math.Floor(m.Value * n + 0.5);
            return o;
        }

        public double LotDanger(int l)
        {
            string k = LotKind(l);
            if (Scavenge[k] is JObject sk) return (double)sk["danger"];
            return 0.08;
        }

        public Vector2Int NearestWalkable(Vector2Int p, int maxr = 12)
        {
            if (InMap(p) && Walkable(p)) return p;
            for (int r = 1; r < maxr + 1; r++)
                for (int dy = -r; dy < r + 1; dy++)
                    for (int dx = -r; dx < r + 1; dx++)
                    {
                        if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                        var q = p + new Vector2Int(dx, dy);
                        if (InMap(q) && Walkable(q)) return q;
                    }
            return new Vector2Int(-1, -1);
        }

        public string RandomName(IRng rng) =>
            string.Format("{0} {1}", FIRST[rng.RandiRange(0, FIRST.Length - 1)], LAST[rng.RandiRange(0, LAST.Length - 1)]);

        public string RandomJob(IRng rng)
        {
            int tot = 0;
            foreach (int w in JOB_WEIGHTS) tot += w;
            int r = rng.RandiRange(1, tot);
            for (int i = 0; i < JOBS.Length; i++)
            {
                r -= JOB_WEIGHTS[i];
                if (r <= 0) return JOBS[i];
            }
            return "Civilian";
        }

        public int LandmarkNear(Vector2Int p, double r = 7.0)
        {
            foreach (int l in Landmarks)
            {
                Lot L = Lots[l];
                double dx = Math.Max(0.0, Math.Max((double)L.X - p.x, p.x - ((double)L.X + L.W - 1)));
                double dy = Math.Max(0.0, Math.Max((double)L.Y - p.y, p.y - ((double)L.Y + L.H - 1)));
                if (Math.Sqrt(dx * dx + dy * dy) <= r) return l;
            }
            return -1;
        }

        void AddFireStations()
        {
            var best = new Dictionary<int, (double d, int l)>();
            for (int l = 0; l < Lots.Count; l++)
            {
                Lot L = Lots[l];
                if (!(L.Kind == "office" || L.Kind == "apartment") || L.Landmark != "") continue;
                int cells = L.Cells;
                if (cells < 10 || cells > 40) continue;
                int di = L.District;
                double[] c = Districts[di].C;
                double d = Vector2.Distance(LotCenter(l), new Vector2((float)c[0], (float)c[1]) + new Vector2(14, 10));
                if (!best.ContainsKey(di) || d < best[di].d) best[di] = (d, l);
            }
            foreach (var kv in best) Lots[kv.Value.l].Kind = "fire_station";
        }

        public static readonly string[] LABEL_MAJOR = { "hospital", "police", "school", "fire_station", "warehouse", "supermarket", "checkpoint", "clinic", "ranger", "sawmill" };
        public static readonly string[] LABEL_MINOR = { "pharmacy", "gas_station", "hardware", "gun_shop", "church", "farm", "farmhouse" };
        public static readonly Dictionary<string, Color> LABEL_COLOR = new Dictionary<string, Color>
        {
            ["hospital"] = Gd.Hex("#ff8080"), ["clinic"] = Gd.Hex("#ff9e9e"), ["pharmacy"] = Gd.Hex("#ffb0b0"),
            ["police"] = Gd.Hex("#8fb8ff"), ["checkpoint"] = Gd.Hex("#8fb8ff"), ["gun_shop"] = Gd.Hex("#c0c8ff"),
            ["school"] = Gd.Hex("#ffe08a"), ["church"] = Gd.Hex("#f0e6c8"), ["fire_station"] = Gd.Hex("#ff9a4a"),
            ["warehouse"] = Gd.Hex("#d8c090"), ["factory"] = Gd.Hex("#c8b8a0"), ["supermarket"] = Gd.Hex("#a8e890"),
            ["gas_station"] = Gd.Hex("#f0d060"), ["hardware"] = Gd.Hex("#e0c080"), ["farm"] = Gd.Hex("#b8e080"),
            ["ranger"] = Gd.Hex("#b8e0a0"), ["sawmill"] = Gd.Hex("#e0b880"), ["farmhouse"] = Gd.Hex("#e8d8a8"),
        };
        public List<LabelLot> LabelLots = new List<LabelLot>();

        void BuildLabels()
        {
            LabelLots.Clear();
            for (int l = 0; l < Lots.Count; l++)
            {
                string k = Lots[l].Kind;
                int tier = -1;
                if (Lots[l].Landmark != "" || LABEL_MAJOR.Contains(k)) tier = 0;
                else if (LABEL_MINOR.Contains(k)) tier = 1;
                else if (k == "factory" && Lots[l].Cells >= 44) tier = 1;
                if (tier < 0) continue;
                LabelLots.Add(new LabelLot { Lot = l, Tier = tier, Name = k != "factory" ? LotName(l) : "Factory", Kind = k });
            }
            // GDScript sort_custom (unstable): tier asc, then cells desc. Stable here.
            Gd.StableSort(LabelLots, (a, b) =>
                a.Tier != b.Tier ? a.Tier.CompareTo(b.Tier) : Lots[b.Lot].Cells.CompareTo(Lots[a.Lot].Cells));
        }
    }
}
