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
            ToolMode.Hand => "LMB scoop snow into your hands (sculpture: a chunk · ground: a handful) · carrying: LMB lets go, hold to throw · Shift+LMB smooth · a carried ball rolls while your cursor is near the ground · Tab accessories",
            ToolMode.Accessory => "scroll pick · LMB place · RMB remove · Tab close",
            _ => "",
        };
    }
}
