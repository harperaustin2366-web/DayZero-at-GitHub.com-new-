// Expansion world systems (design/SYSTEMS_30.md). Active only when Game.ExpansionRules is on;
// with the flag off nothing here runs and the original v0.10.8a sim stays bit-exact.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace DayZero.Core
{
    public enum ExpSeason { Spring = 0, Summer = 1, Autumn = 2, Winter = 3 }
    // Same order as Runtime FxWeatherKind (Clear, Rain, Storm, Fog, Snow). Heatwave folds into Clear, Blizzard into Snow (lean v1).
    public enum ExpWeather { Clear = 0, Rain = 1, Storm = 2, Fog = 3, Snow = 4 }

    public sealed partial class Game
    {
        // ---------------- #3 noise meter ----------------
        // Separate RNG so expansion rolls never shift the main sim's RNG call order.
        readonly GdRng _xrng = new GdRng();
        // per-base source breakdown for the current hour (not saved)
        readonly Dictionary<int, Dictionary<string, float>> _noiseSrc = new Dictionary<int, Dictionary<string, float>>();

        public void AddNoise(int baseIdx, float amt, string src)
        {
            var b = BaseAny(baseIdx);
            if (b == null || amt <= 0f) return;
            amt *= CurrentWeather == ExpWeather.Rain ? 0.6f : CurrentWeather == ExpWeather.Fog ? 0.8f : 1f;   // #2: rain damps, fog muffles
            b.Noise = Mathf.Clamp(b.Noise + amt, 0f, 100f);
            if (!_noiseSrc.TryGetValue(baseIdx, out var d)) _noiseSrc[baseIdx] = d = new Dictionary<string, float>();
            d[src ?? "Other"] = (d.TryGetValue(src ?? "Other", out var v) ? v : 0f) + amt;
        }

        /// <summary>0 Quiet (0-24), 1 Heard (25-49), 2 Loud (50-74), 3 Beacon (75-100).</summary>
        public int NoiseBand(int baseIdx)
        {
            var b = BaseAny(baseIdx);
            if (b == null) return 0;
            return b.Noise >= 75f ? 3 : b.Noise >= 50f ? 2 : b.Noise >= 25f ? 1 : 0;
        }

        public Dictionary<string, float> NoiseSources(int baseIdx) => _noiseSrc.TryGetValue(baseIdx, out var d) ? d : null;

        /// <summary>Hourly: band spawns (cap 4 per camp), then decay 15% (10% at night).</summary>
        void StepExpNoise()
        {
            if (C == null) return;
            bool night = IsNight();
            foreach (var b in C.Bases)
            {
                _xrng.SetSeed((S.Seed ^ 0x5EED) + (long)(S.T * 7.0) + b.Id * 131L);
                int band = NoiseBand(b.Id);
                int k = band == 3 ? 4 : band == 2 ? _xrng.RandiRange(2, 3) : band == 1 ? 1 : 0;
                if (MaxZombies <= 0) k = 0;
                k = Math.Min(4, k);
                for (int i = 0; i < k; i++)
                {
                    double a = _xrng.Randf() * V.TAU;
                    var t = V.I(V.F(b.Pos).Add(V.F(Math.Cos(a), Math.Sin(a)).Mul(_xrng.RandfRange(12.0, 18.0))));
                    t = new Vector2Int(V.Clampi(t.x, 0, D.W - 1), V.Clampi(t.y, 0, D.H - 1));
                    SpawnZombie(Ti(D.NearestWalkable(t)), 0);
                }
                if (k >= 2) AddLog(string.Format("The gunfire carried. More of the dead are drifting toward {0}.", b.Name), "loss");
                b.Noise *= night ? 0.90f : 0.85f;
                if (b.Noise < 0.05f) b.Noise = 0f;
                _noiseSrc.Remove(b.Id);
            }
        }

        // ---------------- #2 weather and seasons ----------------
        public const int SEASON_DAYS = 14;
        // weather chance per season [Clear, Rain, Storm, Fog, Snow] (percent)
        static readonly int[][] WEATHER_TABLE =
        {
            new[] { 45, 30, 8, 12, 5 },    // Spring (blizzard 5 -> snow)
            new[] { 70, 15, 10, 5, 0 },    // Summer (heatwave 15 -> clear)
            new[] { 43, 30, 12, 15, 0 },   // Autumn (heatwave 3 -> clear)
            new[] { 35, 10, 5, 10, 40 },   // Winter (snow 30 + blizzard 10)
        };
        static readonly string[] SEASON_NAME = { "Spring", "Summer", "Autumn", "Winter" };

        // Day 1 is Summer day 8: days 1-7 Summer, 8-21 Autumn, 22-35 Winter, 36-49 Spring, ...
        int SeasonIndexOfDay(int day) => ((SEASON_DAYS + 7 + day - 1) / SEASON_DAYS) % 4;
        int SeasonDayOfDay(int day) => (SEASON_DAYS + 7 + day - 1) % SEASON_DAYS + 1;

        public ExpSeason CurrentSeason => S == null ? ExpSeason.Summer : (ExpSeason)SeasonIndexOfDay(Day());
        public int CurrentSeasonDay => S == null ? 1 : SeasonDayOfDay(Day());
        public ExpWeather CurrentWeather => !ExpansionRules || S == null ? ExpWeather.Clear : (ExpWeather)V.Clampi(S.Weather, 0, 4);
        public double WeatherHoursLeft => S == null ? 0.0 : Math.Max(0.0, S.WeatherEndT - S.T);

        /// <summary>0..1 for the view: kind strength, easing in the last 1.5 h of a spell.</summary>
        public float WeatherIntensity
        {
            get
            {
                var w = CurrentWeather;
                float k = w == ExpWeather.Clear ? 0f : w == ExpWeather.Rain ? 0.6f : w == ExpWeather.Storm ? 1f : w == ExpWeather.Fog ? 0.7f : 0.8f;
                return k * Mathf.Clamp((float)(WeatherHoursLeft / 1.5), 0.3f, 1f);
            }
        }

        public string SeasonLabel => string.Format("{0} {1}/{2}", SEASON_NAME[(int)CurrentSeason].ToUpperInvariant(), CurrentSeasonDay, SEASON_DAYS);

        // multipliers (only read behind ExpansionRules)
        double ExpCropMul() { switch (CurrentSeason) { case ExpSeason.Summer: return 1.2; case ExpSeason.Autumn: return 0.8; case ExpSeason.Winter: return 0.15; default: return 1.0; } }
        double ExpWaterMul()
        {
            double m;
            switch (CurrentSeason) { case ExpSeason.Spring: m = 1.2; break; case ExpSeason.Summer: m = 0.8; break; case ExpSeason.Winter: m = 0.5; break; default: m = 1.0; break; }
            if (CurrentWeather == ExpWeather.Rain || CurrentWeather == ExpWeather.Storm) m *= 1.3;   // rain catch (lean stand-in for +3/collector level)
            return m;
        }
        double ExpZombieSpeedMul()
        {
            double m = CurrentSeason == ExpSeason.Summer ? 1.1 : CurrentSeason == ExpSeason.Winter ? 0.75 : 1.0;
            if (S.Weather == (int)ExpWeather.Snow) m *= 0.8;
            return m;
        }
        double ExpRaidMul() => CurrentSeason == ExpSeason.Summer ? 1.05 : CurrentSeason == ExpSeason.Winter ? 0.85 : 1.0;
        double ExpSightMul() => CurrentWeather == ExpWeather.Fog ? 0.6 : 1.0;

        double ExpLootMul(int lot, string res)
        {
            var d = D.LotDoor(lot);
            int dg = DistrictDangerAt(d.x, d.y);
            double m = dg <= 1 ? 0.8 : dg == 2 ? 0.9 : dg == 3 ? 1.0 : dg == 4 ? 1.3 : 1.6;
            if (res == "food" && CurrentSeason == ExpSeason.Autumn) m *= 1.3;
            if (CurrentWeather == ExpWeather.Rain) m *= 0.9;
            return m;
        }

        double ExpEncounterMul(Vector2 pos)
        {
            int dg = DistrictDangerAt((int)pos.x, (int)pos.y);
            double m = dg <= 1 ? 0.3 : dg == 2 ? 0.7 : dg == 3 ? 1.0 : dg == 4 ? 1.5 : 2.2;
            if (CurrentWeather == ExpWeather.Fog) m *= 0.7;
            return m;
        }

        /// <summary>Hourly from TickHourPost (ExpansionRules only): calendar, weather roll, storm damage, district danger.</summary>
        void StepExpWorld()
        {
            int si = (int)CurrentSeason, sd = CurrentSeasonDay;
            if (S.SeasonDay != 0 && si != S.Season)
                AddLog(string.Format("{0} has come.{1}", SEASON_NAME[si], si == 3 ? " The dead are slow in the cold, but nothing grows." : si == 2 ? " Harvest time: stock up before the frost." : ""), "major");
            else if (sd == SEASON_DAYS && S.SeasonDay != sd && si == 2)
                AddLog("Frost tonight. Winter begins tomorrow.", "major");
            else if (sd == SEASON_DAYS && S.SeasonDay != sd)
                AddLog(string.Format("{0} begins tomorrow.", SEASON_NAME[(si + 1) % 4]), "info");
            S.Season = si; S.SeasonDay = sd;

            if (S.T >= S.WeatherEndT)
            {
                _xrng.SetSeed((S.Seed ^ 0x3EA7) + (long)(S.T * 7.0));
                int roll = _xrng.RandiRange(0, 99), w = 0, acc = 0;
                var tab = WEATHER_TABLE[si];
                for (w = 0; w < 4; w++) { acc += tab[w]; if (roll < acc) break; }
                S.Weather = w;
                S.WeatherEndT = S.T + _xrng.RandiRange(6, 18);
                if (w == (int)ExpWeather.Storm)
                {
                    bool hit = false;
                    foreach (var kv in S.Fences)
                        if (_xrng.Randf() < 0.03f) { kv.Value.Hp = Math.Max(1.0, kv.Value.Hp - _xrng.RandiRange(10, 25)); hit = true; }
                    AddLog(hit ? "A storm is tearing at the fences." : "A storm is rolling in.", "info");
                }
                else if (w == (int)ExpWeather.Fog) AddLog("Fog rolls in. Towers see less, but so do the dead.", "info");
                else if (w == (int)ExpWeather.Snow) AddLog("Snow is falling.", "info");
            }
            StepDistrictDanger();
        }

        // ---------------- #23 district danger (calculation only; securing is phase 4) ----------------
        int[] _distTiles;   // walkable-agnostic tile count per district (cached per map)

        /// <summary>Hourly: x = avg sector infection + zombies per 100 tiles x 0.05, banded to D1..D5.</summary>
        void StepDistrictDanger()
        {
            int nd = D.Districts.Count;
            if (nd == 0 || D.DistrictMap == null || D.DistrictMap.Length == 0) return;
            if (_distTiles == null || _distTiles.Length != nd)
            {
                _distTiles = new int[nd];
                foreach (var b in D.DistrictMap) if (b < nd) _distTiles[b]++;
            }
            var inf = new double[nd]; var ns = new int[nd]; var zc = new int[nd];
            if (S.Zinf != null)
                for (int s = 0; s < S.Zinf.Length && s < SW * SH; s++)
                {
                    int x = Math.Min(D.W - 1, (s % SW) * SECT + SECT / 2), y = Math.Min(D.H - 1, (s / SW) * SECT + SECT / 2);
                    int di = D.DistrictMap[y * D.W + x];
                    if (di >= nd) continue;
                    inf[di] += S.Zinf[s]; ns[di]++;
                }
            for (int i = 0; i < Zc.Count; i++)
            {
                if (Zh[i] == 255 || Zc[i] < 0 || Zc[i] >= D.DistrictMap.Length) continue;
                int di = D.DistrictMap[Zc[i]];
                if (di < nd) zc[di]++;
            }
            if (S.DistrictDanger == null || S.DistrictDanger.Length != nd) S.DistrictDanger = new int[nd];
            for (int di = 0; di < nd; di++)
            {
                double x = (ns[di] > 0 ? inf[di] / ns[di] : 0.0) + (_distTiles[di] > 0 ? zc[di] * 100.0 / _distTiles[di] : 0.0) * 0.05;
                S.DistrictDanger[di] = x < 0.15 ? 1 : x < 0.35 ? 2 : x < 0.6 ? 3 : x < 0.85 ? 4 : 5;
            }
        }

        /// <summary>District danger 1 (Safe) .. 5 (Overrun) at a tile; 3 when not yet computed.</summary>
        public int DistrictDangerAt(int x, int y)
        {
            if (S?.DistrictDanger == null || D.DistrictMap == null || D.DistrictMap.Length == 0) return 3;
            x = V.Clampi(x, 0, D.W - 1); y = V.Clampi(y, 0, D.H - 1);
            int di = D.DistrictMap[y * D.W + x];
            return di < S.DistrictDanger.Length ? V.Clampi(S.DistrictDanger[di], 1, 5) : 3;
        }

        public static readonly string[] DANGER_LABEL = { "", "D1 Safe", "D2 Low", "D3 Moderate", "D4 High", "D5 Overrun" };
    }
}
