// Low-level mesh builder for the procedural map (MapGen). Pure C#: only UnityEngine value types
// (Vector2/3, Color/Color32, Mathf), no engine objects, so the same code runs inside Unity and in
// the offline preview harness (work/unity-verify/Preview) that renders PNGs without an editor.
//
// Conventions: 1 unit = 1 map tile (~3.5 m). Tile point (x, y) -> world (x, h, -y).
// Lighting is baked into vertex colours (art bible section 3): top faces keep the palette colour,
// walls facing the key light ~0.84, walls facing away ~0.52 with a slight cool shift.
using System.Collections.Generic;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed class MeshData
    {
        public readonly List<Vector3> V = new List<Vector3>(8192);
        public readonly List<Color32> C = new List<Color32>(8192);
        public readonly List<int> T = new List<int>(12288);
        public int Count => V.Count;
    }

    /// <summary>Oriented rectangle in tile space: centre, unit axes and half extents.</summary>
    public struct ORect
    {
        public Vector2 C, U, V; public float Hu, Hv;
        public ORect(Vector2 c, Vector2 u, float hu, float hv) { C = c; U = u; V = new Vector2(-u.y, u.x); Hu = hu; Hv = hv; }
        public Vector2 P(float s, float t) => C + U * (s * Hu) + V * (t * Hv);          // s,t in [-1,1]
        public Vector2 At(float du, float dv) => C + U * du + V * dv;                  // offsets in tiles
        public Vector2[] Quad() => new[] { P(-1, -1), P(1, -1), P(1, 1), P(-1, 1) };
        public ORect Inset(float d) => new ORect(C, U, Mathf.Max(0.05f, Hu - d), Mathf.Max(0.05f, Hv - d));
        public ORect Sub(float s0, float s1, float t0, float t1)
        {
            var c = P((s0 + s1) * 0.5f, (t0 + t1) * 0.5f);
            return new ORect(c, U, Hu * (s1 - s0) * 0.5f, Hv * (t1 - t0) * 0.5f);
        }
        public bool Contains(Vector2 p, float pad = 0f)
        {
            var d = p - C;
            return Mathf.Abs(Vector2.Dot(d, U)) <= Hu + pad && Mathf.Abs(Vector2.Dot(d, V)) <= Hv + pad;
        }
        public float Short => Mathf.Min(Hu, Hv);
    }

    public sealed class Geo
    {
        public readonly MeshData M;
        public readonly MeshData S;     // shadow mesh (alpha in vertex colour), may be null
        public Geo(MeshData m, MeshData s) { M = m; S = s; }

        // key light: from screen top-left at the default camera yaw (MapView.YAW), 50 deg elevation.
        // Shade(): top faces ~1.0 (palette colour), lit walls ~0.89, walls facing away ~0.57 + cool shift
        public static readonly Vector3 SUN = new Vector3(0.103f, 0.766f, 0.634f).normalized;
        public const float SHADOW_Y = 0.052f;
        public static readonly Color GRIME = Hex("#3a3226");

        public static Vector3 W(Vector2 p, float h) => new Vector3(p.x, h, -p.y);

        public static Color Hex(string s)
        {
            if (s.StartsWith("#")) s = s.Substring(1);
            int v = System.Convert.ToInt32(s, 16);
            return new Color(((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f, 1f);
        }

        public static Color Mul(Color c, float k) => new Color(c.r * k, c.g * k, c.b * k, 1f);
        public static Color Lerp(Color a, Color b, float t) { t = Mathf.Clamp01(t); return new Color(a.r + (b.r - a.r) * t, a.g + (b.g - a.g) * t, a.b + (b.b - a.b) * t, 1f); }
        public static Color Desat(Color c, float amt)
        {
            float g = c.r * 0.3f + c.g * 0.59f + c.b * 0.11f;
            return Lerp(c, new Color(g, g, g, 1f), amt);
        }

        public static Color Shade(Color c, Vector3 n)
        {
            float nl = Mathf.Max(0f, Vector3.Dot(n, SUN));
            float k = 0.57f + 0.50f * nl + 0.06f * Mathf.Max(0f, n.y);
            float s = 1f - nl;
            return new Color(Mathf.Clamp01(c.r * k * (1f - 0.05f * s)), Mathf.Clamp01(c.g * k * (1f - 0.012f * s)), Mathf.Clamp01(c.b * k * (1f + 0.06f * s)), 1f);
        }

        static Color32 C32(Color c) => new Color32((byte)(Mathf.Clamp01(c.r) * 255f + 0.5f), (byte)(Mathf.Clamp01(c.g) * 255f + 0.5f), (byte)(Mathf.Clamp01(c.b) * 255f + 0.5f), 255);

        static Vector3 FaceN(Vector3 a, Vector3 b, Vector3 c, Vector3 outward)
        {
            var n = Vector3.Cross(b - a, c - a);
            float m = n.magnitude;
            if (m < 1e-8f) return Vector3.up;
            n = n / m;
            if (Vector3.Dot(n, outward) < 0f) n = -n;
            return n;
        }

        // ---------------- raw ----------------
        public void QuadRaw(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color ca, Color cb, Color cc, Color cd)
        {
            var m = M; int i = m.V.Count;
            m.V.Add(a); m.V.Add(b); m.V.Add(c); m.V.Add(d);
            m.C.Add(C32(ca)); m.C.Add(C32(cb)); m.C.Add(C32(cc)); m.C.Add(C32(cd));
            m.T.Add(i); m.T.Add(i + 1); m.T.Add(i + 2); m.T.Add(i); m.T.Add(i + 2); m.T.Add(i + 3);
        }

        public void TriRaw(Vector3 a, Vector3 b, Vector3 c, Color col)
        {
            var m = M; int i = m.V.Count; var k = C32(col);
            m.V.Add(a); m.V.Add(b); m.V.Add(c); m.C.Add(k); m.C.Add(k); m.C.Add(k);
            m.T.Add(i); m.T.Add(i + 1); m.T.Add(i + 2);
        }

        /// <summary>Flat-shaded quad; outward hints which side the normal faces.</summary>
        public void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color col, Vector3 outward)
        {
            var n = FaceN(a, b, c, outward);
            var k = Shade(col, n);
            QuadRaw(a, b, c, d, k, k, k, k);
        }

        public void Tri(Vector3 a, Vector3 b, Vector3 c, Color col, Vector3 outward)
        {
            TriRaw(a, b, c, Shade(col, FaceN(a, b, c, outward)));
        }

        /// <summary>Horizontal quad from 4 tile-space points at height h (colours per corner, unshaded = top light).</summary>
        public void Flat(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float h, Color c0, Color c1, Color c2, Color c3)
        {
            QuadRaw(W(p0, h), W(p1, h), W(p2, h), W(p3, h), c0, c1, c2, c3);
        }
        public void Flat(Vector2[] q, float h, Color c) => Flat(q[0], q[1], q[2], q[3], h, c, c, c, c);

        /// <summary>Vertical wall a->b (tile space) from y0 to y1. ao: height of the darkened base band (grime + contact AO).</summary>
        public void Wall(Vector2 a, Vector2 b, Vector2 outDir, float y0, float y1, Color col, float ao = 0f, Color? topTint = null)
        {
            var o3 = new Vector3(outDir.x, 0f, -outDir.y);
            var n = FaceN(W(a, y0), W(b, y0), W(b, y1), o3);
            Color lc = Shade(col, n);
            Color tc = topTint.HasValue ? Shade(topTint.Value, n) : lc;
            if (ao > 0f && y1 - y0 > ao * 1.4f)
            {
                float ym = y0 + ao;
                Color bc = Lerp(Mul(lc, 0.66f), Shade(GRIME, n), 0.25f);
                QuadRaw(W(a, y0), W(b, y0), W(b, ym), W(a, ym), bc, bc, lc, lc);
                QuadRaw(W(a, ym), W(b, ym), W(b, y1), W(a, y1), lc, lc, tc, tc);
            }
            else if (ao > 0f)
            {
                Color bc = Mul(lc, 0.75f);
                QuadRaw(W(a, y0), W(b, y0), W(b, y1), W(a, y1), bc, bc, tc, tc);
            }
            else QuadRaw(W(a, y0), W(b, y0), W(b, y1), W(a, y1), lc, lc, tc, tc);
        }

        public static Vector2 OutDir(Vector2 a, Vector2 b, Vector2 ctr)
        {
            var e = b - a; var n = new Vector2(e.y, -e.x);
            float m = n.magnitude; if (m < 1e-6f) return Vector2.zero; n = n / m;
            if (Vector2.Dot(n, (a + b) * 0.5f - ctr) < 0f) n = -n;
            return n;
        }

        /// <summary>Box over a convex 4-corner footprint. top=false leaves the roof open.</summary>
        public void Box(Vector2[] q, float y0, float y1, Color wall, Color top, float ao = 0f, bool cap = true, Color? topTint = null)
        {
            var ctr = (q[0] + q[1] + q[2] + q[3]) * 0.25f;
            for (int i = 0; i < 4; i++)
            {
                Vector2 a = q[i], b = q[(i + 1) & 3];
                Wall(a, b, OutDir(a, b, ctr), y0, y1, wall, ao, topTint);
            }
            if (cap) Flat(q, y1, Shade(top, Vector3.up));
        }

        public void Box(ORect r, float y0, float y1, Color wall, Color top, float ao = 0f, bool cap = true) => Box(r.Quad(), y0, y1, wall, top, ao, cap);

        /// <summary>Small oriented box at p (half sizes along dir and across).</summary>
        public void BoxAt(Vector2 p, Vector2 dir, float hl, float hw, float y0, float y1, Color wall, Color top)
            => Box(new ORect(p, dir, hl, hw).Quad(), y0, y1, wall, top);

        /// <summary>Frustum: base quad at y0, top quad qt at y1 (rubble heaps, cabins of cars, hip roofs).</summary>
        public void Frustum(Vector2[] q, float y0, Vector2[] qt, float y1, Color side, Color top, bool cap = true)
        {
            var ctr = (q[0] + q[1] + q[2] + q[3]) * 0.25f;
            var cw = W(ctr, (y0 + y1) * 0.5f);
            for (int i = 0; i < 4; i++)
            {
                var A0 = W(q[i], y0); var B0 = W(q[(i + 1) & 3], y0); var B1 = W(qt[(i + 1) & 3], y1); var A1 = W(qt[i], y1);
                Face(A0, B0, B1, A1, side, (A0 + B1) * 0.5f - cw + Vector3.up * 0.001f);
            }
            if (cap) Flat(qt, y1, Shade(top, Vector3.up));
        }

        /// <summary>Pyramid / tent top: quad q at y0 to apex.</summary>
        public void Pyramid(Vector2[] q, float y0, Vector2 apex, float y1, Color col)
        {
            var ctr = (q[0] + q[1] + q[2] + q[3]) * 0.25f;
            var A = W(apex, y1);
            for (int i = 0; i < 4; i++)
            {
                var a = W(q[i], y0); var b = W(q[(i + 1) & 3], y0);
                Tri(a, b, A, col, (a + b) * 0.5f - W(ctr, y0) + Vector3.up * 0.3f);
            }
        }

        /// <summary>Gable roof over rect r (ridge along r.U), eaves at y0, ridge at y1, overhang ov.
        /// Returns nothing; gable-end triangles fill the walls under the roof.</summary>
        public void Gable(ORect r, float y0, float y1, Color roof, Color wall, float ov, int bands = 3)
        {
            // eave rectangle (overhang on all sides), ridge line along U through the centre
            float hu = r.Hu + ov, hv = r.Hv + ov;
            float drop = ov * (y1 - y0) / Mathf.Max(0.05f, r.Hv);   // keep the pitch through the overhang
            float ye = y0 - drop;
            Vector2 R0 = r.At(-hu, 0f), R1 = r.At(hu, 0f);
            for (int side = -1; side <= 1; side += 2)
            {
                Vector2 E0 = r.At(-hu, side * hv), E1 = r.At(hu, side * hv);
                var outward = new Vector3(r.V.x * side, 0.8f, -r.V.y * side);
                // shingle bands: alternating slight value steps toward the ridge
                for (int k = 0; k < bands; k++)
                {
                    float t0 = (float)k / bands, t1 = (float)(k + 1) / bands;
                    Vector2 a0 = Vector2.Lerp(E0, R0, t0), a1 = Vector2.Lerp(E1, R1, t0), b0 = Vector2.Lerp(E0, R0, t1), b1 = Vector2.Lerp(E1, R1, t1);
                    float h0 = Mathf.Lerp(ye, y1, t0), h1 = Mathf.Lerp(ye, y1, t1);
                    Color rc = Mul(roof, (k & 1) == 0 ? 0.97f : 1.03f);
                    Face(W(a0, h0), W(a1, h0), W(b1, h1), W(b0, h1), rc, outward);
                }
                // fascia strip under the eave edge
                Face(W(E0, ye - 0.035f), W(E1, ye - 0.035f), W(E1, ye), W(E0, ye), Mul(roof, 0.7f), new Vector3(r.V.x * side, 0f, -r.V.y * side));
            }
            // gable-end walls (triangles in the wall plane)
            for (int end = -1; end <= 1; end += 2)
            {
                Vector2 a = r.At(end * r.Hu, -r.Hv), b = r.At(end * r.Hu, r.Hv), m = r.At(end * r.Hu, 0f);
                var outward = new Vector3(r.U.x * end, 0f, -r.U.y * end);
                Tri(W(a, y0), W(b, y0), W(m, y1), wall, outward);
            }
        }

        /// <summary>Rough low-poly sphere with shared vertices (soft shading): foliage, bushes.</summary>
        public void Blob(Vector3 c, float rx, float ry, Color col, int salt, int seg = 7)
        {
            var m = M; int i0 = m.V.Count;
            // vertices: top, ring1 (+0.5), ring2 (-0.25), bottom
            float[] ry2 = { 0.55f, -0.3f };
            float[] rr = { 0.86f, 0.95f };
            AddBV(c + new Vector3(0, ry, 0), Vector3.up, col, salt, 0);
            for (int r = 0; r < 2; r++)
                for (int k = 0; k < seg; k++)
                {
                    float a = (k + (r == 1 ? 0.5f : 0f)) / seg * Mathf.PI * 2f + salt * 0.37f;
                    float jit = 0.88f + 0.24f * MapGen.Hf(salt * 7 + r, k);
                    var n = new Vector3(Mathf.Cos(a) * rr[r], ry2[r] * 1.6f, Mathf.Sin(a) * rr[r]).normalized;
                    var p = c + new Vector3(Mathf.Cos(a) * rx * rr[r] * jit, ry * ry2[r] * jit, Mathf.Sin(a) * rx * rr[r] * jit);
                    AddBV(p, n, col, salt, 1 + r * seg + k);
                }
            AddBV(c + new Vector3(0, -ry * 0.75f, 0), Vector3.down, col, salt, 99);
            int top = i0, r1 = i0 + 1, r2 = i0 + 1 + seg, bot = i0 + 1 + 2 * seg;
            for (int k = 0; k < seg; k++)
            {
                int k1 = (k + 1) % seg;
                m.T.Add(top); m.T.Add(r1 + k1); m.T.Add(r1 + k);
                m.T.Add(r1 + k); m.T.Add(r1 + k1); m.T.Add(r2 + k);
                m.T.Add(r1 + k1); m.T.Add(r2 + k1); m.T.Add(r2 + k);
                m.T.Add(r2 + k); m.T.Add(r2 + k1); m.T.Add(bot);
            }
        }

        void AddBV(Vector3 p, Vector3 n, Color col, int salt, int k)
        {
            float j = 0.86f + 0.26f * MapGen.Hf(salt * 13 + k, k * 7 + 3);
            var c = Shade(Mul(col, j), n);
            // underside darker (self-occlusion)
            if (n.y < 0f) c = Mul(c, 0.8f + 0.2f * (1f + n.y));
            M.V.Add(p); M.C.Add(C32(c));
        }

        /// <summary>N-sided cone with shared side vertices (conifers, flames, spires).</summary>
        public void Cone(Vector2 p, float rad, float y0, float y1, Color col, float rot, int seg = 6)
        {
            var m = M;
            var apex = W(p, y1);
            for (int k = 0; k < seg; k++)
            {
                float a0 = rot + k * Mathf.PI * 2f / seg, a1 = rot + (k + 1) * Mathf.PI * 2f / seg;
                var A = W(p + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * rad, y0);
                var B = W(p + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * rad, y0);
                Tri(A, B, apex, col, (A + B) * 0.5f - W(p, y0) + Vector3.up * (rad * 0.6f));
            }
        }

        /// <summary>N-sided prism (barrels, tanks, poles).</summary>
        public void Cyl(Vector2 p, float rad, float y0, float y1, Color side, Color top, int seg = 8)
        {
            var pts = new Vector2[seg];
            for (int k = 0; k < seg; k++) { float a = k * Mathf.PI * 2f / seg + 0.2f; pts[k] = p + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rad; }
            for (int k = 0; k < seg; k++)
            {
                var a = pts[k]; var b = pts[(k + 1) % seg];
                Wall(a, b, OutDir(a, b, p), y0, y1, side);
            }
            var tc = Shade(top, Vector3.up);
            for (int k = 1; k < seg - 1; k++) TriRaw(W(pts[0], y1), W(pts[k], y1), W(pts[k + 1], y1), tc);
        }

        /// <summary>Flat n-gon fan (glow pools, potholes), centre colour cc to rim colour rc.</summary>
        public void Disc(Vector2 p, float rad, float h, Color cc, Color rc, int seg = 8, float rot = 0f)
        {
            var m = M; int i0 = m.V.Count;
            m.V.Add(W(p, h)); m.C.Add(C32(cc));
            for (int k = 0; k < seg; k++)
            {
                float a = rot + k * Mathf.PI * 2f / seg;
                m.V.Add(W(p + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rad, h)); m.C.Add(C32(rc));
            }
            for (int k = 0; k < seg; k++) { m.T.Add(i0); m.T.Add(i0 + 1 + k); m.T.Add(i0 + 1 + (k + 1) % seg); }
        }

        /// <summary>Oriented 3D beam between two points (braces, rails, wires, poles).</summary>
        public void Beam(Vector3 a, Vector3 b, float w, Color col)
        {
            var d = b - a; float len = d.magnitude; if (len < 1e-5f) return;
            var f = d / len;
            var side = Vector3.Cross(f, Mathf.Abs(f.y) > 0.9f ? Vector3.right : Vector3.up).normalized * w;
            var up = Vector3.Cross(side.normalized, f).normalized * w;
            var p = new[] { a - side - up, a + side - up, a + side + up, a - side + up };
            var q = new[] { b - side - up, b + side - up, b + side + up, b - side + up };
            var c = (a + b) * 0.5f;
            for (int i = 0; i < 4; i++)
            {
                int j = (i + 1) & 3;
                Face(p[i], p[j], q[j], q[i], col, (p[i] + q[j]) * 0.5f - c);
            }
        }

        // ---------------- shadows (alpha-blended ground decals) ----------------
        readonly List<Vector2> _hullIn = new List<Vector2>(32);   // per Geo (not static): chunks are built on a worker thread too

        /// <summary>Ground shadow of a prism footprint pts raised to height h (convex hull of footprint and its sun projection).</summary>
        public void Shadow(IList<Vector2> pts, float h, float alpha)
        {
            if (S == null || h <= 0.02f) return;
            var off = new Vector2(-SUN.x / SUN.y * h, SUN.z / SUN.y * h);
            _hullIn.Clear();
            for (int i = 0; i < pts.Count; i++) { _hullIn.Add(pts[i]); _hullIn.Add(pts[i] + off); }
            var hull = Hull(_hullIn);
            if (hull.Count < 3) return;
            var col = new Color32(22, 24, 18, (byte)Mathf.Clamp(alpha * 255f, 0f, 255f));
            int i0 = S.V.Count;
            foreach (var p in hull) { S.V.Add(W(p, SHADOW_Y)); S.C.Add(col); }
            for (int k = 1; k < hull.Count - 1; k++) { S.T.Add(i0); S.T.Add(i0 + k); S.T.Add(i0 + k + 1); }
        }

        /// <summary>Soft round shadow (trees): octagon offset by the sun direction.</summary>
        public void ShadowBlob(Vector2 p, float rad, float h, float alpha)
        {
            if (S == null) return;
            var off = new Vector2(-SUN.x / SUN.y * h, SUN.z / SUN.y * h);
            var c = p + off;
            var col = new Color32(22, 24, 18, (byte)Mathf.Clamp(alpha * 255f, 0f, 255f));
            int i0 = S.V.Count;
            S.V.Add(W(c, SHADOW_Y)); S.C.Add(col);
            for (int k = 0; k < 8; k++)
            {
                float a = k * Mathf.PI / 4f;
                S.V.Add(W(c + new Vector2(Mathf.Cos(a) * rad * 1.1f, Mathf.Sin(a) * rad * 0.9f), SHADOW_Y)); S.C.Add(col);
            }
            for (int k = 0; k < 8; k++) { S.T.Add(i0); S.T.Add(i0 + 1 + k); S.T.Add(i0 + 1 + (k + 1) % 8); }
        }

        static List<Vector2> Hull(List<Vector2> p)
        {
            p.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            var h = new List<Vector2>(p.Count + 1);
            for (int pass = 0; pass < 2; pass++)
            {
                int start = h.Count;
                for (int ii = 0; ii < p.Count; ii++)
                {
                    var q = pass == 0 ? p[ii] : p[p.Count - 1 - ii];
                    while (h.Count >= start + 2 && Cross(h[h.Count - 2], h[h.Count - 1], q) <= 0f) h.RemoveAt(h.Count - 1);
                    h.Add(q);
                }
                h.RemoveAt(h.Count - 1);
            }
            return h;
        }

        static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
    }
}
