namespace Snowfield.Player
{
    /// <summary>Every context action the cursor can prompt for. The HUD maps each to an icon + label.</summary>
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
        Grab,
        Drop,
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
            CursorAction.Grab => "Pick up",
            CursorAction.Drop => "Drop (hold: throw)",
            _ => "",
        };
    }
}
