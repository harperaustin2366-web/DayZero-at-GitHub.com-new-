// IMGUI side of the ARMORY / GARAGE menus (layout in SideMenus.Armory.cs, drawn by DrawSideMenu on the UI kit) and
// the wreck sheet (docked into the sheet area). The map markers (wrecks, camps, rescue pins, tow jobs) are drawn in GameUI.WorldMarks.cs.
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed partial class GameUI
    {
        /// <summary>Called by DrawGame with the other map labels. v0.14.0a: wrecks are meshes now; this draws every
        /// floating marker (camps, rescue pins, wrecks, tow jobs) and keeps the world layer current (GameUI.WorldMarks.cs).</summary>
        void DrawWreckMarks(MapView map, float w, float h) => DrawWorldMarks(map, w, h);

        /// <summary>main.gd _sheet_wreck: SIPHON FUEL / TOW WITH squad, at the bottom like the other map sheets.</summary>
        void DrawWreckSheet(float w, float h, MapView map)
        {
            DrawDockedSheet(w, h, map, ctx => SideMenus.WreckSheet(ctx, map.SelectedWreck, () => map.ClearBuilding()));   // v0.17 kit, docked
        }
    }
}
