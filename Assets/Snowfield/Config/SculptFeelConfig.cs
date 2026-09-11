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
        [Tooltip("World metres per voxel. 3 cm: a cat face is ~15 voxels across, and every threshold below is in metres so this is free to tune.")]
        public float voxelSize = 0.03f;

        [Header("Add brush")]
        [Tooltip("Brush radius in metres.")]
        public float addRadius = 0.25f;
        [Tooltip("Density added per tick at the brush core (0-255). This IS the packing feel.")]
        [Range(1f, 64f)] public float addRatePerTick = 10f;
        [Tooltip("Normalised distance where falloff starts (1 = edge, 0 = centre). Inside this the brush is full strength.")]
        [Range(0f, 1f)] public float addShoulder = 0.6f;
        [Tooltip("Brush ticks per second while held.")]
        [Range(10f, 120f)] public float ticksPerSecond = 60f;

        [Header("Smooth brush (pat = weld + finish + repair)")]
        public float smoothRadius = 0.3f;
        [Tooltip("Pat merge strength — THE first tuning target: ~3 overlapping beads should become a clean tube in 1-2 strokes, not 10.")]
        [Range(0f, 1f)] public float smoothStrength = 0.5f;
        [Range(0f, 1f)] public float smoothShoulder = 0.5f;
        [Tooltip("Blur kernel half-width in voxels (1 = 3³, 2 = 5³). Wider = beads merge in fewer strokes.")]
        [Range(1, 3)] public int smoothKernelRadius = 2;
        [Tooltip("Compaction gained per pat tick at the brush core (0-255). Moderate: the fallback for firming loose fill in place.")]
        public float patCompactionPerTick = 1.5f;

        [Header("Compaction (set by how snow arrives — never a mode)")]
        [Tooltip("Compaction of snow a rolling ball picks up. Rolling IS packing.")]
        [Range(0, 255)] public int compactionRolled = 220;
        [Tooltip("Compaction of a scooped handful / loose fill.")]
        [Range(0, 255)] public int compactionScooped = 40;
        [Tooltip("The contact shell of a fuse is raised to at least this.")]
        [Range(0, 255)] public int compactionWeld = 180;
        [Tooltip("Compaction assumed for saves/snapshots that predate the channel, and for the starter mound.")]
        [Range(0, 255)] public int compactionLegacy = 160;
        [Tooltip("Mean compaction at or above which a held ball counts as packed: RMB carves it instead of squeezing it.")]
        [Range(0, 255)] public int packedThreshold = 200;
        [Tooltip("Volume lost per full compaction span gained (0 → 255). ~1/3: packing = same snow, smaller. The one multiplier shared by pat and squeeze.")]
        [Range(0f, 0.8f)] public float packShrinkFraction = 0.33f;

        [Header("Shave (RMB drag) — feel budget goes here")]
        [Tooltip("Cut depth below the surface plane on fully packed snow (m). Thick enough to shape, thin enough not to feel like biting.")]
        public float shaveDepthPacked = 0.045f;
        [Tooltip("Depth multiplier on fresh powder: a ragged gouge, not a skin. The packed/fluffy contrast must be night-and-day.")]
        public float shaveDepthFluffyMultiplier = 3f;
        [Tooltip("Ramp under the cut floor (fraction of depth) where removal fades out.")]
        [Range(0f, 2f)] public float shaveSoftFraction = 0.5f;
        [Tooltip("Lateral falloff shoulder on packed snow: tight, so cuts terminate sharply (carved, not melted).")]
        [Range(0f, 1f)] public float shaveShoulderPacked = 0.85f;
        [Tooltip("Lateral falloff shoulder on fluffy snow: wide, no crisp termination.")]
        [Range(0f, 1f)] public float shaveShoulderFluffy = 0.25f;
        [Tooltip("Boundary noise on fluffy snow as a fraction of the brush radius (ragged edge; never reaches outside the brush).")]
        [Range(0f, 0.6f)] public float shaveNoiseFluffy = 0.3f;
        [Tooltip("After a fluffy cut, one relaxation pass at this strength rounds the cut edges off (slump).")]
        [Range(0f, 1f)] public float slumpStrength = 0.5f;
        [Tooltip("A new stamp lands every this-many brush radii of cursor travel along the surface.")]
        [Range(0.1f, 1f)] public float shaveStampSpacing = 0.35f;
        [Tooltip("Shaved-off snow sheds as loose lumps at your feet once this much has come off (m³). 0.0005 ≈ a 5 cm ball.")]
        public float shedVolume = 0.0005f;
        [Tooltip("Fluffy snow over-sheds: lumps this many times bigger and crumblier.")]
        public float shedFluffyMultiplier = 2.5f;
        [Tooltip("Score (Shift+RMB): brush radius fraction. Tiny.")]
        [Range(0.05f, 1f)] public float scoreRadiusFraction = 0.3f;
        [Tooltip("Score: depth multiplier over shave — a narrow groove along the stroke.")]
        public float scoreDepthMultiplier = 2.5f;
        [Tooltip("Small bites in packed snow swap to this tight shoulder so they leave crisp edges instead of spherical scoops.")]
        [Range(0f, 1f)] public float crispShoulder = 0.9f;
        [Tooltip("Bites at or under this radius (m) get the crisp shoulder on packed snow; it fades out by twice this.")]
        public float crispBrushRadius = 0.12f;

        [Header("Squeeze (RMB hold with a ball in hand)")]
        [Tooltip("Seconds of holding for a full squeeze.")]
        public float squeezeTime = 0.5f;
        [Tooltip("Crunch stages over the hold; each one shrinks and packs a step.")]
        [Range(1, 4)] public int squeezeStages = 3;
        [Tooltip("Volume lost by a full squeeze (from fresh powder). Lossy on purpose: squeeze trades volume for workability, bulk still wants rolling.")]
        [Range(0f, 0.6f)] public float squeezeShrink = 0.33f;
        [Tooltip("Where a packed ball is held for in-hand carving: forward of the body and height above the feet (m).")]
        public Vector2 workPose = new Vector2(0.55f, 0.72f);

        [Header("Structure (breakage)")]
        [Tooltip("Features thinner than this (m) break off in fresh powder.")]
        public float thinLimitFluffy = 0.18f;
        [Tooltip("Features thinner than this (m) break off in fully packed snow. The fluffy/packed gap should be 3-4×: packing unlocks a material.")]
        public float thinLimitPacked = 0.05f;
        [Tooltip("Density (0-255) at which voxels count as connected for the island check. Below iso so touching shoulders still hold.")]
        [Range(1, 255)] public int connectThreshold = 64;
        [Tooltip("Snow within this height (m) of the ground under it counts as resting on it.")]
        public float groundSeedDepth = 0.2f;
        [Tooltip("Snow within this distance (m) of a twig segment is held by it: it never counts as thin, and it bridges the connectivity flood.")]
        public float twigSupportRadius = 0.07f;
        [Tooltip("How far (m) a twig's support extends below its snow contact point, into the body it is stuck in.")]
        public float twigSupportBelow = 0.12f;
        [Tooltip("Detached islands smaller than this (m³) crumble into shed lumps instead of dropping as a ball.")]
        public float islandCrumbVolume = 0.0003f;

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
        [Tooltip("A thrown ball with mean compaction under this bursts into powder lumps on a hard landing; packed balls survive and fly true.")]
        [Range(0, 255)] public int burstCompaction = 90;
        [Tooltip("Impact speed (m/s) that bursts a fluffy ball.")]
        public float burstSpeed = 4.5f;

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

        // ---------- derived (kept here so every caller — tool, net replay, tests — derives identically) ----------

        /// <summary>0 = fresh powder … 1 = fully packed, from a compaction byte.</summary>
        public static float Pack(float compaction) => Mathf.Clamp01(compaction / 255f);

        /// <summary>Shave depth in metres for a given packing; score multiplies it.</summary>
        public float ShaveDepth(float pack, bool score)
            => shaveDepthPacked * Mathf.Lerp(shaveDepthFluffyMultiplier, 1f, pack) * (score ? scoreDepthMultiplier : 1f);

        public float ShaveShoulder(float pack) => Mathf.Lerp(shaveShoulderFluffy, shaveShoulderPacked, pack);
        public float ShaveNoise(float pack) => shaveNoiseFluffy * (1f - pack);
        public float Slump(float pack) => slumpStrength * (1f - pack);
        public float ShedVolume(float pack) => shedVolume * Mathf.Lerp(shedFluffyMultiplier, 1f, pack);

        /// <summary>The bite falloff: small bites in packed snow terminate crisply; everything else is the soft add shoulder.</summary>
        public float BiteShoulder(float pack, float radius)
        {
            float small = crispBrushRadius <= 0f ? 0f : Mathf.Clamp01((crispBrushRadius * 2f - radius) / crispBrushRadius);
            return Mathf.Lerp(addShoulder, crispShoulder, pack * small);
        }
    }
}
