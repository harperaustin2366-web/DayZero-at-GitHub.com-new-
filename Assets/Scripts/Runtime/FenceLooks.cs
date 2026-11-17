// v0.16 graphics idea #13 "Fence materials you can tell apart": every fence tier gets its own build and colour
// (0 wood planks, 1 chain-link on steel posts, 2 corrugated sheet metal, 3 precast concrete panels, 4 reinforced
// concrete with a steel cap and razor wire), and shows its damage by the tile's hp fraction:
//   below 75%: cracks, dents, splits;  below 50%: missing planks / holes / bent mesh / torn sheets;
//   below 25%: big gaps, leaning sections, sagging mesh, debris at the base.
// Gates are built from the same material. Pure C# on UnityEngine value types (render only, no sim reads beyond the
// tier and hp fraction the callers pass), so the offline preview draws exactly what the game draws.
using System;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed partial class MapGen
    {
        static readonly Color PLANK_A = Hex("#8a6a44"), PLANK_B = Hex("#7c7062"), PLANK_C = Hex("#9c7c50"), PLANK_D = Hex("#6c5640");
        static readonly Color PLANK_DARK = Hex("#2c231b"), PLANK_RAW = Hex("#c09a68");
        static readonly Color MESH_L = Hex("#b8c2c6"), MESH_D = Hex("#6e787c"), POST_S = Hex("#6c7478"), POST_ST = Hex("#9aa4a8");
        static readonly Color SHEET_A = Hex("#7d8b96"), SHEET_B = Hex("#8c6446"), SHEET_C = Hex("#6c7c6e"), SHEET_D = Hex("#a29c90");
        static readonly Color F_CRACK = Hex("#24201c"), F_REBAR = Hex("#6a3e24"), F_STEEL = Hex("#4c5256"), F_HAZ = Hex("#c09030");

        /// <summary>0 intact, 1 below 75% (cracks / dents), 2 below 50% (holes / missing pieces), 3 below 25% (gaps, leaning, debris).</summary>
        public static int FenceStage(float frac) => frac < 0.25f ? 3 : frac < 0.5f ? 2 : frac < 0.75f ? 1 : 0;

        static Vector3 O3(Vector2 n) => new Vector3(n.x, 0f, -n.y);

        // ---------------- shared builders ----------------
        /// <summary>Vertex grid of a vertical panel a0..a1, (nx+1)*(ny+1) points, row-major from the bottom. The top edge
        /// is offset by lean along nrm; corr zig-zags alternate columns along nrm (corrugation).</summary>
        static Vector3[] PanelGrid(Vector2 a0, Vector2 a1, Vector2 nrm, float y0, float y1, float lean, int nx, int ny, float corr)
        {
            var v = new Vector3[(nx + 1) * (ny + 1)];
            for (int j = 0; j <= ny; j++)
            {
                float fj = (float)j / ny;
                for (int i = 0; i <= nx; i++)
                {
                    var b = Vector2.Lerp(a0, a1, (float)i / nx) + nrm * (lean * fj + ((i & 1) == 1 ? corr : 0f));
                    v[j * (nx + 1) + i] = Geo.W(b, Mathf.Lerp(y0, y1, fj));
                }
            }
            return v;
        }

        /// <summary>Two-sided panel from a grid. holes: bit (j*nx+i) set = cell missing. diamonds: each cell drawn as four
        /// triangles round its centre, light top/bottom and dark sides, which reads as chain-link weave.</summary>
        static void PanelDraw(Geo g, Vector3[] v, int nx, int ny, Vector2 nrm, Color c, ulong holes, bool diamonds, int salt, float var)
        {
            var on = O3(nrm);
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    int cell = j * nx + i;
                    if (cell < 64 && (holes >> cell & 1UL) != 0) continue;
                    var p00 = v[j * (nx + 1) + i]; var p10 = v[j * (nx + 1) + i + 1];
                    var p11 = v[(j + 1) * (nx + 1) + i + 1]; var p01 = v[(j + 1) * (nx + 1) + i];
                    Color cc = var > 0f ? Lerp(c, Mul(c, 0.82f), Hf(salt + cell * 13, 41) * var) : c;
                    if (!diamonds)
                    {
                        g.Face(p00, p10, p11, p01, cc, on);
                        g.Face(p10, p00, p01, p11, Mul(cc, 0.94f), -on);
                        continue;
                    }
                    var m = (p00 + p10 + p11 + p01) * 0.25f;
                    Color lt = cc, dk = Lerp(cc, MESH_D, 0.55f);
                    g.Tri(p00, p10, m, dk, on); g.Tri(p10, p11, m, lt, on); g.Tri(p11, p01, m, dk, on); g.Tri(p01, p00, m, lt, on);
                    g.Tri(p10, p00, m, dk, -on); g.Tri(p11, p10, m, lt, -on); g.Tri(p01, p11, m, dk, -on); g.Tri(p00, p01, m, lt, -on);
                }
        }

        /// <summary>Thin stripe painted on both faces of a slab of half-thickness ht (cracks, rust streaks, splits).
        /// off: position along dir; slant: how far the top end runs along dir.</summary>
        static void Streak(Geo g, Vector2 p, Vector2 dir, Vector2 nrm, float ht, float off, float y0, float y1, float wid, float slant, Color c, float lean = 0f, float h = 1f)
        {
            for (int sd = -1; sd <= 1; sd += 2)
            {
                var o = nrm * (sd * (ht + 0.004f));
                float l0 = lean * (y0 / h), l1 = lean * (y1 / h);
                var a = p + dir * off + o + nrm * l0; var b = p + dir * (off + slant) + o + nrm * l1;
                var A0 = Geo.W(a - dir * wid, y0); var A1 = Geo.W(a + dir * wid, y0);
                var B1 = Geo.W(b + dir * wid, y1); var B0 = Geo.W(b - dir * wid, y1);
                if (sd > 0) g.QuadRaw(A0, A1, B1, B0, c, c, c, c); else g.QuadRaw(A1, A0, B0, B1, c, c, c, c);
            }
        }

        /// <summary>Box whose top is shifted by lean along nrm (planks / slabs that tilt as they fail).</summary>
        static void TiltBox(Geo g, Vector2 p, Vector2 dir, Vector2 nrm, float hl, float ht, float y0, float y1, float lean, Color wall, Color top)
        {
            if (Mathf.Abs(lean) < 0.004f) { g.Box(new ORect(p, dir, hl, ht).Quad(), y0, y1, wall, top); return; }
            var q = new ORect(p, dir, hl, ht).Quad();
            var t = new ORect(p + nrm * lean, dir, hl, ht).Quad();
            var ctr = Geo.W(p + nrm * lean * 0.5f, (y0 + y1) * 0.5f);
            for (int i = 0; i < 4; i++)
            {
                int k = (i + 1) & 3;
                var A = Geo.W(q[i], y0); var B = Geo.W(q[k], y0); var C = Geo.W(t[k], y1); var D = Geo.W(t[i], y1);
                g.Face(A, B, C, D, wall, (A + C) * 0.5f - ctr);
            }
            g.Flat(t, y1, Geo.Shade(top, Vector3.up));
        }

        // ---------------- entry points (called by the wall / fence-line drawing) ----------------
        /// <summary>One element of a fence run centred at p, st long, looking like its tier at hp fraction frac.</summary>
        void WallPiece(Geo g, Vector2 p, Vector2 dir, Vector2 nrm, float st, int tier, float frac, int salt)
        {
            int s = FenceStage(frac);
            switch (tier)
            {
                case 0: PlankPiece(g, p, dir, nrm, st, s, salt); break;
                case 1: ChainPiece(g, p, dir, nrm, st, s, salt); break;
                case 2: SheetPiece(g, p, dir, nrm, st, s, salt); break;
                default: ConcretePiece(g, p, dir, nrm, st, s, salt, tier >= 4); break;
            }
        }

        /// <summary>A gate in the fence's own material: two posts, a lintel and a leaf ~0.84 wide.</summary>
        void GateLeaf(Geo g, Vector2 p, Vector2 dir, Vector2 nrm, int tier, float frac)
        {
            int s = FenceStage(frac);
            int salt = Mathf.FloorToInt(p.x * 37f) * 131 + Mathf.FloorToInt(p.y * 37f);
            var l = p - dir * 0.45f; var r = p + dir * 0.45f;
            float lean = s == 3 ? 0.12f : s == 2 ? 0.04f : 0f;
            switch (tier)
            {
                case 0:
                {
                    g.Cyl(l, 0.07f, 0f, 1.25f, LOG, LOG_TIP, 6); g.Cyl(r, 0.07f, 0f, 1.25f, LOG, LOG_TIP, 6);
                    g.Beam(Geo.W(l, 1.2f), Geo.W(r, 1.2f), 0.045f, TIMBER);
                    for (int i = 0; i < 8; i++) PlankPiece(g, p + dir * (-0.3675f + i * 0.105f), dir, nrm, 0.105f, s, salt + i * 7, 0.95f, false);
                    if (s < 3)
                    {
                        var o = nrm * 0.03f;   // Z brace: two ledgers and a diagonal
                        g.Beam(Geo.W(p - dir * 0.4f + o, 0.2f), Geo.W(p + dir * 0.4f + o, 0.2f), 0.025f, TIMBER);
                        g.Beam(Geo.W(p - dir * 0.4f + o, 0.78f), Geo.W(p + dir * 0.4f + o, 0.78f), 0.025f, TIMBER);
                        g.Beam(Geo.W(p - dir * 0.38f + o, 0.22f), Geo.W(p + dir * 0.38f + o, 0.76f), 0.022f, TIMBER);
                    }
                    break;
                }
                case 1:
                {
                    g.Cyl(l, 0.04f, 0f, 1.25f, POST_S, POST_ST, 6); g.Cyl(r, 0.04f, 0f, 1.25f, POST_S, POST_ST, 6);
                    g.Beam(Geo.W(l, 1.2f), Geo.W(r, 1.2f), 0.025f, POST_S);
                    var a0 = p - dir * 0.4f; var a1 = p + dir * 0.4f;
                    var v = PanelGrid(a0, a1, nrm, 0.05f, 0.92f, lean, 4, 4, 0f);
                    ulong holes = s >= 2 ? (1UL << (int)(Hf(salt, 3) * 16f)) : 0UL;
                    if (s == 3) holes |= 1UL << (int)(Hf(salt, 4) * 16f) | 1UL << (int)(Hf(salt, 5) * 16f);
                    if (s >= 1) { int c = 1 * 5 + 2; v[c] += O3(nrm) * 0.06f; }
                    PanelDraw(g, v, 4, 4, nrm, Lerp(MESH_L, MESH_D, s * 0.12f), holes, true, salt, 0f);
                    // tube frame + diagonal brace
                    var t0 = a0 + nrm * lean; var t1 = a1 + nrm * lean;
                    g.Beam(Geo.W(a0, 0.05f), Geo.W(a1, 0.05f), 0.016f, POST_ST);
                    g.Beam(Geo.W(t0, 0.92f), Geo.W(t1, 0.92f), 0.016f, POST_ST);
                    g.Beam(Geo.W(a0, 0.05f), Geo.W(t0, 0.92f), 0.016f, POST_ST);
                    g.Beam(Geo.W(a1, 0.05f), Geo.W(t1, 0.92f), 0.016f, POST_ST);
                    if (s < 3) g.Beam(Geo.W(a0, 0.07f), Geo.W(t1, 0.9f), 0.012f, POST_S);
                    break;
                }
                case 2:
                {
                    g.Cyl(l, 0.05f, 0f, 1.25f, Hex("#4a4440"), Hex("#5a5450"), 6); g.Cyl(r, 0.05f, 0f, 1.25f, Hex("#4a4440"), Hex("#5a5450"), 6);
                    g.Beam(Geo.W(l, 1.2f), Geo.W(r, 1.2f), 0.03f, F_STEEL);
                    var a0 = p - dir * 0.41f; var a1 = p + dir * 0.41f;
                    var v = PanelGrid(a0, a1, nrm, 0.04f, 0.98f, lean, 10, 3, 0.022f);
                    ulong holes = 0;
                    if (s >= 2) holes |= 1UL << (10 + (int)(Hf(salt, 3) * 10f));
                    if (s == 3) holes |= 1UL << (20 + (int)(Hf(salt, 4) * 10f)) | 1UL << (21 + (int)(Hf(salt, 4) * 9f));
                    if (s >= 1) for (int i = 3; i <= 5; i++) v[1 * 11 + i] -= O3(nrm) * 0.05f;
                    PanelDraw(g, v, 10, 3, nrm, Lerp(SHEET_A, Hex("#3a3430"), s * 0.12f), holes, false, salt, 0.25f);
                    var t0 = a0 + nrm * lean; var t1 = a1 + nrm * lean; var o = nrm * 0.04f;
                    g.Beam(Geo.W(a0 + o, 0.12f), Geo.W(a1 + o, 0.12f), 0.02f, F_STEEL);
                    g.Beam(Geo.W(t0 + o, 0.88f), Geo.W(t1 + o, 0.88f), 0.02f, F_STEEL);
                    if (s < 3) g.Beam(Geo.W(a0 + o, 0.14f), Geo.W(t1 + o, 0.86f), 0.018f, F_STEEL);
                    if (s >= 1) Streak(g, p, dir, nrm, 0.025f, 0.12f, 0.1f, 0.7f, 0.03f, 0f, F_REBAR);
                    break;
                }
                default:
                {
                    // concrete pillars, a heavy steel slide leaf with hazard chevrons
                    float ph = tier >= 4 ? 1.35f : 1.28f;
                    g.Box(new ORect(l, dir, 0.08f, 0.1f).Quad(), 0f, ph, CONC_D, CONC_TOP, 0.2f);
                    g.Box(new ORect(r, dir, 0.08f, 0.1f).Quad(), 0f, ph, CONC_D, CONC_TOP, 0.2f);
                    g.Box(new ORect(p, dir, 0.53f, 0.08f).Quad(), 1.16f, 1.26f, CONC, CONC_TOP);
                    var a0 = p - dir * 0.37f; var a1 = p + dir * 0.37f;
                    var v = PanelGrid(a0, a1, nrm, 0.03f, 1.02f, lean * 0.6f, 4, 3, 0f);
                    ulong holes = s >= 2 ? 1UL << (4 + (int)(Hf(salt, 3) * 8f)) : 0UL;
                    if (s >= 1) v[1 * 5 + 2] += O3(nrm) * 0.06f;
                    if (s == 3) { v[3 * 5 + 4] += O3(nrm) * 0.15f - Vector3.up * 0.2f; }
                    PanelDraw(g, v, 4, 3, nrm, Lerp(F_STEEL, Hex("#2c2a28"), s * 0.12f), holes, false, salt, 0.2f);
                    if (s < 3)
                        for (int k = 0; k < 3; k++)
                            Streak(g, p, dir, nrm, 0.0f, -0.3f + k * 0.25f, 0.55f, 0.85f, 0.035f, 0.12f, F_HAZ, lean * 0.6f, 1.02f);
                    break;
                }
            }
            if (s == 3) GroundBits(g, p, dir, nrm, tier, salt, 3);
        }

        /// <summary>Loose pieces lying at the foot of a badly damaged section (both sides).</summary>
        void GroundBits(Geo g, Vector2 p, Vector2 dir, Vector2 nrm, int tier, int salt, int n)
        {
            for (int i = 0; i < n; i++)
            {
                if (Hf(salt + i * 29, 51) < 0.35f) continue;
                var q = p + dir * ((Hf(salt + i, 52) - 0.5f) * 0.6f) + nrm * ((Hf(salt + i, 53) < 0.5f ? -1f : 1f) * (0.12f + Hf(salt + i, 54) * 0.22f));
                float a = Hf(salt + i, 55) * Mathf.PI; var dd = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                switch (tier)
                {
                    case 0: g.BoxAt(q, dd, 0.16f + Hf(salt + i, 56) * 0.14f, 0.04f, 0f, 0.022f, Lerp(PLANK_D, PLANK_DARK, 0.3f), PLANK_A); break;
                    case 1: g.Beam(Geo.W(q - dd * 0.12f, 0.03f), Geo.W(q + dd * 0.12f, 0.045f), 0.022f, MESH_D); break;
                    case 2: g.BoxAt(q, dd, 0.14f, 0.1f, 0f, 0.012f, SHEET_B, Lerp(SHEET_B, SHEET_D, 0.3f)); break;
                    default: g.BoxAt(q, dd, 0.06f + Hf(salt + i, 57) * 0.05f, 0.05f, 0f, 0.05f + Hf(salt + i, 58) * 0.05f, CONC_D, CONC); break;
                }
            }
        }

        // ---------------- tier 0: wood planks ----------------
        void PlankPiece(Geo g, Vector2 p, Vector2 dir, Vector2 nrm, float st, int s, int salt, float baseH = 0f, bool debris = true)
        {
            float hw = Mathf.Min(st * 0.44f, 0.055f), ht = 0.017f;
            float r = Hf(salt, 3);
            bool missing = (s == 2 && r < 0.28f) || (s == 3 && r < 0.55f);
            if (missing)
            {
                // gap in the fence; the plank lies at the base (or a short splintered stub is left)
                if (Hf(salt, 4) < 0.45f) g.Box(new ORect(p, dir, hw, ht).Quad(), 0f, 0.1f + Hf(salt, 5) * 0.12f, PLANK_DARK, Lerp(PLANK_RAW, PLANK_DARK, 0.3f));
                if (debris && Hf(salt, 6) < (s == 3 ? 0.6f : 0.35f))
                {
                    float a = (Hf(salt, 7) - 0.5f) * 1.2f; var dd = new Vector2(Mathf.Cos(a) * dir.x - Mathf.Sin(a) * dir.y, Mathf.Sin(a) * dir.x + Mathf.Cos(a) * dir.y);
                    var q = p + nrm * ((Hf(salt, 8) < 0.5f ? -1f : 1f) * (0.2f + Hf(salt, 9) * 0.2f));
                    g.BoxAt(q, new Vector2(-dd.y, dd.x), 0.3f + Hf(salt, 10) * 0.12f, hw, 0f, 0.022f, Lerp(PLANK_D, PLANK_DARK, 0.25f), Lerp(PLANK_A, PLANK_B, Hf(salt, 11)));
                }
                return;
            }
            float h = (baseH > 0f ? baseH : 0.9f + Hf(salt, 1) * 0.1f);
            Color[] pal = { PLANK_A, PLANK_B, PLANK_C, PLANK_D };
            Color c = Lerp(pal[(int)(Hf(salt, 2) * 4f) & 3], PLANK_B, Hf(salt, 12) * 0.3f);
            if (s >= 1) c = Lerp(c, PLANK_DARK, 0.1f * s);
            float lean = s == 3 ? (Hf(salt, 13) - 0.25f) * 0.32f : s == 2 ? (Hf(salt, 13) - 0.5f) * 0.08f : 0f;
            bool broken = s >= 2 && Hf(salt, 14) < (s == 2 ? 0.25f : 0.4f);
            if (broken) h *= 0.35f + Hf(salt, 15) * 0.3f;
            TiltBox(g, p, dir, nrm, hw, ht, 0f, h, lean, c, Lerp(c, PLANK_RAW, 0.2f));
            var top = p + nrm * lean;
            var tq = new ORect(top, dir, hw, ht).Quad();
            if (broken) g.Pyramid(tq, h, top + dir * hw * (Hf(salt, 16) < 0.5f ? 0.8f : -0.8f), h + 0.07f, Lerp(PLANK_RAW, c, 0.4f));   // splintered end
            else g.Pyramid(tq, h, top, h + 0.06f, Lerp(c, PLANK_RAW, 0.15f));   // pointed picket top
            // weathered dark seam on the plank edge (reads as separate boards) + splits / cracks when hurt
            Streak(g, p, dir, nrm, ht, hw * 0.92f, 0.02f, h * 0.98f, 0.006f, 0f, Mul(c, 0.55f), lean, h);
            if (s >= 1 && Hf(salt, 17) < 0.55f + 0.15f * s)
            {
                float off = (Hf(salt, 18) - 0.5f) * hw;
                Streak(g, p, dir, nrm, ht, off, h * (0.25f + Hf(salt, 19) * 0.2f), h * 0.92f, 0.007f, (Hf(salt, 20) - 0.5f) * 0.03f, F_CRACK, lean, h);
            }
            if (s >= 1 && Hf(salt, 21) < 0.2f) Streak(g, p, dir, nrm, ht, 0f, h * 0.45f, h * 0.55f, hw * 0.5f, 0f, Lerp(c, PLANK_DARK, 0.6f), lean, h);   // knot hole / scorch
        }

        // ---------------- tier 1: chain-link ----------------
        void ChainPiece(Geo g, Vector2 p, Vector2 dir, Vector2 nrm, float st, int s, int salt)
        {
            const int NX = 6, NY = 4;
            var a0 = p - dir * st * 0.5f; var a1 = p + dir * st * 0.5f;
            float h = 0.8f;
            float postLean = s == 3 && Hf(salt, 2) < 0.5f ? (Hf(salt, 3) - 0.3f) * 0.3f : 0f;
            // post with a barbed-wire arm leaning out
            var pt = a0 + nrm * postLean;
            g.Beam(Geo.W(a0, 0f), Geo.W(pt, 0.88f), 0.026f, POST_S);
            g.Cyl(pt, 0.032f, 0.86f, 0.9f, POST_ST, POST_ST, 6);
            var arm = pt + nrm * 0.1f;
            g.Beam(Geo.W(pt, 0.86f), Geo.W(arm, 0.98f), 0.012f, POST_S);
            float lean = s == 3 ? (Hf(salt, 4) - 0.35f) * 0.28f : 0f;
            var v = PanelGrid(a0, a1, nrm, 0.04f, h, lean, NX, NY, 0f);
            var on = O3(nrm);
            if (s >= 1)
            {
                // dent: push a patch of the weave in
                int ci = 2 + (int)(Hf(salt, 5) * 2f), cj = 1 + (int)(Hf(salt, 6) * 2f);
                float push = (Hf(salt, 7) < 0.5f ? -1f : 1f) * (0.05f + 0.03f * s);
                v[cj * (NX + 1) + ci] += on * push;
                v[cj * (NX + 1) + ci + 1] += on * push * 0.6f;
            }
            ulong holes = 0;
            if (s >= 2)
            {
                // bent: bottom corner peeled up and out; a torn hole
                for (int i = NX - 1; i <= NX; i++) v[i] += on * 0.14f + Vector3.up * 0.16f;
                v[NX + 1 + NX] += on * 0.07f + Vector3.up * 0.06f;
                holes |= 1UL << (NX * (1 + (int)(Hf(salt, 8) * 2f)) + (int)(Hf(salt, 9) * NX));
            }
            if (s == 3)
            {
                // sagging top, big ragged gap
                for (int i = 1; i < NX; i++) v[NY * (NX + 1) + i] -= Vector3.up * (0.22f * Mathf.Sin(Mathf.PI * i / NX)) - on * 0.05f;
                int c0 = (int)(Hf(salt, 10) * (NX - 2));
                for (int j = 1; j < NY; j++) for (int i = c0; i < c0 + 2 + (j & 1); i++) holes |= 1UL << (j * NX + Math.Min(i, NX - 1));
            }
            Color mc = Lerp(MESH_L, Hex("#7e7a70"), 0.12f * s + Hf(salt, 11) * 0.08f);
            PanelDraw(g, v, NX, NY, nrm, mc, holes, true, salt, 0f);
            // top rail + bottom tension wire + two barbed strands
            var t0 = v[NY * (NX + 1)]; var t1 = v[NY * (NX + 1) + NX];
            if (s < 3) g.Beam(t0, t1, 0.013f, POST_ST);
            else { var tm = v[NY * (NX + 1) + NX / 2]; g.Beam(t0, tm, 0.013f, POST_ST); }
            g.Beam(v[0], v[NX], 0.006f, MESH_D);
            if (s < 3)
                for (int k = 0; k < 2; k++)
                {
                    var b0 = Vector2.Lerp(pt, arm, 0.5f + k * 0.5f); var b1 = b0 + dir * st;
                    g.Beam(Geo.W(b0, 0.92f + k * 0.06f), Geo.W(b1, 0.92f + k * 0.06f - (s >= 2 && k == 1 ? 0.08f : 0f)), 0.005f, Hex("#5a6064"));
                }
            if (s == 3) GroundBits(g, p, dir, nrm, 1, salt, 2);
        }

        // ---------------- tier 2: corrugated sheet metal ----------------
        void SheetPiece(Geo g, Vector2 p, Vector2 dir, Vector2 nrm, float st, int s, int salt)
        {
            const int NX = 6, NY = 3;
            Color[] pal = { SHEET_A, SHEET_B, SHEET_C, SHEET_D };
            Color pc = pal[(int)(Hf(salt, 2) * 4f) & 3];
            if (s >= 1) pc = Lerp(pc, Hex("#3a2e26"), 0.1f * s);
            var a0 = p - dir * st * 0.52f; var a1 = p + dir * st * 0.52f;
            float h = 0.98f + (Hf(salt, 1) - 0.5f) * 0.1f;
            bool post = (salt & 1) == 0;
            if (post) g.Cyl(a0 - nrm * 0.04f, 0.03f, 0f, 1.04f, Hex("#4a4440"), Hex("#5a5450"), 5);
            // stringer rails behind the sheets (stay when sheets go)
            var ri = -nrm * 0.045f;
            g.Beam(Geo.W(a0 + ri, 0.22f), Geo.W(a1 + ri, 0.22f), 0.016f, Hex("#4a4440"));
            if (s < 3) g.Beam(Geo.W(a0 + ri, 0.78f), Geo.W(a1 + ri, 0.78f), 0.016f, Hex("#4a4440"));
            if (s == 3 && Hf(salt, 3) < 0.4f)
            {
                // the whole sheet is down: lying at the base on one side
                float side = Hf(salt, 4) < 0.5f ? 1f : -1f;
                var fv = PanelGrid(a0 + nrm * side * 0.05f, a1 + nrm * side * 0.05f, nrm, 0.01f, 0.1f, side * h * 0.92f, NX, 1, 0.02f);
                PanelDraw(g, fv, NX, 1, nrm, Lerp(pc, Hex("#2e2622"), 0.2f), 0UL, false, salt, 0.2f);
                return;
            }
            float lean = s == 3 ? (Hf(salt, 5) - 0.3f) * 0.3f : s == 2 ? (Hf(salt, 5) - 0.5f) * 0.06f : 0f;
            float top = s == 3 ? h * (0.7f + Hf(salt, 6) * 0.25f) : h;
            var v = PanelGrid(a0, a1, nrm, 0f, top, lean, NX, NY, 0.022f);
            var on = O3(nrm);
            // ragged tops
            for (int i = 0; i <= NX; i++) v[NY * (NX + 1) + i] += Vector3.up * ((Hf(salt + i, 7) - 0.5f) * 0.05f);
            if (s >= 1)
            {
                // dents: a kicked-in patch
                int ci = 1 + (int)(Hf(salt, 8) * (NX - 2));
                v[1 * (NX + 1) + ci] -= on * 0.05f; v[1 * (NX + 1) + ci + 1] -= on * 0.04f; v[2 * (NX + 1) + ci] -= on * 0.03f;
            }
            ulong holes = 0;
            if (s >= 2)
            {
                // torn top corner folded over, a punched hole
                int tc = Hf(salt, 9) < 0.5f ? 0 : NX;
                v[NY * (NX + 1) + tc] += on * 0.12f - Vector3.up * 0.22f;
                v[(NY - 1) * (NX + 1) + tc] += on * 0.05f;
                holes |= 1UL << (NX + 1 + (int)(Hf(salt, 10) * (NX - 2)));
            }
            if (s == 3) { int c0 = (int)(Hf(salt, 11) * (NX - 2)); holes |= 3UL << (2 * NX + c0) | 1UL << (NX + c0); }
            PanelDraw(g, v, NX, NY, nrm, pc, holes, false, salt, 0.18f);
            // rivet line along the top stringer + rust streaks
            Streak(g, p, dir, nrm, 0.024f, 0f, top * 0.8f, top * 0.815f, st * 0.48f, 0f, Mul(pc, 0.6f), lean, top);
            if (s >= 1 || Hf(salt, 12) < 0.2f)
            {
                float off = (Hf(salt, 13) - 0.5f) * st * 0.6f;
                Streak(g, p, dir, nrm, 0.024f, off, top * (0.15f + Hf(salt, 14) * 0.2f), top * 0.78f, 0.02f, 0f, Lerp(RUST, SHEET_B, 0.3f), lean, top);
            }
            if (s == 3) GroundBits(g, p, dir, nrm, 2, salt, 2);
        }

        // ---------------- tier 3/4: precast concrete panels / reinforced ----------------
        void ConcretePiece(Geo g, Vector2 p, Vector2 dir, Vector2 nrm, float st, int s, int salt, bool reinforced)
        {
            const int NX = 3, NY = 3;
            float h = reinforced ? 1.15f : 1.05f;
            Color cc = Lerp(CONC, CONC_D, Hf(salt, 2) * 0.3f);
            if (s >= 1) cc = Lerp(cc, Hex("#5a564c"), 0.1f * s);
            // footing (T-wall base) stays even when the panel is wrecked
            g.Box(new ORect(p, dir, st * 0.49f, 0.15f).Quad(), 0f, 0.1f, Mul(cc, 0.85f), Mul(CONC_TOP, 0.92f), 0.2f);
            g.Frustum(new ORect(p, dir, st * 0.47f, 0.11f).Quad(), 0.1f, new ORect(p, dir, st * 0.47f, 0.075f).Quad(), 0.24f, cc, CONC_TOP, false);
            float hl = st * 0.47f;   // a joint gap between panels
            float lean = s == 3 ? (Hf(salt, 3) - 0.3f) * 0.22f : 0f;
            ulong holes = 0;
            if (s == 3 || (s == 2 && Hf(salt, 15) < 0.7f)) holes |= 1UL << (NX + (int)(Hf(salt, 4) * NX));               // a chunk out of the middle
            if (s == 3)
            {
                holes |= 1UL << (2 * NX + (int)(Hf(salt, 5) * NX)) | 1UL << (2 * NX + (int)(Hf(salt, 6) * NX));
                if (Hf(salt, 7) < 0.45f) holes |= 7UL << (2 * NX);                     // top third broken off
            }
            bool chip = s >= 1 && Hf(salt, 8) < 0.6f;
            int chipCol = Hf(salt, 9) < 0.5f ? 0 : NX - 1;
            float cw = hl * 2f / NX, y0 = 0.24f, ch = (h - y0) / NY;
            for (int j = 0; j < NY; j++)
                for (int i = 0; i < NX; i++)
                {
                    int cell = j * NX + i;
                    if ((holes >> cell & 1UL) != 0) continue;
                    float ya = y0 + j * ch, yb = ya + ch;
                    if (j == NY - 1 && chip && i == chipCol) yb -= 0.07f + 0.05f * s;
                    if (j == NY - 1 && s == 3 && (holes >> (cell - NX) & 1UL) != 0) continue;   // nothing floats over a hole at the end
                    var cp = p + dir * (-hl + cw * (i + 0.5f)) + nrm * lean * (ya - y0) / (h - y0);
                    TiltBox(g, cp, dir, nrm, cw * 0.5f, 0.07f, ya, yb, lean * ch / (h - y0), cc, CONC_TOP);
                }
            // exposed rebar in the holes
            for (int cell = 0; cell < NX * NY; cell++)
            {
                if ((holes >> cell & 1UL) == 0) continue;
                int i = cell % NX, j = cell / NX;
                var cp = p + dir * (-hl + cw * (i + 0.5f)) + nrm * lean * (j * ch) / (h - y0);
                float ya = y0 + j * ch;
                g.Beam(Geo.W(cp - dir * cw * 0.25f, ya), Geo.W(cp - dir * cw * 0.2f + nrm * (j == NY - 1 ? 0.06f : 0f), ya + ch * (j == NY - 1 ? 1.1f : 1f)), 0.007f, F_REBAR);
                g.Beam(Geo.W(cp + dir * cw * 0.22f, ya), Geo.W(cp + dir * cw * 0.3f - nrm * (j == NY - 1 ? 0.08f : 0f), ya + ch * (j == NY - 1 ? 0.9f : 1f)), 0.007f, F_REBAR);
                g.Beam(Geo.W(cp - dir * cw * 0.5f, ya + ch * 0.5f), Geo.W(cp + dir * cw * 0.5f, ya + ch * 0.5f), 0.006f, F_REBAR);
            }
            // cracks: a zig-zag on both faces
            if (s >= 1)
            {
                float off = (Hf(salt, 10) - 0.5f) * hl, yA = y0 + ch * 0.2f, yB = y0 + ch * (1.2f + Hf(salt, 11) * 0.6f), yC = h * 0.97f;
                float z = (Hf(salt, 12) < 0.5f ? 1f : -1f) * 0.07f;
                Streak(g, p, dir, nrm, 0.07f, off, yA, yB, 0.008f, z, F_CRACK, lean, h);
                if (s >= 1 && (holes >> (2 * NX) & 7UL) == 0) Streak(g, p, dir, nrm, 0.07f, off + z, yB, yC, 0.007f, -z * 0.7f, F_CRACK, lean, h);
                if (s >= 2) Streak(g, p, dir, nrm, 0.07f, -off * 0.5f, y0 + 0.02f, y0 + ch * 0.9f, 0.006f, -z, F_CRACK, lean, h);
            }
            // vertical joint shadow + lifting loops / steel cap + razor wire for reinforced
            Streak(g, p, dir, nrm, 0.07f, hl - 0.012f, y0, h - 0.02f, 0.008f, 0f, Mul(cc, 0.7f), lean, h);
            bool topIntact = (holes >> (2 * NX) & 7UL) == 0 && !chip;
            if (!reinforced)
            {
                if (topIntact && s < 3)
                    for (int k = -1; k <= 1; k += 2) { var lp = p + dir * (k * hl * 0.5f); g.Beam(Geo.W(lp - dir * 0.03f, h), Geo.W(lp + dir * 0.03f, h + 0.04f), 0.01f, F_REBAR); }
            }
            else
            {
                for (int k = 0; k < 2; k++)   // steel ribs
                {
                    float off = -hl * 0.5f + k * hl;
                    if ((holes >> (NX + (k == 0 ? 0 : NX - 1)) & 1UL) == 0) Streak(g, p, dir, nrm, 0.07f, off, y0 + 0.02f, h * (s == 3 ? 0.6f : 0.98f), 0.022f, 0f, F_STEEL, lean, h);
                }
                if (topIntact)
                {
                    TiltBox(g, p + nrm * lean, dir, nrm, hl, 0.085f, h, h + 0.05f, 0f, F_STEEL, Hex("#6a7074"));
                    if (s < 3)
                    {
                        // razor wire: a zig-zag coil along the cap
                        int nz = 6; var basep = p + nrm * lean;
                        for (int k = 0; k < nz; k++)
                        {
                            var q0 = basep + dir * (-hl + 2f * hl * k / nz) + nrm * ((k & 1) == 0 ? 0.05f : -0.05f);
                            var q1 = basep + dir * (-hl + 2f * hl * (k + 1) / nz) + nrm * ((k & 1) == 0 ? -0.05f : 0.05f);
                            g.Beam(Geo.W(q0, h + 0.1f), Geo.W(q1, h + 0.16f - (s >= 2 && k > 3 ? 0.07f : 0f)), 0.006f, Hex("#8a9294"));
                        }
                    }
                }
                if ((salt % 3) == 0 && s < 3)
                    Streak(g, p, dir, nrm, 0.07f, 0f, h * 0.62f, h * 0.7f, hl * 0.85f, 0f, F_HAZ, lean, h);
            }
            if (s >= 2) GroundBits(g, p, dir, nrm, 3, salt, s == 3 ? 4 : 2);
        }

        // ---------------- offline preview hook ----------------
        /// <summary>A short straight sample of one fence tier at one hp fraction, with a gate in the middle: three tiles
        /// of fence, gate, three tiles of fence along +x from 'at'. Used by the offline preview only.</summary>
        public Chunk FenceSample(Vector2 at, int tier, float frac, bool gate = true)
        {
            var g = new Geo(new MeshData(), new MeshData());
            var dir = new Vector2(1f, 0f); var nrm = new Vector2(0f, 1f);
            float step = WallStep(tier);
            float len = 3f;
            int n = Mathf.Max(1, Mathf.RoundToInt(len / step)); float st = len / n;
            for (int side = 0; side < 2; side++)
            {
                var a = at + dir * (side * (len + (gate ? 1f : 0f)));
                for (int i = 0; i < n; i++) WallPiece(g, a + dir * ((i + 0.5f) * st), dir, nrm, st, tier, frac, 7000 + side * 977 + i * 13 + tier * 100003);
                if (tier == 0) FenceRail(g, a, a + dir * len, nrm, 0);
            }
            if (gate) { var gp = at + dir * (len + 0.5f); GateLeaf(g, gp, dir, nrm, tier, frac); GateMark(g, gp, nrm); }
            return new Chunk { Mesh = g.M, Shadow = g.S };
        }
    }
}
