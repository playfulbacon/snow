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

        /// <summary>
        /// What Tab does from here. There is deliberately no prose list of the mode's controls any more: the
        /// controls are <see cref="SculptTool.Actions"/>, drawn as prompts, and a second hand-written copy of
        /// them in a hint string is what went stale last time.
        /// </summary>
        public static CursorAction TabAction(ToolMode m) =>
            m == ToolMode.Accessory ? CursorAction.CloseAccessories : CursorAction.OpenAccessories;
    }
}
