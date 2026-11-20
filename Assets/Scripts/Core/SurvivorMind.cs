// v0.19 survivor minds (Austin: "add a brain to the normal AI that walk around in your colonies so when you hold down
// for the information you can talk to him and see how they're doing").
// Every survivor of your colony gets an offline mind: a personality, a past (hometown, old job, where they were on day
// one, who they lost, a hope, a fear, a regret, a habit), opinions about the people around them and about you, a mood
// read from their own state and the camp's, and a memory of what happened lately (read from the game log) and of what
// you talked about (TalkLog, kept in the save as GameState.Talk).
// READ-ONLY w.r.t. the sim: nothing here writes sim state or touches Game.Rng / Frng / GRng. All variety comes from a
// string hash of seed + survivor id + topic + day, so the classic parity seeds stay bit-exact. The only thing written
// is the TalkLog (a UI-side record: questions asked, your replies, names seen), and only when the TALK panel calls
// Ask / Reply / Observe.
// RULE (Austin): a direct player order to your own colony (gut, scavenge, rescue, jobs, squad moves...) ALWAYS wins; minds never block, delay, cancel or alter one - feelings show only in words and trust.
// Text is plain ASCII on purpose (the phone font has no fancy quotes or symbols). Survivors never mention the game,
// menus, numbers or stats: they talk about food, walls, people and the dead.
//
// API for other features (leader popularity, etc.):
//   SurvivorMind.TrustInLeader(g, survivorId)  0..1, how much this survivor trusts you right now
//   SurvivorMind.TrustReasons(g, survivorId)   the factors behind it (key, signed weight, a short plain line)
//   SurvivorMind.For(g, person)                the full Mind (personality, mood, backstory, relationships)
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DayZero.Core
{
    // ------------------------------------------------------------------ talk memory (save side, UI owned)
    public sealed class TalkPerson
    {
        public int N;                                                         // conversations (questions asked)
        public Dictionary<string, double> Asked = new Dictionary<string, double>();   // topic -> game hour last asked
        public Dictionary<string, int> AskedN = new Dictionary<string, int>();        // topic -> times asked
        public string LastTopic = "";
        public double LastT = -1.0, FirstT = -1.0;
        public double Trust;                                                  // sum of reply effects, clamped
        public List<string> Replies = new List<string>();                     // "T:tone" newest last (cap 12)
        public int SayDay; public double SayGain;                              // v0.19.2 typed talk: daily cap on kind-word trust
    }

    public sealed class TalkLog
    {
        public int V = 1;
        public Dictionary<int, TalkPerson> P = new Dictionary<int, TalkPerson>();
        public Dictionary<int, string> Names = new Dictionary<int, string>();    // every survivor id seen -> full name
        public Dictionary<int, double> Seen = new Dictionary<int, double>();     // id -> first game hour seen in camp
        public Dictionary<int, int> Kills0 = new Dictionary<int, int>();         // id -> kills when first seen (drift)
        public Dictionary<int, List<int>> Bonds = new Dictionary<int, List<int>>(); // id -> closest friends seen (the sim forgets the dead)
        public TalkPerson Of(int id) { if (!P.TryGetValue(id, out var t)) { t = new TalkPerson(); P[id] = t; } return t; }
    }

    public struct TrustReason
    {
        public string Key;      // fed | hungry | safe | breach | deaths | friend_died | risky | talk | time | morale | outlook
        public float Delta;     // signed contribution to TrustInLeader
        public string Text;     // short plain line, in the survivor's terms
        public TrustReason(string k, float d, string t) { Key = k; Delta = d; Text = t; }
    }

    public struct FallenInfo { public string Name, Job, Cause; public int Day; public double T; }

    public sealed class TalkReply { public string Id, Label; public TalkReply(string id, string label) { Id = id; Label = label; } }

    public sealed class TalkAnswer
    {
        public string Topic = "", Question = "", Text = "";
        public List<TalkReply> Replies = new List<TalkReply>();   // when non-empty the survivor waits for your answer
    }

    public sealed class Mind
    {
        public int Id; public string Name = "", First = "", Sex = "", Stage = "adult";
        // base personality (-1..1) and after drift
        public float Brave0, Brave, Chatty, Hope0, Hope, Work; public bool Dark; public bool Hardened, Broke;
        public string Home = "", OldJob = "", DayOne = "", Dream = "", Fear = "", Regret = "", Quirk = "", Miss = "";
        public string LostWho = "", LostName = "", LostHow = "";      // the one they lost at the start ("my sister", "Jess", "...")
        public bool BornHere, Kid;
        // now
        public float Mood;            // -1 awful .. 1 good
        public string MoodLine = "";
        public string Grief = "";     // first name of someone close who died lately ("" = none)
        public float Trust;           // TrustInLeader
        public List<TrustReason> Reasons = new List<TrustReason>();
        // relationships (first names, "" = none)
        public int BestFriendId = -1, PartnerId = -1;
        public string Partner = "", BestFriend = "", Crush = "", Rival = "", WatchTrust = "", LostPartner = "";
        public List<string> Kids = new List<string>();
    }

    public static class SurvivorMind
    {
        // ------------------------------------------------------------------ hashing (never the sim RNG)
        static uint Fnv(string s)
        {
            uint h = 2166136261u;
            for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; }
            h ^= h >> 15; h *= 0x2c1b3c6du; h ^= h >> 12;
            return h;
        }
        static uint H(Game g, int id, string salt) => Fnv((g?.S?.Seed ?? 0) + "|" + id + "|" + salt);
        static float U(Game g, int id, string salt) => (H(g, id, salt) % 10000u) / 10000f;
        static float S1(Game g, int id, string salt) => U(g, id, salt) * 2f - 1f;
        static string Pick(string[] a, uint h) => a.Length == 0 ? "" : a[(int)(h % (uint)a.Length)];

        // ------------------------------------------------------------------ memory store
        /// <summary>Fallen survivors of the run (Runtime sets this to RunStats' memorial list). Null = read the game log.</summary>
        public static Func<IEnumerable<FallenInfo>> FallenSource;
        static GameState _memFor; static TalkLog _mem;
        static readonly JsonSerializer Ser = JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

        public static TalkLog Memory(GameState s)
        {
            if (s == null) return new TalkLog();
            if (ReferenceEquals(s, _memFor) && _mem != null) return _mem;
            TalkLog m = null;
            if (s.Talk != null) { try { m = s.Talk.ToObject<TalkLog>(Ser); } catch { m = null; } }
            m ??= new TalkLog();
            m.P ??= new Dictionary<int, TalkPerson>(); m.Names ??= new Dictionary<int, string>();
            m.Seen ??= new Dictionary<int, double>(); m.Kills0 ??= new Dictionary<int, int>(); m.Bonds ??= new Dictionary<int, List<int>>();
            _memFor = s; _mem = m;
            return m;
        }

        /// <summary>Write the talk memory into the state (it is saved with the game).</summary>
        public static void Commit(GameState s)
        {
            if (s == null || !ReferenceEquals(s, _memFor) || _mem == null) return;
            try { s.Talk = JObject.FromObject(_mem, Ser); } catch { }
        }

        /// <summary>UI tick: remember the names of everyone in camp (so a friend who dies later is still known by name).</summary>
        public static bool Observe(Game g)
        {
            if (g?.S == null || g.C == null) return false;
            var m = Memory(g.S); bool ch = false;
            foreach (var p in g.C.People)
            {
                if (p.St == "dead") continue;
                if (!m.Names.TryGetValue(p.Id, out var n) || n != p.Name) { m.Names[p.Id] = p.Name ?? ""; ch = true; }
                if (!m.Seen.ContainsKey(p.Id)) { m.Seen[p.Id] = g.S.T; m.Kills0[p.Id] = p.Kills; ch = true; }
                if (p.Rel == null) continue;
                if (!m.Bonds.TryGetValue(p.Id, out var bl)) { bl = new List<int>(); m.Bonds[p.Id] = bl; }
                foreach (var kv in p.Rel)
                    if (kv.Value >= Game.REL_FRIEND * 0.6 && int.TryParse(kv.Key, out int qid) && !bl.Contains(qid)) { bl.Add(qid); ch = true; }
                while (bl.Count > 6) bl.RemoveAt(0);
            }
            return ch;
        }

        // ------------------------------------------------------------------ events from the log
        public sealed class Ev { public string Kind = "", Who = "", What = "", Where = ""; public double T; }
        static readonly Regex R_DIED = new Regex(@"^(.+?) \(([A-Za-z]+)\) (.+)\.$");
        static readonly Regex R_TURNED = new Regex(@"^(.+?) (?:was hiding a bite and turned|turned inside)");
        static readonly Regex R_LOOT = new Regex(@"^(.+?) brought back (.+?)(?: to .+)?\.$");
        static readonly Regex R_PART = new Regex(@"^(.+?) found an? (.+?) at (.+)\.$");
        static readonly Regex R_HORDE = new Regex(@"^A horde of \d+ is moving on (.+?)!");
        static readonly Regex R_BREACH = new Regex(@"^Zombies broke through the (gate|fence) on (.+)!");
        static readonly Regex R_BITTEN = new Regex(@"^(.+?) was bitten");
        static readonly Regex R_JOIN = new Regex(@"^A stranger, (.+?) \(");
        static readonly Regex R_HURT = new Regex(@"^(.+?) was hurt in an? (.+?) ambush");
        static readonly Regex R_JUMPED = new Regex(@"^(.+?) jumped (.+?) near (.+?) and took");
        static readonly Regex R_PULLED = new Regex(@"^(.+?) pulled through");
        static readonly Regex R_REBUILT = new Regex(@"^(.+?) rebuilt the broken wall on (.+)\.$");
        static readonly Regex R_LOSTC = new Regex(@"^Lost contact with (.+?) in (.+?)\.");
        static readonly Regex R_RESCUED = new Regex(@"^Rescued (.+?)[:]");

        static int MyCol(Game g) { int i = g.S.Cols.IndexOf(g.C); return i < 0 ? g.CurCol : i; }

        /// <summary>Recent events of your colony (newest first), within `hours`.</summary>
        // v0.19 popularity: the same few windows are read for every survivor in the same hour; parse the log once per
        // (state, time, colony, log tail, window). Callers only read the list.
        static GameState _recS; static double _recT = -1; static int _recCol = -2, _recN = -1;
        static readonly Dictionary<double, List<Ev>> _recCache = new Dictionary<double, List<Ev>>();

        public static List<Ev> Recent(Game g, double hours = 72.0)
        {
            if (g?.S?.Log == null || g.C == null) return new List<Ev>();
            int lastN = g.S.Log.Count > 0 ? g.S.Log[g.S.Log.Count - 1].N : 0;
            int myc = MyCol(g);
            if (!ReferenceEquals(_recS, g.S) || _recT != g.S.T || _recCol != myc || _recN != lastN) { _recCache.Clear(); _recS = g.S; _recT = g.S.T; _recCol = myc; _recN = lastN; }
            if (_recCache.TryGetValue(hours, out var hit)) return hit;
            var res = RecentScan(g, hours);
            _recCache[hours] = res;
            return res;
        }

        static List<Ev> RecentScan(Game g, double hours)
        {
            var list = new List<Ev>();
            int col = MyCol(g);
            for (int i = g.S.Log.Count - 1; i >= 0; i--)
            {
                var e = g.S.Log[i];
                if (e == null || g.S.T - e.T > hours) break;
                if (e.Col != col && e.Col >= 0 && g.S.Cols.Count > 1) continue;
                string t = e.Text ?? ""; Match m;
                Ev ev = null;
                if ((m = R_TURNED.Match(t)).Success) ev = new Ev { Kind = "died", Who = m.Groups[1].Value, What = "turned" };
                else if (e.Kind == "death" && (m = R_DIED.Match(t)).Success) ev = new Ev { Kind = "died", Who = m.Groups[1].Value, What = m.Groups[3].Value };
                else if ((m = R_LOOT.Match(t)).Success) ev = new Ev { Kind = "loot", Who = m.Groups[1].Value, What = m.Groups[2].Value };
                else if ((m = R_PART.Match(t)).Success) ev = new Ev { Kind = "part", Who = m.Groups[1].Value, What = m.Groups[2].Value, Where = m.Groups[3].Value };
                else if ((m = R_HORDE.Match(t)).Success) ev = new Ev { Kind = "horde", Where = m.Groups[1].Value };
                else if ((m = R_BREACH.Match(t)).Success) ev = new Ev { Kind = "breach", What = m.Groups[1].Value, Where = m.Groups[2].Value };
                else if ((m = R_BITTEN.Match(t)).Success) ev = new Ev { Kind = "bitten", Who = m.Groups[1].Value };
                else if ((m = R_JOIN.Match(t)).Success) ev = new Ev { Kind = "joined", Who = m.Groups[1].Value };
                else if ((m = R_HURT.Match(t)).Success) ev = new Ev { Kind = "ambush", Who = m.Groups[1].Value, What = m.Groups[2].Value };
                else if ((m = R_JUMPED.Match(t)).Success) ev = new Ev { Kind = "robbed", Who = m.Groups[2].Value, What = m.Groups[1].Value, Where = m.Groups[3].Value };
                else if ((m = R_PULLED.Match(t)).Success) ev = new Ev { Kind = "cured", Who = m.Groups[1].Value };
                else if ((m = R_REBUILT.Match(t)).Success) ev = new Ev { Kind = "rebuilt", Who = m.Groups[1].Value, Where = m.Groups[2].Value };
                else if ((m = R_LOSTC.Match(t)).Success) ev = new Ev { Kind = "lostgroup", What = m.Groups[1].Value, Where = m.Groups[2].Value };
                else if ((m = R_RESCUED.Match(t)).Success) ev = new Ev { Kind = "rescued", What = m.Groups[1].Value };
                else if (t.StartsWith("People are going hungry")) ev = new Ev { Kind = "hungry" };
                else if (t.StartsWith("A supply drop landed")) ev = new Ev { Kind = "drop" };
                else if (t.Contains(" raided ") && t.Contains(" and took ")) ev = new Ev { Kind = "raided", Who = t.Split(new[] { " raided " }, StringSplitOptions.None)[0] };
                else if (t.StartsWith("Dawn at ") || t.Contains("the line held")) ev = new Ev { Kind = "held" };
                if (ev == null) continue;
                ev.T = e.T;
                list.Add(ev);
            }
            return list;
        }

        public static List<FallenInfo> Fallen(Game g)
        {
            var r = new List<FallenInfo>();
            try { var src = FallenSource?.Invoke(); if (src != null) r.AddRange(src); } catch { }
            if (r.Count == 0 && g?.S != null)
                foreach (var e in Recent(g, 1e9)) if (e.Kind == "died") r.Add(new FallenInfo { Name = e.Who, Cause = e.What, T = e.T, Day = (int)(e.T / 24.0) + 1 });
            return r;
        }

        static string FirstOf(string n) => string.IsNullOrEmpty(n) ? "" : n.Split(' ')[0];

        // ------------------------------------------------------------------ backstory tables
        static readonly string[] TOWNS = { "Tacoma", "Spokane", "Boise", "Eugene", "Medford", "Reno", "Fresno", "Bakersfield", "Tulsa", "Wichita", "Dayton",
            "Toledo", "Scranton", "Erie", "Duluth", "Billings", "Provo", "El Paso", "Lubbock", "Mobile", "Shreveport", "Macon", "a farm town nobody's heard of",
            "a trailer park outside Yakima", "the suburbs", "right here in the city", "a fishing town up the coast", "an army base down south" };
        static readonly string[] JOB_ANY = { "a barista", "a bartender", "a line cook", "an accountant", "a substitute teacher", "a mail carrier", "a bus driver",
            "a cashier at a big box store", "a call center rep", "a hairdresser", "a real estate agent", "a youth pastor", "a dental hygienist",
            "a pizza delivery driver", "a warehouse picker", "an insurance adjuster", "a wedding photographer", "a librarian", "a tattoo artist", "a dog groomer" };
        static readonly Dictionary<string, string[]> JOB_BY = new Dictionary<string, string[]>
        {
            ["Medic"] = new[] { "an ER nurse", "a paramedic", "a vet tech", "a med student", "a hospice nurse" },
            ["Soldier"] = new[] { "National Guard", "an Army mechanic", "a Marine, two tours", "a security contractor", "a range instructor" },
            ["Cop"] = new[] { "a patrol cop", "a mall security guard", "a county deputy", "a corrections officer", "a bouncer" },
            ["Engineer"] = new[] { "an electrician", "a plumber", "a carpenter", "a welder", "an HVAC tech", "a roofer" },
            ["Scientist"] = new[] { "a lab tech", "a grad student in chemistry", "a high school science teacher", "a water plant chemist", "a pharmacist" },
            ["Farmer"] = new[] { "a farmhand", "a landscaper", "a community garden volunteer", "a nursery worker", "a rancher's kid" },
            ["Scavenger"] = new[] { "a bike courier", "a repo man", "a pawn shop clerk", "a junkyard worker", "a parkour kid with a YouTube channel" },
        };
        static readonly string[] DAY_ONE = { "stuck in traffic on the interstate", "at work, on a double shift", "in a hospital waiting room", "at my kid's soccer game",
            "asleep. Slept right through the first night", "at a gas station buying smokes", "at the airport, flight got cancelled", "at a wedding, of all things",
            "at the laundromat", "in a dentist's chair", "at the grocery store, the shelves were already empty", "on a bus that never got where it was going",
            "at my mom's place, Sunday dinner", "camping. Came back down the mountain and the town was gone", "in a bar, watching it on the news like it was a movie",
            "at the DMV. Seriously. Waiting for my number" };
        static readonly string[] REL_F = { "my mom", "my sister", "my wife", "my daughter", "my grandma", "my best friend" };
        static readonly string[] REL_M = { "my dad", "my brother", "my husband", "my son", "my grandpa", "my roommate" };
        static readonly string[] REL_PET = { "my dog", "my old cat", "my dog" };
        static readonly string[] NAMES_F = { "Jess", "Maria", "Annie", "Kayla", "Rosa", "Becca", "Lena", "Tasha", "June", "Carol", "Mei", "Nora" };
        static readonly string[] NAMES_M = { "Danny", "Marcus", "Joe", "Tommy", "Luis", "Ray", "Eli", "Kevin", "Sam", "Hank", "Andre", "Pete" };
        static readonly string[] NAMES_PET = { "Biscuit", "Tank", "Pepper", "Moose", "Luna", "Rocco", "Peanut", "Duke" };
        static readonly string[] LOST_HOW = { "We got separated at the evacuation point. I tell myself they made it onto a bus.", "I was on the phone with them when the line went dead.",
            "They got bit the first night. I couldn't... I didn't do what I should have.", "I went back for them. The house was empty and the door was open.",
            "They were three states away. No calls, no texts, nothing since.", "The army said they'd bring them to the stadium. Everybody knows what happened at the stadium.",
            "I watched it happen from across the street. Couldn't get there in time.", "I still don't know. Not knowing is worse, I think." };
        static readonly string[] DREAMS = { "find a lake with no dead around it and just fish. All day.", "open a little diner. Pancakes, bad coffee, everybody welcome.",
            "drive to the ocean and see if it still looks the same.", "sleep eight hours in a real bed with the door unlocked.", "plant an orchard. Something that takes years. Something that assumes there's a later.",
            "find out if my people made it.", "teach kids to read again. Somebody has to.", "build a house with my own hands. Big porch.", "see a ball game. Any game. Hot dogs.",
            "get a dog. A big dumb one." };
        static readonly string[] FEARS = { "getting bit and not telling anybody. I think about that a lot.", "the dark. I know. Grown adult.", "being the reason someone else dies.",
            "being alone at the end. Not dying, just... alone.", "that this is it. That this is all it'll ever be now.", "fire. We've got so much wood around here.",
            "turning in my sleep and nobody noticing.", "running out of food in winter.", "losing my mind before I lose my life." };
        static readonly string[] REGRETS = { "I left someone behind on day two. They were yelling my name. I kept running.", "I took the last bottle of insulin from a pharmacy. There was a guy behind me who needed it.",
            "I never told my dad I was sorry. We hadn't talked in years.", "I locked a door I shouldn't have locked.", "I lied about why I was late, the day it started. Small thing. Can't stop thinking about it.",
            "I used to complain about my job every single day. I'd give anything for that boring desk now.", "I stole from the first group I was with. Food. They didn't make it, and I don't know if it was my fault.",
            "I didn't go to my sister's birthday. Said I was busy. It was two days before." };
        static readonly string[] QUIRKS = { "I count my steps. Always have. Seven hundred and something to the gate, if you're wondering.", "I hum when I'm scared. Drives the others nuts.",
            "I still wind my watch every morning. It's the only thing that keeps time anymore.", "I name the dead ones before I put them down. Feels more decent.",
            "I keep a list of every movie I'd watch again. It's long.", "I can't sleep unless my boots are on.", "I collect bottle caps. Don't ask why. I don't know either.",
            "I talk to the crows. They're good listeners.", "I check the locks three times. Every night.", "I whittle. Got a whole little zoo of wooden animals in my bag." };
        static readonly string[] MISS = { "coffee. Real coffee. With the foam on it.", "music. Just turning it up loud in the car.", "hot showers. I'd kill for a hot shower. Well. You know what I mean.",
            "pizza at two in the morning.", "being bored. Real boredom, the safe kind.", "my phone. Not even for calls. Just scrolling garbage at night.", "the smell of rain on hot asphalt, without worrying what's in it.",
            "Sunday mornings. Cartoons, cereal, nobody needing anything.", "grocery stores. The lights, the music. Thirty kinds of cereal.", "fireworks. Now any loud noise just means trouble." };

        static Mind _cacheMind; static double _cacheT = -1; static int _cacheId = -1; static GameState _cacheS;

        // ------------------------------------------------------------------ the mind
        public static Mind For(Game g, Person p)
        {
            if (g?.S == null || p == null) return null;
            if (_cacheMind != null && _cacheId == p.Id && ReferenceEquals(_cacheS, g.S) && Math.Abs(_cacheT - g.S.T) < 0.05) return _cacheMind;
            var m = new Mind { Id = p.Id, Name = p.Name ?? "", First = p.First, Sex = p.Sex ?? "" };
            int id = p.Id;
            string stage = "adult";
            try { stage = g.AgeStage(p); } catch { }
            m.Stage = stage; m.Kid = stage != "adult"; m.BornHere = p.Born >= 0.0 && stage != "adult";

            // personality (hash) + the Expansion traits where present
            m.Brave0 = S1(g, id, "brave"); m.Chatty = S1(g, id, "chatty"); m.Hope0 = S1(g, id, "hope"); m.Work = S1(g, id, "work");
            m.Dark = U(g, id, "dark") < 0.45f;
            var tr = p.Traits;
            if (tr != null)
            {
                if (tr.Contains("coward")) m.Brave0 = Mathf.Min(m.Brave0, -0.5f);
                if (tr.Contains("leader") || tr.Contains("tough")) m.Brave0 = Mathf.Max(m.Brave0, 0.4f);
                if (tr.Contains("quiet")) m.Chatty = Mathf.Min(m.Chatty, -0.5f);
                if (tr.Contains("loudmouth")) m.Chatty = Mathf.Max(m.Chatty, 0.6f);
            }
            // backstory
            m.Home = Pick(TOWNS, H(g, id, "town"));
            string[] jobs = JOB_BY.TryGetValue(p.Job ?? "", out var jb) && U(g, id, "jobfit") < 0.7f ? jb : JOB_ANY;
            m.OldJob = Pick(jobs, H(g, id, "oldjob"));
            m.DayOne = Pick(DAY_ONE, H(g, id, "day1"));
            bool petLoss = U(g, id, "pet") < 0.15f;
            bool fem = U(g, id, "relsex") < 0.5f;
            m.LostWho = petLoss ? Pick(REL_PET, H(g, id, "relw")) : Pick(fem ? REL_F : REL_M, H(g, id, "relw"));
            m.LostName = petLoss ? Pick(NAMES_PET, H(g, id, "reln")) : Pick(fem ? NAMES_F : NAMES_M, H(g, id, "reln"));
            if (m.Kid) { m.LostWho = fem ? "my mom" : "my dad"; m.OldJob = "school"; }
            if ((m.LostWho == "my wife" && m.Sex == "f") || (m.LostWho == "my husband" && m.Sex == "m")) m.LostWho = m.Sex == "f" ? "my girlfriend" : "my boyfriend";
            m.LostHow = petLoss ? "Had to leave them in the yard when we ran. I still hear the barking some nights." : Pick(LOST_HOW, H(g, id, "relhow"));
            m.Dream = Pick(DREAMS, H(g, id, "dream")); m.Fear = Pick(FEARS, H(g, id, "fear")); m.Regret = Pick(REGRETS, H(g, id, "regret"));
            m.Quirk = Pick(QUIRKS, H(g, id, "quirk")); m.Miss = Pick(MISS, H(g, id, "miss"));

            // relationships (sim relations + hash for rivals / crushes)
            var mem = Memory(g.S);
            var alive = g.C.People.Where(q => q.St != "dead" && q.Id != p.Id).ToList();
            var partner = alive.FirstOrDefault(q => q.Id == p.Partner && p.Partner > 0);
            m.Partner = partner?.First ?? "";
            m.LostPartner = string.IsNullOrEmpty(p.Lost) ? "" : FirstOf(p.Lost);
            foreach (int k in p.Kids) { var kq = alive.FirstOrDefault(q => q.Id == k); if (kq != null) m.Kids.Add(kq.First); }
            double best = Game.REL_FRIEND * 0.6; Person bf = null;
            foreach (var q in alive)
            {
                if (q == partner) continue;
                double v = p.Rel?.Get(q.Id.ToString(), 0.0) ?? 0.0;
                if (v > best) { best = v; bf = q; }
            }
            m.BestFriend = bf?.First ?? ""; m.BestFriendId = bf?.Id ?? -1; m.PartnerId = partner?.Id ?? -1;
            bool adult = !m.Kid;
            if (adult && partner == null)
                foreach (var q in alive.OrderBy(q => H(g, Math.Min(id, q.Id) * 7919 + Math.Max(id, q.Id), "crush")))
                {
                    if (q == bf || q.First == m.First || p.Kids.Contains(q.Id) || p.Parents.Contains(q.Id) || q.Partner > 0) continue;
                    bool qa = true; try { qa = g.IsAdult(q); } catch { }
                    if (!qa) continue;
                    if (U(g, id * 31 + q.Id, "crushy") < 0.22f) m.Crush = q.First;
                    break;
                }
            foreach (var q in alive.OrderBy(q => H(g, id * 104729 + q.Id, "rival")))
            {
                if (q == partner || q == bf || q.First == m.Crush || q.First == m.First || p.Kids.Contains(q.Id) || p.Parents.Contains(q.Id)) continue;
                double v = p.Rel?.Get(q.Id.ToString(), 0.0) ?? 0.0;
                if (v < Game.REL_FRIEND && U(g, id * 13 + q.Id, "rivaly") < 0.35f) m.Rival = q.First;
                break;
            }
            Person wt = null; double ws = -1;
            foreach (var q in alive)
            {
                bool fighter = q.Job == "Soldier" || q.Job == "Cop" || q.Guard;
                if (!fighter) continue;
                double s = q.Kills * 2.0 + (p.Rel?.Get(q.Id.ToString(), 0.0) ?? 0.0) + (q.Job == "Soldier" ? 10 : 0) - (q.First == m.Rival ? 100 : 0);
                if (s > ws) { ws = s; wt = q; }
            }
            m.WatchTrust = wt?.First ?? "";

            // grief: someone close died lately (partner, friend by relation, or a kid)
            var fallen = Fallen(g);
            var bonds = new List<int>();
            if (mem.Bonds.TryGetValue(p.Id, out var bl0)) bonds.AddRange(bl0);
            foreach (var kv in p.Rel ?? new ODict<string, double>()) if (kv.Value >= Game.REL_FRIEND * 0.6 && int.TryParse(kv.Key, out int rq) && !bonds.Contains(rq)) bonds.Add(rq);
            for (int bi = bonds.Count - 1; bi >= 0; bi--)
            {
                int qid = bonds[bi];
                if (alive.Any(q => q.Id == qid)) continue;
                if (!mem.Names.TryGetValue(qid, out var nm)) continue;
                var f = fallen.LastOrDefault(x => x.Name == nm);
                if (f.Name != null && g.S.T - (f.T > 0 ? f.T : (f.Day - 1) * 24.0 + 12.0) < 24.0 * 5) { m.Grief = FirstOf(nm); break; }
            }
            if (m.Grief == "" && m.LostPartner != "")
            {
                var f = fallen.LastOrDefault(x => FirstOf(x.Name) == m.LostPartner);
                if (f.Name != null && g.S.T - (f.T > 0 ? f.T : (f.Day - 1) * 24.0) < 24.0 * 7) m.Grief = m.LostPartner;
            }

            // drift: fights harden the nervous, grief and hunger dim the hopeful
            double seen = mem.Seen.TryGetValue(id, out var st0) ? st0 : g.C.StartT;
            int k0 = mem.Kills0.TryGetValue(id, out var kk) ? kk : 0;
            float days = (float)Math.Max(0.0, (g.S.T - seen) / 24.0);
            int nights = 0; try { nights = g.C.Nraid?.Nights ?? 0; } catch { }
            m.Brave = Mathf.Clamp(m.Brave0 + Mathf.Min(0.7f, p.Kills * 0.05f) + Mathf.Min(0.3f, days * 0.02f) + Mathf.Min(0.2f, nights * 0.02f), -1f, 1f);
            m.Hardened = m.Brave0 < -0.15f && m.Brave > 0.2f;
            var sit = SituationOf(g, p);
            m.Hope = Mathf.Clamp(m.Hope0 + (m.Grief != "" ? -0.6f : 0f) + (sit.Starving ? -0.4f : sit.FoodDays < 2 ? -0.2f : 0.05f) + (float)((g.C.Morale - 60.0) / 150.0), -1f, 1f);
            m.Broke = p.FreezeEndT > g.S.T || p.Stress > 80.0;

            // mood
            float mood = 0.15f + m.Hope0 * 0.2f;
            if (sit.Starving) mood -= 0.6f; else if (sit.FoodDays < 2) mood -= 0.25f;
            if (p.Hp < 35) mood -= 0.45f; else if (p.Hp < 65) mood -= 0.2f;
            if (sit.BittenKnown) mood -= 0.7f;
            if (m.Grief != "") mood -= 0.5f;
            mood += (float)((g.C.Morale - 60.0) / 120.0);
            if (sit.Breaches > 0) mood -= 0.2f;
            if (sit.RaidComing) mood -= 0.15f * (1f - m.Brave);
            if (m.Partner != "") mood += 0.1f;
            if (Game.ExpansionRules) mood -= (float)(p.Stress / 200.0 + p.Fatigue / 400.0);
            m.Mood = Mathf.Clamp(mood, -1f, 1f);
            m.MoodLine = MoodLine(g, p, m, sit);

            // trust in you
            Trust(g, p, m, sit, mem, fallen);
            _cacheMind = m; _cacheId = id; _cacheT = g.S.T; _cacheS = g.S;
            return m;
        }

        // ------------------------------------------------------------------ situation (camp + self), read only
        public sealed class Situation
        {
            public double Food, Water, Meds, Ammo, FoodDays, WaterDays; public bool Starving, BittenKnown, HiddenBite, Away, Night;
            public int Alive, Breaches, Damaged, Injured, Medics, Farmers, IdleScav, Guards, BedsShort; public bool Garden, RaidComing, RaidNow, MajorRaid;
            public string WeakSide = "", WeakStreet = "", HurtName = "", BittenName = "", Period = "day";
            public int Hour;
        }

        static readonly string[] COMPASS = { "east", "northeast", "north", "northwest", "west", "southwest", "south", "southeast" };

        public static Situation SituationOf(Game g, Person p)
        {
            var s = new Situation();
            var c = g.C; var r = c.Res;
            s.Food = r.Get("food", 0.0); s.Water = r.Get("water", 0.0); s.Meds = r.Get("meds", 0.0); s.Ammo = r.Get("ammo", 0.0);
            var alive = c.People.Where(q => q.St != "dead").ToList();
            s.Alive = alive.Count;
            double eat = 0.0;
            foreach (var q in alive) { string st = "adult"; try { st = g.AgeStage(q); } catch { } eat += st == "baby" ? 0.4 : st == "child" ? 0.7 : 1.0; }
            eat = Math.Max(0.5, eat);
            s.FoodDays = s.Food / eat; s.WaterDays = s.Water / eat;
            s.Starving = s.Food < 1.0 || s.Water < 1.0;
            s.BittenKnown = p.Known && p.Inf >= 0.0; s.HiddenBite = !p.Known && p.Inf >= 0.0;
            s.Away = p.St != "base";
            s.Hour = g.Hour();
            s.Night = s.Hour >= 21 || s.Hour < 5;
            s.Period = s.Hour >= 5 && s.Hour < 12 ? "morning" : s.Hour < 17 ? "afternoon" : s.Hour < 21 ? "evening" : "night";
            foreach (var q in alive)
            {
                if (q.Hp < 55) { s.Injured++; if (q.Id != p.Id && (s.HurtName == "" || q.Hp < 30)) s.HurtName = q.First; }
                if (q.Known && q.Inf >= 0.0 && q.Id != p.Id) s.BittenName = q.First;
                if (q.Job == "Medic") s.Medics++;
                if (q.Job == "Farmer") s.Farmers++;
                if (q.Guard) s.Guards++;
                if (q.Job == "Scavenger" && q.St == "base" && !q.Guard && !q.Hold) s.IdleScav++;
            }
            try { s.BedsShort = Math.Max(0, alive.Count - g.BedsTotal()); } catch { }
            s.Garden = c.Bases.Any(b => b.U("garden") > 0);
            int ci = MyCol(g);
            s.Breaches = 0;
            Vector2 acc = Vector2.zero; int n = 0; int worstK = -1; double worst = 2.0;
            foreach (var kv in g.S.Breaches) if (kv.Value.Col == ci && !g.S.Fences.Has(kv.Key)) { s.Breaches++; { var tq = g.Tp(kv.Key); acc += new Vector2(tq.x, tq.y); } n += 3; worstK = kv.Key; worst = -1; }
            foreach (var kv in g.S.Fences)
            {
                if (kv.Value.Col != ci) continue;
                double mx = 1.0; try { mx = g.FenceTileMax(kv.Key); } catch { }
                double f = mx > 0 ? kv.Value.Hp / mx : 1.0;
                if (f < 0.5) { s.Damaged++; { var tq = g.Tp(kv.Key); acc += new Vector2(tq.x, tq.y); } n++; if (f < worst) { worst = f; worstK = kv.Key; } }
            }
            var mb = c.Bases.FirstOrDefault(b => b.Main) ?? c.Bases.FirstOrDefault();
            if (n > 0 && mb != null)
            {
                var d = acc / n - new Vector2(mb.Pos.x, mb.Pos.y);
                if (d.sqrMagnitude > 0.5f)
                {
                    float a = Mathf.Atan2(-d.y, d.x);   // map y grows south
                    int i = ((int)Math.Round(a / (Mathf.PI / 4f)) % 8 + 8) % 8;
                    s.WeakSide = COMPASS[i];
                }
                if (worstK >= 0) s.WeakStreet = Game.StreetName(g.Tp(worstK));
            }
            var R0 = c.Nraid;
            if (R0 != null) { s.RaidNow = R0.On && R0.Eta >= 0 && g.S.T >= R0.Eta - 0.25; s.RaidComing = R0.On || R0.Warned; s.MajorRaid = R0.Major; }
            return s;
        }

        static string MoodLine(Game g, Person p, Mind m, Situation s)
        {
            if (m.Kid && m.Stage == "baby") return "Too little to talk. Grabs your finger and won't let go.";
            if (s.BittenKnown) return "Bitten. Knows what that means.";
            if (m.Grief != "") return "Grieving " + m.Grief + ".";
            if (s.HiddenBite) return "Pale, sweating, keeps a sleeve pulled down.";
            if (m.Broke) return "Shut down. Staring at nothing.";
            if (s.Starving) return "Hungry and short-tempered.";
            if (p.Hp < 35) return "Badly hurt. Trying not to show it.";
            if (s.RaidNow) return m.Brave > 0.3f ? "Locked in. Watching the wall." : "Scared. Hands won't stop shaking.";
            if (p.Hp < 65) return "Banged up, but on their feet.";
            if (Game.ExpansionRules && p.Fatigue > 70) return "Running on empty.";
            if (m.Mood > 0.45f) return m.Partner != "" ? "In good spirits. Keeps glancing at " + m.Partner + "." : "In good spirits.";
            if (m.Mood > 0.15f) return m.Hardened ? "Calm. Harder than when they got here." : "Holding up.";
            if (m.Mood > -0.2f) return m.Hope < 0 ? "Quiet. Worn down." : "Tired, but hanging in.";
            return "Struggling.";
        }

        // ------------------------------------------------------------------ trust in the leader
        static void Trust(Game g, Person p, Mind m, Situation s, TalkLog mem, List<FallenInfo> fallen)
        {
            var R = m.Reasons; R.Clear();
            void A(string k, float d, string t) { if (Math.Abs(d) > 0.001f) R.Add(new TrustReason(k, d, t)); }
            if (s.Starving) A("hungry", -0.2f, "We're going hungry.");
            else if (s.FoodDays < 2) A("hungry", -0.07f, "Food's running low.");
            else if (s.FoodDays > 5) A("fed", 0.1f, "We eat every day.");
            else A("fed", 0.04f, "There's food on the table.");
            int recentBreach = Recent(g, 48).Count(e => e.Kind == "breach");
            if (s.Breaches > 0 || recentBreach > 0) A("breach", -0.05f - 0.03f * Math.Min(3, recentBreach + s.Breaches), "The dead got through the wall.");
            else if (s.Damaged == 0 && g.S.Fences.Count > 0) A("safe", 0.07f, "The walls hold.");
            int died3 = fallen.Count(f => g.S.T - (f.T > 0 ? f.T : (f.Day - 1) * 24.0 + 12.0) < 72.0);
            if (died3 > 0) A("deaths", -Math.Min(0.24f, 0.06f * died3), died3 == 1 ? "We lost someone." : "We keep losing people.");
            if (m.Grief != "") A("friend_died", -0.08f, "I lost " + m.Grief + ".");
            var rec = Recent(g, 72);
            int risky = rec.Count(e => (e.Kind == "ambush" || e.Kind == "robbed") && FirstOf(e.Who) == p.First);
            int lostg = rec.Count(e => e.Kind == "lostgroup" || e.Kind == "ambush" || e.Kind == "robbed");
            if (risky > 0) A("risky", -0.08f, "You sent me out and it went bad.");
            else if (lostg > 0) A("risky", -0.03f * Math.Min(3, lostg), "Runs are going wrong out there.");
            if (mem.P.TryGetValue(p.Id, out var tp) && Math.Abs(tp.Trust) > 0.001) A("talk", (float)tp.Trust, tp.Trust > 0 ? "You listen when I talk." : "You brushed me off.");
            double seen = mem.Seen.TryGetValue(p.Id, out var t0) ? t0 : g.C.StartT;
            A("time", Mathf.Min(0.08f, (float)((g.S.T - seen) / 24.0 * 0.01)), "You've kept me alive this long.");
            A("morale", (float)((g.C.Morale - 60.0) / 300.0), g.C.Morale >= 60 ? "People seem okay here." : "Everyone's on edge.");
            A("outlook", m.Hope0 * 0.05f, m.Hope0 >= 0 ? "I want to believe in this place." : "I don't trust easy.");
            float t = 0.5f; foreach (var x in R) t += x.Delta;
            m.Trust = Mathf.Clamp01(t);
        }

        public static float TrustInLeader(Game g, int survivorId)
        {
            var p = g?.C?.People.FirstOrDefault(q => q.Id == survivorId && q.St != "dead");
            var m = p != null ? For(g, p) : null;
            return m?.Trust ?? 0.5f;
        }

        public static List<TrustReason> TrustReasons(Game g, int survivorId)
        {
            var p = g?.C?.People.FirstOrDefault(q => q.Id == survivorId && q.St != "dead");
            var m = p != null ? For(g, p) : null;
            return m != null ? new List<TrustReason>(m.Reasons) : new List<TrustReason>();
        }

        // ------------------------------------------------------------------ conversation
        public static readonly string[][] TOPICS =
        {
            new[] { "how", "How are you doing?" }, new[] { "need", "What do you need?" }, new[] { "lately", "What happened lately?" },
            new[] { "camp", "How's the camp?" }, new[] { "about", "Tell me about yourself" }, new[] { "advice", "Any advice?" },
            new[] { "trust", "Who do you trust here?" }, new[] { "miss", "What do you miss?" }, new[] { "makeit", "Will we make it?" },
            new[] { "leader", "How am I doing?" },
        };
        public static string TopicLabel(string id) { foreach (var t in TOPICS) if (t[0] == id) return t[1]; return id; }

        public static bool CanTalk(Game g, Person p)
        {
            if (g?.S == null || g.C == null || p == null || p.St == "dead" || !g.C.People.Contains(p)) return false;
            try { return g.AgeStage(p) != "baby"; } catch { return true; }
        }

        sealed class Voice
        {
            public Game G; public Person P; public Mind M; public Situation S; public uint Seed; int _n; public bool Food, Hobby;
            public string Pk(params string[] a) => Pick(a, Fnv(Seed + ":" + (_n++)));
            public bool Coin(float pr = 0.5f) => (Fnv(Seed + "#" + (_n++)) % 1000u) / 1000f < pr;
            public string Open()
            {
                var m = M;
                if (m.Kid) return Pk("Um.", "", "Well...", "");
                if (m.Brave < -0.3f) return Pk("Uh... ", "I mean... ", "Honestly? ", "Okay, um. ", "God. ");
                if (m.Chatty < -0.4f) return Pk("", "Mm. ", "", "Hm. ");
                if (m.Brave > 0.4f) return Pk("Look. ", "Straight up? ", "", "Alright. ");
                if (m.Chatty > 0.4f) return Pk("Oh man. ", "So. ", "Okay, okay. ", "Ha. Okay. ");
                return Pk("", "Well... ", "Honestly? ", "");
            }
            public string Joke()
            {
                if (!M.Dark || M.Kid) return "";
                return " " + Pk("At least the commute's short now.", "Hey, nobody asks me to fill out timesheets anymore.", "Silver lining: no more taxes.",
                    "I've lost weight though. Apocalypse diet. Zero stars.", "My therapist would have a field day. If she wasn't, you know. One of them.",
                    "Could be worse. Could be one of those guys outside, all teeth and no plans.");
            }
        }

        static string Ago(double hrs, int hour)
        {
            if (hrs < 2.5) return "a little while ago";
            if (hrs < 12 && hour >= 12) return "this morning";
            if (hrs < 20) return "last night";
            if (hrs < 40) return "yesterday";
            if (hrs < 60) return "the night before last";
            return "a few days back";
        }

        static string AskedBefore(Game g, TalkPerson tp, string topic, Voice v)
        {
            if (tp == null || !tp.Asked.TryGetValue(topic, out var t)) return "";
            double hrs = g.S.T - t;
            int n = tp.AskedN.TryGetValue(topic, out var nn) ? nn : 1;
            if (hrs < 1.0) return v.Pk("You just asked me that. ", "Again? Okay. ", "Same answer as a minute ago, mostly. ");
            if (hrs < 12) return v.Pk("You asked me that " + Ago(hrs, g.Hour()) + ". ", "Still the same as " + Ago(hrs, g.Hour()) + ", more or less. ");
            if (hrs < 40) return v.Pk("You asked me that yesterday. ", "Funny, you asked me the same thing yesterday. ") + (n > 3 ? "You keep checking on me. I notice. " : "");
            return n > 2 ? v.Pk("You always ask me that. It's kind of nice, actually. ", "You never stop asking that one. ") : "";
        }

        /// <summary>Ask a topic. Records it in the talk memory (UI side) and returns the answer. Never touches the sim.</summary>
        public static TalkAnswer Ask(Game g, Person p, string topic)
        {
            var ans = new TalkAnswer { Topic = topic, Question = TopicLabel(topic) };
            if (!CanTalk(g, p)) { ans.Text = "..."; return ans; }
            var mem = Memory(g.S); Observe(g);
            var tp = mem.Of(p.Id);
            var m = For(g, p); var s = SituationOf(g, p);
            int asked = tp.AskedN.TryGetValue(topic, out var an) ? an : 0;
            var v = new Voice { G = g, P = p, M = m, S = s, Seed = H(g, p.Id, topic + "/" + g.Day() + "/" + s.Period + "/" + asked) };
            string pre = AskedBefore(g, tp, topic, v);
            if (pre == "" && tp.LastTopic != "" && tp.LastTopic != topic && tp.LastT >= 0 && g.S.T - tp.LastT > 6 && g.S.T - tp.LastT < 60 && v.Coin(0.35f))
                pre = v.Pk("Last time you wanted to know " + Past(tp.LastTopic) + ". ", "Didn't we talk " + Ago(g.S.T - tp.LastT, g.Hour()) + "? ") + "Anyway. ";
            if (tp.N == 0 && !m.Kid) pre = v.Pk("Huh. Nobody's asked me anything in a while. ", "", "Oh. Hey. ", "") + pre;
            string radio = s.Away ? v.Pk("(over the radio) ", "(radio crackles) ") : "";
            string body;
            switch (topic)
            {
                case "how": body = How(v, ans); break;
                case "need": body = Need(v); break;
                case "lately": body = Lately(v); break;
                case "camp": body = Camp(v); break;
                case "about": body = About(v); break;
                case "advice": body = Advice(v); break;
                case "trust": body = TrustTalk(v); break;
                case "miss": body = MissTalk(v, ans); break;
                case "makeit": body = MakeIt(v, ans); break;
                case "leader": body = Leader(v, ans); break;
                default: body = "Hm?"; break;
            }
            ans.Text = Clean(radio + pre + body);
            tp.N++; tp.Asked[topic] = g.S.T; tp.AskedN[topic] = asked + 1; tp.LastTopic = topic; tp.LastT = g.S.T;
            if (tp.FirstT < 0) tp.FirstT = g.S.T;
            _cacheMind = null;
            return ans;
        }

        static string Past(string t)
        {
            switch (t)
            {
                case "how": return "how I was holding up"; case "need": return "what I needed"; case "lately": return "what's been going on";
                case "camp": return "what I think of this place"; case "about": return "about my life before"; case "advice": return "what I'd do in your shoes";
                case "trust": return "who I trust"; case "miss": return "what I miss"; case "makeit": return "if we'd make it"; case "leader": return "how you're doing";
                default: return "something";
            }
        }

        /// <summary>Your answer to a question the survivor asked back. Shifts their trust in you (talk memory only).</summary>
        public static string Reply(Game g, Person p, string topic, string replyId)
        {
            if (!CanTalk(g, p)) return "...";
            var mem = Memory(g.S); var tp = mem.Of(p.Id);
            var m = For(g, p); var s = SituationOf(g, p);
            var v = new Voice { G = g, P = p, M = m, S = s, Seed = H(g, p.Id, "reply/" + topic + "/" + replyId + "/" + tp.N) };
            bool bad = s.Starving || s.Breaches > 0 || m.Grief != "" || s.FoodDays < 1.5;
            float d; string t;
            switch (replyId)
            {
                case "hope":
                    if (m.Hope0 < -0.3f && bad) { d = -0.01f; t = v.Pk("Yeah. Sure. You don't have to sell it to me.", "Don't do that. Don't promise. People who promise end up lying.", "...Okay. If you say so."); }
                    else { d = m.Brave < 0 ? 0.05f : 0.03f; t = v.Pk("Okay. Okay. I needed to hear that.", "Thanks. Really. I'm gonna hold you to it.", "Yeah. Yeah, we will. Thanks, boss.", "...Thank you. That helps more than you'd think."); }
                    break;
                case "honest":
                    d = m.Hope0 < 0 ? 0.05f : 0.025f;
                    t = v.Pk("At least you're straight with me. I'll take that over a pep talk.", "Yeah. Nobody knows. Thanks for not pretending.", "Honest. I respect that. Keep doing that.", "Okay. We keep going, then. One day at a time.");
                    break;
                default:
                    d = m.Brave > 0.4f ? -0.015f : -0.05f;
                    t = m.Brave > 0.4f ? v.Pk("Fair. There's work to do.", "Right. Back to it.") : v.Pk("...Right. Sorry I bothered you.", "Got it. Won't happen again.", "Wow. Okay.", "Sure. Forget I said anything.");
                    break;
            }
            tp.Trust = Math.Max(-0.2, Math.Min(0.2, tp.Trust + d));
            tp.Replies.Add(((int)g.S.T) + ":" + replyId);
            while (tp.Replies.Count > 12) tp.Replies.RemoveAt(0);
            _cacheMind = null;
            return Clean(t);
        }

        static void Offer(TalkAnswer a, string hope, string honest, string cold)
        {
            a.Replies.Add(new TalkReply("hope", hope)); a.Replies.Add(new TalkReply("honest", honest)); a.Replies.Add(new TalkReply("cold", cold));
        }

        // ---------------- topics
        static string How(Voice v, TalkAnswer a)
        {
            var m = v.M; var s = v.S; var p = v.P;
            var sb = new StringBuilder(v.Open());
            if (s.BittenKnown)
                return sb.Append(v.Pk("I'm bit. You know that. I keep looking at it like it might change its mind. If it comes to it... make it quick, okay? Don't let me walk around like that.",
                    "How do you think? Every time my head hurts I wonder if this is it. Just... stay with me a bit.")).ToString();
            if (s.HiddenBite)
                return sb.Append(v.Pk("Fine. I'm fine. Just cold. Don't look at me like that.", "I'm good. Little fever, probably something I ate. It's nothing. Leave it.", "Why does everyone keep asking? I'm fine.")).ToString();
            if (m.Grief != "")
            {
                sb.Append(v.Pk("I keep turning around to say something to " + m.Grief + ". Then I remember.", "Not great. " + m.Grief + "'s gone and everyone's just... eating dinner. Like normal.",
                    "I dug the hole for " + m.Grief + " myself. My hands still hurt. Good, I guess."));
                if (v.Coin(0.5f)) { sb.Append(" Do you think it gets easier?"); Offer(a, "It does. Slowly.", "I don't know. I miss people too.", "We can't stop now."); }
                return sb.ToString();
            }
            if (m.Broke) return sb.Append(v.Pk("I can't. I just can't today. Everything's too loud.", "I'm not... I'm not okay. I don't want to go out there. Please don't make me go out there.")).ToString();
            if (s.Starving) sb.Append(v.Pk("Stomach's been growling since yesterday. I'm chewing on a belt like it's jerky.", "Hungry. Everyone's hungry. People are starting to snap at each other over crumbs.", "I'm so hungry I'm dreaming about cafeteria food. Cafeteria food."));
            else if (p.Hp < 35) sb.Append(v.Pk("I've been better. Everything hurts and the bandage needs changing.", "Lost a lot of blood. Don't tell the others it's this bad.", "If I lie still, it's fine. Mostly."));
            else if (p.Hp < 65) sb.Append(v.Pk("Banged up. Nothing that won't heal. I think.", "Sore. Took a knock out there. I'll live.", "Limping a little. Don't make a thing of it."));
            else if (Game.ExpansionRules && p.Fatigue > 70) sb.Append(v.Pk("Tired. Bone tired. I could sleep standing up.", "Haven't slept right in days. My eyes feel like sandpaper."));
            else if (m.Mood > 0.4f) sb.Append(v.Pk("Good, actually. Weird to say that out loud.", "Not bad! Ate today, nobody died. That's a great day now.", "Pretty good. " + (m.Partner != "" ? m.Partner + " makes it easier." : "Sun's out, walls are up."), "I'm alright. Better than alright."));
            else if (m.Mood > 0f) sb.Append(v.Pk("Hanging in there.", "Okay. Day by day.", "Can't complain. Well. I could. But I won't.", "Surviving. That's the job now, right?"));
            else sb.Append(v.Pk("Not great, if I'm honest.", "Some days are harder. This is one.", "Tired of being scared all the time.", "I'm here. That's about all I've got."));
            if (s.Away && (p.Goal == "scav" || p.Goal == "tf" || p.Goal == "rescue" || p.Goal == "hunt" || p.Goal == "sq" || p.Goal == "sqs" || p.Goal == "sqa") && m.Brave < 0.3f)
                sb.Append(" " + v.Pk("I'm going, I'm going. I just don't love this one.", "Out here because you said so. I'll get it done. Doesn't mean I like it.", "Every corner out here could be the last one. But okay. I'm on it."));
            if (s.RaidNow) sb.Append(" " + (m.Brave > 0.3f ? v.Pk("Now's not the time though. They're at the wall.", "Talk later. They're hitting the wall.") : v.Pk("Can you hear them? They're right there. Right there.", "They're at the wall. I can hear the scratching.")));
            else if (s.RaidComing) sb.Append(" " + (m.Brave > 0.3f ? "There's a big one coming tonight. I'm ready." : v.Pk("And they're coming tonight. I can feel it.", "Everyone says they're coming. I can't stop shaking.")));
            else if (s.Night && m.Brave < -0.2f) sb.Append(" " + v.Pk("Nights are the worst. Every noise is one of them.", "I don't like it when it's this dark."));
            else if (s.Breaches > 0 || s.Damaged > 2) sb.Append(" " + WallWorry(v));
            if (m.Hardened && v.Coin(0.4f)) sb.Append(" " + v.Pk("Funny. When I got here I'd jump at my own shadow. Now I just... handle it.", "I used to shake every time they got close. Not anymore. Not sure that's a good thing."));
            if (m.Mood > 0 && v.Coin(0.3f)) sb.Append(v.Joke());
            if (m.Mood < -0.15f && a.Replies.Count == 0 && v.Coin(0.6f)) { sb.Append(" Is it going to get better?"); Offer(a, "It will. I promise.", "I don't know. But I'm trying.", "Not now. Keep it together."); }
            return sb.ToString();
        }

        static string WallWorry(Voice v)
        {
            var s = v.S; string side = s.WeakSide != "" ? "the " + s.WeakSide + " side" : "the wall";
            if (s.Breaches > 0) return v.Pk("And there's a hole in " + side + ". A hole. In the wall.", "They broke through on " + side + ". Nobody's sleeping till it's fixed.");
            return v.Pk("And those walls on " + side + "... they won't hold another night.", "Did you see " + side + "? It's held together with hope and old nails.");
        }

        static string Need(Voice v)
        {
            var m = v.M; var s = v.S; var p = v.P;
            var sb = new StringBuilder(v.Open());
            if (s.BittenKnown) return sb.Append("Time. Or a cure. Neither of which you've got. Just... don't leave me alone at the end.").ToString();
            if (p.Hp < 50 && s.Meds < 3) return sb.Append(v.Pk("Medicine. Real stuff. Somebody's got to go find bandages, antibiotics, anything.", "Something for the pain. We're out of everything.")).ToString();
            if (p.Hp < 50 && s.Medics == 0) return sb.Append("Somebody who knows how to stitch a wound. We don't have a single medic.").ToString();
            if (p.Hp < 50) return sb.Append(v.Pk("Rest. And for the medic to stop poking at me.", "A few days off my feet. Doc says I'm lucky.")).ToString();
            if (s.Starving) return sb.Append(v.Pk("Food. Anything. I'd eat the dog food at this point.", "Food. Water. Before people start doing things they can't take back.")).ToString();
            if (s.FoodDays < 2) sb.Append(v.Pk("Honestly, a fuller pantry. We're scraping the bottom.", "More food coming in. The shelves look thin."));
            else if (s.WaterDays < 2) sb.Append(v.Pk("Water. We're rationing sips.", "Clean water. Mouth's like sandpaper."));
            else if (s.Breaches > 0 || s.Damaged > 2) sb.Append(v.Pk("A stronger wall. I can't sleep with it like this.", "Planks, nails, whatever. Get that wall fixed."));
            else if (s.BedsShort > 0) sb.Append(v.Pk("A bed. I've been sleeping on a pile of coats.", "Somewhere to lie down that isn't the floor."));
            else if (m.Brave < -0.3f && s.Guards < 2) sb.Append("More people on watch at night. Please. I don't care who.");
            else if (s.Ammo < 10 && (p.Job == "Soldier" || p.Job == "Cop")) sb.Append(v.Pk("Bullets. I'm counting every round.", "Ammo. I'm down to throwing harsh language."));
            else if (string.IsNullOrEmpty(p.Wpn) && !m.Kid && v.Coin(0.5f)) sb.Append(v.Pk("Something better than my bare hands, if you've got it.", "A weapon. Anything with a handle."));
            else if (m.Work < -0.4f) sb.Append(v.Pk("A day off. A real one.", "A nap. Maybe two. Don't look at me like that."));
            else if (m.Partner == "" && m.Crush != "" && v.Coin(0.4f)) sb.Append("Honestly? Five minutes alone with " + m.Crush + " without someone yelling about chores. Don't tell anyone I said that.");
            else sb.Append(v.Pk("I'm alright for now. Save it for someone who needs it.", "Nothing. Seriously. Give it to the kids.", "Socks. Dry socks. That's the dream.", "Something to read. I've read the same magazine forty times.", "Coffee. But I know."));
            if (m.Chatty > 0.3f && v.Coin(0.4f)) sb.Append(" " + v.Pk("And maybe a hug. Kidding. Mostly.", "Oh, and " + m.Miss.TrimEnd('.') + ". A guy can dream."));
            return sb.ToString();
        }

        static string WhoNice(Voice v, string who) { string f = FirstOf(who); return f == v.P.First ? "I" : f; }

        static string Lately(Voice v)
        {
            var m = v.M; var g = v.G; var p = v.P;
            var ev = Recent(g, 72);
            var lines = new List<string>();
            int h = g.Hour(), loot = 0;
            var seenEv = new HashSet<string>();
            foreach (var e in ev)
            {
                string who = WhoNice(v, e.Who), ago = Ago(g.S.T - e.T, h);
                string l = null;
                switch (e.Kind)
                {
                    case "died": l = who == m.Grief ? "We lost " + who + " " + ago + ". I don't want to talk about it." : v.Pk("We lost " + who + " " + ago + ".", who + " died " + ago + ". Just like that."); break;
                    case "horde": l = v.Pk("That horde " + ago + ". I've never seen so many.", "The horde that came " + ago + ". I still hear it when I close my eyes."); break;
                    case "breach": l = v.Pk("They broke through on " + Street(e.Where) + " " + ago + ".", "The wall on " + Street(e.Where) + " gave out " + ago + "."); break;
                    case "loot": l = who == "I" ? "I hauled back a good load " + ago + ". Felt like Christmas." : v.Pk(who + " came back loaded " + ago + ".", who + " brought back " + Goods(e.What) + " " + ago + ". Good run."); if (loot++ > 0) l = null; break;
                    case "part": l = who + " found a " + e.What + " out at " + Words(e.Where) + " " + ago + "."; if (loot++ > 0) l = null; break;
                    case "bitten": l = who == "I" ? "I got bit. You know that." : v.Pk(who + " got bit " + ago + ".", "Did you hear about " + who + "? Bitten."); break;
                    case "joined": l = v.Pk("A new face, " + FirstOf(e.Who) + ", showed up " + ago + ".", FirstOf(e.Who) + " walked up to the gate " + ago + ". Seems alright."); break;
                    case "ambush": l = who == "I" ? "Got jumped by " + e.What + " people " + ago + ". Still sore." : who + " got hurt in an ambush " + ago + "."; break;
                    case "robbed": l = (who == "I" ? "Those " + e.What + " people took everything I had" : e.What + " robbed " + who) + " " + ago + "."; break;
                    case "cured": l = who + " pulled through the fever " + ago + ". Some good news for once."; break;
                    case "rebuilt": l = who == "I" ? "I patched the wall " + ago + ". My thumbs are purple." : who + " patched the wall on " + Street(e.Where) + " " + ago + "."; break;
                    case "lostgroup": l = "We lost a group out in " + e.Where + " " + ago + ". Heard them on the radio and then nothing."; break;
                    case "rescued": l = "We brought in the " + Words(e.What) + " " + ago + ". More mouths, more hands."; break;
                    case "hungry": l = "People went hungry " + ago + "."; break;
                    case "drop": l = "A supply drop came down " + ago + ". Somebody's still out there flying planes."; break;
                    case "raided": l = e.Who + " raided us " + ago + ". Bastards."; break;
                    case "held": l = g.S.T - e.T < 20 ? "The line held last night. Barely." : "The line held the other night. Barely."; break;
                }
                if (l != null && (e.Kind == "rescued" || e.Kind == "joined" || e.Kind == "hungry" || e.Kind == "held") && !seenEv.Add(e.Kind + "|" + e.What + "|" + e.Who)) l = null;
                if (l != null && !lines.Contains(l)) lines.Add(l);
                if (lines.Count >= 3) break;
            }
            var sb = new StringBuilder(v.Open());
            if (lines.Count == 0)
                return sb.Append(v.Pk("Quiet, honestly. Quiet makes me nervous.", "Not much. Same walls, same faces, same canned beans.", "Nothing worth telling. Which is the best kind of news now.",
                    "Slow days. I'll take them.")).ToString();
            int keep = m.Chatty > 0.3f ? 3 : m.Chatty < -0.3f ? 1 : 2;
            sb.Append(string.Join(" ", lines.Take(keep)));
            var died = ev.FirstOrDefault(e => e.Kind == "died");
            if (died != null && FirstOf(died.Who) != m.Grief && v.Coin(0.5f)) sb.Append(" " + v.Pk("Didn't know them that well. Still.", "Feels like we should've done more.", "Every time it's someone, the camp gets a little quieter."));
            else if (ev.Any(e => e.Kind == "loot" || e.Kind == "drop") && v.Coin(0.4f)) sb.Append(" " + v.Pk("So, not all bad.", "Small wins. They count."));
            return sb.ToString();
        }

        static string Camp(Voice v)
        {
            var m = v.M; var s = v.S; var g = v.G;
            var sb = new StringBuilder(v.Open());
            var bits = new List<string>();
            if (s.Starving) bits.Add(v.Pk("Everyone's starving. You can feel it in the air, people watching each other's plates.", "There's no food. That's the camp right now."));
            else if (s.FoodDays < 2) bits.Add(v.Pk("Food's getting thin. Two days, maybe less, the way we eat.", "The pantry looks bad."));
            else if (s.FoodDays > 6) bits.Add(v.Pk("We're eating. That counts for a lot.", "Pantry's in decent shape for once."));
            if (s.Breaches > 0 || s.Damaged > 2) bits.Add(WallWorry(v));
            else if (g.S.Fences.Count > 0) bits.Add(v.Pk("The walls feel solid.", "Wall's holding. I check it every night anyway."));
            else bits.Add(v.Pk("We don't have a real wall. That keeps me up.", "No wall. Just us and a lot of hoping."));
            if (s.BedsShort > 0) bits.Add("We're packed in like sardines. People sleeping in the hallway.");
            if (s.Injured > 1) bits.Add("A lot of us are hurt" + (s.HurtName != "" ? ", " + s.HurtName + " worst of all." : "."));
            if (g.C.Morale < 35) bits.Add(v.Pk("People are on edge. Somebody's going to snap.", "Nobody's laughing anymore."));
            else if (g.C.Morale > 75) bits.Add(v.Pk("People are laughing again at dinner. Haven't heard that in a while.", "Good vibe lately, honestly."));
            if (m.Rival != "" && v.Coin(0.35f)) bits.Add(v.Pk("And " + m.Rival + " keeps leaving their stuff everywhere. Small thing. Drives me up the wall.", m.Rival + " and I don't really get along. Whatever."));
            int keep = m.Chatty > 0.3f ? 3 : m.Chatty < -0.3f ? 1 : 2;
            sb.Append(string.Join(" ", bits.Take(keep)));
            if (m.Hope > 0.3f && v.Coin(0.4f)) sb.Append(" " + v.Pk("It's starting to feel like a home.", "I like it here. Don't tell anyone, I have a reputation."));
            else if (m.Hope < -0.3f && v.Coin(0.4f)) sb.Append(" " + v.Pk("It's a box. A box with walls. We're waiting in it.", "It's fine until it isn't."));
            return sb.ToString();
        }

        static string About(Voice v)
        {
            var m = v.M; var p = v.P;
            var sb = new StringBuilder(v.Open());
            if (m.BornHere)
                return sb.Append(v.Pk("I was born here. Mom says before, there were lights everywhere at night and nobody had to be quiet. I don't really believe her.",
                    "I don't remember before. There's always been the dead. Is it true people used to have dogs just for fun?")).ToString();
            if (m.Kid)
                return sb.Append("I was at school when it happened. They made us hide in the gym. " + v.Pk("I don't know where " + m.LostWho + " is. Maybe they're looking for me. Maybe they can't find the camp.",
                    Cap(m.LostWho) + " came to get me. Then they went back for the car and didn't come back.") + " " + v.Pk("I want to " + m.Dream, "When I'm big I'm going to " + m.Dream)).ToString();
            int part = (int)(Fnv(v.Seed + "about") % 5u);
            string from = char.IsUpper(m.Home[0]) ? v.Pk("I'm from " + m.Home + ". ", "Grew up in " + m.Home + ". ") : "I'm from " + m.Home + ". ";
            switch (part)
            {
                case 0:
                    sb.Append(from + "I was " + m.OldJob + ". " + v.Pk("Feels like a different person's life.", "Seems so stupid now, the stuff I used to stress about.", "Used to hate Mondays. Ha."));
                    sb.Append(" When it started I was " + m.DayOne + ".");
                    break;
                case 1:
                    sb.Append("I lost " + m.LostWho + ". " + m.LostName + ". " + m.LostHow);
                    if (m.Chatty > 0.2f) sb.Append(" " + v.Pk("Sorry. You didn't ask for all that.", "Anyway."));
                    break;
                case 2:
                    sb.Append("When this is over? I want to " + m.Dream + " " + v.Pk("Don't laugh.", "Got it all planned out.", "Somebody's gotta dream."));
                    sb.Append(" " + v.Pk("What scares me is " + m.Fear, "Biggest fear? " + Cap(m.Fear)));
                    break;
                case 3:
                    sb.Append(v.Pk("Here's a thing nobody knows. ", "Can I tell you something? ") + m.Regret + " " + v.Pk("I think about it every day.", "Don't tell the others.", "There. Now you know."));
                    break;
                default:
                    sb.Append(from + m.Quirk);
                    if (m.Partner != "") sb.Append(" " + v.Pk(m.Partner + " thinks it's cute. I think.", m.Partner + " pretends to be annoyed by it."));
                    break;
            }
            if (m.Kids.Count > 0 && v.Coin(0.4f)) sb.Append(" And I've got " + string.Join(" and ", m.Kids) + " to look out for now. Everything's for them.");
            if (m.Hardened && v.Coin(0.4f)) sb.Append(" I was a mess when I got here. You remember. Not anymore.");
            return sb.ToString();
        }

        static readonly string[] ORD = { "zero", "First", "Second", "Third", "Fourth", "Fifth", "Sixth", "Seventh", "Eighth", "Ninth", "Tenth", "Eleventh", "Twelfth" };
        static readonly string[] NUM = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve" };
        /// <summary>"Oak St & 4 Ave" -> "Oak Street by Fourth Avenue" (people don't talk in grid numbers).</summary>
        static string Street(string w)
        {
            if (string.IsNullOrEmpty(w)) return "the wall";
            var m = Regex.Match(w, @"^(.+?) St & (\d+) Ave$");
            if (!m.Success) return Words(w);
            int n = int.Parse(m.Groups[2].Value);
            return m.Groups[1].Value + " Street by " + (n < ORD.Length ? ORD[n] : "the far") + " Avenue";
        }
        /// <summary>Spell out small numbers, drop the rest.</summary>
        static string Words(string w) => Regex.Replace(w ?? "", @"\d+", mm => { int n; return int.TryParse(mm.Value, out n) && n < NUM.Length ? NUM[n] : "a lot of"; });
        /// <summary>"5 food, 3 water, 1 ammo" -> "food and water".</summary>
        static string Goods(string w)
        {
            var items = Regex.Replace(w ?? "", @"\d+\s*", "").Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).Take(2).ToList();
            return items.Count == 0 ? "supplies" : string.Join(" and ", items);
        }

        static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        static string Advice(Voice v)
        {
            var m = v.M; var s = v.S; var g = v.G;
            var tips = new List<string>();
            if (s.Starving || s.FoodDays < 1.5)
                tips.Add(s.Garden ? v.Pk("Put more people on the gardens. And send the scavengers out for food, today, not tomorrow.", "Food first. Everything else waits. More farmers, more runs.")
                                  : v.Pk("We need a garden. Seeds, dirt, anything. And somebody out scavenging food now.", "Get food coming in. A garden, scavenging runs, trade, whatever works."));
            if (s.WaterDays < 1.5) tips.Add("Water's the one that kills you fastest. Find more or build a way to catch it.");
            if (s.Breaches > 0) tips.Add("Fix that hole in the wall" + (s.WeakStreet != "" ? " over on " + Street(s.WeakStreet) : "") + " before dark. Nothing else matters if they walk right in.");
            else if (s.Damaged > 2) tips.Add("Patch the wall on the " + (s.WeakSide != "" ? s.WeakSide + " side" : "weak side") + ". It's barely standing.");
            if (s.RaidComing && !s.RaidNow) tips.Add(s.Guards < 2 ? "Put more people on the wall tonight. We're thin." : "They're coming tonight. Make sure the guards are fed and awake.");
            if (s.BittenName != "") tips.Add("Keep an eye on " + s.BittenName + ". I'm sorry, but somebody has to say it.");
            if (s.Injured > 0 && s.Medics == 0) tips.Add("We need a medic. People are getting hurt and nobody knows what they're doing" + (s.HurtName != "" ? ". " + s.HurtName + " especially." : "."));
            else if (s.Injured > 0 && s.Meds < 3) tips.Add("We're out of meds. A hospital or a pharmacy run would save lives.");
            if (s.IdleScav > 0 && !s.Night && !s.Starving) tips.Add("Some of the scavengers are just sitting around camp. Send them out while it's light.");
            if (s.BedsShort > 0) tips.Add("We need more beds. People sleeping on the floor get sick and cranky.");
            if (s.Ammo < 10 && s.Guards > 0) tips.Add("Ammo's low. Guards can't hold a wall with harsh words.");
            if (g.C.Morale < 35) tips.Add("Give people something good. A meal, a day off, anything. Morale's in the dirt.");
            if (g.S.Fences.Count == 0) tips.Add("Build a wall. Even a crappy one. It's the difference between a camp and a buffet.");
            var sb = new StringBuilder(v.Open());
            if (tips.Count == 0)
                return sb.Append(v.Pk("Honestly? Keep doing what you're doing. It's working.", "Stock up before winter. That's all I've got. Things are good right now.",
                    "Don't get comfortable. That's when it goes wrong.", "Keep the walls up and the pantry full. Simple as that.")).ToString();
            // the most urgent thing always comes first; a chatty one adds one more (rotating through the rest)
            sb.Append(tips[0]);
            if (m.Chatty > 0.3f && tips.Count > 1) sb.Append(" Also: " + tips[1 + (int)(v.Seed % (uint)(tips.Count - 1))]);
            if (m.Brave < -0.3f && v.Coin(0.4f)) sb.Append(" Sorry. I know it's not my call.");
            return sb.ToString();
        }

        static string TrustTalk(Voice v)
        {
            var m = v.M; var s = v.S;
            var sb = new StringBuilder(v.Open());
            if (s.Alive <= 1) return sb.Append("It's just me here. And you. So... you, I guess.").ToString();
            var bits = new List<string>();
            if (m.Partner != "") bits.Add(v.Pk(m.Partner + ". Obviously. I'd trust " + m.Partner + " with my life. I do, every night.", m.Partner + ", first. Always."));
            if (m.BestFriend != "") bits.Add(v.Pk(m.BestFriend + " has my back. We've been through some stuff.", m.BestFriend + "'s good people. Real good."));
            if (m.WatchTrust != "" && m.WatchTrust != m.Partner) bits.Add(v.Pk("On watch? " + m.WatchTrust + ". Doesn't miss.", "If I'm on the wall, I want " + m.WatchTrust + " next to me."));
            if (m.Rival != "") bits.Add(v.Pk("Not " + m.Rival + ". Can't say why. Just a feeling.", m.Rival + "? Ehh. Let's just say I count my rations after they walk by."));
            if (m.Crush != "" && v.Coin(0.6f)) bits.Add(v.Pk("And " + m.Crush + "... is nice. That's all. Stop smiling.", "...Okay, " + m.Crush + " is kind of great. Don't you dare say anything."));
            if (m.Grief != "") bits.Add("I trusted " + m.Grief + ". Look where that got us.");
            if (bits.Count == 0) bits.Add(m.Brave < -0.2f ? "Nobody, yet. I'm working on it." : "Everybody, a little. Nobody, all the way. That's how you live now.");
            sb.Append(string.Join(" ", bits.Take(m.Chatty > 0.3f ? 3 : 2)));
            if (v.Coin(0.5f)) sb.Append(" " + (m.Trust > 0.65f ? v.Pk("And you. You've kept us breathing.", "You too, for what it's worth.") : m.Trust < 0.35f ? v.Pk("You? Jury's out.", "You, I'm still deciding.") : v.Pk("You're alright.", "")));
            return sb.ToString();
        }

        static string MissTalk(Voice v, TalkAnswer a)
        {
            var m = v.M; var s = v.S;
            var sb = new StringBuilder(v.Open());
            if (m.BornHere) return sb.Append("Miss? I don't know. The other kids talk about ice cream. I'd like to try that.").ToString();
            if (m.Grief != "") return sb.Append(m.Grief + ". I miss " + m.Grief + ". That's the whole list right now.").ToString();
            int k = (int)(v.Seed % 3u);
            if (k == 0) sb.Append(Cap(m.Miss) + " " + v.Pk("Stupid, right?", "Funny what sticks with you.", "I think about it more than I should."));
            else if (k == 1) sb.Append(m.LostName + ". " + Cap(m.LostWho) + ". " + v.Pk("Every single day.", "I still talk to them sometimes, in my head.", "Some nights I can't remember their voice. That's the worst part."));
            else sb.Append(v.Pk("Not being afraid. Just walking somewhere without checking every corner.", "Strangers. Saying hi to people you don't know and not wondering if they'll kill you.", "Silence that's just silence."));
            if (s.Period == "night" && v.Coin(0.5f)) sb.Append(" " + v.Pk("Nights like this make it worse.", "Look at those stars though. Never saw them like this before. No city lights."));
            if (m.Mood < 0 && v.Coin(0.4f)) { sb.Append(" You ever miss it? Before?"); Offer(a, "Every day. But we'll build something.", "Yeah. I miss a lot of people.", "No point looking back."); }
            return sb.ToString();
        }

        static string MakeIt(Voice v, TalkAnswer a)
        {
            var m = v.M; var s = v.S;
            var sb = new StringBuilder(v.Open());
            bool bad = s.Starving || s.Breaches > 0 || s.FoodDays < 1.5 || v.G.C.Morale < 30;
            if (m.Hope > 0.3f && !bad) sb.Append(v.Pk("Yeah. I really think we will. Look at this place.", "We will. We're too stubborn to die.", "I think so. I have to think so."));
            else if (m.Hope > 0f) sb.Append(bad ? v.Pk("If we fix what's broken, maybe. Big if.", "Some of us. Not sure all of us.") : v.Pk("Maybe. Better odds than most, I bet.", "I hope so. That's the most honest I can be."));
            else sb.Append(bad ? v.Pk("Look around. Not like this.", "Honestly? Not like this. Something's gotta change.", "No. Not unless something changes. Soon.") : v.Pk("Nobody makes it. Not really. We just last a little longer.", "Make it to what? There's no finish line."));
            if (m.Dark && v.Coin(0.3f)) sb.Append(" " + v.Pk("Worst case, I get to be the scariest one out there. Tall, handsome, hungry.", "If not, I'm haunting " + (m.Rival != "" ? m.Rival : "somebody") + "."));
            sb.Append(" " + v.Pk("Do you? Think we'll make it?", "What do you think? Honestly.", "Tell me the truth. Will we?"));
            Offer(a, "We'll make it. I promise.", "I don't know. But we keep going.", "Stop asking. Get back to work.");
            return sb.ToString();
        }

        static string Leader(Voice v, TalkAnswer a)
        {
            var m = v.M; var s = v.S;
            var sb = new StringBuilder(v.Open());
            var top = m.Reasons.OrderByDescending(r => Math.Abs(r.Delta)).Take(2).ToList();
            if (m.Trust > 0.7f) sb.Append(v.Pk("You're doing good. Better than I would. People listen to you.", "Honestly? I'd follow you anywhere. Maybe not anywhere. Most places.", "You're keeping us alive. Don't let it go to your head."));
            else if (m.Trust > 0.5f) sb.Append(v.Pk("Okay. Not perfect. Nobody could be.", "Fair, mostly. Some calls I wouldn't have made.", "You're doing alright. Could be worse. I've seen worse."));
            else if (m.Trust > 0.3f) sb.Append(v.Pk("I'm... not sure, honestly. Some days I wonder.", "People are talking. Not all of it's good.", "You want the truth? Some of us are worried."));
            else sb.Append(v.Pk("You really want to know? Not good. Not good at all.", "People are scared, and some of them are scared of your choices.", "I've stopped expecting things to get better."));
            foreach (var r in top) if (!string.IsNullOrEmpty(r.Text) && v.Coin(0.75f)) sb.Append(" " + r.Text);
            if (m.Trust < 0.45f || v.Coin(0.4f)) { sb.Append(" " + v.Pk("Are you even listening to us?", "Do you have a plan? A real one?", "Can you fix this?")); Offer(a, "I'll fix it. You have my word.", "I'm doing my best. Tell me what you see.", "I make the calls here."); }
            return sb.ToString();
        }

        // ================================================================== v0.19.2 typed talk (Austin: "I want to be able
        // to actually type to talk to the AI"). Fully offline: no server, no API, no network. The typed line is normalized
        // (lowercase, punctuation out, chat shorthand expanded, light stemming, one/two-letter typo tolerance, synonym
        // classes), scored against hand-written intent phrases, and answered from the same mind, past, mood, memory and
        // camp state as the TALK buttons. Still read-only w.r.t. the sim: hash-based variety (no RNG draws), the only write
        // is the TalkLog (trust nudges like Reply) plus a small transient context per survivor (last lines, who "her" is).
        public const int SAY_MAX = 120;

        /// <summary>Plain ASCII, one line, at most SAY_MAX chars (phone keyboards send emoji and smart quotes).</summary>
        const string ACCENT = "\u00e0\u00e1\u00e2\u00e3\u00e4\u00e5\u00e8\u00e9\u00ea\u00eb\u00ec\u00ed\u00ee\u00ef\u00f2\u00f3\u00f4\u00f5\u00f6\u00f9\u00fa\u00fb\u00fc\u00f1\u00e7\u00c0\u00c1\u00c9\u00c8\u00cd\u00d3\u00da\u00d1\u00c7";
        const string PLAIN = "aaaaaaeeeeiiiiooooouuuuncAAEEIOUNC";
        public static string SanitizeTyped(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s)
            {
                if (ch == '\u2018' || ch == '\u2019' || ch == '\u02bc') sb.Append('\'');
                else if (ch == '\u201c' || ch == '\u201d') sb.Append('"');
                else if (ch == '\u2026') sb.Append("...");
                else if (ch == '\u2013' || ch == '\u2014') sb.Append('-');
                else if (ch == '\n' || ch == '\r' || ch == '\t') sb.Append(' ');
                else if (ch > 127 && ACCENT.IndexOf(ch) >= 0) sb.Append(PLAIN[ACCENT.IndexOf(ch)]);
                else if (ch >= 32 && ch <= 126) sb.Append(ch);
            }
            string t = Regex.Replace(sb.ToString(), "  +", " ").Trim();
            if (t.Length > SAY_MAX) t = t.Substring(0, SAY_MAX).TrimEnd();
            return t;
        }

        // ---------------- transient conversation context (not saved; a fresh load starts a fresh chat)
        sealed class ChatCtx
        {
            public readonly List<string> Lines = new List<string>();     // their recent answers (no repeats back-to-back)
            public string LastIntent = "", LastWhy = "", AskedBack = "";  // AskedBack: the question they asked you, if any
            public int SubjectId = -1;                                   // who "her" / "him" / "them" points at
            public List<int> Clarify; public string ClarifyIntent = "";
            public int Rude; public double RudeT = -1, GreetT = -1; public int Gib, Unk; public string PlayerName = "";
        }
        static GameState _ctxS;
        static readonly Dictionary<int, ChatCtx> _ctx = new Dictionary<int, ChatCtx>();
        static ChatCtx Ctx(Game g, int id)
        {
            if (!ReferenceEquals(_ctxS, g.S)) { _ctx.Clear(); _ctxS = g.S; }
            if (!_ctx.TryGetValue(id, out var c)) { c = new ChatCtx(); _ctx[id] = c; }
            return c;
        }

        // ---------------- normalization
        static readonly Dictionary<string, string> EXPAND = new Dictionary<string, string>
        {
            ["im"] = "i am", ["ive"] = "i have", ["ill"] = "i will", ["id"] = "i would", ["dont"] = "do not", ["doesnt"] = "does not", ["didnt"] = "did not",
            ["cant"] = "can not", ["cannot"] = "can not", ["wont"] = "will not", ["isnt"] = "is not", ["arent"] = "are not", ["wasnt"] = "was not",
            ["youre"] = "you are", ["yore"] = "you are", ["ur"] = "your", ["u"] = "you", ["r"] = "are", ["ya"] = "you", ["yu"] = "you", ["yall"] = "you all", ["youll"] = "you will",
            ["whats"] = "what is", ["wat"] = "what", ["wut"] = "what", ["wht"] = "what", ["hows"] = "how is", ["whos"] = "who is", ["wheres"] = "where is",
            ["thats"] = "that is", ["theres"] = "there is", ["lets"] = "let us", ["gonna"] = "going to", ["wanna"] = "want to", ["gotta"] = "got to",
            ["pls"] = "please", ["plz"] = "please", ["thx"] = "thanks", ["thanx"] = "thanks", ["tnx"] = "thanks", ["ty"] = "thanks", ["tysm"] = "thanks", ["thank"] = "thanks",
            ["k"] = "ok", ["kk"] = "ok", ["okay"] = "ok", ["okey"] = "ok", ["yea"] = "yes", ["yeah"] = "yes", ["yep"] = "yes", ["yup"] = "yes", ["ye"] = "yes", ["yah"] = "yes",
            ["nah"] = "no", ["nope"] = "no", ["naw"] = "no", ["rn"] = "right now", ["idk"] = "i do not know", ["dunno"] = "do not know", ["tbh"] = "", ["ngl"] = "", ["pls"] = "please",
            ["lol"] = "haha", ["lmao"] = "haha", ["lmfao"] = "haha", ["rofl"] = "haha", ["hahaha"] = "haha", ["hah"] = "haha", ["hehe"] = "haha", ["ha"] = "haha",
            ["bc"] = "because", ["cuz"] = "because", ["coz"] = "because", ["cause"] = "because", ["hru"] = "how are you", ["wyd"] = "what are you doing",
            ["wassup"] = "sup", ["whatsup"] = "sup", ["wazzup"] = "sup", ["hiya"] = "hi", ["heya"] = "hey", ["hii"] = "hi", ["hiii"] = "hi", ["heyy"] = "hey", ["y"] = "why",
            ["luv"] = "love", ["ppl"] = "people", ["bf"] = "boyfriend", ["gf"] = "girlfriend", ["zeds"] = "zombies", ["zs"] = "zombies", ["ok"] = "ok", ["fav"] = "favorite", ["fave"] = "favorite",
            ["shes"] = "she is", ["hes"] = "he is", ["theyre"] = "they are", ["were"] = "were", ["whered"] = "where did", ["howd"] = "how did", ["couldnt"] = "could not", ["shouldnt"] = "should not",
            ["wouldnt"] = "would not", ["havent"] = "have not", ["hasnt"] = "has not", ["aint"] = "is not", ["gimme"] = "give me", ["kinda"] = "kind of", ["sorta"] = "sort of",
            ["tho"] = "though", ["thru"] = "through", ["sry"] = "sorry", ["soz"] = "sorry", ["srry"] = "sorry", ["omg"] = "", ["wtf"] = "what", ["stfu"] = "shut up", ["gtfo"] = "get out",
        };

        static string Stem(string w)
        {
            if (w == "doing") return "do"; if (w == "going") return "go"; if (w == "being") return "be"; if (w == "nothing" || w == "something" || w == "anything" || w == "everything") return w;
            if (w.Length > 5 && w.EndsWith("ing")) return w.Substring(0, w.Length - 3);
            if (w.Length > 4 && w.EndsWith("ed") && !w.EndsWith("eed")) return w.Substring(0, w.Length - 2);
            if (w.Length > 3 && w.EndsWith("s") && !w.EndsWith("ss") && !w.EndsWith("us") && !w.EndsWith("is")) return w.Substring(0, w.Length - 1);
            return w;
        }

        // synonym classes: any word in a class also matches "@class" in the intent phrases
        static readonly string[][] CLASSES =
        {
            new[] { "@food", "food", "eat", "eating", "ate", "meal", "meals", "hungry", "hunger", "starving", "starve", "ration", "rations", "pantry", "dinner", "lunch", "breakfast", "supper", "canned", "beans", "grub", "snack", "cook", "cooking" },
            new[] { "@water", "water", "thirsty", "thirst", "drink", "drinking", "dehydrated" },
            new[] { "@meds", "meds", "medicine", "medicines", "medical", "bandage", "bandages", "antibiotics", "pills", "doctor", "medkit", "firstaid", "infirmary", "clinic" },
            new[] { "@hurt", "hurt", "injured", "wounded", "wound", "pain", "bleeding", "sick", "ill", "fever" },
            new[] { "@ammo", "ammo", "bullets", "bullet", "rounds", "ammunition", "guns", "gun", "weapons", "weapon", "rifle", "shells", "shotgun", "pistol", "armory" },
            new[] { "@wall", "wall", "walls", "fence", "fences", "gate", "gates", "barricade", "barricades", "defense", "defenses", "defences", "perimeter" },
            new[] { "@zed", "zombie", "zombies", "zed", "undead", "walkers", "walker", "biters", "infected", "horde", "hordes", "monsters", "creatures", "rotters" },
            new[] { "@weather", "weather", "rain", "raining", "rainy", "storm", "stormy", "snow", "snowing", "fog", "foggy", "cold", "freezing", "sunny", "windy", "winter", "summer", "season", "temperature" },
            new[] { "@greet", "hi", "hello", "hey", "yo", "sup", "howdy", "greetings", "hola", "heyo", "helo", "hallo" },
            new[] { "@thanks", "thanks", "appreciate", "grateful", "cheers" },
            new[] { "@bye", "bye", "goodbye", "goodnight", "cya", "farewell", "later", "night" },
            new[] { "@yes", "yes", "sure", "ok", "definitely", "absolutely", "correct", "agreed", "course", "affirmative", "totally", "yess", "yessir", "aye" },
            new[] { "@no", "no", "never", "negative", "nop" },
            new[] { "@insult", "stupid", "idiot", "dumb", "moron", "useless", "pathetic", "worthless", "loser", "coward", "ugly", "trash", "garbage", "lazy", "annoying",
                "jerk", "fool", "suck", "sucks", "bitch", "bastard", "asshole", "dick", "freak", "fuck", "fucking", "shit", "crap", "dumbass", "idiots", "retard", "weakling", "clown", "creep" },
            new[] { "@kind", "proud", "great", "awesome", "amazing", "brave", "strong", "smart", "nice", "cool", "hero", "best", "incredible", "wonderful", "kind", "tough", "badass", "legend", "helpful" },
            new[] { "@flirt", "cute", "hot", "pretty", "beautiful", "handsome", "sexy", "gorgeous", "date", "kiss", "marry", "crush", "attractive", "babe", "darling", "sweetheart", "hottie", "cutie" },
            new[] { "@sad", "sad", "tired", "scared", "afraid", "lonely", "depressed", "exhausted", "worried", "stressed", "angry", "upset", "down", "overwhelmed", "terrified", "miserable", "hopeless", "lost" },
            new[] { "@happy", "happy", "good", "fine", "glad", "alright", "excited", "better", "great" },
            new[] { "@fam", "family", "families", "parents", "mom", "mother", "dad", "father", "brother", "sister", "siblings", "kids", "children", "son", "daughter", "wife", "husband", "married", "girlfriend", "boyfriend", "partner" },
        };

        static readonly string[] STOP = { "the", "a", "an", "and", "or", "but", "to", "of", "in", "on", "at", "for", "with", "is", "are", "was", "were", "be", "been", "am", "i",
            "you", "he", "she", "it", "we", "they", "me", "my", "your", "our", "their", "his", "her", "them", "us", "him", "this", "that", "what", "who", "where", "when", "why",
            "how", "do", "does", "did", "can", "could", "would", "should", "will", "have", "has", "had", "not", "so", "just", "really", "very", "about", "from", "up", "down",
            "out", "over", "again", "there", "here", "then", "than", "too", "also", "any", "some", "all", "every", "much", "many", "more", "most", "get", "got", "like", "know",
            "think", "want", "need", "go", "going", "come", "see", "say", "tell", "let", "make", "take", "give", "feel", "look", "well", "oh", "um", "uh", "hmm", "please",
            "because", "if", "now", "today", "tonight", "tomorrow", "yesterday", "anything", "something", "nothing", "everything", "anyone", "someone", "everyone", "people",
            "thing", "things", "one", "way", "time", "day", "night", "still", "ever", "never", "always", "maybe", "kind", "sort", "lot", "little", "bit", "man", "dude", "bro",
            "boss", "sir", "mate", "buddy", "friend", "guy", "guys", "okay", "haha", "huh", "yo", "hey", "right", "doing", "happened", "happen", "think", "thought", "said",
            "these", "those", "which", "while", "into", "off", "back", "being", "my", "mine", "yours", "yourself", "myself", "own", "same", "other", "another", "doing", "life",
            "old", "new", "good", "bad", "real", "fake", "game", "play", "here", "there", "camp", "base", "said", "talk", "talking", "ask", "asking", "question", "answer" };

        // plain words people type that no intent needs: known, so the typo fixer leaves them alone
        static readonly string[] COMMON = { "situation", "status", "pizza", "pineapple", "music", "movie", "movies", "book", "books", "dog", "dogs", "cat", "cats",
            "car", "cars", "house", "money", "school", "work", "sleep", "slept", "night", "morning", "today", "tomorrow", "week", "month", "year", "years", "world", "city",
            "outside", "inside", "place", "everybody", "somebody", "nobody", "anybody", "someone", "really", "actually", "probably", "though", "through", "around", "something",
            "stuff", "pretty", "kinda", "maybe", "should", "would", "could", "might", "must", "still", "already", "enough", "much", "many", "else", "anyway", "about", "after",
            "before", "again", "never", "always", "sometimes", "often", "think", "believe", "remember", "forget", "understand", "listen", "watch", "wait", "stay", "leave",
            "keep", "help", "start", "stop", "play", "playing", "talk", "speak", "said", "says", "tell", "told", "story", "true", "false", "right", "wrong", "sure", "fine",
            "thing", "things", "people", "person", "everyone", "favorite", "color", "colour", "song", "songs", "sport", "sports", "football", "basketball", "baseball", "soccer",
            "beer", "coffee", "tea", "chocolate", "cake", "candy", "sugar", "phone", "internet", "computer", "television", "shower", "bath", "clothes", "shoes", "hair",
            "kill", "killed", "dead", "die", "died", "alive", "living", "live", "lives", "survivor", "survivors", "group", "team", "together", "alone", "lonely", "help",
            "please", "ever", "seen", "sound", "sounds", "heard", "hear", "smell", "taste", "touch", "feel", "felt", "look", "looks", "looking", "seem", "seems" };
        // never fuzzy-match a typo onto these (a near miss of "situation" is not "simulation")
        static readonly HashSet<string> NO_FUZZ = new HashSet<string> { "simulation", "programm", "artificial", "developer", "algorithm", "matrix", "chatgpt", "robot", "game" };

        // intent phrases: "intent|weight|phrase;phrase;..." (words are stemmed at load; "@x" = a synonym class).
        // A phrase matches when its words appear in order, at most two other words between them. Ties go to the earlier row.
        static readonly string[] RULES =
        {
            "meta|5|are you real;are you an ai;are you a robot;are you a bot;is this a game;this is a game;video game;npc;chatgpt;simulation;are you human;artificial intelligence;programmed;computer program;language model;you are not real;you are fake;you are a bot;you are an ai;you are a robot;in a game;matrix;developer;dev",
            "meta|3|ai;robot;bot;cpu;algorithm",
            "threat|4|kill you;shoot you;kick you out;throw you out;feed you to;leave you out;exile you;beat you;get rid of you;you are next;leave you behind;you will die;lock you out;stab you;hurt you;starve you;banish you;punish you",
            "myname|5|what is my name;who am i;do you know my name;remember my name;know who i am",
            "insult|3|you @insult;you are @insult;shut up;fuck you;screw you;go to hell;hate you;nobody like you;you suck;fuck off;piss off;get out;you are a @insult;you are so @insult;waste of space;waste of food",
            "playername|5|my name is;call me;i am called",
            "promise|4|i promise;keep you safe;protect you;got your back;have your back;i will get us;i will fix;i will find;we will be ok;we will be fine;we will make it;i will not let;you will be safe;i swear;you have my word;i will keep;i will take care",
            "leader|4|how am i do;am i a good;think of me;think about me;do you like me;do you trust me;my leadership;the leader;what do people think;what do they think of me;am i do;good leader;bad leader;rate me;opinion of me;feel about me;my job as;my choices;my decisions",
            "apology|4|sorry;apologize;apology;my bad;forgive me;i was wrong;did not mean;i apologise",
            "thanks|4|@thanks;thank you",
            "compliment|3|good job;well done;great job;nice work;proud of you;you are @kind;you are do great;you are do good;you did good;you did great;i like you;you rock;you are the best;good work;you are a hero;glad you are here;you are so @kind;you are really @kind;nice job;you are doing well",
            "flirt|4|go out with me;marry me;love you;kiss me;you are cute;you are hot;date me;be my;are you single;you are @flirt;you look @flirt;want to date;on a date;your number;sleep with me;hook up;wanna kiss",
            "flirt|2|@flirt",
            "insult|1.6|@insult",
            "joke|4|tell me a joke;joke;make me laugh;something funny;say something funny;cheer me up;know any joke",
            "how|3|how are you;how you do;you ok;you alright;you good;are you ok;how do you feel;how you feel;how is it go;you hold up;how have you been;are you @hurt;how is your day;how you hold;how you been;you feel ok;sup;what is up",
            "need|3|what do you need;need anything;you need;get you anything;can i get you;what can i do;want anything;anything i can do;how can i help;need help;can i help",
            "lately|3|what happen;any new;what is new;what is go on;what did i miss;anything happen;lately;recent;what is the news;catch me up",
            "camp|3|the camp;this camp;our camp;the base;this place;the colony;how is everyone;how is every;morale;how many of us;our home;the settlement;our people;how is it here",
            "camp|1.5|camp;base;colony;settlement;everyone",
            "about|3|about yourself;tell me about you;your story;your past;before all this;before this;before the outbreak;who were you;your life;yourself;who are you really;tell me something",
            "advice|3|advice;what should i do;what should we do;what would you do;suggest;any idea;tip;what do we need;priority;what next;what now;what do we do;your opinion;should i",
            "trust|3|who do you trust;trust anyone;who are your friend;your friend;best friend;get along;who do you like;anyone you like;trust anybody",
            "trust|1.5|trust;friend",
            "miss|3|what do you miss;do you miss;you miss;miss most",
            "makeit|3|make it;will we survive;we survive;are we go to die;we go to die;go to be ok;go to be alright;we go to make;any chance;will we live;we go to be fine;will it get better;get better;is there hope;survive this",
            "name|4|your name;who are you;what do i call you;what should i call you;call you;name again",
            "age|4|how old;your age;what age;birthday;born when",
            "job|4|your job;what did you do;for a live;do for work;used to do;what was your job;career;profession;your work before;what do you do;work before;old job",
            "hometown|4|where are you from;where you from;hometown;where did you grow;grow up;where were you born;where did you live;from originally;come from;where is home",
            "family|4|your family;any family;have kid;have family;married;your parent;your mom;your dad;sibling;your brother;your sister;your wife;your husband;girlfriend;boyfriend;your kid;children;have a partner",
            "lost|4|who did you lose;lose anyone;lost anyone;lost someone;who did you lost;anyone die;who die;lost anybody;anyone you lost;lose somebody",
            "fear|4|what scare you;are you scared;are you afraid;afraid of;scared of;your fear;fear;worst nightmare;what are you afraid;frighten;terrify you;worst fear",
            "dream|4|your dream;what do you want;after all this;after this;when this is over;what do you hope;your hope;future;if you could;wish;when it is over;one day",
            "regret|4|regret;secret;ashamed;guilty;confess;worst thing you",
            "favorite|3|favorite;favourite;what do you like;do you like to;your hobby;for fun;hobby",
            "doing|4|what are you do;what you up to;where are you;what you do right now;what are you up to;are you busy;what is your task;whatcha do",
            "faith|3|god;pray;church;heaven;religion;believe in;faith;afterlife;bible",
            "food|3|@food", "water|3|@water", "meds|3|@meds", "ammo|3|@ammo", "wall|3|@wall", "zed|3|@zed", "weather|3|@weather",
            "youfeel|3.5|i am @food;i am @hurt;i feel @hurt;i am so @food;i am @water",
            "youfeel|3|i am @sad;i feel @sad;i am so @sad;i am really @sad;i am @happy;i feel @happy;i am do @happy;i am do ok",
            "bye|4|@bye;see you;talk later;good night;got to go;i have to go;take care;talk to you later;catch you later",
            "greet|2|@greet;good morning;good evening;good afternoon",
            "dismiss|3|not now;get back to work;stop ask;enough;whatever;who care;do not care;stop talk;quiet;be quiet;not interest",
            "why|3|why;how come;what make you say;what do you mean;reason;explain;how so",
            "idk|3|i do not know;not sure;do not know;maybe;no idea;who know;hard to say",
            "laugh|3|haha;funny;hilarious",
            "yes|2|@yes",
            "no|2|@no",
        };

        sealed class Rule { public string Intent; public float W; public string[][] Phrases; }
        static List<Rule> _rules; static HashSet<string> _vocab; static Dictionary<string, string> _cls; static List<string> _vocabList;

        static void InitNlu()
        {
            if (_rules != null) return;
            _cls = new Dictionary<string, string>();
            _vocab = new HashSet<string>();
            foreach (var c in CLASSES) for (int i = 1; i < c.Length; i++)
                {
                    string s = Stem(c[i]); if (!_cls.ContainsKey(s)) _cls[s] = c[0]; if (!_cls.ContainsKey(c[i])) _cls[c[i]] = c[0];
                    _vocab.Add(s); _vocab.Add(c[i]);
                }
            foreach (var w in STOP) _vocab.Add(Stem(w));
            foreach (var w in COMMON) _vocab.Add(Stem(w));
            var rules = new List<Rule>();
            foreach (var row in RULES)
            {
                var parts = row.Split('|');
                var r = new Rule { Intent = parts[0], W = float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) };
                r.Phrases = parts[2].Split(';').Select(ph => ph.Split(' ').Where(x => x.Length > 0).Select(x => x[0] == '@' ? x : Stem(x)).ToArray()).ToArray();
                foreach (var ph in r.Phrases) foreach (var x in ph) if (x[0] != '@') _vocab.Add(x);
                rules.Add(r);
            }
            foreach (var w in new[] { "medic", "soldier", "cop", "engineer", "scientist", "farmer", "scavenger", "civilian", "her", "him", "she", "he", "they", "them", "first",
                "second", "other", "older", "younger", "kid", "boy", "girl", "baby", "one", "guy", "lady", "woman" }) _vocab.Add(Stem(w));
            _vocabList = _vocab.Where(x => x.Length >= 4 && !NO_FUZZ.Contains(x)).OrderBy(x => x, StringComparer.Ordinal).ToList();
            _rules = rules;
        }

        /// <summary>Optimal string alignment distance (adjacent swaps count as one), early out above max.</summary>
        static int Dist(string a, string b, int max)
        {
            if (Math.Abs(a.Length - b.Length) > max) return max + 1;
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                int rowMin = int.MaxValue;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    int v = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                    if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) v = Math.Min(v, d[i - 2, j - 2] + 1);
                    d[i, j] = v; if (v < rowMin) rowMin = v;
                }
                if (rowMin > max) return max + 1;
            }
            return d[a.Length, b.Length];
        }

        sealed class Tok { public string Raw = "", S = "", C; public bool Known; }

        static string Squash(string w)
        {
            // "heyyyy" -> "heyy", "soooo" -> "soo": more than two of a letter in a row is just emphasis
            var sb = new StringBuilder();
            for (int i = 0; i < w.Length; i++) if (i < 2 || w[i] != w[i - 1] || w[i] != w[i - 2]) sb.Append(w[i]);
            return sb.ToString();
        }

        static List<Tok> Tokens(string text, ICollection<string> names)
        {
            InitNlu();
            string low = text.ToLowerInvariant().Replace("'", "");
            low = Regex.Replace(low, @"[^a-z0-9 ]", " ");
            var words = new List<string>();
            foreach (var w0 in low.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string w = Squash(w0);
                if (EXPAND.TryGetValue(w, out var ex)) { foreach (var x in ex.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) words.Add(x); }
                else if (w.Length > 2 && EXPAND.TryGetValue(w.Substring(0, w.Length - 1), out ex) && (w.EndsWith("y") || w.EndsWith("o"))) { foreach (var x in ex.Split(' ')) if (x.Length > 0) words.Add(x); }
                else words.Add(w);
            }
            var toks = new List<Tok>();
            foreach (var w in words)
            {
                var t = new Tok { Raw = w, S = Stem(w) };
                if (_vocab.Contains(t.S) || _vocab.Contains(w) || names.Contains(w)) t.Known = true;
                else if (w.Length >= 4 && !Regex.IsMatch(w, @"\d"))
                {
                    int max = w.Length >= 8 ? 2 : 1; string best = null; int bd = max + 1; bool tie = false;
                    foreach (var v in _vocabList)
                    {
                        if (max == 2 && v[0] != t.S[0]) continue;   // two-letter fixes keep the first letter
                        int dd = Dist(t.S, v, max);
                        if (dd < bd) { bd = dd; best = v; tie = false; }
                        else if (dd == bd && dd <= max && best != null && !(_cls.TryGetValue(v, out var c1) && _cls.TryGetValue(best, out var c2) && c1 == c2)) tie = true;   // same class = same meaning
                    }
                    if (best != null && bd <= max && !tie) { t.S = Stem(best); t.Raw = best; t.Known = true; }
                }
                if (_cls.TryGetValue(t.S, out var c)) t.C = c; else if (_cls.TryGetValue(w, out c)) t.C = c;
                toks.Add(t);
            }
            return toks;
        }

        static bool Match(List<Tok> toks, string[] ph)
        {
            int ti = 0;
            for (int k = 0; k < ph.Length; k++)
            {
                int found = -1, lim = k == 0 ? toks.Count : Math.Min(toks.Count, ti + 3);
                for (int j = ti; j < lim; j++) { var t = toks[j]; if (t.S == ph[k] || t.C == ph[k] || (t.Raw == ph[k])) { found = j; break; } }
                if (found < 0) return false;
                ti = found + 1;
            }
            return true;
        }

        sealed class Parse
        {
            public string Text = "", Norm = "";
            public List<Tok> Toks; public Dictionary<string, float> Score = new Dictionary<string, float>();
            public string Top = "", Second = ""; public float TopS, SecondS;
            public List<Person> Named = new List<Person>(); public List<string> DeadNamed = new List<string>(); public bool Pronoun, SelfNamed;
            public float Known; public bool Question, Empty;
        }

        static readonly string[] PRONOUN = { "her", "him", "she", "he", "them", "they", "hers", "his" };
        static readonly string[] JOBW = { "medic", "soldier", "cop", "engineer", "scientist", "farmer", "scavenger", "civilian" };

        static Parse Understand(Game g, Person me, string text, TalkLog mem)
        {
            var P = new Parse { Text = text };
            var alive = g.C.People.Where(q => q.St != "dead").ToList();
            var names = new HashSet<string>();
            foreach (var q in alive) foreach (var part in (q.Name ?? "").ToLowerInvariant().Split(' ')) if (part.Length > 1) names.Add(part);
            foreach (var kv in mem.Names) foreach (var part in (kv.Value ?? "").ToLowerInvariant().Split(' ')) if (part.Length > 1) names.Add(part);
            P.Toks = Tokens(text, names);
            P.Norm = string.Join(" ", P.Toks.Select(t => t.Raw));
            P.Question = text.TrimEnd().EndsWith("?") || (P.Toks.Count > 0 && new[] { "what", "who", "where", "when", "why", "how", "do", "does", "did", "are", "is", "can", "will", "would", "should", "could", "have" }.Contains(P.Toks[0].Raw));
            if (P.Toks.Count == 0) { P.Empty = true; return P; }
            P.Known = P.Toks.Count(t => t.Known) / (float)P.Toks.Count;
            foreach (var r in _rules)
                foreach (var ph in r.Phrases)
                    if (Match(P.Toks, ph))
                    {
                        float add = r.W + 0.15f * (ph.Length - 1);
                        P.Score[r.Intent] = Math.Max(P.Score.TryGetValue(r.Intent, out var o) ? o : 0f, add);
                    }
            // names of people in camp (first or last, one typo allowed on longer names), and the dead remembered by name
            var raws = P.Toks.Select(t => t.Raw).ToList();
            // names that are also everyday words (Will, Mark, Hope, June...) only count when written with a capital
            // letter somewhere other than the start of the line, or when nothing else in the line means anything
            var capWords = new HashSet<string>(Regex.Matches(text, @"(?<=\S\s+)[A-Z][a-z]+").Cast<Match>().Select(mm => mm.Value.ToLowerInvariant()));
            bool weakLine = !P.Score.Values.Any(x => x >= 1.5f);
            bool NameHit(string part)
            {
                part = part.ToLowerInvariant(); if (part.Length < 2) return false;
                if ((_vocab.Contains(part) || _vocab.Contains(Stem(part))) && !capWords.Contains(part) && !weakLine) return false;
                foreach (var t in P.Toks)
                {
                    if (t.Raw == part) return true;
                    if (part.Length >= 5 && t.Raw.Length >= 4 && !_vocab.Contains(t.Raw) && Dist(t.Raw, part, 1) <= 1) return true;
                }
                return false;
            }
            var hits = new List<(Person q, int sc)>();
            foreach (var q in alive)
            {
                var parts = (q.Name ?? "").Split(' ');
                int hs = (parts.Length > 0 && NameHit(parts[0]) ? 1 : 0) + (parts.Length > 1 && NameHit(parts[parts.Length - 1]) ? 2 : 0);
                if (hs == 0) continue;
                if (q.Id == me.Id) { if (hs != 2 || hits.Count == 0) P.SelfNamed = true; }
                else hits.Add((q, hs));
            }
            if (hits.Count > 0) { int top = hits.Max(x => x.sc); P.Named.AddRange(hits.Where(x => x.sc == top).Select(x => x.q)); }
            foreach (var kv in mem.Names)
            {
                if (alive.Any(q => q.Id == kv.Key)) continue;
                var f = FirstOf(kv.Value);
                if (f.Length > 1 && NameHit(f) && !P.Named.Any(q => q.First == f) && !P.DeadNamed.Contains(kv.Value)) P.DeadNamed.Add(kv.Value);
            }
            P.Pronoun = raws.Any(w => PRONOUN.Contains(w)) && !raws.Contains("i");
            if (P.Toks.Count > 4 && !(P.Toks[0].C == "@yes" || P.Toks[0].C == "@no")) { P.Score.Remove("yes"); P.Score.Remove("no"); }
            var ord = P.Score.OrderByDescending(kv => kv.Value).ThenBy(kv => _rules.FindIndex(r => r.Intent == kv.Key)).ToList();
            if (ord.Count > 0) { P.Top = ord[0].Key; P.TopS = ord[0].Value; }
            if (ord.Count > 1) { P.Second = ord[1].Key; P.SecondS = ord[1].Value; }
            return P;
        }

        static bool Gibberish(Parse P)
        {
            if (P.Empty) return true;
            if (P.TopS >= 1.5f || P.Named.Count > 0 || P.SelfNamed) return false;
            int letters = P.Text.Count(char.IsLetter);
            if (letters == 0) return true;
            int vowels = P.Text.ToLowerInvariant().Count(ch => "aeiouy".IndexOf(ch) >= 0);
            if (letters >= 4 && vowels * 6 < letters) return true;
            return P.Known < 0.34f;
        }

        // ---------------- numbers in words (people don't talk in digits)
        static readonly string[] ONES = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen" };
        static readonly string[] TENS = { "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety" };
        static string NumW(int n) => n < 20 ? ONES[Math.Max(0, n)] : n < 100 ? TENS[n / 10] + (n % 10 > 0 ? "-" + ONES[n % 10] : "") : "a lot";

        static int AgeOf(Game g, Person p, Mind m)
        {
            if (m.Stage == "child") return 6 + (int)(H(g, p.Id, "age") % 6u);
            if (m.Stage == "teen") return 13 + (int)(H(g, p.Id, "age") % 5u);
            int a = 19 + (int)(H(g, p.Id, "age") % 44u);
            if (p.Job == "Soldier" && a < 22) a += 4;
            return a;
        }

        static readonly string[] FAV_FOOD = { "my grandma's enchiladas", "gas station nachos. Don't judge me", "pho. The big bowl", "a cheeseburger, medium, extra pickles", "peach cobbler",
            "fried chicken from this one place by the highway", "pancakes. Any time of day", "my dad's chili", "sushi from the grocery store, which tells you how fancy I am", "mac and cheese from the blue box" };
        static readonly string[] FAV_THING = { "old country songs, the sad ones", "fishing at dawn", "crossword puzzles, in pen", "loud punk records", "card games. I will take your rations at poker",
            "bad horror movies", "fixing up old bikes", "drawing. Mostly birds", "running. Before running meant something else", "the radio. Late night call-in shows" };

        // ---------------- the entry point
        /// <summary>A line you typed. Understands it offline and answers in character. pending: the question they asked you
        /// back (TalkAnswer with Replies) if any; a yes / no / kind / rude line answers it like the reply buttons.
        /// Records the talk in the TalkLog (UI side). Never touches the sim.</summary>
        public static TalkAnswer Say(Game g, Person p, string typed, TalkAnswer pending = null)
        {
            string text = SanitizeTyped(typed);
            var ans = new TalkAnswer { Topic = "say", Question = text };
            if (!CanTalk(g, p)) { ans.Text = "..."; return ans; }
            if (text.Length == 0) { ans.Text = ""; return ans; }
            var mem = Memory(g.S); Observe(g);
            var tp = mem.Of(p.Id);
            var cx = Ctx(g, p.Id);
            var m = For(g, p); var s = SituationOf(g, p);
            var P = Understand(g, p, text, mem);
            string intent = P.Top; float sc = P.TopS;
            if (sc < 1.5f) intent = "";

            // 1) they asked you something back: yes / no / kind / rude answers it (same effect as the reply buttons)
            if (pending != null && pending.Replies.Count > 0)
            {
                string rid = null;
                switch (intent)
                {
                    case "yes": case "promise": case "compliment": case "thanks": rid = "hope"; break;
                    case "no": rid = pending.Topic == "leader" ? "cold" : "honest"; break;
                    case "idk": case "apology": case "youfeel": rid = "honest"; break;
                    case "insult": case "threat": case "dismiss": rid = "cold"; break;
                }
                if (rid != null)
                {
                    ans.Topic = "reply";
                    ans.Text = Reply(g, p, pending.Topic, rid);
                    if (intent == "insult" || intent == "threat") { cx.Rude++; cx.RudeT = g.S.T; }
                    Remember(cx, ans.Text, "reply", WhyReply(m, s, rid)); cx.AskedBack = "";
                    return ans;
                }
            }

            // 2) "which Maria?" pending: the next line picks one by surname or job
            if (cx.Clarify != null && cx.Clarify.Count > 1)
            {
                var low = " " + P.Norm + " ";
                var pick = cx.Clarify.Select(id => g.C.People.FirstOrDefault(q => q.Id == id && q.St != "dead")).Where(q => q != null)
                    .FirstOrDefault(q => { var nm = (q.Name ?? "").ToLowerInvariant().Split(' '); return (nm.Length > 1 && low.Contains(" " + nm[nm.Length - 1] + " ")) || low.Contains(" " + (q.Job ?? "").ToLowerInvariant() + " "); });
                if (pick == null && (low.Contains(" first ") || low.Contains(" one ")) ) pick = g.C.People.FirstOrDefault(q => q.Id == cx.Clarify[0]);
                if (pick == null && (low.Contains(" second ") || low.Contains(" other "))) pick = g.C.People.FirstOrDefault(q => q.Id == cx.Clarify[1]);
                cx.Clarify = null;
                if (pick != null) { P.Named.Clear(); P.Named.Add(pick); if (intent == "" || intent == "yes" || intent == "no") intent = "person"; }
            }

            uint seed = H(g, p.Id, "say/" + P.Norm + "/" + tp.N + "/" + g.Day());
            var v = new Voice { G = g, P = p, M = m, S = s, Seed = seed };

            // named person / pronoun: talk about them unless the line is clearly about something else
            string norm0 = " " + P.Norm + " ";
            Person subj = null; string deadSubj = null;
            if (P.Named.Count > 0) subj = P.Named[0];
            else if (P.DeadNamed.Count > 0) deadSubj = P.DeadNamed[0];
            else if (P.Pronoun && cx.SubjectId >= 0 && !P.Score.ContainsKey("playername")) subj = g.C.People.FirstOrDefault(q => q.Id == cx.SubjectId && q.St != "dead");
            string[] aboutOthers = { "", "trust", "flirt", "how", "why", "yes", "no", "idk", "insult", "compliment", "doing", "about", "fear", "name", "age", "job", "hometown", "family", "favorite", "lost", "dream", "laugh", "threat", "greet", "camp" };
            bool personTalk = (subj != null || deadSubj != null) && (aboutOthers.Contains(intent) || (P.Named.Count > 0 || deadSubj != null) && (sc < 3.5f || intent == "lately" || intent == "miss"));
            // "do you trust me" / "how am I doing": the leader is you, not a named person
            if (intent == "leader" || intent == "playername" || intent == "meta") personTalk = false;
            // insults / threats aimed at someone else ("Maria is useless") are about them; aimed at "you" stay personal
            if (personTalk && (intent == "insult" || intent == "threat" || intent == "compliment") && P.Toks.Any(t => t.Raw == "you")) personTalk = false;
            if (!personTalk && subj == null && deadSubj == null && P.Pronoun && P.Named.Count == 0 && (intent == "" || intent == "how" || intent == "trust" || intent == "why") && P.Toks.Count <= 6
                && (P.Toks.Count <= 2 || norm0.Contains(" what about ") || norm0.Contains(" how is ") || norm0.Contains(" how about ") || norm0.StartsWith(" and ") || norm0.Contains(" do you like ")))
                intent = "who";
            if (P.SelfNamed && !personTalk && (intent == "" || intent == "greet")) intent = intent == "" ? "selfname" : intent;

            // ambiguous first name: ask which one
            if (personTalk && P.Named.Count > 1 && P.Named.Select(q => q.First).Distinct().Count() < P.Named.Count)
            {
                var same = P.Named.Where(q => q.First == P.Named[0].First).ToList();
                if (same.Count > 1)
                {
                    cx.Clarify = same.Select(q => q.Id).ToList();
                    string Desc(Person q) { var nm = (q.Name ?? "").Split(' '); return q.Name + (q.Job != "Civilian" ? ", the " + (q.Job ?? "").ToLowerInvariant() : ""); }
                    ans.Text = Clean(v.Pk("Which " + same[0].First + "? ", "We've got more than one " + same[0].First + ". ") + string.Join(" or ", same.Take(3).Select(Desc)) + "?");
                    Remember(cx, ans.Text, "clarify", ""); cx.AskedBack = "clarify";
                    tp.N++; return ans;
                }
            }

            // "what about you?" / "and you?": turn the last personal question around, else how they're doing
            string norm = " " + P.Norm + " ";
            if (!personTalk && (norm.Contains(" what about you ") || norm.Trim() == "and you" || norm.Trim() == "you" || norm.Contains(" how about you ")))
            {
                string[] personal = { "name", "age", "job", "hometown", "family", "fear", "dream", "favorite", "lost", "regret" };
                intent = personal.Contains(cx.LastIntent) ? cx.LastIntent : "how";
                if (P.Score.ContainsKey("youfeel") && intent == "how") P.Second = "youfeel";
            }

            // imperative orders: they don't happen by talking (orders go through the normal controls and always win)
            if (!personTalk && (intent == "" || intent == "wall" || intent == "food" || intent == "water" || intent == "zed" || intent == "ammo") && !P.Question && P.Toks.Count >= 2 &&
                new[] { "go", "guard", "scavenge", "build", "fix", "get", "bring", "find", "gut", "patrol", "hunt", "defend", "farm", "loot", "repair", "attack", "kill", "come", "follow", "rest", "sleep" }.Contains(P.Toks[0].Raw))
                intent = "order";

            string body; string why = "";
            string radio = s.Away ? v.Pk("(over the radio) ", "(radio crackles) ", "(radio) ") : "";
            float d = 0f;
            bool topicAsk = false;
            string[] TOPIC_IDS = { "how", "need", "lately", "camp", "about", "advice", "trust", "miss", "makeit", "leader" };

            if (personTalk)
            {
                if (subj != null) { body = AboutPerson(v, subj, intent, P, out why); cx.SubjectId = subj.Id; }
                else body = AboutDead(v, deadSubj, out why);
                intent = "person";
            }
            else if (TOPIC_IDS.Contains(intent) && !(intent == "how" && P.Score.ContainsKey("youfeel") && P.Score["youfeel"] > P.Score["how"]))
            {
                // the ten TALK topics: same answers as the buttons (and the same "you asked me that" memory)
                var a = Ask(g, p, intent);
                topicAsk = true;
                ans.Topic = a.Topic; ans.Replies = a.Replies;
                body = a.Text; why = WhyTopic(v, intent);
                radio = "";
            }
            else
            {
                if (intent == "how") intent = "youfeel";
                switch (intent)
                {
                    case "greet": body = Greet(v, cx, out d); break;
                    case "thanks": body = m.Kid ? v.Pk("Okay!", "You're welcome. My mom taught me to say that.", "Sure.") : v.Pk("Anytime.", "Don't mention it.", "Sure thing.", "Yeah, of course.", "Hey. We look out for each other.", "It's nothing. You'd do the same."); d = 0.01f; why = "You said thanks. People don't, much, anymore."; break;
                    case "compliment": body = Compliment(v, out d); why = "Hard to take a compliment when we're all just trying not to die."; break;
                    case "apology": body = Apology(v, cx, out d); why = "Because we've got enough enemies outside the wall."; break;
                    case "promise": body = Promise(v, out d); why = "Because I've heard promises before. Some people kept them."; break;
                    case "insult": body = Insult(v, cx, out d); why = "Because you said it. That's why."; break;
                    case "threat": body = Threat(v, cx, out d); why = "You threatened me. What did you think I'd say?"; break;
                    case "flirt": body = Flirt(v, out why); break;
                    case "joke": body = JokeTalk(v); why = "Because if we stop laughing, we're already dead."; break;
                    case "laugh": body = v.Pk("What's so funny?", "Ha. Glad somebody's laughing.", "Heh.", "You've got a weird sense of humor, you know that?", "Laughing's good. We don't do it enough."); break;
                    case "meta": body = Meta(v, P); why = "Because I'm standing right here. I'm hungry and my feet hurt. That's about as real as it gets."; break;
                    case "playername": body = PlayerName(v, cx, text); break;
                    case "selfname": body = v.Pk("Yeah? That's me.", "Present.", "What? I'm right here.", "That's my name. Don't wear it out."); break;
                    case "name": body = NameTalk(v); why = "My mom picked it. Ask her. Well. You can't."; break;
                    case "age": body = AgeTalk(v); why = "Because years count double now."; break;
                    case "job": body = JobTalk(v); why = "It paid the bills. Then the bills stopped coming."; break;
                    case "hometown": body = HomeTalk(v); why = "Because there's nothing left to go back to. Or I don't want to find out there isn't."; break;
                    case "family": body = FamilyTalk(v); why = "Because they're all I think about, when I let myself."; break;
                    case "lost": body = LostTalk(v); why = "Because I wasn't there. Or I was, and it didn't matter."; break;
                    case "fear": body = FearTalk(v); why = "You don't pick what scares you. It picks you."; break;
                    case "dream": body = DreamTalk(v); why = "Because somebody has to believe there's a later."; break;
                    case "regret": body = RegretTalk(v); why = "Because I can't take it back. That's what makes it a regret."; break;
                    case "favorite": v.Food = P.Toks.Any(t => t.C == "@food" || t.Raw == "food"); v.Hobby = P.Toks.Any(t => t.Raw == "hobby" || t.Raw == "fun" || t.Raw == "music" || t.Raw == "movie"); body = FavTalk(v); why = "No reason. That's what makes it a favorite."; break;
                    case "doing": body = DoingTalk(v); why = "Because somebody has to, and today it's me."; break;
                    case "faith": body = FaithTalk(v); why = "Because I've seen too much to be sure of anything. Either way."; break;
                    case "food": body = SupplyTalk(v, "food", out why); break;
                    case "water": body = SupplyTalk(v, "water", out why); break;
                    case "meds": body = SupplyTalk(v, "meds", out why); break;
                    case "ammo": body = SupplyTalk(v, "ammo", out why); break;
                    case "wall": body = WallTalk(v, out why); break;
                    case "zed": body = ZedTalk(v, out why); break;
                    case "weather": body = WeatherTalk(v, out why); break;
                    case "youfeel": body = YouFeel(v, P); why = "Because you're carrying all of us. Somebody should ask how you are."; break;
                    case "bye": body = Bye(v); break;
                    case "dismiss": body = m.Brave > 0.4f ? v.Pk("Fine.", "Right. Back to it.", "Suit yourself.") : v.Pk("Okay. Sorry.", "...Sure.", "Got it."); d = -0.005f; break;
                    case "order": body = OrderTalk(v, P); why = "Because you're the one who sends people. I just go."; break;
                    case "who": body = v.Pk("Who do you mean?", "Who? You'll have to give me a name.", "Her? Him? Who are we talking about?"); break;
                    case "myname": body = cx.PlayerName != "" ? v.Pk("You're " + cx.PlayerName + ". You told me.", cx.PlayerName + ". I don't forget a name.") : v.Pk("Boss. That's all I've ever called you.", "You never told me. You're the boss, that's what I know.", "Honestly? Everyone just calls you boss."); break;
                    case "why": body = cx.LastWhy != "" ? cx.LastWhy : v.Pk("Why what?", "Why what? You lost me.", "Because. That's the answer to most things now."); break;
                    case "yes": case "no": case "idk": body = YesNo(v, cx, intent); break;
                    default:
                        if (!P.Text.Any(char.IsLetterOrDigit)) body = Silence(v);
                        else body = Gibberish(P) ? Confused(v, cx) : Unknown(v, cx, P);
                        intent = "unknown"; break;
                }
            }

            // a quick hello / thanks / sorry in front of a real question gets a short acknowledgement first
            string ack = "";
            bool repeatPre = body.StartsWith("You just asked") || body.StartsWith("Again?") || body.StartsWith("Same answer") || body.StartsWith("Still the same") || body.StartsWith("You asked");
            if (!repeatPre && intent != "greet" && intent != "thanks" && intent != "apology" && P.SecondS >= 1.5f)
            {
                if (P.Second == "greet" && (cx.GreetT < 0 || g.S.T - cx.GreetT > 6)) { ack = v.Pk("Hey. ", "Hi. ", "Oh, hey. "); cx.GreetT = g.S.T; }
                else if (P.Second == "thanks") { ack = v.Pk("Sure. ", "Anytime. "); d += 0.005f; }
                else if (P.Second == "apology") { ack = "It's fine. "; d += 0.01f; }
                else if (P.Second == "youfeel" && P.Score.ContainsKey("youfeel")) ack = YouFeelAck(v, P);
            }

            // no repeats back-to-back: re-roll the variety seed a few times if this exact line was just said
            string full = Clean(radio + ack + body);
            for (int tries = 1; tries < 6 && Echo(cx, full) && !topicAsk; tries++)
            {
                v = new Voice { G = g, P = p, M = m, S = s, Seed = seed + (uint)(tries * 7919) };
                full = Clean(radio + ack + Regen(v, cx, intent, P, subj, deadSubj, text, out _));
            }
            ans.Text = full;
            if (!topicAsk) { ans.Topic = intent; tp.N++; }
            if (tp.FirstT < 0) tp.FirstT = g.S.T;

            // trust: small nudges for kind / rude / promises, with a daily cap on the good side so it can't be farmed
            if (Math.Abs(d) > 0.0001f)
            {
                int day = g.Day();
                if (tp.SayDay != day) { tp.SayDay = day; tp.SayGain = 0; }
                if (d > 0) { double room = Math.Max(0.0, 0.05 - tp.SayGain); d = (float)Math.Min(d, room); tp.SayGain += d; }
                tp.Trust = Math.Max(-0.2, Math.Min(0.2, tp.Trust + d));
                tp.Replies.Add(((int)g.S.T) + ":say_" + intent);
                while (tp.Replies.Count > 12) tp.Replies.RemoveAt(0);
            }
            Remember(cx, ans.Text, intent, why);
            if (intent != "unknown") cx.Gib = 0;
            cx.AskedBack = ans.Text.EndsWith("?") ? intent : "";
            _cacheMind = null;
            return ans;
        }

        /// <summary>Re-roll for the no-repeat rule (same branch, new seed).</summary>
        static string Regen(Voice v, ChatCtx cx, string intent, Parse P, Person subj, string deadSubj, string text, out string why)
        {
            why = ""; float d;
            switch (intent)
            {
                case "person": return subj != null ? AboutPerson(v, subj, "", P, out why) : AboutDead(v, deadSubj, out why);
                case "greet": return Greet(v, cx, out d);
                case "compliment": return Compliment(v, out d);
                case "insult": return Insult(v, cx, out d);
                case "threat": return Threat(v, cx, out d);
                case "promise": return Promise(v, out d);
                case "flirt": return Flirt(v, out why);
                case "joke": return JokeTalk(v);
                case "meta": return Meta(v, P);
                case "unknown": return Gibberish(P) ? Confused(v, cx) : Unknown(v, cx, P);
                case "youfeel": return YouFeel(v, P);
                case "bye": return Bye(v);
                case "food": case "water": case "meds": case "ammo": return SupplyTalk(v, intent, out why);
                case "wall": return WallTalk(v, out why);
                case "zed": return ZedTalk(v, out why);
                case "weather": return WeatherTalk(v, out why);
                case "fear": return FearTalk(v);
                case "dream": return DreamTalk(v);
                case "favorite": return FavTalk(v);
                case "doing": return DoingTalk(v);
                case "yes": case "no": case "idk": return YesNo(v, cx, intent);
                case "order": return OrderTalk(v, P);
                default: return v.Pk("Like I said.", "Same as before, really.", "You heard me.", "I already told you.") ;
            }
        }

        /// <summary>True when the line, or any longer sentence of it, was said in the last few answers.</summary>
        static bool Echo(ChatCtx cx, string full)
        {
            if (cx.Lines.Contains(full)) return true;
            var recent = cx.Lines.Skip(Math.Max(0, cx.Lines.Count - 3)).ToList();
            foreach (var sent in full.Split(new[] { ". ", "? ", "! " }, StringSplitOptions.RemoveEmptyEntries))
                if (sent.Length > 18 && recent.Any(l => l.Contains(sent))) return true;
            return false;
        }

        static void Remember(ChatCtx cx, string line, string intent, string why)
        {
            cx.Lines.Add(line); while (cx.Lines.Count > 6) cx.Lines.RemoveAt(0);
            if (intent != "why") { cx.LastIntent = intent; cx.LastWhy = why ?? ""; }
            else cx.LastWhy = "";
        }

        static string WhyReply(Mind m, Situation s, string rid) => rid == "hope" ? "Because I want to believe you." : rid == "cold" ? "Because that's what you said to me." : "Because nobody knows. At least you didn't pretend.";

        static string WhyTopic(Voice v, string topic)
        {
            var m = v.M; var s = v.S; var p = v.P;
            switch (topic)
            {
                case "how":
                    if (s.BittenKnown) return "Because I'm bitten. There's no other why.";
                    if (m.Grief != "") return "Because " + m.Grief + " is gone. That's why.";
                    if (s.Starving) return "Because I haven't eaten right in days.";
                    if (p.Hp < 50) return "Because I got hurt and it still hurts. Simple.";
                    if (s.RaidComing) return "Because they're coming tonight and everybody knows it.";
                    if (s.Breaches > 0 || s.Damaged > 2) return "Because the wall's in bad shape and I can hear them out there.";
                    if (m.Mood > 0.3f) return v.Pk("Food in my belly, a wall at my back, people I like. That's a good day now.", m.Partner != "" ? "Honestly? " + m.Partner + ". And we ate today." : "We ate today and nobody died. Bar's low, but we cleared it.");
                    return v.Pk("Just tired. Of all of it.", "It's the waiting. Always waiting for the next bad thing.", "I don't know. Some days just weigh more.");
                case "leader":
                    {
                        bool good = m.Trust >= 0.55f;
                        var top = m.Reasons.Where(r => good ? r.Delta > 0 : r.Delta < 0).OrderByDescending(r => Math.Abs(r.Delta)).Take(2).Select(r => r.Text).ToList();
                        if (top.Count == 0) return "Just a feeling.";
                        return (good ? v.Pk("Mostly? ", "Simple. ", "") : v.Pk("You want it straight? ", "Because ", "")) .Replace("Because ", "") + string.Join(" ", top) + (good ? "" : " " + v.Pk("People notice.", "That's what people talk about."));
                    }
                case "makeit":
                    if (s.Starving || s.FoodDays < 1.5) return "Because there's no food, and hungry people make bad choices.";
                    if (s.Breaches > 0) return "Because there's a hole in the wall. Things get in through holes.";
                    if (m.Hope > 0.3f) return "Because look at us. We're still here. That's not nothing.";
                    return v.Pk("Because everybody dies eventually. Now it's just sooner.", "Because I've seen bigger camps than this go down.");
                case "camp": return s.Starving ? "Because the food's gone." : s.Breaches > 0 ? "Because the wall's broken." : v.G.C.Morale < 40 ? "Because people stopped laughing. That's how it starts." : "Because it's holding. For now.";
                case "trust": return m.Rival != "" ? "With " + m.Rival + "? Little things. Rations that go missing. A look. It adds up." : "Trust gets earned out here. Takes a while.";
                case "need": return s.Starving ? "Because I'm starving. Literally." : "Because that's what's missing. You asked.";
                case "advice": return "Because I've watched other camps fall. It's always the same few things.";
                case "miss": return "Because it's gone, and I didn't know it was the good part while it was happening.";
                case "about": return "Because you asked. Nobody asks.";
                case "lately": return "Because that's what happened. I don't make it up.";
            }
            return "";
        }

        // ---------------- typed-only answers
        static string Greet(Voice v, ChatCtx cx, out float d)
        {
            var m = v.M; var s = v.S; var g = v.G;
            d = 0f;
            bool again = cx.GreetT >= 0 && g.S.T - cx.GreetT < 4;
            if (cx.GreetT < 0 || g.S.T - cx.GreetT > 20) d = 0.005f;
            cx.GreetT = g.S.T;
            if (m.Kid) return again ? v.Pk("Hi again!", "You already said hi.") : v.Pk("Hi!", "Hey. Hi.", "Hello. Are you the boss?", "Hi. Did you bring anything to eat?");
            if (again) return v.Pk("Hi again.", "Still here.", "You already said hi, boss. Hi.", "Hey. Again.");
            string tod = s.Period == "morning" ? v.Pk("Morning.", "Morning, boss.", "Hey. Early, huh?") : s.Period == "night" ? v.Pk("Hey. Can't sleep either?", "Evening. Well, night.", "Hey. Late one.") : v.Pk("Hey.", "Hey, boss.", "Hi.", "Oh. Hey.");
            string tail;
            if (s.BittenKnown) tail = v.Pk("Don't come too close.", "Still me. For now.");
            else if (m.Grief != "") tail = v.Pk("Sorry, I'm not much company today.", "...Hi.");
            else if (s.RaidNow) tail = v.Pk("Kind of busy. They're at the wall.", "Keep your head down.");
            else if (m.Mood > 0.4f) tail = v.Pk("Good to see you.", "What's up?", "You look like you need a coffee. We don't have any.");
            else if (m.Mood < -0.2f) tail = v.Pk("What do you need?", "", "Long day.");
            else tail = v.Pk("What's going on?", "", "Need something?", "");
            return tod + (tail != "" ? " " + tail : "");
        }

        static string Compliment(Voice v, out float d)
        {
            var m = v.M; d = 0.015f;
            if (m.Kid) return v.Pk("Really? Thanks!", "I helped! Did you see?", "...Thanks. Nobody says that.");
            if (m.Trust < 0.3f) { d = 0.008f; return v.Pk("Nice words. Food would be nicer.", "Okay. Thanks, I guess.", "Words are cheap right now. But okay."); }
            if (m.Brave > 0.4f) return v.Pk("Just doing the job.", "Appreciate it. Don't get soft on me.", "Thanks. Somebody's got to.", "Ha. Tell that to my back.");
            if (m.Brave < -0.3f) return v.Pk("Me? Really? ...Thanks. I needed that.", "I don't feel brave. But thanks.", "Wow. Okay. Thank you.");
            return v.Pk("Thanks. That means something.", "Hey, thanks. You too.", "Appreciate it, boss.", "Heh. Thanks. Don't tell the others, they'll want raises.");
        }

        static string Apology(Voice v, ChatCtx cx, out float d)
        {
            var m = v.M; var g = v.G;
            bool recentRude = cx.RudeT >= 0 && g.S.T - cx.RudeT < 24;
            if (recentRude)
            {
                d = 0.03f; cx.Rude = Math.Max(0, cx.Rude - 2);
                if (m.Brave > 0.4f) return v.Pk("Alright. We're good. Don't make it a habit.", "Fine. Water under the bridge.", "Okay. Apology accepted.");
                return v.Pk("Okay. Thank you for saying that.", "...It's okay. Everybody's stressed.", "Thanks. That actually helps.");
            }
            d = 0.005f;
            return v.Pk("For what? You're fine.", "Sorry for what?", "Nothing to be sorry about.", "Okay... you don't have to apologize to me.");
        }

        static string Promise(Voice v, out float d)
        {
            var m = v.M; var s = v.S;
            bool bad = s.Starving || s.Breaches > 0 || m.Grief != "" || s.FoodDays < 1.5;
            if (m.Hope0 < -0.3f && bad) { d = -0.005f; return v.Pk("Don't promise. People who promise end up lying.", "Yeah. Heard that one before.", "Promises don't fill the pantry."); }
            d = 0.02f;
            if (m.Kid) return v.Pk("Pinky promise?", "Okay. I believe you.", "You promise promise?");
            return v.Pk("Okay. I'm holding you to that.", "...Thanks. I needed to hear that.", "Alright. I believe you. Don't make me regret it.", "Good. Because I'm counting on it.", "You'd better. We all are.");
        }

        static string Insult(Voice v, ChatCtx cx, out float d)
        {
            var m = v.M; var g = v.G;
            bool streak = cx.RudeT >= 0 && g.S.T - cx.RudeT < 2;
            cx.Rude = streak ? cx.Rude + 1 : 1; cx.RudeT = g.S.T;
            d = streak ? -0.04f : -0.03f;
            if (cx.Rude >= 3) return v.Pk("I'm done talking.", "...", "Go yell at somebody else.", "We're done here.");
            if (m.Kid) return v.Pk("That's mean.", "I'm telling.", "...Why would you say that?");
            if (m.Brave > 0.4f) return v.Pk("Say that again when the wall's fixed.", "Big talk. You want to stand watch tonight instead of me?", "Wow. Real leadership right there.", "Careful. People remember who talks to them like that.");
            if (m.Brave < -0.3f) return v.Pk("...Okay. I'm trying. I really am.", "I know. I know I'm not good at this.", "Why would you say that? I'm doing my best.");
            return v.Pk("Nice. Real nice.", "Wow. Okay.", "Don't take it out on me.", "That's how it's going to be? Fine.", "I'll remember that.");
        }

        static string Threat(Voice v, ChatCtx cx, out float d)
        {
            var m = v.M;
            cx.Rude += 2; cx.RudeT = v.G.S.T;
            d = m.Brave > 0.4f ? -0.04f : -0.05f;
            if (m.Kid) return v.Pk("Stop it. You're scaring me.", "I want my mom.", "...Please don't.");
            if (m.Brave > 0.4f) return v.Pk("Go ahead. See who watches your back after.", "Try it. I'm not scared of you. I've seen worse than you every night.", "You threaten your own people now? That's how camps die.");
            if (m.Brave < -0.3f) return v.Pk("Please. I'll work harder. I'll do anything.", "Don't. Please. I didn't do anything.", "Okay. Okay. I'm sorry. Whatever it was.");
            return v.Pk("Is that how you lead? Threats?", "Wow. People are going to hear about this.", "You don't mean that. ...Do you?");
        }

        static string Flirt(Voice v, out string why)
        {
            var m = v.M;
            why = "Because now is not the time. Ask me again when the dead stop walking.";
            if (m.Kid) return v.Pk("Ew. That's weird.", "Gross. You're old.", "...I'm telling everyone you said that.");
            if (m.Partner != "") { why = "Because of " + m.Partner + ". Obviously."; return v.Pk("Ha. " + m.Partner + " would have something to say about that.", "Flattered. Taken, though. " + m.Partner + " has good aim.", "Careful. " + m.Partner + " is right over there."); }
            if (m.Grief != "" || m.LostPartner != "") { why = "Because I'm still not over losing someone."; return v.Pk("I... can't. Not right now. Not after everything.", "That's kind. But no. I'm not there."); }
            if (m.Crush != "") { why = "Let's just say I've got my eye on someone else. Don't ask who."; return v.Pk("Heh. Sweet. But my heart's kind of... spoken for. Sort of.", "You're nice. But there's somebody else I keep looking at.", "Smooth. But no. I've got a thing for someone. Don't ask."); }
            if (m.Trust < 0.35f) return v.Pk("Nice try. Feed us first.", "Really? Now? Fix the wall, then we'll talk.", "Ha. No.");
            return v.Pk("Smooth. Real smooth. Ask me again when the dead stop walking.", "Ha! Is that what passes for flirting at the end of the world?", "Boss, you're sweet. But somebody's got to keep a clear head around here.", "Buy me dinner first. Real dinner. Not beans.", "I'll think about it. Right after we survive the week.");
        }

        static readonly string[] JOKES_CLEAN = { "Why don't zombies eat clowns? They taste funny.", "What do you call a zombie who won't stop talking? A walkie-talkie.",
            "I told the others I'd stop making zombie jokes. They said I'd been dead serious for too long.", "Why did the zombie skip school? He felt rotten.",
            "Knock knock. ...Okay, nobody's knocking. That's the joke. We'd all hide.", "What's a zombie's favorite cereal? Rice Creepies.",
            "Why did the scavenger bring a ladder? Heard the prices went through the roof. ...Ha. There are no prices.",
            "What do zombies eat with their cheese? Grave crackers." };
        static readonly string[] JOKES_DARK = { "At least the commute's short now.", "Silver lining: no more taxes.", "I've lost weight though. Apocalypse diet. Zero stars.",
            "The good news is the housing market's wide open.", "My ex finally wants me for my brains.", "My therapist would have a field day. If she wasn't, you know. One of them." };

        static string JokeTalk(Voice v)
        {
            var m = v.M;
            if (m.Kid) return v.Pk("Why did the zombie go to school? To get more brains! ...Get it?", "What's a zombie's favorite toy? A deady bear!", "I know one! Knock knock! ...You have to say who's there.");
            if (m.Grief != "" || v.S.BittenKnown) return v.Pk("Not today. Sorry.", "I don't have any in me right now.");
            if (m.Mood < -0.3f && m.Chatty < 0.3f) return v.Pk("Not really in a joking mood.", "Look around. That's the joke.");
            string j = m.Dark && v.Coin(0.5f) ? v.Pk(JOKES_DARK) : v.Pk(JOKES_CLEAN);
            return (m.Chatty < -0.4f ? "" : v.Pk("Okay. ", "Alright, here's one. ", "", "Fine. ")) + j + (m.Chatty > 0.3f ? " " + v.Pk("I've got a million of these.", "...Tough crowd.", "Don't tell me you didn't smile.") : "");
        }

        static string Meta(Voice v, Parse P)
        {
            var m = v.M; var raws = P.Toks.Select(t => t.Raw).ToList();
            bool game = raws.Contains("game") || raws.Contains("simulation") || raws.Contains("matrix") || raws.Contains("npc");
            bool ai = raws.Any(w => w == "ai" || w == "robot" || w == "bot" || w == "chatgpt" || w == "artificial" || w == "programmed" || w == "computer" || w == "algorithm" || w == "model");
            if (m.Kid) return v.Pk("Are YOU a robot? Robots aren't real. Mom said.", "I'm a kid, not a robot. Robots don't get hungry.", "That's silly.");
            if (game) return v.Pk("A game? If this is a game, it's a bad one and I want my money back.", "Some game. I can't find the pause button.", "Sure feels like somebody up there's playing with us. Doesn't make the bites hurt less.", "If it's a game, I'd like to talk to whoever made it. I've got notes.");
            if (ai) return v.Pk("A robot? Robots don't get blisters.", "If I was a machine I wouldn't need to eat, and believe me, I need to eat.", "Do I look like a computer to you? Have you seen my hands?", "Ha. I wish I was a robot. Robots don't get scared.");
            return v.Pk("Real? I bleed, I eat beans, I'm scared of the dark. Real enough.", "You okay? Did you hit your head out there? Look at me.", "Pinch me. Actually don't, I bruise easy.", "Are YOU real? Some days I wonder if any of this is.", "I'm as real as that wall. Which isn't saying a lot, these days.");
        }

        static string PlayerName(Voice v, ChatCtx cx, string text)
        {
            var mm = Regex.Match(text, @"(?:name is|call me|i am called|i'm called)\s+([A-Za-z][A-Za-z\-]{0,15})", RegexOptions.IgnoreCase);
            string n = mm.Success ? Cap(mm.Groups[1].Value.ToLowerInvariant()) : "";
            if (n == "") return v.Pk("Okay. Nice to meet you, I guess.", "Sure thing.");
            bool same = cx.PlayerName == n; cx.PlayerName = n;
            if (same) return v.Pk("I know, " + n + ". I remember.", "Yeah, " + n + ". You told me.");
            if (v.M.Kid) return v.Pk("Hi, " + n + "!", n + "? That's a nice name.");
            return v.Pk(n + ". Got it. Nice to meet you properly.", "Alright, " + n + ". Still calling you boss, though.", n + ", huh? Suits you.", "Good to put a name to the face, " + n + ".");
        }

        static string NameTalk(Voice v)
        {
            var m = v.M; var p = v.P;
            if (m.Kid) return v.Pk("I'm " + p.First + ".", p.First + ". What's yours?");
            if (m.Chatty < -0.4f) return v.Pk(p.First + ".", "It's " + p.Name + ".");
            return v.Pk("It's " + p.Name + ". " + p.First + " is fine.", p.Name + ". You're bad with names, huh?", "Name's " + p.First + ". " + p.Name + " if you're writing it on a list.", p.First + ". We've met. I'll let it slide.");
        }

        static string AgeTalk(Voice v)
        {
            var m = v.M; int a = AgeOf(v.G, v.P, m);
            if (m.Kid) return v.Pk("I'm " + NumW(a) + ".", Cap(NumW(a)) + ". And a half.", "I'm " + NumW(a) + ". Almost " + NumW(a + 1) + ".");
            string ag = Cap(NumW(a)) + ".";
            if (a < 26) return ag + " " + v.Pk("Yeah. Don't say anything.", "Old enough.", "Feels like fifty.");
            if (a > 50) return ag + " " + v.Pk("Old enough to know better. Still here though.", "Don't let it fool you. I can still outrun most of them.", "Knees feel ninety.");
            return ag + " " + v.Pk("Feels like a hundred some days.", "Give or take. I stopped counting birthdays.", "Why, do I look older?");
        }

        static string JobTalk(Voice v)
        {
            var m = v.M; var p = v.P;
            if (m.Kid) return v.Pk("I went to school. Now I help with chores.", "I'm a kid. I don't have a job. I carry water sometimes.");
            string now = (p.Job ?? "Civilian") == "Civilian" ? "Now I do whatever needs doing." : "Now I'm the " + p.Job.ToLowerInvariant() + " around here, apparently.";
            return "I was " + m.OldJob + ". " + v.Pk("Feels like another life.", "Hated it. Would give anything to go back.", "Weird to think about now.", "Paid the rent. Mostly.") + " " + now;
        }

        static string HomeTalk(Voice v)
        {
            var m = v.M;
            if (m.BornHere) return "Here. I was born here. This is the only place I know.";
            string from = char.IsUpper(m.Home[0]) ? "I'm from " + m.Home + "." : "Grew up in " + m.Home + ".";
            return from + " " + v.Pk("Haven't been back. Don't want to see it like this.", "Probably nothing left of it now.", "Nice place. Was.", "You ever been? Don't bother now.", "Miss it some days. Not the winters.");
        }

        static string FamilyTalk(Voice v)
        {
            var m = v.M; var bits = new List<string>();
            if (m.Kid)
            {
                var par = v.G.C.People.Where(q => q.St != "dead" && v.P.Parents.Contains(q.Id)).ToList();
                if (par.Count > 0) return v.Pk(par[0].First + "'s right over there. " + (par.Count > 1 ? "And " + par[1].First + "." : "We stick together."), "I've got " + string.Join(" and ", par.Select(q => q.First)) + ". They're the best.");
                string pr = Pron(m.LostWho);
                return v.Pk("I don't know where " + m.LostWho + " is. Maybe " + pr + "'s looking for me.", "I had " + m.LostWho + ". I still look for " + (pr == "she" ? "her" : pr == "he" ? "him" : "them") + " at the gate.");
            }
            if (m.Partner != "") bits.Add(v.Pk(m.Partner + ". That's my family now.", "I've got " + m.Partner + ". That's everything."));
            if (m.Kids.Count > 0) bits.Add("And " + string.Join(" and ", m.Kids) + ". " + v.Pk("Everything I do is for them.", "They're why I get up."));
            if (m.LostPartner != "") bits.Add("I had " + m.LostPartner + ". We lost them here.");
            bits.Add((bits.Count > 0 ? "Before all this, there was " : "There was ") + m.LostWho + ". " + m.LostName + ". " + v.Pk("I don't know where they are.", "I try not to think about it.", "Gone, I think."));
            return string.Join(" ", bits.Take(v.M.Chatty > 0.3f ? 3 : 2));
        }

        static string LostTalk(Voice v)
        {
            var m = v.M;
            if (m.Grief != "") return v.Pk("Here? " + m.Grief + ". It's still fresh.", m.Grief + ". Don't make me talk about it.");
            if (m.Kid) return "My " + m.LostWho.Replace("my ", "") + ". " + v.Pk("I don't know where they went.", "I still look for them at the gate.");
            return Cap(m.LostWho) + ". " + m.LostName + ". " + m.LostHow;
        }

        static string FearTalk(Voice v)
        {
            var m = v.M; var s = v.S;
            if (m.Kid) return v.Pk("The dark. And the noises they make.", "That everyone leaves. Like before.");
            string lead = m.Brave > 0.5f ? v.Pk("Not much, anymore. But if I'm honest? ", "Scared? Me? ...Fine. ") : m.Brave < -0.3f ? v.Pk("Everything. But the big one? ", "Where do I start. ") : v.Pk("Honestly? ", "");
            string now = s.RaidComing ? " And tonight, them. They're coming." : s.Breaches > 0 ? " And right now, that hole in the wall." : "";
            return lead + Cap(m.Fear) + now;
        }

        static string DreamTalk(Voice v)
        {
            var m = v.M;
            if (m.Kid) return v.Pk("When I'm big I'm going to " + m.Dream, "I want to " + m.Dream);
            string lead = m.Hope < -0.3f ? v.Pk("Dreams. Ha. Fine. ", "If there's an after? ") : v.Pk("When this is over? ", "Someday? ", "");
            return lead + "I want to " + m.Dream + " " + v.Pk("Don't laugh.", "Somebody's gotta dream.", "That's the plan, anyway.", "");
        }

        static string RegretTalk(Voice v)
        {
            var m = v.M;
            if (m.Kid) return v.Pk("I yelled at my mom the last day. I didn't mean it.", "I broke a window once. That's it. I'm a kid.");
            if (m.Chatty < -0.4f && m.Trust < 0.5f) return v.Pk("That's mine to carry.", "Not something I talk about.");
            return v.Pk("Here's a thing nobody knows. ", "Okay. Since you asked. ", "") + m.Regret + " " + v.Pk("There. Now you know.", "Don't tell the others.", "I think about it every day.");
        }

        static string FavTalk(Voice v)
        {
            var g = v.G; var p = v.P;
            string food = Pick(FAV_FOOD, H(g, p.Id, "favfood")), thing = Pick(FAV_THING, H(g, p.Id, "favthing"));
            if (v.M.Kid) return v.Pk("Ice cream. I think. I only had it once.", "Drawing. And the dog at the gate. Is there a dog? There should be a dog.");
            if (v.Food) return v.Pk(Cap(food) + ". God, I'd kill for it.", "Easy. " + Cap(food) + ".", Cap(food) + ". Don't get me started.");
            if (v.Hobby) return v.Pk(Cap(thing) + ". Before all this, anyway.", "Used to be " + thing + ".");
            return v.Pk("Food? " + Cap(food) + ". God, I'd kill for it.", "Before? " + Cap(thing) + ".", "Honestly, I miss " + v.M.Miss, Cap(thing) + ". And " + food + ". In that order.");
        }

        static string DoingTalk(Voice v)
        {
            var p = v.P; var s = v.S; var g = v.G;
            if (p.SleepEndT > g.S.T) return v.Pk("Trying to sleep, actually.", "Was sleeping. Now I'm talking to you.");
            if (p.RestEndT > g.S.T) return v.Pk("Resting. Doctor's orders. Well, your orders.", "Day off. Enjoying it. Don't ruin it.");
            if (v.M.Kid) return v.Pk("Playing. Kind of. There's not much to play with.", "Helping carry stuff. I'm strong.", "Nothing. Grown-ups keep telling me to stay inside.");
            if (s.Away) return v.Pk("Out on a run. Keeping my head down.", "Out here, where you sent me. Quiet so far.", "Moving. Can't talk long out here.");
            if (p.Guard) return v.Pk("On watch. Eyes on the dark.", "Guarding the wall. Nothing so far.", "Watching the fence line. Same as always.");
            switch (p.Job)
            {
                case "Farmer": return v.Pk("Working the beds. Dirt under every nail.", "Weeding. Glamorous, I know.");
                case "Medic": return v.Pk("Counting bandages. Again.", "Checking on the hurt ones.");
                case "Engineer": return v.Pk("Fixing what's broken. There's always something.", "Patching things. Nails, wire, prayer.");
                case "Scavenger": return v.Pk("Waiting to go out. Sitting still makes me itchy.", "Fixing my pack for the next run.");
                case "Soldier": case "Cop": return v.Pk("Cleaning my weapon. Keeping it ready.", "Walking the line. Old habits.");
                case "Scientist": return v.Pk("Trying to figure out the water filters.", "Reading. Thinking. Somebody has to.");
            }
            return v.Pk("Whatever needs doing.", "Not much. Waiting for the next thing.", "Helping where I can.");
        }

        static string FaithTalk(Voice v)
        {
            var m = v.M;
            if (m.Kid) return v.Pk("My grandma said we should pray before bed. I still do.", "Is heaven real? Somebody said the dead ones can't go.");
            if (m.Hope > 0.3f) return v.Pk("I pray. Every night. Someone's listening, I think.", "I believe. Hard not to, when you're still alive after all this.");
            if (m.Hope < -0.3f) return v.Pk("If there's a god, he left the door open.", "I stopped praying the second night.", "God? Look outside.");
            return v.Pk("Used to go to church at Christmas. Now I just pray the wall holds.", "I don't know. I talk to somebody at night. Might just be me.", "Some days yes. Some days no.");
        }

        static string Pron(string who)
        {
            foreach (var w in new[] { "mom", "sister", "wife", "daughter", "grandma", "girlfriend" }) if (who.EndsWith(w)) return "she";
            foreach (var w in new[] { "dad", "brother", "husband", "son", "grandpa", "boyfriend" }) if (who.EndsWith(w)) return "he";
            return "they";
        }

        static string Days(double d) => d < 1 ? "not even a day" : d < 2 ? "about a day" : d < 3.5 ? "a couple of days" : d < 7 ? "a few days" : d < 14 ? "a week or so" : "a good while";

        static string SupplyTalk(Voice v, string what, out string why)
        {
            var m = v.M; var s = v.S; var p = v.P;
            why = "";
            switch (what)
            {
                case "food":
                    why = s.FoodDays < 2 ? "Because more of us eat than we bring in. That math only goes one way." : "Because the gardens and the runs are keeping up. For now.";
                    if (s.Food < 1) return v.Pk("There isn't any. That's the food situation.", "Gone. We're out. People are boiling grass.", "Out. Completely.") + " " + v.Pk("Somebody needs to go out. Today.", "I'm so hungry it hurts.");
                    if (s.FoodDays < 2) return v.Pk("Thin. ", "Bad. ") + Cap(Days(s.FoodDays)) + v.Pk(" left, the way we eat.", " of food, maybe.") + " " + (s.Garden ? "The gardens can't keep up." : "And no garden.");
                    if (s.FoodDays < 5) return v.Pk("We're okay. ", "Fine for now. ") + Cap(Days(s.FoodDays)) + " worth. " + v.Pk("Wouldn't hurt to send someone out.", "Not enough to relax.");
                    return v.Pk("We're good on food. Pantry's actually full for once.", "Eating fine. " + Cap(Days(s.FoodDays)) + " of it, at least.", "Food's the one thing I'm not worried about right now.") + (m.Chatty > 0.3f ? " " + v.Pk("Still beans, though. Always beans.", "Would kill for something fresh.") : "");
                case "water":
                    why = "Because water goes faster than anyone thinks.";
                    if (s.Water < 1) return v.Pk("We're out of water. That kills you faster than anything out there.", "Dry. Completely. My tongue feels like sandpaper.");
                    if (s.WaterDays < 2) return v.Pk("Low. We're rationing sips.", "Not much. " + Cap(Days(s.WaterDays)) + ", maybe.") + " " + v.Pk("Rain would help.", "We need a better way to catch it.");
                    return v.Pk("Water's fine. For now.", "Enough water. " + Cap(Days(s.WaterDays)) + " of it.", "We're okay on water. Tastes like the barrel, but it's wet.");
                case "meds":
                    why = s.Medics == 0 ? "Because nobody here really knows medicine." : "Because people keep getting hurt faster than we find supplies.";
                    {
                        string stock = s.Meds < 1 ? v.Pk("We've got nothing. Not even aspirin.", "Out of meds. Completely.") : s.Meds < 3 ? v.Pk("Almost out. A few bandages.", "Barely anything left.") : v.Pk("We've got some. Not a lot.", "Enough for now, if nobody does anything stupid.");
                        string who = s.Medics == 0 ? " " + v.Pk("And no real medic.", "And nobody who knows how to use it.") : "";
                        string hurt = s.HurtName != "" ? " " + s.HurtName + v.Pk(" needs it most.", " is in bad shape.") : "";
                        string self = p.Hp < 50 ? " " + v.Pk("I could use some myself.", "My side's still bleeding, so.") : "";
                        return stock + who + hurt + self;
                    }
                default:
                    why = "Because every night we shoot more than we find.";
                    {
                        bool fighter = p.Job == "Soldier" || p.Job == "Cop" || p.Guard;
                        string a = s.Ammo < 1 ? v.Pk("No ammo. None. We're down to bats and harsh words.", "Out. Every round's gone.") : s.Ammo < 10 ? v.Pk("Low. I'm counting every round.", "Barely any. Make every shot count.") : s.Ammo < 40 ? v.Pk("Some. Enough for a bad night. Not two.", "We've got a little.") : v.Pk("Plenty, for once.", "We're stocked. Don't waste it.");
                        if (fighter && s.Ammo < 10) a += " " + v.Pk("And I'm the one who needs it.", "Can't hold a wall with an empty gun.");
                        if (string.IsNullOrEmpty(p.Wpn) && !m.Kid && v.Coin(0.5f)) a += " " + v.Pk("Wouldn't mind a real weapon either.", "I'm still using whatever I can swing.");
                        return a;
                    }
            }
        }

        static string WallTalk(Voice v, out string why)
        {
            var s = v.S; var g = v.G;
            if (g.S.Fences.Count == 0) { why = "Because a camp without a wall is a buffet."; return v.Pk("What wall? We don't have one. That keeps me up.", "We need a wall. Any wall. Right now it's just us and hoping."); }
            string side = s.WeakSide != "" ? "the " + s.WeakSide + " side" : "one side";
            if (s.Breaches > 0) { why = "Because they already got through once. They'll find it again."; return v.Pk("There's a hole in it. On " + side + ". A hole.", "It's broken" + (s.WeakStreet != "" ? ", over on " + Street(s.WeakStreet) : "") + ". Nobody sleeps till it's fixed.") + " " + v.Pk("Fix it before dark.", "Please get someone on it."); }
            if (s.Damaged > 2) { why = "Because every night they hit it, and every night less of it is left."; return v.Pk("Battered. " + Cap(side) + " is barely standing.", "It's held together with hope and old nails, especially " + side + "."); }
            if (s.Damaged > 0) { why = "Because nothing out there stops trying."; return v.Pk("Mostly solid. A few weak spots on " + side + ".", "Holding. Could use some patching on " + side + "."); }
            why = "Because we keep fixing it. That's the whole secret.";
            return v.Pk("Solid. I check it every night anyway.", "Wall's holding. Best thing we've built.", "It's good. Good enough that I sleep. Some.");
        }

        static string ZedTalk(Voice v, out string why)
        {
            var s = v.S; var g = v.G; var m = v.M;
            var rec = Recent(g, 48);
            why = "Because there's always more of them. That's the only thing they're good at.";
            if (s.RaidNow) return v.Pk("They're at the wall right now. Can you hear them?", "Right now? Out there, scratching. Lots of them.");
            if (s.RaidComing) return (s.MajorRaid ? v.Pk("A big one's coming. Bigger than anything yet.", "Something big is moving our way. Everybody can feel it.") : v.Pk("They're coming tonight. You can hear them gathering.", "Tonight. They'll come tonight.")) + " " + (m.Brave > 0.3f ? "Let them." : v.Pk("I hate this part.", "My hands won't stop shaking."));
            if (rec.Any(e => e.Kind == "horde")) return v.Pk("That horde the other day... I've never seen so many.", "After that horde, I don't think anything will surprise me.");
            int n = 0; try { n = g.ZombieCount(); } catch { }
            string tail = m.Brave > 0.4f ? v.Pk("They're slow. We're not.", "One at a time, they're nothing.") : m.Brave < -0.3f ? v.Pk("I can't look at them.", "I dream about them. Every night.") : v.Pk("You get used to them. Sort of.", "They never stop. That's the scary part.");
            return (n > 400 ? v.Pk("The city's crawling with them.", "There's more of them than ever out there.") : v.Pk("Quiet-ish. Too quiet, maybe.", "Out there, same as always.", "Shuffling around out past the wall.")) + " " + tail;
        }

        static string WeatherTalk(Voice v, out string why)
        {
            var g = v.G; var s = v.S;
            why = "Because weather's the one thing out here that doesn't want to eat you.";
            string w = "";
            if (Game.ExpansionRules)
            {
                ExpWeather cw = ExpWeather.Clear; ExpSeason se = ExpSeason.Summer;
                try { cw = g.CurrentWeather; se = g.CurrentSeason; } catch { }
                switch (cw)
                {
                    case ExpWeather.Rain: w = v.Pk("Raining. At least the barrels fill up.", "Wet. Everything's wet."); break;
                    case ExpWeather.Storm: w = v.Pk("Storm's rolling. Can't hear them coming in this.", "Nasty out. Thunder sounds like gunfire."); break;
                    case ExpWeather.Fog: w = v.Pk("Fog. I hate fog. They come out of it like ghosts.", "Can't see past the wall in this soup."); break;
                    case ExpWeather.Snow: w = v.Pk("Snowing. Pretty, until you remember we need firewood.", "Cold. Snow. My fingers are done."); break;
                    default: w = v.Pk("Clear, for now.", "Not bad out. Dry, at least."); break;
                }
                if (se == ExpSeason.Winter && cw != ExpWeather.Snow) w += " " + v.Pk("Winter, though. Food won't grow.", "Cold season. Rough on everyone.");
                else if (se == ExpSeason.Autumn && v.Coin(0.5f)) w += " Winter's coming. We should stock up.";
            }
            else w = s.Night ? v.Pk("Dark and cold. Same as every night.", "Can't see much. Stars are nice, though.") : s.Period == "morning" ? v.Pk("Nice morning, actually. Almost normal.", "Cool and quiet. Good time for a run.") : v.Pk("Warm. Dry. The dead smell worse in the heat.", "Clear sky. Weird how pretty it still gets.");
            return w;
        }

        static string YouFeelAck(Voice v, Parse P)
        {
            bool sad = P.Toks.Any(t => t.C == "@sad");
            return sad ? v.Pk("Yeah, me too. ", "I hear you. ") : v.Pk("Good to hear. ", "Glad somebody is. ");
        }

        static string YouFeel(Voice v, Parse P)
        {
            var m = v.M;
            bool sad = P.Toks.Any(t => t.C == "@sad") || !P.Toks.Any(t => t.C == "@happy");
            bool scared = P.Toks.Any(t => t.Raw == "scared" || t.Raw == "afraid" || t.Raw == "terrified");
            bool tired = P.Toks.Any(t => t.Raw == "tired" || t.Raw == "exhausted");
            bool hungry = P.Toks.Any(t => t.C == "@food");
            if (m.Kid) return sad ? v.Pk("It's okay. You can hold my hand if you want.", "Don't be sad. You're the boss.") : v.Pk("Yay!", "Me too!");
            if (!sad) return v.Pk("Good. Somebody should be.", "Glad to hear it. Don't jinx it.", "Good. We need you sharp.", "Nice. Hold on to that.");
            if (hungry) return v.Pk("Yeah. All of us are. Eat something, boss. We need you thinking straight.", "Join the club. Maybe we send someone out for food?");
            if (scared) return m.Brave > 0.3f ? v.Pk("That's alright. Scared keeps you alive. I've got your back.", "Everybody is. Just don't let it make your choices.") : v.Pk("You too? I thought it was just me.", "Me too. All the time. We'll be scared together, I guess.");
            if (tired) return v.Pk("Then sleep. We'll hold the wall for a few hours.", "You've been carrying all of us. Take a break, boss.", "Get some rest. Seriously. You look like hell.");
            return v.Pk("Hey. You're doing better than you think.", "That's allowed. You don't have to be okay all the time.", "I know. It's a lot. You're not alone in it.", "Want to sit for a minute? Nobody's watching.");
        }

        static string Bye(Voice v)
        {
            var m = v.M; var s = v.S;
            if (m.Kid) return v.Pk("Bye!", "See you later!", "Bye. Come back, okay?");
            if (s.Night) return v.Pk("Night. Get some sleep.", "Goodnight. Lock the door.", "Night, boss.");
            return v.Pk("See you.", "Stay safe out there.", "Later, boss.", "Go on. I'll be here.", "Don't be a stranger.");
        }

        static string OrderTalk(Voice v, Parse P)
        {
            var m = v.M;
            if (m.Kid) return v.Pk("I'm just a kid. Ask a grown-up.", "Mom says I'm not allowed.");
            if (m.Brave < -0.3f) return v.Pk("Okay. If you send me, I'll go. Just... don't send me alone.", "If that's an order, make it one. I won't like it, but I'll go.", "You want me out there? Then send me. I'll do it.");
            if (m.Brave > 0.4f) return v.Pk("Say the word and I'm gone.", "Make it an order and I'm on it.", "Just point me at it. Officially.");
            return v.Pk("You're the boss. Send me and I'll go.", "If you want me on it, put me on it. I go where you send me.", "Make it official and it's done.");
        }

        static string YesNo(Voice v, ChatCtx cx, string kind)
        {
            var m = v.M;
            switch (cx.AskedBack)
            {
                case "clarify": return v.Pk("Which one, though?", "I still don't know who you mean.");
                case "greet": case "youfeel":
                    return kind == "yes" ? v.Pk("Yeah? Good.", "Okay. Good.") : kind == "no" ? v.Pk("No? Okay. Let me know if you need anything.", "Alright.") : v.Pk("Fair enough.", "Yeah. Nobody really knows anything anymore.");
            }
            if (cx.AskedBack != "" && cx.Lines.Count > 0)
                return kind == "yes" ? v.Pk("Yeah. I thought so.", "Good. Me too.", "Okay. That helps.") : kind == "no" ? v.Pk("No? Huh. Okay.", "Really? Alright.", "Fair.") : v.Pk("Yeah. Me neither.", "That's honest, at least.");
            if (cx.LastIntent == "person" && cx.Lines.Count > 0)
                return kind == "yes" ? v.Pk("Yeah.", "Mm-hm.") : kind == "no" ? v.Pk("No? Well, that's how I see it.", "You asked.") : v.Pk("Yeah. Hard to say with people.", "Me neither, really.");
            if (m.Kid) return kind == "yes" ? "Yes what?" : kind == "no" ? "No what?" : "Me neither.";
            return kind == "yes" ? v.Pk("Yes... what?", "Okay. Yes to what?", "Glad we agree. On something.") : kind == "no" ? v.Pk("No what?", "Okay... no.", "No to what? You lost me.") : v.Pk("Me neither. Nobody knows anything anymore.", "Join the club.");
        }

        static string Silence(Voice v)
        {
            if (v.M.Kid) return v.Pk("What?", "Why are you looking at me like that?");
            return v.Pk("...?", "You okay? You went quiet.", "Something on your mind?", "Yeah. I know. Me too.", "Cat got your tongue?");
        }

        static string Confused(Voice v, ChatCtx cx)
        {
            var m = v.M; cx.Gib++;
            if (m.Kid) return v.Pk("What? That's not words.", "Huh?", "Are you okay? You're talking funny.");
            if (cx.Gib >= 3) return v.Pk("Okay, now you're just messing with me.", "Are you okay? Should I get the medic?", "Boss. Sleep. Now. You're scaring me.");
            if (v.S.Away) return v.Pk("You're breaking up. Say again?", "Static. Didn't catch that. Say again?", "Radio's garbling you. Repeat?");
            return v.Pk("What?", "Say that again? Slower.", "Huh? You hit your head or something?", "I didn't catch a word of that.", "Was that English?", "...Okay? Sure.");
        }

        static string Unknown(Voice v, ChatCtx cx, Parse P)
        {
            var m = v.M; var s = v.S; cx.Unk++;
            if (m.Kid) return v.Pk("I don't get it.", "What does that mean?", "Huh? Can we talk about something else?");
            string lead = v.Pk("Not sure what you mean.", "I don't follow.", "You lost me there.", "Huh. Not sure what to say to that.", "Okay... I don't know about that.", "That's a new one.");
            string pivot;
            if (s.Starving) pivot = v.Pk("All I can think about is food right now.", "Sorry, I'm too hungry to think straight.");
            else if (s.RaidComing) pivot = v.Pk("My head's on tonight. They're coming.", "Can it wait? Tonight's going to be rough.");
            else if (m.Grief != "") pivot = v.Pk("I keep thinking about " + m.Grief + ".", "Sorry. My head's somewhere else.");
            else if (s.Breaches > 0 || s.Damaged > 2) pivot = "I'm more worried about the wall, honestly.";
            else pivot = v.Pk("Ask me something easier.", "Ask me about the camp, or about me. I'm better at that.", "", "Try me again?", "", "Anyway. Things are okay here.");
            return lead + (pivot != "" ? " " + pivot : "");
        }

        // ---------------- other people
        static string AboutDead(Voice v, string fullName, out string why)
        {
            var m = v.M; var g = v.G; string f = FirstOf(fullName);
            bool close = m.Grief == f || m.LostPartner == f;
            why = close ? "Because they were mine. My person." : "Because they were one of us. That's enough.";
            // how and when, from the memorial / log (people say "got dragged down", not the log's wording)
            var fi = Fallen(g).LastOrDefault(x => x.Name == fullName);
            string how = "";
            if (fi.Name != null)
            {
                string c = (fi.Cause ?? "").Trim().TrimEnd('.');
                c = Regex.Replace(c, @"^was ", "got ");
                double t = fi.T > 0 ? fi.T : (fi.Day - 1) * 24.0 + 12.0;
                how = c != "" ? f + " " + Words(c) + " " + Ago(g.S.T - t, g.Hour()) + "." : "We lost " + f + " " + Ago(g.S.T - t, g.Hour()) + ".";
            }
            if (close) return (how != "" && v.Coin(0.5f) ? how + " " : "") + v.Pk("Please don't. Not " + f + ". Not yet.", "I miss " + f + " every single day.", "I keep turning around to tell " + f + " something.");
            if (m.Kid) return v.Pk(f + " died. Everyone was crying.", "I don't want to talk about " + f + ".");
            if (how == "") how = v.Pk("We lost " + f + ".", f + "'s gone.");
            return how + " " + v.Pk("I didn't know them that well. Still hurts.", "We buried them by the fence.", "One of us. That's all that matters now.", "Every time it's someone, the camp gets a little quieter.", "Wasn't fair. None of it is.");
        }

        static string AboutPerson(Voice v, Person q, string intent, Parse P, out string why)
        {
            var g = v.G; var p = v.P; var m = v.M;
            string f = q.First;
            double rel = p.Rel?.Get(q.Id.ToString(), 0.0) ?? 0.0;
            bool partner = m.PartnerId == q.Id, myKid = p.Kids.Contains(q.Id), myParent = p.Parents.Contains(q.Id);
            bool friend = m.BestFriendId == q.Id || rel >= Game.REL_FRIEND * 0.6, crush = m.Crush == f, rival = m.Rival == f, watch = m.WatchTrust == f;
            string qStage = "adult"; try { qStage = g.AgeStage(q); } catch { }
            string job = (q.Job ?? "Civilian") == "Civilian" ? "" : q.Job.ToLowerInvariant();
            // state of the other person, as they'd see it
            string state = "";
            if (q.Known && q.Inf >= 0.0) state = v.Pk(f + "'s bitten. You know that. I can't look at it.", "And " + f + " is bitten. I don't want to think about what's next.");
            else if (q.St != "base") state = v.Pk(f + "'s out on a run right now.", "They're out there right now. I hate when people are out.");
            else if (q.Hp < 40) state = v.Pk(f + "'s hurt bad. Somebody should be with them.", "They got torn up out there. Not doing great.");
            else if (q.Guard) state = v.Pk(f + "'s on the wall tonight.", "On watch, last I saw.");
            string body;
            if (intent == "insult" || intent == "threat")
            {
                why = "Because we can't afford to turn on each other.";
                body = rival ? v.Pk("Ha. Can't say I disagree. But keep it down.", "Don't let them hear you. Even if you're not wrong.") : partner || myKid || friend ? v.Pk("Hey. Watch it. That's " + f + " you're talking about.", "Don't talk about " + f + " like that. Not to me.") : v.Pk("That's harsh. " + f + " pulls their weight.", "Easy. We need everybody.");
                return body;
            }
            if (partner)
            {
                why = "Because " + f + " is the reason I get up in the morning.";
                body = v.Pk(f + "? " + f + "'s everything. Don't tell them I said that.", "I'd walk into a horde for " + f + ". I mean it.", f + " keeps me sane. Mostly.", "Best thing that's happened to me since all this. Worst timing, best person.");
            }
            else if (myKid)
            {
                why = "Because " + f + " is my kid. That's the whole reason.";
                body = v.Pk(f + "'s my whole world. Everything I do is for them.", "My " + (q.Sex == "f" ? "girl" : "boy") + ". Growing up too fast in a world like this.", f + "? Smart. Stubborn. Gets that from me.");
            }
            else if (myParent)
            {
                why = "Because they're my " + (q.Sex == "f" ? "mom" : "dad") + ".";
                body = m.Kid ? v.Pk("That's my " + (q.Sex == "f" ? "mom" : "dad") + "! They're the best.", "My " + (q.Sex == "f" ? "mom" : "dad") + " says I have to listen to you.") : "That's my " + (q.Sex == "f" ? "mom" : "dad") + ". " + v.Pk("Still telling me what to do.", "Glad we're together. Most people didn't get that.");
            }
            else if (crush && !m.Kid)
            {
                why = v.Pk("Why do you want to know? ...Fine. They're kind. And they laugh at my jokes.", "No reason. Shut up.");
                body = v.Pk("...What about " + f + "? Why are you asking? Did they say something?", f + "'s nice. That's all. Why are you smiling?", "Okay, " + f + " is kind of great. Don't you dare say anything.");
            }
            else if (rival)
            {
                why = v.Pk("Little things. They eat first and work last. Or that's how it looks to me.", "They took my spot by the stove the first night. Petty? Maybe. I don't forget.", "I don't trust them. Can't say why. Gut feeling.");
                body = v.Pk(f + "? Ehh. Let's just say I count my rations after they walk by.", "Me and " + f + " don't really get along. Whatever.", "Don't get me started on " + f + ".", f + " is... fine. From a distance.");
            }
            else if (friend)
            {
                why = "Because " + f + " has had my back more times than I can count.";
                body = v.Pk(f + "'s good people. Real good.", f + " has my back. We've been through some stuff.", "I'd trust " + f + " with my life. I already have, a few times.", f + "? My friend. One of the few.");
            }
            else if (qStage != "adult")
            {
                why = "Because kids are the only reason any of this matters.";
                body = v.Pk(f + "'s a sweet kid. Too young for all this.", "Little " + f + "? Keeps us all honest.", "Kids like " + f + " are why we keep the walls up.");
            }
            else if (rel < -5)
            {
                why = "We had words. It's not worth repeating.";
                body = v.Pk("We don't really talk, " + f + " and me.", f + "? We've had our moments. Not the good kind.", "I keep my distance from " + f + ".");
            }
            else
            {
                why = "I just don't know them that well yet.";
                string jb = job == "" ? "" : v.Pk("The " + job + ". ", f + "'s our " + job + ". ");
                string op = q.Kills > 10 ? v.Pk("Has put down more of them than anybody.", "Tough. Doesn't flinch.") : job == "medic" ? v.Pk("Steady hands.", "Patched me up once.") : job == "farmer" ? "Keeps us fed." : job == "engineer" ? "Can fix anything." : v.Pk("Seems alright.", "Don't know them that well.", "Quiet. Does the work.", "Okay, I guess. We don't talk much.");
                body = jb + op;
            }
            if (watch && !partner && !rival && v.Coin(0.6f)) body += " " + v.Pk("On the wall? I want " + f + " next to me.", "Best shot we've got.");
            if (state != "" && (intent != "trust")) body += " " + state;
            if (intent == "flirt" && !crush && !m.Kid) body += " " + v.Pk("Why, you interested?", "Are you trying to set us up? Stop.");
            return body;
        }

        static string Clean(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s) sb.Append(ch < 32 || ch > 126 ? ' ' : ch);
            return Regex.Replace(sb.ToString(), "  +", " ").Trim();
        }
    }
}
