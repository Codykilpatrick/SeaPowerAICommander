# CLAUDE.md

Working notes for agents on this repo. The READMEs explain the design; this file is the
things that will cost you an hour or break something if you don't know them.

## What this is

A force-level AI for Sea Power, driven by an LLM. Three projects, three target frameworks:

| | TFM | Role |
|---|---|---|
| `contract/` | netstandard2.0 | shared DTOs — referenced by **both** sides |
| `mod/` | net472 | the BepInEx plugin, runs inside the game |
| `sidecar/` | net8.0 | out-of-process brain, calls OpenRouter |

Three TFMs because Unity's Mono cannot host a modern HTTP/JSON stack. The contract exists
so the wire format and the LLM's JSON schema cannot drift away from what the executor can
actually carry out.

The repo is `Codykilpatrick/SeaPowerAICommander` (public). Cody owns it.

> The local folder is still named `SeaPowerForceAI`. That is just the directory — nothing
> in git depends on it.

## Build

```bash
dotnet build
```

Steam library elsewhere: `-p:SeaPowerDir="D:\Steam\steamapps\common\Sea Power"`.
Skip deployment: `-p:DeployToGame=false`.

**Baseline is 0 warnings, 0 errors.** Anything else is yours — fix it before committing.

Two traps:

- **`dotnet` is not on the inherited PATH** in a fresh non-interactive shell. Prepend it:
  ```powershell
  $env:Path = [Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [Environment]::GetEnvironmentVariable("Path","User")
  ```
- **`$(SeaPowerDir)` supplies the reference assemblies**, not just the deploy target.
  Pointing it at an empty scratch directory breaks the build rather than skipping the
  copy. Use `-p:DeployToGame=false` for that.

## Deployment is NOT BepInEx/plugins

A build deploys to `Sea Power_Data/StreamingAssets/SeaPowerAICommander/`, because Anchor
Chain finds mods by scanning `FileManager.Directories` — the game's own mod search paths.
A dev build therefore installs exactly like a Workshop item and appears in the mod menu.

The folder name comes from `ModDeployDir` in `Directory.Build.props`. **If you change it,
delete the old folder.** Anchor Chain registers every StreamingAssets subfolder holding an
`_info.ini`, so a leftover one loads a *second copy* of the mod: two Harmony patch sets on
the same hook, two plugins booting, double the decisions and double the API spend. This
has already happened once, via the project rename.

## Sea Power cannot hot-reload code mods

Toggling the mod in the menu only reloads the scene. Loading or unloading the plugin needs
the game fully closed and reopened — when disabling, too. A prompt change needs only a
sidecar restart, which is one of the reasons the brain is out of process.

Practical effect: **you cannot iterate on `mod/` quickly.** Prefer changes to `sidecar/`
when there is a choice, and batch mod-side changes.

## Three rules that must not be broken

### 1. The picture is detection-limited by construction

`TacticalPicture` is built **only** from `Taskforce.PlottingTable` — never
`ObjectsManager._listOfAllObjects`. That is the whole reason a brain fed this type cannot
cheat the way the game's own air-strike pipeline does
(`AirStrikeStates/AssigningAircraft.cs:93` reads the global list directly).

Populating any field here from a global object list silently destroys the guarantee, and
no test will catch it. The one acknowledged exception is `LaunchAirstrike`, which runs the
game's own strike pipeline and does see ground truth.

### 2. Never rename a `Force` identifier that is not the product name

The game models AI in three layers — **Force / Group / Unit** — and choosing the empty
*Force* layer is the entire premise. So `ForceOrder`, `ForceOrderKind`, `IForceBrain`,
`ForceBrainTick` and "force-level" in prose are domain vocabulary and stay.

`SeaPower.TaskForceAI` is **the game's own class and the Harmony patch target**. Renaming
it breaks the mod outright.

### 3. Briefing attribution — `BriefingIsOwnSide`

`MissionManager.Objectives` are authored **for the player**. Which makes them two
completely different things depending on who is being commanded:

- Driving the **enemy**: they are the opposing plan. Infer a posture against them, never
  hand them over as intelligence, never mirror down to specifics.
- Driving the **player's own delegated fleet**: they *are* this force's orders. Adopt them.

`TacticalPicture.BriefingIsOwnSide` carries which, and `ObjectiveDeriver` switches between
two prompts on it. Get it backwards and the fleet pursues roughly the opposite of its
mission while looking like it is merely reasoning badly.

This was a real bug. The field now called `MissionBriefings` used to be
`OpposingObjectives` — a name that was true only while the commander drove the enemy
exclusively, and the rename is part of the fix. **Do not reintroduce a name that asserts
whose a briefing is.** The mission format numbers opening messages by task force
(`Taskforce1StartMessage=`) but nothing maps a number onto the force being commanded.

## The game assumes a human is driving — and quietly undoes orders

**Read this before debugging any "the order did not take" report.**

Sea Power is full of autonomy gated on flags the player's own UI sets. A mod that writes
the state without the flag gets silently overridden, usually within a tick, and the order
looks accepted the whole way. Delegating the player's force means finding each gate, and
there is **no single fix** — each wants a different answer:

| Mechanism | Symptom | Right response |
|---|---|---|
| `CheckForPlayerAbort` | any `IsPlayerObject` unit reverts weapons Free→Tight on reaching a waypoint | **suppress** — `PlayerAbortGuard`, scoped to delegated forces only |
| `Submarine.ApplyAiTransitSpeed` | boat re-picks its own speed every tick | **claim the flag** — set `_hasExplicitSpeedOrder`, as the game's waypoint task does |
| Aircraft AI states (`MPA`/`CAP`/`Intercept`/`AEW`) | waypoints wiped; routes rebuilt from `SetRelativeToStationWaypointTask` | **refuse** — `MoveTo` is rejected for air units |
| `Vessel` `PerformingAirOps` | launching carrier ignores speed AND course | **report it** — the game is right; say so and don't fight it |
| `Winchester` | non-player aircraft drops to Hold | **nothing** — it is out of ordnance, which the reach fields already show as 0 |

Two traps worth naming:

- **`ObjectBase.setPlayerCommandOverride` is not the fix for aircraft.** It looks like
  one. `AircraftStates/PlayerOverride` flies the aircraft by `Input.mousePosition`, and at
  priority 1 it also suppresses bingo-fuel and missile evasion.
- **Reach fields count only ordnance still aboard.** `airDefenceReachNM == 0` on a fighter
  means *empty*, not *incapable*. That signal sat unused in every picture for a whole
  session because nothing said what it meant.

Do not diagnose these from the decompile alone. Several confident readings were wrong —
Winchester for player aircraft, formation speed caps, the ammunition gate on launches.
**Read a saved picture instead** (see Testing): it shows exactly what the model got.

## Patching the game's UI

The game's context menu is built imperatively in C# and its XAML is baked into Noesis
assets, so the only way in is a Harmony **Postfix appending** to the collection
`GetContextMenuItems` returns. See `mod/src/UI/DelegationMenu.cs`.

- **Append, never insert at an index.** The game's own entries shift between updates.
- **Build the entry or omit it — never grey it out.** `ContextMenuItem`'s enabled state is
  a `ReadOnlyReactiveProperty` fixed at construction. A row that silently does nothing is
  worse than no row.
- Use the raw-string `ContextMenuItem(label, items, command)` ctor, **not** the
  `(section, key)` one — that resolves through the game's locale files and renders
  `MISSING TEXT - [Section]Key` for anything not in them.
- Some of the game's own menu entries mutate units through captured fields rather than any
  patchable method (`UnitObject._weaponStatus`, `_objectBase._playerCommandOverride`), so
  **no order gate can observe them.** Don't build a design that depends on detecting every
  player action.

Harmony targets written as `typeof(X)` + `nameof(X.Y)` are compiler-verified, so a wrong
target fails the build. Prefer that over string targets.

## Delegation is a task-force concept

`DrivePlayerTaskforce` is one boolean, read at the single Player-side guard in
`ForceBrainTick`. There is deliberately no per-unit delegation: the commander builds one
picture and takes one decision per force, so per-unit would cost the same per cycle while
being much harder to reason about.

It is read fresh every tick, so it can be flipped at runtime — that is what the right-click
menu does.

## Cost is a design constraint

One OpenRouter call per AI task force per decision. Delegating the player's fleet doubles
it. `TickIntervalSeconds` is **game** time while `MinRequestGapMs` is **wall-clock**, and
that gap is the only thing stopping time compression multiplying the bill — at 10× a
60-second tick fires every 6 real seconds.

Before adding anything that runs per decision, count what it costs per battle.

## Failure must stay non-fatal

If the sidecar is down or a cycle fails, the plugin logs a warning and the game's own
tactical AI keeps fighting. Keep it that way — nothing here should be able to break a
mission. `HttpBrain` also drops a submission while one is in flight: a stale naval picture
is worse than none.

`IForceBrain` is `Submit` / `TryTakeOrders`, never a blocking `Decide()`. Implementations
must be safe to call from the Unity main thread and must not block in either method.

## Model output is never trusted

`OrderSchema.Build()` generates the JSON schema by reflecting over `ForceOrderKind` — the
same enum `OrderExecutor` switches on — so the model cannot emit an order the executor has
no way to carry out. The executor still validates every order against the live task force:
a bad unit id is dropped with a log line, never thrown.

### Adding an order kind touches FOUR places

1. `ForceOrderKind` — the enum the schema's kind list generates from.
2. `ForceOrderKinds.IsPositionDependent` — classify it, or it defaults to perishable.
3. `OrderExecutor.ApplyOne` — implement it; keep validation in the executor.
4. **`OpenRouterClient.Parse`** — read your new field out of the model's response.

Step 4 is the one that gets forgotten, because the other three sit near each other and it
does not. `Parse` hand-maps every property by name, so a field missing from it arrives
null however correct the schema is. This has already shipped twice: `SetEmcon` and
`LaunchAircraft` both went live with their fields in the contract, the schema AND the
executor, and every single order was refused with an empty value until `Parse` caught up.

Update `OrderExecutor.Describe` too, or the log prints a bare kind with no parameters -
which is how you end up unable to tell `SetEmcon Silent` from `SetEmcon Radiate` in a
post-mortem.

Prefer reusing an existing field to adding a fifth. `salvo` already means "how many" and
is wired through all four places.

## Testing

**There is no test suite.** Verification is a build plus a live mission. When you change
anything mod-side, say plainly in your report that it has not been run in game unless you
have actually run it.

Useful signals in a live run:

- BepInEx log at boot: `Sea Power AI Commander <version> booted`.
- Sidecar console prints `=== Restated objective ... (own briefing) ===` when driving the
  player's fleet, or `=== Derived objective ... ===` when driving the enemy. The wrong one
  means briefing attribution is broken.
- `Debug.LogUnitState` (on by default) gives one readable line per unit and contact per
  decision — far easier to read than `DumpPictureJson`. Note it does NOT print roles or
  reach for every field, so it is a summary, not the payload.
- **Every picture sent is saved** to `sidecar/bin/Debug/net8.0/pictures/picture-*.json`.
  This is the best debugging tool in the repo and the only way to see what the model
  actually received. Reach for it before theorising — it settled three wrong diagnoses in
  one evening. PowerShell reads it in one line:
  ```powershell
  (Get-Content picture-XXXX.json -Raw | ConvertFrom-Json).ownUnits | Select-Object name,roles,damagePercent
  ```
- `orderProblems` in the picture is the previous cycle's verify pass — the mod re-checks
  each accepted order against live state and tells the commander what did not take. A
  repeated entry means the commander is not reading it, or the cause is not in the picture.

The sidecar handles decisions **concurrently**, one task per request. It needs no locking:
the mod already allows one decision in flight per task force (`HttpBrain._inFlight` is
per-instance, one brain per `TaskForceAI`), and spend is bounded by its static rate gate.
Awaiting in the accept loop instead cost 15 timeouts in one battle once three task forces
were being driven — each request waited for the previous decision before it even started.

Config lives at `BepInEx/config/com.codykilpatrick.aicommander.cfg`, generated on first
run. Changing the plugin GUID renames that file and silently resets everything to
defaults.

## Conventions

- Comments explain **why**, especially where the code looks wrong but isn't. Match that
  density; it is the house style and most of it is load-bearing.
- Commit messages are prose explaining the problem and the reasoning, not a changelog.
- Git identity is **not set globally** on this machine — commits fail with "Author identity
  unknown". Both Sea Power repos set it locally as
  `Cody Kilpatrick <ckilpatrick@spear.ai>`. Set it per-repo, never globally.
- The API key lives only in `OPENROUTER_API_KEY` in the environment. It is never read by
  the mod and must never reach the repo. Sidecar env vars are `AICOMMANDER_*`.
