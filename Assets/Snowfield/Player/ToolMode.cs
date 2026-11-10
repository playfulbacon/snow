namespace Snowfield.Player
{
    /// <summary>
    /// Empty hands or an equipped accessory. Inventory visibility is independent of the held item.
    /// </summary>
    public enum ToolMode
    {
        Hand = 0,
        Accessory = 1,
    }

    public static class ToolModeInfo
    {
        public static string DisplayName(ToolMode m) => m == ToolMode.Accessory ? "Item" : "Hand";

        /// <summary>
        /// What Tab does from here. There is deliberately no prose list of the mode's controls any more: the
        /// controls are <see cref="SculptTool.Actions"/>, drawn as prompts, and a second hand-written copy of
        /// them in a hint string is what went stale last time.
        /// </summary>
        public static CursorAction TabAction(bool inventoryOpen, ToolMode mode) =>
            inventoryOpen ? CursorAction.CloseAccessories
            : mode == ToolMode.Accessory ? CursorAction.PutAwayItem : CursorAction.OpenAccessories;
    }
}
