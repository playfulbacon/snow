namespace Snowfield.Player
{
    /// <summary>Every context action the cursor can prompt for. The HUD maps each to an icon + label.</summary>
    public enum CursorAction
    {
        None = 0,
        AddSnow,
        Carve,
        Smooth,
        StartSnowball,
        PushSnowball,
        PickUpSnowball,
        SetDownSnowball,
        AttachSnowball,
        Throw,
        RetrieveAccessory,
        PlaceAccessory,
        PickUpSculpture,
    }

    public static class CursorActionInfo
    {
        public static string DefaultLabel(CursorAction a) => a switch
        {
            CursorAction.AddSnow => "Add snow",
            CursorAction.Carve => "Carve",
            CursorAction.Smooth => "Smooth",
            CursorAction.StartSnowball => "Start snowball",
            CursorAction.PushSnowball => "Push (hold)",
            CursorAction.PickUpSnowball => "Pick up",
            CursorAction.SetDownSnowball => "Set down",
            CursorAction.AttachSnowball => "Attach",
            CursorAction.Throw => "Throw (hold)",
            CursorAction.RetrieveAccessory => "Take back",
            CursorAction.PlaceAccessory => "Place",
            CursorAction.PickUpSculpture => "Pick up",
            _ => "",
        };
    }
}
