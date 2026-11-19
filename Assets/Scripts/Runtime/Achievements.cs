// E12 achievements: local, stored in PlayerPrefs (key "dz_ach_v1"), shared by every run on this phone.
// Pure observer like RunStats: it reads the state the sim already keeps (colony stats, bases, research, the last war
// report, vehicles) and listens to Game.LogAdded for "the line held" dawns. It never calls anything that rolls the
// sim's RNG or changes the state. Checked about once a second from Bootstrap.Update; unlocks queue a toast.
// Run counters that are not in the save (raids held, a winter seen) are kept per run seed with the game hour of the
// last counted event, so loading an older save does not count the same dawn twice.
using System;
using System.Collections.Generic;
using System.Linq;
using DayZero.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed class AchDef
    {
        public string Id, Title, Desc, Group;
        public double Target = 1.0;
        /// <summary>count = "n / target", flag = done or not, pct = 0..1 shown as percent.</summary>
        public string Kind = "count";
        public Func<AchCtx, double> Value;
    }

    /// <summary>What a check can read: the game, the viewed colony, its index and this run's extra counters.</summary>
    public sealed class AchCtx
    {
        public Game G; public Colony C; public int Col; public AchRun Run;
        public double DaysDone => Math.Max(0.0, Math.Floor((G.S.T - C.StartT) / 24.0));
        public int Alive => C.People.Count(p => p.St != "dead");
    }

    /// <summary>Per-run counters kept outside the save (keyed by run seed).</summary>
    public sealed class AchRun
    {
        public double T = -1.0;     // game hour of the last counted event
        public int Held;            // night raids ended with no breach and nobody inside
        public bool Winter;         // a winter day has been seen
    }

    public sealed class AchStore
    {
        public Dictionary<string, long> U = new Dictionary<string, long>();     // id -> unlock time (unix seconds)
        public Dictionary<string, double> B = new Dictionary<string, double>(); // id -> best progress seen
        public Dictionary<string, AchRun> R = new Dictionary<string, AchRun>(); // run seed -> counters
        public List<string> RO = new List<string>();                            // run keys, oldest first (capped)
    }

    public static class Achievements
    {
        public const string PREF = "dz_ach_v1";
        const int RUNS_CAP = 12;
        public static readonly List<AchDef> All = new List<AchDef>();
        /// <summary>Unlocks waiting to be shown (GameUI pops them one at a time).</summary>
        public static readonly List<AchDef> Toasts = new List<AchDef>();
        static AchStore _st;
        static Game _g;
        static DateTime _next = DateTime.MinValue;
        static bool _dirty;

        static void Add(string group, string id, string title, string desc, double target, Func<AchCtx, double> v, string kind = "count")
            => All.Add(new AchDef { Group = group, Id = id, Title = title, Desc = desc, Target = target, Value = v, Kind = kind });

        static double F(bool b) => b ? 1.0 : 0.0;

        static Achievements()
        {
            // survival
            Add("Survival", "days10", "Ten Days Gone", "Survive 10 days in one run.", 10, x => x.DaysDone);
            Add("Survival", "days25", "Still Standing", "Survive 25 days in one run.", 25, x => x.DaysDone);
            Add("Survival", "days50", "Old Hands", "Survive 50 days in one run.", 50, x => x.DaysDone);
            Add("Survival", "days100", "The Long Haul", "Survive 100 days in one run.", 100, x => x.DaysDone);
            Add("Survival", "nodeath20", "Nobody Left Behind", "Reach day 20 without losing anyone.", 20,
                x => x.C.Stat("dead") + x.C.Stat("turned") == 0 ? x.DaysDone : 0.0);
            Add("Survival", "hard10", "Hard Times", "Survive 10 days on Hard.", 10, x => x.G.S.Difficulty >= 2 ? x.DaysDone : 0.0);
            Add("Survival", "winter", "Spring Again", "Expanded rules: live through a winter into spring.", 1,
                x => F(x.Run.Winter && x.G.S.ExpRules && x.G.CurrentSeason == ExpSeason.Spring), "flag");
            Add("Survival", "fall", "The City Is Yours", "Hold out for 10 days after the Fall.", 1, x => F(x.C.Won), "flag");
            Add("Survival", "swarm", "Swarm Survivor", "Custom game: survive 10 days with zombies at x2.0 or more.", 10,
                x => x.G.S.Custom != null && x.G.S.Custom.Zombies >= 2.0 ? x.DaysDone : 0.0);
            // combat
            Add("Combat", "kills100", "First Hundred", "Kill 100 zombies in one run.", 100, x => x.C.Stat("kills"));
            Add("Combat", "kills500", "Exterminator", "Kill 500 zombies in one run.", 500, x => x.C.Stat("kills"));
            Add("Combat", "kills2000", "Body Count", "Kill 2000 zombies in one run.", 2000, x => x.C.Stat("kills"));
            Add("Combat", "held5", "Hold the Gate", "End 5 night raids with no breach, in one run.", 5, x => x.Run.Held);
            Add("Combat", "held15", "Iron Gate", "End 15 night raids with no breach, in one run.", 15, x => x.Run.Held);
            Add("Combat", "war", "Victor", "Win a war against a rival camp.", 1, x => F(x.C.War?.Report != null && x.C.War.Report.Won), "flag");
            // colony
            Add("Colony", "rescue10", "Good Samaritan", "Rescue 10 survivors in one run.", 10, x => x.C.Stat("rescued"));
            Add("Colony", "rescue50", "Safe Haven", "Rescue 50 survivors in one run.", 50, x => x.C.Stat("rescued"));
            Add("Colony", "pop20", "Growing Camp", "Have 20 survivors alive at once.", 20, x => x.Alive);
            Add("Colony", "pop40", "Small Town", "Have 40 survivors alive at once.", 40, x => x.Alive);
            Add("Colony", "morale", "High Spirits", "Raise colony morale to 90.", 90, x => Math.Floor(x.C.Morale));
            Add("Colony", "pact", "Allies", "Sign a pact with another camp.", 1, x => F(x.C.Pacts.Count > 0), "flag");
            // building
            Add("Building", "outpost", "Planting the Flag", "Build your first outpost.", 1, x => x.C.Bases.Count(b => !b.Main && b.Done >= 1.0), "flag");
            Add("Building", "ring", "Walled In", "Fence a full ring around a camp.", 1, x => F(x.C.Bases.Any(b => x.G.IsEnclosed(b))), "flag");
            Add("Building", "convert3", "Change of Use", "Put 3 buildings to a new use.", 3, x => Converted(x));
            Add("Building", "drive", "Back on the Road", "Repair a wreck and send it out with a driver.", 1, x => F(x.C.Vehs.Any(v => v.St == "out" || v.Drv >= 0)), "flag");
            Add("Building", "tier", "Scholar", "Research every project in one tier of a branch.", 1, x => TierBest(x), "pct");
            Add("Building", "research", "Mad Scientist", "Research everything.", 1, x => ResearchAll(x), "pct");
        }

        static double Converted(AchCtx x)
        {
            int n = 0;
            foreach (var kv in x.G.S.Conv) if (kv.Value != null && kv.Value.Col == x.Col && kv.Value.Ready <= 0.0) n++;
            return n;
        }

        static double TierBest(AchCtx x)
        {
            var techs = Data.Instance.Research;
            if (techs == null || techs.Count == 0) return 0.0;
            var done = new HashSet<string>(x.C.Research.Done);
            var tot = new Dictionary<string, int>(); var got = new Dictionary<string, int>();
            foreach (var kv in techs)
            {
                if (!(kv.Value is JObject t)) continue;
                string k = (string)t["cat"] + "/" + (t["tier"] != null ? ((double)t["tier"]).ToString("0") : "0");
                tot[k] = (tot.TryGetValue(k, out var a) ? a : 0) + 1;
                if (done.Contains(kv.Key)) got[k] = (got.TryGetValue(k, out var b) ? b : 0) + 1;
            }
            double best = 0.0;
            foreach (var kv in tot) if (kv.Value > 0) best = Math.Max(best, (got.TryGetValue(kv.Key, out var g) ? g : 0) / (double)kv.Value);
            return best;
        }

        static double ResearchAll(AchCtx x)
        {
            var techs = Data.Instance.Research;
            if (techs == null || techs.Count == 0) return 0.0;
            int n = 0;
            foreach (var id in x.C.Research.Done) if (techs.ContainsKey(id)) n++;
            return n / (double)techs.Count;
        }

        // ---------------- storage ----------------
#if DZ_PREVIEW
        static string _mem = "";
        static string LoadPref() => _mem;
        static void SavePref(string s) { _mem = s; }
        public static void ResetForTest() { _mem = ""; _st = null; Toasts.Clear(); }
#else
        static string LoadPref() => PlayerPrefs.GetString(PREF, "");
        static void SavePref(string s) { PlayerPrefs.SetString(PREF, s); PlayerPrefs.Save(); }
#endif

        public static AchStore Store
        {
            get
            {
                if (_st != null) return _st;
                try { string s = LoadPref(); _st = string.IsNullOrEmpty(s) ? null : JsonConvert.DeserializeObject<AchStore>(s); } catch { _st = null; }
                _st ??= new AchStore();
                _st.U ??= new Dictionary<string, long>(); _st.B ??= new Dictionary<string, double>();
                _st.R ??= new Dictionary<string, AchRun>(); _st.RO ??= new List<string>();
                return _st;
            }
        }

        static void Persist()
        {
            if (!_dirty) return;
            _dirty = false;
            try { SavePref(JsonConvert.SerializeObject(Store)); } catch (Exception e) { Debug.LogWarning("achievements save: " + e.Message); }
        }

        public static bool IsUnlocked(string id) => Store.U.ContainsKey(id);
        public static int UnlockedCount => All.Count(a => Store.U.ContainsKey(a.Id));
        public static double Best(string id) => Store.B.TryGetValue(id, out var v) ? v : 0.0;

        static string RunKey(GameState s) => s.Seed.ToString();

        static AchRun RunOf(GameState s)
        {
            var st = Store; string k = RunKey(s);
            if (st.R.TryGetValue(k, out var r) && r != null) return r;
            r = new AchRun();
            st.R[k] = r; st.RO.Remove(k); st.RO.Add(k);
            while (st.RO.Count > RUNS_CAP) { st.R.Remove(st.RO[0]); st.RO.RemoveAt(0); }
            _dirty = true;
            return r;
        }

        // ---------------- detection ----------------
        static void Attach(Game g)
        {
            if (ReferenceEquals(g, _g)) return;
            if (_g != null) _g.LogAdded -= OnLog;
            _g = g;
            if (g != null) g.LogAdded += OnLog;
        }

        static bool Ready(Game g, out Colony c, out int col)
        {
            c = null; col = 0;
            var S = g?.S;
            if (S == null || S.Phase == "pick" || S.Cols == null || S.Cols.Count == 0) return false;
            col = Math.Max(0, Math.Min(g.ViewCol, S.Cols.Count - 1));
            c = S.Cols[col];
            return c.Started && !c.Lost;
        }

        /// <summary>"Dawn at X. n of the m that came in the night are down, the line held." with nobody left inside.</summary>
        static void OnLog(LogEntry e)
        {
            try
            {
                var g = _g;
                if (!Ready(g, out _, out int col) || e == null || e.Col != col || e.Kind != "major") return;
                if (e.Text == null || !e.Text.StartsWith("Dawn at ", StringComparison.Ordinal) || e.Text.IndexOf("the line held", StringComparison.Ordinal) < 0) return;
                var run = RunOf(g.S);
                if (g.S.T <= run.T + 1e-6) return;   // already counted (a reload of an older save)
                run.T = g.S.T; run.Held++; _dirty = true;
            }
            catch (Exception ex) { Debug.LogWarning("achievements log: " + ex.Message); }
        }

        /// <summary>Per frame from Bootstrap.Update (any state). Evaluates about once a second.</summary>
        public static void Tick(Game g)
        {
            try
            {
                CustomGame.Track(g);
                Attach(g);
                var now = DateTime.UtcNow;
                if (now < _next) return;
                _next = now.AddSeconds(1.0);
                Evaluate(g);
            }
            catch (Exception e) { Debug.LogWarning("achievements: " + e.Message); }
        }

        /// <summary>Check every achievement against the viewed colony now; returns the ones unlocked by this call.</summary>
        public static List<AchDef> Evaluate(Game g)
        {
            var got = new List<AchDef>();
            Attach(g);
            if (!Ready(g, out var c, out int col)) { Persist(); return got; }
            var run = RunOf(g.S);
            if (g.S.ExpRules && g.CurrentSeason == ExpSeason.Winter && !run.Winter) { run.Winter = true; _dirty = true; }
            var x = new AchCtx { G = g, C = c, Col = col, Run = run };
            var st = Store;
            foreach (var a in All)
            {
                double v;
                try { v = a.Value(x); } catch { continue; }
                if (double.IsNaN(v)) continue;
                if (v > Best(a.Id) + 1e-9) { st.B[a.Id] = Math.Min(v, a.Target); _dirty = true; }
                if (v >= a.Target - 1e-9 && !st.U.ContainsKey(a.Id))
                {
                    st.U[a.Id] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    _dirty = true;
                    got.Add(a);
                    Toasts.Add(a);
                    if (Toasts.Count > 6) Toasts.RemoveAt(0);
                }
            }
            Persist();
            return got;
        }

        /// <summary>Progress 0..1 and its text ("37 / 50", "60%", "Not yet") for the screen. Uses the best value seen.</summary>
        public static float Progress(AchDef a, out string text)
        {
            bool un = IsUnlocked(a.Id);
            double v = un ? a.Target : Math.Min(a.Target, Best(a.Id));
            float f = a.Target <= 0 ? 0f : (float)(v / a.Target);
            if (un) text = "UNLOCKED";
            else if (a.Kind == "flag") text = "Not yet";
            else if (a.Kind == "pct") text = string.Format("{0}%", (int)Math.Floor(v * 100.0));
            else text = string.Format("{0} / {1}", (int)Math.Floor(v), (int)a.Target);
            return Math.Max(0f, Math.Min(1f, f));
        }

        // ---------------- screen ----------------
        static Color INK => SideMenus.INK; static Color INK2 => SideMenus.INK2; static Color MUTE => SideMenus.MUTE;
        static Color AMBER => SideMenus.AMBER;

        /// <summary>The ACHIEVEMENTS screen (title and Menu tab): every achievement, locked or unlocked, with progress bars.</summary>
        public static void Screen(MenuCtx c, Action close)
        {
            c.Dim();
            float pw = Math.Min(1200f, c.W - 40f), ph = Math.Min(690f, c.H - 20f);
            var inner = c.Panel(new Rect((c.W - pw) / 2f, (c.H - ph) / 2f, pw, ph), "ACHIEVEMENTS");
            var L = new UiLayout(inner, 10f);
            var F = new UiLayout(L.Bottom(62f), 14f);
            c.Btn(F.Right(190f), "CLOSE", UiRole.Danger, close, true, false, 22);
            int n = UnlockedCount;
            var fr = UiLayout.Rows(F.Rest, 4f, 1f, 0.7f);
            c.Fit(fr[0], string.Format("{0} of {1} unlocked", n, All.Count), 20, n > 0 ? AMBER : INK, true);
            c.KBar(UiLayout.Inset(fr[1], 0f, 2f, 0f, 2f), All.Count == 0 ? 0f : n / (float)All.Count, UiRole.Accent);
            const float RH = 92f, GAP = 8f;
            int cols = c.W >= 1000f ? 2 : 1;
            int rows = (All.Count + cols - 1) / cols;
            // unlocked first (newest first), then locked by progress
            var order = All.OrderByDescending(a => IsUnlocked(a.Id) ? 1 : 0)
                .ThenByDescending(a => IsUnlocked(a.Id) ? Store.U[a.Id] : 0L)
                .ThenByDescending(a => Progress(a, out _)).ToList();
            float cw = c.Scroll("ach_list", L.Rest, rows * (RH + GAP) - GAP);
            float colW = (cw - (cols - 1) * 10f) / cols;
            for (int i = 0; i < order.Count; i++)
            {
                var a = order[i];
                int r = i / cols, k = i % cols;
                var row = new Rect(k * (colW + 10f), r * (RH + GAP), colW, RH);
                bool un = IsUnlocked(a.Id);
                c.Plate(row, i, un);
                if (un) c.Box(new Rect(row.x + 3f, row.y + 6f, 4f, row.height - 12f), new Color(0.96f, 0.72f, 0.30f, 0.95f));
                var R = UiLayout.Inset(row, 18f, 8f, 14f, 8f);
                var tx = UiLayout.Rows(R, 3f, 1.1f, 0.85f, 0.75f);
                var top = UiLayout.Columns(tx[0], 8f, 1f, -130f);
                c.Fit(top[0], a.Title, 21, un ? AMBER : INK, true);
                c.Fit(top[1], a.Group.ToUpperInvariant(), 13, MUTE, true, MenuAlign.Right);
                c.Fit(tx[1], a.Desc, 16, un ? INK : INK2, false);
                float f = Progress(a, out string pt);
                c.KBar(tx[2], f, un ? UiRole.Accent : UiRole.Primary, pt);
            }
            c.EndScroll();
        }
    }
}
