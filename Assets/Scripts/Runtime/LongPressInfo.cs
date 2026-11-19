// E19 (batch B1): long-press info. Holding a finger still on the map for 0.5 s shows an info card for what is under
// it - a survivor, a zombie, a wreck, a faction colony, one of your camps, or a building - with its name, type, HP,
// job / state and contents. It never issues an order: MapView only treats a touch shorter than 0.45 s as a tap, so the
// release after a long press does nothing. The card closes with CLOSE, after a few seconds, or on the next long press.
// * LongPressInfo (pure, also compiled by the offline preview): the card data and its layout.
// * MapView part: TickLongPress (one-line hook in MapView.Update) and the hit test.
// * GameUI part: draws the card.
using System;
using System.Collections.Generic;
using System.Linq;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public static class LongPressInfo
    {
        public sealed class Card
        {
            public string Title = "", Type = "";
            public readonly List<KeyValuePair<string, string>> Rows = new List<KeyValuePair<string, string>>();
            public Vector2 At;
            public void Row(string k, string v) => Rows.Add(new KeyValuePair<string, string>(k, string.IsNullOrEmpty(v) ? "-" : v));
        }

        public const float HOLD_S = 0.5f, SHOW_S = 8f;
        public static Card Current;
        public static float ShownAt = -100f;
        public static bool Showing => Current != null;

        public static void Clear() => Current = null;
        public static void Show(Card c, float now) { Current = c; ShownAt = now; }

        // ---------------- card builders (pure: Game + Data only) ----------------
        static string Goal(string g)
        {
            switch (g ?? "")
            {
                case "scav": return "scavenging";
                case "tf": return "on a supply run";
                case "hunt": return "hunting zombies";
                case "patrol": return "on patrol";
                case "repair": return "repairing the wall";
                case "build": return "building";
                case "rescue": return "on a rescue";
                case "sq": case "sqs": case "sqa": return "with a squad";
                case "home": return "heading home";
                case "": return "";
                default: return g;
            }
        }

        public static string Carry(ODict<string, double> carry)
        {
            if (carry == null) return "Nothing";
            var parts = new List<string>();
            foreach (var kv in carry) if (kv.Value >= 0.5) parts.Add(string.Format("{0} {1}", (int)Math.Round(kv.Value), kv.Key));
            return parts.Count == 0 ? "Nothing" : string.Join(", ", parts.Take(5));
        }

        public static Card Survivor(Game g, Person p)
        {
            var c = new Card { Title = p.Name, Type = "Survivor - " + p.Job, At = p.Pos };
            c.Row("HP", ((int)Math.Round(p.Hp)).ToString() + (p.Known && p.Inf >= 0.0 ? "  (bitten)" : ""));
            string st;
            switch (p.St)
            {
                case "base": st = p.Guard ? (g.OnDuty() ? "On guard duty" : "At camp, guard tonight") : p.Hold ? "Staying at camp" : "At camp"; break;
                case "move": st = "On the move" + (Goal(p.Goal) != "" ? ", " + Goal(p.Goal) : ""); break;
                case "work": st = "Working" + (Goal(p.Goal) != "" ? ", " + Goal(p.Goal) : ""); break;
                default: st = p.St; break;
            }
            c.Row("Job", p.Job);
            c.Row("State", st);
            var b = g.BaseById(p.Base);
            if (b != null) c.Row("Camp", g.BaseLabel(b));
            c.Row("Carrying", Carry(p.Carry));
            string gear = string.Join(", ", new[] { p.Wpn, p.Vest, p.Helm }.Where(x => !string.IsNullOrEmpty(x)));
            c.Row("Gear", gear == "" ? "None" : gear);
            if (p.Kills > 0) c.Row("Kills", p.Kills.ToString());
            return c;
        }

        public static Card Zombie(Game g, int i)
        {
            var at = g.ZPos(i);
            var c = new Card { Title = "Zombie", Type = "The dead", At = at };
            byte m = g.Zh[i];
            c.Row("HP", "Not tracked - one good hit drops it");
            c.Row("State", m == 1 ? "Moving with a horde" : m == 3 ? "Night raid - heading for your wall" : m == 0 ? "Wandering" : "Shambling");
            int near = 0;
            for (int k = 0; k < g.Zc.Count; k++) if (g.Zh[k] != 255 && (g.ZPos(k) - at).sqrMagnitude < 25f) near++;
            c.Row("Nearby", near <= 1 ? "Alone" : string.Format("{0} within 5 tiles", near));
            c.Row("Contents", "Nothing worth taking");
            return c;
        }

        public static Card WreckCard(Game g, Wreck w)
        {
            var vd = Data.VehDef(w.Type);
            var c = new Card { Title = vd.Name + " wreck", Type = "Vehicle wreck", At = new Vector2(w.Pos.x + 0.5f, w.Pos.y + 0.5f) };
            c.Row("HP", string.Format("{0}%", (int)Math.Round(Math.Max(0.0, w.Hp) * 100.0)));
            c.Row("State", w.Own >= 0 ? "Claimed - tow it home with a squad" : "Abandoned on the road");
            var need = w.Need != null ? w.Need.Select(n => Data.PART_LABEL.TryGetValue(n, out var l) ? l : n).ToList() : new List<string>();
            c.Row("Needs", need.Count == 0 ? "Nothing - just fuel" : string.Join(", ", need));
            c.Row("Fuel", ((int)Math.Round(w.Fuel)).ToString());
            c.Row("Seats / cargo", string.Format("{0} / {1}", vd.Seats, vd.Cargo));
            return c;
        }

        public static Card Colony(Game g, Camp cp)
        {
            var c = new Card { Title = cp.Name, Type = "Survivor colony - " + cp.Att, At = new Vector2(cp.Pos.x + 0.5f, cp.Pos.y + 0.5f) };
            c.Row("HP", string.Format("Strength {0}", (int)Math.Round(cp.Str)));
            string att = cp.Att;
            try { att = g.CampAttitude(cp); } catch { }
            c.Row("State", cp.Alive ? att + (string.IsNullOrEmpty(cp.Leader) ? "" : ", led by " + cp.Leader) : "Gone silent");
            c.Row("People", cp.Pop.ToString());
            var stock = new List<string>();
            if (cp.Stock != null) foreach (var kv in cp.Stock.OrderByDescending(x => x.Value).Take(4)) if (kv.Value >= 1.0) stock.Add(string.Format("{0} {1}", (int)kv.Value, kv.Key));
            c.Row("Stock", stock.Count == 0 ? (cp.Surplus != null && cp.Surplus.Count > 0 ? "Has spare " + string.Join(", ", cp.Surplus) : "Unknown") : string.Join(", ", stock));
            if (cp.Needs != null && cp.Needs.Count > 0) c.Row("Needs", string.Join(", ", cp.Needs));
            return c;
        }

        public static Card OwnCamp(Game g, Base b)
        {
            var c = new Card { Title = g.BaseLabel(b), Type = b.Main ? "Your main camp" : "Your outpost", At = AlertFeed.BaseSpot(b) };
            c.Row("HP", b.Done < 1.0 ? string.Format("Being built {0}%", (int)(b.Done * 100)) : "Standing");
            int res = g.CampResidents(b.Id);
            int guards = g.C.People.Count(p => p.St != "dead" && p.Base == b.Id && p.Guard);
            c.Row("State", string.Format("{0} living here, {1} on guard", res, guards));
            var ups = new List<string>();
            foreach (var kv in b.Up) if (kv.Value > 0) ups.Add(Data.UPGRADES.TryGetValue(kv.Key, out var ud) ? ud.Name : kv.Key);
            c.Row("Upgrades", ups.Count == 0 ? "None yet" : string.Join(", ", ups.Take(5)));
            var r = g.C.Res;
            c.Row("Stores", string.Format("{0} food, {1} water, {2} meds, {3} ammo", (int)r.Get("food", 0), (int)r.Get("water", 0), (int)r.Get("meds", 0), (int)r.Get("ammo", 0)));
            return c;
        }

        public static Card Building(Game g, int l, string type)
        {
            var d = Data.Instance;
            string name = d.LotName(l);
            var c = new Card { Title = string.IsNullOrEmpty(name) ? type : name, Type = type, At = d.LotCenter(l) };
            c.Row("HP", "-");
            var cv = g.ConvOf(l);
            string st;
            if (cv != null) st = "Your " + Game.ConvNameOf(cv.Type) + (cv.Ready > 0.0 ? " (being set up)" : "");
            else if (g.InCampYard(l)) st = "Inside your camp";
            else if (g.Searched(l)) st = "Searched";
            else st = "Not searched yet";
            c.Row("State", st);
            float loot = l < g.S.Loot.Length ? g.S.Loot[l] : 0f;
            c.Row("Contents", cv != null ? "In use" : g.Searched(l) && loot <= 0.15f ? "Picked clean" : string.Format("Loot left: about {0}%", (int)Math.Round(Mathf.Clamp01(loot) * 100f)));
            c.Row("District", d.DistrictAt(new Vector2Int(Mathf.FloorToInt(c.At.x), Mathf.FloorToInt(c.At.y)))?.Name ?? "-");
            return c;
        }

        // ---------------- layout (pure; shared with the offline preview) ----------------
        public const float ROW_H = 30f;
        /// <summary>Card: right of the camera column, from the camera column top; rows of ROW_H.</summary>
        public static Rect CardRect(float w, float top, int rows) => new Rect(104f, top, Mathf.Min(520f, Mathf.Max(380f, w * 0.36f)), 52f + 34f + rows * ROW_H + 64f + 12f);
    }

#if !DZ_PREVIEW
    public sealed partial class MapView
    {
        float _lpT0 = -1f;
        Vector2 _lpStart;
        bool _lpFired;

        /// <summary>Called once per frame from Update (after HandleInput). Watches one finger (or the mouse) held still.</summary>
        void TickLongPress()
        {
            var b = Bootstrap.Instance;
            var ui = b?.UI;
            if (b == null || ui == null || !Ready || _cam == null || G?.S == null || ui.ModalOpen) { _lpT0 = -1f; return; }
            bool began, down; Vector2 pos;
            int tc = Input.touchCount;
            if (tc >= 2) { _lpT0 = -1f; return; }
            if (tc == 1)
            {
                var t = Input.GetTouch(0);
                began = t.phase == TouchPhase.Began;
                down = t.phase != TouchPhase.Ended && t.phase != TouchPhase.Canceled;
                pos = t.position;
            }
            else
            {
                began = Input.GetMouseButtonDown(0);
                down = Input.GetMouseButton(0);
                pos = Input.mousePosition;
            }
            if (began)
            {
                bool ok = !ui.BlocksPointer(TopLeft(pos)) && ActiveTool == Tool.None && !TargetTool;
                if (!ui.BlocksPointer(TopLeft(pos))) LongPressInfo.Clear();   // a new press on the map dismisses the card
                _lpT0 = ok ? Time.unscaledTime : -1f; _lpStart = pos; _lpFired = false;
                return;
            }
            if (!down) { _lpT0 = -1f; return; }
            if (_lpT0 < 0f || _lpFired) return;
            if ((pos - _lpStart).magnitude > TapSlop() * 1.5f) { _lpT0 = -1f; return; }
            if (Time.unscaledTime - _lpT0 < LongPressInfo.HOLD_S) return;
            _lpFired = true;
            try
            {
                var card = LongPressAt(_lpStart);
                if (card != null) { LongPressInfo.Show(card, Time.unscaledTime); b.PlayClick(); }
            }
            catch (Exception e) { Debug.LogException(e); }
        }

        /// <summary>What is under the finger: survivor / zombie (nearest within a zoom-scaled radius), wreck, colony,
        /// own camp, building. Read only.</summary>
        LongPressInfo.Card LongPressAt(Vector2 screen)
        {
            var g = G;
            if (g?.S == null || g.C == null || _gen == null) return null;
            var ray = _cam.ScreenPointToRay(new Vector3(screen.x, screen.y, 0f));
            if (!Ground(screen, out var hit)) return null;
            var gp = new Vector2(hit.x, -hit.z);
            float r = Mathf.Clamp(_dist * 0.03f, 0.9f, 3f);
            // people and the dead
            Person bp = null; float bd = r;
            if (g.C.Started)
                foreach (var p in g.C.People)
                {
                    if (p.St == "dead") continue;
                    float d = (p.Pos - gp).magnitude;
                    if (d < bd) { bd = d; bp = p; }
                }
            int bz = -1; float zd = bp != null ? bd * 0.8f : r;   // a survivor right there wins over a zombie beside them
            for (int i = 0; i < g.Zc.Count; i++)
            {
                if (g.Zh[i] == 255) continue;
                float d = (g.ZPos(i) - gp).magnitude;
                if (d < zd) { zd = d; bz = i; }
            }
            if (bz >= 0) return LongPressInfo.Zombie(g, bz);
            if (bp != null) return LongPressInfo.Survivor(g, bp);
            var tile = new Vector2Int(Mathf.FloorToInt(gp.x), Mathf.FloorToInt(gp.y));
            if (g.S.Wrecks != null)
            {
                var w = g.WreckNear(tile, 1.1);
                if (w != null) return LongPressInfo.WreckCard(g, w);
            }
            int lot = _gen.PickLot(ray.origin, ray.direction);
            Camp bc = null; float cd = 1.3f;
            foreach (var cp in g.S.Camps)
            {
                if (!cp.Alive) continue;
                if (lot >= 0 && cp.Lot == lot) { bc = cp; break; }
                float d = (new Vector2(cp.Pos.x + 0.5f, cp.Pos.y + 0.5f) - gp).magnitude;
                if (d < cd) { cd = d; bc = cp; }
            }
            if (bc != null) return LongPressInfo.Colony(g, bc);
            if (g.C.Started)
                foreach (var b in g.C.Bases)
                    if ((lot >= 0 && b.Lot == lot) || (lot < 0 && (new Vector2(b.Pos.x + 0.5f, b.Pos.y + 0.5f) - gp).magnitude < 1.5f))
                        return LongPressInfo.OwnCamp(g, b);
            if (lot >= 0) return LongPressInfo.Building(g, lot, BuildingType(lot));
            return null;
        }
    }

    public sealed partial class GameUI
    {
        void DrawLongPressCard(float w, float h, MapView map)
        {
            var c = LongPressInfo.Current;
            if (c == null) return;
            if (Time.unscaledTime - LongPressInfo.ShownAt > LongPressInfo.SHOW_S) { LongPressInfo.Clear(); return; }
            var f = _hf;
            float top = f != null && f.Cam != null && f.Cam.Length > 0 ? f.Cam[0].y : 214f;
            var r = Blk(LongPressInfo.CardRect(w, top, c.Rows.Count));
            var inner = UiKit.Panel(r, c.Title);
            var L = new UiLayout(inner, 4f);
            UiKit.FitLabel(L.Top(30f), c.Type.ToUpperInvariant(), 16, TextAnchor.MiddleLeft, UiKit.InkDim, true, 11);
            foreach (var kv in c.Rows)
            {
                var row = UiLayout.Columns(L.Top(LongPressInfo.ROW_H - 4f), 10f, -118f, 1f);
                UiKit.FitLabel(row[0], kv.Key.ToUpperInvariant(), 14, TextAnchor.MiddleLeft, UiKit.Amber, true, 10);
                UiKit.FitLabel(row[1], kv.Value, 16, TextAnchor.MiddleLeft, UiKit.Ink, false, 10);
            }
            var bb = UiLayout.Columns(L.Bottom(56f), 10f, 1f, 1f);
            if (KBtn(bb[0], "SHOW ON MAP", UiRole.Primary, false, 17)) map?.FlyTo(c.At);
            if (KBtn(bb[1], "CLOSE", UiRole.Danger, false, 17)) LongPressInfo.Clear();
            else if (Event.current.type == EventType.Repaint)
            {
                // a thin bar running down to the auto-close
                float k = 1f - Mathf.Clamp01((Time.unscaledTime - LongPressInfo.ShownAt) / LongPressInfo.SHOW_S);
                GUI.DrawTexture(new Rect(r.x + 8f, r.yMax - 6f, (r.width - 16f) * k, 2f), _amberLineTex);
            }
        }
    }
#endif
}
