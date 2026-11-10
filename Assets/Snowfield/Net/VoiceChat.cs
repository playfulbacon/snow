using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Services.Core;
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

        /// <summary>
        /// Mic state. Mirrored onto <see cref="Snowfield.Player.NetStatus"/> so the HUD's hint can offer the
        /// action you do not currently have ("mute" or "unmute") without reaching into this assembly.
        /// </summary>
        public static bool Muted
        {
            get => s_Muted;
            private set { s_Muted = value; Snowfield.Player.NetStatus.MicMuted = value; }
        }
        static bool s_Muted;

        static string _channel;
        static bool _initialized;
        static VoiceTicker _ticker;
        static Task _leaveTask;

        // ---------------------------------------------------------------- devices

        /// <summary>
        /// Microphones and outputs Vivox can see, by name. Empty until <see cref="RefreshDevicesAsync"/> has
        /// brought the audio client up - which is why the audio page asks for it when it opens rather than
        /// assuming a list is already there.
        /// </summary>
        public static IReadOnlyList<string> MicDevices => s_Mics;
        public static IReadOnlyList<string> OutputDevices => s_Outputs;
        static readonly List<string> s_Mics = new List<string>();
        static readonly List<string> s_Outputs = new List<string>();

        /// <summary>Raised when the device lists change (a headset plugged in, or the first enumeration).</summary>
        public static event Action DevicesChanged;

        /// <summary>True once the audio client is up and the device lists mean something.</summary>
        public static bool DevicesKnown => _initialized;

        /// <summary>
        /// Bring the audio client up far enough to list and pick devices. Initialising Vivox is enough for that
        /// - no login, no channel - so the player can choose their microphone before ever joining a field, which
        /// is exactly when they want to. Fails soft: without Unity Services (no project link, no network) the
        /// lists stay empty and the audio page says so rather than offering an empty dropdown.
        /// </summary>
        public static async Task RefreshDevicesAsync()
        {
            try
            {
                if (!await EnsureClientAsync()) return;
                CacheDevices();
                ApplyDevices();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SnowNet] Could not list audio devices: {e.Message}");
            }
        }

        /// <summary>
        /// Vivox initialised once, whoever asks first - the audio page or a session starting. The logout hooks
        /// are part of that once: without them Vivox can tear its own login down and Joined stays true forever.
        /// </summary>
        static async Task<bool> EnsureClientAsync()
        {
            if (UnityServices.State != ServicesInitializationState.Initialized)
                await UnityServices.InitializeAsync();
            if (VivoxService.Instance == null)
            {
                Debug.LogWarning("[SnowNet] Vivox service not present (not enabled in the Unity Cloud dashboard?) - no voice.");
                return false;
            }
            if (_initialized) return true;

            await VivoxService.Instance.InitializeAsync();
            VivoxService.Instance.LoggedOut += () => Joined = false;
            VivoxService.Instance.ConnectionFailedToRecover += () => Joined = false;
            // A headset arriving or leaving mid-game changes both what the page should show and what is in use.
            VivoxService.Instance.AvailableInputDevicesChanged += OnDeviceListChanged;
            VivoxService.Instance.AvailableOutputDevicesChanged += OnDeviceListChanged;
            _initialized = true;
            return true;
        }

        static void OnDeviceListChanged()
        {
            CacheDevices();
            ApplyDevices();
        }

        static void CacheDevices()
        {
            var vivox = VivoxService.Instance;
            if (vivox == null) return;
            s_Mics.Clear();
            s_Outputs.Clear();
            foreach (var device in vivox.AvailableInputDevices)
                if (device != null && !string.IsNullOrEmpty(device.DeviceName)) s_Mics.Add(device.DeviceName);
            foreach (var device in vivox.AvailableOutputDevices)
                if (device != null && !string.IsNullOrEmpty(device.DeviceName)) s_Outputs.Add(device.DeviceName);
            DevicesChanged?.Invoke();
        }

        /// <summary>
        /// Put the player's chosen devices into use. Matched by name, because that is what was stored: an index
        /// would point at a different microphone every time the list reorders. A name that is not plugged in
        /// today simply does not match, and Vivox keeps using the system default - which is the right answer.
        /// </summary>
        public static void ApplyDevices()
        {
            var vivox = VivoxService.Instance;
            if (vivox == null) return;

            string mic = Snowfield.Config.GameSettings.MicDevice;
            if (!string.IsNullOrEmpty(mic) && vivox.ActiveInputDevice?.DeviceName != mic)
                foreach (var device in vivox.AvailableInputDevices)
                    if (device != null && device.DeviceName == mic) { Fire(vivox.SetActiveInputDeviceAsync(device)); break; }

            string output = Snowfield.Config.GameSettings.OutputDevice;
            if (!string.IsNullOrEmpty(output) && vivox.ActiveOutputDevice?.DeviceName != output)
                foreach (var device in vivox.AvailableOutputDevices)
                    if (device != null && device.DeviceName == output) { Fire(vivox.SetActiveOutputDeviceAsync(device)); break; }
        }

        /// <summary>A device switch nobody waits on; a failure is a warning, not a dead audio page.</summary>
        static async void Fire(Task task)
        {
            try { await task; }
            catch (Exception e) { Debug.LogWarning($"[SnowNet] Audio device switch failed: {e.Message}"); }
        }

        public static async Task JoinAsync(string sessionId)
        {
            // A rejoin races the previous session's teardown: joining while LeaveAsync is suspended between
            // channel-leave and logout would get its fresh channel torn down under it. Wait the leave out.
            if (_leaveTask != null)
            {
                try { await _leaveTask; } catch { /* logged inside */ }
                _leaveTask = null;
            }
            if (!await EnsureClientAsync()) return;
            if (!VivoxService.Instance.IsLoggedIn)
                await VivoxService.Instance.LoginAsync();

            _channel = "snow-" + sessionId;
            // Identical numbers on every peer or they land in different channels (baked into the channel URI).
            var positional = new Channel3DProperties(32, 2, 1.0f, AudioFadeModel.InverseByDistance);
            await VivoxService.Instance.JoinPositionalChannelAsync(_channel, ChatCapability.AudioOnly, positional);
            Joined = true;
            SetOutputVolume(Snowfield.Config.GameSettings.VoiceVolume);
            CacheDevices();
            ApplyDevices();   // the microphone the player picked, not whatever Vivox picked for them
            Debug.Log($"[SnowNet] Voice up in '{_channel}' — open mic, M to mute");

            if (_ticker == null)
            {
                var go = new GameObject("VoiceTicker");
                UnityEngine.Object.DontDestroyOnLoad(go); // qualified: this file also uses System.Object
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

        /// <summary>
        /// Other players' voices, from a 0..1 slider. Vivox takes a volume *adjustment* of -50..50 where 0 is
        /// "as recorded", so the slider's top is unity gain and everything below it only ever turns people down —
        /// mapping 1.0 to +50 would ship a game that shouts at you by default.
        /// </summary>
        public static void SetOutputVolume(float volume01)
        {
            if (VivoxService.Instance == null) return;
            int adjustment = Mathf.RoundToInt(Mathf.Lerp(-50f, 0f, Mathf.Clamp01(volume01)));
            try { VivoxService.Instance.SetOutputDeviceVolume(adjustment); }
            catch (System.Exception e) { Debug.LogWarning($"[SnowNet] Voice volume failed: {e.Message}"); }
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
