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
- **Snow that joins two sculptures makes them one.** Every `Fuse` ends in `SculptureFactory.MergeTouching`: if a neighbouring sculpture's snow now meets this one's — overlapping, or within `mergeReachVoxels` (marching cubes rounds both surfaces off, so a voxel short of touching already reads as joined) — it is fused in as well, and the **bigger grid survives** because `Fuse` regrows the target and would clip the source at the wall. Packing snow on is the only thing that can close such a gap, so that is the only place this is asked. Three deliberate limits: loose balls and anything in a player's hands or mid-flight are never swallowed (attaching a ball is a tap, not a side effect); two shapes whose union would not fit `maxGridSize` stay two, seam and all, rather than lose snow off the end; and a merge is **not** re-derived on remotes — it replicates as the ordinary fuse event it already is (ids of both), so a replay never looks for merges of its own. Contact test: `SnowSculpture.TouchesSnow` → Burst `ContactTestJob`, which walks only the box where the two could meet and stops at the first solid-solid voxel. A merge throws no snow burst (`Fuse(..., spray: false)`): the fuse that closed the gap already puffed at the point of contact, and `BurstOnJoin` would put a second one at the centre of a snowman nobody touched. The factory also `Retire`s what it consumes (deactivate, then destroy) because `Destroy` is deferred and a dead sculpture would otherwise be found and merged straight back in.

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
- **Impacts spray** (`Snowfield.Sculpture.SnowBurst`): one self-bootstrapping world-space `ParticleSystem` that emits nothing on its own — every burst is explicit `Emit` calls with the position, velocity and size worked out from the contact point, the snow's radius and the impact speed. Called from the three places snow arrives somewhere with force: a ball landing, a ball striking a player, and **every** `SculptureFactory.Fuse` (so a hand-placed handful, a splat and a *replayed remote attach* all spray, for no extra bytes on the wire). Its shader is `Assets/Snowfield/Sculpture/Resources/SnowBurst.shader` — textureless, a soft round blob from the quad's own UV, in a `Resources` folder for the same reason `RetroSnow` is (nothing serialises a reference, so stripping would take it).

### Brushes
- **Add**: spherical falloff kernel raising density, rate-capped per tick so snow *accumulates* under a held press (this cap is the "packing" feel — make it tunable).
- **Smooth/pat**: blur kernel over the brush region. Cheap, high payoff.
- **Scoop**: one discrete bite, and **held it keeps biting as the cursor sweeps** — every bite of one press is centred on the *plane through the first contact point*, so a drag shaves an even band instead of boring into its own crater (the ray would otherwise land on the inside wall of the hole just dug and take the next bite from there). Spacing/rate in `SculptFeelConfig` (`scoopHoldSpacing`, `scoopHoldInterval`); the press that started the scoop keeps meaning scoop, so its release is never read as a drop or a throw, and colliders are deferred to the release because cooking a 192³ sculpture ten times a second is the one thing a sweep cannot afford.
- Brush ops are `IJobParallelFor` over the affected voxel AABB.

### Hands = stretchy two-bone IK (`Snowfield.Player.HandRig`), not an animation set
- The rule is **the hands go where the snow is**. Snow is authored elsewhere (a carried ball rides the carry anchor / your feet / the attach preview; a scooped chunk is born under the cursor and flies to the anchor), so the arms follow it rather than the other way round — which makes a scoop read for free.
- First-person bones **stretch** as far as needed: elbow and wrist local offsets scale along their own bone. `HandSyncReceiver` enables `HandRig.LimitStretch` on remote rigs only, capped at `handMaxStretch` (default 1x, natural length). Original hand goals still travel on the wire. Solved in LateUpdate after Mecanim, rest lengths restored first so nothing compounds.
- No `OnAnimatorIK`/Animation Rigging: a delta-rotation two-bone solve (`HandRig.Solve`, pure and unit-tested in `Snowfield.Player.EditTests`) needs no bone-axis knowledge and no IK Pass on the controller.
- Held snow is **one-handed until it is worth two**: under `handTwoHandedRadius` (0.22 m) it rides in one palm at your side (`PalmAnchor`) and the other hand stays free; bigger than that and both hands hoist it overhead (`CarryAnchor`). `SnowballRoller.TwoHandedCarry` is the single answer to that question — it picks both the hold position and the hand count, so they can never disagree. A whole carried sculpture always counts as two-handed.
- **Measure poses off the rig, not the capsule.** This character is chibi-proportioned: the capsule is 2.1 m but the shoulders sit at ~0.98 m and the eye line (`CameraPivot`, hand-tuned, and fixed now that nothing animates the capsule height) at **1.08 m** — most of the height is head. Anything held much above 1.0 m is over the player's own head and off the top of their screen. `HandRigPlaytest` logs shoulder height and the viewport coordinates of held snow for exactly this reason.
- `SculptTool.DriveHands` owns snow intent (hold / pat / scoop-ready); `HeldAccessoryView` owns accessory carry and placement intent, then follows the solved palm in LateUpdate. `HandRig` owns mechanics (spring, weight blend, solve). `Reach` every frame; `Pulse` for snow that stops existing the instant you touch it (fuse, throw, drop, accessory).

### Weight and strength (BUILT, Nov 2026)
- **A ball weighs what its volume weighs** (`Snowball.Mass`, packed snow at 350 kg/m³) and **there is no design cap on how big it can get** — only the wall of its own grid (`SculptFeelConfig.MaxBallRadius`, derived from `snowballGridSize`, now 64³ ≈ 1.2 m radius; loose balls never regrow, so the wall is final). What actually stops you is your own strength: the top rolling speed is `pushPower / mass`, and the same limit caps **how fast you can walk** — the ball is glued in front of you, so a ball that cannot keep up has to slow *you* down rather than be left behind. `pushMinSpeed` is a floor, not a stall: a limit of zero would weld the player to the spot with no way out.
- **Arms add up.** Each peer says what it has its hands on; every pusher derives the same limit from mass, power and head count, so two players roll a boulder neither could budge and nobody has to be authoritative about its speed. With empty hands, standing against and facing a ball someone else is rolling (within `pushHelpRange`) is what "helping" is — `SnowballRoller.LateUpdate`. Offline you are on your own.
- **Past the lift capacity the snow stays on the ground**: it rolls, it cannot be hoisted overhead or thrown, and it fuses *where it sits* (roll it up against a sculpture and tap). This is what keeps "no cap" from meaning a waist-high boulder floating over your head. One question, `SnowballRoller.TooHeavyToLift`, answered the same way by the hold pose, the throw and the HUD prompts.
- **Capacity is `liftMass` per pair of arms** (60 kg each ≈ a 35 cm ball), so the limit is a head count rather than a number: a snowman one player cannot budge is liftable the moment a second steps up to it. `SnowballRoller.CanLift` is the one place that asks, for a loose ball, a dropped snowball and a whole sculpture alike (`LiftCapacity` × `SnowPush.Arms`).
- **Your arms are on what the reticle is on**, when you are close enough to touch it — that is how two people get counted on the same snow *before* either of them lifts it (`SnowballRoller.ClaimableAtReticle`, read off the tool's aim so it costs no query). A claim on resting snow carries no speed limit; you are only leaning on it. Carrying a whole sculpture spends the same budget as shoving one, so a snowman you can just about lift is a waddle.
- **A sculpture's weight comes from its density** (`SnowSculpture.Mass` = volume × `snowDensity`), which is a pass over every voxel — 7 million on a grown 192³ grid. So it is cached, invalidated by any grid change, rescanned at most twice a second, and **not asked mid-stroke**: you are not about to pick up what you are patting, and asking while patting would rescan the grid for nothing. A ball answers from its nominal radius instead, which is free and is the number the player can see.
- The character controller obeys this through `Snowfield.Player.SnowPush` — the same inversion seam as `SnowGround`, because Snowfield.* cannot reference Assembly-CSharp.

### Snowballs — faked, not simulated (built: each snowball is a small 48³ `SnowSculpture` + `Snowball`, loose until something is attached, then `SculptureFactory.Promote`s it to 96³; flight = temporary Rigidbody + sphere collider, splat = `Absorb`)
- Rolling: a sphere prop that scales up while rolling over fresh snow, consuming a terrain decal layer (leaves the classic trail).
- **They hit people.** A body with a `Snowfield.Sculpture.SnowballTarget` (the local player self-wires one; remote avatars get one plus a capsule) takes the splat and a shove scaled by the ball's momentum against the player's own mass, instead of being scenery the ball rolls off. The ball does **not** stick to a person — it drops at their feet and settles into the usual rest event, which is why none of this needed a wire format of its own. See NETWORKING.md for who detects the hit and who applies it.
- Attaching: **stamp a sphere of density** into the target sculpture's grid, delete the prop. Reuses brush plumbing.
- Throwing: the ball leaves the hand with your own momentum added, backspin, and a speed that **falls as the ball gets heavier** — a boulder is a heave, a handful is a fastball. Lift is an *angle* above the reticle, never an added upward speed (a fixed one bends a weak throw far more than a hard one, so the ball never lands where you aimed). The thrower's colliders are passed to `Snowball.Launch` and ignored for the flight: the ball is born centimetres from your own capsule and you can outrun a soft throw. On contact the velocity is **split along the surface normal** (`Snowball.Bite`) — speed *into* the surface is swallowed, a third of the speed *along* it survives and becomes rolling spin, so a flat throw skids a metre or two instead of dying where it lands. Only a contact carrying real normal speed takes that bite, and grounded-ness is a **grace timer, not a collision edge**: PhysX drops and remakes contact constantly as a ball rolls over terrain seams, so keying rolling resistance off Enter/Exit switches it off for half the roll and the ball coasts away. Rolling resistance is **damping plus a constant `ploughResistance`**, and the constant is the part that matters: speed-proportional damping can only asymptote, and on any slope at all it asymptotes *above* `restSpeed`, so the ball never rests and trundles downhill for ever. A constant force has a stall threshold instead (~21° at 2.9 m/s²); past that the ball settles into a slow creep rather than a real roll, so `creepSpeed`/`creepTime` plant anything crawling for 2 s — otherwise it would stay a dynamic sphere and never get its mesh colliders back. The landing lift onto the visible snow (the shell has no collider, so the sphere rides the terrain a snow-depth below) eases in over `settleTime` rather than popping in one frame.

### Menus & settings = one panel, prefab rows, two statics (BUILT, Nov 2026)
- **Esc owns the pause menu** (`Snowfield.UI.PauseMenu`), not the player controller: it opens, and
  `Snowfield.Config.GamePause` frees the cursor and stops the clock. The controller's Esc-unlocks-cursor is gone;
  its LMB relock is suppressed while paused or the first click on a button would snatch the mouse back.
- **A session is never frozen.** `GamePause.Set(paused, freezeTime)` — single-player stops `Time.timeScale`, a
  shared field does not (the others are still sculpting), and resuming only restores a freeze it caused.
- **Pages are built at runtime from row prefabs** under `Assets/Prefabs/UI` (`MenuButton` · `MenuSlider` ·
  `MenuToggle` · `MenuDropdown` · `MenuRow` · `MenuPanel` · `ConfirmDialog` · `PauseMenu`) into one `MenuPanel`.
  Half of each page depends on the moment (in a session or not, this screen's resolutions, is the mic live), so a
  hand-authored page tree goes stale — but what a slider *looks like* is not a runtime question and lives in the
  prefab. `PauseMenuPrefabs` writes each one **once** and never touches it again: restyling them is the point.
  The panel grows to its content (nested `VerticalLayoutGroup` + `ContentSizeFitter`).
- **A column must control its children's height.** With `childControlHeight` off, a `VerticalLayoutGroup` stacks
  its children by reading each one's *current* RectTransform height rather than asking what height it wants — and
  a freshly instantiated row has not been laid out yet, so the answer is whatever its prefab was saved with.
  Three row prefabs had been saved with a zero-size root, which put every slider, toggle and dropdown on a page
  at the same offset, drawn on top of one another. The height now comes from each row's own `LayoutElement`,
  which is where its authored size lives anyway (the `MenuButton` trick), so the prefabs still own their looks
  and the column cannot be fooled by a stale rect. `Sized` also writes the size onto the RectTransform, because
  the saved prefab is what a layout group reads on the first frame an instance exists.
- **Settings live in `Snowfield.Config.GameSettings`**, PlayerPrefs-backed, applied on write, saved on menu close.
  Everything that obeys a setting reads it from there, so no one has to know the menu exists (asmdef direction:
  Assembly-CSharp → Config, never the reverse). Master volume is `AudioListener.volume`; music and effects are
  stored for the Phase 2 audio layers to multiply in (there is no game audio yet — those two sliders currently
  move a number nothing listens to); voice is applied to Vivox by `Snowfield.Net.VoiceChat.SetOutputVolume`.
- **Microphone and output device** are picked on the Audio page, stored **by name** (an index would point at a
  different microphone every time the list reorders) with "System default" always first. Listing them means
  bringing the Vivox audio client up — `InitializeAsync` is enough, no login, no channel — so it happens when
  someone opens that page rather than on every launch, and `VoiceChat.DevicesChanged` rebuilds the page when the
  answer arrives (or when a headset is plugged in). Without Unity Services the row says so rather than offering
  an empty dropdown. Applied by `VoiceChat.ApplyDevices` off `GameSettings.Changed`, like every other setting.
  Outside play mode `Apply` touches **nothing but the audio listener** — v-sync and quality level are the
  project's own settings assets, and a test that writes them is how the repo grows settings nobody authored.
- Quit to Title saves the field first (nothing autosaves on a scene load, only on quit) and leaves the session.

### Sticks & found objects = props, zero voxel involvement
- Raycast to sculpture surface, socket/parent, store `{prefabId, localPos, localRot}` in the sculpture record.
- Inventory accessories are held in front/right (`HeldAccessoryView`), following the solved palm at authored size. Palm orientation constrains fingertip aim and wrist roll while keeping item facing independent; `Grip` markers can author a position and palm rotation per prefab. Aiming never moves the hand onto snow. A click pins the chosen stack and surface point, reaches, then places/spends at contact; cancelling before contact spends nothing. There is no surface ghost or floating inventory model. Hold offset, item/palm rotation and placement/recovery timing live in `SculptFeelConfig`.

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
  (scoop, fuse/splat, exact regrow geometry, prop attach/remove, rest) as host-relayed events through
  `SnowNetChannel`; the origin applies locally first and skips its own echo. Physics flights stay local —
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

- Assembly definitions from the start: `Snowfield.Voxel`, `Snowfield.Player`, `Snowfield.Net`, `Snowfield.UI` (menus; it may reference the lot, nothing may reference it).
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

- Wire the sculpting kit into the Main scene (idempotent, touches nothing else): `-nographics -quit -executeMethod SnowDays.EditorTools.MainSceneSetup.Run` (also menu Snowfield ▸ Ensure Main Scene Sculpting). Scene layout convention — one responsibility per GameObject: `Player` (SnowDays.PlayerController +HandRig, moves its own `CameraPivot › PlayerCamera`; whole subtree on layer Ignore Raycast) › `SculptTool` (+AccessoryPlacer +SnowballRoller) · `CarryAnchor` · `PalmAnchor`; roots `Sculptures` (SculptureFactory) · `SaveLoad` · `BrushCursor` (inactive) · `HUD` (Canvas + ToolHud) · `PauseMenu` (its own canvas, sortingOrder 100) · `EventSystem` (uGUI needs one; the scene had none before the menu). SnowDeform/Snowfall/ground-adapter bootstrap themselves at play — no scene objects.
- Build the menu prefab kit (create-only, per prefab; run by the scene setup too): `-nographics -quit -executeMethod SnowDays.EditorTools.PauseMenuPrefabs.Run` (also menu Snowfield ▸ Create Pause Menu Prefabs).
- Render the Main scene to PNG (needs graphics, so no `-nographics`): `-executeMethod Snowfield.Editor.HeadlessScreenshot.Run -screenshotOut Screenshots/x.png` (add `-screenshotDemo` to stamp a mound in front of the camera).
- Look at the arms: create `Assets/Editor/HANDRIG_PLAYTEST.txt` (and touch a `.cs` — markers alone do not force the domain reload) to run `HandRigPlaytest`. Enters play mode, scoops a ball, captures the carry and roll poses first- and third-person to `Temp/handrig_*.png`, and logs the stretch factor and hand-to-grip error per arm. Needs the Unity **app frontmost** for the whole run: the editor drops the cursor lock when it is not, and the tool is inert without one.
- Tests: `-runTests -testPlatform EditMode|PlayMode -testResults out.xml` (PlayMode needs graphics). Assemblies with `includePlatforms: []` (`Snowfield.Player.Tests`, `Snowfield.Sculpture.Tests`, `Snowfield.Net.Tests`) are **PlayMode** tests; the EditMode ones are the Editor-only assemblies (`Snowfield.Voxel.Tests`, `Snowfield.Player.EditTests`, `Snowfield.UI.EditTests`). `Snowfield.Net.Tests` needs `"testables": ["com.unity.netcode.gameobjects"]` (in the manifest) for the NGO in-process host+client helpers.
- **Live-editor ops (batchmode can't run while an editor holds the project lock):** drop a request file into `NetOps/` and poll `NetOps/<op>.result` — `refresh` · `scene_setup` (MainSceneSetup.Run; skips if the open scene is dirty) · `tests_editmode`/`tests_playmode` (file content = `;`-separated assembly filter; **empty = this project's `Snowfield.*Tests` only** — `"testables"` in the manifest means an unfiltered run also runs NGO's whole runtime suite, which is hours, so going that wide takes an explicit `*`) · `play_smoke` (content = seconds of play mode) · `build` (content = output path, macOS dev build) · `restart` (saves titled dirty scenes, relaunches the editor — required once after adding a package with Burst function pointers, e.g. Unity Transport, or Burst throws `Burst failed to compile the function pointer` at runtime). Implementation: `Assets/Editor/SnowOps.cs`.

Gotchas learned: `SerializedProperty.objectReferenceValue` silently drops refs to custom ScriptableObjects in batchmode — assign fields directly (`SnowSculpture.EditorAssign`, MainSceneSetup does this everywhere). Edit-mode screenshots render shadows but not play-mode-only behaviour (brush, SnowDeform window, snowfall, the GameCube fullscreen passes). The player camera is **Untagged** (`Camera.main` is null in Main) — wire cameras explicitly, and don't tag it: `SnowfallSystem` follows `Camera.main` and today's scene behaviour is the reference. The SnowDays-era pitfalls (URP YAML `m_Version`, marker-armed playtests, Editor.log ownership) are in auto-memory `unity-urp-handauthored-assets`.

## Controls (Main scene)

One persistent state (Hand) + a Tab accessory overlay: **E** pick the whole thing up — a loose ball, a snowball somebody left lying about, a finished sculpture — if your arms (and any helping you) can manage its weight, then **LMB** puts it down: on another sculpture it fuses, which is how a snowman gets assembled out of parts, and anywhere else it is set on the ground. The prompt says "too heavy to lift alone" when it is, because a friend changes the answer. · **LMB** scoop snow into your hands — on a sculpture it bites out the chunk under the red cursor sphere, on bare ground a handful (mass continuity, leaves a divot in the surface snow), and **holding it keeps scooping** as you sweep the cursor — every bite of one press at the depth the first one started at, so a drag shaves a band rather than digging a hole; while carrying, a tap lets go where the snow already is (fuses into snow, otherwise falls under gravity) and holding charges a throw · **RMB** smooth/pat (works with a ball in hand — the ball stays put) · **Shift+LMB** planar shave on a sculpture, **Shift+scroll** turns the cut line · a carried ball rolls at your feet only while the cursor points near you, otherwise it is held — in one hand at your side, or overhead in both once it is too big to palm; **past `liftMass` it is simply too heavy to pick up at all** — it stays on the ground, rolls, cannot be thrown, and fuses where it sits, and the heavier it is the slower both it and you go (another player leaning on the same ball lends you their arms; see Weight and strength) · **scroll** brush radius · **Tab** accessory overlay (scroll picks · LMB places, or removes the accessory under the cursor; **lights and scarf** are chains — each click adds a point, the strand drapes live over the snow, clicking one of its own points closes a loop, Shift+click finishes). Sculpting inputs only fire while the cursor is locked (mouse-look mode). The arms follow all of this: a handful rides in one palm and a big ball takes both hands, one hand pats or places while the other holds, and a free hand drifts over snow it could scoop — stretching as far as it takes (see Hands, above). First person (`SnowDays.PlayerController`): WASD move · mouse look · Shift run · Space jump · **Esc** opens the pause menu (LMB re-locks the cursor if you leave it free some other way). The menu is part of `Game.prefab`: Resume · Audio · Controls · Display · Multiplayer · Save Field · Quit to Title · Quit to Desktop, the last three asking first where it matters (`PauseMenu`, see Menus & settings). Trampled snow refills on its own (`SnowDeformSystem.m_RefillSeconds`, 600 s; deformation lives in an 80 m window around the player and is lost beyond it). Sculptures auto-save to `persistentDataPath/sculptures/field0` on quit and auto-load on start (**F5** save · **F9** reload; `SaveLoadManager`). Field switching was removed with the heightmap terrain (slot 1 is now the client-side copy of a multiplayer session's shared field). **Crouch (Q) and tiptoe (E) were removed in Nov 2026** — the capsule keeps its authored height, so the eye line no longer rides a stance, and E belongs wholly to picking snow up; the stance code (height scales, the overhang sphere-cast, the speed multipliers) is in git if they come back. **Multiplayer**: the game starts single-player; **Shift+N** joins the one public session (or hosts it) and drops you into the shared field, and Shift+N again leaves it — see NETWORKING.md. **M** toggles the voice-chat mic; F9 is disabled while in a session. Everything you can do is on screen: the HUD draws one prompt row per available action in a stack under the reticle — `[key] [label]`, the key as a mouse picture or a lettered cap — fed by `SculptTool.Actions`, plus a persistent bottom-right strip for Tab, Esc and (in a session) the mic. One row per action with its own key means an action can only be shown with the input that actually performs it. The whole scheme is written down once in `Snowfield.Player.Controls`, spelled by `InputGlyphInfo` and labelled by `CursorActionInfo`; the pause menu's Keys page prints that same table, so there is no second copy to go stale. The HUD's top-left status lines (mode/aim readout, connection state) are **off by default** — they are dev chrome; turn them on with ToolHud ▸ status ▸ show or the Display page's DEV STATUS LINES. The join code is its own top-left label and only appears while hosting (`JoinCodeLabel`); a code is always written as two groups of three (`JoinCodeFormat`), and the Start menu's Join Game field shows those groups as `--- ---` filling in as you type (`JoinCodeField`). **Snowballs hit other players**, who wear the splat and take a shove scaled by the ball's momentum; the ball drops at their feet. Impacts and every attach throw a puff of snow at the contact point (`SnowBurst`). Snowballs are already small sculptures: brush them, attach balls anywhere on their surface, decorate them; attaching fixes them in place and promotes them to a full grid (`SculptureFactory.Fuse/Promote`). Ground raise/carve sculpting was dropped with the heightmap (the trample surface only presses down); walking and rolling still press paths into it.
