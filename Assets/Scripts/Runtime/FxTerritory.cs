// v0.18 territory tint (Austin: "when you fence in a fob or out post the secured area turns the color of your faction so
// you can see borders of other colonies"). Every camp, FOB or outpost whose ground is enclosed by fences / gates / towers
// (the sim's own flood fill, Game.EnclBaseAt over Game.Defense.cs RebuildEnclosures) gets a translucent wash in its
// owner's colour, with a stronger line along the inside of the fence. Faction camps (S.Camps) are always walled
// compounds (MapGen.FactionLayoutOf yard + palisade), so their yard gets the faction colour (Game.FactionColor).
// Unfenced own / colony sites get only a faint dashed claim circle (BaseZoneR); faction camps also get a dashed ring
// in their colour, since their small compounds hide behind the neighbouring roofs. A broken ring un-tints on the next
// enclosure rebuild (EnclGen changes), a site changing owner recolours it.
// Visual only: reads the sim, never writes to it, no RNG. One combined mesh for all territories (one draw call),
// rebuilt only when its hash changes (checked every 0.4 s, no allocations on the check); a separate GameObject so it
// shows at every zoom level (not part of the LOD chunks). Toggle "Territory colors": PlayerPrefs "set_territory".
using System;
using System.Collections.Generic;
using DayZero.Core;
using UnityEngine;
#if !DZ_PREVIEW
using UnityEngine.Rendering;
#endif

namespace DayZero.Runtime
{
    public static class FxTerritory
    {
        /// <summary>Player's territory colour: a clear blue. The banner gold (FxColonyBanner BN_MINE) sits too close to
        /// the gold / yellow faction colours (#c9b26a, #e8c95a) to tell the borders apart at a glance.</summary>
        public static readonly Color PLAYER = new Color(0.17f, 0.47f, 1.0f, 1f);
        const float FILL_A = 0.20f, FILL_A_FAR = 0.30f, EDGE_A = 0.62f, EDGE_A_FAR = 0.78f, DASH_A = 0.38f;
        const float EDGE_W = 0.22f, EDGE_W_FAR = 0.55f;
        public const float FAR_DIST = 60f, FACTION_RING_R = 7f;

        static int[] _own = new int[0], _own2 = new int[0];
        static readonly Dictionary<int, Color> _ownCol = new Dictionary<int, Color>();
        static readonly Dictionary<int, RectInt> _ownBox = new Dictionary<int, RectInt>();
        static readonly Vector2[] _poly = new Vector2[8];
        static readonly List<int> _colKeys = new List<int>();

        /// <summary>Marching-squares case of the cell whose corners are the centres of tiles (x,y) (x+1,y) (x+1,y+1) (x,y+1).</summary>
        static int Cell(int o, int x, int y, int W, int H)
        {
            int Smp(int tx, int ty) => tx < 0 || ty < 0 || tx >= W || ty >= H ? 0 : (_own2[ty * W + tx] == o ? 1 : 0);
            return Smp(x, y) | Smp(x + 1, y) << 1 | Smp(x + 1, y + 1) << 2 | Smp(x, y + 1) << 3;
        }

        /// <summary>One partial cell: the inside polygon (corners + edge midpoints, fan) and the contour strip(s) offset inward.</summary>
        static void CellPoly(MeshData m, int x, int y, int m4, Color32 cf, Color32 ce, float ew)
        {
            float x0 = x + 0.5f, y0 = y + 0.5f, x1 = x + 1.5f, y1 = y + 1.5f, xm = x + 1f, ym = y + 1f;
            // corners 0 TL, 1 TR, 2 BR, 3 BL; edge e_i between corner i and i+1
            Vector2 C(int i) => i == 0 ? new Vector2(x0, y0) : i == 1 ? new Vector2(x1, y0) : i == 2 ? new Vector2(x1, y1) : new Vector2(x0, y1);
            Vector2 E(int i) => i == 0 ? new Vector2(xm, y0) : i == 1 ? new Vector2(x1, ym) : i == 2 ? new Vector2(xm, y1) : new Vector2(x0, ym);
            bool In(int i) => (m4 >> (i & 3) & 1) != 0;
            int n = 0; Vector2 ctr = Vector2.zero; int nin = 0;
            for (int i = 0; i < 4; i++)
            {
                if (In(i)) { _poly[n++] = C(i); ctr += C(i); nin++; }
                if (In(i) != In(i + 1)) _poly[n++] = E(i);
            }
            if (n < 3) return;
            int b0 = m.V.Count;
            for (int i = 0; i < n; i++) { m.V.Add(new Vector3(_poly[i].x, 0f, -_poly[i].y)); m.C.Add(cf); }
            for (int i = 1; i + 1 < n; i++) { m.T.Add(b0); m.T.Add(b0 + i); m.T.Add(b0 + i + 1); }
            // contour: around each outside corner (saddles: two pieces), else the two crossing edges
            if (m4 == 5 || m4 == 10)
            {
                for (int i = 0; i < 4; i++)
                    if (!In(i)) Strip(m, E((i + 3) & 3), E(i), C(i), ew, ce, true);
                return;
            }
            int ea = -1, eb = -1;
            for (int i = 0; i < 4; i++) if (In(i) != In(i + 1)) { if (ea < 0) ea = i; else eb = i; }
            if (eb < 0) return;
            Strip(m, E(ea), E(eb), ctr / Mathf.Max(1, nin), ew, ce, false);
        }

        /// <summary>Strip of width w on the inside of segment p-q; ref is a point inside (or, away = true, outside).</summary>
        static void Strip(MeshData m, Vector2 p, Vector2 q, Vector2 refPt, float w, Color32 c, bool away)
        {
            var d = q - p; float len = d.magnitude; if (len < 1e-4f) return;
            var nrm = new Vector2(-d.y, d.x) / len;
            float side = Vector2.Dot(refPt - p, nrm);
            if ((side < 0f) != away) nrm = -nrm;
            Quad(m, p, q, q + nrm * w, p + nrm * w, c);
        }

        /// <summary>The colour a colony's territory is drawn in: the player blue for the viewed colony, else its colony colour.</summary>
        public static Color ColonyColor(Game g, int ci)
        {
            int me = g.CurCol >= 0 ? g.CurCol : g.ViewCol;
            return ci == me ? PLAYER : g.ColColor(ci);
        }

        static Color32 C32(Color c, float a) =>
            new Color32((byte)(Mathf.Clamp01(c.r) * 255f + 0.5f), (byte)(Mathf.Clamp01(c.g) * 255f + 0.5f), (byte)(Mathf.Clamp01(c.b) * 255f + 0.5f), (byte)(Mathf.Clamp01(a) * 255f + 0.5f));

        static void Quad(MeshData m, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, Color32 c)
        {
            int i = m.V.Count;
            m.V.Add(new Vector3(p0.x, 0f, -p0.y)); m.V.Add(new Vector3(p1.x, 0f, -p1.y));
            m.V.Add(new Vector3(p2.x, 0f, -p2.y)); m.V.Add(new Vector3(p3.x, 0f, -p3.y));
            m.C.Add(c); m.C.Add(c); m.C.Add(c); m.C.Add(c);
            m.T.Add(i); m.T.Add(i + 1); m.T.Add(i + 2); m.T.Add(i); m.T.Add(i + 2); m.T.Add(i + 3);
        }

        static void Box(MeshData m, float x0, float y0, float x1, float y1, Color32 c) =>
            Quad(m, new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1), c);

        /// <summary>Signature of everything the tint depends on; no allocations.</summary>
        public static int Key(Game g, MapGen gen, bool far)
        {
            if (g?.S == null) return 0;
            unchecked
            {
                int h = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(g);
                h = h * 31 + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(g.S);
                h = h * 31 + (gen == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(gen));
                h = h * 31 + g.EnclGen; h = h * 31 + g.CurCol; h = h * 31 + g.ViewCol; h = h * 31 + (far ? 1 : 0);
                var cols = g.S.Cols;
                for (int ci = 0; ci < cols.Count; ci++)
                {
                    var c = cols[ci];
                    h = h * 31 + (c.Lost ? 1 : 0) + c.Merged * 3;
                    var bs = c.Bases;
                    for (int i = 0; i < bs.Count; i++)
                    {
                        var b = bs[i];
                        h = h * 31 + b.Id; h = h * 31 + b.Col; h = h * 31 + b.Pos.x * 4099 + b.Pos.y;
                        h = h * 31 + b.Lvl * 2 + (b.Done >= 1.0 ? 1 : 0);
                    }
                }
                var camps = g.S.Camps;
                for (int i = 0; i < camps.Count; i++)
                {
                    var c = camps[i];
                    h = h * 31 + (c.Alive ? 1 : 0); h = h * 31 + c.Lot; h = h * 31 + (c.Color ?? "").GetHashCode();
                }
                return h;
            }
        }

        /// <summary>Appends every territory to m (vertices at y = 0; the caller lifts the object just above ground).</summary>
        public static void Build(Game g, MapGen gen, MeshData m, bool far)
        {
            if (g?.S == null) return;
            var d = Data.Instance;
            int W = d.W, H = d.H, N = W * H;
            if (N <= 0) return;
            float fa = far ? FILL_A_FAR : FILL_A, ea = far ? EDGE_A_FAR : EDGE_A, ew = far ? EDGE_W_FAR : EDGE_W;

            // 1. owner grid from the sim's enclosure ownership (one entry per flood-filled region), with per-owner boxes
            if (_own.Length != N) { _own = new int[N]; _own2 = new int[N]; }
            Array.Clear(_own, 0, N);
            _ownCol.Clear(); _ownBox.Clear();
            for (int k = 0; k < N; k++)
            {
                var b = g.EnclBaseAt(k);
                if (b == null) continue;
                int o = b.Id + 1, x = k % W, y = k / W;
                _own[k] = o;
                if (_ownBox.TryGetValue(o, out var bx)) _ownBox[o] = new RectInt(Math.Min(bx.x, x), Math.Min(bx.y, y), Math.Max((bx.x + bx.width), x + 1) - Math.Min(bx.x, x), Math.Max((bx.y + bx.height), y + 1) - Math.Min(bx.y, y));
                else { _ownBox[o] = new RectInt(x, y, 1, 1); _ownCol[o] = ColonyColor(g, b.Col); }
            }
            {
                // grow each region by one tile into the barrier it stops at (fence / gate / tower / building wall / water),
                // so the edge runs along the middle of the fence line instead of a tile inside it
                Array.Copy(_own, _own2, N);
                var tl = d.Tiles; var S = g.S;
                _colKeys.Clear(); foreach (var kv in _ownBox) _colKeys.Add(kv.Key);
                foreach (int o in _colKeys)
                {
                    var bx = _ownBox[o];
                    int xa = Math.Max(0, bx.x - 1), xb = Math.Min(W - 1, (bx.x + bx.width)), ya = Math.Max(0, bx.y - 1), yb = Math.Min(H - 1, (bx.y + bx.height));
                    for (int y = ya; y <= yb; y++)
                        for (int x = xa; x <= xb; x++)
                        {
                            int k = y * W + x;
                            if (_own[k] != 0 || _own2[k] != 0) continue;
                            int t = tl[k];
                            if (t != Data.T_BLD && t != Data.T_WATER && !S.Fences.Has(k) && !S.Towers.Has(k) && !S.Gates.Has(k)) continue;
                            bool touch = false;
                            for (int dy = -1; dy <= 1 && !touch; dy++)
                                for (int dx = -1; dx <= 1; dx++)
                                {
                                    int nx = x + dx, ny = y + dy;
                                    if (nx < 0 || ny < 0 || nx >= W || ny >= H || _own[ny * W + nx] != o) continue;
                                    touch = true; break;
                                }
                            if (touch) _own2[k] = o;
                        }
                }
                // faction compounds (always walled, MapGen.FactionLayoutOf): their lot plus the palisade yard, grown half a
                // tile so the edge runs just outside the palisade (the building fills most of a compound)
                if (gen != null)
                    for (int ci = 0; ci < S.Camps.Count; ci++)
                    {
                        var c = S.Camps[ci];
                        if (!c.Alive || !gen.FactionLayoutOf(c, out var lay)) continue;
                        int o = -(ci + 1);
                        var yd = lay.Yard; var grown = new ORect(yd.C, yd.U, yd.Hu + 0.6f, yd.Hv + 0.6f);
                        float ext = (grown.Hu + grown.Hv) + 1f;
                        int xa = Mathf.FloorToInt(yd.C.x - ext), xb = Mathf.CeilToInt(yd.C.x + ext), ya = Mathf.FloorToInt(yd.C.y - ext), yb = Mathf.CeilToInt(yd.C.y + ext);
                        bool lotOk = c.Lot >= 0 && c.Lot < d.Lots.Count && d.LotId.Length == N;
                        if (lotOk) { var L = d.Lots[c.Lot]; xa = Math.Min(xa, L.X); ya = Math.Min(ya, L.Y); xb = Math.Max(xb, L.X + L.W); yb = Math.Max(yb, L.Y + L.H); }
                        xa = Math.Max(0, xa); ya = Math.Max(0, ya); xb = Math.Min(W - 1, xb); yb = Math.Min(H - 1, yb);
                        int bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = -1, by1 = -1;
                        for (int y = ya; y <= yb; y++)
                            for (int x = xa; x <= xb; x++)
                            {
                                int k = y * W + x;
                                if (_own2[k] != 0) continue;
                                if (!(lotOk && d.LotId[k] == c.Lot) && !grown.Contains(new Vector2(x + 0.5f, y + 0.5f))) continue;
                                _own2[k] = o;
                                bx0 = Math.Min(bx0, x); by0 = Math.Min(by0, y); bx1 = Math.Max(bx1, x); by1 = Math.Max(by1, y);
                            }
                        if (bx1 < 0) continue;
                        _ownBox[o] = new RectInt(bx0, by0, bx1 - bx0 + 1, by1 - by0 + 1);
                        _ownCol[o] = Game.FactionColor(c);
                    }
                // marching squares over tile centres: smooth 45-degree edges, fill + inner border strip per owner
                foreach (var kv in _ownBox)
                {
                    int o = kv.Key; var bx = kv.Value;
                    var cf = C32(_ownCol[o], fa); var ce = C32(_ownCol[o], ea);
                    int xa = bx.x - 2, xb = (bx.x + bx.width) + 1, ya = bx.y - 2, yb = (bx.y + bx.height) + 1;
                    for (int y = ya; y <= yb; y++)
                    {
                        int x = xa;
                        while (x <= xb)
                        {
                            int m4 = Cell(o, x, y, W, H);
                            if (m4 == 15)
                            {
                                int x0 = x;
                                while (x <= xb && Cell(o, x, y, W, H) == 15) x++;
                                Box(m, x0 + 0.5f, y + 0.5f, x + 0.5f, y + 1.5f, cf);
                                continue;
                            }
                            if (m4 != 0) CellPoly(m, x, y, m4, cf, ce, ew);
                            x++;
                        }
                    }
                }
            }

            // 2. unfenced own / colony sites: faint dashed claim circle
            var cols = g.S.Cols;
            for (int ci = 0; ci < cols.Count; ci++)
            {
                var col = cols[ci];
                if (col.Lost || col.Merged >= 0) continue;
                foreach (var b in col.Bases)
                {
                    if (b.Done < 1.0 || g.IsEnclosed(b)) continue;
                    DashedCircle(m, new Vector2(b.Pos.x + 0.5f, b.Pos.y + 0.5f), (float)g.BaseZoneR(b), far ? 0.4f : 0.14f, C32(ColonyColor(g, b.Col), DASH_A));
                }
            }
            // faction compounds sit wedged between houses, so their tinted yard hides behind roofs from most angles: a dashed
            // ring in the faction colour around each live camp (outpost zone radius) marks them at a glance
            foreach (var c in g.S.Camps)
                if (c.Alive && c.Lot >= 0) DashedCircle(m, new Vector2(c.Pos.x + 0.5f, c.Pos.y + 0.5f), FACTION_RING_R, far ? 0.45f : 0.16f, C32(Game.FactionColor(c), DASH_A + 0.12f));

        }

        static void DashedCircle(MeshData m, Vector2 c, float r, float w, Color32 col)
        {
            if (r <= 0.5f) return;
            int n = Mathf.Clamp((int)(r * 2.2f) * 2, 16, 64);
            for (int k = 0; k < n; k += 2)
            {
                float a0 = k * Mathf.PI * 2f / n, a1 = (k + 1) * Mathf.PI * 2f / n;
                var u0 = new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)); var u1 = new Vector2(Mathf.Cos(a1), Mathf.Sin(a1));
                Quad(m, c + u0 * (r - w * 0.5f), c + u1 * (r - w * 0.5f), c + u1 * (r + w * 0.5f), c + u0 * (r + w * 0.5f), col);
            }
        }

#if !DZ_PREVIEW
        // ---------------- settings toggle ----------------
        // MERGE: move into Runtime/GameSettings.cs (PlayerPrefs key "set_territory", default on) when it lands.
        public const string PREF = "set_territory";
        static int _on = -1;
        public static bool Enabled
        {
            get { if (_on < 0) _on = PlayerPrefs.GetInt(PREF, 1) != 0 ? 1 : 0; return _on == 1; }
            set { _on = value ? 1 : 0; PlayerPrefs.SetInt(PREF, _on); PlayerPrefs.Save(); }
        }

        // ---------------- runtime layer (MapView.Update calls Tick once a frame) ----------------
        static GameObject _go;
        static Mesh _mesh;
        static Material _mat;
        static readonly MeshData _md = new MeshData();
        static int _key = int.MinValue;
        static float _checkT;
        static float _lift = -1f;

        /// <summary>show = map playing and ready; hides the layer otherwise. Rebuilds only on change.</summary>
        public static void Tick(Transform parent, Game g, MapGen gen, float dist, bool show)
        {
            show = show && g?.S != null && Enabled;
            if (!show) { if (_go != null && _go.activeSelf) _go.SetActive(false); return; }
            if (_go == null && !Create(parent)) return;
            if (!_go.activeSelf) { _go.SetActive(true); _checkT = 0f; }
            bool far = dist > FAR_DIST;
            _checkT -= Time.unscaledDeltaTime;
            bool farChanged = _mesh != null && far != _lastFar;
            if (_checkT <= 0f || farChanged)
            {
                _checkT = 0.4f;
                int k = Key(g, gen, far);
                if (k != _key) { _key = k; _lastFar = far; Rebuild(g, gen, far); }
            }
            // just above the ground; a little higher when zoomed out so it never z-fights the coarse ground
            float lift = 0.055f + dist * 0.0012f;
            if (Mathf.Abs(lift - _lift) > 0.002f) { _lift = lift; _go.transform.localPosition = new Vector3(0f, lift, 0f); }
        }
        static bool _lastFar;

        /// <summary>Force a rebuild on the next tick (new game / load).</summary>
        public static void Invalidate() { _key = int.MinValue; _checkT = 0f; }

        static bool Create(Transform parent)
        {
            if (_mat == null) _mat = MakeMaterial();
            if (_mat == null) return false;
            _go = new GameObject("Territory");
            _go.transform.SetParent(parent, false);
            _mesh = new Mesh { name = "Territory" };
            _mesh.MarkDynamic();
            _go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var r = _go.AddComponent<MeshRenderer>();
            r.sharedMaterial = _mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            var mpb = new MaterialPropertyBlock();
            mpb.SetColor("_RendererColor", Color.white);   // Sprites/Default fallback multiplies by it
            r.SetPropertyBlock(mpb);
            _key = int.MinValue;
            return true;
        }

        /// <summary>Transparent unlit vertex colour, depth-tested, no depth write, drawn after the ground shadows.
        /// Hidden/Internal-Colored first (alpha blend + depth bias), then Sprites/Default, then the map's own decal shader.</summary>
        static Material MakeMaterial()
        {
            var ic = Shader.Find("Hidden/Internal-Colored");
            if (ic != null && ic.isSupported)
            {
                var m = new Material(ic) { name = "Territory", renderQueue = 3000 };
                m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                m.SetInt("_Cull", (int)CullMode.Off);
                m.SetInt("_ZWrite", 0);
                m.SetInt("_ZTest", (int)CompareFunction.LessEqual);
                m.SetFloat("_ZBias", -1f);
                return m;
            }
            var sp = Shader.Find("Sprites/Default");
            if (sp != null && sp.isSupported) return new Material(sp) { name = "Territory (sprites)", renderQueue = 3000 };
            var ss = Resources.Load<Shader>("Shaders/DayZeroShadow");
            if (ss != null && ss.isSupported) return new Material(ss) { name = "Territory (decal)", renderQueue = 3000 };
            Debug.LogWarning("[FxTerritory] no transparent vertex-colour shader; territory tint off");
            return null;
        }

        static void Rebuild(Game g, MapGen gen, bool far)
        {
            _md.V.Clear(); _md.C.Clear(); _md.T.Clear();
            Build(g, gen, _md, far);
            _mesh.Clear();
            _mesh.indexFormat = _md.V.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            _mesh.SetVertices(_md.V);
            _mesh.SetColors(_md.C);
            _mesh.SetTriangles(_md.T, 0, false);
            _mesh.RecalculateBounds();
        }
#endif
    }
}
