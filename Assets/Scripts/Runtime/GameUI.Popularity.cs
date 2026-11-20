// v0.19 leader popularity UI (EXPANDED rules only; Core/Popularity.cs has the rules and the numbers).
// * SupportView (pure, also compiled by the offline preview): draw ops for the SUPPORT row in the stats card, the
//   SUPPORT sheet (what people say, who is unhappy, TALK buttons), the mutiny / CAMP VOTE banner and the exile screen,
//   so the game and the preview lay them out with the same code.
// * GameUI part: draws them, opens / closes the sheet, turns Game.SupportNote into toasts and alerts, and after an
//   exile closes what was open, refreshes the map and shows the exile screen until START OVER.
// Nothing here writes sim state except ExileShown (the "seen" flag of the exile screen). Orders are never blocked.
using System;
using System.Collections.Generic;
using System.Linq;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public static class SupportView
    {
        public enum OpK { Panel, Plate, Well, Text, Button, Bar }
        public sealed class Op
        {
            public OpK K; public Rect R; public string T = "", Id = ""; public int Size = 18; public bool Bold = true;
            public Color Col = new Color(1f, 1f, 1f, 1f); public bool Center; public UiRole Role = UiRole.Primary; public float Frac; public int Idx;
        }

        static readonly Color Amber = new Color(0.96f, 0.72f, 0.30f, 1f);
        static readonly Color Orange = new Color(0.93f, 0.50f, 0.22f, 1f);
        static readonly Color Green = new Color(0.55f, 0.78f, 0.38f, 1f);
        static readonly Color Red = new Color(0.94f, 0.33f, 0.26f, 1f);
        static Color Ink => UiSkin.KitInk;
        static Color Dim => UiSkin.KitInkDim;
        static Color Mute => UiSkin.KitInkMute;

        public static Color BandColor(int band) => band <= 0 ? Green : band == 1 ? Amber : band == 2 ? Orange : Red;
        public static UiRole BandRole(int band) => band <= 0 ? UiRole.Primary : band == 1 ? UiRole.Accent : UiRole.Danger;
        static Color WordColor(float t) => t >= Game.SUP_STEADY ? Green : t >= Game.SUP_UNEASY ? Amber : t >= Game.SUP_ANGRY ? Orange : Red;

        static Op T(Rect r, string t, int size, Color c, bool bold = true, bool center = false) => new Op { K = OpK.Text, R = r, T = t ?? "", Size = size, Col = c, Bold = bold, Center = center };

        /// <summary>The tap area of the SUPPORT row: the row plus the bottom edge of the stats card (about 48 px tall).</summary>
        public static Rect RowHit(Rect row, Rect stats) => new Rect(stats.x, row.y - 7f, stats.width, stats.yMax - row.y + 7f);

        /// <summary>SUPPORT row in the stats card: label, bar, word. p &lt; 0: not measured yet.</summary>
        public static List<Op> Row(Rect row, double p, int band, bool vote, bool pulseOn)
        {
            var ops = new List<Op>();
            ops.Add(new Op { K = OpK.Plate, R = row, Idx = band >= 2 ? 1 : 0 });
            var cols = UiLayout.Columns(UiLayout.Inset(row, 8f, 0f, 6f, 0f), 6f, -76f, 1f, -82f);
            ops.Add(T(cols[0], "SUPPORT", 14, Dim, true));
            if (p < 0) { ops.Add(T(cols[2], "-", 15, Mute, true)); return ops; }
            ops.Add(new Op { K = OpK.Bar, R = UiLayout.Inset(cols[1], 0f, 6f, 0f, 6f), Frac = Mathf.Clamp01((float)p), Role = BandRole(band) });
            string word = vote ? "VOTE" : Game.SUP_WORDS[Mathf.Clamp(band, 0, 3)];
            var col = BandColor(band);
            if (vote && !pulseOn) col = new Color(col.r, col.g, col.b, 0.45f);
            ops.Add(T(cols[2], word.ToUpperInvariant(), 16, col, true));
            return ops;
        }

        public static Rect SheetRect(float w, float h)
        {
            float pw = Mathf.Min(1180f, w - 200f), ph = Mathf.Min(620f, h - 70f);
            return new Rect((w - pw) * 0.5f + 20f, Mathf.Max(40f, (h - ph) * 0.5f + 10f), pw, ph);
        }

        /// <summary>The SUPPORT sheet. Buttons: "talk:&lt;id&gt;", "close".</summary>
        public static List<Op> Sheet(float w, float h, SupportReport r, Func<string, int, bool, float> measure)
        {
            var ops = new List<Op>();
            var pr = SheetRect(w, h);
            ops.Add(new Op { K = OpK.Panel, R = pr, T = "SUPPORT - HOW YOUR PEOPLE SEE YOU" });
            var L = new UiLayout(UiLayout.Inset(pr, 14f, 54f, 14f, 12f), 8f);
            // big word, bar, trend
            var top = UiLayout.Columns(L.Top(50f), 14f, -200f, 1f, -150f);
            var bc = BandColor(r.Band);
            ops.Add(T(top[0], r.Vote ? "VOTE CALLED" : r.Word.ToUpperInvariant(), 30, bc, true));
            ops.Add(new Op { K = OpK.Bar, R = UiLayout.Inset(top[1], 0f, 10f, 0f, 10f), Frac = Mathf.Clamp01((float)Math.Max(0.0, r.P)), Role = BandRole(r.Band) });
            ops.Add(T(top[2], r.Trend, 20, r.Trend == "Falling" ? Orange : r.Trend == "Rising" ? Green : Dim, true));
            // status (one or two lines)
            var stR = L.Top(54f);
            var stLines = Wrap(r.Status, stR.width, 18, true, measure);
            for (int i = 0; i < stLines.Count && i < 2; i++)
                ops.Add(T(new Rect(stR.x, stR.y + i * 26f, stR.width, 26f), stLines[i], 18, r.Vote || r.Band >= 3 ? Red : Ink, true));
            var bottom = L.Bottom(60f);
            var cols = UiLayout.Columns(L.Rest, 16f, 1.1f, 1f);

            // what they say
            var lc = cols[0];
            ops.Add(T(new Rect(lc.x, lc.y, lc.width, 26f), "WHAT THEY SAY", 16, Amber, true));
            float y = lc.y + 32f;
            int idx = 0;
            var rows = r.Bad.Select(x => (x, false)).Concat(r.Good.Take(Math.Max(1, 5 - r.Bad.Count)).Select(x => (x, true))).ToList();
            foreach (var (x, good) in rows)
            {
                if (y + 62f > lc.yMax) break;
                var rr = new Rect(lc.x, y, lc.width, 58f);
                ops.Add(new Op { K = OpK.Plate, R = rr, Idx = idx++ });
                var ri = UiLayout.Inset(rr, 12f, 4f, 10f, 4f);
                ops.Add(T(new Rect(ri.x, ri.y, ri.width, 28f), "\"" + x.Text + "\"", 18, good ? Green : Ink, false));
                string who = x.N <= 1 ? "- " + x.Who : string.Format("- {0} and {1} other{2}", x.Who, x.N - 1, x.N - 1 == 1 ? "" : "s");
                ops.Add(T(new Rect(ri.x, ri.y + 28f, ri.width, 22f), who, 14, Dim, false));
                y += 64f;
            }
            if (rows.Count == 0) ops.Add(T(new Rect(lc.x, y, lc.width, 28f), "Nobody has much to say yet.", 17, Dim, false));

            // who is unhappy (TALK buttons)
            var rc = cols[1];
            ops.Add(T(new Rect(rc.x, rc.y, rc.width, 26f), r.Vote ? "HOW THEY WOULD VOTE" : "LEAST HAPPY WITH YOU", 16, Amber, true));
            y = rc.y + 32f; idx = 0;
            foreach (var p in r.People)
            {
                if (y + 62f > rc.yMax) break;
                var rr = new Rect(rc.x, y, rc.width, 58f);
                ops.Add(new Op { K = OpK.Plate, R = rr, Idx = idx++, });
                var c2 = UiLayout.Columns(UiLayout.Inset(rr, 12f, 4f, 4f, 4f), 8f, 1f, -104f);
                ops.Add(T(new Rect(c2[0].x, c2[0].y, c2[0].width, 26f), p.Name, 18, Ink, true));
                string sub = p.Word + (string.IsNullOrEmpty(p.Why) ? "" : ": \"" + p.Why + "\"");
                ops.Add(T(new Rect(c2[0].x, c2[0].y + 26f, c2[0].width, 22f), sub, 14, WordColor(p.Trust), false));
                ops.Add(new Op { K = OpK.Button, R = UiLayout.Center(c2[1], 104f, 50f), T = "TALK", Id = "talk:" + p.Id, Role = UiRole.Primary, Size = 18 });
                y += 64f;
            }
            var bb = UiLayout.Columns(bottom, 12f, 1f, -220f);
            ops.Add(T(bb[0], "Feed them. Fix the walls. TALK to them - they remember. Your orders are always followed.", 15, Dim, false));
            ops.Add(new Op { K = OpK.Button, R = bb[1], T = "CLOSE", Id = "close", Role = UiRole.Danger, Size = 20 });
            return ops;
        }

        /// <summary>Banner in the top lane while support is at Mutiny or a vote is called. Button id "open".</summary>
        public static List<Op> Banner(Rect lane, float y, SupportReport r)
        {
            var ops = new List<Op>();
            if (!r.ExileOn || (!r.Vote && r.Band < 3)) return ops;
            float bw = Mathf.Min(620f, lane.width);
            var b = new Rect(Mathf.Floor(lane.x + (lane.width - bw) * 0.5f), y, bw, 64f);
            ops.Add(new Op { K = OpK.Panel, R = b, T = "" });
            var c = UiLayout.Columns(UiLayout.Inset(b, 14f, 6f, 8f, 8f), 10f, 1f, -150f);
            string l1, l2;
            if (r.Vote)
            {
                int hh = (int)Math.Ceiling(r.VoteInH);
                l1 = string.Format("CAMP VOTE IN {0}H - {1} FOR YOU, {2} AGAINST", hh, r.For, r.Against);
                l2 = "Last chance: feed them, fix the walls, talk to them.";
            }
            else
            {
                l1 = "MUTINY - A VOTE ON YOU IS COMING";
                l2 = string.Format("They call a vote in about {0}h if nothing changes. Feed them, talk to them.", Math.Max(1, (int)Math.Ceiling(r.ClockLeftH)));
            }
            ops.Add(T(new Rect(c[0].x, c[0].y, c[0].width, 28f), l1, 20, Red, true));
            ops.Add(T(new Rect(c[0].x, c[0].y + 28f, c[0].width, 22f), l2, 15, Ink, false));
            ops.Add(new Op { K = OpK.Button, R = UiLayout.Center(c[1], 150f, 48f), T = "WHY?", Id = "open", Role = UiRole.Danger, Size = 19 });
            return ops;
        }

        /// <summary>The exile screen. Buttons: "go" (START OVER), "look" (look at their camp).</summary>
        public static List<Op> Exile(float w, float h, ExileInfo e, Func<string, int, bool, float> measure)
        {
            var ops = new List<Op>();
            float pw = Mathf.Min(1060f, w - 160f), ph = Mathf.Min(600f, h - 60f);
            var pr = new Rect((w - pw) * 0.5f, (h - ph) * 0.5f, pw, ph);
            ops.Add(new Op { K = OpK.Panel, R = pr, T = "VOTED OUT" });
            var L = new UiLayout(UiLayout.Inset(pr, 22f, 58f, 22f, 16f), 6f);
            ops.Add(T(L.Top(40f), string.Format("Day {0}. The camp voted: {1} for you, {2} against.", e.Day, e.For, e.Against), 26, Red, true));
            string first = e.Leader.Split(' ')[0];
            var paras = new List<(string t, int s, Color c, bool b)>
            {
                (string.Format("{0} is now {1}, led by {2} ({3}). They keep the camp, its {4} people and everything in the stores.", e.OldName, e.CampName, e.Leader, e.LeaderJob, e.Pop), 19, Ink, false),
            };
            if (e.Why.Count > 0) paras.Add(("What they said: " + string.Join("  ", e.Why.Take(3).Select(x => "\"" + x + "\"")), 17, Dim, false));
            paras.Add((e.Att == "Hostile"
                ? string.Format("{0} remembers how you ruled and wants nothing to do with you. They let you walk away; stay clear of them.", first)
                : string.Format("{0} remembers how you ruled. They let you walk away. In time you might even trade with them.", first), 17, Dim, false));
            if (!string.IsNullOrEmpty(e.Quote)) paras.Add((string.Format("{0}: \"{1}\"", first, e.Quote), 17, Ink, false));
            if (!string.IsNullOrEmpty(e.Story)) paras.Add((string.Format("Who {0} is: {1}", first, e.Story), 15, Mute, false));
            var loyal = e.With.Where(x => !x.EndsWith(" (drifter)")).ToList();
            var drift = e.With.Where(x => x.EndsWith(" (drifter)")).ToList();
            string crew = loyal.Count > 0
                ? Join(loyal) + (loyal.Count == 1 ? ", who still believes in you" : ", who still believe in you") + (drift.Count > 0 ? ", and " + Join(drift) + ", drifters you met on the road" : "")
                : "nobody from the old camp. " + Join(drift) + " fell in with you on the road";
            paras.Add((string.Format("You start over in {0} with {1}. Your research comes with you. Everything else starts from nothing.", e.Site, crew), 19, Amber, true));
            var body = L.Rest;
            float y = body.y + 6f;
            var bottom = new Rect(body.x, body.yMax - 66f, body.width, 66f);
            foreach (var (t, s, c, b) in paras)
            {
                foreach (var line in Wrap(t, body.width, s, b, measure))
                {
                    if (y + 28f > bottom.y - 6f) break;
                    ops.Add(T(new Rect(body.x, y, body.width, 28f), line, s, c, b));
                    y += 28f;
                }
                y += 10f;
            }
            var bb = UiLayout.Columns(bottom, 14f, 1f, 1.4f);
            ops.Add(new Op { K = OpK.Button, R = bb[0], T = "LOOK AT THEIR CAMP", Id = "look", Role = UiRole.Neutral, Size = 20 });
            ops.Add(new Op { K = OpK.Button, R = bb[1], T = "START OVER", Id = "go", Role = UiRole.Accent, Size = 24 });
            return ops;
        }

        static string Join(List<string> n)
        {
            var s = n.Select(x => x.Replace(" (drifter)", "")).ToList();
            if (s.Count == 0) return "nobody";
            if (s.Count == 1) return s[0];
            return string.Join(", ", s.Take(s.Count - 1)) + " and " + s[s.Count - 1];
        }

        public static List<string> Wrap(string text, float width, int size, bool bold, Func<string, int, bool, float> measure)
        {
            var o = new List<string>(); var cur = "";
            foreach (var word in (text ?? "").Split(' '))
            {
                if (word.Length == 0) continue;
                string t = cur.Length == 0 ? word : cur + " " + word;
                if (cur.Length > 0 && measure(t, size, bold) > width) { o.Add(cur); cur = word; }
                else cur = t;
            }
            if (cur.Length > 0) o.Add(cur);
            return o;
        }
    }

#if !DZ_PREVIEW
    public sealed partial class GameUI
    {
        bool _supOpen;
        SupportReport _supRep; double _supRepT = -1; float _supRepRt; GameState _supRepS;
        Game _supHooked;

        public bool SupportOpen => _supOpen && Game.ExpansionRules && _b != null && _b.State == Bootstrap.Screen2.Playing && !_b.Picking;
        public bool ExileOpen => Game.ExpansionRules && _b != null && _b.State == Bootstrap.Screen2.Playing && !_b.Picking && G?.S != null && G.PendingExile() != null;

        SupportReport SupRep(bool fresh = false)
        {
            var g = G;
            if (g?.S == null) return null;
            bool stale = _supRep == null || !ReferenceEquals(_supRepS, g.S) || Math.Abs(g.S.T - _supRepT) >= 1.0 || (fresh && Time.unscaledTime - _supRepRt > 1.5f);
            if (stale)
            {
                try { _supRep = g.SupportNow(); } catch (Exception e) { Debug.LogException(e); _supRep = new SupportReport(); }
                _supRepT = g.S.T; _supRepS = g.S; _supRepRt = Time.unscaledTime;
            }
            return _supRep;
        }

        /// <summary>Per frame (hook in DrawGame): listen to the sim's support notes.</summary>
        void SupportTick()
        {
            var g = G;
            if (ReferenceEquals(_supHooked, g)) return;
            if (_supHooked != null) _supHooked.SupportNote -= OnSupportNote;
            _supHooked = g;
            if (g != null) g.SupportNote += OnSupportNote;
        }

        void OnSupportNote(string kind, string text)
        {
            try
            {
                _supRep = null;
                if (kind == "grumble" || kind == "up") return;   // in the log only
                var mb = G?.MainBase();
                AlertFeed.Add("support", text, mb != null, mb != null ? AlertFeed.BaseSpot(mb) : Vector2.zero, G?.S?.T ?? 0.0);
                if (kind == "vote" || kind == "lost" || kind == "exile") AlertFeed.AttackFlag = true;   // fast-forward stops for the vote
                if (kind == "exile")
                {
                    _supOpen = false; CloseTalk(); _menuOpen = false; LongPressInfo.Clear();
                    var map = MapView.Instance;
                    if (map != null) { map.ClearSelection(); map.OnCampFounded(); }
                    return;
                }
                _b.Toast(text);
            }
            catch (Exception e) { Debug.LogException(e); }
        }

        /// <summary>The SUPPORT row in the stats card (replaces the colony line with the expanded rules).</summary>
        void DrawSupportRow()
        {
            var c = G?.C; var L = c?.Lead;
            var row = _hf.SupportRow;
            bool vote = L != null && L.VoteT > 0;
            foreach (var o in SupportView.Row(row, L?.P ?? -1.0, L?.Band ?? 0, vote, Mathf.Repeat(Time.unscaledTime, 1f) < 0.6f)) Paint(o);
            if (GUI.Button(Blk(SupportView.RowHit(row, _hf.Stats)), GUIContent.none, GUIStyle.none)) { _supOpen = true; _b.PlayClick(); SupRep(true); }
        }

        void DrawSupportBanner(float w, ref float y)
        {
            if (!Game.ExpansionRules || G?.C == null) return;
            var L = G.C.Lead;
            if (L == null || (L.VoteT <= 0 && L.Band < 3)) return;
            var r = SupRep();
            if (r == null) return;
            var ops = SupportView.Banner(TopLane(w), y, r);
            if (ops.Count == 0) return;
            string hit = null;
            foreach (var o in ops) { if (o.K == SupportView.OpK.Panel) Blk(o.R); if (Paint(o)) hit = o.Id; }
            if (hit == "open") { _supOpen = true; SupRep(true); }
            y += 70f;
        }

        void DrawSupportSheet(float w, float h)
        {
            if (!SupportOpen) return;
            var r = SupRep(true);
            if (r == null) { _supOpen = false; return; }
            string hit = null;
            foreach (var o in SupportView.Sheet(w, h, r, (t, s, b) => TextW(t, s, b))) if (Paint(o)) hit = o.Id;
            if (hit == null) return;
            if (hit == "close") { _supOpen = false; return; }
            if (hit.StartsWith("talk:") && int.TryParse(hit.Substring(5), out int pid)) { _supOpen = false; OpenTalk(pid); }
        }

        void DrawExileScreen(float w, float h)
        {
            var g = G; var e = g?.PendingExile();
            if (e == null) return;
            string hit = null;
            foreach (var o in SupportView.Exile(w, h, e, (t, s, b) => TextW(t, s, b))) if (Paint(o)) hit = o.Id;
            if (hit == null) return;
            g.ExileShown();
            var map = MapView.Instance;
            if (hit == "look" && map != null) map.ViewCamp(e.CampId);
            else if (map != null) { var mb = g.MainBase(); if (mb != null) map.FlyTo(new Vector2(mb.Pos.x + 0.5f, mb.Pos.y + 0.5f)); }
        }

        /// <summary>Draws one op; true when it is a button that was tapped.</summary>
        bool Paint(SupportView.Op o)
        {
            switch (o.K)
            {
                case SupportView.OpK.Panel: UiKit.Panel(Blk(o.R), string.IsNullOrEmpty(o.T) ? null : o.T); break;
                case SupportView.OpK.Plate: UiKit.RowPlate(o.R, o.Idx); break;
                case SupportView.OpK.Well: UiKit.RowPlate(o.R, 1); break;
                case SupportView.OpK.Text: UiKit.FitLabel(o.R, o.T, o.Size, o.Center ? TextAnchor.MiddleCenter : TextAnchor.MiddleLeft, o.Col, o.Bold, 10); break;
                case SupportView.OpK.Bar: UiKit.Bar(o.R, o.Frac, o.Role); break;
                case SupportView.OpK.Button: return KBtn(o.R, o.T, o.Role, false, o.Size);
            }
            return false;
        }
    }
#endif
}
