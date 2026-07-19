# Lightfield Match Core — Parallel Implementation Plan

Implements 19 of the 22 active decisions (defers aerial flight, referee Gate-Director v2, and Afterglow Walk-Inside per decision S's ground-only milestone). Strategy: **one serial Foundation phase** defines the Core contracts that unblock **7 disjoint tracks** working in parallel on separate asmdefs, then a serial integration order.

---

## Phase 0 — Foundation (I do this myself, serially, on `main`)

Purpose: define every cross-track contract in `Core/` so the 7 parallel tracks never touch a shared file. Mirror the existing `ILobbyService` pattern (`Core/Lobby.cs:42`).

### 0.1 VCS setup
- `git init` in repo root; `.gitignore` already exists.
- Initial commit of current state on `main`.
- Tag `v0-spec-v1` so the pre-Lightfield state is recoverable.

### 0.2 New Core enums (`Core/Enums.cs` — append only, don't reorder)
- `MatchState { Idle, Warmup, Countdown, Live, Scoring, Expired }` (distinct from existing `GameState` — match is a sub-FSM owned by `MatchManager`)
- `GatePlacement { Ground, Aerial }` (aerial stubbed in ground-only milestone)
- `PlayerRole { None, Runner, Host, Referee }`
- `CrashTier { NonLeader, Leader }`

### 0.3 GameConfig additions (`Core/GameConfig.cs` — new `[Header("Lightfield Match")]` block)
Append (do not remove existing fields):
```
[Header("Lightfield Match")]
public float gatesPerPlayer = 0.5f;
public float gateCollectionRadius = 2.0f;
public float tailRadius = 0.5f;              // decision T — authoritative, frozen at countdown
public float matchDurationSeconds = 360f;    // decision O — 6 min default
public float matchCountdownSeconds = 3f;
public int crashLumenLossNonLeader = 1;      // decision F
public int crashLumenLossLeader = 2;
public float stolenLumenPickupSeconds = 8f;
public float emergenceGraceSeconds = 2f;     // decision D — extends existing trailGracePeriod
public float lightfieldBaseRadiusMeters = 50f;  // decision K — ground disc for milestone
public float lightfieldDomeCeilingMeters = 6f;  // decision K — hard altitude ceiling stub (aerial deferred)
public float sweepSubdivideMaxStepMeters = 2f;  // decision N — long-sweep subdivision
```
Re-serialize `Assets/Resources/GameConfig.asset` so the new fields land (the bootstrap will pick them up; force-save once).

### 0.4 New Core interfaces (one file each, all in `LightRunners.Core` namespace)
- `IMatchSession` — match FSM front: `MatchState State`, `float TimeRemaining`, `bool IsHostAuthority`, `event Action<MatchState> StateChanged`. Plus `BeginMatch()/EndMatch()`.
- `ILumenScoreboard` — `int GetLumens(string playerId)`, `string LeaderPlayerId`, `event Action<string,int> LumensChanged`, `event Action<string> LeaderChanged`, `CrashTier GetCrashTier(string playerId)`.
- `IGateDirector` — `int ActiveGateCount`, `event Action<GateId,GeoPoint,GatePlacement> GateSpawned`, `event Action<GateId> GateDespawned`, `event Action<GateId,string> GateCollected`.
- `ILightfieldVolume` — `bool IsInside(GeoPoint)`, `event Action<string> BoundaryViolated`.
- `IMatchTransport` — the seam that replaces `GameManager`'s direct `FusionLauncher` coupling: `bool IsConnected`, `event Action<bool> ConnectionChanged`, `void ConnectMatch(string roomId, string playerId)`, `void Disconnect()`.
- `IMatchReplaySink` — captures events for Afterglow: `void RecordLumen(string player, GeoPoint at, double t)`, `void RecordCrash(string player, GeoPoint at, double t)`, `void RecordTrailSnapshot(...)`.
- `ITailAuthority` — `float FrozenTailRadius { get; }`, `void FreezeAtCountdown()`, `bool IsFrozen { get; }`.

### 0.5 GameEvents additions (`Core/GameEvents.cs` — append only)
Add: `MatchStateChanged(MatchState)`, `LumensChanged(playerId, newTotal)`, `LeaderChanged(leaderId)`, `GateCollected(gateId, playerId)`, `GateSpawned`, `GateDespawned`, `BoundaryViolated(playerId)`, `MatchExpired`. **Do not remove** existing events.

### 0.6 Null/stub implementations in Core (`Core/NullMatchServices.cs` — new file)
For each interface in 0.4, provide a no-op `Null*` implementation (mirrors the existing `NullLobbyService` pattern at `Backend/LobbyServices.cs`). These get registered by default so editor-only playmode still compiles and runs without Fusion/Backend.

### 0.7 Delete RunScorer (per your decision)
- Delete `Assets/LightRunners/Trail/RunScorer.cs`.
- Delete `Assets/LightRunners/Tests/RunScorerTests.cs` (16 tests).
- Leave the call-sites in `Gameplay/RunSummaryUI.cs` and the `record_run` RPC for Track E / Track D to replace — note them with `// TODO(lumen-scoreboard):` comments so the tracks can grep for them.
- Do NOT touch `Supabase/schema.sql` in Phase 0 — that's Track E.

### 0.8 Wire Phase 0 contracts into ServiceLocator
Update `Gameplay/PlatformServiceRegistry.cs` to register `NullMatchSession`, `NullLumenScoreboard`, etc. by default. This is the one `Gameplay/` file Phase 0 touches; it's append-only.

### 0.9 Verify Phase 0
- Confirm project still compiles (Unity's compile is the test — there's no CI and no git history yet, so this is a manual checkpoint).
- Confirm EditMode tests still run (TrailDataTests, CollisionMathTests, CoordinateConverterTests, TrailSnapshotTests — minus the deleted RunScorerTests).
- Commit Phase 0 on `main`. Push the `v0-spec-v1` and `v1-lightfield-foundation` tags.

---

## Parallel Tracks (background agents, after Phase 0 merges)

Each track branches from `main` post-Phase-0. Each owns a disjoint asmdef/folder. **No track edits a hot-spot file owned by another track.** Hot-spots and their owners:

| Hot-spot file | Owner track |
|---|---|
| `Core/GameConfig.cs`, `GameEvents.cs`, `Enums.cs`, `ServiceLocator.cs`, new Core interfaces | **Closed after Phase 0** — no further edits |
| `Core/BeaconFormData.cs` | untouched |
| `Gameplay/GameManager.cs` | Track D only |
| `Gameplay/PlatformServiceRegistry.cs` | closed after Phase 0 |
| `Editor/SceneSetup.cs`, `PrefabSetup.cs` | Track G only |
| `Tests/LightRunners.Tests.asmdef` | **no track edits** — each track creates its own `LightRunners.Tests.<X>` asmdef |
| `SPEC.md`, `README.md` | Track G merges everyone's doc updates at the end |

### Track A — Trail / Lumen / Collision (branch `track/trail-lumen`)
**Owns:** `Assets/LightRunners/Trail/` (all files), new `Tests/LightRunners.Tests.Trail.asmdef` + `Tests/Trail/` test files.
**Implements decisions:** B (energy-dependent Snake tail), D (universal collision grace), E (Lumen unit), F (crash penalty + stealable pickups), N (subdivide long sweeps), T (tail radius frozen at countdown, capsule-chain geometry, collision derives from radius).
**Key new classes:**
- `Trail/LumenScoreboard.cs` — implements `ILumenScoreboard`. Pure C#. Per-player `Dictionary<string,int>`. `Award(playerId)` on Gate collect. `ApplyCrashPenalty(playerId)` uses `GetCrashTier` (leader = the player with the max Lumens; ties → no leader penalty) and spawns a stealable-pickup record.
- `Trail/SnakeTailModel.cs` — energy-budget finite-length rule (decision B). Replaces the fixed `maxTrailPoints` cap with `maxSegments = floor(energyBudget / segmentCost)`. Oldest segment dissolves on advance. Preserves the `TotalLength` accumulator (pitfall #18 invariant stays).
- `Trail/TailGeometry.cs` — capsule-chain tube built from `tailRadius` (replaces flat LineRenderer ribbon in `NeonTrailRenderer.cs`). `FrozenTailRadius` lives here via `ITailAuthority`.
- Extend `TrailCollisionDetector.CheckCollision` — derive threshold from `tailRadius·2`; add `SubdivideSweep(prevPos, playerPos, maxStep)` so long teleports/vehicle moves are tested segment-by-segment (decision N).
- Tests: `LumenScoreboardTests` (leader detection, crash tier, cap-by-held-score), `SnakeTailModelTests` (energy prune, accumulator invariant), `SweepSubdivisionTests`.

### Track B — Lightfield volume + Lumen Gates (branch `track/lightfield-gates`)
**Owns:** new folder `Assets/LightRunners/Lightfield/` + new asmdef `LightRunners.Lightfield` (references Core, Location). New `Tests/LightRunners.Tests.Lightfield.asmdef`.
**Implements decisions:** G (Gate hemisphere viz + tunable radius/ratio), K (Lightfield boundary), L (spherical trigger, half-buried ground / aerial stub), M (density formula + respawn).
**Key new classes:**
- `Lightfield/LightfieldVolume.cs` — implements `ILightfieldVolume`. Ground-only milestone: boundary = circular disc of radius `lightfieldBaseRadiusMeters` around match origin + a hard altitude ceiling (`lightfieldDomeCeilingMeters`). Crossing either fires `BoundaryViolated`. Full dome math deferred (aerial).
- `Lightfield/GateSpawner.cs` — implements `IGateDirector`. `activeGateCount = max(1, ceil(playerCount × gatesPerPlayer))`. Maintains active gate pool; on `GateCollected`, respawn elsewhere inside volume. Altitude placement = ground for milestone (decision L's altitude-band logic stubbed).
- `Lightfield/LumenGate.cs` (MonoBehaviour) — single spherical trigger volume; visualized as hemisphere when ground-anchored. Fires `GateCollected` on trigger enter by a runner.
- `Lightfield/StolenLumenPickup.cs` — spawn-on-crash pickup (consumed by Track A's scoreboard via the bus).
- Tests: `GateDensityTests` (formula table incl. `ceil` edge cases at 1/2/3/7 players), `LightfieldBoundaryTests` (inside/outside/on-edge for disc and ceiling).

### Track C — Networking: Fusion Host Mode (branch `track/host-mode-networking`)
**Owns:** `Assets/LightRunners/Multiplayer/` (3 files), new `Tests/LightRunners.Tests.Multiplayer.asmdef` (test file compiles empty without `FUSION_WEAVER`, gated like the source).
**Implements decisions:** Q (Host Mode + host authority + referee as validated command client), R (referee role enum + validation stub — full Gate-Director UI deferred to v2).
**Constraint:** Does NOT touch `Gameplay/GameManager.cs` — communicates through `IMatchTransport` from Phase 0. Track D consumes the transport.
**Key changes:**
- `Multiplayer/FusionLauncher.cs` — `GameMode.Shared` → `GameMode.Host` (`TryStart`, currently `:83`). `Connect` → `ConnectMatch` (implements `IMatchTransport`). Room creator gets State Authority on the match NetworkObject.
- `Multiplayer/NetworkPlayer.cs` — `IsLocalAuthority` semantics change from `Object.HasStateAuthority` to "host && this is the host's avatar" (`:33`). Host owns the authoritative `LumenScoreboard` state; clients send Gate-collect / crash RPCs to host; host validates and applies.
- `Multiplayer/NetworkTrailSync.cs` — keep batched trail replication, but tail radius is now part of the match state object (frozen at countdown by host).
- `Multiplayer/RefereeClient.cs` (new) — `PlayerRole.Referee` connection; validates referee commands against a host-issued token; only Gate-Director v2 RPCs land here later (stubbed).
- Tests: limited (Fusion types don't compile without the SDK); write the pure-validation logic in a separable `RefereeTokenValidator` static class and test that.

### Track D — Match orchestration + HUD + AR-primary (branch `track/match-orchestration`)
**Owns:** `Assets/LightRunners/Gameplay/` (all files except `PlatformServiceRegistry.cs` which is closed) + the empty `Assets/LightRunners/UI/` folder + new `Tests/LightRunners.Tests.Gameplay.asmdef`.
**Implements decisions:** H (AR primary + radar), I (FPS indicators + leader crown), O (host-tunable match timer), the integration of A/B/E/F/M/P into a coherent match FSM.
**Key new/changed classes:**
- `Gameplay/MatchManager.cs` (new) — implements `IMatchSession`. FSM: `Idle→Warmup→Countdown→Live→Scoring→Expired`. Owns the `matchDurationSeconds` clock, fires `MatchStateChanged`, queries `ILumenScoreboard` for leader. Countdown freezes tail radius via `ITailAuthority.FreezeAtCountdown()` (decision T).
- `Gameplay/GameManager.cs` (refactor) — strip out match-like concerns (voluntary EndRun, run-end-as-crash); delegate match lifecycle to `MatchManager`. Keep: app-lifecycle/pause (spec §20), ServiceLocator wiring, location-start, fallback collision detector. Replace direct `FusionLauncher` calls (`:283, :296, :494`) with `IMatchTransport` from the locator. Crash no longer terminal — instead `MatchManager` respawns the player after the penalty.
- `Gameplay/RunSummaryUI.cs` (refactor) — delete the 4-axis ScoreBreakdown display (Track E dropped the schema); show Lumens, rank, leader. Grep-and-replace all `RunScorer`/`ScoreBreakdown` references left by Phase 0.7.
- `UI/TacticalRadar.cs` (new, in currently-empty `UI/` asmdef) — small corner widget that expands while the player is stopped (decision H). Separate from `OSMMinimapView` to avoid touching Map/.
- `UI/OffScreenIndicator.cs` + `UI/LeaderCrown.cs` (new) — FPS-style screen-edge arrows pointing at off-screen players with identity/distance/above-below; crown on current leader (decision I).
- `Gameplay/ViewModeBootstrap.cs` (new) — default `ViewMode = AR` instead of `Map` (decision H).
- Tests: `MatchFsmTests` (state transitions), `TacticalRadarExpandTests` (stopped vs. moving).

### Track E — Backend schema: drop RunScorer, add match tables (branch `track/backend-match-schema`)
**Owns:** `Assets/LightRunners/Backend/`, `Supabase/`, new `Tests/LightRunners.Tests.Backend.asmdef`.
**Implements:** Removes RunScorer persistence; adds match-result persistence (needed by decisions E/O).
**Key changes:**
- `Supabase/schema.sql` — migration: drop `distance/speed/beauty/proximity/total` columns from `run_history`, add `lumens INT NOT NULL DEFAULT 0`. New tables: `matches (id, room_id, started_at, ended_at, duration_seconds, winner_player_id)`, `match_players (match_id, player_id, lumens, finish_rank, role)`.
- `Supabase/schema.sql` RPCs — replace `record_run` with `record_match_result(p_match_id, p_player_id, p_lumens, p_rank)`. Add `create_match`, `finalize_match`.
- `Backend/TrailRepository.cs` + `Backend/PlayerRepository.cs` — update to new schema. Remove `ScoreBreakdown` from the persistence path.
- Tests: schema is SQL; the test class can validate the migration's idempotency by running it twice against an in-memory Postgres stub (or skip if no Postgres available — document as human-checkpoint like the existing schema tests).

### Track F — Afterglow Overview (branch `track/afterglow-overview`)
**Owns:** new folder `Assets/LightRunners/Afterglow/` + new asmdef `LightRunners.Afterglow` (references Core, Trail, Location). New `Tests/LightRunners.Tests.Afterglow.asmdef`.
**Implements decision U** (ground-only milestone ships Overview only; Walk-Inside stubbed).
**Key new classes:**
- `Afterglow/ReplayPackage.cs` — serializable record of one match: trail snapshots (per-player, frozen at expiry), Lumen-collect events, crash events. Populated by `IMatchReplaySink` (Phase 0 interface).
- `Afterglow/OverviewCameraController.cs` — top-down orthographic camera that frames the completed trail art (decision A's "completed = art" axis).
- `Afterglow/AfterglowViewController.cs` — implements `IARViewController`-like contract; switching Overview↔(stubbed)WalkInside preserves selected trails (decision U).
- `Afterglow/WalkInsideStub.cs` — placeholder for aerial-unlock follow-up; logs "Walk-Inside unlocks after aerial milestone" (decision S).
- Tests: `ReplayPackageTests` (event ordering, trail-snapshot finalization).

### Track G — Editor: scenes + prefabs + docs (branch `track/editor-scenes`)
**Owns:** `Assets/LightRunners/Editor/` (all 4 files). **Runs LAST** — serializes everyone's scene/prefab needs.
**Key changes:**
- `Editor/SceneSetup.cs` — add to Game.unity: `MatchManager`, `TacticalRadar`, `OffScreenIndicator`/`LeaderCrown` canvas, `GateSpawner`, `LightfieldVolume`, `AfterglowViewController`. All via the existing `TryAddType` reflection pattern so it compiles regardless of which tracks have merged.
- `Editor/PrefabSetup.cs` — generate `Resources/Gates/LumenGate.prefab` and `Resources/Gates/StolenLumenPickup.prefab` (idempotent, like the existing beacon-prefab path).
- `Editor/SetupValidator.cs` — check new GameConfig fields are non-zero, new prefabs exist, MatchManager present in scene.
- Update `SPEC.md` (add Lightfield section, mark RunScorer removed, document Host-Mode divergence from §8.1) and `README.md` (new menu items, new validate rows).

---

## Integration order (serial, after parallel tracks complete)

1. Merge **Track A** (Trail/Lumen) — pure C#, lowest risk; establishes `LumenScoreboard` and `ITailAuthority` impls everyone else consumes.
2. Merge **Track E** (Backend schema) — independent of A but pairs with the Lumen model.
3. Merge **Track B** (Lightfield/Gates) — new asmdef, no conflicts.
4. Merge **Track F** (Afterglow) — new asmdef, no conflicts.
5. Merge **Track C** (Host-Mode networking) — Multiplayer only; depends on Track A's scoreboard authority model.
6. Merge **Track D** (Match orchestration) — owns GameManager; consumes A/B/C/E; largest integration risk.
7. Merge **Track G** (Editor scenes + docs) — depends on all of the above; final compile gate.

After each merge: re-run EditMode tests for the merged track's asmdef. Final step: open Unity, regenerate scenes via `Light-Runners → Setup → Generate All Scenes`, run full EditMode suite, run editor playmode once to confirm the loop boots.

---

## Explicitly deferred (decision S — aerial milestone follow-up)

- True 3D hemispherical dome collision (Track B stubs a hard ceiling).
- Aerial flight participation + altitude-band Gate placement.
- Referee Gate-Director UI v2 (Track C ships the role + token validation; placement UI deferred).
- Afterglow Walk-Inside AR view (Track F ships Overview only).
- Phone-altitude-sensing validation — explicitly a human/device checkpoint per spec §18 phase 12.

## Risks I'll flag during execution
- **Fusion Host-Mode rewrite (Track C) is the highest-risk track** — it diverges from SPEC.md §8.1 (which says Shared Mode) and can't compile-test without the paid Fusion SDK. I'll keep all Fusion changes behind `#if FUSION_WEAVER` and isolate pure validation logic into testable statics.
- **GameManager refactor (Track D)** is the merge bottleneck — Track D must be merged after C and is the most likely to surface integration surprises. I'll budget a rework cycle there.
- **No CI / no live Unity in this environment** — compile verification is a manual human step. I'll deliver code that matches existing conventions and pitfall guards; Unity batchmode test runs are the user's call.