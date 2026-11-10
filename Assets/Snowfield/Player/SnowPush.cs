using System;
using Snowfield.Sculpture;
using UnityEngine;

namespace Snowfield.Player
{
    /// <summary>
    /// Shoving snow about, and what it costs you. Two facts live here because two assemblies that cannot see
    /// each other both need them (the SnowGround / NetAvatarHooks inversion pattern again):
    ///
    ///   • <see cref="SpeedLimit"/> — how fast the body may move this frame. <see cref="SnowballRoller"/> writes
    ///     it while you have your hands on a ball; the character controller (a SnowDays type in
    ///     Assembly-CSharp, which Snowfield.* may not reference) reads it and caps its own target speed. This is
    ///     the whole of "there is no size cap any more": a bigger ball is heavier, a heavier ball is slower, and
    ///     past a certain size you simply cannot get it moving at a useful pace on your own.
    ///
    ///   • <see cref="RemoteHelpers"/> — how many OTHER players have their hands on the same ball. The net layer
    ///     (Snowfield.Net, which references this assembly, so the dependency has to point this way) installs it
    ///     from the pushing stream; offline it is null and everyone is on their own. Strength adds up, so two
    ///     players roll a boulder neither could budge.
    ///
    /// Every pusher of a given ball derives the same limit from the same numbers — mass, power, head count — so
    /// nobody has to be authoritative about how fast it is going.
    /// </summary>
    public static class SnowPush
    {
        /// <summary>Top horizontal speed the body may manage (m/s). Infinity when nothing is holding it back.</summary>
        public static float SpeedLimit { get; private set; } = float.PositiveInfinity;

        /// <summary>What the local player has their hands on, or null. Read by the net layer to tell the others.</summary>
        public static SnowSculpture Pushing { get; private set; }

        /// <summary>
        /// Installed by the net layer: how many players other than this one are pushing a given object.
        /// Null (offline, or before a session) means nobody.
        /// </summary>
        public static Func<SnowSculpture, int> RemoteHelpers;

        /// <summary>Arms on this object, counting your own. Always at least 1, so it is safe to divide by.</summary>
        public static int Arms(SnowSculpture s)
        {
            if (s == null || RemoteHelpers == null) return 1;
            return 1 + Mathf.Max(0, RemoteHelpers(s));
        }

        /// <summary>
        /// Top speed <paramref name="massKg"/> of snow can be rolled at by <paramref name="arms"/> players.
        /// A flat power budget divided by the weight: doubling the mass halves the speed, and an extra pair of
        /// arms doubles it back. <paramref name="minSpeed"/> is a floor rather than a stall — the same limit caps
        /// how fast the pusher can walk, and a limit of zero would weld them to the spot with no way out.
        /// </summary>
        public static float SpeedFor(float massKg, int arms, float power, float minSpeed) =>
            Mathf.Max(minSpeed, power * Mathf.Max(1, arms) / Mathf.Max(1f, massKg));

        /// <summary>Hands on <paramref name="pushing"/>, and this is as fast as it will go.</summary>
        public static void Set(SnowSculpture pushing, float speedLimit)
        {
            Pushing = pushing;
            SpeedLimit = Mathf.Max(0.01f, speedLimit);
        }

        /// <summary>Hands free; nothing is slowing the player down.</summary>
        public static void Clear()
        {
            Pushing = null;
            SpeedLimit = float.PositiveInfinity;
        }

        /// <summary>
        /// Static state outlives a play session when domain reload is off: a limit left behind by the last
        /// session would silently cap the player's speed on the next one, with nothing on screen to explain it.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Boot()
        {
            RemoteHelpers = null;
            Clear();
        }
    }
}
