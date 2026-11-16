// IMGUI renderer for the RESEARCH, TRADE, SQUADS and TASK FORCE side menus (layout in SideMenus*.cs), the
// squad / air-support target bar and the survivor-group SOS sheet, plus the original's
// "Research complete · tap to open RESEARCH" note (main.gd show_research_note).
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
            var ctx = NewCtx(w, h, map);
            Blk(SideMenus.TargetBarRect(w, h));
            string send = map.ActiveTool == MapView.Tool.Drone ? "SCOUT" : map.ActiveTool == MapView.Tool.Strike ? "STRIKE" : "SEND";
            SideMenus.TargetBar(ctx, map.TargetText, send, () =>
            {
                string e = map.ConfirmTool();
                _b.Toast(e != "" ? e : send == "SEND" ? "On their way." : send == "SCOUT" ? "The drone is up." : "Gunship inbound.");
            }, () => map.CancelTool());
            DrawItems(ctx);
        }

        /// <summary>SOS sheet of a survivor group tapped on the map (main.gd _sheet_group).</summary>
        void DrawGroupSheet(float w, float h, MapView map)
        {
            var ctx = NewCtx(w, h, map);
            Blk(SideMenus.GroupSheetRect(w, h));
            SideMenus.GroupSheet(ctx, map.SelectedGroup, () => map.ClearBuilding());
            DrawItems(ctx);
        }

        void DrawItems(MenuCtx ctx)
        {
            var old = GUI.color;
            foreach (var it in ctx.Items)
            {
                switch (it.Kind)
                {
                    case MenuKind.Box: GUI.DrawTexture(it.R, Solid2(it.Fill)); break;
                    case MenuKind.Bar:
                        GUI.DrawTexture(it.R, Solid2(new Color(0f, 0f, 0f, 0.6f)));
                        GUI.DrawTexture(new Rect(it.R.x, it.R.y, it.R.width * it.Frac, it.R.height), Solid2(it.Col));
                        break;
                    case MenuKind.Label:
                        {
                            var st = MLabel(it.Size, it.Bold, it.Align, it.Wrap);
                            st.normal.textColor = it.Col;
                            GUI.Label(it.R, it.Text, st);
                            break;
                        }
                    case MenuKind.Button:
                        GUI.enabled = it.On;
                        if (Btn(it.R, it.Text, MButton(it.Style, it.Size))) { GUI.enabled = true; it.Click?.Invoke(); }
                        GUI.enabled = true;
                        break;
                }
            }
            GUI.color = old;
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
            var r = Blk(SideMenus.ResearchNoteRect(w, y));
            string name = (string)Data.Instance.Research[_rnoteRid]["name"];
            var st = MButton("olive", 18);
            if (Btn(r, "Research complete: " + name + "  ·  tap to open RESEARCH", st))
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
