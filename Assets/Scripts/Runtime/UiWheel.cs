// v0.17 scroll-wheel picker for IMGUI (replaces the "< value >" arrow pickers). A drum of items in a recessed
// well: the centre item sits in an amber band, items away from the centre curve back (packed closer, smaller,
// fainter). Drag (touch or mouse) with momentum, flick lands exactly on an item, tap above / below (left / right)
// the band steps one item. State is kept per id, so any number of wheels can live inside a UiKit scroll list; a
// vertical wheel owns a drag that starts on it, a horizontal wheel lets the list take a mostly-vertical drag.
// The pure half (state, physics, Plan) is also compiled by the offline preview.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace DayZero.Runtime
{
    /// <summary>Per-wheel state (Pos is the drum position in items; Target the item it is settling on).</summary>
    public sealed class UiWheelState
    {
        public float Pos, Target, Vel;
        public bool Init, Down, Dragging, UserMoved;
        public float PressAxis, PressPos, LastAxis, LastT;
        public Vector2 PressPt;
        public int Frame = -1;
    }

    /// <summary>One item as drawn: its rect, how far from the centre (0 = selected), text scale and alpha.</summary>
    public struct UiWheelItem { public int Index; public Rect R; public float Dist, Scale, Alpha; }

    public static partial class UiWheel
    {
        public const float TapSlop = 7f, Settle = 7f;

        /// <summary>Pure drum physics. Released: Pos glides exponentially onto Target (a flick picks the Target its
        /// momentum would land near, so it never overshoots then snaps back).</summary>
        public static void Press(UiWheelState s, float axis, Vector2 pt, float t) { s.Down = true; s.Dragging = false; s.PressAxis = axis; s.PressPt = pt; s.PressPos = s.Pos; s.LastAxis = axis; s.LastT = t; s.Vel = 0f; }

        public static void Drag(UiWheelState s, float axis, float ext, int count, float t)
        {
            if (!s.Dragging && Mathf.Abs(axis - s.PressAxis) > TapSlop) s.Dragging = true;
            if (!s.Dragging) return;
            float raw = s.PressPos - (axis - s.PressAxis) / ext, max = count - 1;
            s.Pos = raw < 0f ? raw * 0.35f : raw > max ? max + (raw - max) * 0.35f : raw;
            float dt = t - s.LastT;
            if (dt > 1e-4f) s.Vel = Mathf.Lerp(s.Vel, -(axis - s.LastAxis) / ext / dt, 0.6f);
            s.LastAxis = axis; s.LastT = t;
        }

        /// <summary>Finger up. tapSide: -1 tap before the band, +1 after it, 0 on it (only used when it was a tap).</summary>
        public static void Release(UiWheelState s, int tapSide, int count, float t)
        {
            s.Down = false;
            int max = Mathf.Max(0, count - 1);
            if (!s.Dragging)
            {
                if (tapSide != 0) { s.Target = Mathf.Clamp(Mathf.RoundToInt(s.Target) + tapSide, 0, max); s.UserMoved = true; }
            }
            else
            {
                if (t - s.LastT > 0.08f) s.Vel = 0f;
                float land = s.Pos + Mathf.Clamp(s.Vel, -40f, 40f) / Settle;
                s.Target = Mathf.Clamp(Mathf.RoundToInt(land), 0, max);
                s.UserMoved = true;
            }
            s.Dragging = false; s.Vel = 0f;
        }

        public static void Step(UiWheelState s, float dt)
        {
            if (s.Down || dt <= 0f) return;
            s.Pos = s.Target + (s.Pos - s.Target) * Mathf.Exp(-Settle * Mathf.Min(dt, 0.05f));
            if (Mathf.Abs(s.Pos - s.Target) < 0.004f) s.Pos = s.Target;
        }

        /// <summary>Settle bookkeeping shared by the game and the preview: returns the index to report.
        /// A user move is reported once when the drum lands; if the caller does not adopt it (or the value changes
        /// elsewhere) the drum follows selected.</summary>
        public static int Resolve(UiWheelState s, int selected, int count)
        {
            selected = Mathf.Clamp(selected, 0, Mathf.Max(0, count - 1));
            if (!s.Init) { s.Init = true; s.Pos = s.Target = selected; }
            int tgt = Mathf.RoundToInt(s.Target);
            if (!s.Down && s.Pos == s.Target)
            {
                if (s.UserMoved) { s.UserMoved = false; if (tgt != selected) return tgt; }
                else if (tgt != selected) { s.Pos = s.Target = selected; }
            }
            return selected;
        }

        /// <summary>Layout of the visible drum items inside r. visible = items shown (odd, 3 or 5).</summary>
        public static int Plan(UiWheelState s, Rect r, int count, int visible, bool horizontal, List<UiWheelItem> into)
        {
            into.Clear();
            visible = Mathf.Max(3, visible | 1);
            float len = horizontal ? r.width : r.height, ext = len / visible;
            float mid = horizontal ? r.x + r.width * 0.5f : r.y + r.height * 0.5f;
            float span = (visible + 1) * 0.5f;          // |d| at which an item reaches the drum's edge
            float radius = len * 0.5f / Mathf.Sin(Mathf.PI * 0.5f * (visible * 0.5f) / span);
            int lo = Mathf.Max(0, Mathf.FloorToInt(s.Pos - span)), hi = Mathf.Min(count - 1, Mathf.CeilToInt(s.Pos + span));
            for (int i = lo; i <= hi; i++)
            {
                float d = i - s.Pos;
                float th = Mathf.PI * 0.5f * d / span;
                if (Mathf.Abs(th) >= Mathf.PI * 0.5f) continue;
                float c = Mathf.Cos(th), off = Mathf.Sin(th) * radius;
                float sz = ext * Mathf.Max(0.35f, c);
                float a = Mathf.Clamp01(c * c * 1.15f - 0.08f);
                if (a <= 0.02f) continue;
                Rect ir = horizontal ? new Rect(mid + off - ext * 0.5f, r.y, ext, r.height) : new Rect(r.x, mid + off - sz * 0.5f, r.width, sz);
                into.Add(new UiWheelItem { Index = i, R = ir, Dist = Mathf.Abs(d), Scale = 0.72f + 0.28f * c, Alpha = a });
            }
            return into.Count;
        }

        /// <summary>The centre band rect (one item extent) of a wheel in r.</summary>
        public static Rect Band(Rect r, int visible, bool horizontal)
        {
            visible = Mathf.Max(3, visible | 1);
            if (horizontal) { float e = r.width / visible; return new Rect(r.x + (r.width - e) * 0.5f, r.y, e, r.height); }
            float h = r.height / visible; return new Rect(r.x, r.y + (r.height - h) * 0.5f, r.width, h);
        }

#if !DZ_PREVIEW
        static readonly Dictionary<string, UiWheelState> _st = new Dictionary<string, UiWheelState>();
        static readonly List<UiWheelItem> _items = new List<UiWheelItem>();
        static readonly int WheelHash = "DZKitWheel".GetHashCode();
        static GUIStyle _bandV, _bandH; static Texture2D _shadeV, _shadeH;

        public static UiWheelState State(string id)
        {
            if (!_st.TryGetValue(id, out var s)) { s = new UiWheelState(); _st[id] = s; }
            return s;
        }

        /// <summary>Forget a wheel's state (e.g. its survivor left).</summary>
        public static void Forget(string id) => _st.Remove(id);

        /// <summary>Vertical drum picker. Returns the selected index: equal to selected until the user lands the
        /// drum on another item, then that item's index (once). Apply it and pass it back as selected next frame.</summary>
        public static int Vertical(string id, Rect r, IList<string> items, int selected, int visible = 3, int maxSize = 20)
            => Draw(id, r, items, selected, visible, false, maxSize);

        /// <summary>Horizontal drum picker (items slide left / right; good in wide list rows).</summary>
        public static int Horizontal(string id, Rect r, IList<string> items, int selected, int visible = 3, int maxSize = 20)
            => Draw(id, r, items, selected, visible, true, maxSize);

        static void EnsureStyles()
        {
            if (_bandV != null) return;
            _bandV = new GUIStyle(); UiSkin.Apply(_bandV, UiSkin.KitBand(false));
            _bandH = new GUIStyle(); UiSkin.Apply(_bandH, UiSkin.KitBand(true));
            _shadeV = UiSkin.ToTex(UiSkin.KitDrumShade(false));
            _shadeH = UiSkin.ToTex(UiSkin.KitDrumShade(true));
        }

        static int Draw(string id, Rect r, IList<string> items, int selected, int visible, bool hz, int maxSize)
        {
            EnsureStyles();
            int count = items != null ? items.Count : 0;
            int cid = GUIUtility.GetControlID(WheelHash, FocusType.Passive, r);
            if (count == 0) return -1;
            var s = State(id);
            int result = Resolve(s, selected, count);
            visible = Mathf.Max(3, visible | 1);
            float ext = (hz ? r.width : r.height) / visible;
            var ev = Event.current; float now = Time.unscaledTime;
            float Axis(Vector2 p) => hz ? p.x : p.y;

            // a parent list stole the touch (mostly-vertical drag over a horizontal wheel)
            if (s.Down && GUIUtility.hotControl != cid && ev.type != EventType.Layout)
            {
                s.Down = false; s.Dragging = false; s.Vel = 0f;
                s.Target = Mathf.Clamp(Mathf.RoundToInt(s.Pos), 0, count - 1);
                s.UserMoved = Mathf.RoundToInt(s.Target) != result;
                UiInput.Release(cid);
            }

            switch (ev.GetTypeForControl(cid))
            {
                case EventType.MouseDown:
                    if (GUI.enabled && r.Contains(ev.mousePosition) && GUIUtility.hotControl == 0)
                    {
                        GUIUtility.hotControl = cid; UiInput.Claim(cid, hz ? 0 : 1);
                        Press(s, Axis(ev.mousePosition), ev.mousePosition, now); ev.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == cid)
                    {
                        Drag(s, Axis(ev.mousePosition), ext, count, now);
                        if (s.Dragging) UiInput.Locked = true;
                        ev.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == cid)
                    {
                        GUIUtility.hotControl = 0; UiInput.Release(cid);
                        var band = Band(r, visible, hz);
                        float a = Axis(ev.mousePosition), b0 = hz ? band.x : band.y, b1 = b0 + ext;
                        Release(s, a < b0 ? -1 : a > b1 ? 1 : 0, count, now);
                        ev.Use();
                    }
                    break;
                case EventType.ScrollWheel:
                    if (r.Contains(ev.mousePosition) && !s.Down)
                    {
                        s.Target = Mathf.Clamp(Mathf.RoundToInt(s.Target) + (ev.delta.y > 0 ? 1 : -1), 0, count - 1); s.UserMoved = true; ev.Use();
                    }
                    break;
                case EventType.Repaint:
                    if (s.Frame != Time.frameCount) { s.Frame = Time.frameCount; Step(s, Time.unscaledDeltaTime); }
                    Paint(s, r, items, count, visible, hz, maxSize);
                    break;
            }
            return result;
        }

        static void Paint(UiWheelState s, Rect r, IList<string> items, int count, int visible, bool hz, int maxSize)
        {
            var well = UiKit.WellStyle;
            well.Draw(r, false, false, false, false);
            var band = Band(r, visible, hz);
            (hz ? _bandH : _bandV).Draw(hz ? new Rect(band.x, r.y + 3f, band.width, r.height - 6f) : new Rect(r.x + 3f, band.y, r.width - 6f, band.height), false, false, false, false);
            var inner = UiLayout.Inset(r, 3f);
            GUI.BeginGroup(inner);
            var local = new Rect(r.x - inner.x, r.y - inner.y, r.width, r.height);
            Plan(s, local, count, visible, hz, _items);
            var oc = GUI.color;
            for (int k = 0; k < _items.Count; k++)
            {
                var it = _items[k];
                bool centre = it.Dist < 0.5f;
                var col = centre ? new Color(1f, 0.86f, 0.52f, 1f) : UiSkin.KitInkDim;
                col.a = it.Alpha;
                int size = Mathf.Max(10, Mathf.RoundToInt(maxSize * it.Scale));
                float ext2 = (hz ? local.width : local.height) / visible;
                var tr = hz ? UiLayout.Inset(it.R, 4f, 0f, 4f, 0f) : new Rect(it.R.x + 8f, it.R.y + it.R.height * 0.5f - ext2 * 0.5f, it.R.width - 16f, ext2);
                // fit at full extent so the size never pumps while it scrolls, then centre in the squashed slot
                UiKit.FitLabel(tr, items[it.Index], size, TextAnchor.MiddleCenter, col, centre, 10, centre);
            }
            GUI.color = oc;
            GUI.EndGroup();
            if (hz) GUI.DrawTexture(new Rect(r.x + 3f, r.y + 3f, r.width - 6f, r.height - 6f), _shadeH);
            else GUI.DrawTexture(new Rect(r.x + 3f, r.y + 3f, r.width - 6f, r.height - 6f), _shadeV);
        }
#endif
    }
}
