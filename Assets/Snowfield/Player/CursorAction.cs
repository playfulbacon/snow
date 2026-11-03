namespace Snowfield.Player
{
    /// <summary>
    /// Every action the game can offer the player, contextual or persistent. The HUD maps each to an icon and a
    /// label; <see cref="ActionPrompt"/> pairs one with the input that performs it.
    ///
    /// Labels never contain a key name. The key comes from the prompt's <see cref="InputGlyph"/> and is drawn, so
    /// "Finish (Shift)" and "Drop (hold: throw)" — labels that smuggled a binding into the text while the icon
    /// beside them claimed a different button — are gone.
    /// </summary>
    public enum CursorAction
    {
        None = 0,
        Smooth,
        AttachSnowball,
        Throw,
        RetrieveAccessory,
        PlaceAccessory,
        AddPoint,
        UsePoint,
        FinishChain,
        ScoopSnow,
        Drop,
        SliceSnow,
        RotateSlice,
        BrushRadius,
        PickAccessory,
        OpenAccessories,
        CloseAccessories,
        OpenMenu,
        MuteMic,
        UnmuteMic,
    }

    public static class CursorActionInfo
    {
        public static string DefaultLabel(CursorAction a) => a switch
        {
            CursorAction.Smooth => "Smooth",
            CursorAction.AttachSnowball => "Attach",
            CursorAction.Throw => "Throw",
            CursorAction.RetrieveAccessory => "Take back",
            CursorAction.PlaceAccessory => "Place",
            CursorAction.AddPoint => "Add point",
            CursorAction.UsePoint => "Use this point",
            CursorAction.FinishChain => "Finish the strand",
            CursorAction.ScoopSnow => "Scoop",
            CursorAction.Drop => "Let go",
            CursorAction.SliceSnow => "Slice",
            CursorAction.RotateSlice => "Turn the cut",
            CursorAction.BrushRadius => "Brush size",
            CursorAction.PickAccessory => "Pick",
            CursorAction.OpenAccessories => "Accessories",
            CursorAction.CloseAccessories => "Close accessories",
            CursorAction.OpenMenu => "Menu",
            CursorAction.MuteMic => "Mute mic",
            CursorAction.UnmuteMic => "Unmute mic",
            _ => "",
        };
    }

    /// <summary>
    /// One thing the player can do right now, and what does it. The unit every on-screen prompt is built from:
    /// the reticle stack, the persistent corner hints and the pause menu's controls page all render these, so
    /// there is one answer to "what can I do" rather than one per surface.
    /// </summary>
    public readonly struct ActionPrompt
    {
        public readonly CursorAction Action;
        public readonly InputGlyph Input;
        /// <summary>The input is held rather than tapped; the prompt says so.</summary>
        public readonly bool Hold;

        public ActionPrompt(CursorAction action, InputGlyph input, bool hold = false)
        {
            Action = action;
            Input = input;
            Hold = hold;
        }

        public bool IsSomething => Action != CursorAction.None && Input != InputGlyph.None;
    }
}
