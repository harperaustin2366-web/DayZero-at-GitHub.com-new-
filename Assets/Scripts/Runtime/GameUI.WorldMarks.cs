// v0.14.0a: floating map markers (MapView.World.cs gathers them, MapView taps them):
//   * faction camps: a pin in the stance colour (hostile red, neutral amber, friendly green) with a flag, the
//     camp name and its stance, readable zoomed all the way out
//   * survivors waiting for rescue: a small green pin with a person, so the player can find them (v0.19.2: gone the
//     moment they are rescued / lost; blue "TEAM ON THE WAY" while a task force heads there; grey "NO BEDS" when every
//     bed is taken)
//   * wrecks: a jerrycan marker (own lost vehicles green), and a tow job's progress bar over the squad
// Also keeps the camps / wrecks world layer current (MapView.UpdateWorldLayer).
// v0.17: labels are planned first (PlanMarks, from DrawMapLabels, through the shared LabelPlacer): a camp name, SOS or
// wreck label is drawn only where it overlaps no HUD rect and no other label; pins under the HUD fade out.
using System.Collections.Generic;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed partial class GameUI
    {
        static Texture2D _pinTex, _flagTex, _personTex;

        /// <summary>White-on-transparent glyphs (tinted with GUI.color): a map pin, a flag, a person.</summary>
        static void EnsureMarkTex()
        {
            if (_pinTex != null) return;
            _pinTex = Glyph((x, y) =>
            {
                // disc (centre 32,24 r 21) with a point down to (32,62); a dark rim
                float dx = x - 32f, dy = y - 24f, r = Mathf.Sqrt(dx * dx + dy * dy);
                float disc = 21.5f - r;
                float tri = y > 24f ? Mathf.Min(62f - y, (1f - (y - 24f) / 38f) * 16f - Mathf.Abs(dx)) : -9f;
                return Mathf.Max(disc, tri);
            }, true);
            _flagTex = Glyph((x, y) =>
            {
                float pole = Mathf.Min(2.5f - Mathf.Abs(x - 18f), Mathf.Min(y - 8f, 56f - y));
                float fl = Mathf.Min(Mathf.Min(x - 19f, 48f - x - (y - 10f) * 0.1f), Mathf.Min(y - 10f + Mathf.Sin(x * 0.25f) * 1.5f, 34f - y + Mathf.Sin(x * 0.25f) * 1.5f));
                return Mathf.Max(pole, fl);
            }, false);
            _personTex = Glyph((x, y) =>
            {
                float hx = x - 32f, hy = y - 18f;
                float head = 9f - Mathf.Sqrt(hx * hx + hy * hy);
                float bx = Mathf.Abs(x - 32f), by = y - 30f;
                float body = Mathf.Min(Mathf.Min(by, 56f - y), 16f - by * 0.12f - bx);
                return Mathf.Max(head, body);
            }, false);
        }

        static Texture2D Glyph(System.Func<float, float, float> sdf, bool rim)
        {
            const int N = 64;
            var t = new Texture2D(N, N, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float d = sdf(x + 0.5f, y + 0.5f);
                    float a = Mathf.Clamp01(d + 0.5f);
                    float shade = rim ? Mathf.Lerp(0.35f, 1f, Mathf.Clamp01((d - 1.5f) / 1.5f)) : 1f;   // dark outline for contrast on the map
                    byte c = (byte)(shade * 255f);
                    px[(N - 1 - y) * N + x] = new Color32(c, c, c, (byte)(a * 255f));   // texture rows go bottom-up
                }
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }

        void DrawTex(Rect r, Texture2D t, Color c)
        {
            var old = GUI.color; GUI.color = c;
            GUI.DrawTexture(r, t, ScaleMode.StretchToFill, true);
            GUI.color = old;
        }

        struct MarkPlan { public MapView.WorldMark M; public Vector2 V; public bool Name, Sub, UnderHud; public Rect NameR, SubR; }
        readonly List<MarkPlan> _markPlan = new List<MarkPlan>();
        int _markPlanFrame = -1;

        /// <summary>Screen positions and label slots for every marker. Priority: faction camps, then SOS, then wrecks.
        /// Called from DrawMapLabels each repaint, before the street / district labels claim space.</summary>
        void PlanMarks(MapView map, float w, float h)
        {
            map.UpdateWorldLayer();
            _markPlan.Clear();
            _markPlanFrame = Time.frameCount;
            var marks = map.Marks();
            float dist = map.Dist;
            for (int pass = 0; pass < 4; pass++)
                foreach (var m in marks)
                {
                    var want = pass == 0 ? MapView.MarkKind.Camp : pass == 1 ? MapView.MarkKind.Group : pass == 2 ? MapView.MarkKind.Tow : MapView.MarkKind.Wreck;
                    if (m.Kind != want) continue;
                    if (!map.TryScreen(m.World, out var sp)) continue;
                    var v = ToVirtual(sp);
                    if (v.x < -40 || v.x > w + 40 || v.y < 30 || v.y > h + 20) continue;
                    var mp = new MarkPlan { M = m, V = v };
                    switch (m.Kind)
                    {
                        case MapView.MarkKind.Camp:
                        {
                            float s = map.SelectedCamp == m.Cid ? 52f : 44f;
                            mp.UnderHud = _lp.UnderHud(new Rect(v.x - s / 2, v.y - s, s, s));
                            mp.NameR = LabelRect(v.x, v.y + 2f, m.Label, 15, 20f);
                            mp.SubR = LabelRect(v.x, v.y + 24f, m.Sub, 12, 16f);
                            mp.Name = !mp.UnderHud && _lp.Place(mp.NameR);
                            mp.Sub = mp.Name && _lp.Place(mp.SubR);
                            break;
                        }
                        case MapView.MarkKind.Group:
                        {
                            mp.UnderHud = _lp.UnderHud(new Rect(v.x - 17f, v.y - 34f, 34f, 34f));
                            if (dist < 45f || map.SelectedGroup == m.Id) { mp.NameR = LabelRect(v.x, v.y + 1f, SosText(m), 12, 16f); mp.Name = !mp.UnderHud && _lp.Place(mp.NameR); }
                            break;
                        }
                        case MapView.MarkKind.Tow:
                            mp.NameR = new Rect(v.x - 85f, v.y - 40f, 170f, 34f);
                            mp.UnderHud = _lp.UnderHud(mp.NameR);
                            mp.Name = !mp.UnderHud;
                            break;
                        case MapView.MarkKind.Wreck:
                        {
                            if (dist > 110f) continue;
                            bool sel = map.SelectedWreck == m.Id;
                            mp.UnderHud = _lp.UnderHud(new Rect(v.x - 13f, v.y - 26f, 26f, 26f));
                            if (dist < 40f || sel) { mp.NameR = LabelRect(v.x, v.y + 1f, m.Sub != "" ? m.Sub : m.Label, 12, 16f); mp.Name = !mp.UnderHud && _lp.Place(mp.NameR); }
                            break;
                        }
                    }
                    _markPlan.Add(mp);
                }
        }

        static string SosText(MapView.WorldMark m) => m.St == MapGen.SOS_OPEN ? "SOS - " + m.Sub : m.Sub;

        Rect LabelRect(float cx, float y, string t, int size, float h)
        {
            float tw = Mathf.Min(300f, TextW(t ?? "", size) + 8f);
            return new Rect(cx - tw / 2f, y, tw, h);
        }

        /// <summary>All floating markers, from this repaint's plan. Camps last so their names sit on top.</summary>
        void DrawWorldMarks(MapView map, float w, float h)
        {
            if (Event.current.type != EventType.Repaint) return;   // drawing only; taps go to the map (MapView.World.cs)
            if (_markPlanFrame != Time.frameCount) { BeginLabels(); PlanMarks(map, w, h); }
            EnsureMarkTex();
            float dist = map.Dist;
            float pulse = 0.85f + 0.15f * Mathf.Sin(Time.unscaledTime * 4f);
            // wrecks, then rescue pins, tow jobs, camps
            for (int pass = 0; pass < 4; pass++)
                foreach (var mp in _markPlan)
                {
                    var m = mp.M; var v = mp.V;
                    var want = pass == 0 ? MapView.MarkKind.Wreck : pass == 1 ? MapView.MarkKind.Group : pass == 2 ? MapView.MarkKind.Tow : MapView.MarkKind.Camp;
                    if (m.Kind != want) continue;
                    var oc = GUI.color;
                    if (mp.UnderHud) GUI.color = new Color(oc.r, oc.g, oc.b, oc.a * 0.25f);
                    switch (m.Kind)
                    {
                        case MapView.MarkKind.Wreck:
                        {
                            bool sel = map.SelectedWreck == m.Id;
                            bool towing = m.Sub != "";
                            float s = sel ? 34f : dist > 70f ? 18f : 26f;
                            Icon(new Rect(v.x - s / 2, v.y - s, s, s), "garage", towing ? new Color(0.86f, 0.62f, 0.2f) : m.Col);
                            if (mp.Name) UiKit.FitLabel(mp.NameR, towing ? m.Sub : m.Label, 12, TextAnchor.MiddleCenter, sel ? SideMenus.AMBER : new Color(0.9f, 0.88f, 0.8f, 0.9f), true, 10);
                            break;
                        }
                        case MapView.MarkKind.Group:
                        {
                            bool open = m.St == MapGen.SOS_OPEN, nobeds = m.St == MapGen.SOS_NOBEDS;
                            float s = (dist > 90f ? 22f : dist > 45f ? 28f : 34f) * (map.SelectedGroup == m.Id ? 1.25f : 1f) * (nobeds ? 0.85f : 1f);
                            var r = new Rect(v.x - s / 2, v.y - s * 1.02f, s, s);
                            var gc = m.Col; gc.a = (open ? pulse : nobeds ? 0.75f : 1f) * GUI.color.a;   // only an open SOS pulses
                            DrawTex(r, _pinTex, gc);
                            DrawTex(new Rect(r.x + s * 0.24f, r.y + s * 0.08f, s * 0.52f, s * 0.52f), _personTex, new Color(1f, 1f, 1f, (nobeds ? 0.7f : 0.95f) * GUI.color.a));
                            if (mp.Name) UiKit.FitLabel(mp.NameR, SosText(m), 12, TextAnchor.MiddleCenter, open ? new Color(0.62f, 0.95f, 0.55f, 0.95f) : nobeds ? new Color(0.78f, 0.77f, 0.7f, 0.9f) : new Color(0.62f, 0.8f, 1f, 0.95f), true, 10);
                            break;
                        }
                        case MapView.MarkKind.Tow:
                        {
                            if (!mp.Name) break;
                            var r = mp.NameR; float bw = r.width, bh = r.height;
                            GUI.DrawTexture(r, Solid2(new Color(0.1f, 0.1f, 0.08f, 0.82f)));
                            GUI.DrawTexture(new Rect(r.x, r.y, 3, bh), Solid2(m.Col));
                            UiKit.FitLabel(new Rect(r.x + 6, r.y + 2, bw - 10, 16), "TOW " + m.Sub.ToUpperInvariant() + " " + (int)(m.Frac * 100f) + "%", 12, TextAnchor.MiddleLeft, new Color(0.95f, 0.85f, 0.6f), true, 9);
                            GUI.DrawTexture(new Rect(r.x + 8, r.y + 21, bw - 16, 7), Solid2(new Color(0f, 0f, 0f, 0.6f)));
                            GUI.DrawTexture(new Rect(r.x + 8, r.y + 21, (bw - 16) * Mathf.Clamp01(m.Frac), 7), Solid2(m.Col));
                            break;
                        }
                        case MapView.MarkKind.Camp:
                        {
                            bool sel = map.SelectedCamp == m.Cid;
                            float s = sel ? 52f : 44f;
                            var r = new Rect(v.x - s / 2, v.y - s * 1.02f, s, s);
                            var pc = m.Col; pc.a *= GUI.color.a;
                            DrawTex(r, _pinTex, pc);
                            DrawTex(new Rect(r.x + s * 0.22f, r.y + s * 0.06f, s * 0.56f, s * 0.56f), _flagTex, new Color(1f, 1f, 1f, 0.95f * GUI.color.a));
                            if (mp.Name) UiKit.FitLabel(mp.NameR, m.Label, 15, TextAnchor.MiddleCenter, new Color(0.96f, 0.93f, 0.85f), true, 11);
                            if (mp.Sub) UiKit.FitLabel(mp.SubR, m.Sub, 12, TextAnchor.MiddleCenter, Color.Lerp(m.Col, Color.white, 0.25f), true, 9);
                            break;
                        }
                    }
                    GUI.color = oc;
                }
        }
    }
}
