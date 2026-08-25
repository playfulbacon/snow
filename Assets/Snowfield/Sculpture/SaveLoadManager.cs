using System;
using System.Collections.Generic;
using System.IO;
using Snowfield.Voxel;
using Unity.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Snowfield.Sculpture
{
    /// <summary>
    /// Local persistence for everything under the sculpture factory: one JSON file per sculpture in
    /// persistentDataPath/sculptures. Auto-loads on start, auto-saves on quit; F5 saves, F9 reloads.
    /// The density blob (GridSerializer RLE, base64) is the exact format Phase 3 uploads.
    /// </summary>
    public class SaveLoadManager : MonoBehaviour
    {
        [Tooltip("Folder under persistentDataPath.")]
        public string folder = "sculptures";
        public bool loadOnStart = true;
        public bool saveOnQuit = true;

        string Dir => Path.Combine(Application.persistentDataPath, folder);

        void Start()
        {
            if (loadOnStart) LoadAll();
        }

        void OnApplicationQuit()
        {
            if (saveOnQuit) SaveAll();
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            if (kb.f5Key.wasPressedThisFrame) SaveAll();
            if (kb.f9Key.wasPressedThisFrame) LoadAll();
        }

        // ------------------------------------------------------------------ save

        [ContextMenu("Save All")]
        public void SaveAll()
        {
            var factory = SculptureFactory.Instance;
            if (factory == null) return;
            Directory.CreateDirectory(Dir);
            foreach (var f in Directory.GetFiles(Dir, "*.json")) File.Delete(f);

            int i = 0;
            foreach (var s in FindObjectsByType<SnowSculpture>(FindObjectsSortMode.None))
            {
                if (s.Grid == null || !s.Grid.IsCreated) continue;
                var record = ToRecord(s);
                File.WriteAllText(Path.Combine(Dir, $"s{i}.json"), JsonUtility.ToJson(new SculptureFile { sculpture = record }));
                i++;
            }
            Debug.Log($"[Snowfield] Saved {i} sculptures to {Dir}");
        }

        public static SculptureRecord ToRecord(SnowSculpture s)
        {
            var ball = s.GetComponent<Snowball>();
            var r = new SculptureRecord
            {
                position = s.transform.position,
                rotation = s.transform.rotation,
                gridSize = s.Info.size,
                voxelSize = s.Info.voxelSize,
                gridOffset = s.gridOffset,
                isSnowball = ball != null,
                snowballRadius = ball != null ? ball.radius : 0f,
                isLoose = ball != null && ball.IsLoose,
                densityB64 = Convert.ToBase64String(GridSerializer.Encode(s.Grid.Density)),
            };
            foreach (var p in s.Props)
                r.props.Add(new PropRecord { prefabId = p.prefabId, localPos = p.LocalPos, localRot = p.LocalRot });
            return r;
        }

        // ------------------------------------------------------------------ load

        [ContextMenu("Load All")]
        public void LoadAll()
        {
            var factory = SculptureFactory.Instance;
            if (factory == null || !Directory.Exists(Dir)) return;

            // wipe current runtime sculptures (props are children, they go with them)
            foreach (var s in FindObjectsByType<SnowSculpture>(FindObjectsSortMode.None))
                Destroy(s.gameObject);

            int n = 0;
            foreach (var file in Directory.GetFiles(Dir, "*.json"))
            {
                try
                {
                    var wrapped = JsonUtility.FromJson<SculptureFile>(File.ReadAllText(file));
                    if (wrapped?.sculpture != null) { FromRecord(factory, wrapped.sculpture); n++; }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Snowfield] Failed to load {file}: {e.Message}");
                }
            }
            Debug.Log($"[Snowfield] Loaded {n} sculptures from {Dir}");
        }

        public static SnowSculpture FromRecord(SculptureFactory factory, SculptureRecord r)
        {
            var s = factory.CreateEmpty(r.gridSize, r.gridOffset, r.position, r.rotation);
            var blob = Convert.FromBase64String(r.densityB64);
            GridSerializer.Decode(blob, s.Grid.Density);
            s.Grid.MarkAllDirty();

            if (r.isSnowball)
            {
                var ball = s.gameObject.AddComponent<Snowball>();
                ball.radius = r.snowballRadius;
                if (!r.isLoose) ball.Fix();
            }

            foreach (var pr in r.props)
            {
                var entry = AccessoryCatalog.Find(pr.prefabId);
                if (entry == null) continue;
                var go = entry.Build();
                AccessoryCatalog.SetColliders(go, true);
                AccessoryCatalog.AddPickCollider(go, entry);
                var prop = go.AddComponent<SculptureProp>();
                prop.Attach(s, pr.prefabId,
                    s.transform.TransformPoint(pr.localPos),
                    s.transform.rotation * pr.localRot);
            }

            s.Remesh();
            s.RebuildColliders();
            return s;
        }
    }
}
