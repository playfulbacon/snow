namespace Snowfield.Net
{
    /// <summary>
    /// What the Start menu asked for, read once by <see cref="NetBootstrap"/> when the game scene loads.
    ///
    /// The menu cannot host or join by itself: the NetworkManager lives in the game scene, and NGO scene
    /// management is off because every peer is expected to already be standing in that scene when it connects.
    /// So the menu records an intent, loads the scene, and the bootstrap acts on it there. Static because it has
    /// to survive the scene load, and cleared on read so a second visit to the menu starts from nothing.
    /// </summary>
    public static class NetLaunch
    {
        public enum Mode { Offline, Host, Join }

        public static Mode Requested;
        public static string Code = "";

        public static void Clear()
        {
            Requested = Mode.Offline;
            Code = "";
        }

        public static void RequestHost()
        {
            Requested = Mode.Host;
            Code = "";
        }

        public static void RequestJoin(string code)
        {
            Requested = Mode.Join;
            Code = (code ?? "").Trim();
        }
    }
}
