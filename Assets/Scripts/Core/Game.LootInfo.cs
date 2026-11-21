// v0.19.3 (Austin's screenshot 21:38: "Sometimes there's no loot button, only GUT. Fix that."): the building sheet
// showed SEND TASK FORCE TO LOOT only while a building was unsearched with loot left, and hid the whole row otherwise
// (searched by auto-scavengers, another colony or a faction, picked clean, gutted), so outside the safe zone the sheet
// was left with the convert-only message and GUT. LootInfo tells the sheet, for every lootable building, whether the
// LOOT button can send survivors right now (the same send_raid order as before) or why not, so the button always shows
// (disabled with the reason when it can't). Read only: no state, no RNG, no rule change (parity identical).
using System;
using UnityEngine;

namespace DayZero.Core
{
    public sealed partial class Game
    {
        public struct LootState
        {
            /// <summary>False: the sheet shows no loot row (in use, part of a camp, parking / open field).</summary>
            public bool Show;
            /// <summary>True: LOOT can send survivors now.</summary>
            public bool Ok;
            /// <summary>0..1 share of the original loot still in the building.</summary>
            public float Left;
            /// <summary>Ok: what is left / who goes. Not ok: why the button is disabled.</summary>
            public string Text;
        }

        /// <summary>Loot row state of the building sheet for lot l (bid: the camp to send from, -1 = nearest).</summary>
        public LootState LootInfo(int l, int bid = -1)
        {
            var o = new LootState();
            if (S == null || l < 0 || l >= D.Lots.Count || l >= S.Loot.Length) return o;
            string k = D.LotKind(l);
            if (k == "parking" || (k == "farm" && D.Lots[l].Landmark == "")) return o;
            if (ConvOf(l) != null || LotOwnerBase(l) != null) return o;
            o.Show = true;
            float left = Mathf.Clamp01(S.Loot[l]);
            o.Left = left;
            if (Gutting(l)) { o.Text = "A crew is gutting it: nothing left to search."; return o; }
            if (IsGutted(l)) { o.Text = "Gutted to the shell: nothing left to search."; return o; }
            if (Searched(l) || left < 0.15f)
            {
                string who = "";
                int by = SearchedBy(l);
                if (by > 0 && by != CurCol + 1) who = " by " + (ColonyAt(by - 1)?.Name ?? "another colony");
                if (Searched(l))
                {
                    double hrs = S.Srch[l] + REGROW_H - S.T;
                    int days = (int)Math.Ceiling(Math.Max(0.0, hrs) / 24.0);
                    o.Text = days <= 1 ? string.Format("Already looted{0} - regrows in about a day.", who)
                                       : string.Format("Already looted{0} - regrows in {1} days.", who, days);
                }
                else o.Text = "Already picked clean - nothing left to find.";
                return o;
            }
            foreach (var p in C.People)
                if (p.St != "dead" && (p.Tf == -1000000 - l || (p.Goal == "scav" && p.Lot == l)))
                { o.Text = string.Format("{0} is already on the way to search it.", p.First); return o; }
            if (bid >= 0 && BaseById(bid) == null) bid = -1;
            if (!C.Bases.Exists(b => b.Done >= 1.0)) { o.Text = "No finished camp to send survivors from."; return o; }
            var team = PickTeam(D.LotCenter(l), 3, bid);
            if (team.Count == 0)
            {
                o.Text = bid >= 0 ? string.Format("Nobody free at {0} (everyone is out, hurt, a child, in a squad or on hold).", BaseById(bid).Name)
                                  : "Nobody free at camp (everyone is out, hurt, a child, in a squad or on hold).";
                return o;
            }
            o.Ok = true;
            int pct = (int)Math.Round(left * 100f);
            string state = left >= 0.95f ? "Not searched yet" : string.Format("Partly looted: about {0}% left", pct);
            o.Text = string.Format("{0}. {1} head{2} out now.", state, string.Join(", ", team.ConvertAll(p => p.First)), team.Count == 1 ? "s" : "");
            return o;
        }
    }
}
