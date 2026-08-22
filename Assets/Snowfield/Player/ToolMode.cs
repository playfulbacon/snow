namespace Snowfield.Player
{
    /// <summary>Top-level interaction modes. <see cref="ToolModeInfo.All"/> defines number-key and cycle order.</summary>
    public enum ToolMode
    {
        Sculpt = 0,
        EmptyHand = 1,
        Accessory = 2,
    }

    public static class ToolModeInfo
    {
        /// <summary>Display/cycle order: index 0 is key 1 and the starting mode.</summary>
        public static readonly ToolMode[] All = { ToolMode.EmptyHand, ToolMode.Sculpt, ToolMode.Accessory };
        public static ToolMode Default => All[0];
        public static int IndexOf(ToolMode m) => System.Array.IndexOf(All, m);

        public static string DisplayName(ToolMode m) => m switch
        {
            ToolMode.Sculpt => "Sculpt",
            ToolMode.EmptyHand => "Empty Hand",
            ToolMode.Accessory => "Accessory",
            _ => m.ToString(),
        };

        public static string Hint(ToolMode m) => m switch
        {
            ToolMode.Sculpt => "LMB add · RMB carve · scroll size",
            ToolMode.EmptyHand => "LMB on snow smooth · hold LMB push snowball (or start one on ground) · RMB pick up · carrying: LMB place/attach, hold RMB throw",
            ToolMode.Accessory => "scroll pick · LMB place · RMB remove",
            _ => "",
        };
    }
}
