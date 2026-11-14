// Typed replacements for the untyped GDScript Dictionaries/Arrays used in data.gd.
// Optional keys that GDScript reads with .get(key, default) are nullable here (null == key absent).
// All cost maps use double: GDScript mixed int and float values (e.g. "nails": 0.25).
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DayZero.Core
{
    /// <summary>
    /// One lot. GDScript stored lots as a 10-element Array:
    /// [0]=X [1]=Y [2]=W [3]=H [4]=Kind [5]=DoorX [6]=DoorY [7]=District [8]=Cells [9]=Landmark ("" if none).
    /// </summary>
    public sealed class Lot
    {
        public int X, Y, W, H;
        public string Kind;
        public int DoorX, DoorY, District, Cells;
        public string Landmark;

        public Lot(int x, int y, int w, int h, string kind, int doorX, int doorY, int district, int cells, string landmark)
        { X = x; Y = y; W = w; H = h; Kind = kind; DoorX = doorX; DoorY = doorY; District = district; Cells = cells; Landmark = landmark; }

        public static Lot FromJArray(JArray a) => new Lot(
            (int)(double)a[0], (int)(double)a[1], (int)(double)a[2], (int)(double)a[3], (string)a[4],
            (int)(double)a[5], (int)(double)a[6], (int)(double)a[7], (int)(double)a[8], (string)a[9] ?? "");

        public Lot Clone() => (Lot)MemberwiseClone();
    }

    public sealed class District
    {
        public string Id, Name, Kind;
        public string Region; // only set for band districts (small_town / farmland)
        public double[] C;    // centre [x, y] in tiles (GDScript Array of floats)
    }

    public sealed class RegionDef
    {
        public string Id, Name;
        public RectInt Rect;
        public bool Locked;
        public double Zdens;
    }

    public sealed class UpgradeDef
    {
        public string Name, Desc;
        public int Max;
        public bool? Slot;     // absent in GDScript for most entries
        public string Unlock;  // null if absent
        public List<Dictionary<string, double>> Cost;
    }

    /// <summary>Common shape returned by gear_def(): weapons, armor and gun parts.</summary>
    public class GearDef
    {
        public string Name;
        public int? Armory;
        public Dictionary<string, double> Cost;
        public Dictionary<string, int> Parts;
        public double? H;
        public double? Val;
    }

    public sealed class WeaponDef : GearDef
    {
        public double? Dmg;   // UNARMED has no dmg; gun_hit defaults to 35
        public double? Hit;   // gun_hit defaults to 0.45
        public double Range, Ammo;
        public int? Noise;
        public int Multi;
    }

    public sealed class ArmorDef : GearDef
    {
        public string Slot;   // "vest" | "helm"
        public double Armor;
    }

    public sealed class GPartDef : GearDef { }

    public sealed class VehicleDef
    {
        public string Name;
        public int Seats, Cargo, Tank, Hp;
        public double Speed, Use, Armor, Rh;
        public bool Light;
        public bool? Mil;
        public int? GarageLvl;
        public double? MinFix;
        public Dictionary<string, double> Fix;
        public string[] Need;
        public Vector3 Size;
        public Color Color;
    }

    public sealed class FenceDef
    {
        public string Id, Name, Unlock;
        public int Hp;
        public Dictionary<string, double> Cost;
        public Color Color;
    }

    public sealed class TowerDef
    {
        public string Id, Name, Unlock, Desc;
        public int Hp, Shots;
        public double Range, Hit, Ammo;
        public bool Crew;
        public int? Power;
        public Dictionary<string, double> Cost;
    }

    public sealed class BaseLevelDef
    {
        public string Name, Unlock;
        public int Beds, Slots, Storage, Defense;
        public double Zone;
        public int? MinPeople;
        public Dictionary<string, double> Cost;
    }

    public sealed class ConversionDef
    {
        public string Name, Tag, Icon, Desc;
        public Color Color;
    }

    public sealed class TrapDef
    {
        public string Id, Name, Unlock, Desc;
        public string Fx;      // null if absent ("lure" | "alarm" | "fire")
        public int Hp;
        public double Kill, Slow;
        public double? R, Wear;
        public Dictionary<string, double> Cost;
    }

    /// <summary>label_lots entry: GDScript [l, tier, name, kind].</summary>
    public sealed class LabelLot
    {
        public int Lot, Tier;
        public string Name, Kind;
    }

    /// <summary>Loot table: resource -> [min, max]. GDScript kept ints for non-material entries;
    /// here everything is double (check callers that used randi_range on these).</summary>
    public sealed class LootTable : Dictionary<string, double[]> { }
}
