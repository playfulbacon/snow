using UnityEngine;

namespace SnowDays
{
    /// <summary>
    /// Stamps footprints into the SnowDeformSystem trample map. Tracks the
    /// rig's animated foot bones (humanoid mapping first, name search as
    /// fallback) and stamps an oriented print whenever a foot is near the
    /// ground and has moved a step's worth since its last print - which
    /// naturally yields alternating prints at the animation's real footfall
    /// positions. With no findable feet it falls back to a body-width trail.
    /// SnowDeformSystem attaches this to the player automatically.
    /// </summary>
    [DefaultExecutionOrder(900)] // after animation, before SnowDeformSystem drains stamps
    public class SnowFootprints : MonoBehaviour
    {
        [Header("Print Shape")]
        [SerializeField] private float m_FootLength = 0.31f;
        [SerializeField] private float m_FootWidth = 0.15f;
        [SerializeField, Range(0f, 1f)] private float m_Strength = 1f;
        [SerializeField, Range(0.05f, 1f)] private float m_EdgeSoftness = 0.4f;
        [SerializeField, Range(0f, 1f)] private float m_EdgeNoise = 0.35f;

        [Header("Detection")]
        // Ground clearance below which a foot counts as planted. Measured at
        // the toes bone when the rig has one (toe base rests ~2cm up); the
        // ankle fallback needs a much larger allowance (~18cm rest height).
        [SerializeField] private float m_ToeGroundThreshold = 0.12f;
        [SerializeField] private float m_GroundThreshold = 0.24f;
        // Horizontal distance a planted foot must travel before re-stamping.
        [SerializeField] private float m_StepSpacing = 0.34f;
        // A planted foot is world-stationary (the clips are root-motion
        // matched); the swing foot moves fast even when it dips low. Only
        // slow feet stamp, so prints land at real footfalls.
        [SerializeField] private float m_MaxPlantSpeed = 1.1f;
        // Prints center on the sole, not the ankle bone.
        [SerializeField] private float m_ForwardOffset = 0.09f;

        [Header("Fallback Trail")]
        [SerializeField] private float m_TrailWidth = 0.55f;
        [SerializeField] private float m_TrailSpacing = 0.3f;

        private struct FootState
        {
            public Transform bone;
            public Transform toes;
            public Vector2 lastStamp;
            public Vector2 prevPos;
            public bool hasPrev;
            public bool wasDown;
        }

        private FootState[] m_Feet;
        private Vector2 m_LastTrailStamp;

        private void Start()
        {
            var animator = GetComponentInChildren<Animator>();
            Transform left = null, right = null, leftToes = null, rightToes = null;

            if (animator != null && animator.isHuman)
            {
                left = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
                right = animator.GetBoneTransform(HumanBodyBones.RightFoot);
                leftToes = animator.GetBoneTransform(HumanBodyBones.LeftToes);
                rightToes = animator.GetBoneTransform(HumanBodyBones.RightToes);
            }

            if (left == null || right == null)
                FindFeetByName(transform, ref left, ref right);

            if (left != null && right != null)
            {
                m_Feet = new[]
                {
                    new FootState { bone = left, toes = leftToes, lastStamp = FarAway() },
                    new FootState { bone = right, toes = rightToes, lastStamp = FarAway() },
                };
                Debug.Log($"[SnowFootprints] feet: '{left.name}'/'{right.name}', toes: "
                    + $"{(leftToes != null ? leftToes.name : "none")}/{(rightToes != null ? rightToes.name : "none")}");
            }
            else
            {
                Debug.Log("[SnowFootprints] No foot bones found; using body trail fallback.");
                m_Feet = null;
                m_LastTrailStamp = FarAway();
            }
        }

        private static Vector2 FarAway() => new Vector2(1e9f, 1e9f);

        private static void FindFeetByName(Transform root, ref Transform left, ref Transform right)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>())
            {
                string n = t.name.ToLowerInvariant();
                if (!n.Contains("foot") && !n.Contains("ankle")) continue;
                bool isLeft = n.Contains("l_") || n.Contains("_l") || n.Contains("left") || n.StartsWith("l.");
                bool isRight = n.Contains("r_") || n.Contains("_r") || n.Contains("right") || n.StartsWith("r.");
                if (isLeft && left == null) left = t;
                else if (isRight && right == null) right = t;
            }
        }

        private void LateUpdate()
        {
            var sys = SnowDeformSystem.Instance;
            if (sys == null) return;

            if (m_Feet == null)
            {
                UpdateTrail(sys);
                return;
            }

            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            for (int i = 0; i < m_Feet.Length; i++)
            {
                ref FootState foot = ref m_Feet[i];
                Vector3 p = foot.bone.position;
                var xz = new Vector2(p.x, p.z);
                float speed = foot.hasPrev ? (xz - foot.prevPos).magnitude / dt : 0f;
                foot.prevPos = xz;
                bool skipVelocity = !foot.hasPrev;
                foot.hasPrev = true;

                // Clearance measured at the toes when available - the ankle
                // bone never gets near the ground on some gaits (strafing).
                Vector3 probe = foot.toes != null ? foot.toes.position : p;
                float threshold = foot.toes != null ? m_ToeGroundThreshold : m_GroundThreshold;
                if (!sys.TryGetGroundHeight(probe.x, probe.z, out float ground))
                    continue;

                bool down = probe.y - ground < threshold
                    && (skipVelocity || speed <= m_MaxPlantSpeed);
                if (down)
                {
                    // A freshly landed foot stamps almost immediately; a foot
                    // that stays planted only re-stamps after a real step.
                    float need = foot.wasDown ? m_StepSpacing : 0.05f;
                    if ((xz - foot.lastStamp).sqrMagnitude >= need * need)
                    {
                        Vector2 dir = FootDirection(ref foot).normalized;
                        Vector3 center = p + new Vector3(dir.x, 0f, dir.y) * m_ForwardOffset;
                        sys.Stamp(center, dir, m_FootLength, m_FootWidth,
                            m_Strength, m_EdgeSoftness, m_EdgeNoise);
                        foot.lastStamp = xz;
                    }
                }
                foot.wasDown = down;
            }
        }

        private Vector2 FootDirection(ref FootState foot)
        {
            if (foot.toes != null)
            {
                Vector3 d = foot.toes.position - foot.bone.position;
                var planar = new Vector2(d.x, d.z);
                if (planar.sqrMagnitude > 0.001f) return planar;
            }
            Vector3 fwd = transform.forward;
            return new Vector2(fwd.x, fwd.z);
        }

        // No feet found: drag a soft body-width trench along the path.
        private void UpdateTrail(SnowDeformSystem sys)
        {
            Vector3 p = transform.position;
            if (!sys.TryGetGroundHeight(p.x, p.z, out float ground)) return;
            if (p.y - ground > 1.2f) return; // airborne

            var xz = new Vector2(p.x, p.z);
            if ((xz - m_LastTrailStamp).sqrMagnitude < m_TrailSpacing * m_TrailSpacing) return;

            Vector3 fwd = transform.forward;
            sys.Stamp(p, new Vector2(fwd.x, fwd.z), m_TrailSpacing * 1.6f, m_TrailWidth,
                m_Strength, 0.6f, m_EdgeNoise);
            m_LastTrailStamp = xz;
        }
    }
}
