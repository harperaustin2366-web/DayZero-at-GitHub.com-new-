// E13 seeded challenge codes. A run's world seed, difficulty and EXPANDED flag packed into a short code a player can
// read out or paste to a friend, who types it on the title screen (ENTER CODE) to play the same city.
//   short form, 6 chars = 30 bits: seed (27 bits) | difficulty (2) | expanded (1)       - every new game gets one
//   long form,  8 chars = 40 bits: seed (31 bits) | difficulty (2) | expanded (1) | check (6) - older saves whose
//                                  seed is wider than 27 bits
//   custom suffix, 3 chars = 15 bits (E14 CUSTOM GAME runs only): zombies, loot, raids, day length as step indexes into
//                                  CustomGame's slider steps (3 bits each) | check (3). So a code is 6 or 8 characters,
//                                  or 9 / 11 for a custom run; a normal run never has the suffix.
// The bits are mixed by an invertible multiply / xor so neighbouring seeds give unrelated looking codes. Alphabet:
// Crockford base32 (no I, L, O, U), so a code read aloud is hard to mistype. Pure code, no UnityEngine.GUI: the
// offline preview draws the same keypad (SeedCodeUi.Entry) through the MenuCtx display list.
using System;
using System.Text;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public static class SeedCode
    {
        public const string ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        public const int SHORT_LEN = 6, LONG_LEN = 8;
        public const int SHORT_SEED_BITS = 27;
        const ulong M30 = (1UL << 30) - 1, M40 = (1UL << 40) - 1;
        const ulong MUL30 = 0x2C9277B5UL & M30 | 1UL, MUL40 = 0x9E3779B97FUL & M40 | 1UL;
        const ulong XOR30 = 0x15A3C96EUL & M30, XOR40 = 0x5DEECE66DUL & M40;

        /// <summary>A fresh world seed that fits the short code (27 bits, never 0). Clock based, so it draws nothing
        /// from the game's RNG streams.</summary>
        public static int RandomSeed()
        {
            ulong x = (ulong)DateTime.UtcNow.Ticks;
            x ^= x >> 33; x *= 0xFF51AFD7ED558CCDUL; x ^= x >> 33; x *= 0xC4CEB9FE1A85EC53UL; x ^= x >> 33;
            int s = (int)(x & ((1UL << SHORT_SEED_BITS) - 1));
            return s == 0 ? 1 : s;
        }

        /// <summary>The code for a run (6 chars when the seed fits 27 bits, else 8).</summary>
        public static string Encode(int seed, int difficulty, bool expanded, CustomRules custom = null)
        {
            string tail = custom != null && !custom.IsDefault ? CustomSuffix(custom) : "";
            return EncodeBase(seed, difficulty, expanded) + tail;
        }

        static string EncodeBase(int seed, int difficulty, bool expanded)
        {
            if (seed < 0) seed = 0;
            int d = Math.Max(0, Math.Min(2, difficulty));
            if (seed < (1 << SHORT_SEED_BITS))
            {
                ulong v = ((ulong)seed << 3) | ((ulong)d << 1) | (expanded ? 1UL : 0UL);
                v = ((v * MUL30) & M30) ^ XOR30;
                return ToB32(v, SHORT_LEN);
            }
            ulong w = ((ulong)(uint)seed << 3) | ((ulong)d << 1) | (expanded ? 1UL : 0UL);   // 34 bits
            w = (w << 6) | Check(w);
            w = ((w * MUL40) & M40) ^ XOR40;
            return ToB32(w, LONG_LEN);
        }

        public static string ForGame(Game g) => g?.S == null ? "" : Encode(g.S.Seed, g.S.Difficulty, g.S.ExpRules, g.S.Custom);

        /// <summary>For display: a long code gets a dash in the middle ("ABCD-EFGH"), a custom suffix its own ("J3MR5W-K2P").</summary>
        public static string Pretty(string code)
        {
            if (string.IsNullOrEmpty(code)) return "";
            int n = code.Length;
            string tail = n == SHORT_LEN + 3 || n == LONG_LEN + 3 ? "-" + code.Substring(n - 3) : "";
            string b = tail != "" ? code.Substring(0, n - 3) : code;
            if (b.Length == LONG_LEN) b = b.Substring(0, 4) + "-" + b.Substring(4);
            return b + tail;
        }

        // ---------------- custom suffix ----------------
        static readonly double[][] STEPS = { CustomGame.ZOMBIE_STEPS, CustomGame.LOOT_STEPS, CustomGame.RAID_STEPS, CustomGame.DAY_STEPS };
        const ulong SUF_MUL = 0x2D4BUL, SUF_XOR = 0x5A3CUL, M15 = (1UL << 15) - 1;

        static int Near(double[] steps, double v)
        {
            int best = 0;
            for (int i = 1; i < steps.Length; i++) if (Math.Abs(steps[i] - v) < Math.Abs(steps[best] - v)) best = i;
            return best;
        }

        static string CustomSuffix(CustomRules r)
        {
            double[] v = { r.Zombies, r.Loot, r.Raids, r.DayLen };
            ulong x = 0;
            for (int i = 0; i < 4; i++) x = (x << 3) | (uint)Math.Min(7, Near(STEPS[i], v[i]));
            x = (x << 3) | ((x * 0x9E37UL >> 7) & 7);
            x = ((x * SUF_MUL) & M15) ^ SUF_XOR;
            return ToB32(x, 3);
        }

        static bool TryCustom(string suf, out CustomRules r)
        {
            r = null;
            ulong x = 0;
            foreach (char ch in suf) { int k = ALPHABET.IndexOf(ch); if (k < 0) return false; x = (x << 5) | (uint)k; }
            x = ((x ^ SUF_XOR) * Inverse(SUF_MUL, 15)) & M15;
            ulong body = x >> 3;
            if ((x & 7) != ((body * 0x9E37UL >> 7) & 7)) return false;
            var idx = new int[4];
            for (int i = 3; i >= 0; i--) { idx[i] = (int)(body & 7); body >>= 3; }
            for (int i = 0; i < 4; i++) if (idx[i] >= STEPS[i].Length) return false;
            r = new CustomRules { Zombies = STEPS[0][idx[0]], Loot = STEPS[1][idx[1]], Raids = STEPS[2][idx[2]], DayLen = STEPS[3][idx[3]] };
            return !r.IsDefault;
        }

        /// <summary>Typed text back to settings. Accepts lower case, spaces and dashes, and reads I / L as 1 and O as 0.
        /// False (with a reason) for a wrong length, an unknown letter, a bad difficulty or a failed check.</summary>
        public static bool TryDecode(string text, out int seed, out int difficulty, out bool expanded, out string error)
            => TryDecode(text, out seed, out difficulty, out expanded, out _, out error);

        /// <summary>As above; custom = the E14 multipliers of a 9 / 11 character code (null for a normal run).</summary>
        public static bool TryDecode(string text, out int seed, out int difficulty, out bool expanded, out CustomRules custom, out string error)
        {
            seed = 0; difficulty = 1; expanded = false; error = ""; custom = null;
            string s = Normalize(text);
            if (s.Length == SHORT_LEN + 3 || s.Length == LONG_LEN + 3)
            {
                if (!TryCustom(s.Substring(s.Length - 3), out custom)) { error = "The last 3 characters (custom rules) are not valid."; return false; }
                s = s.Substring(0, s.Length - 3);
            }
            if (s.Length != SHORT_LEN && s.Length != LONG_LEN) { error = "A code has 6 or 8 characters (9 or 11 for a custom game)."; return false; }
            ulong v = 0;
            foreach (char ch in s)
            {
                int k = ALPHABET.IndexOf(ch);
                if (k < 0) { error = "Unknown character " + ch + "."; return false; }
                v = (v << 5) | (uint)k;
            }
            if (s.Length == SHORT_LEN)
            {
                v = ((v ^ XOR30) * Inverse(MUL30, 30)) & M30;
                int d = (int)((v >> 1) & 3);
                if (d > 2) { error = "That code is not valid."; return false; }
                seed = (int)(v >> 3); difficulty = d; expanded = (v & 1) != 0;
                return true;
            }
            v = ((v ^ XOR40) * Inverse(MUL40, 40)) & M40;
            ulong body = v >> 6;
            int d2 = (int)((body >> 1) & 3);
            if ((v & 63) != Check(body) || d2 > 2 || (body >> 3) > int.MaxValue) { error = "That code is not valid. Check each character."; return false; }
            seed = (int)(body >> 3); difficulty = d2; expanded = (body & 1) != 0;
            return true;
        }

        public static string Normalize(string text)
        {
            var sb = new StringBuilder();
            foreach (char c0 in text ?? "")
            {
                char c = char.ToUpperInvariant(c0);
                if (c == ' ' || c == '-' || c == '_') continue;
                if (c == 'I' || c == 'L') c = '1';
                else if (c == 'O') c = '0';
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>"Normal  -  Expanded rules" style line for a decoded code.</summary>
        public static string Describe(int difficulty, bool expanded, CustomRules custom = null)
            => Game.DIFF_NAMES[Math.Max(0, Math.Min(Game.DIFF_NAMES.Length - 1, difficulty))] + "  -  " + (expanded ? "Expanded rules" : "Classic rules")
               + (custom != null && !custom.IsDefault ? "  -  " + CustomGame.Summary(custom) : "");

        static string ToB32(ulong v, int len)
        {
            var c = new char[len];
            for (int i = len - 1; i >= 0; i--) { c[i] = ALPHABET[(int)(v & 31)]; v >>= 5; }
            return new string(c);
        }

        static ulong Check(ulong body)
        {
            ulong h = body * 0x9E3779B97F4A7C15UL;
            h ^= h >> 29;
            return (h >> 20) & 63;
        }

        /// <summary>Multiplicative inverse of an odd a modulo 2^bits (Newton iteration).</summary>
        static ulong Inverse(ulong a, int bits)
        {
            ulong x = a;   // correct to 3 bits for odd a
            for (int i = 0; i < 6; i++) x *= 2UL - a * x;
            return bits >= 64 ? x : x & ((1UL << bits) - 1);
        }
    }

    /// <summary>ENTER CODE screen state (title screen).</summary>
    public sealed class SeedEntryState
    {
        public bool Open;
        public string Typed = "";
        public void Reset() { Typed = ""; }
    }

    /// <summary>E13 screens laid out into a MenuCtx display list: the ENTER CODE keypad and the code chip on the run
    /// summary. The game paints them with SideMenus.Flush; the offline preview paints the same list.</summary>
    public static class SeedCodeUi
    {
        static Color INK => SideMenus.INK; static Color INK2 => SideMenus.INK2; static Color MUTE => SideMenus.MUTE;
        static Color AMBER => SideMenus.AMBER; static Color GREEN => SideMenus.GREEN; static Color RED => SideMenus.RED;

        public const int MAX_LEN = LONG_LEN_PLUS;
        const int LONG_LEN_PLUS = SeedCode.LONG_LEN + 3;

        /// <summary>Keypad: a field of character boxes, a status line, 32 big keys (4 x 8), then CANCEL / DELETE /
        /// CLEAR / START. start(seed, difficulty, expanded, custom) runs once the typed code decodes.</summary>
        public static void Entry(MenuCtx c, SeedEntryState st, Action<int, int, bool, CustomRules> start, Action close)
        {
            c.Dim();
            float pw = Math.Min(1060f, c.W - 40f), ph = Math.Min(676f, c.H - 24f);
            var inner = c.Panel(new Rect((c.W - pw) / 2f, (c.H - ph) / 2f, pw, ph), "ENTER CHALLENGE CODE");
            var L = new UiLayout(inner, 10f);

            // field: 6 boxes, 8 once the player types past 6
            string t = st.Typed ?? "";
            int slots = t.Length <= SeedCode.SHORT_LEN ? SeedCode.SHORT_LEN : t.Length <= SeedCode.LONG_LEN ? SeedCode.LONG_LEN : t.Length <= SeedCode.SHORT_LEN + 3 ? SeedCode.SHORT_LEN + 3 : MAX_LEN;
            var field = L.Top(76f);
            float gap = 10f, bw = Math.Min(72f, (field.width - gap * (slots - 1)) / slots), fw = slots * bw + (slots - 1) * gap;
            float fx = field.x + (field.width - fw) / 2f;
            for (int i = 0; i < slots; i++)
            {
                var b = new Rect(fx + i * (bw + gap), field.y + 2f, bw, field.height - 4f);
                c.Plate(b, i, i == t.Length);
                if (i < t.Length) c.Fit(UiLayout.Inset(b, 4f), t[i].ToString(), 40, AMBER, true, MenuAlign.Center);
            }

            bool ok = SeedCode.TryDecode(t, out int seed, out int diff, out bool exp, out CustomRules cust, out string err);
            string line; Color col;
            if (t.Length == 0) { line = "Type the code a friend shared to play the same city with the same settings."; col = INK2; }
            else if (ok) { line = "Valid code:  " + SeedCode.Describe(diff, exp, cust) + (cust == null ? "  -  world " + seed : ""); col = GREEN; }
            else if (t.Length < SeedCode.SHORT_LEN || t.Length == SeedCode.SHORT_LEN + 1 || t.Length == SeedCode.LONG_LEN + 2)
            { line = "Keep typing  -  " + t.Length + " of " + (t.Length < SeedCode.SHORT_LEN ? SeedCode.SHORT_LEN : t.Length < SeedCode.LONG_LEN ? SeedCode.LONG_LEN : MAX_LEN); col = INK2; }
            else { line = err; col = RED; }
            c.Fit(L.Top(30f), line, 19, col, ok, MenuAlign.Center);

            var foot = L.Bottom(70f);
            var fb = UiLayout.Columns(foot, 12f, 1f, 1f, 1f, 1.4f);
            c.Btn(fb[0], "CANCEL", UiRole.Neutral, () => { st.Open = false; st.Reset(); close?.Invoke(); }, true, false, 22);
            c.Btn(fb[1], "DELETE", UiRole.Primary, () => { if (st.Typed.Length > 0) st.Typed = st.Typed.Substring(0, st.Typed.Length - 1); }, t.Length > 0, false, 22);
            c.Btn(fb[2], "CLEAR", UiRole.Danger, () => st.Typed = "", t.Length > 0, false, 22);
            int s2 = seed, d2 = diff; bool e2 = exp; var c2 = cust;
            c.Btn(fb[3], "START GAME", ok ? UiRole.Accent : UiRole.Disabled, () => { st.Open = false; st.Reset(); start?.Invoke(s2, d2, e2, c2); }, ok, false, 24);

            // 4 rows x 8 keys
            var pad = L.Rest;
            const int COLS = 8, ROWS = 4;
            float kg = 8f;
            float kh = Math.Min(84f, (pad.height - kg * (ROWS - 1)) / ROWS);
            float kw = (pad.width - kg * (COLS - 1)) / COLS;
            float py = pad.y + (pad.height - (kh * ROWS + kg * (ROWS - 1))) / 2f;
            bool full = t.Length >= MAX_LEN;
            for (int i = 0; i < SeedCode.ALPHABET.Length; i++)
            {
                int row = i / COLS, colI = i % COLS;
                var kr = new Rect(pad.x + colI * (kw + kg), py + row * (kh + kg), kw, kh);
                string ch = SeedCode.ALPHABET[i].ToString();
                c.Btn(kr, ch, i < 10 ? UiRole.Neutral : UiRole.Primary, () => { if (st.Typed.Length < MAX_LEN) st.Typed += ch; }, !full, false, 30);
            }
        }

        /// <summary>Run summary: "CODE  K7M2QX" chip in the sheet's header (same sheet rect as ReportScreens.Summary).</summary>
        public static void SummaryChip(MenuCtx c, Game g)
        {
            string code = SeedCode.ForGame(g);
            if (code == "") return;
            float pw = Math.Min(1240f, c.W - 40f), ph = Math.Min(690f, c.H - 32f);
            var sheet = new Rect((c.W - pw) / 2f, (c.H - ph) / 2f, pw, ph);
            float cw = code.Length > SeedCode.LONG_LEN ? 360f : 288f;
            c.Chip(new Rect(sheet.xMax - cw - 16f, sheet.y + 8f, cw, 34f), "CHALLENGE CODE  " + SeedCode.Pretty(code), UiRole.Accent, 17);
        }
    }
}
