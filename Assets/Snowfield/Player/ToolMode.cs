namespace Snowfield.Player
{
    /// <summary>
    /// The only persistent state is Hand; Accessory is a Tab overlay you dip into to place/remove decorations.
    /// </summary>
    public enum ToolMode
    {
        Hand = 0,
        Accessory = 1,
    }

    public static class ToolModeInfo
    {
        public static string DisplayName(ToolMode m) => m == ToolMode.Accessory ? "Accessories" : "Hand";

        public static string Hint(ToolMode m) => m switch
        {
            ToolMode.Hand => "LMB add · RMB carve (ground: scoop) · Shift+LMB smooth · F pick up/drop (hold: throw) · Shift while holding a ball: roll · Tab accessories",
            ToolMode.Accessory => "scroll pick · LMB place · RMB remove · Tab close",
            _ => "",
        };
    }
}
