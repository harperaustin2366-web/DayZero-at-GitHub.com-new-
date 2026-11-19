// Port of scripts/core/sfx.gd (autoload "Sfx"): settings (audio, max zombies, tips, 3D) and
// one-shot sound playback with per-sound cooldowns and listener culling.
// Audio and settings storage sit behind interfaces so this compiles and runs without assets:
//   IAudioBackend   - Unity: an AudioSource pool + AudioClips from Resources/sfx (not in Core)
//   ISettingsStore  - Unity: PlayerPrefs or a file; Godot used user://settings.cfg
// Hook it to the game with: game.SfxRequested += (n, at) => sfx.Play(n, at);
using System;
using System.Collections.Generic;
using UnityEngine;

namespace DayZero.Core
{
    public interface IAudioBackend
    {
        /// <summary>Whether a clip with this name exists (Godot: load("res://assets/sfx/NAME.wav")).</summary>
        bool HasClip(string name);
        /// <summary>Play once on a free voice. Return false if every voice is busy.</summary>
        bool PlayOneShot(string name, float volumeDb, float pitch);
        /// <summary>Master bus mute / volume (linear 0..1).</summary>
        void SetMaster(bool enabled, float volume);
    }

    public interface ISettingsStore
    {
        bool Load(out Dictionary<string, object> values);
        void Save(Dictionary<string, object> values);
    }

    /// <summary>No-op audio (headless / tests). Records what would have played.</summary>
    public sealed class NullAudio : IAudioBackend
    {
        public readonly List<string> Played = new List<string>();
        public bool HasClip(string name) => true;
        public bool PlayOneShot(string name, float volumeDb, float pitch) { Played.Add(name); return true; }
        public void SetMaster(bool enabled, float volume) { }
    }

    public sealed class Sfx
    {
        public static readonly string[] NAMES = { "groan1", "groan2", "groan3", "gunshot", "rifle", "footstep", "click", "open", "alarm", "hammer", "scream", "hit", "siren" };
        public static readonly Dictionary<string, double> COOLDOWN = new Dictionary<string, double>
        {
            ["gunshot"] = 0.09, ["rifle"] = 0.12, ["groan"] = 1.2, ["scream"] = 1.5, ["alarm"] = 4.0, ["siren"] = 6.0,
            ["hammer"] = 0.8, ["hit"] = 0.15, ["click"] = 0.03, ["open"] = 0.05, ["footstep"] = 0.2,
        };
        const int VOICES = 10;

        public bool Enabled = true;
        public double Volume = 0.8;
        public int MaxZombies = 1100;
        public bool ShowTips = true;
        public bool Graphics3D = true;
        /// <summary>Visible map area in tiles; sounds placed outside it (grown by 25%) are skipped.</summary>
        public Rect ListenerRect;

        readonly IAudioBackend _audio;
        readonly ISettingsStore _store;
        readonly IRng _rng;
        readonly Func<double> _clock;
        readonly HashSet<string> _streams = new HashSet<string>();
        readonly Dictionary<string, double> _last = new Dictionary<string, double>();

        /// <param name="clock">Seconds (Godot: Time.get_ticks_msec() / 1000). Defaults to Gd.TicksMsec.</param>
        public Sfx(IAudioBackend audio, ISettingsStore store = null, IRng rng = null, Func<double> clock = null)
        {
            _audio = audio ?? new NullAudio();
            _store = store;
            _rng = rng ?? new GdRng((ulong)DateTime.UtcNow.Ticks);
            _clock = clock ?? (() => Gd.TicksMsec() / 1000.0);
            foreach (var n in NAMES) if (_audio.HasClip(n)) _streams.Add(n);
            LoadSettings();
        }

        public void LoadSettings()
        {
            if (_store != null && _store.Load(out var v))
            {
                Enabled = Get(v, "audio/enabled", true);
                Volume = Get(v, "audio/volume", 0.8);
                MaxZombies = Get(v, "game/max_zombies", 1100);
                ShowTips = Get(v, "game/show_tips", true);
                Graphics3D = Get(v, "graphics/mode_3d", true);
            }
            Apply();
        }

        static T Get<T>(Dictionary<string, object> v, string k, T d)
        {
            if (!v.TryGetValue(k, out var o) || o == null) return d;
            try { return (T)Convert.ChangeType(o, typeof(T)); } catch { return d; }
        }

        public void SaveSettings()
        {
            _store?.Save(new Dictionary<string, object>
            {
                ["audio/enabled"] = Enabled, ["audio/volume"] = Volume, ["game/max_zombies"] = MaxZombies,
                ["game/show_tips"] = ShowTips, ["graphics/mode_3d"] = Graphics3D,
            });
            Apply();
        }

        void Apply() => _audio.SetMaster(Enabled, (float)Math.Max(0.001, Volume));

        /// <summary>sfx.gd play(name, at, vol_db). at = (-1,-1) for a non-positional sound.</summary>
        public void Play(string name, Vector2 at, float volDb = 0f)
        {
            if (!Enabled) return;
            string key = name;
            if (name == "groan") name = new[] { "groan1", "groan2", "groan3" }[_rng.RandiRange(0, 2)];
            if (!_streams.Contains(name)) return;
            double now = _clock();
            double cd = COOLDOWN.TryGetValue(key, out var c) ? c : 0.1;
            if (now - (_last.TryGetValue(key, out var l) ? l : -10.0) < cd) return;
            if (at.x >= 0f && ListenerRect.width > 0f)
            {
                var grown = Gd.Grow(ListenerRect, ListenerRect.width * 0.25f);
                if (!Gd.Has(grown, at)) return;
                volDb -= (float)V.Clamp((ListenerRect.width - 40.0) / 20.0, 0.0, 10.0);
            }
            _last[key] = now;
            float pitch = 0.9f + _rng.Randf() * 0.2f;
            _audio.PlayOneShot(name, volDb, pitch);
        }

        public void Play(string name) => Play(name, new Vector2(-1, -1));

        /// <summary>v0.19 UI cue (day-start sting): a non-positional one-shot at a fixed pitch, no cooldown, no RNG.</summary>
        public void PlayTone(string name, float pitch, float volDb = 0f)
        {
            if (!Enabled || !_streams.Contains(name)) return;
            _audio.PlayOneShot(name, volDb, pitch);
        }
    }
}
