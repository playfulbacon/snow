using System;
using UnityEngine;

namespace Snowfield.Net
{
    /// <summary>
    /// The inversion seam between Snowfield.Net and Assembly-CSharp (SnowDays), mirroring the SnowGround
    /// pattern: Snowfield.* may not reference Assembly-CSharp, so the SnowDays side registers what the net
    /// layer needs (the local player rig) and reacts to what it produces (remote avatars needing footprints).
    /// </summary>
    public static class NetAvatarHooks
    {
        public struct LocalPlayerRig
        {
            public Transform root;      // yaw + position live here
            public Animator animator;   // MoveX/MoveY/Speed/IsGrounded written by PlayerController
            public Transform head;      // camera pivot: the voice listener/speaker position
        }

        /// <summary>Registered from Assembly-CSharp (SnowNetGlue). Null fields mean "not available yet".</summary>
        public static Func<LocalPlayerRig> GetLocalPlayer;

        /// <summary>Raised when a remote player's avatar spawns; SnowDays adds SnowFootprints to it here.</summary>
        public static Action<GameObject> RemoteAvatarSpawned;
    }
}
