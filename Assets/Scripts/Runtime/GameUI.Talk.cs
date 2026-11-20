// v0.19 TALK: hold a finger on one of your survivors, tap TALK on the info card, and have a conversation.
// * TalkView (pure, also compiled by the offline preview): the panel as a flat list of draw ops (plate, chat lines,
//   question buttons, reply buttons, CLOSE), so the game and the preview lay it out with the same code.
// * GameUI part: opens / closes the panel, runs the ops with UiKit, asks Core/SurvivorMind for the answers and keeps
//   the talk memory in the save (GameState.Talk). The sim keeps running, like the other sheets; nothing here writes
//   sim state.
using System;
using System.Collections.Generic;
using System.Linq;
using DayZero.Core;
using UnityEngine;

namespace DayZero.Runtime
{
    public static class TalkView
    {
        public sealed class Line { public bool You; public string Text = ""; }
        public enum OpK { Panel, Well, Text, Button }
        public sealed class Op
        {
            public OpK K; public Rect R; public string T = "", Id = ""; public int Size = 18; public bool Bold;
            public Color Col = new Color(1f, 1f, 1f, 1f); public bool Center; public UiRole Role = UiRole.Primary; public bool On = true;
        }

        static readonly Color Amber = new Color(0.96f, 0.72f, 0.30f, 1f);
        static Color Ink => UiSkin.KitInk;
        static Color Dim => UiSkin.KitInkDim;
        public const int ANS_SIZE = 19, YOU_SIZE = 16;
        public const float LINE_H = 25f, OPT_H = 62f, OPT_GAP = 10f;

        public static Rect PanelRect(float w, float h)
        {
            float pw = Mathf.Min(1180f, w - 200f), ph = Mathf.Min(620f, h - 70f);
            return new Rect((w - pw) * 0.5f + 20f, Mathf.Max(40f, (h - ph) * 0.5f + 10f), pw, ph);
        }

        /// <summary>Greedy word wrap with the given width measure.</summary>
        public static List<string> Wrap(string text, float width, int size, bool bold, Func<string, int, bool, float> measure)
        {
            var o = new List<string>();
            var cur = "";
            foreach (var word in (text ?? "").Split(' '))
            {
                if (word.Length == 0) continue;
                string t = cur.Length == 0 ? word : cur + " " + word;
                if (cur.Length > 0 && measure(t, size, bold) > width) { o.Add(cur); cur = word; }
                else cur = t;
            }
            if (cur.Length > 0) o.Add(cur);
            return o;
        }

        /// <summary>The whole panel. replies != null: the survivor asked something back and waits for your answer.</summary>
        public static List<Op> Plan(float w, float h, string name, string mood, List<Line> chat, string[][] topics, List<TalkReply> replies, bool gone,
            Func<string, int, bool, float> measure)
        {
            var ops = new List<Op>();
            var r = PanelRect(w, h);
            ops.Add(new Op { K = OpK.Panel, R = r, T = "TALK - " + name });
            var inner = UiLayout.Inset(r, 12f, 52f, 12f, 12f);
            var L = new UiLayout(inner, 8f);
            var moodR = L.Top(30f);
            ops.Add(new Op { K = OpK.Text, R = new Rect(moodR.x, moodR.y, 80f, moodR.height), T = "MOOD", Size = 15, Bold = true, Col = Amber });
            ops.Add(new Op { K = OpK.Text, R = new Rect(moodR.x + 84f, moodR.y, moodR.width - 84f, moodR.height), T = mood, Size = 18, Bold = false, Col = Ink });
            var bottom = L.Bottom(58f);
            var cols = UiLayout.Columns(L.Rest, 16f, 1.25f, 1f);
            // chat: newest at the bottom, older lines dimmed, as many as fit
            var chatR = cols[0];
            ops.Add(new Op { K = OpK.Well, R = chatR });
            var ci = UiLayout.Inset(chatR, 14f, 10f, 14f, 10f);
            var blocks = new List<List<Op>>();
            float y = ci.yMax;
            for (int i = chat.Count - 1; i >= 0; i--)
            {
                var ln = chat[i];
                bool newest = i >= chat.Count - 2;
                int size = ln.You ? YOU_SIZE : ANS_SIZE;
                var rows = Wrap(ln.You ? "YOU: " + ln.Text : ln.Text, ci.width, size, ln.You, measure);
                float bh = rows.Count * LINE_H + (ln.You ? 2f : 10f);
                if (y - bh < ci.y) break;
                y -= bh;
                var b = new List<Op>();
                for (int k = 0; k < rows.Count; k++)
                {
                    var col = ln.You ? Amber : Ink;
                    if (!newest) col = new Color(col.r, col.g, col.b, 0.55f);
                    b.Add(new Op { K = OpK.Text, R = new Rect(ci.x, y + k * LINE_H, ci.width, LINE_H), T = rows[k], Size = size, Bold = ln.You, Col = col });
                }
                blocks.Add(b);
            }
            for (int i = blocks.Count - 1; i >= 0; i--) ops.AddRange(blocks[i]);
            if (chat.Count == 0)
                ops.Add(new Op { K = OpK.Text, R = new Rect(ci.x, ci.y, ci.width, 30f), T = gone ? "They're gone." : "Pick something to say.", Size = 18, Col = Dim });
            // right: questions, or your answer to theirs
            var oc = cols[1];
            if (gone) { }
            else if (replies != null && replies.Count > 0)
            {
                ops.Add(new Op { K = OpK.Text, R = new Rect(oc.x, oc.y, oc.width, 28f), T = "YOUR ANSWER", Size = 16, Bold = true, Col = Amber });
                float yy = oc.y + 36f;
                for (int i = 0; i < replies.Count; i++)
                {
                    ops.Add(new Op { K = OpK.Button, R = new Rect(oc.x, yy, oc.width, 70f), T = replies[i].Label, Id = "r:" + replies[i].Id, Role = i == 2 ? UiRole.Danger : i == 0 ? UiRole.Accent : UiRole.Primary, Size = 18 });
                    yy += 80f;
                }
                ops.Add(new Op { K = OpK.Button, R = new Rect(oc.x, yy + 6f, oc.width, 56f), T = "SAY NOTHING", Id = "r:none", Role = UiRole.Neutral, Size = 16 });
            }
            else
            {
                float cellH = Mathf.Min(OPT_H + 10f, (oc.height - OPT_GAP * 4f) / 5f);
                for (int i = 0; i < topics.Length; i++)
                    ops.Add(new Op { K = OpK.Button, R = UiLayout.Cell(oc, 2, i, cellH, OPT_GAP), T = topics[i][1], Id = "t:" + topics[i][0], Role = UiRole.Primary, Size = 18 });
            }
            var bb = UiLayout.Columns(bottom, 12f, 1f, -220f);
            ops.Add(new Op { K = OpK.Text, R = bb[0], T = gone ? "" : replies != null && replies.Count > 0 ? "They're waiting on you." : "Tap a question. They remember what you talk about.", Size = 15, Col = Dim });
            ops.Add(new Op { K = OpK.Button, R = bb[1], T = "CLOSE", Id = "close", Role = UiRole.Danger, Size = 20 });
            return ops;
        }
    }

#if !DZ_PREVIEW
    public sealed partial class GameUI
    {
        int _talkPid = -1;
        readonly List<TalkView.Line> _talkChat = new List<TalkView.Line>();
        TalkAnswer _talkPending;
        float _talkTickT;
        string _talkName = "";
        GameState _talkS;

        public bool TalkOpen => _talkPid >= 0 && _b != null && _b.State == Bootstrap.Screen2.Playing && !_b.Picking;

        void HookFallen()
        {
            if (SurvivorMind.FallenSource != null) return;
            SurvivorMind.FallenSource = () =>
            {
                var l = RunStats.Instance.Log?.Fallen;
                return l == null ? Enumerable.Empty<FallenInfo>() : l.Select(f => new FallenInfo { Name = f.Name, Job = f.Job, Cause = f.Cause, Day = f.Day }).ToList();
            };
        }

        public void OpenTalk(int pid)
        {
            var g = G; if (g?.C == null) return;
            var p = g.C.People.FirstOrDefault(q => q.Id == pid && q.St != "dead");
            if (p == null || !SurvivorMind.CanTalk(g, p)) return;
            HookFallen();
            _talkPid = pid; _talkS = g.S; _talkName = p.Name ?? ""; _talkChat.Clear(); _talkPending = null;
            LongPressInfo.Clear();
        }

        void CloseTalk() { _talkPid = -1; _talkPending = null; _talkChat.Clear(); }

        /// <summary>Per frame (hook in DrawGame): remember names in camp now and then, so later talks know who died.</summary>
        void TalkTick()
        {
            try
            {
                if (G?.S == null || G.C == null || !G.C.Started) return;
                if (Time.unscaledTime - _talkTickT < 3f) return;
                _talkTickT = Time.unscaledTime;
                if (SurvivorMind.Observe(G)) SurvivorMind.Commit(G.S);
            }
            catch (Exception e) { Debug.LogException(e); }
        }

        void DrawTalk(float w, float h)
        {
            if (!TalkOpen) return;
            var g = G;
            if (g?.S == null || g.C == null || !ReferenceEquals(g.S, _talkS)) { CloseTalk(); return; }
            var p = g.C.People.FirstOrDefault(q => q.Id == _talkPid && q.St != "dead");
            bool gone = p == null;
            string mood = gone ? "Gone." : SurvivorMind.For(g, p)?.MoodLine ?? "";
            if (gone && (_talkChat.Count == 0 || _talkChat[_talkChat.Count - 1].Text != "They're gone.")) { _talkChat.Add(new TalkView.Line { Text = "They're gone." }); _talkPending = null; }
            var ops = TalkView.Plan(w, h, gone ? _talkName : p.Name, mood, _talkChat, SurvivorMind.TOPICS, _talkPending?.Replies, gone, (t, s, b) => TextW(t, s, b));
            string hit = null;
            foreach (var o in ops)
            {
                switch (o.K)
                {
                    case TalkView.OpK.Panel: UiKit.Panel(Blk(o.R), o.T); break;
                    case TalkView.OpK.Well: UiKit.RowPlate(o.R, 1); break;
                    case TalkView.OpK.Text: UiKit.FitLabel(o.R, o.T, o.Size, o.Center ? TextAnchor.MiddleCenter : TextAnchor.MiddleLeft, o.Col, o.Bold, 11); break;
                    case TalkView.OpK.Button: if (KBtn(o.R, o.T, o.Role, false, o.Size)) hit = o.Id; break;
                }
            }
            if (hit == null) return;
            try
            {
                if (hit == "close") { CloseTalk(); return; }
                if (gone) return;
                if (hit.StartsWith("t:"))
                {
                    var a = SurvivorMind.Ask(g, p, hit.Substring(2));
                    _talkChat.Add(new TalkView.Line { You = true, Text = a.Question });
                    _talkChat.Add(new TalkView.Line { Text = a.Text });
                    _talkPending = a.Replies.Count > 0 ? a : null;
                }
                else if (hit.StartsWith("r:") && _talkPending != null)
                {
                    string id = hit.Substring(2);
                    if (id != "none")
                    {
                        var rep = _talkPending.Replies.FirstOrDefault(x => x.Id == id);
                        _talkChat.Add(new TalkView.Line { You = true, Text = rep?.Label ?? "" });
                        _talkChat.Add(new TalkView.Line { Text = SurvivorMind.Reply(g, p, _talkPending.Topic, id) });
                    }
                    else _talkChat.Add(new TalkView.Line { You = true, Text = "(you say nothing)" });
                    _talkPending = null;
                }
                while (_talkChat.Count > 16) _talkChat.RemoveAt(0);
                SurvivorMind.Commit(g.S);
            }
            catch (Exception e) { Debug.LogException(e); }
        }
    }
#endif
}
