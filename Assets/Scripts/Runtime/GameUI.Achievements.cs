// E12 achievements glue: the ACHIEVEMENTS screen (title button and Menu tab > Game), and the unlock toast.
// Layout lives in Achievements.Screen (a SideMenus display list, previewed offline); this part opens it and paints it.
using System;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
#if !DZ_PREVIEW
    public sealed partial class GameUI
    {
        bool _achOpen;
        float _achToastT;
        AchDef _achToastCur;

        void OpenAchievements() { _achOpen = true; UiKit.ScrollTo("ach_list", 0f); }

        /// <summary>ACHIEVEMENTS or CUSTOM GAME screen on top (title or menu overlay). True when one was drawn.</summary>
        bool DrawAchCustomScreens(float w, float h)
        {
            if (_achOpen)
            {
                var ctx = NewCtx(w, h, MapView.Instance);
                Achievements.Screen(ctx, () => _achOpen = false);
                SideMenus.Flush(ctx);
                return true;
            }
            return DrawCustomGameScreen(w, h);   // GameUI.CustomGame.cs
        }

        /// <summary>Menu tab, Game column: one ACHIEVEMENTS button with the unlocked count.</summary>
        void DrawAchMenuButton(UiLayout col)
        {
            if (KBtn(col.Top(62f), string.Format("ACHIEVEMENTS  {0}/{1}", Achievements.UnlockedCount, Achievements.All.Count), UiRole.Neutral)) OpenAchievements();
        }

        /// <summary>Title screen: a small card right of the main panel with ACHIEVEMENTS and CUSTOM GAME.</summary>
        void DrawTitleExtras(float w, float h)
        {
            float x = 40f + Mathf.Min(w - 80f, 720f) + 20f;
            float cw = Mathf.Min(360f, w - 40f - x);
            if (cw < 220f) { x = Mathf.Max(40f, w - 300f); cw = 260f; }
            UiKit.Panel(new Rect(x, 176f, cw, 340f));
            float bx = x + 16f, bw = cw - 32f;
            // ACHIEVEMENTS level with CONTINUE, CUSTOM GAME level with the difficulty buttons
            if (KBtn(new Rect(bx, 190f, bw, 72f), "ACHIEVEMENTS", UiRole.Primary, false, 22)) OpenAchievements();
            UiKit.FitLabel(new Rect(bx, 268f, bw, 26f), string.Format("{0} of {1} unlocked", Achievements.UnlockedCount, Achievements.All.Count), 16, TextAnchor.MiddleLeft, UiKit.InkDim, false);
            if (KBtn(new Rect(bx, 326f, bw, 72f), "CUSTOM GAME", UiRole.Accent, false, 22)) OpenCustomGame();
            UiKit.FitLabel(new Rect(bx, 404f, bw, 26f), "Zombies, loot, raids, day length", 16, TextAnchor.MiddleLeft, UiKit.InkDim, false);
        }

        /// <summary>Unlock toast: a plate under the top HUD for 4.5 s per achievement.</summary>
        void DrawAchToast(float w, float h)
        {
            if (_achToastCur == null)
            {
                if (Achievements.Toasts.Count == 0) return;
                _achToastCur = Achievements.Toasts[0];
                Achievements.Toasts.RemoveAt(0);
                _achToastT = 4.5f;
                _b.PlayClick();
            }
            if (Event.current.type == EventType.Repaint) _achToastT -= Time.unscaledDeltaTime;
            if (_achToastT <= 0f) { _achToastCur = null; return; }
            float a = Mathf.Clamp01(Mathf.Min(_achToastT / 0.4f, (4.5f - _achToastT) / 0.25f));
            float tw = Mathf.Min(560f, w - 80f);
            var r = new Rect(w / 2f - tw / 2f, (_hf != null && _hf.Chips.Length > 0 ? _hf.Chips[0].yMax + 10f : 114f) - (1f - a) * 20f, tw, 84f);
            var oc = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, a);
            var inner = UiKit.Panel(r);
            var rows = UiLayout.Rows(inner, 2f, 0.8f, 1.2f);
            UiKit.FitLabel(rows[0], "ACHIEVEMENT UNLOCKED", 15, TextAnchor.MiddleCenter, UiKit.InkDim, true);
            UiKit.FitLabel(rows[1], _achToastCur.Title, 26, TextAnchor.MiddleCenter, UiKit.Amber, true);
            GUI.color = oc;
        }
    }
#endif
}
