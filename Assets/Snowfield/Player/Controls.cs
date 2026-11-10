using System.Collections.Generic;
using System.Text;

namespace Snowfield.Player
{
    /// <summary>
    /// The control scheme written down once. Anything that tells the player what the keys are reads this: the
    /// pause menu's controls page, the HUD's movement line, and anything added later.
    ///
    /// The sculpting rows take their wording from <see cref="CursorActionInfo"/>, the same table the on-screen
    /// prompts use, so a renamed action is renamed everywhere. The spellings of the inputs come from
    /// <see cref="InputGlyphInfo"/>, for the same reason. What is left here is only the mapping — which, as the
    /// one copy of it, is the thing to keep honest when a binding changes in
    /// <see cref="SculptTool"/> or <c>PlayerController</c>.
    /// </summary>
    public static class Controls
    {
        public readonly struct Binding
        {
            public readonly InputGlyph Input;
            public readonly string What;

            public Binding(InputGlyph input, string what) { Input = input; What = what; }

            /// <summary>"Shift + LMB" — how the key is written, from the one place that knows.</summary>
            public string Key => InputGlyphInfo.Name(Input);
        }

        static string Label(CursorAction a) => CursorActionInfo.DefaultLabel(a);

        /// <summary>Getting about. Owned by <c>SnowDays.PlayerController</c>.</summary>
        public static readonly Binding[] Movement =
        {
            new Binding(InputGlyph.Move, "Move"),
            new Binding(InputGlyph.Look, "Look"),
            new Binding(InputGlyph.Run, "Run"),
            new Binding(InputGlyph.Jump, "Jump"),
        };

        /// <summary>The hands. Owned by <see cref="SculptTool"/>; each row mirrors a branch of its action table.</summary>
        public static readonly Binding[] Sculpting =
        {
            new Binding(InputGlyph.LeftMouse,
                        Label(CursorAction.ScoopSnow) + " (hold to "
                        + Label(CursorAction.ScoopAcross).ToLowerInvariant() + ") · "
                        + Label(CursorAction.Drop) + " · hold to " + Label(CursorAction.Throw).ToLowerInvariant()),
            new Binding(InputGlyph.RightMouse, Label(CursorAction.Smooth) + " and pat"),
            new Binding(InputGlyph.PickUp, "Pick up a sculpture or a loose ball and carry it"),
            new Binding(InputGlyph.ShiftLeftMouse, Label(CursorAction.SliceSnow) + " a sculpture"),
            new Binding(InputGlyph.Scroll, Label(CursorAction.BrushRadius) + " · " + Label(CursorAction.PickAccessory)
                        + " in the overlay"),
            new Binding(InputGlyph.ShiftScroll, Label(CursorAction.RotateSlice)),
        };

        /// <summary>Everything that is about the game rather than the snow.</summary>
        public static readonly Binding[] Game =
        {
            new Binding(InputGlyph.Tab, Label(CursorAction.OpenAccessories)),
            new Binding(InputGlyph.Escape, Label(CursorAction.OpenMenu)),
            new Binding(InputGlyph.SessionChord, "Join or leave the shared field"),
            new Binding(InputGlyph.Mute, "Mute the microphone"),
            new Binding(InputGlyph.SaveKey, "Save the field"),
            new Binding(InputGlyph.ReloadKey, "Reload the field"),
        };

        /// <summary>Every binding, in the order a controls page should read them.</summary>
        public static IEnumerable<Binding> All
        {
            get
            {
                foreach (var b in Movement) yield return b;
                foreach (var b in Sculpting) yield return b;
                foreach (var b in Game) yield return b;
            }
        }

        /// <summary>The movement keys as one line, for the HUD's dev status readout.</summary>
        public static string MovementLine
        {
            get
            {
                if (_movementLine != null) return _movementLine;
                var sb = new StringBuilder();
                foreach (var b in Movement)
                {
                    if (sb.Length > 0) sb.Append(" · ");
                    sb.Append(b.Key).Append(' ').Append(b.What.ToLowerInvariant());
                }
                return _movementLine = sb.ToString();
            }
        }

        static string _movementLine;
    }
}
