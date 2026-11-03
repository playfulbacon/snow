namespace Snowfield.Player
{
    /// <summary>
    /// What the HUD is allowed to know about networking. The net layer writes it; <see cref="ToolHud"/> reads it.
    /// The dependency runs this way round on purpose — the same inversion <see cref="SnowGround"/> uses — so the
    /// HUD never depends on the networking assembly, or on there being one at all.
    /// </summary>
    public static class NetStatus
    {
        /// <summary>Empty means "say nothing": single-player shows no networking chrome.</summary>
        public static string Line = "";

        /// <summary>In a shared field. The HUD only offers the mic control while this is true.</summary>
        public static bool InSession;

        /// <summary>Mic state, so the hint can offer the action you do not currently have.</summary>
        public static bool MicMuted;
    }
}
