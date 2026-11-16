// IMGUI side of the faction camp sheet, the WAR screen, the war status line and the "_war" demand decision
// (layout in SideMenus.Factions.cs). main.gd _sheet_camp, _show_war, _refresh_war_hud, _show_war_report.
using DayZero.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed partial class GameUI
    {
        int _warSeen = -1;
        object _warState;

        /// <summary>Faction camp sheet for the camp tapped on the map.</summary>
        void DrawFactionSheet(float w, float h, MapView map)
        {
            var ctx = NewCtx(w, h, map);
            SideMenus.FactionSheet(ctx, map.SelectedCamp, () => map.ClearBuilding());
            foreach (var it in ctx.Items) if (it.Kind == MenuKind.Box) Blk(it.R);
            DrawItems(ctx);
        }

        /// <summary>main.gd lbl_war / _refresh_war_hud: war status under the top bar (tap opens WAR), and the
        /// battle report pops up once when a new one comes in.</summary>
        void DrawWarHud(float w, ref float y, MapView map)
        {
            var wv = G.WarOf(G.ViewCol);
            if (wv == null) return;
            int rid = wv.Report?.Id ?? 0;
            if (!ReferenceEquals(_warState, G.S) || _warSeen < 0) { _warState = G.S; _warSeen = rid; }
            else if (rid > _warSeen)
            {
                _warSeen = rid;
                OpenSide("war", map);
                _side.Sub = "report"; _side.WRPage = 0;
            }
            string t = SideMenus.Plain(G.WarStatusText(G.ViewCol));
            if (string.IsNullOrEmpty(t)) return;
            var r = Blk(SideMenus.WarHudRect(w, y));
            var st = MButton("brown", 17);
            if (Btn(r, t, st)) OpenSide("war", map);
            y += r.height + 6;
        }

        /// <summary>The "_war" demand gets its own layout so the long text and both answers fit; other events keep DrawDecision.</summary>
        bool DrawWarDemand(float w, float h, JObject ev)
        {
            if ((string)ev["id"] != "_war") return false;
            var ctx = NewCtx(w, h, MapView.Instance);
            SideMenus.WarDemand(ctx, ev, i => _b.Answer(ev, i));
            DrawItems(ctx);
            return true;
        }
    }
}
