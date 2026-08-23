using Unity.Mathematics;

namespace Snowfield.Voxel
{
    /// <summary>
    /// Anything the brush can stroke: sculptures (density) and the ground (heightmap).
    /// Rate units differ per implementation (density/tick vs metres/tick); the caller picks the right config value.
    /// </summary>
    public interface IBrushTarget
    {
        void ApplyAdd(float3 worldCenter, float radiusMetres, float ratePerTick, float shoulder);
        void ApplySmooth(float3 worldCenter, float radiusMetres, float strength, float shoulder);
        void Remesh();
        void RebuildColliders();
    }
}
