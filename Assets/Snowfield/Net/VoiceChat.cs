using System.Threading.Tasks;
using Unity.Services.Vivox;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Snowfield.Net
{
    /// <summary>
    /// Positional voice over Vivox: one 3D channel per session, audible to ~32 m, panned from avatar positions.
    /// Open mic by default (snow fields are quiet places); M toggles mute. The speaker/listener position is the
    /// camera pivot — the chibi rig's eye line — reported a few times a second.
    /// Requires Vivox to be enabled for this project in the Unity Cloud dashboard; fails soft without it.
    /// </summary>
    public static class VoiceChat
    {
        public static bool Joined { get; private set; }
        public static bool Muted { get; private set; }

        static string _channel;
        static bool _initialized;
        static VoiceTicker _ticker;
        static Task _leaveTask;

        public static async Task JoinAsync(string sessionId)
        {
            // A rejoin races the previous session's teardown: joining while LeaveAsync is suspended between
            // channel-leave and logout would get its fresh channel torn down under it. Wait the leave out.
            if (_leaveTask != null)
            {
                try { await _leaveTask; } catch { /* logged inside */ }
                _leaveTask = null;
            }
            if (VivoxService.Instance == null)
            {
                Debug.LogWarning("[SnowNet] Vivox service not present (not enabled in the Unity Cloud dashboard?) — no voice.");
                return;
            }
            if (!_initialized)
            {
                await VivoxService.Instance.InitializeAsync();
                _initialized = true;
            }
            if (!VivoxService.Instance.IsLoggedIn)
                await VivoxService.Instance.LoginAsync();

            _channel = "snow-" + sessionId;
            // Identical numbers on every peer or they land in different channels (baked into the channel URI).
            var positional = new Channel3DProperties(32, 2, 1.0f, AudioFadeModel.InverseByDistance);
            await VivoxService.Instance.JoinPositionalChannelAsync(_channel, ChatCapability.AudioOnly, positional);
            Joined = true;
            Debug.Log($"[SnowNet] Voice up in '{_channel}' — open mic, M to mute");

            if (_ticker == null)
            {
                var go = new GameObject("VoiceTicker");
                Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideInHierarchy;
                _ticker = go.AddComponent<VoiceTicker>();
            }
        }

        public static Task LeaveAsync() => _leaveTask = LeaveInternalAsync();

        static async Task LeaveInternalAsync()
        {
            if (!Joined || VivoxService.Instance == null) return;
            Joined = false;
            try
            {
                // The channel leave routinely throws after a network drop; logout must still run.
                try { await VivoxService.Instance.LeaveAllChannelsAsync(); }
                catch (System.Exception e) { Debug.LogWarning($"[SnowNet] Voice channel leave failed: {e.Message}"); }
                await VivoxService.Instance.LogoutAsync();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[SnowNet] Voice logout failed: {e.Message}");
            }
            finally
            {
                _channel = null;
            }
        }

        public static void ToggleMute()
        {
            if (VivoxService.Instance == null) return;
            Muted = !Muted;
            if (Muted) VivoxService.Instance.MuteInputDevice();
            else VivoxService.Instance.UnmuteInputDevice();
            Debug.Log(Muted ? "[SnowNet] Mic muted" : "[SnowNet] Mic live");
        }

        /// <summary>Reports the local head position into the positional channel and watches the mute key.</summary>
        sealed class VoiceTicker : MonoBehaviour
        {
            float _timer;

            void Update()
            {
                var kb = Keyboard.current;
                if (kb != null && kb.mKey.wasPressedThisFrame && Joined) ToggleMute();

                if (!Joined || _channel == null) return;
                _timer += Time.unscaledDeltaTime;
                if (_timer < 0.25f) return;
                _timer = 0f;

                var rig = NetAvatarHooks.GetLocalPlayer?.Invoke();
                Transform head = rig?.head != null ? rig.Value.head : rig?.root;
                if (head == null) return;
                try
                {
                    VivoxService.Instance.Set3DPosition(head.gameObject, _channel);
                }
                catch
                {
                    // Channel mid-teardown; next tick will see Joined == false.
                }
            }
        }
    }
}
