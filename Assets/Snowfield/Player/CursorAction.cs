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
        ScoopAcross,
        PickUpSnow,
        TooHeavy,
        Drop,
        SliceSnow,
        RotateSlice,
        BrushRadius,
        PickAccessory,
        OpenAccessories,
        CloseAccessories,
        CollectItem,
        StackFull,
        FindItems,
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
            CursorAction.ScoopAcross => "Scoop across the snow",
            CursorAction.PickUpSnow => "Pick it up",
            // Not an action: it says why the key is on screen and doing nothing, the way StackFull does. With
            // another player's arms on the same snow the capacity doubles, so this is a "not yet", not a "never".
            CursorAction.TooHeavy => "Too heavy to lift alone",
            CursorAction.Drop => "Let go",
            CursorAction.SliceSnow => "Slice",
            CursorAction.RotateSlice => "Turn the cut",
            CursorAction.BrushRadius => "Brush size",
            CursorAction.PickAccessory => "Pick",
            // The accessory overlay is the inventory now, so the prompts call it that: the mode name on screen
            // comes from ToolModeInfo.DisplayName and the two must not disagree.
            CursorAction.OpenAccessories => "Inventory",
            CursorAction.CloseAccessories => "Close inventory",
            CursorAction.CollectItem => "Pick up item",
            // Not actions: both say why the click is not on offer. They carry an input anyway so the row reads
            // like the rest of the stack, and the one that would have done it is the honest one to show.
            CursorAction.StackFull => "Inventory full",
            CursorAction.FindItems => "Find items to collect",
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
