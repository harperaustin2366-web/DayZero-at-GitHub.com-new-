// v0.19 (batch B2, game feel). Visual only: reads the sim, never writes to it, no game RNG.
//   E8 camera shake: a short, decaying shake when a gate or fence of the viewed colony breaks, a tower is torn down,
//      or a big night-raid wave reaches the wall (Raid.AtWall). Strength falls off with the event's distance from
//      the camera focus. A broken gate also gets a ~0.6 s slow-motion beat: only the camera lerps and the Crowd
//      animation slow down (VisualTimeScale), the simulation step and Time.timeScale are untouched.
//      Both follow the "Screen shake" setting (GameSettings.ScreenShake, PlayerPrefs "set_shake").
//   E10 tap feedback: the selected building / camp / tile / group / squad / long-pressed unit gets a soft pulsing
//      glow ring on the ground, and an order given through Bootstrap.Act (send a squad, a rescue, a supply run,
//      move to another camp ...) draws a fading line along the route the sim just gave the people (their road
//      path, copied at order time), or a straight line when no path was set, for ~1.5 s.
// One small transparent mesh (one draw call) rebuilt only while something is showing.
// The pure part (shake / slow-mo curves, ring and path meshes) also compiles in the offline preview (DZ_PREVIEW).
using System;
using System.Collections.Generic;
using DayZero.Core;
using UnityEngine;
#if !DZ_PREVIEW
using UnityEngine.Rendering;
#endif

namespace DayZero.Runtime
{
    public static class FxFeel
    {
        // ---------------- tuning ----------------
        public const float SLOW_S = 0.6f, SLOW_MIN = 0.3f;
        public const float PATH_S = 1.5f, PATH_REVEAL_S = 0.35f;
        public static readonly Color GLOW = new Color(1.00f, 0.87f, 0.50f, 1f);
        public static readonly Color PATH = new Color(1.00f, 0.76f, 0.30f, 1f);

        // ---------------- pure curves ----------------
        /// <summary>Shake direction at t seconds into a shake of length dur (x, y in -1..1, decaying to 0).</summary>
        public static Vector2 ShakeAt(float t, float dur, int seed)
        {
            if (t < 0f || t >= dur) return Vector2.zero;
            float k = 1f - t / dur; k *= k;
            float s = seed * 1.618f;
            float x = Mathf.Sin(t * 47f + s) * 0.6f + Mathf.Sin(t * 83f + s * 2.1f) * 0.4f;
            float y = Mathf.Sin(t * 53f + s * 1.3f) * 0.6f + Mathf.Sin(t * 71f + s * 0.7f) * 0.4f;
            return new Vector2(x, y) * k;
        }

        /// <summary>Visual time multiplier t seconds after a gate break: dips to SLOW_MIN, holds, eases back by SLOW_S.</summary>
        public static float SlowMul(float t)
        {
            if (t < 0f || t >= SLOW_S) return 1f;
            if (t < 0.06f) return Mathf.Lerp(1f, SLOW_MIN, t / 0.06f);
            if (t < 0.36f) return SLOW_MIN;
            float u = (t - 0.36f) / (SLOW_S - 0.36f);
            return Mathf.Lerp(SLOW_MIN, 1f, u * u * (3f - 2f * u));
        }

        /// <summary>Pulse 0..1 (selection glow), period ~1.3 s.</summary>
        public static float Pulse(float now) { float s = Mathf.Sin(now * 4.8f); return s * s; }

        // ---------------- pure meshes (ground plane, y = 0; tile coords -> world (x, 0, -y)) ----------------
        static readonly List<Vector2> _loop = new List<Vector2>(128);
        static readonly List<Vector2> _nrm = new List<Vector2>(128);

        static Vector3 W(Vector2 p) => new Vector3(p.x, 0f, -p.y);
        static Color32 C(Color c, float a) => new Color(c.r, c.g, c.b, Mathf.Clamp01(a));

        /// <summary>Soft band around a closed loop: transparent at -wIn, colour (alpha a) on the loop, transparent at +wOut.</summary>
        static void Band(MeshData m, List<Vector2> loop, float wIn, float wOut, Color col, float a)
        {
            int n = loop.Count;
            if (n < 3) return;
            // outward normals (loop centroid decides the side)
            var ctr = Vector2.zero;
            for (int i = 0; i < n; i++) ctr += loop[i];
            ctr /= n;
            _nrm.Clear();
            for (int i = 0; i < n; i++)
            {
                var p0 = loop[(i + n - 1) % n]; var p1 = loop[i]; var p2 = loop[(i + 1) % n];
                var d = (p2 - p0); if (d.sqrMagnitude < 1e-8f) d = p2 - p1;
                var nr = new Vector2(d.y, -d.x).normalized;
                if (Vector2.Dot(nr, p1 - ctr) < 0f) nr = -nr;
                _nrm.Add(nr);
            }
            Color32 c0 = C(col, 0f), c1 = C(col, a);
            int b = m.V.Count;
            for (int i = 0; i < n; i++)
            {
                m.V.Add(W(loop[i] - _nrm[i] * wIn)); m.C.Add(c0);
                m.V.Add(W(loop[i])); m.C.Add(c1);
                m.V.Add(W(loop[i] + _nrm[i] * wOut)); m.C.Add(c0);
            }
            for (int i = 0; i < n; i++)
            {
                int i0 = b + i * 3, i1 = b + ((i + 1) % n) * 3;
                for (int r = 0; r < 2; r++)
                {
                    m.T.Add(i0 + r); m.T.Add(i1 + r); m.T.Add(i1 + r + 1);
                    m.T.Add(i0 + r); m.T.Add(i1 + r + 1); m.T.Add(i0 + r + 1);
                }
            }
        }

        /// <summary>Pulsing glow ring of radius r around c (tiles). pulse 0..1.</summary>
        public static void GlowRing(MeshData m, Vector2 c, float r, float pulse, Color col, float alpha = 1f)
        {
            float rr = r * (1f + 0.07f * pulse);
            int seg = Mathf.Clamp((int)(rr * 14f), 24, 72);
            _loop.Clear();
            for (int i = 0; i < seg; i++)
            {
                float a = i * Mathf.PI * 2f / seg;
                _loop.Add(c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rr);
            }
            float w = Mathf.Clamp(r * 0.22f, 0.16f, 0.7f);
            Band(m, _loop, w * (0.8f + 0.5f * pulse), w * (1.1f + 0.9f * pulse), col, alpha * (0.55f + 0.40f * pulse));
        }

        /// <summary>Pulsing glow outline around an oriented rectangle (building lot, camp yard), rounded corners.</summary>
        public static void GlowRect(MeshData m, ORect o, float pad, float pulse, Color col, float alpha = 1f)
        {
            float rad = pad + 0.12f * pulse;
            float hu = Mathf.Max(0.05f, o.Hu), hv = Mathf.Max(0.05f, o.Hv);
            _loop.Clear();
            const int CS = 6;
            for (int q = 0; q < 4; q++)
            {
                float su = q == 0 || q == 3 ? 1f : -1f, sv = q < 2 ? 1f : -1f;
                var cc = o.C + o.U * (su * hu) + o.V * (sv * hv);
                float a0 = q * Mathf.PI * 0.5f;
                for (int i = 0; i <= CS; i++)
                {
                    float a = a0 + i * (Mathf.PI * 0.5f) / CS;
                    _loop.Add(cc + (o.U * Mathf.Cos(a) + o.V * Mathf.Sin(a)) * rad);
                }
            }
            float w = Mathf.Clamp(Mathf.Min(hu, hv) * 0.18f, 0.18f, 0.6f);
            Band(m, _loop, w * (0.8f + 0.5f * pulse), w * (1.1f + 0.9f * pulse), col, alpha * (0.55f + 0.40f * pulse));
        }

        /// <summary>Straightens the stair steps of a grid path (Douglas-Peucker, 0.75 tile) and rounds the corners
        /// (two Chaikin passes); ends kept. In place.</summary>
        public static void Smooth(List<Vector2> p, int passes = 2)
        {
            if (p == null || p.Count < 3) return;
            var keep = new bool[p.Count];
            keep[0] = keep[p.Count - 1] = true;
            var stack = new Stack<(int, int)>();
            stack.Push((0, p.Count - 1));
            while (stack.Count > 0)
            {
                var (a, b) = stack.Pop();
                if (b - a < 2) continue;
                var d = p[b] - p[a]; float len = d.magnitude; int bi = -1; float bd = 0.75f;
                for (int i = a + 1; i < b; i++)
                {
                    var q = p[i] - p[a];
                    float dist = len < 1e-4f ? q.magnitude : Mathf.Abs(d.x * q.y - d.y * q.x) / len;
                    if (dist > bd) { bd = dist; bi = i; }
                }
                if (bi < 0) continue;
                keep[bi] = true; stack.Push((a, bi)); stack.Push((bi, b));
            }
            int n = 0;
            for (int i = 0; i < p.Count; i++) if (keep[i]) p[n++] = p[i];
            p.RemoveRange(n, p.Count - n);
            if (p.Count < 3) return;
            var tmp = new List<Vector2>(p.Count * 2);
            for (int k = 0; k < passes && p.Count < 1600; k++)
            {
                tmp.Clear();
                tmp.Add(p[0]);
                for (int i = 0; i + 1 < p.Count; i++)
                {
                    tmp.Add(Vector2.Lerp(p[i], p[i + 1], 0.25f));
                    tmp.Add(Vector2.Lerp(p[i], p[i + 1], 0.75f));
                }
                tmp.Add(p[p.Count - 1]);
                p.Clear(); p.AddRange(tmp);
            }
        }

        /// <summary>Order route line, age seconds after the order: draws on over PATH_REVEAL_S, then fades out by
        /// PATH_S; brighter dashes run toward the destination; a ring marks the end. width in tiles.</summary>
        public static void PathLine(MeshData m, List<Vector2> pts, float age, float width, Color col)
        {
            if (pts == null || pts.Count < 2 || age >= PATH_S) return;
            float fade = age < 0.12f ? age / 0.12f : age < 0.9f ? 1f : 1f - (age - 0.9f) / (PATH_S - 0.9f);
            fade = Mathf.Clamp01(fade);
            float total = 0f;
            for (int i = 1; i < pts.Count; i++) total += (pts[i] - pts[i - 1]).magnitude;
            if (total < 0.05f) return;
            float rv = Mathf.Clamp01(age / PATH_REVEAL_S); rv = 1f - (1f - rv) * (1f - rv);
            float shown = total * rv;
            // resample every ~0.5 tile (dashes + soft tail at the start)
            const float STEP = 0.5f;
            _loop.Clear();
            var dash = new List<float>(64);
            float run = 0f;
            _loop.Add(pts[0]); dash.Add(0f);
            for (int i = 1; i < pts.Count && run < shown; i++)
            {
                var a = pts[i - 1]; var b = pts[i];
                float len = (b - a).magnitude;
                if (len < 1e-4f) continue;
                int n = Mathf.Max(1, Mathf.CeilToInt(len / STEP));
                for (int k = 1; k <= n; k++)
                {
                    float s = run + len * k / n;
                    if (s > shown) { _loop.Add(Vector2.Lerp(a, b, (shown - run) / len)); dash.Add(shown); run = shown; break; }
                    _loop.Add(Vector2.Lerp(a, b, (float)k / n)); dash.Add(s);
                }
                if (run < shown) run += len;
            }
            int cnt = _loop.Count;
            if (cnt < 2) return;
            float hw = width * 0.5f;
            int b0 = m.V.Count;
            for (int i = 0; i < cnt; i++)
            {
                var d = _loop[Mathf.Min(cnt - 1, i + 1)] - _loop[Mathf.Max(0, i - 1)];
                var nr = d.sqrMagnitude < 1e-8f ? Vector2.up : new Vector2(-d.y, d.x).normalized;
                float s = dash[i];
                float ph = s * 0.9f - age * 3.2f; ph -= Mathf.Floor(ph);   // dashes run toward the end
                float br = ph < 0.5f ? 1f : 0.55f;
                float tail = Mathf.Clamp01(s / 1.2f);                  // soft start at the walker
                float a = fade * br * (0.35f + 0.65f * tail) * 0.85f;
                Color32 ce = C(col, 0f), cm = C(col, a);
                m.V.Add(W(_loop[i] - nr * hw)); m.C.Add(ce);
                m.V.Add(W(_loop[i] - nr * hw * 0.35f)); m.C.Add(cm);
                m.V.Add(W(_loop[i] + nr * hw * 0.35f)); m.C.Add(cm);
                m.V.Add(W(_loop[i] + nr * hw)); m.C.Add(ce);
            }
            for (int i = 0; i + 1 < cnt; i++)
            {
                int i0 = b0 + i * 4, i1 = b0 + (i + 1) * 4;
                for (int r = 0; r < 3; r++)
                {
                    m.T.Add(i0 + r); m.T.Add(i1 + r); m.T.Add(i1 + r + 1);
                    m.T.Add(i0 + r); m.T.Add(i1 + r + 1); m.T.Add(i0 + r + 1);
                }
            }
            // destination ring once the line has reached it
            if (rv >= 0.999f) GlowRing(m, pts[pts.Count - 1], Mathf.Max(0.55f, width * 2.2f), 1f - fade, col, fade);
        }

#if !DZ_PREVIEW
        // ================= runtime =================
        struct Shake { public float T0, Amp, Dur; public int Seed; }
        sealed class Route { public readonly List<Vector2> P = new List<Vector2>(); public float T0; }

        static readonly List<Shake> _shakes = new List<Shake>();
        static readonly List<Route> _routes = new List<Route>();
        static float _slowT0 = -100f;
        static int _seed;

        /// <summary>Multiplier for visual-only time (camera lerps, Crowd animation). 1 outside the gate-break beat.</summary>
        public static float VisualTimeScale => GameSettings.ScreenShake ? SlowMul(Time.unscaledTime - _slowT0) : 1f;

        /// <summary>Forget everything showing (new game / load).</summary>
        public static void Clear()
        {
            _shakes.Clear(); _routes.Clear(); _slowT0 = -100f; _pend = Ev.None; _waveKey = double.NaN;
            _towerKeys.Clear(); _towerCount = -1;
        }

        // ---------------- event intake (subscriptions on the Game; read-only) ----------------
        enum Ev { None, Gate, Fence, Tower }
        static Game _hooked;
        static Ev _pend; static int _pendFrame;
        static Vector3 _focus; static float _dist = 20f;
        static double _waveKey = double.NaN;
        static readonly HashSet<int> _towerKeys = new HashSet<int>();
        static readonly List<int> _tmpKeys = new List<int>();
        static int _towerCount = -1;

        static void HookGame(Game g)
        {
            if (ReferenceEquals(g, _hooked)) return;
            if (_hooked != null) { _hooked.LogAdded -= OnLog; _hooked.SfxRequested -= OnSfx; }
            _hooked = g;
            Clear();
            if (g != null) { g.LogAdded += OnLog; g.SfxRequested += OnSfx; }
        }

        static void OnLog(LogEntry e)
        {
            var g = _hooked;
            if (e == null || e.Text == null || g?.S == null) return;
            if (e.Col != Mathf.Max(0, g.ViewCol)) return;   // only the colony the player is looking at
            if (e.Kind != "loss") return;
            if (e.Text.StartsWith("Zombies broke through the gate", StringComparison.Ordinal)) { Flush(); _pend = Ev.Gate; _pendFrame = Time.frameCount; }
            else if (e.Text.StartsWith("Zombies broke through the fence", StringComparison.Ordinal)) { Flush(); _pend = Ev.Fence; _pendFrame = Time.frameCount; }
            else if (e.Text.StartsWith("A tower on ", StringComparison.Ordinal) && e.Text.EndsWith("was torn down by the dead.", StringComparison.Ordinal))
            {
                // the tower is already gone from S.Towers: the key missing from last frame's snapshot is where it stood
                Vector2? at = null;
                foreach (int k in _towerKeys) if (!g.S.Towers.ContainsKey(k)) { at = new Vector2(k % Data.Instance.W + 0.5f, k / Data.Instance.W + 0.5f); break; }
                Hit(Ev.Tower, at);
            }
        }

        static void OnSfx(string name, Vector2 at)
        {
            // the sim plays "alarm" at the broken tile right after the breach log line
            if (name == "alarm" && _pend != Ev.None) { var k = _pend; _pend = Ev.None; Hit(k, at + new Vector2(0.5f, 0.5f)); }
        }

        static void Flush() { if (_pend != Ev.None) { var k = _pend; _pend = Ev.None; Hit(k, null); } }

        /// <summary>0..1 by distance from the camera focus (no position: a muted 0.5).</summary>
        static float Near(Vector2? at)
        {
            if (!at.HasValue) return 0.5f;
            var f = new Vector2(_focus.x, -_focus.z);
            float d = (at.Value - f).magnitude, d0 = Mathf.Max(10f, _dist * 1.1f), d1 = Mathf.Max(30f, _dist * 2.8f);
            return d <= d0 ? 1f : d >= d1 ? 0f : 1f - (d - d0) / (d1 - d0);
        }

        static void Hit(Ev k, Vector2? at)
        {
            float near = Near(at);
            if (near <= 0.01f) return;
            switch (k)
            {
                case Ev.Gate: Kick(0.020f * near, 0.50f); if (near > 0.3f) _slowT0 = Time.unscaledTime; break;
                case Ev.Fence: Kick(0.011f * near, 0.32f); break;
                case Ev.Tower: Kick(0.016f * near, 0.45f); break;
            }
        }

        /// <summary>Start a shake. amp = fraction of the camera distance at the peak.</summary>
        public static void Kick(float amp, float dur)
        {
            if (_shakes.Count >= 6) _shakes.RemoveAt(0);
            _shakes.Add(new Shake { T0 = Time.unscaledTime, Amp = amp, Dur = dur, Seed = ++_seed });
        }

        // ---------------- order routes (Bootstrap.Act) ----------------
        static readonly Dictionary<int, List<Vector2>> _before = new Dictionary<int, List<Vector2>>();
        static int _orderCol = -1;

        static bool IsOrder(string a) => a == "send_squad" || a == "send_squad_scav" || a == "send_squad_map" || a == "squad_attack"
            || a == "send_rescue" || a == "send_raid" || a == "move_to_base" || a == "recall_squad";

        /// <summary>Before Game.DoAction: remembers everybody's current path list of the acting colony.</summary>
        public static bool BeforeOrder(Game g, string action)
        {
            _before.Clear(); _orderCol = -1;
            if (g?.S?.Cols == null || action == null || !IsOrder(action)) return false;
            int ci = Mathf.Max(0, g.CurCol);
            if (ci >= g.S.Cols.Count) return false;
            _orderCol = ci;
            foreach (var p in g.S.Cols[ci].People) if (p != null && p.St != "dead") _before[p.Id] = p.Path;
            return true;
        }

        /// <summary>After a successful order: copies each new path once (squads share one list), else a straight line.</summary>
        public static void AfterOrder(Game g, string action, object[] args, string err, bool armed)
        {
            if (!armed || !string.IsNullOrEmpty(err) || g?.S?.Cols == null || _orderCol < 0 || _orderCol >= g.S.Cols.Count) return;
            try
            {
                var seen = new List<List<Vector2>>();
                int added = 0;
                Vector2 from = Vector2.zero; int nFrom = 0;
                foreach (var p in g.S.Cols[_orderCol].People)
                {
                    if (p == null || p.St == "dead" || p.Path == null) continue;
                    if (_before.TryGetValue(p.Id, out var old) && ReferenceEquals(old, p.Path)) continue;
                    if (!_before.ContainsKey(p.Id)) continue;
                    from += p.Pos; nFrom++;
                    if (p.Path.Count < 2 || seen.Contains(p.Path) || added >= 6) continue;
                    seen.Add(p.Path);
                    var r = new Route { T0 = Time.unscaledTime };
                    r.P.Add(p.Pos);
                    for (int i = Mathf.Clamp(p.Pi + 1, 1, p.Path.Count - 1); i < p.Path.Count; i++) r.P.Add(p.Path[i] + new Vector2(0.5f, 0.5f));
                    if (r.P.Count > 400) r.P.RemoveRange(400, r.P.Count - 400);
                    Smooth(r.P);
                    AddRoute(r); added++;
                }
                if (added == 0 && args != null && args.Length >= 2 && args[1] is Vector2Int tgt)
                {
                    // squad orders with a map target but no road path yet: a straight line from the squad
                    var q = args[0] is int sid ? g.SquadById(sid) : null;
                    if (q != null) foreach (var p in g.SquadMembers(q)) { from += p.Pos; nFrom++; }
                    if (nFrom > 0)
                    {
                        var r = new Route { T0 = Time.unscaledTime };
                        r.P.Add(from / nFrom); r.P.Add(new Vector2(tgt.x + 0.5f, tgt.y + 0.5f));
                        AddRoute(r);
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning("[FxFeel] order line: " + e.Message); }
            finally { _before.Clear(); _orderCol = -1; }
        }

        static void AddRoute(Route r)
        {
            if (_routes.Count >= 8) _routes.RemoveAt(0);
            _routes.Add(r);
        }

        // ---------------- per frame ----------------
        /// <summary>Map not playing / not ready: hide the layer and let go of the game's events.</summary>
        public static void Hide()
        {
            HookGame(null);
            if (_go != null && _go.activeSelf) _go.SetActive(false);
        }

        static GameObject _go;
        static Mesh _mesh;
        static Material _mat;
        static readonly MeshData _md = new MeshData();
        static bool _empty = true;

        /// <summary>MapView.Update, right after the camera is placed for the frame: watches the sim, shakes the camera
        /// and rebuilds the glow / route mesh. show = map playing and ready.</summary>
        public static void Tick(MapView map, Camera cam, Game g, MapGen gen, Vector3 focus, float dist, bool show)
        {
            HookGame(show ? g : null);
            if (!show || g?.S == null)
            {
                if (_go != null && _go.activeSelf) _go.SetActive(false);
                return;
            }
            Flush();   // a breach whose alarm never came (should not happen): shake without a position
            _focus = focus; _dist = dist;
            float now = Time.unscaledTime;
            WatchTowers(g);
            WatchWave(g);

            // ---- E8 shake ----
            var off = Vector2.zero;
            for (int i = _shakes.Count - 1; i >= 0; i--)
            {
                var s = _shakes[i];
                float t = now - s.T0;
                if (t >= s.Dur) { _shakes.RemoveAt(i); continue; }
                off += ShakeAt(t, s.Dur, s.Seed) * s.Amp;
            }
            if (cam != null && GameSettings.ScreenShake && off.sqrMagnitude > 0f)
            {
                float cap = 0.03f;
                if (off.magnitude > cap) off = off.normalized * cap;
                var tr = cam.transform;
                tr.position += (tr.right * off.x + tr.up * off.y) * dist;
            }

            // ---- E10 glow + routes ----
            _md.V.Clear(); _md.C.Clear(); _md.T.Clear();
            try { Selection(map, g, gen, Pulse(now)); } catch (Exception e) { Debug.LogWarning("[FxFeel] glow: " + e.Message); }
            float pw = Mathf.Clamp(0.26f + dist * 0.006f, 0.26f, 1.2f);
            for (int i = _routes.Count - 1; i >= 0; i--)
            {
                float age = now - _routes[i].T0;
                if (age >= PATH_S) { _routes.RemoveAt(i); continue; }
                PathLine(_md, _routes[i].P, age, pw, PATH);
            }
            Upload(map.transform, dist);
        }

        static void WatchTowers(Game g)
        {
            var tw = g.S.Towers;
            if (tw.Count == _towerCount) return;
            _towerCount = tw.Count;
            _towerKeys.Clear();
            foreach (var kv in tw) _towerKeys.Add(kv.Key);
        }

        /// <summary>Big wave: the night raid on the viewed colony first has a crowd at the wall.</summary>
        static void WatchWave(Game g)
        {
            var ri = g.RaidInfo();
            if (ri == null || !ri.On || ri.Start == _waveKey) return;
            int need = Mathf.Max(8, (int)(ri.Spawned * 0.35f));
            if (ri.AtWall < need) return;
            _waveKey = ri.Start;
            var b = g.BaseAny(ri.Bid);
            Vector2? at = b != null ? new Vector2(b.Pos.x + 0.5f, b.Pos.y + 0.5f) : (Vector2?)null;
            float near = Near(at);
            if (near > 0.01f) Kick((ri.Major ? 0.016f : 0.011f) * near, 0.7f);
        }

        static void Selection(MapView map, Game g, MapGen gen, float pulse)
        {
            if (map == null) return;
            int W = Data.Instance.W;
            Vector2 Tile(int k) => new Vector2(k % W + 0.5f, k / W + 0.5f);
            if (map.BuildingLot >= 0 && gen != null && gen.LotBox(map.BuildingLot, out var lr, out _)) GlowRect(_md, lr, 0.45f, pulse, GLOW);
            if (map.SelectedBase >= 0 && gen != null)
            {
                var b = g.BaseAny(map.SelectedBase);
                if (b != null && gen.Layout(b, out var lay)) GlowRect(_md, lay.Yard, 0.5f, pulse, GLOW);
                else if (b != null) GlowRing(_md, new Vector2(b.Pos.x + 0.5f, b.Pos.y + 0.5f), 4f, pulse, GLOW);
            }
            if (map.SelectedCamp != "" && gen != null)
            {
                var c = g.CampById(map.SelectedCamp);
                if (c != null && c.Lot >= 0 && gen.LotBox(c.Lot, out var cr, out _)) GlowRect(_md, cr, 0.6f, pulse, GLOW);
            }
            if (map.SelectedTower >= 0) GlowRing(_md, Tile(map.SelectedTower), 0.95f, pulse, GLOW);
            if (map.SelectedFence >= 0) GlowRing(_md, Tile(map.SelectedFence), 0.8f, pulse, GLOW);
            if (map.SelectedTrap >= 0) GlowRing(_md, Tile(map.SelectedTrap), 0.8f, pulse, GLOW);
            if (map.SelectedGroup >= 0)
            {
                var gr = g.GroupById(map.SelectedGroup);
                if (gr != null) GlowRing(_md, new Vector2(gr.Pos.x + 0.5f, gr.Pos.y + 0.5f), 1.3f, pulse, GLOW);
            }
            if (map.SelectedWreck >= 0)
            {
                var w = g.WreckById(map.SelectedWreck);
                if (w != null) GlowRing(_md, new Vector2(w.Pos.x + 0.5f, w.Pos.y + 0.5f), 1.4f, pulse, GLOW);
            }
            // squad picking a target: a ring under each member that is outside
            if (map.ActiveTool != MapView.Tool.None && map.SquadId >= 0)
            {
                var q = g.SquadById(map.SquadId);
                if (q != null) foreach (var p in g.SquadMembers(q)) if (p.St != "dead" && !g.IsInside(p)) GlowRing(_md, p.Pos, 0.6f, pulse, GLOW);
            }
            // long-pressed unit / spot (E19 info card)
            var card = LongPressInfo.Current;
            if (card != null && Time.unscaledTime - LongPressInfo.ShownAt < LongPressInfo.SHOW_S) GlowRing(_md, card.At, 0.65f, pulse, GLOW);
        }

        static void Upload(Transform parent, float dist)
        {
            bool any = _md.V.Count > 0;
            if (!any)
            {
                if (!_empty && _mesh != null) { _mesh.Clear(); _empty = true; }
                if (_go != null && _go.activeSelf) _go.SetActive(false);
                return;
            }
            if (_go == null && !Create(parent)) return;
            if (!_go.activeSelf) _go.SetActive(true);
            _go.transform.localPosition = new Vector3(0f, 0.07f + dist * 0.0014f, 0f);   // above the territory wash
            _mesh.Clear();
            _mesh.indexFormat = _md.V.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            _mesh.SetVertices(_md.V);
            _mesh.SetColors(_md.C);
            _mesh.SetTriangles(_md.T, 0, false);
            _mesh.RecalculateBounds();
            _empty = false;
        }

        static bool Create(Transform parent)
        {
            if (_mat == null) _mat = MakeMaterial();
            if (_mat == null) return false;
            _go = new GameObject("FeelFx");
            _go.transform.SetParent(parent, false);
            _mesh = new Mesh { name = "FeelFx" };
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
            return true;
        }

        /// <summary>Transparent unlit vertex colour (same chain as FxTerritory), drawn just after it.</summary>
        static Material MakeMaterial()
        {
            var ic = Shader.Find("Hidden/Internal-Colored");
            if (ic != null && ic.isSupported)
            {
                var m = new Material(ic) { name = "FeelFx", renderQueue = 3001 };
                m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                m.SetInt("_Cull", (int)CullMode.Off);
                m.SetInt("_ZWrite", 0);
                m.SetInt("_ZTest", (int)CompareFunction.LessEqual);
                m.SetFloat("_ZBias", -1f);
                return m;
            }
            var sp = Shader.Find("Sprites/Default");
            if (sp != null && sp.isSupported) return new Material(sp) { name = "FeelFx (sprites)", renderQueue = 3001 };
            var ss = Resources.Load<Shader>("Shaders/DayZeroShadow");
            if (ss != null && ss.isSupported) return new Material(ss) { name = "FeelFx (decal)", renderQueue = 3001 };
            Debug.LogWarning("[FxFeel] no transparent vertex-colour shader; glow and route lines off");
            return null;
        }
#endif
    }
}
