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
- Each sculpture: bounded density grid, 96³ voxels @ **3 cm** (Phase 1.5 decision, was 4 cm) = ~2.88 m cube. `NativeArray<byte>` density, iso-level 128, plus a second byte per voxel: **compaction** (0 powder … 255 packed — see Phase 1.5 below). Old saves keep their own voxel size (`SnowSculpture.voxelSizeOverride`).
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

### Phase 1.5: compaction & carving (BUILT Sep 2026, untuned — see docs/PHASE1.5.md for the full map)
- **Coarse in, fine out.** Adding is always blobby (scoop, roll, fuse); precision comes only from removing and refining, and only in packed snow. Snow is never created or deleted.
- **Compaction is set by how snow arrives, never a mode**: rolled = 220, scooped handful/shed lump = 40, fuse weld shell ≥ 180, pat adds a little, squeeze = 255. Every job in `Snowfield.Voxel` carries it mass-weighted (`BrushMath.MixCompaction`). Reads: per-vertex tint in `SnowSculpt.shader` (`_SnowPackedTint`), procedural `SnowAudio` pitch. Packing shrinks: one multiplier `packShrinkFraction` (pat = density scale per tick, squeeze = geometric resample `RescaleJob`).
- **Shave (RMB drag)** is a surface-relative disc (`ShaveJob`): everything above the cut plane inside the disc goes, so passes converge on the stroke, not the tool. Stamped along the drag path by distance, not time. All params derive from the packing under the cursor (`SculptureShave.ParamsFor`): packed = thin/crisp, fluffy = 3× deeper, wide shoulder, ragged (noise only shrinks the disc), slump pass, over-shed. Shaved mass sheds as fluffy lumps at your feet (`SculptTool.ShedLump`). **Score** = Shift+RMB, same job, tiny radius, deeper.
- **Squeeze (RMB hold, ball in hand)**: staged `RescaleJob` to 255 compaction, ~1/3 volume lost from powder (lossy on purpose). A ball at/over `packedThreshold` comes up to the **work pose** instead and RMB carves it in the palms (`SnowballRoller.SetWorkPose`). Fluffy *thrown* balls burst on a hard landing (`SculptureFactory.Burst`); drops never do.
- **Structure** (`SculptureStructure.Check`, after every bite and shave stroke on a fixed sculpture, never on loose balls): thinness = per-voxel morphological opening quantized to voxel steps, limit lerp(`thinLimitFluffy` 18 cm, `thinLimitPacked` 5 cm) → at 3 cm voxels fluffy breaks ≤ 18 cm, packed breaks ≤ 6 cm; connectivity = flood from the ground line (`SculptureStructure.GroundHeight`, wired by the roller) — islands lift out as voxel-aligned balls and drop. **Twig = armature**: snow within `twigSupportRadius` of a twig segment never counts as thin and bridges the flood. Outcomes replicate as exact voxels (`Thinned`/`Detached` events); peers never re-run the check.
- **Four numbers decide whether this update works** and none have been play-tuned yet: `smoothStrength`+`smoothKernelRadius` (beads → tube in 1–2 strokes), `shaveDepthPacked`, `thinLimitPacked`/`thinLimitFluffy`, `squeezeShrink`. Not built: pinch (optional), underfoot compaction, sintering.

### Hands = stretchy two-bone IK (`Snowfield.Player.HandRig`), not an animation set
- The rule is **the hands go where the snow is**. Snow is authored elsewhere (a carried ball rides the carry anchor / your feet / the attach preview; a scooped chunk is born under the cursor and flies to the anchor), so the arms follow it rather than the other way round — which makes a scoop read for free.
- Reaching that snow is not an arm's length away, so the bones **stretch**: elbow and wrist local offsets scale along their own bone (capped at `handMaxStretch`, default 3.5x ≈ 1.35 m) and linear skinning turns that into taffy. Cartoon on purpose. Solved in LateUpdate after Mecanim, rest lengths restored first so nothing compounds.
- No `OnAnimatorIK`/Animation Rigging: a delta-rotation two-bone solve (`HandRig.Solve`, pure and unit-tested in `Snowfield.Player.EditTests`) needs no bone-axis knowledge and no IK Pass on the controller.
- Held snow is **one-handed until it is worth two**: under `handTwoHandedRadius` (0.22 m) it rides in one palm at your side (`PalmAnchor`) and the other hand stays free; bigger than that and both hands hoist it overhead (`CarryAnchor`). `SnowballRoller.TwoHandedCarry` is the single answer to that question — it picks both the hold position and the hand count, so they can never disagree. A whole carried sculpture always counts as two-handed.
- **Measure poses off the rig, not the capsule.** This character is chibi-proportioned: the capsule is 2.1 m but the shoulders sit at ~0.98 m and the eye line (`CameraPivot`, hand-tuned) at **1.08 m** — most of the height is head. Anything held much above 1.0 m is over the player's own head and off the top of their screen. `HandRigPlaytest` logs shoulder height and the viewport coordinates of held snow for exactly this reason.
- `SculptTool.DriveHands` owns intent (hold / pat / place / ready-hover), `HandRig` owns mechanics (spring, weight blend, solve). `Reach` every frame; `Pulse` for snow that stops existing the instant you touch it (fuse, throw, drop, accessory).

### Snowballs — faked, not simulated (built: each snowball is a small 48³ `SnowSculpture` + `Snowball`, loose until something is attached, then `SculptureFactory.Promote`s it to 96³; flight = temporary Rigidbody + sphere collider, splat = `Absorb`)
- Rolling: a sphere prop that scales up while rolling over fresh snow, consuming a terrain decal layer (leaves the classic trail).
- Attaching: **stamp a sphere of density** into the target sculpture's grid, delete the prop. Reuses brush plumbing.
- Throwing: the ball leaves the hand with your own momentum added, backspin, and a speed that **falls as the ball gets heavier** — a boulder is a heave, a handful is a fastball. Lift is an *angle* above the reticle, never an added upward speed (a fixed one bends a weak throw far more than a hard one, so the ball never lands where you aimed). The thrower's colliders are passed to `Snowball.Launch` and ignored for the flight: the ball is born centimetres from your own capsule and you can outrun a soft throw. On contact the velocity is **split along the surface normal** (`Snowball.Bite`) — speed *into* the surface is swallowed, a third of the speed *along* it survives and becomes rolling spin, so a flat throw skids a metre or two instead of dying where it lands. Only a contact carrying real normal speed takes that bite, and grounded-ness is a **grace timer, not a collision edge**: PhysX drops and remakes contact constantly as a ball rolls over terrain seams, so keying rolling resistance off Enter/Exit switches it off for half the roll and the ball coasts away. Rolling resistance is **damping plus a constant `ploughResistance`**, and the constant is the part that matters: speed-proportional damping can only asymptote, and on any slope at all it asymptotes *above* `restSpeed`, so the ball never rests and trundles downhill for ever. A constant force has a stall threshold instead (~21° at 2.9 m/s²); past that the ball settles into a slow creep rather than a real roll, so `creepSpeed`/`creepTime` plant anything crawling for 2 s — otherwise it would stay a dynamic sphere and never get its mesh colliders back. The landing lift onto the visible snow (the shell has no collider, so the sphere rides the terrain a snow-depth below) eases in over `settleTime` rather than popping in one frame.

### Sticks & found objects = props, zero voxel involvement
- Raycast to sculpture surface, socket/parent, store `{prefabId, localPos, localRot}` in the sculpture record.

### Data model (Phase 3)
```
Sculpture: { id, fieldPos, densityBlob (RLE-compressed), props[], authorId, neighborhoodId, createdAt, modifiedAt, buriedFlags }
```
RLE compresses brutally well (density fields are mostly-empty or mostly-full). Sync = poll "sculptures in my neighborhood modified since T" on login + lazy interval.

### Live co-sculpt = P2P NGO + UGS Sessions (BUILT, Aug 2026 — see NETWORKING.md for the full map)
- Everyone lands in one shared field on launch: quick-join any open public session or host a new one
  (`NetBootstrap`, UGS Sessions `MatchmakeSessionAsync` over **Relay**; the Sessions package starts/stops NGO
  itself — never call StartHost/StartClient around it). Any failure degrades to plain offline single-player.
- Exactly the planned event model: brush strokes (with explicit tick counts + target ids), structural results
  (scoop, fuse/splat, exact regrow geometry, prop attach/remove, rest, and since Phase 1.5: shave stamps with
  their derived params, squeeze, shed, thinned/detached voxels, burst) as host-relayed events through
  `SnowNetChannel`; the origin applies locally first and skips its own echo. Wire version 3: snapshots and
  scoops carry the compaction blob next to the density blob. Physics flights stay local —
  remotes get a cosmetic kinematic arc corrected by the splat/rest event. Carried balls stream pose+radius
  at 10 Hz unreliable. Voice = Vivox positional channel per session (open mic, **M** mutes).
- Sculpture identity = logical ulong ids in `SculptureRegistry` (`SculptureNet` seam raises
  Created/Replaced/Removed through the factory's destroy-and-replace churn). Late joiners wipe their local
  field and stream the host's world as id-tagged save-format records; clients save the shared field to slot 1
  so quit-autosave never clobbers their own field 0; F9 is blocked in-session.
- If drift appears: periodic chunk checksums, loser re-downloads blob (`EncodeSnapshot` is the ready repair
  payload). Jam-grade is fine.
- Session end state still uploads to PocketBase so it persists for the neighborhood. Phase 3 stays PocketBase polling — a live session is not persistence.

## Phases (build in order; each phase is independently valuable)

1. **Sandbox (no networking):** field, third-person character, add brush, smooth brush, incremental remesh, snowball roll+attach, stick props, local save/load. *Gate: is the toy delightful? If not, stop and fix feel.*
2. **Feel pass:** audio layers (soft fwump), particles, snow shader (dusting, soft rim), path carving + snowfall cycle, day/night or time-of-day mood.
3. **Async neighborhood:** PocketBase, auth, neighborhood assignment (friends-first via invite links; organic joiners fill youngest non-full neighborhood, ~40–80 players), upload/download, polling, "bury under snow" report (per-player hide + server threshold buries for all).
4. **Friend live co-sculpt:** stroke sync for 2–6 invited players. *(Built early, Aug 2026, as one public
   shared room for everyone — see NETWORKING.md. Friends-only rooms later = session name/password knobs.)*
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

- Wire the sculpting kit into the Main scene (idempotent, touches nothing else): `-nographics -quit -executeMethod SnowDays.EditorTools.MainSceneSetup.Run` (also menu Snowfield ▸ Ensure Main Scene Sculpting). Scene layout convention — one responsibility per GameObject: `Player` (SnowDays.PlayerController +HandRig, moves its own `CameraPivot › PlayerCamera`; whole subtree on layer Ignore Raycast) › `SculptTool` (+AccessoryPlacer +SnowballRoller) · `CarryAnchor` · `PalmAnchor`; roots `Sculptures` (SculptureFactory) · `SaveLoad` · `BrushCursor` (inactive) · `HUD` (Canvas + ToolHud). SnowDeform/Snowfall/ground-adapter bootstrap themselves at play — no scene objects.
- Render the Main scene to PNG (needs graphics, so no `-nographics`): `-executeMethod Snowfield.Editor.HeadlessScreenshot.Run -screenshotOut Screenshots/x.png` (add `-screenshotDemo` to stamp a mound in front of the camera).
- Look at the arms: create `Assets/Editor/HANDRIG_PLAYTEST.txt` (and touch a `.cs` — markers alone do not force the domain reload) to run `HandRigPlaytest`. Enters play mode, scoops a ball, captures the carry and roll poses first- and third-person to `Temp/handrig_*.png`, and logs the stretch factor and hand-to-grip error per arm. Needs the Unity **app frontmost** for the whole run: the editor drops the cursor lock when it is not, and the tool is inert without one.
- Tests: `-runTests -testPlatform EditMode|PlayMode -testResults out.xml` (PlayMode needs graphics). **The Phase 1.5 code and its tests (`CompactionTests`, `StructureTests`, the new `WorldSyncTests`) were written without an editor and have not been run yet — run EditMode `Snowfield.Voxel.Tests` first.** Assemblies with `includePlatforms: []` (`Snowfield.Player.Tests`, `Snowfield.Sculpture.Tests`, `Snowfield.Net.Tests`) are **PlayMode** tests; the EditMode ones are the Editor-only assemblies (`Snowfield.Voxel.Tests`, `Snowfield.Player.EditTests`). `Snowfield.Net.Tests` needs `"testables": ["com.unity.netcode.gameobjects"]` (in the manifest) for the NGO in-process host+client helpers.
- **Live-editor ops (batchmode can't run while an editor holds the project lock):** drop a request file into `NetOps/` and poll `NetOps/<op>.result` — `refresh` · `scene_setup` (MainSceneSetup.Run; skips if the open scene is dirty) · `tests_editmode`/`tests_playmode` (file content = `;`-separated assembly filter) · `play_smoke` (content = seconds of play mode) · `build` (content = output path, macOS dev build) · `restart` (saves titled dirty scenes, relaunches the editor — required once after adding a package with Burst function pointers, e.g. Unity Transport, or Burst throws `Burst failed to compile the function pointer` at runtime). Implementation: `Assets/Editor/SnowOps.cs`.

Gotchas learned: `SerializedProperty.objectReferenceValue` silently drops refs to custom ScriptableObjects in batchmode — assign fields directly (`SnowSculpture.EditorAssign`, MainSceneSetup does this everywhere). Edit-mode screenshots render shadows but not play-mode-only behaviour (brush, SnowDeform window, snowfall, the GameCube fullscreen passes). The player camera is **Untagged** (`Camera.main` is null in Main) — wire cameras explicitly, and don't tag it: `SnowfallSystem` follows `Camera.main` and today's scene behaviour is the reference. The SnowDays-era pitfalls (URP YAML `m_Version`, marker-armed playtests, Editor.log ownership) are in auto-memory `unity-urp-handauthored-assets`.

## Controls (Main scene)

One persistent state (Hand) + a Tab accessory overlay: **LMB** scoop snow into your hands — on a sculpture it bites out the chunk under the red cursor sphere (small bites in packed snow leave crisp edges), on bare ground a handful (mass continuity, leaves a divot in the surface snow); while carrying, a tap lets go where the snow already is (fuses into snow, otherwise falls under gravity) and holding charges a throw · **Shift+LMB** smooth/pat (works with a ball in hand; welds, finishes, firms a little) · **RMB drag** shave the surface under the yellow cursor (crisp on packed snow, a crumbly gouge on powder; shavings pile up at your feet) · **Shift+RMB drag** score a groove · **RMB hold with a ball in hand** squeeze it packed (three crunches); a packed ball comes up in front of you instead and RMB carves it in the palms · cutting something too thin for its packing, or free of the ground, breaks it off (twigs hold snow up) · a carried ball rolls at your feet only while the cursor points near you, otherwise it is held — in one hand at your side, or overhead in both once it is too big to palm · **scroll** brush radius · **Tab** accessory overlay (scroll picks · LMB places, or removes the accessory under the cursor). Sculpting inputs only fire while the cursor is locked (mouse-look mode). The arms follow all of this: a handful rides in one palm and a big ball takes both hands, one hand pats or places while the other holds, and a free hand drifts over snow it could scoop — stretching as far as it takes (see Hands, above). First person (`SnowDays.PlayerController`): WASD move · mouse look · Shift run · Space jump · Q crouch · E tiptoe (hold-based stances; capsule is bottom-anchored, camera pivot rides the height) · Esc unlocks cursor (LMB re-locks). Trampled snow refills on its own (`SnowDeformSystem.m_RefillSeconds`, 600 s; deformation lives in an 80 m window around the player and is lost beyond it). Sculptures auto-save to `persistentDataPath/sculptures/field0` on quit and auto-load on start (**F5** save · **F9** reload; `SaveLoadManager`). Field switching was removed with the heightmap terrain (slot 1 is now the client-side copy of a multiplayer session's shared field). **Multiplayer**: the game starts single-player; **Shift+N** joins the one public session (or hosts it) and drops you into the shared field, and Shift+N again leaves it — see NETWORKING.md. **M** toggles the voice-chat mic; F9 is disabled while in a session; the HUD's third status line shows the connection state. Snowballs are already small sculptures: brush them, attach balls anywhere on their surface, decorate them; attaching fixes them in place and promotes them to a full grid (`SculptureFactory.Fuse/Promote`). Ground raise/carve sculpting was dropped with the heightmap (the trample surface only presses down); walking and rolling still press paths into it.
