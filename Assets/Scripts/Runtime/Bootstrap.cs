// Boot entry point. Lives on the "Bootstrap" GameObject in Assets/Scenes/Boot.unity.
// If the scene wiring is broken (missing script reference, scene not in the build list),
// EnsureBootstrap() below creates the GameObject at runtime, so the game always starts.
//
// Owns the Game (core simulation), Sfx, the frame loop, saves, and the IMGUI front end (GameUI).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DayZero.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DayZero.Runtime
{
    [DisallowMultipleComponent]
    public sealed class Bootstrap : MonoBehaviour
    {
        public static Bootstrap Instance { get; private set; }

        public Game Game { get; private set; }
        public Sfx Sfx { get; private set; }
        /// <summary>Decisions waiting for the player (GDScript: the event popup queue).</summary>
        public readonly List<JObject> Pending = new List<JObject>();
        public readonly List<string> Toasts = new List<string>();
        float _toastT;

        public enum Screen2 { Loading, Title, Playing, Failed }
        public Screen2 State { get; private set; } = Screen2.Loading;
        public string Fatal = "";
        public bool Paused;
        /// <summary>Simulation speed: number of Process() calls per frame (1, 2 or 4).</summary>
        public int Speed = 1;
        public bool Ended;
        public bool Won;

        GameUI _ui;
        public GameUI UI => _ui;
        public MapView Map { get; private set; }

        // ---------------- fallback wiring ----------------
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureBootstrap()
        {
            if (Instance != null) return;
            Debug.Log("DayZero: no Bootstrap in the scene, creating one at runtime.");
            var go = new GameObject("Bootstrap (runtime)");
            go.AddComponent<Bootstrap>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            // Number formatting and parsing must not depend on the phone's locale.
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

            Application.targetFrameRate = 60;
            UnityEngine.Screen.sleepTimeout = SleepTimeout.SystemSetting;
            UnityEngine.Screen.autorotateToPortrait = false;
            UnityEngine.Screen.autorotateToPortraitUpsideDown = false;
            UnityEngine.Screen.autorotateToLandscapeLeft = true;
            UnityEngine.Screen.autorotateToLandscapeRight = true;
            UnityEngine.Screen.orientation = ScreenOrientation.AutoRotation;

            if (Camera.main == null && FindAnyCamera() == null)
            {
                var cgo = new GameObject("Main Camera");
                cgo.tag = "MainCamera";
                var cam = cgo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.07f, 0.08f, 0.08f, 1f);
                cam.orthographic = true;
                cgo.AddComponent<AudioListener>();
                DontDestroyOnLoad(cgo);
            }

            Game.DefaultSaveDir = Path.Combine(Application.persistentDataPath, "saves");
            _ui = new GameUI(this);
            Map = gameObject.AddComponent<MapView>();
        }

        static Camera FindAnyCamera() => Camera.allCamerasCount > 0 ? Camera.allCameras[0] : null;

        IEnumerator Start()
        {
            yield return null; // draw one "Loading" frame first; world generation takes a moment
            try
            {
                Game = new Game();
                Game.AutosaveOn = true;
                Game.DecisionAdded += ev => Pending.Add(ev);
                Game.GameOver += won => { Ended = true; Won = won; };
                Sfx = new Sfx(new UnityAudioBackend(gameObject), new PlayerPrefsSettingsStore());
                Game.SfxRequested += (n, at) => Sfx.Play(n, at);
                State = Screen2.Title;
            }
            catch (Exception e)
            {
                Fail("Could not load the game data.", e);
            }
        }

        void Fail(string what, Exception e)
        {
            Debug.LogException(e);
            Fatal = what + "\n\n" + e.GetType().Name + ": " + e.Message;
            State = Screen2.Failed;
        }

        public bool HasSave()
        {
            try { return Game != null && Game.HasSave(); } catch { return false; }
        }

        // ---------------- commands from the UI ----------------
        /// <summary>New game: build the world and show the map with no camp yet ("pick" phase). The player taps a
        /// building (MapView) and confirms (GameUI -> ConfirmStart), which founds the colony there.</summary>
        public void NewGame(int difficulty)
        {
            try
            {
                Pending.Clear(); Ended = false; Won = false; Paused = false; Speed = 1;
                Game.NewRun(difficulty, -1, 1);
                Game.Use(0);
                Game.Running = false;
                Map?.Invalidate();
                State = Screen2.Playing;
            }
            catch (Exception e) { Fail("Could not start a new game.", e); }
        }

        /// <summary>True while the new game waits for the player to choose the building for the camp.</summary>
        public bool Picking => State == Screen2.Playing && Game?.S != null && Game.C != null && !Game.C.Started && Game.S.Phase == "pick";

        /// <summary>Found the colony in the building selected on the map (Godot: DoAction "start_at" -> StartAt).</summary>
        public void ConfirmStart()
        {
            if (!Picking || Map == null || Map.SelectedLot < 0) return;
            try
            {
                Game.Use(0);
                string err = Game.StartAt(Map.SelectedTile);
                if (err != "") { Toast("Can't set up camp there: " + err); return; }
                Game.Use(0);
                Game.Running = true;
                Map.OnCampFounded();
                Game.SaveGame();
                Toast("Camp founded. Survive. Build. Defend. Expand.");
            }
            catch (Exception e) { Fail("Could not found the camp.", e); }
        }

        public void LoadSaved()
        {
            try
            {
                Pending.Clear(); Ended = false; Won = false; Paused = false;
                Map?.Invalidate();   // v0.12.0a: drop the old world's yard geometry before the save is installed
                if (!Game.LoadGame()) { Toast(Game.LastLoadError != "" ? Game.LastLoadError : "No save found."); return; }
                Map?.Invalidate();   // camp yards come from the loaded bases (the chosen start site is a saved Base)
                Game.Use(Math.Max(0, Game.ViewCol));
                Game.Running = true;
                Ended = Game.S.Phase == "lost";
                State = Screen2.Playing;
                Toast("Game loaded.");
            }
            catch (Exception e) { Fail("Could not load the save.", e); }
        }

        public void SaveNow()
        {
            if (Game == null || State != Screen2.Playing) return;
            try { Toast(Game.SaveGame() ? "Game saved." : "Save failed."); }
            catch (Exception e) { Debug.LogException(e); Toast("Save failed: " + e.Message); }
        }

        public void ToTitle()
        {
            if (Game != null && State == Screen2.Playing && !Ended) SaveQuiet();
            if (Game != null) Game.Running = false;
            Pending.Clear();
            State = Screen2.Title;
        }

        public void Answer(JObject ev, int choice)
        {
            Pending.Remove(ev);
            try
            {
                string outcome = Game.ApplyChoice(ev, choice);
                if (!string.IsNullOrEmpty(outcome)) Toast(outcome);
            }
            catch (Exception e) { Debug.LogException(e); Toast("Error: " + e.Message); }
        }

        /// <summary>Player command (GDScript Net.act). Returns "" on success or the reason it failed.</summary>
        public string Act(string action, params object[] args)
        {
            try
            {
                string r = Game.DoAction(Math.Max(0, Game.CurCol), action, args);
                Game.Use(Math.Max(0, Game.ViewCol));
                return r ?? "";
            }
            catch (Exception e) { Debug.LogException(e); return "Error: " + e.Message; }
        }

        /// <summary>Fast-forward a number of in-game hours (blocks for a moment).</summary>
        public void Skip(double hours)
        {
            if (!CanRun()) return;
            try { Game.Advance(hours); Game.Use(Math.Max(0, Game.ViewCol)); }
            catch (Exception e) { Fail("The simulation crashed while skipping time.", e); }
        }

        public void Toast(string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            Toasts.Add(s);
            if (Toasts.Count > 4) Toasts.RemoveAt(0);
            _toastT = 4f;
        }

        public void PlayClick() { try { Sfx?.Play("click"); } catch { } }

        bool CanRun() => State == Screen2.Playing && Game?.S != null && !Ended && Game.S.Phase != "lost";

        // ---------------- frame loop ----------------
        void Update()
        {
            if (_toastT > 0f) { _toastT -= Time.unscaledDeltaTime; if (_toastT <= 0f) Toasts.Clear(); }
            if (!CanRun()) return;
            if (Paused || Pending.Count > 0) return;
            try
            {
                double dt = Math.Min(Time.deltaTime, 0.1f);
                for (int i = 0; i < Speed; i++) Game.Process(dt);
                Game.Use(Math.Max(0, Game.ViewCol));
                if (Game.S.Phase == "lost") Ended = true;
            }
            catch (Exception e) { Fail("The simulation crashed.", e); }
        }

        void SaveQuiet()
        {
            try { if (Game?.S != null && Game.S.Phase != "pick") Game.SaveGame(); }
            catch (Exception e) { Debug.LogException(e); }
        }

        void OnApplicationPause(bool paused) { if (paused && State == Screen2.Playing) SaveQuiet(); }
        void OnApplicationQuit() { if (State == Screen2.Playing) SaveQuiet(); }

        void OnGUI() => _ui.Draw();
    }
}
