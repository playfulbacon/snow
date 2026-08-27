namespace Snowfield.Player
{
    /// <summary>
    /// One line of networking state for the HUD to show. The net layer writes it; <see cref="ToolHud"/> reads it.
    /// The dependency runs this way round on purpose — the same inversion <see cref="SnowGround"/> uses — so the
    /// HUD never depends on the networking assembly, or on there being one at all.
    /// </summary>
    public static class NetStatus
    {
        /// <summary>Empty means "say nothing": single-player shows no networking chrome.</summary>
        public static string Line = "";
    }
}
