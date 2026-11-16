// Port of Godot 4.4 AStarGrid2D for the subset game.gd uses:
// DIAGONAL_MODE_NEVER, Manhattan compute/estimate heuristics, cell_size (1,1), offset 0,
// per-cell solid flag and weight_scale, get_point_path / get_id_path (with allow_partial_path).
// The open list is a binary heap with Godot's SortArray push/pop/adjust semantics and the
// SortPoints comparator (lower f first, ties -> higher g first), so tie-breaking and
// therefore the produced paths match the Godot build.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace DayZero.Core
{
    public sealed class AStarGrid
    {
        public RectInt Region { get; private set; }
        int _w, _h;
        bool[] _solid;
        // v0.14.0a: fence layer (Austin's rule change). A blocked cell is impassable like a solid one, except as the
        // first or last cell of a search, so someone standing on a fence (or sent to one, to repair it) still gets a
        // path. All false unless fences exist, so fenceless runs search exactly like Godot's AStarGrid2D.
        bool[] _block;
        int _blockN;
        int _end = -1;
        float[] _weight;
        // per-search scratch
        float[] _g, _f, _absG, _absF;
        int[] _prev, _openPass, _closedPass;
        int _pass = 1;
        int _lastClosest = -1;
        readonly List<int> _open = new List<int>();
        readonly int[] _nb = new int[4];

        public AStarGrid(RectInt region) { SetRegion(region); }

        public void SetRegion(RectInt region)
        {
            Region = region;
            _w = Math.Max(0, region.width); _h = Math.Max(0, region.height);
            int n = _w * _h;
            _solid = new bool[n]; _weight = new float[n]; _block = new bool[n]; _blockN = 0;
            for (int i = 0; i < n; i++) _weight[i] = 1f;
            _g = new float[n]; _f = new float[n]; _absG = new float[n]; _absF = new float[n];
            _prev = new int[n]; _openPass = new int[n]; _closedPass = new int[n];
        }

        bool InBounds(int x, int y) => x >= Region.x && y >= Region.y && x < Region.x + _w && y < Region.y + _h;
        int Id(int x, int y) => (y - Region.y) * _w + (x - Region.x);

        public void SetSolid(Vector2Int p, bool solid) { if (InBounds(p.x, p.y)) _solid[Id(p.x, p.y)] = solid; }
        public bool IsSolid(Vector2Int p) => InBounds(p.x, p.y) && _solid[Id(p.x, p.y)];

        /// <summary>Fence layer: blocked cells can't be crossed (gates are left unblocked).</summary>
        public void SetBlocked(Vector2Int p, bool on)
        {
            if (!InBounds(p.x, p.y)) return;
            int i = Id(p.x, p.y);
            if (_block[i] == on) return;
            _block[i] = on;
            _blockN += on ? 1 : -1;
        }
        public bool IsBlocked(Vector2Int p) => InBounds(p.x, p.y) && _block[Id(p.x, p.y)];
        public int BlockedCount => _blockN;
        public void SetWeight(Vector2Int p, float w) { if (InBounds(p.x, p.y)) _weight[Id(p.x, p.y)] = w; }

        /// <summary>fill_weight_scale_region(rect, w), clipped to the grid region.</summary>
        public void FillWeightRegion(RectInt r, float w)
        {
            int x0 = Math.Max(r.x, Region.x), y0 = Math.Max(r.y, Region.y);
            int x1 = Math.Min(r.x + r.width, Region.x + _w), y1 = Math.Min(r.y + r.height, Region.y + _h);
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++) _weight[Id(x, y)] = w;
        }

        // SortPoints: "A is worse than B" -> A sinks.
        bool Worse(int a, int b)
        {
            if (_f[a] > _f[b]) return true;
            if (_f[a] < _f[b]) return false;
            return _g[a] < _g[b];
        }

        void PushHeap(int hole, int top, int value)
        {
            int parent = (hole - 1) / 2;
            while (hole > top && Worse(_open[parent], value))
            {
                _open[hole] = _open[parent];
                hole = parent;
                parent = (hole - 1) / 2;
            }
            _open[hole] = value;
        }

        void AdjustHeap(int hole, int len, int value)
        {
            int top = hole;
            int second = 2 * hole + 2;
            while (second < len)
            {
                if (Worse(_open[second], _open[second - 1])) second--;
                _open[hole] = _open[second];
                hole = second;
                second = 2 * (second + 1);
            }
            if (second == len)
            {
                _open[hole] = _open[second - 1];
                hole = second - 1;
            }
            PushHeap(hole, top, value);
        }

        void PopHeap()
        {
            // SortArray::pop_heap(first=0, last=size): result slot = last-1, value = array[last-1]
            int last = _open.Count;
            int value = _open[last - 1];
            _open[last - 1] = _open[0];
            AdjustHeap(0, last - 1, value);
        }

        float Manhattan(int a, int b)
        {
            int ax = a % _w, ay = a / _w, bx = b % _w, by = b / _w;
            return Math.Abs(ax - bx) + Math.Abs(ay - by);
        }

        bool Walkable(int x, int y)
        {
            if (!InBounds(x, y)) return false;
            int i = Id(x, y);
            return !_solid[i] && (_blockN == 0 || !_block[i] || i == _end);
        }

        bool Solve(int begin, int end, bool partial)
        {
            _lastClosest = -1;
            _pass++;
            _end = end;
            if (_solid[end] && !partial) return false;
            bool found = false;
            _open.Clear();
            _g[begin] = 0; _f[begin] = Manhattan(begin, end);
            _absG[begin] = 0; _absF[begin] = _f[begin];
            _open.Add(begin);
            int[] nb = _nb;
            while (_open.Count > 0)
            {
                int p = _open[0];
                if (_lastClosest < 0 || _absF[_lastClosest] > _absF[p] || (_absF[_lastClosest] >= _absF[p] && _absG[_lastClosest] > _absG[p]))
                    _lastClosest = p;
                if (p == end) { found = true; break; }
                PopHeap();
                _open.RemoveAt(_open.Count - 1);
                _closedPass[p] = _pass;

                int px = p % _w + Region.x, py = p / _w + Region.y;
                int nn = 0;
                // order: top, right, bottom, left (AStarGrid2D::_get_nbors, DIAGONAL_MODE_NEVER)
                if (Walkable(px, py - 1)) nb[nn++] = Id(px, py - 1);
                if (Walkable(px + 1, py)) nb[nn++] = Id(px + 1, py);
                if (Walkable(px, py + 1)) nb[nn++] = Id(px, py + 1);
                if (Walkable(px - 1, py)) nb[nn++] = Id(px - 1, py);
                for (int i = 0; i < nn; i++)
                {
                    int e = nb[i];
                    if (_solid[e] || _closedPass[e] == _pass) continue;
                    float tentative = _g[p] + 1f * _weight[e];
                    bool isNew = false;
                    if (_openPass[e] != _pass)
                    {
                        _openPass[e] = _pass;
                        _open.Add(e);
                        isNew = true;
                    }
                    else if (tentative >= _g[e]) continue;
                    _prev[e] = p;
                    _g[e] = tentative;
                    _f[e] = _g[e] + Manhattan(e, end);
                    _absG[e] = tentative;
                    _absF[e] = _f[e] - _g[e];
                    if (isNew) PushHeap(_open.Count - 1, 0, e);
                    else PushHeap(_open.IndexOf(e), 0, e);
                }
            }
            return found;
        }

        List<int> IdPath(Vector2Int from, Vector2Int to, bool partial)
        {
            if (!InBounds(from.x, from.y) || !InBounds(to.x, to.y)) return null;
            int a = Id(from.x, from.y), b = Id(to.x, to.y);
            var ids = new List<int>();
            if (a == b) { ids.Add(a); return ids; }
            int endP = b;
            if (!Solve(a, b, partial))
            {
                if (!partial || _lastClosest < 0) return ids;
                endP = _lastClosest;
            }
            int cur = endP;
            ids.Add(cur);
            while (cur != a) { cur = _prev[cur]; ids.Add(cur); }
            ids.Reverse();
            return ids;
        }

        /// <summary>get_point_path(): cell positions as Vector2 (cell_size 1, offset 0). Empty if none.</summary>
        public List<Vector2> GetPointPath(Vector2Int from, Vector2Int to, bool partial = false)
        {
            var o = new List<Vector2>();
            var ids = IdPath(from, to, partial);
            if (ids == null) return o;
            foreach (int i in ids) o.Add(new Vector2(i % _w + Region.x, i / _w + Region.y));
            return o;
        }

        /// <summary>get_id_path(): cell ids as Vector2Int. Empty if none.</summary>
        public List<Vector2Int> GetIdPath(Vector2Int from, Vector2Int to, bool partial = false)
        {
            var o = new List<Vector2Int>();
            var ids = IdPath(from, to, partial);
            if (ids == null) return o;
            foreach (int i in ids) o.Add(new Vector2Int(i % _w + Region.x, i / _w + Region.y));
            return o;
        }
    }
}
