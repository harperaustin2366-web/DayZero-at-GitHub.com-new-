// E5 autosave + save slots. Slot 0 (AUTO) is the existing save (Game.SAVE_PATH, what CONTINUE loads): written by the
// 30 s autosave, at every dawn (RunStats) and when the app is paused or closed (Bootstrap). Slots 1-3 are manual and
// use the same save code and JSON format, only another file name. A tiny sidecar "<file>.meta" carries what the slot
// card shows (day, colony size, colony name, difficulty) so the list never has to parse a whole save.
using System;
using DayZero.Core;
using Newtonsoft.Json.Linq;

namespace DayZero.Runtime
{
    public static class SaveSlots
    {
        public const int MANUAL = 3;
        /// <summary>SaveCount after the last save written through this class (RunStats skips its auto meta then).</summary>
        public static int LastWriter = -1;

        public sealed class Info
        {
            public bool Has;
            public int Day, Pop, Diff = -1;
            public string Colony = "";
            public long Time;          // unix seconds of the newest copy
            public bool Expanded;
        }

        public static string SlotPath(int slot) => slot <= 0 ? Game.SAVE_PATH : "dayzero_v4_slot" + slot + ".json";
        public static string SlotName(int slot) => slot <= 0 ? "AUTOSAVE" : "SLOT " + slot;

        static ISaveStore Store(Game g) => g.Store ??= new FileSaveStore(Game.DefaultSaveDir);

        /// <summary>A game that can be written right now (solo, past the start pick, not over).</summary>
        public static bool CanSave(Game g) => g?.S != null && !g.Mp && !g.IsClient && g.S.Phase != "pick" && g.S.Phase != "lost" && g.C != null && g.C.Started;

        public static Info Read(Game g, int slot)
        {
            var info = new Info();
            if (g == null) return info;
            string path = SlotPath(slot);
            try
            {
                info.Has = g.HasSave(path);
                if (!info.Has) return info;
                info.Time = g.SaveTime(path);
                var st = Store(g);
                if (st.Exists(path + ".meta"))
                {
                    var m = JObject.Parse(st.ReadAllText(path + ".meta"));
                    info.Day = (int?)m["day"] ?? 0; info.Pop = (int?)m["pop"] ?? 0; info.Diff = (int?)m["diff"] ?? -1;
                    info.Colony = (string)m["colony"] ?? ""; info.Expanded = (bool?)m["exp"] ?? false;
                }
            }
            catch (Exception e) { UnityEngine.Debug.LogWarning("slots: " + e.Message); }
            return info;
        }

        public static void WriteMeta(Game g, string path)
        {
            if (g?.S == null || g.C == null) return;
            try
            {
                int pop = 0;
                foreach (var p in g.C.People) if (p.St != "dead") pop++;
                var m = new JObject
                {
                    ["day"] = g.Day(), ["pop"] = pop, ["colony"] = g.C.Name ?? "", ["diff"] = g.S.Difficulty, ["exp"] = g.S.ExpRules,
                    ["t"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                };
                Store(g).WriteAllText(path + ".meta", m.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch (Exception e) { UnityEngine.Debug.LogWarning("slots meta: " + e.Message); }
        }

        static bool SaveTo(Game g, string path)
        {
            if (!CanSave(g)) return false;
            RunStats.Instance.Flush();
            if (!g.SaveGame(path)) return false;
            LastWriter = g.SaveCount;
            WriteMeta(g, path);
            return true;
        }

        /// <summary>The AUTO slot (dawn, app pause / quit).</summary>
        public static bool SaveAuto(Game g) => g != null && g.AutosaveOn && SaveTo(g, SlotPath(0));

        public static bool Save(Game g, int slot) => SaveTo(g, SlotPath(slot));

        public static void Delete(Game g, int slot)
        {
            if (g == null || slot <= 0) return;
            try { g.DeleteSave(SlotPath(slot)); Store(g).Delete(SlotPath(slot) + ".meta"); }
            catch (Exception e) { UnityEngine.Debug.LogWarning("slots delete: " + e.Message); }
        }

        public static string When(long unix)
        {
            if (unix <= 0) return "";
            try { return DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("MMM d yyyy, HH:mm", System.Globalization.CultureInfo.InvariantCulture); }
            catch { return ""; }
        }
    }
}
