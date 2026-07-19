# Light Runners — Reimplementation Specification

A complete, from-scratch specification for rebuilding the game. Distilled from the
existing `fix/nostr-auth` codebase (commit `e5f73c9`, 2026-07-03) and the design
captured in `PLAN.md` / `SETUP.md`. The "Known pitfalls" call out the mistakes the
first implementation made, so a fresh build can avoid them.

**Provenance:** most behavior here is verified against v1 source, and phases 1–4
are now re-implemented in this repo (`Assets/LightRunners/`) — where this document
and that code disagree, this document is the target and the divergence is called
out inline. Two kinds of content are *design, not source-verified*: the friend-match
feature (§8.5, the friend-match parts of §12.5, `PartyLobby`) and everything in
§20–§26 (added 2026-07-04 after a gap review against the phase 1–4 code).

---

## 1. What the game is

**Light Runners** is a real-world, location-based AR multiplayer racing game
inspired by *Tron* light cycles. Players carry a phone outdoors. As they walk
or run, the device's GPS traces a neon "trail" behind them on a map and — in AR
mode — floating in the world around them. The trail is a wall: if a player
crosses their own trail or anyone else's, they crash. A run ends when the
player crashes or voluntarily ends it.

A run is scored on four axes — **distance, speed, beauty** (how curvy/interesting
the path is) **and proximity** to other live runners — and persisted to a
shared backend so leaderboards (global and per-region) exist across sessions.
Players are grouped into rooms by geographic grid cell, so you only ever race
the people physically near you.

### 1.1 Pillars
- **Move your body to play.** Core input is GPS, not a joystick. The phone is a
  window onto a game painted over the real world.
- **Map-first, AR-optional.** The minimap is the primary, always-available view.
  AR is a toggle that layers trails over the live camera feed.
- **Anonymous by default.** One tap to play. No accounts, no keys, no PII. An
  ephemeral identity is created and resumed via a refresh token.
- **Race who's near you, or race your friends.** The default experience drops
  you into the room of whoever is physically nearby (zero-friction anonymous
  play). A **friend-match** flow layered on top lets you create or join a
  private room via a 6-character code — the only explicit grouping primitive.
  No friends lists, no social graph, no invites: codes are shared out-of-band.
- **Cross-platform from day one.** iOS 14+ and Android 8+, IL2CPP/ARM64, with
  platform-specific sensor code isolated behind interfaces.

### 1.2 Target platforms & engine
- **Unity 6000.4.x** (URP). Project created as a 3D (URP) project.
- **iOS 14.0+**, **Android API 26+**, IL2CPP, ARM64.
- Portrait phone orientation; UI scaled to 1080×1920 reference, match-height.
- Required third-party: **Photon Fusion 2** (shared-mode multiplayer),
  **AR Foundation 6** (+ ARKit / ARCore providers), **Supabase** (Postgres +
  PostGIS backend + anonymous auth). Free OSM raster tiles for the map (no
  Mapbox token). Unity Mathematics, Burst, Input System are bundled.

---

## 2. Player experience & game loop

### 2.1 Scenes (two only)
1. **Login** — title, a one-line "tap Play to start" hint, a **Play** button,
   and a status text. Authenticates anonymously, then loads the Game scene.
2. **Game** — owns every runtime system: location, trails, multiplayer, map, AR,
   HUD, run summary, crash sequence. The lobby *is* the Game scene in its
   `Lobby` state (no separate lobby scene).

### 2.2 Core loop
```
                                  ┌──Create/Join Friend Room──┐
                                  ▼                            │
Login ──Play──▶ Lobby ──Start Run──▶ Running ──End Run──▶ Summary ──Continue──▶ Lobby
                  │                 │                                   ▲
                  │                 └──────Crash───────────────────────┘
                  └─(default) auto-zone room from GPS
```
- **Lobby:** map visible, HUD hidden except a centered **Start Run** button.
  Player can cycle beacon form, toggle map/AR, and open the **Friend Match**
  panel (§8.5) to create or join a private room by code. **Start Run** connects
  to the room — the geographic zone room by default, or the code-resolved room
  if a friend match is in progress.
- **Running:** HUD shows speed / altitude / elapsed time / distance / live
  runner count. An **End Run** button is visible. Trail records on every GPS
  update; collision is checked every tick.
- **Crashed:** crash sequence plays (slow-mo + colored screen flash), then the
  **summary panel** appears over the scene.
- **Summary (one panel, two causes):** total score, distance/time/avg-speed,
  four score breakdowns, crash cause text. Buttons: **Run Again** (→ new run)
  and **Continue to Lobby**. The same panel serves voluntary end-of-run.
  Formats and exact button semantics are pinned in §25.

### 2.3 Game states (the `GameState` enum)
`Initializing`, `Login`, `Lobby`, `PartyLobby`, `Starting`, `Running`,
`Crashed`, `Paused`. A `GameManager` singleton owns the current state and fires
`OnStateChanged`. HUD visibility and button enablement derive purely from
state transitions. `PartyLobby` is the pre-run state for a friend match
(§8.5): same map view as `Lobby`, plus a roster of joined players and a
host-only **Start** button. *(`PartyLobby` is a phase-9 addition — the phase-4
code's enum doesn't have it yet; add it when §8.5 lands.)*

Every state has a defined meaning — no dead enum values:

| State | Meaning | Entered from | Exits to |
|---|---|---|---|
| `Initializing` | scene bootstrapping, services registering | app start | `Login` / `Lobby` |
| `Login` | Login scene, waiting for tap-Play + anonymous auth | `Initializing` | `Lobby` |
| `Lobby` | Game scene idle; map visible, Start Run shown | `Login`, `Crashed` (Continue), `Running` (voluntary end) | `PartyLobby`, `Starting` |
| `PartyLobby` | friend-match roster; host sees Start, joiners wait | `Lobby` | `Starting`, `Lobby` (leave/expire) |
| `Starting` | **the async connect window**: Photon `Connect` is in flight. HUD disabled, "Connecting…" hint. On success → `Running`. On failure or after **`connectTimeoutSeconds` (default 8 s)** → `Running` anyway, solo, with the §8.4 fallback detector and an "offline race" HUD badge | `Lobby`, `PartyLobby` | `Running` |
| `Running` | run in progress; trail records, collision checks | `Starting` | `Crashed`, `Lobby` |
| `Crashed` | crash sequence + summary panel showing | `Running` | `Running` (Run Again), `Lobby` (Continue) |
| `Paused` | app backgrounded mid-run (see §20). Recording suspended; run auto-ends after `backgroundGraceSeconds` (default 60 s) | `Running` | `Running` (refocus within grace), `Lobby` (grace expired → voluntary end) |

*(Phase-4 note: the current code sets `Starting` and `Running` synchronously in
one call because there is no async connect yet; the timeout semantics arrive
with phase 8. `Paused` is currently unused — wire it per §20.)*

### 2.4 Beacon forms (player avatar)
Eight selectable avatar shapes, each with a distinct trail color. Three are
unlocked from the start; five unlock by level. Fallback primitive meshes are
generated in code for any missing prefab, so the game runs with zero art.

| Form | Display | Color | Unlocked at level |
|---|---|---|---|
| Hoverboard | Hoverboard | cyan `(0,1,1)` | 0 |
| Sphere | Orb | magenta `(1,.2,1)` | 0 |
| Drone | Drone | green `(.2,1,.4)` | 0 |
| AbstractShape | Prism | orange `(1,.5,0)` | 5 |
| FloatingCube | Cube | yellow `(1,1,.2)` | 10 |
| Motorcycle | Runner | red `(1,.1,.1)` | 15 |
| Phoenix | Phoenix | amber `(1,.6,0)` | 20 |
| Waveform | Waveform | electric-blue `(.4,.6,1)` | 25 |

### 1.5 Active Decisions (2026-07-18) — Lightfield Match Core

The 4-axis score-and-crash-ends-the-run game described in §1.4 / §2 has been
**superseded in flight by a parallel match-core migration** ("the Lightfield
match core"). A 6-track parallel effort (A/B/C/D/E/F/G) replaced the open-ended
run loop with a **timed host-authoritative match** where players collect
**Lumens** by touching gates, crash is no longer terminal (you respawn with a
Lumen penalty), and the highest Lumen count at clock expiry wins. This is the
**authoritative** design; the legacy text below remains for provenance and is
marked "DELETED"/"DEPRECATED" where it conflicts.

22 decisions were made; **19 are implemented** in this milestone, **3 are
deferred to v2** (decision S):

| Decision | Topic | Status |
|---|---|---|
| A | Completed trails persist as neon world art ("Afterglow") | ✅ (overview camera) |
| B | Lumen Gate visual contract (hemisphere + trigger) | ✅ |
| C | Integer Lumen tally is the sole score primitive | ✅ |
| D | Emergence grace: own trail cannot kill for N s after start | ✅ |
| E | RunScorer DELETED — replaced by integer Lumen tally (see §7.4) | ✅ |
| F | Crash is no longer terminal: penalty + respawn; drops a stealable Lumen | ✅ |
| G | Lumen Gates as the collection primitive | ✅ |
| H | AR is the primary view; map is a corner radar | ✅ |
| I | Leader identity always visible (crown + off-screen indicator) | ✅ |
| K | Hemispherical play volume ("Lightfield"); ground disc + ceiling for milestone | ✅ |
| L | Ground-only Gate placement; aerial deferred (decision S) | ✅ |
| M | Gate density formula: `max(1, ceil(players × gatesPerPlayer))` | ✅ |
| N | Sweep-subdivide long teleports for collision (vehicle / GPS jump) | ✅ |
| O | Timed match; most Lumens wins on clock expiry (default 6 min) | ✅ |
| P | Match is a strict sub-FSM on top of `GameState` (decision-P architecture) | ✅ |
| Q | **Shared Mode → Host Mode** (authoritative host); see §8.1 | ✅ |
| R | Referee as a validated command client (NOT state authority); v2 UI deferred | ✅ token+PlaceBonusGate |
| S | Aerial flight + full Gate-Director UI + Afterglow Walk-Inside | ⏸ deferred v2 |
| T | Tail radius frozen at countdown, preserved in Afterglow | ✅ |
| U | Afterglow is ONE package, two views (Overview/Walk-Inside) | ✅ (Overview) |
| V | (reserved) | — |
| — | Backend match schema: `matches` / `match_players` tables + RPCs | ✅ (Track E) |

**Deferred (decision S):** aerial flight (full orb gates at altitude bands),
the full referee Gate-Director UI (decision R v2), and the Afterglow
Walk-Inside view (immersive replay camera). All three are stubbed in the
milestone — the contracts are stable so v2 is additive.

A new "Lightfield Architecture" subsection (see end of §3) lists the 7 Core
match contracts and which assembly implements each.

---

## 3. Architecture

### 3.1 Service locator
A static `ServiceLocator` (type-keyed dictionary) is the single seam through
which cross-system dependencies resolve. Registrations happen in `Awake`/`Start`
of the owning objects; resolution happens later (in `Start` or on demand) so
initialization order doesn't have to be perfect. Interfaces registered:

| Interface | Registered by | Implementation |
|---|---|---|
| `IAuthService` | `PlatformServiceRegistry` (Login scene, `DontDestroyOnLoad`) | `SupabaseAuthService` |
| `IAltitudeService` | `PlatformServiceRegistry` | platform-specific (see §6) |
| `ILobbyService` | `SupabaseManager.Awake` (Backend assembly; null-op stub when Supabase URL is blank) | `SupabaseLobbyService` (§8.5) |
| `IMapProvider` | `OSMMinimapView.Awake` | `OSMMinimapView` (self) |
| `IARViewController` | `ARViewManager.Start` | `ARViewManager` (self) |
| `TrailRepository` | `GameManager.Awake` | a `MonoBehaviour` added to the GameManager GO |

> **Pattern:** singletons that "peek" (`Instance` returns the live instance or
> null, never creates) plus a service locator for the cross-cutting interfaces.
> `PlatformServiceRegistry` is idempotent — if services are already registered
> (e.g., carried over from Login via `DontDestroyOnLoad`), it skips.

### 3.2 Event bus
`GameEvents` is a static `event` bus in Core. Events:
`PlayerCrashed(string causedByPlayerId)`,
`GameStateChanged(GameState prev, GameState next)`,
`ViewModeChanged(ViewMode mode)`. `PlayerCrashed` exists specifically to let the
**Multiplayer** assembly raise a crash without referencing the **Gameplay**
assembly (Gameplay already references Multiplayer, so a direct call would be
circular). Use this bus for any cross-assembly notification that would
otherwise create a dependency cycle.

### 3.3 Assembly definitions (compile-time layering)
Recreate these exact asmdefs to keep the dependency graph acyclic and to make
platform code compiler-gated:

```
LightRunners.Core          refs: Unity.Mathematics
                           DataTypes, GameConfig, Singleton, ServiceLocator,
                           GameEvents, PerformanceMonitor, StringUtils
LightRunners.Location      refs: Core      + versionDefine UNITY_XR_ARFOUNDATION
LightRunners.Trail         refs: Unity.Mathematics, Core, Location
LightRunners.Beacon        refs: Unity.Mathematics, Core, Location
LightRunners.Map           refs: Core, Location, Trail, Beacon
LightRunners.Backend       refs: Core, Location, Trail
LightRunners.Identity      refs: Core, Backend
LightRunners.Gameplay      refs: Core, Location, Trail, Beacon, Map, Backend, Identity
LightRunners.Multiplayer   refs: Core, Location, Trail, Beacon  + FUSION_WEAVER gate
LightRunners.AR            refs: Core, Location, Trail, Beacon, Backend + UNITY_XR_ARFOUNDATION
LightRunners.UI            refs: Core, Beacon
LightRunners.Editor        refs: all of the above (editor only)
```

**Key invariant:** Gameplay references Multiplayer, not the reverse. Multiplayer
talks back to Gameplay only through the `GameEvents` bus. Game logic (Core/Trail)
never references platform-specific code — enforced by the compiler.

### 3.4 Platform / package gating via scripting defines
- `FUSION_WEAVER` / `FUSION2` — set by Photon's weaver for each build target
  (Android, iOS, Standalone). All multiplayer source is wrapped in
  `#if FUSION_WEAVER` so the project compiles even before Fusion is imported.
- `UNITY_XR_ARFOUNDATION` — **not** auto-set by Unity. Add it as a **version
  define** on the `LightRunners.AR` and `LightRunners.Location` asmdefs:
  `expression ""` on package `com.unity.xr.arfoundation`. This auto-tracks the
  package's presence, so AR source compiles in/out without manual defines.

### 3.5 Where `#if` appears
- `UNITY_EDITOR` — editor-only simulated-walk GPS mode in `LocationProvider`.
- `UNITY_ANDROID` — barometer altitude service (JNI).
- `UNITY_IOS` — ARKit altitude service.
- `UNITY_XR_ARFOUNDATION` — `ARViewManager`, `IosARKitAltitudeService`,
  `AltitudeServiceFactory` branches, scene-generator AR object creation.
- `FUSION_WEAVER` — `FusionLauncher`, `NetworkPlayer`, `NetworkTrailSync`, the
  prefab generator, and the `Connect`/`Disconnect` calls in `GameManager`.

### 3.6 Lightfield Architecture (active decisions 2026-07-18)

The match core is built on **7 Core interfaces**, each owned by exactly one
implementing assembly. Implementations register on the `ServiceLocator`
(§3.1) at match start; `PlatformServiceRegistry` installs `Null*` stubs first
so the editor runs before any track is merged.

| Interface | Implementing assembly | Constructing class |
|---|---|---|
| `IMatchSession` | `LightRunners.Gameplay` (Track D) | `MatchManager` (singleton MonoBehaviour; sub-FSM `Idle→Warmup→Countdown→Live→Scoring→Expired`) |
| `ILumenScoreboard` | `LightRunners.Trail` (Track A) | `LumenScoreboard` (pure C#; integer tally; leader + dropped-Lumen queue) |
| `IGateDirector` | `LightRunners.Lightfield` (Track B) | `GateSpawner` (pure C#; density pool + bonus gates) |
| `ILightfieldVolume` | `LightRunners.Lightfield` (Track B) | `LightfieldVolume` (pure C#; disc + ceiling; boundary-violation events) |
| `IMatchTransport` | `LightRunners.Multiplayer` (Track C) | `FusionLauncher` (host mode; `NullMatchTransport` offline fallback) |
| `IMatchReplaySink` | `LightRunners.Afterglow` (Track F) | `ReplayPackageSink` (pure C#; accumulates `ReplayPackage`, finalizes on MatchExpired) |
| `ITailAuthority` | `LightRunners.Trail` (Track A) | `TailAuthority` (pure C#; tail radius frozen at countdown) |

**Why pure-C# hosts over MonoBehaviours:** the host authorities (scoreboard,
gate director, lightfield volume, replay sink, tail authority) are pure logic
with no Unity lifecycle needs, so they're plain C# classes registered on the
locator. `MatchManager` constructs and registers them in `Awake`, overwriting
the `Null*` stubs. The scene generator (`SceneSetup`) therefore does NOT place
GameObject stand-ins for these — `TryAddType` is a graceful no-op for non-
MonoBehaviour types, and the calls stay in place so a future conversion wires
automatically.

**Cross-track event flow:**

```
GameEvents (static bus, Core)
  ├─ GateCollected(gateId, playerId)   ← LumenGate / StolenLumenPickup (Track B)
  │                                     → LumenScoreboard.Award (Track A)
  │                                     → ReplayPackageSink.RecordLumen (Track F)
  ├─ LumensChanged(playerId, newTotal) ← LumenScoreboard (Track A)
  │                                     → StolenLumenPickupSpawner (Track B, heuristic)
  │                                     → TacticalRadar / LeaderCrown (Track D)
  │                                     → ReplayPackageSink (Track F)
  ├─ LeaderChanged(newLeaderId)        ← LumenScoreboard (Track A)
  │                                     → LeaderCrown / OffScreenIndicator (Track D)
  ├─ GateSpawned / GateDespawned       ← GateSpawner (Track B)
  │                                     → TacticalRadar (Track D)
  ├─ BoundaryViolated(playerId)        ← LightfieldVolume (Track B)
  ├─ PlayerCrashed(playerId)           ← GameManager (single listener; delegates to MatchManager)
  ├─ MatchExpired                      ← MatchManager (Track D)
  │                                     → ReplayPackageSink.Finalize (Track F)
  │                                     → AfterglowViewController.Show (Track F)
  └─ ConnectionStateChanged            ← FusionLauncher (Track C)
                                        → MatchManager (online/offline host detection)
```

**Scene wiring (Track G):** the Game scene gets five new GameObjects/stacks —
`MatchManager`, `ViewModeBootstrap`, `Afterglow` (with `OverviewCamera` child),
`MatchHUD` canvas (carrying `TacticalRadar`/`OffScreenIndicator`/`LeaderCrown`)
— plus `NetworkMatchState` is spawned by the host from a Resources prefab at
match start. `LightfieldVolume` and `GateSpawner` are NOT in the scene — they
are pure-C# hosts the MatchManager constructs. See §14.2 for the full scene
manifest and §14.3 for the new prefab menu.

---

## 4. Core data model (`LightRunners.Core`)

### 4.1 `GeoPoint`
```csharp
struct GeoPoint { double latitude, longitude, altitude; }
```
- `HorizontalDistanceTo` — **Haversine** (R = 6,371,000 m), ignores altitude.
- `VerticalDistanceTo`, `Distance3DTo` — for scoring/collision height checks.

### 4.2 `TrailPoint`
```csharp
struct TrailPoint { GeoPoint position; double timestamp; int ownerSequenceIndex; }
```
`ownerSequenceIndex` is the point's **run-scoped sequence number** (0, 1, 2, …
monotonic for the owner's whole run) — the key used to merge remote batches in
arrival order (see §8.2). It is **not** an index into the live `_points` list:
once pruning (§7.1) has dropped old points, sequence numbers exceed list
indices. Never use one where the other is meant (pitfall #19).

**Clock semantics (pin these):** `timestamp` is `Time.timeAsDouble` — seconds
since local app start, monotonic, **not** wall-clock and **not** comparable
across devices. On the wire (§8.2) each batch carries `time_offset` values
relative to the batch's first point, so a decoded remote point's timestamp is
only meaningful *within its own trail* (inter-point deltas for rendering/speed).
Anything persisted for humans (DB rows, `run_history`) uses server-side
`now()` / `TIMESTAMPTZ`, never these values.

### 4.3 Enums & meta
- `BeaconFormType` — 8 values (§2.4).
- `BeaconFormData` — `formType, displayName, prefabName, trailColor, unlocked,
  requiredLevel`; ships a `Defaults` array with the table in §2.4.
- `GameState` — §2.3.
- `ViewMode` — `Map` / `AR`.
- `PlayerIdentity` — `{ string userId (auth.uid UUID); string displayName }`.

### 4.4 `GameConfig` (ScriptableObject at `Resources/GameConfig.asset`)
One asset drives every tunable. Group it exactly like the original so designers
can find knobs:

| Group | Fields | Defaults |
|---|---|---|
| Location | `trailPointMinDistance`, `gpsSampleInterval`, `gpsAccuracyThreshold`, `barometerWeight` | 1.0 m, 0.5 s, 20 m, 0.7 |
| Trail | `maxTrailPoints`, `trailWidth`, `trailGracePeriod`, `trailGroundOffset` | 5000, 0.5, 2 s, 0.3 m |
| Collision | `collisionCheckRadius`, `collisionThreshold`, `selfCollisionSkipPoints` | 5 m, 1.5 m, 10 |
| Networking | `fusionAppVersion`, `maxPlayersPerRoom`, `networkTickRate`, `trailSyncBatchSize`, `connectTimeoutSeconds`, `roomJoinRetryLimit` | "1.0", 20, 30, 10, 8 s, 3 |
| Friend Match *(phase 9 — not in the phase-4 `GameConfig` yet)* | `lobbyCodeLength`, `lobbyMaxMembers`, `lobbyCodeAlphabet`, `lobbyIdleTimeoutSeconds`, `lobbyRegionCell`, `lobbyPollIntervalSeconds` | 6, 8, "ABCDEFGHJKLMNPQRSTUVWXYZ23456789", 1800, 0.1, 5 s |
| Backend | `supabaseUrl`, `supabaseAnonKey`, `trailSaveInterval` | "", "", 5 s |
| Map/OSM | `osmTileUserAgent`, `osmMinimapSize`, `osmDefaultZoom`, `osmMaxConcurrentRequests`, `osmTileRequestInterval`, `osmTileCacheSize`, `defaultLatitude`, `defaultLongitude` | "LightRunners/1.0", 200, 16, 2, 1.0 s, 64, 37.7749, -122.4194 |
| AR | `arTrailRenderDistance`, `arMaxNearbyTrails` | 50 m, 50 |
| Beacon | `beaconBaseScale`, `beaconBobAmplitude`, `beaconBobFrequency`, `beaconGlowIntensity`, `beaconRotationSpeed` | 1, 0.1, 2, 2, 45 |
| Scoring *(DEPRECATED — decision E)* | `proximitySampleInterval`, `proximityRadius` | 10 s, 100 m |
| Lifecycle | `backgroundGraceSeconds` | 60 s |
| **Lightfield Match** *(active decisions 2026-07-18 — see §1.5, §3.6)* | `gatesPerPlayer`, `gateCollectionRadius`, `tailRadius`, `matchDurationSeconds`, `matchCountdownSeconds`, `crashLumenLossNonLeader`, `crashLumenLossLeader`, `stolenLumenPickupSeconds`, `emergenceGraceSeconds`, `lightfieldBaseRadiusMeters`, `lightfieldDomeCeilingMeters`, `sweepSubdivideMaxStepMeters` | 0.5, 2.0 m, 0.5 m, 360 s, 3 s, 1, 2, 8 s, 2 s, 50 m, 6 m, 2 m |

> **Lightfield fields:** `gatesPerPlayer` is the density ratio
> (decision M: `activeGateCount = max(1, ceil(players × gatesPerPlayer))`);
> `gateCollectionRadius` is the Lumen Gate trigger (decision G);
> `tailRadius` is the authoritative frozen tail (decision T);
> `matchDurationSeconds` is the host-tunable match clock (decision O);
> `crashLumenLossLeader` / `NonLeader` are the tier-scaled crash penalties
> (decision F); `stolenLumenPickupSeconds` is the stealable-Lumen lifetime
> (decision F); `emergenceGraceSeconds` extends `trailGracePeriod` so a freshly
> spawned runner can't re-crash into their own tail (decision D);
> `lightfieldBaseRadiusMeters` / `lightfieldDomeCeilingMeters` define the
> ground disc + altitude ceiling stub (decision K; full dome deferred per
> decision S); `sweepSubdivideMaxStepMeters` sub-sweeps long teleports so a
> vehicle move / GPS jump can't tunnel through a wall (decision N).

**Two grace mechanisms, two jobs — don't conflate them:**
- `selfCollisionSkipPoints` (count) — the *self-collision* grace: the newest N
  segments of your own trail are excluded from collision (§7.2, pitfall #1).
- `trailGracePeriod` (seconds) — the *run-start* grace: for this many seconds
  after entering `Running`, **no collision fires at all** (any owner). GPS
  needs a beat to settle after Start Run; without this, a noisy first fix can
  land you on top of a remote trail and kill the run at t=0.

> **Secrets are per-environment**, not in source. `supabaseUrl` /
> `supabaseAnonKey` are blank in the committed asset and filled per install.
> Fusion's AppId lives in `Assets/Photon/Fusion/Resources/PhotonAppSettings.asset`.

---

## 5. Coordinate systems

Two coordinate spaces coexist; convert between them with a single utility.

### 5.1 `CoordinateConverter` (static, stateful)
- **Geo** = WGS84 lat/lon/alt.
- **World** = local Unity meters: **X = east, Y = up (altitude), Z = north**,
  relative to a lazily-set reference point (the first GPS fix of a run).
- Earth radius `6,378,137 m`. Conversion uses the equirectangular approximation
  scaled by `cos(refLat)` for longitude.
- `SetReference(lat, lon)` / `EnsureReference(point)` — call once at the first
  trail point. The reference **must** be set before any geo→world conversion;
  `EnsureReference` makes the first call self-initializing.
- **Reference lifetime = one run.** `GameManager.StartRun` calls `SetReference`
  with the current fix, so every run (including Run Again) re-origins world
  space. Anything caching world-space positions across the reset (AR objects,
  line renderers) must be rebuilt from geo on run start — within a run the
  reference never moves.
- `GeoToWorld`, `WorldToGeo` (inverse), `Bearing(from, to)` (degrees, 0=N, CW).

### 5.2 Map pixel space (separate)
`OSMTileProvider.LatLonToTile / TileToLatLon` use the standard slippy-map
mercator math (independent of `CoordinateConverter`). `MapTileRenderer` maps
geo → composite-texture pixels using the center tile as origin.

> **Known pitfall:** these two systems are independent. Don't try to unify them;
  keep AR/3D in World space and the minimap in pixel space.

---

## 6. Location subsystem (`LightRunners.Location`)

### 6.1 `LocationProvider` (singleton)
Owns the device GPS. Emits `OnPositionUpdated(GeoPoint)` — the single source of
truth for "where the player is." Trail recording and the map both subscribe.

- **`Initialize()`** — on device: checks `Input.location.isEnabledByUser`,
  requests Android fine-location permission if needed, then
  `Input.location.Start(1m accuracy, 0m updateDistance)`, enables the compass,
  initializes the altitude service. **In editor:** there is no GPS hardware, so
  it starts a **simulated-walk mode** instead (WASD/arrows move, Q/E turn, Shift
  sprints) that drives `OnPositionUpdated` exactly like real GPS. *This simulator
  is mandatory for testing the whole loop without a device.*
- **`Update()`** — throttles sampling by `GPSPowerManager.CurrentSampleInterval`,
  then reads `Input.location.lastData`. Rejects samples whose timestamp didn't
  advance and whose `horizontalAccuracy > gpsAccuracyThreshold`. Feeds GPS
  altitude to the altitude service, reads back the fused altitude, and only
  emits a new point if it moved ≥ `trailPointMinDistance` from the last.
- `GPSActive`, `IsInitialized`, `CurrentPosition`, `AltitudeService` exposed.

### 6.2 Altitude (`IAltitudeService`, `IDisposable`)
GPS altitude is noisy (~5 m RMS). Three implementations, chosen by
`AltitudeServiceFactory.Create()` — **the only place game code checks
`Application.platform`**:

| Platform | Service | How |
|---|---|---|
| Android | `AndroidBarometerAltitudeService` (`UNITY_ANDROID`) | pressure sensor via JNI (`SensorManager`, `TYPE_PRESSURE=6`); barometric formula `h=44330·(1−(p/1013.25)^0.1903)`; calibrated against the first GPS altitude; fused by `barometerWeight`. Falls back to GPS if no sensor. |
| iOS | `IosARKitAltitudeService` (`UNITY_IOS`) | EMA-smoothed GPS altitude, plus ARKit relative-height corrections applied when an AR session is active. |
| Editor/Standalone | `FallbackGPSAltitudeService` | 1-D Kalman filter on raw GPS altitude. |

Interface: `Initialize`, `OnGPSUpdate(gpsAlt)`, `GetAltitude(gpsAlt)`,
`IsAvailable`, `Calibrated`.

### 6.3 `GPSPowerManager`
Battery-aware sampling. Triples `gpsSampleInterval` below 15% battery
(non-charging); doubles it while backgrounded; restores on recovery. Exposes
`CurrentSampleInterval`, consumed by `LocationProvider`.

---

## 7. Trail subsystem (`LightRunners.Trail`)

### 7.1 `TrailData` (plain class, one per active trail)
- `List<TrailPoint> _points` + owner id, beacon form, trail color.
- `AddPoint`, `AddPoints`, `Clear`, `PruneTo(max)` (drops oldest).
- `TotalLength` — **maintained as a running accumulator**: each `AddPoint` adds
  the haversine distance from the previous point; `PruneTo` must NOT reduce it.
  *Do not recompute it by summing the live list* — after pruning, a recompute
  undercounts exactly the runs long enough to hit the 40-point distance cap
  (5000 points × 1 m min spacing ≈ the 5 km scoring ceiling; pitfall #18). The
  phase-4 code recomputes from the list — fix it to the accumulator form.
- **Pruning applies to remote trails too** (same `maxTrailPoints` cap), or a
  long remote run grows without bound. This requires the §8.2 merge to key on a
  sequence cursor, not on `PointCount` — see below.
- `TakeSnapshot(fromIndex)` → `TrailSnapshot`: serializes points to a packed
  `float[]` of `[lat, lon, alt, time_offset]` quadruples, with `startIndex` and
  `ownerId`. `Decode()` reverses it. This is the wire format for both network
  sync and (legacy) backend batch saves.

### 7.2 `TrailManager` (singleton)
Holds `Dictionary<playerId, TrailData>`, plus the special `_localTrail`.

- **`StartRun(playerId, form, color)`** — creates a new local `TrailData`, resets
  the run clock and point counter. **Must be idempotent for the same run**
  (same owner+form+color → no-op, keep existing points). *Two callers hit this —
  `GameManager.StartRun` and `NetworkPlayer.Spawned` (local authority) — and the
  later call must not wipe points already recorded.*
- **`OnLocationUpdate(geo)`** — appends a point to the local trail, prunes at
  `maxTrailPoints`, fires `OnLocalPointAdded`.
- **`UpdateRemoteTrail(playerId, snapshot)`** — append-only merge by
  `ownerSequenceIndex`: each trail tracks a **`HighestAppliedSequence` cursor**,
  and only points with `ownerSequenceIndex > HighestAppliedSequence` are
  appended (advancing the cursor). This makes overlapping/out-of-order batches
  idempotent **and survives pruning**. *The phase-4 code compares against
  `PointCount` instead — correct only while nothing is ever pruned; migrate to
  the cursor before remote pruning lands (pitfall #19).*
- **`GetTrailSegmentsNear(center, radius, excludePlayerId, skipRecent, results)`**
  — returns all segments (start/end geo pairs) within `radius` of `center`,
  across every trail. For the *local* player's trail, it skips the newest
  `skipRecent` segments (grace period: the segments just laid share endpoints
  with the current movement segment and would cause instant crash-on-turn),
  **but still checks older self-segments** so looping over your own trail
  crashes you. *This self-collision skip direction is the single most-tested
  invariant in the game — get it right.*
- **`EndRun(crashed)`** — fires `OnTrailCrashed` if crashed, clears local trail.
- `RemoveRemoteTrail(playerId)` — fires `OnRemoteTrailRemoved`.
- Events: `OnLocalPointAdded`, `OnRemoteTrailUpdated`, `OnRemoteTrailRemoved`,
  `OnTrailCrashed`. Map and AR subscribe to these.

### 7.3 `TrailCollisionDetector` (MonoBehaviour, attachable)
- `CheckCollision(playerPos, prevPos, localPlayerId)` — queries
  `GetTrailSegmentsNear` with `collisionCheckRadius`, converts the player's last
  movement and each candidate segment to world space, and tests **2D segment
  intersection on the XZ plane** (`SegmentsIntersect2D`). On intersection,
  confirms via `PointToSegmentDistance < collisionThreshold` **and** height
  difference `< collisionThreshold·2`. Fires `OnCollisionDetected(ownerId)`.
- **Reentrancy guard** (`_isChecking`) — a single check can't overlap itself.

### 7.4 `RunScorer` (static) — DEPRECATED

> **DELETED (decision E, 2026-07-18).** Track E removed `RunScorer`, the
> 4-axis `RunScore` struct, the `record_run` RPC's score columns, and the
> corresponding 16 EditMode tests (one per axis table + guards + proximity
> sampler). The authoritative score primitive is now the **integer Lumen
> tally** on `ILumenScoreboard` (Track A's `LumenScoreboard`), one Lumen per
> Gate touch (decision C). The 4-axis formula below is **historical**,
> preserved for provenance; new code MUST NOT call `RunScorer.Calculate`.
>
> See §1.5 for the active decisions map and §3.6 for the new match-contract
> architecture. The `RunSummaryUI` was refactored to read the Lumen tally;
> `record_match_player` (Track E's schema, §12.5) persists `lumen_count` not
> the 4 axis scores.

`Calculate(trail, duration, otherPlayersNearby)` → `RunScore` *(historical)*:

| Component | Max | Rule |
|---|---|---|
| Distance | 40 | `clamp01(distance / 5000) · 40` — distance is the §7.1 accumulator, not a list recompute |
| Speed | 20 | avg = distance/duration; 0 below 0.5 m/s, ramps 0.5→2, flat (max) 2→5, decays 5→15, 0 above 15 |
| Beauty | 30 | `0.7·curve + 0.3·alt`; curve = avg abs heading change per segment, normalized `clamp01(deg/30)`, scaled down linearly above 60° (0 again at 120°); alt = `clamp01(altitudeRange_m / 50)` where altitudeRange = max−min altitude over the run |
| Proximity | 10 | `min(peakNearby, 5) · 2` |

`totalScore` was the rounded sum. Persisted via the (now-removed) `record_run`
RPC. The 16 deleted EditMode tests covered the axis tables + null-trail /
<2-point / zero-duration guards + the proximity sampler fix (pitfall #17).

**Proximity input — sample during the run, not at the end.** *(Historical
context — no longer wired.)* While `Running`, `GameManager` sampled
`TrailManager.CountPlayersNear(currentPos, proximityRadius, localId)` every
`proximitySampleInterval` (defaults: 100 m / 10 s) and kept the **maximum**
observed (`peakNearby`). That peak was what `Calculate` received. *The phase-4
code instead sampled once at end-of-run within `collisionCheckRadius` (5 m) —
which was ~always 0 and made the axis dead (pitfall #17). `CountPlayersNear`
measures against each remote trail's newest point, i.e. that runner's live
position.*

### 7.5 Rendering & LOD
- `NeonTrailRenderer` (`[RequireComponent(LineRenderer)]`) — builds a
  `LightRunners/NeonTrailEnhanced` material tinted with the trail color and an
  emissive boost; world-space line, ground-offset on Y. Incrementally appends
  only new points since `_lastRenderedIndex`.
- `TrailLODManager` — three distance bands (20 m full-res / 50 m medium / 100 m
  low), culls beyond the farthest band. Updates periodically, not per-frame.

> **Shader note:** the spec references `LightRunners/NeonTrailEnhanced`,
  `LightRunners/BeaconGlow`, `LightRunners/ScreenCrashFlash`. Reimplement these
  as URP-compatible shaders (emissive neon trail, additive glow, fullscreen
  flash with chromatic aberration + vignette). Their exact uniforms are pinned
  in §13.

### 7.6 `CrashSequence` (MonoBehaviour)
On crash: sets `Time.timeScale` to `slowMotionScale` (0.2), drives a
`flashMaterial` over `flashDuration` (0.3 s) using `_FlashIntensity`,
`_Distortion`, `_VignetteIntensity`, `_FlashColor = trailColor`, holds slow-mo
to `slowMotionDuration` (0.8 s), then restores `time.timeScale = 1`. Always
reset `timeScale` in `OnDestroy`.

---

## 8. Multiplayer (`LightRunners.Multiplayer`, gated `FUSION_WEAVER`)

### 8.1 `FusionLauncher` (singleton, `INetworkRunnerCallbacks`)

> **DIVERGENCE (decision Q, 2026-07-18): the migration from Shared Mode to
> Host Mode is authoritative.** Track C rewrote `FusionLauncher` as an
> `IMatchTransport` host-authoritative singleton: the host owns the
> authoritative match state (`NetworkMatchState`), the frozen tail radius
> (decision T), and is the sole validator of referee commands (decision R).
> `MatchManager.IsHostAuthority` resolves `IMatchTransport.IsHost`
> reflectively; in offline/editor mode `NullMatchTransport` returns `true` so
> the FSM drives correctly without a network. The **Shared-Mode text below is
> historical** (preserved verbatim for provenance); new code MUST assume Host
> Mode. The friend-match code-name scheme (§8.5), the room-name floor-rounding
> invariant, and the offline-badge fallback all carry over unchanged.

#### Authoritative behavior (Host Mode, decision Q)
- The first client to enter an empty room becomes the host (State Authority
  on the match-scoped `NetworkObject`s).
- The host spawns **`NetworkMatchState`** (`Resources/Player/NetworkMatchState.prefab`,
  generated by the editor — see §14.3) at match start; it carries the
  `[Networked] FrozenTailRadius` (decision T). Clients receive the host's
  value via Fusion's `OnChanged` callback and propagate it to their local
  `ITailAuthority`.
- The host is the sole constructor of the pure-C# authorities
  (`LumenScoreboard`, `TailAuthority`, `GateSpawner`, `LightfieldVolume`) —
  clients read state through the networked props + replicated trail sync (§8.2).
- **Referee (decision R):** a separate `RefereeClient` `NetworkBehaviour`
  presents a host-issued token; the host validates it via
  `RefereeTokenValidator.Validate` before forwarding any Gate-Director
  command. The referee has NO State Authority. v2 will add the full
  Gate-Director UI (decision S deferral).

#### Historical behavior (Shared Mode, superseded)
- **Shared mode** (`GameMode.Shared`) — every client had authority over its own
  avatar; no dedicated host.
- `Connect(roomName, playerId)` — creates a `NetworkRunner`, calls
  `StartGame`, on success spawns the local avatar and stamps its `PlayerId`.
  AppId/app-version live in `PhotonAppSettings.asset` (not in `StartGameArgs`).
  **The room name is the single matchmaking primitive** — both flows below are
  just producers of a room-name string.
- **Default (anonymous) room-name scheme:**
  `zone_{floor(lat·10)/10}_{floor(lon·10)/10}`, so only players within the same
  ~0.1° cell share a room. Computed from the player's current GPS fix in
  `GameManager.StartRun`. Note `floor`, not truncation: at (37.7749, −122.4194)
  the room is `zone_37.7_-122.5` (floor(−1224.194)/10 = −122.5). Truncating
  toward zero would double the cell width at the equator/prime meridian and,
  worse, silently split clients that disagree on the rounding.
- **Friend-match room-name scheme (§8.5):** `party_{uppercaseCode}`, where the
  code is minted by the `create_lobby` RPC. Same `Connect` call, different
  string — no separate code path in `FusionLauncher`.
- Spawns the avatar from `Resources/Player/NetworkPlayer.prefab`.
- `Disconnect` shuts the runner down. Tracked callbacks: `OnPlayerJoined`
  (spawn local avatar only for `runner.LocalPlayer == player`), `OnPlayerLeft`,
  `OnShutdown`.

**Connection edge cases (all must be handled — a run must never silently lack
crash detection or hang):**
- **Connect timeout / failure:** `Connect` races a `connectTimeoutSeconds`
  (default 8 s) timer during `GameState.Starting`. On timeout or `StartGame`
  failure, the run proceeds **solo**: fallback detector (§8.4) active, HUD
  shows an "offline race" badge, live-runner count reads 1. Do not block Start
  Run on the network.
- **Room full:** with `maxPlayersPerRoom` set via `SessionProperties`/player
  cap, a 21st join fails. Retry with a numeric suffix — `zone_37.7_-122.4_2`,
  `_3`, … up to `roomJoinRetryLimit` (3) — then fall back to solo. Players in
  overflow rooms don't see the main room's runners; acceptable at v1 scale.
- **Mid-run disconnect** (`OnShutdown` while `Running`): the run **continues
  solo** — do not crash or end it. Re-attach the fallback detector, keep
  already-received remote trails as static walls (they stop growing), show the
  offline badge. No automatic reconnect mid-run (a reconnect would need trail
  re-sync from index 0; defer that complexity).
- **Photon region:** pin a fixed region per deployment (`FixedRegion` in
  `PhotonAppSettings`). Shared-mode matchmaking is **per Photon region** — with
  auto-region, two phones in the same GPS zone can land on different Photon
  regions and get *different rooms with the same name*, silently never meeting
  (pitfall #20). Same-zone players are geographically colocated, so a fixed
  region costs little latency.

### 8.5 Friend match (private rooms via codes)
Layered on top of §8.1. Lets a host create a private room and friends join via
a 6-character code shared out-of-band. **No friends list, no social graph, no
in-app invite** — the code is the entire grouping primitive, by design (§1.1).

**Architecture:**
```
┌─────────────┐  create_lobby(host)   ┌────────────────────┐
│ Host Lobby  │ ────────────────────▶ │ Supabase           │
│ UI (§2.2)   │ ◀── code + room_name │  lobby_rooms table  │
└─────────────┘                       │  + 2 RPCs (§12.5)   │
      │ share code out-of-band        └────────────────────┘
      ▼                                        ▲
┌─────────────┐  join_lobby(code,uid)         │
│ Friend Lobby│ ────────────────────────────▶ │
│ UI          │ ◀── room_name or "full"/"expired"
└─────────────┘
      │ both: FusionLauncher.Connect(room_name, playerId)
      ▼
  Game scene (Running) — identical to the anonymous flow from here on
```

**Components:**
- **`LobbyService`** (Backend assembly, registered on `ServiceLocator` so
  Gameplay doesn't depend on Backend directly — mirrors the `IAuthService`
  pattern in §12.1). Methods: `CreateLobby(hostId) → LobbyInfo`,
  `JoinLobby(code, userId) → LobbyInfo`, `LeaveLobby(userId)`,
  `GetLobbyMembers(code) → PlayerIdentity[]`. All async over `UnityWebRequest`.
- **`LobbyInfo`** (Core): `{ string code; string roomName; string hostId;
  PlayerIdentity[] members; DateTime expiresAt; }`.
- **`LobbyUIController`** (Gameplay): the Lobby-scene panel. Two entry buttons:
  **Create Room** (mints a code, displays it large + a copy button, moves to
  `GameState.PartyLobby`, polls every `lobbyPollIntervalSeconds`), and **Join
  Room** (text field + Join button, uppercases/trims input, calls `JoinLobby`,
  on success moves to `PartyLobby`; on `lobby_full`/`lobby_expired`/
  `lobby_closed`/not-found shows the specific error inline and stays put).
  `PartyLobby` shows the roster (display names, host marked), the code, and a
  **Leave** button (calls `leave_lobby`, back to `Lobby`). The host may start
  with any member count ≥ 1.
- **Start signaling — how joiners know the race began.** Joiners are *not* in
  the Photon room while waiting (they connect only when the race starts), so
  the start signal must travel through Supabase, not Fusion:
  1. Host taps **Start Race** → client calls the `start_lobby_race()` RPC,
     which sets `lobby_rooms.status='racing'`, `started_at=now()` — then the
     host runs `GameManager.StartRun`, which reads
     `LobbyService.ActiveRoomName` instead of computing the zone name.
  2. Every client in `PartyLobby` polls `get_lobby(code)` every
     `lobbyPollIntervalSeconds` (5 s). When `status='racing'`, joiners
     auto-run `GameManager.StartRun` against the same room name (worst-case
     start skew ≈ one poll interval; acceptable — trails sync on join).
  3. If a poll returns `status='closed'` or the row is gone (expired/swept),
     show "lobby closed" and return to `Lobby`.
- **`LobbyRoomsTable`** in `Supabase/schema.sql` (§12.5) — see schema section.

**Lifecycle & cleanup:**
- A lobby row is created with `expires_at = now() + lobbyIdleTimeoutSeconds`
  (default 30 min). A cron / pg_cron task (or a lazy sweep in `create_lobby`)
  deletes expired rows. Members leaving decrement the count; the host leaving
  reassigns host to the next member or marks the lobby closed.
- `Disconnect` on the host does **not** destroy the Photon room — Photon's own
  room TTL (default 0 for shared mode) controls that. For friend matches, set
  `PhotonAppSettings` room TTL to `lobbyIdleTimeoutSeconds` so a brief
  disconnect doesn't orphan joiners.

**RLS:** `lobby_rooms` rows are world-readable (so joiners can look up a code
by exact string), but only the host may write `room_name`/`members`, and only
a member may remove themselves. Writes are mediated by the RPCs (SECURITY
DEFINER) so the RLS rules are simple.

### 8.2 `NetworkTrailSync` (batched trail replication)
This is the heart of multiplayer visibility. Sits next to `NetworkPlayer` on
the same NetworkObject.
- A `[Networked] NetworkArray<float>` of fixed capacity (`MaxBatchPoints · 4`,
  `MaxBatchPoints = 16`) holds one packed batch of points, plus `BatchStart`,
  `BatchCount`, `BatchSeq`, **and a per-batch double origin** `OriginLat`,
  `OriginLon` (two `[Networked] double`s).
- **Wire precision (pitfall #16): never put absolute lat/lon in a `float`.**
  A 32-bit float has ~7 significant digits; at longitude −122° one ulp is
  ≈ 0.7–0.9 m — the same order as `collisionThreshold` (1.5 m), so remote
  trails would be quantized into unreliable collision geometry. Pack each point
  as **offsets from the batch origin**: `[dLat·1e5, dLon·1e5, alt,
  time_offset]` (float offsets of ±0.01° span ≈ 1 km per batch with millimeter
  resolution — far more than 16 points ever cover). Decode as
  `origin + offset/1e5` in doubles. The same offset encoding applies to
  `TrailSnapshot` (§7.1) since it shares this layout. *The phase-4
  `TrailSnapshot` packs absolute floats — fix before phase 8.*
- **Authority (`PushLocalTrail`)** — every `FixedUpdateNetwork`, if the local
  trail grew since `_lastSentPointCount`, packs a batch starting at the *oldest
  unsent* point (`from = _lastSentPointCount`), advances by exactly the points
  packed so surplus points flow in subsequent ticks. *Do not send the freshest
  tail* — that drops intermediate points and makes remote clients bridge the gap
  with a phantom straight segment (false/missed collisions). Batch size is
  `min(config.trailSyncBatchSize, MaxBatchPoints)`.
- **Proxy (`PullRemoteTrail`)** — applies each distinct batch exactly once
  (`BatchSeq != _lastAppliedSeq`), reads the owner id from the sibling
  `NetworkPlayer`, builds a `TrailSnapshot`, and calls
  `TrailManager.UpdateRemoteTrail`. Append-only index merge makes this
  idempotent.

### 8.3 `NetworkPlayer` (`NetworkBehaviour`)
Networked state: `PlayerId (NetworkString<_64)`, `BeaconFormType`, `IsCrashed`,
`PositionX/Y/Z`, `Heading`.
- **`Spawned`** — builds the beacon (`BeaconController` on a child GO) and adds
  a `TrailCollisionDetector` to itself, wired to `OnCrash`. Local authority also
  calls `TrailManager.StartRun` (idempotent — see §7.2).
- **`FixedUpdateNetwork`** — local authority: reads
  `LocationProvider.CurrentPosition`, converts to world, sets the networked
  position/heading fields, updates the beacon, and runs the collision check.
  Remote proxy: reads the networked position into the beacon visual.
- **`OnCrash(causedById)`** — sets `IsCrashed`, plays the beacon crash effect,
  ends the local trail, and raises `GameEvents.RaisePlayerCrashed`. **Never**
  call `GameManager` directly (would be a circular assembly ref).

### 8.4 Fallback collision (no-Fusion path)
`GameManager` keeps its own `TrailCollisionDetector` as a **fallback**: it runs
only while no local-authority `NetworkPlayer` exists (i.e. Fusion failed to
connect). Without it, a run with no network has *no crash detection at all*.
On collision it raises the same `GameEvents.RaisePlayerCrashed`, so the crash
pipeline is identical regardless of network state.

---

## 9. Beacon subsystem (`LightRunners.Beacon`)

### 9.1 `BeaconFormManager` (singleton)
Owns the form table (loaded from `BeaconFormData.Defaults`). `SelectForm`
respects unlock state; `GetTrailColor`, `GetPrefabName`, `GetDisplayName`,
`IsFormUnlocked`, `UnlockForm` round out the API.

### 9.2 `BeaconController`
The player avatar visual. Holds a model root, glow particles, a light, and a
Unity trail renderer. `SetForm` loads `Resources/Beacons/<prefabName>` or, if
missing, **builds a primitive fallback mesh in code** (so the game runs with
zero prefabs) — each of the 8 forms has a distinct primitive composition
(cuboid hoverboard, sphere orb, cylinder drone with 4 rotor spheres, custom
tetrahedron prism, cube, motorcycle chassis+wheels, capsule phoenix with wings,
a row of sine-height bars for waveform). `UpdatePosition` applies bob
animation, faces the heading, spins the model. `SetTrailColor` tints the light,
particles, and trail. `PlayCrashEffect` bursts particles.

### 9.3 `BeaconEffects` (optional, `[RequireComponent(BeaconController)]`)
Speed-reactive particle FX: glow emission scales with speed; speed-lines appear
above a threshold; trail-create pulse and a colored crash explosion. Respects
`PerformanceMonitor.ReduceParticles`.

---

## 10. Map subsystem (`LightRunners.Map`)

### 10.1 `IMapProvider`
The seam: `Initialize(lat,lon,zoom)`, `UpdateCenter`, `SetZoom`,
`ShowPlayerBeacon`, `UpdatePlayerBeacon`, `DrawTrailOverlay(playerId, points,
color)`, `RemoveTrailOverlay`, `Show`/`Hide`, `IsVisible`, `IsInitialized`.

### 10.2 `OSMMinimapView` (implements `IMapProvider`, self-registers in Awake)
Corner RawImage minimap, expandable on tap (3× via `ExpandButton`). Subscribes
to `LocationProvider.OnPositionUpdated` (recenters) and
`TrailManager.OnLocalPointAdded` / `OnRemoteTrailUpdated` (draws trail
polylines in the owner's beacon color). Redraws all overlays on recenter.

### 10.3 `OSMTileProvider`
Fetches `https://tile.openstreetmap.org/{z}/{x}/{y}.png`. **OSM policy
compliance** (enforced by config): `User-Agent` header set, ≤
`osmMaxConcurrentRequests` (2) concurrent, ≥ `osmTileRequestInterval` (1 s)
between requests. Two-level cache: in-memory LRU (`osmTileCacheSize` 64) +
on-disk PNG at `Application.persistentDataPath/osm_tiles`. Slippy-map mercator
math in `LatLonToTile`/`TileToLatLon`.

> **Production note:** self-host tiles (Docker) for scale; OSM's free endpoint
  is for dev only.

### 10.4 `MapTileRenderer`
Composites a 3×3 tile grid into one RGB `Texture2D` (flipped Y for screen
space), draws onto a separate RGBA overlay texture: player dot (filled circle)
and trail polylines (Bresenham, thickness 2). `GeoToPixel` maps geo → composite
pixel relative to the center tile.

---

## 11. AR subsystem (`LightRunners.AR`, gated `UNITY_XR_ARFOUNDATION`)

### 11.1 `IARViewController`
Lifecycle seam so `ViewTransitionManager` can drive AR without referencing AR
Foundation: `EnterAR`, `ExitAR`, `IsARAvailable`, `IsARActive`,
`LoadNearbyTrails`, `UpdateARHeightOffset`.

### 11.2 `ARViewManager` (singleton, implements `IARViewController`, self-registers)
- Serialized refs (gated): `ARSession`, `XROrigin` (AR Foundation 6 — **not**
  the deprecated `ARSessionOrigin`), `ARPlaneManager`; plus `arTrailParent`,
  `arBeaconParent`.
- `EnterAR` — enables session/origin/plane manager, subscribes to plane events
  (first detected plane locks the initial altitude baseline), loads nearby
  persisted trails from the backend and projects them.
- `LoadNearbyTrails` — calls `TrailRepository.LoadNearbyTrails(pos,
  arTrailRenderDistance)`, capped at `arMaxNearbyTrails`, projecting each into
  an `ARTrailObject`.
- `ProjectTrailIntoAR` / `ShowBeaconInAR` / `UpdateBeaconPosition` /
  `RemoveBeacon` — manage AR-space trail/beacon GameObjects, converting geo →
  world via `CoordinateConverter` and applying `trailGroundOffset` on Y.
- `_arAvailable` is true only on `UNITY_ANDROID || UNITY_IOS`.

### 11.3 `ARTrailObject`
A `LineRenderer` with the neon material, built from a `TrailSnapshot`'s world
points. `UpdateVisibility(camPos, maxDist)` culls by nearest-point distance and
fades alpha quadratically with distance.

### 11.4 `ViewTransitionManager` (in Gameplay, not AR)
Cross-fades between Map and AR over `transitionDuration` (0.6 s, smoothstep):
swaps `Camera.depth` and the two `CanvasGroup` alphas, enables/disables the
cameras, and calls `IARViewController.EnterAR/ExitAR` at the right moments.
Subscribes to `GameManager.OnViewModeChanged`.

---

## 12. Backend & identity

### 12.1 Identity (`LightRunners.Identity`)
- `IAuthService` — `SignInAnonymously(onSuccess, onError)`,
  `TryRestoreSession()`, `Logout`, `CurrentIdentity`, `CurrentUserId`,
  `IsAuthenticated`, events `OnAuthenticated` / `OnLogout`. The seam is kept so
  a future self-sovereign auth flow can slot in without touching call sites.
- `SupabaseAuthService` — anonymous auth. Persists the **refresh token** in
  `PlayerPrefs` (`sb_refresh_token`) so a returning user keeps the same ephemeral
  account. Display name = `"Runner_" + first 6 chars of the UUID`.

### 12.2 `SupabaseManager` (singleton, `Backend`)
Thin REST/RPC client over `UnityWebRequest`. Base URL + anon key from
`GameConfig`. Auth header = `Bearer <access_token or anon key>`. Methods:
`SignInAnonymously` (POST `/auth/v1/signup` empty body), `RestoreSession`
(POST `/auth/v1/token?grant_type=refresh_token`), `SignOut`, plus generic
`Get/Post/Upsert/Patch/Rpc/RpcRaw/InvokeEdgeFunction` over `/rest/v1/...`.

### 12.3 Repositories
- `TrailRepository` (MonoBehaviour on GameManager GO) — `StartAutoSave` (loops
  every `trailSaveInterval`, POSTs the current trail as a `trails` row then
  batches its `trail_points` in groups of 100), `SaveFullTrailOnCrash`,
  `FinalizeTrail` (PATCHes total distance / point count / crashed /
  crash-cause / end geo / ended_at), `LoadNearbyTrails` (RPC `get_nearby_trails`).
  Geo points serialized as PostGIS `SRID=4326;POINT(lon lat alt)`.
- `PlayerRepository` — `RegisterOrUpdatePlayer` (upsert), `GetPlayer`.

### 12.4 Run summary persistence
`RunSummaryUI.ShowSummary` computes the score, populates the panel, and POSTs
the `record_run` RPC with the full score breakdown + beacon form + crashed flag.

### 12.5 Database schema (`Supabase/schema.sql`)
PostgreSQL + PostGIS. Tables:
- **`players`** — `id UUID PK → auth.users(id)`, display_name, beacon_form,
  total_distance, total_runs, longest_run, level, timestamps. Auto-created by
  the `on_auth_user_created` trigger (covers anonymous sign-in).
- **`trails`** — one per run: player_id, room_id, beacon_form, color rgb,
  total_distance, point_count, crashed, crash_cause_id, start/end geo
  (`GEOGRAPHY(POINTZ,4326)`), timestamps. GiST index on start_geo.
- **`trail_points`** — trail_id, lat/lon/alt, sequence_index, a **generated
  stored** `geo` geography column, timestamp. GiST on geo, btree on
  (trail_id, sequence_index).
- **`run_history`** — full score breakdown per run; indexed for leaderboards.
- **`lobby_rooms`** — one per active friend match (§8.5):
  `code TEXT PK` (the 6-char share code), `host_id UUID → players(id)`,
  `room_name TEXT NOT NULL` (the `party_*` Photon room name), `region TEXT`
  (the host's zone cell, so leaderboards can attribute runs to the room's
  region), `members JSONB` (array of `{user_id, display_name}`),
  `created_at TIMESTAMPTZ DEFAULT now()`, `expires_at TIMESTAMPTZ NOT NULL`
  (default `now() + lobbyIdleTimeoutSeconds`), `status TEXT`
  (`open|racing|closed`), `started_at TIMESTAMPTZ` (set by `start_lobby_race`;
  the joiners' start signal — §8.5). Indexes: btree on `expires_at` (for
  sweep), btree on `host_id`.
- **`lobby_join_attempts`** — rate-limit ledger for `join_lobby` (pitfall #15):
  `user_id UUID`, `attempted_at TIMESTAMPTZ DEFAULT now()`, `code TEXT`,
  `success BOOLEAN`. `join_lobby` counts the caller's rows in the last 60 s
  before doing anything; > 10 → raise `rate_limited`. Sweep rows older than
  1 h alongside the lobby sweep. Failed lookups stay queryable so code-spray
  attacks are visible.

RPC functions: `get_nearby_trails`, `update_player_stats`, `record_run`
(insert history + update stats), `get_global_leaderboard`,
`get_nearby_leaderboard` (PostGIS `ST_DWithin`), `get_player_best`.

**Level formula (pinned — this drives the §2.4 unlocks):**
```
level = floor(sqrt(total_distance_meters / 1000))     -- i.e. floor(sqrt(km))
```
Level 5 (Prism) = 25 km lifetime, 10 (Cube) = 100 km, 15 (Runner) = 225 km,
20 (Phoenix) = 400 km, 25 (Waveform) = 625 km. *v1 said "log₂-based" — with
log₂, level 25 needs 2²⁵ km ≈ 33 million km; the top three unlocks were
unreachable (pitfall #21). sqrt keeps early levels quick and late levels a
season-scale goal for a daily runner.* The client learns its level from the
`players` row on sign-in (`PlayerRepository.GetPlayer`) and re-reads it in the
run summary after `record_run`; unlock enforcement is client-side only at v1
(cosmetic stakes — see §22).

**Friend-match RPCs** (§8.5), all `SECURITY DEFINER` so RLS stays simple:
- `create_lobby(host_user_uuid, region TEXT)` → `{code, room_name}`. Mints a
  random code from `lobbyCodeAlphabet`, idempotent-on-collision (retry until
  unique), inserts the row with the host as the sole member, lazy-sweeps
  expired rows as a side effect.
- `join_lobby(code TEXT)` → `{room_name, members, host_id, expires_at}` or
  raises `lobby_full` / `lobby_expired` / `lobby_closed`. Appends
  `auth.uid()` to members if not present and the lobby is under
  `lobbyMaxMembers`.
- `leave_lobby()` → removes `auth.uid()` from any lobby's members; if the
  caller was host, promotes the next member or sets `status='closed'`.
- `start_lobby_race()` → host-only; sets `status='racing'`, `started_at=now()`
  on the caller's hosted lobby. Raises `not_host` otherwise.
- `get_lobby(code TEXT)` → `{room_name, members, host_id, status, started_at,
  expires_at}` (read, public — serves both the join lookup and the joiners'
  start-signal poll, §8.5).

**RLS** everywhere; ownership keyed by `auth.uid()`. Reads are public (so
leaderboards/nearby trails work), writes are owner-only. Realtime publication
includes `trails`.

---

## 13. Shaders (URP)

Three game-specific shaders, referenced by name via `Shader.Find`:

| Shader | Used by | Key uniforms |
|---|---|---|
| `LightRunners/NeonTrailEnhanced` | `NeonTrailRenderer`, `ARTrailObject` | `_BaseColor`, `_EmissionColor`, `_Width` |
| `LightRunners/BeaconGlow` | beacon prefabs (editor generator) | `_BaseColor`, `_EmissionColor` |
| `LightRunners/ScreenCrashFlash` | `CrashSequence` fullscreen material | `_FlashIntensity`, `_Distortion`, `_VignetteIntensity`, `_FlashColor` |

Fallback materials (when these aren't present) use
`Universal Render Pipeline/Lit` with high metallic/smoothness. Reimplement the
three shaders as UPR-compatible (emissive additive neon, glow, fullscreen
flash with chromatic aberration + vignette).

---

## 14. Scene structure (generated, never hand-edited)

Both scenes are built by editor scripts under a `Light-Runners/Setup` menu
(`SceneSetup`, `PrefabSetup`). **Scenes must be regenerated, not YAML-edited.**
This keeps scene state reviewable and reproducible.

### 14.1 Login scene
```
MainCamera (Camera, solid-color dark)
Canvas (ScreenSpaceOverlay, scaler 1080×1920 match-height)
  LoginPanel (full-stretch)
    TitleText "Light-Runners"
    InfoText  "Anonymous sign-in — tap Play to start"
    PlayButton (Button)        → LoginUI.OnPlayClicked → load "Game"
    StatusText "" (yellow)
  + LoginUI (loginPanel, loginButton, statusText, infoText)
EventSystem + StandaloneInputModule
```

### 14.2 Game scene
```
PlatformServiceRegistry   (Awake FIRST — registers IAuthService + IAltitudeService; DontDestroyOnLoad; idempotent)
GameManager               (owns state, TrailRepository, fallback collision detector)
LocationProvider          (GPS / editor sim)
TrailManager
BeaconFormManager
FusionLauncher            (FUSION_WEAVER)
SupabaseManager
PerformanceMonitor        (targetFrameRate 60, adaptive quality)
GPSPowerManager
Map/
  Minimap (Canvas sortingOrder -1; RawImage + OSMMinimapView + ExpandButton)
AR/                       (UNITY_XR_ARFOUNDATION — built by reflection so the generator compiles without the package)
  ARSession               (UnityEngine.XR.ARFoundation.ARSession)
  XROrigin                (Unity.XR.CoreUtils.XROrigin + ARPlaneManager + ARCamera w/ TrackedPoseDriver)
  ARTrails, ARBeacons
  ARViewManager           (wired: session, origin, planeManager, arTrailParent, arBeaconParent)
MainCamera                (top-down, depth 0)
ViewTransitionManager     (mainCamera, arCamera, mapCanvasGroup α=1, arCanvasGroup α=0)
TrailLODManager
CrashSequence             (flashMaterial = LightRunners/ScreenCrashFlash)
HUDCanvas (sortingOrder 10)
  HUDPanel (full-stretch)
    SpeedText, AltitudeText, TimeText, DistanceText, PlayersText
    ViewToggle "AR Mode", BeaconFormButton "Hoverboard", EndRunButton "End Run"
    CrashPanel (hidden; fallback only — superseded by SummaryPanel)
  StartRunButton (sibling of HUDPanel; visible at Lobby)
  SummaryPanel (full-stretch, hidden; the single end-of-run screen)
    SummaryCrashText, TotalScoreText + label
    SummaryDistanceText/TimeText/AvgSpeedText + labels
    DistanceScoreText/SpeedScoreText/BeautyScoreText/ProximityScoreText
    RunAgainButton, ContinueButton
    + RunSummaryUI
# Lightfield match core (active decisions 2026-07-18 — see §1.5 / §3.6)
MatchManager               (Track D — sub-FSM; constructs LumenScoreboard/TailAuthority/GateSpawner/LightfieldVolume/ReplayPackageSink in Awake)
ViewModeBootstrap          (Track D — decision H; forces AR on Start)
Afterglow/                 (Track F — decision A/U; toggled on MatchExpired)
  OverviewCamera           (Track F — top-down orthographic; frames captured trails)
  + AfterglowViewController (Track F — overviewView=OverviewCamera; walkInsideView=null per decision S)
MatchHUD (Canvas sortingOrder 11)
  + TacticalRadar          (Track D — decision H; gate + runner blips; self-builds ring/blips)
  + OffScreenIndicator     (Track D — decision I; screen-edge arrows; leader tint)
  + LeaderCrown            (Track D — decision I; follows leader's projected pos)
EventSystem + StandaloneInputModule
Player/                   (spawned at runtime from Resources/Player/NetworkPlayer.prefab)
NetworkMatchState         (host spawns from Resources/Player/NetworkMatchState.prefab at match start; NOT scene-placed — NetworkObjects live on a NetworkRunner)
# Pure-C# hosts NOT in the scene — MatchManager constructs and registers them on the ServiceLocator:
#   LumenScoreboard, TailAuthority (Track A) ; GateSpawner, LightfieldVolume (Track B) ; ReplayPackageSink (Track F)
```

### 14.3 Editor menu (`Light-Runners/Setup`)
- **Generate All Scenes** — Login + Game.
- **Login Scene / Game Scene** — individually.
- **Ensure Project Assets (URP + GameConfig)** — `[InitializeOnLoad]` once per
  session; creates `Assets/Settings/URP/URPAsset.asset` and
  `Resources/GameConfig.asset` if absent.
- **NetworkPlayer Prefab** (FUSION_WEAVER) — minimal prefab:
  `NetworkObject` + `NetworkPlayer` + `NetworkTrailSync`. *Do not* add beacon or
  collision components here — `NetworkPlayer.Spawned` builds those at runtime,
  and duplicating them would double the beacon and the collision check.
- **NetworkMatchState Prefab** (FUSION_WEAVER, Track C, decision Q/T) — minimal
  prefab: `NetworkObject` + `NetworkMatchState`. The host spawns one per match
  at match start; it carries the `[Networked] FrozenTailRadius`. Not scene-
  placed (NetworkObjects live on a NetworkRunner).
- **Beacon Prefabs** — the 8 beacon prefabs in `Resources/Beacons/`.
- **Gate Prefabs** (Track B, decisions G/L/M/R) — `Resources/Gates/LumenGate.prefab`
  and `Resources/Gates/StolenLumenPickup.prefab`. Recipe per prefab: a
  `SphereCollider` (isTrigger) + the behaviour. The visual hemisphere / orb is
  built in code at runtime, so the prefab ships with zero art.
- **All Prefabs** — Beacons + NetworkPlayer + Gates + NetworkMatchState.
- **Tools → Validate Setup** — `SetupValidator` EditorWindow checks GameConfig
  (+ Lightfield fields non-default), both scenes in Build Settings, beacon
  prefabs, gate prefabs, Supabase URL, URP pipeline, AR Foundation, Fusion,
  custom shaders, Lightfield/Afterglow assemblies present, MatchManager in
  Game scene.

> **Build Settings:** both scenes must be added; Login at index 0.

---

## 15. Build settings

### Android
- Min API 26; IL2CPP; ARM64.
- Permissions: Internet, Access Fine Location, Barometer (sensor).
- XR Plug-in Management: enable **ARCore**.

### iOS
- Min iOS 14.0.
- Info.plist: `NSLocationWhenInUseUsageDescription`, `NSCameraUsageDescription`.
- XR Plug-in Management: enable **ARKit**.

Scripting defines per build target: `FUSION_WEAVER`, `FUSION2` (set by Fusion's
wizard), and the `UNITY_XR_ARFOUNDATION` version-defines on the AR/Location
asmdefs (auto). Camera + location usage descriptions must be set.

---

## 16. Crash pipeline (the critical path — trace it carefully)

Two entry points converge on one handler:

```
NetworkPlayer.SyncLocalPosition ─┐                         (Fusion path)
GameManager.OnPositionUpdate ────┤─▶ TrailCollisionDetector.CheckCollision
                                 │      └─▶ OnCollisionDetected(ownerId)
                                 │             ├─ [Fusion] NetworkPlayer.OnCrash ─▶ IsCrashed=true, EndRun
                                 │             └─ [fallback] GameManager.OnFallbackCollision
                                 │                                                  │
                                 └──────────────────────────────────────┐            │
                                                                        ▼            ▼
              GameEvents.RaisePlayerCrashed(causedByPlayerId) ◀──────────────────────┘
                                  │
                                  ▼
              GameManager.OnPlayerCrashed  (guard: state == Running)
                  ├─ SaveFullTrailOnCrash + FinalizeTrail(crashed=true)
                  ├─ TrailManager.EndRun(crashed=true)
                  ├─ TeardownFallbackCollisionDetector
                  ├─ FusionLauncher.Disconnect   (FUSION_WEAVER)
                  ├─ CrashSequence.Play(color)
                  ├─ SetState(Crashed)
                  └─ RunSummaryUI.ShowSummary(...)
```

**Invariants to preserve:**
- `OnPlayerCrashed` is double-fire-guarded (`state != Running → return`) because
  both the Fusion detector and the fallback can fire.
- The local trail must be saved *before* `EndRun` nulls it.
- `timeScale` must always return to 1 (CrashSequence + its OnDestroy).

---

## 17. Known pitfalls (lessons from v1)

These are the bugs found during the v1 trace (`PLAN.md` Phase 4). Bake the
fixes into the new implementation:

1. **Self-collision grace direction.** Skip the *newest* N segments (shared
   endpoints with the movement segment) but **keep checking older self-segments**
   — otherwise you can never crash into your own loop. Verify with: sharp turns
   don't self-crash; crossing your own older trail does.
2. **Duplicate `StartRun`.** Two callers (`GameManager.StartRun` and local
   `NetworkPlayer.Spawned`) — make `StartRun` idempotent for the same
   owner+form+color, or the late call wipes the opening segment.
3. **No death without Fusion.** If `Connect` fails, attach a local-only
   collision detector (the §8.4 fallback) or the run is uncrashable.
4. **Batch-gap phantom segments.** Send trail batches from the oldest unsent
   point, never the freshest tail; otherwise remote clients bridge gaps with
   straight phantom segments that cause false/missed collisions.
5. **`UNITY_XR_ARFOUNDATION` is custom.** Add it as a version-define on the AR
   and Location asmdefs, or all AR code is compiled out and the toggle does
   nothing.
6. **AR Foundation 6 API.** Use `XROrigin` (Unity.XR.CoreUtils), not the
   deprecated `ARSessionOrigin`. The AR camera needs an
   `InputSystem.XR.TrackedPoseDriver`.
7. **Collision threshold vs GPS noise.** Real GPS jitter is 3–10 m; tune
   `collisionCheckRadius` / `collisionThreshold` /
   `selfCollisionSkipPoints` on-device so the game is neither unfair nor
   uncrashable.
8. **End Run had no caller in v1.** Wire an End Run button, visible only in
   `Running`, from day one.
9. **RunSummaryUI wasn't in any scene.** Put it in the Game scene; it's the
   end-of-run screen for both crash and voluntary end.
10. **Secrets are per-environment.** Ship `GameConfig.asset` with blank
    Supabase URL/key; fill per install. Don't commit real keys.
11. **Friend code is the only grouping primitive — resist feature creep.** Do
    not add friends lists, social graphs, or push invites. The 6-char code
    shared out-of-band is the entire UX (§1.1). The moment `players` gains a
    `friends` column, the anonymous-by-default pillar is dead.
12. **Lobby host migration is mandatory.** If the host disconnects mid-lobby
    without promotion, the room orphans and joiners sit forever. `leave_lobby`
    must promote the next member (or close the lobby) atomically; do not rely
    on the client to do it.
13. **Anonymous-zone and friend-room must share the `Connect` call.** The only
    thing that differs is the room-name string. Two code paths in
    `FusionLauncher` is a bug — it means the crash pipeline (§16) and
    `NetworkPlayer` setup have to fork. One entry point, two producers.
14. **Lobby TTL ≠ Photon room TTL.** The `lobby_rooms.expires_at` (default 30
    min) is when the *backend* row sweeps; the Photon room TTL (default 0 for
    shared mode) is when the *match* dies. Set Photon's TTL to the lobby TTL
    or a brief host disconnect orphans everyone.
15. **Codes are guessable by design — rate-limit `join_lobby`.** 6 chars from a
    32-symbol alphabet is ~10⁹, but a determined attacker can brute-force. Cap
    `join_lobby` calls per `auth.uid()` per minute (e.g. 10) at the RLS / RPC
    layer (the `lobby_join_attempts` ledger, §12.5), and log repeated misses so
    you can spot code-spray attacks early.

The following were found in the 2026-07-04 gap review of the phase 1–4 code
(design-level, caught before device testing):

16. **Absolute lat/lon in a 32-bit float is ~1 m of quantization.** One float
    ulp at longitude −122° ≈ 0.7–0.9 m — the same order as `collisionThreshold`
    (1.5 m). Remote trails built from float lat/lon are jagged enough to
    produce false and missed collisions. The wire format must send per-batch
    double origins + float *offsets* (§8.2).
17. **Proximity sampled once at end-of-run is a dead axis.** Sampling
    `CountPlayersNear` at crash time within the 5 m collision radius yields 0
    for essentially every run. Sample every `proximitySampleInterval` within
    `proximityRadius` during the run and score the peak (§7.4).
18. **`TotalLength` recomputed from a pruned list undercounts long runs.** At
    5000 points × 1 m spacing, pruning kicks in right at the 5 km
    distance-score ceiling — the exact runs it corrupts. Keep a running
    accumulator that `PruneTo` never touches (§7.1).
19. **Sequence index ≠ list index once pruning exists.** The remote-merge
    condition `ownerSequenceIndex >= PointCount` and snapshot slicing by list
    position are only correct while nothing is ever pruned. Track a
    `HighestAppliedSequence` cursor per trail and slice snapshots by sequence
    number, not list position (§7.2, §4.2).
20. **Photon shared-mode matchmaking is per region.** Auto-region selection can
    put two same-zone phones in different Photon regions — same room name,
    different rooms, and they silently never meet. Pin `FixedRegion` in
    `PhotonAppSettings` (§8.1).
21. **A log₂ level curve makes the top unlocks unreachable.** Level 25 under
    log₂(km) is 2²⁵ ≈ 33 million km. Use `floor(sqrt(km))` (§12.5) — Waveform
    at 625 km lifetime is ambitious but human.

---

## 18. Suggested build order

Each phase leaves a compiling, runnable project.

1. **Project skeleton.** Unity 6 URP project; create the asmdef graph (§3.3);
   `GameConfig.asset`; `Core` types (§4); `CoordinateConverter` (§5);
   `ServiceLocator`, `Singleton<T>`, `GameEvents`.
2. **Location + editor sim.** `LocationProvider` with the `UNITY_EDITOR`
   simulated-walk mode, `IAltitudeService` + `FallbackGPSAltitudeService`,
   `GPSPowerManager`. *You can now move a dot in playmode.*
3. **Trail + collision + scoring.** `TrailData`, `TrailManager`,
   `TrailCollisionDetector`, `RunScorer`, `NeonTrailRenderer`. Verify
   self-crash vs turn-grace in the simulator.
4. **Game loop + HUD + summary + scenes.** `GameManager`, `HUDController`,
   `RunSummaryUI`, `CrashSequence`, `ViewTransitionManager`, the two scenes +
   the `Light-Runners/Setup` generator. Full loop testable in editor.
5. **Beacons.** `BeaconFormManager`, `BeaconController` (with fallback meshes),
   `BeaconEffects`.
6. **Map.** `OSMTileProvider` (rate-limited, cached), `MapTileRenderer`,
   `OSMMinimapView`. Trails draw on the minimap.
7. **Backend + identity.** `SupabaseManager`, repos, `schema.sql`, anonymous
   auth, `record_run`. Verify a `players` row appears on sign-in.
8. **Multiplayer (anonymous).** `FusionLauncher` (shared mode, zone rooms),
   `NetworkTrailSync` (oldest-unsent batching), `NetworkPlayer`. Fallback
   collision detector for the no-Fusion path. Verify: two editors see each
   other's trails; crossing a remote trail crashes the right client.
9. **Friend match.** `lobby_rooms` table + `create_lobby` / `join_lobby` /
   `leave_lobby` RPCs (§12.5); `ILobbyService` + `SupabaseLobbyService`
   (Backend, registered via `SupabaseManager`); `LobbyUIController` + the
   `PartyLobby` state (§2.3, §8.5). Verify: host mints code, second client
   joins by code, both land in the same `party_*` Photon room and see each
   other's trails via the §8.2 sync. Rate-limit `join_lobby`.
10. **AR.** Install AR Foundation 6 + ARKit/ARCore; version-define
    `UNITY_XR_ARFOUNDATION`; `ARViewManager`, `ARTrailObject`; wire the toggle.
11. **Shaders + polish.** Neon/glow/flash shaders; crash FX tuning; LOD;
    performance monitor; on-device tuning of collision thresholds.
12. **Device verification (human checkpoints).** GPS trail recording; AR camera
    + trails; two-phone multiplayer crash; Supabase RLS cross-player write test;
    iOS TestFlight + signed APK.
13. **Lightfield match core (active decisions 2026-07-18 — parallel tracks
    A/B/C/D/E/F/G; see §1.5 / §3.6).** Seven parallel tracks land a
    host-authoritative timed match on top of phases 1–12:
    - **Track A** (`Trail/`): `LumenScoreboard`, `SnakeTailModel`,
      `TailAuthority`, sweep subdivision (decisions C/E/F/I/N/T). Replaces the
      `RunScorer` score primitive with the integer Lumen tally.
    - **Track B** (`Lightfield/`): `LightfieldVolume`, `GateSpawner`,
      `LumenGate`, `StolenLumenPickup` (decisions G/K/L/M/R).
    - **Track C** (`Multiplayer/`): `FusionLauncher` migrates **Shared → Host
      Mode** (decision Q); `NetworkMatchState` replicates the frozen tail
      radius (decision T); `RefereeClient` + `RefereeTokenValidator` for
      validated bonus-gate commands (decision R).
    - **Track D** (`Gameplay/` + `UI/`): `MatchManager` sub-FSM (decision P),
      `ViewModeBootstrap` (decision H — AR-primary), `TacticalRadar` /
      `OffScreenIndicator` / `LeaderCrown` (decisions H/I), `RunSummaryUI`
      refactor to read the Lumen tally.
    - **Track E** (`Backend/` + `Supabase/`): drops `RunScorer` columns and
      `record_run` RPC; adds `matches` / `match_players` tables +
      `record_match_player` RPC; `crash_lumen_loss_*` persistence.
    - **Track F** (`Afterglow/`): `ReplayPackage`, `ReplayPackageSink`,
      `OverviewCameraController`, `AfterglowViewController` (decisions A/U/T/S —
      Walk-Inside stubbed per decision S).
    - **Track G** (`Editor/`): scene generator + prefab generator + validator
      wiring for all of the above; SPEC/README updates.

    Each track compiles independently of the others (reflection-driven scene
    wiring + `Null*` ServiceLocator stubs). Verify after merge: a single editor
    session can Start Match → countdown → live → touch a gate (+1 Lumen) →
    crash (penalty + respawn) → clock expiry → Afterglow Overview showing the
    finished trails at the frozen tail radius.

---

## 19. Verification checklist (definition of "done")

- [ ] Editor playmode: Login → Play → Lobby → Start Run → walk (sim) → End Run
      → summary → Lobby, with no errors.
- [ ] Editor playmode: crossing your own older trail crashes; sharp turns don't.
- [ ] Editor: two Fusion instances (shared mode + sim, or ParrelSync) see each
      other's beacons and trails; crossing a remote trail crashes the right phone.
- [ ] Friend match (phase 9): host mints a code, a second client joins by code,
      both land in the same `party_*` room and see each other's trails
      (independent of their GPS zone). Host leaving promotes a joiner or closes
      the lobby. `join_lobby` rejects a full / expired / closed lobby and is
      rate-limited per `auth.uid()`.
- [ ] Device: GPS records a real trail; AR camera shows trails in world space;
      altitude reads sensibly; Map↔AR toggle mid-run doesn't break recording.
- [ ] Backend: anonymous sign-in creates a `players` row; a run writes
      `trails` + `trail_points` + `run_history`; cross-player write blocked by RLS.
- [ ] `Tools → Validate Setup` passes; both scenes in Build Settings at the
      right indices; iOS + Android build profiles committed.
- [ ] Wire precision: a remote trail decoded from §8.2 batches deviates < 5 cm
      from the sender's geo points (unit-testable via `TrailSnapshot`
      round-trip at longitude −122°).
- [ ] Scoring: a simulated run alongside a second client scores proximity > 0;
      a 6 km simulated run scores the full 40 distance points despite pruning.
- [ ] Lifecycle: backgrounding mid-run > `backgroundGraceSeconds` ends the run
      as a voluntary end with the trail saved; re-focusing within grace resumes
      recording; the screen never sleeps during `Running`.
- [ ] Offline: airplane-mode Start Run still gives a crashable solo run with
      the offline badge; a run recorded offline enqueues `record_run` and
      flushes on next launch with connectivity.
- [ ] EditMode test suite (§26) green.

---

## 20. App lifecycle, screen & battery policy

**v1 stance: this is a foreground, screen-on game.** No iOS background-location
mode, no Android foreground service. That decision trades "phone in pocket"
play for a dramatically simpler build (no background-mode review friction, no
service lifecycle) — revisit only after v1 ships.

What that requires, concretely:

- **Wake lock.** `Screen.sleepTimeout = SleepTimeout.NeverSleep` on entering
  `Running`; restore `SystemSetting` on leaving it. Without this, the OS dims
  and sleeps mid-run and GPS dies with the screen. (Unspecified in v1 — nothing
  in the phase-4 code sets it.)
- **Backgrounding mid-run** (`OnApplicationPause(true)` while `Running`):
  enter `GameState.Paused`. Trail recording and collision checks stop (Unity
  stops ticking anyway on most devices); note the pause wall-clock time.
  - Refocus within `backgroundGraceSeconds` (60 s): return to `Running`. Do
    **not** bridge the gap with a segment — insert a *discontinuity marker*
    (see below). Run-duration for scoring keeps counting wall-clock time.
  - Refocus after grace, or OS kill: the run ends as a voluntary end. On next
    launch after a kill, an interrupted run is simply gone (v1 accepts the
    loss; the auto-save rows from §12.3 remain as an orphaned `trails` row —
    `FinalizeTrail` never ran — which the retention sweep (§23) cleans up).
- **Discontinuity markers.** A `TrailPoint` gap after `Paused`, a GPS dropout
  (> 10 s without an accepted fix), or an accuracy rejection streak means two
  consecutive points don't represent continuous movement. Mark the *first*
  point after the gap (`isSegmentStart` flag, or equivalently: the renderer,
  collision detector, and `TotalLength` accumulator all treat the
  (gap-1 → gap) pair as a non-segment). Otherwise the game draws — and crashes
  players on — a phantom wall across whatever you skipped.
- **Battery:** `GPSPowerManager` (§6.3) already covers sampling backoff. The
  `Paused` flow above is the other half.

## 21. Failure, offline & retry policy

The game must degrade to "solo run, local score" — never to a hang or an
uncrashable run. Photon failure handling is specified in §8.1; this section
covers auth, GPS, and Supabase.

| Failure | Behavior |
|---|---|
| Login: no network / Supabase down | Status text shows the error plainly ("Can't reach server — check connection"), Play button re-enabled for retry. After 3 consecutive failures, offer **Play offline** → Game scene with a local `local-<guid>` identity; no persistence, no multiplayer, everything else works. |
| Location permission denied | Blocking panel in the Game scene: one line of why + a **Grant** button (re-request / deep-link to app settings). The game is unplayable without location — do not fake it on device. Editor sim is unaffected. |
| Location services off / no fix for 30 s | Non-blocking banner "Waiting for GPS…" in Lobby; Start Run disabled until the first accepted fix. |
| Supabase write fails mid-run (auto-save / finalize / `record_run`) | Retry each request up to 2× with 2 s backoff, then drop auto-save batches (lossy by design) but **queue `record_run` and `FinalizeTrail`** payloads to `Application.persistentDataPath/pending_ops.json`; flush on next successful connectivity (app launch or next run end). Score shown locally either way — never block the summary panel on the network. |
| Supabase read fails (nearby trails, leaderboard) | Show empty state; log; no retry loop. |

Retry counts/backoffs are constants, not config — they're not gameplay tunables.

## 22. Trust model & anti-cheat stance

**v1 trusts the client.** GPS can be spoofed and `record_run` accepts a
client-computed score; a motivated cheater owns any leaderboard. This is
acceptable at v1 because the stakes are cosmetic (beacon unlocks, bragging
rights) — but say it out loud and add the cheap server-side floor now:

- `record_run` (SECURITY DEFINER) **rejects physically implausible runs**
  instead of trusting the payload: average speed > 12 m/s (world-class sprint
  is ~10), distance > 100 km, duration < 10 s with distance > 100 m, or a
  score exceeding the §7.4 axis maxima (>100 total or any axis over its cap).
  Rejected runs return success to the client (don't teach the prober) but land
  in a `rejected_runs` audit table, not `run_history`.
- Server-side score *recompute* from `trail_points` (teleport-hop detection,
  per-segment speed) is explicitly **post-v1** — the RPC shape shouldn't
  change, so it can be added without a client release.
- Unlock enforcement stays client-side (cosmetic; see §12.5).

## 23. Privacy, safety & data retention

A location game that publishes trails is a location-history database. Design
positions, not afterthoughts:

- **Identity floor:** anonymous UUIDs, random display names, no PII anywhere
  in the schema (§1.1 pillar). Keep it that way — reviews of new columns
  should ask "is this PII?"
- **Trail exposure:** `get_nearby_trails` is public-read *by design* (AR shows
  strangers' trails). Mitigations, not elimination: (a) **retention** — a
  daily sweep deletes `trails` + `trail_points` older than **30 days** (also
  bounds the biggest table; a 5 km run is ~5000 `trail_points` rows); (b)
  `get_nearby_trails` serves only trails from the **last 24 h** — AR is about
  *recent* runs, and stale trails are the home-address risk; (c) `run_history`
  keeps scores forever but no geometry (leaderboards survive the sweep).
  A "trails reveal where you ran" line belongs in the first-run disclaimer.
- **Home-address self-exposure:** starting a run at your front door publishes
  your front door for 24 h. v1 mitigation is documentation (the disclaimer),
  not geometry fuzzing — fuzzing the first/last 100 m breaks trail-wall
  gameplay near spawn. Revisit post-v1.
- **Physical safety disclaimer:** one-time full-screen notice on first run
  ("Heads up — eyes on the world, not the screen; don't run into traffic"),
  acknowledged once, stored in `PlayerPrefs`. AR mode doubles the risk; the
  notice covers both.
- **Age:** no age gate at v1 (no accounts, no PII, no chat — low COPPA surface
  by construction). Adding *any* social feature re-opens this question
  (pitfall #11 again).

## 24. Scope declarations (v1)

Explicit in-or-out so nobody "helpfully" adds them mid-build:

| Concern | v1 status |
|---|---|
| Leaderboard **UI** | **Out.** The RPCs (§12.5) ship and are load-bearing for retention math, but no screen renders them at v1. First post-v1 feature: a Lobby panel with global / nearby tabs. Don't let it creep into phase 4 UI work. |
| Audio | **Out**, except a single crash SFX slot on `CrashSequence` (serialized `AudioClip`, silent if empty). No music, no engine hum. |
| Haptics | **In, trivially:** `Handheld.Vibrate()` on crash, nothing else. |
| Localization | **Out.** English strings, hardcoded. No string-table plumbing at v1. |
| Accessibility | **Partial:** the 8 beacon forms are shape-distinct as well as color-distinct (§9.2's differing primitive compositions are load-bearing for colorblind players — keep them distinct). Trail *ownership* however is color-only; a post-v1 item. Text sizes follow the 1080×1920 reference scale; no dynamic type. |
| Tutorial / onboarding | **Out**, beyond the §23 safety notice and the Login one-liner. The loop is self-explanatory or it's broken. |
| Spectating, chat, emotes | **Out.** Chat especially — it re-opens moderation and age-gating (§23). |

## 25. HUD, summary & performance details

Pins for what phases 4–5 currently leave to taste:

- **Text stack:** UI text goes through `TMP_TextAdaptor` (Gameplay) so the
  scene generator works with or without TextMeshPro imported — TMP when
  present, legacy `UnityEngine.UI.Text` otherwise. New UI must use the adaptor,
  not raw `Text`/`TMP_Text` references.
- **HUD formats (update ~4×/s, not per-frame):** speed `F2` + " m/s" (current
  smoothed speed over the last 3 accepted fixes, not run average); altitude
  `F0` + " m"; distance: meters `F0` below 1 km, else `F2` + " km"; time
  `m:ss` (hours roll into minutes); players = live runner count in room
  (`TrailManager.LivePlayerCount`; reads 1 when solo/offline).
- **Summary panel:** the four axis rows show `score / max` (`"31 / 40"`); crash
  cause text: `"You crossed your own trail"` / `"You hit {displayName}'s
  trail"` / `"Run complete"` (voluntary). **Run Again** = `StartRun` again
  (fresh trail, same room-name production — re-runs the §8.1 connect,
  including for a still-`racing` friend room); **Continue** → `Lobby`.
- **`PerformanceMonitor` (Core):** sets `Application.targetFrameRate = 60`,
  `vSyncCount = 0`; keeps an EMA of fps; `ReduceParticles` latches true while
  smoothed fps < `lowFpsThreshold` (default 30). Consumers: `BeaconEffects`
  (§9.3), and phase-11 may add LOD-band tightening. That's the whole adaptive
  system — resist inventing a quality ladder.

## 26. Automated tests (EditMode — pure-C# seams)

The architecture already isolates pure logic; test it. The original phase-1–4
seam lived in one `LightRunners.Tests` editor asmdef (refs: Core, Trail,
Location). The Lightfield match core (§1.5, 2026-07-18) splits tests across
per-track asmdefs so each track's tests compile + run independently of the
others — a track can be merged without forcing the others' tests to land.
Unity Test Framework throughout; runnable via `Window → General → Test Runner`
and CI-able via `-runTests`.

**Per-track asmdefs (Lightfield match core):**

| Asmdef | Refs | Covers |
|---|---|---|
| `LightRunners.Tests` (original) | Core, Trail, Location | The original phase-1–4 cases below (sans the deleted `RunScorer` rows) |
| `LightRunners.Tests.Trail` | Core, Trail | `LumenScoreboard` integer tally + leader math + dropped-Lumen queue; `TailAuthority` freeze/unfreeze; `SnakeTailModel` sweep-subdivide (Track A) |
| `LightRunners.Tests.Lightfield` | Core, Lightfield | `LightfieldGeometry` disc + ceiling; `LightfieldVolume` boundary-violation dedup; `GateDensity.ActiveGateCount` table; `GateSpawner` density-pool + bonus-gate + referee-token gate (Track B) |
| `LightRunners.Tests.Backend` | Core, Backend | `RefereeTokenValidator` token-validate; `matches` / `match_players` RPC round-trip; RunScorer columns absent (Track E) |
| `LightRunners.Tests.Afterglow` | Core, Afterglow | `ReplayPackageSink` accumulate + finalize idempotency; `ReplayPackage` round-trip; selection/focus preserved across view switches (Track F) |
| `LightRunners.Tests.Multiplayer` | Core, Multiplayer (FUSION_WEAVER) | `NetworkMatchState` frozen-radius propagation contract; `RefereeClient` validate-then-forward (Track C) |
| `LightRunners.Tests.Gameplay` | Core, Gameplay | `MatchManager` FSM transitions; crash double-handling contract; offline `IsHostAuthority` fallback (Track D) |

**Original phase-1–4 cases (in `LightRunners.Tests`):**

| Area | Cases |
|---|---|
| `CoordinateConverter` | geo→world→geo round-trip < 1 cm within 5 km of reference; bearing N/E/S/W cardinal checks; reference reset between runs |
| `GeoPoint` | haversine against 2–3 known city-pair distances; zero-distance; antimeridian pair |
| `TrailData` | accumulator `TotalLength` unchanged by `PruneTo`; `AddPoint` sequence gating; `IsSameRun` truth table |
| Merge (§7.2) | out-of-order + overlapping batches idempotent; merge correct *after* pruning (cursor, not count) |
| `TrailSnapshot` | encode/decode round-trip < 5 cm at longitude −122° (guards pitfall #16); empty/short trails |
| ~~`RunScorer`~~ | **DELETED (decision E).** The 16 cases (axis tables + null-trail / <2-point / zero-duration guards + proximity sampler) were removed with the class — Track E. |
| Collision math | segment-intersection truth table incl. collinear, shared-endpoint, near-miss at threshold ± ε; grace-skip window boundaries (`skipRecent` = 0, N, count−1) |
| ~~`RunScorer` guards~~ | **DELETED (decision E).** |

PlayMode smoke (editor, scene-generated): Login → Lobby → Running → crash via a
scripted self-loop → `Crashed` → summary visible → Continue → `Lobby`; assert
`timeScale == 1` at the end. *(The Lightfield match core extends this to
Start Match → Countdown → Live → touch-a-gate → crash-and-respawn →
MatchExpired → Afterglow Overview, but the original golden path stays the
single canonical smoke.)* The sim already makes manual testing cheap.

