// v0.15 graphics (idea #15): graffiti and banners on the survivors' own base.
//  - the colony's name painted in big white block letters (red drop shadow + red brush underline) high on the main
//    camp building's street-facing wall (the face toward the camp gate / door), using the same 5x7 stroke font as the
//    street graffiti (MapGen.Street.cs ST_FONT) plus the glyphs it lacks (G J M Q W X Y Z, digits, a few marks)
//  - a flagpole on the main building's roof with a waving cloth flag in the colony colour (player mustard, other
//    colonies rust) carrying a pale cross emblem; the cloth is a 10x5 quad strip vertex-animated every frame
//  - a wooden tally board by the gate: days survived as groups of five chalk marks (4 strokes + a slash), up to 100
//    marks, then the count painted as a number
// The static part (letters, pole, board) is one mesh rebuilt only when the colony name, day count, main base or the
// map generator changes; the flag is a small second mesh rewritten per frame. Same DayZero/Map shader as the city
// (vertex colour, haze). Visual only: reads Game.S / Colony / Base and MapGen layout, never writes, no sim RNG.
// Hook: one line in FxView.Apply.
using System.Collections.Generic;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed partial class MapGen
    {
        public struct BannerFlag { public Vector2 P; public float Y; public Vector2 Fly; public Color Col; public float Seed; }

        static readonly Color BN_WHITE = Hex("#dcd6c6"), BN_RED = Hex("#9a2a22"), BN_CHALK = Hex("#e4dfd0");
        static readonly Color BN_BOARD = Hex("#7a5a3a"), BN_BOARD_D = Hex("#5a412b"), BN_POST = Hex("#4e3b2a"), BN_POLE = Hex("#3a3631");
        static readonly Color BN_MINE = Hex("#bd882f"), BN_OTHER = Hex("#6e3226"), BN_EMBLEM = Hex("#e6dcc0");

        // glyphs the street graffiti font does not have (same 5x7 grid, '#' = paint)
        static readonly Dictionary<char, string[]> BN_FONT = new Dictionary<char, string[]>
        {
            ['G'] = new[] { " ####", "#    ", "#    ", "# ###", "#   #", "#   #", " ### " },
            ['J'] = new[] { "  ###", "    #", "    #", "    #", "    #", "#   #", " ### " },
            ['M'] = new[] { "#   #", "## ##", "# # #", "# # #", "#   #", "#   #", "#   #" },
            ['Q'] = new[] { " ### ", "#   #", "#   #", "#   #", "# # #", "#  # ", " ## #" },
            ['W'] = new[] { "#   #", "#   #", "#   #", "# # #", "# # #", "## ##", "#   #" },
            ['X'] = new[] { "#   #", "#   #", " # # ", "  #  ", " # # ", "#   #", "#   #" },
            ['Y'] = new[] { "#   #", "#   #", " # # ", "  #  ", "  #  ", "  #  ", "  #  " },
            ['Z'] = new[] { "#####", "    #", "   # ", "  #  ", " #   ", "#    ", "#####" },
            ['0'] = new[] { " ### ", "#   #", "#  ##", "# # #", "##  #", "#   #", " ### " },
            ['1'] = new[] { "  #  ", " ##  ", "  #  ", "  #  ", "  #  ", "  #  ", " ### " },
            ['2'] = new[] { " ### ", "#   #", "    #", "   # ", "  #  ", " #   ", "#####" },
            ['3'] = new[] { "#### ", "    #", "    #", " ### ", "    #", "    #", "#### " },
            ['4'] = new[] { "   # ", "  ## ", " # # ", "#  # ", "#####", "   # ", "   # " },
            ['5'] = new[] { "#####", "#    ", "#### ", "    #", "    #", "#   #", " ### " },
            ['6'] = new[] { " ### ", "#    ", "#    ", "#### ", "#   #", "#   #", " ### " },
            ['7'] = new[] { "#####", "    #", "   # ", "  #  ", "  #  ", "  #  ", "  #  " },
            ['8'] = new[] { " ### ", "#   #", "#   #", " ### ", "#   #", "#   #", " ### " },
            ['9'] = new[] { " ### ", "#   #", "#   #", " ####", "    #", "    #", " ### " },
            ['-'] = new[] { "     ", "     ", "     ", " ### ", "     ", "     ", "     " },
            ['.'] = new[] { "     ", "     ", "     ", "     ", "     ", " ##  ", " ##  " },
            ['\''] = new[] { "  #  ", "  #  ", "     ", "     ", "     ", "     ", "     " },
            ['!'] = new[] { "  #  ", "  #  ", "  #  ", "  #  ", "  #  ", "     ", "  #  " },
            ['&'] = new[] { " ##  ", "#  # ", " ##  ", " #   ", "# # #", "#  # ", " ## #" },
        };

        static string[] BannerGlyph(char ch)
        {
            if (ST_FONT.TryGetValue(ch, out var r)) return r;
            return BN_FONT.TryGetValue(ch, out r) ? r : null;
        }

        static string BannerClean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c0 in s.ToUpperInvariant())
            {
                char c = c0 == '_' ? ' ' : c0;
                sb.Append(c == ' ' || BannerGlyph(c) != null ? c : ' ');
            }
            return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), " +", " ").Trim();
        }

        /// <summary>Completed days since the colony started (the tally shows 0 on its first day).</summary>
        public static int BannerDays(Game game, Colony c)
        {
            int startDay = (int)(c.StartT / 24.0) + 1;
            return Mathf.Max(0, game.Day() - startDay);
        }

        static Base BannerBase(Colony c)
        {
            if (c == null || c.Lost || c.Merged >= 0 || c.Bases == null || c.Bases.Count == 0) return null;
            foreach (var b in c.Bases) if (b.Main) return b;
            return c.Bases[0];
        }

        /// <summary>Changes whenever the banner geometry would change (name, day, base, lot height, generator).</summary>
        public int BannerKey(Game game)
        {
            unchecked
            {
                int h = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this) * 31 + game.S.Seed;
                h = h * 31 + game.CurCol; h = h * 31 + (PlainCamp ? 1 : 0); h = h * 31 + game.S.Cols.Count;
                foreach (var c in game.S.Cols)
                {
                    var b = BannerBase(c);
                    if (b == null) { h = h * 31 + 7; continue; }
                    h = h * 31 + (c.Name ?? "").GetHashCode(); h = h * 31 + BannerDays(game, c);
                    h = h * 31 + b.Id; h = h * 31 + b.Lot; h = h * 31 + b.Pos.x * 4099 + b.Pos.y;
                    if (b.Lot >= 0 && LotH != null && b.Lot < LotH.Length) h = h * 31 + (int)(LotH[b.Lot] * 100f);
                }
                return h;
            }
        }

        /// <summary>Static banner geometry for every live player colony's main base; fills flags with the cloth anchors.</summary>
        public void BannerStatic(Geo g, Game game, List<BannerFlag> flags)
        {
            if (game?.S == null) return;
            for (int ci = 0; ci < game.S.Cols.Count; ci++)
            {
                var c = game.S.Cols[ci];
                var b = BannerBase(c);
                if (b == null || !Layout(b, out var lay)) continue;
                Color fc = ci == game.CurCol ? BN_MINE : BN_OTHER;
                string name = BannerClean(c.Name);
                var gate = lay.Gate;
                var ctr = lay.HasBld ? lay.Bld.C : lay.Yard.C;
                var outv = gate - ctr; if (outv.sqrMagnitude < 1e-4f) outv = -CampFront(); outv = outv.normalized;
                var side = new Vector2(-outv.y, outv.x);
                var fly = new Vector2(0.259f, 0.966f);   // across the default camera view so the cloth shows broadside
                bool bld = lay.HasBld && b.Lot >= 0 && b.Lot < _info.Length && _info[b.Lot].Valid && _info[b.Lot].Floors > 0;
                if (bld)
                {
                    var info = _info[b.Lot];
                    // name on the face whose outside looks toward the gate / door
                    // name on the most open face: street / sidewalk outside, nothing built right in front of it,
                    // turned toward the default camera, toward the door / gate
                    int face = 0; float bd = -99f; var cam = new Vector2(-0.966f, 0.259f);
                    for (int f = 0; f < 4; f++)
                    {
                        FaceEdge(info.R, f, out var fa, out var fb, out var o);
                        var m = (fa + fb) * 0.5f;
                        int cc = ClsAt(m + o * 1.0f);
                        float sc = (cc == C_SIDE || cc == C_ROAD || cc == C_PLAZA ? 2f : 0f) + (BnFree(m + o * 0.8f, b.Lot) ? 1.5f : -2f)
                                 + Vector2.Dot(o, cam) * 1.2f + Vector2.Dot(o, outv) * 0.4f + Mathf.Min(1f, (fb - fa).magnitude * 0.25f);
                        if (sc > bd) { bd = sc; face = f; }
                    }
                    FaceEdge(info.R, face, out var A, out var B, out var O);
                    if (name.Length > 0) WallName(g, name, A, B, O, info.WallTop);
                    // roof flagpole
                    float roof = Mathf.Max(info.WallTop, info.Top);
                    var pp = info.R.C;
                    float top = roof + 1.7f;
                    g.Beam(Geo.W(pp, roof - 0.06f), Geo.W(pp, top + 0.06f), 0.022f, BN_POLE);
                    g.BoxAt(pp, info.R.U, 0.03f, 0.03f, top + 0.06f, top + 0.11f, Hex("#8a8376"), Hex("#a29a88"));
                    g.BoxAt(pp, info.R.U, 0.07f, 0.07f, roof - 0.04f, roof + 0.03f, BN_POST, BN_BOARD_D);   // footing block
                    flags.Add(new BannerFlag { P = pp, Y = top, Fly = fly, Col = fc, Seed = b.Id * 1.37f });
                }
                else
                {
                    // no building: a freestanding sign beside the gate carries the name, flag on a tall pole
                    var sp = gate + outv * 0.7f + side * 1.2f;
                    if (name.Length > 0) Sign(g, name, sp, outv, b.Id);
                    var pp = gate + side * 0.9f - outv * 0.3f;
                    g.Beam(Geo.W(pp, 0f), Geo.W(pp, 2.46f), 0.022f, BN_POLE);
                    flags.Add(new BannerFlag { P = pp, Y = 2.4f, Fly = fly, Col = fc, Seed = b.Id * 1.37f });
                }
                // tally board by the gate / door: first spot with nothing built on it (board ends and centre)
                var cands = new[] { gate - side * 1.15f + outv * 0.35f, gate + side * 1.25f + outv * 0.35f, gate + outv * 1.3f - side * 0.9f, gate + outv * 1.3f + side * 0.9f, gate + outv * 1.6f };
                Vector2 tp = cands[0], to = outv;
                foreach (var q in cands)
                {
                    var qa = ReadDir(outv);
                    if (BnFree(q, b.Lot, true) && BnFree(q + qa * 0.45f, b.Lot, true) && BnFree(q - qa * 0.45f, b.Lot, true)) { tp = q; break; }
                }
                Tally(g, tp, to, BannerDays(game, c), b.Id);
            }
        }

        /// <summary>True when no building footprint (another lot's, or with self also the base's own) covers p.</summary>
        bool BnFree(Vector2 p, int baseLot, bool self = false)
        {
            if (self && baseLot >= 0 && baseLot < _info.Length && _info[baseLot].Valid && _info[baseLot].R.Contains(p, 0.12f)) return false;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    var t = new Vector2Int(Mathf.FloorToInt(p.x) + dx, Mathf.FloorToInt(p.y) + dy);
                    if (!d.InMap(t)) continue;
                    int l = d.LotAt(t);
                    if (l < 0 || l == baseLot || l >= _info.Length) continue;
                    var inf = _info[l];
                    if (inf.Valid && inf.Floors > 0 && inf.Dmg < 3 && inf.R.Contains(p, 0.12f)) return false;
                }
            return true;
        }

        static Vector2 ReadDir(Vector2 O) => new Vector2(O.y, -O.x);   // left-to-right seen from outside (as graffiti)

        void WallName(Geo g, string name, Vector2 A, Vector2 B, Vector2 O, float wallTop)
        {
            float len = (B - A).magnitude; if (len < 0.3f || wallTop < 0.25f) return;
            var along = ReadDir(O);
            var mid = (A + B) * 0.5f;
            float room = len * 0.86f;
            float px = Mathf.Min(0.065f, (wallTop - 0.1f) / 10.5f);
            int cols = name.Length * 6 - 1;
            if (cols * px > room) px = room / cols;
            if (px < 0.016f)
            {
                px = Mathf.Min(0.016f, (wallTop - 0.1f) / 10.5f);
                int max = Mathf.Max(1, (int)((room / px + 1f) / 6f));
                if (name.Length > max) name = name.Substring(0, max).TrimEnd();
                cols = name.Length * 6 - 1;
                if (px < 0.01f) return;
            }
            float width = cols * px;
            var start = mid - along * (width * 0.5f);
            float yTop = wallTop - Mathf.Max(0.05f, px * 1.6f);
            // red painted band (stands proud of window frames), dark hems, then white letters with a dark drop shadow
            var u0 = start - along * px * 1.4f; var u1 = start + along * (width + px * 1.4f);
            float b0 = yTop - 8.3f * px, b1 = yTop + 1.3f * px;
            g.Wall(u0 + O * 0.034f, u1 + O * 0.034f, O, b0, b1, BN_RED);
            g.Wall(u0 + O * 0.036f, u1 + O * 0.036f, O, b0, b0 + px * 0.45f, Mul(BN_RED, 0.62f));
            g.Wall(u0 + O * 0.036f, u1 + O * 0.036f, O, b1 - px * 0.45f, b1, Mul(BN_RED, 0.62f));
            Text(g, name, start + along * (px * 0.3f), along, O, 0.038f, yTop - px * 0.3f, px, Mul(BN_RED, 0.45f));
            Text(g, name, start, along, O, 0.041f, yTop, px, BN_WHITE);
        }

        /// <summary>Block letters on a vertical plane: start = left edge, along = reading direction, O = outward normal.</summary>
        void Text(Geo g, string s, Vector2 start, Vector2 along, Vector2 O, float lift, float yTop, float px, Color col)
        {
            var st = start + O * lift;
            for (int ci = 0; ci < s.Length; ci++)
            {
                var rows = BannerGlyph(s[ci]); if (rows == null) continue;
                for (int r = 0; r < 7; r++)
                {
                    string row = rows[r]; int c0 = -1;
                    for (int cc = 0; cc <= 5; cc++)
                    {
                        bool on = cc < 5 && row[cc] == '#';
                        if (on && c0 < 0) c0 = cc;
                        if (!on && c0 >= 0)
                        {
                            float s0 = (ci * 6 + c0) * px, s1 = (ci * 6 + cc) * px;
                            g.Wall(st + along * s0, st + along * s1, O, yTop - (r + 1) * px, yTop - r * px, col);
                            c0 = -1;
                        }
                    }
                }
            }
        }

        /// <summary>Freestanding name sign (camp without a building): two posts and a wide plank, white letters.</summary>
        void Sign(Geo g, string name, Vector2 p, Vector2 O, int salt)
        {
            var along = ReadDir(O);
            const float hw = 0.9f, y0 = 0.45f, y1 = 0.85f;
            foreach (int s in new[] { -1, 1 }) g.Cyl(p + along * (hw * 0.85f * s) - O * 0.05f, 0.035f, 0f, y1 + 0.05f, BN_POST, BN_POST, 6);
            g.Box(new ORect(p, along, hw, 0.025f).Quad(), y0, y1, BN_BOARD, BN_BOARD_D);
            int cols = name.Length * 6 - 1;
            float px = Mathf.Min(0.045f, hw * 1.8f / cols);
            if (px < 0.012f) { int max = Mathf.Max(1, (int)((hw * 1.8f / 0.012f + 1f) / 6f)); if (name.Length > max) name = name.Substring(0, max).TrimEnd(); cols = name.Length * 6 - 1; px = Mathf.Min(0.045f, hw * 1.8f / cols); }
            float width = cols * px, yTop = (y0 + y1) * 0.5f + 3.5f * px;
            var start = p + O * 0.025f - along * (width * 0.5f);
            Text(g, name, start + along * (px * 0.3f), along, O, 0.004f, yTop - px * 0.3f, px, BN_RED);
            Text(g, name, start, along, O, 0.008f, yTop, px, BN_WHITE);
        }

        /// <summary>Wooden tally board facing O: 'DAYS' in red, then groups of five chalk marks (or the number past 100).</summary>
        void Tally(Geo g, Vector2 p, Vector2 O, int days, int salt)
        {
            var along = ReadDir(O);
            const float hw = 0.44f, y0 = 0.28f, y1 = 0.88f;
            foreach (int s in new[] { -1, 1 }) g.Cyl(p + along * (0.37f * s) - O * 0.05f, 0.032f, 0f, y1 + 0.06f, BN_POST, Hex("#5e4a36"), 6);
            g.Box(new ORect(p, along, hw, 0.025f).Quad(), y0, y1, BN_BOARD, BN_BOARD_D);
            var face = p + O * 0.026f; var left = face - along * hw;
            // plank seams
            foreach (float sy in new[] { 0.48f, 0.68f }) g.Wall(left + O * 0.002f, left + along * (hw * 2f) + O * 0.002f, O, sy - 0.006f, sy + 0.006f, BN_BOARD_D);
            g.Shadow(new ORect(p + O * 0.15f, along, hw, 0.14f).Quad(), 0.5f, 0.22f);
            // header
            const float hp = 0.022f; float hwid = 23f * hp;
            Text(g, "DAYS", face - along * (hwid * 0.5f), along, O, 0.004f, y1 - 0.03f, hp, BN_RED);
            var n3 = new Vector3(O.x, 0f, -O.y);
            if (days > 100)
            {
                string num = days.ToString();
                int cols = num.Length * 6 - 1;
                float px = Mathf.Min(0.055f, hw * 1.7f / cols);
                float w = cols * px, yt = (y0 + y1 - 0.08f) * 0.5f + 3.5f * px;
                Text(g, num, face - along * (w * 0.5f), along, O, 0.004f, yt, px, BN_CHALK);
                return;
            }
            Color ck = Geo.Shade(BN_CHALK, n3);
            // up to 30 days: big marks, 3 groups a row; then 5 groups a row, 4 rows (100 marks)
            float sc = days <= 30 ? 1.6f : 1f; int per = days <= 30 ? 3 : 5;
            float gw = 0.16f * sc, sp = 0.028f * sc, mh = 0.075f * sc, rowH = 0.1f * sc, sw = 0.012f * sc;
            float x0 = (hw * 2f - (per * gw - (gw - 3f * sp))) * 0.5f, yRow0 = y1 - 0.11f;
            for (int k = 0; k < days; k++)
            {
                int grp = k / 5, m = k % 5, row = grp / per, gi = grp % per;
                float gx = x0 + gi * gw, yb = yRow0 - row * rowH - mh;
                float jx = (Hf(salt * 131 + k, 41) - 0.5f) * 0.006f, jy = (Hf(salt * 131 + k, 42) - 0.5f) * 0.01f;
                if (m < 4)
                {
                    float sx = gx + m * sp + jx;
                    Mark(g, left, along, O, sx, yb + jy, sx + (Hf(k, 43) - 0.5f) * 0.008f, yb + mh + jy, sw, ck);
                }
                else Mark(g, left, along, O, gx - 0.012f + jx, yb + 0.012f + jy, gx + 3 * sp + 0.012f + jx, yb + mh - 0.014f + jy, sw * 0.9f, ck);
            }
        }

        static void Mark(Geo g, Vector2 left, Vector2 along, Vector2 O, float s0, float yA, float s1, float yB, float w, Color c)
        {
            var d = new Vector2(s1 - s0, yB - yA); float len = d.magnitude; if (len < 1e-4f) return;
            var n = new Vector2(-d.y, d.x) / len * (w * 0.5f);
            Vector3 P(float s, float y) { var q = left + along * s + O * 0.03f; return new Vector3(q.x, y, -q.y); }
            g.QuadRaw(P(s0 - n.x, yA - n.y), P(s1 - n.x, yB - n.y), P(s1 + n.x, yB + n.y), P(s0 + n.x, yA + n.y), c, c, c, c);
        }

        /// <summary>The waving cloth for one flag at time t: a 10x5 quad strip pinned to the pole, pale cross emblem.</summary>
        public static void BannerCloth(Geo g, BannerFlag f, float t)
        {
            const int NX = 10, NY = 5; const float L = 0.82f, H = 0.5f;
            var fly = f.Fly; var side = new Vector2(-fly.y, fly.x);
            Vector3 Pt(int i, int j, out float ph)
            {
                float u = i / (float)NX, v = j / (float)NY;
                ph = t * 4.3f - u * 5.6f + f.Seed + v * 0.6f;
                float lat = (Mathf.Sin(ph) * 0.10f + Mathf.Sin(ph * 1.7f + v * 1.3f) * 0.025f) * u;
                float x = L * u * (1f - 0.05f * u * (1f + Mathf.Cos(ph)));
                var q = f.P + fly * x + side * lat;
                float y = f.Y - H * v - 0.07f * u * u + Mathf.Sin(ph + 0.6f) * 0.025f * u;
                return new Vector3(q.x, y, -q.y);
            }
            float K(float ph) => 0.80f + 0.22f * Mathf.Cos(ph);
            for (int i = 0; i < NX; i++)
                for (int j = 0; j < NY; j++)
                {
                    bool em = i == 3 || j == 2;
                    Color cc = em ? BN_EMBLEM : f.Col;
                    var a = Pt(i, j, out float pa); var b = Pt(i + 1, j, out float pb); var c = Pt(i + 1, j + 1, out float pc); var d = Pt(i, j + 1, out float pd);
                    g.QuadRaw(a, b, c, d, Mul(cc, K(pa)), Mul(cc, K(pb)), Mul(cc, K(pc)), Mul(cc, K(pd)));
                }
        }
    }

    // ---- runtime component ----
    public sealed class FxColonyBanner : MonoBehaviour
    {
        static FxColonyBanner _inst;
        Mesh _stat, _cloth; Material _mat; MeshRenderer _mrS, _mrF;
        int _key = int.MinValue, _frame = -10;
        readonly List<MapGen.BannerFlag> _flags = new List<MapGen.BannerFlag>();
        MeshData _fm;

        /// <summary>Called from FxView.Apply every map frame.</summary>
        public static void Tick(Camera cam, Game g)
        {
            if (g?.S == null) return;
            var gen = Game.YardProvider?.Target as MapGen;
            if (gen == null) return;
            if (_inst == null)
            {
                var go = new GameObject("FxColonyBanner"); Object.DontDestroyOnLoad(go);
                _inst = go.AddComponent<FxColonyBanner>();
                if (!_inst.Init()) { Destroy(go); _inst = null; return; }
            }
            try { _inst.Run(g, gen); } catch (System.Exception) { /* visual only */ }
        }

        bool Init()
        {
            var sh = Resources.Load<Shader>("Shaders/DayZeroMap");
            if (sh == null || !sh.isSupported) sh = Shader.Find("Sprites/Default");
            if (sh == null) return false;
            _mat = new Material(sh) { name = "ColonyBanner", hideFlags = HideFlags.DontSave };
            _stat = new Mesh { name = "BannerStatic", hideFlags = HideFlags.DontSave };
            _cloth = new Mesh { name = "BannerCloth", hideFlags = HideFlags.DontSave }; _cloth.MarkDynamic();
            _mrS = Child("Static", _stat); _mrF = Child("Cloth", _cloth);
            return true;
        }

        MeshRenderer Child(string n, Mesh m)
        {
            var go = new GameObject(n); go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = m;
            var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = _mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            return r;
        }

        void Run(Game g, MapGen gen)
        {
            _frame = Time.frameCount;
            int key = gen.BannerKey(g);
            if (key != _key)
            {
                _key = key; _flags.Clear();
                var geo = new Geo(new MeshData(), new MeshData());
                gen.BannerStatic(geo, g, _flags);
                Upload(_stat, geo.M);
            }
            if (_fm == null) _fm = new MeshData();
            _fm.V.Clear(); _fm.C.Clear(); _fm.T.Clear();
            var fg = new Geo(_fm, null);
            float t = Time.time;
            foreach (var f in _flags) MapGen.BannerCloth(fg, f, t);
            Upload(_cloth, _fm);
        }

        static void Upload(Mesh m, MeshData d)
        {
            m.Clear();
            m.SetVertices(d.V); m.SetColors(d.C); m.SetTriangles(d.T, 0, false);
            m.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
        }

        void LateUpdate()
        {
            bool on = Time.frameCount - _frame <= 2 && Time.frameCount - FxView.Frame <= 2;
            if (_mrS != null) _mrS.enabled = on;
            if (_mrF != null) _mrF.enabled = on && _flags.Count > 0;
        }

        void OnDestroy() { if (_stat != null) Destroy(_stat); if (_cloth != null) Destroy(_cloth); if (_mat != null) Destroy(_mat); }
    }
}
