using System.Collections.Generic;
using Snowfield.Voxel;
using UnityEngine;

namespace Snowfield.Sculpture
{
    /// <summary>
    /// Where the snow's surface actually is, to a fraction of a millimetre.
    ///
    /// Read off the density field, not the chunk colliders: colliders are only cooked on brush release and are off
    /// entirely for a carried ball, whereas the density is always current. The one primitive is a line search for the
    /// iso crossing (<see cref="Snap"/>) — an exact answer along a known direction, which beats any amount of guessing
    /// at where the surface might be, and the gradient at the crossing is then a real surface normal.
    ///
    /// Choosing the direction is the caller's job, because only the caller knows what it is looking for. Outside the
    /// snow there is nothing to go on locally — the density is flat zero a voxel out and the gradient dies with it —
    /// so the direction has to come from somewhere else: a draped strand carries the surface normal from the point it
    /// was clicked onto, which on anything rounded points very nearly at the surface it wants.
    /// </summary>
    public static class SurfaceProbe
    {
        /// <summary>Bisections once a crossing is bracketed. 8 takes a half-voxel bracket to about 0.08 mm.</summary>
        const int Bisections = 8;

        public static bool Usable(SnowSculpture s) => s != null && s.Grid != null && s.Grid.IsCreated;

        /// <summary>Signed depth into the snow in density units: positive inside, zero exactly on the surface.</summary>
        public static float Depth(SnowSculpture s, Vector3 p) => s.SampleDensityWorld(p) - VoxelGridInfo.Iso;

        public static bool IsInside(SnowSculpture s, Vector3 p) => Usable(s) && Depth(s, p) > 0f;

        /// <summary>
        /// Outward surface direction from the density gradient. Zero where there is no gradient to read — a hair
        /// outside the snow, or deep inside it where the density saturates.
        /// </summary>
        public static Vector3 Normal(SnowSculpture s, Vector3 p)
        {
            if (!Usable(s)) return Vector3.zero;
            Vector3 g = Gradient(s, p, s.Info.voxelSize);
            float m = g.magnitude;
            return m < 1e-4f ? Vector3.zero : -g / m;
        }

        /// <summary>
        /// The iso crossing nearest <paramref name="p"/> on the line through it along <paramref name="outward"/>,
        /// within <paramref name="range"/> metres either way. Walks out in half-voxel shells from
        /// <paramref name="p"/>, so the *nearest* crossing wins rather than the first one some march happens to meet,
        /// then bisects the bracket it found.
        /// </summary>
        public static bool Snap(SnowSculpture s, Vector3 p, Vector3 outward, float range, out Vector3 hit)
        {
            hit = p;
            if (!Usable(s) || outward.sqrMagnitude < 1e-8f || range <= 0f) return false;
            outward = outward.normalized;

            float here = Depth(s, p);
            if (Mathf.Abs(here) < 1e-3f) return true;      // already on it
            bool inside = here > 0f;

            float step = Mathf.Max(s.Info.voxelSize * 0.5f, 0.005f);
            int shells = Mathf.Max(1, Mathf.CeilToInt(range / step));
            float lastOut = here, lastIn = here;

            for (int i = 1; i <= shells; i++)
            {
                float t = i * step, t0 = (i - 1) * step;
                float dOut = Depth(s, p + outward * t);
                float dIn = Depth(s, p - outward * t);
                bool crossOut = Crosses(lastOut, dOut);
                bool crossIn = Crosses(lastIn, dIn);

                if (crossOut && crossIn)
                {
                    float tOut = Bisect(s, p, outward, t0, lastOut, t);
                    float tIn = Bisect(s, p, -outward, t0, lastIn, t);
                    // Both sides cross in the same shell: take the nearer, and on a tie go the way the point needs
                    // to move — out of the snow if it is buried, into it if it is hanging beside it.
                    bool useOut = inside ? tOut <= tIn : tOut < tIn;
                    hit = useOut ? p + outward * tOut : p - outward * tIn;
                    return true;
                }
                if (crossOut) { hit = p + outward * Bisect(s, p, outward, t0, lastOut, t); return true; }
                if (crossIn) { hit = p - outward * Bisect(s, p, -outward, t0, lastIn, t); return true; }

                lastOut = dOut;
                lastIn = dIn;
            }
            return false;
        }

        /// <summary>
        /// Lay <paramref name="p"/> on the nearest snow surface it can reach, across every sculpture in
        /// <paramref name="surfaces"/>. <paramref name="outwardHint"/> is where the caller believes the outside is;
        /// a point already buried ignores it and follows its own gradient out instead. Returns false — leaving
        /// <paramref name="hit"/> at <paramref name="p"/> — when there is no snow within <paramref name="range"/>.
        /// </summary>
        public static bool Lay(IReadOnlyList<SnowSculpture> surfaces, Vector3 p, Vector3 outwardHint, float range,
                               out Vector3 hit, out Vector3 normal)
        {
            hit = p;
            normal = Vector3.zero;
            if (surfaces == null) return false;

            float best = float.MaxValue;
            for (int i = 0; i < surfaces.Count; i++)
            {
                var s = surfaces[i];
                if (!Usable(s)) continue;
                var reach = s.WorldBounds;
                reach.Expand(range * 2f);
                if (!reach.Contains(p)) continue;

                bool inside = Depth(s, p) > 0f;
                Vector3 axis = inside ? Outward(s, p) : outwardHint;
                if (axis.sqrMagnitude < 1e-6f) axis = Vector3.up;
                // A buried point has to come out whatever the caller's reach was: that is correctness, not clinging.
                float span = inside ? Mathf.Max(range, s.Info.WorldExtent) : range;

                if (!Snap(s, p, axis, span, out var candidate))
                {
                    // Nothing along the hint. A strand also simply falls onto whatever is underneath it, and that is
                    // the one other direction always worth a look.
                    if (!Snap(s, p, Vector3.up, span, out candidate)) continue;
                }

                float d = (candidate - p).sqrMagnitude;
                if (d >= best) continue;
                best = d;
                hit = candidate;
                Vector3 n = Normal(s, candidate);
                normal = n.sqrMagnitude > 1e-6f ? n : axis.normalized;
            }
            return best < float.MaxValue;
        }

        /// <summary>Move a point that is inside the snow out to <paramref name="offset"/> above the surface.</summary>
        public static void PushOut(SnowSculpture s, ref Vector3 p, float offset)
        {
            if (!Usable(s) || Depth(s, p) <= 0f) return;
            Vector3 axis = Outward(s, p);
            if (!Snap(s, p, axis, Mathf.Max(s.Info.WorldExtent, 1f), out var surface)) return;
            Vector3 n = Normal(s, surface);
            p = surface + (n.sqrMagnitude > 1e-6f ? n : axis) * offset;
        }

        /// <summary>
        /// Which way is out, for a point inside the snow. The gradient knows, except deep inside where the density
        /// saturates at 255 and there is no gradient at all; there, away from the grid centre is the best guess
        /// going, and the line search finds the real surface along it regardless.
        /// </summary>
        static Vector3 Outward(SnowSculpture s, Vector3 p)
        {
            Vector3 n = Normal(s, p);
            if (n.sqrMagnitude > 1e-6f) return n;
            Vector3 dir = p - s.WorldBounds.center;
            return dir.sqrMagnitude < 1e-8f ? Vector3.up : dir.normalized;
        }

        static bool Crosses(float a, float b) => (a > 0f) != (b > 0f);

        /// <summary>Narrow a bracketed crossing on the ray from <paramref name="origin"/> along <paramref name="dir"/>.</summary>
        static float Bisect(SnowSculpture s, Vector3 origin, Vector3 dir, float t0, float f0, float t1)
        {
            for (int i = 0; i < Bisections; i++)
            {
                float tm = (t0 + t1) * 0.5f;
                float fm = Depth(s, origin + dir * tm);
                if (Crosses(f0, fm)) t1 = tm;
                else { t0 = tm; f0 = fm; }
            }
            return (t0 + t1) * 0.5f;
        }

        /// <summary>Central-difference density gradient, density units per metre. Points into the snow.</summary>
        static Vector3 Gradient(SnowSculpture s, Vector3 p, float h) => new Vector3(
            s.SampleDensityWorld(p + new Vector3(h, 0f, 0f)) - s.SampleDensityWorld(p - new Vector3(h, 0f, 0f)),
            s.SampleDensityWorld(p + new Vector3(0f, h, 0f)) - s.SampleDensityWorld(p - new Vector3(0f, h, 0f)),
            s.SampleDensityWorld(p + new Vector3(0f, 0f, h)) - s.SampleDensityWorld(p - new Vector3(0f, 0f, h)))
            / (2f * h);
    }
}
