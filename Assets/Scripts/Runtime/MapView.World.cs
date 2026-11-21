// v0.14.0a: the world layer for the other colonies and the vehicle wrecks (MapGen.BuildWorldLayer: faction
// compounds, claimed-lot pennants, fallen-camp ruins, wreck meshes), rebuilt when the camps or wrecks change,
// plus the floating map markers the HUD draws (GameUI.WorldMarks.cs) and their taps:
//   * faction camps: a pin in the stance colour (hostile red, neutral amber, friendly green) with the name,
//     visible zoomed out; tap opens the faction sheet (flies there when zoomed out)
//   * survivor groups waiting for rescue (S.Groups "wait"): a small green pin; tap opens their SOS sheet
//   * wrecks: tap opens the wreck sheet (TOW TO GARAGE); a squad on a tow job shows its progress
// The layer is parented to the static map root so it hides / goes away with it. Nothing here changes the sim.
// v0.19.2: an SOS pin goes the moment its group stops waiting (rescued, lost, gone) and an open SOS sheet for it
// closes; while a task force is on the way the pin turns blue (TEAM ON THE WAY), and with every bed taken it turns
// grey (NO BEDS: a rescue would leave them behind). Looted buildings get a painted X on the roof (MapGen.Marks.cs),
// one shared mesh rebuilt when the looted set changes (UpdateLootMarks).
using System.Collections.Generic;
using DayZero.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace DayZero.Runtime
{
    public sealed partial class MapView
    {
        public enum MarkKind { Camp, Group, Wreck, Tow }

        public struct WorldMark
        {
            public MarkKind Kind; public int Id; public string Cid;
            public Vector3 World; public Color Col; public string Label, Sub; public float Frac;
            public int St;   // Group: MapGen.SOS_OPEN / SOS_TEAM / SOS_NOBEDS
        }

        public static readonly Color SOS_GREEN = new Color(0.36f, 0.8f, 0.34f), SOS_BLUE = new Color(0.36f, 0.6f, 0.86f), SOS_GREY = new Color(0.55f, 0.55f, 0.5f);

        GameObject _worldGo; Mesh _worldMesh, _worldShadowMesh; string _worldKey = ""; float _worldT;
        readonly List<WorldMark> _marks = new List<WorldMark>();
        int _marksFrame = -1;

        /// <summary>Rebuild the camps / wrecks layer when they changed (checked twice a second). Called by the HUD
        /// every repaint while a game is on screen.</summary>
        public void UpdateWorldLayer()
        {
            var g = G;
            if (!Ready || _gen == null || _root == null || g?.S == null) return;
            if (SelectedGroup >= 0 && MapGen.SosPin(g.GroupById(SelectedGroup), -1) == MapGen.SOS_NONE) SelectedGroup = -1;   // rescued / gone: sheet and ring go too
            UpdateLootMarks(g);
            StreamWreckChunks(g);   // v0.19.3 Expansion: wreck meshes near the camera
            if (_worldGo == null) _worldKey = "";   // destroyed with the map root on a rebuild
            _worldT -= Time.unscaledDeltaTime;
            if (_worldT > 0f && _worldKey != "") return;
            _worldT = 0.5f;
            var sb = new System.Text.StringBuilder();
            sb.Append(_gen.GetHashCode()).Append('|');
            foreach (var c in g.S.Camps) sb.Append(c.Lot).Append(c.Alive ? 'a' : 'x').Append(c.Claim?.Count ?? 0).Append(c.Pop / 6).Append(',');
            sb.Append('|');
            if (g.S.Wrecks != null) foreach (var w in g.S.Wrecks) sb.Append(w.Id).Append('@').Append(w.Pos.x).Append(',').Append(w.Pos.y).Append(';');
            sb.Append('|').Append(SelectedWreck);
            string key = sb.ToString();
            if (key == _worldKey && _worldGo != null) return;
            _worldKey = key;
            if (_worldGo != null) Destroy(_worldGo);
            if (_worldMesh != null) Destroy(_worldMesh);
            if (_worldShadowMesh != null) Destroy(_worldShadowMesh);
            _worldMesh = null; _worldShadowMesh = null;
            _worldGo = new GameObject("Colonies and wrecks");
            _worldGo.transform.SetParent(_root.transform, false);
            var ch = _gen.BuildWorldLayer(g, SelectedWreck);
            if (ch.Mesh.Count > 0) { _worldMesh = ToMesh("Colonies", ch.Mesh); MakeObject("Colonies", _worldMesh, _objMat, _worldGo.transform); }
            if (_shadowMat != null && ch.Shadow.Count > 0) { _worldShadowMesh = ToMesh("Colonies shadows", ch.Shadow); MakeObject("Colonies shadows", _worldShadowMesh, _shadowMat, _worldGo.transform); }
        }

        // ---------------- looted roofs: painted X (v0.19.2) ----------------
        GameObject _lootGo; Mesh _lootMesh; readonly MeshData _lootMd = new MeshData(); int _lootKey; float _lootT;

        static int LootWidthBucket(float dist) => dist < 35f ? 0 : dist < 70f ? 1 : dist < 120f ? 2 : 3;
        static readonly float[] LOOT_W = { 1f, 1.45f, 2.1f, 2.9f };

        /// <summary>Rebuilds the looted-roof X layer when the looted set (or the zoom bucket) changed; checked 3x a second,
        /// allocation-free when nothing changed. One mesh, the map material, no shadows.</summary>
        void UpdateLootMarks(Game g)
        {
            if (_lootGo == null) _lootKey = 0;   // destroyed with the map root on a rebuild
            _lootT -= Time.unscaledDeltaTime;
            if (_lootT > 0f && _lootGo != null) return;
            _lootT = 0.33f;
            int wb = LootWidthBucket(_dist);
            int key = _gen.LootMarkKey(g, wb);
            if (key == _lootKey && _lootGo != null) return;
            _lootKey = key;
            _gen.BuildLootMarks(g, _lootMd, LOOT_W[wb]);
            if (_lootGo == null)
            {
                if (_lootMesh != null) Destroy(_lootMesh);
                _lootMesh = new Mesh { name = "Looted roofs" };
                _lootMesh.MarkDynamic();
                _lootGo = MakeObject("Looted roofs", _lootMesh, _objMat, _root.transform);
            }
            _lootMesh.Clear();
            if (_lootMd.V.Count > 65000) _lootMesh.indexFormat = IndexFormat.UInt32;
            _lootMesh.SetVertices(_lootMd.V);
            _lootMesh.SetColors(_lootMd.C);
            _lootMesh.SetTriangles(_lootMd.T, 0);
            _lootMesh.RecalculateBounds();
        }

        static string StanceWord(Game g, Camp c)
        {
            if (g.C != null && g.C.Pacts.Contains(c.Id)) return "ALLY (PACT)";
            var w = g.WarOf(g.ViewCol);
            if (w != null && w.Lord == c.Id) return "YOUR OVERLORD";
            if (c.Lord == g.ViewCol) return "YOUR VASSAL";
            if (w != null && w.Foe == c.Id && w.St != "none") return "AT WAR";
            string a = g.CampAttitude(c);
            return a == "Raiders" ? "RAIDERS" : a.ToUpperInvariant();
        }

        /// <summary>Every marker for this frame (camps, waiting groups, wrecks, tow jobs) in world space.</summary>
        public List<WorldMark> Marks()
        {
            if (_marksFrame == Time.frameCount) return _marks;
            _marksFrame = Time.frameCount;
            _marks.Clear();
            var g = G;
            if (g?.S == null || _gen == null) return _marks;
            foreach (var c in g.S.Camps)
            {
                if (!c.Alive || c.Lot < 0 || c.Lot >= D.Lots.Count) continue;
                var lc = D.LotCenter(c.Lot);
                float top = Mathf.Max(_gen.LotH[c.Lot], 1.2f) + 1.9f;
                string att = g.CampAttitude(c);
                if (c.Att == "Raiders") att = "Hostile";
                if (g.C != null && g.C.Pacts.Contains(c.Id)) att = "Friendly";
                _marks.Add(new WorldMark { Kind = MarkKind.Camp, Cid = c.Id, World = Geo.W(lc, top), Col = MapGen.StanceColor(att), Label = c.Name, Sub = StanceWord(g, c) + " - " + c.Pop + " people" });
            }
            if (g.S.Groups != null && g.S.Groups.Count > 0)
            {
                int beds = g.C != null ? g.BedsFree() : -1;
                foreach (var gr in g.S.Groups)
                {
                    int st = MapGen.SosPin(gr, beds);
                    if (st == MapGen.SOS_NONE) continue;   // rescued / lost / gone: no pin anywhere
                    _marks.Add(new WorldMark { Kind = MarkKind.Group, Id = gr.Id, St = st, World = Geo.W(new Vector2(gr.Pos.x + 0.5f, gr.Pos.y + 0.5f), 0.95f),
                        Col = st == MapGen.SOS_TEAM ? SOS_BLUE : st == MapGen.SOS_NOBEDS ? SOS_GREY : SOS_GREEN, Label = gr.Label,
                        Sub = st == MapGen.SOS_TEAM ? "TEAM ON THE WAY" : st == MapGen.SOS_NOBEDS ? gr.N + " waiting - NO BEDS" : gr.N + " to rescue" });
                }
            }
            if (g.S.Wrecks != null)
                foreach (var w in g.S.Wrecks)
                {
                    if (!WreckPinned(g, w, SelectedWreck)) continue;   // v0.19.3 Expansion: street cars have no pin
                    var q = g.TowingSquad(w.Id);
                    _marks.Add(new WorldMark { Kind = MarkKind.Wreck, Id = w.Id, World = Geo.W(new Vector2(w.Pos.x + 0.5f, w.Pos.y + 0.5f), 0.95f),
                        Col = w.Own == g.ViewCol ? new Color(0.45f, 0.75f, 0.35f) : new Color(0.62f, 0.58f, 0.5f), Label = Data.VehDef(w.Type).Name + " wreck", Sub = q != null ? q.Name + " on the way" : "" });
                }
            if (g.C != null)
                foreach (var q in g.C.Squads)
                {
                    if (!g.TowStatus(q, out double frac, out string phase, out Vector2 at)) continue;
                    string what = q.Towing != null ? Data.VehDef(q.Towing.Type).Name : (g.WreckById(q.TowId) != null ? Data.VehDef(g.WreckById(q.TowId).Type).Name : "wreck");
                    _marks.Add(new WorldMark { Kind = MarkKind.Tow, Id = q.Id, World = Geo.W(at, 1.35f), Col = new Color(0.86f, 0.62f, 0.2f), Label = q.Name + ": " + phase, Sub = what, Frac = (float)frac });
                }
            return _marks;
        }

        /// <summary>Tap radius around a marker, in screen pixels (~6 mm).</summary>
        static float MarkTapPx() => Mathf.Max(34f, (UnityEngine.Screen.dpi > 0f ? UnityEngine.Screen.dpi : 320f) * 0.24f);

        /// <summary>Nearest marker of one kind under the finger (screen space, so pins work zoomed out). gp = ground point of the tap.</summary>
        bool MarkAt(Vector2 gp, MarkKind kind, out WorldMark hit)
        {
            hit = default;
            if (gp.x < 0f || !TryScreen(Geo.W(gp, 0f), out var tap)) return false;
            float best = MarkTapPx(); bool any = false;
            foreach (var m in Marks())
            {
                if (m.Kind != kind || !TryScreen(m.World, out var sp)) continue;
                // the pin is drawn above its anchor: measure to the pin's centre too
                float d = Mathf.Min((sp - tap).magnitude, (sp + new Vector2(0f, -MarkTapPx() * 0.6f) - tap).magnitude);
                if (d < best) { best = d; hit = m; any = true; }
            }
            return any;
        }

        /// <summary>Green rescue pin tapped: open the group's SOS sheet (fly over first when zoomed out).</summary>
        bool TapGroupMark(Vector2 gp)
        {
            if (!MarkAt(gp, MarkKind.Group, out var m)) return false;
            var gr = G?.GroupById(m.Id);
            if (gr == null) return false;
            if (_dist > 45f) FlyTo(new Vector2(gr.Pos.x + 0.5f, gr.Pos.y + 0.5f));
            SelectGroup(gr.Id);
            Bootstrap.Instance?.PlayClick();
            return true;
        }

        /// <summary>Faction pin tapped: open the faction sheet (fly over first when zoomed out).</summary>
        bool TapCampMark(Vector2 gp)
        {
            if (!MarkAt(gp, MarkKind.Camp, out var m)) return false;
            if (_dist > 60f) ViewCamp(m.Cid); else SelectCamp(m.Cid);
            Bootstrap.Instance?.PlayClick();
            return true;
        }

        /// <summary>Wreck marker (or a tow job's marker, which opens the wreck it is going for) tapped.</summary>
        bool TapWreckMark(Vector2 gp)
        {
            var g = G;
            if (g?.S?.Wrecks == null) return false;
            int wid = -1; Vector2 at = default;
            if (MarkAt(gp, MarkKind.Wreck, out var m)) { var w = g.WreckById(m.Id); if (w != null) { wid = w.Id; at = new Vector2(w.Pos.x + 0.5f, w.Pos.y + 0.5f); } }
            if (wid < 0) return false;
            ClearBuilding();
            if (_dist > 60f) FlyTo(at);
            SelectedWreck = wid;
            Bootstrap.Instance?.PlayClick();
            return true;
        }
    }
}
