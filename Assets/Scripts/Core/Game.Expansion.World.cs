// Expansion world systems (design/SYSTEMS_30.md). Active only when Game.ExpansionRules is on;
// with the flag off nothing here runs and the original v0.10.8a sim stays bit-exact.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace DayZero.Core
{
    public sealed partial class Game
    {
        // ---------------- #3 noise meter ----------------
        // Separate RNG so expansion rolls never shift the main sim's RNG call order.
        readonly GdRng _xrng = new GdRng();
        // per-base source breakdown for the current hour (not saved)
        readonly Dictionary<int, Dictionary<string, float>> _noiseSrc = new Dictionary<int, Dictionary<string, float>>();

        public void AddNoise(int baseIdx, float amt, string src)
        {
            var b = BaseAny(baseIdx);
            if (b == null || amt <= 0f) return;
            b.Noise = Mathf.Clamp(b.Noise + amt, 0f, 100f);
            if (!_noiseSrc.TryGetValue(baseIdx, out var d)) _noiseSrc[baseIdx] = d = new Dictionary<string, float>();
            d[src ?? "Other"] = (d.TryGetValue(src ?? "Other", out var v) ? v : 0f) + amt;
        }

        /// <summary>0 Quiet (0-24), 1 Heard (25-49), 2 Loud (50-74), 3 Beacon (75-100).</summary>
        public int NoiseBand(int baseIdx)
        {
            var b = BaseAny(baseIdx);
            if (b == null) return 0;
            return b.Noise >= 75f ? 3 : b.Noise >= 50f ? 2 : b.Noise >= 25f ? 1 : 0;
        }

        public Dictionary<string, float> NoiseSources(int baseIdx) => _noiseSrc.TryGetValue(baseIdx, out var d) ? d : null;

        /// <summary>Hourly: band spawns (cap 4 per camp), then decay 15% (10% at night).</summary>
        void StepExpNoise()
        {
            if (C == null) return;
            bool night = IsNight();
            foreach (var b in C.Bases)
            {
                _xrng.SetSeed((S.Seed ^ 0x5EED) + (long)(S.T * 7.0) + b.Id * 131L);
                int band = NoiseBand(b.Id);
                int k = band == 3 ? 4 : band == 2 ? _xrng.RandiRange(2, 3) : band == 1 ? 1 : 0;
                if (MaxZombies <= 0) k = 0;
                k = Math.Min(4, k);
                for (int i = 0; i < k; i++)
                {
                    double a = _xrng.Randf() * V.TAU;
                    var t = V.I(V.F(b.Pos).Add(V.F(Math.Cos(a), Math.Sin(a)).Mul(_xrng.RandfRange(12.0, 18.0))));
                    t = new Vector2Int(V.Clampi(t.x, 0, D.W - 1), V.Clampi(t.y, 0, D.H - 1));
                    SpawnZombie(Ti(D.NearestWalkable(t)), 0);
                }
                if (k >= 2) AddLog(string.Format("The gunfire carried. More of the dead are drifting toward {0}.", b.Name), "loss");
                b.Noise *= night ? 0.90f : 0.85f;
                if (b.Noise < 0.05f) b.Noise = 0f;
                _noiseSrc.Remove(b.Id);
            }
        }
    }
}
