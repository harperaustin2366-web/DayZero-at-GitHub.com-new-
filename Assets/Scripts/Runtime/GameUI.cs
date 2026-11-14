// Minimal runtime UI, built entirely in code with IMGUI (no prefabs, fonts or scene objects
// needed). Laid out on a 1280x720-high virtual canvas like the Godot project and scaled to the
// screen, inside the device safe area.
//
// Covers: title screen (new game by difficulty / continue), top bar (clock, phase, resources,
// population, morale, pause, speed, skip), tabs for Colony, Jobs, Log and Menu (save / load /
// sound), decision popups and the game-over screen.
using System;
using System.Collections.Generic;
using System.Linq;
using DayZero.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed class GameUI
    {
        const float VH = 720f;          // virtual height (Godot viewport 1280x720)
        const float BAR_H = 64f;
        const float TAB_H = 52f;
        const int PAGE = 7;

        static readonly string[] RES_KEYS = { "food", "water", "meds", "ammo", "fuel" };
        static readonly string[] TABS = { "Colony", "Jobs", "Log", "Menu" };

        readonly Bootstrap _b;
        int _tab;
        int _jobPage, _logPage;
        bool _styled;
        GUIStyle _label, _small, _title, _button, _bigButton, _box, _chip, _warn;
        Texture2D _bgTex, _panelTex, _chipTex;

        public GameUI(Bootstrap b) { _b = b; }

        Game G => _b.Game;

        // ---------------- styles ----------------
        static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, c); t.Apply();
            t.hideFlags = HideFlags.DontSave;
            return t;
        }

        void EnsureStyles()
        {
            if (_styled) return;
            _styled = true;
            _bgTex = Solid(new Color(0.07f, 0.08f, 0.08f, 1f));
            _panelTex = Solid(new Color(0.12f, 0.13f, 0.13f, 0.96f));
            _chipTex = Solid(new Color(0.06f, 0.07f, 0.07f, 0.85f));
            var ink = new Color(0.92f, 0.90f, 0.84f);

            _label = new GUIStyle(GUI.skin.label) { fontSize = 20, wordWrap = true };
            _label.normal.textColor = ink;
            _small = new GUIStyle(_label) { fontSize = 16 };
            _small.normal.textColor = new Color(0.75f, 0.74f, 0.70f);
            _title = new GUIStyle(_label) { fontSize = 30, fontStyle = FontStyle.Bold };
            _warn = new GUIStyle(_label) { fontSize = 18, fontStyle = FontStyle.Bold };
            _warn.normal.textColor = new Color(1f, 0.45f, 0.35f);
            _button = new GUIStyle(GUI.skin.button) { fontSize = 20, wordWrap = true };
            _bigButton = new GUIStyle(_button) { fontSize = 26 };
            _box = new GUIStyle(GUI.skin.box);
            _box.normal.background = _panelTex;
            _chip = new GUIStyle(_label) { fontSize = 19, alignment = TextAnchor.MiddleCenter, wordWrap = false };
            _chip.normal.background = _chipTex;
        }

        // ---------------- entry ----------------
        public void Draw()
        {
            EnsureStyles();
            var safe = UnityEngine.Screen.safeArea;
            if (safe.width <= 0 || safe.height <= 0) safe = new Rect(0, 0, UnityEngine.Screen.width, UnityEngine.Screen.height);
            GUI.DrawTexture(new Rect(0, 0, UnityEngine.Screen.width, UnityEngine.Screen.height), _bgTex);

            float s = Mathf.Max(0.35f, safe.height / VH);
            // IMGUI's origin is top-left; safeArea's y is measured from the bottom.
            float top = UnityEngine.Screen.height - safe.yMax;
            GUI.matrix = Matrix4x4.TRS(new Vector3(safe.x, top, 0f), Quaternion.identity, new Vector3(s, s, 1f));
            float w = safe.width / s, h = VH;

            switch (_b.State)
            {
                case Bootstrap.Screen2.Loading: Center(w, h, "DayZero", "Building the city…"); break;
                case Bootstrap.Screen2.Failed: DrawFailed(w, h); break;
                case Bootstrap.Screen2.Title: DrawTitle(w, h); break;
                case Bootstrap.Screen2.Playing: DrawGame(w, h); break;
            }
            DrawToasts(w, h);
            GUI.matrix = Matrix4x4.identity;
        }

        bool Btn(Rect r, string text, GUIStyle st = null)
        {
            if (!GUI.Button(r, text, st ?? _button)) return false;
            _b.PlayClick();
            return true;
        }

        void Center(float w, float h, string title, string sub)
        {
            GUI.Label(new Rect(w / 2 - 300, h / 2 - 60, 600, 50), title, _title);
            GUI.Label(new Rect(w / 2 - 300, h / 2, 600, 40), sub, _label);
        }

        void DrawFailed(float w, float h)
        {
            GUI.Box(new Rect(40, 40, w - 80, h - 80), GUIContent.none, _box);
            GUI.Label(new Rect(70, 60, w - 140, 50), "Something went wrong", _title);
            GUI.Label(new Rect(70, 120, w - 140, h - 280), _b.Fatal, _label);
            if (_b.Game != null && Btn(new Rect(70, h - 140, 320, 64), "Back to title", _bigButton)) _b.ToTitle();
        }

        void DrawTitle(float w, float h)
        {
            GUI.Label(new Rect(60, 50, w - 120, 60), "DAY ZERO: OUTBREAK", _title);
            GUI.Label(new Rect(60, 105, w - 120, 40), "v" + Application.version + " · Unity port", _small);
            float y = 190;
            bool has = _b.HasSave();
            GUI.enabled = has;
            if (Btn(new Rect(60, y, 420, 76), has ? "Continue" : "Continue (no save)", _bigButton)) _b.LoadSaved();
            GUI.enabled = true;
            y += 110;
            GUI.Label(new Rect(60, y, 420, 34), "New game", _label);
            y += 40;
            for (int d = 0; d < Game.DIFF_NAMES.Length; d++)
            {
                if (Btn(new Rect(60 + d * 220, y, 200, 72), Game.DIFF_NAMES[d], _bigButton)) _b.NewGame(d);
            }
            GUI.Label(new Rect(60, h - 90, w - 120, 60),
                "Starting a new game replaces the current save. The camp site is picked automatically in this build.", _small);
        }

        // ---------------- in game ----------------
        void DrawGame(float w, float h)
        {
            if (G?.S == null || G.C == null) { Center(w, h, "DayZero", "No game running."); return; }
            DrawTopBar(w);
            float y0 = BAR_H + 6;
            for (int i = 0; i < TABS.Length; i++)
            {
                var r = new Rect(8 + i * 170, y0, 160, TAB_H - 6);
                bool on = _tab == i;
                if (Btn(r, on ? "[ " + TABS[i] + " ]" : TABS[i])) _tab = i;
            }
            var body = new Rect(8, y0 + TAB_H, w - 16, h - y0 - TAB_H - 8);
            GUI.Box(body, GUIContent.none, _box);
            var inner = new Rect(body.x + 14, body.y + 10, body.width - 28, body.height - 20);
            switch (_tab)
            {
                case 0: DrawColony(inner); break;
                case 1: DrawJobs(inner); break;
                case 2: DrawLog(inner); break;
                case 3: DrawMenu(inner); break;
            }

            if (_b.Ended || G.S.Phase == "lost") DrawGameOver(w, h);
            else if (_b.Pending.Count > 0) DrawDecision(w, h, _b.Pending[0]);
        }

        void DrawTopBar(float w)
        {
            var c = G.C;
            GUI.Box(new Rect(0, 0, w, BAR_H), GUIContent.none, _box);
            GUI.Label(new Rect(10, 4, 230, 30), G.ClockText() + (G.IsNight() ? "  night" : ""), _label);
            GUI.Label(new Rect(10, 34, 330, 28), PhaseText(), _small);

            float x = 250;
            foreach (var k in RES_KEYS)
            {
                double v = c.Res.Get(k, 0.0);
                Chip(ref x, k + " " + (int)v);
            }
            Chip(ref x, "mats " + (int)G.MatsTotal());
            Chip(ref x, "pop " + G.AlivePeople().Count);
            Chip(ref x, "morale " + (int)c.Morale);

            // controls, right-aligned
            float bx = w - 8;
            bx -= 86; if (Btn(new Rect(bx, 6, 80, 52), "+6h")) _b.Skip(6);
            bx -= 86; if (Btn(new Rect(bx, 6, 80, 52), "+1h")) _b.Skip(1);
            bx -= 86; if (Btn(new Rect(bx, 6, 80, 52), "x" + _b.Speed)) _b.Speed = _b.Speed >= 4 ? 1 : _b.Speed * 2;
            bx -= 106; if (Btn(new Rect(bx, 6, 100, 52), _b.Paused ? "Play" : "Pause")) _b.Paused = !_b.Paused;
        }

        void Chip(ref float x, string text)
        {
            float cw = Mathf.Max(70f, _chip.CalcSize(new GUIContent(text)).x + 16f);
            GUI.Label(new Rect(x, 14, cw, 36), text, _chip);
            x += cw + 4;
        }

        string PhaseText()
        {
            string ph = G.PhaseOf(Math.Max(0, G.CurCol));
            string t;
            switch (ph)
            {
                case "spread": t = string.Format("Infection {0}% of the city", (int)(G.SpreadPct() * 100.0)); break;
                case "fall": t = string.Format("THE FALL · hold {0:0.0} more days", G.HoldLeft() / 24.0); break;
                case "lost": t = "Overrun"; break;
                default: t = ph; break;
            }
            var ri = G.RaidInfo();
            if (ri != null && (ph == "spread" || ph == "fall"))
            {
                if (ri.On) t = string.Format("NIGHT ATTACK · {0} closing in", ri.Spawned);
                else if (ri.Warned) t = string.Format("ATTACK TONIGHT · about {0}", ri.N);
            }
            return t;
        }

        void DrawColony(Rect r)
        {
            var c = G.C;
            float y = r.y;
            GUI.Label(new Rect(r.x, y, r.width, 36), c.Name, _title); y += 44;
            var alive = G.AlivePeople();
            GUI.Label(new Rect(r.x, y, r.width, 30), string.Format("{0} survivors · {1} camp(s) · {2} zombies on the map · kills {3} · rescued {4}",
                alive.Count, c.Bases.Count, G.ZombieCount(), c.Stat("kills"), c.Stat("rescued")), _label);
            y += 36;
            var ri = G.RaidInfo();
            if (ri != null && (ri.On || ri.Warned))
            {
                GUI.Label(new Rect(r.x, y, r.width, 28), ri.On ? "Your camp is under attack. Guards are on the line." : "Scouts expect an attack tonight. Put Soldiers and Cops on guard.", _warn);
                y += 32;
            }
            float col = Mathf.Min(560f, r.width / 2 - 10);
            float yl = y, yr = y;
            GUI.Label(new Rect(r.x, yl, col, 28), "Camps", _label); yl += 30;
            foreach (var b in c.Bases.Take(6))
            {
                GUI.Label(new Rect(r.x + 10, yl, col - 10, 26), "• " + G.BaseLabel(b) + (b.Done < 1.0 ? string.Format(" (building {0}%)", (int)(b.Done * 100)) : ""), _small);
                yl += 26;
            }
            yl += 8;
            GUI.Label(new Rect(r.x, yl, col, 28), "Stockpile", _label); yl += 30;
            string res = string.Join("   ", c.Res.Where(kv => kv.Value >= 1.0).Select(kv => kv.Key + " " + (int)kv.Value));
            GUI.Label(new Rect(r.x + 10, yl, col - 10, r.yMax - yl), res, _small);

            float xr = r.x + col + 20;
            GUI.Label(new Rect(xr, yr, col, 28), "Jobs", _label); yr += 30;
            foreach (var j in Data.JOBS)
            {
                int n = G.CountJob(j);
                GUI.Label(new Rect(xr + 10, yr, col - 10, 26), string.Format("{0,-10} {1}", j, n), _small);
                yr += 25;
            }
            int kids = alive.Count(p => p.Job == "Child");
            if (kids > 0) { GUI.Label(new Rect(xr + 10, yr, col - 10, 26), "Children " + kids, _small); yr += 25; }
            yr += 8;
            if (Btn(new Rect(xr, yr, 260, 52), "Manage jobs")) _tab = 1;
        }

        void DrawJobs(Rect r)
        {
            var people = G.AlivePeople();
            int pages = Math.Max(1, (people.Count + PAGE - 1) / PAGE);
            _jobPage = Mathf.Clamp(_jobPage, 0, pages - 1);
            GUI.Label(new Rect(r.x, r.y, r.width - 360, 32), string.Format("Survivors ({0}) · tap < > to change a job", people.Count), _label);
            if (Btn(new Rect(r.xMax - 340, r.y - 4, 110, 44), "< Prev")) _jobPage = Math.Max(0, _jobPage - 1);
            GUI.Label(new Rect(r.xMax - 222, r.y + 4, 100, 32), string.Format("{0}/{1}", _jobPage + 1, pages), _label);
            if (Btn(new Rect(r.xMax - 110, r.y - 4, 110, 44), "Next >")) _jobPage = Math.Min(pages - 1, _jobPage + 1);

            float y = r.y + 50, rowH = (r.height - 50) / PAGE;
            foreach (var p in people.Skip(_jobPage * PAGE).Take(PAGE))
            {
                string info = string.Format("{0}  ·  {1}  ·  hp {2}{3}", p.Name, StateText(p), (int)p.Hp, p.Stage != null && p.Stage != "adult" ? "  ·  " + p.Stage : "");
                GUI.Label(new Rect(r.x, y + 4, r.width - 430, rowH - 4), info, _label);
                float bx = r.xMax - 420;
                if (Btn(new Rect(bx, y + 2, 64, rowH - 8), "<")) CycleJob(p, -1);
                GUI.Label(new Rect(bx + 70, y + 8, 200, rowH - 8), p.Job, _chip);
                if (Btn(new Rect(bx + 276, y + 2, 64, rowH - 8), ">")) CycleJob(p, 1);
                y += rowH;
            }
        }

        static string StateText(Person p)
        {
            switch (p.St)
            {
                case "base": return "at camp";
                case "move": return "travelling";
                case "work": return string.IsNullOrEmpty(p.Goal) ? "working" : "on " + p.Goal;
                default: return p.St;
            }
        }

        void CycleJob(Person p, int dir)
        {
            // Step through Data.JOBS in the chosen direction; a job the survivor can't take
            // (JobBlock: a child, no free gear...) is skipped and its reason shown.
            var jobs = Data.JOBS;
            int n = jobs.Length;
            int i = Math.Max(0, Array.IndexOf(jobs, p.Job));
            string firstErr = null;
            for (int k = 1; k < n; k++)
            {
                string next = jobs[(((i + dir * k) % n) + n) % n];
                string err = _b.Act("set_job", p.Id, next);
                if (err == "")
                {
                    string desc;
                    string msg = p.Name + " is now a " + next + (Data.JOB_DESC.TryGetValue(next, out desc) ? ". " + desc : ".");
                    if (firstErr != null) msg += "\n(skipped: " + firstErr + ")";
                    _b.Toast(msg);
                    return;
                }
                if (firstErr == null) firstErr = err;
                if (err.StartsWith("That survivor") || err.StartsWith("Error") || err.StartsWith("Your colony") || err.StartsWith("No colony")) break;
            }
            _b.Toast(firstErr ?? "No other job available.");
        }

        void DrawLog(Rect r)
        {
            var log = G.S.Log.Where(e => e.Col < 0 || e.Col == G.CurCol).Reverse().ToList();
            const int per = 12;
            int pages = Math.Max(1, (log.Count + per - 1) / per);
            _logPage = Mathf.Clamp(_logPage, 0, pages - 1);
            GUI.Label(new Rect(r.x, r.y, 400, 32), "Log (newest first)", _label);
            if (Btn(new Rect(r.xMax - 340, r.y - 4, 110, 44), "< Newer")) _logPage = Math.Max(0, _logPage - 1);
            GUI.Label(new Rect(r.xMax - 222, r.y + 4, 100, 32), string.Format("{0}/{1}", _logPage + 1, pages), _label);
            if (Btn(new Rect(r.xMax - 110, r.y - 4, 110, 44), "Older >")) _logPage = Math.Min(pages - 1, _logPage + 1);
            float y = r.y + 48, rowH = (r.height - 48) / per;
            foreach (var e in log.Skip(_logPage * per).Take(per))
            {
                string when = string.Format("d{0} {1:00}:00", (int)(e.T / 24.0) + 1, (int)e.T % 24);
                var st = e.Kind == "death" || e.Kind == "siren" || e.Kind == "major" ? _warn : _small;
                GUI.Label(new Rect(r.x, y, r.width, rowH), when + "  " + e.Text, st);
                y += rowH;
            }
        }

        void DrawMenu(Rect r)
        {
            float y = r.y;
            if (Btn(new Rect(r.x, y, 300, 64), "Save now", _bigButton)) _b.SaveNow();
            if (Btn(new Rect(r.x + 320, y, 300, 64), "Load last save", _bigButton)) _b.LoadSaved();
            if (Btn(new Rect(r.x + 640, y, 300, 64), "Title screen", _bigButton)) _b.ToTitle();
            y += 90;
            var sfx = _b.Sfx;
            if (sfx != null)
            {
                GUI.Label(new Rect(r.x, y, 200, 36), "Sound", _label);
                if (Btn(new Rect(r.x + 200, y - 4, 160, 48), sfx.Enabled ? "On" : "Off")) { sfx.Enabled = !sfx.Enabled; sfx.SaveSettings(); }
                if (Btn(new Rect(r.x + 380, y - 4, 70, 48), "-")) { sfx.Volume = Math.Max(0.0, Math.Round(sfx.Volume - 0.1, 1)); sfx.SaveSettings(); }
                GUI.Label(new Rect(r.x + 460, y + 4, 120, 36), (int)(sfx.Volume * 100) + "%", _label);
                if (Btn(new Rect(r.x + 560, y - 4, 70, 48), "+")) { sfx.Volume = Math.Min(1.0, Math.Round(sfx.Volume + 0.1, 1)); sfx.SaveSettings(); }
                y += 70;
            }
            GUI.Label(new Rect(r.x, y, r.width, 120),
                "The game autosaves every few minutes and when the app goes to the background. " +
                "This build has no map yet: survivors work on their own, you set jobs, answer events and control time.", _small);
        }

        // ---------------- popups ----------------
        void DrawDecision(float w, float h, JObject ev)
        {
            var choices = ev["choices"] as JArray ?? new JArray();
            float pw = Mathf.Min(900f, w - 60), ph = Mathf.Min(h - 60, 230f + choices.Count * 84f);
            var p = new Rect((w - pw) / 2, (h - ph) / 2, pw, ph);
            GUI.Box(new Rect(0, 0, w, h), GUIContent.none, _box);
            GUI.Box(p, GUIContent.none, _box);
            GUI.Label(new Rect(p.x + 24, p.y + 16, pw - 48, 44), (string)ev["title"] ?? "Event", _title);
            GUI.Label(new Rect(p.x + 24, p.y + 66, pw - 48, 140), (string)ev["text"] ?? "", _label);
            float y = p.y + 210;
            for (int i = 0; i < choices.Count; i++)
            {
                var ch = choices[i] as JObject;
                string label = (string)ch?["label"] ?? ("Option " + (i + 1));
                string hint = (string)ch?["hint"];
                if (Btn(new Rect(p.x + 24, y, pw - 48, 76), string.IsNullOrEmpty(hint) ? label : label + "\n" + hint)) { _b.Answer(ev, i); return; }
                y += 84;
            }
        }

        void DrawGameOver(float w, float h)
        {
            float pw = Mathf.Min(760f, w - 60);
            var p = new Rect((w - pw) / 2, h / 2 - 170, pw, 340);
            GUI.Box(new Rect(0, 0, w, h), GUIContent.none, _box);
            GUI.Box(p, GUIContent.none, _box);
            GUI.Label(new Rect(p.x + 24, p.y + 20, pw - 48, 44), _b.Won ? "You held out" : "Your colony has fallen", _title);
            var c = G.C;
            GUI.Label(new Rect(p.x + 24, p.y + 80, pw - 48, 100), string.Format("Day {0}. {1} zombies killed, {2} survivors rescued.",
                G.Day(), c?.Stat("kills") ?? 0, c?.Stat("rescued") ?? 0), _label);
            if (Btn(new Rect(p.x + 24, p.y + 240, 300, 70), "New game", _bigButton)) _b.NewGame(1);
            if (Btn(new Rect(p.x + pw - 324, p.y + 240, 300, 70), "Title screen", _bigButton)) _b.ToTitle();
        }

        void DrawToasts(float w, float h)
        {
            if (_b.Toasts.Count == 0) return;
            float y = h - 16;
            for (int i = _b.Toasts.Count - 1; i >= 0; i--)
            {
                float th = Mathf.Max(40f, _small.CalcHeight(new GUIContent(_b.Toasts[i]), w * 0.6f) + 12f);
                y -= th + 6;
                var r = new Rect(w * 0.2f, y, w * 0.6f, th);
                GUI.Box(r, GUIContent.none, _box);
                GUI.Label(new Rect(r.x + 10, r.y + 6, r.width - 20, r.height - 8), _b.Toasts[i], _small);
            }
        }
    }
}
