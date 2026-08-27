using System;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Snowfield.Net
{
    /// <summary>
    /// Joins the shared field on <b>Shift+N</b>: UGS anonymous sign-in, then quick-join any open public session
    /// or create one (P2P over Relay — the Sessions package starts the NGO host/client itself; never call
    /// StartHost/StartClient around it). Shift+N again leaves. The game starts single-player and every failure
    /// degrades back to it, so nothing about going online is load-bearing.
    /// Host leaving kills the session (NGO has no host migration); clients notice and re-quick-join, so the
    /// survivors reconvene under a new host after a few seconds.
    /// </summary>
    public sealed class NetBootstrap : MonoBehaviour
    {
        [Tooltip("Join on play instead of waiting for Shift+N. Off by default — going online is a choice. " +
                 "MainSceneSetup rewrites this, so use --snow-autoconnect for a test instance instead.")]
        public bool autoConnect;
        [Tooltip("Players per session incl. host; overflow quick-joins into a fresh session.")]
        public int maxPlayers = 8;
        public string sessionName = "snowfield";
        [Tooltip("Seconds to hunt for an existing session before hosting a new one.")]
        public float quickJoinTimeout = 6f;
        [Tooltip("Seconds before a dropped client tries to find a new session.")]
        public float rejoinDelay = 4f;
        [Tooltip("Player avatar prefab (NetworkObject + NetAvatar). Wired by MainSceneSetup.")]
        public GameObject avatarPrefab;
        [Tooltip("World-sync channel prefab (NetworkObject + SnowNetChannel); host-spawned. Wired by MainSceneSetup.")]
        public GameObject channelPrefab;

        /// <summary>One-line connection state, for logs and the HUD.</summary>
        public static string Status
        {
            get => s_Status;
            private set { s_Status = value; Snowfield.Player.NetStatus.Line = value == Offline ? "" : "net: " + value; }
        }
        static string s_Status = Offline;
        const string Offline = "offline";

        ISession _session;
        bool _connecting;
        float _retryAt = -1f;
        int _attempts;
        const int MaxAttempts = 5;

        async void Start()
        {
            Status = Offline; // also clears any HUD line left by a previous play session
            if (!autoConnect && !AutoConnectRequested()) return;
            await ConnectAsync();
        }

        void Update()
        {
            if (ToggleChordPressed(Keyboard.current)) Toggle();

            if (_retryAt > 0f && Time.unscaledTime >= _retryAt && !_connecting)
            {
                _retryAt = -1f;
                _ = ConnectAsync();
            }
        }

        /// <summary>
        /// Shift+N — go online, or leave. Static and public so the binding itself is testable without a live
        /// service standing behind it.
        /// </summary>
        public static bool ToggleChordPressed(Keyboard keyboard) =>
            keyboard != null
            && keyboard.nKey.wasPressedThisFrame
            && (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed);

        /// <summary>Connect if offline, leave if connected. A press while still connecting is ignored.</summary>
        public void Toggle()
        {
            if (_connecting)
            {
                Debug.Log("[SnowNet] Still connecting — press Shift+N again once it settles.");
                return;
            }
            if (_session != null) { _ = DisconnectAsync(); return; }
            _attempts = 0;   // a deliberate press starts the retry budget over
            _retryAt = -1f;
            _ = ConnectAsync();
        }

        /// <summary>
        /// Leave the shared field and go back to sculpting alone. The world in memory stays as it is — a client
        /// keeps the field it was just sharing (and keeps saving it to slot 1); their own field returns on the
        /// next launch. Deliberately no auto-rejoin afterwards.
        /// </summary>
        public async Task DisconnectAsync()
        {
            var session = _session;
            _session = null;   // session events check this, so nulling first suppresses the rejoin path
            _retryAt = -1f;
            _attempts = 0;
            Status = Offline;
            Debug.Log("[SnowNet] Leaving the shared field");
            try { await VoiceChat.LeaveAsync(); }
            catch (Exception e) { Debug.LogWarning($"[SnowNet] Voice leave failed: {e.Message}"); }
            try
            {
                if (session != null)
                {
                    if (session.IsHost) await session.AsHost().DeleteAsync();
                    else await session.LeaveAsync();
                }
            }
            catch (Exception e) { Debug.LogWarning($"[SnowNet] Session leave failed: {e.Message}"); }
        }

        public async Task ConnectAsync()
        {
            if (_connecting || _session != null) return;
            if (NetworkManager.Singleton == null)
            {
                Debug.LogWarning("[SnowNet] No NetworkManager in scene — staying offline. Run Snowfield ▸ Ensure Main Scene Sculpting.");
                return;
            }
            _connecting = true;
            _attempts++;
            RegisterPrefabs(NetworkManager.Singleton);
            try
            {
                Status = "signing in";
                if (UnityServices.State != ServicesInitializationState.Initialized)
                {
                    var options = new InitializationOptions();
                    string profile = ProfileOverride();
                    if (!string.IsNullOrEmpty(profile)) options.SetProfile(profile);
                    await UnityServices.InitializeAsync(options);
                }
                if (!AuthenticationService.Instance.IsSignedIn)
                    await AuthenticationService.Instance.SignInAnonymouslyAsync();
            }
            catch (Exception e)
            {
                Status = "offline (services unavailable)";
                Debug.LogWarning($"[SnowNet] Unity Services init/sign-in failed — retrying. {e.Message}");
                _connecting = false;
                ScheduleRetry();
                return;
            }

            try
            {
                Status = "finding a field";
                var sessionOptions = new SessionOptions
                {
                    Name = sessionName,
                    MaxPlayers = Mathf.Clamp(maxPlayers, 2, 32),
                }.WithRelayNetwork();
                var quickJoin = new QuickJoinOptions
                {
                    CreateSession = true,
                    Timeout = TimeSpan.FromSeconds(quickJoinTimeout),
                };
                _session = await MultiplayerService.Instance.MatchmakeSessionAsync(quickJoin, sessionOptions);
                _attempts = 0;
                Status = Describe(_session);
                Debug.Log($"[SnowNet] {(_session.IsHost ? "Hosting" : "Joined")} session {_session.Id} " +
                          $"({_session.PlayerCount}/{_session.MaxPlayers}, code {_session.Code})");
                WireSession(_session);

                // The world-sync channel is a dynamic spawn (scene management is off, so no in-scene NetworkObjects).
                var nm = NetworkManager.Singleton;
                if (nm != null && nm.IsServer && SnowNetChannel.Instance == null && channelPrefab != null)
                    NetworkObject.InstantiateAndSpawn(channelPrefab, nm);
            }
            catch (Exception e)
            {
                Status = "offline (no session)";
                Debug.LogWarning($"[SnowNet] Session join failed — playing offline. {e}");
                _session = null;
                _connecting = false;
                ScheduleRetry();
                return;
            }
            finally
            {
                _connecting = false;
            }

            try
            {
                await VoiceChat.JoinAsync(_session.Id);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SnowNet] Voice chat unavailable: {e.Message}");
            }
        }

        void WireSession(ISession session)
        {
            session.RemovedFromSession += () => OnSessionEnded("removed from session");
            session.Deleted += () => OnSessionEnded("session deleted");
            session.StateChanged += state =>
            {
                if (state == SessionState.Disconnected || state == SessionState.Deleted)
                    OnSessionEnded($"session {state}");
            };
            session.PlayerJoined += _ => UpdateCount(session);
            session.PlayerHasLeft += _ => UpdateCount(session);
        }

        static void UpdateCount(ISession session)
        {
            try { Status = Describe(session); }
            catch { /* session mid-teardown */ }
        }

        static string Describe(ISession session) =>
            $"{(session.IsHost ? "hosting" : "joined")} {session.PlayerCount}/{session.MaxPlayers}" +
            "   ·   M mute   ·   Shift+N leave";

        void OnSessionEnded(string why)
        {
            if (_session == null) return;
            Debug.Log($"[SnowNet] Session over ({why}); looking for a new one in {rejoinDelay:0}s");
            _session = null;
            Status = "reconnecting";
            _ = VoiceChat.LeaveAsync();
            ScheduleRetry();
        }

        void ScheduleRetry()
        {
            if (_attempts >= MaxAttempts)
            {
                Debug.LogWarning($"[SnowNet] Giving up after {MaxAttempts} attempts — playing offline.");
                Status = "offline";
                return;
            }
            _retryAt = Time.unscaledTime + rejoinDelay;
        }

        void RegisterPrefabs(NetworkManager nm)
        {
            if (nm == null || nm.IsListening) return;
            if (avatarPrefab != null && nm.NetworkConfig.PlayerPrefab == null)
                nm.NetworkConfig.PlayerPrefab = avatarPrefab;
            TryAddPrefab(nm, avatarPrefab);
            TryAddPrefab(nm, channelPrefab);
        }

        static void TryAddPrefab(NetworkManager nm, GameObject prefab)
        {
            if (prefab == null) return;
            try
            {
                if (!nm.NetworkConfig.Prefabs.Contains(prefab))
                    nm.AddNetworkPrefab(prefab);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SnowNet] Could not register prefab {prefab.name}: {e.Message}");
            }
        }

        /// <summary>`--snow-autoconnect` / `SNOW_AUTOCONNECT=1`: go online without the keypress, for a second
        /// instance launched by a script or a test rig that has no one to press Shift+N.</summary>
        static bool AutoConnectRequested()
        {
            if (Environment.GetEnvironmentVariable("SNOW_AUTOCONNECT") == "1") return true;
            foreach (var arg in Environment.GetCommandLineArgs())
                if (arg == "--snow-autoconnect") return true;
            return false;
        }

        static string ProfileOverride()
        {
            // Two instances on one machine share the anonymous account unless profiles differ:
            //   Snowfield.app --snow-profile p2      (or env SNOW_PROFILE=p2)
            string env = Environment.GetEnvironmentVariable("SNOW_PROFILE");
            if (!string.IsNullOrEmpty(env)) return env;
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--snow-profile")
                    return args[i + 1];
            return null;
        }

        async void OnDestroy()
        {
            // Start the session leave BEFORE any await: on application quit the sync context never pumps
            // again, so anything after the first await is dead code — and a lobby nobody left keeps matching
            // new players into a dead session for ~30 s until the service reaps it. The synchronous prefix of
            // Leave/Delete puts the HTTP call on the wire even mid-quit. Hosts delete outright so the lobby
            // dies with them instead of migrating to a client holding dead relay metadata.
            var session = _session;
            _session = null;
            Task leave = null;
            try
            {
                if (session != null)
                    leave = session.IsHost ? session.AsHost().DeleteAsync() : session.LeaveAsync();
            }
            catch { /* quitting */ }
            try
            {
                await VoiceChat.LeaveAsync();
                if (leave != null) await leave;
            }
            catch { /* quitting */ }
            Status = Offline; // leave no stale HUD line behind for the next play session
        }
    }
}
