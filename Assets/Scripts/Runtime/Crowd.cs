// Crowd layer (v0.11.4a): survivors, zombies and waiting survivor groups drawn as procedural
// low-poly humanoids (CharGen), batched on the CPU into ONE dynamic vertex-coloured mesh per frame
// (plus one blob-shadow mesh), so the whole crowd costs two draw calls with the existing map
// material and needs no instancing shader variants. Pure C# (UnityEngine value types only), so the
// offline preview renders the exact same meshes.
//
// Everything follows the game simulation (Game.*.cs); nothing here changes it:
//   * survivors: Person.Pos (the sim moves them along its own A* paths, routines, guard posts),
//     St / Goal / Guard / Post / Carry / Wpn pick the animation and the outfit; people inside a
//     building, in a vehicle or travelling to another map are hidden
//   * zombies: Game.ZPos(i) for the zombies near the camera; mode Zh (0 wander, 1 horde, 3 night
//     raid) and proximity to a camp pick shamble / attack; a zombie that vanishes from the sim right
//     after a gunshot or melee hit nearby plays a fall and lies for a few seconds
//   * gunfire: Game.SfxRequested("gunshot"/"rifle"/"hit", pos) is the sim's shot event; the nearest
//     visible survivor aims, flashes and draws a tracer to the nearest zombie
//   * survivor groups waiting for rescue (S.Groups, St "wait") stand where the sim put them and wave
//   * v0.14.0a: people of the other colonies (S.Camps) live in their compounds (MapGen.Factions.cs): a few per
//     camp by population, wearing the camp colour, gate guards on the bigger ones; purely cosmetic
//   * v0.14.0a: vehicles: a survivor leading a vehicle (Person.Vlead / Vt, Game.Gear.cs) is drawn as that
//     vehicle, and a squad bringing a wreck home (Squad.Towing) drags it behind on a tow line
// Cosmetic only (the sim has no value for these): where exactly a survivor stands inside the yard
// (their sim position is clamped into the palisade and pushed out of the camp building), guards on
// duty are drawn just inside the palisade in the direction of their sim post (the first one on the
// watchtower), zombies that the sim lets into the yard are held at the palisade and drawn attacking
// it, tracer targets, and the corpse lying time.
using System;
using System.Collections.Generic;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed class Crowd
    {
        public const float MT = MapGen.MT;           // metres -> tiles
        const float GROUND_Y = 0.047f;

        /// <summary>Caps (phone budget): full-detail figures, distant 3-box figures.</summary>
        public int MaxFull = 120, MaxLow = 260;
        public float FullDist = 55f, LowDist = 125f;   // camera distance (MapView.Dist) limits

        public int LastFull, LastLow, LastVerts;

        sealed class Agent
        {
            public int Key, Salt;
            public bool Zombie, Seen, Manual, Tower;
            public Vector2 Pos, Tgt, Sim;
            public float Yaw, Speed, Phase, T, Height;
            public CharModel M, Low;
            public string Sig = "";
            public CharGen.Anim A;
            public float AimT, FlashT, TracerT, DeadT = -1f;
            public Vector2 AimAt; public Vector3 TracerTo;
            public float AttackYaw; public bool Attacking, AtWall;
            public int Mode;
            public float Dist2;
            public int Fac = -1, Leg; public float Wait; public bool Post;   // faction folk (v0.14.0a)
        }

        sealed class VehAgent
        {
            public Vector2 Pos, Dir = new Vector2(1f, 0f);
            public string Type = "pickup"; public int Salt; public bool Seen, Wreck, TowSeen;
            public bool HasTow; public Vector2 TowPos, TowDir = new Vector2(1f, 0f); public string TowType = "pickup"; public int TowSalt;
        }
        readonly Dictionary<int, VehAgent> _vehs = new Dictionary<int, VehAgent>();
        readonly Dictionary<int, MapGen.FactionLayout> _facLay = new Dictionary<int, MapGen.FactionLayout>();
        public int Vehicles => _vehs.Count;

        readonly MapGen _gen;
        readonly Dictionary<int, Agent> _surv = new Dictionary<int, Agent>();
        readonly List<Agent> _zom = new List<Agent>();
        readonly List<Agent> _dying = new List<Agent>();
        readonly List<Agent> _manual = new List<Agent>();
        readonly Dictionary<int, CharModel> _zModels = new Dictionary<int, CharModel>();
        readonly Dictionary<int, CharModel> _lowModels = new Dictionary<int, CharModel>();
        readonly List<(Vector2 at, float t, bool shot)> _events = new List<(Vector2, float, bool)>();
        readonly List<(Vector2 at, float t)> _recentHits = new List<(Vector2, float)>();
        readonly List<MapGen.CampLayout> _camps = new List<MapGen.CampLayout>();
        readonly List<Vector2> _breaches = new List<Vector2>();
        ODict<int, Fence> _fences;

        bool NearBreach(Vector2 p, float r)
        {
            for (int i = 0; i < _breaches.Count; i++) if ((_breaches[i] - p).sqrMagnitude < r * r) return true;
            return false;
        }
        readonly M34[] _bones = new M34[CharModel.BONES];
        float _time;
        int _nextZ = 1, _lastKills = -1;

        public Crowd(MapGen gen) { _gen = gen; }

        public void Clear() { _lastKills = -1; _vehs.Clear(); _facLay.Clear(); _surv.Clear(); _zom.Clear(); _dying.Clear(); _events.Clear(); _recentHits.Clear(); }

        /// <summary>Hook for Game.SfxRequested: shots and melee hits drive muzzle flashes, aiming and zombie falls.</summary>
        public void OnSfx(string name, Vector2 at)
        {
            bool shot = name == "gunshot" || name == "rifle";
            if (!shot && name != "hit") return;
            if (_events.Count < 48) _events.Add((at, _time, shot));
        }

        // ================= manual figures (preview line-ups) =================
        public void AddManual(CharModel m, Vector2 tilePos, float yaw, CharGen.Anim a, float phase = 0f, float t = 0f, float height = 0f)
        {
            _manual.Add(new Agent { Manual = true, M = m, Pos = tilePos, Tgt = tilePos, Yaw = yaw, A = a, Phase = phase, T = t, Height = height, Salt = _manual.Count * 7 + 3, Zombie = m.Zombie });
        }
        public void ClearManual() => _manual.Clear();

        // ================= sync with the simulation =================
        static float YawOf(Vector2 dTile) => Mathf.Atan2(dTile.x, -dTile.y);   // tile dir -> world yaw (forward = (sin, 0, cos))

        /// <summary>Advance animation and follow the sim. dt in seconds (0 while paused keeps everything frozen
        /// except idle breathing). focus = tile the camera looks at, camDist = camera distance in tiles.</summary>
        public void Update(Game g, float dt, Vector2 focus, float camDist)
        {
            _time += dt;
            for (int i = _recentHits.Count - 1; i >= 0; i--) if (_time - _recentHits[i].t > 1.6f) _recentHits.RemoveAt(i);
            if (g?.S == null) { _surv.Clear(); _zom.Clear(); return; }
            float R = Mathf.Clamp(camDist * 1.6f + 18f, 26f, 110f);
            if (camDist > LowDist) { _zom.Clear(); _dying.Clear(); }

            // camp layouts (yard, building, fire, tower) for every base
            _camps.Clear();
            foreach (var b in g.AllBases()) if (_gen.Layout(b, out var lay)) _camps.Add(lay);
            // v0.12.0a: breaches let the dead through the palisade; intact fence tiles of placed walls are attacked
            _breaches.Clear();
            if (g.S.Breaches != null) foreach (var kv in g.S.Breaches) if (!g.S.Fences.ContainsKey(kv.Key)) _breaches.Add(new Vector2(kv.Key % _gen.MapW + 0.5f, kv.Key / _gen.MapW + 0.5f));
            _fences = g.S.Fences;

            foreach (var a in _surv.Values) a.Seen = false;
            SyncSurvivors(g, dt, focus, R, camDist);
            SyncGroups(g, dt, focus, R);
            SyncFactionFolk(g, dt, focus, R);
            SyncVehicles(g, dt, focus, R);
            _drop.Clear();
            foreach (var kv in _surv) if (!kv.Value.Seen) _drop.Add(kv.Key);
            foreach (int k in _drop) _surv.Remove(k);
            if (camDist <= LowDist) SyncZombies(g, dt, focus, R);
            HandleEvents();
            for (int i = _dying.Count - 1; i >= 0; i--)
            {
                var a = _dying[i];
                a.DeadT += dt; a.T += dt;
                if (a.DeadT > 7f) _dying.RemoveAt(i);
            }
            foreach (var a in _manual) a.T += dt;
        }

        void Follow(Agent a, Vector2 tgt, float dt, float snap)
        {
            a.Tgt = tgt;
            var d = tgt - a.Pos;
            float dist = d.magnitude;
            if (dist > snap) { a.Pos = tgt; a.Speed = 0f; return; }
            float k = 1f - Mathf.Exp(-dt * 7f);
            var step = d * k;
            a.Pos += step;
            float sp = dt > 0f ? step.magnitude / dt : 0f;
            a.Speed = Mathf.Lerp(a.Speed, sp, 1f - Mathf.Exp(-dt * 6f));
            if (step.magnitude > 0.004f) a.Yaw = LerpAngle(a.Yaw, YawOf(d), 1f - Mathf.Exp(-dt * 9f));
        }

        static float LerpAngle(float a, float b, float t)
        {
            float d = b - a;
            while (d > Mathf.PI) d -= 2f * Mathf.PI;
            while (d < -Mathf.PI) d += 2f * Mathf.PI;
            return a + d * Mathf.Clamp01(t);
        }

        static bool LongGun(string w) => w == "rifle" || w == "hunting" || w == "shotgun";

        void SyncSurvivors(Game g, float dt, Vector2 focus, float R, float camDist)
        {
            foreach (var c in g.S.Cols)
            {
                if (c.Lost || c.Merged >= 0 || !c.Started) continue;
                // guards on duty per base, sorted by id: the first one of the main camp mans the watchtower
                foreach (var p in c.People)
                {
                    if (p.St == "dead" || p.Veh || p.Trav.HasValue || g.IsInside(p)) continue;
                    if ((p.Pos - focus).sqrMagnitude > R * R) continue;
                    int key = (c.Id << 20) | (p.Id & 0xFFFFF);
                    if (!_surv.TryGetValue(key, out var a)) { a = new Agent { Key = key, Salt = p.Id, Yaw = (p.Id % 8) * 0.785f }; _surv[key] = a; }
                    bool rifle = LongGun(p.Wpn);
                    bool carry = false;
                    foreach (var kv in p.Carry) if (kv.Value > 0.01) { carry = true; break; }
                    string sig = p.Job + (rifle ? "R" : "") + (carry ? "C" : "");
                    if (a.M == null || a.Sig != sig)
                    {
                        a.M = CharGen.Survivor(p.Job, p.Id, rifle, carry);
                        a.Low = CharGen.Low(SurvTop(p.Job, p.Id), Geo.Hex("#3a4a5a"), Geo.Hex("#a8785a"), false);
                        a.Sig = sig;
                    }
                    var b = g.BaseById(p.Base);
                    bool duty = p.St == "base" && p.Guard && p.Post.HasValue && g.OnDuty();
                    Vector2 tgt = p.Pos; float h = 0f; a.Tower = false;
                    MapGen.CampLayout lay = default; bool inCamp = false;
                    if (p.St == "base" && b != null && _gen.Layout(b, out lay))
                    {
                        inCamp = true;
                        if (duty) tgt = GuardSpot(g, c, p, b, lay, out h);
                        else
                        {
                            // several people often share one sim spot (home door, routine spot): fan them out a little
                            var jit = new Vector2(MapGen.Hf(p.Id, 61) - 0.5f, MapGen.Hf(p.Id, 62) - 0.5f) * 1.6f;
                            tgt = YardSpot(lay, MapIntoYard(lay, b, p.Pos) + jit, p.Id);
                        }
                    }
                    a.Height = h;
                    bool first = a.T == 0f;
                    Follow(a, tgt, dt, h > 0f || first ? 0.01f : 3.5f);
                    a.Seen = true;
                    a.T += dt;
                    // walk phase advances with distance travelled (stride ~1.5 m per half cycle), capped for fast-forward
                    float dphase = Mathf.Min(a.Speed * dt / (1.5f * MT) * Mathf.PI, dt * 3.5f * 2f * Mathf.PI);
                    a.Phase += dphase;
                    a.AimT -= dt; a.FlashT -= dt; a.TracerT -= dt;
                    bool moving = a.Speed > 0.12f;
                    if (a.AimT > 0f) { a.A = CharGen.Anim.Aim; a.Yaw = LerpAngle(a.Yaw, YawOf(a.AimAt - a.Pos), 1f - Mathf.Exp(-dt * 14f)); }
                    else if (moving) a.A = CharGen.Anim.Walk;
                    else if (p.St == "work")
                    {
                        switch (p.Goal)
                        {
                            case "build": case "repair": a.A = CharGen.Anim.Chop; break;
                            case "hunt": case "patrol": a.A = CharGen.Anim.Guard; break;
                            default: a.A = CharGen.Anim.Search; break;   // scavenging / gathering a building
                        }
                    }
                    else if (duty) { a.A = CharGen.Anim.Guard; if (inCamp) a.Yaw = LerpAngle(a.Yaw, YawOf(a.Pos - lay.Yard.C), 1f - Mathf.Exp(-dt * 4f)); }
                    else
                    {
                        var rt = g.RoutineState(p);
                        a.A = CharGen.Anim.Idle;
                        if (rt != null && rt.Code == "y" && rt.Arr)
                        {
                            // yard work (sim routine "y"): split between chopping/hammering, sorting supplies, standing watch by the fire
                            int w = p.Id % 3;
                            a.A = w == 0 ? CharGen.Anim.Chop : w == 1 ? CharGen.Anim.Search : CharGen.Anim.Idle;
                        }
                        if (inCamp && a.A == CharGen.Anim.Idle && (lay.Fire - a.Pos).sqrMagnitude < 4f)
                            a.Yaw = LerpAngle(a.Yaw, YawOf(lay.Fire - a.Pos), 1f - Mathf.Exp(-dt * 2f));   // gather round the fire
                    }
                }
            }
        }
        readonly List<int> _drop = new List<int>();

        static Color SurvTop(string job, int id)
        {
            switch (job)
            {
                case "Soldier": return Geo.Hex("#4f4838");
                case "Cop": return Geo.Hex("#3a4048");
                case "Medic": return Geo.Hex("#8a8678");
                case "Engineer": return Geo.Hex("#8a6a30");
                default: return MapGen.Hf(id, 3) < 0.5f ? Geo.Hex("#5a5a3a") : Geo.Hex("#6a4a30");
            }
        }

        /// <summary>The sim scatters people at a camp around its door (Game.MovePeople: +-spread/2 + wobble). Map that
        /// scatter proportionally onto the drawn yard, so the yard fills instead of everyone hugging the palisade.</summary>
        static Vector2 MapIntoYard(MapGen.CampLayout lay, Base b, Vector2 p)
        {
            // the sim spreads camp life over a few tiles around the camp (door scatter, routine yard spots Ws);
            // compress that radially onto the drawn yard so it fills instead of everyone hugging the palisade
            var y = lay.Yard;
            var d = p - y.C;
            float r = d.magnitude;
            if (r < 1e-3f) return y.C;
            float k = (float)Math.Tanh(r / 5.5f) / r;
            float du = Vector2.Dot(d, y.U) * k, dv = Vector2.Dot(d, y.V) * k;   // each in [-1, 1]
            return y.C + y.U * (du * (y.Hu - 0.35f)) + y.V * (dv * (y.Hv - 0.35f));
        }

        /// <summary>Sim position kept inside the palisade and outside the camp building (cosmetic).</summary>
        static Vector2 YardSpot(MapGen.CampLayout lay, Vector2 p, int id)
        {
            var y = lay.Yard;
            var d = p - y.C;
            float du = Mathf.Clamp(Vector2.Dot(d, y.U), -(y.Hu - 0.3f), y.Hu - 0.3f), dv = Mathf.Clamp(Vector2.Dot(d, y.V), -(y.Hv - 0.3f), y.Hv - 0.3f);
            var q = y.C + y.U * du + y.V * dv;
            if (lay.HasBld && lay.Bld.Contains(q, 0.14f))
            {
                // push out through the nearest building face
                var b = lay.Bld; var e = q - b.C;
                float eu = Vector2.Dot(e, b.U), ev = Vector2.Dot(e, b.V);
                float gu = b.Hu + 0.16f - Mathf.Abs(eu), gv = b.Hv + 0.16f - Mathf.Abs(ev);
                if (gu < gv) q += b.U * (Mathf.Sign(eu == 0f ? 1f : eu) * gu); else q += b.V * (Mathf.Sign(ev == 0f ? 1f : ev) * gv);
                // still outside the yard (building fills that side)? slide toward the fire
                var dq = q - y.C;
                if (Mathf.Abs(Vector2.Dot(dq, y.U)) > y.Hu - 0.25f || Mathf.Abs(Vector2.Dot(dq, y.V)) > y.Hv - 0.25f)
                {
                    float ang = (id % 11) / 11f * 6.283f;
                    q = lay.Fire + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * (0.55f + (id % 3) * 0.18f);
                }
            }
            var f = q - lay.Fire;
            if (f.sqrMagnitude < 0.36f * 0.36f) q = lay.Fire + (f.sqrMagnitude < 1e-4f ? new Vector2(0.4f, 0f) : f.normalized * 0.4f);
            return q;
        }

        /// <summary>Guard on duty: just inside the palisade toward the sim post (first guard of the main camp
        /// on the watchtower platform). Cosmetic placement of a sim value (Person.Post).</summary>
        Vector2 GuardSpot(Game g, Colony c, Person p, Base b, MapGen.CampLayout lay, out float height)
        {
            height = 0f;
            int rank = 0;
            foreach (var q in c.People)
                if (q != p && q.Base == p.Base && q.St == "base" && q.Guard && q.Post.HasValue && q.Id < p.Id && !g.IsInside(q)) rank++;
            if (rank == 0 && lay.Main) { height = lay.TowerTop; return lay.Tower; }
            var y = lay.Yard;
            var d = p.Post.Value - y.C;
            if (d.sqrMagnitude < 1e-4f) d = new Vector2(Mathf.Cos(p.Id), Mathf.Sin(p.Id));
            float du = Vector2.Dot(d, y.U), dv = Vector2.Dot(d, y.V);
            float m = Mathf.Max(Mathf.Abs(du) / y.Hu, Mathf.Abs(dv) / y.Hv);
            float k = (1f / Mathf.Max(1e-3f, m));
            var spot = y.C + (y.U * du + y.V * dv) * k;
            // stand on the firing step just inside the palisade (MapGen.FiringStep), clear of the gate gap
            Vector2 inward = Mathf.Abs(du) / y.Hu > Mathf.Abs(dv) / y.Hv ? -y.U * Mathf.Sign(du) : -y.V * Mathf.Sign(dv);
            var st = spot + inward * MapGen.STEP_IN;
            var gd = st - (lay.Gate + inward * MapGen.STEP_IN);
            if (gd.sqrMagnitude < 0.75f * 0.75f) st += new Vector2(-inward.y, inward.x) * (0.8f * (Vector2.Dot(gd, new Vector2(-inward.y, inward.x)) >= 0f ? 1f : -1f));
            height = MapGen.STEP_H;
            return st;
        }

        void SyncGroups(Game g, float dt, Vector2 focus, float R)
        {
            if (g.S.Groups == null) return;
            foreach (var gr in g.S.Groups)
            {
                if (gr.St != "wait") continue;
                var gp = new Vector2(gr.Pos.x + 0.5f, gr.Pos.y + 0.5f);
                if ((gp - focus).sqrMagnitude > R * R * 0.5f) continue;
                int n = 1;   // one survivor at the door calls for help; the rest of the group hides inside
                for (int k = 0; k < n; k++)
                {
                    int key = unchecked((int)0x40000000 | ((gr.Id & 0x3FFFF) << 3) | k);
                    if (!_surv.TryGetValue(key, out var a))
                    {
                        int seed = gr.Id * 31 + k * 7 + 500000;
                        string job = k == 0 ? "Civilian" : (MapGen.Hf(seed, 5) < 0.3f ? "Scavenger" : "Civilian");
                        float ang = k * 2.1f + gr.Id;
                        var pos = gp + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * (k == 0 ? 0f : 0.28f);
                        a = new Agent { Key = key, Salt = seed, M = CharGen.Survivor(job, seed, false, false), Low = CharGen.Low(SurvTop(job, seed), Geo.Hex("#3a4a5a"), Geo.Hex("#a8785a"), false), Pos = pos, Tgt = pos, Yaw = ang + 3.1f, Sig = "group" };
                        _surv[key] = a;
                    }
                    a.Seen = true; a.T += dt;
                    a.A = CharGen.Anim.Wave;   // calling for rescue (recruit loop: send a rescue / take them in)
                }
            }
        }

        // ================= other colonies (v0.14.0a) =================
        /// <summary>A few people of every faction camp near the camera: walking between huts, at the fire, guarding
        /// the gate. Cosmetic only; how many follows the camp's sim population (Camp.Pop).</summary>
        void SyncFactionFolk(Game g, float dt, Vector2 focus, float R)
        {
            if (g.S.Camps == null) return;
            foreach (var c in g.S.Camps)
            {
                if (!c.Alive || c.Lot < 0) continue;
                var cp = new Vector2(c.Pos.x + 0.5f, c.Pos.y + 0.5f);
                if ((cp - focus).sqrMagnitude > R * R) continue;
                if (!_facLay.TryGetValue(c.Lot, out var lay))
                {
                    if (!_gen.FactionLayoutOf(c, out lay)) continue;
                    _facLay[c.Lot] = lay;
                }
                var band = Game.FactionColor(c);
                int n = Mathf.Clamp(2 + c.Pop / 5, 2, 8);
                int guards = c.Pop >= 14 ? 2 : 0;
                var gs = new Vector2(-lay.GateOut.y, lay.GateOut.x);
                for (int k = 0; k < n; k++)
                {
                    int key = unchecked((int)0x50000000 | ((c.Lot & 0xFFFF) << 4) | k);
                    bool guard = k < guards;
                    if (!_surv.TryGetValue(key, out var a))
                    {
                        int seed = c.Lot * 41 + k * 13 + 900000;
                        string job = guard ? (c.Att == "Raiders" ? "Scavenger" : "Soldier") : (MapGen.Hf(seed, 5) < 0.3f ? "Scavenger" : MapGen.Hf(seed, 6) < 0.3f ? "Farmer" : "Civilian");
                        Vector2 pos = guard ? lay.Gate + lay.GateOut * 0.35f + gs * (k == 0 ? 0.85f : -0.85f) : FolkSpot(lay, seed, 0);
                        a = new Agent { Key = key, Salt = seed, Fac = c.Lot, Post = guard, M = CharGen.Survivor(job, seed, guard, false, band),
                            Low = CharGen.Low(band, Geo.Hex("#3a4a5a"), Geo.Hex("#a8785a"), false), Pos = pos, Tgt = pos, Yaw = YawOf(lay.GateOut), Sig = "fac" };
                        a.Wait = MapGen.Hf(seed, 9) * 4f;
                        _surv[key] = a;
                    }
                    a.Seen = true; a.T += dt;
                    if (a.Post) { a.A = CharGen.Anim.Guard; a.Speed = 0f; a.Yaw = LerpAngle(a.Yaw, YawOf(lay.GateOut), 1f - Mathf.Exp(-dt * 3f)); continue; }
                    var d = a.Tgt - a.Pos; float dist = d.magnitude;
                    if (dist > 0.04f)
                    {
                        float step = Mathf.Min(dist, 0.3f * dt);
                        a.Pos += d / dist * step;
                        a.Speed = dt > 0f ? step / dt : 0f;
                        a.Yaw = LerpAngle(a.Yaw, YawOf(d), 1f - Mathf.Exp(-dt * 8f));
                        a.Phase += Mathf.Min(a.Speed * dt / (1.5f * MT) * Mathf.PI, dt * 7f * Mathf.PI);
                        a.A = CharGen.Anim.Walk;
                    }
                    else
                    {
                        a.Speed = 0f;
                        a.Wait -= dt;
                        if (a.Wait <= 0f)
                        {
                            a.Leg++;
                            a.Tgt = FolkSpot(lay, a.Salt, a.Leg);
                            a.Wait = 3f + MapGen.Hf(a.Salt, a.Leg * 3 + 1) * 7f;
                        }
                        else
                        {
                            int act = (a.Salt + a.Leg) % 4;
                            a.A = act == 0 ? CharGen.Anim.Search : act == 1 ? CharGen.Anim.Chop : CharGen.Anim.Idle;
                            if ((lay.Fire - a.Pos).sqrMagnitude < 2.5f) { a.A = CharGen.Anim.Idle; a.Yaw = LerpAngle(a.Yaw, YawOf(lay.Fire - a.Pos), 1f - Mathf.Exp(-dt * 2f)); }
                        }
                    }
                }
            }
        }

        /// <summary>Somewhere to walk to inside a faction yard: the fire, a hut door, or open yard (not in the building).</summary>
        static Vector2 FolkSpot(MapGen.FactionLayout lay, int salt, int leg)
        {
            float r = MapGen.Hf(salt, leg * 7 + 2);
            if (r < 0.3f)
            {
                float ang = MapGen.Hf(salt, leg * 7 + 3) * 6.283f;
                return lay.Fire + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * 0.6f;
            }
            if (r < 0.6f && lay.Huts != null && lay.Huts.Count > 0)
            {
                var h = lay.Huts[(int)(MapGen.Hf(salt, leg * 7 + 4) * lay.Huts.Count) % lay.Huts.Count];
                var toC = lay.Yard.C - h; if (toC.sqrMagnitude > 1e-4f) toC = toC.normalized;
                return h + toC * 0.55f;
            }
            for (int t = 0; t < 8; t++)
            {
                var q = lay.Yard.P(MapGen.Hf(salt, leg * 7 + 5 + t * 11) * 1.6f - 0.8f, MapGen.Hf(salt, leg * 7 + 6 + t * 13) * 1.6f - 0.8f);
                if (lay.HasBld && lay.Bld.Contains(q, 0.25f)) continue;
                return q;
            }
            return lay.Fire + new Vector2(0.5f, 0.3f);
        }

        // ================= vehicles and tows (v0.14.0a) =================
        void SyncVehicles(Game g, float dt, Vector2 focus, float R)
        {
            foreach (var v in _vehs.Values) { v.Seen = false; v.TowSeen = false; }
            float R2 = R * R * 1.5f;
            foreach (var c in g.S.Cols)
            {
                if (c.Lost || c.Merged >= 0 || !c.Started) continue;
                foreach (var p in c.People)
                {
                    if (!p.Vlead || p.St == "dead" || p.Trav.HasValue || g.IsInside(p)) continue;
                    if ((p.Pos - focus).sqrMagnitude > R2) continue;
                    int key = (c.Id << 20) | (p.Id & 0xFFFFF);
                    var v = VehFollow(key, p.Pos, dt);
                    v.Type = p.Vt ?? "pickup"; v.Salt = (p.Vid ?? p.Id) * 97 + 11; v.Wreck = false;
                }
                foreach (var q in c.Squads)
                {
                    if (q.Towing == null) continue;
                    // the puller: the squad's vehicle lead, else the first member out walking (a light wreck dragged by hand)
                    Person lead = null; bool driving = false;
                    foreach (var p in c.People)
                    {
                        if (p.Sq != q.Id || p.St == "dead" || p.Trav.HasValue || g.IsInside(p)) continue;
                        if (p.Vlead) { lead = p; driving = true; break; }
                        if (lead == null && !p.Veh) lead = p;
                    }
                    if (lead == null || (lead.Pos - focus).sqrMagnitude > R2) continue;
                    VehAgent v;
                    if (driving && _vehs.TryGetValue((c.Id << 20) | (lead.Id & 0xFFFFF), out var dv)) v = dv;
                    else
                    {
                        // on foot: an invisible puller at the lead walker (no vehicle mesh), the wreck behind
                        v = VehFollow(unchecked((int)0x60000000 | (c.Id << 16) | (q.Id & 0xFFFF)), lead.Pos, dt);
                        v.Type = "none";
                    }
                    var tw = q.Towing;
                    v.TowSeen = true;
                    if (!v.HasTow) { v.HasTow = true; v.TowPos = v.Pos - v.Dir * 2f; v.TowDir = v.Dir; }
                    v.TowType = tw.Type; v.TowSalt = tw.Id * 97 + 11;
                    float L = (driving ? MapGen.VehicleSize(v.Type).x : 0.2f) + MapGen.VehicleSize(tw.Type).x + 0.3f;
                    var d = v.Pos - v.TowPos; float dl = d.magnitude;
                    if (dl > L * 3f) { v.TowPos = v.Pos - v.Dir * L; v.TowDir = v.Dir; }
                    else if (dl > L && dl > 1e-4f) { v.TowPos = v.Pos - d / dl * L; v.TowDir = d / dl; }
                }
            }
            _vdrop.Clear();
            foreach (var kv in _vehs) { if (!kv.Value.Seen) _vdrop.Add(kv.Key); else if (!kv.Value.TowSeen) kv.Value.HasTow = false; }
            foreach (int k in _vdrop) _vehs.Remove(k);
        }
        readonly List<int> _vdrop = new List<int>();

        VehAgent VehFollow(int key, Vector2 at, float dt)
        {
            if (!_vehs.TryGetValue(key, out var v)) { v = new VehAgent { Pos = at }; _vehs[key] = v; }
            v.Seen = true;
            var d = at - v.Pos; float dist = d.magnitude;
            if (dist > 3.5f) { v.Pos = at; return v; }
            var step = d * (1f - Mathf.Exp(-dt * 7f));
            v.Pos += step;
            if (step.magnitude > 0.003f) v.Dir = Vector2.Lerp(v.Dir, d / Mathf.Max(1e-4f, dist), 1f - Mathf.Exp(-dt * 6f)).normalized;
            return v;
        }

        void SyncZombies(Game g, float dt, Vector2 focus, float R)
        {
            // candidates: live sim zombies near the camera, nearest first, capped
            _cand.Clear();
            int n = g.Zc.Count;
            float R2 = R * R;
            for (int i = 0; i < n; i++)
            {
                if (g.Zh[i] == 255) continue;
                var zp = g.ZPos(i);
                float d2 = (zp - focus).sqrMagnitude;
                if (d2 > R2) continue;
                _cand.Add((i, zp, d2));
            }
            int cap = MaxFull + MaxLow;
            if (_cand.Count > cap) { _cand.Sort((x, y) => x.d2.CompareTo(y.d2)); _cand.RemoveRange(cap, _cand.Count - cap); }

            // match candidates to existing visual zombies (spatial hash, nearest within 1.5 tiles)
            _grid.Clear();
            for (int k = 0; k < _zom.Count; k++)
            {
                _zom[k].Seen = false;
                long cell = Cell(_zom[k].Sim);
                if (!_grid.TryGetValue(cell, out var lst)) { lst = new List<int>(4); _grid[cell] = lst; }
                lst.Add(k);
            }
            _newZ.Clear();
            foreach (var cd in _cand)
            {
                int best = -1; float bd = 1.5f * 1.5f;
                int cx = Mathf.FloorToInt(cd.p.x), cy = Mathf.FloorToInt(cd.p.y);
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        if (_grid.TryGetValue(((long)(cx + dx) << 32) ^ (uint)(cy + dy), out var lst))
                            foreach (int k in lst)
                            {
                                var a = _zom[k];
                                if (a.Seen) continue;
                                float dd = (a.Sim - cd.p).sqrMagnitude;
                                if (dd < bd) { bd = dd; best = k; }
                            }
                Agent z;
                if (best >= 0) z = _zom[best];
                else
                {
                    int id = _nextZ++;
                    int variant = (int)(MapGen.Hf(id, 77) * 8f) % 8;
                    z = new Agent { Key = id, Salt = id, Zombie = true, Pos = cd.p, Tgt = cd.p, Yaw = MapGen.Hf(id, 5) * 6.28f, M = ZModel(variant, id), Low = ZLow(id) };
                    z.Phase = MapGen.Hf(id, 9) * 6.28f;
                    _newZ.Add(z);
                }
                z.Seen = true;
                z.Sim = cd.p;
                z.Mode = g.Zh[cd.i];
                ZombieStep(z, cd.p, dt);
            }
            // vanished from the sim: fell (only as many as the sim's kill counters rose, and only near a recent
            // shot / hit) or simply despawned / culled
            int kills = 0;
            foreach (var c in g.S.Cols) kills += c.Stat("kills");
            int falls = _lastKills < 0 ? 0 : Mathf.Max(0, kills - _lastKills);
            _lastKills = kills;
            for (int k = _zom.Count - 1; k >= 0; k--)
            {
                var a = _zom[k];
                if (a.Seen) continue;
                if (falls > 0 && (a.Pos - focus).sqrMagnitude < R2 * 0.85f && HitNear(a.Pos) && _dying.Count < 40)
                {
                    a.DeadT = 0f; a.A = CharGen.Anim.Dead; a.T = 0f;
                    _dying.Add(a); falls--;
                }
                _zom.RemoveAt(k);
            }
            _zom.AddRange(_newZ);
        }

        readonly List<(int i, Vector2 p, float d2)> _cand = new List<(int, Vector2, float)>();
        readonly Dictionary<long, List<int>> _grid = new Dictionary<long, List<int>>();
        readonly List<Agent> _newZ = new List<Agent>();
        static long Cell(Vector2 p) => ((long)Mathf.FloorToInt(p.x) << 32) ^ (uint)Mathf.FloorToInt(p.y);

        bool HitNear(Vector2 p)
        {
            foreach (var h in _recentHits) if ((h.at - p).sqrMagnitude < 12f * 12f) return true;
            return false;
        }

        CharModel ZModel(int variant, int id)
        {
            int key = variant * 4 + (id & 3);
            if (!_zModels.TryGetValue(key, out var m)) { m = CharGen.Zombie(variant, key * 13 + 1); _zModels[key] = m; }
            return m;
        }

        CharModel ZLow(int id)
        {
            int key = id & 3;
            if (!_lowModels.TryGetValue(key, out var m))
            {
                m = CharGen.Low(Geo.Mul(Geo.Desat(key % 2 == 0 ? Geo.Hex("#5a5a3a") : Geo.Hex("#4a3a3a"), 0.6f), 0.75f), Geo.Hex("#3a3a30"), Geo.Hex("#7a8068"), true);
                _lowModels[key] = m;
            }
            return m;
        }

        void ZombieStep(Agent z, Vector2 simPos, float dt)
        {
            var tgt = simPos;
            z.Attacking = false; z.AtWall = false;
            // hold zombies at the palisade: anything inside (or touching) a yard is drawn at the wall, attacking it
            foreach (var lay in _camps)
            {
                var y = lay.Yard;
                var d = tgt - y.C;
                float du = Vector2.Dot(d, y.U), dv = Vector2.Dot(d, y.V);
                float pad = 0.32f;
                if (Mathf.Abs(du) < y.Hu + pad && Mathf.Abs(dv) < y.Hv + pad && !NearBreach(tgt, 1.3f))
                {
                    float gu = y.Hu + pad - Mathf.Abs(du), gv = y.Hv + pad - Mathf.Abs(dv);
                    Vector2 inward;
                    if (gu < gv) { tgt += y.U * (Mathf.Sign(du == 0f ? 1f : du) * gu); inward = -y.U * Mathf.Sign(du == 0f ? 1f : du); }
                    else { tgt += y.V * (Mathf.Sign(dv == 0f ? 1f : dv) * gv); inward = -y.V * Mathf.Sign(dv == 0f ? 1f : dv); }
                    // spread along the wall so they don't stack
                    var along = new Vector2(-inward.y, inward.x);
                    tgt += along * ((MapGen.Hf(z.Key, 31) - 0.5f) * 1.6f) - inward * (MapGen.Hf(z.Key, 32) * 0.35f);
                    z.Attacking = true; z.AtWall = true; z.AttackYaw = YawOf(inward);
                    break;
                }
            }
            // v0.12.0a: right against a placed wall's tile: claw at it
            if (!z.Attacking && _fences != null)
            {
                int tx = Mathf.FloorToInt(tgt.x), ty = Mathf.FloorToInt(tgt.y);
                foreach (var dd in Game.DIRS)
                {
                    int k = (ty + dd.y) * _gen.MapW + tx + dd.x;
                    if (k < 0 || !_fences.ContainsKey(k)) continue;
                    var fc = new Vector2(tx + dd.x + 0.5f, ty + dd.y + 0.5f);
                    if ((fc - tgt).sqrMagnitude > 1.0f) continue;
                    z.Attacking = true; z.AtWall = true; z.AttackYaw = YawOf(fc - tgt);
                    break;
                }
            }
            // in the open: a zombie right next to a survivor attacks them (the sim's encounter check)
            if (!z.Attacking)
                foreach (var s in _surv.Values)
                    if (s.Height <= 0f && (s.Pos - tgt).sqrMagnitude < 0.45f * 0.45f) { z.Attacking = true; z.AttackYaw = YawOf(s.Pos - tgt); break; }
            Follow(z, tgt, dt, z.T == 0f ? 0.001f : 3f);
            z.T += dt;
            if (z.Attacking)
            {
                z.A = CharGen.Anim.Attack;
                z.Yaw = LerpAngle(z.Yaw, z.AttackYaw, 1f - Mathf.Exp(-dt * 6f));
            }
            else
            {
                // shamble phase from distance (short ~0.6 m steps), at least a slow lurch while drifting
                float dp = z.Speed * dt / (0.6f * MT) * Mathf.PI;
                z.Phase += Mathf.Min(dp, dt * 2.6f * 2f * Mathf.PI);
                z.A = z.Speed > 0.03f || z.Mode != 0 ? CharGen.Anim.Shamble : CharGen.Anim.Idle;
                if (z.A == CharGen.Anim.Shamble && z.Speed <= 0.03f) z.Phase += dt * 2.2f;
            }
        }

        void HandleEvents()
        {
            foreach (var ev in _events)
            {
                _recentHits.Add((ev.at, _time));
                if (!ev.shot) continue;
                // shooter: nearest visible survivor to the sim's shot position
                Agent sh = null; float bd = 1.6f * 1.6f;
                foreach (var a in _surv.Values)
                {
                    float d = (a.Pos - ev.at).sqrMagnitude;
                    if (d < bd) { bd = d; sh = a; }
                }
                if (sh == null)
                {
                    // tower / unseen shooter: take the guard nearest the event within a camp
                    foreach (var a in _surv.Values) if (a.Height > 0f && (a.Pos - ev.at).sqrMagnitude < 9f * 9f) { sh = a; break; }
                }
                if (sh == null) continue;
                Agent tz = null; float zd = 10f * 10f;
                foreach (var z in _zom)
                {
                    float d = (z.Pos - sh.Pos).sqrMagnitude;
                    if (d < zd) { zd = d; tz = z; }
                }
                sh.AimT = 0.9f; sh.FlashT = 0.07f;
                if (tz != null)
                {
                    sh.AimAt = tz.Pos; sh.TracerT = 0.06f;
                    sh.TracerTo = Geo.W(tz.Pos, GROUND_Y + 1.1f * MT * (tz.M != null && tz.M.Hunch > 0 ? 0.9f : 1f));
                }
                else sh.AimAt = sh.Pos + new Vector2(Mathf.Sin(sh.Yaw), -Mathf.Cos(sh.Yaw));
            }
            _events.Clear();
        }

        // ================= mesh batching =================
        /// <summary>Write the whole crowd into m (vertex-coloured, lighting baked from world normals) and blob
        /// shadows into shadow. camPos = camera world position (tracers face it); camDist = camera distance.</summary>
        public void Build(MeshData m, MeshData shadow, Vector3 camPos, float camDist)
        {
            m.V.Clear(); m.C.Clear(); m.T.Clear();
            if (shadow != null) { shadow.V.Clear(); shadow.C.Clear(); shadow.T.Clear(); }
            var sg = new Geo(null, shadow);
            _draw.Clear();
            foreach (var a in _manual) _draw.Add(a);
            if (camDist <= LowDist)
            {
                foreach (var a in _surv.Values) _draw.Add(a);
                foreach (var a in _zom) _draw.Add(a);
                foreach (var a in _dying) _draw.Add(a);
            }
            foreach (var a in _draw) { var w = Geo.W(a.Pos, 0f) - camPos; a.Dist2 = w.x * w.x + w.y * w.y + w.z * w.z; }
            _draw.Sort((x, y) => x.Manual != y.Manual ? (x.Manual ? -1 : 1) : x.Dist2.CompareTo(y.Dist2));
            int full = 0, low = 0;
            float fullR = Mathf.Max(30f, camDist * 2.2f);
            foreach (var a in _draw)
            {
                bool useFull = a.Manual || (camDist <= FullDist && full < MaxFull && a.Dist2 < fullR * fullR);
                if (!useFull && (camDist > LowDist || low >= MaxLow || a.Low == null)) continue;
                var root = M34.Scaled(M34.TRS(Geo.W(a.Pos, GROUND_Y + a.Height + Lift(a.Pos) - SinkOf(a)), 0f, a.Yaw), MT);
                if (useFull)
                {
                    full++;
                    var pose = CharGen.Animate(a.M, a.A, a.T, a.Phase, a.Salt);
                    if (a.DeadT >= 0f) pose.Lie = Mathf.Clamp01(a.DeadT / 0.55f);
                    CharGen.Bones(a.M, pose, root, _bones);
                    for (int b = 0; b < CharModel.BONES; b++) Emit(m, a.M.Parts[b], _bones[b]);
                    if (a.FlashT > 0f && !a.Zombie) MuzzleFx(m, a, camPos);
                }
                else
                {
                    low++;
                    float bob = a.A == CharGen.Anim.Walk || a.A == CharGen.Anim.Shamble ? Mathf.Abs(Mathf.Sin(a.Phase)) * 0.03f : 0f;
                    var lr = root.Mul(M34.TRS(new Vector3(0f, a.Low.LegLen + bob, 0f), a.Zombie ? 0.25f : 0f));
                    if (a.DeadT >= 0f) lr = root.Mul(M34.TRS(new Vector3(0f, 0.15f, 0f), -1.5f));
                    Emit(m, a.Low.Parts[0], lr);
                }
                if (shadow != null && a.Height <= 0f) sg.ShadowBlob(a.Pos, a.DeadT >= 0f ? 0.2f : 0.13f, 0.1f, a.DeadT >= 0f ? 0.25f : 0.32f);
            }
            if (camDist <= LowDist * 1.4f)
            {
                var vg = new Geo(m, shadow);
                foreach (var v in _vehs.Values)
                {
                    bool puller = v.Type != "none";
                    if (puller) MapGen.Vehicle(vg, v.Pos, v.Dir, v.Type, v.Salt, v.Wreck ? MapGen.VehLook.Towed : MapGen.VehLook.Driving, 0.02f + Lift(v.Pos));
                    if (!v.HasTow) continue;
                    MapGen.Vehicle(vg, v.TowPos, v.TowDir, v.TowType, v.TowSalt, MapGen.VehLook.Towed, 0.02f + Lift(v.TowPos));
                    // tow line: from the rear of the puller to the nose of the wreck
                    var a0 = puller ? v.Pos - v.Dir * MapGen.VehicleSize(v.Type).x : v.Pos; var a1 = v.TowPos + v.TowDir * MapGen.VehicleSize(v.TowType).x;
                    vg.Beam(Geo.W(a0, 0.16f), Geo.W(a1, 0.16f), 0.012f, Geo.Hex("#2a2a26"));
                }
            }
            LastFull = full; LastLow = low; LastVerts = m.V.Count;
        }
        readonly List<Agent> _draw = new List<Agent>(512);

        float Lift(Vector2 p) => _gen != null ? _gen.DeckLift(p) : 0f;   // standing on a raised bridge deck (MapGen.Bridges.cs)
        static float SinkOf(Agent a) => a.DeadT > 5.5f ? (a.DeadT - 5.5f) * 0.12f : 0f;

        static void Emit(MeshData m, CharPart part, M34 x)
        {
            int nq = part.Quads;
            if (nq == 0) return;
            float inv = 1f / MT;
            for (int q = 0; q < nq; q++)
            {
                var n = x.R(part.N[q]) * inv;
                Color32 col = part.Glow[q] ? (Color32)part.C[q] : (Color32)Geo.Shade(part.C[q], n);
                int i = m.V.Count;
                int pi = q * 4;
                m.V.Add(x.P(part.P[pi])); m.V.Add(x.P(part.P[pi + 1])); m.V.Add(x.P(part.P[pi + 2])); m.V.Add(x.P(part.P[pi + 3]));
                m.C.Add(col); m.C.Add(col); m.C.Add(col); m.C.Add(col);
                m.T.Add(i); m.T.Add(i + 1); m.T.Add(i + 2); m.T.Add(i); m.T.Add(i + 2); m.T.Add(i + 3);
            }
        }

        static readonly Color32 FLASH_CORE = new Color32(255, 243, 170, 255), FLASH_RIM = new Color32(250, 190, 60, 255), TRACER = new Color32(255, 214, 120, 255);

        void MuzzleFx(MeshData m, Agent a, Vector3 camPos)
        {
            Vector3 muzzle = a.M.Rifle ? _bones[11].P(new Vector3(0f, 0.035f, 0.62f)) : _bones[6].P(new Vector3(0f, -0.36f, 0.12f));
            var toCam = (camPos - muzzle).normalized;
            float s = 0.09f;
            // star: two crossed camera-facing quads
            var r1 = Vector3.Cross(toCam, Vector3.up).normalized; var u1 = Vector3.Cross(r1, toCam).normalized;
            for (int k = 0; k < 2; k++)
            {
                var r = k == 0 ? (r1 + u1).normalized : (r1 - u1).normalized; var u = Vector3.Cross(r, toCam).normalized;
                int i = m.V.Count;
                m.V.Add(muzzle - r * s * 1.6f); m.V.Add(muzzle + u * s * 0.6f); m.V.Add(muzzle + r * s * 1.6f); m.V.Add(muzzle - u * s * 0.6f);
                m.C.Add(FLASH_RIM); m.C.Add(FLASH_CORE); m.C.Add(FLASH_RIM); m.C.Add(FLASH_CORE);
                m.T.Add(i); m.T.Add(i + 1); m.T.Add(i + 2); m.T.Add(i); m.T.Add(i + 2); m.T.Add(i + 3);
            }
            if (a.TracerT > 0f)
            {
                var d = a.TracerTo - muzzle; float len = d.magnitude;
                if (len > 0.05f)
                {
                    var f = d / len;
                    var side = Vector3.Cross(f, toCam).normalized * 0.012f;
                    var b0 = muzzle + f * Mathf.Min(0.2f, len * 0.2f);
                    int i = m.V.Count;
                    m.V.Add(b0 - side); m.V.Add(b0 + side); m.V.Add(a.TracerTo + side); m.V.Add(a.TracerTo - side);
                    m.C.Add(TRACER); m.C.Add(TRACER); m.C.Add(FLASH_CORE); m.C.Add(FLASH_CORE);
                    m.T.Add(i); m.T.Add(i + 1); m.T.Add(i + 2); m.T.Add(i); m.T.Add(i + 2); m.T.Add(i + 3);
                }
            }
        }

        // ================= stats (preview / debug overlay) =================
        public int Survivors => _surv.Count;
        public int Zombies => _zom.Count;
        public int Dying => _dying.Count;
        /// <summary>Centre of the zombies attacking a wall (falls back to p).</summary>
        public Vector2 AttackCentre(Vector2 p)
        {
            Agent best = null; int bn = 0;
            foreach (var z in _zom)
            {
                if (!z.AtWall) continue;
                int n = 0; foreach (var o in _zom) if (o.AtWall && (o.Pos - z.Pos).sqrMagnitude < 9f) n++;
                if (n > bn) { bn = n; best = z; }
            }
            if (best == null) return p;
            Vector2 c = Vector2.zero; int k = 0;
            foreach (var o in _zom) if (o.AtWall && (o.Pos - best.Pos).sqrMagnitude < 9f) { c += o.Pos; k++; }
            return c / k;
        }
        public int ZombiesNear(Vector2 p, float r) { int n = 0; foreach (var z in _zom) if ((z.Pos - p).sqrMagnitude < r * r) n++; return n; }
        public bool AnyFlash { get { foreach (var a in _surv.Values) if (a.FlashT > 0f) return true; return false; } }
        public int AtWall { get { int n = 0; foreach (var z in _zom) if (z.AtWall) n++; return n; } }
        public int Attacking { get { int n = 0; foreach (var z in _zom) if (z.Attacking) n++; return n; } }
    }
}
