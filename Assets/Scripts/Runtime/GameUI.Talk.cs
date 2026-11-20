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
        public enum OpK { Panel, Well, Text, Button, Input }
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

        public const float IN_H = 60f;

        /// <summary>The panel when the phone keyboard is up: only the top part of the screen is visible, so the panel
        /// shrinks to it (newest lines + the text box), full width.</summary>
        public static Rect TypingRect(float w, float h, float kbFrac)
        {
            var full = PanelRect(w, h);
            float top = 8f, bottom = h * (1f - Mathf.Clamp(kbFrac, 0.3f, 0.7f)) - 8f;
            return new Rect(full.x, top, full.width, Mathf.Max(220f, bottom - top));
        }

        static void Chat(List<Op> ops, Rect ci, List<Line> chat, Func<string, int, bool, float> measure)
        {
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
        }

        /// <summary>The text box + SEND (and HIDE while the keyboard is up). The Input op is drawn by the game as a
        /// tappable box that opens the phone keyboard (or a GUI.TextField on desktop).</summary>
        static void InputRow(List<Op> ops, Rect row, string input, bool typing)
        {
            bool empty = string.IsNullOrEmpty(input);
            var cols = typing ? UiLayout.Columns(row, 10f, 1f, -150f, -120f) : UiLayout.Columns(row, 10f, 1f, -130f);
            ops.Add(new Op { K = OpK.Input, R = cols[0], Id = "input", T = empty ? (typing ? "" : "Type to talk...") : input, Size = 19, Col = empty ? Dim : Ink, On = !empty });
            ops.Add(new Op { K = OpK.Button, R = cols[1], T = "SEND", Id = "send", Role = UiRole.Accent, Size = 20, On = !empty });
            if (typing) ops.Add(new Op { K = OpK.Button, R = cols[2], T = "HIDE", Id = "hidekb", Role = UiRole.Neutral, Size = 18 });
        }

        /// <summary>The whole panel. replies != null: the survivor asked something back and waits for your answer.
        /// input: what you've typed so far. typing: the phone keyboard is up (kbFrac of the screen), so the chat and
        /// the text box move to the top part of the screen.</summary>
        public static List<Op> Plan(float w, float h, string name, string mood, List<Line> chat, string[][] topics, List<TalkReply> replies, bool gone,
            Func<string, int, bool, float> measure, string input = null, bool typing = false, float kbFrac = 0f)
        {
            var ops = new List<Op>();
            if (typing && !gone)
            {
                var tr = TypingRect(w, h, kbFrac);
                ops.Add(new Op { K = OpK.Panel, R = tr, T = "TALK - " + name });
                var ti = UiLayout.Inset(tr, 12f, 52f, 12f, 12f);
                var row = new Rect(ti.x, ti.yMax - IN_H + 4f, ti.width, IN_H - 4f);
                var well = new Rect(ti.x, ti.y, ti.width, row.y - ti.y - 8f);
                ops.Add(new Op { K = OpK.Well, R = well });
                Chat(ops, UiLayout.Inset(well, 14f, 6f, 14f, 6f), chat, measure);
                if (chat.Count == 0) ops.Add(new Op { K = OpK.Text, R = new Rect(well.x + 14f, well.y + 6f, well.width - 28f, 30f), T = "Say anything. Enter sends.", Size = 18, Col = Dim });
                InputRow(ops, row, input, true);
                return ops;
            }
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
            Chat(ops, UiLayout.Inset(chatR, 14f, 10f, 14f, 10f), chat, measure);
            if (chat.Count == 0)
                ops.Add(new Op { K = OpK.Text, R = new Rect(chatR.x + 14f, chatR.y + 10f, chatR.width - 28f, 30f), T = gone ? "They're gone." : "Type something, or tap a quick question.", Size = 18, Col = Dim });
            // right: the text box on top, then the quick questions (or your answer to theirs)
            var oc = cols[1];
            if (!gone)
            {
                InputRow(ops, new Rect(oc.x, oc.y, oc.width, IN_H), input, false);
                oc = new Rect(oc.x, oc.y + IN_H + 12f, oc.width, oc.height - IN_H - 12f);
            }
            if (gone) { }
            else if (replies != null && replies.Count > 0)
            {
                ops.Add(new Op { K = OpK.Text, R = new Rect(oc.x, oc.y, oc.width, 26f), T = "YOUR ANSWER (or type it)", Size = 16, Bold = true, Col = Amber });
                float bh = Mathf.Min(64f, (oc.height - 30f - 4f * 8f) / 4f);
                float yy = oc.y + 30f;
                for (int i = 0; i < replies.Count; i++)
                {
                    ops.Add(new Op { K = OpK.Button, R = new Rect(oc.x, yy, oc.width, bh), T = replies[i].Label, Id = "r:" + replies[i].Id, Role = i == 2 ? UiRole.Danger : i == 0 ? UiRole.Accent : UiRole.Primary, Size = 18 });
                    yy += bh + 8f;
                }
                ops.Add(new Op { K = OpK.Button, R = new Rect(oc.x, yy, oc.width, Mathf.Min(52f, bh)), T = "SAY NOTHING", Id = "r:none", Role = UiRole.Neutral, Size = 16 });
            }
            else
            {
                float cellH = Mathf.Min(OPT_H, (oc.height - OPT_GAP * 4f) / 5f);
                for (int i = 0; i < topics.Length; i++)
                    ops.Add(new Op { K = OpK.Button, R = UiLayout.Cell(oc, 2, i, cellH, OPT_GAP), T = topics[i][1], Id = "t:" + topics[i][0], Role = UiRole.Primary, Size = 17 });
            }
            var bb = UiLayout.Columns(bottom, 12f, 1f, -220f);
            ops.Add(new Op { K = OpK.Text, R = bb[0], T = gone ? "" : replies != null && replies.Count > 0 ? "They're waiting on you." : "Type anything, or tap a question. They remember what you talk about.", Size = 15, Col = Dim });
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

        void CloseTalk() { TalkKbClose(); _talkPid = -1; _talkPending = null; _talkChat.Clear(); _talkInput = ""; }

        // ---------------- v0.19.2 typing: a text box + SEND. On a phone, tapping the box opens the system keyboard
        // (TouchScreenKeyboard, single line, 120 chars); while it is up the panel moves to the top part of the screen.
        // Enter / Done sends. On desktop the box is a GUI.TextField (Enter sends).
        string _talkInput = "";
        TouchScreenKeyboard _talkKb;
        GUIStyle _talkField;
        const string TALK_FIELD = "dzTalkInput";

        bool TalkKbUp => _talkKb != null && _talkKb.status == TouchScreenKeyboard.Status.Visible;

        void TalkKbOpen()
        {
            try
            {
                TouchScreenKeyboard.hideInput = false;
                _talkKb = TouchScreenKeyboard.Open(_talkInput ?? "", TouchScreenKeyboardType.Default, true, false, false, false, "Say something...", SurvivorMind.SAY_MAX);
            }
            catch (Exception e) { Debug.LogException(e); _talkKb = null; }
        }

        void TalkKbClose()
        {
            try { if (_talkKb != null && _talkKb.active) _talkKb.active = false; } catch { }
            _talkKb = null;
        }

        float TalkKbFrac()
        {
            float f = 0f;
            try { var a = TouchScreenKeyboard.area; if (Screen.height > 0) f = a.height / Screen.height; } catch { }
            return f > 0.15f ? Mathf.Clamp(f + 0.04f, 0.3f, 0.7f) : 0.56f;   // Android often reports 0 until the keyboard settles
        }

        /// <summary>Read the phone keyboard each frame: live text, Done = send, Canceled / LostFocus = just hide.</summary>
        bool TalkKbPoll()
        {
            if (_talkKb == null) return false;
            bool send = false;
            try
            {
                var st = _talkKb.status;
                string t = SurvivorMind.SanitizeTyped(_talkKb.text ?? "");
                if (st == TouchScreenKeyboard.Status.Visible || st == TouchScreenKeyboard.Status.Done) _talkInput = t;
                if (st == TouchScreenKeyboard.Status.Done) { send = true; _talkKb = null; }
                else if (st == TouchScreenKeyboard.Status.Canceled || st == TouchScreenKeyboard.Status.LostFocus) _talkKb = null;
            }
            catch (Exception e) { Debug.LogException(e); _talkKb = null; }
            return send;
        }

        void TalkSend(Game g, Person p)
        {
            string text = SurvivorMind.SanitizeTyped(_talkInput);
            _talkInput = "";
            if (_talkKb != null) { try { _talkKb.text = ""; } catch { } }
            if (text.Length == 0 || p == null) return;
            var a = SurvivorMind.Say(g, p, text, _talkPending);
            _talkChat.Add(new TalkView.Line { You = true, Text = a.Question });
            if (!string.IsNullOrEmpty(a.Text)) _talkChat.Add(new TalkView.Line { Text = a.Text });
            _talkPending = a.Replies.Count > 0 ? a : null;
            _b.PlayClick();
        }

        /// <summary>Per frame (hook in DrawGame): remember names in camp now and then, so later talks know who died.</summary>
        void TalkTick()
        {
            try
            {
                if (_talkKb != null && !TalkOpen) TalkKbClose();   // another sheet took over: put the keyboard away
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
            if (gone) TalkKbClose();
            string mood = gone ? "Gone." : SurvivorMind.For(g, p)?.MoodLine ?? "";
            if (gone && (_talkChat.Count == 0 || _talkChat[_talkChat.Count - 1].Text != "They're gone.")) { _talkChat.Add(new TalkView.Line { Text = "They're gone." }); _talkPending = null; }
            bool touch = TouchScreenKeyboard.isSupported;
            bool kbSend = touch && TalkKbPoll();
            // desktop: Enter in the text box sends (checked before the TextField sees the key)
            var ev = Event.current;
            bool enter = !touch && ev != null && ev.type == EventType.KeyDown && (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter)
                         && GUI.GetNameOfFocusedControl() == TALK_FIELD;
            if (enter) ev.Use();
            bool typing = touch && TalkKbUp;
            var ops = TalkView.Plan(w, h, gone ? _talkName : p.Name, mood, _talkChat, SurvivorMind.TOPICS, _talkPending?.Replies, gone, (t, s, b) => TextW(t, s, b),
                _talkInput, typing, typing ? TalkKbFrac() : 0f);
            string hit = null;
            foreach (var o in ops)
            {
                switch (o.K)
                {
                    case TalkView.OpK.Panel: UiKit.Panel(Blk(o.R), o.T); break;
                    case TalkView.OpK.Well: UiKit.RowPlate(o.R, 1); break;
                    case TalkView.OpK.Text: UiKit.FitLabel(o.R, o.T, o.Size, o.Center ? TextAnchor.MiddleCenter : TextAnchor.MiddleLeft, o.Col, o.Bold, 11); break;
                    case TalkView.OpK.Button: if (KBtn(o.R, o.T, o.Role, false, o.Size)) hit = o.Id; break;
                    case TalkView.OpK.Input: DrawTalkInput(o, touch, typing); if (touch && !typing && TalkBoxTapped(o.R)) hit = "input"; break;
                }
            }
            if (kbSend) hit = "send";
            if (enter) hit = "send";
            if (hit == null) return;
            try
            {
                if (hit == "close") { CloseTalk(); return; }
                if (gone) return;
                if (hit == "input") { TalkKbOpen(); return; }
                if (hit == "hidekb") { TalkKbClose(); return; }
                if (hit == "send") { TalkSend(g, p); }
                else if (hit.StartsWith("t:"))
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
                while (_talkChat.Count > 24) _talkChat.RemoveAt(0);
                SurvivorMind.Commit(g.S);
            }
            catch (Exception e) { Debug.LogException(e); }
        }

        /// <summary>The text box: a plate with the text (phone: tap opens the keyboard) or a real TextField (desktop).</summary>
        void DrawTalkInput(TalkView.Op o, bool touch, bool typing)
        {
            UiKit.RowPlate(o.R, 0, typing);
            var ir = UiLayout.Inset(o.R, 14f, 0f, 14f, 0f);
            if (touch)
            {
                string shown = o.T;
                if (typing && (Time.unscaledTime % 1f) < 0.55f) shown += "|";
                UiKit.FitLabel(ir, TalkTail(shown, ir.width, o.Size), o.Size, TextAnchor.MiddleLeft, o.Col, false, 12);
                return;
            }
            if (_talkField == null)
            {
                _talkField = new GUIStyle(GUI.skin.textField) { fontSize = 19, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(0, 0, 0, 0), wordWrap = false, clipping = TextClipping.Clip };
                foreach (var st in new[] { _talkField.normal, _talkField.focused, _talkField.hover, _talkField.active }) { st.background = null; st.textColor = UiSkin.KitInk; }
            }
            bool empty = string.IsNullOrEmpty(_talkInput);
            if (empty && GUI.GetNameOfFocusedControl() != TALK_FIELD) UiKit.FitLabel(ir, "Type to talk...", o.Size, TextAnchor.MiddleLeft, UiSkin.KitInkDim, false, 12);
            GUI.SetNextControlName(TALK_FIELD);
            string t = GUI.TextField(ir, _talkInput ?? "", SurvivorMind.SAY_MAX, _talkField);
            if (t != _talkInput) _talkInput = t.Any(ch => ch < 32 || ch > 126) ? SurvivorMind.SanitizeTyped(t) : t;   // no emoji / control glyphs
        }

        /// <summary>Long input: show the end of it (what you're typing now), not the start.</summary>
        string TalkTail(string t, float width, int size)
        {
            if (string.IsNullOrEmpty(t) || TextW(t, size, false) <= width) return t;
            int cut = 0;
            while (cut < t.Length - 1 && TextW("..." + t.Substring(cut), size, false) > width) cut += 2;
            return "..." + t.Substring(Math.Min(cut, t.Length - 1));
        }

        /// <summary>Tap on the text box (touch): a plain button press over its rect, no visuals.</summary>
        bool TalkBoxTapped(Rect r)
        {
            var e = Event.current;
            if (e == null) return false;
            if (e.type == EventType.MouseUp && r.Contains(e.mousePosition)) { e.Use(); _b.PlayClick(); return true; }
            return false;
        }
    }
#endif
}
