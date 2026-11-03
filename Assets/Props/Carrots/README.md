# Carrot items

Each of the nine mesh objects in `Assets/Props/Carrots.blend` has a separate placeable prefab, pickup prefab, and item definition.

Drag a prefab from **Pickups/** into the environment to create a visible, collectible carrot. LMB collects it, Tab selects its inventory slot, and LMB on snow places that exact variant. Every variant stacks to six; different variants occupy different stacks. The default supply spawner includes all nine types.

| Source mesh | Placement prefab | Item id |
| --- | --- | --- |
| Carrot1 | Prefabs/Carrot1.prefab | carrot |
| Cube | Prefabs/Carrot_Cube.prefab | carrot_cube |
| Cube.001 | Prefabs/Carrot_Cube_001.prefab | carrot_cube_001 |
| Cube.002 | Prefabs/Carrot_Cube_002.prefab | carrot_cube_002 |
| Cube.003 | Prefabs/Carrot_Cube_003.prefab | carrot_cube_003 |
| Cube.004 | Prefabs/Carrot_Cube_004.prefab | carrot_cube_004 |
| Cube.005 | Prefabs/Carrot_Cube_005.prefab | carrot_cube_005 |
| Cube.006 | Prefabs/Carrot_Cube_006.prefab | carrot_cube_006 |
| Cube.007 | Prefabs/Carrot_Cube_007.prefab | carrot_cube_007 |

Pickup filenames append `_Pickup`. These prefabs include a visible model, a finite `ItemPickup`, and a trigger collider. **Prefabs/** contains the corresponding visual/placement assets used by the in-hand preview, placement, save loading, and network replay. Meshes reference the original blend import rather than copied geometry.

The generator preserves the source proportions and relative object scales at 0.05 world units per imported unit, yielding carrots about 20–58 cm long. The broad end is the pivot; the tip points along +Y before placement. A shared orange URP material is in **Materials/**. Item identities and stack/placement settings live in `Assets/Resources/Items/Carrots/`.

After editing the blend, use **Snowfield > Items > Build Carrot Prefabs** to regenerate the model and pickup prefabs. This overwrites generated prefab layouts, preserves existing item definition settings and material tuning, and verifies the result. **Verify Carrot Prefabs** checks mesh coverage, unique ids, catalog registration, and pickup wiring. **Preview Carrot Prefabs** renders `Screenshots/Items/carrots.png` without changing a scene on disk. Keep Blender installed to reimport the source blend; player builds use Unity's imported meshes and do not need Blender.
