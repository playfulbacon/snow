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
        [Tooltip("Wipe the trampled snow (footprints, trenches, scoops) when arriving at a field.")]
        public bool freshSnowOnArrival = true;

        public int CurrentField { get; private set; }

        void Awake() => Instance = this;
        void OnDestroy() { if (Instance == this) Instance = null; }

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
            CurrentField = ((CurrentField + delta) % fieldCount + fieldCount) % fieldCount;
            save.SetField(CurrentField);
            save.LoadAll();

            if (freshSnowOnArrival)
            {
                var terrain = SnowTerrain.Instance;
                if (terrain != null) terrain.ResetHeights();
            }
        }
    }
}
