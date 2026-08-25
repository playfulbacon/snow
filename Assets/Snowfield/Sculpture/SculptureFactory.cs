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
            return s;
        }

        /// <summary>Bare sculpture with an explicit grid shape and pose; density is the caller's job (loading).</summary>
        public SnowSculpture CreateEmpty(int gridSize, Vector3 gridOffset, Vector3 position, Quaternion rotation)
        {
            var go = new GameObject("Sculpture");
            go.SetActive(false);
            go.transform.SetParent(container != null ? container : transform, false);
            go.transform.SetPositionAndRotation(position, rotation);
            var s = go.AddComponent<SnowSculpture>();
            s.EditorAssign(config, snowMaterial);
            s.gridSizeOverride = gridSize;
            s.gridOffset = gridOffset;
            go.SetActive(true);
            return s;
        }

        /// <summary>
        /// New loose snowball: a small sculpture whose grid is centred on <paramref name="centre"/>, pre-stamped with a sphere.
        /// The root transform is the ball centre (so rolling can rotate it).
        /// </summary>
        public Snowball CreateSnowball(Vector3 centre, float radius)
        {
            int size = Mathf.Max(16, config.snowballGridSize / 16 * 16);
            float extent = size * config.voxelSize;
            var go = new GameObject("Snowball");
            go.SetActive(false);
            go.transform.SetParent(container != null ? container : transform, false);
            go.transform.position = centre;
            var s = go.AddComponent<SnowSculpture>();
            s.EditorAssign(config, snowMaterial);
            s.gridSizeOverride = size;
            s.gridOffset = new Vector3(-extent * 0.5f, -extent * 0.5f, -extent * 0.5f);
            var ball = go.AddComponent<Snowball>();
            ball.radius = radius;
            go.SetActive(true);
            s.StampSphere(centre, radius, ball.stampShoulder);
            s.Remesh();
            s.RebuildColliders();
            return ball;
        }

        /// <summary>
        /// Move a loose ball's snow (and any props) into a brand-new fixed full-size sculpture on the ground under it.
        /// Returns the new sculpture; the ball is destroyed.
        /// </summary>
        public SnowSculpture Promote(Snowball ball)
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
            Destroy(ball.gameObject);
            return big;
        }

        /// <summary>
        /// Rebuild a fixed sculpture into a larger, re-centred (unrotated) grid that also covers
        /// <paramref name="neededWorld"/>. Capped at config.maxGridSize; returns the sculpture unchanged at the cap.
        /// The old object is destroyed; props migrate.
        /// </summary>
        public SnowSculpture Regrow(SnowSculpture s, Bounds neededWorld)
        {
            var needed = s.SnowBoundsWorld();
            if (needed.size == Vector3.zero) needed = neededWorld; else needed.Encapsulate(neededWorld);
            float margin = config.regrowMarginVoxels * config.voxelSize;
            needed.Expand(margin * 2f);

            int maxSize = Mathf.Max(config.gridSize, config.maxGridSize / 16 * 16);
            float largestAxis = Mathf.Max(needed.size.x, Mathf.Max(needed.size.y, needed.size.z));
            int sizeVox = Mathf.CeilToInt(largestAxis / config.voxelSize / 16f) * 16;
            sizeVox = Mathf.Clamp(sizeVox, s.Info.size, maxSize);

            bool contained = s.WorldBounds.Contains(needed.min) && s.WorldBounds.Contains(needed.max);
            if (contained) return s;
            if (sizeVox == s.Info.size && s.Info.size >= maxSize && s.transform.rotation == Quaternion.identity)
                return s; // at the cap: the wall is final

            float extent = sizeVox * config.voxelSize;
            Vector3 origin = new Vector3(
                needed.center.x - extent * 0.5f,
                Mathf.Min(needed.min.y, s.WorldBounds.min.y),
                needed.center.z - extent * 0.5f);
            var big = CreateEmpty(sizeVox, Vector3.zero, origin, Quaternion.identity);
            big.Absorb(s);
            foreach (var prop in s.Props.ToArray())
            {
                prop.transform.SetParent(big.transform, true);
                prop.Reattach(big);
            }
            big.Remesh();
            big.RebuildColliders();
            Destroy(s.gameObject);
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
            Destroy(source.gameObject);
            return target;
        }
    }
}
