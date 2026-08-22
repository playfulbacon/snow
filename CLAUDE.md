# CLAUDE.md — Snowfield (working title)

A small, seasonal multiplayer game about sculpting snow in a shared field. Live for the month of December 2026 only. Inspired by a rare Vancouver snowfall where fields and beaches filled with strangers' snowmen — an ephemeral art gallery.

**This is an evening side project. Bias every decision toward boring, known, shippable. Target: Dec 1, 2026.**

## Pillars (use these to resolve design/scope questions)

1. **The sculpting feel is the product.** Polish budget goes to the brush, not features.
2. **Asynchronous presence.** You encounter what real people *left*, not the people themselves. Live sync is friends-only.
3. **Ephemerality is the point.** One month, then it melts. No retention mechanics, no progression treadmill.
4. **Failure-tolerant material.** Snow is blobby, matte, and forgiving — lean on this to hide technical simplicity.

## Tech stack

- Unity (LTS), URP, C#
- Burst + Jobs for voxel work. **No compute shaders in v1** — CPU jobs are fast enough at our grid sizes and far more debuggable.
- Backend (Phase 3+): PocketBase. Polling sync, no sockets for stranger content.
- Platform: PC first (itch.io or Steam — undecided, don't hardcode assumptions).

## Core architecture (decided — don't relitigate without asking)

### Sculptures = per-sculpture local voxel grids, NOT a world-spanning volume
- Each sculpture: bounded density grid, e.g. 96³ voxels @ 4cm = ~3.84m cube. `NativeArray<byte>` density, iso-level 128.
- Chunked into 16³ chunks (6³ = 216 chunks per sculpture). Dirty-flag chunks; remesh only dirty chunks.
- Meshing: **classic marching cubes**. Snow's aesthetic hides all MC artifacts. No dual contouring, no octrees.
- Normals from the **density gradient (central differences)**, not from mesh geometry — smooth-shaded for free. Faceted snow reads as geometry; smooth snow reads as snow.
- MeshColliders update lazily (on brush release, not per frame).

### Terrain = ordinary heightmap, not voxels
- Ground is a heightmap mesh or Unity Terrain with a **path layer**: a RenderTexture the character stamps footprints/trails into; shader displaces/darkens. Periodic snowfall lerps the RT back toward zero over hours.
- Snowfall also adds a shader-level dusting on upward-facing normals of sculptures (visual only — never modifies voxel data / anyone's work).

### Brushes
- **Add**: spherical falloff kernel raising density, rate-capped per tick so snow *accumulates* under a held press (this cap is the "packing" feel — make it tunable).
- **Smooth/pat**: blur kernel over the brush region. Cheap, high payoff.
- Brush ops are `IJobParallelFor` over the affected voxel AABB.

### Snowballs — faked, not simulated
- Rolling: a sphere prop that scales up while rolling over fresh snow, consuming a terrain decal layer (leaves the classic trail).
- Attaching: **stamp a sphere of density** into the target sculpture's grid, delete the prop. Reuses brush plumbing.

### Sticks & found objects = props, zero voxel involvement
- Raycast to sculpture surface, socket/parent, store `{prefabId, localPos, localRot}` in the sculpture record.

### Data model (Phase 3)
```
Sculpture: { id, fieldPos, densityBlob (RLE-compressed), props[], authorId, neighborhoodId, createdAt, modifiedAt, buriedFlags }
```
RLE compresses brutally well (density fields are mostly-empty or mostly-full). Sync = poll "sculptures in my neighborhood modified since T" on login + lazy interval.

### Friend co-op (Phase 4)
- Sync **brush strokes** (pos, radius, strength, timestamp), not density data. Each client applies deterministically. If drift appears: periodic chunk checksums, loser re-downloads blob. Jam-grade is fine.

## Phases (build in order; each phase is independently valuable)

1. **Sandbox (no networking):** field, third-person character, add brush, smooth brush, incremental remesh, snowball roll+attach, stick props, local save/load. *Gate: is the toy delightful? If not, stop and fix feel.*
2. **Feel pass:** audio layers (soft fwump), particles, snow shader (dusting, soft rim), path carving + snowfall cycle, day/night or time-of-day mood.
3. **Async neighborhood:** PocketBase, auth, neighborhood assignment (friends-first via invite links; organic joiners fill youngest non-full neighborhood, ~40–80 players), upload/download, polling, "bury under snow" report (per-player hide + server threshold buries for all).
4. **Friend live co-sculpt:** stroke sync for 2–6 invited players.
5. **Ship:** allowance tuning (daily snow grant; rolled snow is free — it comes from the field), December content beats (solstice, NYE), store page.

**If November gets ugly (Wordbound launches Dec 10): Phase 4 is the first cut.** Async neighborhood alone delivers the fantasy.

## Conventions

- Assembly definitions from the start: `Snowfield.Voxel`, `Snowfield.Player`, `Snowfield.Net` (later).
- All feel parameters (brush rate cap, falloff curve, accumulation speed, snowfall decay hours) in ScriptableObjects — tuning happens in play mode, often.
- Keep marching cubes tables in one static class; keep jobs pure and testable.
- No third-party voxel assets. The voxel core is small and owning it matters.
- Commit small; this is an evenings project and sessions will be short.

## Open decisions (ask before assuming)

- Working title / final name
- itch.io vs Steam
- Exact grid size & voxel resolution (start 96³ @ 4cm, tune after Phase 1)
- Whether sculptures can be *edited* by strangers (current lean: no — additive-only communal lumps maybe later)

## Dev workflow (headless, no editor needed)

Unity: `C:\Program Files\Unity\Hub\Editor\6000.3.19f1\Editor\Unity.exe`. All commands take `-batchmode -projectPath C:\Projects\snow -logFile <log>`.

- Regenerate settings/scene (idempotent, never clobbers an existing scene): `-nographics -quit -executeMethod Snowfield.Editor.ProjectBootstrap.Run`
- Re-wire player rig into the scene: `-nographics -quit -executeMethod Snowfield.Editor.SandboxActors.Run` (also menu Snowfield ▸ Ensure Sandbox Actors). Scene layout convention — one responsibility per GameObject: `Player` (SnowCharacter) › `OrbitCamera` (moves Main Camera) · `SculptTool` (+AccessoryPlacer); `Main Camera` is a bare camera; `HUD` is its own root with Canvas + ToolHud.
- Render the scene to PNG (needs graphics, so no `-nographics`): `-executeMethod Snowfield.Editor.HeadlessScreenshot.Run -screenshotOut Screenshots/x.png`
- Tests: `-runTests -testPlatform EditMode|PlayMode -testResults out.xml` (PlayMode needs graphics)

Gotchas learned: `SerializedProperty.objectReferenceValue` silently drops refs to custom ScriptableObjects in batchmode — assign fields directly (`SnowSculpture.EditorAssign`). Edit-mode screenshots render shadows but not play-mode-only behaviour (orbit camera, brush); `HeadlessScreenshot` snaps the orbit camera manually.

## Controls (Sandbox)

Modes: **1 Snow** (LMB add · RMB carve · scroll radius) · **2 Empty Hand** (hold LMB on a snowball to push it, or on bare ground to start one · RMB picks up anything: snowball → LMB sets down / attaches to snow; loose twig/carrot/button/pebble or a placed accessory → inventory · LMB on snow smooths) · **3 Accessory** (scroll pick · LMB place from inventory · RMB retrieve). Left Shift cycles modes. WASD move · Tab toggle cursor lock · +/- zoom. `FieldScatter` litters the field with items at start.
