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

        static float CampSwitch(MenuCtx c, float x, float iw, float y)
        {
            var g = c.G; var st = c.St;
            var cl = CampList(g); var cb = SqBase(g, st);
            if (cb == null) return y;
            int ix = Math.Max(0, cl.FindIndex(b => b.Id == cb.Id));
            int ns = g.CampSquads(cb.Id).Count;
            string t = string.Format("{0}  ·  {1} / {2} beds  ·  {3} squad{4}{5}", cb.Name, g.CampResidents(cb.Id), g.BaseBeds(cb), ns, ns == 1 ? "" : "s",
                cl.Count > 1 ? string.Format("  ({0} / {1})", ix + 1, cl.Count) : "");
            bool many = cl.Count > 1;
            c.Button(new Rect(x, y, 64, 46), "<", "olive", 22, () => { st.Camp = cl[(ix - 1 + cl.Count) % cl.Count].Id; st.SqPage = st.TfPage = 0; }, many);
            c.Label(new Rect(x + 72, y + 10, iw - 144, 28), t, 18, cb.Main ? CAMP_MAIN : CAMP_OUT, true, false, MenuAlign.Center);
            c.Button(new Rect(x + iw - 64, y, 64, 46), ">", "olive", 22, () => { st.Camp = cl[(ix + 1) % cl.Count].Id; st.SqPage = st.TfPage = 0; }, many);
            return y + 52f;
        }

        /// <summary>main.gd _camp_auto_row: this camp's auto-scavenge / auto-rescue / auto-convert switches (set_auto key on bid).</summary>
        static float CampAutoRow(MenuCtx c, float x, float iw, float y)
        {
            var g = c.G; var cb = SqBase(g, c.St);
            if (cb == null) return y;
            var items = new[] { ("scav", "AUTO-SCAVENGE"), ("rescue", "AUTO-RESCUE"), ("conv", "AUTO-CONVERT") };
            float bw = (iw - 16f) / 3f;
            for (int i = 0; i < items.Length; i++)
            {
                string key = items[i].Item1; int bid = cb.Id;
                bool on = g.CampAuto(cb, key);
                c.Button(new Rect(x + i * (bw + 8f), y, bw, 44), items[i].Item2 + ": " + (on ? "ON" : "OFF"), on ? "sel" : "dark", 16, () =>
                {
                    string e = c.Act("set_auto", new object[] { key, !on, bid });
                    if (e != "") c.Toast(e);
                });
            }
            return y + 50f;
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
            var p = Frame(c, "SQUADS");
            float x = p.x + 18, iw = p.width - 36, y = p.y + 66;
            y = CampSwitch(c, x, iw, y);
            y = CampAutoRow(c, x, iw, y);
            var cb = SqBase(g, st);
            string intro = string.Format("Each camp fields its own squads (up to {0}) from the people who live there; they leave from and return to its gate. CLEAR ROAD: cleared ground becomes yours to fence. SCAVENGE: search the buildings around a spot anywhere in range, also beyond the auto-scavenge radius (searched buildings get an X). Up to {1} people per squad, {2} tiles of road: Scouting research raises both. Each trip costs 3 ammo per member.",
                Game.SQUAD_MAX, g.SquadSizeMax(), g.SquadRange());
            float ih = c.H_(intro, 14, false, iw);
            c.Label(new Rect(x, y, iw, ih), intro, 14, INK2); y += ih + 8f;

            var cs = cb != null ? g.CampSquads(cb.Id) : new List<Squad>();
            float bottom = p.yMax - 70f;
            if (cs.Count == 0) c.Label(new Rect(x, y, iw, 28), string.Format("No squads at {0} yet.", cb?.Name ?? "this camp"), 16, INK);
            const float PAD = 8f, BH = 46f;
            var hs = new List<float>(); var ml = new List<(string mem, float mh)>();
            foreach (var q in cs)
            {
                string m = MemberLine(g, q);
                float mh = c.H_(m, 14, false, iw - 32f);
                ml.Add((m, mh));
                hs.Add(PAD + 26f + mh + (q.St == "travel" ? 0f : 6f + BH) + PAD);   // a squad on the road has no buttons
            }
            var pages = Pages(hs, bottom - y, 6f);
            st.SqPage = Math.Max(0, Math.Min(pages.Count - 1, st.SqPage));
            int from = pages[st.SqPage], to = st.SqPage + 1 < pages.Count ? pages[st.SqPage + 1] : cs.Count;
            bool scavOk = g.HasUnlock("squad_scav");
            for (int i = from; i < to; i++)
            {
                var q = cs[i]; int sid = q.Id;
                var card = new Rect(x, y, iw, hs[i]);
                c.Box(card, new Color(0.15f, 0.15f, 0.12f));
                c.Box(new Rect(card.x, card.y, 4, card.height), AMBER);
                float ty = card.y + PAD - 2f;
                c.Label(new Rect(card.x + 16, ty, iw - 32, 26), string.Format("{0} · {1} · {2} kills · {3} blocks cleared", q.Name, SquadStatus(g, q), q.Kills, q.Cleared), 17, AMBER, true, false); ty += 26f;
                c.Label(new Rect(card.x + 16, ty, iw - 32, ml[i].mh), ml[i].mem, 14, INK); ty += ml[i].mh + 6f;
                float bx = card.x + 16;
                void B(string t, string style, float w, Action a, bool on = true) { c.Button(new Rect(bx, ty, w, BH), t, style, 16, a, on); bx += w + 8f; }
                if (q.St == "idle")
                {
                    B("CLEAR ROAD", "sel", 150, () => c.Target?.Invoke("clear", sid));
                    B(scavOk ? "SCAVENGE" : "SCAVENGE (Scouting T1)", "olive", scavOk ? 150 : 250, () => c.Target?.Invoke("scav", sid), scavOk);
                    B("SEND TO MAP", "olive", 160, () => { st.Sub = "map"; st.MapSid = sid; });
                    B("DISBAND", "brown", 130, () => { string e = c.Act("disband_squad", new object[] { sid }); if (e != "") c.Toast(e); });
                }
                else if (q.St == "field") B("SEND TO MAP", "sel", 160, () => { st.Sub = "map"; st.MapSid = sid; });
                else if (q.St != "travel") B("RECALL", "olive", 130, () => { string e = c.Act("recall_squad", new object[] { sid }); c.Toast(e != "" ? e : q.Name + " is heading home."); });
                y += hs[i] + 6f;
            }
            // bottom row: FORM NEW SQUAD · DRONE SCOUT · GUNSHIP STRIKE (left), pager (right)
            float by = p.yMax - 60f, fx = p.x + 18;
            c.Button(new Rect(fx, by, 210, 50), "FORM NEW SQUAD", "olive", 17, () => { st.Sub = "form"; st.Pick.Clear(); st.FPage = 0; }); fx += 218;
            if (g.HasUnlock("drone")) { c.Button(new Rect(fx, by, 170, 50), "DRONE SCOUT", "olive", 17, () => c.Target?.Invoke("drone", -1)); fx += 178; }
            if (g.HasUnlock("air_strike")) { c.Button(new Rect(fx, by, 190, 50), "GUNSHIP STRIKE", "brown", 17, () => c.Target?.Invoke("strike", -1)); fx += 198; }
            if (pages.Count > 1) Pager(c, p, st.SqPage, pages.Count, v => st.SqPage = v, "");   // its buttons start at xMax - 392, right of the row above
        }

        /// <summary>main.gd _show_form_squad: tick 2..max people who live at the camp and are home.</summary>
        static void FormSquad(MenuCtx c)
        {
            var g = c.G; var st = c.St;
            var p = Frame(c, "FORM A SQUAD");
            c.Button(new Rect(p.xMax - 284, p.y + 10, 130, 48), "< BACK", "olive", 18, () => st.Sub = "");
            float x = p.x + 18, iw = p.width - 36, y = p.y + 66;
            var cb = SqBase(g, st);
            int cbid = cb?.Id ?? -1;
            string intro = string.Format("{0}: pick 2-{1} people who live here and are at camp (Scouting research allows bigger squads). Soldiers and Cops fight best; anyone in a squad gets armed from the Armory first.", cb?.Name ?? "Camp", g.SquadSizeMax());
            float ih = c.H_(intro, 14, false, iw);
            c.Label(new Rect(x, y, iw, ih), intro, 14, INK2); y += ih + 8f;
            var order = new[] { "Soldier", "Cop", "Scavenger", "Civilian", "Engineer", "Farmer", "Medic", "Scientist" };
            var ppl = g.AlivePeople().Where(q => q.Base == cbid && q.St == "base" && q.Sq == 0 && !(q.Inf >= 0.0 && q.Known) && !g.IsMinor(q)).ToList();
            ppl = ppl.Select((q, i) => (q, i)).OrderBy(t => { int k = Array.IndexOf(order, t.q.Job); return k < 0 ? 99 : k; }).ThenBy(t => t.i).Select(t => t.q).ToList();
            st.Pick.RemoveWhere(id => !ppl.Any(q => q.Id == id));
            c.Label(new Rect(x, y, iw, 24), string.Format("{0} picked", st.Pick.Count), 16, AMBER, true, false); y += 30f;
            if (ppl.Count == 0) c.Label(new Rect(x, y, iw, 28), string.Format("Nobody free at {0} right now.", cb?.Name ?? "this camp"), 16, INK);
            const int COLS = 2; const float RH = 48f, GAP = 6f;
            float cw = (iw - GAP) / COLS;
            int rows = Math.Max(1, (int)((p.yMax - 70f - y + GAP) / (RH + GAP)));
            int per = rows * COLS, pages = Math.Max(1, (ppl.Count + per - 1) / per);
            st.FPage = Math.Max(0, Math.Min(pages - 1, st.FPage));
            int k2 = 0;
            foreach (var q in ppl.Skip(st.FPage * per).Take(per))
            {
                int pid = q.Id; bool on = st.Pick.Contains(pid);
                string wn = q.Wpn != "" && Data.WEAPONS.TryGetValue(q.Wpn, out var wd) ? " · " + wd.Name : "";
                var r = new Rect(x + (k2 % COLS) * (cw + GAP), y + (k2 / COLS) * (RH + GAP), cw, RH);
                c.Button(r, string.Format("{0}  {1} · {2} · hp {3}{4}", on ? "[x]" : "[  ]", q.Name, q.Job, (int)q.Hp, wn), on ? "sel" : "dark", 16, () => { if (!st.Pick.Remove(pid)) st.Pick.Add(pid); });
                k2++;
            }
            float by = p.yMax - 60f;
            c.Button(new Rect(p.x + 18, by, 240, 50), "FORM SQUAD", "sel", 18, () =>
            {
                string e = c.Act("form_squad", new object[] { st.Pick.ToList(), "", cbid });
                if (e != "") { c.Toast(e); return; }
                st.Pick.Clear(); st.Sub = "";
            }, st.Pick.Count >= 2);
            if (pages > 1) Pager(c, p, st.FPage, pages, v => st.FPage = v, "");
        }

        static readonly string[] MAP_IDS = { "city", "small_town", "farmland", "military", "highway" };

        /// <summary>main.gd _show_send_map: travel to another map (send_squad_map).</summary>
        static void SendMap(MenuCtx c)
        {
            var g = c.G; var st = c.St;
            var q = g.SquadById(st.MapSid);
            if (q == null) { st.Sub = ""; Squads(c); return; }
            var p = Frame(c, "SEND " + q.Name.ToUpperInvariant() + " TO MAP");
            c.Button(new Rect(p.xMax - 284, p.y + 10, 130, 48), "< BACK", "olive", 18, () => st.Sub = "");
            float x = p.x + 18, iw = p.width - 36, y = p.y + 66;
            string intro = string.Format("Roads: City ↔ Small Town, Forest ↔ Farmland (City and Forest are one map). Each hop takes {0:0} h by vehicle ({1}x on foot) and fuel per vehicle, paid on departure; vehicle armour softens road ambushes. They arrive at the map's entry road and move into your camp there, or hold the spot so you can set up an outpost.", Game.HOP_HOURS, (int)Game.HOP_WALK_MULT);
            float ih = c.H_(intro, 14, false, iw);
            c.Label(new Rect(x, y, iw, ih), intro, 14, INK2); y += ih + 10f;
            const float RH = 66f;
            foreach (var rid in MAP_IDS)
            {
                var info = g.TravelInfoOf(q.Id, rid);
                string nm = rid == "city" ? "City / Forest" : (Data.Instance.REGIONS.TryGetValue(rid, out var rd) ? rd.Name : rid);
                string desc = info.Err;
                if (desc == "") desc = string.Format("{0} hop{1} · {2:0} h · {3}", info.Hops, info.Hops != 1 ? "s" : "", info.Hours, info.Veh != "" ? string.Format("{0}, {1:0} fuel", info.Veh.ToLowerInvariant(), info.Fuel) : "on foot");
                var card = new Rect(x, y, iw, RH);
                c.Box(card, new Color(0.13f, 0.14f, 0.12f));
                c.Box(new Rect(card.x, card.y, 4, RH), info.Err == "" ? GREEN : new Color(0.30f, 0.30f, 0.28f));
                c.Label(new Rect(card.x + 16, card.y + 6, iw - 150, 24), nm, 17, info.Err == "" ? INK : INK2, true, false);
                c.Label(new Rect(card.x + 16, card.y + 32, iw - 150, 30), desc, 14, INK2);
                string r2 = rid; int sid = q.Id;
                c.Button(new Rect(card.xMax - 120, card.y + 10, 108, 46), "GO", "sel", 18, () =>
                {
                    string e = c.Act("send_squad_map", new object[] { sid, r2 });
                    c.Toast(e != "" ? e : q.Name + " is on the road.");
                    if (e == "") st.Sub = "";
                }, info.Err == "");
                y += RH + 6f;
            }
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
            c.Box(p, PANEL);
            c.Box(new Rect(p.x, p.y, p.width, 3), AMBER);
            float x = p.x + 16, iw = p.width - 32, y = p.y + 10;
            float th = Math.Min(60f, c.H_(info.Title, 15, false, iw));
            c.Label(new Rect(x, y, iw, th), info.Title, 15, INK); y += th + 4f;
            float bw = 300f;
            c.Label(new Rect(x, y, iw - bw - 12f, p.yMax - 8f - y), info.Detail, 14, info.Ok ? AMBER : RED);
            c.Button(new Rect(p.xMax - 16 - 290, p.yMax - 62, 150, 52), sendLabel, "sel", 18, send, info.Ok);
            c.Button(new Rect(p.xMax - 16 - 132, p.yMax - 62, 132, 52), "CANCEL", "dark", 18, cancel);
        }

        // ---------------- TASK FORCE (main.gd _show_taskforce) ----------------
        static void TaskForce(MenuCtx c)
        {
            var g = c.G; var st = c.St;
            var p = Frame(c, "TASK FORCE");
            float x = p.x + 18, iw = p.width - 36, y = p.y + 66;
            y = CampSwitch(c, x, iw, y);
            var cb = SqBase(g, st);
            string intro = string.Format("Send up to 3 fighters from {0} to rescue survivors. They leave from and come back to its gate; the rescued get beds there, or at your nearest camp with room when it's full. They route around zombie-heavy blocks, but anyone who runs into the dead might fight, die, or come back bitten and hide it.", cb?.Name ?? "this camp");
            float ih = c.H_(intro, 15, false, iw);
            c.Label(new Rect(x, y, iw, ih), intro, 15, INK2); y += ih + 6f;
            y = CampAutoRow(c, x, iw, y);
            int teamN = cb != null ? g.PickTeam(V.F(cb.Pos), 3, cb.Id).Count : 0;
            c.Label(new Rect(x, y, iw, 24), teamN > 0 ? "Ready to go: " + teamN : "Nobody fit to go at this camp right now.", 16, teamN > 0 ? AMBER : RED, true, false); y += 30f;
            var gs = g.S.Groups.Where(gr => gr.St == "wait").ToList();
            if (cb != null) { var cp = V.F(cb.Pos); gs = gs.Select((gr, i) => (gr, i)).OrderBy(t => V.F(t.gr.Pos).DistTo(cp)).ThenBy(t => t.i).Select(t => t.gr).ToList(); }
            if (gs.Count == 0) c.Label(new Rect(x, y, iw, 28), "No survivor groups on the radio right now.", 17, INK);
            const float RH = 56f, GAP = 6f;
            int per = Math.Max(1, (int)((p.yMax - 70f - y + GAP) / (RH + GAP)));
            int pages = Math.Max(1, (gs.Count + per - 1) / per);
            st.TfPage = Math.Max(0, Math.Min(pages - 1, st.TfPage));
            int cbid = cb?.Id ?? -1;
            foreach (var gr in gs.Skip(st.TfPage * per).Take(per))
            {
                var card = new Rect(x, y, iw, RH);
                c.Box(card, new Color(0.13f, 0.15f, 0.12f));
                c.Box(new Rect(card.x, card.y, 4, RH), gr.Tf ? AMBER : GREEN);
                string t = string.Format("{0} ({1}) · {2} · infection {3}%", gr.Label, gr.N, Data.Instance.DistrictAt(gr.Pos).Name, (int)(g.InfAt(gr.Pos) * 100.0));
                c.Label(new Rect(card.x + 16, card.y + 4, iw - 250, RH - 8), t, 16, INK);
                int gid = gr.Id;
                c.Button(new Rect(card.xMax - 222, card.y + 6, 100, 44), "VIEW", "olive", 16, () => { c.ViewGroup?.Invoke(gid); });
                if (gr.Tf) c.Label(new Rect(card.xMax - 114, card.y + 16, 104, 24), "en route", 16, AMBER, true, false, MenuAlign.Center);
                else c.Button(new Rect(card.xMax - 114, card.y + 6, 104, 44), "SEND", "sel", 16, () =>
                {
                    string e = c.Act("send_rescue", new object[] { gid, cbid });
                    if (e != "") c.Toast(e); else { c.Toast("The task force is heading out."); st.Open = ""; }
                });
                y += RH + GAP;
            }
            Pager(c, p, st.TfPage, pages, v => st.TfPage = v, "Supply runs: tap a building on the map, then SEND TASK FORCE TO LOOT.");
        }

        public static Rect GroupSheetRect(float w, float h) { float pw = Math.Min(820f, w - 200f), ph = 230f; return new Rect(w / 2f - pw / 2f, h - 62f - ph - 10f, pw, ph); }

        /// <summary>main.gd _sheet_group: the SOS sheet for a survivor group tapped on the map.</summary>
        public static void GroupSheet(MenuCtx c, int gid, Action close)
        {
            var g = c.G;
            var gr = g.GroupById(gid);
            if (gr == null || gr.St != "wait") { close?.Invoke(); return; }
            var p = GroupSheetRect(c.W, c.H);
            c.Box(p, PANEL);
            c.Box(new Rect(p.x, p.y, p.width, 3), GREEN);
            float x = p.x + 16, iw = p.width - 32, y = p.y + 10;
            c.Label(new Rect(x, y, iw - 140, 30), "SOS: " + gr.Label, 22, GREEN, true, false);
            c.Button(new Rect(p.xMax - 126, p.y + 8, 112, 44), "CLOSE", "dark", 18, close);
            y += 44f;
            string s = string.Format("{0} people holding out in {1}. Area infection {2}%.", gr.N, Data.Instance.DistrictAt(gr.Pos).Name, (int)(g.InfAt(gr.Pos) * 100.0));
            c.Label(new Rect(x, y, iw, 24), s, 16, INK2, false, false); y += 28f;
            float by = p.yMax - 62f;
            if (gr.Tf) { c.Label(new Rect(x, y, iw, 24), "A task force is on the way.", 16, AMBER, true, false); }
            else
            {
                var team = g.PickTeam(V.F(gr.Pos), 3);
                if (team.Count == 0) c.Label(new Rect(x, y, iw, 44), "Nobody fit to go (soldiers, cops, scavengers or civilians at a camp).", 15, RED);
                else
                {
                    string tl = "Team: " + string.Join(", ", team.Select(q => string.Format("{0} ({1})", q.First, Data.JOB_SHORT.TryGetValue(q.Job, out var js) ? js : q.Job)));
                    c.Label(new Rect(x, y, iw, 24), tl, 15, INK, false, false); y += 24f;
                    c.Label(new Rect(x, y, iw, 22), "They'll pick a route around the worst zombie areas.", 14, INK2, false, false);
                    c.Button(new Rect(x, by, 280, 52), "SEND TASK FORCE", "sel", 18, () =>
                    {
                        string e = c.Act("send_rescue", new object[] { gid });
                        c.Toast(e != "" ? e : "The task force is heading out.");
                    });
                }
            }
            if (g.HasUnlock("heli"))
                c.Button(new Rect(x + 292, by, 330, 52), "HELICOPTER RESCUE (12 fuel)", "olive", 17, () =>
                {
                    string e = c.Act("heli_rescue", new object[] { gid });
                    c.Toast(e != "" ? e : "The helicopter is on its way.");
                });
        }
    }
}
