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
        public static List<Ev> Recent(Game g, double hours = 72.0)
        {
            var list = new List<Ev>();
            if (g?.S?.Log == null || g.C == null) return list;
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
            public Game G; public Person P; public Mind M; public Situation S; public uint Seed; int _n;
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
            else sb.Append(bad ? v.Pk("Look around. What do you think?", "Honestly? Not like this. Something's gotta change.", "No. Not unless something changes. Soon.") : v.Pk("Nobody makes it. Not really. We just last a little longer.", "Make it to what? There's no finish line."));
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

        static string Clean(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s) sb.Append(ch < 32 || ch > 126 ? ' ' : ch);
            return Regex.Replace(sb.ToString(), "  +", " ").Trim();
        }
    }
}
