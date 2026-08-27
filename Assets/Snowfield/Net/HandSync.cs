using Snowfield.Player;
using Unity.Netcode;
using UnityEngine;

namespace Snowfield.Net
{
    /// <summary>
    /// One frame of another player's arm intent. The hands go where the snow is, so this is the goal the owner's
    /// <see cref="HandRig"/> was solved against — not bone rotations, which are the rig's own business and would
    /// cost twenty times as much to send.
    ///
    /// Stored in the owner's BODY space, not world space. The owner samples its goal and its body pose in the
    /// same frame while a remote's body is an interpolated approximation, so a world-space goal would leave the
    /// hand correct and the arm visibly mis-stretched against a body that has not caught up. Body-relative keeps
    /// the arm attached to the shoulder it belongs to, and — because carried snow rides anchors on the body —
    /// it is also the frame most of these goals were authored in.
    /// </summary>
    public struct HandSyncPose : INetworkSerializable
    {
        public Vector3 LeftPosition, LeftAim;
        public Vector3 RightPosition, RightAim;
        public float LeftWeight, RightWeight;

        public bool LeftActive => LeftWeight > 0.001f;
        public bool RightActive => RightWeight > 0.001f;
        public bool AnyActive => LeftActive || RightActive;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref LeftPosition);
            serializer.SerializeValue(ref LeftAim);
            serializer.SerializeValue(ref RightPosition);
            serializer.SerializeValue(ref RightAim);
            serializer.SerializeValue(ref LeftWeight);
            serializer.SerializeValue(ref RightWeight);
        }

        /// <summary>Read what <paramref name="rig"/> was asked for this frame, in <paramref name="body"/>'s space.</summary>
        public static HandSyncPose Sample(HandRig rig, Transform body)
        {
            var pose = default(HandSyncPose);
            if (rig == null || body == null || !rig.IsReady) return pose;

            var left = rig.CurrentGoal(HandRig.Side.Left);
            if (left.Active)
            {
                pose.LeftPosition = body.InverseTransformPoint(left.Position);
                pose.LeftAim = body.InverseTransformDirection(left.Aim);
                pose.LeftWeight = left.Weight;
            }
            var right = rig.CurrentGoal(HandRig.Side.Right);
            if (right.Active)
            {
                pose.RightPosition = body.InverseTransformPoint(right.Position);
                pose.RightAim = body.InverseTransformDirection(right.Aim);
                pose.RightWeight = right.Weight;
            }
            return pose;
        }
    }

    /// <summary>
    /// Replays a remote player's hand intent onto their avatar's rig. Poses arrive ~15 times a second; the goal
    /// is eased between them so the arms do not step, and <see cref="HandRig"/>'s own spring and weight blending
    /// do the rest — including easing a hand home the moment the owner stops asking for it.
    /// </summary>
    public sealed class HandSyncReceiver
    {
        /// <summary>Stop reaching if the owner has gone this long without a word (their release packet was lost,
        /// or they dropped) — better a hand that returns to the animation than one stuck in the air.</summary>
        const float StaleAfter = 0.5f;
        const float FollowRate = 30f; // e-folds/second toward the latest goal

        HandSyncPose _target;
        bool _hasPose;
        float _age;

        Vector3 _leftPos, _leftAim, _rightPos, _rightAim;
        bool _leftActive, _rightActive;

        public void Receive(in HandSyncPose pose)
        {
            _target = pose;
            _hasPose = true;
            _age = 0f;
        }

        /// <summary>Call every frame before the rig's LateUpdate. <paramref name="body"/> is the avatar root.</summary>
        public void Tick(HandRig rig, Transform body, float dt)
        {
            if (!_hasPose || rig == null || body == null || !rig.IsReady) return;
            _age += dt;
            if (_age > StaleAfter) { _leftActive = _rightActive = false; return; }

            float k = 1f - Mathf.Exp(-FollowRate * dt);
            Follow(ref _leftPos, ref _leftAim, ref _leftActive,
                _target.LeftPosition, _target.LeftAim, _target.LeftActive, k);
            Follow(ref _rightPos, ref _rightAim, ref _rightActive,
                _target.RightPosition, _target.RightAim, _target.RightActive, k);

            if (_leftActive)
                rig.Reach(HandRig.Side.Left, body.TransformPoint(_leftPos), _target.LeftWeight,
                    body.TransformDirection(_leftAim));
            if (_rightActive)
                rig.Reach(HandRig.Side.Right, body.TransformPoint(_rightPos), _target.RightWeight,
                    body.TransformDirection(_rightAim));
        }

        static void Follow(ref Vector3 pos, ref Vector3 aim, ref bool active,
            Vector3 targetPos, Vector3 targetAim, bool targetActive, float k)
        {
            // Engaging: start at the new goal. Easing in from wherever the hand was last asked to be would
            // sweep it across the world, and the rig already blends the weight in from the animated pose.
            if (targetActive && !active) { pos = targetPos; aim = targetAim; }
            else if (targetActive) { pos = Vector3.Lerp(pos, targetPos, k); aim = Vector3.Lerp(aim, targetAim, k); }
            active = targetActive;
        }
    }
}
