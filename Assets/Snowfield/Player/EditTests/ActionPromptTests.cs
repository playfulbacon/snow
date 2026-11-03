using System;
using System.Collections.Generic;
using NUnit.Framework;
using Snowfield.Player;

namespace Snowfield.Player.Tests
{
    /// <summary>
    /// Guards the one rule the prompt system rests on: a label says what an action does, a glyph says what
    /// performs it, and neither says the other's job. The bug these replace was three hand-written copies of the
    /// control scheme — the prompt labels, the HUD's hint string and the menu's printed table — disagreeing with
    /// each other and with the code.
    /// </summary>
    public class ActionPromptTests
    {
        static IEnumerable<CursorAction> Actions()
        {
            foreach (CursorAction a in Enum.GetValues(typeof(CursorAction)))
                if (a != CursorAction.None) yield return a;
        }

        static IEnumerable<InputGlyph> Glyphs()
        {
            foreach (InputGlyph g in Enum.GetValues(typeof(InputGlyph)))
                if (g != InputGlyph.None) yield return g;
        }

        [Test]
        public void EveryActionHasALabel()
        {
            foreach (var a in Actions())
                Assert.IsNotEmpty(CursorActionInfo.DefaultLabel(a), $"{a} has no label, so its prompt would be blank.");
        }

        /// <summary>
        /// No label names a key. "Finish (Shift)" and "Drop (hold: throw)" used to, while the icon beside them
        /// claimed a different button — the label and the picture contradicting each other in one prompt.
        /// </summary>
        [Test]
        public void NoLabelNamesAKey()
        {
            string[] keyish = { "shift", "lmb", "rmb", "ctrl", "alt", "tab", "esc", "f5", "f9", "click", "mouse", "hold:" };
            foreach (var a in Actions())
            {
                string label = CursorActionInfo.DefaultLabel(a).ToLowerInvariant();
                foreach (var word in keyish)
                    Assert.IsFalse(label.Contains(word),
                        $"{a}'s label \"{CursorActionInfo.DefaultLabel(a)}\" names a key; the glyph does that.");
            }
        }

        [Test]
        public void EveryGlyphHasAFace()
        {
            foreach (var g in Glyphs())
                Assert.IsNotEmpty(InputGlyphInfo.Face(g), $"{g} has nothing written on it, so its key cap would be empty.");
        }

        [Test]
        public void ModifiedGlyphsReadAsACombination()
        {
            Assert.AreEqual("Shift + LMB", InputGlyphInfo.Name(InputGlyph.ShiftLeftMouse));
            Assert.AreEqual("Shift + Scroll", InputGlyphInfo.Name(InputGlyph.ShiftScroll));
            Assert.AreEqual("Tab", InputGlyphInfo.Name(InputGlyph.Tab));
        }

        /// <summary>
        /// Only the mouse has pictures today. Anything else has to fall through to a key cap, because the old
        /// HUD drew nothing at all for a prompt whose sprite was unassigned.
        /// </summary>
        [Test]
        public void OnlyMouseInputsClaimAPicture()
        {
            foreach (var g in Glyphs())
            {
                var art = InputGlyphInfo.Art(g);
                bool mousey = g == InputGlyph.LeftMouse || g == InputGlyph.RightMouse
                              || g == InputGlyph.ShiftLeftMouse || g == InputGlyph.Scroll
                              || g == InputGlyph.ShiftScroll;
                Assert.AreEqual(mousey, art != GlyphArt.Key, $"{g} draws {art}.");
            }
        }

        [Test]
        public void ControlsListIsCompleteAndWithoutRepeats()
        {
            var seen = new HashSet<InputGlyph>();
            int count = 0;
            foreach (var b in Controls.All)
            {
                Assert.IsNotEmpty(b.Key, "A binding with no key cannot be printed.");
                Assert.IsNotEmpty(b.What, $"{b.Key} is bound to nothing legible.");
                Assert.IsTrue(seen.Add(b.Input), $"{b.Input} is listed twice; the controls page would repeat it.");
                count++;
            }
            Assert.Greater(count, 10, "The controls page lost most of the scheme.");
        }

        [Test]
        public void MovementLineNamesTheMovementKeys()
        {
            string line = Controls.MovementLine;
            foreach (var b in Controls.Movement)
                StringAssert.Contains(b.Key, line);
        }

        [Test]
        public void AnEmptyPromptIsNotSomething()
        {
            Assert.IsFalse(default(ActionPrompt).IsSomething);
            Assert.IsFalse(new ActionPrompt(CursorAction.Smooth, InputGlyph.None).IsSomething);
            Assert.IsFalse(new ActionPrompt(CursorAction.None, InputGlyph.LeftMouse).IsSomething);
            Assert.IsTrue(new ActionPrompt(CursorAction.Smooth, InputGlyph.RightMouse).IsSomething);
        }
    }
}
