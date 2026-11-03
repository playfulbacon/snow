namespace Snowfield.Player
{
    /// <summary>
    /// Every input the game binds, named once. This is the single place that knows how a control is *written* —
    /// "Shift + LMB", "Scroll", "Tab" — so the HUD prompt, the pause menu's controls page and anything else that
    /// tells the player about a control all spell it the same way and all go stale together or not at all.
    ///
    /// Before this existed the same facts were written down in three places (the prompt labels, the HUD's hint
    /// string, the menu's printed table) and all three had drifted away from the code.
    /// </summary>
    public enum InputGlyph
    {
        None = 0,
        LeftMouse,
        RightMouse,
        ShiftLeftMouse,
        Scroll,
        ShiftScroll,
        Tab,
        Escape,
        Mute,
        SaveKey,
        ReloadKey,
        SessionChord,
        Move,
        Look,
        Run,
        Jump,
        Stances,
    }

    /// <summary>How a glyph is drawn: a mouse picture where there is one, otherwise a key cap with text in it.</summary>
    public enum GlyphArt
    {
        Key = 0,
        LeftMouse,
        RightMouse,
        Scroll,
    }

    public static class InputGlyphInfo
    {
        /// <summary>The whole control, written out: "Shift + LMB". Used by the pause menu's controls list.</summary>
        public static string Name(InputGlyph g)
        {
            string mod = Modifier(g);
            string key = Face(g);
            return mod.Length == 0 ? key : mod + " + " + key;
        }

        /// <summary>The modifier held alongside, or "" for most inputs. Drawn as its own key cap on a prompt.</summary>
        public static string Modifier(InputGlyph g) => g switch
        {
            InputGlyph.ShiftLeftMouse => "Shift",
            InputGlyph.ShiftScroll => "Shift",
            InputGlyph.SessionChord => "Shift",
            _ => "",
        };

        /// <summary>What is written on the key cap (ignored when <see cref="Art"/> supplies a picture).</summary>
        public static string Face(InputGlyph g) => g switch
        {
            InputGlyph.LeftMouse => "LMB",
            InputGlyph.RightMouse => "RMB",
            InputGlyph.ShiftLeftMouse => "LMB",
            InputGlyph.Scroll => "Scroll",
            InputGlyph.ShiftScroll => "Scroll",
            InputGlyph.Tab => "Tab",
            InputGlyph.Escape => "Esc",
            InputGlyph.Mute => "M",
            InputGlyph.SaveKey => "F5",
            InputGlyph.ReloadKey => "F9",
            InputGlyph.SessionChord => "N",
            InputGlyph.Move => "WASD",
            InputGlyph.Look => "Mouse",
            InputGlyph.Run => "Shift",
            InputGlyph.Jump => "Space",
            InputGlyph.Stances => "Q / E",
            _ => "",
        };

        /// <summary>
        /// Which picture stands in for the key cap. A mouse button gets the mouse sprite when the HUD has one and
        /// falls back to its text face when it does not — the old three-slot prompt drew nothing at all for the
        /// slot whose sprite was unassigned, which is how a bound action ended up on screen with no key on it.
        /// </summary>
        public static GlyphArt Art(InputGlyph g) => g switch
        {
            InputGlyph.LeftMouse => GlyphArt.LeftMouse,
            InputGlyph.ShiftLeftMouse => GlyphArt.LeftMouse,
            InputGlyph.RightMouse => GlyphArt.RightMouse,
            InputGlyph.Scroll => GlyphArt.Scroll,
            InputGlyph.ShiftScroll => GlyphArt.Scroll,
            _ => GlyphArt.Key,
        };
    }
}
