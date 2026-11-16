// game.gd: night watch (guards), relationships / partners / births / growing up,
// beds and placing new arrivals, and the daily home/work routines inside camps.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DayZero.Core
{
    public sealed partial class Game
    {
        // ---------------- guards ----------------
        public bool OnDuty() { int h = Hour(); return h >= 19 || h < 6; }
        public List<Person> GuardsAt(int bid) => C.People.Where(p => p.St != "dead" && p.Base == bid && p.Guard).ToList();
        public int GuardsTotal() => C.People.Count(p => p.St != "dead" && p.Guard);
        public int GuardsWanted(Base b)
        {
            int n = BasePeople(b.Id).Count(p => !IsMinor(p));
            return V.Clampi((int)Math.Ceiling(n / 5.0), 1, 8);
        }

        public string SetGuard(Person p, bool on)
        {
            if (on && p.Sq > 0) return string.Format("{0} is in a squad.", p.Name);
            if (on && IsMinor(p)) return string.Format("{0} is too young for the night watch.", p.Name);
            p.Guard = on;
            p.Gauto = false;
            if (!on) p.Post = null;
            Equip();
            EmitChanged();
            return "";
        }

        static readonly Dictionary<string, double> GUARD_PRI = new Dictionary<string, double>
        { ["Soldier"] = 0, ["Cop"] = 1, ["Scavenger"] = 3, ["Civilian"] = 4, ["Farmer"] = 5, ["Engineer"] = 6, ["Scientist"] = 7, ["Medic"] = 8 };

        void AutoGuards(bool force = false)
        {
            if (!C.Auto.Guards) return;
            int h = Hour();
            if (!force && !(h >= 18 && h <= 23)) return;
            var named = new List<string>();
            foreach (var b in C.Bases)
            {
                if (b.Done < 1.0) continue;
                var ppl = BasePeople(b.Id);
                int have = ppl.Count(p => p.Guard);
                int want = GuardsWanted(b);
                if (have >= want) continue;
                var cand = new List<(double pr, Person p)>();
                foreach (var p in ppl)
                {
                    if (p.Guard || p.Sq > 0 || p.Hp < 50.0 || (p.Inf >= 0.0 && p.Known)) continue;
                    if (IsMinor(p)) continue;
                    if (p.Gauto == false) continue;
                    double pr = GUARD_PRI.GetOr(p.Job, 5.0);
                    if (p.Wpn != "") pr -= 0.5;
                    if (p.St != "base") pr += 6.0;
                    if (new[] { "tf", "sq", "sqs", "sqa", "build" }.Contains(p.Goal)) pr += 20.0;
                    cand.Add((pr, p));
                }
                Gd.StableSort(cand, (x, y) => x.pr.CompareTo(y.pr));
                for (int i = 0; i < Math.Min(want - have, cand.Count); i++)
                {
                    var p = cand[i].p;
                    p.Guard = true; p.Gauto = true;
                    named.Add(p.First);
                }
            }
            if (named.Count > 0)
            {
                Equip();
                AddLog(string.Format("{0} took the night watch.", string.Join(", ", named)), "info");
            }
            if (force) RecallGuards();
        }

        void RecallGuards()
        {
            foreach (var p in C.People.ToList())
            {
                if (p.St == "dead" || !p.Guard || p.Sq > 0) continue;
                if ((p.St == "move" || p.St == "work") && (p.Goal == "scav" || p.Goal == "hunt" || p.Goal == "patrol" || p.Goal == "repair"))
                {
                    if (p.Goal == "scav") { _claimed.Remove(p.Lot); p.Lot = -1; }
                    else if (p.Goal == "repair") _claimed.Remove(-1000 - Ti(p.Tgt));
                    GoHome(p);
                }
            }
        }

        void PostGuards(bool force = false)
        {
            int h = Hour();
            if (!force && h != 18 && !(h >= 19 || h < 6)) return;
            var R0 = C.Nraid;
            foreach (var b in C.Bases)
            {
                var gs = new List<Person>();
                bool missing = false;
                foreach (var p in GuardsAt(b.Id))
                    if (p.St == "base")
                    {
                        gs.Add(p);
                        if (!p.Post.HasValue) missing = true;
                    }
                if (gs.Count == 0) continue;
                if (!force && h != 18 && !missing) continue;
                Gd.StableSort(gs, (x, y) => x.Id.CompareTo(y.Id));
                double rp = BaseZoneR(b) - 1.0;
                if (IsEnclosed(b)) rp = V.Clamp(Math.Sqrt(EnclosedCount(b) / Math.PI) * 0.9, rp, 22.0);
                bool focus = R0.Bid == b.Id && (R0.Warned || R0.On);
                int n = gs.Count;
                var used = new HashSet<int>();
                var inner = _enclInner.GetOr(b.Id, new int[0]);
                if (IsEnclosed(b) && _enclRad.GetOr(b.Id, 0.0) > 24.0 && inner.Length > 0)
                {
                    var ec = _enclCtr.GetOr(b.Id, V.F(b.Pos));
                    for (int i = 0; i < n; i++)
                    {
                        double ang2 = V.TAU * i / n;
                        if (focus) ang2 = R0.Dir + (i - (n - 1) * 0.5) * V.Clamp(1.6 / n, 0.12, 0.4);
                        int bestk = -1; double bda = 1000000000.0;
                        foreach (int k in inner)
                        {
                            if (used.Contains(k) || Blocked(k)) continue;
                            var pv = V.F(Tp(k)).Add(V.HALF).Sub(ec);
                            double da = Math.Abs(V.Wrapf(pv.Ang() - ang2, -Math.PI, Math.PI)) * 100.0 - pv.Len() * 0.01;
                            if (da < bda) { bda = da; bestk = k; }
                        }
                        if (bestk < 0) break;
                        used.Add(bestk);
                        gs[i].Post = V.F(Tp(bestk)).Add(V.HALF);
                    }
                    continue;
                }
                bool yardPosts = PhaseARules && !IsEnclosed(b) && S.Walls.Any(w => w.Starter && w.Bid == b.Id);
                var yd = yardPosts ? YardOf(b) : default;
                for (int i = 0; i < n; i++)
                {
                    double ang = V.TAU * i / n;
                    if (focus) ang = R0.Dir + (i - (n - 1) * 0.5) * V.Clamp(1.6 / n, 0.18, 0.5);
                    if (yardPosts)
                    {
                        // v0.12.0a: behind the palisade, toward the threat (the firing step the view draws)
                        var dir = V.F(Math.Cos(ang), Math.Sin(ang));
                        float m = Math.Max(Math.Abs(Vector2.Dot(dir, yd.U)), Math.Abs(Vector2.Dot(dir, yd.V)));
                        var wantY = yd.C + dir * ((yd.Half - 0.8f) / Math.Max(0.3f, m));
                        int bk = -1; double bdy = 1e9;
                        int RY = (int)Math.Ceiling(yd.Half) + 1;
                        var cq = V.I(yd.C);
                        for (int dy = -RY; dy <= RY; dy++)
                            for (int dx = -RY; dx <= RY; dx++)
                            {
                                var q = cq.Add(dx, dy);
                                if (!D.InMap(q) || !D.Walkable(q)) continue;
                                int k = Ti(q);
                                if (Blocked(k) || used.Contains(k)) continue;
                                var qc = V.F(q).Add(V.HALF) - yd.C;
                                if (Math.Abs(Vector2.Dot(qc, yd.U)) > yd.Half - 0.3f || Math.Abs(Vector2.Dot(qc, yd.V)) > yd.Half - 0.3f) continue;
                                double dd = V.F(q).Add(V.HALF).Dist2To(wantY);
                                if (dd < bdy) { bdy = dd; bk = k; }
                            }
                        if (bk >= 0) { used.Add(bk); gs[i].Post = V.F(Tp(bk)).Add(V.HALF); continue; }
                    }
                    var want = V.F(b.Pos).Add(V.HALF).Add(V.F(Math.Cos(ang), Math.Sin(ang)).Mul(rp));
                    var best = b.Pos; double bd = 1000000000.0;
                    int R2 = (int)Math.Ceiling(rp) + 2;
                    for (int dy = -R2; dy < R2 + 1; dy++)
                        for (int dx = -R2; dx < R2 + 1; dx++)
                        {
                            var q = b.Pos.Add(dx, dy);
                            if (!D.InMap(q) || !D.Walkable(q)) continue;
                            int k = Ti(q);
                            if (Safe[k] == 0 || Blocked(k) || used.Contains(k)) continue;
                            if (V.Len(dx, dy) < 2.0) continue;
                            double dd = V.F(q).Add(V.HALF).Dist2To(want);
                            if (dd < bd) { bd = dd; best = q; }
                        }
                    used.Add(Ti(best));
                    gs[i].Post = V.F(best).Add(V.HALF);
                }
            }
        }

        void GuardPostFights()
        {
            if (!OnDuty()) return;
            foreach (var p in C.People.ToList())
            {
                if (p.St != "base" || !p.Guard || !p.Post.HasValue) continue;
                var near = ZombiesNear(p.Pos, 0.9, 3);
                if (near.Count == 0) continue;
                if (PhaseARules) near.RemoveAll(zi => WallBetween(ZPos(zi), p.Pos));   // v0.12.0a: not through an intact wall
                if (near.Count == 0) continue;
                var b = BaseById(p.Base);
                double atk = 0.18 * 30.0 / (30.0 + (b != null ? Defense(b) : 10.0));
                foreach (int zi in near)
                {
                    if (Rng.Randf() >= atk) continue;
                    if (Rng.Randf() < 0.7f)
                    {
                        p.Hp -= 25.0;
                        PlaySfx("hit", p.Pos);
                        if (p.Hp <= 0.0)
                        {
                            KillPerson(p, string.Format("fell holding the line at {0}", b?.Name ?? "camp"));
                            PlaySfx("scream", p.Pos);
                            break;
                        }
                    }
                    else
                    {
                        Bite(p, " on the night watch");
                        break;
                    }
                }
            }
        }

        // ---------------- family ----------------
        public const double REL_FRIEND = 25.0;
        public const double REL_CLOSE = 60.0;
        public const double REL_PARTNER = 85.0;
        public const double REL_GAIN = 0.6;
        public const double REL_FADE = 0.04;
        public const int REL_KEEP = 14;
        public const double PARTNER_CHEM = 0.7;
        public const double PARTNER_CHANCE = 0.06;
        public const double PREG_H = 240.0;
        public const double BIRTH_GAP_H = 360.0;
        public const double CONCEIVE_CHANCE = 0.006;
        public const double BABY_H = 96.0;
        public const double CHILD_H = 384.0;
        public const double FAM_ADULT_H = 720.0;
        public const double FOOD_DAYS = 3.0;
        public const double MORALE_FAMILY = 45.0;

        void FamInit(Person p, string sex = "")
        {
            if (p.Sex == null || (sex != "" && p.Sex != sex)) p.Sex = sex != "" ? sex : D.SexOf(p.Name ?? "", Frng);
            if (p.Stage == null) p.Stage = AgeStage(p);
            if (p.Rel == null) p.Rel = new ODict<string, double>();
            if (p.Parents == null) p.Parents = new List<int>();
            if (p.Pnames == null) p.Pnames = new List<string>();
            if (p.Kids == null) p.Kids = new List<int>();
            if (p.Lost == null) p.Lost = "";
        }

        void EnsureFamily(GameState d)
        {
            foreach (var c in d.Cols) foreach (var p in c.People) FamInit(p);
        }

        public string AgeStage(Person p)
        {
            double b = p.Born;
            if (b < 0.0) return "adult";
            double a = (S?.T ?? 0.0) - b;
            if (a < BABY_H) return "baby";
            if (a < CHILD_H) return "child";
            if (a < FAM_ADULT_H) return "teen";
            return "adult";
        }

        public bool IsAdult(Person p) => AgeStage(p) == "adult";
        public bool IsKid(Person p) { string s = AgeStage(p); return s == "baby" || s == "child"; }
        public bool IsMinor(Person p) => AgeStage(p) != "adult";

        public string JobBlock(Person p, string job)
        {
            if (IsKid(p)) return string.Format("{0} is only a child.", p.First);
            if (job == "Child") return "Only children stay as kids.";
            if (IsMinor(p) && Data.DANGER_JOBS.Contains(job)) return string.Format("{0} is too young to be a {1}.", p.First, job);
            return "";
        }

        public double RelOf(Person p, Person q) => p.Rel?.Get(q.Id.ToString(), 0.0) ?? 0.0;

        public string RelLabel(Person p, Person q)
        {
            if (p.Partner == q.Id && q.Id > 0) return "partner";
            double v = RelOf(p, q);
            if (v >= REL_CLOSE) return "close friend";
            if (v >= REL_FRIEND) return "friend";
            return "";
        }

        double Chem(int a, int b)
        {
            uint h = GdHash.StringHash(string.Format("{0}-{1}-{2}", Math.Min(a, b), Math.Max(a, b), S.Seed));
            return 0.3 + (Math.Abs((long)h) % 1000) / 1000.0 * 1.3;
        }

        void AddRelPerson(Person p, Person q, double v)
        {
            string k = q.Id.ToString();
            p.Rel[k] = V.Clamp(p.Rel.Get(k, 0.0) + v, 0.0, 100.0);
        }

        void StepFamily()
        {
            if (C.People.Count == 0) return;
            foreach (var p in C.People) FamInit(p);
            GrowUp();
            StepRelationships();
            StepBirths();
            if (Hour() == 7) KeepFamiliesTogether();
        }

        void StepRelationships()
        {
            var byBase = new ODict<int, List<Person>>();
            foreach (var p in C.People)
            {
                if (p.St == "dead" || IsKid(p)) continue;
                if (!byBase.Has(p.Base)) byBase[p.Base] = new List<Person>();
                byBase[p.Base].Add(p);
            }
            var over = new Dictionary<int, bool>();
            foreach (var b in C.Bases) over[b.Id] = BasePeople(b.Id).Count > BaseBeds(b);
            double mood = 0.5 + V.Clamp(C.Morale, 0.0, 100.0) / 100.0;
            bool hard = C.Nraid.On;
            foreach (var kv in byBase)
            {
                var ppl = kv.Value;
                if (ppl.Count < 2) continue;
                foreach (var p in ppl)
                {
                    double tot = 0.0;
                    var ws = new List<double>();
                    foreach (var q0 in ppl)
                    {
                        if (q0.Id == p.Id) { ws.Add(0.0); continue; }
                        double w = Chem(p.Id, q0.Id) * (1.0 + RelOf(p, q0) / 30.0);
                        ws.Add(w); tot += w;
                    }
                    if (tot <= 0.0) continue;
                    double r = Frng.Randf() * tot;
                    var q = ppl[ppl.Count - 1];
                    for (int i = 0; i < ppl.Count; i++)
                    {
                        r -= ws[i];
                        if (r <= 0.0 && ws[i] > 0.0) { q = ppl[i]; break; }
                    }
                    if (q.Id == p.Id) continue;
                    double m = REL_GAIN * Chem(p.Id, q.Id) * mood;
                    if (p.Job == q.Job) m *= 1.4;
                    if (p.St != "base" || q.St != "base") m *= 0.5;
                    if (over.GetOr(kv.Key, false)) m *= 0.8;
                    if (hard) m *= 1.3;
                    if (p.Partner == q.Id) m *= 1.5;
                    AddRelPerson(p, q, m); AddRelPerson(q, p, m);
                    MaybePartners(p, q);
                }
            }
            foreach (var p in C.People)
            {
                if (p.St == "dead") continue;
                var r = p.Rel;
                foreach (string k in r.KeyList())
                {
                    var q = PersonById(int.Parse(k));
                    if (q == null || q.St == "dead") { r.Remove(k); continue; }
                    if (q.Base != p.Base && p.Partner != q.Id)
                    {
                        r[k] = r[k] - REL_FADE;
                        if (r[k] <= 0.0) r.Remove(k);
                    }
                }
                if (r.Count > REL_KEEP)
                {
                    var ks = r.KeyList();
                    Gd.StableSort(ks, (a, b) => r[b].CompareTo(r[a]));
                    for (int i = REL_KEEP; i < ks.Count; i++)
                        if (int.Parse(ks[i]) != p.Partner) r.Remove(ks[i]);
                }
            }
        }

        void MaybePartners(Person p, Person q)
        {
            if (p.Partner != 0 || q.Partner != 0) return;
            if (!IsAdult(p) || !IsAdult(q) || p.Sex == q.Sex) return;
            if (q.Parents.Contains(p.Id) || p.Parents.Contains(q.Id)) return;
            foreach (int pa in p.Parents) if (q.Parents.Contains(pa)) return;
            if (RelOf(p, q) < REL_PARTNER || RelOf(q, p) < REL_PARTNER) return;
            if (Chem(p.Id, q.Id) < PARTNER_CHEM) return;
            if (Frng.Randf() >= PARTNER_CHANCE) return;
            MakePartners(p, q);
        }

        public void MakePartners(Person p, Person q)
        {
            p.Partner = q.Id; q.Partner = p.Id;
            p.Lost = ""; q.Lost = "";
            C.Morale = Math.Min(100.0, C.Morale + 2.0);
            AddLog(string.Format("{0} and {1} became partners.", p.Name, q.Name), "good");
        }

        int Pregnancies(int bid = -1) => C.People.Count(p => p.St != "dead" && p.Preg >= 0.0 && (bid < 0 || p.Base == bid));

        public string FamilyBlock(Base b)
        {
            if (b == null) return "no camp";
            var R0 = C.Nraid;
            if (R0.On || (R0.Warned && R0.Bid == b.Id)) return "under attack";
            int pop = AlivePeople().Count;
            double tf = 0, tw = 0;
            foreach (var bb in C.Bases)
            {
                var r = BaseBalance(bb);
                tf += r["food"]; tw += r["water"];
            }
            foreach (var k in new[] { "food", "water" })
            {
                if (R(k) < Math.Max(30.0, FOOD_DAYS * pop)) return "not enough " + k;
                if ((k == "food" ? tf : tw) < 0.0) return "running short of " + k;
            }
            if (BasePeople(b.Id).Count + Pregnancies(b.Id) >= BaseBeds(b)) return "no free beds";
            int beds = C.Bases.Sum(BaseBeds);
            if (pop + Pregnancies() >= beds) return "no free beds";
            if (C.Morale < MORALE_FAMILY) return "morale too low";
            return "";
        }

        void StepBirths()
        {
            foreach (var m in C.People.ToList())
            {
                if (m.St == "dead" || m.Preg < 0.0) continue;
                if (S.T - m.Preg >= PREG_H) GiveBirth(m);
            }
            int adults = C.People.Count(p => p.St != "dead" && IsAdult(p));
            if (Pregnancies() >= Math.Max(1, adults / 8)) return;
            var whyCache = new Dictionary<int, string>();
            foreach (var m in C.People.ToList())
            {
                if (m.St == "dead" || m.Sex != "f" || m.Partner == 0 || m.Preg >= 0.0 || !IsAdult(m)) continue;
                var f = PersonById(m.Partner);
                if (f == null || f.St == "dead" || f.Base != m.Base) continue;
                if (S.T - m.LastBirth < BIRTH_GAP_H) continue;
                if (m.Hp < 60.0 || m.Inf >= 0.0 || (f.Inf >= 0.0 && f.Known)) continue;
                if (!whyCache.ContainsKey(m.Base)) whyCache[m.Base] = FamilyBlock(BaseById(m.Base));
                if (whyCache[m.Base] != "") continue;
                if (Frng.Randf() >= CONCEIVE_CHANCE) continue;
                m.Preg = S.T; m.PregBy = f.Id;
                AddLog(string.Format("{0} and {1} are expecting a baby.", m.Name, f.Name), "good");
                C.Morale = Math.Min(100.0, C.Morale + 3.0);
                whyCache[m.Base] = FamilyBlock(BaseById(m.Base));
                if (Pregnancies() >= Math.Max(1, adults / 8)) return;
            }
        }

        Person GiveBirth(Person m)
        {
            var f = PersonById(m.PregBy);
            bool girl = Frng.Randf() < 0.5f;
            string first = (girl ? Data.BABY_F : Data.BABY_M)[Frng.RandiRange(0, 19)];
            var parts = (f?.Name ?? m.Name).Split(' ');
            string last = parts[parts.Length - 1];
            var b = AddPerson("Child", m.Base, m.Pos, string.Format("{0} {1}", first, last));
            FamInit(b, girl ? "f" : "m");
            b.Born = S.T; b.Stage = "baby";
            b.Parents = new List<int> { m.Id }; b.Pnames = new List<string> { m.Name };
            if (f != null)
            {
                b.Parents.Add(f.Id); b.Pnames.Add(f.Name);
                f.Kids.Add(b.Id);
            }
            else if (m.PregBy != 0) b.Parents.Add(m.PregBy);
            m.Kids.Add(b.Id);
            m.Preg = -1.0; m.PregBy = 0; m.LastBirth = S.T;
            b.Hp = 100.0;
            C.Morale = Math.Min(100.0, C.Morale + 6.0);
            if (f == null) AddLog(string.Format("A baby {0}, {1}, was born to {2}.", girl ? "girl" : "boy", first, m.Name), "good");
            else AddLog(string.Format("A baby {0}, {1}, was born to {2} and {3}.", girl ? "girl" : "boy", first, f.Name, m.Name), "good");
            return b;
        }

        void GrowUp()
        {
            foreach (var p in C.People)
            {
                if (p.St == "dead" || p.Born < 0.0) continue;
                string st = AgeStage(p);
                if (st == (p.Stage ?? "")) continue;
                p.Stage = st;
                switch (st)
                {
                    case "teen":
                        if (p.Job == "Child") p.Job = "Civilian";
                        AddLog(string.Format("{0} is old enough to help around {1} now.", p.Name, BaseById(p.Base)?.Name ?? "camp"), "info");
                        break;
                    case "adult":
                        AddLog(string.Format("{0} has come of age. The camp has one more grown-up to count on.", p.Name), "good");
                        break;
                }
            }
        }

        void KeepFamiliesTogether()
        {
            foreach (var p in C.People.ToList())
            {
                if (p.St == "dead" || p.Sq > 0) continue;
                if (IsKid(p))
                {
                    foreach (int pid in p.Parents)
                    {
                        var pa = PersonById(pid);
                        if (pa != null && pa.St != "dead" && pa.Col == CurCol)
                        {
                            if (pa.Base != p.Base && BaseById(pa.Base) != null) MoveToBase(p, pa.Base);
                            break;
                        }
                    }
                    continue;
                }
                var q = PersonById(p.Partner);
                if (q == null || q.Base == p.Base || p.Hold || p.Guard) continue;
                if (q.Col != CurCol || (p.Id > q.Id && !q.Hold)) continue;
                var to = BaseById(q.Base);
                if (to == null || to.Done < 1.0 || BasePeople(to.Id).Count >= BaseBeds(to)) continue;
                MoveToBase(p, to.Id);
                AddLog(string.Format("{0} moved to {1} to be with {2}.", p.Name, to.Name, q.First), "info");
            }
        }

        void FamilyOnDeath(Person p)
        {
            if (p.Rel == null) return;
            var q = PersonById(p.Partner);
            if (q != null && q.St != "dead")
            {
                q.Partner = 0; q.Lost = p.Name;
                q.Rel?.Remove(p.Id.ToString());
                C.Morale = Math.Max(0.0, C.Morale - 6.0);
                AddLog(string.Format("{0} lost {1} partner, {2}. The whole camp feels it.", q.Name, q.Sex == "f" ? "her" : "his", p.First), "loss");
            }
            if (IsMinor(p))
            {
                var ns = new List<string>();
                foreach (int pid in p.Parents)
                {
                    var pa = PersonById(pid);
                    if (pa != null && pa.St != "dead") ns.Add(pa.First);
                }
                if (ns.Count > 0)
                {
                    C.Morale = Math.Max(0.0, C.Morale - 8.0);
                    AddLog(string.Format("{0} are mourning their child, {1}.", string.Join(" and ", ns), p.First), "loss");
                }
            }
            else
            {
                int orphans = 0;
                foreach (int kid in p.Kids)
                {
                    var k = PersonById(kid);
                    if (k != null && k.St != "dead" && IsMinor(k)) orphans++;
                }
                if (orphans > 0) C.Morale = Math.Max(0.0, C.Morale - 2.0 * orphans);
            }
            p.Preg = -1.0;
        }

        public string FamilyText(Person p)
        {
            var bits = new List<string>();
            string st = AgeStage(p);
            if (st != "adult")
            {
                int days = (int)((S.T - (p.Born >= 0 ? p.Born : S.T)) / 24.0);
                bits.Add(string.Format("{0}, {1} day{2} old", Gd.Capitalize(st), days, days == 1 ? "" : "s"));
            }
            var q = PersonById(p.Partner);
            if (q != null) bits.Add("Partner: " + q.First);
            else if (!string.IsNullOrEmpty(p.Lost)) bits.Add("Lost partner: " + p.Lost.Split(' ')[0]);
            if (p.Preg >= 0.0)
            {
                int left = Math.Max(1, (int)Math.Ceiling((PREG_H - (S.T - p.Preg)) / 24.0));
                bits.Add(string.Format("Expecting ({0} day{1} to go)", left, left == 1 ? "" : "s"));
            }
            if (p.Pnames.Count > 0) bits.Add("Parents: " + string.Join(", ", p.Pnames.Select(n => n.Split(' ')[0])));
            var ks = new List<string>();
            foreach (int kid in p.Kids)
            {
                var k = PersonById(kid);
                if (k != null && k.St != "dead") ks.Add(string.Format("{0} ({1})", k.First, AgeStage(k)));
            }
            if (ks.Count > 0) bits.Add("Kids: " + string.Join(", ", ks));
            var close = new List<string>(); var fr = new List<string>();
            var r = p.Rel;
            var keys = r.KeyList();
            Gd.StableSort(keys, (a, b) => r[b].CompareTo(r[a]));
            foreach (var k in keys)
            {
                if (close.Count + fr.Count >= 4 || r[k] < REL_FRIEND) break;
                var o = PersonById(int.Parse(k));
                if (o == null || o.Id == p.Partner) continue;
                if (r[k] >= REL_CLOSE) close.Add(o.First);
                else fr.Add(o.First);
            }
            if (close.Count > 0) bits.Add("Close: " + string.Join(", ", close));
            if (fr.Count > 0) bits.Add("Friends: " + string.Join(", ", fr));
            return string.Join("  ·  ", bits);
        }

        public Dictionary<string, int> FamilyCounts()
        {
            var c = new Dictionary<string, int> { ["couples"] = 0, ["expecting"] = 0, ["babies"] = 0, ["children"] = 0, ["teens"] = 0 };
            foreach (var p in AlivePeople())
            {
                if (p.Partner > p.Id) c["couples"]++;
                if (p.Preg >= 0.0) c["expecting"]++;
                switch (AgeStage(p))
                {
                    case "baby": c["babies"]++; break;
                    case "child": c["children"]++; break;
                    case "teen": c["teens"]++; break;
                }
            }
            return c;
        }

        // ---------------- beds & arrivals ----------------
        public const int RT_PATH_BUDGET = 8;
        public const int RT_SLOT_R = 7;
        public const int RT_SLOT_MAX = 40;
        static readonly Dictionary<string, string[]> WORK_CONV = new Dictionary<string, string[]>
        { ["Farmer"] = new[] { "farm", "water" }, ["Medic"] = new[] { "hospital" }, ["Scientist"] = new[] { "school" }, ["Engineer"] = new[] { "workshop", "armory" }, ["Scavenger"] = new[] { "garage" } };
        static readonly string[] CIV_CONV = { "water", "storage", "farm", "garage", "armory", "workshop", "hospital", "school" };
        static readonly Dictionary<string, string> CONV_WORK = new Dictionary<string, string>
        { ["farm"] = "farm", ["water"] = "cistern", ["hospital"] = "clinic", ["school"] = "lab", ["workshop"] = "workshop", ["armory"] = "armory", ["storage"] = "depot", ["garage"] = "garage" };
        static readonly string[] OUTDOOR_CONV = { "farm", "water" };
        public static readonly Dictionary<string, string> WORK_LABEL = new Dictionary<string, string>
        {
            ["farm"] = "the rooftop farm", ["cistern"] = "the water cistern", ["clinic"] = "the infirmary", ["lab"] = "the school lab",
            ["workshop"] = "the workshop", ["armory"] = "the armory", ["depot"] = "the storage depot", ["garage"] = "the garage", ["post"] = "a wall post",
            ["walls"] = "the walls", ["gate"] = "the gate", ["kitchen"] = "the camp kitchen", ["fields"] = "the camp gardens", ["play"] = "playing near home",
        };
        readonly Dictionary<int, Routine> _rt = new Dictionary<int, Routine>();
        readonly Dictionary<int, bool> _rtNeed = new Dictionary<int, bool>();
        int _rtBudget = RT_PATH_BUDGET;
        readonly Dictionary<Vector2Int, List<Vector2>> _slotCache = new Dictionary<Vector2Int, List<Vector2>>();
        readonly Dictionary<int, int[]> _inside = new Dictionary<int, int[]>();
        bool _insideDirty = true;
        public int InsideVer;
        public int RtPaths;

        public int CampKey(Base b) => b.Lot >= 0 ? b.Lot : -2 - b.Id;

        public Vector2Int BldDoor(int key)
        {
            if (key >= 0)
            {
                var d = D.NearestWalkable(D.LotDoor(key), 3);
                return d.x >= 0 ? d : D.LotDoor(key);
            }
            var b = BaseAny(-2 - key);
            return b != null ? b.Pos : V.NONE;
        }

        public Vector2 BldBadgePos(int key)
        {
            if (key >= 0) return D.LotCenter(key);
            var b = BaseAny(-2 - key);
            return b != null ? V.F(b.Pos).Add(new Vector2(0.5f, -1.2f)) : new Vector2(-100, -100);
        }

        public bool IsInside(Person p) => p.Inb != NO_BLD;
        public int BedsTotal() => C.Bases.Sum(BaseBeds);
        public int BedsUsed() => AlivePeople().Count;
        public int BedsFree() => C.Bases.Sum(CampFree);
        public int CampResidents(int bid) => C.People.Count(p => p.St != "dead" && p.Base == bid);

        public int CampFree(Base b)
        {
            if (b == null || b.Done < 1.0) return 0;
            return Math.Max(0, BaseBeds(b) - CampResidents(b.Id));
        }

        void Refuse(int n, string text)
        {
            if (n <= 0) return;
            C.AddStat("refused", n);
            AddLog(text, "loss");
        }

        /// <summary>Spread new arrivals over camps (same map) with free beds, keeping families together.
        /// Returns camp id -> number of people sent there instead of at_bid.</summary>
        Dictionary<int, int> PlaceArrivals(List<Person> arr, int atBid)
        {
            var moved = new ODict<int, int>();
            var result = new Dictionary<int, int>();
            if (arr.Count == 0) return result;
            var at = BaseById(atBid);
            if (at == null)
            {
                at = MainBase();
                if (at == null) return result;
                atBid = at.Id;
            }
            var ids = new HashSet<int>(arr.Select(p => p.Id));
            var free = new Dictionary<int, int>();
            var order = new List<(int first, float d, int bid)>();
            string atMap = Rgroup(at.Pos);
            foreach (var b in C.Bases)
            {
                if (b.Done < 1.0) continue;
                if (Rgroup(b.Pos) != atMap) continue;
                int n = C.People.Count(p => p.St != "dead" && p.Base == b.Id && !ids.Contains(p.Id));
                free[b.Id] = Math.Max(0, BaseBeds(b) - n);
                order.Add((b.Id == atBid ? 0 : 1, V.F(b.Pos).DistTo(V.F(at.Pos)), b.Id));
            }
            Gd.StableSort(order, (x, y) => x.first != y.first ? x.first.CompareTo(y.first) : (x.d != y.d ? x.d.CompareTo(y.d) : x.bid.CompareTo(y.bid)));
            var root = new Dictionary<int, int>();
            foreach (var p in arr) root[p.Id] = p.Id;
            int Find(int k) { while (root[k] != k) k = root[k]; return k; }
            foreach (var p in arr)
            {
                var links = new List<int> { p.Partner };
                links.AddRange(p.Parents);
                links.AddRange(p.Kids);
                foreach (int l in links)
                    if (ids.Contains(l))
                    {
                        int a = Find(p.Id), b2 = Find(l);
                        if (a != b2) root[Math.Max(a, b2)] = Math.Min(a, b2);
                    }
            }
            var clusters = new Dictionary<int, List<Person>>();
            var corder = new List<int>();
            foreach (var p in arr)
            {
                int r = Find(p.Id);
                if (!clusters.ContainsKey(r)) { clusters[r] = new List<Person>(); corder.Add(r); }
                clusters[r].Add(p);
            }
            Gd.StableSort(corder, (x, y) => clusters[x].Count != clusters[y].Count ? clusters[y].Count.CompareTo(clusters[x].Count) : x.CompareTo(y));
            var dest = new Dictionary<int, int>();
            foreach (int r in corder)
            {
                var cl = clusters[r];
                int pick = -1;
                foreach (var o in order)
                    if (free[o.bid] >= cl.Count) { pick = o.bid; break; }
                if (pick >= 0)
                {
                    free[pick] -= cl.Count;
                    foreach (var p in cl) dest[p.Id] = pick;
                    continue;
                }
                foreach (var p in cl)
                {
                    int d = atBid;
                    foreach (var o in order)
                        if (free[o.bid] > 0) { d = o.bid; break; }
                    if (free.ContainsKey(d)) free[d] = Math.Max(0, free[d] - 1);
                    dest[p.Id] = d;
                }
            }
            foreach (var p in arr)
            {
                int d = dest.GetOr(p.Id, atBid);
                if (d == atBid) continue;
                var db = BaseById(d);
                if (db == null) continue;
                p.Base = d;
                p.Trav = d;
                Go(p, db.Pos, "home");
                moved[d] = moved.Get(d, 0) + 1;
            }
            foreach (var kv in moved)
            {
                int n = kv.Value;
                C.AddStat("sent", n);
                AddLog(string.Format("{0} survivor{1} sent to {2} ({3} full).", n, n != 1 ? "s" : "", BaseById(kv.Key)?.Name ?? "another camp", at.Name), "info");
                result[kv.Key] = n;
            }
            if (moved.Count > 0) _rtNeed[CurCol] = true;
            return result;
        }

        int NearestCampId(Vector2 pos)
        {
            int best = -1; double bd = 1e18;
            foreach (var b in C.Bases)
            {
                if (b.Done < 1.0) continue;
                double d = V.F(b.Pos).Dist2To(pos);
                if (d < bd) { bd = d; best = b.Id; }
            }
            if (best < 0) best = MainBase()?.Id ?? -1;
            return best;
        }

        public int BedRoom(int n) => V.Clampi(BedsFree(), 0, n);

        public int BedsFreeOf(int ci)
        {
            int prev = CurCol;
            Use(ci);
            int f = BedsFree();
            if (prev >= 0) Use(prev);
            return f;
        }

        public List<(int key, int beds)> BaseHomes(Base b)
        {
            int ci = b.Col;
            var bl = new List<int>();
            foreach (var kv in S.Conv)
                if (kv.Value.Type == "barracks" && kv.Value.Col == ci && kv.Value.Bid == b.Id) bl.Add(kv.Key);
            bl.Sort();
            int bunk = (int)(8.0 * (1.0 + Rx("housing_mult")));
            var o = new List<(int, int)> { (CampKey(b), Math.Max(0, BaseBeds(b) - bunk * bl.Count)) };
            foreach (int l in bl) o.Add((l, bunk));
            return o;
        }

        void InsideAdd(int key, int ci, int d)
        {
            InsideVer++;
            if (_insideDirty) return;
            if (!_inside.TryGetValue(key, out var a)) a = new int[MAX_COLONIES];
            int c = V.Clampi(ci, 0, MAX_COLONIES - 1);
            a[c] = Math.Max(0, a[c] + d);
            if (a.Sum() == 0) _inside.Remove(key);
            else _inside[key] = a;
        }

        void RecountInside()
        {
            _inside.Clear();
            _insideDirty = false;
            foreach (var c in S.Cols)
                foreach (var p in c.People)
                {
                    if (p.St == "dead" || !IsInside(p)) continue;
                    if (!_inside.TryGetValue(p.Inb, out var a)) { a = new int[MAX_COLONIES]; _inside[p.Inb] = a; }
                    a[V.Clampi(p.Col, 0, MAX_COLONIES - 1)]++;
                }
            InsideVer++;
        }

        public Dictionary<int, int[]> InsideCounts()
        {
            if (_insideDirty) RecountInside();
            return _inside;
        }

        public int InsideN(int key, int ci = -1)
        {
            if (!InsideCounts().TryGetValue(key, out var a)) return 0;
            if (ci < 0) return a.Sum();
            return ci < a.Length ? a[ci] : 0;
        }

        void EnterBld(Person p, int key)
        {
            if (IsInside(p)) LeaveBld(p);
            p.Inb = key;
            var d = BldDoor(key);
            if (d.x >= 0) p.Pos = V.F(d).Add(V.HALF);
            InsideAdd(key, p.Col, 1);
        }

        void LeaveBld(Person p)
        {
            int key = p.Inb;
            if (key == NO_BLD) return;
            p.Inb = NO_BLD;
            var d = BldDoor(key);
            if (d.x >= 0) p.Pos = V.F(d).Add(V.HALF);
            InsideAdd(key, p.Col, -1);
        }

        void EnsureRoutine(GameState d)
        {
            foreach (var c in d.Cols)
                foreach (var p in c.People)
                {
                    if (p.St != "base") p.Inb = NO_BLD;
                    if (p.Work == null) p.Work = "";
                }
            _insideDirty = true;
            _rt.Clear(); _rtNeed.Clear(); _slotCache.Clear();
        }

        List<Vector2> Slots(Vector2Int anchor)
        {
            if (_slotCache.TryGetValue(anchor, out var c)) return c;
            var o = new List<Vector2>();
            var a = D.NearestWalkable(anchor, 3);
            if (a.x < 0)
            {
                o.Add(V.F(anchor));
                _slotCache[anchor] = o;
                return o;
            }
            bool wantSafe = Ti(a) < Safe.Length && Safe[Ti(a)] == 1;
            var seen = new HashSet<int> { Ti(a) };
            var q = new List<Vector2Int> { a };
            int qi = 0;
            while (qi < q.Count && o.Count < RT_SLOT_MAX)
            {
                var cc = q[qi]; qi++;
                int k = Ti(cc);
                if (!Blocked(k) && (!wantSafe || Safe[k] == 1)) o.Add(V.F(cc));
                foreach (var dv in DIRS)
                {
                    var n = cc + dv;
                    if (!D.InMap(n) || !D.Walkable(n)) continue;
                    if (Math.Abs(n.x - a.x) > RT_SLOT_R || Math.Abs(n.y - a.y) > RT_SLOT_R) continue;
                    int nk = Ti(n);
                    if (seen.Contains(nk)) continue;
                    seen.Add(nk);
                    q.Add(n);
                }
            }
            if (o.Count == 0) o.Add(V.F(a));
            _slotCache[anchor] = o;
            return o;
        }

        Vector2 SlotPos(Vector2Int anchor, int rank, int pid)
        {
            var s = Slots(anchor);
            var t = s[rank % s.Count];
            long h = ((long)pid * 2654435761L) & 65535;
            return t.Add(V.HALF).Add(V.F(((h & 255) / 255.0 - 0.5) * 0.4, (((h >> 8) & 255) / 255.0 - 0.5) * 0.4));
        }

        Vector2Int LineSpot(Base b, int i, int n, double phase)
        {
            var inner = _enclInner.GetOr(b.Id, new int[0]);
            if (IsEnclosed(b) && inner.Length > 0)
            {
                int k = inner[(int)((i + phase) * inner.Length / Math.Max(1.0, n)) % inner.Length];
                return Tp(k);
            }
            double ang = V.TAU * (i + phase) / Math.Max(1.0, n);
            double r = Math.Max(2.0, BaseZoneR(b) - 1.5);
            var w = V.I(V.F(b.Pos).Add(V.F(Math.Cos(ang), Math.Sin(ang)).Mul(r)).Rnd());
            var q = D.NearestWalkable(w, 4);
            return q.x >= 0 ? q : b.Pos;
        }

        void AssignRoutines()
        {
            _rtNeed[CurCol] = false;
            var raid = C.Nraid;
            foreach (var b in C.Bases)
            {
                if (b.Done < 1.0) continue;
                var ppl = BasePeople(b.Id);
                if (ppl.Count == 0) continue;
                Gd.StableSort(ppl, (x, y) => x.Id.CompareTo(y.Id));
                var homes = BaseHomes(b);
                var cap = new Dictionary<int, int>();
                foreach (var h in homes) cap[h.key] = h.beds;
                int ck = CampKey(b);
                var used = new Dictionary<int, int>();
                var pending = new List<Person>();
                foreach (var p in ppl)
                {
                    int hk = p.Home;
                    if (cap.ContainsKey(hk) && used.GetOr(hk, 0) < cap[hk]) used[hk] = used.GetOr(hk, 0) + 1;
                    else pending.Add(p);
                }
                foreach (var p in pending)
                {
                    int pick = NO_BLD;
                    var fam = new List<int> { p.Partner };
                    fam.AddRange(p.Parents);
                    fam.AddRange(p.Kids);
                    foreach (int fid in fam)
                    {
                        var q = PersonById(fid);
                        if (q == null || q.St == "dead" || q.Base != b.Id) continue;
                        int qh = q.Home;
                        if (cap.ContainsKey(qh) && used.GetOr(qh, 0) < cap[qh]) { pick = qh; break; }
                    }
                    if (pick == NO_BLD)
                    {
                        int best = -1;
                        foreach (var h in homes)
                        {
                            int fr = h.beds - used.GetOr(h.key, 0);
                            if (fr > best) { best = fr; pick = h.key; }
                        }
                        if (best <= 0) pick = ck;
                    }
                    p.Home = pick;
                    used[pick] = used.GetOr(pick, 0) + 1;
                }
                foreach (var p in ppl)
                {
                    var q = PersonById(p.Partner);
                    var tgts = new List<Person>();
                    if (q != null && q.Base == b.Id && p.Id > q.Id) tgts.Add(q);
                    if (IsMinor(p))
                        foreach (int pid in p.Parents)
                        {
                            var pa = PersonById(pid);
                            if (pa != null && pa.St != "dead" && pa.Base == b.Id) tgts.Add(pa);
                        }
                    foreach (var t in tgts)
                    {
                        int th = t.Home, ph = p.Home;
                        if (th == ph) break;
                        if (cap.ContainsKey(th) && used.GetOr(th, 0) < cap[th])
                        {
                            used[ph] = used.GetOr(ph, 0) - 1; used[th] = used.GetOr(th, 0) + 1;
                            p.Home = th;
                            break;
                        }
                    }
                }
                var convs = new Dictionary<string, List<int>>();
                foreach (var kv in S.Conv)
                {
                    var c = kv.Value;
                    if (c.Col == CurCol && c.Bid == b.Id)
                    {
                        if (!convs.ContainsKey(c.Type)) convs[c.Type] = new List<int>();
                        convs[c.Type].Add(kv.Key);
                    }
                }
                foreach (var l in convs.Values) l.Sort();
                var rankT = new Dictionary<string, int>();
                var guards = new List<Person>(); var walls = new List<Person>();
                foreach (var p in ppl)
                {
                    string w = "kitchen"; int wl = -1;
                    if (IsKid(p)) w = "play";
                    else if (p.Job == "Soldier" || p.Job == "Cop") { w = "post"; guards.Add(p); }
                    else
                    {
                        var types = WORK_CONV.TryGetValue(p.Job, out var wt) ? wt.ToList() : new List<string>();
                        if (p.Job == "Civilian" || p.Job == "Child" || types.Count == 0)
                        {
                            types = new List<string>();
                            int off = p.Id % CIV_CONV.Length;
                            for (int i = 0; i < CIV_CONV.Length; i++) types.Add(CIV_CONV[(off + i) % CIV_CONV.Length]);
                        }
                        foreach (var t in types)
                            if (convs.TryGetValue(t, out var arr))
                            {
                                int rk = rankT.GetOr(t, 0); rankT[t] = rk + 1;
                                wl = arr[rk % arr.Count]; w = CONV_WORK[t];
                                break;
                            }
                        if (wl < 0)
                        {
                            switch (p.Job)
                            {
                                case "Farmer": w = "fields"; break;
                                case "Engineer": w = "walls"; walls.Add(p); break;
                                case "Scavenger": w = "gate"; break;
                                default: w = "kitchen"; break;
                            }
                        }
                    }
                    p.Work = w; p.Wl = wl;
                }
                var yard = new Dictionary<Vector2Int, int>();
                var towers = TowersOfBase(b.Id);
                towers.Sort();
                int nt = towers.Count;
                foreach (var p in ppl)
                {
                    if (!_rt.TryGetValue(p.Id, out var r))
                    {
                        r = new Routine();
                        _rt[p.Id] = r;
                    }
                    int hk = p.Home;
                    var hd = BldDoor(hk);
                    if (hd.x < 0) hd = b.Pos;
                    int wk = NO_BLD; var wa = b.Pos;
                    string w = p.Work; int wl = p.Wl;
                    switch (w)
                    {
                        case "play": wa = hd; break;
                        case "post":
                            int gi = guards.IndexOf(p);
                            if (nt > 0)
                            {
                                int tk = towers[gi % nt];
                                wa = D.NearestWalkable(Tp(tk), 3);
                                if (wa.x < 0) wa = b.Pos;
                            }
                            else wa = LineSpot(b, gi, guards.Count, 0.0);
                            break;
                        case "walls": wa = LineSpot(b, walls.IndexOf(p), walls.Count, 0.5); break;
                        case "fields":
                            var fq = D.NearestWalkable(b.Pos.Add(3, 3), 4);
                            wa = fq.x >= 0 ? fq : b.Pos;
                            break;
                        case "gate": wa = b.Pos; break;
                        case "kitchen": wk = ck; wa = b.Pos; break;
                        default:
                            if (wl >= 0)
                            {
                                wa = BldDoor(wl);
                                if (wa.x < 0) wa = b.Pos;
                                if (!OUTDOOR_CONV.Contains(ConvType(wl))) wk = wl;
                            }
                            break;
                    }
                    int rk2 = yard.GetOr(wa, 0); yard[wa] = rk2 + 1;
                    var ws = SlotPos(wa, rk2, p.Id);
                    double j = ((p.Id * 40503) & 1023) / 1023.0;
                    double eta = V.F(wa).DistTo(V.F(hd)) * 1.4 / HUMAN_SPEED;
                    double leave = V.Clamp(20.5 - Math.Max(eta, 0.25) - j * 0.75, 17.5, 20.5);
                    bool danger = false;
                    if (raid.On && raid.Bid == b.Id) danger = true;
                    else if (ZombiesNear(V.F(wa).Add(V.HALF), 3.0, 1).Count > 0) danger = true;
                    if (r.Hk != hk || !r.Wa.Same(wa) || r.Wk != wk || !r.Ws.Same(ws)) r.Code = "";
                    r.Hk = hk; r.Hd = hd; r.Wk = wk; r.Wa = wa; r.Ws = ws; r.J = j; r.Leave = leave; r.Danger = danger; r.Bid = b.Id;
                }
            }
        }

        (string code, int key, Vector2Int tile, Vector2 spot) RtTarget(Person p, Routine r)
        {
            double h = S.T % 24.0;
            double j = r.J;
            var home = ("h", r.Hk, r.Hd, V.F(r.Hd).Add(V.HALF));
            if (p.Guard && OnDuty() && p.Post.HasValue)
            {
                var pv = p.Post.Value;
                return ("p", NO_BLD, V.I(pv), pv);
            }
            if (r.Danger || (p.Inf >= 0.0 && p.Known) || p.Hp < 40.0) return home;
            if (IsKid(p))
            {
                if (h < 7.5 + j || h >= 19.0 + j * 0.5) return home;
                return ("y", NO_BLD, V.I(r.Ws), r.Ws);
            }
            if (h < 5.5 + j * 1.5 || h >= r.Leave) return home;
            int wk = r.Wk;
            if (wk != NO_BLD && (S.T + j * 11.0) % 3.0 >= 0.45)
                return ("w", wk, r.Wa, V.F(r.Wa).Add(V.HALF));
            return ("y", NO_BLD, V.I(r.Ws), r.Ws);
        }

        bool Routine(Person p, double dh)
        {
            if (!_rt.TryGetValue(p.Id, out var r) || r.Bid != p.Base)
            {
                _rtNeed[CurCol] = true;
                return true;
            }
            var t = RtTarget(p, r);
            if (r.Code != t.code || !r.Tt.Same(t.tile))
            {
                int inb = p.Inb;
                if (inb != NO_BLD && inb == t.key)
                {
                    r.Code = t.code; r.Tt = t.tile; r.Ek = t.key; r.Arr = true;
                    return true;
                }
                if (_rtBudget <= 0) return true;
                _rtBudget--;
                LeaveBld(p);
                var from = D.NearestWalkable(V.I(p.Pos), 3);
                if (from.x < 0) from = V.I(p.Pos);
                var to = t.tile;
                var path = new List<Vector2>();
                if (!from.Same(to))
                {
                    path = FindPath(from, to);
                    RtPaths++;
                }
                if (path.Count < 2) path = new List<Vector2> { V.F(from), V.F(to) };
                path = FromHere(path, p.Pos);
                r.Path = path; r.Pi = 0; r.Seg = 0.0; r.Arr = false; r.Set = false;
                r.Code = t.code; r.Tt = t.tile; r.Ek = t.key;
            }
            if (IsInside(p)) return true;
            if (!r.Arr)
            {
                var path = r.Path;
                r.Seg += dh * HUMAN_SPEED * (p.Hp / 200.0 + 0.5);
                while (r.Seg >= 1.0 && r.Pi < path.Count - 1)
                {
                    r.Seg -= 1.0;
                    r.Pi++;
                }
                if (r.Pi >= path.Count - 1)
                {
                    r.Arr = true;
                    r.Path = new List<Vector2>();
                    if (r.Ek != NO_BLD)
                    {
                        EnterBld(p, r.Ek);
                        return true;
                    }
                }
                else
                {
                    p.Pos = path[r.Pi].LerpTo(path[r.Pi + 1], (float)r.Seg).Add(V.HALF);
                    return true;
                }
            }
            var spot = t.spot;
            double amp = t.code == "p" ? 0.18 : 0.3;
            var want = spot.Add(V.F(Math.Sin(S.T * 0.9 + p.Id), Math.Cos(S.T * 0.7 + p.Id * 1.3)).Mul(amp));
            if (!(r.Set ?? true))
            {
                float mv = (float)(dh * HUMAN_SPEED * (p.Hp / 200.0 + 0.5));
                var cur = p.Pos;
                if (cur.DistTo(want) > mv)
                {
                    p.Pos = cur.MoveToward(want, mv);
                    return true;
                }
                r.Set = true;
            }
            p.Pos = want;
            return true;
        }

        public bool IsWalking(Person p)
        {
            if (p.St == "move") return true;
            if (p.St != "base" || IsInside(p)) return false;
            return _rt.TryGetValue(p.Id, out var r) && (!r.Arr || !(r.Set ?? true));
        }

        public Routine RoutineState(Person p) => _rt.TryGetValue(p.Id, out var r) ? r : null;
    }
}
