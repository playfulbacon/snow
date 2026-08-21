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
    }
}
