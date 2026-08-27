using System.IO;
using Snowfield.Field;
using Snowfield.Sculpture;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Snowfield.Player
{
    /// <summary>
    /// Left/Right arrows flip between fields — separate plots of snow, each with its own saved sculptures.
    /// Lives in Snowfield.Player because switching touches the save manager, the terrain and the player's hands.
    /// (Phase 3 turns these into neighbourhoods; for now they are local slots.)
    /// </summary>
    public class FieldSwitcher : MonoBehaviour
    {
        public static FieldSwitcher Instance { get; private set; }

        [Tooltip("How many fields the arrows cycle through.")]
        public int fieldCount = 8;
        [Tooltip("File name for a field's terrain heights, stored beside its sculptures.")]
        public string terrainFile = "terrain.bin";

        public int CurrentField { get; private set; }

        void Awake() => Instance = this;
        void OnDestroy() { if (Instance == this) Instance = null; }

        void Start() => LoadTerrain();
        void OnApplicationQuit() => SaveTerrain();

        void Update()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            if (kb.rightArrowKey.wasPressedThisFrame) Switch(1);
            else if (kb.leftArrowKey.wasPressedThisFrame) Switch(-1);
        }

        public void Switch(int delta)
        {
            var save = SaveLoadManager.Instance;
            if (save == null || fieldCount <= 1) return;

            // Whatever is in your hands belongs to the field you are leaving.
            var tool = FindAnyObjectByType<SculptTool>();
            if (tool != null && tool.Roller != null && tool.Roller.IsEngaged) tool.Roller.Release();

            save.SaveAll();
            SaveTerrain();

            CurrentField = ((CurrentField + delta) % fieldCount + fieldCount) % fieldCount;
            save.SetField(CurrentField);
            save.LoadAll();
            LoadTerrain();
        }

        // ---------- terrain travels with the field ----------

        string TerrainPath(int index)
        {
            var save = SaveLoadManager.Instance;
            return save == null ? null : Path.Combine(save.FieldDir(index), terrainFile);
        }

        void SaveTerrain()
        {
            var terrain = SnowTerrain.Instance;
            string path = TerrainPath(CurrentField);
            if (terrain == null || !terrain.IsCreated || path == null) return;
            var blob = terrain.SaveHeights();
            if (blob == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, blob);
        }

        void LoadTerrain()
        {
            var terrain = SnowTerrain.Instance;
            if (terrain == null || !terrain.IsCreated) return;
            string path = TerrainPath(CurrentField);
            if (path != null && File.Exists(path) && terrain.LoadHeights(File.ReadAllBytes(path))) return;
            terrain.ResetHeights(); // never visited (or the field resolution changed): untouched snow
        }
    }
}
