// v0.19.2 map marks (render only; reads the sim, never writes it, no RNG):
//   * looted roofs: every building whose loot has been searched out (Game.Searched: S.Srch >= 0, which also covers
//     squads, raids, faction scavengers and gutting, all of which go through MarkSearched) gets a big spray-painted X on
//     its roof, like the original world_view.gd _draw_searched. White paint for your own colony, rust red when another
//     colony or a faction camp got there first. The paint fades toward the roof colour as the loot regrows
//     (Game.REGROW_H) and goes when S.Srch is reset. Converted lots, camps, roofless shells and collapsed ruins get
//     none. Everything is derived from saved loot state, so it survives save / load with nothing new saved.
//     One shared mesh for the whole map in the map material (one draw call), rebuilt only when the set of looted lots
//     changes (MapView.World.cs UpdateLootMarks); the roof surface follows the same LotInfo the near and far LODs use.
//   * SosPin: which map pin a survivor group gets (none once it is rescued, lost or gone; "team on the way";
//     "no beds"; open SOS). MapView.World.cs and the offline previews use the same rule.
using System.Collections.Generic;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed partial class MapGen
    {
        public const int SOS_NONE = 0, SOS_OPEN = 1, SOS_TEAM = 2, SOS_NOBEDS = 3;

        /// <summary>Map pin for a survivor group: SOS_NONE once it is not waiting any more (rescued, lost, gone) or
        /// no longer in S.Groups, SOS_TEAM while a task force is on the way, SOS_NOBEDS when every bed is taken (a
        /// rescue would leave them behind), else SOS_OPEN. bedsFree: Game.BedsFree() (pass -1 to skip the check).</summary>
        public static int SosPin(Group gr, int bedsFree)
        {
            if (gr == null || gr.St != "wait") return SOS_NONE;
            if (gr.Tf) return SOS_TEAM;
            if (bedsFree == 0) return SOS_NOBEDS;
            return SOS_OPEN;
        }

        public static readonly Color PAINT_OWN = Hex("#e6dfcc"), PAINT_OTHER = Hex("#b3412f");

        readonly HashSet<int> _lootSkip = new HashSet<int>();

        /// <summary>Cheap change key for the looted-roof layer (no allocation): which lots are searched, by whom, how
        /// faded, plus the things that hide a mark (conversions, camps, ruins) and the stroke width bucket.</summary>
        public int LootMarkKey(Game g, int widthBucket)
        {
            var S = g?.S;
            if (S?.Srch == null || S.Srch.Length != _info.Length) return 0;
            unchecked
            {
                int h = (int)2166136261 ^ widthBucket;
                double t = S.T;
                for (int l = 0; l < S.Srch.Length; l++)
                {
                    float s = S.Srch[l];
                    if (s < 0f) continue;
                    int who = S.SrchC != null && l < S.SrchC.Length ? S.SrchC[l] : 0;
                    h = (h ^ (l * 64 + who * 4 + FadeStep(t - s))) * 16777619;
                }
                h = (h ^ (S.Conv?.Count ?? 0)) * 16777619;
                h = (h ^ (S.Camps?.Count ?? 0)) * 16777619;
                h = (h ^ (_campLots.Count * 7 + _ruin.Count * 131)) * 16777619;
                if (S.Camps != null) foreach (var c in S.Camps) h = (h ^ (c.Alive ? c.Lot : -c.Lot - 2)) * 16777619;
                return h == 0 ? 1 : h;
            }
        }

        static int FadeStep(double age)
        {
            double k = (age - Game.REGROW_H * 0.6) / (Game.REGROW_H * 0.4);
            return k <= 0.0 ? 0 : k >= 1.0 ? 3 : 1 + (int)(k * 2.0);
        }

        /// <summary>Paints the X on every looted roof into md (cleared first). widthScale widens the strokes when the
        /// camera is far out so the marks stay readable on the zoomed-out map.</summary>
        public void BuildLootMarks(Game g, MeshData md, float widthScale)
        {
            md.V.Clear(); md.C.Clear(); md.T.Clear();
            var S = g?.S;
            if (S?.Srch == null || S.Srch.Length != _info.Length) return;
            _lootSkip.Clear();
            if (S.Camps != null) foreach (var c in S.Camps) if (c.Alive && c.Lot >= 0) _lootSkip.Add(c.Lot);
            var geo = new Geo(md, null);
            LIFT = 0.01f * Mathf.Max(1f, widthScale);
            int own = g.ViewCol + 1;
            for (int l = 0; l < S.Srch.Length; l++)
            {
                if (S.Srch[l] < 0f || _lootSkip.Contains(l) || _campLots.Contains(l)) continue;
                if (S.Conv != null && S.Conv.Has(l)) continue;
                if (!LootRoof(l, out var R, out float y0, out float y1, out int hole, out bool gable, out Color roof)) continue;
                int who = S.SrchC != null && l < S.SrchC.Length ? S.SrchC[l] : own;
                Color paint = who == own || who == 0 ? PAINT_OWN : PAINT_OTHER;
                int fs = FadeStep(S.T - S.Srch[l]);
                if (fs > 0) paint = Lerp(paint, roof, 0.18f * fs);
                PaintX(geo, l, R, y0, y1, hole, gable, paint, roof, widthScale);
            }
        }

        /// <summary>The roof surface an X goes on, matching what the near (and far) LOD draws: a flat roof at y0, or a
        /// gable with its ridge along R.U at y1 and eaves at y0; hole: the slope (-1 / +1) with a RoofHole, 0 none.</summary>
        bool LootRoof(int l, out ORect R, out float y0, out float y1, out int hole, out bool gable, out Color roof)
        {
            var info = _info[l];
            R = info.R; y0 = info.WallTop; y1 = y0; hole = 0; gable = false; roof = info.Roof;
            if (!info.Valid || info.Floors <= 0 || info.R.Hu < 0.05f || info.R.Hv < 0.05f) return false;
            if (_ruin.ContainsKey(l)) return false;                                  // collapsed: nothing to paint on
            if (!_campLots.Contains(l) && ClearedRect(info.R)) return false;         // cleared for a camp yard
            switch (info.A)
            {
                case Arch.Parking: case Arch.Farm: case Arch.None: return false;
                case Arch.House: case Arch.Hut: case Arch.Barn:
                {
                    int dmg = info.A == Arch.Barn ? Mathf.Min(info.Dmg, 1) : info.Dmg;
                    if (dmg == 3) return false;                                       // burnt-out shell, no roof
                    if (R.Hu < R.Hv) R = new ORect(R.C, R.V, R.Hv, R.Hu);
                    y1 = info.Top; gable = true;
                    if (dmg == 2 || (dmg == 1 && Hf(l, 30) < 0.4f)) hole = Hf(l, 36) < 0.5f ? -1 : 1;   // House(): RoofHole side
                    return true;
                }
                case Arch.Church:
                {
                    if (R.Hu < R.Hv) R = new ORect(R.C, R.V, R.Hv, R.Hu);
                    R = new ORect(R.At(R.Hu * 0.15f, 0f), R.U, R.Hu * 0.85f, R.Hv * 0.75f);   // the nave
                    y1 = info.WallTop + R.Hv * 1.2f; gable = true;
                    return true;
                }
                case Arch.Gas:
                    R = R.Sub(0.0f, 1f, -0.9f, 0.9f); y0 = y1 = 1.45f; roof = Hex("#8a8678");   // the canopy
                    return true;
                case Arch.Industrial:
                {
                    if (info.Dmg == 3) return false;
                    if (R.Hu < R.Hv) R = new ORect(R.C, R.V, R.Hv, R.Hu);
                    if (d.Lots[l].Kind == "supermarket") return true;
                    y1 = info.WallTop + R.Hv * 0.22f; gable = true;
                    if (info.Dmg >= 1) hole = Hf(l, 36) < 0.5f ? -1 : 1;   // Industrial(): RoofHole side
                    return true;
                }
                default:
                {
                    if (info.Dmg == 3) return false;
                    if (Hf(l, 15) < 0.5f) R = new ORect(R.C, -R.U, R.Hu, R.Hv);   // same flip as Block()
                    if (info.Dmg == 2 && R.Hu > 0.6f && R.Hv > 0.6f) R = R.Sub(-1f, 0.1f + Hf(l, 16) * 0.3f, -1f, 1f);   // the standing part
                    return true;
                }
            }
        }

        /// <summary>Two rough spray strokes corner to corner. A flat roof gets one big X; a gable gets one X on each
        /// slope (from the map camera only one slope faces you, so a single X over the ridge read as a V), none on a
        /// slope with a hole in it.</summary>
        void PaintX(Geo geo, int l, ORect R, float y0, float y1, int hole, bool gable, Color paint, Color roof, float widthScale)
        {
            float shortS = Mathf.Min(R.Hu, R.Hv);
            if (shortS < 0.08f) return;
            if (!gable)
            {
                // inset clear of the parapet
                float m = Mathf.Min(Mathf.Max(0.13f, shortS * 0.16f), shortS * 0.45f);
                OneX(geo, l * 4, new ORect(R.C, R.U, Mathf.Max(0.05f, R.Hu - m), Mathf.Max(0.05f, R.Hv - m)), R, y0, y1, false, paint, roof, widthScale);
                return;
            }
            float slopeK = Mathf.Sqrt(1f + Mathf.Pow((y1 - y0) / Mathf.Max(0.05f, R.Hv), 2f));   // true slope length per plan unit
            for (int side = -1; side <= 1; side += 2)
            {
                if (side == hole) continue;
                float hv = Mathf.Max(0.04f, R.Hv * 0.5f - Mathf.Min(0.07f, R.Hv * 0.12f));
                float hu = Mathf.Min(R.Hu - Mathf.Min(0.1f, R.Hu * 0.2f), hv * slopeK * 1.25f);   // about square on the slope
                if (hu < 0.05f) continue;
                OneX(geo, l * 4 + 2 + (side > 0 ? 1 : 0), new ORect(R.At(0f, side * R.Hv * 0.5f), R.U, hu, hv), R, y0, y1, true, paint, roof, widthScale);
            }
        }

        void OneX(Geo geo, int salt, ORect X, ORect R, float y0, float y1, bool gable, Color paint, Color roof, float widthScale)
        {
            float diag = Mathf.Sqrt(X.Hu * X.Hu + X.Hv * X.Hv) * 2f;
            float w = Mathf.Clamp(diag * 0.09f, 0.07f, 0.3f) * widthScale;
            w = Mathf.Min(w, Mathf.Min(X.Hu, X.Hv) * 0.9f);
            Stroke(geo, salt * 2, X.P(-1f, -1f), X.P(1f, 1f), w, R, y0, y1, gable, paint, roof);
            Stroke(geo, salt * 2 + 1, X.P(1f, -1f), X.P(-1f, 1f), w, R, y0, y1, gable, paint, roof);
        }

        static float RoofY(ORect R, float y0, float y1, bool gable, Vector2 p)
        {
            if (!gable) return y0;
            float v = Mathf.Abs(Vector2.Dot(p - R.C, R.V));
            return y1 - (y1 - y0) * v / Mathf.Max(0.05f, R.Hv);
        }

        static Color PaintShade(Color c, ORect R, bool gable, float side)
        {
            if (!gable) return c;
            return Geo.Shade(c, new Vector3(R.V.x * side, 0.8f, -R.V.y * side).normalized);
        }

        const int XSEG = 6;
        static float LIFT = 0.012f;   // set per build: a touch higher when zoomed out (depth precision)

        void Stroke(Geo geo, int salt, Vector2 a, Vector2 b, float w, ORect R, float y0, float y1, bool gable, Color paint, Color roof)
        {
            var dir = (b - a).normalized;
            var perp = new Vector2(-dir.y, dir.x);
            Vector2 pl = default, pr = default; float pthin = 0f;
            for (int i = 0; i <= XSEG; i++)
            {
                float t = (float)i / XSEG;
                var p = Vector2.Lerp(a, b, t);
                if (i > 0 && i < XSEG) p += perp * ((Hf(salt, 300 + i) - 0.5f) * w * 0.35f);   // hand wobble
                float end = Mathf.Min(t, 1f - t) * XSEG;                                             // 0 at the tips
                float hw = w * 0.5f * (0.86f + 0.28f * Hf(salt, 320 + i)) * (end < 0.5f ? 0.62f : end < 1.5f ? 0.92f : 1f);
                Vector2 ol = perp * hw;
                var ql = p + ol; var qr = p - ol;
                float thin = (end < 0.5f ? 0.3f : 0f) + 0.12f * Hf(salt, 340 + i);   // thin spray at the tips, uneven along
                if (i > 0)
                {
                    float side = Mathf.Sign(Vector2.Dot((pl + pr + ql + qr) * 0.25f - R.C, R.V));
                    if (side == 0f) side = 1f;
                    Color c0 = PaintShade(Lerp(paint, roof, pthin), R, gable, side), c1 = PaintShade(Lerp(paint, roof, thin), R, gable, side);
                    geo.QuadRaw(W3(pl, R, y0, y1, gable), W3(pr, R, y0, y1, gable), W3(qr, R, y0, y1, gable), W3(ql, R, y0, y1, gable), c0, c0, c1, c1);
                }
                pl = ql; pr = qr; pthin = thin;
            }
            // overspray: a few small dots beside the tips
            for (int k = 0; k < 3; k++)
            {
                float t = k == 0 ? 0.04f : k == 1 ? 0.96f : 0.25f + Hf(salt, 360) * 0.5f;
                var p = Vector2.Lerp(a, b, t) + perp * ((Hf(salt, 361 + k) < 0.5f ? -1f : 1f) * w * (0.75f + 0.5f * Hf(salt, 364 + k)));
                float s = w * (0.16f + 0.12f * Hf(salt, 367 + k));
                var u = dir * s; var v = perp * s;
                float side = Mathf.Sign(Vector2.Dot(p - R.C, R.V));
                Color c = PaintShade(Lerp(paint, roof, 0.35f), R, gable, side == 0f ? 1f : side);
                if (gable && Mathf.Abs(Vector2.Dot(p - R.C, R.V)) < s * 1.5f) continue;   // keep dots off the ridge
                geo.QuadRaw(W3(p - u - v, R, y0, y1, gable), W3(p + u - v, R, y0, y1, gable), W3(p + u + v, R, y0, y1, gable), W3(p - u + v, R, y0, y1, gable), c, c, c, c);
            }
        }

        static Vector3 W3(Vector2 p, ORect R, float y0, float y1, bool gable) => Geo.W(p, RoofY(R, y0, y1, gable, p) + LIFT);
    }
}
