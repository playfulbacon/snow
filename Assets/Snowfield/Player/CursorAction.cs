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
        AddPoint,
        UsePoint,
        FinishChain,
        ScoopSnow,
        MakeMound,
        Grab,
        Drop,
        CollectItem,
        StackFull,
        FindItems,
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
            CursorAction.AddPoint => "Add point",
            CursorAction.UsePoint => "Use this point",
            CursorAction.FinishChain => "Finish strand",
            CursorAction.ScoopSnow => "Scoop",
            CursorAction.MakeMound => "Start mound",
            CursorAction.Grab => "Pick up",
            CursorAction.Drop => "Drop (hold: throw)",
            CursorAction.CollectItem => "Pick up item",
            CursorAction.StackFull => "Inventory full",
            CursorAction.FindItems => "Find items to collect",
            _ => "",
        };
    }
}
