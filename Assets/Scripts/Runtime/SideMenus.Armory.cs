// ARMORY and GARAGE side menus plus the wreck sheet, ported from the Godot original (main.gd _show_armory,
// _render_armory, _arm_craft_rows, _arm_loadout_rows, _arm_pick, _arm_stat, _armory_rows, _show_garage,
// _render_garage, _garage_rows, _sheet_wreck). Same display-list approach as RESEARCH / TRADE (SideMenus.cs):
// GameUI draws it with IMGUI, the offline preview paints it to PNG. Every button sends the original's player
// action (craft, craft_cancel, equip, auto_equip, craft_ammo, repair_vehicle, refuel_vehicle, assign_vehicle,
// rebuild_vehicle, siphon_wreck, tow_wreck) through Bootstrap.Act -> Game.DoAction; no rules live here.
// The original shows "10 AMMO" and REBUILD on a camp's FACILITIES sheet (Armory / Garage upgrade rows); the
// port has no camp facilities sheet yet, so CampArmoryRow / CampGarageRow are public for it and are also shown
// inside ARMORY / GARAGE for the camps that qualify, so the actions stay reachable.
using System;
using System.Collections.Generic;
using System.Linq;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    /// <summary>ARMORY / GARAGE screen state (main.gd _arm_tab, the EQUIP pick modal, scroll position).</summary>
    public sealed class ArmoryMenuState
    {
        public string Tab = "loadouts";   // loadouts | workshop | assemble
        public int Page, GPage;
        public int PickPid = -1;          // EQUIP · name modal (main.gd _arm_pick)
        public string PickSlot = "";
    }

    public static partial class SideMenus
    {
        static readonly Color CARD = new Color(0.11f, 0.12f, 0.12f);
        static readonly Color CARD_EDGE = new Color(0.22f, 0.24f, 0.20f);

        /// <summary>main.gd _short_cost: "4 metal 2 plast" (RES_SHORT names, space separated).</summary>
        public static string ShortCost(IEnumerable<KeyValuePair<string, double>> c)
        {
            if (c == null) return "";
            return string.Join(" ", c.Select(kv => string.Format("{0} {1}", (int)Math.Ceiling(kv.Value - 0.001),
                Lower(Data.RES_SHORT.TryGetValue(kv.Key, out var s) ? s : Data.RES_LABEL.TryGetValue(kv.Key, out var l) ? l : kv.Key))));
        }

        static string PartsText(Dictionary<string, int> parts) =>
            string.Join(", ", parts.Select(kv => string.Format("{0} {1}", kv.Value, Lower(Data.GPARTS[kv.Key].Name))));

        /// <summary>main.gd _arm_stat.</summary>
        public static string ArmStat(string id)
        {
            if (Data.WEAPONS.TryGetValue(id, out var w))
                return string.Format("dmg {0} · range {1:0.0} · acc {2}% · noise {3} · ammo {4:0.0}{5}", (int)(w.Dmg ?? 0), w.Range, (int)((w.Hit ?? 0) * 100.0), w.Noise ?? 0, w.Ammo,
                    w.Multi > 1 ? string.Format(" · hits {0}", w.Multi) : "");
            if (Data.ARMOR.TryGetValue(id, out var a)) return string.Format("armour {0}% ({1})", (int)(a.Armor * 100.0), a.Slot == "vest" ? "vest" : "helmet");
            return "";
        }

        /// <summary>game.gd _guard_rank (display order only, same formula as Game.GuardRank).</summary>
        static int GuardRankUi(Game g, Person p)
        {
            if (g.IsMinor(p)) return 99;
            int r = p.Sq > 0 ? 0 : 4;
            r += p.Job == "Soldier" ? 0 : p.Job == "Cop" ? 1 : p.Job == "Scavenger" ? 3 : (p.Guard ? 2 : 6);
            return r;
        }

        static void ArmAct(MenuCtx c, string act, params object[] args)
        {
            string e = c.Act(act, args);
            if (e != "") c.Toast(e);
        }

        // ---------------- ARMORY (main.gd _show_armory / _render_armory) ----------------
        static void Armory(MenuCtx c)
        {
            var g = c.G; var a = c.St.Arm;
            a.PickPid = -1;   // v0.17: the EQUIP modal is a wheel on each loadout slot
            var L = Frame(c, "ARMORY");
            var q = g.C.Craft;
            string head = g.HasArmory() ? string.Format("Armory level {0}", g.ArmoryLevel()) : "No Armory yet: gut a building and convert it to an Armory to change loadouts and assemble guns";
            head += " · " + (g.HasWorkshop() ? "Workshop ready" : "no Workshop (parts need one)");
            if (q.Count > 0)
                head += string.Format(" · working on: {0} ({1}%){2}", Data.GearDefOf(q[0].Id).Name, (int)(g.CraftProgress() * 100.0), q.Count > 1 ? string.Format(", {0} more queued", q.Count - 1) : "");
            // camp sheet "10 AMMO" (main.gd _armory_rows): a camp with the Armory facility
            bool ammo = g.BestArmory() >= 1;
            var top = new UiLayout(L.Top(BTN_H), 12f);
            if (ammo) c.Btn(top.Right(230f), "10 AMMO", UiRole.Primary, () =>
            {
                string e = c.Act("craft_ammo", new object[0]);
                c.Toast(e != "" ? e : "+10 ammo");
            }, true, false, 19, ShortCost(Game.AMMO_CRAFT));
            float hh = Math.Min(BTN_H, c.H_(head, 15, false, top.Rest.width));
            c.Para(UiLayout.Center(top.Rest, top.Rest.width, hh), head, 15, INK2);
            var tabs = new[] { ("loadouts", "LOADOUTS"), ("workshop", "WORKSHOP"), ("assemble", "ASSEMBLE") };
            var tr = UiLayout.Even(L.Top(BTN_H), 3, 10f);
            for (int i = 0; i < tabs.Length; i++)
            {
                string id = tabs[i].Item1;
                c.Btn(tr[i], tabs[i].Item2, a.Tab == id ? UiRole.Accent : UiRole.Primary, () => { a.Tab = id; a.Page = 0; }, true, a.Tab == id, 20);
            }
            if (a.Tab == "workshop") ArmCraftRows(c, L, Data.GPART_ORDER, true);
            else if (a.Tab == "assemble") ArmCraftRows(c, L, Data.WEAPON_ORDER.Concat(Data.ARMOR_ORDER).ToArray(), false);
            else ArmLoadoutRows(c, L);
        }

        /// <summary>main.gd _render_armory QUEUE block: one chip per job with CANCEL (craft_cancel index).</summary>
        static float ArmQueue(MenuCtx c, float x, float iw, float y)
        {
            var q = c.G.C.Craft;
            if (q.Count == 0) return y;
            c.Header(new Rect(x, y, iw, 40f), "QUEUE", 20);
            y += 48f;
            const int COLS = 3; const float QH = 64f, GAP = 8f;
            for (int i = 0; i < q.Count; i++)
            {
                var r = UiLayout.Cell(new Rect(x, y, iw, 0), COLS, i, QH, GAP);
                Card(c, r, i, i == 0 ? AMBER : CARD_EDGE, i == 0);
                var cc = UiLayout.Columns(UiLayout.Inset(r, 16f, 4f, 6f, 4f), 8f, 1f, -140f);
                var ln = UiLayout.Rows(cc[0], 0f, 1.2f, 1f);
                c.Fit(i == 0 ? ln[0] : cc[0], Data.GearDefOf(q[i].Id).Name, 17, INK, true);
                if (i == 0) c.Fit(ln[1], "working", 14, AMBER, false);
                int ix = i;
                c.Btn(UiLayout.Center(cc[1], cc[1].width, 52f), "CANCEL", UiRole.Danger, () => ArmAct(c, "craft_cancel", ix), true, false, 17);
            }
            return y + ((q.Count + COLS - 1) / COLS) * (QH + GAP);
        }

        static float QueueH(Game g) => g.C.Craft.Count == 0 ? 0f : 48f + ((g.C.Craft.Count + 2) / 3) * 72f;

        /// <summary>main.gd _arm_craft_rows: WORKSHOP parts (CRAFT) or ASSEMBLE guns and armour (BUILD L#), two cards a
        /// row in one scroll list with the queue under them.</summary>
        static void ArmCraftRows(MenuCtx c, UiLayout L, string[] ids, bool parts)
        {
            var g = c.G;
            Footer(c, L, parts ? "Parts: CRAFT queues one job (up to 8). CANCEL refunds it." : "Armory level and Workshop parts unlock each build. CANCEL refunds it.");
            var view = L.Rest;
            float iw = view.width - 12f;
            string intro = parts ? "Workshop parts are made from material items, one job at a time (more Workshops work faster)."
                : "Guns, vests and helmets are assembled at the Armory from Workshop parts (scavenged gun stores, precincts and checkpoints turn up parts and finished gear too).";
            float ih = c.H_(intro, 15, false, iw);
            const float GAP = 8f, BW = 160f, PAD = 10f;
            float cw = (iw - GAP) / 2f, tw = cw - BW - 36f;
            var rows = new List<(string id, string title, string txt, string err, float th, float eh)>();
            foreach (var id in ids)
            {
                var d = Data.GearDefOf(id);
                string need = ShortCost(d.Cost);
                if (d.Parts != null) need = PartsText(d.Parts) + (need != "" ? ", " + need : "");
                string title = string.Format("{0}  ·  have {1}{2}", d.Name, g.GearTotal(id), parts ? "" : string.Format(" ({0} free)", g.GearFree(id)));
                string txt = string.Format("{0} · {1:0}h", need, (d.H ?? 3.0) / g.CraftSpeed(parts ? "part" : "gun"));
                if (!parts) txt += "\n" + ArmStat(id);
                string err = g.CraftError(id);
                rows.Add((id, title, txt, err, c.H_(txt, 15, false, tw), err == "" ? 0f : c.H_(err, 14, false, cw - 32f)));
            }
            var hs = new List<float>();
            for (int i = 0; i < rows.Count; i += 2)
            {
                float h0 = CraftCardH(rows[i].th, rows[i].eh), h1 = i + 1 < rows.Count ? CraftCardH(rows[i + 1].th, rows[i + 1].eh) : 0f;
                hs.Add(Math.Max(h0, h1));
            }
            float qh = QueueH(g);
            c.Scroll("arm_" + (parts ? "workshop" : "assemble"), view, ih + 10f + Total(hs, GAP) + (qh > 0f ? GAP + qh : 0f));
            c.Para(new Rect(0, 0, iw, ih), intro, 15, INK2);
            float y = ih + 10f;
            for (int r = 0; r < hs.Count; r++)
            {
                for (int k = 0; k < 2; k++)
                {
                    int i = r * 2 + k;
                    if (i >= rows.Count) break;
                    var row = rows[i];
                    var card = new Rect(k * (cw + GAP), y, cw, hs[r]);
                    Card(c, card, i, row.err == "" ? new Color(0.62f, 0.66f, 0.36f) : CARD_EDGE);
                    c.Fit(new Rect(card.x + 18f, card.y + PAD, tw, 26f), row.title, 18, INK, true);
                    c.Para(new Rect(card.x + 18f, card.y + PAD + 28f, tw, row.th), row.txt, 15, INK2);
                    var d = Data.GearDefOf(row.id); string id = row.id;
                    c.Btn(new Rect(card.xMax - BW - 10f, card.y + PAD, BW, BTN_H), parts ? "CRAFT" : string.Format("BUILD L{0}", d.Armory ?? 1), row.err == "" ? UiRole.Accent : UiRole.Neutral,
                        () => ArmAct(c, "craft", id), true, false, 19);
                    if (row.err != "") c.Para(new Rect(card.x + 18f, card.y + PAD + Math.Max(28f + row.th, BTN_H) + 4f, cw - 32f, row.eh), row.err, 14, RED);
                }
                y += hs[r] + GAP;
            }
            if (qh > 0f) ArmQueue(c, 0f, iw, y);
            c.EndScroll();
        }

        static float CraftCardH(float th, float eh) => 10f + Math.Max(28f + th, BTN_H) + (eh > 0f ? 4f + eh : 0f) + 12f;

        /// <summary>The items a loadout slot can take: nothing, what they wear, and every free piece for the slot.</summary>
        static List<string> SlotOptions(Game g, Person pp, string slot)
        {
            string cur = pp.Slot(slot) ?? "";
            var o = new List<string> { "" };
            foreach (var id in slot == "wpn" ? Data.WEAPON_ORDER : Data.ARMOR_ORDER)
                if (Game.SlotOf(id) == slot && (id == cur || g.GearFree(id) > 0)) o.Add(id);
            if (cur != "" && !o.Contains(cur)) o.Add(cur);
            return o;
        }

        /// <summary>main.gd _arm_loadout_rows: rack, AUTO-EQUIP, one row per adult with a wheel per slot (gun / vest /
        /// helmet). Landing a wheel on an item sends equip (the original's EQUIP modal; "no gun" = TAKE OFF).</summary>
        static void ArmLoadoutRows(MenuCtx c, UiLayout L)
        {
            var g = c.G;
            bool can = g.HasArmory();
            var ppl = g.C.People.Where(pp => pp.St != "dead" && !g.IsMinor(pp)).OrderBy(pp => GuardRankUi(g, pp)).ToList();   // stable, as sort_custom on _guard_rank
            Footer(c, L, string.Format("{0} adults · drag a slot wheel to equip or take off", ppl.Count));
            var rack = new List<string>();
            foreach (var id in Data.WEAPON_ORDER.Concat(Data.ARMOR_ORDER))
                if (g.GearFree(id) > 0) rack.Add(string.Format("{0} {1}", g.GearFree(id), Lower(Data.GearDefOf(id).Name)));
            string rt = "On the rack: " + (rack.Count > 0 ? string.Join(", ", rack) : "nothing spare");
            var top = new UiLayout(L.Top(BTN_H), 12f);
            c.Btn(top.Right(220f), "AUTO-EQUIP", can ? UiRole.Accent : UiRole.Neutral, () => ArmAct(c, "auto_equip"), true, false, 20);
            float rh = Math.Min(BTN_H, c.H_(rt, 16, false, top.Rest.width));
            c.Para(UiLayout.Center(top.Rest, top.Rest.width, rh), rt, 16, AMBER);
            if (!can)
            {
                const string nt = "Loadouts can only be changed with an Armory in the colony. Until then fighters pick up whatever is free.";
                c.Fit(L.Top(26f), nt, 16, RED, false);
            }
            var view = L.Rest;
            float iw = view.width - 12f;
            const float RH = 104f, GAP = 8f;
            float qh = QueueH(g);
            c.Scroll("arm_loadouts", view, Math.Max(0f, ppl.Count * (RH + GAP) - GAP) + (qh > 0f ? GAP + qh : 0f));
            float y = 0f; int k = 0;
            float sw = Math.Min(250f, (iw - 300f) / 3f - 10f);
            foreach (var pp in ppl)
            {
                var card = new Rect(0, y, iw, RH);
                Card(c, card, k++, pp.Sq > 0 || pp.Job == "Soldier" || pp.Job == "Cop" || pp.Guard ? AMBER : CARD_EDGE);
                var cols = new UiLayout(UiLayout.Inset(card, 18f, 8f, 10f, 8f), 10f);
                var wheels = new Rect[3];
                for (int s = 2; s >= 0; s--) wheels[s] = cols.Right(sw);
                var ln = UiLayout.Rows(UiLayout.Center(cols.Rest, cols.Rest.width, 62f), 4f, 1.2f, 1f);
                c.Fit(ln[0], pp.Name, 20, INK, true);
                c.Fit(ln[1], string.Format("{0} · armour {1}%", pp.Job, (int)(g.ArmorOf(pp) * 100.0)), 16, INK2, false);
                for (int s = 0; s < Game.SLOTS.Length; s++)
                {
                    string slot = Game.SLOTS[s];
                    var opts = SlotOptions(g, pp, slot);
                    string none = slot == "wpn" ? "no gun" : slot == "vest" ? "no vest" : "no helmet";
                    var labels = opts.Select(id => id == "" ? none : Data.GearDefOf(id)?.Name ?? id).ToList();
                    int pid = pp.Id;
                    c.Wheel("arm_" + pid + "_" + slot, wheels[s], labels, Math.Max(0, opts.IndexOf(pp.Slot(slot) ?? "")), i => ArmAct(c, "equip", pid, slot, opts[i]), false, can, 3, 18);
                }
                y += RH + GAP;
            }
            if (qh > 0f) ArmQueue(c, 0f, iw, y);
            c.EndScroll();
        }

        // ---------------- GARAGE (main.gd _show_garage / _render_garage) ----------------
        static string VehState(Game g, Vehicle v)
        {
            switch (v.St)
            {
                case "ok": return Game.VehRuns(v) ? "parked" : "parked, not running";
                case "out": return "out on the road";
                case "repair": return string.Format("in repair ({0:0}h left)", Math.Max(0.0, v.End - g.S.T));
                case "wreck": return "wreck, needs repair";
            }
            return v.St;
        }

        static void Garage(MenuCtx c)
        {
            var g = c.G;
            var L = Frame(c, "GARAGE");
            Footer(c, L, string.Format("{0} vehicle{1} · drag ASSIGN to give one to a squad", g.C.Vehs.Count, g.C.Vehs.Count == 1 ? "" : "s"));
            var caps = g.C.Bases.Where(g.HasGarage).Select(b => string.Format("{0} {1}/{2}", b.Name, g.GarageUsed(b), g.GarageCap(b))).ToList();
            string l1 = caps.Count > 0 ? "Garages: " + string.Join(", ", caps) : "No Garage yet: gut a building and convert it to a Garage (2 vehicles each, +1 per Garage upgrade).";
            c.Para(L.Top(c.H_(l1, 15, false, L.Rest.width)), l1, 15, INK2);
            string l2 = string.Format("Fuel in stores: {0} · Parts: {1}", (int)g.C.Res.Get("fuel", 0.0),
                string.Join(", ", Data.VEHICLE_PARTS.Select(k => string.Format("{0} {1}", Data.PART_LABEL[k], g.C.Parts.Get(k, 0)))));
            c.Para(L.Top(c.H_(l2, 16, false, L.Rest.width)), l2, 16, AMBER);

            // rows: the camp Garage rows (REBUILD, camp sheet in the original), then one card per vehicle
            var view = L.Rest;
            float iw = view.width - 12f;
            var rows = new List<(float h, Action<float> draw)>();
            foreach (var b in g.C.Bases.Where(g.HasGarage).ToList())
            {
                var bb = b;
                rows.Add((CampGarageRowH(c, bb, iw), yy => CampGarageRow(c, bb, 0f, iw, yy)));
            }
            if (g.C.Vehs.Count == 0)
            {
                const string nv = "No vehicles. Tap a wreck on the road and tow it in with a squad, or rebuild one from parts on a camp's Garage.";
                float nh = c.H_(nv, 16, false, iw);
                rows.Add((nh, yy => c.Para(new Rect(0, yy, iw, nh), nv, 16, INK2)));
            }
            foreach (var v in g.C.Vehs.ToList())
            {
                var vv = v;
                var (t1, t2) = VehTexts(g, vv);
                float th = 28f, dh = c.H_(t2, 15, false, iw - 36f);
                float ch = 10f + th + 4f + dh + 8f + BTN_H + 12f;
                rows.Add((ch, yy => VehCard(c, vv, 0f, iw, yy, t1, t2, th, dh, ch)));
            }
            c.Scroll("garage", view, Total(rows.Select(r => r.h).ToList(), ROW_GAP));
            float y = 0f;
            foreach (var r in rows) { r.draw(y); y += r.h + ROW_GAP; }
            c.EndScroll();
        }

        static (string, string) VehTexts(Game g, Vehicle v)
        {
            var d = Data.VehDef(v.Type);
            var q = g.SquadById(v.Sq);
            var nd = v.Need.Select(n => Lower(Data.PART_LABEL.TryGetValue(n, out var l) ? l : n)).ToList();
            string t1 = string.Format("{0} · {1} · {2}", d.Name, g.BaseById(v.Base)?.Name ?? "?", VehState(g, v));
            string t2 = string.Format("Condition {0}% · fuel {1}/{2} · {3} seats · cargo {4} · speed x{5:0.0} · armour {6}% · uses {7:0.00} fuel/tile{8}\nSquad: {9}",
                (int)(v.Hp * 100.0), (int)v.Fuel, d.Tank, d.Seats, d.Cargo, d.Speed, (int)(d.Armor * 100.0), d.Use,
                nd.Count > 0 ? " · missing " + string.Join(", ", nd) : "", q != null ? q.Name : "none (scavengers / task forces may take it)");
            t2 += "\n" + VehNext(g, v);
            return (t1, t2);
        }

        /// <summary>v0.14.0a: the next step for a vehicle (towed wreck -> repair -> refuel -> assign / drive).</summary>
        static string VehNext(Game g, Vehicle v)
        {
            if (v.St == "out") return "On the road.";
            if (v.St == "repair") return "Mechanics are on it. It runs when the repair is done.";
            if (v.St == "wreck" || !Game.VehRuns(v)) return "Next: REPAIR it here (parts and materials).";
            if (v.Fuel < 1.0) return "Next: REFUEL it from the stores.";
            if (v.Sq < 0) return "Ready to drive. ASSIGN it to a squad, or scavengers and task forces from this camp take it.";
            return "Ready to drive with its squad.";
        }

        static void VehCard(MenuCtx c, Vehicle v, float x, float iw, float y, string t1, string t2, float th, float dh, float ch)
        {
            var g = c.G;
            var card = new Rect(x, y, iw, ch);
            Card(c, card, v.Id, Game.VehRuns(v) ? GREEN : v.St == "repair" ? AMBER : CARD_EDGE);
            float tx = x + 18f, tw = iw - 36f, ty = y + 10f;
            c.Fit(new Rect(tx, ty, tw, th), t1, 20, INK, true); ty += th + 4f;
            c.Para(new Rect(tx, ty, tw, dh), t2, 15, INK2); ty += dh + 8f;
            string e = g.VehRepairError(v);
            int vid = v.Id;
            var B = new UiLayout(new Rect(tx, ty, tw, BTN_H), 10f);
            // ASSIGN: a wheel of "no squad" + this camp's squads (the original cycles them)
            var opts = new List<int> { -1 };
            foreach (var sq in g.C.Squads) if (sq.Base == v.Base) opts.Add(sq.Id);
            if (!opts.Contains(v.Sq)) opts.Add(v.Sq);
            var names = opts.Select(id => id < 0 ? "no squad" : g.SquadById(id)?.Name ?? "squad").ToList();
            var ar = B.Right(Math.Min(420f, tw * 0.36f));
            c.Wheel("veh_assign_" + vid, ar, names, Math.Max(0, opts.IndexOf(v.Sq)), i => ArmAct(c, "assign_vehicle", vid, opts[i]), true, true, 3, 18);
            c.Fit(B.Right(90f), "ASSIGN", 17, INK2, true, MenuAlign.Right);
            c.Btn(B.Right(170f), "REFUEL", UiRole.Primary, () => ArmAct(c, "refuel_vehicle", vid), true, false, 19);
            c.Btn(B.Rest, "REPAIR", e == "" ? UiRole.Accent : UiRole.Neutral, () => ArmAct(c, "repair_vehicle", vid), true, false, 19,
                e == "" ? Game.FmtCost(g.VehRepairCost(v)) + string.Format(" · {0:0}h", g.VehRepairHours(v)) : e);
        }

        // ---------------- camp FACILITIES rows (main.gd _armory_rows / _garage_rows) ----------------
        static string CampGarageText(Game g, Base b) =>
            string.Format("{0} · Garage space: {1}/{2} ({3} free to drive) · Parts: {4}", b.Name, g.GarageUsed(b), g.GarageCap(b), g.VehiclesFree(b),
                string.Join(", ", Data.VEHICLE_PARTS.Select(k => string.Format("{0} {1}", Data.PART_LABEL[k], g.C.Parts.Get(k, 0)))));

        static string RebuildLabel(Game g, Base b) =>
            string.Format("REBUILD {0}", Data.VehDef(new[] { "pickup", "truck", "humvee" }[Math.Min(g.GarageUsed(b), 2)]).Name.ToUpperInvariant());

        public static float CampGarageRowH(MenuCtx c, Base b, float iw)
        {
            var g = c.G;
            string e = g.CanRebuildVehicle(b);
            return 10f + c.H_(CampGarageText(g, b), 16, false, iw - 36f) + 6f + BTN_H + (e != "" ? 4f + c.H_(e, 14, false, iw - 36f) : 0f) + 12f;
        }

        /// <summary>main.gd _garage_rows: Garage space, parts and REBUILD (rebuild_vehicle on this camp).</summary>
        public static void CampGarageRow(MenuCtx c, Base b, float x, float iw, float y)
        {
            var g = c.G;
            float h = CampGarageRowH(c, b, iw);
            Card(c, new Rect(x, y, iw, h), 0, AMBER, true);
            string t = CampGarageText(g, b);
            float th = c.H_(t, 16, false, iw - 36f);
            c.Para(new Rect(x + 18f, y + 10f, iw - 36f, th), t, 16, AMBER);
            float by = y + 10f + th + 6f;
            string e = g.CanRebuildVehicle(b);
            int bid = b.Id;
            c.Btn(new Rect(x + 18f, by, Math.Min(620f, iw - 36f), BTN_H), RebuildLabel(g, b), e == "" ? UiRole.Accent : UiRole.Neutral, () =>
            {
                string e2 = c.Act("rebuild_vehicle", new object[] { bid });
                c.Toast(e2 != "" ? e2 : "The vehicle runs again.");
            }, true, false, 19, "1 of each part + " + Game.FmtCost(g.VehicleCost(b)));
            if (e != "") c.Para(new Rect(x + 18f, by + BTN_H + 4f, iw - 36f, c.H_(e, 14, false, iw - 36f)), e, 14, INK2);
        }

        /// <summary>main.gd _armory_rows: arsenal line, 10 AMMO (craft_ammo) and OPEN ARMORY, for a camp sheet.</summary>
        public static float CampArmoryRow(MenuCtx c, float x, float iw, float y)
        {
            var g = c.G;
            var guns = Data.WEAPON_ORDER.Where(w => g.C.Weapons.Get(w, 0) > 0).Select(w => string.Format("{0} {1}", g.C.Weapons.Get(w, 0), Lower(Data.WEAPONS[w].Name))).ToList();
            string t = string.Format("Arsenal: {0}. Loadouts, gun parts and assembly are in the ARMORY screen.", guns.Count > 0 ? string.Join(", ", guns) : "no guns");
            float th = c.H_(t, 15, false, iw);
            c.Para(new Rect(x, y, iw, th), t, 15, AMBER); y += th + 4f;
            c.Btn(new Rect(x + 16, y, 230, BTN_H), "10 AMMO", UiRole.Primary, () =>
            {
                string e = c.Act("craft_ammo", new object[0]);
                c.Toast(e != "" ? e : "+10 ammo");
            }, true, false, 19, ShortCost(Game.AMMO_CRAFT));
            c.Btn(new Rect(x + 256, y, 220, BTN_H), "OPEN ARMORY", UiRole.Accent, () => Open(c.St, "armory"));
            return y + BTN_H + 6f;
        }

        // ---------------- wreck sheet (main.gd _sheet_wreck) ----------------
        public static Rect WreckSheetRect(float w, float h, int squads)
        {
            float ph = 262f + Math.Min(4, Math.Max(1, squads)) * (BTN_H + 6f);
            return SheetRect(w, h, 900f, Math.Min(ph, h - 62f - 10f - 120f));
        }

        public static int IdleSquads(Game g) => g.C.Squads.Count(q => q.St == "idle");

        /// <summary>The wreck sheet (main.gd _sheet_wreck): info, SIPHON FUEL (siphon_wreck) and v0.14.0a's TOW TO
        /// GARAGE block: one button per idle squad (tow_wreck, the original's tow rules in Game.TowError), what is
        /// missing when none can go (a Garage, a free squad, a vehicle for heavy wrecks), progress while a squad is
        /// already on the way, and what comes next (GARAGE: repair, refuel, assign).</summary>
        public static void WreckSheet(MenuCtx c, int wid, Action close) { WreckSheetBody(c, wid, close); Flush(c); }

        static void WreckSheetBody(MenuCtx c, int wid, Action close)
        {
            var g = c.G;
            var w = g.WreckById(wid);
            if (w == null) { close?.Invoke(); return; }
            var d = Data.VehDef(w.Type);
            var idle = g.C.Squads.Where(q => q.St == "idle").ToList();
            var L = SheetFrame(c, WreckSheetRect(c.W, c.H, idle.Count), d.Name.ToUpperInvariant() + " WRECK", close);
            if (w.Own == g.ViewCol) c.Fit(L.Top(22f), "Yours: left on the road. Tow it back to a Garage and fix it up.", 16, GREEN, false);
            c.Fit(L.Top(24f), Game.WreckInfo(w), 17, INK, false);
            c.Fit(L.Top(22f), string.Format("{0} - speed x{1:0.0} - tank {2} - {3}", Game.StreetName(w.Pos), d.Speed, d.Tank, d.Light ? "light: a squad of 4 can drag it" : "heavy: needs a working vehicle to tow"), 15, INK2, false);
            var sr = new UiLayout(L.Top(BTN_H), 12f);
            c.Btn(sr.Left(280f), string.Format("SIPHON FUEL ({0})", (int)w.Fuel), UiRole.Primary, () =>
            {
                string e = c.Act("siphon_wreck", new object[] { wid });
                if (e != "") c.Toast(e);   // main.gd _res_toast: errors only (the log says how much)
            }, true, false, 19);
            c.Para(sr.Rest, "Then: tow it to a Garage, repair it there, refuel it and assign it to a squad. Scavengers and task forces take free vehicles too.", 14, INK2);
            c.Header(L.Top(40f), "TOW TO GARAGE", 20);
            // already on its way?
            var tq = g.TowingSquad(wid);
            if (tq != null && g.TowStatus(tq, out double frac, out string phase, out _))
            {
                c.Fit(L.Top(24f), string.Format("{0} is {1} ({2}%). It goes into {3}'s Garage.", tq.Name, phase, (int)(frac * 100.0), g.BaseById(tq.Base)?.Name ?? "camp"), 17, INK, false);
                c.KBar(L.Top(26f), (float)frac, UiRole.Accent, (int)(frac * 100.0) + "%");
                return;
            }
            if (!g.C.Bases.Any(g.HasGarage))
            {
                var r = new UiLayout(L.Top(BTN_H), 12f);
                c.Btn(r.Right(230f), "OPEN GARAGE", UiRole.Accent, () => { close?.Invoke(); Open(c.St, "garage"); });
                c.Para(r.Rest, "No Garage yet. Gut a building and convert it to a Garage (2 vehicles each), or upgrade a camp's Garage.", 15, INK2);
                return;
            }
            if (idle.Count == 0)
            {
                var r = new UiLayout(L.Top(BTN_H), 12f);
                c.Btn(r.Right(230f), "OPEN SQUADS", UiRole.Accent, () => { close?.Invoke(); Open(c.St, "squads"); });
                c.Para(r.Rest, d.Light ? "No squad is free. Form one in SQUADS: 4 people can drag it, or give the squad a working vehicle." : "No squad is free. Form one in SQUADS and assign it a working, fuelled vehicle to tow with.", 15, INK2);
                return;
            }
            foreach (var q in idle.Take(4))
            {
                string e = g.TowError(wid, q.Id); int sid = q.Id;
                var b = g.BaseById(q.Base);
                var r = new UiLayout(L.Top(BTN_H), 12f);
                string lbl = string.Format("TOW TO {0} WITH {1}", (b?.Name ?? "CAMP").ToUpperInvariant(), q.Name.ToUpperInvariant());
                var br = e == "" ? r.Rest : r.Left(Math.Min(460f, r.Rest.width * 0.55f));
                c.Btn(br, lbl, e == "" ? UiRole.Accent : UiRole.Neutral, () =>
                {
                    string e2 = c.Act("tow_wreck", new object[] { wid, sid });
                    c.Toast(e2 != "" ? e2 : "On the way. When it's in, open GARAGE to repair, refuel and assign it.");
                    close?.Invoke();   // main.gd closes the sheet either way
                }, true, false, 18);
                if (e != "") c.Para(r.Rest, e, 14, INK2);
            }
        }
    }
}
