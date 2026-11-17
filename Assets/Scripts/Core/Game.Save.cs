// game.gd: save / load (JSON via Newtonsoft instead of Godot store_var), autosave, and the
// state snapshots the multiplayer layer uses (serialize / net_colony / apply_state, zombie packing,
// client-side interpolation).
//
// SAVE FORMAT: a JSON object { "format": SAVE_FORMAT, "ver": 17, ...GameState (snake_case)... }.
// Godot saves (binary store_var, versions 4..17) cannot be read: the v4..v16 migration chain was
// not ported because nothing can produce those dictionaries outside Godot.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using UnityEngine;

namespace DayZero.Core
{
    /// <summary>File access for saves (Godot user://). Paths are relative names like "dayzero_v4.json".</summary>
    public interface ISaveStore
    {
        bool Exists(string path);
        string ReadAllText(string path);
        void WriteAllText(string path, string text);
        void Delete(string path);
        void Move(string from, string to);
        /// <summary>Last-modified time in seconds (only compared, never displayed).</summary>
        long ModifiedTime(string path);
    }

    /// <summary>ISaveStore over a directory (Unity: Application.persistentDataPath).</summary>
    public sealed class FileSaveStore : ISaveStore
    {
        readonly string _root;
        public FileSaveStore(string root) { _root = root; Directory.CreateDirectory(root); }
        string P(string p) => Path.Combine(_root, p);
        public bool Exists(string path) => File.Exists(P(path));
        public string ReadAllText(string path) => File.ReadAllText(P(path));
        public void WriteAllText(string path, string text) => File.WriteAllText(P(path), text);
        public void Delete(string path) { if (File.Exists(P(path))) File.Delete(P(path)); }
        public void Move(string from, string to) => File.Move(P(from), P(to));
        public long ModifiedTime(string path) => new DateTimeOffset(File.GetLastWriteTimeUtc(P(path))).ToUnixTimeSeconds();
    }

    public sealed class Vector2JsonConverter : JsonConverter
    {
        public override bool CanConvert(Type t) => t == typeof(Vector2) || t == typeof(Vector2?);
        public override void WriteJson(JsonWriter w, object value, JsonSerializer s)
        {
            if (value == null) { w.WriteNull(); return; }
            var v = (Vector2)value;
            w.WriteStartArray(); w.WriteValue(v.x); w.WriteValue(v.y); w.WriteEndArray();
        }
        public override object ReadJson(JsonReader r, Type t, object existing, JsonSerializer s)
        {
            var tok = JToken.Load(r);
            if (tok.Type == JTokenType.Null) return null;
            return new Vector2((float)tok[0], (float)tok[1]);
        }
    }

    public sealed class Vector2IntJsonConverter : JsonConverter
    {
        public override bool CanConvert(Type t) => t == typeof(Vector2Int) || t == typeof(Vector2Int?);
        public override void WriteJson(JsonWriter w, object value, JsonSerializer s)
        {
            if (value == null) { w.WriteNull(); return; }
            var v = (Vector2Int)value;
            w.WriteStartArray(); w.WriteValue(v.x); w.WriteValue(v.y); w.WriteEndArray();
        }
        public override object ReadJson(JsonReader r, Type t, object existing, JsonSerializer s)
        {
            var tok = JToken.Load(r);
            if (tok.Type == JTokenType.Null) return null;
            return new Vector2Int((int)tok[0], (int)tok[1]);
        }
    }

    public sealed partial class Game
    {
        public const string OLD_SAVE_PATH = "dayzero_v3.json";
        public const string OLD_MP_SAVE_PATH = "dayzero_v3_mp.json";
        public const double AUTOSAVE_SEC = 30.0;
        public double AutosaveT;
        public double LastSaveMs;
        public bool LastSaveOk;
        public int SaveCount;
        public bool SaveFailTest;
        public string LastLoadError = "";

        public static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver { NamingStrategy = new SnakeCaseNamingStrategy() },
            Converters = new List<JsonConverter> { new Vector2JsonConverter(), new Vector2IntJsonConverter() },
            NullValueHandling = NullValueHandling.Ignore,
            ReferenceLoopHandling = ReferenceLoopHandling.Error,
            FloatFormatHandling = FloatFormatHandling.DefaultValue,
        };
        static JsonSerializer _ser;
        static JsonSerializer Ser => _ser ??= JsonSerializer.Create(JsonSettings);

        /// <summary>Directory used when no Store is set. Unity hosts should set Store (or this) to
        /// Application.persistentDataPath.</summary>
        public static string DefaultSaveDir = "saves";
        ISaveStore StoreOrDefault => Store ??= new FileSaveStore(DefaultSaveDir);

        string OldPath(string path) => path == SAVE_PATH ? OLD_SAVE_PATH : (path == MP_SAVE_PATH ? OLD_MP_SAVE_PATH : "__none__");
        IEnumerable<string> SaveCands(string path) => new[] { path, path + ".tmp", path + ".bak", OldPath(path) };

        public bool HasSave(string path = SAVE_PATH) => SaveCands(path).Any(StoreOrDefault.Exists);

        public long SaveTime(string path = SAVE_PATH)
        {
            long t = 0;
            foreach (var p in SaveCands(path)) if (StoreOrDefault.Exists(p)) t = Math.Max(t, StoreOrDefault.ModifiedTime(p));
            return t;
        }

        /// <summary>serialize(include_z, net, with_cols): the whole state as a JObject.</summary>
        public JObject Serialize(bool includeZ = true, bool net = false, bool withCols = true)
        {
            if (includeZ)
            {
                S.Zc = Zc.ToArray(); S.Zn = Zn.ToArray(); S.Zp = Zp.ToArray(); S.Zh = Zh.ToArray();
            }
            var d = JObject.FromObject(S, Ser);
            S.Zc = null; S.Zn = null; S.Zp = null; S.Zh = null;
            d["format"] = SAVE_FORMAT;
            if (!withCols) d.Remove("cols");
            else
            {
                var cols = (JArray)d["cols"];
                for (int ci = 0; ci < cols.Count; ci++)
                {
                    var ppl = (JArray)cols[ci]["people"];
                    for (int i = 0; i < ppl.Count; i++)
                    {
                        var q = (JObject)ppl[i];
                        if (net) { q.Remove("carry"); q.Remove("home"); q.Remove("work"); q.Remove("wl"); }
                        else if ((string)q["st"] == "move") q["resume"] = true;
                    }
                }
            }
            if (net)
            {
                d.Remove("loot"); d.Remove("srch"); d.Remove("srch_c");
                var lz = new byte[SW * SH];
                for (int i = 0; i < SW * SH; i++) lz[i] = (byte)(int)(V.Clamp(S.Zinf[i], 0.0, 1.0) * 255.0);
                d["zinf"] = lz;
                d["active"] = Active;
                var hq = new byte[SW * SH];
                for (int i = 0; i < SW * SH; i++) hq[i] = (byte)V.Clampi((int)S.CtrlH[i], 0, 255);
                d["ctrl_h"] = hq;
                d.Remove("scout");
            }
            return d;
        }

        public static readonly string[] NET_SUM_DROP = { "trade", "cd", "res", "parts", "weapons", "kit", "craft", "vehs", "nraid", "auto", "auto_saved", "pacts", "next_arrival_t", "next_event_t", "spawn" };
        public static readonly string[] NET_LEAN_DROP = { "path", "carry", "home", "work", "wl", "rel", "parents", "pnames", "kids", "preg", "preg_by", "last_birth", "lost" };
        public static readonly string[] NET_COLD = { "rel", "parents", "pnames", "kids", "last_birth", "preg_by", "name", "sex", "born", "lost", "known", "kills", "tf", "pi", "seg", "cd", "partner", "preg", "stage", "wpn", "vest", "helm", "base", "lot", "goal", "tgt" };
        public const int SIGHT_BASE_SECT = 5;
        public const int SIGHT_PERSON_SECT = 2;

        /// <summary>net_colony: one colony for a network snapshot (full, or a lean summary limited to seen sectors).</summary>
        public JObject NetColony(int ci, bool full, byte[] seen = null)
        {
            var c = S.Cols[ci];
            var c2 = JObject.FromObject(c, Ser);
            var ppl = new JArray();
            int alive = 0;
            foreach (var p in c.People)
            {
                if (p.St != "dead") alive++;
                if (!full)
                {
                    if (p.St == "dead" || seen == null || seen.Length == 0) continue;
                    int sx = V.Clampi((int)p.Pos.x / SECT, 0, SW - 1), sy = V.Clampi((int)p.Pos.y / SECT, 0, SH - 1);
                    if (seen[sy * SW + sx] == 0) continue;
                }
                var q = JObject.FromObject(p, Ser);
                q.Remove("carry"); q.Remove("home"); q.Remove("work"); q.Remove("wl");
                if (!full) foreach (var k in NET_LEAN_DROP) q.Remove(k);
                ppl.Add(q);
            }
            c2["people"] = ppl;
            if (!full)
            {
                c2["_sum"] = true;
                c2["npop"] = alive;
                foreach (var k in NET_SUM_DROP) c2.Remove(k);
                c2["research"] = new JObject { ["done"] = JArray.FromObject(c.Research.Done), ["cur"] = "", ["prog"] = new JObject() };
            }
            return c2;
        }

        public byte[] SeenSectors(int ci, Rect? cam = null)
        {
            var m = new byte[SW * SH];
            var c = ColonyAt(ci);
            void Mark(Vector2 pos, int r)
            {
                int sx = (int)pos.x / SECT, sy = (int)pos.y / SECT;
                for (int y = Math.Max(0, sy - r); y < Math.Min(SH, sy + r + 1); y++)
                    for (int x = Math.Max(0, sx - r); x < Math.Min(SW, sx + r + 1); x++) m[y * SW + x] = 1;
            }
            if (c != null)
            {
                foreach (var b in c.Bases) Mark(V.F(b.Pos), SIGHT_BASE_SECT);
                foreach (var p in c.People) if (p.St != "base" && p.St != "dead") Mark(p.Pos, SIGHT_PERSON_SECT);
            }
            if (cam.HasValue && cam.Value.width > 0f)
            {
                var r = Gd.Grow(cam.Value, 8f);
                for (int y = Math.Max(0, (int)r.y / SECT); y < Math.Min(SH, (int)(r.y + r.height) / SECT + 1); y++)
                    for (int x = Math.Max(0, (int)r.x / SECT); x < Math.Min(SW, (int)(r.x + r.width) / SECT + 1); x++) m[y * SW + x] = 1;
            }
            return m;
        }

        public bool SaveGame(string path = "")
        {
            if (path == "") path = Mp ? MP_SAVE_PATH : SAVE_PATH;
            if (IsClient) return false;
            if (S == null || S.Phase == "pick" || S.Phase == "lost") return false;
            long t0 = Gd.TicksMsec();
            FlushHourQ();
            string text;
            try { text = Serialize(true).ToString(Formatting.None); }
            catch (Exception e) { Debug.LogWarning("save: serialize failed: " + e.Message); LastSaveOk = false; return false; }
            var st = StoreOrDefault;
            string tmp = path + ".tmp";
            try { st.WriteAllText(tmp, text); }
            catch (Exception e) { Debug.LogWarning(string.Format("save: can't write {0} ({1})", tmp, e.Message)); LastSaveOk = false; return false; }
            if (text.Length < 64) { LastSaveOk = false; return false; }
            if (SaveFailTest) return false;
            try
            {
                if (st.Exists(path))
                {
                    if (st.Exists(path + ".bak")) st.Delete(path + ".bak");
                    st.Move(path, path + ".bak");
                }
                st.Move(tmp, path);
            }
            catch (Exception e) { Debug.LogWarning("save: rename failed: " + e.Message); LastSaveOk = false; return false; }
            LastSaveMs = Gd.TicksMsec() - t0;
            LastSaveOk = true;
            SaveCount++;
            AutosaveT = 0.0;
            return true;
        }

        (string path, JObject d) ReadSave(string path)
        {
            var st = StoreOrDefault;
            var cands = new List<(long pri, string p)>();
            int pri = 4;
            foreach (var p in SaveCands(path))
            {
                pri--;
                if (st.Exists(p)) cands.Add((st.ModifiedTime(p) * 4 + pri, p));
            }
            Gd.StableSort(cands, (a, b) => b.pri.CompareTo(a.pri));
            foreach (var c in cands)
            {
                string txt;
                try { txt = st.ReadAllText(c.p); } catch { continue; }
                if (txt == null || txt.Length < 64) continue;
                JObject d = null;
                try { d = JObject.Parse(txt); }
                catch (JsonException)
                {
                    LastLoadError = string.Format("{0} is not a JSON save (Godot store_var saves can't be loaded by this build).", c.p);
                    Debug.LogWarning("save: " + LastLoadError);
                    continue;
                }
                if (d["cols"] != null && d["ver"] != null && (string)d["format"] == SAVE_FORMAT) return (c.p, d);
                Debug.LogWarning(string.Format("save: {0} is damaged or not a {1} save, trying the next copy", c.p, SAVE_FORMAT));
            }
            return ("", null);
        }

        void AutosaveTick(double delta)
        {
            AutosaveT += delta;
            if (AutosaveT < AUTOSAVE_SEC) return;
            AutosaveT = 0.0;
            SaveGame();
        }

        public bool LoadGame(string path = "")
        {
            if (path == "") path = Mp ? MP_SAVE_PATH : SAVE_PATH;
            if (!HasSave(path)) return false;
            var rs = ReadSave(path);
            if (rs.d == null) return false;
            if ((int)rs.d["ver"] != SAVE_VERSION) { LastLoadError = "Unsupported save version " + (int)rs.d["ver"]; return false; }
            GameState d;
            try { d = rs.d.ToObject<GameState>(Ser); }
            catch (Exception e) { LastLoadError = "Damaged save: " + e.Message; Debug.LogWarning("load: " + LastLoadError); return false; }
            return LoadState(d);
        }

        /// <summary>Install a deserialized GameState (shared by LoadGame and tests).</summary>
        public bool LoadState(GameState d)
        {
            D.SetWorldSeed(d.Seed);
            foreach (var c in d.Cols) { GearInit(c); if (c.Res != null) FixRes(c.Res); }
            if (d.Loot == null || d.Loot.Length != D.Lots.Count) FitToMap(d);
            Zc = new List<int>(d.Zc ?? new int[0]); Zn = new List<int>(d.Zn ?? new int[0]);
            Zp = new List<float>(d.Zp ?? new float[0]); Zh = new List<byte>(d.Zh ?? new byte[0]);
            d.Zc = null; d.Zn = null; d.Zp = null; d.Zh = null;
            S = d;
            ExpansionRules = S.ExpRules;
            CurCol = -1;
            ViewCol = V.Clampi(ViewCol, 0, S.Cols.Count - 1);
            Rng.SetSeed(S.Seed + (long)(S.T * 7.0));
            Frng.SetSeed((S.Seed ^ 24301) + (long)(S.T * 7.0));
            Wrng.SetSeed((S.Seed ^ 14866) + (long)(S.T * 7.0));
            _peopleById.Clear(); _claimed.Clear(); _baseLots.Clear(); _fxCache.Clear(); _encl.Clear(); _enclVer = -1; _claimVer = -1; _claimSig = 0;
            _seedCache.Clear(); _tileSegVer = -1;
            _convCacheVer = -1; _flowBuilt = false; _hourQ.Clear();
            foreach (var e in S.Log) _logN = Math.Max(_logN, e.N);
            EnsureKeys(S);
            EnsureFamily(S);
            EnsureRoutine(S);
            if (S.Srch == null || S.Srch.Length != D.Lots.Count) { S.Srch = SrchNew(); S.SrchC = SrchCNew(); }
            _factionLotsDirty = true;
            for (int i = 0; i < S.Camps.Count; i++)
                if (!S.Camps[i].Fi.HasValue) FactionInit(S.Camps[i], i);
            foreach (var c in S.Cols)
                foreach (var p in c.People)
                {
                    p.Path = new List<Vector2>();
                    p.Veh = false; p.Vlead = false;
                    _peopleById[p.Id] = p;
                }
            for (int i = 0; i < S.Cols.Count; i++)
            {
                Use(i);
                foreach (var p in C.People.ToList())
                {
                    if (p.Resume)
                    {
                        p.Resume = false;
                        p.St = "base";
                        if (!Go(p, p.Tgt, p.Goal)) p.St = "base";
                    }
                    else if (p.St == "work") p.Wt = Math.Min(p.Wt, 1.0);
                }
            }
            Use(ViewCol);
            EnsureWalls();
            _wInf = new float[SW * SH];
            ResetWeights();
            UpdateWeights();
            RebuildSafe();
            UpdateActive();
            if (S.Phase == "fall") BuildFlow();
            RebuildBuckets();
            Running = S.Phase == "spread" || S.Phase == "fall";
            EmitChanged();
            return true;
        }

        void EnsureKeys(GameState d)
        {
            d.Conv ??= new ODict<int, Conversion>(); d.Gates ??= new ODict<int, int>(); d.Traps ??= new ODict<int, Trap>();
            d.Gut ??= new ODict<int, int>(); d.Guts ??= new ODict<int, GutJob>(); d.Fsegs ??= new ODict<int, FenceSeg>();
            d.Wrecks ??= new List<Wreck>(); d.Players ??= new ODict<string, PlayerInfo>();
            d.Ctrl ??= new byte[SW * SH]; d.CtrlH ??= new float[SW * SH]; d.Scout ??= new float[SW * SH]; d.Zinf ??= new float[SW * SH];
            foreach (var c in d.Cols)
            {
                c.Nraid ??= RaidNew();
                c.War ??= WarNew();
                c.Auto ??= AutoNew();
                c.Vehs ??= new List<Vehicle>();
                c.Research ??= new ResearchState();
                c.Cd ??= new ODict<string, double>();
            }
            foreach (var cp in d.Camps) cp.Owed ??= new ODict<string, double>();
        }

        void FitToMap(GameState d)
        {
            var loot = new float[D.Lots.Count]; Array.Fill(loot, 1f);
            d.Loot = loot;
            d.Srch = SrchNew(); d.SrchC = SrchCNew();
            foreach (int k in d.Fences.KeyList()) if (!D.Walkable(Tp(k))) d.Fences.Remove(k);
            foreach (int k in d.Towers.KeyList()) if (!D.Walkable(Tp(k))) d.Towers.Remove(k);
            foreach (var c in d.Cols)
            {
                foreach (var b in c.Bases) b.Pos = D.NearestWalkable(b.Pos, 12);
                foreach (var p in c.People)
                {
                    var w = D.NearestWalkable(V.I(p.Pos), 12);
                    p.Pos = V.F(w).Add(V.HALF);
                    p.Lot = -1;
                    if (p.St == "work") p.St = "base";
                    p.Resume = false;
                    if (p.St == "move") p.St = "base";
                }
            }
            foreach (var g in d.Groups) { g.Pos = D.NearestWalkable(g.Pos, 12); g.Lot = Math.Max(0, D.LotAt(g.Pos)); }
            foreach (var c in d.Camps) { c.Pos = D.NearestWalkable(c.Pos, 12); c.Lot = Math.Max(0, D.LotAt(c.Pos)); }
            var zc2 = new List<int>(); var zh2 = new List<byte>();
            for (int i = 0; i < (d.Zc?.Length ?? 0); i++)
            {
                int k = d.Zc[i];
                if (k >= 0 && k < D.W * D.H && D.Walkable(Tp(k))) { zc2.Add(k); zh2.Add(d.Zh[i]); }
            }
            d.Zc = zc2.ToArray(); d.Zn = zc2.ToArray(); d.Zp = new float[zc2.Count]; d.Zh = zh2.ToArray();
            d.FenceVer++;
        }

        public void DeleteSave(string path = SAVE_PATH)
        {
            foreach (var p in SaveCands(path)) StoreOrDefault.Delete(p);
        }

        // ---------------- network client side ----------------
        public const double CLIENT_SNAP_TILES = 40.0;
        public const double CLIENT_GLIDE_S = 0.22;
        List<Vector2> _czFrom = new List<Vector2>(), _czTo = new List<Vector2>();
        double _czT, _czDur = 0.25;

        /// <summary>apply_state: install a host snapshot (from Serialize(net:true) + NetColony) on a client.
        /// Operates on the JSON form; see PORT_NOTES for the keys the host may send.</summary>
        public void ApplyState(JObject d, int myCol)
        {
            string oldPhase = S?.Phase ?? "";
            if (d["seed"] != null) D.SetWorldSeed((int)d["seed"]);
            if (d["zinf"] != null && d["zinf"].Type == JTokenType.String)
            {
                var lz = Convert.FromBase64String((string)d["zinf"]);
                d["zinf"] = JArray.FromObject(lz.Select(b => b / 255.0f).ToArray());
            }
            if (d["active"] != null && d["active"].Type == JTokenType.String) { Active = Convert.FromBase64String((string)d["active"]); d.Remove("active"); }
            if (d["ctrl_h"] != null && d["ctrl_h"].Type == JTokenType.String)
            {
                var hq = Convert.FromBase64String((string)d["ctrl_h"]);
                d["ctrl_h"] = JArray.FromObject(hq.Select(b => (float)b).ToArray());
            }
            if (d["scout"] == null) d["scout"] = JArray.FromObject(new float[SW * SH]);
            var loot = S?.Loot;
            if (loot == null || loot.Length != D.Lots.Count) { loot = new float[D.Lots.Count]; Array.Fill(loot, 1f); }
            if (d["loot_q"] != null)
            {
                var lq = Convert.FromBase64String((string)d["loot_q"]);
                for (int i = 0; i < Math.Min(lq.Length, loot.Length); i++) loot[i] = lq[i] / 255.0f;
                d.Remove("loot_q");
            }
            var sr = S?.Srch; var sc = S?.SrchC;
            if (sr == null || sr.Length != D.Lots.Count) { sr = SrchNew(); sc = SrchCNew(); }
            if (d["srch_q"] != null)
            {
                var sq = Convert.FromBase64String((string)d["srch_q"]);
                for (int i = 0; i < Math.Min(sq.Length, sr.Length); i++) { sc[i] = sq[i]; sr[i] = sq[i] > 0 ? 0f : -1f; }
                d.Remove("srch_q");
            }
            d.Remove("loot"); d.Remove("srch"); d.Remove("srch_c");
            _factionLotsDirty = true;
            _convCacheVer = -1;
            var keepPos = _peopleById.Values.ToDictionary(p => p.Id, p => p.Pos);
            JObject old = S != null ? JObject.FromObject(S, Ser) : new JObject();
            if (d["_keep"] is JArray keep)
            {
                foreach (var kt in keep)
                {
                    string k = (string)kt;
                    if (k == "active" || k == "loot_q" || k == "srch_q") continue;
                    if (old[k] != null) d[k] = old[k];
                    else if (k == "camps" || k == "groups" || k == "log") d[k] = new JArray();
                    else if (k == "zinf" || k == "ctrl_h") d[k] = JArray.FromObject(new float[SW * SH]);
                    else if (k == "ctrl") d[k] = new byte[SW * SH];
                    else d[k] = new JObject();
                }
                d.Remove("_keep");
            }
            if (d["log_add"] is JArray la)
            {
                var nl = d["log"] is JArray l0 ? new JArray(l0) : new JArray();
                foreach (var e in la) nl.Add(e);
                while (nl.Count > 80) nl.RemoveAt(0);
                d["log"] = nl;
                d.Remove("log_add");
            }
            var oldCols = old["cols"] as JArray ?? new JArray();
            var cols = (JArray)d["cols"];
            for (int i = 0; i < cols.Count; i++)
            {
                var c = (JObject)cols[i];
                var oc = i < oldCols.Count ? oldCols[i] as JObject : null;
                if (c["resb"] != null)
                {
                    var rd = new JObject();
                    var rb = c["resb"].ToObject<float[]>();
                    for (int j = 0; j < Math.Min(rb.Length, Data.BASIC.Length); j++) rd[Data.BASIC[j]] = rb[j];
                    if (c["resm"] != null)
                    {
                        var rm = c["resm"].ToObject<float[]>();
                        for (int j = 0; j < Math.Min(rm.Length, Data.MATS.Length); j++) rd[Data.MATS[j]] = rm[j];
                    }
                    else if (oc?["res"] is JObject ores)
                        foreach (var m in Data.MATS) rd[m] = ores[m] ?? 0.0;
                    c.Remove("resb"); c.Remove("resm"); c.Remove("_mkeep");
                    c["res"] = rd;
                }
                if (c["_gkeep"] != null && (bool)c["_gkeep"])
                {
                    c.Remove("_gkeep");
                    if (oc != null) foreach (var gk in new[] { "weapons", "kit", "craft", "vehs", "parts" }) if (oc[gk] != null) c[gk] = oc[gk];
                }
                if (c["ld"] is JObject ld)
                {
                    foreach (JObject p in (JArray)c["people"])
                    {
                        var e = ld[((int)p["id"]).ToString()] as JArray;
                        p["wpn"] = (string)e?[0] ?? ""; p["vest"] = (string)e?[1] ?? ""; p["helm"] = (string)e?[2] ?? "";
                    }
                    c.Remove("ld");
                }
                c.Remove("_lkeep");
                if (c["_tkeep"] != null && (bool)c["_tkeep"])
                {
                    c.Remove("_tkeep");
                    if (oc?["trade"] != null) c["trade"] = oc["trade"];
                }
                if (c["_same"] != null && (bool)c["_same"]) cols[i] = oc ?? new JObject();
                else if (c["_hot"] != null && (bool)c["_hot"])
                {
                    c.Remove("_hot");
                    var oldPeople = new Dictionary<int, JObject>();
                    foreach (var ocol in oldCols) foreach (JObject op in (JArray)ocol["people"]) oldPeople[(int)op["id"]] = op;
                    foreach (JObject p in (JArray)c["people"])
                        if (oldPeople.TryGetValue((int)p["id"], out var op))
                            foreach (var k in NET_COLD) if (p[k] == null && op[k] != null) p[k] = op[k];
                }
            }
            var ns = d.ToObject<GameState>(Ser);
            ns.Loot = loot; ns.Srch = sr; ns.SrchC = sc;
            S = ns;
            EnsureKeys(S);
            for (int i = 0; i < cols.Count; i++)
            {
                if (!(cols[i] is JObject cj) || cj["_sum"] == null) continue;
                var tmpl = NewColonyDict("");
                var c = S.Cols[i];
                if (cj["res"] == null) c.Res = tmpl.Res;
                if (cj["kit"] == null) c.Kit = tmpl.Kit;
                if (cj["weapons"] == null) c.Weapons = tmpl.Weapons;
                if (cj["parts"] == null) c.Parts = tmpl.Parts;
            }
            EnsureFamily(S);
            EnsureRoutine(S);
            foreach (var c in S.Cols) if (c.Res != null) FixRes(c.Res);
            CurCol = -1;
            ViewCol = V.Clampi(myCol, 0, S.Cols.Count - 1);
            _peopleById.Clear();
            foreach (var c in S.Cols)
                foreach (var p in c.People)
                {
                    p.Path = new List<Vector2>();
                    p.Carry ??= new ODict<string, double>();
                    if (keepPos.TryGetValue(p.Id, out var kp) && (p.St != "base" || (!IsInside(p) && kp.DistTo(p.Pos) < CLIENT_SNAP_TILES)))
                    {
                        var tgt = p.Pos;
                        p.Pos = kp;
                        SetClientTarget(p, tgt, CLIENT_GLIDE_S);
                    }
                    _peopleById[p.Id] = p;
                }
            Use(ViewCol);
            RebuildSafe();
            if (oldPhase != S.Phase) PhaseChanged?.Invoke(S.Phase);
            EmitChanged();
        }

        public void ApplyZombies(byte[] buf, double dur, Vector2Int? originArg = null)
        {
            var origin = originArg ?? V.NONE;
            bool compact = origin.x >= 0;
            int es = compact ? 3 : 4;
            int n = buf.Length / es;
            var to = new List<Vector2>(n);
            var mode = new List<byte>(n);
            for (int i = 0; i < n; i++)
            {
                if (compact)
                {
                    int o = i * 3;
                    int v3 = buf[o] | (buf[o + 1] << 8) | (buf[o + 2] << 16);
                    to.Add(new Vector2(origin.x + (v3 & 1023) / 4.0f + 0.125f, origin.y + ((v3 >> 10) & 1023) / 4.0f + 0.125f));
                    mode.Add((byte)((v3 >> 20) & 1));
                    continue;
                }
                uint v = BitConverter.ToUInt32(buf, i * 4);
                to.Add(new Vector2((v & 4095) / 4.0f + 0.125f, ((v >> 12) & 4095) / 4.0f + 0.125f));
                mode.Add((byte)((v >> 24) & 1));
            }
            var cur = ClientCur();
            var from = new List<Vector2>(n);
            for (int i = 0; i < n; i++) from.Add(i < cur.Count && cur[i].Dist2To(to[i]) < 4.0f ? cur[i] : to[i]);
            _czFrom = from; _czTo = to; _czT = 0.0; _czDur = Math.Max(0.05, dur);
            Zc = new List<int>(n); Zn = new List<int>(n); Zp = new List<float>(n); Zh = mode;
            for (int i = 0; i < n; i++)
            {
                int k = Ti(new Vector2Int(V.Clampi((int)to[i].x, 0, D.W - 1), V.Clampi((int)to[i].y, 0, D.H - 1)));
                Zc.Add(k); Zn.Add(k); Zp.Add(0f);
            }
            RebuildBuckets();
        }

        List<Vector2> ClientCur()
        {
            var o = new List<Vector2>(_czTo.Count);
            float f = (float)V.Clamp(_czT / _czDur, 0.0, 1.0);
            for (int i = 0; i < _czTo.Count; i++) o.Add(_czFrom[i].LerpTo(_czTo[i], f));
            return o;
        }

        public void SetClientTarget(Person p, Vector2 to, double dur)
        {
            var cur = p.Pos;
            if (cur.DistTo(to) > CLIENT_SNAP_TILES)
            {
                p.Pos = to;
                p.CTo = null; p.CFrom = null;
                return;
            }
            p.CFrom = cur; p.CTo = to; p.CLt = 0.0; p.CLd = Math.Max(0.05, dur);
        }

        void ClientInterp(double delta)
        {
            _czT += delta;
            foreach (var p in _peopleById.Values)
            {
                if (!p.CTo.HasValue) continue;
                if (!p.CFrom.HasValue)
                {
                    p.Pos = p.Pos.LerpTo(p.CTo.Value, (float)Math.Min(1.0, delta * 6.0));
                    continue;
                }
                p.CLt += delta;
                p.Pos = p.CFrom.Value.LerpTo(p.CTo.Value, (float)Math.Min(1.0, p.CLt / p.CLd));
            }
        }

        public List<byte[]> PackZombiesMany(IList<Rect> views, IList<Vector2Int> origins)
        {
            int n = Zc.Count;
            var pos = new Vector2[n];
            var head = new int[SW * SH]; Array.Fill(head, -1);
            var nxt = new int[n];
            for (int i = 0; i < n; i++)
            {
                if (Zh[i] == 255) continue;
                var p = ZPos(i);
                pos[i] = p;
                int sct = V.Clampi((int)p.y / SECT, 0, SH - 1) * SW + V.Clampi((int)p.x / SECT, 0, SW - 1);
                nxt[i] = head[sct]; head[sct] = i;
            }
            var outs = new List<byte[]>();
            for (int vi = 0; vi < views.Count; vi++)
            {
                var r = Gd.Grow(views[vi], 6f);
                var origin = origins[vi];
                var idx = new List<int>();
                for (int sy = Math.Max(0, (int)r.y / SECT); sy < Math.Min(SH, (int)(r.y + r.height) / SECT + 1); sy++)
                    for (int sx = Math.Max(0, (int)r.x / SECT); sx < Math.Min(SW, (int)(r.x + r.width) / SECT + 1); sx++)
                    {
                        int i = head[sy * SW + sx];
                        while (i >= 0) { if (Gd.Has(r, pos[i])) idx.Add(i); i = nxt[i]; }
                    }
                idx.Sort();
                int es = origin.x >= 0 ? 3 : 4;
                var o = new byte[idx.Count * es];
                int w = 0;
                foreach (int i in idx) { PackOne(o, w, pos[i], Zh[i], origin, es); w += es; }
                outs.Add(o);
            }
            return outs;
        }

        public byte[] PackZombies(Rect view, Vector2Int? originArg = null)
        {
            var origin = originArg ?? V.NONE;
            var o = new List<byte>();
            var r = Gd.Grow(view, 6f);
            int es = origin.x >= 0 ? 3 : 4;
            var tmp = new byte[4];
            for (int i = 0; i < Zc.Count; i++)
            {
                if (Zh[i] == 255) continue;
                var p = ZPos(i);
                if (!Gd.Has(r, p)) continue;
                PackOne(tmp, 0, p, Zh[i], origin, es);
                for (int j = 0; j < es; j++) o.Add(tmp[j]);
            }
            return o.ToArray();
        }

        static void PackOne(byte[] o, int at, Vector2 p, byte zh, Vector2Int origin, int es)
        {
            int m = (zh == 1 || zh == 3) ? 1 : 0;
            if (es == 3)
            {
                int v3 = V.Clampi((int)((p.x - origin.x) * 4.0f), 0, 1023) | (V.Clampi((int)((p.y - origin.y) * 4.0f), 0, 1023) << 10) | (m << 20);
                o[at] = (byte)(v3 & 255); o[at + 1] = (byte)((v3 >> 8) & 255); o[at + 2] = (byte)((v3 >> 16) & 255);
            }
            else
            {
                int v = V.Clampi((int)(p.x * 4.0f), 0, 4095) | (V.Clampi((int)(p.y * 4.0f), 0, 4095) << 12) | (m << 24);
                o[at] = (byte)(v & 255); o[at + 1] = (byte)((v >> 8) & 255); o[at + 2] = (byte)((v >> 16) & 255); o[at + 3] = (byte)((v >> 24) & 255);
            }
        }
    }
}
