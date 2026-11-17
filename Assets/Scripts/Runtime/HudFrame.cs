// v0.17 HUD layout (stage B of the UI redo): every in-game HUD rect and the tabbed menu frame, computed once per
// frame from the virtual screen size with the UiLayout rules, so no two HUD panels can overlap. Pure C#: GameUI
// draws from it and the offline preview lays out the very same rects.
//   top-left   stats card (survivors / beds / zombies / dead, time, phase, colony line)
//   next to it season / weather chip + noise gauge (expanded rules only)
//   top centre MENU         top right   ZOMBIES KILLED / RESCUED counters, resource chips under them
//   left       HOME + - MAP DANGER column      bottom centre  PAUSE x1 +1h +6h segmented control
// Other HUD owners keep their own spots: banners start at BannerY (centre), right-side panels at y 108, bottom sheets
// end above y = h - 72 (TimeBar starts at h - 70).
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed class HudFrame
    {
        public const float M = 10f, GAP = 8f;
        public const int CHIPS = 7;

        public Rect Stats, StatsIn;
        public Rect[] StatCell = new Rect[4];
        public Rect TimeRow, PhaseRow, ColonyRow;
        public Rect World, SeasonRow, NoiseRow;
        public Rect Menu, Counters;
        public Rect[] CounterHalf = new Rect[2];
        public Rect[] Chips = new Rect[CHIPS];
        public bool ChipNames;            // chips wide enough to show the resource name next to the value
        public Rect[] Cam;                // HOME, +, -, MAP (+ DANGER with the expanded rules)
        public Rect TimeBar;
        public Rect[] TimeSeg = new Rect[4];
        public float BannerY;

        public static HudFrame Make(float w, float h, bool expansion)
        {
            var f = new HudFrame();
            f.Stats = new Rect(M, M, 300f, 190f);
            f.StatsIn = UiLayout.Inset(f.Stats, 14f, 10f, 14f, 10f);
            var rows = UiLayout.Rows(f.StatsIn, 4f, -38f, -38f, -32f, -24f, -22f);
            for (int r = 0; r < 2; r++)
            {
                var c = UiLayout.Columns(rows[r], 10f, 1f, 1f);
                f.StatCell[r * 2] = c[0]; f.StatCell[r * 2 + 1] = c[1];
            }
            f.TimeRow = rows[2]; f.PhaseRow = rows[3]; f.ColonyRow = rows[4];

            f.Menu = new Rect(w * 0.5f - 75f, M, 150f, 54f);
            float wx = f.Stats.xMax + GAP;
            float ww = Mathf.Clamp(f.Menu.x - 12f - wx, 170f, 300f);
            f.World = expansion ? new Rect(wx, M, ww, 86f) : new Rect(wx, M, 0f, 0f);
            if (expansion)
            {
                var wr = UiLayout.Rows(UiLayout.Inset(f.World, 6f, 6f, 6f, 8f), 4f, 1f, 1f);
                f.SeasonRow = wr[0]; f.NoiseRow = wr[1];
            }

            float cw = Mathf.Min(400f, w - f.Menu.xMax - 12f - M);
            f.Counters = new Rect(w - M - cw, M, cw, 50f);
            var halves = UiLayout.Columns(UiLayout.Inset(f.Counters, 12f, 4f, 12f, 6f), 10f, 1f, 1f);
            f.CounterHalf[0] = halves[0]; f.CounterHalf[1] = halves[1];

            // resource chips: right-aligned under the counters, never left of the stats / world panels
            float left = (expansion ? f.World.xMax : f.Stats.xMax) + 12f, right = w - M;
            float chipW = Mathf.Min(132f, (right - left - 6f * (CHIPS - 1)) / CHIPS);
            f.ChipNames = chipW >= 116f;
            float x = right;
            for (int i = CHIPS - 1; i >= 0; i--) { x -= chipW; f.Chips[i] = new Rect(x, 68f, chipW, 36f); x -= 6f; }
            f.BannerY = 112f;

            int n = expansion ? 5 : 4;
            f.Cam = new Rect[n];
            float y = f.Stats.yMax + 14f;
            for (int i = 0; i < n; i++) { f.Cam[i] = new Rect(M, y, 84f, 56f); y += 64f; }

            // one segmented control: wider PLAY / PAUSE, then x1 +1h +6h
            float tw = 4f + 104f + 3f * (4f + 80f) + 4f;
            f.TimeBar = new Rect(w * 0.5f - tw * 0.5f, h - 70f, tw, 64f);
            var segs = UiLayout.Columns(UiLayout.Inset(f.TimeBar, 4f), 4f, -104f, 1f, 1f, 1f);
            for (int i = 0; i < 4; i++) f.TimeSeg[i] = segs[i];
            return f;
        }

        /// <summary>Every HUD rect labels on the map must stay clear of.</summary>
        public void Avoid(System.Collections.Generic.List<Rect> into)
        {
            into.Add(Stats); if (World.width > 0f) into.Add(World); into.Add(Menu); into.Add(Counters);
            foreach (var c in Chips) into.Add(c);
            foreach (var c in Cam) into.Add(c);
            into.Add(TimeBar);
        }
    }

    /// <summary>Tabbed menu frame (COLONY / JOBS / LOG / MENU + CLOSE) and the content area under it.</summary>
    public sealed class MenuFrame
    {
        public Rect TabPlate, Close, Content;
        public Rect[] Tabs = new Rect[4];

        public static MenuFrame Make(float w, float h)
        {
            var f = new MenuFrame();
            var L = UiLayout.Screen(w, h, 14f, 10f);
            f.TabPlate = L.Top(68f);
            var cols = UiLayout.Columns(UiLayout.Inset(f.TabPlate, 8f, 6f, 8f, 6f), 8f, 1f, 1f, 1f, 1f, 0.5f, 0.9f);
            for (int i = 0; i < 4; i++) f.Tabs[i] = cols[i];
            f.Close = cols[5];
            f.Content = L.Rest;
            return f;
        }
    }

    /// <summary>Greedy label placement: a label is shown only where it overlaps no HUD rect and no label placed
    /// before it (callers add labels in priority order).</summary>
    public sealed class LabelPlacer
    {
        readonly System.Collections.Generic.List<Rect> _placed = new System.Collections.Generic.List<Rect>();
        public readonly System.Collections.Generic.List<Rect> Avoid = new System.Collections.Generic.List<Rect>();
        public void Clear() { _placed.Clear(); Avoid.Clear(); }

        /// <summary>True (and reserved) when r is clear of the HUD and every placed label.</summary>
        public bool Place(Rect r, float pad = 2f)
        {
            var p = new Rect(r.x - pad, r.y - pad, r.width + pad * 2f, r.height + pad * 2f);
            foreach (var a in Avoid) if (UiLayout.Overlaps(p, a)) return false;
            foreach (var a in _placed) if (UiLayout.Overlaps(p, a)) return false;
            _placed.Add(r);
            return true;
        }

        /// <summary>Like Place but tries a few upward nudges first (up to maxUp px).</summary>
        public bool PlaceNudged(ref Rect r, float maxUp = 36f)
        {
            for (float dy = 0f; dy <= maxUp; dy += 12f)
            {
                var t = new Rect(r.x, r.y - dy, r.width, r.height);
                if (Place(t)) { r = t; return true; }
            }
            return false;
        }

        /// <summary>True when r touches a HUD rect (pins under the HUD are faded).</summary>
        public bool UnderHud(Rect r) { foreach (var a in Avoid) if (UiLayout.Overlaps(r, a)) return true; return false; }
    }
}
