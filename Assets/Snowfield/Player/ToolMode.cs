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
        public static string DisplayName(ToolMode m) => m == ToolMode.Accessory ? "Inventory" : "Hand";

        public static string Hint(ToolMode m) => m switch
        {
            ToolMode.Hand => "LMB pick up items / scoop snow · carrying: LMB lets go, hold to throw · Shift+LMB smooth · a carried ball rolls near the ground · Tab inventory",
            ToolMode.Accessory => "scroll cycle items · LMB pick up / place / take back · each placement uses 1 item · Tab close",
            _ => "",
        };
    }
}
