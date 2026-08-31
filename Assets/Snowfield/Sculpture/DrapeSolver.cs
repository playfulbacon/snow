using System;
using System.Collections.Generic;
using Snowfield.Config;
using Snowfield.Voxel;
using UnityEngine;

namespace Snowfield.Sculpture
{
    /// <summary>
    /// A verlet strand hanging between anchor points and resting on whatever snow is under it.
    ///
    /// Collision is by **density, not colliders**: a particle inside the iso-surface is pushed back out along the
    /// density gradient. That works while chunk colliders are uncooked or switched off (mid-edit, carried balls),
    /// which a raycast against MeshColliders would not.
    ///
    /// Deliberately deterministic — fixed step, fixed pass counts, no Time.deltaTime, no randomness — so every peer
    /// and every reload re-derives the same strand from the same anchors. Only the anchors are ever stored or sent.
    /// </summary>
    public sealed class DrapeRope
    {
        /// <summary>Ground height at a world position. Assigned at runtime by the player side (Sculpture cannot see SnowGround).</summary>
        public static Func<Vector3, float> GroundSampler;

        public readonly List<Vector3> Points = new List<Vector3>();
        readonly List<Vector3> _prev = new List<Vector3>();
        readonly List<bool> _pinned = new List<bool>();
        float _restLength;

        public int Count => Points.Count;

        /// <summary>
        /// Seed the strand along the anchor polyline. Anchors are pinned; the spans between them get particles
        /// spaced <see cref="SculptFeelConfig.drapeSegmentLength"/> apart, with <see cref="DrapeStyle.Slack"/>
        /// worth of extra rope so it sags instead of pulling straight.
        /// </summary>
        public void Seed(IReadOnlyList<Vector3> anchorsWorld, DrapeStyle style, SculptFeelConfig cfg)
        {
            Points.Clear(); _prev.Clear(); _pinned.Clear();
            if (anchorsWorld == null || anchorsWorld.Count == 0) return;

            float spacing = Mathf.Max(0.01f, cfg != null ? cfg.drapeSegmentLength : 0.05f);
            int budget = cfg != null ? Mathf.Max(8, cfg.drapeMaxParticles) : 400;

            Add(anchorsWorld[0], true);
            for (int a = 0; a + 1 < anchorsWorld.Count; a++)
            {
                Vector3 from = anchorsWorld[a], to = anchorsWorld[a + 1];
                float span = Vector3.Distance(from, to);
                int steps = Mathf.Max(1, Mathf.CeilToInt(span * style.Slack / spacing));
                for (int i = 1; i <= steps; i++)
                {
                    if (Points.Count >= budget) break;
                    // Sag the seed slightly so gravity has a direction to work with instead of a straight line.
                    float t = i / (float)steps;
                    Vector3 p = Vector3.Lerp(from, to, t);
                    p.y -= Mathf.Sin(t * Mathf.PI) * span * (style.Slack - 1f) * 0.5f;
                    Add(p, i == steps);
                }
            }
            _restLength = TotalSeedLength(anchorsWorld, style) / Mathf.Max(1, Points.Count - 1);
        }

        void Add(Vector3 p, bool pinned)
        {
            Points.Add(p);
            _prev.Add(p);
            _pinned.Add(pinned);
        }

        static float TotalSeedLength(IReadOnlyList<Vector3> anchors, DrapeStyle style)
        {
            float len = 0f;
            for (int i = 0; i + 1 < anchors.Count; i++) len += Vector3.Distance(anchors[i], anchors[i + 1]);
            return Mathf.Max(0.01f, len * style.Slack);
        }

        /// <summary>
        /// Seed and settle in one go. The strand is a pure function of its anchors — solved from scratch, never
        /// carried frame to frame — which is what keeps it from wobbling while you move the cursor, and what makes
        /// the live preview identical to the finished strand.
        /// </summary>
        public void Solve(IReadOnlyList<Vector3> anchorsWorld, DrapeStyle style, SculptFeelConfig cfg,
                          IReadOnlyList<SnowSculpture> colliders)
        {
            Seed(anchorsWorld, style, cfg);
            Run(cfg != null ? cfg.drapeSettleSteps : 160, style, cfg, colliders);
        }

        /// <summary>Advance the simulation. Normally reached through <see cref="Solve"/>.</summary>
        public void Run(int steps, DrapeStyle style, SculptFeelConfig cfg, IReadOnlyList<SnowSculpture> colliders)
        {
            if (Points.Count < 2) return;
            float dt = cfg != null ? Mathf.Max(0.001f, cfg.drapeStepSeconds) : 0.016f;
            float damping = cfg != null ? Mathf.Clamp01(cfg.drapeDamping) : 0.92f;
            float gravity = cfg != null ? cfg.drapeGravity : -9.81f;
            int passes = cfg != null ? Mathf.Max(1, cfg.drapeConstraintPasses) : 4;
            float offset = (cfg != null ? cfg.drapeSurfaceOffset : 0.01f) + style.Radius;

            for (int s = 0; s < steps; s++)
            {
                Integrate(dt, damping, gravity);
                // Collision sits INSIDE the constraint loop. Resolving it afterwards let the rope sink each step and
                // get shoved back out the next one — an oscillator by construction.
                for (int p = 0; p < passes; p++)
                {
                    Constrain();
                    if (p == passes - 1 || p == passes / 2) Collide(colliders, offset);
                }
                Constrain();
            }
        }

        void Integrate(float dt, float damping, float gravity)
        {
            float gdt2 = gravity * dt * dt;
            for (int i = 0; i < Points.Count; i++)
            {
                if (_pinned[i]) { _prev[i] = Points[i]; continue; }
                Vector3 p = Points[i];
                Vector3 velocity = (p - _prev[i]) * damping;
                _prev[i] = p;
                Points[i] = p + velocity + new Vector3(0f, gdt2, 0f);
            }
        }

        void Constrain()
        {
            for (int i = 0; i + 1 < Points.Count; i++)
            {
                Vector3 a = Points[i], b = Points[i + 1];
                Vector3 delta = b - a;
                float dist = delta.magnitude;
                if (dist < 1e-6f) continue;
                float correction = (dist - _restLength) / dist * 0.5f;
                Vector3 shift = delta * correction;
                bool pa = _pinned[i], pb = _pinned[i + 1];
                if (pa && pb) continue;
                if (pa) Points[i + 1] = b - shift * 2f;
                else if (pb) Points[i] = a + shift * 2f;
                else { Points[i] = a + shift; Points[i + 1] = b - shift; }
            }
        }

        void Collide(IReadOnlyList<SnowSculpture> colliders, float offset)
        {
            var ground = GroundSampler;
            for (int i = 0; i < Points.Count; i++)
            {
                if (_pinned[i]) continue;
                Vector3 p = Points[i];
                if (colliders != null)
                    for (int c = 0; c < colliders.Count; c++)
                        PushOutOf(colliders[c], ref p, offset);
                if (ground != null)
                {
                    float gy = ground(p) + offset;
                    if (p.y < gy) p.y = gy;
                }
                Points[i] = p;
            }
        }

        /// <summary>
        /// Outward surface direction near <paramref name="p"/>, from the density gradient — what a ribbon lies flat
        /// against. Falls back when the point is nowhere near snow.
        /// </summary>
        public static Vector3 SurfaceUp(IReadOnlyList<SnowSculpture> surfaces, Vector3 p, Vector3 fallback)
        {
            if (surfaces == null) return fallback;
            for (int i = 0; i < surfaces.Count; i++)
            {
                var s = surfaces[i];
                if (s == null || s.Grid == null || !s.Grid.IsCreated) continue;
                if (!s.WorldBounds.Contains(p)) continue;
                float h = s.Info.voxelSize;
                Vector3 grad = new Vector3(
                    s.SampleDensityWorld(p + new Vector3(h, 0f, 0f)) - s.SampleDensityWorld(p - new Vector3(h, 0f, 0f)),
                    s.SampleDensityWorld(p + new Vector3(0f, h, 0f)) - s.SampleDensityWorld(p - new Vector3(0f, h, 0f)),
                    s.SampleDensityWorld(p + new Vector3(0f, 0f, h)) - s.SampleDensityWorld(p - new Vector3(0f, 0f, h)));
                if (grad.sqrMagnitude > 1e-6f) return (-grad).normalized;
            }
            return fallback;
        }

        /// <summary>Move a point that is inside the snow back out to just above the iso-surface.</summary>
        public static void PushOutOf(SnowSculpture s, ref Vector3 p, float offset)
        {
            if (s == null || s.Grid == null || !s.Grid.IsCreated) return;
            var bounds = s.WorldBounds;
            if (!bounds.Contains(p)) return;

            float density = s.SampleDensityWorld(p);
            if (density <= VoxelGridInfo.Iso) return;

            float h = s.Info.voxelSize;
            Vector3 grad = new Vector3(
                s.SampleDensityWorld(p + new Vector3(h, 0f, 0f)) - s.SampleDensityWorld(p - new Vector3(h, 0f, 0f)),
                s.SampleDensityWorld(p + new Vector3(0f, h, 0f)) - s.SampleDensityWorld(p - new Vector3(0f, h, 0f)),
                s.SampleDensityWorld(p + new Vector3(0f, 0f, h)) - s.SampleDensityWorld(p - new Vector3(0f, 0f, h)))
                / (2f * h); // density units per metre

            float slope = grad.magnitude;
            if (slope < 1e-4f) { p += Vector3.up * offset; return; } // flat interior: nudge up rather than stall
            Vector3 outward = -grad / slope;
            float depth = (density - VoxelGridInfo.Iso) / slope; // metres to the surface
            p += outward * Mathf.Min(depth + offset, 0.5f);      // cap so a bad gradient cannot fling the strand
        }
    }
}
