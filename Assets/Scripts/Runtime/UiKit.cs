// v0.17 UI kit for the IMGUI screens (stage A of the UI redo). Stage B converts GameUI / SideMenus screens to it.
//   UiLayout   non-overlapping rects: dock Top / Bottom / Left / Right off one area, Rows / Columns / Grid splits.
//   UiFit      font size that fits a rect on ONE line (shrinks to a minimum, then ellipsis): no wrapping, no clipping.
//   UiKit      Panel (textured plate + header), Header, Button (raised face, pressed, selected, disabled), Chip,
//              Divider, Bar, FitLabel, and BeginList / BeginScroll: a clipped vertical list with touch drag,
//              momentum and rubber-band edges that replaces Prev / Next paging. Children (buttons, UiWheel) still
//              get taps; once a drag passes 8 px the list takes over and the child's press is cancelled.
// The top half is pure C# (also compiled by the offline preview); only the #if !DZ_PREVIEW block touches IMGUI.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace DayZero.Runtime
{
    /// <summary>Colour role of a button / chip / bar.</summary>
    public enum UiRole { Primary, Accent, Danger, Neutral, Disabled }

    /// <summary>Carves non-overlapping rects off one area. Each dock takes its strip plus Gap from what is left
    /// (Rest), so two panels docked from the same layout can never overlap.</summary>
    public sealed class UiLayout
    {
        public float Gap;
        Rect _rest;
        public Rect Rest => _rest;

        public UiLayout(Rect area, float gap = 8f) { _rest = area; Gap = gap; }

        /// <summary>The whole virtual screen (GameUI already maps the safe area to 0..w x 0..h) minus a margin.</summary>
        public static UiLayout Screen(float w, float h, float margin = 10f, float gap = 8f) => new UiLayout(new Rect(margin, margin, w - margin * 2f, h - margin * 2f), gap);

        public Rect Top(float h) { h = Mathf.Min(h, _rest.height); var r = new Rect(_rest.x, _rest.y, _rest.width, h); Cut(0, h); return r; }
        public Rect Bottom(float h) { h = Mathf.Min(h, _rest.height); var r = new Rect(_rest.x, _rest.y + _rest.height - h, _rest.width, h); Cut(1, h); return r; }
        public Rect Left(float w) { w = Mathf.Min(w, _rest.width); var r = new Rect(_rest.x, _rest.y, w, _rest.height); Cut(2, w); return r; }
        public Rect Right(float w) { w = Mathf.Min(w, _rest.width); var r = new Rect(_rest.x + _rest.width - w, _rest.y, w, _rest.height); Cut(3, w); return r; }
        /// <summary>Fractional docks (0..1 of what is left).</summary>
        public Rect TopFrac(float f) => Top(_rest.height * f);
        public Rect BottomFrac(float f) => Bottom(_rest.height * f);
        public Rect LeftFrac(float f) => Left(_rest.width * f);
        public Rect RightFrac(float f) => Right(_rest.width * f);

        void Cut(int side, float n)
        {
            float k = Mathf.Min(n + Gap, side < 2 ? _rest.height : _rest.width);
            switch (side)
            {
                case 0: _rest = new Rect(_rest.x, _rest.y + k, _rest.width, _rest.height - k); break;
                case 1: _rest = new Rect(_rest.x, _rest.y, _rest.width, _rest.height - k); break;
                case 2: _rest = new Rect(_rest.x + k, _rest.y, _rest.width - k, _rest.height); break;
                default: _rest = new Rect(_rest.x, _rest.y, _rest.width - k, _rest.height); break;
            }
        }

        public static Rect Inset(Rect r, float pad) => new Rect(r.x + pad, r.y + pad, Mathf.Max(0f, r.width - pad * 2f), Mathf.Max(0f, r.height - pad * 2f));
        public static Rect Inset(Rect r, float l, float t, float rr, float b) => new Rect(r.x + l, r.y + t, Mathf.Max(0f, r.width - l - rr), Mathf.Max(0f, r.height - t - b));
        /// <summary>A w x h rect centred in r (clamped to r).</summary>
        public static Rect Center(Rect r, float w, float h) { w = Mathf.Min(w, r.width); h = Mathf.Min(h, r.height); return new Rect(r.x + (r.width - w) * 0.5f, r.y + (r.height - h) * 0.5f, w, h); }

        /// <summary>Split r into columns. Positive sizes are weights, negative sizes are fixed pixels (abs).</summary>
        public static Rect[] Columns(Rect r, float gap, params float[] sizes) => Split(r, gap, sizes, false);
        /// <summary>Split r into rows. Positive sizes are weights, negative sizes are fixed pixels (abs).</summary>
        public static Rect[] Rows(Rect r, float gap, params float[] sizes) => Split(r, gap, sizes, true);

        static Rect[] Split(Rect r, float gap, float[] sizes, bool rows)
        {
            int n = sizes.Length; var o = new Rect[n];
            if (n == 0) return o;
            float total = (rows ? r.height : r.width) - gap * (n - 1), fixedSum = 0f, wsum = 0f;
            for (int i = 0; i < n; i++) { if (sizes[i] < 0f) fixedSum += -sizes[i]; else wsum += sizes[i]; }
            float free = Mathf.Max(0f, total - fixedSum), p = rows ? r.y : r.x;
            for (int i = 0; i < n; i++)
            {
                float s = sizes[i] < 0f ? -sizes[i] : (wsum > 0f ? free * sizes[i] / wsum : 0f);
                o[i] = rows ? new Rect(r.x, p, r.width, s) : new Rect(p, r.y, s, r.height);
                p += s + gap;
            }
            return o;
        }

        /// <summary>n equal columns.</summary>
        public static Rect[] Even(Rect r, int n, float gap, bool rows = false)
        {
            var s = new float[Mathf.Max(1, n)]; for (int i = 0; i < s.Length; i++) s[i] = 1f;
            return Split(r, gap, s, rows);
        }

        /// <summary>Cell i of a grid with cols columns and fixed cell height, flowing top to bottom.</summary>
        public static Rect Cell(Rect r, int cols, int i, float cellH, float gap)
        {
            float cw = (r.width - gap * (cols - 1)) / cols;
            return new Rect(r.x + (i % cols) * (cw + gap), r.y + (i / cols) * (cellH + gap), cw, cellH);
        }

        public static bool Overlaps(Rect a, Rect b) => a.x < b.x + b.width && b.x < a.x + a.width && a.y < b.y + b.height && b.y < a.y + a.height;
        public static bool Contains(Rect r, Vector2 p) => p.x >= r.x && p.x < r.x + r.width && p.y >= r.y && p.y < r.y + r.height;
    }

    /// <summary>Single-line text fitting with any width measure (IMGUI CalcSize in the game, DejaVu in the preview).</summary>
    public static class UiFit
    {
        public const float LineH = 1.18f;   // line height per font px
        public const string Ellipsis = "...";

        /// <summary>Largest size in [minSize, maxSize] at which text fits w x h on one line. If even minSize is too
        /// wide, shown is cut with "..." to fit. width(text, size) returns the text's pixel width at that size.</summary>
        public static int Size(string text, float w, float h, int maxSize, int minSize, Func<string, int, float> width, out string shown)
        {
            shown = text ?? "";
            if (minSize > maxSize) minSize = maxSize;
            int byH = Mathf.FloorToInt(h / LineH);
            int top = Mathf.Max(minSize, Mathf.Min(maxSize, byH));
            if (shown.Length == 0) return top;
            for (int s = top; s >= minSize; s--) if (width(shown, s) <= w) return s;
            // ellipsis at the minimum size: longest prefix that fits
            int lo = 0, hi = shown.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (width(shown.Substring(0, mid).TrimEnd() + Ellipsis, minSize) <= w) lo = mid; else hi = mid - 1;
            }
            shown = lo > 0 ? shown.Substring(0, lo).TrimEnd() + Ellipsis : (width(Ellipsis, minSize) <= w ? Ellipsis : "");
            return minSize;
        }
    }

    /// <summary>State of one scroll list (kept per id across frames).</summary>
    public sealed class UiScrollState
    {
        public float Offset, Vel, Max;
        public bool Down, Dragging;
        public Vector2 PressPos; public float PressOffset, LastY, LastT;
        public int Frame = -1;
    }

    /// <summary>Scroll physics, pure: drag follows the finger (rubber band past the ends), release keeps the
    /// finger's velocity and decays it, out-of-range offsets spring back.</summary>
    public static class UiScrollPhys
    {
        public const float DragSlop = 8f, Decel = 3.2f, Spring = 14f, Band = 0.45f;

        public static void Press(UiScrollState s, Vector2 p, float t) { s.Down = true; s.Dragging = false; s.PressPos = p; s.PressOffset = s.Offset; s.LastY = p.y; s.LastT = t; s.Vel = 0f; }

        /// <summary>Finger moved while dragging the list.</summary>
        public static void Drag(UiScrollState s, float y, float t)
        {
            float raw = s.PressOffset - (y - s.PressPos.y);
            s.Offset = Rubber(raw, s.Max);
            float dt = t - s.LastT;
            if (dt > 1e-4f) { float v = -(y - s.LastY) / dt; s.Vel = Mathf.Lerp(s.Vel, v, 0.6f); }
            s.LastY = y; s.LastT = t;
        }

        public static void Release(UiScrollState s, float t)
        {
            s.Down = false; s.Dragging = false;
            if (t - s.LastT > 0.08f) s.Vel = 0f;   // finger rested before lifting: no fling
            s.Vel = Mathf.Clamp(s.Vel, -4000f, 4000f);
        }

        static float Rubber(float raw, float max)
        {
            if (raw < 0f) return raw * Band;
            if (raw > max) return max + (raw - max) * Band;
            return raw;
        }

        public static void Step(UiScrollState s, float dt)
        {
            if (s.Down || dt <= 0f) return;
            dt = Mathf.Min(dt, 0.05f);
            bool outside = s.Offset < 0f || s.Offset > s.Max;
            if (Mathf.Abs(s.Vel) > 4f)
            {
                s.Offset += s.Vel * dt;
                s.Vel *= Mathf.Exp(-(outside ? 18f : Decel) * dt);
            }
            else s.Vel = 0f;
            if (s.Offset < 0f || s.Offset > s.Max)
            {
                float b = s.Offset < 0f ? 0f : s.Max;
                if (Mathf.Abs(s.Vel) < 60f) { s.Offset = b + (s.Offset - b) * Mathf.Exp(-Spring * dt); s.Vel = 0f; if (Mathf.Abs(s.Offset - b) < 0.3f) s.Offset = b; }
            }
        }
    }

    /// <summary>Who holds the current touch among nested kit controls (a wheel inside a scroll list).</summary>
    public static class UiInput
    {
        /// <summary>Control id of the wheel that took the press (0 = none); Axis 0 horizontal, 1 vertical;
        /// Locked once that wheel has moved along its axis (the list may then no longer steal the drag).</summary>
        public static int ClaimId, ClaimAxis; public static bool Locked;
        public static void Claim(int id, int axis) { ClaimId = id; ClaimAxis = axis; Locked = axis == 1; }
        public static void Release(int id) { if (ClaimId == id) { ClaimId = 0; Locked = false; } }
    }

#if !DZ_PREVIEW
    /// <summary>One visible window of a BeginList: rows First..Last-1 are on screen; Row(i) is row i's rect in the
    /// list's content coordinates (draw rows there, between BeginList and EndList).</summary>
    public struct UiList
    {
        public int First, Last, Count; public float RowH, Width, Gap;
        public Rect Row(int i) => new Rect(0f, i * (RowH + Gap), Width, RowH);
    }

    public static class UiKit
    {
        // ---------------- colours ----------------
        public static Color RoleColor(UiRole r)
        {
            switch (r)
            {
                case UiRole.Accent: return UiSkin.KitAmber;
                case UiRole.Danger: return UiSkin.KitRust;
                case UiRole.Neutral: return UiSkin.KitSteel;
                case UiRole.Disabled: return new Color(0.30f, 0.30f, 0.27f, 0.8f);
                default: return UiSkin.KitOlive;
            }
        }

        public static Color Ink => UiSkin.KitInk;
        public static Color InkDim => UiSkin.KitInkDim;
        public static Color InkMute => UiSkin.KitInkMute;
        public static Color Amber => new Color(0.96f, 0.72f, 0.30f, 1f);
        public static Color Good => UiSkin.KitGood;
        public static Color Bad => UiSkin.KitBad;

        // ---------------- textures / styles (built once) ----------------
        static bool _ready;
        static GUIStyle _plate, _header, _well, _thumb, _fill, _lbl;
        static readonly GUIStyle[,] _btn = new GUIStyle[5, 4];
        static readonly GUIStyle[] _chip = new GUIStyle[5];
        static Texture2D _grain, _divider, _fade;
        static readonly GUIContent _gc = new GUIContent();

        static GUIStyle Sliced(UiSkin.Sheet s)
        {
            var st = new GUIStyle();
            UiSkin.Apply(st, s);
            return st;
        }

        static void Ensure()
        {
            if (_ready) return;
            _ready = true;
            _plate = Sliced(UiSkin.KitPlate());
            _header = Sliced(UiSkin.KitHeader());
            _well = Sliced(UiSkin.KitWell());
            _thumb = Sliced(UiSkin.KitThumb());
            _fill = Sliced(UiSkin.KitFill());
            for (int r = 0; r < 5; r++)
            {
                for (int s = 0; s < 4; s++) _btn[r, s] = Sliced(UiSkin.KitButton(RoleColor((UiRole)r), s));
                _chip[r] = Sliced(UiSkin.KitChip(r == 0 ? new Color(0.62f, 0.66f, 0.36f) : RoleColor((UiRole)r)));
            }
            _grain = UiSkin.ToTex(UiSkin.KitGrain()); _grain.wrapMode = TextureWrapMode.Repeat;
            _divider = UiSkin.ToTex(UiSkin.KitDivider());
            _fade = UiSkin.ToTex(UiSkin.KitFade());
            _lbl = new GUIStyle(GUI.skin.label) { wordWrap = false, richText = false, clipping = TextClipping.Clip, padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0) };
        }

        internal static GUIStyle WellStyle { get { Ensure(); return _well; } }

        static void DrawStyle(GUIStyle st, Rect r)
        {
            if (Event.current.type == EventType.Repaint) st.Draw(r, false, false, false, false);
        }

        // ---------------- text ----------------
        struct FitKey : IEquatable<FitKey>
        {
            public string T; public int W, H, Max, Min; public bool B;
            public bool Equals(FitKey o) => W == o.W && H == o.H && Max == o.Max && Min == o.Min && B == o.B && string.Equals(T, o.T);
            public override bool Equals(object o) => o is FitKey k && Equals(k);
            public override int GetHashCode() => ((T?.GetHashCode() ?? 0) * 31 + W) * 31 + H * 7 + Max * 131 + Min * 17 + (B ? 1 : 0);
        }
        static readonly Dictionary<FitKey, KeyValuePair<int, string>> _fit = new Dictionary<FitKey, KeyValuePair<int, string>>();
        static bool _measureBold;

        static float Width(string t, int size)
        {
            _lbl.fontSize = size; _lbl.fontStyle = _measureBold ? FontStyle.Bold : FontStyle.Normal;
            _gc.text = t;
            return _lbl.CalcSize(_gc).x;
        }
        static readonly Func<string, int, float> _width = Width;

        /// <summary>Font size + shown text that fit r on one line (cached). Use when you need the size yourself.</summary>
        public static int FitSize(Rect r, string text, int maxSize, int minSize, bool bold, out string shown)
        {
            Ensure();
            var k = new FitKey { T = text, W = Mathf.RoundToInt(r.width), H = Mathf.RoundToInt(r.height), Max = maxSize, Min = minSize, B = bold };
            if (_fit.TryGetValue(k, out var v)) { shown = v.Value; return v.Key; }
            if (_fit.Count > 1024) _fit.Clear();
            _measureBold = bold;
            int s = UiFit.Size(text, r.width - 2f, r.height, maxSize, minSize, _width, out shown);
            _fit[k] = new KeyValuePair<int, string>(s, shown);
            return s;
        }

        /// <summary>One-line label that never wraps or clips: shrinks from maxSize to minSize, then cuts with "...".
        /// Drawn with a 1 px dark shadow for legibility over the map.</summary>
        public static void FitLabel(Rect r, string text, int maxSize = 20, TextAnchor align = TextAnchor.MiddleLeft, Color? color = null, bool bold = true, int minSize = 11, bool shadow = true)
        {
            if (Event.current.type != EventType.Repaint || string.IsNullOrEmpty(text)) return;
            int size = FitSize(r, text, maxSize, minSize, bold, out var shown);
            _lbl.fontSize = size; _lbl.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal; _lbl.alignment = align;
            _gc.text = shown;
            var oc = GUI.color;
            if (shadow)
            {
                _lbl.normal.textColor = new Color(0f, 0f, 0f, 0.7f);
                GUI.Label(new Rect(r.x + 1f, r.y + 1.5f, r.width, r.height), _gc, _lbl);
            }
            _lbl.normal.textColor = color ?? Ink;
            GUI.Label(r, _gc, _lbl);
            GUI.color = oc;
        }

        /// <summary>Wrapped paragraph (body text only; titles and buttons use FitLabel). Returns the height used.</summary>
        public static float Paragraph(Rect r, string text, int size = 17, Color? color = null)
        {
            Ensure();
            _lbl.fontSize = size; _lbl.fontStyle = FontStyle.Normal; _lbl.alignment = TextAnchor.UpperLeft; _lbl.wordWrap = true;
            _gc.text = text ?? "";
            float h = _lbl.CalcHeight(_gc, r.width);
            if (Event.current.type == EventType.Repaint) { _lbl.normal.textColor = color ?? InkDim; GUI.Label(new Rect(r.x, r.y, r.width, Mathf.Max(h, r.height)), _gc, _lbl); }
            _lbl.wordWrap = false;
            return h;
        }

        // ---------------- panels ----------------
        public const float HeaderH = 40f, PanelPad = 12f;

        /// <summary>Textured plate with an optional header strip. Returns the content rect (inside padding, below the
        /// header). Panels drawn from one UiLayout never overlap.</summary>
        public static Rect Panel(Rect r, string title = null, int titleSize = 22)
        {
            Ensure();
            DrawStyle(_plate, r);
            if (Event.current.type == EventType.Repaint)
            {
                var g = UiLayout.Inset(r, 3f);
                GUI.DrawTextureWithTexCoords(g, _grain, new Rect(0f, 0f, g.width / 64f, g.height / 64f));
            }
            if (string.IsNullOrEmpty(title)) return UiLayout.Inset(r, PanelPad);
            Header(new Rect(r.x + 4f, r.y + 4f, r.width - 8f, HeaderH), title, titleSize);
            return UiLayout.Inset(r, PanelPad, HeaderH + 4f + 8f, PanelPad, PanelPad);
        }

        /// <summary>Header strip (amber notch + amber rule) with a fitted title.</summary>
        public static void Header(Rect r, string text, int maxSize = 22)
        {
            Ensure();
            DrawStyle(_header, r);
            FitLabel(new Rect(r.x + 14f, r.y + 2f, r.width - 24f, r.height - 6f), text?.ToUpperInvariant(), maxSize, TextAnchor.MiddleLeft, Amber, true, 12);
        }

        /// <summary>Engraved horizontal divider (a 3 px line centred vertically in r).</summary>
        public static void Divider(Rect r)
        {
            Ensure();
            if (Event.current.type == EventType.Repaint) GUI.DrawTexture(new Rect(r.x, r.y + r.height * 0.5f - 1.5f, r.width, 3f), _divider);
        }

        /// <summary>Small badge with a coloured tab: "8 SURVIVORS", "TF", "HP 100".</summary>
        public static void Chip(Rect r, string text, UiRole role = UiRole.Neutral, int maxSize = 16)
        {
            Ensure();
            DrawStyle(_chip[(int)role], r);
            FitLabel(new Rect(r.x + 9f, r.y, r.width - 13f, r.height), text, maxSize, TextAnchor.MiddleCenter, InkDim, true, 10);
        }

        /// <summary>Progress bar in a recessed well; value text (optional) right-aligned inside.</summary>
        public static void Bar(Rect r, float frac, UiRole role = UiRole.Accent, string text = null)
        {
            Ensure();
            DrawStyle(_well, r);
            frac = Mathf.Clamp01(frac);
            if (frac > 0f && Event.current.type == EventType.Repaint)
            {
                var oc = GUI.color; GUI.color = RoleColor(role) * oc;
                _fill.Draw(new Rect(r.x + 3f, r.y + 3f, (r.width - 6f) * frac, r.height - 6f), false, false, false, false);
                GUI.color = oc;
            }
            if (!string.IsNullOrEmpty(text)) FitLabel(UiLayout.Inset(r, 8f, 1f, 8f, 1f), text, 16, TextAnchor.MiddleRight, Ink, true, 10);
        }

        // ---------------- buttons ----------------
        static readonly int BtnHash = "DZKitButton".GetHashCode();

        /// <summary>Label rect of a kit button's face (above the lip; sunk when pressed).</summary>
        public static Rect ButtonFace(Rect r, bool pressed) => pressed
            ? new Rect(r.x + 4f, r.y + UiSkin.KitPressDrop, r.width - 8f, r.height - UiSkin.KitLip - 1f)
            : new Rect(r.x + 4f, r.y, r.width - 8f, r.height - UiSkin.KitLip);

        /// <summary>Raised button with a fitted one-line label. Returns true on release inside r. A press that turns
        /// into a list drag is cancelled. selected = amber frame (current tab / mode).</summary>
        public static bool Button(Rect r, string text, UiRole role = UiRole.Primary, bool selected = false, int maxSize = 22)
        {
            Ensure();
            bool enabled = GUI.enabled && role != UiRole.Disabled;
            int id = GUIUtility.GetControlID(BtnHash, FocusType.Passive, r);
            var ev = Event.current;
            bool clicked = false;
            switch (ev.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (enabled && r.Contains(ev.mousePosition) && GUIUtility.hotControl == 0) { GUIUtility.hotControl = id; ev.Use(); }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id) ev.Use();
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id) { GUIUtility.hotControl = 0; ev.Use(); clicked = r.Contains(ev.mousePosition); }
                    break;
                case EventType.Repaint:
                    bool down = GUIUtility.hotControl == id && r.Contains(ev.mousePosition);
                    int st = !enabled ? 3 : down ? 1 : selected ? 2 : 0;
                    _btn[(int)(enabled ? role : UiRole.Disabled), st].Draw(r, false, false, false, false);
                    FitLabel(ButtonFace(r, down), text, maxSize, TextAnchor.MiddleCenter, enabled ? Ink : InkMute, true, 11, enabled);
                    break;
            }
            return clicked;
        }

        // ---------------- scroll list ----------------
        static readonly Dictionary<string, UiScrollState> _scroll = new Dictionary<string, UiScrollState>();
        static readonly int ListHash = "DZKitList".GetHashCode();
        struct Open { public UiScrollState S; public Rect View; public float ContentH; }
        static readonly Stack<Open> _open = new Stack<Open>();

        public static UiScrollState ScrollState(string id)
        {
            if (!_scroll.TryGetValue(id, out var s)) { s = new UiScrollState(); _scroll[id] = s; }
            return s;
        }

        /// <summary>Jump a list to an offset (e.g. 0 when a screen opens).</summary>
        public static void ScrollTo(string id, float offset) { var s = ScrollState(id); s.Offset = offset; s.Vel = 0f; }

        /// <summary>Begin a clipped, touch-scrolled view of contentH px. Draw children in content coordinates
        /// (x 0..view.width, y 0..contentH) until EndScroll. Returns the visible window in content coordinates
        /// (cull rows outside it).</summary>
        public static Rect BeginScroll(string id, Rect view, float contentH)
        {
            Ensure();
            var s = ScrollState(id);
            int lid = GUIUtility.GetControlID(ListHash, FocusType.Passive, view);
            s.Max = Mathf.Max(0f, contentH - view.height);
            var ev = Event.current;
            float now = Time.unscaledTime;
            switch (ev.type)   // raw type: the list watches drags even while a child holds hotControl
            {
                case EventType.MouseDown:
                    if (view.Contains(ev.mousePosition) && GUIUtility.hotControl == 0) UiScrollPhys.Press(s, ev.mousePosition, now);
                    break;
                case EventType.MouseDrag:
                    if (!s.Down) break;
                    if (!s.Dragging)
                    {
                        var d = ev.mousePosition - s.PressPos;
                        bool wheelHas = UiInput.ClaimId != 0 && (UiInput.Locked || UiInput.ClaimAxis == 1);
                        if (!wheelHas && Mathf.Abs(d.y) > UiScrollPhys.DragSlop && Mathf.Abs(d.y) > Mathf.Abs(d.x) && s.Max > 0f)
                        {
                            s.Dragging = true;
                            UiInput.Release(UiInput.ClaimId);   // an unlocked horizontal wheel lets go
                            GUIUtility.hotControl = lid;         // cancels the child's press
                            s.PressPos = ev.mousePosition; s.PressOffset = s.Offset; s.LastY = ev.mousePosition.y; s.LastT = now;
                        }
                        else if (GUIUtility.hotControl != 0 && GUIUtility.hotControl != lid && wheelHas) s.Down = false;
                    }
                    if (s.Dragging) { UiScrollPhys.Drag(s, ev.mousePosition.y, now); ev.Use(); }
                    break;
                case EventType.MouseUp:
                    if (s.Down)
                    {
                        bool was = s.Dragging;
                        UiScrollPhys.Release(s, now);
                        if (was) { if (GUIUtility.hotControl == lid) GUIUtility.hotControl = 0; ev.Use(); }
                    }
                    break;
                case EventType.ScrollWheel:
                    if (view.Contains(ev.mousePosition)) { s.Offset = Mathf.Clamp(s.Offset + ev.delta.y * 24f, 0f, s.Max); s.Vel = 0f; ev.Use(); }
                    break;
                case EventType.Repaint:
                    if (s.Frame != Time.frameCount) { s.Frame = Time.frameCount; UiScrollPhys.Step(s, Time.unscaledDeltaTime); }
                    break;
            }
            float off = Mathf.Round(s.Offset);
            GUI.BeginGroup(view);
            GUI.BeginGroup(new Rect(0f, -off, view.width, Mathf.Max(contentH, view.height) + Mathf.Abs(off)));
            _open.Push(new Open { S = s, View = view, ContentH = contentH });
            return new Rect(0f, off, view.width, view.height);
        }

        /// <summary>End a BeginScroll / BeginList: edge fades where more content hides, and a thin position thumb.</summary>
        public static void EndScroll()
        {
            GUI.EndGroup();
            var o = _open.Count > 0 ? _open.Pop() : default;
            if (o.S != null && Event.current.type == EventType.Repaint)
            {
                float w = o.View.width, h = o.View.height, off = o.S.Offset;
                if (off > 1f) GUI.DrawTexture(new Rect(0f, 0f, w, Mathf.Min(28f, off + 6f)), _fade);
                if (off < o.S.Max - 1f) GUI.DrawTextureWithTexCoords(new Rect(0f, h - 28f, w, 28f), _fade, new Rect(0f, 1f, 1f, -1f));
                if (o.S.Max > 0f)
                {
                    float th = Mathf.Max(36f, h * h / Mathf.Max(h, o.ContentH));
                    float y = (h - th) * Mathf.Clamp01(off / o.S.Max);
                    _thumb.Draw(new Rect(w - 7f, y + 2f, 6f, th - 4f), false, false, false, false);
                }
            }
            GUI.EndGroup();
        }

        /// <summary>Scroll list of count rows of rowH (gap between rows). Draw rows First..Last-1 at Row(i), then
        /// call EndList. Replaces Prev / Next paging: every row is one drag away.</summary>
        public static UiList BeginList(string id, Rect view, int count, float rowH, float gap = 6f)
        {
            float step = rowH + gap, contentH = Mathf.Max(0f, count * step - gap);
            var win = BeginScroll(id, new Rect(view.x, view.y, view.width, view.height), contentH);
            int first = Mathf.Clamp(Mathf.FloorToInt((win.y - rowH) / step), 0, count);
            int last = Mathf.Clamp(Mathf.CeilToInt((win.y + win.height) / step) + 1, first, count);
            return new UiList { First = first, Last = last, Count = count, RowH = rowH, Width = view.width - 10f, Gap = gap };
        }

        public static void EndList() => EndScroll();

        /// <summary>Row backing plate for list rows (subtle, alternating).</summary>
        public static void RowPlate(Rect r, int index, bool highlight = false)
        {
            Ensure();
            if (Event.current.type != EventType.Repaint) return;
            var oc = GUI.color;
            GUI.color = highlight ? new Color(1f, 0.85f, 0.55f, 0.95f * oc.a) : new Color(1f, 1f, 1f, (index % 2 == 0 ? 0.55f : 0.35f) * oc.a);
            _well.Draw(r, false, false, false, false);
            GUI.color = oc;
        }
    }
#endif
}
