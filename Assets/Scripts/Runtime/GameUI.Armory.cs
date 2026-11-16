// IMGUI side of the ARMORY / GARAGE menus (layout in SideMenus.Armory.cs, drawn by DrawSideMenu) and the wreck
// sheet + wreck markers: the port's map has no wreck meshes yet, so each wreck on screen gets a marker the
// player can tap (MapView.TrySelectWreck), as the original's world_view draws them.
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed partial class GameUI
    {
        static readonly Color WRECK_MARK = new Color(0.55f, 0.52f, 0.46f);

        /// <summary>Marker over each wreck in view (zoomed in enough to read the street), own wrecks in green.</summary>
        void DrawWreckMarks(MapView map, float w, float h)
        {
            if (map.Dist > 70f || G.S.Wrecks == null) return;
            var tiny = MLabel(12, true, MenuAlign.Center, false);
            foreach (var wk in G.S.Wrecks)
            {
                if (!map.TryScreen(Geo.W(new Vector2(wk.Pos.x + 0.5f, wk.Pos.y + 0.5f), 0.4f), out var sp)) continue;
                var v = ToVirtual(sp);
                if (v.x < 0 || v.x > w || v.y < 40 || v.y > h) continue;
                bool sel = map.SelectedWreck == wk.Id;
                float s = sel ? 34f : 26f;
                Icon(new Rect(v.x - s / 2, v.y - s, s, s), "garage", wk.Own == G.ViewCol ? new Color(0.45f, 0.75f, 0.35f) : WRECK_MARK);
                if (map.Dist < 40f)
                {
                    tiny.normal.textColor = sel ? SideMenus.AMBER : new Color(0.9f, 0.88f, 0.8f, 0.9f);
                    GUI.Label(new Rect(v.x - 60, v.y + 1, 120, 16), "wreck", tiny);
                }
            }
        }

        /// <summary>main.gd _sheet_wreck: SIPHON FUEL / TOW WITH squad, at the bottom like the other map sheets.</summary>
        void DrawWreckSheet(float w, float h, MapView map)
        {
            var ctx = NewCtx(w, h, map);
            Blk(SideMenus.WreckSheetRect(w, h, SideMenus.IdleSquads(G)));
            SideMenus.WreckSheet(ctx, map.SelectedWreck, () => map.ClearBuilding());
            DrawItems(ctx);
        }
    }
}
