using UnityEngine;

namespace Snowfield.Config
{
    /// <summary>All tunables for the sculpting feel. Edit in play mode; values are read every tick.</summary>
    [CreateAssetMenu(menuName = "Snowfield/Sculpt Feel Config", fileName = "SculptFeelConfig")]
    public class SculptFeelConfig : ScriptableObject
    {
        [Header("Grid")]
        [Tooltip("Voxels per axis of a sculpture grid. Must be a multiple of 16.")]
        public int gridSize = 96;
        [Tooltip("World metres per voxel.")]
        public float voxelSize = 0.04f;

        [Header("Add brush")]
        [Tooltip("Brush radius in metres.")]
        public float addRadius = 0.25f;
        [Tooltip("Density added per tick at the brush core (0-255). This IS the packing feel.")]
        [Range(1f, 64f)] public float addRatePerTick = 10f;
        [Tooltip("Normalised distance where falloff starts (1 = edge, 0 = centre). Inside this the brush is full strength.")]
        [Range(0f, 1f)] public float addShoulder = 0.6f;
        [Tooltip("Brush ticks per second while held.")]
        [Range(10f, 120f)] public float ticksPerSecond = 60f;

        [Header("Smooth brush")]
        public float smoothRadius = 0.3f;
        [Range(0f, 1f)] public float smoothStrength = 0.35f;
        [Range(0f, 1f)] public float smoothShoulder = 0.5f;

        [Header("Remesh")]
        [Tooltip("Remesh dirty chunks at this rate while sculpting (Hz).")]
        [Range(1f, 60f)] public float remeshHz = 10f;

        [Header("Snowball")]
        public float snowballStartRadius = 0.15f;
        public float snowballMaxRadius = 0.6f;
        [Tooltip("Radius gained per metre rolled over fresh snow.")]
        public float snowballGrowthPerMetre = 0.04f;
        [Tooltip("Radius of a freshly scooped handful of snow (m).")]
        public float scoopRadius = 0.12f;
        [Tooltip("Depth of the divot a scoop leaves in the field (m).")]
        public float scoopDivotDepth = 0.04f;
        [Tooltip("Voxels per axis for a loose snowball's own grid (multiple of 16). Must hold the max diameter plus brush room.")]
        public int snowballGridSize = 48;
        [Tooltip("Auto-regrow ceiling: a sculpture's grid grows on demand up to this many voxels per axis (multiple of 16).")]
        public int maxGridSize = 192;
        [Tooltip("Free voxels the brush wants between its edge and the grid wall before a regrow is triggered.")]
        public int regrowMarginVoxels = 4;

        [Header("Terrain (heightmap field)")]
        [Tooltip("Field side length in metres.")]
        public float terrainFieldSize = 40f;
        [Tooltip("Heightmap cell size in metres.")]
        public float terrainCellSize = 0.05f;
        [Tooltip("Cells per chunk side. Field is rounded up to whole chunks.")]
        public int terrainChunkCells = 200;
        [Tooltip("Deepest the brush can carve below the untouched surface (m).")]
        public float terrainMaxCarveDepth = 0.6f;
        [Tooltip("Highest the brush can raise above the untouched surface (m).")]
        public float terrainMaxRaise = 0.8f;
        [Tooltip("Metres added per tick by the Sculpt brush on the ground (carve uses the negative).")]
        public float terrainAddPerTick = 0.004f;
        [Tooltip("Remesh dirty ground chunks at this rate (Hz).")]
        [Range(1f, 60f)] public float terrainRemeshHz = 10f;
        [Tooltip("Re-cook ground colliders at this rate (Hz). You walk on these, so not only on brush release.")]
        [Range(0.5f, 20f)] public float terrainColliderHz = 2f;

        [Header("Paths")]
        public float footprintRadius = 0.12f;
        public float footprintDepth = 0.02f;
        [Tooltip("Metres walked between footprints.")]
        public float footstepSpacing = 0.45f;
        [Tooltip("Trench depth under a rolling snowball as a fraction of its radius.")]
        [Range(0f, 1f)] public float rollTrenchDepthFraction = 0.25f;
        [Tooltip("Footprints and trenches cannot pack the snow deeper than this (m). Carving can.")]
        public float terrainPathDepthCap = 0.12f;

        [Header("Hands")]
        [Tooltip("Snow at least this big (radius, m) takes both hands; anything smaller sits in one palm.")]
        public float handTwoHandedRadius = 0.22f;
        [Tooltip("How many times its natural length an arm may stretch to reach the snow. 1 disables stretching.")]
        [Range(1f, 6f)] public float handMaxStretch = 3.5f;
        [Tooltip("Spring stiffness pulling a hand onto its target. Higher snaps to it, lower lets the arm trail.")]
        public float handSpringStiffness = 260f;
        [Tooltip("Spring damping. Below 2*sqrt(stiffness) (~32) the hand overshoots and wobbles on arrival.")]
        public float handSpringDamping = 22f;
        [Tooltip("How far a free hand drifts toward snow it could scoop, as a ready pose. 0 disables it.")]
        [Range(0f, 1f)] public float handHoverWeight = 0.3f;
        [Tooltip("Seconds a hand stays on snow it just let go of (fuse, drop, throw, accessory).")]
        public float handFollowThrough = 0.18f;
        [Tooltip("How far a patting hand bobs off the surface (m).")]
        public float handPatAmplitude = 0.05f;
        [Tooltip("Pats per second while smoothing.")]
        public float handPatRate = 5f;

        [Header("Snowfall")]
        [Tooltip("Hours of snowfall to refill a full-depth path. 0 disables recovery.")]
        public float snowfallRecoverHours = 2f;
        [Tooltip("How often the recovery pass runs (s).")]
        public float snowfallTickSeconds = 2f;
        [Tooltip("Recovery speed while holding the debug 'let it snow' key (m/s).")]
        public float letItSnowPerSecond = 0.05f;
    }
}
