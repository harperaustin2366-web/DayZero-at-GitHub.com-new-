// E6 morning report, E7 memorial wall, E15 run summary: the run's bookkeeping, kept in the save as GameState.RunLog
// (a plain JObject, so older saves without it simply start an empty log). Pure observer: it reads the state, listens
// to Game.PersonDied / LootDeposited / LogAdded, and never calls anything that touches the simulation's RNG.
// A snapshot is taken at every dawn (06:00); at the next dawn the difference becomes the morning report.
using System;
using System.Collections.Generic;
using System.Linq;
using DayZero.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DayZero.Runtime
{
    public sealed class FallenEntry
    {
        public string Name = "", Job = "", Cause = "";
        public int Day;          // day of death
        public double Days;      // days with the colony
    }

    /// <summary>What happened between two dawns (the morning report card).</summary>
    public sealed class MorningReport
    {
        public int Day;                          // the day that is starting
        public int PopBefore, PopNow;
        public List<FallenEntry> Died = new List<FallenEntry>();
        public List<string> Joined = new List<string>();
        public Dictionary<string, double> Loot = new Dictionary<string, double>();
        public int Kills, Rescued, Breaches, TowersLost;
        public double WallDmg;
        public int Threat; public bool ThreatMajor, ThreatKnown;
    }

    /// <summary>Accumulators since the last dawn (saved, so a reload mid-day keeps the report honest).</summary>
    public sealed class DawnSnap
    {
        public double T; public int Day;
        public List<int> Ids = new List<int>();
        public int Kills, Rescued, Pop;
        public List<FallenEntry> Died = new List<FallenEntry>();
        public Dictionary<string, double> Loot = new Dictionary<string, double>();
        public int Breaches, TowersLost;
        public double WallDmg, RaidDmgSeen;
    }

    public sealed class RunLogData
    {
        public int V = 1;
        public List<int> Pop = new List<int>();      // survivors at each dawn, [0] = day 1
        public List<int> Kills = new List<int>();    // zombies killed during each day (dawn to dawn), [0] = day 1
        public int Peak;
        public List<FallenEntry> Fallen = new List<FallenEntry>();
        public Dictionary<int, double> Join = new Dictionary<int, double>();   // person id -> game hour joined
        public DawnSnap Snap;
        public bool Final;
    }

    public sealed class RunStats
    {
        public static readonly RunStats Instance = new RunStats();
        public const int DAWN_H = 6, FALLEN_CAP = 300;
        static readonly JsonSerializer Ser = JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

        public RunLogData Log { get; private set; }
        /// <summary>The report waiting to be shown (GameUI clears it when dismissed).</summary>
        public MorningReport Pending;
        Game _g; GameState _s; int _col; long _dawnIdx = long.MinValue; bool _dirty; int _seenSaves = -1;
        readonly HashSet<int> _known = new HashSet<int>();

        /// <summary>Per frame from Bootstrap.Update (any state; cheap).</summary>
        public static void Tick(Game g) { try { Instance.Step(g); } catch (Exception e) { UnityEngine.Debug.LogWarning("runstats: " + e.Message); } }

        void Attach(Game g)
        {
            if (ReferenceEquals(g, _g)) return;
            if (_g != null) { _g.PersonDied -= OnDied; _g.LootDeposited -= OnLoot; _g.LogAdded -= OnLog; }
            _g = g; _s = null;
            g.PersonDied += OnDied; g.LootDeposited += OnLoot; g.LogAdded += OnLog;
        }

        public void Step(Game g)
        {
            if (g == null) return;
            Attach(g);
            var S = g.S;
            if (S == null || S.Phase == "pick" || S.Cols == null || S.Cols.Count == 0) { _s = null; return; }
            int col = Math.Max(0, Math.Min(g.ViewCol, S.Cols.Count - 1));
            var c = S.Cols[col];
            if (!c.Started) return;
            if (!ReferenceEquals(S, _s) || col != _col) Begin(g, S, col);
            if (Log.Final) return;
            long di = DawnIndex(S.T);
            var alive = c.People;
            // joiners (rescued groups, births, recruits): remember when they arrived
            if (alive.Count != _known.Count || alive.Any(p => !_known.Contains(p.Id)))
            {
                foreach (var p in alive)
                    if (p.St != "dead" && _known.Add(p.Id) && !Log.Join.ContainsKey(p.Id)) { Log.Join[p.Id] = S.T; _dirty = true; }
                int n = alive.Count(p => p.St != "dead");
                if (n > Log.Peak) { Log.Peak = n; _dirty = true; }
            }
            // night raid wall damage (Raid.Dmg restarts with every raid)
            var R0 = c.Nraid;
            if (R0 != null && Log.Snap != null)
            {
                if (R0.On)
                {
                    double d = R0.Dmg >= Log.Snap.RaidDmgSeen ? R0.Dmg - Log.Snap.RaidDmgSeen : R0.Dmg;
                    if (d > 0.0) { Log.Snap.WallDmg += d; _dirty = true; }
                    Log.Snap.RaidDmgSeen = R0.Dmg;
                }
                else Log.Snap.RaidDmgSeen = 0.0;
            }
            if (S.Phase == "lost" || c.Lost) { Finish(g, c); return; }
            if (di > _dawnIdx)
            {
                _dawnIdx = di;
                Dawn(g, c);
                Sync();
                SaveSlots.SaveAuto(g);   // E5: autosave at every dawn
            }
            if (_dirty) Sync();
            // E5: keep the AUTO slot's card (day, colony size) in step with the 30 s autosave
            if (g.SaveCount != _seenSaves)
            {
                _seenSaves = g.SaveCount;
                if (SaveSlots.LastWriter != g.SaveCount) SaveSlots.WriteMeta(g, SaveSlots.SlotPath(0));
            }
        }

        static long DawnIndex(double t) => (long)Math.Floor((t - DAWN_H) / 24.0);

        void Begin(Game g, GameState S, int col)
        {
            _s = S; _col = col; Pending = null; _known.Clear(); _seenSaves = g.SaveCount;
            RunLogData d = null;
            if (S.RunLog != null) { try { d = S.RunLog.ToObject<RunLogData>(Ser); } catch { d = null; } }
            var c = S.Cols[col];
            bool fresh = d == null;
            d ??= new RunLogData();
            d.Pop ??= new List<int>(); d.Kills ??= new List<int>(); d.Fallen ??= new List<FallenEntry>(); d.Join ??= new Dictionary<int, double>();
            Log = d;
            foreach (var p in c.People)
                if (p.St != "dead")
                {
                    _known.Add(p.Id);
                    if (!d.Join.ContainsKey(p.Id)) d.Join[p.Id] = fresh ? Math.Min(S.T, c.StartT) : S.T;
                }
            d.Peak = Math.Max(d.Peak, _known.Count);
            if (d.Pop.Count < g.Day() && !d.Final) SetAt(d.Pop, g.Day() - 1, _known.Count);
            _dawnIdx = DawnIndex(S.T);
            if (d.Snap == null) d.Snap = Snapshot(g, c);
            _dirty = true;
            Sync();
        }

        DawnSnap Snapshot(Game g, Colony c)
        {
            var ids = c.People.Where(p => p.St != "dead").Select(p => p.Id).ToList();
            return new DawnSnap { T = g.S.T, Day = g.Day(), Ids = ids, Pop = ids.Count, Kills = c.Stat("kills"), Rescued = c.Stat("rescued") };
        }

        void Dawn(Game g, Colony c)
        {
            var sn = Log.Snap ?? Snapshot(g, c);
            var alive = c.People.Where(p => p.St != "dead").ToList();
            var rep = new MorningReport
            {
                Day = g.Day(), PopBefore = sn.Pop, PopNow = alive.Count,
                Died = new List<FallenEntry>(sn.Died), Loot = new Dictionary<string, double>(sn.Loot),
                Kills = Math.Max(0, c.Stat("kills") - sn.Kills), Rescued = Math.Max(0, c.Stat("rescued") - sn.Rescued),
                Breaches = sn.Breaches, TowersLost = sn.TowersLost, WallDmg = sn.WallDmg,
            };
            var before = new HashSet<int>(sn.Ids);
            foreach (var p in alive) if (!before.Contains(p.Id)) rep.Joined.Add(p.Name);
            Threat(g, c, rep);
            // daily series: population at each dawn, kills of the day that just ended
            SetAt(Log.Kills, Math.Max(1, sn.Day) - 1, rep.Kills);
            SetAt(Log.Pop, rep.Day - 1, alive.Count);
            Log.Peak = Math.Max(Log.Peak, alive.Count);
            Log.Snap = Snapshot(g, c);
            if (rep.Day >= 2) Pending = rep;
            _dirty = true;
        }

        static void SetAt(List<int> l, int i, int v)
        {
            if (i < 0) return;
            while (l.Count <= i) l.Add(-1);
            l[i] = v;
        }

        /// <summary>Tonight's expected attack: the scheduled raid when there is one, else the size formula (no RNG).</summary>
        static void Threat(Game g, Colony c, MorningReport rep)
        {
            var R0 = c.Nraid;
            if (R0 != null && (R0.On || R0.NextT >= 0.0)) { rep.Threat = R0.N; rep.ThreatKnown = true; }
            else
            {
                int ci = g.S.Cols.IndexOf(c);
                rep.Threat = g.RaidSize(ci);
            }
            rep.ThreatMajor = rep.Threat >= Game.RAID_MAJOR;
        }

        void Finish(Game g, Colony c)
        {
            if (Log.Final) return;
            var sn = Log.Snap;
            if (sn != null) SetAt(Log.Kills, Math.Max(1, sn.Day) - 1, Math.Max(0, c.Stat("kills") - sn.Kills));
            int day = c.LostDay > 0 ? c.LostDay : g.Day();
            if (Log.Pop.Count >= day) Log.Pop.Add(0); else SetAt(Log.Pop, day - 1, 0);   // the graph ends at zero
            Log.Final = true;
            Pending = null;
            _dirty = true;
            Sync();
        }

        /// <summary>Write pending log changes into the state (right before a save).</summary>
        public void Flush() { if (_dirty) Sync(); }

        void Sync()
        {
            if (_s == null || Log == null) return;
            _dirty = false;
            try { _s.RunLog = JObject.FromObject(Log, Ser); } catch (Exception e) { UnityEngine.Debug.LogWarning("runstats sync: " + e.Message); }
        }

        // ---------------- hooks (fired inside Game.Process) ----------------
        void OnDied(Person p, string cause)
        {
            if (_s == null || Log == null || Log.Final || p.Col != _col) return;
            double joined = Log.Join.TryGetValue(p.Id, out var jt) ? jt : _s.Cols[_col].StartT;
            var e = new FallenEntry { Name = p.Name ?? "", Job = p.Job ?? "", Cause = CauseText(cause), Day = (int)(_s.T / 24.0) + 1, Days = Math.Max(0.0, (_s.T - joined) / 24.0) };
            Log.Fallen.Add(e);
            if (Log.Fallen.Count > FALLEN_CAP) Log.Fallen.RemoveAt(0);
            Log.Join.Remove(p.Id);
            _known.Remove(p.Id);
            Log.Snap?.Died.Add(e);
            _dirty = true;
        }

        void OnLoot(Person p, ODict<string, double> carry)
        {
            if (_s == null || Log?.Snap == null || Log.Final || p.Col != _col) return;
            foreach (var kv in carry)
                if (kv.Value > 0.0) Log.Snap.Loot[kv.Key] = (Log.Snap.Loot.TryGetValue(kv.Key, out var v) ? v : 0.0) + kv.Value;
            _dirty = true;
        }

        void OnLog(LogEntry e)
        {
            if (_s == null || Log?.Snap == null || Log.Final || e.Col != _col || e.Kind != "loss") return;
            if (e.Text.StartsWith("Zombies broke through the", StringComparison.Ordinal)) { Log.Snap.Breaches++; _dirty = true; }
            else if (e.Text.StartsWith("A tower on", StringComparison.Ordinal)) { Log.Snap.TowersLost++; _dirty = true; }
        }

        public static string CauseText(string cause)
        {
            if (string.IsNullOrEmpty(cause)) return "Unknown";
            if (cause == "turned") return "Turned after a bite";
            return char.ToUpperInvariant(cause[0]) + cause.Substring(1);
        }

        // ---------------- run summary (E15) ----------------
        public int BestDay(out int kills)
        {
            kills = 0; int best = 0;
            if (Log == null) return 0;
            for (int i = 0; i < Log.Kills.Count; i++) if (Log.Kills[i] > kills) { kills = Log.Kills[i]; best = i + 1; }
            return best;
        }

        /// <summary>Population per day for the graph (gaps from older saves are carried from the day before).</summary>
        public List<int> PopSeries(out int firstDay)
        {
            var r = new List<int>();
            firstDay = 1;
            if (Log == null) return r;
            for (int i = 0; i < Log.Pop.Count && Log.Pop[i] < 0; i++) firstDay = i + 2;
            int last = -1;
            foreach (int v in Log.Pop)
            {
                if (v >= 0) last = v;
                if (last >= 0) r.Add(last);   // days before the log started (older saves) are left out
            }
            return r;
        }
    }
}
