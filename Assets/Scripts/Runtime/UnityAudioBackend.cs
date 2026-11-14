// Real IAudioBackend for Unity: a small pool of 2D AudioSources playing clips from
// Assets/Resources/Sfx/<name>.wav (copied from the Godot project's assets/sfx).
// A missing clip is simply reported as absent, so Sfx skips it and nothing plays.
using System;
using System.Collections.Generic;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed class UnityAudioBackend : IAudioBackend
    {
        public const string CLIP_DIR = "Sfx/";
        const int VOICES = 10;

        readonly AudioSource[] _voices;
        readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>();
        int _next;

        public UnityAudioBackend(GameObject host)
        {
            _voices = new AudioSource[VOICES];
            for (int i = 0; i < VOICES; i++)
            {
                var src = host.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.loop = false;
                src.spatialBlend = 0f; // 2D; positional culling/attenuation is done by Sfx
                _voices[i] = src;
            }
        }

        AudioClip Clip(string name)
        {
            if (_clips.TryGetValue(name, out var c)) return c;
            try { c = Resources.Load<AudioClip>(CLIP_DIR + name); }
            catch (Exception e) { Debug.LogWarning("sfx: could not load " + name + ": " + e.Message); c = null; }
            _clips[name] = c;
            return c;
        }

        public bool HasClip(string name) => Clip(name) != null;

        public bool PlayOneShot(string name, float volumeDb, float pitch)
        {
            var clip = Clip(name);
            if (clip == null) return false;
            for (int k = 0; k < VOICES; k++)
            {
                int i = (_next + k) % VOICES;
                var v = _voices[i];
                if (v == null || v.isPlaying) continue;
                _next = (i + 1) % VOICES;
                v.clip = clip;
                v.pitch = pitch;
                v.volume = Mathf.Clamp01(Mathf.Pow(10f, volumeDb / 20f));
                v.Play();
                return true;
            }
            return false; // every voice busy (Godot dropped the sound too)
        }

        public void SetMaster(bool enabled, float volume)
        {
            AudioListener.pause = false;
            AudioListener.volume = enabled ? Mathf.Clamp01(volume) : 0f;
        }
    }

    /// <summary>ISettingsStore on PlayerPrefs (Godot used user://settings.cfg).</summary>
    public sealed class PlayerPrefsSettingsStore : ISettingsStore
    {
        const string PREFIX = "dz.";
        static readonly string[] KEYS = { "audio/enabled", "audio/volume", "game/max_zombies", "game/show_tips", "graphics/mode_3d" };

        public bool Load(out Dictionary<string, object> values)
        {
            values = new Dictionary<string, object>();
            bool any = false;
            foreach (var k in KEYS)
            {
                if (!PlayerPrefs.HasKey(PREFIX + k)) continue;
                values[k] = Parse(PlayerPrefs.GetString(PREFIX + k));
                any = true;
            }
            return any;
        }

        // Typed values so Sfx's Convert.ChangeType never parses a culture-dependent string.
        static object Parse(string s)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (bool.TryParse(s, out var b)) return b;
            if (int.TryParse(s, System.Globalization.NumberStyles.Integer, inv, out var i)) return i;
            if (double.TryParse(s, System.Globalization.NumberStyles.Float, inv, out var d)) return d;
            return s;
        }

        public void Save(Dictionary<string, object> values)
        {
            foreach (var kv in values)
                PlayerPrefs.SetString(PREFIX + kv.Key, Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture));
            PlayerPrefs.Save();
        }
    }
}
