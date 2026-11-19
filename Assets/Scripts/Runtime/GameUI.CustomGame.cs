// E14 custom game glue: the CUSTOM GAME screen opened from the title. Layout lives in CustomGame.Screen
// (SideMenus display list, previewed offline); START hands the rules to CustomGame.Pending and starts a new game,
// Bootstrap.NewGame writes them into the new state (CustomGame.ApplyNewRun).
using System;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
#if !DZ_PREVIEW
    public sealed partial class GameUI
    {
        readonly CustomGameState _custom = new CustomGameState();

        void OpenCustomGame() { _custom.Open = true; _custom.Difficulty = 1; }

        bool DrawCustomGameScreen(float w, float h)
        {
            if (!_custom.Open) return false;
            if (_b.State != Bootstrap.Screen2.Title) { _custom.Open = false; return false; }
            var ctx = NewCtx(w, h, MapView.Instance);
            CustomGame.Screen(ctx, _custom, d =>
            {
                _custom.Open = false;
                CustomGame.Pending = CustomGame.Draft.Clone();
                _b.NewGame(d);
            }, () => _custom.Open = false);
            SideMenus.Flush(ctx);
            return true;
        }
    }
#endif
}
