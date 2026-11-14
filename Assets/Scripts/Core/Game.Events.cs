// game.gd: phases (spread -> fall -> hold), hordes, random events and decisions, the
// player action dispatcher (do_action), gifts between colonies, person gifts and merging colonies.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DayZero.Core
{
    public sealed partial class Game
    {
        // ---------------- phases ----------------
        void StepPhase()
        {
            if (S.Phase == "spread")
            {
                int nLand = 0, nDone = 0;
                for (int i = 0; i < SW * SH; i++)
                    if (SectLand[i] > 0.2f && SectProt[i] == 0 && SectWild[i] < 0.5f)
                    {
                        nLand++;
                        if (S.Zinf[i] >= 0.9f) nDone++;
                    }
                if (nLand > 0 && (double)nDone / nLand >= FALL_SHARE)
                {
                    for (int i = 0; i < SW * SH; i++)
                        if (SectLand[i] > 0.2f && SectProt[i] == 0) S.Zinf[i] = Math.Max(S.Zinf[i], 0.9f);
                    S.Phase = "fall";
                    S.FallT = S.T;
                    S.NextHordeT = S.T + 12.0;
                    BuildFlow();
                    AddLog("THE FALL: the whole city belongs to the dead. They're coming for your camps now. Hold out for 10 days.", "death");
                    PlaySfx("alarm", V.F(MainBase()?.Pos ?? new Vector2Int(0, 0)));
                    PhaseChanged?.Invoke(S.Phase);
                    for (int i = 0; i < S.Cols.Count; i++)
                        if (ColAlive(S.Cols[i]))
                            EmitDecision(new JObject
                            {
                                ["id"] = "_fall", ["col"] = i, ["title"] = "The city has fallen",
                                ["text"] = "Every block outside your walls belongs to the dead. From now on they'll come for your camps in hordes. Barricade the streets, arm your guards and hold out for 10 days.",
                                ["choices"] = new JArray { new JObject { ["label"] = "We hold the line", ["effects"] = new JObject() } },
                            });
                }
            }
            else if (S.Phase == "fall")
            {
                if (S.T >= S.NextHordeT) LaunchHorde();
                if (S.T - S.FallT >= HOLD_HOURS)
                    for (int i = 0; i < S.Cols.Count; i++)
                    {
                        var c = S.Cols[i];
                        if (ColAlive(c) && !c.WonShown)
                        {
                            c.WonShown = true; c.Won = true;
                            LogCol = i;
                            AddLog(string.Format("Ten days since the Fall and {0} still standing. The city is yours, what's left of it.", !Mp ? "you're" : c.Name + " is"), "major");
                            LogCol = -1;
                            ColonyOver?.Invoke(i, true);
                            if (S.Cols.Count == 1) GameOver?.Invoke(true);
                        }
                    }
            }
        }

        public double HoldLeft() => S.Phase != "fall" ? HOLD_HOURS : Math.Max(0.0, HOLD_HOURS - (S.T - S.FallT));

        void LaunchHorde()
        {
            double daysSince = (S.T - S.FallT) / 24.0;
            S.NextHordeT = S.T + Rng.RandfRange(18.0, 28.0);
            var targets = AllBases();
            if (targets.Count == 0) return;
            var target = targets[Rng.RandiRange(0, targets.Count - 1)];
            int n = (int)Math.Min(90.0, 14.0 + 7.0 * daysSince);
            var tc = ColonyAt(target.Col);
            string helped = "";
            foreach (var cid in tc?.Pacts ?? new List<string>())
            {
                var cp = CampById(cid);
                if (cp != null && cp.Alive) { n = (int)(n * 0.75); helped = cp.Name; break; }
            }
            double r0 = Math.Max(26.0, Math.Sqrt(EnclosedCount(target) / Math.PI) * 1.4 + 8.0);
            int made = 0;
            for (int t = 0; t < n * 10; t++)
            {
                if (made >= n) break;
                double ang = Rng.Randf() * V.TAU;
                double r = Rng.RandfRange(r0, r0 + 12.0);
                var q = V.I(V.F(target.Pos).Add(V.F(Math.Cos(ang), Math.Sin(ang)).Mul(r)));
                if (!D.InMap(q) || !SpawnOk(Ti(q))) continue;
                if (Flow[Ti(q)] >= 99999) continue;
                if (Zc.Count >= MaxZombies + 120)
                {
                    int j = Rng.RandiRange(0, Zc.Count - 1);
                    Zh[j] = 1;
                }
                else SpawnZombie(Ti(q), 1);
                made++;
            }
            int lc = LogCol;
            LogCol = target.Col;
            AddLog(string.Format("A horde of {0} is moving on {1}!{2}", made, target.Name, helped != "" ? string.Format(" Fighters from {0} thinned it out on the way.", helped) : ""), "death");
            LogCol = lc;
            PlaySfx("alarm", V.F(target.Pos));
        }

        // ---------------- events & decisions ----------------
        public const double EVENT_MIN_H = 48.0;
        public const double EVENT_MAX_H = 72.0;
        public const double EVENT_FIRST_H = 60.0;

        void MaybeEvents()
        {
            if (S.T < C.NextEventT) return;
            C.NextEventT = S.T + Rng.RandfRange(EVENT_MIN_H, EVENT_MAX_H);
            var pool = new List<JObject>();
            foreach (JObject e in D.Events)
            {
                string ph = (string)e["phase"] ?? "any";
                int minDay = e["min_day"] != null ? (int)(double)e["min_day"] : 1;
                if (ph == "fall" && Day() > 12) continue;
                if (ph == "survive" && Day() < minDay) continue;
                if (Day() < minDay) continue;
                int w = e["weight"] != null ? (int)(double)e["weight"] : 1;
                for (int i = 0; i < w; i++) pool.Add(e);
            }
            if (pool.Count == 0) return;
            var ev = (JObject)pool[Rng.RandiRange(0, pool.Count - 1)].DeepClone();
            var crew = AlivePeople();
            string nm = crew.Count > 0 ? crew[Rng.RandiRange(0, crew.Count - 1)].Name : "someone";
            ev["text"] = ((string)ev["text"]).Replace("{name}", nm);
            ev["col"] = CurCol;
            S.NextUid++;
            ev["uid"] = S.NextUid;
            foreach (JObject c in (JArray)ev["choices"]) c["label"] = ((string)c["label"]).Replace("{name}", nm.Split(' ')[0]);
            EmitDecision(ev);
        }

        /// <summary>Resolve a decision: ev as emitted by DecisionAdded, ci = index of the chosen option.
        /// Returns the outcome text.</summary>
        public string ApplyChoice(JObject ev, int ci)
        {
            int prev = CurCol;
            if (ev["col"] != null && (int)ev["col"] >= 0) Use((int)ev["col"]);
            LogCol = CurCol;
            string o = ApplyChoiceInner(ev, ci);
            LogCol = -1;
            Use(prev);
            return o;
        }

        string ApplyChoiceInner(JObject ev, int ci)
        {
            var choices = (JArray)ev["choices"];
            if (ci < 0 || ci >= choices.Count) return "";
            var ch = (JObject)choices[ci];
            var eff = ch["effects"] as JObject ?? new JObject();
            string txt;
            if (eff["chance"] != null)
            {
                ApplyEffects(eff);
                var block = Rng.Randf() < (double)eff["chance"] ? eff["success"] as JObject : eff["fail"] as JObject;
                block ??= new JObject();
                ApplyEffects(block);
                txt = (string)block["text"] ?? "";
            }
            else
            {
                ApplyEffects(eff);
                txt = (string)eff["text"] ?? "";
            }
            EmitChanged();
            return txt;
        }

        void ApplyEffects(JObject e)
        {
            foreach (var k in Data.RESOURCES)
                if (e[k] != null) C.Res[k] = Math.Max(0.0, R(k) + (double)e[k]);
            if (e["materials"] != null) AddRes("materials", (double)e["materials"]);
            if (e["morale"] != null) C.Morale = V.Clamp(C.Morale + (double)e["morale"], 0.0, 100.0);
            var crew = AlivePeople();
            var mb = MainBase();
            if (e["injure"] != null)
                for (int i = 0; i < (int)(double)e["injure"]; i++)
                {
                    if (crew.Count == 0) break;
                    var p = crew[Rng.RandiRange(0, crew.Count - 1)];
                    p.Hp = Math.Max(10.0, p.Hp - 40.0);
                }
            if (e["bite"] != null)
                for (int i = 0; i < (int)(double)e["bite"]; i++)
                {
                    if (crew.Count == 0) break;
                    Bite(crew[Rng.RandiRange(0, crew.Count - 1)]);
                }
            if (e["kill"] != null)
                for (int i = 0; i < (int)(double)e["kill"]; i++)
                {
                    crew = AlivePeople();
                    if (crew.Count <= 1) break;
                    KillPerson(crew[Rng.RandiRange(0, crew.Count - 1)], "didn't make it");
                }
            if (e["add_people"] != null && mb != null)
            {
                int want = (int)(double)e["add_people"];
                int room = BedRoom(want);
                var earr = new List<Person>();
                for (int i = 0; i < room; i++) earr.Add(AddPerson(D.RandomJob(Rng), mb.Id, V.F(mb.Pos)));
                if (room < want) Refuse(want - room, string.Format("{0} had to be turned away. {1}", want - room, NO_BEDS_MSG));
                PlaceArrivals(earr, mb.Id);
            }
            if (e["add_role"] != null && mb != null)
            {
                string r = (string)e["add_role"];
                if (BedsFree() > 0) PlaceArrivals(new List<Person> { AddPerson(MapRole(r), mb.Id, V.F(mb.Pos)) }, mb.Id);
                else Refuse(1, string.Format("They had to be turned away. {0}", NO_BEDS_MSG));
            }
            if (e["spawn_group"] != null)
            {
                var g = SpawnGroup(true);
                if (g != null && e["min"] != null)
                {
                    int mn = (int)(double)e["min"];
                    g.N = Rng.RandiRange(mn, e["max"] != null ? (int)(double)e["max"] : mn);
                }
            }
            if (e["war"] != null) WarChoice((string)e["war"], (string)e["camp"] ?? "");
            if (e["damage_barricade"] != null)
            {
                var keys = S.Fences.Where(kv => kv.Value.Col == CurCol).Select(kv => kv.Key).ToList();
                if (keys.Count > 0)
                {
                    int k = keys[Rng.RandiRange(0, keys.Count - 1)];
                    HitBlocker(k, S.Fences[k].Hp * 0.6);
                }
            }
        }

        // ---------------- player actions ----------------
        /// <summary>Single entry point for player commands (local UI and network). Arguments use C# types:
        /// Vector2Int for tiles, int / long / double for numbers, string, bool, IList for lists.</summary>
        public string DoAction(int ci, string action, IList<object> args, string who = "")
        {
            var c = ColonyAt(ci);
            if (c == null) return "No colony.";
            if (c.Merged >= 0) return "Your colony joined another one.";
            if (c.Lost && action != "gift") return "Your colony has fallen. You can keep watching.";
            if (!c.Started && action != "start_at") return "Set up your main camp first.";
            Use(ci);
            Actor = who;
            LogCol = ci;
            string e;
            try { e = DoActionInner(action, args ?? new object[0]); }
            catch (ArgumentOutOfRangeException) { e = "Bad arguments."; }
            catch (InvalidCastException) { e = "Bad arguments."; }
            catch (FormatException) { e = "Bad arguments."; }
            Actor = "";
            LogCol = -1;
            Use(ViewCol);
            return e;
        }

        Person OwnPerson(int pid)
        {
            var p = PersonById(pid);
            if (p == null || p.Col != CurCol) return null;
            return p;
        }

        static int I(object o) => o is JToken jt ? (int)jt : Convert.ToInt32(o);
        static string Str(object o) => o is JToken jt ? (string)jt : Convert.ToString(o);
        static bool B(object o) => o is JToken jt ? (bool)jt : Convert.ToBoolean(o);
        static double F(object o) => o is JToken jt ? (double)jt : Convert.ToDouble(o);

        static List<Vector2Int> TilesArg(object v)
        {
            var o = new List<Vector2Int>();
            if (!(v is IEnumerable en) || v is string) return o;
            var items = en.Cast<object>().ToList();
            if (items.Count > FENCE_MAX_TILES) return o;
            foreach (var t in items)
            {
                if (t is Vector2Int vi) o.Add(vi);
                else if (t is Vector2 vf) o.Add(V.I(vf));
                else return new List<Vector2Int>();
            }
            return o;
        }

        List<int> EdgesArg(object v)
        {
            var o = new List<int>();
            if (!(v is IEnumerable en) || v is string) return o;
            var items = en.Cast<object>().ToList();
            if (items.Count > SEG_MAX_EDGES) return o;
            foreach (var e in items)
            {
                if (!(e is int || e is long)) return new List<int>();
                int ei = Convert.ToInt32(e);
                if (ei < 0 || ei >= D.RgEdges()) return new List<int>();
                o.Add(ei);
            }
            return o;
        }

        string DoActionInner(string action, IList<object> a)
        {
            switch (action)
            {
                case "start_at": return StartAt((Vector2Int)a[0]);
                case "build_outpost": return BuildOutpost((Vector2Int)a[0]);
                case "build_fence":
                    {
                        var tl = TilesArg(a.Count > 0 ? a[0] : null);
                        if (tl.Count == 0) return "Draw a line first.";
                        return BuildFence(tl, a.Count > 1 ? I(a[1]) : 0);
                    }
                case "build_fence_segs":
                    {
                        var el = EdgesArg(a.Count > 0 ? a[0] : null);
                        if (el.Count == 0) return "Tap two fence posts first.";
                        return BuildFenceSegs(el, a.Count > 1 ? I(a[1]) : 0);
                    }
                case "remove_fence_seg":
                    if (a.Count == 0) return "No fence there.";
                    return RemoveFenceSeg(I(a[0]));
                case "remove_fence":
                    {
                        var tl2 = TilesArg(a.Count > 0 ? a[0] : null);
                        if (tl2.Count == 0) return "No fence there.";
                        return RemoveFence(tl2);
                    }
                case "place_tower":
                    if (a.Count < 2 || !(a[0] is Vector2Int)) return "Bad tower spot.";
                    return PlaceTower((Vector2Int)a[0], I(a[1]));
                case "upgrade_tower": return UpgradeTower(I(a[0]));
                case "remove_tower": return RemoveTower(I(a[0]));
                case "upgrade_base":
                    {
                        var bb = BaseById(I(a[0]));
                        if (bb == null) return BaseAny(I(a[0])) != null ? "That camp isn't yours." : "No such camp.";
                        return UpgradeBase(bb);
                    }
                case "form_squad":
                    {
                        if (a.Count < 1 || !(a[0] is IEnumerable) || a[0] is string) return "Pick the squad members.";
                        int fb = a.Count > 2 ? I(a[2]) : -1;
                        if (fb >= 0 && BaseById(fb) == null) return "That camp isn't yours.";
                        return FormSquad(((IEnumerable)a[0]).Cast<object>().Select(I).ToList(), a.Count > 1 ? Str(a[1]) : "", fb);
                    }
                case "disband_squad": return DisbandSquad(I(a[0]));
                case "send_squad":
                    if (a.Count < 2 || !(a[1] is Vector2Int)) return "Pick a target on the map.";
                    return SendSquad(I(a[0]), (Vector2Int)a[1]);
                case "recall_squad": return RecallSquad(I(a[0]));
                case "send_squad_map":
                    if (a.Count < 2) return "Pick a map.";
                    return SendSquadMap(I(a[0]), Str(a[1]));
                case "drone_scout":
                    if (!(a[0] is Vector2Int)) return "Pick a spot.";
                    return DroneScout((Vector2Int)a[0]);
                case "heli_rescue": return HeliRescue(I(a[0]));
                case "air_strike":
                    if (!(a[0] is Vector2Int)) return "Pick a spot.";
                    return AirStrike((Vector2Int)a[0]);
                case "camp_pact": return CampPact(Str(a[0]));
                case "gift_person": return GiftPerson(I(a[0]), I(a[1]));
                case "do_upgrade":
                    {
                        var b = BaseById(I(a[0]));
                        if (b == null) return BaseAny(I(a[0])) != null ? "That camp isn't yours." : "No such camp.";
                        if (!Data.UPGRADES.ContainsKey(Str(a[1]))) return "Unknown facility.";
                        return DoUpgrade(b, Str(a[1]));
                    }
                case "rebuild_vehicle":
                    {
                        var b2 = BaseById(I(a[0]));
                        if (b2 == null) return "That camp isn't yours.";
                        return RebuildVehicle(b2);
                    }
                case "repair_vehicle": return RepairVehicle(I(a[0]));
                case "refuel_vehicle": return RefuelVehicle(I(a[0]));
                case "assign_vehicle": return AssignVehicle(I(a[0]), I(a[1]));
                case "tow_wreck": return TowWreck(I(a[0]), I(a[1]));
                case "siphon_wreck": return SiphonWreck(I(a[0]));
                case "set_job":
                    {
                        var p = OwnPerson(I(a[0]));
                        if (p == null) return "That survivor isn't in your colony.";
                        if (!Data.JOBS.Contains(Str(a[1]))) return "Unknown job.";
                        string jb = JobBlock(p, Str(a[1]));
                        if (jb != "") return jb;
                        SetJob(p, Str(a[1]));
                        return "";
                    }
                case "move_to_base":
                    {
                        var p2 = OwnPerson(I(a[0]));
                        if (p2 == null) return "That survivor isn't in your colony.";
                        if (BaseById(I(a[1])) == null) return "That camp isn't yours.";
                        if (p2.Sq > 0 && p2.Base != I(a[1])) return string.Format("{0} is in a squad at {1}. Take them out of it first.", p2.Name, BaseById(p2.Base)?.Name ?? "their camp");
                        MoveToBase(p2, I(a[1]));
                        return "";
                    }
                case "send_rescue":
                    {
                        int rb = a.Count > 1 ? I(a[1]) : -1;
                        if (rb >= 0 && BaseById(rb) == null) return "That camp isn't yours.";
                        return SendRescue(I(a[0]), false, rb);
                    }
                case "send_raid":
                    {
                        int rb2 = a.Count > 1 ? I(a[1]) : -1;
                        if (rb2 >= 0 && BaseById(rb2) == null) return "That camp isn't yours.";
                        return SendRaid(V.Clampi(I(a[0]), 0, D.Lots.Count - 1), rb2);
                    }
                case "start_research":
                    if (D.Research[Str(a[0])] == null) return "Unknown research.";
                    return StartResearch(Str(a[0]));
                case "craft_weapon":
                case "craft":
                    if (a.Count < 1 || Data.GearDefOf(Str(a[0])) == null) return "Unknown item.";
                    return Craft(Str(a[0]));
                case "craft_cancel": return CraftCancel(a.Count > 0 ? I(a[0]) : -1);
                case "equip":
                    if (a.Count < 3) return "Pick a survivor, a slot and an item.";
                    return EquipSlot(I(a[0]), Str(a[1]), Str(a[2]));
                case "auto_equip": return AutoEquip();
                case "craft_ammo": return CraftAmmo();
                case "camp_trade": return CampTrade(Str(a[0]), Str(a[1]), Str(a[2]));
                case "set_trade":
                    if (a.Count < 3) return "Bad trade order.";
                    return SetTrade(Str(a[0]), Str(a[1]), I(a[2]));
                case "camp_gift": return CampGift(Str(a[0]));
                case "camp_recruit": return CampRecruit(Str(a[0]));
                case "set_auto":
                    {
                        if (a.Count < 2) return "Bad setting.";
                        int ab = a.Count > 2 ? I(a[2]) : -1;
                        if (ab >= 0 && BaseById(ab) == null) return "That camp isn't yours.";
                        object v = a[1] is JValue jv ? jv.Value : a[1];
                        return SetAuto(Str(a[0]), v, ab);
                    }
                case "set_hold":
                    {
                        var ph = OwnPerson(I(a[0]));
                        if (ph == null) return "That survivor isn't in your colony.";
                        ph.Hold = a.Count > 1 && B(a[1]);
                        EmitChanged();
                        return "";
                    }
                case "send_squad_scav":
                    if (a.Count < 2 || !(a[1] is Vector2Int)) return "Pick a target on the map.";
                    return SendSquadScav(I(a[0]), (Vector2Int)a[1]);
                case "squad_attack":
                    if (a.Count < 2) return "Pick a squad and a camp.";
                    return SquadAttack(I(a[0]), Str(a[1]));
                case "camp_parley": return CampParley(Str(a[0]));
                case "camp_tribute": return CampTribute(Str(a[0]));
                case "gift": return Gift(I(a[0]), Str(a[1]), F(a[2]));
                case "gut":
                    if (a.Count == 0) return "Tap a building.";
                    return StartGut(V.Clampi(I(a[0]), -1, D.Lots.Count - 1));
                case "convert_building":
                    if (a.Count < 2) return "Pick a building and a use.";
                    return ConvertBuilding(V.Clampi(I(a[0]), -1, D.Lots.Count - 1), Str(a[1]));
                case "set_guard":
                    {
                        var pg = OwnPerson(I(a[0]));
                        if (pg == null) return "That survivor isn't in your colony.";
                        return SetGuard(pg, a.Count > 1 && B(a[1]));
                    }
                case "place_gate": return PlaceGate(I(a[0]));
                case "remove_gate": return RemoveGate(I(a[0]));
                case "place_trap":
                    if (a.Count < 2 || !(a[0] is Vector2Int)) return "Bad trap spot.";
                    return PlaceTrap((Vector2Int)a[0], I(a[1]));
                case "place_traps":
                    {
                        var tl3 = TilesArg(a.Count > 0 ? a[0] : null);
                        if (tl3.Count == 0) return "Mark a trap line first.";
                        return PlaceTraps(tl3, a.Count > 1 ? I(a[1]) : 0);
                    }
                case "remove_trap": return RemoveTrap(I(a[0]));
                case "upgrade_trap": return UpgradeTrap(I(a[0]));
                case "war_enlist":
                    if (a.Count < 1) return "Pick a survivor.";
                    return WarEnlist(I(a[0]), a.Count <= 1 || B(a[1]));
                case "war_enlist_all": return WarEnlistAll();
                case "war_clear": return WarClear();
                case "war_declare": return WarDeclare();
                case "craft_melee": return CraftWarGear("melee");
                case "craft_vest": return CraftWarGear("vest");
            }
            return "Unknown action.";
        }

        public string Gift(int toCi, string res, double amount)
        {
            var t = ColonyAt(toCi);
            if (t == null || toCi == CurCol) return "Pick another colony.";
            if (!ColAlive(t)) return string.Format("{0} can't receive anything.", t.Name ?? "They");
            if (!Data.RESOURCES.Contains(res)) return "Unknown resource.";
            amount = Math.Floor(amount);
            if (amount < 1.0) return "Send at least 1.";
            if (R(res) < amount) return string.Format("You only have {0} {1}.", (int)R(res), Lower(ResLabel(res)));
            C.Res[res] = R(res) - amount;
            double got = Math.Floor(amount * (1.0 + Rx("gift_bonus")));
            t.Res[res] = t.Res.Get(res, 0.0) + got;
            AddLog(string.Format("Sent {0} {1} to {2}{3}.", (int)amount, Lower(ResLabel(res)), t.Name, got > amount ? string.Format(" ({0} arrived thanks to gift customs)", (int)got) : ""), "good");
            int lc = LogCol;
            LogCol = toCi;
            AddLog(string.Format("{0} sent you {1} {2}.", C.Name, (int)got, Lower(ResLabel(res))), "good");
            LogCol = lc;
            EmitChanged();
            return "";
        }

        public string GiftPerson(int pid, int toCi)
        {
            if (!HasUnlock("gift_person")) return string.Format("Research {0} first.", UnlockSourceName("gift_person"));
            var p = OwnPerson(pid);
            if (p == null) return "That survivor isn't in your colony.";
            if (p.St != "base") return string.Format("{0} is out right now.", p.Name);
            if (p.Sq > 0) return string.Format("Take {0} out of their squad first.", p.Name);
            if (IsMinor(p)) return string.Format("{0} is too young to leave home.", p.Name);
            if (p.Partner != 0 && PersonById(p.Partner) != null) return string.Format("{0} won't leave their partner.", p.Name);
            var t = ColonyAt(toCi);
            if (t == null || toCi == CurCol || !ColAlive(t)) return "Pick another living colony.";
            if (AlivePeople().Count <= 2) return "You can't spare anyone.";
            var tb = t.Bases.LastOrDefault(b => b.Main);
            if (tb == null) return "They have no camp.";
            if (BedsFreeOf(toCi) <= 0) return string.Format("{0} has no free bed for {1} anywhere.", t.Name, p.Name);
            _claimed.Remove(p.Lot);
            C.People.Remove(p);
            p.Col = toCi; p.Base = tb.Id;
            p.Trav = null;
            t.People.Add(p);
            string fromName = C.Name;
            AddLog(string.Format("{0} ({1}) left to join {2}.", p.Name, p.Job, t.Name), "info");
            int lc = LogCol;
            LogCol = toCi;
            AddLog(string.Format("{0} ({1}) arrived from {2} to join you.", p.Name, p.Job, fromName), "good");
            LogCol = lc;
            int prev = CurCol;
            Use(toCi);
            Go(p, tb.Pos, "home");
            _rtNeed[toCi] = true;
            int lc2 = LogCol;
            LogCol = toCi;
            PlaceArrivals(new List<Person> { p }, tb.Id);
            LogCol = lc2;
            Equip();
            Use(prev);
            Equip();
            EmitChanged();
            return "";
        }

        static readonly string[] OUTPOST_LETTERS = { "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L" };

        public string MergeColonies(int fromCi, int intoCi)
        {
            var f = ColonyAt(fromCi); var t = ColonyAt(intoCi);
            if (f == null || t == null || fromCi == intoCi) return "Can't merge those.";
            if (!t.Started || t.Lost || t.Merged >= 0) return "That colony can't take anyone in.";
            foreach (var b in f.Bases)
            {
                b.Col = intoCi;
                if (b.Main) { b.Main = false; b.Up["radio"] = 0; }
                int n = t.Bases.Count(bb => !bb.Main);
                b.Name = "Outpost " + OUTPOST_LETTERS[Math.Min(n, OUTPOST_LETTERS.Length - 1)];
                t.Bases.Add(b);
            }
            foreach (var p in f.People) { p.Col = intoCi; t.People.Add(p); }
            foreach (var k in Data.RESOURCES) t.Res[k] = t.Res.Get(k, 0.0) + f.Res.Get(k, 0.0);
            foreach (var kv in f.Parts) t.Parts[kv.Key] = t.Parts.Get(kv.Key, 0) + kv.Value;
            foreach (var kv in f.Weapons) t.Weapons[kv.Key] = t.Weapons.Get(kv.Key, 0) + kv.Value;
            foreach (var kv in f.Stats) t.Stats[kv.Key] = t.Stats.Get(kv.Key, 0) + kv.Value;
            foreach (var r in f.Research.Done) if (!t.Research.Done.Contains(r)) t.Research.Done.Add(r);
            foreach (var kv in S.Fences) if (kv.Value.Col == fromCi) kv.Value.Col = intoCi;
            foreach (var kv in S.Towers) if (kv.Value.Col == fromCi) kv.Value.Col = intoCi;
            foreach (var kv in S.Fsegs) if (kv.Value.Col == fromCi) kv.Value.Col = intoCi;
            foreach (var kv in S.Conv) if (kv.Value.Col == fromCi) kv.Value.Col = intoCi;
            foreach (var k in S.Gates.KeyList()) if (S.Gates[k] == fromCi) S.Gates[k] = intoCi;
            foreach (var kv in S.Traps) if (kv.Value.Col == fromCi) kv.Value.Col = intoCi;
            S.ConvVer++;
            var ctrl = S.Ctrl;
            for (int i = 0; i < ctrl.Length; i++) if (ctrl[i] == fromCi + 1) ctrl[i] = (byte)(intoCi + 1);
            t.Squads.AddRange(f.Squads);
            foreach (var cp in f.Pacts) if (!t.Pacts.Contains(cp)) t.Pacts.Add(cp);
            f.Squads = new List<Squad>();
            f.Bases = new List<Base>(); f.People = new List<Person>();
            f.Merged = intoCi;
            foreach (var kv in S.Players) if (kv.Value.Col == fromCi) kv.Value.Col = intoCi;
            if (ViewCol == fromCi) ViewCol = intoCi;
            CurCol = -1;
            _fxCache.Clear();
            Use(ViewCol);
            S.FenceVer++;
            RebuildSafe();
            _flowDirty = true;
            LogCol = intoCi;
            _insideDirty = true; _rtNeed.Clear();
            AddLog(string.Format("{0} joined {1}. One colony now: survivors, camps and supplies are shared.", f.Name, t.Name), "major");
            LogCol = -1;
            EmitChanged();
            return "";
        }
    }
}
