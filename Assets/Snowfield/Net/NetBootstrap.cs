using System;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;

namespace Snowfield.Net
{
    /// <summary>
    /// Joins everyone into one shared field on launch: UGS anonymous sign-in, then quick-join any open public
    /// session or create one (P2P over Relay — the Sessions package starts the NGO host/client itself; never
    /// call StartHost/StartClient around it). Every failure degrades to plain offline single-player.
    /// Host leaving kills the session (NGO has no host migration); clients notice and re-quick-join, so the
    /// survivors reconvene under a new host after a few seconds.
    /// </summary>
    public sealed class NetBootstrap : MonoBehaviour
    {
        [Tooltip("Join the public session automatically on play. Off for tests.")]
        public bool autoConnect = true;
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

        /// <summary>One-line connection state, for logs and any future HUD.</summary>
        public static string Status { get; private set; } = "offline";

        ISession _session;
        bool _connecting;
        float _retryAt = -1f;
        int _attempts;
        const int MaxAttempts = 5;

        async void Start()
        {
            if (!autoConnect) return;
            await ConnectAsync();
        }

        void Update()
        {
            if (_retryAt > 0f && Time.unscaledTime >= _retryAt && !_connecting)
            {
                _retryAt = -1f;
                _ = ConnectAsync();
            }
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
                Debug.LogWarning($"[SnowNet] Unity Services init/sign-in failed — playing offline. {e.Message}");
                _connecting = false;
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
                Status = _session.IsHost ? $"hosting ({_session.PlayerCount}/{_session.MaxPlayers})"
                                         : $"joined ({_session.PlayerCount}/{_session.MaxPlayers})";
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
            try { Status = $"{(session.IsHost ? "hosting" : "joined")} ({session.PlayerCount}/{session.MaxPlayers})"; }
            catch { /* session mid-teardown */ }
        }

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
        }
    }
}
