// v0.14.0a: floating map markers (MapView.World.cs gathers them, MapView taps them):
//   * faction camps: a pin in the stance colour (hostile red, neutral amber, friendly green) with a flag, the
//     camp name and its stance, readable zoomed all the way out
//   * survivors waiting for rescue: a small green pin with a person, so the player can find them
//   * wrecks: a jerrycan marker (own lost vehicles green), and a tow job's progress bar over the squad
// Also keeps the camps / wrecks world layer current (MapView.UpdateWorldLayer).
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

        /// <summary>All floating markers. Camps last so their names sit on top.</summary>
        void DrawWorldMarks(MapView map, float w, float h)
        {
            if (Event.current.type == EventType.Repaint) map.UpdateWorldLayer();
            if (Event.current.type != EventType.Repaint) return;   // drawing only; taps go to the map (MapView.World.cs)
            EnsureMarkTex();
            var marks = map.Marks();
            float dist = map.Dist;
            var tiny = MLabel(12, true, MenuAlign.Center, false);
            var name = MLabel(15, true, MenuAlign.Center, false);
            var sub = MLabel(12, true, MenuAlign.Center, false);
            float pulse = 0.85f + 0.15f * Mathf.Sin(Time.unscaledTime * 4f);
            // wrecks (zoomed in enough to see the street), then rescue pins, tow jobs, camps
            for (int pass = 0; pass < 4; pass++)
                foreach (var m in marks)
                {
                    var want = pass == 0 ? MapView.MarkKind.Wreck : pass == 1 ? MapView.MarkKind.Group : pass == 2 ? MapView.MarkKind.Tow : MapView.MarkKind.Camp;
                    if (m.Kind != want) continue;
                    if (!map.TryScreen(m.World, out var sp)) continue;
                    var v = ToVirtual(sp);
                    if (v.x < -40 || v.x > w + 40 || v.y < 30 || v.y > h + 20) continue;
                    switch (m.Kind)
                    {
                        case MapView.MarkKind.Wreck:
                        {
                            if (dist > 110f) break;
                            bool sel = map.SelectedWreck == m.Id;
                            bool towing = m.Sub != "";
                            float s = sel ? 34f : dist > 70f ? 18f : 26f;
                            Icon(new Rect(v.x - s / 2, v.y - s, s, s), "garage", towing ? new Color(0.86f, 0.62f, 0.2f) : m.Col);
                            if (dist < 40f || sel)
                            {
                                tiny.normal.textColor = sel ? SideMenus.AMBER : new Color(0.9f, 0.88f, 0.8f, 0.9f);
                                Shadowed(new Rect(v.x - 90, v.y + 1, 180, 16), towing ? m.Sub : m.Label, tiny);
                            }
                            break;
                        }
                        case MapView.MarkKind.Group:
                        {
                            float s = (dist > 90f ? 22f : dist > 45f ? 28f : 34f) * (map.SelectedGroup == m.Id ? 1.25f : 1f);
                            var r = new Rect(v.x - s / 2, v.y - s * 1.02f, s, s);
                            var gc = m.Col; gc.a = pulse;
                            DrawTex(r, _pinTex, gc);
                            DrawTex(new Rect(r.x + s * 0.24f, r.y + s * 0.08f, s * 0.52f, s * 0.52f), _personTex, new Color(1f, 1f, 1f, 0.95f));
                            if (dist < 45f || map.SelectedGroup == m.Id)
                            {
                                tiny.normal.textColor = new Color(0.62f, 0.95f, 0.55f, 0.95f);
                                Shadowed(new Rect(v.x - 100, v.y + 1, 200, 16), "SOS - " + m.Sub, tiny);
                            }
                            break;
                        }
                        case MapView.MarkKind.Tow:
                        {
                            float bw = 170f, bh = 34f;
                            var r = new Rect(v.x - bw / 2, v.y - bh - 6, bw, bh);
                            GUI.DrawTexture(r, Solid2(new Color(0.1f, 0.1f, 0.08f, 0.82f)));
                            GUI.DrawTexture(new Rect(r.x, r.y, 3, bh), Solid2(m.Col));
                            tiny.normal.textColor = new Color(0.95f, 0.85f, 0.6f);
                            GUI.Label(new Rect(r.x + 6, r.y + 2, bw - 10, 16), "TOW " + m.Sub.ToUpperInvariant() + " " + (int)(m.Frac * 100f) + "%", tiny);
                            GUI.DrawTexture(new Rect(r.x + 8, r.y + 21, bw - 16, 7), Solid2(new Color(0f, 0f, 0f, 0.6f)));
                            GUI.DrawTexture(new Rect(r.x + 8, r.y + 21, (bw - 16) * Mathf.Clamp01(m.Frac), 7), Solid2(m.Col));
                            break;
                        }
                        case MapView.MarkKind.Camp:
                        {
                            bool sel = map.SelectedCamp == m.Cid;
                            float s = sel ? 52f : 44f;
                            var r = new Rect(v.x - s / 2, v.y - s * 1.02f, s, s);
                            DrawTex(r, _pinTex, m.Col);
                            DrawTex(new Rect(r.x + s * 0.22f, r.y + s * 0.06f, s * 0.56f, s * 0.56f), _flagTex, new Color(1f, 1f, 1f, 0.95f));
                            name.normal.textColor = new Color(0.96f, 0.93f, 0.85f);
                            Shadowed(new Rect(v.x - 130, v.y + 2, 260, 20), m.Label, name);
                            sub.normal.textColor = Color.Lerp(m.Col, Color.white, 0.25f);
                            Shadowed(new Rect(v.x - 130, v.y + 21, 260, 16), m.Sub, sub);
                            break;
                        }
                    }
                }
        }
    }
}
