# Phase 1 — Sculpting Sandbox: Technical Sketch

Goal: a delightful offline toy. Field, character, brush add/smooth, snowball, sticks, save/load. No networking.

## Class layout

```
Snowfield.Voxel/
  VoxelGrid.cs          // owns NativeArray<byte> density, dims, voxelSize; chunk dirty flags
  MarchingCubesTables.cs// static edge/tri tables
  MeshChunkJob.cs       // IJob per dirty 16³ chunk → NativeList verts/tris/normals
  BrushJobs.cs          // AddBrushJob, SmoothBrushJob (IJobParallelFor over brush AABB)
  SphereStampJob.cs     // snowball attach = one-shot density stamp
  GridSerializer.cs     // RLE encode/decode density; JSON for props
Snowfield.Sculpture/
  SnowSculpture.cs      // MonoBehaviour: VoxelGrid + chunk GameObjects (MeshFilter/Renderer/Collider)
  SculptureProp.cs      // socketed stick/object; {prefabId, localPos, localRot}
  SculptureSpawner.cs   // places the starter mound (pre-stamped hemisphere of density)
Snowfield.Player/
  SnowCharacter.cs      // third-person controller
  SculptTool.cs         // raycast → brush cursor; hold-to-apply; size & strength controls
  SnowballRoller.cs     // fake accumulation sphere; trail decal; attach-on-press
  PropPlacer.cs         // pick up stick → surface raycast → socket
Snowfield.Field/
  SnowTerrain.cs        // heightmap + path RenderTexture; footprint stamping
  SnowfallCycle.cs      // lerps path RT toward zero; drives dusting shader param
Snowfield.Config/
  SculptFeelConfig.cs   // ScriptableObject: all tunables
```

## Core data

```csharp
public struct VoxelGridInfo {
    public int size;        // 96 (voxels per axis)
    public float voxelSize; // 0.04f
    public const int ChunkSize = 16;
    public const byte Iso = 128;
}
// density: NativeArray<byte>, index = x + y*size + z*size*size
// world pos of voxel (x,y,z) = gridOrigin + new float3(x,y,z) * voxelSize
```

## Add brush (the heart — get this right first)

```csharp
[BurstCompile]
public struct AddBrushJob : IJobParallelFor {
    public NativeArray<byte> density;      // full grid; job iterates only AABB indices
    public int gridSize;
    public float3 brushCenterLocal;        // in voxel space
    public float radiusVoxels;
    public float ratePerTick;              // THE packing feel; e.g. 6–20 density/tick
    // Execute(i): map i → (x,y,z) within brush AABB
    // d = distance(voxel, brushCenter) / radiusVoxels
    // if d < 1: falloff = smoothstep(1, 0.6, d)   // soft shoulder, flat core
    //   density = min(255, density + ratePerTick * falloff)
}
```

- Rate cap per tick + held press = accumulation. Tune `ratePerTick` and falloff shoulder in play mode.
- Smooth brush: same AABB, output = weighted average of 6-neighborhood, lerped by falloff × strength. Double-buffer the AABB region (read old, write new).

## Meshing

- On brush release **and** on a ~10 Hz timer while sculpting: collect dirty chunks, schedule one `MeshChunkJob` per chunk (they parallelize across chunks), complete, upload via `Mesh.SetVertices/SetIndexBufferData`.
- Sample density at chunk-local corners **+1 voxel apron** from neighbors so seams match.
- **Normals: central-difference gradient of density at each vertex position**, normalized & negated. Do not compute from triangles.
- MeshCollider: rebuild only on brush release (cooking is expensive).

## Snowball

- While rolling on fresh terrain snow: `scale += k * distanceTravelled`, clamp; stamp trail into path RT.
- Attach: raycast from ball to sculpture; convert hit to grid space; run `SphereStampJob` (same falloff as brush, one shot, full strength); destroy ball; mark chunks dirty.

## Terrain paths

- Path layer = single-channel RT (e.g. 1024²) over the field. Character stamps soft dots each step; snowball stamps a wide swath.
- Terrain shader: displace vertices down by pathRT × maxDepth; darken/roughen in trench.
- `SnowfallCycle`: every N minutes, blit `pathRT = max(0, pathRT - decay)`. (Phase 1 can just expose a "let it snow" debug key.)

## Save/load (local)

- `GridSerializer`: RLE runs of (count, value) over the density array; props as JSON list. One file per sculpture in `persistentDataPath`. This format becomes the network blob in Phase 3 unchanged.

## Milestone order

1. Grid + marching cubes rendering a pre-stamped hemisphere (the starter mound). Static, no brush.
2. Add brush with cursor + hold-to-apply. **Stop and tune feel.**
3. Smooth brush.
4. Dirty-chunk incremental remesh + gradient normals (if not already).
5. Snowball roll + attach.
6. Stick props.
7. Path RT + snowfall decay.
8. Save/load.

## Definition of delightful (Phase 1 gate)

You can make a recognizable snowman with body-by-snowball, face-by-brush, arms-by-stick in under 3 minutes, and patting it smooth feels good enough that you do it when you don't need to.
