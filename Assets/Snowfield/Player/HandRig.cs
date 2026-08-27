using Snowfield.Config;
using UnityEngine;

namespace Snowfield.Player
{
    /// <summary>
    /// Stretchy two-bone arm IK for the humanoid player rig. The rule the whole thing is built on: <b>the hands go
    /// where the snow is</b>. Snow is at arm's length only by accident — a carried ball floats at the carry anchor,
    /// a scoop starts wherever the cursor was, a ball you are rolling is down at your feet — so rather than clamp
    /// the reach (which reads as the hands giving up), the arm lengthens: the elbow and wrist offsets scale along
    /// their own bone and linear skinning turns that into taffy. Cartoon on purpose.
    ///
    /// Runs in LateUpdate, after Mecanim has written the animated pose, and restores rest bone lengths before
    /// reading it so nothing compounds frame to frame. Callers push a goal every frame (<see cref="Reach"/>); a
    /// hand nobody asks for eases back onto the animation. <see cref="Pulse"/> is the one-shot version, for the
    /// moments where the snow stops existing at the instant you touch it (fusing a ball on, throwing, planting a
    /// carrot) and the hand should still be seen doing it.
    /// </summary>
    [DisallowMultipleComponent]
    public class HandRig : MonoBehaviour
    {
        public enum Side { Left = 0, Right = 1 }

        [Tooltip("Stretch and spring feel live here (Hands section). Tuned in play mode.")]
        public SculptFeelConfig config;
        [Tooltip("Humanoid animator to drive. Defaults to the one under this object.")]
        public Animator animator;

        [Header("Solve")]
        [Tooltip("Direction the elbow is pulled, in this object's space (mirrored for the left arm). Keeps a nearly straight arm from flipping its bend.")]
        public Vector3 elbowHint = new Vector3(0.5f, -0.8f, -0.35f);
        [Range(0f, 1f)] public float elbowHintWeight = 0.55f;
        [Tooltip("How far the shoulder swings along with a reach. Every degree here is length the stretch does not have to fake.")]
        [Range(0f, 1f)] public float shoulderAssist = 0.25f;
        [Tooltip("How hard the wrist turns to the requested aim (the direction the fingertips should point).")]
        [Range(0f, 1f)] public float handAimWeight = 0.8f;
        [Tooltip("Fraction of natural arm length where stretching starts. Below 1 the elbow keeps a bend instead of locking straight.")]
        [Range(0.7f, 1f)] public float reachSlack = 0.96f;
        [Tooltip("Seconds to blend a hand onto a new goal, and back off it when the caller stops asking.")]
        public float engageTime = 0.07f;
        public float releaseTime = 0.2f;

        static readonly HumanBodyBones[] ShoulderBones = { HumanBodyBones.LeftShoulder, HumanBodyBones.RightShoulder };
        static readonly HumanBodyBones[] UpperBones = { HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm };
        static readonly HumanBodyBones[] LowerBones = { HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm };
        static readonly HumanBodyBones[] HandBones = { HumanBodyBones.LeftHand, HumanBodyBones.RightHand };

        Arm[] _arms;

        /// <summary>Both arms found their bones; nothing else on this component does anything until they have.</summary>
        public bool IsReady => _arms != null && _arms[0].valid && _arms[1].valid;

        void Awake() => Build();

        void Build()
        {
            if (animator == null) animator = GetComponentInChildren<Animator>(true);
            _arms = new[] { BuildArm(Side.Left), BuildArm(Side.Right) };
            if (!IsReady) return;

            // A stretched arm leaves the skinned bounds behind, and a mesh culled by its own bind pose pops out of
            // view exactly when the arm is doing something interesting.
            foreach (var smr in animator.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                smr.updateWhenOffscreen = true;
        }

        Arm BuildArm(Side side)
        {
            var a = new Arm();
            if (animator == null || !animator.isHuman) return a;
            int i = (int)side;
            a.shoulder = animator.GetBoneTransform(ShoulderBones[i]);
            a.upper = animator.GetBoneTransform(UpperBones[i]);
            a.lower = animator.GetBoneTransform(LowerBones[i]);
            a.hand = animator.GetBoneTransform(HandBones[i]);
            a.valid = a.upper != null && a.lower != null && a.hand != null;
            if (!a.valid) return a;
            a.restLower = a.lower.localPosition;
            a.restHand = a.hand.localPosition;
            // Mixamo ends each limb in a *_end bone; it is the only fingertip direction this rig has.
            a.tip = a.hand.childCount > 0 ? a.hand.GetChild(0) : null;
            return a;
        }

        // ---------- what the hands are asked to do ----------

        /// <summary>
        /// Put a hand at <paramref name="position"/> this frame. Call it every frame you want the hand there;
        /// stop calling and it eases back to the animation. <paramref name="aim"/> is the world direction the
        /// fingertips should point (zero leaves the wrist alone). <paramref name="weight"/> below 1 is a partial
        /// reach — the hand drifts that far from its animated pose toward the goal.
        /// </summary>
        public void Reach(Side side, Vector3 position, float weight = 1f, Vector3 aim = default)
        {
            if (!IsReady) return;
            Arm a = _arms[(int)side];
            a.goal = position;
            a.aim = aim;
            a.request = Mathf.Clamp01(weight);
        }

        /// <summary>
        /// Leave a hand at <paramref name="position"/> for <paramref name="seconds"/> with no further calls. For
        /// snow that stops existing the moment you touch it: without this, fusing a ball snaps the arms home on
        /// the same frame the ball disappears and the press is never seen. Outranks <see cref="Reach"/> while it lasts.
        /// </summary>
        public void Pulse(Side side, Vector3 position, float seconds, Vector3 aim = default)
        {
            if (!IsReady || seconds <= 0f) return;
            Arm a = _arms[(int)side];
            a.pulseGoal = position;
            a.pulseAim = aim;
            a.pulseUntil = Time.time + seconds;
        }

        /// <summary>Where the shoulder joint currently is — the anchor callers should measure a hold pose from.</summary>
        public Vector3 ShoulderPosition(Side side)
        {
            if (!IsReady) return transform.position;
            Arm a = _arms[(int)side];
            return (a.shoulder != null ? a.shoulder : a.upper).position;
        }

        // ---------- per-frame ----------

        void LateUpdate()
        {
            if (!IsReady) return;
            float dt = Mathf.Min(Time.deltaTime, 1f / 30f); // a hitch must not launch the spring
            for (int i = 0; i < _arms.Length; i++) Apply(_arms[i], (Side)i, dt);
        }

        void Apply(Arm a, Side side, float dt)
        {
            // Undo last frame's stretch before reading anything, so lengths never compound.
            a.lower.localPosition = a.restLower;
            a.hand.localPosition = a.restHand;

            bool pulsing = Time.time < a.pulseUntil;
            float request = pulsing ? 1f : a.request;
            Vector3 goal = pulsing ? a.pulseGoal : a.goal;
            Vector3 aim = pulsing ? a.pulseAim : a.aim;
            a.request = 0f; // callers re-ask every frame

            Vector3 animHand = a.hand.position;
            bool idle = a.weight <= 0.002f;
            if (idle)
            {
                if (request <= 0f) { a.weight = 0f; a.pos = animHand; a.vel = Vector3.zero; return; }
                a.pos = animHand; // start the reach from wherever the animation left the hand
                a.vel = Vector3.zero;
            }

            float time = request > a.weight ? engageTime : releaseTime;
            a.weight = Mathf.Lerp(a.weight, request, 1f - Mathf.Exp(-dt / Mathf.Max(time, 0.001f)));

            // The hand trails its goal on a spring. Under-damped by default: an arm whipped out to a scoop
            // wobbles when it lands, which is most of the charm.
            Vector3 spring = request > 0f ? goal : animHand;
            float k = config != null ? config.handSpringStiffness : 260f;
            float c = config != null ? config.handSpringDamping : 24f;
            a.vel += ((spring - a.pos) * k - a.vel * c) * dt;
            a.pos += a.vel * dt;

            Solve(a, side, Vector3.Lerp(animHand, a.pos, a.weight), a.weight, aim);
        }

        void Solve(Arm a, Side side, Vector3 target, float weight, Vector3 aim)
        {
            // Shoulder first: a real reach starts at the collarbone.
            if (a.shoulder != null && shoulderAssist > 0f)
            {
                Vector3 from = a.hand.position - a.shoulder.position;
                Vector3 to = target - a.shoulder.position;
                if (from.sqrMagnitude > 1e-8f && to.sqrMagnitude > 1e-8f)
                    a.shoulder.rotation = Quaternion.Slerp(Quaternion.identity,
                        Quaternion.FromToRotation(from, to), shoulderAssist * weight) * a.shoulder.rotation;
            }

            // Bend plane: keep the animation's own elbow direction, nudged toward the hint so a straightened
            // arm cannot flip its bend inside out.
            Vector3 hint = elbowHint;
            if (side == Side.Left) hint.x = -hint.x;
            Vector3 bend = Vector3.Lerp(a.lower.position - a.upper.position,
                transform.TransformDirection(hint), elbowHintWeight * weight);

            float maxStretch = config != null ? Mathf.Max(1f, config.handMaxStretch) : 3.5f;
            Solve(a.upper, a.lower, a.hand, a.restLower, a.restHand, target, bend, maxStretch, reachSlack);

            if (aim.sqrMagnitude > 1e-6f && handAimWeight > 0f)
            {
                Vector3 tip = a.tip != null ? a.tip.position - a.hand.position : a.hand.position - a.lower.position;
                if (tip.sqrMagnitude > 1e-8f)
                    a.hand.rotation = Quaternion.Slerp(Quaternion.identity,
                        Quaternion.FromToRotation(tip, aim), handAimWeight * weight) * a.hand.rotation;
            }
        }

        // ---------- the solver ----------

        /// <summary>
        /// Two-bone IK that lengthens instead of giving up. Restores <paramref name="restLower"/>/
        /// <paramref name="restHand"/> first, so calling it repeatedly with the same bone rotations is idempotent.
        /// If the target sits past the arm's natural length the two bone offsets scale along their own bone (up to
        /// <paramref name="maxStretch"/>x) until it is in range; past that the arm points at the target fully
        /// extended and comes up short. Bone axes never enter into it — both bones are aimed with a delta
        /// rotation from where they currently point — so this works on any rig.
        /// </summary>
        /// <param name="bendDir">World direction the elbow is pushed toward; only its component across the
        /// shoulder-to-target axis is used.</param>
        /// <param name="slack">Fraction of natural length at which stretching starts. Below 1 the elbow keeps a bend.</param>
        public static void Solve(Transform upper, Transform lower, Transform hand,
            Vector3 restLower, Vector3 restHand, Vector3 target, Vector3 bendDir,
            float maxStretch, float slack = 0.96f)
        {
            lower.localPosition = restLower;
            hand.localPosition = restHand;

            Vector3 root = upper.position;
            Vector3 elbowNow = lower.position;
            float upperLen = Vector3.Distance(root, elbowNow);
            float lowerLen = Vector3.Distance(elbowNow, hand.position);
            if (upperLen < 1e-5f || lowerLen < 1e-5f) return;

            Vector3 toTarget = target - root;
            float dist = toTarget.magnitude;
            if (dist < 1e-5f) return;
            Vector3 dir = toTarget / dist;

            // Scaling a bone offset along its own bone keeps the elbow's direction from the shoulder, so the
            // bend plane measured below is still valid after the stretch.
            float natural = (upperLen + lowerLen) * Mathf.Clamp(slack, 0.5f, 1f);
            float stretch = Mathf.Clamp(dist / natural, 1f, Mathf.Max(1f, maxStretch));
            if (stretch > 1.0001f)
            {
                lower.localPosition = restLower * stretch;
                hand.localPosition = restHand * stretch;
                upperLen *= stretch;
                lowerLen *= stretch;
            }

            Vector3 bend = Vector3.ProjectOnPlane(bendDir, dir);
            if (bend.sqrMagnitude < 1e-10f) bend = Vector3.ProjectOnPlane(elbowNow - root, dir);
            if (bend.sqrMagnitude < 1e-10f) bend = Vector3.ProjectOnPlane(Vector3.down, dir);
            if (bend.sqrMagnitude < 1e-10f) return;
            bend.Normalize();

            // Law of cosines for the elbow, target clamped into what the (stretched) arm can actually cover.
            float reach = Mathf.Clamp(dist, Mathf.Abs(upperLen - lowerLen) + 1e-4f, (upperLen + lowerLen) * 0.999f);
            float cos = Mathf.Clamp((upperLen * upperLen + reach * reach - lowerLen * lowerLen)
                                    / (2f * upperLen * reach), -1f, 1f);
            Vector3 elbow = root + dir * (upperLen * cos) + bend * (upperLen * Mathf.Sqrt(1f - cos * cos));

            Aim(upper, lower.position, elbow);       // read before the rotation lands, by C# argument order
            Aim(lower, hand.position, root + dir * reach);
        }

        /// <summary>Turn <paramref name="bone"/> so what is at <paramref name="from"/> ends up at <paramref name="to"/>.</summary>
        static void Aim(Transform bone, Vector3 from, Vector3 to)
        {
            Vector3 p = bone.position;
            Vector3 a = from - p, b = to - p;
            if (a.sqrMagnitude < 1e-10f || b.sqrMagnitude < 1e-10f) return;
            bone.rotation = Quaternion.FromToRotation(a, b) * bone.rotation;
        }

        class Arm
        {
            public Transform shoulder, upper, lower, hand, tip;
            public Vector3 restLower, restHand;
            public bool valid;

            public Vector3 goal, aim;   // this frame's request
            public float request;
            public Vector3 pulseGoal, pulseAim;
            public float pulseUntil;

            public Vector3 pos, vel;    // spring, world space
            public float weight;
        }
    }
}
