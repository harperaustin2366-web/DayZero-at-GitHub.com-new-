// Wreck taps (main.gd _select_at: G.wreck_near(tile, 1.1) opens the wreck sheet). GameUI draws the sheet
// (SideMenus.WreckSheet). v0.14.0a: wrecks are meshes on the map (MapGen.Vehicles.cs) with a marker over each;
// the markers (and the green rescue pins, MapView.World.cs) are also tappable in screen space, zoomed out too.
// v0.19.3 Expansion: every map car is a wreck (~900), so they are not one world-layer mesh any more: the chunks near
// the camera get their own wreck mesh (MapGen.BuildWreckChunk), rebuilt only when that chunk's wrecks change.
using System.Collections.Generic;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed partial class MapView
    {
        public int SelectedWreck { get; private set; } = -1;   // wreck tapped (wreck sheet)

        // ---------------- Expansion: wreck meshes streamed per chunk ----------------
        const float WRECK_MAX_DIST = 110f;   // zoomed out further than this the cars are specks: none drawn
        const int WRECK_CHUNKS_MAX = 16, WRECK_BUILDS_PER_TICK = 2;
        readonly Dictionary<int, GameObject> _wreckGo = new Dictionary<int, GameObject>();
        readonly Dictionary<int, int> _wreckGoKey = new Dictionary<int, int>();
        readonly Dictionary<int, int> _wreckKeyNow = new Dictionary<int, int>();
        readonly List<int> _wreckWant = new List<int>(32), _wreckTmp = new List<int>(32);
        GameObject _wreckRoot; float _wreckT;

        /// <summary>Per HUD repaint (via UpdateWorldLayer), 4x a second: keep a wreck mesh on each chunk near the camera,
        /// rebuilt when its wrecks change (tow, strip, new); chunks out of reach are dropped. Classic: nothing here.</summary>
        void StreamWreckChunks(Game g)
        {
            if (!Game.ExpansionRules || g?.S?.Wrecks == null || _gen == null || _root == null || _ncx == 0) { ClearWreckChunks(); return; }
            if (_wreckRoot == null) { _wreckGo.Clear(); _wreckGoKey.Clear(); }   // destroyed with the map root
            _wreckT -= Time.unscaledDeltaTime;
            if (_wreckT > 0f) return;
            _wreckT = 0.25f;
            WantedLod0(_wreckWant, Vector3.zero, CH * 0.5f, WRECK_CHUNKS_MAX, WRECK_MAX_DIST);
            // one pass: a content key per chunk
            _wreckKeyNow.Clear();
            foreach (var w in g.S.Wrecks)
            {
                int c = Mathf.Clamp(w.Pos.y / CH, 0, _ncy - 1) * _ncx + Mathf.Clamp(w.Pos.x / CH, 0, _ncx - 1);
                _wreckKeyNow.TryGetValue(c, out int k);
                unchecked { k = k * 31 + w.Id * 7919 + w.Pos.x * 131 + w.Pos.y * 17 + (w.Burnt ? 5 : 0) + (int)((w.Ang ?? 0f) * 100f); }
                _wreckKeyNow[c] = k;
            }
            // drop chunks no longer wanted
            _wreckTmp.Clear(); foreach (var kv in _wreckGo) if (!_wreckWant.Contains(kv.Key)) _wreckTmp.Add(kv.Key);
            foreach (int c in _wreckTmp) { if (_wreckGo[c] != null) DestroyChunk(_wreckGo[c]); _wreckGo.Remove(c); _wreckGoKey.Remove(c); }
            // build missing / changed ones, nearest first, a couple per tick
            int built = 0;
            foreach (int c in _wreckWant)
            {
                _wreckKeyNow.TryGetValue(c, out int key);
                if (_wreckGo.ContainsKey(c) && _wreckGoKey[c] == key) continue;
                if (built >= WRECK_BUILDS_PER_TICK) { _wreckT = 0f; break; }
                if (_wreckGo.TryGetValue(c, out var old) && old != null) DestroyChunk(old);
                _wreckGo.Remove(c); _wreckGoKey.Remove(c);
                built++;
                if (_wreckRoot == null) { _wreckRoot = new GameObject("Wrecks"); _wreckRoot.transform.SetParent(_root.transform, false); }
                GameObject go = null;
                if (key != 0)
                {
                    var ch = _gen.BuildWreckChunk(g, c % _ncx, c / _ncx);
                    if (ch.Mesh.Count > 0 || (ch.Shadow != null && ch.Shadow.Count > 0))
                    {
                        go = new GameObject("Wrecks " + (c % _ncx) + "," + (c / _ncx));
                        go.transform.SetParent(_wreckRoot.transform, false);
                        if (ch.Mesh.Count > 0) MakeObject("Mesh", ToMesh(go.name, ch.Mesh), _objMat, go.transform);
                        if (_shadowMat != null && ch.Shadow != null && ch.Shadow.Count > 0) MakeObject("Shadows", ToMesh(go.name + " shadows", ch.Shadow), _shadowMat, go.transform);
                    }
                }
                _wreckGo[c] = go; _wreckGoKey[c] = key;
            }
        }

        void ClearWreckChunks()
        {
            if (_wreckGo.Count == 0 && _wreckRoot == null) return;
            foreach (var go in _wreckGo.Values) if (go != null) DestroyChunk(go);
            _wreckGo.Clear(); _wreckGoKey.Clear();
            if (_wreckRoot != null) Destroy(_wreckRoot);
            _wreckRoot = null;
        }

        /// <summary>Expansion: which wrecks get a floating pin (the rest are only meshes on the street): military
        /// vehicles, your own lost cars, the selected one and one a squad is towing / heading for.</summary>
        static bool WreckPinned(Game g, Wreck w, int sel) =>
            !Game.ExpansionRules || w.Id == sel || w.Own == g.ViewCol || w.Type == "humvee" || Data.VehDef(w.Type).Mil == true || g.TowingSquad(w.Id) != null;

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
