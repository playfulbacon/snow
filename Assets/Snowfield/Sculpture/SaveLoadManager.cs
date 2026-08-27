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
        public static SaveLoadManager Instance { get; private set; }

        /// <summary>
        /// Set by the network layer while a shared session is live: F9 would replace the shared world with a
        /// local save on this client only, desyncing it from everyone. Saving (F5/quit) stays allowed.
        /// </summary>
        public static bool BlockManualLoad;

        [Tooltip("Folder under persistentDataPath. Each field gets a numbered subfolder.")]
        public string folder = "sculptures";
        [Tooltip("Which field is loaded; FieldSwitcher drives this with the arrow keys.")]
        public int field = 0;
        public bool loadOnStart = true;
        public bool saveOnQuit = true;

        string Dir => FieldDir(field);

        /// <summary>Folder holding one field's sculptures (and its terrain blob).</summary>
        public string FieldDir(int index) => Path.Combine(Application.persistentDataPath, folder, "field" + index);

        void Awake() => Instance = this;
        void OnDestroy() { if (Instance == this) Instance = null; }

        /// <summary>Point at another field's folder. Save the old one and load the new one around this call.</summary>
        public void SetField(int index) => field = Mathf.Max(0, index);

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
            if (kb.f9Key.wasPressedThisFrame && !BlockManualLoad) LoadAll();
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
                // A ball someone (local or remote) is holding or has in the air would reload hovering at the
                // hand pose with no physics to bring it down. A handful of snow is not worth persisting.
                var ball = s.GetComponent<Snowball>();
                if (ball != null && ball.IsLoose &&
                    (ball.Current == Snowball.State.Carrying || ball.Current == Snowball.State.Flying)) continue;
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
            if (factory == null) return;

            // Wipe current runtime sculptures first, even if this field has no folder yet — otherwise the previous
            // field's snow would follow you here. (Immediate: a deferred Destroy would let a save in the same frame
            // see both old and new copies.)
            foreach (var s in FindObjectsByType<SnowSculpture>(FindObjectsSortMode.None))
                DestroyImmediate(s.gameObject);
            if (!Directory.Exists(Dir)) { Debug.Log($"[Snowfield] Field {field} is fresh snow"); return; }

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
                AccessoryCatalog.MakePickable(go);
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
