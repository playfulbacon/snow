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
- MeshColliders update lazily (on brush release, not per frame). Chunk GameObjects are created lazily (only chunks with geometry).
- Brush strokes are **multi-target**: every grid under the kernel receives the stroke, so overlapping objects have no dead seams.
- Fixed sculptures **auto-regrow**: an additive stroke or fuse that needs room rebuilds the sculpture into a larger, re-centred grid (Burst `Absorb`), capped at `maxGridSize` (192 ≈ 7.7 m). Loose snowballs never regrow; promotion covers them.

### Ground snow = SnowDays' SnowDeform surface (heightmap `Snowfield.Field` module REMOVED in the SnowDays merge, Aug 2026)
- The world is the SnowDays Americana-island town (Unity Terrains + Synty props, scene `Main`). Its `SnowDays.SnowDeformSystem` (Assets/SnowDeform, Assembly-CSharp) drapes a displaced snow shell over the terrain: an 80 m world-anchored window with a GPU trample RT (2048², footprints/trails stamp in, decay refills over `m_RefillSeconds`) + CPU height & trample mirrors for gameplay queries.
- The sculpting tools reach it through `Snowfield.Player.SnowGround` (static) / `ISnowGroundBackend`, registered at runtime by `SnowDays.SnowDeformGroundAdapter` (self-bootstraps; scoops/trenches → `SnowDeformSystem.Stamp`). **Asmdef rule: Snowfield.* assemblies cannot reference Assembly-CSharp — always invert the dependency this way.**
- Footprints come from `SnowDays.SnowFootprints` (animation-driven, auto-attached to the player by SnowDeformSystem). Scoop divots and snowball trenches go through `SnowGround.StampDepression`. Deformation is windowed and decays — it does not persist per-field (FieldSwitcher was removed with the heightmap).
- Snowfall visuals: `SnowDays.SnowfallSystem` (retro flake box, self-bootstraps). Sculpture dusting remains a future shader-level idea (never modify voxel data).

### Snow look = one shader family (`Assets/SnowDeform/Resources`)
- `SnowLook.hlsl` owns what snow *looks like*: the terrain's snow diffuse tiled in world space, a cold-tinted ambient, and the sun quantized into bands. Both snow shaders include it, so the field and the things built on it are one material. Put look changes there, not in a single shader.
- `SnowSurface.shader` = the ground shell (flat XZ projection — the reference mapping). `SnowSculpt.shader` = sculpture chunks; marching-cubes meshes have position + gradient normal and **no UVs**, so it projects the same texture triplanar and its Y plane matches the ground exactly.
- The sculpture material is `Assets/Settings/Snow.mat`. It carries `_SnowBaseMap`/`_SnowTexTiling` as material values (a material can't read the runtime binding, and sculptures render in edit mode); `MainSceneSetup.SyncSnowMaterial` copies them off the scene's terrain snow layer using the same rule `SnowDeformSystem` uses at runtime. Re-run the setup command after changing terrain layers.
- `SnowDeformVerify` compiles every pass of every shader in that folder — add new ones to its list.

### Brushes
- **Add**: spherical falloff kernel raising density, rate-capped per tick so snow *accumulates* under a held press (this cap is the "packing" feel — make it tunable).
- **Smooth/pat**: blur kernel over the brush region. Cheap, high payoff.
- Brush ops are `IJobParallelFor` over the affected voxel AABB.

### Snowballs — faked, not simulated (built: each snowball is a small 48³ `SnowSculpture` + `Snowball`, loose until something is attached, then `SculptureFactory.Promote`s it to 96³; flight = temporary Rigidbody + sphere collider, splat = `Absorb`)
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
- Transport: **Netcode for GameObjects 2.x + Unity Relay** (same stack as the minstrels project — invite links are Relay join codes; characters are NetworkTransforms).
- Sync **brush strokes** (pos, radius, strength, timestamp) as RPCs, not density data. Each client applies deterministically. Structural events (fuse, promote, throw *impact*, prop attach) are also sent as events — physics flights don't replay identically, so send the resulting splat, not the flight.
- If drift appears: periodic chunk checksums, loser re-downloads blob (same RLE blob as save/load). Jam-grade is fine.
- Session end state still uploads to PocketBase so it persists for the neighborhood. Phase 3 stays PocketBase polling — a live session is not persistence.

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

Unity: `/Applications/Unity/Hub/Editor/6000.3.19f1/Unity.app/Contents/MacOS/Unity`. All commands take `-batchmode -projectPath /Users/noahrayburn/Projects/snow -logFile <log>`.

- Wire the sculpting kit into the Main scene (idempotent, touches nothing else): `-nographics -quit -executeMethod SnowDays.EditorTools.MainSceneSetup.Run` (also menu Snowfield ▸ Ensure Main Scene Sculpting). Scene layout convention — one responsibility per GameObject: `Player` (SnowDays.PlayerController, moves its own `CameraPivot › PlayerCamera`; whole subtree on layer Ignore Raycast) › `SculptTool` (+AccessoryPlacer +SnowballRoller) · `CarryAnchor`; roots `Sculptures` (SculptureFactory) · `SaveLoad` · `BrushCursor` (inactive) · `HUD` (Canvas + ToolHud). SnowDeform/Snowfall/ground-adapter bootstrap themselves at play — no scene objects.
- Render the Main scene to PNG (needs graphics, so no `-nographics`): `-executeMethod Snowfield.Editor.HeadlessScreenshot.Run -screenshotOut Screenshots/x.png` (add `-screenshotDemo` to stamp a mound in front of the camera).
- Tests: `-runTests -testPlatform EditMode|PlayMode -testResults out.xml` (PlayMode needs graphics)

Gotchas learned: `SerializedProperty.objectReferenceValue` silently drops refs to custom ScriptableObjects in batchmode — assign fields directly (`SnowSculpture.EditorAssign`, MainSceneSetup does this everywhere). Edit-mode screenshots render shadows but not play-mode-only behaviour (brush, SnowDeform window, snowfall, the GameCube fullscreen passes). The player camera is **Untagged** (`Camera.main` is null in Main) — wire cameras explicitly, and don't tag it: `SnowfallSystem` follows `Camera.main` and today's scene behaviour is the reference. The SnowDays-era pitfalls (URP YAML `m_Version`, marker-armed playtests, Editor.log ownership) are in auto-memory `unity-urp-handauthored-assets`.

## Controls (Main scene)

One persistent state (Hand) + a Tab accessory overlay: **LMB** scoop snow into your hands — on a sculpture it bites out the chunk under the red cursor sphere, on bare ground a handful (mass continuity, leaves a divot in the surface snow); while carrying, a tap lets go where the snow already is (fuses into snow, otherwise falls under gravity) and holding charges a throw · **Shift+LMB** smooth/pat (works with a ball in hand) · RMB pick-up is disabled for now · a carried ball rolls at your feet only while the cursor points near you, otherwise it is held overhead · **scroll** brush radius · **Tab** accessory overlay (scroll picks · LMB places, or removes the accessory under the cursor). Sculpting inputs only fire while the cursor is locked (mouse-look mode). First person (`SnowDays.PlayerController`): WASD move · mouse look · Shift run · Space jump · Q crouch · E tiptoe (hold-based stances; capsule is bottom-anchored, camera pivot rides the height) · Esc unlocks cursor (LMB re-locks). Trampled snow refills on its own (`SnowDeformSystem.m_RefillSeconds`, 600 s; deformation lives in an 80 m window around the player and is lost beyond it). Sculptures auto-save to `persistentDataPath/sculptures/field0` on quit and auto-load on start (**F5** save · **F9** reload; `SaveLoadManager`). Field switching was removed with the heightmap terrain. Snowballs are already small sculptures: brush them, attach balls anywhere on their surface, decorate them; attaching fixes them in place and promotes them to a full grid (`SculptureFactory.Fuse/Promote`). Ground raise/carve sculpting was dropped with the heightmap (the trample surface only presses down); walking and rolling still press paths into it.
