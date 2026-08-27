using UnityEngine;

namespace Snowfield.Player
{
    /// <summary>
    /// The ground-snow surface the sculpting tools talk to. The heightmap SnowTerrain this replaced lived in its
    /// own assembly; the SnowDays surface (SnowDeformSystem) lives in Assembly-CSharp, which asmdefs cannot
    /// reference — so the backend registers itself here at runtime (see SnowDeformGroundAdapter in
    /// Assets/SnowDeform). All positions are world-space metres.
    /// </summary>
    public interface ISnowGroundBackend
    {
        /// <summary>Ready to sample (the deformation window around the player is filled).</summary>
        bool IsCreated { get; }

        /// <summary>World Y of the visible snow surface at this XZ (undisturbed cover included).</summary>
        float SampleHeight(Vector3 world);

        /// <summary>True while the snow here is no more than <paramref name="tolerance"/> metres below fresh.</summary>
        bool IsFreshAt(Vector3 world, float tolerance);

        /// <summary>
        /// Press a round depression into the surface: flat-bottomed inside <paramref name="shoulder"/> (0..1 of the
        /// radius), falling off to nothing at the edge. Scoops, footprints and snowball trenches all come through here.
        /// </summary>
        void StampDepression(Vector3 worldCenter, float radiusMetres, float depthMetres, float shoulder);
    }

    /// <summary>Static access point, mirroring the old SnowTerrain.Instance call sites. May be null.</summary>
    public static class SnowGround
    {
        public static ISnowGroundBackend Instance;
    }
}
