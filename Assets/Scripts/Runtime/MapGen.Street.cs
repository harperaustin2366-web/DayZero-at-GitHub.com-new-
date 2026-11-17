// Street-level detail (graphics idea #9): the aftermath on the pavement.
//  - abandoned shopping carts (open wire basket on four casters) on sidewalks
//  - paper scraps (tiny flat sheets) blown along sidewalks and streets
//  - crashed bikes lying flat on the road / kerb (two wheels, frame, bars)
//  - a few sheet-covered bodies (pale lumps, sometimes a dark stain)
//  - spray-painted warnings on building walls that face a street, drawn as block letters from a tiny
//    built-in 5x7 font in faded red / white paint ('NO SURVIVORS', 'TURN BACK', 'INFECTED', 'HELP', 'DEAD INSIDE')
// Deterministic per road-grid cell / lot hash, full-detail (LOD0) chunks only. Visual only: reads no Game state
// and makes no RNG calls. Hook: one line in MapGen.BuildChunk.
using System;
using System.Collections.Generic;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed partial class MapGen
    {
        static readonly Color ST_METAL = Hex("#8a8a84"), ST_METAL_D = Hex("#5e605c"), ST_WHEEL = Hex("#1e1d1b");
        static readonly Color ST_PAPER = Hex("#b2ab9a"), ST_PAPER2 = Hex("#9c9686"), ST_SHEET = Hex("#b9b5aa"), ST_SHEET_D = Hex("#a19d92");
        static readonly Color ST_STAIN = Hex("#3e2420"), ST_PAINT_R = Hex("#9a2a22"), ST_PAINT_W = Hex("#d2ccbc");
        static readonly Color[] ST_BIKE = { Hex("#7a2e28"), Hex("#2e4a5a"), Hex("#5a5a2e"), Hex("#3a3a38") };

        void StreetPass(Geo g, int x0, int y0, int x1, int y1, List<int> lots)
        {
            if (lo) return;
            StreetClutter(g, x0, y0, x1, y1);
            foreach (int l in lots) Graffiti(g, l);
        }

        // ---------------- clutter ----------------
        void StreetClutter(Geo g, int x0, int y0, int x1, int y1)
        {
            CellRange(x0, y0, x1, y1, out int i0, out int i1, out int j0, out int j1);
            for (int j = j0; j < j1; j++)
                for (int i = i0; i < i1; i++)
                {
                    int c = Cls(i, j);
                    if (c != C_SIDE && c != C_ROAD) continue;
                    var ctr = CellC(i, j);
                    if (!InRect(ctr, x0, y0, x1, y1)) continue;
                    int t = TileAtF(ctr.x, ctr.y);
                    if (t == Data.T_WATER || t == Data.T_VOID || t == Data.T_BRIDGE) continue;
                    long k = (long)j * nu + i;
                    float h = Hf(k, 901);
                    bool side = c == C_SIDE;
                    if (h > 0.024f) continue;   // fast reject: everything below triggers under 2.4 %
                    var jit = new Vector2(Hf(k, 902) - 0.5f, Hf(k, 903) - 0.5f) * (CELL * 0.6f);
                    var p = ctr + jit;
                    if (ClearedRect(new ORect(p, GU, 0.4f, 0.4f))) continue;
                    float an = Hf(k, 904) * 6.2832f; var dir = new Vector2(Mathf.Cos(an), Mathf.Sin(an));
                    float gy = GroundY(p);
                    int salt = (int)(k & 0x7fffffff);
                    if (side && h < 0.0035f) Cart(g, p, dir, gy, salt);
                    else if (h < 0.005f) { int b0 = g.M.Count; Bike(g, p, dir, gy, salt); Scale(g, b0, p, gy, 1.35f); }
                    else if (h < 0.0062f) Body(g, p, dir, gy, salt);
                    else Papers(g, p, gy, salt);
                }
        }

        /// <summary>Shopping cart: open wire basket (raised open box + rim), push handle, four casters.</summary>
        void Cart(Geo g, Vector2 p, Vector2 dir, float gy, int salt)
        {
            var side = new Vector2(-dir.y, dir.x);
            float hl = 0.17f, hw = 0.12f, b0 = gy + 0.08f, b1 = gy + 0.22f;
            var top = new ORect(p, dir, hl, hw);
            var bot = new ORect(p + dir * 0.02f, dir, hl * 0.82f, hw * 0.85f);
            // tapered open basket: outer walls light metal, a darker inner floor reads as see-through wire
            g.Frustum(bot.Quad(), b0, top.Quad(), b1, ST_METAL, ST_METAL, false);
            g.Flat(bot.Inset(0.01f).Quad(), b0 + 0.01f, Mul(ST_METAL_D, 0.8f));
            // handle bar behind the basket and the frame down to the casters
            var hb = p - dir * (hl + 0.03f);
            g.Beam(Geo.W(hb - side * hw, b1 + 0.02f), Geo.W(hb + side * hw, b1 + 0.02f), 0.012f, ST_METAL_D);
            for (int e = -1; e <= 1; e += 2)
                g.Beam(Geo.W(p + dir * (hl * 0.7f) * e + side * hw * 0.7f, gy + 0.03f), Geo.W(p + dir * (hl * 0.7f) * e - side * hw * 0.7f, gy + 0.03f), 0.01f, ST_METAL_D);
            for (int a = -1; a <= 1; a += 2)
                for (int s = -1; s <= 1; s += 2)
                    g.BoxAt(p + dir * (hl * 0.72f * a) + side * (hw * 0.72f * s), dir, 0.022f, 0.012f, gy, gy + 0.035f, ST_WHEEL, ST_WHEEL);
            g.Shadow(top.Quad(), 0.22f, 0.22f);
        }

        /// <summary>Crashed bike lying on its side: two spoked-looking rings, a frame triangle, bars and a skid scuff.</summary>
        void Bike(Geo g, Vector2 p, Vector2 dir, float gy, int salt)
        {
            var side = new Vector2(-dir.y, dir.x);
            Color fc = ST_BIKE[(int)(Hf(salt, 1) * ST_BIKE.Length) % ST_BIKE.Length];
            float y = gy + 0.012f, wr = 0.11f, wb = 0.15f;
            // scuff on the ground behind it
            g.Flat(new ORect(p - dir * 0.3f, dir, 0.22f, 0.025f).Quad(), gy + 0.003f, Mul(ASPH_DARK, 0.8f));
            for (int w = -1; w <= 1; w += 2)
            {
                var wc = p + dir * (wb * w) + side * (w > 0 ? (Hf(salt, 2) - 0.5f) * 0.05f : 0f);
                Ring(g, wc, wr, 0.024f, y, ST_WHEEL, w * 3 + salt);
            }
            // frame: top tube, down tube, seat stay (flat strips just above the wheels)
            var bb = p + dir * 0.01f + side * 0.02f;
            var head = p + dir * (wb - 0.03f) + side * 0.06f;
            var seat = p - dir * 0.05f + side * 0.07f;
            Strip(g, bb, head, 0.02f, y + 0.012f, fc);
            Strip(g, seat, head, 0.02f, y + 0.014f, fc);
            Strip(g, bb, seat, 0.02f, y + 0.013f, fc);
            Strip(g, bb, p - dir * wb, 0.012f, y + 0.011f, fc);
            Strip(g, seat, p - dir * wb, 0.010f, y + 0.012f, fc);
            // handlebar across the front, seat
            Strip(g, head - side * 0.06f + dir * 0.01f, head + side * 0.05f + dir * 0.03f, 0.012f, y + 0.02f, ST_METAL_D);
            g.Flat(new ORect(seat + side * 0.02f, dir, 0.03f, 0.014f).Quad(), y + 0.02f, ST_WHEEL);
        }

        /// <summary>Scales the vertices added since v0 about ground point p (props drawn at real size read too small at map zoom).</summary>
        static void Scale(Geo g, int v0, Vector2 p, float gy, float sc)
        {
            for (int vi = v0; vi < g.M.Count; vi++)
            {
                var v = g.M.V[vi];
                g.M.V[vi] = new Vector3(p.x + (v.x - p.x) * sc, gy + (v.y - gy) * sc, -p.y + (v.z + p.y) * sc);
            }
        }

        void Strip(Geo g, Vector2 a, Vector2 b, float w, float y, Color c)
        {
            var d = b - a; float len = d.magnitude; if (len < 1e-4f) return;
            var u = d / len;
            g.Flat(new ORect((a + b) * 0.5f, u, len * 0.5f, w * 0.5f).Quad(), y, c);
        }

        /// <summary>Flat ring of 8 quad segments (bike wheel seen from above).</summary>
        void Ring(Geo g, Vector2 c, float r, float w, float y, Color col, int salt)
        {
            float a0 = Hf(salt, 3) * 0.8f;
            for (int s = 0; s < 8; s++)
            {
                float a = a0 + s * Mathf.PI / 4f, b = a + Mathf.PI / 4f;
                var da = new Vector2(Mathf.Cos(a), Mathf.Sin(a)); var db = new Vector2(Mathf.Cos(b), Mathf.Sin(b));
                g.Flat(c + da * (r - w), c + da * r, c + db * r, c + db * (r - w), y, col, col, col, col);
            }
            // two spokes / hub
            Strip(g, c - new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * r, c + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * r, 0.006f, y - 0.002f, ST_METAL_D);
        }

        /// <summary>Sheet-covered body: a long pale lump with a head bump, sometimes a dark stain seeping out.</summary>
        void Body(Geo g, Vector2 p, Vector2 dir, float gy, int salt)
        {
            if (Hf(salt, 5) < 0.6f)
            {
                var st = new ORect(p + new Vector2(Hf(salt, 6) - 0.5f, Hf(salt, 7) - 0.5f) * 0.08f, dir, 0.24f, 0.14f);
                g.Flat(st.Quad(), gy + 0.004f, ST_STAIN);
                g.Flat(new ORect(st.C + dir * 0.12f, new Vector2(-dir.y, dir.x), 0.1f, 0.07f).Quad(), gy + 0.0045f, Mul(ST_STAIN, 0.9f));
            }
            Color sh = Lerp(ST_SHEET, ST_SHEET_D, Hf(salt, 8));
            var b0 = new ORect(p, dir, 0.27f, 0.1f);
            g.Frustum(b0.Quad(), gy, new ORect(p - dir * 0.03f, dir, 0.2f, 0.055f).Quad(), gy + 0.065f, Mul(sh, 0.92f), sh);
            var hc = p + dir * 0.22f;
            g.Frustum(new ORect(hc, dir, 0.06f, 0.065f).Quad(), gy, new ORect(hc, dir, 0.035f, 0.04f).Quad(), gy + 0.075f, Mul(sh, 0.92f), sh);
            // feet poke the sheet up at the far end
            g.Frustum(new ORect(p - dir * 0.24f, dir, 0.04f, 0.08f).Quad(), gy, new ORect(p - dir * 0.24f, dir, 0.02f, 0.05f).Quad(), gy + 0.07f, Mul(sh, 0.92f), sh);
            g.Shadow(b0.Quad(), 0.07f, 0.22f);
        }

        /// <summary>A handful of loose paper sheets.</summary>
        void Papers(Geo g, Vector2 p, float gy, int salt)
        {
            int n = 2 + (int)(Hf(salt, 10) * 4f);
            for (int s = 0; s < n; s++)
            {
                var q = p + new Vector2(Hf(salt, 11 + s) - 0.5f, Hf(salt, 21 + s) - 0.5f) * 0.6f;
                float an = Hf(salt, 31 + s) * 6.28f;
                float sz = 0.025f + Hf(salt, 41 + s) * 0.02f;
                Color c = Lerp(ST_PAPER, ST_PAPER2, Hf(salt, 51 + s));
                g.Flat(new ORect(q, new Vector2(Mathf.Cos(an), Mathf.Sin(an)), sz, sz * 0.75f).Quad(), GroundY(q) + 0.004f + s * 0.0005f, c);
            }
        }

        // ---------------- graffiti ----------------
        static readonly string[] ST_WORDS = { "NO SURVIVORS", "TURN BACK", "INFECTED", "HELP", "DEAD INSIDE" };

        // 5x7 block font, rows top to bottom, '#' = paint
        static readonly Dictionary<char, string[]> ST_FONT = new Dictionary<char, string[]>
        {
            ['A'] = new[] { " ### ", "#   #", "#   #", "#####", "#   #", "#   #", "#   #" },
            ['B'] = new[] { "#### ", "#   #", "#   #", "#### ", "#   #", "#   #", "#### " },
            ['C'] = new[] { " ####", "#    ", "#    ", "#    ", "#    ", "#    ", " ####" },
            ['D'] = new[] { "#### ", "#   #", "#   #", "#   #", "#   #", "#   #", "#### " },
            ['E'] = new[] { "#####", "#    ", "#    ", "#### ", "#    ", "#    ", "#####" },
            ['F'] = new[] { "#####", "#    ", "#    ", "#### ", "#    ", "#    ", "#    " },
            ['H'] = new[] { "#   #", "#   #", "#   #", "#####", "#   #", "#   #", "#   #" },
            ['I'] = new[] { "#####", "  #  ", "  #  ", "  #  ", "  #  ", "  #  ", "#####" },
            ['K'] = new[] { "#   #", "#  # ", "# #  ", "##   ", "# #  ", "#  # ", "#   #" },
            ['L'] = new[] { "#    ", "#    ", "#    ", "#    ", "#    ", "#    ", "#####" },
            ['N'] = new[] { "#   #", "##  #", "# # #", "# # #", "#  ##", "#   #", "#   #" },
            ['O'] = new[] { " ### ", "#   #", "#   #", "#   #", "#   #", "#   #", " ### " },
            ['P'] = new[] { "#### ", "#   #", "#   #", "#### ", "#    ", "#    ", "#    " },
            ['R'] = new[] { "#### ", "#   #", "#   #", "#### ", "# #  ", "#  # ", "#   #" },
            ['S'] = new[] { " ####", "#    ", "#    ", " ### ", "    #", "    #", "#### " },
            ['T'] = new[] { "#####", "  #  ", "  #  ", "  #  ", "  #  ", "  #  ", "  #  " },
            ['U'] = new[] { "#   #", "#   #", "#   #", "#   #", "#   #", "#   #", " ### " },
            ['V'] = new[] { "#   #", "#   #", "#   #", "#   #", " # # ", " # # ", "  #  " },
        };

        void Graffiti(Geo g, int l)
        {
            if (l < 0 || l >= _info.Length) return;
            var info = _info[l];
            if (!info.Valid || info.Dmg >= 3 || _campLots.Contains(l) || _ruin.ContainsKey(l)) return;
            if (info.A != Arch.House && info.A != Arch.Apartment && info.A != Arch.Office && info.A != Arch.Industrial && info.A != Arch.Shop && info.A != Arch.Civic) return;
            if (Hf(l, 910) > 0.075f || info.WallTop < 0.34f || ClearedRect(info.R)) return;
            // a face whose outside is street / sidewalk; side faces first so the paint avoids the door
            int face = -1;
            for (int s = 1; s <= 4 && face < 0; s++)
            {
                int f = (info.Front + s) & 3;
                FaceEdge(info.R, f, out var a, out var b, out var o);
                var m = (a + b) * 0.5f + o * 1.0f;
                int cc = Cls(Mathf.FloorToInt((TU(m) - u0) / CELL), Mathf.FloorToInt((TV(m) - v0) / CELL));
                if (cc == C_SIDE || cc == C_ROAD) face = f;
            }
            if (face < 0) return;
            FaceEdge(info.R, face, out var A, out var Bp, out var O);
            float len = (Bp - A).magnitude;
            var along = (Bp - A) / Mathf.Max(1e-4f, len);
            var tdir = new Vector2(O.y, -O.x);                     // reading direction, seen from outside
            if (Vector2.Dot(tdir, along) < 0f) { var tmp = A; A = Bp; Bp = tmp; along = -along; }
            bool front = face == info.Front;
            float room = front ? len * 0.4f : len * 0.8f;          // front face: keep to one side of the door
            float ctr = front ? (Hf(l, 911) < 0.5f ? 0.25f : 0.75f) * len : len * (0.4f + Hf(l, 912) * 0.2f);
            int wi = (int)(Hf(l, 913) * ST_WORDS.Length) % ST_WORDS.Length;
            string word = null; float px = 0f;
            for (int tries = 0; tries < ST_WORDS.Length; tries++)
            {
                string w = ST_WORDS[(wi + tries) % ST_WORDS.Length];
                int cols = w.Length * 6 - 1;
                float p = Mathf.Min(Mathf.Min(0.034f, room / cols), (info.WallTop - 0.12f) / 8f);
                if (p >= 0.017f) { word = w; px = p; break; }
            }
            if (word == null) return;
            Color col = Hf(l, 914) < 0.65f ? ST_PAINT_R : ST_PAINT_W;
            float width = (word.Length * 6 - 1) * px;
            var start = A + along * (ctr - width * 0.5f) + O * 0.012f;
            float yTop = 0.1f + 7f * px + Hf(l, 915) * Mathf.Max(0f, info.WallTop - 0.16f - 7f * px) * 0.5f;
            float slant = (Hf(l, 916) - 0.5f) * 0.25f * px;      // hand-sprayed: baseline drifts a little
            int ci = 0;
            foreach (char ch in word)
            {
                if (ST_FONT.TryGetValue(ch, out var rows))
                {
                    for (int r = 0; r < 7; r++)
                    {
                        string row = rows[r];
                        int c0 = -1;
                        for (int c = 0; c <= 5; c++)
                        {
                            bool on = c < 5 && row[c] == '#';
                            if (on && c0 < 0) c0 = c;
                            if (!on && c0 >= 0)
                            {
                                float s0 = (ci * 6 + c0) * px, s1 = (ci * 6 + c) * px;
                                float yo = slant * ci;
                                g.Wall(start + along * s0, start + along * s1, O, yTop - (r + 1) * px + yo, yTop - r * px + yo, col);
                                c0 = -1;
                            }
                        }
                    }
                    // a paint drip under some letters
                    if (Hf(l * 37 + ci, 917) < 0.3f)
                    {
                        float s = (ci * 6 + 1 + (int)(Hf(l * 37 + ci, 918) * 3f)) * px, yo = slant * ci;
                        float dl = px * (1.5f + Hf(l * 37 + ci, 919) * 2.5f);
                        g.Wall(start + along * s, start + along * (s + px * 0.5f), O, yTop - 7f * px - dl + yo, yTop - 7f * px + yo, Mul(col, 0.9f));
                    }
                }
                ci++;
            }
            Stats.TryGetValue("graffiti", out long gn); Stats["graffiti"] = gn + 1;
        }
    }
}
