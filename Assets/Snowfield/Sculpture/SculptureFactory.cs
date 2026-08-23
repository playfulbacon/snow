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

        /// <summary>A target that can take more snow: loose balls are promoted to a full grid first.</summary>
        public SnowSculpture EnsureRoom(SnowSculpture target)
        {
            var ball = target.GetComponent<Snowball>();
            if (ball != null && ball.IsLoose) return Promote(ball);
            return target;
        }

        /// <summary>Fuse <paramref name="ball"/> (at its current transform) into <paramref name="target"/>; the ball is consumed.</summary>
        public SnowSculpture Fuse(SnowSculpture target, Snowball ball)
        {
            if (target == null || ball == null) return target;
            target = EnsureRoom(target);
            target.Absorb(ball.Sculpture);
            target.Remesh();
            target.RebuildColliders();
            var targetBall = target.GetComponent<Snowball>();
            if (targetBall != null) targetBall.Fix();
            Destroy(ball.gameObject);
            return target;
        }
    }
}
