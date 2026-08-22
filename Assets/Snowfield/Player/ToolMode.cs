namespace Snowfield.Player
{
    /// <summary>Top-level interaction modes. Order = number key and cycle order.</summary>
    public enum ToolMode
    {
        Snow = 0,
        EmptyHand = 1,
        Accessory = 2,
    }

    public static class ToolModeInfo
    {
        public static readonly ToolMode[] All = { ToolMode.Snow, ToolMode.EmptyHand, ToolMode.Accessory };

        public static string DisplayName(ToolMode m) => m switch
        {
            ToolMode.Snow => "Snow",
            ToolMode.EmptyHand => "Empty Hand",
            ToolMode.Accessory => "Accessory",
            _ => m.ToString(),
        };

        public static string Hint(ToolMode m) => m switch
        {
            ToolMode.Snow => "LMB add · RMB carve · scroll size",
            ToolMode.EmptyHand => "LMB smooth · scroll size",
            ToolMode.Accessory => "scroll pick · LMB place · RMB remove",
            _ => "",
        };
    }
}
