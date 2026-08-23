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
        PickUpItem,
        RetrieveAccessory,
        PlaceAccessory,
        StackSnowball,
    }

    public static class CursorActionInfo
    {
        public static string DefaultLabel(CursorAction a) => a switch
        {
            CursorAction.AddSnow => "Add snow",
            CursorAction.Carve => "Carve",
            CursorAction.Smooth => "Smooth",
            CursorAction.StartSnowball => "Start snowball",
            CursorAction.PushSnowball => "Push",
            CursorAction.PickUpSnowball => "Pick up",
            CursorAction.SetDownSnowball => "Set down",
            CursorAction.AttachSnowball => "Attach",
            CursorAction.Throw => "Throw (hold)",
            CursorAction.PickUpItem => "Pick up",
            CursorAction.RetrieveAccessory => "Take back",
            CursorAction.PlaceAccessory => "Place",
            CursorAction.StackSnowball => "Stack",
            _ => "",
        };
    }
}
