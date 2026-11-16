// Tap-select of faction camps in play mode (main.gd _select_at, "camp" kind and faction_of_lot), so the faction
// camp sheet (diplomacy) opens on the tapped camp. Own camps are tapped in MapView.Camp.cs.
using System;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed partial class MapView
    {
        public string SelectedCamp { get; private set; } = "";   // faction camp tapped (faction sheet)

        public void SelectCamp(string cid)
        {
            ClearBuilding();
            var c = G?.CampById(cid);
            if (c == null) return;
            SelectedCamp = cid;
            if (_gen != null && c.Lot >= 0 && c.Lot < D.Lots.Count)
            {
                var ch = _gen.Highlight(c.Lot, true);
                _bselGo = new GameObject("Camp selection");
                _bselGo.transform.SetParent(transform, false);
                if (ch.Mesh.Count > 0) MakeObject("Highlight", ToMesh("Highlight", ch.Mesh), _objMat, _bselGo.transform);
                if (_glowMat != null && ch.Shadow.Count > 0) MakeObject("Glow", ToMesh("Glow", ch.Shadow), _glowMat, _bselGo.transform);
            }
        }

        /// <summary>Close the menus' world: fly to a faction camp and open its sheet (TRADE > PARTNER CAMPS > VIEW).</summary>
        public void ViewCamp(string cid)
        {
            var c = G?.CampById(cid);
            if (c == null) return;
            FlyTo(new Vector2(c.Pos.x + 0.5f, c.Pos.y + 0.5f));
            SelectCamp(cid);
        }

        /// <summary>main.gd _select_at: a faction camp within 1.3 tiles of its spot wins over the building behind it.</summary>
        bool TapCampNear(Vector2 gp)
        {
            var g = G;
            if (g?.S == null || gp.x < 0f) return false;
            if (TapCampMark(gp)) return true;   // v0.14.0a: the floating faction pin (MapView.World.cs)
            float bd = 1.3f; string best = null;
            foreach (var c in g.S.Camps)
            {
                if (!c.Alive) continue;
                float d = (new Vector2(c.Pos.x + 0.5f, c.Pos.y + 0.5f) - gp).magnitude;
                if (d < bd) { bd = d; best = c.Id; }
            }
            if (best == null) return false;
            SelectCamp(best);
            Bootstrap.Instance?.PlayClick();
            return true;
        }

        /// <summary>A tapped building that belongs to a faction camp opens its sheet (main.gd faction_of_lot).</summary>
        bool TapCampLot(int lot)
        {
            if (lot < 0 || G?.S == null) return false;
            var c = G.FactionOfLot(lot);
            if (c == null || !c.Alive) return false;
            SelectCamp(c.Id);
            Bootstrap.Instance?.PlayClick();
            return true;
        }
    }
}
