// IMGUI side of the camp UI (layout in SideMenus.Camp.cs, map state in MapView.Camp.cs): ACTIONS entries BUILD OUTPOST,
// CAMPS and CREW & JOBS, the BUILD OUTPOST bar, the trap-line toolbar, the camp sheet, the trap sheet and the
// GUT row on the building sheet. v0.17: every bar / sheet docks into the sheet area on the UI kit (DrawDockedSheet).
using System;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed partial class GameUI
    {
        /// <summary>main.gd _start_outpost.</summary>
        void StartOutpost(MapView map)
        {
            if (map == null) return;
            if (G.Outposts() >= Game.OUTPOST_MAX) { _b.Toast(string.Format("You already run {0} outposts.", Game.OUTPOST_MAX)); return; }
            _panel = Panel.None;
            _side.Open = "";
            map.StartOutpost();
        }

        /// <summary>Bottom sheets / bars owned by the camp UI. True when one was drawn (the others are skipped).</summary>
        bool DrawCampSheets(float w, float h, MapView map)
        {
            if (SideMenus.PendingFly.HasValue) { map.FlyTo(SideMenus.PendingFly.Value); SideMenus.PendingFly = null; }
            if (map.OutpostMode)
            {
                DrawDockedSheet(w, h, map, ctx => SideMenus.OutpostBar(ctx, () => map.EndOutpost()));
                return true;
            }
            if (map.TrapLine)
            {
                DrawDockedSheet(w, h, map, ctx => SideMenus.TrapBar(ctx, map.TrapKind, map.TrapTiles, map.TrapAnchor.x >= 0, k => map.SetTrapKind(k), map.TrapUndo, map.TrapClear,
                    () => { string e = map.ConfirmTool(); _b.Toast(e == "" ? "Traps placed." : e); map.DirtyDynamic(); },
                    () => map.CancelTool()));
                return true;
            }
            if (map.ActiveTool != MapView.Tool.None) return false;
            if (map.SelectedBase >= 0)
            {
                DrawDockedSheet(w, h, map, ctx => SideMenus.BaseSheet(ctx, map.SelectedBase, () => map.ClearBuilding()));
                return true;
            }
            if (map.SelectedTrap >= 0)
            {
                int kind = G.S.Traps.TryGetValue(map.SelectedTrap, out var t) ? t.Kind : 0;
                DrawDockedSheet(w, h, map, ctx => SideMenus.TrapSheet(ctx, map.SelectedTrap, () => map.ClearBuilding(), () => { map.ClearBuilding(); Start(map, MapView.Tool.Trap, 0, G.TrapUnlocked(kind) ? kind : 0); }));
                return true;
            }
            return false;
        }

        /// <summary>Height the GUT row adds to the building sheet (main.gd _sheet_gut).</summary>
        float GutRowH(int l) => SideMenus.GutRowHeight(G, l);

        void DrawGutRow(Rect r, int l)
        {
            if (r.height <= 0f) return;
            var ctx = NewCtx(r.xMax + 10f, r.yMax + 10f, MapView.Instance);
            SideMenus.GutRow(ctx, r, l);
            DrawItems(ctx);
        }
    }
}
