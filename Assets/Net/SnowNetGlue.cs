using Snowfield.Net;
using UnityEngine;

namespace SnowDays
{
    /// <summary>
    /// Assembly-CSharp side of the networking seam (the SnowGround inversion pattern: Snowfield.* cannot see
    /// SnowDays types, so this registers them into <see cref="NetAvatarHooks"/> at load).
    ///   - Hands the net layer the local player rig (root/animator/camera pivot) for avatar-follow and voice.
    ///   - Gives every remote avatar SnowFootprints so other players press trails into your snow window
    ///     (SnowDeformSystem only auto-attaches footprints to the one PlayerController — the local player).
    /// </summary>
    public static class SnowNetGlue
    {
        static PlayerController _cachedLocal;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Register()
        {
            // Plain assignment (not +=): survives disabled domain reloads without stacking subscriptions.
            NetAvatarHooks.GetLocalPlayer = FindLocalRig;
            NetAvatarHooks.RemoteAvatarSpawned = OnRemoteAvatarSpawned;
        }

        static NetAvatarHooks.LocalPlayerRig FindLocalRig()
        {
            if (_cachedLocal == null)
            {
                foreach (var pc in Object.FindObjectsByType<PlayerController>(FindObjectsSortMode.None))
                    if (pc.IsLocal) { _cachedLocal = pc; break; }
            }
            var rig = new NetAvatarHooks.LocalPlayerRig();
            if (_cachedLocal != null)
            {
                rig.root = _cachedLocal.transform;
                rig.animator = _cachedLocal.GetComponentInChildren<Animator>();
                rig.head = _cachedLocal.transform.Find("CameraPivot");
                rig.hands = _cachedLocal.GetComponent<Snowfield.Player.HandRig>();
            }
            return rig;
        }

        static void OnRemoteAvatarSpawned(GameObject avatar)
        {
            if (avatar.GetComponent<SnowFootprints>() == null)
                avatar.AddComponent<SnowFootprints>();
        }
    }
}
