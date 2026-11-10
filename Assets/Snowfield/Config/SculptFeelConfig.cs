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
        [Tooltip("Radius gained per metre rolled over fresh snow.")]
        public float snowballGrowthPerMetre = 0.04f;
        [Tooltip("Radius of a freshly scooped handful of snow (m).")]
        public float scoopRadius = 0.12f;
        [Tooltip("Depth of the divot a scoop leaves in the field (m).")]
        public float scoopDivotDepth = 0.04f;
        [Tooltip("Voxels per axis for a loose snowball's own grid (multiple of 16). This is what sets MaxBallRadius.")]
        public int snowballGridSize = 64;

        /// <summary>
        /// The biggest a loose snowball can get. There is no longer a design cap on ball size — what stops you is
        /// your own strength (see <see cref="pushPower"/>), which turns a boulder into a crawl long before this.
        /// This is only the wall of the ball's own grid: its sphere is stamped into a snowballGridSize cube, and
        /// snow past the edge would simply be clipped off. Loose balls never regrow (CLAUDE.md), so the wall is
        /// final — two voxels of margin keep the stamp's shoulder inside it.
        /// </summary>
        public float MaxBallRadius => snowballGridSize * voxelSize * 0.5f - voxelSize * 2f;

        [Tooltip("Auto-regrow ceiling: a sculpture's grid grows on demand up to this many voxels per axis (multiple of 16).")]
        public int maxGridSize = 192;
        [Tooltip("Free voxels the brush wants between its edge and the grid wall before a regrow is triggered.")]
        public int regrowMarginVoxels = 4;
        [Tooltip("How close two sculptures' snow has to come before they count as joined and merge into one sculpture (voxels). 0 = they must actually overlap.")]
        public int mergeReachVoxels = 1;

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

        [Header("Planar shave")]
        [Tooltip("How long the cut line is (m). The slice takes everything on one side of the line, but only within this span — past either end the snow stands.")]
        public float sliceLength = 1.2f;

        [Header("Reach")]
        [Tooltip("How far from the shoulder you can act on snow (m). This is the single limit on everything the reticle does: scoop, bite, pat, place, and whether the ground under the cursor registers at all. Measured from a metre above the player's feet, so the ground you can aim at is only sqrt(maxReach² - 1) away horizontally — at 1.2 that is 66 cm, which means looking almost straight down. Raising it also lets you sculpt from further off than the arms can stretch (see handMaxStretch).")]
        public float maxReach = 2.2f;
        [Tooltip("A carried ball rolls on the ground only while the cursor points within this distance of you (m, horizontal); further away it is held. Capped in practice by maxReach — above that it stops doing anything.")]
        public float rollEngageDistance = 3.5f;
        [Tooltip("Holding LMB longer than this (s) starts a throw charge; a shorter tap places or drops what you are carrying.")]
        public float throwTapThreshold = 0.2f;

        [Header("Weight and strength")]
        [Tooltip("Packed snow's density (kg/m3). The one number that turns volume into weight, for a rolled ball and a whole sculpture alike - so size is the only thing you have to read off the screen to know how hard something will be to move.")]
        public float snowDensity = 350f;
        [Tooltip("How much snow one pair of arms can shove, in kg*m/s: the top rolling speed is this divided by the ball's mass. At 250 a handful rolls as fast as you can walk, a 40 cm ball keeps up at a stroll, and a waist-high boulder is a crawl you need help with.")]
        public float pushPower = 250f;
        [Tooltip("Slowest a ball can be rolled (m/s). Not a stall: it keeps a monstrous ball nudgeable rather than welding the player to the spot, since the push limit also caps how fast they can walk.")]
        public float pushMinSpeed = 0.2f;
        [Tooltip("How close another player has to be to the ball's surface to lend their arms to it (m). Their strength adds to whoever is rolling it.")]
        public float pushHelpRange = 0.9f;
        [Tooltip("Heaviest snow ONE pair of arms can pick up off the ground (kg) - a rolled ball or a whole sculpture. Anything heavier stays down and can only be rolled, cannot be thrown, and attaches where it sits rather than where the cursor points. Every other player with their hands on it raises this by the same amount again. At the default density 60 kg is a ball about 35 cm across the radius.")]
        public float liftMass = 60f;

        [Header("Scoop")]
        [Tooltip("Holding the scoop button keeps biting as the cursor moves. Metres of cursor travel between bites, as a fraction of the brush radius - 0 disables the drag and a hold scoops one chunk, as it used to.")]
        [Range(0f, 2f)] public float scoopHoldSpacing = 0.55f;
        [Tooltip("Shortest time between bites while the scoop button is held (s), whatever the cursor does.")]
        [Min(0f)] public float scoopHoldInterval = 0.08f;

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

        [Header("Held accessories")]
        [Tooltip("Selected item's grip position in camera space: right, down, forward (metres).")]
        public Vector3 accessoryHoldOffset = new Vector3(0.30f, -0.28f, 0.65f);
        [Tooltip("Held item rotation relative to the camera. Item prefabs point along +Y.")]
        public Vector3 accessoryHoldEuler = new Vector3(-15f, 0f, -15f);
        [Tooltip("Palm pose relative to the held item's Grip: +Y faces out of the palm, +Z points towards the fingertips. Does not rotate the item.")]
        public Vector3 accessoryPalmEuler = new Vector3(0f, 0f, 180f);
        [Tooltip("Seconds to move a held item to the clicked surface before placing it.")]
        [Min(0.01f)] public float accessoryPlaceTime = 0.28f;
        [Tooltip("Seconds after placement for the empty hand to press and return before holding the next item.")]
        [Min(0f)] public float accessoryPlaceRecovery = 0.42f;

        [Header("Inventory selection")]
        [Tooltip("Accumulated wheel movement needed to switch items. A normal wheel notch is 1.")]
        [Min(0.01f)] public float accessoryScrollThreshold = 1f;
        [Tooltip("Minimum seconds between item switches. Extra scroll during this interval is discarded.")]
        [Min(0f)] public float accessoryScrollCooldown = 0.2f;
        [Tooltip("Seconds without scroll input before discarding an incomplete scroll step.")]
        [Min(0.01f)] public float accessoryScrollResetTime = 0.25f;

        [Header("Snowfall")]
        [Tooltip("Hours of snowfall to refill a full-depth path. 0 disables recovery.")]
        public float snowfallRecoverHours = 2f;
        [Tooltip("How often the recovery pass runs (s).")]
        public float snowfallTickSeconds = 2f;
        [Tooltip("Recovery speed while holding the debug 'let it snow' key (m/s).")]
        public float letItSnowPerSecond = 0.05f;

        [Header("Draped accessories")]
        [Tooltip("Spacing between strand points (m). Smaller = a smoother curve and more geometry.")]
        public float drapeSegmentLength = 0.05f;
        [Tooltip("Passes of pull-straight-then-lay-back-down after the strand first lands on the snow. This is what tightens it onto the shape; more passes wrap closer and cost more.")]
        [Range(0, 12)] public int drapeRelaxPasses = 5;
        [Tooltip("How far a slack strand slides downhill per relax pass, as a fraction of the segment length. 0 = it takes the shortest path over the shape.")]
        [Range(0f, 1f)] public float drapeSagPull = 0.35f;
        [Tooltip("How far a strand rests off the snow surface (m), on top of its own thickness.")]
        public float drapeSurfaceOffset = 0.01f;
        [Tooltip("Hard cap on points in one strand, so a very long chain cannot stall the frame.")]
        public int drapeMaxParticles = 400;
        [Tooltip("How quickly the strand's live end follows the cursor. Lower = smoother but laggier; this is what takes the shake out of the raycast.")]
        [Range(1f, 60f)] public float drapeTipFollow = 18f;

        [Header("Draped accessories - jiggle")]
        [Tooltip("How far a strand swings when it is placed or knocked (m). The strand is a fixed shape; this is the only thing that moves it.")]
        public float drapeJiggleAmplitude = 0.035f;
        [Tooltip("Swings per second.")]
        public float drapeJiggleFrequency = 4.5f;
        [Tooltip("Seconds for a swing to die away.")]
        public float drapeJiggleDecay = 1.1f;
        [Tooltip("How close you have to pass for a strand to notice you (m).")]
        public float drapeBumpRadius = 0.45f;
    }
}
