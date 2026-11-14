// Loads JSON data. Primary source: Assets/Resources/<path>.json via Resources.Load<TextAsset>
// (works identically on Android/iOS/desktop, no jar: URL handling needed).
// Fallback: Assets/StreamingAssets/<path>.json (File IO on desktop, UnityWebRequest for
// jar:/http URLs on Android). Must be called from the main thread.
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace DayZero.Core
{
    public static class DataLoader
    {
        /// <param name="path">Resource-relative path without extension, e.g. "Data/research", "Map/city".</param>
        public static string LoadText(string path)
        {
            var ta = Resources.Load<TextAsset>(path);
            if (ta != null) return ta.text;

            string full = Path.Combine(Application.streamingAssetsPath, path + ".json");
            if (full.Contains("://"))
            {
                // Android: StreamingAssets live inside the APK (jar:file://...).
                using (var req = UnityWebRequest.Get(full))
                {
                    var op = req.SendWebRequest();
                    while (!op.isDone) { } // synchronous wait; acceptable at boot for small files
                    if (req.result == UnityWebRequest.Result.Success) return req.downloadHandler.text;
                }
            }
            else if (File.Exists(full))
            {
                return File.ReadAllText(full);
            }
            Debug.LogError("missing data file " + path);
            return null;
        }

        /// <summary>Equivalent of data.gd _load(): returns {} on a missing file.
        /// NOTE: Godot's JSON parser yields every number as float; Newtonsoft keeps integers as long.
        /// Callers convert explicitly, so this only matters if code type-checks values.</summary>
        public static JToken Load(string path)
        {
            string text = LoadText(path);
            if (text == null) return new JObject();
            return JToken.Parse(text);
        }
    }
}
