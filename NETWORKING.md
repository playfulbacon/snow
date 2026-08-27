# Snowfield multiplayer (P2P, one shared field)

Implemented overnight Aug 27 2026. The game starts single-player; **Shift+N** puts you in the shared field —
quick-join any open public session, or become the host of a new one — and Shift+N again leaves. P2P over
Unity Relay: no dedicated servers, no hosting costs beyond the UGS free tiers. Voice is Vivox positional
audio (open mic, ~32 m falloff, M mutes). Connection state shows on the HUD's third status line.

## Stack

- **Netcode for GameObjects 2.13** — host-client topology; the host is just the first player in.
- **Unity Services Multiplayer 2.3 (Sessions)** — `MatchmakeSessionAsync(QuickJoinOptions{CreateSession=true},
  SessionOptions.WithRelayNetwork())` does lobby discovery + Relay allocation + starts NGO itself.
  Never call `StartHost/StartClient/Shutdown` around a session — the package owns that lifecycle.
- **Vivox 16.11** — one positional channel per session (`snow-<sessionId>`), UGS-JWT tokens via anonymous auth.
- Project is linked to Unity Cloud (org `filecorrupted-games`); Relay/Lobby/Vivox are enabled and verified live.

## Architecture (mirrors CLAUDE.md Phase 4: events, not density)

```
gameplay code ──raises──▶ SculptureNet (static seam, Snowfield.Sculpture)
                              │ committed intents        │ lifecycle (Created/Replaced/Removed)
                              ▼                          ▼
                       SnowWorldSync  ◀──────── SculptureRegistry (ulong ids)
                       encode/apply
                              │ byte[]
                              ▼
                       SnowNetChannel (NetworkBehaviour, host-spawned prefab)
                       client ──SubmitEventRpc──▶ host ──BroadcastEventRpc──▶ everyone
```

- **Identity**: sculptures get logical ids `((clientId+1) << 32) | counter`, minted lock-free by the creating
  peer. The factory's destroy-and-replace ops (Promote/Regrow/Fuse) migrate ids via the `Replaced` hook, so
  events always reference stable ids while GameObjects churn.
- **Local-first feel**: the acting player applies every op instantly, then broadcasts; the host relays in
  arrival order; the origin skips its own echo. Brush ticks carry explicit tick counts (never re-derived
  from dt) and explicit target ids (never re-resolved by physics on the remote).
- **Structural ops** (scoop, fuse/splat, regrow, prop attach/remove, rest) are single events carrying their
  full parameter set, including poses that came from physics. Regrow broadcasts its exact grid size+origin so
  peers regrow byte-identically. A Fuse replays its inner Promote/Regrow on every peer (only the outermost op
  broadcasts — `SculptureNet.StructuralDepth`).
- **Physics flights never replay** (CLAUDE.md rule): a throw event spawns a cosmetic kinematic arc on remotes
  (`RemoteBallDrive`), and the authoritative splat (`Fuse`) or `Rest` event snaps the outcome.
- **Carried/rolled balls** stream pose+radius at 10 Hz unreliable from the carrier; remotes ease toward it,
  grow the ball on radius change, and press roll trenches into their own snow window from the replicated
  motion (no extra traffic — deformation is windowed, decaying, cosmetic, per CLAUDE.md).
- **Ground deformation**: scoop divots replay inside the scoop event; footprints come free — remote avatars
  get `SnowFootprints` at spawn (`SnowNetGlue`) and stamp locally from their animated bones.
- **Late join**: the host's world is THE world. A joining client wipes its local field and receives every
  sculpture as one binary record (pose + grid shape + RLE density blob + props — the save format, id-tagged).
  The host encodes synchronously inside the connect callback and streams records in 32 KB parts (NGO caps a
  single RPC's params at 64 KB; a grown 192³ record is bigger); the client holds all world events until the
  promised snapshot count lands, then drains them in order — so "snapshot + later events" is exactly the
  world, with no join-window races. ~1 MB for a 58-sculpture field.
- **Scoops carry the chunk's density blob** in the event (a 48³ handful is ~1–2 KB RLE), so every peer's
  hand-chunk is byte-identical even when a scoop races a concurrent stroke on the same snow.
- **Disconnects**: the host tracks which ball each client's pose stream drives; if a carrier drops mid-carry,
  the host settles the orphan onto the snow with a broadcast Rest event. On session teardown every peer
  sweeps remaining remote-driven balls back to interactable (`RemoteBallDrive.SettleInPlace`), so a survivor
  who re-hosts never shares a bricked world. Ball radius rides every Rest/Throw/Fuse event and is reconciled
  before anything structural consumes it (the 10 Hz stream alone is lossy).
- **Saves**: hosts keep playing on field 0 (their field is the shared one). Clients switch to field slot 1
  for the session — and deliberately stay there for the rest of the process (NGO despawns the channel during
  app quit and on host loss, while the world in memory is still the shared one; resetting the slot would let
  quit-autosave overwrite the solo field). Balls being carried or in flight are skipped by SaveAll (they
  would reload hovering). F9 (manual load) is blocked while in a session.
- **Avatars**: the scene Player is never a NetworkObject. `NetAvatar.prefab` (PlayerPrefab) mirrors the local
  player's root pose (owner-authoritative NetworkTransform) + four animator floats; the owner hides its own
  avatar. No PlayerController, no colliders on avatars — so SnowDeform's window keeps following the real
  local player and brush raycasts can't hit bodies.
- **Arms**: the stretchy IK is synced as *intent*, not bones. `HandRig.CurrentGoal` exposes the per-frame goal
  the rig was solved against with any `Pulse` already resolved into it, so a pulse (fuse, throw, place) needs
  no message of its own — it is just the same goal reported for as long as it holds. The avatar prefab carries
  its own `HandRig` and replays those goals through `Reach`, letting the rig's spring and weight blending do
  the easing (including easing a hand home when the owner stops asking). Goals travel in **body space**: the
  owner samples goal and body pose in one frame while a remote's body is interpolated, so a world-space goal
  would leave the hand right and the arm mis-stretched against a body that hasn't caught up. One unreliable
  owner-permission RPC at 15 Hz, sent only while a hand is active (plus two release packets, and a 0.5 s
  staleness backstop). NGO proxies a client's `SendTo.NotMe` through the host itself — no manual relay.
- **Host leaves** → session dies (NGO has no host migration); clients keep their last world state, then
  re-quick-join after a few seconds and reconvene under a new host (whose local state seeds the new field).

## Files

- `Assets/Snowfield/Sculpture/SculptureNet.cs` — the seam. Gameplay raises; suppressed during replay.
- `Assets/Snowfield/Net/` — `SculptureRegistry`, `SnowWorldSync` (codec + applier, transport-free),
  `SnowNetChannel` (RPC relay + snapshots + carried stream), `RemoteBallDrive`, `NetAvatar`,
  `NetAvatarHooks` (Assembly-CSharp inversion seam), `NetBootstrap` (UGS session flow), `VoiceChat`.
- `Assets/Net/SnowNetGlue.cs` — SnowDays side: local rig registration, footprints on remote avatars.
- `Assets/Editor/NetSceneSetup.cs` — builds `Assets/Net/NetAvatar.prefab` + `NetChannel.prefab`, wires the
  `Network` root (NetworkManager + UnityTransport + NetBootstrap). Runs inside `MainSceneSetup.Run`.
- Seam raise points were added in: `SculptureFactory` (+ `RegrowExact`), `SculptTool`, `SnowballRoller`,
  `AccessoryPlacer` (+ static `PlaceEntry`), `Snowball` (rest on landing), `SaveLoadManager` (F9 guard).

## Testing

- `Snowfield.Net.Tests` (PlayMode): `WorldSyncTests` run every event kind through the real pipeline —
  local op → encoded event → world wipe → snapshot rebuild → replay — and assert density probes, id
  migration, and grid geometry match. `RelayPatternTests` proves the submit/relay/origin-skip RPC shape over
  an in-process NGO host+client (needs `"testables": ["com.unity.netcode.gameobjects"]`, already in the manifest).
- **SnowOps** (`Assets/Editor/SnowOps.cs`): drive the LIVE editor without batchmode (which can't run while
  the editor holds the project lock). Drop a file into `NetOps/` and read back `<op>.result`:
  `refresh` · `scene_setup` · `tests_editmode` / `tests_playmode` (content = assembly filter) ·
  `play_smoke` (content = seconds) · `build` (content = output path) · `restart` (saves titled dirty scenes,
  relaunches the editor — needed once after adding UTP so Burst compiles its function pointers).
- **Two instances on one machine**: run the editor (or one build) plus `Builds/SnowDev.app` with a different
  auth profile, e.g. `SNOW_PROFILE=p2 ./Builds/SnowDev.app/Contents/MacOS/<binary>` (also `--snow-profile p2`).
  Player log: `~/Library/Logs/DefaultCompany/SnowDays/Player.log`.
  **Multiplayer Play Mode virtual players share one anonymous UGS account** — they have no `SNOW_PROFILE` and
  MPPM 2.x ships no runtime API to tell them apart, so if two virtual players fight over one identity, give
  each its own profile (or test with a standalone build alongside the editor, which is what the sync above
  was verified with).

## Knobs

`NetBootstrap` (Network root in Main): `maxPlayers` (8 — also the Relay allocation size), `sessionName`,
`quickJoinTimeout`, `rejoinDelay`, `autoConnect` (off — MainSceneSetup rewrites it every run, so use
`--snow-autoconnect` / `SNOW_AUTOCONNECT=1` to bring up a test instance that has nobody to press Shift+N).
`VoiceChat`: channel numbers are baked
into the URI — every peer must use identical `Channel3DProperties` (32 m audible, 2 m conversational).

## Known gaps / next

- No drift repair yet: same-platform peers stay in lockstep (byte-quantized ops, explicit tick counts, exact
  regrow geometry, scoop blobs), but the planned chunk-checksum → RLE-blob resync (CLAUDE.md) has a
  ready-made slot: `EncodeSnapshot` is the repair payload, keyed by registry id, and `ApplySnapshot` already
  replaces-by-id. ~20 lines in SnowNetChannel when drift is ever observed.
- Concurrent Smooth strokes on the same voxels apply in different orders origin-vs-others (local-first vs
  host-order) — the one remaining non-commuting case; bounded, cosmetic, and covered by future drift repair.
- Remote flights don't collide with sculptures (pure ballistic + ground-slide until the splat event lands).
- Jump animation isn't replicated (remotes play the fall blend); grounded flag is.
- Prop remove matches by (prefabId, nearest localPos) — two identical props at the same spot could pick the
  wrong twin. Harmless for now.
- After a session ends, the (ex-)client keeps playing the shared world and keeps saving it to slot 1; their
  own field 0 returns on next launch. An explicit "return to my field" (SetField(0)+LoadAll) is a UX decision
  for later.
