namespace Snowfield.Player
{
    /// <summary>Every context action the cursor can prompt for. The HUD maps each to an icon + label. Append only: stored prompt rows key off the int.</summary>
    public enum CursorAction
    {
        None = 0,
        AddSnow,
        Carve,
        Smooth,
        SetDownSnowball,
        AttachSnowball,
        Throw,
        RetrieveAccessory,
        PlaceAccessory,
        ScoopSnow,
        MakeMound,
        Grab,
        Drop,
        Shave,
        Score,
        Squeeze,
        CarveInHand,
    }

    public static class CursorActionInfo
    {
        public static string DefaultLabel(CursorAction a) => a switch
        {
            CursorAction.AddSnow => "Add snow",
            CursorAction.Carve => "Carve",
            CursorAction.Smooth => "Smooth",
            CursorAction.SetDownSnowball => "Set down",
            CursorAction.AttachSnowball => "Attach",
            CursorAction.Throw => "Throw",
            CursorAction.RetrieveAccessory => "Take back",
            CursorAction.PlaceAccessory => "Place",
            CursorAction.ScoopSnow => "Scoop",
            CursorAction.MakeMound => "Start mound",
            CursorAction.Grab => "Pick up",
            CursorAction.Drop => "Drop (hold: throw)",
            CursorAction.Shave => "Shave (Shift: score)",
            CursorAction.Score => "Score",
            CursorAction.Squeeze => "Squeeze (hold)",
            CursorAction.CarveInHand => "Carve in hand (hold)",
            _ => "",
        };
    }
}
