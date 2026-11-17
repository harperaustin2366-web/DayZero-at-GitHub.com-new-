// SQUADS and TASK FORCE side menus, ported from the Godot original (main.gd _show_squads, _show_form_squad,
// _show_send_map, _show_taskforce, _camp_switch, _camp_auto_row, _sheet_group, _start_squad_target,
// _preview_squad, _start_air). Same display-list approach as RESEARCH / TRADE (SideMenus.cs): GameUI draws it
// with IMGUI, the offline preview paints it to PNG. Every button sends the original's player action
// (form_squad, disband_squad, recall_squad, send_squad, send_squad_scav, send_squad_map, drone_scout,
// air_strike, send_rescue, heli_rescue, set_auto) through Bootstrap.Act -> Game.DoAction; no rules live here.
// Map targets (CLEAR ROAD, SCAVENGE, DRONE SCOUT, GUNSHIP STRIKE) are picked by tapping the map, as in the
// original: the menu hands over to MapView's target tool and SquadTarget() below explains the tapped spot.
using System;
using System.Collections.Generic;
using System.Linq;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    /// <summary>What a tapped map target means for a squad run / drone / strike (main.gd _preview_squad).</summary>
    public sealed class TargetInfo
    {
        public string Title = "", Detail = "";
        public bool Ok;
        public List<Vector2> Path = new List<Vector2>();
        public List<int> Search = new List<int>();   // lots a scavenging run would search
        public float Radius;                         // drone / strike area (tiles)
    }

    public static partial class SideMenus
    {
        static readonly Color CAMP_MAIN = new Color(0.62f, 0.84f, 0.42f);
        static readonly Color CAMP_OUT = new Color(0.56f, 0.82f, 1f);   // #8fd0ff

        // ---------------- camp picker shared by SQUADS and TASK FORCE ----------------
        /// <summary>main.gd _camp_list: finished camps, the main camp first.</summary>
        public static List<Base> CampList(Game g)
        {
            var o = g.C.Bases.Where(b => b.Done >= 1.0 && b.Main).ToList();
            o.AddRange(g.C.Bases.Where(b => b.Done >= 1.0 && !b.Main));
            return o;
        }

        /// <summary>main.gd _sq_base: the picked camp, or the main camp.</summary>
        public static Base SqBase(Game g, SideMenuState st)
        {
            var b = g.BaseById(st.Camp);
            if (b == null || b.Done < 1.0) { b = g.MainBase(); st.Camp = b?.Id ?? -1; }
            return b;
        }

        /// <summary>main.gd _camp_switch: which camp the screen is about. Several camps: a wheel of their names (the
        /// original's < > buttons); one camp: its name. The camp's beds and squads sit beside it.</summary>
        static void CampSwitch(MenuCtx c, UiLayout L)
        {
            var g = c.G; var st = c.St;
            var cl = CampList(g); var cb = SqBase(g, st);
            if (cb == null) return;
            var row = new UiLayout(L.Top(BTN_H), 10f);
            int ix = Math.Max(0, cl.FindIndex(b => b.Id == cb.Id));
            int ns = g.CampSquads(cb.Id).Count;
            if (cl.Count > 1)
            {
                c.Fit(row.Left(70f), "CAMP", 17, INK2, true);
                var names = cl.Select(b => b.Name).ToList();
                c.Wheel("sq_camp", row.Left(Math.Min(560f, row.Rest.width * 0.5f)), names, ix, i => { st.Camp = cl[i].Id; st.SqPage = st.TfPage = 0; }, true, true, 3, 19);
            }
            else c.Fit(row.Left(Math.Min(420f, row.Rest.width * 0.4f)), cb.Name, 22, cb.Main ? CAMP_MAIN : CAMP_OUT, true);
            var chips = new UiLayout(UiLayout.Center(row.Rest, row.Rest.width, 36f), 8f);
            c.Chip(chips.Left(190f), string.Format("{0} / {1} BEDS", g.CampResidents(cb.Id), g.BaseBeds(cb)), UiRole.Neutral, 16);
            c.Chip(chips.Left(170f), string.Format("{0} SQUAD{1}", ns, ns == 1 ? "" : "S"), ns > 0 ? UiRole.Accent : UiRole.Neutral, 16);
        }

        /// <summary>main.gd _camp_auto_row: this camp's auto-scavenge / auto-rescue / auto-convert switches (set_auto key on bid).</summary>
        static void CampAutoRow(MenuCtx c, UiLayout L)
        {
            var g = c.G; var cb = SqBase(g, c.St);
            if (cb == null) return;
            var items = new[] { ("scav", "AUTO-SCAVENGE"), ("rescue", "AUTO-RESCUE"), ("conv", "AUTO-CONVERT") };
            var cols = UiLayout.Even(L.Top(BTN_H), 3, 10f);
            for (int i = 0; i < items.Length; i++)
            {
                string key = items[i].Item1; int bid = cb.Id;
                bool on = g.CampAuto(cb, key);
                c.Btn(cols[i], items[i].Item2 + (on ? ": ON" : ": OFF"), on ? UiRole.Primary : UiRole.Neutral, () =>
                {
                    string e = c.Act("set_auto", new object[] { key, !on, bid });
                    if (e != "") c.Toast(e);
                }, true, on, 18);
            }
        }

        // ---------------- SQUADS (main.gd _show_squads) ----------------
        static string SquadStatus(Game g, Squad q)
        {
            switch (q.St)
            {
                case "idle": return "standing by";
                case "out": return q.Mode == "scav" ? "heading out to search" : "out clearing";
                case "search": return "searching buildings";
                case "attack": return "attacking a camp";
                case "return": return "heading home";
                case "travel": return string.Format("on the road to {0} ({1:0}h left)", Game.MAP_NAME.TryGetValue(q.Dest ?? "", out var dn) ? dn : "another map", Math.Max(0.0, (q.Tarr ?? g.S.T) - g.S.T));
                case "field": return string.Format("holding the entry road in {0}", Game.MAP_NAME.TryGetValue(q.CurMap ?? "", out var cn) ? cn : "another map");
            }
            return q.St;
        }

        static string MemberLine(Game g, Squad q)
        {
            var mem = g.SquadMembers(q);
            if (mem.Count == 0) return "Nobody left.";
            return string.Join(", ", mem.Select(p => string.Format("{0} ({1}{2}{3})", p.First, Data.JOB_SHORT.TryGetValue(p.Job, out var js) ? js : p.Job,
                p.Wpn != "" && Data.WEAPONS.TryGetValue(p.Wpn, out var wd) ? ", " + wd.Name.ToLowerInvariant() : "", p.Hp < 90.0 ? string.Format(", hurt {0}%", (int)p.Hp) : "")));
        }

        static void Squads(MenuCtx c)
        {
            var g = c.G; var st = c.St;
            if (st.Sub == "form") { FormSquad(c); return; }
            if (st.Sub == "map") { SendMap(c); return; }
            var L = Frame(c, "SQUADS");
            // footer: FORM NEW SQUAD · DRONE SCOUT · GUNSHIP STRIKE
            var fb = new List<BarBtn>();
            if (g.HasUnlock("air_strike")) fb.Add(new BarBtn("GUNSHIP STRIKE", UiRole.Danger, () => c.Target?.Invoke("strike", -1), true, 230f));
            if (g.HasUnlock("drone")) fb.Add(new BarBtn("DRONE SCOUT", UiRole.Primary, () => c.Target?.Invoke("drone", -1), true, 210f));
            fb.Add(new BarBtn("FORM NEW SQUAD", UiRole.Accent, () => { st.Sub = "form"; st.Pick.Clear(); st.FPage = 0; }, true, 250f));
            Footer(c, L, "Each trip costs 3 ammo per member.", fb.ToArray());
            CampSwitch(c, L);
            CampAutoRow(c, L);
            var cb = SqBase(g, st);
            var view = L.Rest;
            float cw = view.width - 12f;
            string intro = string.Format("Each camp fields its own squads (up to {0}) from the people who live there; they leave from and return to its gate. CLEAR ROAD: cleared ground becomes yours to fence. SCAVENGE: search the buildings around a spot anywhere in range, also beyond the auto-scavenge radius (searched buildings get an X). Up to {1} people per squad, {2} tiles of road: Scouting research raises both.",
                Game.SQUAD_MAX, g.SquadSizeMax(), g.SquadRange());
            float ih = c.H_(intro, 15, false, cw);
            var cs = cb != null ? g.CampSquads(cb.Id) : new List<Squad>();
            const float PAD = 10f;
            var hs = new List<float>(); var ml = new List<(string mem, float mh)>();
            foreach (var q in cs)
            {
                string m = MemberLine(g, q);
                float mh = c.H_(m, 15, false, cw - 36f);
                ml.Add((m, mh));
                hs.Add(PAD + 30f + mh + (q.St == "travel" ? 0f : 8f + BTN_H) + PAD);   // a squad on the road has no buttons
            }
            float emptyH = cs.Count == 0 ? 36f : 0f;
            c.Scroll("sq_list_" + (cb?.Id ?? -1), view, ih + 10f + emptyH + Total(hs, ROW_GAP));
            c.Para(new Rect(0, 0, cw, ih), intro, 15, INK2);
            float y = ih + 10f;
            if (cs.Count == 0) c.Fit(new Rect(0, y, cw, 30f), string.Format("No squads at {0} yet. FORM NEW SQUAD picks the people.", cb?.Name ?? "this camp"), 18, INK, false);
            y += emptyH;
            bool scavOk = g.HasUnlock("squad_scav");
            for (int i = 0; i < cs.Count; i++)
            {
                var q = cs[i]; int sid = q.Id;
                var card = new Rect(0, y, cw, hs[i]);
                Card(c, card, i, AMBER, q.St != "idle");
                float tx = card.x + 18f, ty = card.y + PAD - 2f, tw = cw - 36f;
                c.Fit(new Rect(tx, ty, tw, 28f), string.Format("{0} · {1} · {2} kills · {3} blocks cleared", q.Name, SquadStatus(g, q), q.Kills, q.Cleared), 20, AMBER, true); ty += 30f;
                c.Para(new Rect(tx, ty, tw, ml[i].mh), ml[i].mem, 15, INK); ty += ml[i].mh + 8f;
                var B = new UiLayout(new Rect(tx, ty, tw, BTN_H), 10f);
                if (q.St == "idle")
                {
                    c.Btn(B.Left(190f), "CLEAR ROAD", UiRole.Accent, () => c.Target?.Invoke("clear", sid));
                    c.Btn(B.Left(scavOk ? 180f : 300f), scavOk ? "SCAVENGE" : "SCAVENGE (Scouting T1)", UiRole.Primary, () => c.Target?.Invoke("scav", sid), scavOk);
                    c.Btn(B.Left(200f), "SEND TO MAP", UiRole.Primary, () => { st.Sub = "map"; st.MapSid = sid; });
                    c.Btn(B.Right(170f), "DISBAND", UiRole.Danger, () => { string e = c.Act("disband_squad", new object[] { sid }); if (e != "") c.Toast(e); });
                }
                else if (q.St == "field") c.Btn(B.Left(200f), "SEND TO MAP", UiRole.Accent, () => { st.Sub = "map"; st.MapSid = sid; });
                else if (q.St != "travel") c.Btn(B.Left(180f), "RECALL", UiRole.Primary, () => { string e = c.Act("recall_squad", new object[] { sid }); c.Toast(e != "" ? e : q.Name + " is heading home."); });
                y += hs[i] + ROW_GAP;
            }
            c.EndScroll();
        }

        /// <summary>main.gd _show_form_squad: tick 2..max people who live at the camp and are home.</summary>
        static void FormSquad(MenuCtx c)
        {
            var g = c.G; var st = c.St;
            var cb = SqBase(g, st);
            int cbid = cb?.Id ?? -1;
            var L = Frame(c, "FORM A SQUAD  ·  " + (cb?.Name ?? "Camp"), () => st.Sub = "");
            Footer(c, L, string.Format("{0} picked  ·  2-{1} people", st.Pick.Count, g.SquadSizeMax()), new BarBtn("FORM SQUAD", UiRole.Accent, () =>
            {
                string e = c.Act("form_squad", new object[] { st.Pick.ToList(), "", cbid });
                if (e != "") { c.Toast(e); return; }
                st.Pick.Clear(); st.Sub = "";
            }, st.Pick.Count >= 2, 240f));
            string intro = string.Format("Pick 2-{0} people who live here and are at camp (Scouting research allows bigger squads). Soldiers and Cops fight best; anyone in a squad gets armed from the Armory first.", g.SquadSizeMax());
            float ih = c.H_(intro, 15, false, L.Rest.width);
            c.Para(L.Top(ih), intro, 15, INK2);
            var order = new[] { "Soldier", "Cop", "Scavenger", "Civilian", "Engineer", "Farmer", "Medic", "Scientist" };
            var ppl = g.AlivePeople().Where(q => q.Base == cbid && q.St == "base" && q.Sq == 0 && !(q.Inf >= 0.0 && q.Known) && !g.IsMinor(q)).ToList();
            ppl = ppl.Select((q, i) => (q, i)).OrderBy(t => { int k = Array.IndexOf(order, t.q.Job); return k < 0 ? 99 : k; }).ThenBy(t => t.i).Select(t => t.q).ToList();
            st.Pick.RemoveWhere(id => !ppl.Any(q => q.Id == id));
            var view = L.Rest;
            if (ppl.Count == 0) { c.Fit(new Rect(view.x, view.y, view.width, 30f), string.Format("Nobody free at {0} right now.", cb?.Name ?? "this camp"), 18, INK, false); return; }
            int cols = 2, rows = (ppl.Count + cols - 1) / cols;
            float cw = c.Scroll("sq_form_" + cbid, view, rows * (BTN_H + ROW_GAP) - ROW_GAP);
            for (int k = 0; k < ppl.Count; k++)
            {
                var q = ppl[k];
                int pid = q.Id; bool on = st.Pick.Contains(pid);
                string wn = q.Wpn != "" && Data.WEAPONS.TryGetValue(q.Wpn, out var wd) ? " · " + wd.Name : "";
                var r = UiLayout.Cell(new Rect(0, 0, cw, 0), cols, k, BTN_H, ROW_GAP);
                c.Btn(r, string.Format("{0}  {1} · {2} · hp {3}{4}", on ? "[x]" : "[  ]", q.Name, q.Job, (int)q.Hp, wn), on ? UiRole.Accent : UiRole.Neutral,
                    () => { if (!st.Pick.Remove(pid)) st.Pick.Add(pid); }, true, on, 18);
            }
            c.EndScroll();
        }

        static readonly string[] MAP_IDS = { "city", "small_town", "farmland", "military", "highway" };

        /// <summary>main.gd _show_send_map: travel to another map (send_squad_map).</summary>
        static void SendMap(MenuCtx c)
        {
            var g = c.G; var st = c.St;
            var q = g.SquadById(st.MapSid);
            if (q == null) { st.Sub = ""; Squads(c); return; }
            var L = Frame(c, "SEND " + q.Name.ToUpperInvariant() + " TO MAP", () => st.Sub = "");
            var view = L.Rest;
            float cw = view.width - 12f;
            string intro = string.Format("Roads: City - Small Town, Forest - Farmland (City and Forest are one map). Each hop takes {0:0} h by vehicle ({1}x on foot) and fuel per vehicle, paid on departure; vehicle armour softens road ambushes. They arrive at the map's entry road and move into your camp there, or hold the spot so you can set up an outpost.", Game.HOP_HOURS, (int)Game.HOP_WALK_MULT);
            float ih = c.H_(intro, 15, false, cw);
            const float RH = 76f;
            c.Scroll("sq_map", view, ih + 10f + MAP_IDS.Length * (RH + ROW_GAP) - ROW_GAP);
            c.Para(new Rect(0, 0, cw, ih), intro, 15, INK2);
            float y = ih + 10f; int k = 0;
            foreach (var rid in MAP_IDS)
            {
                var info = g.TravelInfoOf(q.Id, rid);
                string nm = rid == "city" ? "City / Forest" : (Data.Instance.REGIONS.TryGetValue(rid, out var rd) ? rd.Name : rid);
                string desc = info.Err;
                if (desc == "") desc = string.Format("{0} hop{1} · {2:0} h · {3}", info.Hops, info.Hops != 1 ? "s" : "", info.Hours, info.Veh != "" ? string.Format("{0}, {1:0} fuel", info.Veh.ToLowerInvariant(), info.Fuel) : "on foot");
                var card = new Rect(0, y, cw, RH);
                Card(c, card, k++, info.Err == "" ? GREEN : new Color(0.36f, 0.36f, 0.32f));
                var cols = UiLayout.Columns(UiLayout.Inset(card, 18f, 8f, 10f, 8f), 10f, 1f, -150f);
                var ln = UiLayout.Rows(cols[0], 2f, 1.2f, 1f);
                c.Fit(ln[0], nm, 20, info.Err == "" ? INK : INK2, true);
                c.Fit(ln[1], desc, 15, INK2, false);
                string r2 = rid; int sid = q.Id;
                c.Btn(UiLayout.Center(cols[1], cols[1].width, BTN_H), "GO", UiRole.Accent, () =>
                {
                    string e = c.Act("send_squad_map", new object[] { sid, r2 });
                    c.Toast(e != "" ? e : q.Name + " is on the road.");
                    if (e == "") st.Sub = "";
                }, info.Err == "");
                y += RH + ROW_GAP;
            }
            c.EndScroll();
        }

        // ---------------- map targets (main.gd _preview_squad / _start_air) ----------------
        /// <summary>Text and overlay for a squad / drone / strike target. kind: clear | scav | drone | strike.
        /// tile.x &lt; 0 = nothing tapped yet.</summary>
        public static TargetInfo SquadTarget(Game g, string kind, int sid, Vector2Int tile)
        {
            var o = new TargetInfo();
            var D = Data.Instance;
            if (kind == "drone" || kind == "strike")
            {
                o.Title = kind == "drone" ? "Tap where the drone should scout (3 fuel)." : "Tap where the gunship strikes (20 fuel, 30 ammo, kills everything within 7 tiles).";
                o.Radius = kind == "drone" ? 2.5f * Game.SECT : 7f;
                if (tile.x < 0 || !D.InMap(tile)) { o.Detail = "Tap the map."; return o; }
                o.Detail = string.Format("{0} · infection {1}%", D.DistrictAt(tile).Name, (int)(g.InfAt(tile) * 100.0));
                o.Ok = true;
                return o;
            }
            var q = g.SquadById(sid);
            if (q == null) { o.Title = "That squad is gone."; return o; }
            var b = g.BaseById(q.Base);
            string bn = b?.Name ?? "base";
            o.Title = kind == "scav"
                ? string.Format("{0}: tap a building or block to search (up to {1} tiles of road from {2}). Best used outside the dashed auto-scavenge circle, where nobody goes on their own.", q.Name, g.SquadRange(), bn)
                : string.Format("{0}: tap the end of the road to clear (up to {1} tiles from {2}).", q.Name, g.SquadRange(), bn);
            if (tile.x < 0 || !D.InMap(tile)) { o.Detail = "Tap a target on the map."; return o; }
            int l = D.BuildingAt(tile);
            var path = g.SquadRoute(q, kind == "scav" && l >= 0 ? D.LotDoor(l) : tile);
            o.Path = path;
            if (path.Count < 2) { o.Detail = "No way to get there."; return o; }
            var rk = g.SquadRiskOf(q, path);
            if (kind == "scav")
            {
                var tg = g.SquadScavTargets(q, V.I(path[path.Count - 1]));
                o.Search = tg;
                bool inside = g.InAutoZone(V.F(tile));
                o.Detail = string.Format("{0} unsearched buildings within {1:0} tiles{2} · {3} tiles of road · avg infection {4}% · expected casualties {5:0.0} of {6}",
                    tg.Count, g.SquadSearchR(), inside ? " (inside your auto radius)" : " (beyond the auto radius)", path.Count, (int)(rk.Inf * 100.0), rk.Loss, rk.N);
                if (tg.Count == 0) o.Detail = "Nothing left to search there (X = already searched).";
                else o.Ok = true;
            }
            else
            {
                o.Detail = string.Format("{0} tiles of road, {1} blocks · avg infection {2}% · expected casualties {3:0.0} of {4}", path.Count, rk.Sectors, (int)(rk.Inf * 100.0), rk.Loss, rk.N);
                o.Ok = true;
            }
            if (path.Count > g.SquadRange()) { o.Detail = string.Format("Too far ({0} tiles, max {1}). Scouting research reaches further.", path.Count, g.SquadRange()); o.Ok = false; }
            return o;
        }

        public static Rect TargetBarRect(float w, float h) { float pw = Math.Min(900f, w - 200f), ph = 176f; return new Rect(w / 2f - pw / 2f, h - 62f - ph - 10f, pw, ph); }

        /// <summary>The bottom bar while a map target is being picked (main.gd toolbar: text, info, SEND, CANCEL).</summary>
        public static void TargetBar(MenuCtx c, TargetInfo info, string sendLabel, Action send, Action cancel)
        {
            var p = TargetBarRect(c.W, c.H);
            var L = new UiLayout(c.Panel(p), 8f);
            var B = new UiLayout(L.Bottom(BTN_H), 10f);
            c.Btn(B.Right(150f), "CANCEL", UiRole.Neutral, cancel);
            c.Btn(B.Right(170f), sendLabel, UiRole.Accent, send, info.Ok);
            float th = Math.Min(L.Rest.height, c.H_(info.Title, 16, false, L.Rest.width));
            c.Para(L.Top(th), info.Title, 16, INK);
            c.Para(UiLayout.Inset(B.Rest, 0f, 0f, 0f, 0f), info.Detail, 15, info.Ok ? AMBER : RED);
            Flush(c);
        }

        // ---------------- TASK FORCE (main.gd _show_taskforce) ----------------
        static void TaskForce(MenuCtx c)
        {
            var g = c.G; var st = c.St;
            var L = Frame(c, "TASK FORCE");
            Footer(c, L, "Supply runs: tap a building on the map, then SEND TASK FORCE TO LOOT.");
            CampSwitch(c, L);
            CampAutoRow(c, L);
            var cb = SqBase(g, st);
            int teamN = cb != null ? g.PickTeam(V.F(cb.Pos), 3, cb.Id).Count : 0;
            var gs = g.S.Groups.Where(gr => gr.St == "wait").ToList();
            if (cb != null) { var cp = V.F(cb.Pos); gs = gs.Select((gr, i) => (gr, i)).OrderBy(t => V.F(t.gr.Pos).DistTo(cp)).ThenBy(t => t.i).Select(t => t.gr).ToList(); }
            var view = L.Rest;
            float cw = view.width - 12f;
            string intro = string.Format("Send up to 3 fighters from {0} to rescue survivors. They leave from and come back to its gate; the rescued get beds there, or at your nearest camp with room when it's full. They route around zombie-heavy blocks, but anyone who runs into the dead might fight, die, or come back bitten and hide it.", cb?.Name ?? "this camp");
            float ih = c.H_(intro, 15, false, cw);
            const float RH = 76f;
            float head = ih + 8f + 32f + 8f + (gs.Count == 0 ? 36f : 0f);
            c.Scroll("tf_list_" + (cb?.Id ?? -1), view, head + Math.Max(0f, gs.Count * (RH + ROW_GAP) - ROW_GAP));
            c.Para(new Rect(0, 0, cw, ih), intro, 15, INK2);
            float y = ih + 8f;
            c.Chip(new Rect(0, y, 300f, 32f), teamN > 0 ? "READY TO GO: " + teamN : "NOBODY FIT TO GO", teamN > 0 ? UiRole.Accent : UiRole.Danger, 16);
            if (teamN == 0) c.Fit(new Rect(310f, y, cw - 310f, 32f), "Nobody fit to go at this camp right now.", 16, RED, false);
            y += 40f;
            if (gs.Count == 0) { c.Fit(new Rect(0, y, cw, 30f), "No survivor groups on the radio right now.", 18, INK, false); y += 36f; }
            int cbid = cb?.Id ?? -1, k = 0;
            foreach (var gr in gs)
            {
                var card = new Rect(0, y, cw, RH);
                Card(c, card, k++, gr.Tf ? AMBER : GREEN, gr.Tf);
                var cols = UiLayout.Columns(UiLayout.Inset(card, 18f, 8f, 10f, 8f), 10f, 1f, -140f, -160f);
                var ln = UiLayout.Rows(cols[0], 2f, 1.2f, 1f);
                c.Fit(ln[0], string.Format("{0} ({1})", gr.Label, gr.N), 19, INK, true);
                c.Fit(ln[1], string.Format("{0} · infection {1}%", Data.Instance.DistrictAt(gr.Pos).Name, (int)(g.InfAt(gr.Pos) * 100.0)), 15, INK2, false);
                int gid = gr.Id;
                c.Btn(UiLayout.Center(cols[1], cols[1].width, BTN_H), "VIEW", UiRole.Neutral, () => { c.ViewGroup?.Invoke(gid); });
                if (gr.Tf) c.Fit(cols[2], "en route", 18, AMBER, true, MenuAlign.Center);
                else c.Btn(UiLayout.Center(cols[2], cols[2].width, BTN_H), "SEND", UiRole.Accent, () =>
                {
                    string e = c.Act("send_rescue", new object[] { gid, cbid });
                    if (e != "") c.Toast(e); else { c.Toast("The task force is heading out."); st.Open = ""; }
                });
                y += RH + ROW_GAP;
            }
            c.EndScroll();
        }

        public static Rect GroupSheetRect(float w, float h) { float pw = Math.Min(860f, w - 200f), ph = 240f; return new Rect(w / 2f - pw / 2f, h - 62f - ph - 10f, pw, ph); }

        /// <summary>main.gd _sheet_group: the SOS sheet for a survivor group tapped on the map.</summary>
        public static void GroupSheet(MenuCtx c, int gid, Action close)
        {
            GroupSheetBody(c, gid, close);
            Flush(c);
        }

        static void GroupSheetBody(MenuCtx c, int gid, Action close)
        {
            var g = c.G;
            var gr = g.GroupById(gid);
            if (gr == null || gr.St != "wait") { close?.Invoke(); return; }
            var p = GroupSheetRect(c.W, c.H);
            var L = new UiLayout(c.Panel(p), 8f);
            var T = new UiLayout(L.Top(48f), 10f);
            c.Btn(T.Right(140f), "CLOSE", UiRole.Danger, close);
            c.Header(new Rect(T.Rest.x, T.Rest.y + 2f, T.Rest.width, 42f), "SOS: " + gr.Label, 22);
            c.Fit(L.Top(26f), string.Format("{0} people holding out in {1}. Area infection {2}%.", gr.N, Data.Instance.DistrictAt(gr.Pos).Name, (int)(g.InfAt(gr.Pos) * 100.0)), 17, INK2, false);
            var B = new UiLayout(L.Bottom(BTN_H), 10f);
            if (gr.Tf) { c.Fit(L.Top(26f), "A task force is on the way.", 17, AMBER, true); }
            else
            {
                var team = g.PickTeam(V.F(gr.Pos), 3);
                if (team.Count == 0) c.Fit(L.Top(26f), "Nobody fit to go (soldiers, cops, scavengers or civilians at a camp).", 16, RED, false);
                else
                {
                    c.Fit(L.Top(24f), "Team: " + string.Join(", ", team.Select(q => string.Format("{0} ({1})", q.First, Data.JOB_SHORT.TryGetValue(q.Job, out var js) ? js : q.Job))), 16, INK, false);
                    c.Fit(L.Top(22f), "They'll pick a route around the worst zombie areas.", 15, INK2, false);
                    c.Btn(B.Left(300f), "SEND TASK FORCE", UiRole.Accent, () =>
                    {
                        string e = c.Act("send_rescue", new object[] { gid });
                        c.Toast(e != "" ? e : "The task force is heading out.");
                    });
                }
            }
            if (g.HasUnlock("heli"))
                c.Btn(B.Left(Math.Min(360f, B.Rest.width)), "HELICOPTER RESCUE", UiRole.Primary, () =>
                {
                    string e = c.Act("heli_rescue", new object[] { gid });
                    c.Toast(e != "" ? e : "The helicopter is on its way.");
                }, true, false, 19, "12 fuel");
        }
    }
}
