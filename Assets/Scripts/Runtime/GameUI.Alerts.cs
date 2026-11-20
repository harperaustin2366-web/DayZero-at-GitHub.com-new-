// E16 + E17 (batch B1): alert feed and the speed auto-drop.
// * AlertFeed (pure, also compiled by the offline preview): sorts sim log lines and decisions into five alert kinds
//   (fence breached, survivor bitten, raid incoming, survivor died, building on fire), finds where each happened and
//   keeps the last MAX. Repeats of the same kind at the same spot within half an hour fold into one row ("x3").
// * GameUI part: an ALERTS button under the camera column with an unread count, a compact list next to it; tapping a
//   row flies the camera there. B1Tick also drops any speed above x1 back to x1 (with a toast) when an attack starts,
//   the siren sounds or a horde is announced. Speed only changes how many Process() calls run per frame, so the sim
//   rules and the classic hands-off results are untouched. x1 / x2 / x4 already exist in the time control.
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed class AlertItem
    {
        public string Kind = "";     // breach | bitten | raid | died | fire
        public string Text = "";
        public Vector2 At;           // tile coordinates (MapView.FlyTo); HasAt false = no spot found
        public bool HasAt;
        public double T;             // game hours
        public int Count = 1;
    }

    public static class AlertFeed
    {
        public const int MAX = 20;
        public static readonly List<AlertItem> Items = new List<AlertItem>();   // oldest first
        public static int Unread;
        /// <summary>Set when an alert means an attack is on its way (raid / siren / horde); B1Tick consumes it.</summary>
        public static bool AttackFlag;

        public static void Clear() { Items.Clear(); Unread = 0; AttackFlag = false; }

        static readonly Regex DIED = new Regex(@"^(.+?) \(([A-Za-z]+)\) ", RegexOptions.Compiled);

        /// <summary>Alert kind for a log line, or null when it is not one of the five alert kinds.</summary>
        public static string Classify(string text, string kind)
        {
            if (string.IsNullOrEmpty(text)) return null;
            if (text.StartsWith("Zombies broke through the ") || (text.StartsWith("A tower on ") && text.Contains("torn down"))) return "breach";
            if (text.Contains(" was bitten") || text.Contains("found a hidden bite on ")) return "bitten";
            if (kind == "siren" || text.StartsWith("SIREN") || text.StartsWith("They're coming:") || text.StartsWith("Contact:") || text.StartsWith("A horde of ")) return "raid";
            if (text.Contains(" raided ") && text.Contains(" and took ")) return "raid";
            if (kind == "death")
            {
                if (text.StartsWith("THE FALL")) return null;
                return "died";
            }
            if (text.Contains("turned inside the wall")) return "died";
            return null;
        }

        public static string Label(string kind)
        {
            switch (kind)
            {
                case "breach": return "BREACH";
                case "bitten": return "BITTEN";
                case "raid": return "RAID";
                case "died": return "DIED";
                case "fire": return "FIRE";
                case "support": return "SUPPORT";   // v0.19 GameUI.Popularity.cs
                default: return "ALERT";
            }
        }

        public static bool IsBad(string kind) => kind == "breach" || kind == "died" || kind == "fire";

        public static string Clock(double t)
        {
            int day = (int)(t / 24.0) + 1;
            double hh = t % 24.0;
            int h = (int)hh, m = (int)((hh - h) * 60.0);
            return string.Format("D{0} {1:00}:{2:00}", day, h, m);
        }

        /// <summary>Record a sim log line if it is an alert. Returns the kind or null.</summary>
        public static string FromLog(Game g, string text, string kind, int col)
        {
            string k = Classify(text, kind);
            if (k == null || g?.S == null) return null;
            if (g.ViewCol >= 0 && col >= 0 && col != g.ViewCol) return null;   // another colony's log (multiplayer)
            bool has = Locate(g, k, text, out var at);
            Add(k, text, has, at, g.S.T);
            // the siren, a major night attack or a horde: fast-forward stops (a minor "Contact:" waits for the attack itself)
            if (k == "raid" && (kind == "siren" || text.StartsWith("SIREN") || text.StartsWith("They're coming:") || text.StartsWith("A horde of "))) AttackFlag = true;
            return k;
        }

        /// <summary>Record a decision popup (only "fire" is an alert: the storeroom fire in camp).</summary>
        public static void FromDecision(Game g, string id, string title)
        {
            if (g?.S == null) return;
            if (id != "fire" && (title == null || !title.StartsWith("Fire"))) return;
            var mb = MainBaseOf(g);
            Vector2 at = mb != null ? BaseSpot(mb) : Vector2.zero;
            Add("fire", string.IsNullOrEmpty(title) ? "Fire in the camp" : title, mb != null, at, g.S.T);
        }

        public static void Add(string kind, string text, bool hasAt, Vector2 at, double t)
        {
            var last = Items.Count > 0 ? Items[Items.Count - 1] : null;
            if (last != null && last.Kind == kind && Math.Abs(t - last.T) < 0.5 && (!hasAt || !last.HasAt || (last.At - at).magnitude < 6f))
            {
                last.Count++; last.Text = text; last.T = t;
                if (hasAt) { last.At = at; last.HasAt = true; }
                Unread++;
                return;
            }
            Items.Add(new AlertItem { Kind = kind, Text = text, HasAt = hasAt, At = at, T = t });
            while (Items.Count > MAX) Items.RemoveAt(0);
            Unread = Math.Min(MAX, Unread + 1);
        }

        // ---------------- where did it happen ----------------
        static Base MainBaseOf(Game g)
        {
            var c = g.C;
            if (c == null) return null;
            foreach (var b in c.Bases) if (b.Main) return b;
            return c.Bases.Count > 0 ? c.Bases[0] : null;
        }

        public static Vector2 BaseSpot(Base b)
        {
            var d = Data.Instance;
            if (b.Lot >= 0 && d != null && b.Lot < d.Lots.Count) return d.LotCenter(b.Lot);
            return new Vector2(b.Pos.x + 0.5f, b.Pos.y + 0.5f);
        }

        static Person PersonNamed(Game g, string name)
        {
            if (g.C == null || string.IsNullOrEmpty(name)) return null;
            foreach (var p in g.C.People) if (p.Name == name) return p;
            foreach (var p in g.C.People) if (p.First == name) return p;
            return null;
        }

        static bool Locate(Game g, string kind, string text, out Vector2 at)
        {
            at = Vector2.zero;
            try
            {
                string name = null;
                switch (kind)
                {
                    case "breach":
                        if (text.StartsWith("Zombies broke through"))
                        {
                            // RecordBreach runs just before the log line: the newest breach of this colony is the one
                            double bt = double.NegativeInfinity; int bk = -1;
                            foreach (var kv in g.S.Breaches)
                                if (kv.Value.T >= bt && (g.CurCol < 0 || kv.Value.Col == g.CurCol)) { bt = kv.Value.T; bk = kv.Key; }
                            if (bk >= 0) { var tp = g.Tp(bk); at = new Vector2(tp.x + 0.5f, tp.y + 0.5f); return true; }
                        }
                        break;
                    case "bitten":
                        int i = text.IndexOf(" was bitten", StringComparison.Ordinal);
                        if (i > 0) name = text.Substring(0, i);
                        else
                        {
                            const string HB = "found a hidden bite on ";
                            int j = text.IndexOf(HB, StringComparison.Ordinal);
                            if (j >= 0) name = text.Substring(j + HB.Length).TrimEnd('.', ' ').Split('.')[0];
                        }
                        break;
                    case "died":
                        var m = DIED.Match(text);
                        if (m.Success) name = m.Groups[1].Value;
                        else
                        {
                            int k = text.IndexOf(" was hiding a bite", StringComparison.Ordinal);
                            if (k < 0) k = text.IndexOf(" turned inside", StringComparison.Ordinal);
                            if (k > 0) name = text.Substring(0, k);
                        }
                        break;
                    case "raid":
                        // the camp the text names, else the camp the night raid targets
                        if (g.C != null)
                        {
                            Base named = null;
                            foreach (var b in g.C.Bases) if (!string.IsNullOrEmpty(b.Name) && text.Contains(b.Name) && (named == null || b.Name.Length > named.Name.Length)) named = b;
                            if (named == null && g.C.Nraid != null && g.C.Nraid.Bid >= 0) named = g.BaseById(g.C.Nraid.Bid);
                            if (named != null) { at = BaseSpot(named); return true; }
                        }
                        break;
                }
                if (name != null)
                {
                    var p = PersonNamed(g, name);
                    if (p != null) { at = p.Pos; return true; }
                }
                var mb = MainBaseOf(g);
                if (mb != null) { at = BaseSpot(mb); return true; }
            }
            catch (Exception) { }
            return false;
        }

        // ---------------- layout (pure; shared with the offline preview) ----------------
        /// <summary>ALERTS button: under the camera column (camBottom = last camera button's yMax).</summary>
        public static Rect ButtonRect(float camBottom) => new Rect(10f, camBottom + 8f, 84f, 56f);

        /// <summary>The open list: right of the camera column, from the first camera button down to the ALERTS button.</summary>
        public static Rect ListRect(float w, float top, float bottom) => new Rect(104f, top, Mathf.Min(480f, Mathf.Max(340f, w * 0.34f)), Mathf.Max(220f, bottom - top));

        public const float ROW_H = 58f, ROW_GAP = 6f;
    }

#if !DZ_PREVIEW
    public sealed partial class GameUI
    {
        Game _alertGame;
        object _alertState;
        bool _alertsOpen;
        int _b1Frame = -1;
        string _lastAtkPhase = "";
        bool _lastSiren;

        void OnAlertLog(LogEntry e)
        {
            try { B1StateCheck(); AlertFeed.FromLog(G, e.Text, e.Kind, e.Col); } catch (Exception ex) { Debug.LogException(ex); }
        }

        void OnAlertDecision(Newtonsoft.Json.Linq.JObject ev)
        {
            try { B1StateCheck(); AlertFeed.FromDecision(G, (string)ev?["id"], (string)ev?["title"]); } catch (Exception ex) { Debug.LogException(ex); }
        }

        /// <summary>A new run or a loaded save replaces Game.S: start a fresh feed (and let the tutorial decide).</summary>
        void B1StateCheck()
        {
            if (G?.S == null || ReferenceEquals(_alertState, G.S)) return;
            _alertState = G.S;
            AlertFeed.Clear();
            _alertsOpen = false;
            _lastAtkPhase = ""; _lastSiren = false;
            LongPressInfo.Clear();
            TutorialRunChanged();
        }

        /// <summary>Once per frame while a game is on screen: event hooks, speed auto-drop, tutorial progress.</summary>
        void B1Tick()
        {
            if (_b1Frame == Time.frameCount || G == null) return;
            _b1Frame = Time.frameCount;
            if (!ReferenceEquals(_alertGame, G))
            {
                if (_alertGame != null) { _alertGame.LogAdded -= OnAlertLog; _alertGame.DecisionAdded -= OnAlertDecision; }
                _alertGame = G;
                G.LogAdded += OnAlertLog;
                G.DecisionAdded += OnAlertDecision;
            }
            B1StateCheck();
            if (G.S == null || G.C == null) return;
            // E16: an attack starting, the siren, or a horde/raid announcement drops fast-forward back to x1
            string ph = ""; bool siren = false;
            if (G.C.Started)
            {
                var a = G.AttackStatus();
                if (a != null) { ph = a.Value.phase; siren = ph == "warn" && (a.Value.major || (Game.PhaseARules && G.HasSiren())); }
            }
            bool trigger = (ph == "attack" && _lastAtkPhase != "attack") || (siren && !_lastSiren) || AlertFeed.AttackFlag;
            _lastAtkPhase = ph; _lastSiren = siren; AlertFeed.AttackFlag = false;
            if (trigger && _b.Speed > 1)
            {
                _b.Speed = 1;
                _b.Toast(ph == "attack" ? "Under attack - speed back to x1." : "Attack incoming - speed back to x1.");
            }
            TutorialTick();
        }

        /// <summary>Batch B1 HUD layer (hook in DrawGame): alerts, tutorial card, long-press info card.</summary>
        void DrawB1Hud(float w, float h, MapView map)
        {
            try
            {
                B1Tick();
                if (G?.S == null || G.C == null) return;
                bool modal = ModalOpen;
                if (!_b.Picking && G.C.Started && !modal) DrawAlerts(w, h, map);
                if (!modal && !_alertsOpen) DrawLongPressCard(w, h, map);
                if (_b.Picking) { if (!modal) DrawTutorialPick(w, h, map); }
                else DrawTutorialHighlight();   // the card itself is drawn in the banner lane (DrawTutorialBanner)
            }
            catch (Exception e) { Debug.LogException(e); }
        }

        void DrawAlerts(float w, float h, MapView map)
        {
            var f = _hf;
            if (f == null || f.Cam == null || f.Cam.Length == 0) return;
            var br = Blk(AlertFeed.ButtonRect(f.Cam[f.Cam.Length - 1].yMax));
            int un = AlertFeed.Unread;
            bool bad = false;
            for (int i = AlertFeed.Items.Count - 1, s = 0; i >= 0 && s < un; i--, s++) if (AlertFeed.IsBad(AlertFeed.Items[i].Kind)) { bad = true; break; }
            if (un > 0 && !_alertsOpen) DrawHalo(UiLayout.Inset(br, 6f), bad ? new Color(0.95f, 0.35f, 0.28f) : new Color(1f, 0.72f, 0.30f), 3f);
            if (KBtn(br, "ALERTS", un > 0 ? (bad ? UiRole.Danger : UiRole.Accent) : UiRole.Neutral, _alertsOpen, 17))
            {
                _alertsOpen = !_alertsOpen;
                if (_alertsOpen) { AlertFeed.Unread = 0; LongPressInfo.Clear(); map?.ClearBuilding(); UiKit.ScrollTo("ui_alerts", Mathf.Max(0f, AlertFeed.Items.Count * (AlertFeed.ROW_H + AlertFeed.ROW_GAP))); }
            }
            if (un > 0 && !_alertsOpen)
            {
                var badge = new Rect(br.xMax - 30f, br.y - 8f, 34f, 24f);
                UiKit.Chip(badge, un > 9 ? "9+" : un.ToString(), bad ? UiRole.Danger : UiRole.Accent, 15);
            }
            if (!_alertsOpen) return;
            var r = Blk(AlertFeed.ListRect(w, f.Cam[0].y, br.yMax));
            var c = UiKit.Panel(r, "Alerts");
            if (KBtn(new Rect(r.xMax - 112f, r.y + 2f, 104f, 44f), "CLOSE", UiRole.Danger, false, 16)) { _alertsOpen = false; return; }
            var items = AlertFeed.Items;
            if (items.Count == 0)
            {
                UiKit.FitLabel(new Rect(c.x, c.y, c.width, 30f), "No alerts yet. Breaches, bites, raids, deaths", 16, TextAnchor.MiddleLeft, UiKit.InkDim, false);
                UiKit.FitLabel(new Rect(c.x, c.y + 30f, c.width, 30f), "and fires show up here.", 16, TextAnchor.MiddleLeft, UiKit.InkDim, false);
                return;
            }
            // newest at the bottom (scrolled there on open), like the log
            var lst = UiKit.BeginList("ui_alerts", c, items.Count, AlertFeed.ROW_H, AlertFeed.ROW_GAP);
            int jump = -1;
            for (int i = lst.First; i < lst.Last; i++)
            {
                var it = items[i];
                var row = lst.Row(i);
                if (UiKit.Button(row, "", UiRole.Neutral, false, 16) && it.HasAt) jump = i;
                var inner = UiLayout.Inset(row, 10f, 4f, 10f, 8f);
                var lines = UiLayout.Rows(inner, 1f, 1f, 1.15f);
                var top = UiLayout.Columns(lines[0], 8f, -84f, 1f);
                UiKit.FitLabel(top[0], AlertFeed.Label(it.Kind), 15, TextAnchor.MiddleLeft, AlertFeed.IsBad(it.Kind) ? UiKit.Bad : UiKit.Amber, true, 11);
                UiKit.FitLabel(top[1], AlertFeed.Clock(it.T) + (it.Count > 1 ? "   x" + it.Count : "") + (it.HasAt ? "   TAP TO VIEW" : ""), 13, TextAnchor.MiddleRight, UiKit.InkDim, false, 10);
                UiKit.FitLabel(lines[1], it.Text, 16, TextAnchor.MiddleLeft, UiKit.Ink, false, 13);
            }
            UiKit.EndList();
            if (jump >= 0 && map != null)
            {
                var it = items[jump];
                map.FlyTo(it.At);
                _alertsOpen = false;
            }
        }
    }
#endif
}
