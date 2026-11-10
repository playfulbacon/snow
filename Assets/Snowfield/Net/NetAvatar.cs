using Snowfield.Player;
using Snowfield.Sculpture;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

namespace Snowfield.Net
{
    /// <summary>
    /// The player prefab: another player's body in your field. Owner-authoritative NetworkTransform carries
    /// position + yaw; this behaviour carries the four locomotion animator params so the remote model walks.
    /// The scene's real Player (camera, input, tools) is never a NetworkObject — the owner just copies its own
    /// root pose and animator floats onto this avatar every frame, and hides the avatar from itself.
    /// The animator controller was authored for this: LocomotionSpeed defaults to 1 and IsGrounded to true, so
    /// an avatar with no sync yet still idles at authored rate instead of T-posing (PlayerSceneSetup).
    /// </summary>
    public sealed class NetAvatar : NetworkBehaviour
    {
        static readonly int MoveXHash = Animator.StringToHash("MoveX");
        static readonly int MoveYHash = Animator.StringToHash("MoveY");
        static readonly int SpeedHash = Animator.StringToHash("Speed");
        static readonly int LocomotionSpeedHash = Animator.StringToHash("LocomotionSpeed");
        static readonly int GroundedHash = Animator.StringToHash("IsGrounded");

        /// <summary>
        /// The animator parameters mirrored onto remote avatars, kept in step with FirstPersonPlayer.controller
        /// by a test. A parameter that drives the legs and is missing here does not just look wrong — it breaks
        /// anything reading the animated pose. LocomotionSpeed is the cautionary tale: without it a remote's
        /// legs cycled at the authored clip rate while its body travelled at the real speed, so the planted
        /// foot slid ~1.5 m/s and SnowFootprints never saw a footfall to stamp.
        /// </summary>
        public static readonly string[] SyncedParameters =
            { "MoveX", "MoveY", "Speed", "LocomotionSpeed", "IsGrounded" };

        readonly NetworkVariable<float> _moveX = new NetworkVariable<float>(0f,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        readonly NetworkVariable<float> _moveY = new NetworkVariable<float>(0f,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        readonly NetworkVariable<float> _speed = new NetworkVariable<float>(0f,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        // Default 1 matches the controller's own default, so an avatar that has not heard from its owner yet
        // still animates at the authored rate instead of freezing.
        readonly NetworkVariable<float> _locomotionSpeed = new NetworkVariable<float>(1f,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        readonly NetworkVariable<bool> _grounded = new NetworkVariable<bool>(true,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        Animator _animator;
        bool _followWarned;
        CapsuleCollider _body;

        // Hands. The owner mirrors its own rig's goals; everyone else replays them onto this avatar's rig.
        const float HandSendInterval = 1f / 15f;
        HandRig _hands;
        readonly HandSyncReceiver _handReceiver = new HandSyncReceiver();
        float _handTimer;
        int _handReleaseSends;

        public override void OnNetworkSpawn()
        {
            _animator = GetComponentInChildren<Animator>(true);
            _hands = GetComponent<HandRig>();
            _body = EnsureBody();
            if (IsOwner)
            {
                // Your own avatar exists only so the others can see you.
                foreach (var r in GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                if (_animator != null) _animator.enabled = false;
                if (_hands != null) _hands.enabled = false; // nothing to solve on a hidden body
                // ...and it sits exactly where your own capsule is, so a live collider here would have the
                // character controller shoving itself around, and your own snowballs hitting you in the back.
                if (_body != null) _body.enabled = false;
                SnapToLocalPlayer();
            }
            else
            {
                MakeHittable();
                NetAvatarHooks.RemoteAvatarSpawned?.Invoke(gameObject);
            }
        }

        /// <summary>
        /// A body for the ball to hit — and for you to walk into. Self-healing rather than only authored in
        /// NetSceneSetup: a prefab from before snowballs could hit people has no collider, and a remote player
        /// you can throw straight through is the kind of thing that reads as the throw being broken.
        /// Matches the local player's capsule (Game.prefab): 2.1 m tall, bottom-anchored, 0.42 m radius.
        /// </summary>
        CapsuleCollider EnsureBody()
        {
            var capsule = GetComponent<CapsuleCollider>();
            if (capsule != null) return capsule;
            capsule = gameObject.AddComponent<CapsuleCollider>();
            capsule.height = 2.1015205f;
            capsule.radius = 0.42030412f;
            capsule.center = new Vector3(0f, 1.0707603f, 0f);
            return capsule;
        }

        /// <summary>
        /// Somebody else's body, on this machine. Their flights do not run here (physics stays with the thrower),
        /// so the only hits this ever sees are from balls thrown by the player at this keyboard — and the shove
        /// has to happen on the machine that owns the body being shoved, hence the RPC rather than a local push.
        /// </summary>
        void MakeHittable()
        {
            var target = GetComponent<SnowballTarget>();
            if (target == null) target = gameObject.AddComponent<SnowballTarget>();
            target.onHit = hit => HitRpc(hit.point, hit.normal, hit.velocity, hit.mass, hit.radius);
        }

        /// <summary>
        /// "You have been hit." Sent by the thrower to the owner of the body, who sprays the snow and takes the
        /// shove on their own capsule. Reliable: a knock the victim never hears about is a throw that visibly
        /// landed and did nothing. NGO proxies a client-to-owner call through the host on its own.
        /// </summary>
        [Rpc(SendTo.Owner)]
        void HitRpc(Vector3 point, Vector3 normal, Vector3 velocity, float mass, float radius)
        {
            var rig = NetAvatarHooks.GetLocalPlayer?.Invoke();
            var mine = rig?.root != null ? rig.Value.root.GetComponent<SnowballTarget>() : null;
            var hit = new SnowballTarget.Hit
            { point = point, normal = normal, velocity = velocity, mass = mass, radius = radius };
            if (mine != null) mine.Strike(hit);
            else SnowBurst.Play(point, normal, radius, Mathf.Max(1f, hit.Impact / 3f)); // at least show the splat
        }

        void SnapToLocalPlayer()
        {
            var rig = NetAvatarHooks.GetLocalPlayer?.Invoke();
            if (rig?.root == null) return;
            var nt = GetComponent<NetworkTransform>();
            if (nt != null) nt.Teleport(rig.Value.root.position, rig.Value.root.rotation, transform.localScale);
            else transform.SetPositionAndRotation(rig.Value.root.position, rig.Value.root.rotation);
        }

        void Update()
        {
            if (!IsSpawned) return;
            if (IsOwner) PushFromLocalPlayer();
            else
            {
                PullIntoAnimator();
                // Before the rig's LateUpdate, which consumes the reach requests this posts.
                _handReceiver.Tick(_hands, transform, Time.deltaTime);
            }
        }

        void PushFromLocalPlayer()
        {
            var rig = NetAvatarHooks.GetLocalPlayer?.Invoke();
            if (rig?.root == null)
            {
                if (!_followWarned) { _followWarned = true; Debug.LogWarning("[SnowNet] No local player rig registered; avatar will not follow."); }
                return;
            }
            transform.SetPositionAndRotation(rig.Value.root.position, rig.Value.root.rotation);
            PushHands(rig.Value);
            var anim = rig.Value.animator;
            if (anim == null || !anim.isActiveAndEnabled) return;
            SetIfChanged(_moveX, anim.GetFloat(MoveXHash));
            SetIfChanged(_moveY, anim.GetFloat(MoveYHash));
            SetIfChanged(_speed, anim.GetFloat(SpeedHash));
            SetIfChanged(_locomotionSpeed, anim.GetFloat(LocomotionSpeedHash));
            if (_grounded.Value != anim.GetBool(GroundedHash)) _grounded.Value = anim.GetBool(GroundedHash);
        }

        void PushHands(in NetAvatarHooks.LocalPlayerRig rig)
        {
            if (rig.hands == null || rig.root == null) return;
            _handTimer += Time.deltaTime;
            if (_handTimer < HandSendInterval) return;

            var pose = HandSyncPose.Sample(rig.hands, rig.root);
            if (pose.AnyActive)
            {
                _handReleaseSends = 2; // idle again later: say so a couple of times, since this is unreliable
            }
            else if (_handReleaseSends > 0)
            {
                _handReleaseSends--;
            }
            else
            {
                return; // hands are down and everyone has been told; nothing to say
            }

            _handTimer = 0f;
            HandsRpc(pose);
        }

        /// <summary>
        /// Owner → everyone else. NGO proxies a client's RPC through the host on its own (keeping the unreliable
        /// delivery and the true sender), so no manual relay is needed; the owner permission is enforced there too.
        /// Unreliable and latest-wins: a dropped hand pose is one skipped frame of arm, corrected 66 ms later.
        /// </summary>
        [Rpc(SendTo.NotMe, Delivery = RpcDelivery.Unreliable, InvokePermission = RpcInvokePermission.Owner)]
        void HandsRpc(HandSyncPose pose)
        {
            _handReceiver.Receive(pose);
        }

        static void SetIfChanged(NetworkVariable<float> v, float value)
        {
            if (Mathf.Abs(v.Value - value) > 0.01f) v.Value = value;
        }

        void PullIntoAnimator()
        {
            if (_animator == null || !_animator.isActiveAndEnabled) return;
            _animator.SetFloat(MoveXHash, _moveX.Value);
            _animator.SetFloat(MoveYHash, _moveY.Value);
            _animator.SetFloat(SpeedHash, _speed.Value);
            _animator.SetFloat(LocomotionSpeedHash, _locomotionSpeed.Value);
            _animator.SetBool(GroundedHash, _grounded.Value);
        }
    }
}
