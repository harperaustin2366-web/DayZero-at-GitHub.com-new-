// game.gd: infection spread, path weights, safe zones, active sectors, zombie spawning /
// movement / buckets, the horde flow field, cleared-sector control and path finding.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DayZero.Core
{
    public sealed partial class Game
    {
        // ---------------- infection spread ----------------
        void Spread()
        {
            float[] z = S.Zinf;
            float[] nz = (float[])z.Clone();
            double k = DIFF_SPREAD[S.Difficulty];
            byte[] ctrl = S.Ctrl;
            bool coarse = (int)Gd.Round(S.T) % 6 == 0;
            for (int sy = 0; sy < SH; sy++)
                for (int sx = 0; sx < SW; sx++)
                {
                    int i = sy * SW + sx;
                    if (SectLand[i] < 0.05f || ctrl[i] > 0) continue;
                    double zf = 1.0;
                    if (SectZone[i] != 0 && SectZone[i] < 3 && ZoneFrozen(SectZone[i]))
                    {
                        if (!coarse) continue;
                        zf = 6.0;
                    }
                    double v = z[i];
                    double wf = SectWild[i] > 0.5f ? WILD_SPREAD : 1.0;
                    double dmax = SectZone[i] != 0 ? SectDens[i] : 1.0;
                    if (v > 0.0)
                        nz[i] = (float)Math.Min(dmax, nz[i] + (0.05 * k * v * (1.0 - v) + 0.003 * k) * wf * dmax * zf);
                    if (v > 0.4)
                    {
                        foreach (var d in DIRS)
                        {
                            int qx = sx + d.x, qy = sy + d.y;
                            if (qx < 0 || qy < 0 || qx >= SW || qy >= SH) continue;
                            int j = qy * SW + qx;
                            if (SectLand[j] < 0.05f || ctrl[j] > 0) continue;
                            if (SectZone[j] != SectZone[i]) continue;
                            nz[j] = (float)Math.Min(SectZone[j] != 0 ? SectDens[j] : 1.0, nz[j] + 0.0135 * k * v * (0.4 + SectLand[j]) * (SectWild[j] > 0.5f ? WILD_SPREAD : 1.0) * zf);
                        }
                    }
                }
            S.Zinf = nz;
            if (S.Phase == "spread" && S.T >= S.NextJumpT)
            {
                S.NextJumpT = S.T + JUMP_HOURS / k * Rng.RandfRange(0.7, 1.3);
                for (int t = 0; t < 60; t++)
                {
                    int i = Rng.RandiRange(0, SW * SH - 1);
                    if (SectLand[i] < 0.3f || nz[i] > 0.05f || SectProt[i] == 1 || SectWild[i] > 0.1f) continue;
                    var c = new Vector2((i % SW) * SECT + SECT / 2, (i / SW) * SECT + SECT / 2);
                    bool near = false;
                    foreach (var b in AllBases())
                        if (V.F(b.Pos).DistTo(c) < 60.0) near = true;
                    if (near) continue;
                    nz[i] = 0.3f;
                    break;
                }
            }
        }

        void ResetWeights() => Astar.FillWeightRegion(new RectInt(0, 0, D.W, D.H), 1f);

        void UpdateWeights()
        {
            for (int i = 0; i < SW * SH; i++)
            {
                float v = S.Zinf[i];
                if (Math.Abs(v - _wInf[i]) < 0.05f) continue;
                _wInf[i] = v;
                double w = 1.0 + 30.0 * v * v;
                _pathCache.Clear(); _pathOrder.Clear();
                int sx = i % SW, sy = i / SW;
                Astar.FillWeightRegion(new RectInt(sx * SECT, sy * SECT, Math.Min(SECT, D.W - sx * SECT), Math.Min(SECT, D.H - sy * SECT)), (float)w);
            }
        }

        // ---------------- safe zones / territory ----------------
        void RebuildSafe()
        {
            if (S == null) return;
            RefreshRefs();
            if (_enclVer != S.FenceVer || _encl.Count != AllBases().Count) RebuildEnclosures();
            else
                foreach (var b in AllBases())
                    if (!_encl.ContainsKey(b.Id)) { RebuildEnclosures(); break; }
            Array.Clear(Safe, 0, Safe.Length);
            if (_territory.Length != D.W * D.H) _territory = new byte[D.W * D.H];
            Array.Clear(_territory, 0, _territory.Length);
            if (SectProt.Length != SW * SH) SectProt = new byte[SW * SH];
            Array.Clear(SectProt, 0, SectProt.Length);
            foreach (var b in AllBases())
            {
                var c = b.Pos;
                double zr = BaseZoneR(b);
                int r = (int)Math.Ceiling(zr);
                byte own = (byte)(b.Col + 1);
                for (int dy = -r; dy < r + 1; dy++)
                    for (int dx = -r; dx < r + 1; dx++)
                    {
                        var q = c.Add(dx, dy);
                        if (D.InMap(q) && V.Len(dx, dy) <= zr)
                        {
                            Safe[Ti(q)] = 1;
                            _territory[Ti(q)] = own;
                        }
                    }
                for (int dy = -r - 4; dy < r + 5; dy += 2)
                    for (int dx = -r - 4; dx < r + 5; dx += 2)
                    {
                        var q = c.Add(dx, dy);
                        if (D.InMap(q) && V.Len(dx, dy) <= zr + 3.0) SectProt[SectOf(q)] = 1;
                    }
                if (_encl.TryGetValue(b.Id, out var e) && e != null)
                {
                    foreach (int k in e) { Safe[k] = 1; _territory[k] = own; }
                    if (_enclSects.TryGetValue(b.Id, out var ss)) foreach (int si in ss) SectProt[si] = 1;
                }
            }
            if (_claimVer != S.FenceVer || _claim.Length != D.W * D.H) RebuildClaims();
            foreach (var kv in _claimTiles)
                foreach (int k in kv.Value)
                {
                    Safe[k] = 1;
                    if (_territory[k] == 0) _territory[k] = (byte)(kv.Key + 1);
                    SectProt[SectOf(Tp(k))] = 1;
                }
            if (_reachVer != _enclVer) RebuildReach();
            if (S.Ctrl != null)
                for (int i = 0; i < SW * SH; i++)
                    if (S.Ctrl[i] > 0) SectProt[i] = 1;
            _flowDirty = true;
        }

        // ---------------- active sectors ----------------
        void UpdateActive()
        {
            if (Active.Length != SW * SH) Active = new byte[SW * SH];
            Array.Clear(Active, 0, Active.Length);
            foreach (var c in S.Cols)
            {
                if (!ColAlive(c)) continue;
                foreach (var b in c.Bases) MarkActive(V.F(b.Pos), ACTIVE_R);
                foreach (var p in c.People)
                    if (p.St != "base" && p.St != "dead") MarkActive(p.Pos, 18.0);
            }
        }

        void MarkActive(Vector2 c, double r)
        {
            int sr = (int)Math.Ceiling(r / SECT);
            var cs = new Vector2Int((int)c.x / SECT, (int)c.y / SECT);
            for (int dy = -sr; dy < sr + 1; dy++)
            {
                int y = cs.y + dy;
                if (y < 0 || y >= SH) continue;
                for (int dx = -sr; dx < sr + 1; dx++)
                {
                    int x = cs.x + dx;
                    if (x < 0 || x >= SW) continue;
                    if (V.Len(dx, dy) <= sr + 0.5) Active[y * SW + x] = 1;
                }
            }
        }

        public bool IsActiveSect(int i) => i >= 0 && i < Active.Length && Active[i] == 1;

        void SpawnDotsHourly()
        {
            if (S.Phase == "pick") return;
            if (Active.Length != SW * SH) UpdateActive();
            for (int i = 0; i < Zc.Count; i++)
                if (Zh[i] == 0 && Active[SectOf(Tp(Zc[i]))] == 0) KillZ(i, false);
            FlushDead();
            var counts = new int[SW * SH];
            foreach (int k in Zc) counts[SectOf(Tp(k))]++;
            double zmul = CustomRules.Z(S);   // E14 custom game (1.0 = original)
            int budget = (zmul == 1.0 ? MaxZombies : (int)Math.Round(MaxZombies * zmul)) - Zc.Count;
            if (budget <= 0) return;
            double perSector = 4.2 * MaxZombies / 1100.0;
            if (zmul != 1.0) perSector *= zmul;
            var order = Enumerable.Range(0, SW * SH).ToList();
            GRng.Shuffle(order);
            foreach (int i in order)
            {
                if (budget <= 0) break;
                if (Active[i] == 0) continue;
                int want = (int)((double)S.Zinf[i] * SectLand[i] * perSector * 1.6 * (SectWild[i] > 0.5f && SectZone[i] == 0 ? WILD_Z : 1.0) * (SectZone[i] != 0 ? SectDens[i] : 1.0));
                int have = counts[i];
                if (have >= want) continue;
                int n = Math.Min(Math.Min(want - have, 3), budget);
                int sx = i % SW, sy = i / SW;
                int tries = n * 6;
                for (int t = 0; t < tries; t++)
                {
                    if (n <= 0) break;
                    var q = new Vector2Int(Rng.RandiRange(sx * SECT, Math.Min(D.W - 1, sx * SECT + SECT - 1)), Rng.RandiRange(sy * SECT, Math.Min(D.H - 1, sy * SECT + SECT - 1)));
                    if (!D.Walkable(q) || Safe[Ti(q)] == 1 || Blocked(Ti(q))) continue;
                    if (!SpawnZombie(Ti(q), 0)) continue;
                    n--; budget--;
                }
            }
        }

        // ---------------- zombies ----------------
        void SpawnZ(int k, int mode)
        {
            Zc.Add(k); Zn.Add(k); Zp.Add(Rng.Randf()); Zh.Add((byte)mode);
            if (SpawnProbeOn) SpawnProbe.Add((k, mode, S.T, ProtectedNow(k)));
        }

        public bool SpawnProbeOn;
        public List<(int k, int mode, double t, bool prot)> SpawnProbe = new List<(int, int, double, bool)>();

        bool ProtectedNow(int k)
        {
            if ((k < Safe.Length && Safe[k] == 1) || (k < _enclOwner.Length && _enclOwner[k] != 0)) return true;
            foreach (var b in AllBases())
                if (V.F(b.Pos).DistTo(V.F(Tp(k))) <= BaseZoneR(b)) return true;
            return false;
        }

        public bool SpawnOk(int k)
        {
            if (k < 0 || k >= D.W * D.H) return false;
            var q = Tp(k);
            if (!D.Walkable(q) || Blocked(k)) return false;
            if (k < Safe.Length && Safe[k] == 1) return false;
            if (k < _territory.Length && _territory[k] != 0) return false;
            if (k < _enclOwner.Length && _enclOwner[k] != 0) return false;
            foreach (var b in AllBases())
                if (V.F(b.Pos).DistTo(V.F(q)) <= BaseZoneR(b) + 1.0) return false;
            return true;
        }

        public bool SpawnZombie(int k, int mode)
        {
            if (!SpawnOk(k)) return false;
            SpawnZ(k, mode);
            return true;
        }

        public void KillZ(int i, bool byPlayer = true)
        {
            if (i < 0 || i >= Zc.Count || Zh[i] == 255) return;
            int k = Zc[i];
            Zh[i] = 255;
            _deadZ.Add(i);
            if (byPlayer)
            {
                C.AddStat("kills", 1);
                int s = SectOf(Tp(k));
                S.Zinf[s] = (float)Math.Max(0.0, S.Zinf[s] - 0.0025);
            }
        }

        readonly List<int> _deadZ = new List<int>();

        void FlushDead()
        {
            if (_deadZ.Count == 0) return;
            _deadZ.Sort();
            int prev = -1;
            for (int j = _deadZ.Count - 1; j >= 0; j--)
            {
                int i = _deadZ[j];
                if (i == prev) continue;
                prev = i;
                int last = Zc.Count - 1;
                if (i > last) continue;
                if (i != last) { Zc[i] = Zc[last]; Zn[i] = Zn[last]; Zp[i] = Zp[last]; Zh[i] = Zh[last]; }
                Zc.RemoveAt(last); Zn.RemoveAt(last); Zp.RemoveAt(last); Zh.RemoveAt(last);
            }
            _deadZ.Clear();
        }

        public Vector2 ZPos(int i)
        {
            if (IsClient)
            {
                if (i >= _czTo.Count) return new Vector2(-100, -100);
                return _czFrom[i].LerpTo(_czTo[i], (float)V.Clamp(_czT / _czDur, 0.0, 1.0));
            }
            var a = Tp(Zc[i]); var b = Tp(Zn[i]);
            long h = ((long)i * 2654435761L) & 65535;
            var j = new Vector2((float)(((h & 255) / 255.0 - 0.5) * 0.7), (float)((((h >> 8) & 255) / 255.0 - 0.5) * 0.7));
            return V.F(a).LerpTo(V.F(b), Zp[i]).Add(V.HALF).Add(j);
        }

        bool ZAllowed(int k, int mode, int fromSect)
        {
            var q = Tp(k);
            int t = D.Tiles[k];
            if (t == Data.T_WATER || t == Data.T_BLD || t == Data.T_VOID) return false;
            if (mode != 2 && mode != 3 && S.Phase != "fall" && Safe[k] == 1) return false;
            if (mode == 0 && S.Phase != "fall")
            {
                int s = SectOf(q);
                if (s != fromSect && S.Zinf[s] < 0.12f) return false;
            }
            return true;
        }

        void MoveZombies(double dh)
        {
            int n = Zc.Count;
            bool fall = S.Phase == "fall";
            int k0 = (ZoneFrozen(1) || ZoneFrozen(2)) ? D.H0 * D.W : 1 << 30;
            int i = 0;
            while (i < n)
            {
                if (Zh[i] == 255) { i++; continue; }
                if (Zc[i] >= k0 && ZoneFrozen(ZoneOf(Tp(Zc[i])))) { i++; continue; }
                double spd = Zh[i] != 0 ? (Zh[i] == 3 ? Z_RAID : Z_FAST) : Z_SPEED;
                if (ExpansionRules) spd *= ExpZombieSpeedMul();   // Expansion: season + snow
                Zp[i] = (float)(Zp[i] + dh * spd);
                int hops = 0;
                while (Zp[i] >= 1.0f && hops < 8)
                {
                    hops++;
                    Zp[i] -= 1.0f;
                    int cur = Zn[i];
                    int prev = Zc[i];
                    Zc[i] = cur;
                    if (_TR.Has(cur) && cur != prev)
                    {
                        TrapHit(i, cur);
                        if (Zh[i] == 255) break;
                    }
                    Zn[i] = ZChoose(i, cur, prev, fall);
                }
                if (Zh[i] != 255 && Zp[i] > 1.0f) Zp[i] = 0f;
                i++;
            }
        }

        int ZChoose(int i, int cur, int prev, bool fall)
        {
            int mode = Zh[i];
            var c = Tp(cur);
            int cs = SectOf(c);
            if ((mode == 3 || (fall && (mode == 1 || (i % 9 == 0)))) && Flow[cur] > 0 && Flow[cur] < 99999)
            {
                int best = cur, bd = Flow[cur];
                foreach (var d in DIRS)
                {
                    var q = c + d;
                    if (!D.InMap(q)) continue;
                    int k = Ti(q);
                    if (Flow[k] < bd) { bd = Flow[k]; best = k; }
                }
                if (best != cur)
                {
                    if (Blocked(best))
                    {
                        HitBlocker(best, mode == 3 ? RAID_WALL_HIT * Z_RAID_HIT : 0.9 / MOVE_MULT);
                        return cur;
                    }
                    return best;
                }
            }
            var tgt = NearestOpenHuman(V.F(c), 4.0f);
            if (tgt.x >= 0f)
            {
                int best2 = -1; double bd2 = 1000000000.0;
                foreach (var d in DIRS)
                {
                    var q = c + d;
                    if (!D.InMap(q)) continue;
                    int k = Ti(q);
                    if (!ZAllowed(k, mode, cs) || Blocked(k)) continue;
                    double dd = V.F(q).Add(V.HALF).Dist2To(tgt);
                    if (dd < bd2) { bd2 = dd; best2 = k; }
                }
                if (best2 >= 0) return best2;
            }
            if (_lures.Count > 0 && mode != 3 && Rng.Randf() < 0.8f)
            {
                int lk = LureStep(i, c, mode, cs);
                if (lk >= 0) return lk;
            }
            var dirv = c.Sub(Tp(prev));
            if (!dirv.IsZero() && Rng.Randf() < 0.7f)
            {
                var q2 = c + dirv;
                if (D.InMap(q2))
                {
                    int k2 = Ti(q2);
                    if (ZAllowed(k2, mode, cs))
                    {
                        if (Blocked(k2))
                        {
                            if (fall || mode != 0) HitBlocker(k2, 0.6 * (mode == 3 ? Z_RAID_HIT : 1.0 / MOVE_MULT));
                        }
                        else return k2;
                    }
                }
            }
            var opts = new List<int>(4);
            foreach (var d in DIRS)
            {
                var q = c + d;
                if (!D.InMap(q)) continue;
                int k = Ti(q);
                if (ZAllowed(k, mode, cs) && !Blocked(k)) opts.Add(k);
            }
            if (opts.Count == 0) return cur;
            return opts[Rng.RandiRange(0, opts.Count - 1)];
        }

        readonly List<Person> _openHumans = new List<Person>();

        Vector2 NearestOpenHuman(Vector2 p, float r)
        {
            var best = new Vector2(-1, -1); float bd = r * r;
            foreach (var h in _openHumans)
            {
                float d = h.Pos.Dist2To(p);
                if (d < bd) { bd = d; best = h.Pos; }
            }
            return best;
        }

        void RebuildBuckets()
        {
            Array.Fill(BucketHead, -1);
            int n = Zc.Count;
            while (BucketNext.Count < n) BucketNext.Add(0);
            if (BucketNext.Count > n) BucketNext.RemoveRange(n, BucketNext.Count - n);
            for (int i = 0; i < n; i++)
            {
                int k = Zp[i] > 0.5f ? Zn[i] : Zc[i];
                BucketNext[i] = BucketHead[k];
                BucketHead[k] = i;
            }
        }

        public List<int> ZombiesNear(Vector2 p, double r, int limit = 64)
        {
            var o = new List<int>();
            int ir = (int)Math.Ceiling(r);
            var c = new Vector2Int((int)p.x, (int)p.y);
            for (int dy = -ir; dy < ir + 1; dy++)
            {
                int y = c.y + dy;
                if (y < 0 || y >= D.H) continue;
                for (int dx = -ir; dx < ir + 1; dx++)
                {
                    int x = c.x + dx;
                    if (x < 0 || x >= D.W) continue;
                    int i = BucketHead[y * D.W + x];
                    while (i >= 0 && i < BucketNext.Count)
                    {
                        if (i < Zc.Count && Zh[i] != 255 && ZPos(i).DistTo(p) <= r)
                        {
                            o.Add(i);
                            if (o.Count >= limit) return o;
                        }
                        i = BucketNext[i];
                    }
                }
            }
            return o;
        }

        void BuildFlow()
        {
            _flowDirty = false;
            _flowBuilt = true;
            Array.Fill(Flow, 99999);
            var q = new List<int>();
            foreach (var b in AllBases())
            {
                int k = Ti(b.Pos);
                Flow[k] = 0; q.Add(k);
            }
            int head = 0;
            int fr = FLOW_R;
            foreach (var b in AllBases())
            {
                fr = Math.Max(fr, (int)(_enclRad.GetOr(b.Id, 0.0) * 1.5) + 40);
                fr = Math.Max(fr, (int)RaidReach(b));
            }
            while (head < q.Count)
            {
                int k = q[head]; head++;
                int d = Flow[k] + 1;
                if (d > fr) continue;
                int x = k % D.W, y = k / D.W;
                foreach (var o in DIRS)
                {
                    int nx = x + o.x, ny = y + o.y;
                    if (nx < 0 || ny < 0 || nx >= D.W || ny >= D.H) continue;
                    int j = ny * D.W + nx;
                    if (Flow[j] <= d) continue;
                    int t = D.Tiles[j];
                    if (t == Data.T_WATER || t == Data.T_BLD || t == Data.T_VOID) continue;
                    Flow[j] = d; q.Add(j);
                }
            }
        }

        // ---------------- cleared sectors ----------------
        void StepControl()
        {
            byte[] ctrl = S.Ctrl;
            float[] ch = S.CtrlH;
            var held = new byte[SW * SH];
            foreach (var b in AllBases())
            {
                byte ci = (byte)(b.Col + 1);
                if (_enclSects.TryGetValue(b.Id, out var ss)) foreach (int si in ss) held[si] = ci;
                held[SectOf(b.Pos)] = ci;
            }
            foreach (var kv in S.Fences) held[SectOf(Tp(kv.Key))] = (byte)(kv.Value.Col + 1);
            foreach (var b in AllBases())
            {
                byte ci2 = (byte)(b.Col + 1);
                if (_enclSects.TryGetValue(b.Id, out var ss))
                    foreach (int si in ss)
                        if (ctrl[si] == 0) { ctrl[si] = ci2; ch[si] = 96f; }
            }
            float[] sc = S.Scout;
            for (int i = 0; i < SW * SH; i++)
            {
                if (sc[i] > 0f) sc[i] = Math.Max(0f, sc[i] - 1f);
                int o = ctrl[i];
                if (o == 0) continue;
                var c = ColonyAt(o - 1);
                if (c == null || !ColAlive(c)) { ctrl[i] = 0; continue; }
                Use(o - 1);
                double holdMax = 96.0 + Rx("clear_hold");
                if (held[i] == o) ch[i] = (float)holdMax;
                else
                {
                    double nb = 0, nn = 0;
                    int sx = i % SW, sy = i / SW;
                    foreach (var d in DIRS)
                    {
                        int qx = sx + d.x, qy = sy + d.y;
                        if (qx < 0 || qy < 0 || qx >= SW || qy >= SH) continue;
                        nb += S.Zinf[qy * SW + qx]; nn += 1.0;
                    }
                    double drain = (S.Phase == "fall" ? 3.0 : 1.0) * (1.0 + 2.0 * nb / Math.Max(1.0, nn));
                    ch[i] = (float)(ch[i] - drain);
                }
                if (ch[i] <= 0f)
                {
                    ctrl[i] = 0;
                    S.Zinf[i] = Math.Max(S.Zinf[i], 0.25f);
                    if (Rng.Randf() < 0.25f)
                    {
                        LogCol = o - 1;
                        AddLog(string.Format("The dead drifted back into a cleared block in {0}. Fence cleared ground to keep it.", D.DistrictAt(V.I(SxCenter(i))).Name), "loss");
                        LogCol = -1;
                    }
                }
                else S.Zinf[i] = Math.Min(S.Zinf[i], 0.06f);
            }
            Use(ViewCol);
        }

        // ---------------- zones frozen while nobody is there ----------------
        public bool[] ZoneLive = { true, true, true };
        void UpdateZoneLive()
        {
            ZoneLive = new[] { true, false, false };
            foreach (var c in S.Cols)
            {
                if (!ColAlive(c)) continue;
                foreach (var b in c.Bases) ZoneLive[ZoneOf(b.Pos)] = true;
                foreach (var p in c.People)
                {
                    if (p.St == "dead") continue;
                    ZoneLive[ZoneOf(new Vector2Int((int)p.Pos.x, (int)p.Pos.y))] = true;
                }
            }
        }

        public bool ZoneFrozen(int z) => z > 0 && z < ZoneLive.Length && !ZoneLive[z];

        // ---------------- path finding ----------------
        readonly Dictionary<(int, int, int, int), List<Vector2>> _pathCache = new Dictionary<(int, int, int, int), List<Vector2>>();
        readonly LinkedList<(int, int, int, int)> _pathOrder = new LinkedList<(int, int, int, int)>();
        AStarGrid Coarse;
        bool _coarseReady;
        const int LONG_PATH = 90;

        /// <summary>Cached path between two tiles (shared list: callers must not mutate it).</summary>
        public List<Vector2> FindPath(Vector2Int a, Vector2Int b)
        {
            var key = (a.x, a.y, b.x, b.y);
            if (_pathCache.TryGetValue(key, out var hit)) return hit;
            List<Vector2> path;
            if (Math.Abs(a.x - b.x) + Math.Abs(a.y - b.y) <= LONG_PATH) path = Astar.GetPointPath(a, b);
            else path = LongPath(a, b);
            _pathCache[key] = path;
            _pathOrder.AddLast(key);
            if (_pathOrder.Count > 400)
            {
                _pathCache.Remove(_pathOrder.First.Value);
                _pathOrder.RemoveFirst();
            }
            return path;
        }

        void EnsureCoarse()
        {
            if (_coarseReady) return;
            Coarse.SetRegion(new RectInt(0, 0, SW, SH));
            for (int i = 0; i < SW * SH; i++) Coarse.SetSolid(new Vector2Int(i % SW, i / SW), SectLand[i] < 0.08f);
            _coarseReady = true;
        }

        List<Vector2> LongPath(Vector2Int a, Vector2Int b)
        {
            EnsureCoarse();
            if (_pathOrder.Count == 0)
                for (int i = 0; i < SW * SH; i++)
                    if (SectLand[i] >= 0.08f) Coarse.SetWeight(new Vector2Int(i % SW, i / SW), (float)(1.0 + 30.0 * S.Zinf[i] * S.Zinf[i]));
            var sa = new Vector2Int(a.x / SECT, a.y / SECT); var sb = new Vector2Int(b.x / SECT, b.y / SECT);
            var cp = Coarse.GetIdPath(sa, sb, true);
            if (cp.Count < 3) return Astar.GetPointPath(a, b);
            var o = new List<Vector2>();
            var cur = a;
            int step = 6;
            int k = step;
            while (true)
            {
                var nxt = b;
                if (k < cp.Count - 2)
                {
                    var sc = cp[k];
                    nxt = D.NearestWalkable(new Vector2Int(sc.x * SECT + SECT / 2, sc.y * SECT + SECT / 2), 6);
                    if (nxt.x < 0) { k++; continue; }
                }
                var leg = Astar.GetPointPath(cur, nxt);
                if (leg.Count == 0) return Astar.GetPointPath(a, b);
                if (o.Count > 0) leg = leg.GetRange(1, leg.Count - 1);
                o.AddRange(leg);
                if (nxt.Same(b)) break;
                cur = nxt;
                k += step;
            }
            return o;
        }

        static List<Vector2> FromHere(List<Vector2> path, Vector2 pos)
        {
            var here = pos.Sub(V.HALF);
            if (path.Count == 0 || path[0].DistTo(here) < 0.05f || path[0].DistTo(here) > 3.0f) return path;
            var o = new List<Vector2>(path);
            o[0] = here;
            return o;
        }
    }
}
