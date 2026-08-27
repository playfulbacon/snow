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
        static readonly int GroundedHash = Animator.StringToHash("IsGrounded");

        readonly NetworkVariable<float> _moveX = new NetworkVariable<float>(0f,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        readonly NetworkVariable<float> _moveY = new NetworkVariable<float>(0f,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        readonly NetworkVariable<float> _speed = new NetworkVariable<float>(0f,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        readonly NetworkVariable<bool> _grounded = new NetworkVariable<bool>(true,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        Animator _animator;
        bool _followWarned;

        public override void OnNetworkSpawn()
        {
            _animator = GetComponentInChildren<Animator>(true);
            if (IsOwner)
            {
                // Your own avatar exists only so the others can see you.
                foreach (var r in GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                if (_animator != null) _animator.enabled = false;
                SnapToLocalPlayer();
            }
            else
            {
                NetAvatarHooks.RemoteAvatarSpawned?.Invoke(gameObject);
            }
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
            else PullIntoAnimator();
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
            var anim = rig.Value.animator;
            if (anim == null || !anim.isActiveAndEnabled) return;
            SetIfChanged(_moveX, anim.GetFloat(MoveXHash));
            SetIfChanged(_moveY, anim.GetFloat(MoveYHash));
            SetIfChanged(_speed, anim.GetFloat(SpeedHash));
            if (_grounded.Value != anim.GetBool(GroundedHash)) _grounded.Value = anim.GetBool(GroundedHash);
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
            _animator.SetBool(GroundedHash, _grounded.Value);
        }
    }
}
