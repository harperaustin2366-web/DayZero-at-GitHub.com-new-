// v0.14.0a HUD skin: every IMGUI panel, button, chip and glow texture is drawn here in code (no image assets).
// Rounded rectangles from a signed-distance function with 1-px anti-aliasing, a vertical gradient, a thin
// keyline, an inner top highlight / bottom shade, an optional inner glow, and a soft drop shadow drawn into
// padding that GUIStyle.overflow pushes outside the control. Textures are 9-sliced by GUIStyle.border, built once
// and cached (nothing is allocated per frame). The pixel code is pure C# so the offline preview composites the
// exact same pixels; only the bottom block touches Texture2D / GUIStyle.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace DayZero.Runtime
{
    public static class UiSkin
    {
        /// <summary>One generated 9-slice sheet. Px row 0 = top. Pad* = shadow padding (GUIStyle.overflow),
        /// Slice = border inset from every texture edge (GUIStyle.border).</summary>
        public sealed class Sheet
        {
            public Color32[] Px;
            public int W, H, PadL, PadT, PadR, PadB, Slice;
        }

        /// <summary>What a rounded rect looks like.</summary>
        public struct Look
        {
            public float Radius;
            public Color Top, Bottom;          // fill gradient (with alpha)
            public Color Rim; public float RimW; // keyline
            public float Hi, Lo;               // inner top highlight / bottom shade strength
            public Color Glow; public float GlowA, GlowW;   // inner glow along the edge
            public float ShadowA, ShadowDy, ShadowBlur;
            public float TopShade;             // pressed: inner shadow along the top edge
            public float PadBlur, PadDy;       // >0: padding laid out for this shadow instead (states of one style share it)
        }

        // ---- palette (art bible: UI panel #282822, text #d1c6ad / #edeace, BUILD #7d7744, CAMP #bd882f, GATHER #92602f) ----
        public static readonly Color PanelTop = new Color(0.135f, 0.14f, 0.11f, 0.88f);
        public static readonly Color PanelBot = new Color(0.07f, 0.075f, 0.06f, 0.86f);
        public static readonly Color Keyline = new Color(0.50f, 0.47f, 0.34f, 0.50f);
        public static readonly Color AmberGlow = new Color(1f, 0.74f, 0.32f, 1f);

        static float Cov(float d) => d <= -0.5f ? 1f : d >= 0.5f ? 0f : 0.5f - d;
        static float Smooth(float t) { t = Mathf.Clamp01(t); return t * t * (3f - 2f * t); }

        static float RRect(float x, float y, float w, float h, float r)
        {
            // distance to a rounded rect spanning [0,w]x[0,h]
            float cx = x - w * 0.5f, cy = y - h * 0.5f;
            float dx = Mathf.Abs(cx) - w * 0.5f + r, dy = Mathf.Abs(cy) - h * 0.5f + r;
            float ox = Mathf.Max(dx, 0f), oy = Mathf.Max(dy, 0f);
            return Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(dx, dy), 0f) - r;
        }

        /// <summary>Rasterise a rounded rect of inner size iw x ih with shadow padding around it.</summary>
        public static Sheet Render(Look k, int iw, int ih)
        {
            float pb = k.PadBlur > 0f ? k.PadBlur : k.ShadowBlur, pd = k.PadBlur > 0f ? k.PadDy : k.ShadowDy;
            bool pad = k.ShadowA > 0f || k.PadBlur > 0f;
            int padX = pad ? Mathf.CeilToInt(pb) + 1 : 0;
            int padT = pad ? Mathf.Max(0, Mathf.CeilToInt(pb - pd)) + 1 : 0;
            int padB = pad ? Mathf.CeilToInt(pb + pd) + 1 : 0;
            int W = iw + padX * 2, H = ih + padT + padB;
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float fx = x + 0.5f - padX, fy = y + 0.5f - padT;
                    float d = RRect(fx, fy, iw, ih, k.Radius);
                    float aIn = Cov(d);
                    float t = Mathf.Clamp01(fy / ih);
                    float e = -d;   // depth inside the edge
                    Color c = Color.Lerp(k.Top, k.Bottom, t);
                    if (k.GlowA > 0f)
                    {
                        float g = k.GlowA * Mathf.Clamp01(1f - (e - k.RimW) / Mathf.Max(0.5f, k.GlowW));
                        c = new Color(Mathf.Lerp(c.r, k.Glow.r, g), Mathf.Lerp(c.g, k.Glow.g, g), Mathf.Lerp(c.b, k.Glow.b, g), Mathf.Max(c.a, c.a + g * (1f - c.a)));
                    }
                    // one-pixel bands just inside the keyline
                    float band = Mathf.Clamp01(1f - Mathf.Abs(e - (k.RimW + 1.0f)) / 1.2f);
                    if (k.Hi > 0f) { float hi = k.Hi * band * Mathf.Pow(1f - t, 2f); c = new Color(Mathf.Lerp(c.r, 1f, hi), Mathf.Lerp(c.g, 0.97f, hi), Mathf.Lerp(c.b, 0.88f, hi), c.a); }
                    if (k.Lo > 0f) { float lo = k.Lo * band * t * t; c = new Color(c.r * (1f - lo), c.g * (1f - lo), c.b * (1f - lo), c.a); }
                    if (k.TopShade > 0f)
                    {
                        float ts = k.TopShade * Mathf.Clamp01(1f - fy / Mathf.Max(1f, ih * 0.35f));
                        c = new Color(c.r * (1f - ts), c.g * (1f - ts), c.b * (1f - ts), c.a);
                    }
                    if (k.RimW > 0f)
                    {
                        float rim = Mathf.Clamp01(k.RimW - e + 0.5f) * k.Rim.a;
                        c = new Color(Mathf.Lerp(c.r, k.Rim.r, rim), Mathf.Lerp(c.g, k.Rim.g, rim), Mathf.Lerp(c.b, k.Rim.b, rim), Mathf.Max(c.a, rim));
                    }
                    float aC = c.a * aIn;
                    float aS = 0f;
                    if (k.ShadowA > 0f)
                    {
                        float ds = RRect(fx, fy - k.ShadowDy, iw, ih, k.Radius) + 1f;
                        float s = 1f - Smooth((ds + 1f) / Mathf.Max(1f, k.ShadowBlur));
                        aS = k.ShadowA * s * s * (1f - aIn);
                    }
                    float a = aC + aS * (1f - aC);
                    if (a <= 0.0005f) { px[y * W + x] = new Color32(0, 0, 0, 0); continue; }
                    float m = aC / a;   // straight alpha: colour of the fill, darkened toward the shadow's black
                    px[y * W + x] = new Color(c.r * m, c.g * m, c.b * m, a);
                }
            int slice = Mathf.CeilToInt(k.Radius) + 5;
            return new Sheet { Px = px, W = W, H = H, PadL = padX, PadR = padX, PadT = padT, PadB = padB, Slice = slice };
        }

        static Color Mul(Color c, float k) => new Color(Mathf.Clamp01(c.r * k), Mathf.Clamp01(c.g * k), Mathf.Clamp01(c.b * k), c.a);
        static Color Tint(Color c, Color to, float k) => new Color(Mathf.Lerp(c.r, to.r, k), Mathf.Lerp(c.g, to.g, k), Mathf.Lerp(c.b, to.b, k), c.a);

        // inner sizes: tall enough that the gradient lives in the stretched middle slice, not only in the corners
        const int IW = 40, IH = 112;

        /// <summary>Dark translucent sheet behind every panel, sheet and popup.</summary>
        public static Sheet Panel() => Render(new Look
        {
            Radius = 10f, Top = PanelTop, Bottom = PanelBot, Rim = Keyline, RimW = 1f, Hi = 0.16f, Lo = 0.25f,
            Glow = AmberGlow, GlowA = 0.035f, GlowW = 6f, ShadowA = 0.42f, ShadowDy = 3f, ShadowBlur = 8f,
        }, IW, IH);

        /// <summary>Top-bar stats panel: a touch more opaque, amber keyline, deeper shadow.</summary>
        public static Sheet HudPanel() => Render(new Look
        {
            Radius = 12f, Top = new Color(0.15f, 0.155f, 0.12f, 0.90f), Bottom = new Color(0.065f, 0.07f, 0.055f, 0.88f),
            Rim = new Color(0.78f, 0.58f, 0.26f, 0.55f), RimW = 1f, Hi = 0.20f, Lo = 0.25f,
            Glow = AmberGlow, GlowA = 0.06f, GlowW = 7f, ShadowA = 0.50f, ShadowDy = 4f, ShadowBlur = 10f,
        }, IW, IH);

        /// <summary>Resource / info chip.</summary>
        public static Sheet Chip() => Render(new Look
        {
            Radius = 9f, Top = new Color(0.13f, 0.135f, 0.105f, 0.90f), Bottom = new Color(0.06f, 0.065f, 0.05f, 0.88f),
            Rim = new Color(0.52f, 0.48f, 0.34f, 0.45f), RimW = 1f, Hi = 0.18f, Lo = 0.2f, ShadowA = 0.38f, ShadowDy = 2f, ShadowBlur = 5f,
        }, IW, 32);

        /// <summary>Neutral light chip, tinted at draw time with GUI.color (top-bar clock: day / dusk / night).</summary>
        public static Sheet TintChip() => Render(new Look
        {
            Radius = 8f, Top = new Color(1f, 1f, 1f, 0.55f), Bottom = new Color(0.62f, 0.62f, 0.62f, 0.40f),
            Rim = new Color(1f, 1f, 1f, 0.55f), RimW = 1f, Hi = 0.25f, Lo = 0.15f,
        }, IW, 28);

        /// <summary>Button states. 0 normal, 1 hover, 2 pressed (darker, inverted gradient, amber inner glow, flatter
        /// shadow), 3 selected / on (amber glow rim).</summary>
        public static Sheet Button(Color b, int state)
        {
            float a = b.a;
            var dark = new Color(b.r * 0.30f, b.g * 0.30f, b.b * 0.28f, Mathf.Min(1f, a + 0.05f));
            switch (state)
            {
                case 2:
                    return Render(new Look
                    {
                        Radius = 8f, Top = Mul(b, 0.70f), Bottom = Mul(b, 0.92f), Rim = Tint(dark, AmberGlow, 0.55f), RimW = 1.2f,
                        Lo = 0f, Hi = 0f, TopShade = 0.28f, Glow = AmberGlow, GlowA = 0.30f, GlowW = 4f,
                        ShadowA = 0.25f, ShadowDy = 1f, ShadowBlur = 3f, PadBlur = 5f, PadDy = 2f,
                    }, IW, 96);
                case 3:
                    return Render(new Look
                    {
                        Radius = 8f, Top = Tint(Mul(b, 1.12f), new Color(1f, 0.95f, 0.8f), 0.06f), Bottom = Mul(b, 0.80f), Rim = new Color(1f, 0.80f, 0.40f, 0.95f), RimW = 1.5f,
                        Hi = 0.32f, Lo = 0.28f, Glow = AmberGlow, GlowA = 0.28f, GlowW = 5f, ShadowA = 0.45f, ShadowDy = 2f, ShadowBlur = 5f,
                    }, IW, 96);
                default:
                    float lift = state == 1 ? 1.08f : 1f;
                    return Render(new Look
                    {
                        Radius = 8f, Top = Tint(Mul(b, 1.14f * lift), new Color(1f, 0.95f, 0.8f), 0.05f), Bottom = Mul(b, 0.80f * lift), Rim = dark, RimW = 1f,
                        Hi = state == 1 ? 0.36f : 0.28f, Lo = 0.30f, Glow = AmberGlow, GlowA = state == 1 ? 0.10f : 0f, GlowW = 4f,
                        ShadowA = 0.42f, ShadowDy = 2f, ShadowBlur = 5f,
                    }, IW, 96);
            }
        }

        /// <summary>Soft round glow (white, alpha falloff), tinted with GUI.color: alert pulse, pressed halo.</summary>
        public static Sheet Glow(int size = 64)
        {
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size * 2f - 1f, v = (y + 0.5f) / size * 2f - 1f;
                    float r = Mathf.Sqrt(u * u + v * v);
                    float a = Mathf.Clamp01(1f - r); a = a * a;
                    px[y * size + x] = new Color(1f, 1f, 1f, a);
                }
            return new Sheet { Px = px, W = size, H = size };
        }

        /// <summary>Horizontal scrim: transparent at the left, dark at the right, soft top and bottom (behind the
        /// right-aligned kill / rescue counters).</summary>
        public static Sheet ScrimRight(int w = 64, int h = 32)
        {
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float ax = Smooth((x + 0.5f) / w * 1.6f);
                    float vy = (y + 0.5f) / h * 2f - 1f;
                    float ay = 1f - Smooth((Mathf.Abs(vy) - 0.55f) / 0.45f);
                    px[y * w + x] = new Color(0.04f, 0.045f, 0.035f, 0.62f * ax * ay);
                }
            return new Sheet { Px = px, W = w, H = h };
        }

        /// <summary>Thin glowing accent bar (selected tab underline), soft at both ends.</summary>
        public static Sheet AccentBar(int w = 64, int h = 8)
        {
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float vx = Mathf.Abs((x + 0.5f) / w * 2f - 1f), vy = Mathf.Abs((y + 0.5f) / h * 2f - 1f);
                    float core = Mathf.Clamp01((0.45f - vy) * 4f) * (1f - Smooth((vx - 0.8f) / 0.2f));
                    float halo = (1f - vy) * (1f - vx * vx) * 0.45f;
                    float a = Mathf.Max(core, halo);
                    px[y * w + x] = Color.Lerp(new Color(1f, 0.70f, 0.25f, a), new Color(1f, 0.93f, 0.7f, a), core);
                }
            return new Sheet { Px = px, W = w, H = h };
        }

        /// <summary>Title / loading backdrop: dark olive at the top to near-black, with a soft vignette.</summary>
        public static Sheet Backdrop(int w = 96, int h = 64)
        {
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float u = (x + 0.5f) / w * 2f - 1f, v = (y + 0.5f) / h * 2f - 1f;
                    var c = Color.Lerp(new Color(0.15f, 0.16f, 0.12f, 1f), new Color(0.05f, 0.055f, 0.045f, 1f), (v + 1f) * 0.5f);
                    float vig = 1f - 0.45f * Smooth((Mathf.Sqrt(u * u * 0.8f + v * v) - 0.45f) / 0.9f);
                    px[y * w + x] = new Color(c.r * vig, c.g * vig, c.b * vig, 1f);
                }
            return new Sheet { Px = px, W = w, H = h };
        }

        /// <summary>Day / dusk / night colour for the clock chip, from the hour of day (0..24).</summary>
        public static Color SkyTint(float hour)
        {
            var night = new Color(0.30f, 0.40f, 0.72f, 0.55f);
            var dawn = new Color(0.92f, 0.52f, 0.34f, 0.60f);
            var day = new Color(0.86f, 0.70f, 0.36f, 0.50f);
            var dusk = new Color(0.90f, 0.40f, 0.26f, 0.62f);
            float h = ((hour % 24f) + 24f) % 24f;
            if (h < 4.5f) return night;
            if (h < 6f) return Color.Lerp(night, dawn, (h - 4.5f) / 1.5f);
            if (h < 8f) return Color.Lerp(dawn, day, (h - 6f) / 2f);
            if (h < 18f) return day;
            if (h < 20f) return Color.Lerp(day, dusk, (h - 18f) / 2f);
            if (h < 21.5f) return Color.Lerp(dusk, night, (h - 20f) / 1.5f);
            return night;
        }

        // =====================================================================================================
        // v0.17 UI kit skin (UiKit / UiWheel). Rugged military-survival plates: dark #1e1d18 base, chamfered
        // corners, 2 px bevel, inner shadow, corner rivets, tiled grain (KitGrain, drawn over plates so the 9-slice
        // never stretches the noise). Buttons have a raised face over a darker lip; pressed sinks into the socket.
        // Everything below is pure pixel code (the offline preview composites the same pixels).
        // =====================================================================================================

        public static readonly Color KitBase = new Color(0.118f, 0.114f, 0.094f, 0.95f);   // #1e1d18
        public static readonly Color KitOlive = new Color(0.43f, 0.42f, 0.23f, 1f);        // primary (art bible BUILD #7d7744, a touch darker)
        public static readonly Color KitAmber = new Color(0.74f, 0.53f, 0.18f, 1f);        // accent  (CAMP #bd882f)
        public static readonly Color KitRust = new Color(0.58f, 0.25f, 0.16f, 1f);         // danger
        public static readonly Color KitSteel = new Color(0.25f, 0.25f, 0.21f, 1f);        // neutral
        public static readonly Color KitInk = new Color(0.93f, 0.92f, 0.81f, 1f);          // #edeace
        public static readonly Color KitInkDim = new Color(0.82f, 0.78f, 0.68f, 1f);       // #d1c6ad
        public static readonly Color KitInkMute = new Color(0.60f, 0.58f, 0.50f, 1f);
        public static readonly Color KitGood = new Color(0.48f, 0.60f, 0.29f, 1f);         // #7a9a4a
        public static readonly Color KitBad = new Color(0.69f, 0.29f, 0.23f, 1f);          // #b04a3a

        static uint Hash(int x, int y, int s)
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + s * 2147483647);
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }
        static float Hash01(int x, int y, int s) => (Hash(x, y, s) & 0xffffff) / 16777216f;

        /// <summary>Distance to a rect [0,w]x[0,h] with 45-degree chamfered corners of size c (negative inside).</summary>
        static float Chamfer(float x, float y, float w, float h, float c)
        {
            float px = Mathf.Abs(x - w * 0.5f), py = Mathf.Abs(y - h * 0.5f);
            float db = Mathf.Max(px - w * 0.5f, py - h * 0.5f);
            float dc = (px + py - (w * 0.5f + h * 0.5f - c)) * 0.70710678f;
            return Mathf.Max(db, dc);
        }

        static Color Over(Color c, Color o, float a) => new Color(Mathf.Lerp(c.r, o.r, a), Mathf.Lerp(c.g, o.g, a), Mathf.Lerp(c.b, o.b, a), c.a);

        /// <summary>Straight-alpha pixel = fill (coverage aIn) over a soft drop shadow.</summary>
        static Color32 WithShadow(Color c, float aIn, float shadowA, float ds, float blur)
        {
            float aC = c.a * aIn;
            float aS = 0f;
            if (shadowA > 0f) { float s = 1f - Smooth((ds + 1f) / Mathf.Max(1f, blur)); aS = shadowA * s * s * (1f - aIn); }
            float a = aC + aS * (1f - aC);
            if (a <= 0.0005f) return new Color32(0, 0, 0, 0);
            float m = aC / a;
            return new Color(c.r * m, c.g * m, c.b * m, a);
        }

        /// <summary>Which bevel side a point at (x,y) inside a w x h rect is nearest: +1 lit (top/left), -1 shade.</summary>
        static float LitSide(float x, float y, float w, float h)
        {
            float t = y, b = h - y, l = x, r = w - x;
            float m = Mathf.Min(Mathf.Min(t, b), Mathf.Min(l, r));
            if (m == t) return 1f; if (m == l) return 0.55f; if (m == r) return -0.55f; return -1f;
        }

        /// <summary>Main panel plate: chamfer 6, 2 px bevel, inner shadow ring, four rivets, soft drop shadow.</summary>
        public static Sheet KitPlate()
        {
            const int iw = 72, ih = 72, pad = 10; const float ch = 6f, dy = 4f, blur = 9f;
            int W = iw + pad * 2, H = ih + pad * 2;
            var px = new Color32[W * H];
            float[] rx = { 9.5f, iw - 9.5f, 9.5f, iw - 9.5f }, ry = { 9.5f, 9.5f, ih - 9.5f, ih - 9.5f };
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float fx = x + 0.5f - pad, fy = y + 0.5f - pad;
                    float d = Chamfer(fx, fy, iw, ih, ch), aIn = Cov(d), e = -d;
                    float t = Mathf.Clamp01(fy / ih);
                    Color c = Color.Lerp(Mul(KitBase, 1.12f), Mul(KitBase, 0.86f), t); c.a = KitBase.a;
                    // inner shadow ring just inside the bevel
                    if (e > 3f) { float k = Mathf.Clamp01(1f - (e - 3f) / 10f); c = Mul(c, 1f - 0.30f * k * k); c.a = KitBase.a; }
                    // 2 px bevel: lit top/left, shaded bottom/right
                    if (e >= 1f && e < 3f)
                    {
                        float side = LitSide(fx, fy, iw, ih);
                        c = side > 0 ? Over(c, new Color(0.62f, 0.58f, 0.44f), 0.30f * side) : Mul(c, 1f + 0.55f * side);
                        c.a = 0.97f;
                    }
                    if (e < 1f) c = new Color(0.035f, 0.033f, 0.027f, 0.96f);   // outer keyline
                    // rivets
                    for (int i = 0; i < 4; i++)
                    {
                        float vx = fx - rx[i], vy = fy - ry[i], r = Mathf.Sqrt(vx * vx + vy * vy);
                        if (r > 3.9f) continue;
                        float cov = Mathf.Clamp01(3.6f - r);
                        float lit = Mathf.Clamp01(0.5f - (vx + vy) / 5f);
                        var rc = r > 2.6f ? new Color(0.03f, 0.03f, 0.025f)
                            : Color.Lerp(new Color(0.24f, 0.22f, 0.17f), new Color(0.78f, 0.73f, 0.58f), lit * lit);
                        if (r < 1.0f && vx < 0f && vy < 0f) rc = new Color(0.92f, 0.88f, 0.74f);
                        c = Over(c, rc, cov); c.a = Mathf.Max(c.a, 0.97f * cov + c.a * (1f - cov));
                    }
                    float ds = Chamfer(fx, fy - dy, iw, ih, ch) + 1f;
                    px[y * W + x] = WithShadow(c, aIn, 0.55f, ds, blur);
                }
            return new Sheet { Px = px, W = W, H = H, PadL = pad, PadR = pad, PadT = pad, PadB = pad, Slice = 15 };
        }

        /// <summary>Tileable grain (64 x 64): light and dark specks + soft blotches, low alpha. Tile it over plates.</summary>
        public static Sheet KitGrain(int n = 64)
        {
            var px = new Color32[n * n];
            const int cell = 8;
            int L = n / cell;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    // tileable value noise (lattice wraps every L cells)
                    float gx = (float)x / cell, gy = (float)y / cell;
                    int x0 = (int)gx, y0 = (int)gy; float u = Smooth(gx - x0), v = Smooth(gy - y0);
                    float a00 = Hash01(x0 % L, y0 % L, 7), a10 = Hash01((x0 + 1) % L, y0 % L, 7), a01 = Hash01(x0 % L, (y0 + 1) % L, 7), a11 = Hash01((x0 + 1) % L, (y0 + 1) % L, 7);
                    float blot = Mathf.Lerp(Mathf.Lerp(a00, a10, u), Mathf.Lerp(a01, a11, u), v);
                    float fine = Hash01(x, y, 3);
                    float val = blot * 0.55f + fine * 0.45f - 0.5f;   // -0.5..0.5
                    var c = val > 0 ? new Color(0.95f, 0.90f, 0.75f, val * 0.10f) : new Color(0f, 0f, 0f, -val * 0.15f);
                    // occasional scratch / speck
                    if (Hash01(x, y, 11) > 0.992f) c = new Color(0.9f, 0.86f, 0.7f, 0.10f);
                    px[y * n + x] = c;
                }
            return new Sheet { Px = px, W = n, H = n };
        }

        /// <summary>Header strip: dark olive-black, top highlight, amber stencil notch at the left, 2 px amber rule below.</summary>
        public static Sheet KitHeader()
        {
            const int W = 48, H = 40;
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float t = (y + 0.5f) / H;
                    Color c = Color.Lerp(new Color(0.20f, 0.195f, 0.15f, 0.96f), new Color(0.125f, 0.12f, 0.095f, 0.96f), t);
                    if (y == 0) c = Over(c, new Color(0.8f, 0.76f, 0.6f), 0.14f);
                    if (y >= H - 2) c = new Color(0.78f, 0.56f, 0.20f, 1f);
                    else if (y == H - 3) c = new Color(0.05f, 0.05f, 0.04f, 0.96f);
                    if (x < 5 && y > 4 && y < H - 6) c = new Color(0.78f, 0.56f, 0.20f, 1f);
                    px[y * W + x] = c;
                }
            return new Sheet { Px = px, W = W, H = H, Slice = 8 };
        }

        public const int KitLip = 5, KitPressDrop = 3, KitBtnPad = 4;

        /// <summary>Kit button sheet. state 0 normal, 1 pressed (face sunk into its socket), 2 selected (amber frame),
        /// 3 disabled. The face is raised over a darker lip (KitLip px) at the bottom; label rect = UiKit.ButtonFace.</summary>
        public static Sheet KitButton(Color b, int state)
        {
            const int iw = 48, ih = 64, pad = KitBtnPad; const float ch = 4f, blur = 4f;
            int W = iw + pad * 2, H = ih + pad * 2;
            var px = new Color32[W * H];
            bool dis = state == 3, down = state == 1, sel = state == 2;
            if (dis) b = new Color(0.30f, 0.30f, 0.27f, 0.80f);
            float faceTop = down ? KitPressDrop : 0f, faceBot = ih - (down ? KitLip - KitPressDrop + 1 : KitLip);
            var lip = Mul(b, 0.42f); lip.a = Mathf.Max(b.a, 0.92f);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float fx = x + 0.5f - pad, fy = y + 0.5f - pad;
                    float d = Chamfer(fx, fy, iw, ih, ch), aIn = Cov(d), e = -d;
                    Color c;
                    if (fy < faceTop) c = new Color(0.05f, 0.05f, 0.04f, 0.95f);           // socket shows above a pressed face
                    else if (fy >= faceBot) { c = lip; if (fy < faceBot + 1f) c = Mul(lip, 0.6f); }
                    else
                    {
                        float t = Mathf.Clamp01((fy - faceTop) / Mathf.Max(1f, faceBot - faceTop));
                        c = Color.Lerp(Tint(Mul(b, down ? 0.95f : 1.18f), new Color(1f, 0.96f, 0.82f), 0.04f), Mul(b, down ? 0.80f : 0.86f), t);
                        c.a = Mathf.Max(b.a, 0.9f);
                        float de = fy - faceTop;   // depth below the face top
                        if (!down && !dis && de < 2f && e > 1f) c = Over(c, new Color(1f, 0.97f, 0.85f), 0.34f * (2f - de) / 2f);   // top highlight
                        if (!down && !dis && e > 1f && e < 2f && fx < iw * 0.5f) c = Over(c, new Color(1f, 0.97f, 0.85f), 0.10f);
                        if (down && de < 4f) c = Mul(c, 0.78f + 0.055f * de);   // inner shade under the socket rim
                        // faint brushed texture
                        c = Mul(c, 0.985f + 0.03f * Hash01(x, (int)(y / 3), 5));
                    }
                    if (e < 1f) c = new Color(0.03f, 0.03f, 0.025f, 0.95f);
                    if (sel)
                    {
                        if (e >= 1f && e < 3f) c = new Color(0.95f, 0.70f, 0.26f, 1f);
                        else if (e >= 3f && e < 6f) c = Over(c, new Color(1f, 0.75f, 0.3f), 0.22f * (6f - e) / 3f);
                    }
                    float ds = Chamfer(fx, fy - (down ? 0.5f : 2f), iw, ih, ch) + 1f;
                    px[y * W + x] = WithShadow(c, aIn, dis ? 0.2f : 0.5f, ds, blur);
                }
            return new Sheet { Px = px, W = W, H = H, PadL = pad, PadR = pad, PadT = pad, PadB = pad, Slice = 9 };
        }

        /// <summary>Chip / badge: small dark plate, keyline, coloured tab on the left (tinted at draw time, so pass white).</summary>
        public static Sheet KitChip(Color tab)
        {
            const int W = 40, H = 28; const float ch = 3f;
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float fx = x + 0.5f, fy = y + 0.5f;
                    float d = Chamfer(fx, fy, W, H, ch), aIn = Cov(d), e = -d;
                    Color c = Color.Lerp(new Color(0.15f, 0.145f, 0.12f, 0.92f), new Color(0.085f, 0.08f, 0.065f, 0.92f), fy / H);
                    if (e < 1f) c = new Color(0.45f, 0.41f, 0.30f, 0.75f);
                    else if (fx < 5f) c = new Color(tab.r, tab.g, tab.b, 1f);
                    px[y * W + x] = WithShadow(c, aIn, 0f, 0f, 1f);
                }
            return new Sheet { Px = px, W = W, H = H, Slice = 6 };
        }

        /// <summary>Recessed well (behind scroll wheels and bars): darker, inverted bevel (shade on top, light lip below).</summary>
        public static Sheet KitWell()
        {
            const int W = 40, H = 40; const float ch = 3f;
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float fx = x + 0.5f, fy = y + 0.5f;
                    float d = Chamfer(fx, fy, W, H, ch), aIn = Cov(d), e = -d;
                    Color c = new Color(0.055f, 0.053f, 0.045f, 0.94f);
                    if (e < 1f) c = new Color(0.02f, 0.02f, 0.016f, 0.96f);
                    else if (e < 3f)
                    {
                        float side = LitSide(fx, fy, W, H);
                        c = side > 0 ? new Color(0.025f, 0.024f, 0.02f, 0.96f) : new Color(0.30f, 0.28f, 0.22f, 0.85f);
                    }
                    px[y * W + x] = WithShadow(c, aIn, 0f, 0f, 1f);
                }
            return new Sheet { Px = px, W = W, H = H, Slice = 6 };
        }

        /// <summary>Selection band across the middle of a wheel: faint amber fill, amber rules on its long sides.
        /// horizontal=false: rules top and bottom (vertical drum).</summary>
        public static Sheet KitBand(bool horizontal)
        {
            int W = horizontal ? 32 : 48, H = horizontal ? 48 : 32;
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int along = horizontal ? x : y, len = horizontal ? W : H;
                    Color c = new Color(0.95f, 0.72f, 0.30f, 0.13f);
                    if (along < 2 || along >= len - 2) c = new Color(0.92f, 0.66f, 0.24f, 0.95f);
                    else if (along < 4 || along >= len - 4) c = new Color(0.95f, 0.72f, 0.30f, 0.22f);
                    px[y * W + x] = c;
                }
            return new Sheet { Px = px, W = W, H = H, Slice = 6 };
        }

        /// <summary>Drum curvature shade: opaque dark at both ends of the wheel axis, clear in the middle.</summary>
        public static Sheet KitDrumShade(bool horizontal)
        {
            int W = horizontal ? 64 : 4, H = horizontal ? 4 : 64;
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float v = horizontal ? (x + 0.5f) / W * 2f - 1f : (y + 0.5f) / H * 2f - 1f;
                    float a = Mathf.Pow(Mathf.Clamp01((Mathf.Abs(v) - 0.25f) / 0.75f), 1.6f) * 0.80f;
                    px[y * W + x] = new Color(0.04f, 0.04f, 0.032f, a);
                }
            return new Sheet { Px = px, W = W, H = H };
        }

        /// <summary>Engraved divider: a dark line over a faint light line, fading out at both ends.</summary>
        public static Sheet KitDivider()
        {
            const int W = 64, H = 3;
            var px = new Color32[W * H];
            for (int x = 0; x < W; x++)
            {
                float vx = Mathf.Abs((x + 0.5f) / W * 2f - 1f), f = 1f - Smooth((vx - 0.6f) / 0.4f);
                px[x] = new Color(0f, 0f, 0f, 0.55f * f);
                px[W + x] = new Color(0.02f, 0.02f, 0.015f, 0.35f * f);
                px[2 * W + x] = new Color(0.85f, 0.80f, 0.62f, 0.16f * f);
            }
            return new Sheet { Px = px, W = W, H = H };
        }

        /// <summary>Vertical fade (8 x 32): dark plate colour at the top row to clear at the bottom (scroll list edges).</summary>
        public static Sheet KitFade()
        {
            const int W = 8, H = 32;
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float t = (y + 0.5f) / H, a = (1f - Smooth(t)) * 0.9f;
                    px[y * W + x] = new Color(0.08f, 0.078f, 0.064f, a);
                }
            return new Sheet { Px = px, W = W, H = H };
        }

        /// <summary>Scroll indicator thumb: a 6 px cream pill.</summary>
        public static Sheet KitThumb()
        {
            const int W = 8, H = 24;
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float d = RRect(x + 0.5f - 1f, y + 0.5f, 6f, H, 3f);
                    px[y * W + x] = new Color(0.90f, 0.86f, 0.72f, 0.55f * Cov(d));
                }
            return new Sheet { Px = px, W = W, H = H, Slice = 4 };
        }

        /// <summary>Progress bar fill (tinted by GUI.color, so white): top highlight, darker bottom.</summary>
        public static Sheet KitFill()
        {
            const int W = 8, H = 16;
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float t = (y + 0.5f) / H, k = y == 0 ? 1f : Mathf.Lerp(0.95f, 0.62f, t);
                    px[y * W + x] = new Color(k, k, k, 1f);
                }
            return new Sheet { Px = px, W = W, H = H, Slice = 2 };
        }

#if !DZ_PREVIEW
        // ---------------- Unity side ----------------
        static readonly Dictionary<long, Texture2D> _btn = new Dictionary<long, Texture2D>();
        static readonly Dictionary<long, Sheet> _btnSheet = new Dictionary<long, Sheet>();

        public static Texture2D ToTex(Sheet s)
        {
            var t = new Texture2D(s.W, s.H, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
            var flip = new Color32[s.Px.Length];   // Texture2D rows run bottom-up
            for (int y = 0; y < s.H; y++) Array.Copy(s.Px, y * s.W, flip, (s.H - 1 - y) * s.W, s.W);
            t.SetPixels32(flip);
            t.Apply(false, true);
            return t;
        }

        public static RectOffset Border(Sheet s) => new RectOffset(s.PadL + s.Slice, s.PadR + s.Slice, s.PadT + s.Slice, s.PadB + s.Slice);
        public static RectOffset Overflow(Sheet s) => new RectOffset(s.PadL, s.PadR, s.PadT, s.PadB);

        /// <summary>Put a sheet on a style as its background (9-slice + shadow overflow). Only for styles whose
        /// states all use sheets with the same padding.</summary>
        public static Texture2D Apply(GUIStyle st, Sheet s)
        {
            var t = ToTex(s);
            st.normal.background = t;
            st.border = Border(s);
            st.overflow = Overflow(s);
            return t;
        }

        static long Key(Color c, int state) { Color32 k = c; return ((long)k.r << 32) | ((long)k.g << 24) | ((long)k.b << 16) | ((long)k.a << 8) | (long)state; }

        static Sheet BtnSheet(Color c, int state)
        {
            long key = Key(c, state);
            if (!_btnSheet.TryGetValue(key, out var s)) { s = Button(c, state); _btnSheet[key] = s; }
            return s;
        }

        /// <summary>Cached button texture for a colour and state.</summary>
        public static Texture2D ButtonTex(Color c, int state)
        {
            long key = Key(c, state);
            if (_btn.TryGetValue(key, out var t) && t != null) return t;
            t = ToTex(BtnSheet(c, state));
            _btn[key] = t;
            return t;
        }

        /// <summary>Skin a button style: normal / hover / pressed / on, 9-sliced, shadows in the overflow. The
        /// pressed state keeps the same overflow (its shadow is just fainter), so nothing jumps.</summary>
        public static void SkinButton(GUIStyle st, Color normal, Color hover, Color active)
        {
            var s0 = BtnSheet(normal, 0);
            st.normal.background = ButtonTex(normal, 0);
            st.hover.background = ButtonTex(hover, 1);
            st.active.background = ButtonTex(active, 2);
            st.focused.background = st.normal.background;
            st.onNormal.background = ButtonTex(active, 3);
            st.onHover.background = st.onNormal.background;
            st.onActive.background = st.active.background;
            // all states share one padding layout (shadow blur 5 / dy 2; the pressed sheet lays its smaller shadow
            // out in the same padding), so the 9-slice lines up in every state
            st.border = Border(s0);
            st.overflow = Overflow(s0);
        }
#endif
    }
}
