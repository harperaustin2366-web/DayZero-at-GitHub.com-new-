// v0.14.0a: procedural vehicle meshes for the sim's vehicles (Data.VEHICLES: pickup, truck, humvee, apc, tank).
// One builder for three uses: wrecks lying on the map (S.Wrecks, rusted / broken / burnt variants, tappable,
// MapGen.Factions.cs), vehicles driven by squads and scavengers and wrecks being towed home (Crowd.cs).
// The sim type decides the silhouette class (light pickup / heavy cargo / military); the wreck id picks a body
// variant within the class (pickup or SUV, box truck or van) so the roads don't fill with clones. Pure C#.
using UnityEngine;

namespace DayZero.Runtime
{
    public sealed partial class MapGen
    {
        public enum VehLook { Driving, Wreck, Towed }

        static readonly Color OLIVE = Hex("#4f4f36"), OLIVE2 = Hex("#5c5c40"), TYRE = Hex("#1e1e1c"), HUB = Hex("#4a4a46");
        static readonly Color GLASS_V = Hex("#2a3032"), GLASS_BROKEN = Hex("#141412"), CHARRED = Hex("#2a2622"), RUSTY = Hex("#6a4028");

        /// <summary>Half length / half width of a vehicle body (tiles), for tap radius and tow offsets.</summary>
        public static Vector2 VehicleSize(string type)
        {
            switch (type)
            {
                case "truck": return new Vector2(0.98f, 0.34f);
                case "humvee": return new Vector2(0.74f, 0.36f);
                case "apc": return new Vector2(1.05f, 0.4f);
                case "tank": return new Vector2(1.12f, 0.5f);
                default: return new Vector2(0.76f, 0.3f);
            }
        }

        /// <summary>Draw one vehicle centred at p, nose along dir. salt picks colour and variant; look = clean (driving),
        /// a wreck (rust, missing wheel, smashed glass, sometimes burnt out, hood up) or a wreck on a tow line.</summary>
        public static void Vehicle(Geo g, Vector2 p, Vector2 dir, string type, int salt, VehLook look, float y = 0.012f, int burntSim = -1, int plate = 0)
        {
            if (dir.sqrMagnitude < 1e-6f) dir = new Vector2(1f, 0f);
            dir = dir.normalized;
            bool wreck = look != VehLook.Driving;
            // v0.19.3: burntSim 0/1 = the sim decides (Expansion: only burnt-out shells look burnt, they can't be towed); -1 = hash
            bool burnt = wreck && (burntSim >= 0 ? burntSim == 1 : Hf(salt, 41) < 0.18f);
            bool rust = wreck && !burnt && Hf(salt, 42) < 0.7f;
            int missing = wreck && Hf(salt, 43) < 0.55f ? (int)(Hf(salt, 44) * 4f) % 4 : -1;   // wheel gone
            float sink = missing >= 0 && look == VehLook.Wreck ? 0.035f : 0f;
            Color paint;
            bool mil = type == "humvee" || type == "apc" || type == "tank";
            if (mil) paint = Lerp(OLIVE, OLIVE2, Hf(salt, 45));
            else paint = CAR_PAINT[(int)(Hf(salt, 46) * CAR_PAINT.Length) % CAR_PAINT.Length];
            if (rust) paint = Lerp(paint, RUSTY, 0.45f + Hf(salt, 47) * 0.35f);
            if (burnt) paint = Lerp(CHARRED, Hex("#4c3022"), Hf(salt, 48) * 0.6f);
            Color glass = wreck ? (Hf(salt, 49) < 0.6f || burnt ? GLASS_BROKEN : GLASS_V) : GLASS_V;
            var side = new Vector2(-dir.y, dir.x);
            float variant = Hf(salt, 50);
            var sz = VehicleSize(type);
            float hl = sz.x, hw = sz.y;
            switch (type)
            {
                case "tank": Tank(g, p, dir, side, paint, y, wreck, burnt, salt); g.Shadow(new ORect(p, dir, hl, hw).Quad(), 0.5f, 0.4f); return;
                case "apc": Apc(g, p, dir, side, paint, glass, y - sink, wreck, missing, salt); g.Shadow(new ORect(p, dir, hl, hw).Quad(), 0.55f, 0.4f); return;
                case "humvee":
                {
                    g.Frustum(new ORect(p, dir, hl, hw * 0.92f).Quad(), y + 0.1f - sink, new ORect(p, dir, hl - 0.02f, hw).Quad(), y + 0.32f, paint, Mul(paint, 1.04f));
                    var cb = new ORect(p - dir * 0.02f, dir, 0.36f, hw * 0.92f); var ct = new ORect(p - dir * 0.06f, dir, 0.3f, hw * 0.86f);
                    g.Frustum(cb.Quad(), y + 0.32f, ct.Quad(), y + 0.54f, Mul(paint, 0.95f), Mul(paint, 1.06f));
                    for (int s = -1; s <= 1; s += 2) g.Wall(p + dir * 0.28f + side * (hw * 0.9f + 0.005f) * s, p - dir * 0.2f + side * (hw * 0.9f + 0.005f) * s, side * s, y + 0.38f, y + 0.5f, glass);
                    g.Face(Geo.W(p + dir * 0.3f + side * hw * 0.86f, y + 0.52f), Geo.W(p + dir * 0.3f - side * hw * 0.86f, y + 0.52f), Geo.W(p + dir * 0.34f - side * hw * 0.9f, y + 0.34f), Geo.W(p + dir * 0.34f + side * hw * 0.9f, y + 0.34f), glass, new Vector3(dir.x, 0.4f, -dir.y));
                    // canvas-covered rear bed
                    g.Frustum(new ORect(p - dir * 0.52f, dir, 0.2f, hw * 0.92f).Quad(), y + 0.32f, new ORect(p - dir * 0.52f, dir, 0.18f, hw * 0.8f).Quad(), y + 0.5f, Hex("#5e5a42"), Hex("#6a6448"));
                    if (!wreck) { g.Cyl(p - dir * 0.05f, 0.08f, y + 0.54f, y + 0.62f, Mul(paint, 0.9f), paint, 6); g.Beam(Geo.W(p - dir * 0.05f, y + 0.6f), Geo.W(p + dir * 0.25f, y + 0.62f), 0.012f, Hex("#2a2a26")); }
                    break;
                }
                case "truck":
                    if (variant < 0.6f)
                    {
                        // box truck: cab forward, tall cargo box behind
                        var cab = new ORect(p + dir * (hl - 0.26f), dir, 0.24f, hw * 0.94f);
                        g.Box(cab.Quad(), y + 0.1f - sink, y + 0.36f, paint, paint);
                        g.Frustum(cab.Quad(), y + 0.36f, new ORect(cab.C - dir * 0.04f, dir, 0.18f, hw * 0.88f).Quad(), y + 0.6f, glass, Mul(paint, 1.05f));
                        Color boxc = Hf(salt, 51) < 0.5f ? Lerp(Hex("#a8a49a"), RUSTY, rust ? 0.35f : 0.05f) : Lerp(Hex("#8a7a5a"), RUSTY, rust ? 0.3f : 0f);
                        if (burnt) boxc = Mul(CHARRED, 1.2f);
                        var box = new ORect(p - dir * 0.24f, dir, hl - 0.28f, hw);
                        g.Box(new ORect(p - dir * 0.05f, dir, hl - 0.05f, hw * 0.7f).Quad(), y + 0.08f - sink, y + 0.18f, Hex("#2a2a26"), Hex("#2a2a26"));
                        g.Box(box.Quad(), y + 0.18f - sink, y + 0.82f - sink, boxc, Mul(boxc, 1.04f), 0.08f);
                        if (wreck && Hf(salt, 52) < 0.5f)
                        {
                            // rear roller door half open, dark inside
                            FaceEdge(box, 3, out var ra, out var rb, out var ro);
                            g.Wall(Vector2.Lerp(ra, rb, 0.1f) + ro * 0.004f, Vector2.Lerp(ra, rb, 0.9f) + ro * 0.004f, ro, y + 0.2f, y + 0.5f, INTERIOR);
                        }
                    }
                    else
                    {
                        // panel van
                        hl = 0.78f;
                        g.Box(new ORect(p - dir * 0.1f, dir, 0.68f, hw).Quad(), y + 0.09f - sink, y + 0.68f, paint, Mul(paint, 1.03f), 0.06f);
                        g.Box(new ORect(p + dir * 0.66f, dir, 0.12f, hw * 0.96f).Quad(), y + 0.09f - sink, y + 0.33f, paint, paint);
                        for (int s = -1; s <= 1; s += 2)
                        {
                            var a = p + dir * 0.48f + side * (hw + 0.006f) * s; var b = a - dir * 0.28f;
                            g.Wall(a, b, side * s, y + 0.4f, y + 0.6f, glass);
                        }
                        g.Face(Geo.W(p + dir * 0.58f + side * hw, y + 0.68f), Geo.W(p + dir * 0.58f - side * hw, y + 0.68f), Geo.W(p + dir * 0.64f - side * hw, y + 0.35f), Geo.W(p + dir * 0.64f + side * hw, y + 0.35f), glass, new Vector3(dir.x, 0.4f, -dir.y));
                        if (Hf(salt, 53) < 0.4f && !burnt) g.Wall(p - dir * 0.5f + side * (hw + 0.007f), p + dir * 0.2f + side * (hw + 0.007f), side, y + 0.3f, y + 0.36f, Hex("#a8a490"));   // faded stripe
                    }
                    break;
                default:   // pickup class: pickup or SUV
                    if (variant < 0.6f)
                    {
                        g.Box(new ORect(p, dir, hl, hw).Quad(), y + 0.09f - sink, y + 0.3f, paint, Mul(paint, 0.6f), 0.05f);
                        var cb = new ORect(p + dir * 0.12f, dir, 0.24f, hw * 0.96f); var ct = new ORect(p + dir * 0.08f, dir, 0.18f, hw * 0.86f);
                        g.Frustum(cb.Quad(), y + 0.3f, ct.Quad(), y + 0.52f, glass, Mul(paint, 0.95f));
                        var bed = new ORect(p - dir * 0.4f, dir, 0.34f, hw);
                        foreach (int f in new[] { 0, 2, 3 })
                        {
                            FaceEdge(bed, f, out var a, out var b, out var o);
                            g.Box(new[] { a, b, b - o * 0.03f, a - o * 0.03f }, y + 0.3f, y + 0.42f, paint, paint);
                        }
                        if (look == VehLook.Driving && Hf(salt, 54) < 0.6f)
                        {
                            // supplies lashed in the bed
                            g.BoxAt(p - dir * 0.45f, dir, 0.14f, 0.12f, y + 0.3f, y + 0.46f, Hex("#7a5536"), Hex("#8a6a48"));
                            g.Cyl(p - dir * 0.25f + side * 0.12f, 0.07f, y + 0.3f, y + 0.5f, Hex("#3a4a5a"), Hex("#2e2c28"), 6);
                        }
                    }
                    else
                    {
                        hl = 0.7f;
                        g.Box(new ORect(p, dir, hl, hw).Quad(), y + 0.09f - sink, y + 0.3f, paint, paint, 0.05f);
                        var cb = new ORect(p - dir * 0.1f, dir, 0.52f, hw * 0.96f); var ct = new ORect(p - dir * 0.14f, dir, 0.44f, hw * 0.86f);
                        g.Frustum(cb.Quad(), y + 0.3f, ct.Quad(), y + 0.5f, glass, Mul(paint, 0.95f));
                        if (Hf(salt, 55) < 0.5f) g.BoxAt(p - dir * 0.15f, dir, 0.3f, 0.2f, y + 0.5f, y + 0.53f, Hex("#2e2c28"), Hex("#3a3631"));   // roof rack
                    }
                    break;
            }
            if (plate > 0 && type != "tank") VehPlates(g, p, dir, side, hl, hw, y, plate);
            // wheels (one gone on many wrecks: that corner rests on the hub)
            for (int k = 0; k < 4; k++)
            {
                float along = (k < 2 ? 1f : -1f) * hl * 0.62f; float s = (k & 1) == 0 ? 1f : -1f;
                var wc = p + dir * along + side * (hw - 0.03f) * s;
                if (k == missing) { g.BoxAt(wc, dir, 0.05f, 0.04f, y - 0.01f, y + 0.06f, HUB, HUB); continue; }
                g.BoxAt(wc, dir, 0.1f, 0.06f, y - sink * 0.3f, y + 0.18f - sink * 0.3f, TYRE, TYRE);
                g.BoxAt(wc + side * 0.061f * s, dir, 0.04f, 0.002f, y + 0.05f, y + 0.12f, HUB, HUB);
            }
            // lights: lit when driving, dead / smashed on wrecks
            for (int s = -1; s <= 1; s += 2)
            {
                var hp = p + dir * (hl + 0.004f) + side * (hw * 0.7f * s);
                g.Wall(hp - side * 0.05f, hp + side * 0.05f, dir, y + 0.17f, y + 0.22f, look == VehLook.Driving ? Hex("#f0e0a0") : Hex("#6a6a5e"));
                var tp = p - dir * (hl + 0.004f) + side * (hw * 0.7f * s);
                g.Wall(tp - side * 0.05f, tp + side * 0.05f, -dir, y + 0.17f, y + 0.22f, Hex("#6a2a24"));
            }
            if (wreck && !mil && Hf(salt, 56) < 0.35f)
            {
                // hood propped up (stripped engine)
                var h0 = p + dir * (hl - 0.32f); var h1 = h0 + dir * 0.06f;
                g.Face(Geo.W(h0 + side * hw * 0.85f, y + 0.32f), Geo.W(h0 - side * hw * 0.85f, y + 0.32f), Geo.W(h1 - side * hw * 0.85f, y + 0.58f), Geo.W(h1 + side * hw * 0.85f, y + 0.58f), paint, new Vector3(dir.x, 0.3f, -dir.y));
            }
            if (wreck && Hf(salt, 57) < 0.5f)
            {
                // a door hanging open on one side
                float s = Hf(salt, 58) < 0.5f ? 1f : -1f;
                var d0 = p + dir * 0.18f + side * hw * s;
                var d1 = d0 + (side * s * 0.75f - dir * 0.66f).normalized * 0.26f;
                g.Box(new[] { d0, d1, d1 + dir * 0.015f, d0 + dir * 0.015f }, y + 0.1f, y + 0.36f, paint, paint);
            }
            g.Shadow(new ORect(p, dir, hl, hw).Quad(), type == "truck" ? 0.6f : 0.35f, 0.38f);
        }

        static readonly Color PLATE_STEEL = Hex("#5e5f5a"), PLATE_RUST = Hex("#6e4a32"), PLATE_BOLT = Hex("#2e2e2a");

        /// <summary>v0.19.3 armour plating: welded scrap panels along both flanks (and over the nose and grille at level 2).</summary>
        static void VehPlates(Geo g, Vector2 p, Vector2 dir, Vector2 side, float hl, float hw, float y, int plate)
        {
            for (int s = -1; s <= 1; s += 2)
            {
                int n = 3;
                for (int k = 0; k < n; k++)
                {
                    float a0 = -hl * 0.85f + k * (hl * 1.7f / n), a1 = a0 + hl * 1.7f / n - 0.02f;
                    Color c = (k + s) % 2 == 0 ? PLATE_STEEL : Lerp(PLATE_STEEL, PLATE_RUST, 0.55f);
                    var A = p + dir * a0 + side * (hw + 0.012f) * s; var B = p + dir * a1 + side * (hw + 0.012f) * s;
                    g.Wall(A, B, side * s, y + 0.1f, y + (plate >= 2 ? 0.42f : 0.3f), c);
                    g.Wall(Vector2.Lerp(A, B, 0.1f) + side * 0.004f * s, Vector2.Lerp(A, B, 0.14f) + side * 0.004f * s, side * s, y + 0.25f, y + 0.27f, PLATE_BOLT);
                }
            }
            // nose plate / ram bar
            var nose = p + dir * (hl + 0.03f);
            g.BoxAt(nose, dir, 0.03f, hw * 0.95f, y + 0.08f, y + (plate >= 2 ? 0.32f : 0.22f), PLATE_STEEL, Lerp(PLATE_STEEL, PLATE_RUST, 0.3f));
            if (plate >= 2)
            {
                // mesh grille over the windscreen
                for (int k = -2; k <= 2; k++)
                {
                    var b0 = p + dir * (hl * 0.42f) + side * (k * hw * 0.35f);
                    g.Beam(Geo.W(b0, y + 0.3f), Geo.W(b0 - dir * 0.08f, y + 0.55f), 0.012f, PLATE_BOLT);
                }
            }
        }

        static void Apc(Geo g, Vector2 p, Vector2 dir, Vector2 side, Color paint, Color glass, float y, bool wreck, int missing, int salt)
        {
            const float hl = 1.05f, hw = 0.4f;
            g.Frustum(new ORect(p, dir, hl * 0.92f, hw * 0.8f).Quad(), y + 0.12f, new ORect(p - dir * 0.02f, dir, hl, hw).Quad(), y + 0.36f, Mul(paint, 0.9f), paint);
            g.Frustum(new ORect(p - dir * 0.05f, dir, hl - 0.02f, hw).Quad(), y + 0.36f, new ORect(p - dir * 0.12f, dir, hl - 0.2f, hw * 0.82f).Quad(), y + 0.62f, paint, Mul(paint, 1.05f));
            for (int s = -1; s <= 1; s += 2) g.Wall(p + dir * 0.75f + side * (hw * 0.85f) * s, p + dir * 0.55f + side * (hw * 0.85f) * s, side * s, y + 0.44f, y + 0.52f, glass);
            g.Cyl(p - dir * 0.1f, 0.16f, y + 0.62f, y + 0.76f, Mul(paint, 0.92f), paint, 8);
            if (!wreck || Hf(salt, 60) < 0.5f) g.Beam(Geo.W(p - dir * 0.1f, y + 0.7f), Geo.W(p + dir * 0.5f, y + 0.72f), 0.02f, Hex("#2a2a26"));
            for (int k = 0; k < 8; k++)
            {
                float along = (-0.75f + (k / 2) * 0.5f); float s = (k & 1) == 0 ? 1f : -1f;
                if (k == missing * 2) continue;
                g.BoxAt(p + dir * along + side * (hw - 0.02f) * s, dir, 0.12f, 0.07f, y - 0.01f, y + 0.22f, TYRE, TYRE);
            }
        }

        static void Tank(Geo g, Vector2 p, Vector2 dir, Vector2 side, Color paint, float y, bool wreck, bool burnt, int salt)
        {
            const float hl = 1.12f, hw = 0.5f;
            // tracks
            for (int s = -1; s <= 1; s += 2)
            {
                var tc = p + side * (hw - 0.13f) * s;
                g.Frustum(new ORect(tc, dir, hl - 0.1f, 0.12f).Quad(), y, new ORect(tc, dir, hl, 0.13f).Quad(), y + 0.26f, Hex("#2a2a26"), Hex("#3a3a34"));
                if (wreck && Hf(salt, 61 + s) < 0.4f) g.BoxAt(tc + dir * (hl + 0.25f) + side * 0.05f * s, dir, 0.3f, 0.1f, y, y + 0.03f, Hex("#2a2a26"), Hex("#33332e"));   // thrown track
            }
            g.Frustum(new ORect(p, dir, hl - 0.06f, hw - 0.22f).Quad(), y + 0.12f, new ORect(p, dir, hl - 0.04f, hw - 0.04f).Quad(), y + 0.34f, Mul(paint, 0.9f), paint);
            // turret (sometimes knocked askew on a wreck)
            float ta = wreck ? (Hf(salt, 64) - 0.5f) * 1.6f : 0f;
            var td = new Vector2(dir.x * Mathf.Cos(ta) - dir.y * Mathf.Sin(ta), dir.x * Mathf.Sin(ta) + dir.y * Mathf.Cos(ta));
            var tp = p - dir * 0.1f;
            g.Frustum(new ORect(tp, td, 0.42f, 0.32f).Quad(), y + 0.34f, new ORect(tp - td * 0.04f, td, 0.34f, 0.26f).Quad(), y + 0.54f, paint, Mul(paint, 1.06f));
            g.Beam(Geo.W(tp + td * 0.34f, y + 0.46f), Geo.W(tp + td * 1.3f, y + (wreck ? 0.4f : 0.48f)), 0.035f, Mul(paint, 0.8f));
            g.Cyl(tp - td * 0.1f + new Vector2(-td.y, td.x) * 0.12f, 0.07f, y + 0.54f, y + 0.6f, Mul(paint, 0.9f), paint, 6);
            if (burnt) g.Disc(p, 1.1f, 0.016f, Hex("#1a1816"), Hex("#3a342a"), 9, salt);
        }
    }
}
