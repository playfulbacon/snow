using System.IO;
using Snowfield.Net;
using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEngine;

namespace SnowDays.EditorTools
{
    /// <summary>
    /// Multiplayer wiring for the Main scene, called from <see cref="MainSceneSetup.Run"/> (and standalone
    /// from the menu). Idempotent, same conventions: find-or-create, direct field assignment.
    /// Builds two prefabs (rebuilt every run; asset GUIDs — and so GlobalObjectIdHashes — stay stable):
    ///   Assets/Net/NetAvatar.prefab  — the player's body as others see it (model + animator + NetworkTransform)
    ///   Assets/Net/NetChannel.prefab — the world-sync channel, host-spawned at session start
    /// and a "Network" root: NetworkManager + UnityTransport + NetBootstrap. Scene management is off — this is
    /// a single-scene game and every peer is already standing in Main when it connects.
    /// </summary>
    public static class NetSceneSetup
    {
        const string NetFolder = "Assets/Net";
        const string AvatarPrefabPath = NetFolder + "/NetAvatar.prefab";
        const string ChannelPrefabPath = NetFolder + "/NetChannel.prefab";
        const string ModelFbxPath = "Assets/CharacterTest/base_basic_shaded.fbx";
        const string ControllerPath = "Assets/Player/FirstPersonPlayer.controller";

        [MenuItem("Snowfield/Ensure Main Scene Networking")]
        public static void RunStandalone()
        {
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/Main.unity",
                UnityEditor.SceneManagement.OpenSceneMode.Single);
            Ensure();
            UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();
        }

        public static void Ensure()
        {
            if (!Directory.Exists(NetFolder)) Directory.CreateDirectory(NetFolder);

            GameObject avatarPrefab = BuildAvatarPrefab();
            GameObject channelPrefab = BuildChannelPrefab();

            var netGo = GameObject.Find("Network") ?? new GameObject("Network");
            var nm = netGo.GetComponent<NetworkManager>() ?? netGo.AddComponent<NetworkManager>();
            var utp = netGo.GetComponent<UnityTransport>() ?? netGo.AddComponent<UnityTransport>();
            nm.NetworkConfig ??= new NetworkConfig();
            nm.NetworkConfig.NetworkTransport = utp;
            nm.NetworkConfig.PlayerPrefab = avatarPrefab;
            nm.NetworkConfig.EnableSceneManagement = false;

            var boot = netGo.GetComponent<NetBootstrap>() ?? netGo.AddComponent<NetBootstrap>();
            boot.avatarPrefab = avatarPrefab;
            boot.channelPrefab = channelPrefab;

            Debug.Log("[NetSceneSetup] Networking ensured: Network root + NetAvatar/NetChannel prefabs.");
        }

        static GameObject BuildAvatarPrefab()
        {
            var root = new GameObject("NetAvatar");
            try
            {
                root.AddComponent<NetworkObject>();
                var nt = root.AddComponent<NetworkTransform>();
                nt.AuthorityMode = NetworkTransform.AuthorityModes.Owner;
                nt.SyncScaleX = nt.SyncScaleY = nt.SyncScaleZ = false;
                nt.Interpolate = true;
                root.AddComponent<NetAvatar>();

                var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(ModelFbxPath);
                if (fbx == null)
                {
                    Debug.LogError($"[NetSceneSetup] Missing {ModelFbxPath}; avatar prefab has no body.");
                }
                else
                {
                    var model = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
                    model.name = "Model";
                    model.transform.SetParent(root.transform, false);
                    model.transform.localPosition = Vector3.zero;
                    model.transform.localRotation = Quaternion.identity;

                    var animator = model.GetComponent<Animator>() ?? model.AddComponent<Animator>();
                    animator.runtimeAnimatorController =
                        AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ControllerPath);
                    foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(ModelFbxPath))
                        if (sub is Avatar humanoid) { animator.avatar = humanoid; break; }
                    animator.applyRootMotion = false;
                    animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

                    foreach (var smr in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        smr.updateWhenOffscreen = true;
                        // The first-person arms mesh is the local player's own view; a remote body never shows it.
                        if (smr.name.ToLowerInvariant().Contains("firstperson"))
                            smr.gameObject.SetActive(false);
                        else
                            smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                    }
                }

                return PrefabUtility.SaveAsPrefabAsset(root, AvatarPrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        static GameObject BuildChannelPrefab()
        {
            var root = new GameObject("NetChannel");
            try
            {
                root.AddComponent<NetworkObject>();
                root.AddComponent<SnowNetChannel>();
                return PrefabUtility.SaveAsPrefabAsset(root, ChannelPrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
