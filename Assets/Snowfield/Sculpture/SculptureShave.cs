using System.Collections.Generic;
using Snowfield.Voxel;
using Unity.Mathematics;
using UnityEngine;

namespace Snowfield.Sculpture
{
    /// <summary>
    /// The one shave applier, shared by the local tool and the network replay so both do exactly the same work:
    /// each stamp is a surface-relative disc (<see cref="ShaveJob"/>), and on fluffy snow every stamp is followed
    /// by one relaxation pass (the slump) so cut edges round themselves off.
    /// </summary>
    public static class SculptureShave
    {
        /// <summary>Derive a stamp's parameters from the snow's packing (0 = powder, 1 = packed). The origin sends the result verbatim.</summary>
        public static ShaveParams ParamsFor(Config.SculptFeelConfig cfg, float voxelSize, float radiusMetres, float pack, bool score, Vector3 seedPoint)
        {
            // Depth is capped against the stamp's own width. Score stacks the fluffy multiplier on the score
            // multiplier, which on powder asked for a 34 cm spike down a 7.5 cm groove — a puncture, not a line,
            // and an AABB an order of magnitude larger than it needed to be.
            float depth = cfg.ShaveDepth(pack, score) / voxelSize;
            depth = Mathf.Min(depth, radiusMetres / voxelSize * cfg.shaveDepthRadiusCap);
            return new ShaveParams
            {
                DepthVoxels = depth,
                SoftVoxels = depth * cfg.shaveSoftFraction,
                Shoulder = cfg.ShaveShoulder(pack),
                NoiseAmplitude = cfg.ShaveNoise(pack),
                Seed = math.hash(new float3(seedPoint.x, seedPoint.y, seedPoint.z)),
            };
        }

        /// <summary>
        /// Apply a frame's stamps to one sculpture. Returns the snow removed (m³) and the voxel AABB touched
        /// (exclusive max; <paramref name="regionMax"/> &lt;= <paramref name="regionMin"/> when nothing was touched).
        /// </summary>
        public static float ApplyStamps(SnowSculpture s, float radius, float slump, IReadOnlyList<SculptureNet.ShaveStamp> stamps,
                                        out int3 regionMin, out int3 regionMax, bool measure = true)
        {
            var cfg = s.Config;
            float removed = 0f;
            regionMin = new int3(int.MaxValue);
            regionMax = new int3(int.MinValue);
            for (int i = 0; i < stamps.Count; i++)
            {
                var st = stamps[i];
                removed += s.ApplyShave(st.point, st.normal, radius, st.prm, out int3 min, out int3 max, measure);
                if (math.all(max > min))
                {
                    regionMin = math.min(regionMin, min);
                    regionMax = math.max(regionMax, max);
                }
                // The slump is an ordinary blur, so it also pushes snow from the solid interior back up into the cut
                // that was just made. Kept small and confined to genuinely fluffy snow, where a collapsing edge is
                // the point; on anything firm it was quietly undoing the stroke.
                if (slump > 0f && cfg != null)
                    s.ApplySmooth(st.point, radius * cfg.slumpRadiusFraction, slump, cfg.smoothShoulder, 0f, 0f);
            }
            return removed;
        }
    }
}
