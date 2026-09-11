using System.Collections.Generic;
using System.Linq;
using Snowfield.Config;
using UnityEngine;

namespace Snowfield.Sculpture
{
    /// <summary>
    /// Creates sculptures and snowballs at runtime, and fuses/promotes them.
    /// Scene singleton on the "Sculptures" root; everything it makes is parented under it.
    /// </summary>
    public class SculptureFactory : MonoBehaviour
    {
        public static SculptureFactory Instance { get; private set; }

        public SculptFeelConfig config;
        public Material snowMaterial;
        [Tooltip("Parent for created sculptures. Defaults to this transform.")]
        public Transform container;

        void Awake() => Instance = this;
        void OnDestroy() { if (Instance == this) Instance = null; }

        /// <summary>Full grid extent in metres (one axis).</summary>
        public float Extent => config.gridSize * config.voxelSize;

        /// <summary>New empty fixed sculpture whose 96³ grid is centred in XZ on <paramref name="groundCentre"/> with its floor at that height.</summary>
        public SnowSculpture CreateAt(Vector3 groundCentre)
        {
            float extent = Extent;
            var go = new GameObject("Sculpture");
            go.SetActive(false); // assign refs before Awake runs
            go.transform.SetParent(container != null ? container : transform, false);
            go.transform.position = groundCentre - new Vector3(extent * 0.5f, 0f, extent * 0.5f);
            var s = go.AddComponent<SnowSculpture>();
            s.EditorAssign(config, snowMaterial);
            s.gridSizeOverride = config.gridSize;
            go.SetActive(true);
            SculptureNet.RaiseCreated(s);
            return s;
        }

        /// <summary>Bare sculpture with an explicit grid shape and pose; density is the caller's job (loading). voxelSize 0 = config.</summary>
        public SnowSculpture CreateEmpty(int gridSize, Vector3 gridOffset, Vector3 position, Quaternion rotation, float voxelSize = 0f)
        {
            var go = new GameObject("Sculpture");
            go.SetActive(false);
            go.transform.SetParent(container != null ? container : transform, false);
            go.transform.SetPositionAndRotation(position, rotation);
            var s = go.AddComponent<SnowSculpture>();
            s.EditorAssign(config, snowMaterial);
            s.gridSizeOverride = gridSize;
            s.voxelSizeOverride = voxelSize;
            s.gridOffset = gridOffset;
            go.SetActive(true);
            SculptureNet.RaiseCreated(s);
            return s;
        }

        /// <summary>New fixed sculpture pre-stamped with a small hemisphere mound at the ground point.</summary>
        public SnowSculpture CreateMound(Vector3 groundPoint, float radius)
        {
            var s = CreateAt(groundPoint);
            s.StampSphere(groundPoint, radius, 0.7f, config.compactionLegacy, clipBelowWorldY: groundPoint.y - config.voxelSize);
            s.Remesh();
            s.RebuildColliders();
            return s;
        }

        /// <summary>
        /// New loose snowball: a small sculpture whose grid is centred on <paramref name="centre"/>, pre-stamped with a sphere.
        /// The root transform is the ball centre (so rolling can rotate it). <paramref name="compaction"/> &lt; 0 = rolled (packed).
        /// </summary>
        public Snowball CreateSnowball(Vector3 centre, float radius, float compaction = -1f)
        {
            var ball = CreateEmptySnowball(centre, radius);
            ball.Sculpture.StampSphere(centre, radius, ball.stampShoulder,
                compaction < 0f ? config.compactionRolled : compaction, float.NegativeInfinity);
            ball.Sculpture.Remesh();
            ball.Sculpture.RebuildColliders();
            return ball;
        }

        /// <summary>An empty snowball shell (grid centred on <paramref name="centre"/>); the caller fills the density. gridSize 0 = config.</summary>
        public Snowball CreateEmptySnowball(Vector3 centre, float nominalRadius, int gridSize = 0, float voxelSize = 0f)
        {
            int size = Mathf.Max(16, (gridSize > 0 ? gridSize : config.snowballGridSize) / 16 * 16);
            float vs = voxelSize > 0f ? voxelSize : config.voxelSize;
            float extent = size * vs;
            var go = new GameObject("Snowball");
            go.SetActive(false);
            go.transform.SetParent(container != null ? container : transform, false);
            go.transform.position = centre;
            var s = go.AddComponent<SnowSculpture>();
            s.EditorAssign(config, snowMaterial);
            s.gridSizeOverride = size;
            s.voxelSizeOverride = voxelSize;
            s.gridOffset = new Vector3(-extent * 0.5f, -extent * 0.5f, -extent * 0.5f);
            var ball = go.AddComponent<Snowball>();
            ball.radius = nominalRadius;
            go.SetActive(true);
            SculptureNet.RaiseCreated(s);
            return ball;
        }

        /// <summary>
        /// Move a loose ball's snow (and any props) into a brand-new fixed full-size sculpture on the ground under it.
        /// Returns the new sculpture; the ball is destroyed.
        /// </summary>
        public SnowSculpture Promote(Snowball ball)
        {
            SculptureNet.PushStructural();
            try
            {
                var big = CreateAt(ball.GroundPoint);
                big.Absorb(ball.Sculpture);
                foreach (var prop in ball.Sculpture.Props.ToArray())
                {
                    prop.transform.SetParent(big.transform, true);
                    prop.Reattach(big);
                }
                big.Remesh();
                big.RebuildColliders();
                SculptureNet.RaiseReplaced(ball.Sculpture, big);
                Destroy(ball.gameObject);
                return big;
            }
            finally { SculptureNet.PopStructural(); }
        }

        /// <summary>
        /// Rebuild a fixed sculpture into a larger, re-centred (unrotated) grid that also covers
        /// <paramref name="neededWorld"/>. Capped at config.maxGridSize; returns the sculpture unchanged at the cap.
        /// The old object is destroyed; props migrate.
        /// </summary>
        public SnowSculpture Regrow(SnowSculpture s, Bounds neededWorld)
        {
            int maxSize = Mathf.Max(config.gridSize, config.maxGridSize / 16 * 16);
            if (s.Info.size >= maxSize) return s; // at the cap: the wall is final (cheap check before the density scan)

            var needed = s.SnowBoundsWorld();
            if (needed.size == Vector3.zero) needed = neededWorld; else needed.Encapsulate(neededWorld);
            float margin = config.regrowMarginVoxels * config.voxelSize;
            needed.Expand(margin * 2f);

            float largestAxis = Mathf.Max(needed.size.x, Mathf.Max(needed.size.y, needed.size.z));
            int sizeVox = Mathf.CeilToInt(largestAxis / config.voxelSize / 16f) * 16;
            sizeVox = Mathf.Clamp(sizeVox, s.Info.size + 16, maxSize); // always at least one chunk bigger

            float extent = sizeVox * config.voxelSize;
            Vector3 origin = new Vector3(
                needed.center.x - extent * 0.5f,
                Mathf.Min(needed.min.y, s.WorldBounds.min.y),
                needed.center.z - extent * 0.5f);
            return RegrowExact(s, sizeVox, origin);
        }

        /// <summary>
        /// The rebuild half of <see cref="Regrow"/> with the target geometry fully specified — the shape the
        /// network layer replays so every peer regrows into byte-identical grids.
        /// </summary>
        public SnowSculpture RegrowExact(SnowSculpture s, int sizeVox, Vector3 origin)
        {
            SnowSculpture big;
            SculptureNet.PushStructural();
            try
            {
                big = CreateEmpty(sizeVox, Vector3.zero, origin, Quaternion.identity);
                big.Absorb(s);
                foreach (var prop in s.Props.ToArray())
                {
                    prop.transform.SetParent(big.transform, true);
                    prop.Reattach(big);
                }
                big.Remesh();
                big.RebuildColliders();
                Debug.Log($"[Snowfield] Regrew sculpture {s.Info.size}³ → {sizeVox}³");
                SculptureNet.RaiseReplaced(s, big);
                Destroy(s.gameObject);
            }
            finally { SculptureNet.PopStructural(); }
            SculptureNet.RaiseRegrowCommitted(big, sizeVox, origin);
            return big;
        }

        /// <summary>A target that can take more snow: loose balls are promoted to a full grid first.</summary>
        public SnowSculpture EnsureRoom(SnowSculpture target)
        {
            var ball = target.GetComponent<Snowball>();
            if (ball != null && ball.IsLoose) return Promote(ball);
            return target;
        }

        /// <summary>Fuse a snowball (at its current transform) into <paramref name="target"/>; the ball is consumed.</summary>
        public SnowSculpture Fuse(SnowSculpture target, Snowball ball) => Fuse(target, ball != null ? ball.Sculpture : null);

        /// <summary>Fuse any sculpture (at its current transform) into <paramref name="target"/>; the source is consumed, its props move across.</summary>
        public SnowSculpture Fuse(SnowSculpture target, SnowSculpture source)
        {
            if (target == null || source == null || target == source) return target;
            SculptureNet.RaiseFuseCommitted(target, source); // before EnsureRoom: ids + source pose still readable
            SculptureNet.PushStructural();
            try
            {
                target = EnsureRoom(target);
                var srcBounds = source.WorldBounds;
                if (!(target.WorldBounds.Contains(srcBounds.min) && target.WorldBounds.Contains(srcBounds.max)))
                    target = Regrow(target, srcBounds);
                target.Absorb(source);
                foreach (var prop in source.Props.ToArray())
                {
                    prop.transform.SetParent(target.transform, true);
                    prop.Reattach(target);
                }
                target.Remesh();
                target.RebuildColliders();
                var targetBall = target.GetComponent<Snowball>();
                if (targetBall != null) targetBall.Fix();
                SculptureNet.RaiseRemoved(source);
                Destroy(source.gameObject);
                return target;
            }
            finally { SculptureNet.PopStructural(); }
        }

        /// <summary>
        /// A fluffy ball hitting something hard: it bursts into a few powder lumps that carry its snow onward.
        /// The ball is consumed. Positions and velocities are explicit so the network replays exactly these.
        /// </summary>
        public List<Snowball> Burst(Snowball ball, Vector3 contact, Vector3 normal, Vector3 velocity)
        {
            var crumbs = new List<Snowball>();
            if (ball == null) return crumbs;
            const int n = 3;
            float volume = ball.Sculpture.DensityVolume();
            float r = SculptureStructure.SnowballRadius(volume / n);
            Vector3 along = Vector3.ProjectOnPlane(velocity, normal);
            Vector3 t1 = Vector3.Cross(normal, along.sqrMagnitude > 1e-4f ? along.normalized : Vector3.forward);
            if (t1.sqrMagnitude < 1e-4f) t1 = Vector3.Cross(normal, Vector3.right);
            t1.Normalize();
            Vector3 t2 = Vector3.Cross(normal, t1);
            var positions = new Vector3[n];
            var velocities = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                float a = i * (2f * Mathf.PI / n);
                Vector3 side = t1 * Mathf.Cos(a) + t2 * Mathf.Sin(a);
                positions[i] = contact + normal * (r * 1.05f) + side * (r * 1.2f);
                velocities[i] = along * 0.45f + side * (1.2f + 0.15f * along.magnitude) + normal * 0.8f;
            }
            SculptureNet.PushStructural();
            try
            {
                for (int i = 0; i < n; i++)
                    crumbs.Add(CreateSnowball(positions[i], r, config.compactionScooped));
            }
            finally { SculptureNet.PopStructural(); }
            SculptureNet.RaiseBurst(ball, crumbs, velocities); // before Removed: the ball's id must still resolve
            SculptureNet.RaiseRemoved(ball.Sculpture);
            Destroy(ball.gameObject);
            for (int i = 0; i < n; i++) crumbs[i].Launch(velocities[i]);
            return crumbs;
        }
    }
}
