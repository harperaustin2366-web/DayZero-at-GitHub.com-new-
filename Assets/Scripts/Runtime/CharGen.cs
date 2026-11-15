// Procedural low-poly humanoids (survivors and zombies), v0.11.4a. Pure C# on UnityEngine value
// types only, so the same code runs in Unity (MapView batches the crowd into one dynamic mesh per
// frame) and in the offline preview harness (work/unity-verify/Preview).
//
// Art bible section 8: ~1.75 m, rigid box parts parented to a small bone hierarchy (no skinning,
// no animation assets), vertex colours, flat shading baked per face at batch time from the world
// normal (Geo.Shade, the same key light as the map). Parts are authored in metres, +Y up, +Z forward.
//
// Bones: 0 hips, 1 torso, 2 head, 3 upper arm L, 4 forearm L, 5 upper arm R, 6 forearm R,
//        7 thigh L, 8 shin L, 9 thigh R, 10 shin R, 11 rifle (child of torso; placed per pose).
using System.Collections.Generic;
using UnityEngine;

namespace DayZero.Runtime
{
    /// <summary>Rigid transform: rotation axes X, Y, Z (columns) + translation T.</summary>
    public struct M34
    {
        public Vector3 X, Y, Z, T;
        public static readonly M34 I = new M34 { X = new Vector3(1, 0, 0), Y = new Vector3(0, 1, 0), Z = new Vector3(0, 0, 1), T = Vector3.zero };
        public Vector3 P(Vector3 v) => X * v.x + Y * v.y + Z * v.z + T;
        public Vector3 R(Vector3 v) => X * v.x + Y * v.y + Z * v.z;
        public M34 Mul(M34 c) => new M34 { X = R(c.X), Y = R(c.Y), Z = R(c.Z), T = P(c.T) };

        /// <summary>Local transform: translate t, then rotate yaw (Y) * pitch (X) * roll (Z), radians.
        /// Positive pitch tips +Y forward (torso leans forward; a hanging limb swings back).</summary>
        public static M34 TRS(Vector3 t, float pitch, float yaw = 0f, float roll = 0f)
        {
            float cp = Mathf.Cos(pitch), sp = Mathf.Sin(pitch), cy = Mathf.Cos(yaw), sy = Mathf.Sin(yaw), cr = Mathf.Cos(roll), sr = Mathf.Sin(roll);
            // Rz
            var x = new Vector3(cr, sr, 0f); var y = new Vector3(-sr, cr, 0f); var z = new Vector3(0f, 0f, 1f);
            // Rx * (Rz)
            x = new Vector3(x.x, x.y * cp - x.z * sp, x.y * sp + x.z * cp);
            y = new Vector3(y.x, y.y * cp - y.z * sp, y.y * sp + y.z * cp);
            z = new Vector3(z.x, z.y * cp - z.z * sp, z.y * sp + z.z * cp);
            // Ry * (...)
            x = new Vector3(x.x * cy + x.z * sy, x.y, -x.x * sy + x.z * cy);
            y = new Vector3(y.x * cy + y.z * sy, y.y, -y.x * sy + y.z * cy);
            z = new Vector3(z.x * cy + z.z * sy, z.y, -z.x * sy + z.z * cy);
            return new M34 { X = x, Y = y, Z = z, T = t };
        }
        public static M34 Scaled(M34 m, float s) => new M34 { X = m.X * s, Y = m.Y * s, Z = m.Z * s, T = m.T };
    }

    /// <summary>Flat-shaded quads of one rigid part, in its bone's local space (metres).</summary>
    public sealed class CharPart
    {
        public readonly List<Vector3> P = new List<Vector3>(96);   // 4 per quad
        public readonly List<Vector3> N = new List<Vector3>(24);   // 1 per quad
        public readonly List<Color> C = new List<Color>(24);       // 1 per quad
        public readonly List<bool> Glow = new List<bool>(24);      // unshaded (eyes, flashes)
        public int Quads => N.Count;

        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color col, bool glow = false)
        {
            var n = Vector3.Cross(b - a, c - a).normalized;
            P.Add(a); P.Add(b); P.Add(c); P.Add(d); N.Add(n); C.Add(col); Glow.Add(glow);
        }

        /// <summary>Single flat quad facing +Z (or -Z) at c: eyes, gore, badges. 4 verts instead of a 20-vert box.</summary>
        public void Plate(Vector3 c, float sx, float sy, Color col, bool back = false, bool glow = false)
        {
            float x = sx * 0.5f, y = sy * 0.5f;
            if (!back) Quad(c + new Vector3(-x, -y, 0f), c + new Vector3(x, -y, 0f), c + new Vector3(x, y, 0f), c + new Vector3(-x, y, 0f), col, glow);
            else Quad(c + new Vector3(x, -y, 0f), c + new Vector3(-x, -y, 0f), c + new Vector3(-x, y, 0f), c + new Vector3(x, y, 0f), col, glow);
        }

        /// <summary>Box centred at c with size s; top face scaled by taper (x,z); per-face colour override for the front (+Z).
        /// bottom=false skips the underside (never seen from the game camera).</summary>
        public void Box(Vector3 c, Vector3 s, Color col, float taperX = 1f, float taperZ = 1f, bool bottom = false, Color? front = null, Color? top = null)
        {
            float hx = s.x * 0.5f, hy = s.y * 0.5f, hz = s.z * 0.5f, tx = hx * taperX, tz = hz * taperZ;
            var b0 = c + new Vector3(-hx, -hy, -hz); var b1 = c + new Vector3(hx, -hy, -hz); var b2 = c + new Vector3(hx, -hy, hz); var b3 = c + new Vector3(-hx, -hy, hz);
            var t0 = c + new Vector3(-tx, hy, -tz); var t1 = c + new Vector3(tx, hy, -tz); var t2 = c + new Vector3(tx, hy, tz); var t3 = c + new Vector3(-tx, hy, tz);
            // outward winding so Cross(b-a, c-a) points out
            Quad(t0, t3, t2, t1, top ?? col);                 // top
            if (bottom) Quad(b0, b1, b2, b3, col);
            Quad(b3, b2, t2, t3, front ?? col);               // front +Z
            Quad(b1, b0, t0, t1, col);                        // back -Z
            Quad(b2, b1, t1, t2, col);                        // right +X
            Quad(b0, b3, t3, t0, col);                        // left -X
        }
    }

    /// <summary>One character variant: parts per bone + proportions.</summary>
    public sealed class CharModel
    {
        public const int BONES = 12;
        public readonly CharPart[] Parts = new CharPart[BONES];
        public bool Zombie, Rifle;
        public float Bulk = 1f;                // torso width multiplier (bloated zombies)
        public float Hunch;                    // extra spine pitch (zombies)
        public float HeadTilt;                 // zombie head roll
        public bool OneArmDown;                // zombie: one arm hangs
        public float LegLen = 0.84f;           // hip height (m)
        public int Verts { get { int n = 0; foreach (var p in Parts) if (p != null) n += p.Quads * 4; return n; } }
        public CharModel() { for (int i = 0; i < BONES; i++) Parts[i] = new CharPart(); }
    }

    /// <summary>Pose: joint angles in radians (see M34.TRS for the sign convention).</summary>
    public struct Pose
    {
        public float HipY, HipPitch, HipRoll, HipYaw;
        public float SpinePitch, SpineRoll, SpineYaw;
        public float HeadPitch, HeadRoll, HeadYaw;
        public float ArmLP, ArmLR, ElbowL, ArmRP, ArmRR, ElbowR;
        public float ThighLP, ThighLR, KneeL, ThighRP, ThighRR, KneeR;
        public int RifleMode;                  // 0 slung on back, 1 low ready, 2 aimed
        public float Lie;                      // 0 standing .. 1 lying on the back (dead)
    }

    public static class CharGen
    {
        static Color Hex(string s) => Geo.Hex(s);
        static Color Lerp(Color a, Color b, float t) => Geo.Lerp(a, b, t);
        static Color Mul(Color c, float k) => Geo.Mul(c, k);

        // ---- palette (art bible 2.5 + 8) ----
        static readonly Color[] SKIN = { Hex("#e0b896"), Hex("#c89a74"), Hex("#a8785a"), Hex("#7a5440"), Hex("#5a3c2c") };
        static readonly Color[] HAIR = { Hex("#2a2018"), Hex("#4a3220"), Hex("#7a5a34"), Hex("#a8885a"), Hex("#6a6a64"), Hex("#7a3a22") };
        static readonly Color[] JACKET = { Hex("#5a5a3a"), Hex("#6a4a30"), Hex("#4a3a3a"), Hex("#4f4838"), Hex("#5e5440") };
        static readonly Color[] SHIRT = { Hex("#8a8270"), Hex("#6e6a5a"), Hex("#7a6a52") };
        static readonly Color[] PANTS = { Hex("#3a4a5a"), Hex("#4f4838"), Hex("#6b6053"), Hex("#3a3a2c"), Hex("#4a4236") };
        static readonly Color BOOT = Hex("#2e2820"), PACK = Hex("#5a5034"), PACK2 = Hex("#6a5a3a"), STRAP = Hex("#3a3226");
        static readonly Color ACCENT = Hex("#bd882f"), METAL = Hex("#2d2b28"), METAL2 = Hex("#4a4b4a"), WOOD = Hex("#7a5536");
        static readonly Color ZSKIN = Hex("#7a8068"), ZSKIN_D = Hex("#4a4e3e"), GORE = Hex("#4a1e1a"), RIOT = Hex("#2a2a2e");
        static readonly Color EYE = Hex("#1e1a14"), ZEYE = Hex("#c8b45a"), SACK = Hex("#8a7a54");

        static float H(int a, int b) => MapGen.Hf(a * 7919 + 13, b * 104729 + 7);

        /// <summary>Survivor variant: job decides the silhouette (hat/helmet, vest, pack, rifle);
        /// seed picks skin, hair and the clothing colourway. carry adds a loot sack on the back.</summary>
        public static CharModel Survivor(string job, int seed, bool rifle, bool carry)
        {
            var m = new CharModel { Rifle = rifle };
            Color skin = SKIN[(int)(H(seed, 1) * SKIN.Length) % SKIN.Length];
            Color hair = HAIR[(int)(H(seed, 2) * HAIR.Length) % HAIR.Length];
            Color jacket = JACKET[(int)(H(seed, 3) * JACKET.Length) % JACKET.Length];
            Color shirt = SHIRT[(int)(H(seed, 4) * SHIRT.Length) % SHIRT.Length];
            Color pants = PANTS[(int)(H(seed, 5) * PANTS.Length) % PANTS.Length];
            Color top = jacket, vest = default; bool hasVest = false;
            string hat = H(seed, 6) < 0.35f ? "beanie" : H(seed, 6) < 0.55f ? "cap" : "hair";
            string pack = H(seed, 7) < 0.6f ? "small" : "big";
            bool bandana = H(seed, 8) < 0.3f;
            switch (job)
            {
                case "Soldier": top = Hex("#4f4838"); pants = Hex("#3a3a2c"); hat = "helmet"; hasVest = true; vest = Hex("#3e3a2c"); pack = "mil"; break;
                case "Cop": top = Hex("#3a4048"); pants = Hex("#2e3238"); hat = "cop"; hasVest = true; vest = Hex("#2a2c30"); pack = "none"; break;
                case "Medic": top = Hex("#8a8678"); hat = "hair"; pack = "medic"; break;
                case "Engineer": top = Hex("#5a5240"); hasVest = true; vest = Hex("#8a6a30"); hat = "hard"; pack = "tool"; break;
                case "Farmer": top = Lerp(shirt, Hex("#6a5a3e"), 0.5f); pants = Hex("#4a4236"); hat = "straw"; pack = "none"; break;
                case "Scavenger": top = Lerp(jacket, Hex("#5a5034"), 0.3f); hat = H(seed, 9) < 0.5f ? "hood" : "beanie"; pack = "big"; bandana = true; break;
                case "Child": case "Kid": pack = "none"; break;
            }
            float S = job == "Child" || job == "Kid" ? 0.7f : 1f;
            BuildBody(m, skin, hair, top, shirt, pants, BOOT, hasVest, vest, hat, S, false, seed);
            var torso = m.Parts[1];
            // mustard armband (player survivors) on the left upper arm
            m.Parts[3].Box(new Vector3(0f, -0.09f, 0f), new Vector3(0.135f, 0.05f, 0.135f), ACCENT);
            if (bandana) m.Parts[2].Box(new Vector3(0f, 0.06f, 0.06f), new Vector3(0.235f, 0.07f, 0.17f), Hex("#6e3226"));
            // packs
            switch (pack)
            {
                case "small": torso.Box(new Vector3(0f, 0.32f, -0.17f), new Vector3(0.3f, 0.32f, 0.12f), PACK, 0.9f, 1f, false, null, Mul(PACK, 1.08f)); break;
                case "big":
                    torso.Box(new Vector3(0f, 0.30f, -0.2f), new Vector3(0.36f, 0.46f, 0.18f), PACK2, 0.9f, 0.9f);
                    torso.Box(new Vector3(0f, 0.58f, -0.2f), new Vector3(0.3f, 0.1f, 0.16f), Hex("#4a4a30"));   // bedroll
                    break;
                case "mil": torso.Box(new Vector3(0f, 0.30f, -0.18f), new Vector3(0.34f, 0.38f, 0.14f), Hex("#3e3a2c")); break;
                case "medic": torso.Box(new Vector3(0f, 0.30f, -0.17f), new Vector3(0.3f, 0.3f, 0.12f), Hex("#7a7466"), 1f, 1f, false, null, null);
                    torso.Plate(new Vector3(0f, 0.33f, -0.232f), 0.12f, 0.04f, Hex("#8a2e26"), true); torso.Plate(new Vector3(0f, 0.33f, -0.233f), 0.04f, 0.12f, Hex("#8a2e26"), true); break;
                case "tool": torso.Box(new Vector3(0f, 0.30f, -0.17f), new Vector3(0.3f, 0.3f, 0.12f), PACK); break;
            }
            if (carry) torso.Box(new Vector3(0.03f, 0.2f, -0.3f), new Vector3(0.36f, 0.34f, 0.26f), Mul(SACK, 0.85f), 0.75f, 0.7f, false, null, SACK);   // loot sack (Person.Carry)
            if (rifle) BuildRifle(m.Parts[11], H(seed, 10) < 0.5f);
            return m;
        }

        /// <summary>Zombie variant (0..7): grey-green skin, torn desaturated clothes, gore; bloated / runner / riot types.</summary>
        public static CharModel Zombie(int variant, int seed)
        {
            var m = new CharModel { Zombie = true };
            int v = ((variant % 8) + 8) % 8;
            Color skin = Lerp(ZSKIN, ZSKIN_D, H(seed, 1) * 0.6f);
            Color hair = H(seed, 2) < 0.4f ? skin : Mul(HAIR[(int)(H(seed, 3) * HAIR.Length) % HAIR.Length], 0.7f);
            Color top = Dead(JACKET[(int)(H(seed, 4) * JACKET.Length) % JACKET.Length]);
            if (H(seed, 5) < 0.4f) top = Dead(SHIRT[(int)(H(seed, 6) * SHIRT.Length) % SHIRT.Length]);
            Color pants = Dead(PANTS[(int)(H(seed, 7) * PANTS.Length) % PANTS.Length]);
            string hat = "hair";
            m.Hunch = 0.22f + H(seed, 8) * 0.18f;
            m.HeadTilt = (H(seed, 9) - 0.5f) * 0.7f;
            m.OneArmDown = v == 3 || v == 6;
            float S = 1f;
            bool vest = false; Color vc = default;
            switch (v)
            {
                case 2: m.Bulk = 1.4f; top = Dead(Hex("#8a8270")); break;                         // bloated
                case 4: S = 1.04f; m.Bulk = 0.85f; break;                                          // runner (thin, long)
                case 5: top = RIOT; pants = Mul(RIOT, 1.1f); hat = "helmet_riot"; vest = true; vc = Hex("#1e1e22"); break; // riot
                case 7: top = Dead(Hex("#4a5a6a")); hat = "cap"; break;                            // ex-worker
            }
            BuildBody(m, skin, hair, top, top, pants, Mul(BOOT, 0.9f), vest, vc, hat, S, true, seed);
            // gore patches
            float tz = 0.116f * (m.Bulk > 1.1f ? 1.25f : 1f) + 0.003f;
            m.Parts[1].Plate(new Vector3((H(seed, 11) - 0.5f) * 0.16f, 0.16f + H(seed, 12) * 0.2f, tz), 0.12f, 0.1f, GORE);
            if (H(seed, 13) < 0.6f) m.Parts[2].Plate(new Vector3(0.06f, 0.1f, 0.128f), 0.05f, 0.07f, GORE);
            if (H(seed, 14) < 0.5f) m.Parts[8].Plate(new Vector3(0f, -0.12f, 0.071f), 0.1f, 0.1f, GORE);
            return m;
        }

        static Color Dead(Color c) => Mul(Geo.Desat(c, 0.6f), 0.75f);

        static void BuildBody(CharModel m, Color skin, Color hair, Color top, Color shirt, Color pants, Color boot, bool vest, Color vestC, string hat, float S, bool zombie, int seed)
        {
            float B = m.Bulk;
            m.LegLen = 0.84f * S;
            var hips = m.Parts[0]; var torso = m.Parts[1]; var head = m.Parts[2];
            // hips / belt
            hips.Box(new Vector3(0f, 0.02f * S, 0f), new Vector3(0.34f * S * Mathf.Max(1f, B * 0.9f), 0.16f * S, 0.21f * S), pants);
            // torso (tapered toward the waist: authored waist-up, wider shoulders)
            float tw = 0.42f * S * B, td = 0.23f * S * (B > 1.1f ? 1.25f : 1f);
            torso.Box(new Vector3(0f, 0.27f * S, 0f), new Vector3(tw * 0.86f, 0.5f * S, td), top, 1.18f, 1f, false, zombie ? Mul(top, 0.92f) : (Color?)null, null);
            if (!zombie) torso.Plate(new Vector3(0f, 0.47f * S, td * 0.5f + 0.003f), 0.12f * S, 0.08f * S, shirt);   // collar / shirt at the neck
            if (vest) torso.Box(new Vector3(0f, 0.3f * S, 0f), new Vector3(tw * 0.95f, 0.34f * S, td + 0.05f * S), vestC, 1.05f, 1f);
            // neck + head (art bible: head 1.25x, ~0.26 m)
            head.Box(new Vector3(0f, 0.03f * S, 0f), new Vector3(0.1f * S, 0.08f * S, 0.1f * S), skin);
            head.Box(new Vector3(0f, 0.16f * S, 0.01f * S), new Vector3(0.22f * S, 0.24f * S, 0.23f * S), skin);
            // eyes (zombies: pale yellow, unshaded so they read in the dark)
            foreach (float sx in new[] { -0.05f, 0.05f })
                head.Plate(new Vector3(sx * S, 0.18f * S, 0.1265f * S), 0.035f * S, 0.03f * S, zombie ? ZEYE : EYE, false, zombie);
            HatOrHair(head, hat, hair, S, seed);
            // arms: upper (sleeve) + forearm (sleeve or bare/torn for zombies) + hand
            Color sleeveL = top, sleeveR = top, foreL = top, foreR = top;
            if (zombie) { if (H(seed, 20) < 0.6f) foreL = skin; if (H(seed, 21) < 0.5f) { foreR = skin; if (H(seed, 22) < 0.5f) sleeveR = Mul(skin, 0.95f); } }
            for (int s = 0; s < 2; s++)
            {
                var up = m.Parts[s == 0 ? 3 : 5]; var lo = m.Parts[s == 0 ? 4 : 6];
                float aw = 0.11f * S * (B > 1.1f ? 1.2f : 1f);
                up.Box(new Vector3(0f, -0.14f * S, 0f), new Vector3(aw, 0.3f * S, aw), s == 0 ? sleeveL : sleeveR, 1.1f, 1.1f);
                lo.Box(new Vector3(0f, -0.12f * S, 0f), new Vector3(aw * 0.9f, 0.26f * S, aw * 0.9f), s == 0 ? foreL : foreR);
                lo.Box(new Vector3(0f, -0.29f * S, 0.005f), new Vector3(0.09f * S, 0.1f * S, 0.1f * S), zombie ? Mul(skin, 0.9f) : skin, 1f, 1f);
                if (zombie && ((s == 0 ? foreL : foreR).Equals(skin)))   // torn sleeve edge
                    lo.Box(new Vector3(0f, -0.01f * S, 0f), new Vector3(aw * 1.05f, 0.05f * S, aw * 1.05f), Mul(top, 0.85f));
            }
            // legs: thigh + shin + oversized boot
            for (int s = 0; s < 2; s++)
            {
                var th = m.Parts[s == 0 ? 7 : 9]; var sh = m.Parts[s == 0 ? 8 : 10];
                float lw = 0.15f * S;
                th.Box(new Vector3(0f, -0.21f * S, 0f), new Vector3(lw, 0.44f * S, lw * 1.05f), pants, 0.9f, 0.9f);
                sh.Box(new Vector3(0f, -0.17f * S, 0f), new Vector3(lw * 0.88f, 0.34f * S, lw * 0.9f), zombie && H(seed, 30 + s) < 0.3f ? Mul(skin, 0.9f) : pants);
                sh.Box(new Vector3(0f, -0.36f * S, 0.035f * S), new Vector3(0.14f * S, 0.08f * S, 0.27f * S), boot, 1f, 0.9f);
            }
        }

        static void HatOrHair(CharPart head, string hat, Color hair, float S, int seed)
        {
            switch (hat)
            {
                case "helmet":
                    head.Box(new Vector3(0f, 0.27f * S, 0f), new Vector3(0.27f * S, 0.1f * S, 0.29f * S), Hex("#4a4634"), 0.85f, 0.85f);
                    head.Box(new Vector3(0f, 0.22f * S, -0.02f), new Vector3(0.28f * S, 0.05f * S, 0.29f * S), Hex("#3e3a2c"));
                    break;
                case "helmet_riot":
                    head.Box(new Vector3(0f, 0.25f * S, 0f), new Vector3(0.27f * S, 0.15f * S, 0.28f * S), RIOT, 0.85f, 0.85f);
                    head.Box(new Vector3(0f, 0.17f * S, 0.14f * S), new Vector3(0.22f * S, 0.1f * S, 0.01f), Hex("#3a4448"));
                    break;
                case "cop":
                    head.Box(new Vector3(0f, 0.27f * S, 0f), new Vector3(0.24f * S, 0.08f * S, 0.25f * S), Hex("#26282c"));
                    head.Box(new Vector3(0f, 0.24f * S, 0.12f * S), new Vector3(0.22f * S, 0.02f * S, 0.1f * S), Hex("#1e1e20"));
                    break;
                case "hard":
                    head.Box(new Vector3(0f, 0.28f * S, 0f), new Vector3(0.26f * S, 0.09f * S, 0.27f * S), Hex("#a8883a"), 0.8f, 0.8f);
                    head.Box(new Vector3(0f, 0.24f * S, 0.03f), new Vector3(0.28f * S, 0.02f * S, 0.32f * S), Hex("#987a32"));
                    break;
                case "straw":
                    head.Box(new Vector3(0f, 0.24f * S, 0f), new Vector3(0.38f * S, 0.02f * S, 0.38f * S), Hex("#7e7048"), 1f, 1f);
                    head.Box(new Vector3(0f, 0.29f * S, 0f), new Vector3(0.22f * S, 0.09f * S, 0.22f * S), Hex("#8a7a4e"), 0.85f, 0.85f);
                    break;
                case "beanie":
                    head.Box(new Vector3(0f, 0.27f * S, -0.005f), new Vector3(0.24f * S, 0.1f * S, 0.245f * S), H(seed, 40) < 0.5f ? Hex("#3a3a30") : Hex("#5a3028"), 0.85f, 0.85f);
                    break;
                case "cap":
                    head.Box(new Vector3(0f, 0.27f * S, 0f), new Vector3(0.235f * S, 0.07f * S, 0.24f * S), H(seed, 41) < 0.5f ? Hex("#4a4a3a") : Hex("#3a4048"));
                    head.Box(new Vector3(0f, 0.25f * S, 0.15f * S), new Vector3(0.2f * S, 0.02f * S, 0.1f * S), Hex("#2e2e26"));
                    break;
                case "hood":
                    head.Box(new Vector3(0f, 0.2f * S, -0.045f * S), new Vector3(0.26f * S, 0.27f * S, 0.2f * S), Hex("#4a4634"), 0.85f, 1f, false, null, null);   // hood: face stays visible
                    break;
                default:
                    head.Box(new Vector3(0f, 0.27f * S, -0.01f), new Vector3(0.235f * S, 0.05f * S, 0.24f * S), hair);
                    head.Box(new Vector3(0f, 0.17f * S, -0.115f * S), new Vector3(0.23f * S, 0.17f * S, 0.03f * S), hair);
                    break;
            }
        }

        /// <summary>Procedural rifle (no Quaternius gun pack in the project): receiver, barrel, stock, magazine, optional optic.
        /// Authored along +Z with the grip at the origin; ~1 m long.</summary>
        static void BuildRifle(CharPart r, bool wood)
        {
            Color stock = wood ? WOOD : Hex("#3a3a34");
            r.Box(new Vector3(0f, 0.02f, 0.08f), new Vector3(0.06f, 0.1f, 0.38f), METAL);           // receiver + handguard
            r.Box(new Vector3(0f, 0.035f, 0.42f), new Vector3(0.03f, 0.03f, 0.36f), METAL2);       // barrel
            r.Box(new Vector3(0f, 0.0f, -0.2f), new Vector3(0.045f, 0.1f, 0.24f), stock, 1f, 1f); // stock
            r.Box(new Vector3(0f, -0.07f, 0.1f), new Vector3(0.035f, 0.11f, 0.06f), METAL, 1f, 1f); // magazine
            if (!wood) r.Box(new Vector3(0f, 0.085f, 0.06f), new Vector3(0.035f, 0.035f, 0.12f), Hex("#2a3a3a")); // optic
        }

        /// <summary>Distant LOD: torso+legs and head as 2 boxes (one quad set, bone 0 only).</summary>
        public static CharModel Low(Color top, Color pants, Color head, bool zombie)
        {
            var m = new CharModel { Zombie = zombie };
            var p = m.Parts[0];
            p.Box(new Vector3(0f, -0.42f, 0f), new Vector3(0.24f, 0.84f, 0.18f), pants);
            p.Box(new Vector3(0f, 0.28f, 0f), new Vector3(0.4f, 0.56f, 0.24f), top, 1f, 1f);
            p.Box(new Vector3(0f, 0.72f, 0f), new Vector3(0.24f, 0.26f, 0.24f), head);
            return m;
        }

        // ================= skeleton =================
        // bone offsets (metres) relative to the parent, scaled by the model's leg length
        /// <summary>Bone matrices for a model in a pose. root = feet position/orientation (metres).</summary>
        public static void Bones(CharModel m, in Pose p, M34 root, M34[] o)
        {
            float S = m.LegLen / 0.84f;
            if (p.Lie > 0f)
            {
                // fall backward about the feet; lying flat at Lie = 1
                float a = -p.Lie * 1.52f;
                root = root.Mul(M34.TRS(new Vector3(0f, p.Lie * 0.1f, 0f), a));
            }
            var hips = root.Mul(M34.TRS(new Vector3(0f, p.HipY > 0f ? p.HipY : m.LegLen, 0f), p.HipPitch, p.HipYaw, p.HipRoll));
            o[0] = hips;
            var torso = hips.Mul(M34.TRS(new Vector3(0f, 0.06f * S, 0f), p.SpinePitch + m.Hunch, p.SpineYaw, p.SpineRoll));
            o[1] = torso;
            o[2] = torso.Mul(M34.TRS(new Vector3(0f, 0.5f * S, 0.01f), p.HeadPitch, p.HeadYaw, p.HeadRoll + m.HeadTilt));
            float sh = 0.235f * S * (m.Bulk > 1.1f ? 1.15f : m.Bulk < 0.9f ? 0.92f : 1f);
            o[3] = torso.Mul(M34.TRS(new Vector3(-sh, 0.46f * S, 0f), p.ArmLP, 0f, p.ArmLR));
            o[4] = o[3].Mul(M34.TRS(new Vector3(0f, -0.29f * S, 0f), p.ElbowL));
            o[5] = torso.Mul(M34.TRS(new Vector3(sh, 0.46f * S, 0f), p.ArmRP, 0f, p.ArmRR));
            o[6] = o[5].Mul(M34.TRS(new Vector3(0f, -0.29f * S, 0f), p.ElbowR));
            o[7] = hips.Mul(M34.TRS(new Vector3(-0.09f * S, -0.02f * S, 0f), p.ThighLP, 0f, p.ThighLR));
            o[8] = o[7].Mul(M34.TRS(new Vector3(0f, -0.43f * S, 0f), p.KneeL));
            o[9] = hips.Mul(M34.TRS(new Vector3(0.09f * S, -0.02f * S, 0f), p.ThighRP, 0f, p.ThighRR));
            o[10] = o[9].Mul(M34.TRS(new Vector3(0f, -0.43f * S, 0f), p.KneeR));
            switch (p.RifleMode)
            {
                case 2: o[11] = torso.Mul(M34.TRS(new Vector3(0.09f * S, 0.4f * S, 0.24f * S), -p.SpinePitch - m.Hunch + p.HeadPitch * 0.3f, 0f, 0f)); break;   // shouldered, level
                case 1: o[11] = torso.Mul(M34.TRS(new Vector3(0.05f * S, 0.22f * S, 0.24f * S), 0.55f, -0.35f, 0.1f)); break;     // low ready
                default: o[11] = torso.Mul(M34.TRS(new Vector3(0.02f, 0.28f * S, -0.2f * S), 0f, Mathf.PI * 0.5f, 0.95f)); break; // slung diagonally on the back
            }
        }

        // ================= animation =================
        public enum Anim { Idle, Walk, Guard, Aim, Search, Chop, Wave, Shamble, Attack, Dead }

        /// <summary>Procedural pose for an animation at time t (s); phase = walk-cycle angle (advanced by distance).</summary>
        public static Pose Animate(CharModel m, Anim a, float t, float phase, int salt)
        {
            var p = new Pose();
            float s = Mathf.Sin(phase), c = Mathf.Cos(phase);
            float j = salt * 0.37f;
            switch (a)
            {
                case Anim.Walk:
                    p.ThighLP = s * 0.5f; p.ThighRP = -s * 0.5f;
                    p.KneeL = Mathf.Max(0f, -c) * 0.85f + 0.08f; p.KneeR = Mathf.Max(0f, c) * 0.85f + 0.08f;
                    p.ArmLP = -s * 0.42f; p.ArmRP = s * 0.42f; p.ElbowL = -0.35f - Mathf.Max(0f, s) * 0.3f; p.ElbowR = -0.35f - Mathf.Max(0f, -s) * 0.3f;
                    p.ArmLR = -0.06f; p.ArmRR = 0.06f;
                    p.HipY = m.LegLen - Mathf.Abs(c) * 0.035f + 0.005f; p.HipYaw = s * 0.08f; p.SpineYaw = -s * 0.12f;
                    p.SpinePitch = 0.07f; p.HeadPitch = -0.05f;
                    if (m.Rifle) { p.RifleMode = 0; }
                    break;
                case Anim.Guard:
                    p.HipY = m.LegLen; p.ThighLR = -0.08f; p.ThighRR = 0.08f; p.KneeL = p.KneeR = 0.06f;
                    p.SpineYaw = Mathf.Sin(t * 0.4f + j) * 0.35f; p.HeadYaw = Mathf.Sin(t * 0.55f + j) * 0.3f;
                    p.SpinePitch = 0.02f * Mathf.Sin(t * 1.4f + j);
                    if (m.Rifle) { p.RifleMode = 1; p.ArmRP = -0.3f; p.ArmRR = 0.12f; p.ElbowR = -1.25f; p.ArmLP = -0.75f; p.ArmLR = 0.42f; p.ElbowL = -0.85f; }
                    else { p.ArmLR = -0.1f; p.ArmRR = 0.1f; p.ElbowL = p.ElbowR = -0.15f; }
                    break;
                case Anim.Aim:
                    p.HipY = m.LegLen - 0.02f; p.ThighLP = -0.18f; p.ThighRP = 0.15f; p.KneeL = 0.2f; p.KneeR = 0.1f;
                    p.SpinePitch = -0.02f; p.HeadPitch = 0.08f; p.HeadRoll = 0.1f;
                    p.RifleMode = 2; p.ArmRP = -1.15f; p.ArmRR = 0.35f; p.ElbowR = -1.05f; p.ArmLP = -1.42f; p.ArmLR = 0.48f; p.ElbowL = -0.3f;
                    if (!m.Rifle) { p.RifleMode = 0; p.ArmRP = -1.5f; p.ArmRR = 0.1f; p.ElbowR = -0.05f; p.ArmLP = -1.4f; p.ArmLR = 0.35f; p.ElbowL = -0.15f; } // pistol grip
                    break;
                case Anim.Search:
                {
                    // crouched, rummaging with alternating hands
                    p.HipY = m.LegLen * 0.62f; p.ThighLP = -1.15f; p.ThighRP = -0.7f; p.KneeL = 1.95f; p.KneeR = 1.45f;
                    p.ThighLR = -0.12f; p.ThighRR = 0.12f;
                    p.SpinePitch = 0.55f; p.HeadPitch = 0.25f;
                    float r1 = Mathf.Sin(t * 4.2f + j), r2 = Mathf.Sin(t * 4.2f + j + 2.1f);
                    p.ArmLP = -0.85f + r1 * 0.3f; p.ElbowL = -0.4f - Mathf.Max(0f, r1) * 0.5f; p.ArmRP = -0.85f + r2 * 0.3f; p.ElbowR = -0.4f - Mathf.Max(0f, r2) * 0.5f;
                    p.ArmLR = 0.1f; p.ArmRR = -0.1f;
                    break;
                }
                case Anim.Chop:
                {
                    // hammering / chopping in the yard: right arm swings over the head and down
                    float cyc = (t * 1.6f + j) % 1f;
                    float sw = cyc < 0.6f ? cyc / 0.6f : 1f - (cyc - 0.6f) / 0.4f;   // raise slowly, strike fast
                    p.HipY = m.LegLen - 0.04f; p.ThighLP = -0.25f; p.ThighRP = 0.2f; p.KneeL = 0.3f; p.KneeR = 0.15f;
                    p.SpinePitch = 0.25f + (1f - sw) * 0.2f;
                    p.ArmRP = -0.6f - sw * 2.2f; p.ElbowR = -0.3f - sw * 0.6f; p.ArmRR = 0.1f;
                    p.ArmLP = -0.7f; p.ElbowL = -0.6f; p.ArmLR = 0.2f; p.HeadPitch = 0.2f;
                    break;
                }
                case Anim.Wave:
                {
                    p.HipY = m.LegLen; p.KneeL = p.KneeR = 0.05f;
                    float w = Mathf.Sin(t * 7f + j);
                    p.ArmRP = -0.2f; p.ArmRR = 2.55f + w * 0.35f; p.ElbowR = -0.25f;
                    p.ArmLR = -0.1f; p.ElbowL = -0.1f; p.HeadPitch = -0.15f;
                    p.SpineRoll = -0.06f;
                    break;
                }
                case Anim.Shamble:
                {
                    // stiff short steps, one foot drags, lurching side to side, arms reaching forward
                    p.ThighLP = s * 0.3f; p.ThighRP = -s * 0.22f;
                    p.KneeL = Mathf.Max(0f, -c) * 0.45f + 0.12f; p.KneeR = 0.15f;
                    p.ThighRR = 0.06f;
                    p.HipY = m.LegLen - 0.03f - Mathf.Abs(c) * 0.04f; p.HipRoll = s * 0.1f; p.SpineRoll = -s * 0.12f;
                    p.SpinePitch = 0.08f + Mathf.Abs(s) * 0.05f; p.HeadPitch = -0.2f;
                    float sw = Mathf.Sin(t * 1.3f + j) * 0.12f;
                    p.ArmLP = -1.3f + sw; p.ElbowL = -0.25f; p.ArmLR = 0.12f;
                    if (m.OneArmDown) { p.ArmRP = 0.15f + s * 0.1f; p.ElbowR = -0.1f; p.ArmRR = 0.12f; }
                    else { p.ArmRP = -1.2f - sw; p.ElbowR = -0.3f; p.ArmRR = -0.12f; }
                    break;
                }
                case Anim.Attack:
                {
                    // pounding / clawing at the palisade
                    float k = t * 6.5f + j;
                    p.HipY = m.LegLen - 0.05f; p.ThighLP = -0.3f; p.ThighRP = 0.25f; p.KneeL = 0.35f; p.KneeR = 0.2f;
                    p.SpinePitch = 0.2f + Mathf.Max(0f, Mathf.Sin(k)) * 0.15f; p.HeadPitch = -0.35f;
                    p.ArmLP = -2.0f + Mathf.Sin(k) * 0.9f; p.ElbowL = -0.5f - Mathf.Max(0f, Mathf.Sin(k)) * 0.6f; p.ArmLR = 0.15f;
                    p.ArmRP = -2.0f + Mathf.Sin(k + Mathf.PI) * 0.9f; p.ElbowR = -0.5f - Mathf.Max(0f, -Mathf.Sin(k)) * 0.6f; p.ArmRR = -0.15f;
                    break;
                }
                case Anim.Dead:
                    p.HipY = m.LegLen; p.ArmLR = -1.2f; p.ArmRR = 1.0f; p.ArmLP = -0.4f; p.ThighLR = -0.15f; p.ThighRR = 0.2f; p.KneeL = 0.3f; p.HeadRoll = 0.4f;
                    break;
                default: // Idle
                    p.HipY = m.LegLen; p.ThighLR = -0.05f; p.ThighRR = 0.05f; p.KneeL = p.KneeR = 0.04f;
                    p.SpinePitch = 0.025f * Mathf.Sin(t * 1.5f + j); p.HeadYaw = Mathf.Sin(t * 0.3f + j) * 0.45f;
                    p.ArmLR = -0.08f; p.ArmRR = 0.08f; p.ElbowL = -0.18f - 0.05f * Mathf.Sin(t * 1.5f + j); p.ElbowR = -0.2f;
                    if (m.Zombie) { p.ArmLP = -0.9f; p.ArmRP = m.OneArmDown ? 0.1f : -0.8f; p.HeadPitch = -0.1f; p.SpineRoll = Mathf.Sin(t * 0.8f + j) * 0.08f; }
                    break;
            }
            return p;
        }
    }
}
