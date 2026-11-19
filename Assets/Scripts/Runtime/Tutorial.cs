// E11 (batch B1): first-night tutorial. Optional on New Game: a TUTORIAL ON / OFF toggle on the title screen, on by
// default until the first run has been started (PlayerPrefs), after that whatever the player last chose.
// Five guided steps, each a small card (step, title, one-line hint, SKIP) plus a pulsing highlight box around the
// control to use. A step advances when the real thing happens in the sim, never on a button press:
//   1 pick your start building   colony founded (C.Started)
//   2 place a fence segment      more fence tiles / segments / wall runs / gates than right after founding
//   3 assign a guard             a survivor put on guard by hand (Guard and not Gauto), or a new Soldier / Cop
//   4 send someone to loot       a task force on a supply run (Tf <= -1000000) or a new Scavenger
//   5 survive night 1            dawn of day 2 with the colony still standing
// Steps already done earlier (the player ran ahead) are ticked off together. The tutorial only reads state; it
// never changes the sim, so it cannot affect balance or the classic hands-off results.
using System;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public static class Tutorial
    {
        public const int STEPS = 5;
        public static readonly string[] TITLES = { "PICK YOUR START BUILDING", "PLACE A FENCE SEGMENT", "ASSIGN A GUARD", "SEND SOMEONE TO LOOT", "SURVIVE NIGHT 1" };
        public static readonly string[] HINTS =
        {
            "Drag to look around, tap a building, then CONFIRM.",
            "Open DEFENSE in ACTIONS and build a piece of fence.",
            "Open CREW & JOBS and tick GUARD on one survivor.",
            "Tap a building outside, then SEND TASK FORCE TO LOOT.",
            "Hold until dawn of day 2. Speed drops to x1 if they attack.",
        };
        public static readonly string[] SHORT = { "pick your start building", "place a fence segment", "assign a guard", "send someone to loot", "survive night 1" };

        /// <summary>-1 = off, 0..4 = the step shown.</summary>
        public static int Step = -1;
        public static bool Active => Step >= 0 && Step < STEPS;
        /// <summary>Whether the next new run starts the tutorial (the title toggle sets it).</summary>
        public static bool Wanted;

        static int _fence0 = -1, _scav0, _arm0;
        static bool _fence, _guard, _loot;

        /// <summary>Game.S was replaced: a new run in the "pick" phase starts the tutorial if wanted; a load ends it.</summary>
        public static void OnRunChanged(Game g)
        {
            bool fresh = g?.S != null && g.C != null && !g.C.Started && g.S.Phase == "pick";
            Step = fresh && Wanted ? 0 : -1;
            _fence0 = -1; _fence = _guard = _loot = false;
        }

        public static void Skip() => Step = -1;

        static int FenceCount(Game g) => g.S.Fences.Count + g.S.Fsegs.Count + g.S.Walls.Count + g.S.Gates.Count;
        static int Armed(Game g) => g.CountJob("Soldier") + g.CountJob("Cop");

        /// <summary>Check the sim once per frame. Returns a toast line when a step (or the tutorial) completes.</summary>
        public static string Detect(Game g)
        {
            if (!Active || g?.S == null || g.C == null) return null;
            var c = g.C;
            if (c.Lost || g.S.Phase == "lost") { Step = -1; return null; }
            if (!c.Started) return null;
            if (_fence0 < 0) { _fence0 = FenceCount(g); _scav0 = g.CountJob("Scavenger"); _arm0 = Armed(g); }
            if (!_fence && FenceCount(g) > _fence0) _fence = true;
            if (!_guard)
            {
                if (Armed(g) > _arm0) _guard = true;
                else foreach (var p in c.People) if (p.St != "dead" && p.Guard && p.Gauto == false) { _guard = true; break; }
            }
            if (!_loot)
            {
                if (g.CountJob("Scavenger") > _scav0) _loot = true;
                else foreach (var p in c.People) if (p.St != "dead" && (p.Tf <= -1000000 || p.Goal == "tf")) { _loot = true; break; }
            }
            int from = Step;
            while (Active && Done(g, Step)) Step++;
            if (Step == from) return null;
            if (Step >= STEPS) { Step = -1; return "Tutorial complete. You made it through the first night. Good luck out there."; }
            return "Done: " + SHORT[Step - 1] + ". Next: " + SHORT[Step] + ".";
        }

        static bool Done(Game g, int step)
        {
            switch (step)
            {
                case 0: return g.C.Started;
                case 1: return _fence;
                case 2: return _guard;
                case 3: return _loot;
                case 4: return g.Day() >= 3 || (g.Day() == 2 && g.Hour() >= 6);
                default: return true;
            }
        }

        // ---------------- layout (pure; shared with the offline preview) ----------------
        public const float CARD_H = 96f, CARD_W = 640f;
        /// <summary>Card while picking the start building: left, under the pick banner, clear of the confirm panel.</summary>
        public static Rect PickCard(float w, float h) => new Rect(104f, 170f, Mathf.Min(520f, w * 0.5f - 120f), CARD_H + 14f);
        /// <summary>In play the card is one more banner in the top lane (under the attack / repair banners).</summary>
        public static Rect LaneCard(Rect lane, float y) { float bw = Mathf.Min(CARD_W, lane.width); return new Rect(Mathf.Floor(lane.x + (lane.width - bw) * 0.5f + 0.5f), y, bw, CARD_H); }

        /// <summary>Inner parts of a card: step line, title, hint, SKIP button.</summary>
        public static void CardParts(Rect card, out Rect step, out Rect title, out Rect hint, out Rect skip)
        {
            var inner = UiLayout.Inset(card, 14f, 8f, 10f, 10f);
            var cols = UiLayout.Columns(inner, 10f, 1f, -104f);
            skip = UiLayout.Center(cols[1], 104f, 56f);
            var rows = UiLayout.Rows(cols[0], 2f, -20f, 1.25f, 1f);
            step = rows[0]; title = rows[1]; hint = rows[2];
        }
    }

#if !DZ_PREVIEW
    public sealed partial class GameUI
    {
        const string PREF_TUT = "dz.tutorial", PREF_TUT_PLAYED = "dz.tutorial.played";
        Rect _tutHl; bool _tutHlOn;

        bool TutorialPref
        {
            get
            {
                if (PlayerPrefs.HasKey(PREF_TUT)) return PlayerPrefs.GetInt(PREF_TUT, 1) == 1;
                return PlayerPrefs.GetInt(PREF_TUT_PLAYED, 0) == 0;
            }
        }

        /// <summary>Title screen: TUTORIAL ON / OFF under the new-game panel.</summary>
        void DrawTutorialToggle(float w, float h)
        {
            // someone updating from an older build with a save has played before: default off for them
            if (!PlayerPrefs.HasKey(PREF_TUT_PLAYED) && _b.HasSave()) { PlayerPrefs.SetInt(PREF_TUT_PLAYED, 1); PlayerPrefs.Save(); }
            bool on = TutorialPref;
            Tutorial.Wanted = on;
            float y = 528f;
            if (KBtn(new Rect(60, y, 300, 60), on ? "TUTORIAL: ON" : "TUTORIAL: OFF", on ? UiRole.Accent : UiRole.Neutral, on, 22))
            {
                on = !on;
                PlayerPrefs.SetInt(PREF_TUT, on ? 1 : 0);
                PlayerPrefs.Save();
                Tutorial.Wanted = on;
            }
            float tw = Mathf.Min(w - 80f, 760f) - 336f;
            UiKit.FitLabel(new Rect(376, y + 4, tw, 26), on ? "First-night guide: 5 short steps, skip any time" : "No guide (tap to turn it on)", 16, TextAnchor.MiddleLeft, UiKit.Ink, false);
            UiKit.FitLabel(new Rect(376, y + 30, tw, 24), "Applies to the next new game", 15, TextAnchor.MiddleLeft, UiKit.InkDim, false);
        }

        /// <summary>Hook point for B1StateCheck: a new run was installed (tutorial wanted flag from prefs).</summary>
        void TutorialRunChanged()
        {
            Tutorial.Wanted = TutorialPref;
            Tutorial.OnRunChanged(G);
            if (G?.S != null && G.S.Phase == "pick" && PlayerPrefs.GetInt(PREF_TUT_PLAYED, 0) == 0) { PlayerPrefs.SetInt(PREF_TUT_PLAYED, 1); PlayerPrefs.Save(); }
        }

        void TutorialTick()
        {
            string msg = Tutorial.Detect(G);
            if (msg != null) _b.Toast(msg);
        }

        /// <summary>Start-site pick: card left of the pick banner, highlight on the banner (or the CONFIRM panel).</summary>
        void DrawTutorialPick(float w, float h, MapView map)
        {
            _tutHlOn = false;
            if (!Tutorial.Active || Tutorial.Step != 0) return;
            if (map != null && map.SelectedLot >= 0)
            {
                float pw = Mathf.Min(640f, w - 220f), ph = 236f;
                _tutHl = new Rect(w / 2 - pw / 2, h - ph - 12f, pw, ph);
            }
            else
            {
                float bw = Mathf.Clamp(w - 600f, 460f, 680f);
                _tutHl = new Rect(w / 2 - bw / 2, _hf.Menu.yMax + 8f, bw, 84f);
            }
            _tutHlOn = true;
            DrawTutorialCard(Blk(Tutorial.PickCard(w, h)));
            DrawTutorialHighlight();
        }

        /// <summary>In play: the card as one more top-lane banner (hook after the banners, y advances like theirs).</summary>
        void DrawTutorialBanner(float w, ref float y, MapView map)
        {
            _tutHlOn = false;
            if (!Tutorial.Active || Tutorial.Step == 0 || _b.Picking || ModalOpen || _alertsOpen || LongPressInfo.Showing) return;
            var r = Blk(Tutorial.LaneCard(TopLane(w), y));
            y += Tutorial.CARD_H + 6f;
            int s = Tutorial.Step;
            bool tools = map != null && (map.ActiveTool != MapView.Tool.None || map.TargetTool);
            if ((s == 1 || s == 2) && _panel == Panel.None && !tools)
            {
                _tutHl = _actionsOpen ? InWorld.ActionsRect(w, VH) : Docks.Tab(w, VH);
                _tutHlOn = true;
            }
            else if (s == 4) { _tutHl = _hf.TimeBar; _tutHlOn = true; }
            DrawTutorialCard(r);
        }

        void DrawTutorialCard(Rect r)
        {
            int s = Tutorial.Step;
            UiKit.Panel(r);
            Tutorial.CardParts(r, out var stepR, out var titleR, out var hintR, out var skipR);
            UiKit.FitLabel(stepR, string.Format("TUTORIAL  -  STEP {0} OF {1}", s + 1, Tutorial.STEPS), 14, TextAnchor.MiddleLeft, UiKit.InkDim, true, 10);
            UiKit.FitLabel(titleR, Tutorial.TITLES[s], 22, TextAnchor.MiddleLeft, UiKit.Amber, true, 13);
            UiKit.FitLabel(hintR, Tutorial.HINTS[s], 16, TextAnchor.MiddleLeft, UiKit.Ink, false, 11);
            if (KBtn(skipR, "SKIP", UiRole.Neutral, false, 18)) { Tutorial.Skip(); _tutHlOn = false; _b.Toast("Tutorial skipped. You can turn it on again on the title screen."); }
        }

        /// <summary>Pulsing amber frame around the control the current step uses (paint only, no touch blocking).</summary>
        void DrawTutorialHighlight()
        {
            if (!_tutHlOn || !Tutorial.Active || Event.current.type != EventType.Repaint) return;
            var r = _tutHl;
            float p = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 4f);
            DrawHalo(r, new Color(1f, 0.72f, 0.30f), 4f);
            var oc = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.55f + 0.45f * p);
            float t = 3f, g = 4f + 3f * p;
            var o = new Rect(r.x - g, r.y - g, r.width + g * 2f, r.height + g * 2f);
            GUI.DrawTexture(new Rect(o.x, o.y, o.width, t), _amberLineTex);
            GUI.DrawTexture(new Rect(o.x, o.yMax - t, o.width, t), _amberLineTex);
            GUI.DrawTexture(new Rect(o.x, o.y, t, o.height), _amberLineTex);
            GUI.DrawTexture(new Rect(o.xMax - t, o.y, t, o.height), _amberLineTex);
            GUI.color = oc;
        }
    }
#endif
}
