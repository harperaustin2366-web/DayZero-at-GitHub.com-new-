// v0.19 leader popularity (EXPANDED rules only). Austin: "based on all of that [survivor minds / trust] also add a
// popularity meter and if it drops too far you might be kicked out of your colony and have to start over, and that
// colony continues without you as a new colony".
//
// SUPPORT = weighted average of SurvivorMind.TrustInLeader over the adults of your colony, smoothed hour by hour.
// Four bands in plain words: Steady / Uneasy / Angry / Mutiny. Nothing is random and nothing comes as a surprise:
//   * every step down is announced (log + toast), Angry and Mutiny bring grumbling in the survivors' own words
//   * MUTINY_CLOCK hours spent in Mutiny (recovering winds the clock back) bring "talk of a vote" at half time and a
//     CAMP VOTE at full time, held VOTE_WINDOW hours later: a last chance to win people back (feed them, fix the walls,
//     TALK to them reassuringly - TALK replies move trust, see SurvivorMind.Reply)
//   * at the vote every adult votes by their own trust (>= VOTE_KEEP keeps you); if more weight is against you: EXILE
// EXILE: you lose the colony. It becomes a rival faction camp (S.Camps) on the same spot: same people (as its
// population), stockpile (as its stock), buildings (main lot + claimed lots) and colour, led by its most trusted /
// respected survivor, who gets a LeaderBrain mind built from their SurvivorMind past, remembering that they threw
// you out and how you ruled. You start over at a new spot (60..170 tiles away) with the two most loyal survivors
// (no family ties) or drifters, a small bag of supplies and your research. The run goes on (no game over).
// Your own colony ALWAYS obeys direct orders until the moment of exile; support never blocks or changes an order.
//
// Gating: StepSupport runs only with Game.ExpansionRules (hourly hook in TickHourPost), so classic runs never touch
// it. It never draws from Rng / Frng / GRng while support is only measured; the exile itself (FactionInit, SpawnGroup)
// uses the sim Rng, which is fine because it only exists in EXPANDED runs. Exile is off online (Mp / clients) and
// with more than one colony, and when the CUSTOM GAME option EXILE is off (GameState.Custom.NoExile).
// Save: Colony.Lead (null until the first expanded hour, skipped in saves when null).
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DayZero.Core
{
    /// <summary>How your colony stands with you (Colony.Lead). Every field has a safe default for old saves.</summary>
    public sealed class LeaderStanding
    {
        public double P = -1.0;          // smoothed support 0..1 (-1 = not measured yet)
        public double Raw = -1.0;        // this hour's weighted trust
        public int Band;                 // 0 Steady, 1 Uneasy, 2 Angry, 3 Mutiny
        public double LowH;              // mutiny clock (hours)
        public double VoteT = -1.0;      // > 0: a camp vote is called for this game hour
        public double VoteCalledT = -1.0;
        public string Caller = "";       // who called the vote
        public bool TalkWarned;          // "talk of a vote" said for this mutiny
        public double GraceT;            // no new vote before this hour
        public double NextGrumbleT;
        public int VotesWon, Exiles;
        public List<double> Days = new List<double>();   // support at the end of each day (newest last, cap 120)
        public double DayMin = 1.0;
        public ExileInfo Exiled;          // the last exile (for the exile screen)
        public bool ExileSeen = true;
    }

    public sealed class ExileInfo
    {
        public double T; public int Day;
        public string OldName = "", CampId = "", CampName = "", Leader = "", LeaderJob = "", Att = "", Site = "", Ruled = "";
        public string Quote = "", Story = "";   // the new leader's memory of the vote, and who they were before
        public int Pop, For, Against;
        public List<string> With = new List<string>();     // who came with you
        public List<string> Why = new List<string>();      // top reasons, survivors' words
    }

    /// <summary>One survivor's view, for the SUPPORT sheet.</summary>
    public sealed class SupportPerson { public int Id; public string Name = "", Job = "", Word = "", Why = ""; public float Trust; }

    /// <summary>One reason, summed over the camp, in one survivor's words.</summary>
    public sealed class SupportReason { public string Key = "", Text = "", Who = ""; public float Sum; public int N; }

    public sealed class SupportReport
    {
        public double P = -1.0; public int Band; public string Word = "Steady", Trend = "", Status = "";
        public int Adults; public bool Vote; public double VoteInH, LowH, ClockLeftH; public bool ExileOn;
        public int For, Against;
        public List<SupportReason> Bad = new List<SupportReason>(), Good = new List<SupportReason>();
        public List<SupportPerson> People = new List<SupportPerson>();   // unhappiest first
    }

    public sealed partial class Game
    {
        // ---------------- design numbers ----------------
        public const double SUP_STEADY = 0.46, SUP_UNEASY = 0.36, SUP_ANGRY = 0.26;   // band floors (below ANGRY = Mutiny)
        public const double SUP_HYST = 0.02;          // a band only changes this far past its line
        public const double SUP_EMA = 0.05;           // per hour smoothing (half-life ~14 h: a bad day shows by evening, not in an hour)
        public const double MUTINY_CLOCK = 48.0;      // hours in Mutiny before a camp vote is called
        public const double VOTE_WINDOW = 24.0;       // last chance between the call and the vote
        public const double VOTE_KEEP = 0.36;         // an adult whose trust is at least this votes for you
        public const double SUP_GRACE_START = 72.0;   // no vote in a colony's first three days
        public const double SUP_GRACE_WON = 72.0;     // ... nor for three days after you won a vote
        public const int VOTE_MIN_ADULTS = 4;
        public static readonly string[] SUP_WORDS = { "Steady", "Uneasy", "Angry", "Mutiny" };

        /// <summary>(kind, text) for the HUD: "down", "up", "grumble", "talk", "vote", "kept", "exile".</summary>
        public event Action<string, string> SupportNote;

        public static int SupportBandOf(double p) => p >= SUP_STEADY ? 0 : p >= SUP_UNEASY ? 1 : p >= SUP_ANGRY ? 2 : 3;
        static readonly double[] BAND_FLOOR = { SUP_STEADY, SUP_UNEASY, SUP_ANGRY, -1.0 };

        /// <summary>Exile (and the vote) can happen in this run: expanded rules, offline, one colony, option on.</summary>
        public bool ExileAllowed() => ExpansionRules && S != null && !Mp && !IsClient && S.Cols.Count == 1 && !(S.Custom?.NoExile ?? false);

        public LeaderStanding StandingOf(Colony c) => c?.Lead;

        static float SupWeight(Game g, Person p)
        {
            float w = 1f;
            if (p.Traits != null && p.Traits.Contains("leader")) w += 0.5f;
            if (p.Job == "Soldier" || p.Job == "Cop") w += 0.2f;
            return w;
        }

        List<Person> SupAdults()
        {
            var o = new List<Person>();
            if (C == null) return o;
            foreach (var p in C.People) { if (p.St == "dead") continue; bool a = true; try { a = IsAdult(p); } catch { } if (a) o.Add(p); }
            return o;
        }

        /// <summary>This hour's weighted trust of the current colony (-1 with no adults). Read only.</summary>
        public double SupportRaw(out int adults, out double forW, out double againstW)
        {
            var ad = SupAdults(); adults = ad.Count; forW = 0; againstW = 0;
            if (ad.Count == 0) return -1.0;
            double sw = 0, st = 0;
            foreach (var p in ad)
            {
                float w = SupWeight(this, p), t = SurvivorMind.TrustInLeader(this, p.Id);
                sw += w; st += w * t;
                if (t >= VOTE_KEEP) forW += w; else againstW += w;
            }
            return sw > 0 ? st / sw : -1.0;
        }

        static uint SupHash(int seed, double t, int salt)
        {
            uint h = 2166136261u ^ (uint)seed;
            h = (h ^ (uint)(long)Math.Floor(t)) * 16777619u; h = (h ^ (uint)salt) * 16777619u;
            h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
            return h;
        }

        // ---------------- hourly (TickHourPost, EXPANDED only) ----------------
        void StepSupport()
        {
            if (S == null || S.Phase == "pick" || S.Phase == "lost") return;
            int exileCol = -1;
            for (int ci = 0; ci < S.Cols.Count; ci++)
            {
                var c = S.Cols[ci];
                if (!ColAlive(c)) continue;
                Use(ci); LogCol = ci;
                try { if (SupportHour(c, ci)) exileCol = ci; }
                catch (Exception e) { Debug.LogWarning("support: " + e.Message); }
                LogCol = -1;
            }
            Use(ViewCol);
            if (exileCol >= 0)
            {
                try { ExileLeader(exileCol); }
                catch (Exception e) { Debug.LogWarning("exile failed: " + e); }
                Use(ViewCol);
            }
        }

        void SupNote(string kind, string text, string logKind)
        {
            AddLog(text, logKind);
            SupportNote?.Invoke(kind, text);
        }

        /// <summary>Returns true when the vote went against you (exile follows).</summary>
        bool SupportHour(Colony c, int ci)
        {
            var L = c.Lead ??= new LeaderStanding { GraceT = c.StartT + SUP_GRACE_START };
            L.Days ??= new List<double>();
            double raw = SupportRaw(out int adults, out double forW, out double againstW);
            if (raw < 0) return false;
            L.Raw = raw;
            if (L.P < 0) { L.P = raw; L.Band = SupportBandOf(raw); L.DayMin = raw; }   // first reading: no announcement
            else L.P = L.P + (raw - L.P) * SUP_EMA;
            double P = L.P;
            L.DayMin = Math.Min(L.DayMin, P);
            if (Hour() == 0) { L.Days.Add(Math.Round(P, 3)); while (L.Days.Count > 120) L.Days.RemoveAt(0); L.DayMin = P; }

            // band with hysteresis: worse only below floor - HYST, better only above floor + HYST
            int band = L.Band;
            while (band < 3 && P < BAND_FLOOR[band] - SUP_HYST) band++;
            while (band > 0 && P >= BAND_FLOOR[band - 1] + SUP_HYST) band--;
            if (band != L.Band)
            {
                bool worse = band > L.Band;
                L.Band = band;
                if (worse)
                {
                    if (band == 1) SupNote("down", "People are starting to doubt you. SUPPORT: Uneasy.", "loss");
                    else if (band == 2) SupNote("down", "Your people are angry with you. SUPPORT: Angry. Feed them, fix the walls, talk to them.", "loss");
                    else SupNote("down", ExileAllowed()
                        ? "Talk of mutiny in camp. SUPPORT: Mutiny. If it stays this low for two days, they will call a vote on you."
                        : "Talk of mutiny in camp. SUPPORT: Mutiny.", "loss");
                }
                else SupNote("up", string.Format("Things are calming down. SUPPORT: {0}.", SUP_WORDS[band]), "good");
            }

            // grumbling in their own words (Angry and worse), about twice a day
            if (band >= 2 && S.T >= L.NextGrumbleT)
            {
                L.NextGrumbleT = S.T + 9.0 + SupHash(S.Seed, S.T, ci * 7 + 3) % 6u;
                var who = SupAdults().Where(p => p.St == "base" || p.St == "work").OrderBy(p => SurvivorMind.TrustInLeader(this, p.Id)).ThenBy(p => p.Id).Take(3).ToList();
                if (who.Count > 0)
                {
                    var p = who[(int)(SupHash(S.Seed, S.T, 11) % (uint)who.Count)];
                    var why = SurvivorMind.TrustReasons(this, p.Id).Where(r => r.Delta < 0).OrderBy(r => r.Delta).FirstOrDefault();
                    string line = why.Text ?? "";
                    if (line == "") line = band == 3 ? "Somebody else should be making the calls." : "I don't know about this anymore.";
                    string tail = band == 3 ? " People are listening." : "";
                    SupNote("grumble", string.Format("{0} ({1}) grumbles: \"{2}\"{3}", p.Name, p.Job, line, tail), "info");
                }
            }

            if (!ExileAllowed()) { L.LowH = 0; L.VoteT = -1; L.TalkWarned = false; return false; }

            // a vote already called: hold it on time
            if (L.VoteT > 0)
            {
                if (S.T < L.VoteT) return false;
                L.VoteT = -1; L.LowH = 0; L.TalkWarned = false;
                if (forW >= againstW || adults < VOTE_MIN_ADULTS) L.Caller = "";
                int nf = 0, na = 0;
                foreach (var p in SupAdults()) { if (SurvivorMind.TrustInLeader(this, p.Id) >= VOTE_KEEP) nf++; else na++; }
                if (forW >= againstW || adults < VOTE_MIN_ADULTS)
                {
                    L.VotesWon++; L.GraceT = S.T + SUP_GRACE_WON;
                    SupNote("kept", string.Format("CAMP VOTE: {0} for you, {1} against. You stay in charge. For now.", nf, na), "good");
                    return false;
                }
                _voteFor = nf; _voteAgainst = na;
                SupNote("lost", string.Format("CAMP VOTE: {0} for you, {1} against. You are out.", nf, na), "major");
                return true;
            }

            // the mutiny clock
            if (band == 3) L.LowH += 1.0;
            else L.LowH = Math.Max(0.0, L.LowH - (band == 2 ? 1.5 : 3.0));
            if (L.LowH <= 0.0) { L.TalkWarned = false; L.Caller = ""; }
            if (S.T < L.GraceT || adults < VOTE_MIN_ADULTS) { L.LowH = Math.Min(L.LowH, MUTINY_CLOCK - 1.0); return false; }
            if (!L.TalkWarned && L.LowH >= MUTINY_CLOCK * 0.5)
            {
                L.TalkWarned = true;
                L.Caller = "";
                var lead = VoteCaller();
                L.Caller = lead?.Name ?? "";
                SupNote("talk", string.Format("{0} is going around camp asking who still backs you. One more bad day and they call a CAMP VOTE.", lead?.Name ?? "Someone"), "loss");
            }
            if (L.LowH >= MUTINY_CLOCK)
            {
                var lead = VoteCaller();
                L.Caller = lead?.Name ?? "";
                L.VoteCalledT = S.T; L.VoteT = S.T + VOTE_WINDOW;
                int vd = (int)(L.VoteT / 24.0) + 1, vh = (int)L.VoteT % 24;
                SupNote("vote", string.Format("CAMP VOTE: {0} calls a vote on your leadership, Day {1} {2:00}:00. You have one day to win people back: feed them, fix the walls, talk to them.",
                    lead?.Name ?? "The camp", vd, vh), "major");
            }
            return false;
        }

        int _voteFor, _voteAgainst;

        /// <summary>The survivor people would follow instead of you: trusted by the others, respected, not in love with you.
        /// Once someone has started gathering votes (LeaderStanding.Caller) it stays them while they live.</summary>
        Person VoteCaller()
        {
            var ad = SupAdults();
            string named = C?.Lead?.Caller ?? "";
            var held = named != "" ? ad.FirstOrDefault(p => p.Name == named) : null;
            return held ?? ad.OrderByDescending(p => NewLeaderScore(p)).ThenBy(p => p.Id).FirstOrDefault();
        }

        double NewLeaderScore(Person p)
        {
            double s = 0;
            foreach (var q in C.People) if (q != p && q.St != "dead" && q.Rel != null) s += Math.Max(0.0, q.Rel.Get(p.Id.ToString(), 0.0)) / 25.0;
            s += Math.Min(10, p.Kills) * 0.15;
            if (p.Traits != null) { if (p.Traits.Contains("leader")) s += 2.0; if (p.Traits.Contains("coward")) s -= 1.5; }
            if (p.Job == "Soldier" || p.Job == "Cop" || p.Job == "Medic") s += 0.6;
            s += (1.0 - SurvivorMind.TrustInLeader(this, p.Id)) * 1.5;   // the one who stood up to you
            if (p.Hp < 40) s -= 1.0;
            return s;
        }

        // ---------------- read side (UI) ----------------
        public SupportReport SupportNow(int ci = -1)
        {
            var r = new SupportReport();
            if (S == null) return r;
            if (ci < 0) ci = ViewCol;
            var c = ColonyAt(ci);
            if (c == null || !c.Started) return r;
            int prev = CurCol;
            Use(ci);
            try
            {
                var L = c.Lead;
                r.ExileOn = ExileAllowed();
                double raw = SupportRaw(out int adults, out double fw, out double aw);
                r.Adults = adults;
                r.P = L != null && L.P >= 0 ? L.P : raw;
                r.Band = L != null && L.P >= 0 ? L.Band : (r.P >= 0 ? SupportBandOf(r.P) : 0);
                r.Word = SUP_WORDS[r.Band];
                double trend = raw - r.P;
                r.Trend = r.P < 0 ? "" : trend > 0.015 ? "Rising" : trend < -0.015 ? "Falling" : "Holding";
                r.LowH = L?.LowH ?? 0; r.ClockLeftH = Math.Max(0.0, MUTINY_CLOCK - r.LowH);
                r.Vote = L != null && L.VoteT > 0; r.VoteInH = r.Vote ? Math.Max(0.0, L.VoteT - S.T) : 0;
                var bad = new Dictionary<string, SupportReason>(); var good = new Dictionary<string, SupportReason>();
                var worst = new Dictionary<string, float>();
                foreach (var p in SupAdults())
                {
                    float t = SurvivorMind.TrustInLeader(this, p.Id); float w = SupWeight(this, p);
                    if (t >= VOTE_KEEP) r.For++; else r.Against++;
                    var rs = SurvivorMind.TrustReasons(this, p.Id);
                    var mine = rs.Where(x => x.Delta < 0 && x.Key != "outlook").OrderBy(x => x.Delta).FirstOrDefault();
                    r.People.Add(new SupportPerson { Id = p.Id, Name = p.Name ?? "", Job = p.Job ?? "", Trust = t, Word = TrustWord(t), Why = mine.Text ?? "" });
                    foreach (var x in rs)
                    {
                        if (x.Key == "outlook" || x.Key == "time") continue;   // temperament / time served: not something to fix
                        var d = x.Delta < 0 ? bad : good;
                        if (!d.TryGetValue(x.Key, out var a)) { a = new SupportReason { Key = x.Key }; d[x.Key] = a; }
                        a.Sum += x.Delta * w; a.N++;
                        float k = Math.Abs(x.Delta);
                        if (!worst.TryGetValue(x.Key + (x.Delta < 0 ? "-" : "+"), out var wk) || k > wk) { worst[x.Key + (x.Delta < 0 ? "-" : "+")] = k; a.Text = x.Text; a.Who = p.First; }
                    }
                }
                r.Bad = bad.Values.OrderBy(x => x.Sum).Take(4).ToList();
                r.Good = good.Values.OrderByDescending(x => x.Sum).Take(3).ToList();
                r.People = r.People.OrderBy(x => x.Trust).ThenBy(x => x.Id).ToList();
                r.Status = SupportStatus(r);
            }
            finally { Use(prev >= 0 ? prev : ViewCol); }
            return r;
        }

        public static string TrustWord(float t) => t >= 0.60f ? "Loyal" : t >= SUP_STEADY ? "Backs you" : t >= SUP_UNEASY ? "Unsure" : t >= SUP_ANGRY ? "Angry" : "Wants you out";

        string SupportStatus(SupportReport r)
        {
            if (r.P < 0) return "Nobody here to lead yet.";
            if (r.Vote)
            {
                int h = (int)Math.Ceiling(r.VoteInH);
                return string.Format("CAMP VOTE in {0}h. Right now {1} would vote for you, {2} against. Win people back before then.", h, r.For, r.Against);
            }
            if (r.Band == 3 && r.ExileOn)
                return string.Format("Mutiny. If it stays like this for about {0} more hours they call a vote on you.", Math.Max(1, (int)Math.Ceiling(r.ClockLeftH)));
            if (r.Band == 3) return "Mutiny. People have stopped listening, but they still follow your orders.";
            if (r.Band == 2) return "People are angry. Fix what they're angry about before it turns into mutiny.";
            if (r.Band == 1) return "Some people have doubts. Nothing serious yet.";
            return "Most people back you.";
        }

        /// <summary>The pending exile screen (null when there is none to show).</summary>
        public ExileInfo PendingExile(int ci = -1)
        {
            var c = ColonyAt(ci < 0 ? ViewCol : ci);
            var L = c?.Lead;
            return L != null && !L.ExileSeen ? L.Exiled : null;
        }

        public void ExileShown(int ci = -1)
        {
            var c = ColonyAt(ci < 0 ? ViewCol : ci);
            if (c?.Lead != null) c.Lead.ExileSeen = true;
        }

        // ---------------- exile ----------------
        static string Hex2(Color c) => string.Format("#{0:x2}{1:x2}{2:x2}", (int)Mathf.Clamp(c.r * 255f, 0f, 255f), (int)Mathf.Clamp(c.g * 255f, 0f, 255f), (int)Mathf.Clamp(c.b * 255f, 0f, 255f));

        static string Cap(string s) => string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s.Substring(1);

        static string FirstSentence(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.IndexOf('.');
            return Cap(i > 0 ? s.Substring(0, i + 1) : s);
        }

        /// <summary>Where to start over: a free building 60..170 tiles from the old camp, away from the dead and the other
        /// camps. Deterministic (no rng). V.NONE when nothing fits.</summary>
        Vector2Int ExileSite(int ci, Vector2Int from)
        {
            var best = V.NONE; double bs = -1e18;
            for (int pass = 0; pass < 2 && best.x < 0; pass++)
                foreach (var cd in SpawnCandidates())
                {
                    var t = cd.t;
                    double d = V.F(t).DistTo(V.F(from));
                    if (pass == 0 ? (d < 60.0 || d > 170.0) : d < 40.0) continue;
                    double inf = InfAt(t);
                    if (inf > (pass == 0 ? 0.25 : 0.3)) continue;
                    double camp = 1e9;
                    foreach (var k in S.Camps) if (k.Alive) camp = Math.Min(camp, V.F(t).DistTo(V.F(k.Pos)));
                    if (camp < (pass == 0 ? 30.0 : 18.0)) continue;
                    if (CanStartAnywhere(t, ci) != "") continue;
                    double s = -Math.Abs(d - 95.0) - inf * 120.0 + Math.Min(camp, 60.0) * 0.3 + (cd.lm ? 6.0 : 0.0);
                    if (s > bs) { bs = s; best = t; }
                }
            return best;
        }

        public const int EXILE_CREW = 4;   // you start over with this many survivors (loyalists first, then drifters)

        void ExileLeader(int ci)
        {
            var old = S.Cols[ci];
            Use(ci); LogCol = ci;
            var mb = MainBase();
            if (mb == null) return;
            int lot = mb.Lot >= 0 ? mb.Lot : D.BuildingAt(mb.Pos);
            if (lot < 0) { AddLog("The camp argued all night and never agreed on anyone else. You stay in charge.", "info"); return; }
            var L = old.Lead ?? new LeaderStanding();
            var adults = SupAdults();
            var alive = old.People.Where(p => p.St != "dead").ToList();

            // who takes over, who walks out with you
            var leader = VoteCaller();
            if (leader == null) return;
            var trust = adults.ToDictionary(p => p.Id, p => SurvivorMind.TrustInLeader(this, p.Id));
            var with = adults.Where(p => p != leader && p.St == "base" && p.Sq <= 0 && p.Partner == 0 && p.Kids.Count == 0 && p.Inf < 0.0 && trust[p.Id] >= 0.55f)
                .OrderByDescending(p => trust[p.Id]).ThenBy(p => p.Id).Take(2).ToList();
            var lm = SurvivorMind.For(this, leader);
            var rep = SupportNow(ci); Use(ci); LogCol = ci;
            var why = rep.Bad.Take(3).Select(x => x.Text).Where(x => !string.IsNullOrEmpty(x)).ToList();
            double ruled = L.Days.Count > 0 ? L.Days.Skip(Math.Max(0, L.Days.Count - 7)).Average() : L.P;
            int dead = old.Stat("dead");
            int pop = alive.Count - with.Count;
            old.Started = false;   // StartError refuses a colony that is already set up
            Vector2Int site;
            try { site = ExileSite(ci, mb.Pos); } finally { old.Started = true; }
            if (site.x < 0) { SupNote("kept", "The camp voted you out, but there is nowhere to go. They let you stay, for now.", "info"); L.GraceT = S.T + SUP_GRACE_WON; return; }

            // ---- the colony becomes a faction camp ----
            var res = old.Res;
            double mats = 0; foreach (var m in Data.MATS) mats += res.Get(m, 0.0);
            int guns = 0; foreach (var kv in old.Weapons) guns += kv.Value;
            string att = ruled < 0.24 || dead >= 6 ? "Hostile" : "Neutral";
            string cid = "exiles_" + (S.Camps.Count(k => k.Id.StartsWith("exiles_")) + 1);
            string cname = old.Name.StartsWith("Colony ") ? leader.First + "'s Free Camp" : old.Name;
            var camp = new Camp
            {
                Id = cid, Name = cname, Leader = leader.Name, Pop = Math.Max(1, pop), Att = att,
                Rel = (double)D.Camps["attitudes"][att]["base"], Lot = lot, Pos = mb.Pos, Alive = true,
                Needs = new List<string>(), Surplus = new List<string>(),
                Desc = string.Format("Your old colony. They voted you out on day {0} and chose {1} to lead them.", Day(), leader.Name),
            };
            var lots = new List<int> { lot };
            foreach (var b in old.Bases) foreach (int l in b.Claim) if (l >= 0 && !lots.Contains(l)) lots.Add(l);
            if (mb.Lot >= 0 && !lots.Contains(mb.Lot)) lots.Add(mb.Lot);
            Vector2Int oldPos = mb.Pos;
            int oldBid = mb.Id;

            // ---- wipe the colony's hold on the world (its walls become the camp's palisade) ----
            var gone = new HashSet<int>(alive.Where(p => !with.Contains(p)).Select(p => p.Id));
            foreach (var p in old.People) { if (gone.Contains(p.Id) || p.St == "dead") { _peopleById.Remove(p.Id); } }
            foreach (var k in _claimed.Where(kv => gone.Contains(kv.Value)).Select(kv => kv.Key).ToList()) _claimed.Remove(k);
            foreach (var k in S.Fences.KeyList()) if (S.Fences[k].Col == ci) S.Fences.Remove(k);
            foreach (var k in S.Fsegs.KeyList()) if (S.Fsegs[k].Col == ci) S.Fsegs.Remove(k);
            foreach (var k in S.Towers.KeyList()) if (S.Towers[k].Col == ci) S.Towers.Remove(k);
            foreach (var k in S.Traps.KeyList()) if (S.Traps[k].Col == ci) S.Traps.Remove(k);
            foreach (var k in S.Gates.KeyList()) if (S.Gates[k] == ci) S.Gates.Remove(k);
            foreach (var k in S.Conv.KeyList()) if (S.Conv[k].Col == ci) S.Conv.Remove(k);
            foreach (var k in S.Guts.KeyList()) if (S.Guts[k].Col == ci) S.Guts.Remove(k);
            foreach (var k in S.Gut.KeyList()) if (S.Gut[k] == ci + 1) S.Gut.Remove(k);
            foreach (var k in S.Breaches.KeyList()) if (S.Breaches[k].Col == ci) S.Breaches.Remove(k);
            S.Walls.RemoveAll(w => w.Col == ci);
            for (int i = 0; i < S.Ctrl.Length; i++) if (S.Ctrl[i] == ci + 1) S.Ctrl[i] = 0;
            foreach (var w in S.Wrecks) if (w.Own == ci) w.Own = -1;
            foreach (var g in S.Groups) g.Tf = false;
            for (int i = 0; i < Zh.Count; i++) if (Zh[i] == 3) Zh[i] = 0;   // the night raid on the old camp breaks up
            S.FenceVer++; S.ConvVer++;

            // ---- a fresh colony in the same slot (index, name and research stay; everything else is new) ----
            var nc = NewColonyDict(old.Name);
            nc.Id = old.Id; nc.Name = old.Name;
            nc.Research = old.Research;
            nc.Trade = old.Trade;
            nc.Ppl = old.Ppl != null ? new PeopleCfg { Shifts = old.Ppl.Shifts, AutoRotate = old.Ppl.AutoRotate, AutoTalk = old.Ppl.AutoTalk } : null;
            nc.Res = new ODict<string, double>();
            foreach (var k in old.Res.Keys) nc.Res[k] = 0.0;
            nc.Res["food"] = 20.0; nc.Res["water"] = 20.0; nc.Res["meds"] = 3.0; nc.Res["ammo"] = 16.0; nc.Res["wood"] = 16.0; nc.Res["nails"] = 6.0;
            nc.Weapons["pistol"] = 1; nc.Weapons["pipe"] = 2;   // what you grabbed on the way out
            foreach (var kv in old.Stats) nc.Stats[kv.Key] = kv.Value;   // the run's tallies go on
            nc.Lead = new LeaderStanding
            {
                Exiles = L.Exiles + 1, VotesWon = L.VotesWon, ExileSeen = false,
                Exiled = new ExileInfo
                {
                    T = S.T, Day = Day(), OldName = old.Name, CampId = cid, CampName = cname, Leader = leader.Name, LeaderJob = leader.Job,
                    Att = att, Pop = pop, For = _voteFor, Against = _voteAgainst, Why = why,
                    Ruled = ruled < 0.24 ? "hard" : ruled < 0.40 ? "poorly" : "fairly",
                },
            };
            S.Cols[ci] = nc;
            CurCol = ci;

            // ---- the camp: data, territory, leader with a mind ----
            S.Camps.Add(camp);
            FactionInit(camp, S.Camps.Count - 1);
            camp.Color = Hex2(ColColor(ci));
            camp.Claim = lots;
            camp.Str = Math.Max(12.0, 8.0 + adults.Count * 1.6 + Math.Min(20, guns) * 1.2);
            camp.Stock = new ODict<string, double>
            {
                ["food"] = Math.Floor(res.Get("food", 0.0)), ["water"] = Math.Floor(res.Get("water", 0.0)), ["ammo"] = Math.Floor(res.Get("ammo", 0.0)),
                ["meds"] = Math.Floor(res.Get("meds", 0.0)), ["materials"] = Math.Floor(mats), ["fuel"] = Math.Floor(res.Get("fuel", 0.0)),
            };
            var st = camp.Stock;
            foreach (var k in new[] { "food", "water", "meds", "ammo" }) if (st.Get(k, 0.0) < camp.Pop * (k == "food" || k == "water" ? 2.0 : 0.3)) camp.Needs.Add(k);
            foreach (var k in new[] { "food", "water", "materials", "ammo" }) if (camp.Surplus.Count < 2 && st.Get(k, 0.0) > camp.Pop * 6.0) camp.Surplus.Add(k);
            if (camp.Needs.Count == 0) camp.Needs.Add("meds");
            if (camp.Surplus.Count == 0) camp.Surplus.Add("materials");
            camp.Rels = new List<double>(); for (int i = 0; i < Math.Max(4, S.Cols.Count); i++) camp.Rels.Add(camp.Rel);
            camp.Truce[ci.ToString()] = S.T + 72.0;   // they let you walk away; three days before old grudges turn into raids
            camp.NextRaid = Math.Max(camp.NextRaid, S.T + 96.0); camp.NextAmbush = Math.Max(camp.NextAmbush, S.T + 72.0);
            foreach (int l in lots) if (l >= 0 && l < S.Loot.Length) S.Loot[l] = 0f;
            camp.Mind = ExiledLeaderMind(camp, ci, leader, lm, why, ruled, dead, rep);
            _factionLotsDirty = true;

            // ---- you start over ----
            var crew = new List<Person>(with);
            var p0 = StartDoor(site);
            int nlot = D.BuildingAt(site);
            nc.Started = true; nc.StartT = S.T;
            nc.Lead.GraceT = S.T + SUP_GRACE_START;
            var nb = AddBase(p0, true, nlot);
            nb.Done = 1.0;
            if (PhaseARules) FoundPalisade(nb);
            foreach (var p in crew)
            {
                p.Col = ci; p.Base = nb.Id; p.Pos = V.F(p0); p.St = "base"; p.Goal = ""; p.Tgt = new Vector2Int(-1, -1); p.Lot = -1;
                p.Inb = Person.NO_BLD; p.Home = Person.NO_BLD; p.Work = ""; p.Wl = -1; p.Path = new List<Vector2>(); p.Carry = new ODict<string, double>();
                p.Wt = 0; p.Tf = 0; p.Sq = 0; p.Veh = false; p.Vlead = false; p.Vt = null; p.Vid = null; p.Vspd = null; p.Vcargo = null; p.Varm = null; p.Tow = false;
                p.Guard = false; p.Gauto = null; p.Post = null; p.Hold = false; p.Trav = null; p.Resume = false; p.Shift = 0; p.Dozing = false;
                p.Parents = new List<int>(); p.Preg = -1.0; p.PregBy = 0;
                if (p.Rel != null) foreach (var k in p.Rel.KeyList()) if (!int.TryParse(k, out int q) || !crew.Any(x => x.Id == q)) p.Rel.Remove(k);
                nc.People.Add(p); _peopleById[p.Id] = p;
            }
            string[] drift = { "Soldier", "Scavenger", "Farmer", "Civilian" };
            for (int i = 0; crew.Count < EXILE_CREW && i < 8; i++)
            {
                var np = AddPerson(drift[i % drift.Length], nb.Id, V.F(p0));
                crew.Add(np);
            }
            foreach (var p in crew) if (p.Job == "Soldier" || p.Job == "Cop") { nc.Weapons.AddTo("pistol", 1); nc.Kit.AddTo("soft_vest", 1); }
            nc.Nraid.LastDay = Day();   // a quiet first night: the dead have not found the new place yet
            _rtNeed[ci] = true;
            Equip();
            _baseLots.Clear(); _fxCache.Clear(); _encl.Clear(); _enclVer = -1; _claimVer = -1; _insideDirty = true; _flowDirty = true;
            RebuildSafe();
            for (int i = 0; i < 6; i++) SpawnGroup(true, p0);
            UpdateActive();
            SpawnDotsHourly();
            RebuildBuckets();
            var info = nc.Lead.Exiled;
            foreach (var p in crew) info.With.Add(p.Name + (with.Contains(p) ? "" : " (drifter)"));
            info.Site = string.Format("{0}, {1}", TakeName(nlot), D.DistrictAt(p0).Name);
            info.Quote = camp.Mind?.Mem.LastOrDefault(x => x.Kind == "exiled")?.Text ?? "";
            info.Story = camp.Mind != null ? (camp.Mind.Before + " " + camp.Mind.Lost).Trim() : "";
            LogCol = ci;
            AddLog(string.Format("EXILE: {0} is now {1}, led by {2}. They keep the camp, its people and its stores.", old.Name, cname, leader.Name), "major");
            AddLog(string.Format("You start over in {0} with {1}.", info.Site, string.Join(", ", crew.Select(p => p.First))), "major");
            LogCol = -1;
            SupportNote?.Invoke("exile", string.Format("You were voted out. {0} now leads {1}.", leader.Name, cname));
            Use(ViewCol);
            EmitChanged();
        }

        static readonly Dictionary<string, string> PAST_WHY = new Dictionary<string, string>
        {
            ["hungry"] = "People went hungry while they gave orders", ["breach"] = "The dead kept getting through the wall",
            ["deaths"] = "We kept burying people", ["friend_died"] = "Everyone lost somebody", ["risky"] = "They kept sending people out to die",
            ["talk"] = "They never listened to us", ["morale"] = "Everyone was at the end of their rope",
        };

        /// <summary>A LeaderBrain mind for the survivor who took over: their own past (SurvivorMind), a temperament from
        /// their personality, and a memory of throwing you out and of how you ruled.</summary>
        LeaderMind ExiledLeaderMind(Camp camp, int ci, Person who, Mind sm, List<string> why, double ruled, int dead, SupportReport rep)
        {
            var m = LeaderNew(camp);
            string her = who.Sex == "f" ? "her" : who.Sex == "m" ? "his" : "their";
            if (sm != null)
            {
                string oj = sm.OldJob ?? "";
                m.Before = string.Format("Was {0}, from {1}. On day one: {2}.", oj, sm.Home, (sm.DayOne ?? "").TrimEnd('.'));
                if (oj == "school") m.Before = string.Format("Was still in school when it started, from {0}.", sm.Home);
                string lw = (sm.LostWho ?? "").Replace("my ", her + " ");
                m.Lost = string.IsNullOrEmpty(lw) ? "" : string.Format("Lost {0}, {1}, at the start.", lw, sm.LostName);
                m.LostName = sm.LostName ?? "";
                m.Fear = FirstSentence((" " + sm.Fear).Replace(" my ", " " + her + " ").Trim());
                m.Values = sm.Brave > 0.4f ? "Strength keeps people alive." : sm.Hope0 > 0.3f ? "Nobody goes hungry. You share what you have." : sm.Work > 0.3f ? "Everyone pulls their weight." : "Keep your word. Listen to your people.";
                if (why.Count > 0 && rep.Bad.Count > 0 && rep.Bad[0].Key == "hungry") m.Values = "Nobody goes hungry again. Not on my watch.";
                m.Bold = V.Clamp(sm.Brave * 0.8, -1.0, 1.0);
                m.Greed = V.Clamp(-sm.Hope0 * 0.3 - sm.Work * 0.2, -1.0, 1.0);
                m.Trust = V.Clamp(sm.Hope0 * 0.5 - 0.2, -1.0, 1.0);
                m.Build = V.Clamp(sm.Work * 0.7 + 0.1, -1.0, 1.0);
            }
            m.Voice = who.Job == "Medic" ? "nurse" : who.Job == "Soldier" || who.Job == "Cop" ? "soldier" : who.Job == "Engineer" ? "engineer" : who.Job == "Farmer" ? "farmer" : "plain";
            while (m.Grudge.Count <= ci) { m.Grudge.Add(0.0); m.Favour.Add(0.0); m.LastRels.Add(CampRel(camp, m.LastRels.Count)); }
            m.Grudge[ci] = V.Clamp((0.40 - ruled) * 120.0 + dead * 2.0, 5.0, 60.0);
            m.Favour[ci] = ruled >= 0.40 ? 10.0 : 0.0;
            m.Anger = V.Clamp(0.3 + (0.40 - ruled), 0.2, 0.8); m.AngryAt = "col:" + ci;
            m.Hope = 0.55; m.Pride = 0.3; m.ProudOf = ""; m.Grief = Math.Max(m.Grief, dead > 2 ? 0.45 : 0.25);
            if (dead >= 3) LeaderRemember(m, "", "lost", string.Format("We buried {0} people under the old leader. Never again.", dead));
            string reason = PAST_WHY.TryGetValue(rep.Bad.Count > 0 ? rep.Bad[0].Key : "", out var pw) ? pw : "Nobody listened to us";
            LeaderRemember(m, "col:" + ci, "exiled", ruled < 0.24
                ? string.Format("We threw them out on day {0}. {1}. They had it coming.", Day(), reason)
                : string.Format("We voted them out on day {0}. {1}. Maybe they tried. It wasn't enough.", Day(), reason));
            m.Plan = "Holding the camp together after the vote.";
            m.Goal = "fortify"; m.GoalT = S.T;
            return m;
        }
    }
}
