# Phase 1.5 — Compaction & Carving (built Sep 2026)

The spec this implements is reproduced at the bottom. This top part is the map of what landed, where it lives,
what is tunable, and what still needs a play session.

**Design law: coarse in, fine out.** Adding is always blobby (scoops, balls, fuses). Precision comes only from
removing and refining, and only in packed snow. Snow is never created or deleted, only moved.

## What was built (milestone order from the spec)

| # | Milestone | Status | Where |
|---|-----------|--------|-------|
| 0 | Resolution decision | **3 cm** (was 4 cm). Every threshold below is in metres, so the number is one field in `SculptFeelConfig.asset`. Old saves keep their own voxel size (`SnowSculpture.voxelSizeOverride`). | `SculptFeelConfig.voxelSize` |
| 1 | Compaction channel + provenance + tint/audio read + volume shrink | built | `VoxelGrid.Compaction`, every job in `Snowfield.Voxel`, `SnowSculpt.shader` (`_SnowPackedTint`), `SnowAudio` |
| 2 | Shave on packed snow | built | `ShaveJob`, `SnowSculpture.ApplyShave`, `SculptureShave`, `SculptTool.ShaveTick` |
| 3 | Fluffy-shave response (depth ×, ragged boundary, slump, over-shed) | built | `SculptFeelConfig.ShaveDepth/ShaveShoulder/ShaveNoise/Slump/ShedVolume` |
| 4 | Squeeze + in-hand carving | built | `RescaleJob`, `SnowSculpture.Squeeze`, `SculptTool.UpdateSqueeze`, `SnowballRoller.SetWorkPose` |
| 5 | Score | built | Shift+RMB: same job, `scoreRadiusFraction` × radius, `scoreDepthMultiplier` × depth |
| 6 | Breakage: connectivity + thinness, compaction-scaled | built | `ThinnessJob`, `ConnectivityJob`, `SculptureStructure.Check` |
| 7 | Twig armature | built | `AccessoryCatalog.IsArmature`, `SnowSculpture.SupportSegments`, `SupportSegment` |
| 8 | Crisp-edge falloff swap for packed small bites | built | `SculptFeelConfig.BiteShoulder` (used by `SculptTool.ScoopChunk`, sent in the Scoop event) |
| 9 | (optional) Throw burst/survive by compaction | built | `Snowball.OnCollisionEnter` → `SculptureFactory.Burst` |
| 9 | (optional) Pinch | **not built** (cuttable per spec; shave + bite first) | — |

Not built from §1: "underfoot while walking" compaction (marked optional/mild). Deferred per spec: sintering.

## Compaction

Second byte per voxel, `VoxelGrid.Compaction`, 0 = fresh powder … 255 = fully packed. Only meaningful where
density > 0; air is always 0. It is set by **how snow arrives**, never a mode:

| Source | Compaction | Code |
|---|---|---|
| Rolled ball / growth | `compactionRolled` (220) | `Snowball.Grow` → `StampSphere` default |
| Squeezed | 255 | `RescaleJob.CompactionBlend` |
| Scooped handful / loose fill / shed lumps | `compactionScooped` (40) | `SnowballRoller.ScoopFrom`, `SculptTool.ShedLump` |
| Fuse weld zone | ≥ `compactionWeld` (180) where both bodies were solid | `AbsorbJob.WeldCompaction` |
| Pat | `+patCompactionPerTick` × falloff, plus blur of neighbours | `SmoothBrushJob` |
| Bite / extract | rides along with the snow | `ExtractChunkJob` |
| Pre-channel saves and snapshots, starter mound | `compactionLegacy` (160) | `SaveLoadManager.FromRecord`, `CreateMound` |

Mixing rule everywhere: `BrushMath.MixCompaction` — mass-weighted, so packed snow buried under powder stays
mostly packed.

**Reads:** the mesh carries compaction per vertex (`SnowVertex.Compaction`, TexCoord0, mass-weighted over the
cell's corners so air corners don't dilute it); `SnowSculpt.shader` multiplies albedo by `_SnowPackedTint`
(0.86, 0.90, 1.0) at full packing. `SnowAudio` is a procedural placeholder (filtered noise bursts) whose pitch
rises with packing; squeeze has one crunch per stage. Phase 2 swaps in recorded layers behind the same calls.

**Volume shrink:** one multiplier, `packShrinkFraction` (0.33 of volume per full 0→255 compaction span). The pat
applies it as a density scale per tick proportional to compaction gained (a few % per pass); the squeeze applies
it as a geometric resample (`RescaleJob`, ~1/3 from powder, less from already-firm snow — the shrink is what the
ball still has to give).

## Shave / score (RMB)

Surface-relative disc at the hit point, oriented by the hit normal: everything above the cut plane inside the
disc is removed outright, a `SoftVoxels` ramp fades it out below `DepthVoxels`. Repeated passes take the local
high spots first, so the surface converges on the stroke, not the tool shape. Stamps land every
`shaveStampSpacing` radii of cursor travel (interpolated along the drag, capped at 8/frame); a still cursor takes
one layer, and colliders re-cook every `shaveColliderRefresh` s mid-stroke so a long drag keeps finding the
receding surface.

Everything about a stamp derives from the packing under the cursor (`SnowSculpture.CompactionUnderSurface`,
sampled 1.5 voxels in along the normal) through `SculptureShave.ParamsFor`, and the derived params are sent
verbatim so peers replay the identical cut:

| | packed (1) | fluffy (0) |
|---|---|---|
| depth | `shaveDepthPacked` 4.5 cm | × `shaveDepthFluffyMultiplier` 3 |
| shoulder | `shaveShoulderPacked` 0.85 (crisp) | `shaveShoulderFluffy` 0.25 (melts out) |
| boundary noise | 0 | `shaveNoiseFluffy` 0.3 × radius, only ever *shrinks* the disc |
| slump after each stamp | 0 | `slumpStrength` 0.5 relaxation pass at 1.2 × radius |
| shed lump | `shedVolume` | × `shedFluffyMultiplier` |

Removed mass is measured per stamp (`RegionMassJob` before/after) and accumulates in the tool; every
`ShedVolume(pack)` m³ a loose fluffy lump drops at your feet (`SculptTool.ShedLump`, a normal snowball launch,
broadcast as a `Shed` event). Leftovers worth ≥ 30 % of a lump shed at stroke end.

**Score** = same job with `scoreRadiusFraction` × radius (min 2 voxels) and `scoreDepthMultiplier` × depth.

## Squeeze and in-hand carving (RMB with a ball in hand)

RMB latches a mode on the press: a ball under `packedThreshold` (mean compaction 200) **squeezes** — over
`squeezeTime` it crunches through `squeezeStages` stages, each a `RescaleJob` step so the last one lands on
exactly 255 and the total linear scale is cbrt(1 − squeezeShrink × (1 − pack₀)). Both hands clench into it
(`SculptTool.HoldPoint` grip shrinks with `SqueezeProgress`; the HUD ring shows the same number).

A ball at or over the threshold instead comes up to the **work pose** (`SnowballRoller.SetWorkPose`: forward
`workPose.x`, up `workPose.y` from the feet, colliders on so the reticle finds it) and RMB drag shaves it, Shift
scores it — one hand holds, the other carves. Release drops it back to the palm. LMB is dead in the work pose,
so nothing gets thrown by accident. Fuse the finished component on as usual.

Throws: a ball launched over 2 m/s with mean compaction under `burstCompaction` (90) that hits something hard at
≥ `burstSpeed` (4.5 m/s) bursts into three fluffy lumps carrying its snow onward (`SculptureFactory.Burst`).
Packed balls survive and fly true. Drops never burst.

## Structure (after every removal on a fixed sculpture)

`SculptureStructure.Check(s, region)` runs after a bite and at the end of a shave/score stroke (never on loose
balls):

1. **Thinness** (`ThinnessJob`, region + margin): a morphological opening with a per-voxel radius. Distances are
   quantized to voxel steps, so the limit is k = ceil(r(c)) steps with r = half of
   lerp(`thinLimitFluffy` 18 cm, `thinLimitPacked` 5 cm, c). A feature survives only if it has an interior voxel
   (≥ k + 1 steps from air), and every voxel within k steps of one is kept: slabs ≤ 2k voxels break, ≥ 2k + 1
   hold. At 3 cm: fluffy breaks ≤ 18 cm, packed breaks ≤ 6 cm — a 3× gap. Corners of thick bodies always survive
   (one full step from an interior keeps you). Snow within `twigSupportRadius` of a twig segment is interior
   regardless. Removed voxels crumble into the shed pile.
2. **Connectivity** (`ConnectivityJob`, whole grid): 6-connected flood at `connectThreshold` (64) from every
   solid voxel within `groundSeedDepth` of the ground line under it (`SculptureStructure.GroundHeight`, wired by
   `SnowballRoller` to the SnowDeform surface; falls back to the snow's own floor), twig-supported voxels bridge
   gaps. Islands adopt their sub-threshold shoulder, then lift out into a voxel-aligned ball grid
   (`ExtractIslandJob` / `ClearLabelJob`, exact copy so conservation holds), inherit their compaction and drop
   (`Snowball.Launch(0)`). Islands under `islandCrumbVolume` crumble instead.

Twig segments: origin − `twigSupportBelow` … origin + 45 cm along the prop's +Y (`AccessoryCatalog.IsArmature`).
Only the twig is an armature.

## Network (SnowWorldSync v3)

Snapshots and scoop events carry the compaction blob next to the density blob. New event kinds:
`Shave` (a frame's stamps with their derived params + slump + target ids), `Squeeze` (id, linear scale,
blend, radius), `Shed` (new ball id, pose, radius, compaction, velocity), `Thinned` (id, region, RLE mask),
`Detached` (source id, island id, voxel offset, grid, pose, radius, blobs), `Burst` (ball id, crumbs). Peers
never re-run the structural check: outcomes arrive as exact voxels, exactly like scoops. `ConfigHash` covers the
new feel numbers.

## The four tuning numbers (spec §7) — all need a play session

1. **Pat merge strength** — `smoothStrength` 0.5, `smoothKernelRadius` 2 (5³ kernel). Target: 3 beads → tube in
   1–2 strokes.
2. **Shave layer depth** — `shaveDepthPacked` 0.045 m (1.5 voxels).
3. **Packed/fluffy thinness gap** — `thinLimitPacked` 0.05 / `thinLimitFluffy` 0.18 (effective 6 cm / 18 cm at
   3 cm voxels).
4. **Squeeze shrink** — `squeezeShrink` 0.33.

Also unverified in play: the work-pose position (`workPose`), the shed cadence (`shedVolume`), the burst
threshold, the packed tint strength, and whether the procedural audio is tolerable for more than a minute.

## Tests

- EditMode `Snowfield.Voxel.Tests.CompactionTests`: compaction provenance in the add/stamp jobs, shave above/below
  the plane, fluffy depth, rescale volume, thinness (packed slab holds / fluffy slab breaks / sheet breaks / twig
  exempts), connectivity (island labelling, twig bridging).
- PlayMode `Snowfield.Sculpture.Tests.StructureTests`: scooped/rolled/weld provenance through the factory,
  squeeze volume + packing, shave layer + mass report, floating snow detaches as a ball conserving mass, thin neck
  breaks fluffy / holds packed, twig holds a fluffy neck.
- PlayMode `Snowfield.Net.Tests.WorldSyncTests`: shave, squeeze, snapshot compaction and detach round-trips.

None of these have been run yet: this was written without an editor. First thing next session:
`NetOps/tests_editmode` (Snowfield.Voxel.Tests) then `NetOps/tests_playmode` (Snowfield.Sculpture.Tests;Snowfield.Net.Tests).

---

# Original spec

# PHASE 1.5 — Compaction & Carving: Fine Shaping Without Breaking the Snow Fantasy

Extends the shipped sandbox (scoop / roll / fuse / pat / accessories). Goal: players can make a sitting cat, a swan, a castle rook — while the material still behaves like snow, not a free sculpting tool.

**Design law: coarse in, fine out.** Adding snow is always blobby (scoops, balls, fuses). Precision comes only from removing and refining — and only in *packed* snow. Snow is never created or deleted, only moved (existing conservation rule holds everywhere below).

---

## 1. Compaction channel

Add a second per-voxel byte alongside density:

```csharp
NativeArray<byte> compaction; // 0 = fresh powder … 255 = fully packed
```

Compaction is set by **how snow arrives** (ambient, no deliberate step required):

| Source                        | Compaction on arrival        |
|-------------------------------|------------------------------|
| Rolled ball                   | Born packed (~220) — rolling *is* packing |
| Squeezed ball (see §3)        | Fully packed (~255)          |
| Scooped handful, loose fill   | Fluffy (~40)                 |
| Fuse weld zone                | Weld shell of contact region raised to ~180 |
| Pat (Shift+LMB)               | +compaction per pass (moderate rate) in brush region |
| Underfoot while walking       | Optional, mild, small radius |

Deferred (do NOT build now, note only): sintering — untouched snow slowly firms over days. Phase 3+ if at all.

Visual/audio state read (required, this is how the system teaches itself):
- Tint: packed snow slightly denser/bluer (shader lerp on compaction sampled per-vertex, same interpolation as normals).
- Audio: pat/carve pitch rises with compaction. Squeeze has 2–3 crunch stages.
- **Volume shrink:** raising compaction slightly reduces density-region volume (a few % under pat, ~1/3 under full squeeze). Packing = same snow, smaller. One multiplier in the relevant jobs.

## 2. Right-click: the shaping hand (context-sensitive, no modes)

Left hand moves mass; right hand refines it. RMB behavior by context:

| Context                          | Action |
|----------------------------------|--------|
| Hands empty, cursor on sculpture, drag | **Shave** |
| Hands empty, Shift + drag on sculpture | **Score** |
| Ball in hands, hold              | **Squeeze** (§3) |
| Otherwise                        | Nothing |

### Shave (the workhorse — feel budget goes here)
Surface-relative removal, NOT a volumetric bite:
1. Raycast → hit point; surface normal = density gradient (already computed for shading).
2. Removal region = shallow disc: brush-radius wide along the surface, **1–2 voxels deep** into it, oriented by the normal. Same AABB job pattern as brushes; falloff is anisotropic — weight by depth below the local surface plane.
3. Stamp continuously along the drag path (reuse stroke interpolation so fast drags don't gap).
4. Removed density accumulates and periodically sheds as small loose lumps at the player's feet (fluffy compaction). Conservation holds; the crumb pile is a deliberate tell.

Why this yields flats/tapers/rounds: repeated shallow passes remove the local *high spots* first, so the surface converges on the stroke plane/path, not the tool shape. Bite decides where material isn't; shave decides what the surface is like.

**Shave on FLUFFY snow — works, but coarsely (never grey out the verb):**
- Depth multiplier ~3× (ragged gouge, not a skin).
- Falloff shoulder widened — no crisp termination.
- Small per-stamp noise on the removal boundary (ragged edge). Noise on the *boundary* only; removal stays predictable and strictly inside the brush region. Never destroy work outside where the player is touching.
- Over-shed: larger crumbly lumps.
- Post-stamp **slump pass** in the affected region: density at steep exposed gradients relaxes down/outward one iteration — cut edges round themselves off.
- Legit use: rough mass removal. Illegitimate: finish. The contrast with packed shave should be dramatic — comically crumbly vs. surgically clean. The gap IS the tutorial.

**Shave on PACKED snow:** thin, clean, crisp. At small brush sizes on high compaction, swap the falloff to a tight shoulder so cuts terminate sharply (carved look, not melted). Same for bite: tiny bites in fully packed snow leave crisp edges instead of spherical scoops (falloff-curve swap keyed to compaction).

### Score
Shave's edge case: tiny radius, deeper bias → a narrow groove along the stroke. For mouth lines, shell plates, brick courses, fur suggestion. Same job, different params.

### Pinch (OPTIONAL — build last, cuttable)
RMB tap on a packed edge: pulls a small ridge up from the local surface, drawing density from immediate neighbors (redistributes, doesn't create). Ear tips, beaks. Skip if shave+bite prove sufficient in testing.

## 3. Squeeze

Ball in hands + hold RMB (~0.5s): ball visibly compresses ~1/3 in volume, 2–3 crunch stages, tint shifts to packed. Result: fully packed ball.
- This is the just-in-time deliberate path: scoop → squeeze → fuse → carve. Pack the component you'll detail, not the whole sculpture.
- Since a held ball is already a small sculpture (existing spec), squeezing unlocks **in-hand carving**: shave/score a small packed piece in the palms, then fuse the finished component on. No new systems — verify the existing tools operate on held balls.
- Throws: fluffy balls burst into powder on impact (density returns to ground layer); squeezed balls survive, fly true, and fuse where they land.
- The shrink is the economy: squeeze trades volume for workability. Keep it lossy so "squeeze everything always" is never optimal; bulk mass still wants rolling.

## 4. Structural rules: breakage + armatures

After any removal op (bite, shave, score), in the affected sculpture:
1. **Connectivity:** flood-fill from the base/ground contact. Disconnected islands detach and drop as balls of the corresponding size (inherit their compaction). Charming failure, not punishment.
2. **Thinness check** (cheap local pass in the edited region): features below a thickness threshold break off. Threshold scales with compaction — packed tolerates **3–4× thinner** than fluffy. The gap must be dramatic: packing should feel like unlocking a material.
3. **Twig armature:** an embedded twig accessory supports the snow around it — voxels within a support radius of a twig's segment pass the thinness check regardless of compaction. Twigs become structure (swan necks, raised arms, tails), not just decoration. Real snow-sculptor technique.

## 5. Pat's revised role

Pat = **weld + finish + repair** (squeeze took over "prepare"):
- Merge bead chains into smooth tubes — tune the blur kernel so ~3 overlapping beads become a clean tube in 1–2 strokes, not 10. First-class tuning target.
- Blend fuse seams; unify surfaces at low strength.
- Moderate compaction gain — the fallback for firming loose fill in place.

## 6. Milestone order

1. Compaction array + provenance rules (rolled=packed, scoop=fluffy, weld shell) + tint/audio read + volume shrink.
2. Shave on packed snow only. **Stop and tune feel** (layer depth, shoulder, shed cadence) — this verb carries the update.
3. Fluffy-shave response (depth ×, ragged boundary, slump pass, over-shed). Tune the packed/fluffy contrast until it's night-and-day.
4. Squeeze (+ verify in-hand carving works on held balls).
5. Score.
6. Breakage: connectivity + thinness, compaction-scaled.
7. Twig armature support.
8. Crisp-edge falloff swap for packed small-brush bite/shave/score.
9. (Optional) Pinch. (Optional) Throw burst/survive by compaction.

**Before milestone 1:** test voxel resolution 2.5–3cm (from 4cm). A cat face is ~10 voxels across at 4cm — half the perceived capability gap may be resolution. Grid memory grows ~2.5–4×; still fine per-sculpture. Decide resolution first so all tuning happens at final fidelity.

## 7. Tuning targets (the update lives or dies on four numbers)

1. **Pat merge strength** — beads → tube in 1–2 strokes.
2. **Shave layer depth** — thick enough to shape, thin enough to not feel like biting.
3. **Packed/fluffy thinness gap** — 3–4×, dramatic, "new material unlocked."
4. **Squeeze shrink** — ~1/3; lossy enough to stay a choice.

## 8. Verification: paper-tested builds (use as acceptance tests)

- **Cat** (sitting, ~1.2m): roll 60–70cm haunch ball + 50cm chest, fuse; scoop-fill the gap; pat to blend; bite the neck/waist silhouette; head ball oversized → bite the notch between ears, shave ear faces, (pinch tips); fuse muzzle handful, pat, shave brow, score mouth/eyes; bead-chain tail on ground, pat to tube, shave round.
- **Turtle:** one big ball, bite off top third → dome; shave rim clean; score shell hexagons; fuse feet/head balls.
- **Swan:** body ball; twig at neck root; squeezed beads along twig; pat to merge; shave round; score wing line. Must FAIL without the armature, HOLD with it.
- **Castle rook:** ball stack, pat; vertical shave passes → true cylinder; score brick courses; evenly spaced small bites → crenellations.
- **Mushroom:** bead stem + ball cap, shave cap underside. Fluffy/thin stem → cap thuds off (breakage as soft tutorial). Squeezed stem → stands.

**Definition of done:** the cat is achievable in ~10 focused minutes by someone who knows the tools, and the fluffy-vs-packed shave contrast makes a first-time player say "oh — I need to pack it first" without any text.

## 9. Explicitly NOT building

Symmetry mode, undo, reference overlays, selection/transform tools, free additive brush of any kind. Each is the line where "playing with snow" becomes "using Blender." The wiggle room lives in the four tuning numbers, not in new abstractions.
