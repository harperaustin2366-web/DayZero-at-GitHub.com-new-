// IMGUI renderer for the RESEARCH, TRADE, SQUADS and TASK FORCE side menus (layout in SideMenus*.cs), the
// squad / air-support target bar and the survivor-group SOS sheet, plus the original's
// "Research complete · tap to open RESEARCH" note (main.gd show_research_note). v0.17: the display lists are
// painted with the UI kit (InWorld.RenderItems) and bottom sheets dock into the sheet area (DrawDockedSheet).
using System;
using System.Collections.Generic;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed partial class GameUI
    {
        readonly SideMenuState _side = new SideMenuState();
        readonly Dictionary<string, GUIStyle> _mStyles = new Dictionary<string, GUIStyle>();
        string _rnoteRid = "";
        float _rnoteUntil;
        double _rnoteSeenT = double.MaxValue;
        object _rnoteState;

        bool SideOpen => _side.Open != "";

        void OpenSide(string which, MapView map)
        {
            _panel = Panel.None;
            map?.CancelTool();
            map?.ClearBuilding();
            SideMenus.Open(_side, which);
        }

        GUIStyle MLabel(int size, bool bold, MenuAlign al, bool wrap)
        {
            string k = "L" + size + (bold ? "b" : "") + (int)al + (wrap ? "w" : "");
            if (_mStyles.TryGetValue(k, out var st)) return st;
            st = new GUIStyle(_label) { fontSize = size, fontStyle = bold ? FontStyle.Bold : FontStyle.Normal, wordWrap = wrap, richText = false, clipping = TextClipping.Clip,
                alignment = al == MenuAlign.Center ? TextAnchor.MiddleCenter : al == MenuAlign.Right ? TextAnchor.UpperRight : TextAnchor.UpperLeft };
            st.padding = new RectOffset(0, 0, 0, 0); st.margin = new RectOffset(0, 0, 0, 0);
            _mStyles[k] = st;
            return st;
        }

        GUIStyle MButton(string style, int size)
        {
            string k = "B" + style + size;
            if (_mStyles.TryGetValue(k, out var st)) return st;
            if (style == "sel")   // selected tab / mode / primary action: darker amber so the light text keeps its contrast
                st = MakeButton(new Color(0.58f, 0.40f, 0.11f, 0.97f), new Color(0.64f, 0.45f, 0.14f, 0.98f), new Color(0.45f, 0.30f, 0.08f, 1f), size);
            else
            {
                var b = style == "amber" ? _amberButton : style == "dark" ? _darkButton : style == "brown" ? _brownButton : _button;
                st = new GUIStyle(b) { fontSize = size };
            }
            _mStyles[k] = st;
            return st;
        }

        float MMeasure(string t, int size, bool bold, float w) => MLabel(size, bold, MenuAlign.Left, true).CalcHeight(new GUIContent(t), w);

        MenuCtx NewCtx(float w, float h, MapView map) => new MenuCtx
        {
            G = G, W = w, H = h, St = _side, Measure = MMeasure, Act = (a, args) => _b.Act(a, args), Toast = _b.Toast,
            Target = (kind, sid) =>
            {
                // main.gd _start_squad_target / _start_air: close the menu, pick the spot on the map
                _side.Open = ""; _panel = Panel.None;
                map?.StartTarget(kind, sid);
            },
            ViewCamp = cid => { _side.Open = ""; _side.Barter = ""; _panel = Panel.None; map?.ViewCamp(cid); },   // GameUI.Factions.cs
            ViewGroup = gid =>
            {
                var gr = G.GroupById(gid);
                _side.Open = "";
                if (gr == null || map == null) return;
                map.FlyTo(new Vector2(gr.Pos.x + 0.5f, gr.Pos.y + 0.5f));
                map.SelectGroup(gid);
            },
        };

        void DrawSideMenu(float w, float h)
        {
            var ctx = NewCtx(w, h, MapView.Instance);
            SideMenus.Build(ctx);
            DrawItems(ctx);
        }

        /// <summary>Squad / drone / strike target bar at the bottom while a map target is picked.</summary>
        void DrawTargetBar(float w, float h, MapView map)
        {
            string send = map.ActiveTool == MapView.Tool.Drone ? "SCOUT" : map.ActiveTool == MapView.Tool.Strike ? "STRIKE" : "SEND";
            DrawDockedSheet(w, h, map, ctx => SideMenus.TargetBar(ctx, map.TargetText, send, () =>
            {
                string e = map.ConfirmTool();
                _b.Toast(e != "" ? e : send == "SEND" ? "On their way." : send == "SCOUT" ? "The drone is up." : "Gunship inbound.");
            }, () => map.CancelTool()));
        }

        /// <summary>SOS sheet of a survivor group tapped on the map (main.gd _sheet_group).</summary>
        void DrawGroupSheet(float w, float h, MapView map)
        {
            DrawDockedSheet(w, h, map, ctx => SideMenus.GroupSheet(ctx, map.SelectedGroup, () => map.ClearBuilding()));
        }

        /// <summary>v0.17: every SideMenus display list (side-menu host frame, sheets, bars) is painted with the UI kit:
        /// textured plates with header strips, kit buttons (CLOSE rust, selected tabs amber, off grey), fitted labels.</summary>
        void DrawItems(MenuCtx ctx)
        {
            if (_kp == null) _kp = new KitPainter(this);
            var old = GUI.color;
            InWorld.RenderItems(_kp, ctx.Items);
            GUI.color = old;
            GUI.enabled = true;
        }

        /// <summary>A SideMenus bottom sheet / bar docked into the sheet area (left of the right dock, above PLAY /
        /// speed): laid out for a virtual screen as wide as the area, then drawn shifted into it, so it can never run
        /// under ACTIONS. Blocks map taps under the sheet.</summary>
        void DrawDockedSheet(float w, float h, MapView map, Action<MenuCtx> layout)
        {
            var area = SheetArea(w, h, map);
            // kit sheets lay out straight into the dock area and paint themselves (SideMenus.Flush)
            var ctx = NewCtx(w, h, map);
            SideMenus.DockArea = area;
            try { layout(ctx); } finally { SideMenus.DockArea = null; }
            if (ctx.Items.Count == 0) { if (ctx.Painted.width > 0f) Blk(ctx.Painted); return; }
            var b = InWorld.Bounds(ctx.Items);
            if (b.width <= 0f && ctx.Painted.width > 0f) b = ctx.Painted;
            float dx = b.width > 0f ? Mathf.Round(area.x + area.width * 0.5f - (b.x + b.width * 0.5f)) : area.x;
            if (b.width > 0f) Blk(new Rect(b.x + dx, b.y, b.width, b.height));
            var m = GUI.matrix;
            GUI.matrix = m * Matrix4x4.Translate(new Vector3(dx, 0f, 0f));
            DrawItems(ctx);
            GUI.matrix = m;
        }

        /// <summary>main.gd show_research_note: a finished project shows a green note for a few seconds; tap opens RESEARCH on its tree.</summary>
        void DrawResearchNote(float w, ref float y, MapView map)
        {
            var log = G.S.Log;
            if (!ReferenceEquals(_rnoteState, G.S)) { _rnoteState = G.S; _rnoteSeenT = G.S.T; _rnoteRid = ""; }
            if (Event.current.type == EventType.Layout)
            {
                for (int i = log.Count - 1; i >= 0 && i >= log.Count - 6; i--)
                {
                    var e = log[i];
                    if (e.T <= _rnoteSeenT) break;
                    if (!string.IsNullOrEmpty(e.Rid) && (e.Col < 0 || e.Col == G.CurCol) && Data.Instance.Research[e.Rid] != null)
                    {
                        _rnoteRid = e.Rid; _rnoteUntil = Time.unscaledTime + 9f; _rnoteSeenT = e.T;
                        break;
                    }
                }
                if (log.Count > 0 && log[log.Count - 1].T > _rnoteSeenT && string.IsNullOrEmpty(_rnoteRid)) _rnoteSeenT = log[log.Count - 1].T;
            }
            if (_rnoteRid == "" || Time.unscaledTime > _rnoteUntil) { _rnoteRid = ""; return; }
            var rn = SideMenus.ResearchNoteRect(w, y);
            var r = Blk(Docks.Banner(TopLane(w), rn.width, y, Mathf.Max(rn.height, 52f)));   // v0.17: in the banner lane, clear of ACTIONS
            string name = (string)Data.Instance.Research[_rnoteRid]["name"];
            if (UiKit.Button(r, "RESEARCH COMPLETE: " + name.ToUpperInvariant() + "  -  TAP TO OPEN", UiRole.Primary, false, 18))
            {
                _side.Cat = (string)Data.Instance.Research[_rnoteRid]["cat"];
                _rnoteRid = "";
                OpenSide("research", map);
                _side.RJump = true;
            }
            y += r.height + 6;
        }
    }
}
