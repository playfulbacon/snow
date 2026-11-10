using System;
using UnityEngine;

namespace Snowfield.Sculpture
{
    /// <summary>
    /// "A thrown snowball can hit this." Put on a body with colliders — the local player and every remote
    /// player's avatar get one — and a flying <see cref="Snowball"/> that touches it reports the hit here
    /// instead of treating it as scenery to roll off.
    ///
    /// Lives in Snowfield.Sculpture because both sides of the hit need it and neither can reach the other: the
    /// local player is a SnowDays type in Assembly-CSharp, remote avatars are Snowfield.Net types, and both of
    /// those reference this assembly. Whoever owns the body wires <see cref="onHit"/> to whatever it does about
    /// being hit — shove the capsule, or send the news to the client that actually owns that body.
    ///
    /// Only the client whose ball it is simulates the flight (CLAUDE.md: physics flights stay local), so a hit
    /// is always detected on the thrower's machine and on nobody else's.
    /// </summary>
    public sealed class SnowballTarget : MonoBehaviour
    {
        /// <summary>One strike, in world space. <see cref="normal"/> points out of the body that was hit.</summary>
        public struct Hit
        {
            public Vector3 point;
            public Vector3 normal;
            /// <summary>The ball's velocity at the moment of contact.</summary>
            public Vector3 velocity;
            public float mass;
            public float radius;

            /// <summary>Speed driving the ball INTO the body (m/s); never negative.</summary>
            public float Impact => Mathf.Max(0f, -Vector3.Dot(velocity, normal));
        }

        [Tooltip("How hard this body is shoved by a hit. 0 takes the snow without budging.")]
        public float knockback = 1f;

        /// <summary>What this body does about being hit. Wired by whoever owns it; null is a legal "nothing".</summary>
        public Action<Hit> onHit;

        /// <summary>
        /// Deliver a hit: the snow bursts where it landed, then the body reacts. The spray happens here rather
        /// than in the handler so a target that does nothing else still looks like it was hit.
        /// </summary>
        public void Strike(in Hit hit)
        {
            SnowBurst.Play(hit.point, hit.normal, hit.radius, Mathf.Max(1f, hit.Impact / 3f));
            onHit?.Invoke(hit);
        }
    }
}
