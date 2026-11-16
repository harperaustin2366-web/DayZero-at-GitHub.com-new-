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
            if (a.PickPid >= 0) { ArmPick(c); return; }
            var p = Frame(c, "ARMORY");
            float x = p.x + 18, iw = p.width - 36, y = p.y + 66;
            var q = g.C.Craft;
            string head = g.HasArmory() ? string.Format("Armory level {0}", g.ArmoryLevel()) : "No Armory yet: gut a building and convert it to an Armory to change loadouts and assemble guns";
            head += " · " + (g.HasWorkshop() ? "Workshop ready" : "no Workshop (parts need one)");
            if (q.Count > 0)
                head += string.Format("\nWorking on: {0} ({1}%){2}", Data.GearDefOf(q[0].Id).Name, (int)(g.CraftProgress() * 100.0), q.Count > 1 ? string.Format(" · {0} more queued", q.Count - 1) : "");
            // camp sheet "10 AMMO" (main.gd _armory_rows): a camp with the Armory facility
            bool ammo = g.BestArmory() >= 1;
            float hw = ammo ? iw - 176f : iw;
            float hh = Math.Max(ammo ? 50f : 0f, c.H_(head, 15, false, hw));
            c.Label(new Rect(x, y, hw, hh), head, 15, INK2);
            if (ammo) c.Button(new Rect(x + iw - 166, y, 166, 50), "10 AMMO\n" + ShortCost(Game.AMMO_CRAFT), "olive", 15, () =>
            {
                string e = c.Act("craft_ammo", new object[0]);
                c.Toast(e != "" ? e : "+10 ammo");
            });
            y += hh + 8f;
            var tabs = new[] { ("loadouts", "LOADOUTS"), ("workshop", "WORKSHOP"), ("assemble", "ASSEMBLE") };
            float tw = (iw - 12f) / 3f;
            for (int i = 0; i < tabs.Length; i++)
            {
                string id = tabs[i].Item1;
                c.Button(new Rect(x + i * (tw + 6f), y, tw, 48), tabs[i].Item2, a.Tab == id ? "sel" : "olive", 17, () => { a.Tab = id; a.Page = 0; });
            }
            y += 56f;
            float bottom = p.yMax - 70f;
            if (a.Tab == "workshop") ArmCraftRows(c, p, x, iw, y, bottom, Data.GPART_ORDER, true);
            else if (a.Tab == "assemble") ArmCraftRows(c, p, x, iw, y, bottom, Data.WEAPON_ORDER.Concat(Data.ARMOR_ORDER).ToArray(), false);
            else ArmLoadoutRows(c, p, x, iw, y, bottom);
        }

        /// <summary>main.gd _render_armory QUEUE block: one chip per job with CANCEL (craft_cancel index).</summary>
        static float ArmQueue(MenuCtx c, float x, float iw, float y)
        {
            var q = c.G.C.Craft;
            if (q.Count == 0) return y;
            c.Label(new Rect(x, y, 90, 22), "QUEUE", 15, AMBER, true, false);
            y += 24f;
            const int COLS = 4; const float QH = 54f, GAP = 6f;
            float qw = (iw - (COLS - 1) * GAP) / COLS;
            for (int i = 0; i < q.Count; i++)
            {
                var r = new Rect(x + (i % COLS) * (qw + GAP), y + (i / COLS) * (QH + GAP), qw, QH);
                c.Box(r, i == 0 ? new Color(0.20f, 0.17f, 0.08f) : CARD);
                c.Box(new Rect(r.x, r.y, 3, QH), i == 0 ? AMBER : CARD_EDGE);
                c.Label(new Rect(r.x + 10, r.y + 4, qw - 104, QH - 8), Data.GearDefOf(q[i].Id).Name + (i == 0 ? "\n(working)" : ""), 14, INK);
                int ix = i;
                c.Button(new Rect(r.xMax - 96, r.y + 5, 90, 44), "CANCEL", "dark", 15, () => ArmAct(c, "craft_cancel", ix));
            }
            return y + ((q.Count + COLS - 1) / COLS) * (QH + GAP);
        }

        static float QueueH(Game g) => g.C.Craft.Count == 0 ? 0f : 24f + ((g.C.Craft.Count + 3) / 4) * 60f + 6f;

        /// <summary>main.gd _arm_craft_rows: WORKSHOP parts (CRAFT) or ASSEMBLE guns and armour (BUILD L#), two cards a row.</summary>
        static void ArmCraftRows(MenuCtx c, Rect p, float x, float iw, float y, float bottom, string[] ids, bool parts)
        {
            var g = c.G; var a = c.St.Arm;
            string intro = parts ? "Workshop parts are made from material items, one job at a time (more Workshops work faster)."
                : "Guns, vests and helmets are assembled at the Armory from Workshop parts (scavenged gun stores, precincts and checkpoints turn up parts and finished gear too).";
            float ih = c.H_(intro, 14, false, iw);
            c.Label(new Rect(x, y, iw, ih), intro, 14, INK2); y += ih + 6f;
            const float GAP = 8f, BW = 132f, PAD = 8f;
            float cw = (iw - GAP) / 2f, tw = cw - BW - 24f;
            var rows = new List<(string id, string txt, string err, float th, float eh)>();
            foreach (var id in ids)
            {
                var d = Data.GearDefOf(id);
                string need = ShortCost(d.Cost);
                if (d.Parts != null) need = PartsText(d.Parts) + (need != "" ? ", " + need : "");
                string txt = string.Format("{0}  ·  have {1}{2}\n{3} · {4:0}h", d.Name, g.GearTotal(id), parts ? "" : string.Format(" ({0} free)", g.GearFree(id)), need,
                    (d.H ?? 3.0) / g.CraftSpeed(parts ? "part" : "gun"));
                if (!parts) txt += "\n" + ArmStat(id);
                string err = g.CraftError(id);
                rows.Add((id, txt, err, c.H_(txt, 14, false, tw), err == "" ? 0f : c.H_(err, 12, false, cw - 24f)));
            }
            // pairs of cards share a row height; the queue sits under the cards on the last page
            var hs = new List<float>();
            for (int i = 0; i < rows.Count; i += 2)
            {
                float h0 = CraftCardH(rows[i].th, rows[i].eh), h1 = i + 1 < rows.Count ? CraftCardH(rows[i + 1].th, rows[i + 1].eh) : 0f;
                hs.Add(Math.Max(h0, h1));
            }
            float qh = QueueH(g);
            var pages = Pages(hs, bottom - y, GAP);
            // the queue must fit under the last page's cards: otherwise it gets a page of its own
            int lastFrom = pages[pages.Count - 1];
            float lastUsed = 0f; for (int i = lastFrom; i < hs.Count; i++) lastUsed += hs[i] + GAP;
            bool qPage = qh > 0f && lastUsed + qh > bottom - y;
            int nPages = pages.Count + (qPage ? 1 : 0);
            a.Page = Math.Max(0, Math.Min(nPages - 1, a.Page));
            if (a.Page < pages.Count)
            {
                int from = pages[a.Page], to = a.Page + 1 < pages.Count ? pages[a.Page + 1] : hs.Count;
                for (int r = from; r < to; r++)
                {
                    for (int k = 0; k < 2; k++)
                    {
                        int i = r * 2 + k;
                        if (i >= rows.Count) break;
                        var row = rows[i];
                        var card = new Rect(x + k * (cw + GAP), y, cw, hs[r]);
                        c.Box(card, CARD);
                        c.Box(new Rect(card.x, card.y, 3, card.height), row.err == "" ? new Color(0.55f, 0.62f, 0.36f) : CARD_EDGE);
                        c.Label(new Rect(card.x + 14, card.y + PAD, tw, row.th), row.txt, 14, INK);
                        var d = Data.GearDefOf(row.id); string id = row.id;
                        c.Button(new Rect(card.xMax - BW - 8, card.y + PAD, BW, 50), parts ? "CRAFT" : string.Format("BUILD L{0}", d.Armory ?? 1), row.err == "" ? "sel" : "dark", 17,
                            () => ArmAct(c, "craft", id));
                        if (row.err != "") c.Label(new Rect(card.x + 14, card.y + PAD + Math.Max(row.th, 50f) + 2f, cw - 24f, row.eh), row.err, 12, RED);
                    }
                    y += hs[r] + GAP;
                }
            }
            if (a.Page == nPages - 1) ArmQueue(c, x, iw, y);
            Pager(c, p, a.Page, nPages, v => a.Page = v, parts ? "Parts: CRAFT queues one job (up to 8). CANCEL refunds it." : "Armory level and Workshop parts unlock each build. CANCEL refunds it.");
        }

        static float CraftCardH(float th, float eh) => 8f + Math.Max(th, 50f) + (eh > 0f ? 2f + eh : 0f) + 8f;

        /// <summary>main.gd _arm_loadout_rows: rack, AUTO-EQUIP, one row per adult with gun / vest / helmet slots.</summary>
        static void ArmLoadoutRows(MenuCtx c, Rect p, float x, float iw, float y, float bottom)
        {
            var g = c.G; var a = c.St.Arm;
            bool can = g.HasArmory();
            var rack = new List<string>();
            foreach (var id in Data.WEAPON_ORDER.Concat(Data.ARMOR_ORDER))
                if (g.GearFree(id) > 0) rack.Add(string.Format("{0} {1}", g.GearFree(id), Lower(Data.GearDefOf(id).Name)));
            string rt = "On the rack: " + (rack.Count > 0 ? string.Join(", ", rack) : "nothing spare");
            float rw = iw - 170f, rh = Math.Max(48f, c.H_(rt, 15, false, rw));
            c.Label(new Rect(x, y, rw, rh), rt, 15, AMBER);
            c.Button(new Rect(x + iw - 160, y, 160, 48), "AUTO-EQUIP", can ? "sel" : "dark", 17, () => ArmAct(c, "auto_equip"));
            y += rh + 6f;
            if (!can)
            {
                const string nt = "Loadouts can only be changed with an Armory in the colony. Until then fighters pick up whatever is free.";
                float nh = c.H_(nt, 14, false, iw);
                c.Label(new Rect(x, y, iw, nh), nt, 14, RED); y += nh + 6f;
            }
            var ppl = g.C.People.Where(pp => pp.St != "dead" && !g.IsMinor(pp)).OrderBy(pp => GuardRankUi(g, pp)).ToList();   // stable, as sort_custom on _guard_rank
            const float RH = 54f, GAP = 6f, SW = 196f;
            float qh = QueueH(g);
            int per = Math.Max(1, (int)((bottom - y + GAP) / (RH + GAP)));
            int pages = Math.Max(1, (ppl.Count + per - 1) / per);
            // queue under the last page's rows, or a page of its own
            int lastN = ppl.Count - (pages - 1) * per;
            bool qPage = qh > 0f && lastN * (RH + GAP) + qh > bottom - y;
            int nPages = pages + (qPage ? 1 : 0);
            a.Page = Math.Max(0, Math.Min(nPages - 1, a.Page));
            if (a.Page < pages)
                foreach (var pp in ppl.Skip(a.Page * per).Take(per))
                {
                    var card = new Rect(x, y, iw, RH);
                    c.Box(card, CARD);
                    c.Box(new Rect(card.x, card.y, 3, RH), pp.Sq > 0 || pp.Job == "Soldier" || pp.Job == "Cop" || pp.Guard ? AMBER : CARD_EDGE);
                    float nw = iw - 3 * (SW + 6f) - 20f;
                    c.Label(new Rect(card.x + 12, card.y + 5, nw, 22), pp.Name, 16, INK, true, false);
                    c.Label(new Rect(card.x + 12, card.y + 28, nw, 20), string.Format("{0} · armour {1}%", pp.Job, (int)(g.ArmorOf(pp) * 100.0)), 14, INK2, false, false);
                    float bx = card.xMax - 3 * (SW + 6f) - 4f;
                    foreach (var s in Game.SLOTS)
                    {
                        string cur = pp.Slot(s) ?? "";
                        string lab = cur != "" ? Data.GearDefOf(cur)?.Name ?? cur : s == "wpn" ? "no gun" : s == "vest" ? "no vest" : "no helmet";
                        int pid = pp.Id; string slot = s;
                        c.Button(new Rect(bx, card.y + 5, SW, RH - 10), lab, cur != "" ? "olive" : "dark", 15, () => { a.PickPid = pid; a.PickSlot = slot; }, can);
                        bx += SW + 6f;
                    }
                    y += RH + GAP;
                }
            if (a.Page == nPages - 1) ArmQueue(c, x, iw, y + 4f);
            Pager(c, p, a.Page, nPages, v => a.Page = v, string.Format("{0} adults · tap a slot to EQUIP or TAKE OFF", ppl.Count));
        }

        /// <summary>main.gd _arm_pick: the EQUIP · name modal (TAKE OFF, then every free item for the slot).</summary>
        static void ArmPick(MenuCtx c)
        {
            var g = c.G; var a = c.St.Arm;
            var pp = g.PersonById(a.PickPid);
            if (pp == null || pp.Col != g.CurCol || pp.St == "dead") { a.PickPid = -1; Armory(c); return; }
            var p = Frame(c, "EQUIP · " + pp.Name);
            c.Button(new Rect(p.xMax - 284, p.y + 10, 130, 48), "< BACK", "olive", 18, () => a.PickPid = -1);
            float x = p.x + 18, iw = p.width - 36, y = p.y + 66;
            string slot = a.PickSlot, cur = pp.Slot(slot) ?? "";
            int pid = pp.Id;
            if (cur != "")
            {
                c.Button(new Rect(x, y, iw, 50), "TAKE OFF " + Data.GearDefOf(cur).Name.ToUpperInvariant(), "dark", 18, () => { a.PickPid = -1; ArmAct(c, "equip", pid, slot, ""); });
                y += 58f;
            }
            const float RH = 62f, GAP = 6f;
            bool any = false;
            foreach (var id in slot == "wpn" ? Data.WEAPON_ORDER : Data.ARMOR_ORDER)
            {
                if (Game.SlotOf(id) != slot || id == cur || g.GearFree(id) < 1) continue;
                any = true;
                var card = new Rect(x, y, iw, RH);
                c.Box(card, CARD);
                c.Label(new Rect(card.x + 14, card.y + 6, iw - 200, RH - 10), string.Format("{0} ({1} free)\n{2}", Data.GearDefOf(id).Name, g.GearFree(id), ArmStat(id)), 15, INK);
                string gid = id;
                c.Button(new Rect(card.xMax - 170, card.y + 6, 160, 50), "EQUIP", "sel", 18, () => { a.PickPid = -1; ArmAct(c, "equip", pid, slot, gid); });
                y += RH + GAP;
            }
            if (!any) c.Label(new Rect(x, y, iw, 26), "Nothing free on the rack for this slot. Craft or trade for more.", 15, INK2);
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
            var g = c.G; var a = c.St.Arm;
            var p = Frame(c, "GARAGE");
            float x = p.x + 18, iw = p.width - 36, y = p.y + 66;
            var caps = g.C.Bases.Where(g.HasGarage).Select(b => string.Format("{0} {1}/{2}", b.Name, g.GarageUsed(b), g.GarageCap(b))).ToList();
            string l1 = caps.Count > 0 ? "Garages: " + string.Join(", ", caps) : "No Garage yet: gut a building and convert it to a Garage (2 vehicles each, +1 per Garage upgrade).";
            float h1 = c.H_(l1, 14, false, iw);
            c.Label(new Rect(x, y, iw, h1), l1, 14, INK2); y += h1 + 2f;
            string l2 = string.Format("Fuel in stores: {0} · Parts: {1}", (int)g.C.Res.Get("fuel", 0.0),
                string.Join(", ", Data.VEHICLE_PARTS.Select(k => string.Format("{0} {1}", Data.PART_LABEL[k], g.C.Parts.Get(k, 0)))));
            float h2 = c.H_(l2, 15, false, iw);
            c.Label(new Rect(x, y, iw, h2), l2, 15, AMBER); y += h2 + 8f;

            // rows: the camp Garage rows (REBUILD, camp sheet in the original), then one card per vehicle
            var rows = new List<(float h, Action<float> draw)>();
            foreach (var b in g.C.Bases.Where(g.HasGarage).ToList())
            {
                var bb = b;
                float rh = CampGarageRowH(c, bb, iw);
                rows.Add((rh, yy => CampGarageRow(c, bb, x, iw, yy)));
            }
            if (g.C.Vehs.Count == 0)
            {
                const string nv = "No vehicles. Tap a wreck on the road and tow it in with a squad, or rebuild one from parts on a camp's Garage.";
                float nh = c.H_(nv, 15, false, iw);
                rows.Add((nh, yy => c.Label(new Rect(x, yy, iw, nh), nv, 15, INK2)));
            }
            foreach (var v in g.C.Vehs.ToList())
            {
                var vv = v;
                var (t1, t2) = VehTexts(g, vv);
                float th = c.H_(t1, 17, true, iw - 24f), dh = c.H_(t2, 14, false, iw - 24f);
                float ch = 8f + th + 2f + dh + 6f + 52f + 8f;
                rows.Add((ch, yy => VehCard(c, vv, x, iw, yy, t1, t2, th, dh, ch)));
            }
            var pages = Pages(rows.Select(r => r.h).ToList(), p.yMax - 70f - y, 6f);
            a.GPage = Math.Max(0, Math.Min(pages.Count - 1, a.GPage));
            int from = pages[a.GPage], to = a.GPage + 1 < pages.Count ? pages[a.GPage + 1] : rows.Count;
            for (int i = from; i < to; i++) { rows[i].draw(y); y += rows[i].h + 6f; }
            Pager(c, p, a.GPage, pages.Count, v => a.GPage = v, string.Format("{0} vehicle{1} · ASSIGN cycles the camp's squads", g.C.Vehs.Count, g.C.Vehs.Count == 1 ? "" : "s"));
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
            c.Box(card, CARD);
            c.Box(new Rect(card.x, card.y, 3, ch), Game.VehRuns(v) ? GREEN : v.St == "repair" ? AMBER : CARD_EDGE);
            float ty = y + 8f;
            c.Label(new Rect(x + 12, ty, iw - 24, th), t1, 17, INK, true); ty += th + 2f;
            c.Label(new Rect(x + 12, ty, iw - 24, dh), t2, 14, INK2); ty += dh + 6f;
            string e = g.VehRepairError(v);
            string rl = "REPAIR\n" + (e == "" ? Game.FmtCost(g.VehRepairCost(v)) + string.Format(" · {0:0}h", g.VehRepairHours(v)) : (e.Length > 40 ? e.Substring(0, 40) : e));
            int vid = v.Id;
            float abw = 150f, fbw = 130f, rbw = iw - 24f - abw - fbw - 12f;
            c.Button(new Rect(x + 12, ty, rbw, 52), rl, e == "" ? "sel" : "dark", 14, () => ArmAct(c, "repair_vehicle", vid));
            c.Button(new Rect(x + 12 + rbw + 6, ty, fbw, 52), "REFUEL", "olive", 17, () => ArmAct(c, "refuel_vehicle", vid));
            var opts = new List<int> { -1 };
            foreach (var sq in g.C.Squads) if (sq.Base == v.Base) opts.Add(sq.Id);
            int nxt = opts[(opts.IndexOf(v.Sq) + 1) % opts.Count];
            c.Button(new Rect(x + 12 + rbw + 6 + fbw + 6, ty, abw, 52), "ASSIGN", "olive", 17, () => ArmAct(c, "assign_vehicle", vid, nxt));
        }

        // ---------------- camp FACILITIES rows (main.gd _armory_rows / _garage_rows) ----------------
        static string CampGarageText(Game g, Base b) =>
            string.Format("{0} · Garage space: {1}/{2} ({3} free to drive) · Parts: {4}", b.Name, g.GarageUsed(b), g.GarageCap(b), g.VehiclesFree(b),
                string.Join(", ", Data.VEHICLE_PARTS.Select(k => string.Format("{0} {1}", Data.PART_LABEL[k], g.C.Parts.Get(k, 0)))));

        static string RebuildLabel(Game g, Base b) =>
            string.Format("REBUILD {0} · 1 of each part + {1}", Data.VehDef(new[] { "pickup", "truck", "humvee" }[Math.Min(g.GarageUsed(b), 2)]).Name.ToUpperInvariant(), Game.FmtCost(g.VehicleCost(b)));

        public static float CampGarageRowH(MenuCtx c, Base b, float iw)
        {
            var g = c.G;
            string e = g.CanRebuildVehicle(b);
            return 8f + c.H_(CampGarageText(g, b), 15, false, iw - 24f) + 4f + 48f + (e != "" ? 4f + c.H_(e, 13, false, iw - 24f) : 0f) + 8f;
        }

        /// <summary>main.gd _garage_rows: Garage space, parts and REBUILD (rebuild_vehicle on this camp).</summary>
        public static void CampGarageRow(MenuCtx c, Base b, float x, float iw, float y)
        {
            var g = c.G;
            float h = CampGarageRowH(c, b, iw);
            c.Box(new Rect(x, y, iw, h), new Color(0.13f, 0.15f, 0.12f));
            c.Box(new Rect(x, y, 3, h), AMBER);
            string t = CampGarageText(g, b);
            float th = c.H_(t, 15, false, iw - 24f);
            c.Label(new Rect(x + 12, y + 8, iw - 24, th), t, 15, AMBER);
            float by = y + 8 + th + 4;
            string e = g.CanRebuildVehicle(b);
            int bid = b.Id;
            c.Button(new Rect(x + 12, by, iw - 24, 48), RebuildLabel(g, b), e == "" ? "sel" : "dark", 16, () =>
            {
                string e2 = c.Act("rebuild_vehicle", new object[] { bid });
                c.Toast(e2 != "" ? e2 : "The vehicle runs again.");
            });
            if (e != "") c.Label(new Rect(x + 12, by + 52, iw - 24, c.H_(e, 13, false, iw - 24f)), e, 13, INK2);
        }

        /// <summary>main.gd _armory_rows: arsenal line, 10 AMMO (craft_ammo) and OPEN ARMORY, for a camp sheet.</summary>
        public static float CampArmoryRow(MenuCtx c, float x, float iw, float y)
        {
            var g = c.G;
            var guns = Data.WEAPON_ORDER.Where(w => g.C.Weapons.Get(w, 0) > 0).Select(w => string.Format("{0} {1}", g.C.Weapons.Get(w, 0), Lower(Data.WEAPONS[w].Name))).ToList();
            string t = string.Format("Arsenal: {0}. Loadouts, gun parts and assembly are in the ARMORY screen.", guns.Count > 0 ? string.Join(", ", guns) : "no guns");
            float th = c.H_(t, 14, false, iw);
            c.Label(new Rect(x, y, iw, th), t, 14, AMBER); y += th + 4f;
            c.Button(new Rect(x + 16, y, 130, 50), "10 AMMO\n" + ShortCost(Game.AMMO_CRAFT), "olive", 14, () =>
            {
                string e = c.Act("craft_ammo", new object[0]);
                c.Toast(e != "" ? e : "+10 ammo");
            });
            c.Button(new Rect(x + 152, y, 180, 50), "OPEN ARMORY", "sel", 16, () => Open(c.St, "armory"));
            return y + 56f;
        }

        // ---------------- wreck sheet (main.gd _sheet_wreck) ----------------
        public static Rect WreckSheetRect(float w, float h, int squads)
        {
            float pw = Math.Min(860f, w - 200f), ph = 250f + Math.Min(4, Math.Max(1, squads)) * 54f;
            return new Rect(w / 2f - pw / 2f, h - 62f - ph - 10f, pw, ph);
        }

        public static int IdleSquads(Game g) => g.C.Squads.Count(q => q.St == "idle");

        /// <summary>The wreck sheet (main.gd _sheet_wreck): info, SIPHON FUEL (siphon_wreck) and v0.14.0a's TOW TO
        /// GARAGE block: one button per idle squad (tow_wreck, the original's tow rules in Game.TowError), what is
        /// missing when none can go (a Garage, a free squad, a vehicle for heavy wrecks), progress while a squad is
        /// already on the way, and what comes next (GARAGE: repair, refuel, assign).</summary>
        public static void WreckSheet(MenuCtx c, int wid, Action close)
        {
            var g = c.G;
            var w = g.WreckById(wid);
            if (w == null) { close?.Invoke(); return; }
            var d = Data.VehDef(w.Type);
            var idle = g.C.Squads.Where(q => q.St == "idle").ToList();
            var p = WreckSheetRect(c.W, c.H, idle.Count);
            c.Box(p, PANEL);
            c.Box(new Rect(p.x, p.y, p.width, 3), AMBER);
            float x = p.x + 16, iw = p.width - 32, y = p.y + 10;
            c.Label(new Rect(x, y, iw - 140, 30), d.Name.ToUpperInvariant() + " WRECK", 22, AMBER, true, false);
            c.Button(new Rect(p.xMax - 126, p.y + 8, 112, 44), "CLOSE", "dark", 18, close);
            y += 44f;
            if (w.Own == g.ViewCol) { c.Label(new Rect(x, y, iw, 22), "Yours: left on the road. Tow it back to a Garage and fix it up.", 14, GREEN, false, false); y += 24f; }
            c.Label(new Rect(x, y, iw, 24), Game.WreckInfo(w), 15, INK, false, false); y += 24f;
            c.Label(new Rect(x, y, iw, 22), string.Format("{0} - speed x{1:0.0} - tank {2} - {3}", Game.StreetName(w.Pos), d.Speed, d.Tank, d.Light ? "light: a squad of 4 can drag it" : "heavy: needs a working vehicle to tow"), 13, INK2, false, false);
            y += 28f;
            c.Button(new Rect(x, y, 260, 46), string.Format("SIPHON FUEL ({0})", (int)w.Fuel), "olive", 17, () =>
            {
                string e = c.Act("siphon_wreck", new object[] { wid });
                if (e != "") c.Toast(e);   // main.gd _res_toast: errors only (the log says how much)
            });
            c.Label(new Rect(x + 272, y + 2, iw - 272, 44), "Then: tow it to a Garage, repair it there, refuel it and assign it to a squad. Scavengers and task forces take free vehicles too.", 13, INK2);
            y += 54f;
            c.Box(new Rect(x, y, iw, 2), CARD_EDGE); y += 8f;
            c.Label(new Rect(x, y, iw, 24), "TOW TO GARAGE", 18, AMBER, true, false); y += 28f;
            // already on its way?
            var tq = g.TowingSquad(wid);
            if (tq != null && g.TowStatus(tq, out double frac, out string phase, out _))
            {
                c.Label(new Rect(x, y, iw, 22), string.Format("{0} is {1} ({2}%). It goes into {3}'s Garage.", tq.Name, phase, (int)(frac * 100.0), g.BaseById(tq.Base)?.Name ?? "camp"), 15, INK, false, false);
                c.Bar(new Rect(x, y + 26, iw, 12), (float)frac, AMBER);
                return;
            }
            if (!g.C.Bases.Any(g.HasGarage))
            {
                c.Label(new Rect(x, y, iw - 220, 50), "No Garage yet. Gut a building and convert it to a Garage (2 vehicles each), or upgrade a camp's Garage.", 14, INK2);
                c.Button(new Rect(x + iw - 210, y, 210, 48), "OPEN GARAGE", "sel", 17, () => { close?.Invoke(); Open(c.St, "garage"); });
                return;
            }
            if (idle.Count == 0)
            {
                c.Label(new Rect(x, y, iw - 220, 50), d.Light ? "No squad is free. Form one in SQUADS: 4 people can drag it, or give the squad a working vehicle." : "No squad is free. Form one in SQUADS and assign it a working, fuelled vehicle to tow with.", 14, INK2);
                c.Button(new Rect(x + iw - 210, y, 210, 48), "OPEN SQUADS", "sel", 17, () => { close?.Invoke(); Open(c.St, "squads"); });
                return;
            }
            foreach (var q in idle.Take(4))
            {
                string e = g.TowError(wid, q.Id); int sid = q.Id;
                var b = g.BaseById(q.Base);
                float bw = e == "" ? iw : Math.Min(420f, iw * 0.5f);
                string lbl = string.Format("TOW TO {0} WITH {1}", (b?.Name ?? "CAMP").ToUpperInvariant(), q.Name.ToUpperInvariant());
                c.Button(new Rect(x, y, bw, 48), lbl, e == "" ? "sel" : "dark", 16, () =>
                {
                    string e2 = c.Act("tow_wreck", new object[] { wid, sid });
                    c.Toast(e2 != "" ? e2 : "On the way. When it's in, open GARAGE to repair, refuel and assign it.");
                    close?.Invoke();   // main.gd closes the sheet either way
                });
                if (e != "") c.Label(new Rect(x + bw + 10, y + 2, iw - bw - 10, 48), e, 13, INK2);
                y += 54f;
            }
        }
    }
}
