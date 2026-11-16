// Wreck taps (main.gd _select_at: G.wreck_near(tile, 1.1) opens the wreck sheet). GameUI draws the sheet
// (SideMenus.WreckSheet). v0.14.0a: wrecks are meshes on the map (MapGen.Vehicles.cs) with a marker over each;
// the markers (and the green rescue pins, MapView.World.cs) are also tappable in screen space, zoomed out too.
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed partial class MapView
    {
        public int SelectedWreck { get; private set; } = -1;   // wreck tapped (wreck sheet)

        /// <summary>A tap within 1.1 tiles of a wreck selects it, as the original. True when one was picked.</summary>
        bool TrySelectWreck(Vector2 gp)
        {
            var g = G;
            if (gp.x < 0f || g?.S?.Wrecks == null) return false;
            if (TapGroupMark(gp)) return true;   // a green rescue pin over a waiting group
            var w = g.WreckNear(new Vector2Int(Mathf.FloorToInt(gp.x), Mathf.FloorToInt(gp.y)), 1.1);
            if (w == null) return TapWreckMark(gp);
            ClearBuilding();
            SelectedWreck = w.Id;
            Bootstrap.Instance?.PlayClick();
            return true;
        }
    }
}
