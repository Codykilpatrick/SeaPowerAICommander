# mod

`net472` — the BepInEx plugin that runs inside the game.

It adds a force-level decision layer to an empty hook the developers left wired up, builds
a detection-limited picture of the battle, hands it to a brain, and carries out whatever
comes back.

## The hook

`SeaPower.TaskForceAI` is constructed per task force (`Taskforce.cs:222`) and its
`OnUpdate` is called every frame (`Taskforce.cs:332`) — with an empty body. This plugin
Harmony-**postfixes** that method, so it *adds* behaviour rather than replacing any. There
is no incumbent logic to conflict with, which is why this hook was chosen over the
4,250-line unit-level `SeaPower.AI`.

The postfix is throttled to `TickIntervalSeconds` (60 game-seconds by default) rather than
running per frame. Force-level intent — where groups go, what posture to hold — changes on
a scale of minutes, so a one-minute command cycle is realistic rather than a compromise. It
also keeps a network-backed brain affordable.

## Flow

```
TaskForceAI.OnUpdate  (Harmony postfix, throttled)
    │
    ├── PictureBuilder ── Taskforce.PlottingTable ──► TacticalPicture
    │                     (own contacts only, never ground truth)
    │
    ├── IForceBrain.Submit(picture)         non-blocking
    ├── IForceBrain.TryTakeOrders(out set)  polled each tick
    │
    └── OrderExecutor ──► validate ──► ObjectBase commands
```

## Files

| File | Job |
|---|---|
| `src/Plugin.cs` | BepInEx entry, config, `CreateBrain()` |
| `src/ForceBrainTick.cs` | the postfix, throttling, order ageing, per-taskforce state |
| `src/Picture/PictureBuilder.cs` | plotting table → `TacticalPicture` |
| `src/Orders/OrderExecutor.cs` | validate and apply a `ForceOrderSet` |
| `src/Orders/AttackScheduler.cs` | holds `CoordinatedAttack` orders until the group is ready |
| `src/IForceBrain.cs` | the brain interface |
| `src/ObservingBrain.cs` | default — logs, orders nothing |
| `src/HttpBrain.cs` | POSTs the picture to the sidecar |
| `src/AnchorChainEntry.cs` | Anchor Chain mod-loader entry point |

## The brain interface is async on purpose

`IForceBrain` is `Submit` / `TryTakeOrders` rather than a blocking `Decide()`. A brain
backed by a network call must never stall the game thread — it takes a picture now and
produces orders whenever it can, which may be several ticks later or never.

`IsBusy` exists so the caller can skip building a picture it cannot use. Assembling one
walks every unit and every plotting-table entry, and under time compression a single
decision spans many ticks: the first live run built 200 pictures to produce 17 decisions
and threw the rest away.

Implementations must be safe to call from the Unity main thread and must not block in
either method.

## Orders are aged before they are applied

A decision made 400 game-seconds ago may no longer describe the battle it arrives into.
`ForceBrainTick` drops orders whose originating picture is too old — on two separate
clocks, because the two kinds of order spoil at different rates. See
`ForceOrderKinds.IsPositionDependent` in [`contract/`](../contract/README.md) for the rule.

`MaxPostureOrderAgeSeconds` defaults to `0`, meaning posture orders never expire.

## Config

`BepInEx/config/com.codykilpatrick.aicommander.cfg`, generated on first run.

| Section | Key | Default | Meaning |
|---|---|---|---|
| General | `Enabled` | `true` | Master switch. The patch stays applied; the tick does nothing. |
| General | `TickIntervalSeconds` | `60` | Game-seconds between decisions. |
| General | `DrivePlayerTaskforce` | `false` | Also run the brain on your own fleet. Testing only. |
| Brain | `Type` | `Observing` | `Observing` or `Sidecar`. |
| Brain | `SidecarEndpoint` | `http://127.0.0.1:8787/` | Loopback only — do not point off-machine. |
| Brain | `SidecarTimeoutMs` | `150000` | Per-cycle ceiling. Background thread, never stalls the game. |
| Brain | `MinRequestGapMs` | `4000` | Minimum **wall-clock** gap between requests across all task forces. |
| Brain | `ForceObjective` | `""` | What the force is trying to achieve. Empty ⇒ derived by the sidecar. |
| Brain | `MaxContactsInPicture` | `25` | Latency tracks payload size, payload tracks contact count. |
| Brain | `MaxPositionalOrderAgeSeconds` | `400` | Drop stale `MoveTo`. |
| Brain | `MaxPostureOrderAgeSeconds` | `0` | Drop stale posture orders. `0` = never. |
| Debug | `LogUnitState` | `true` | One readable line per unit and contact each decision. |
| Debug | `DumpPictureJson` | `false` | Full serialized picture at debug level. Verbose. |

`MinRequestGapMs` is wall-clock while `TickIntervalSeconds` is game time — at 10x
compression the tick fires ten times as often in real seconds, and the gap is what stops
that turning into ten times the spend.

`ForceObjective` is the single biggest influence on behaviour. With no objective a
commander optimises purely for survival and withdraws every time it is outranged.

## Build

From the repo root:

```bash
dotnet build
```

Output lands in `bin/Debug/` and is copied into `BepInEx/plugins/` automatically. Skip the
copy with `-p:DeployToGame=false`. If your Steam library is elsewhere:

```bash
dotnet build -p:SeaPowerDir="D:\Steam\steamapps\common\Sea Power"
```

Every game reference is `Private=false` — those assemblies are already in the process, and
shipping copies would break type identity.

**Sea Power cannot hot-reload code mods.** Toggling the mod only reloads the scene; you
must fully close and reopen the game to load or unload it. Prompt changes need only a
sidecar restart, which is one of the reasons the brain is out of process.

## Known limits

- **Multiplayer will desync** if both clients run brains independently. The host must own
  the loop and replicate the resulting orders. `TaskForceAI.OnFixedUpdate` — also empty —
  is the deterministic-cadence hook to patch for that.
- **`LaunchAirstrike` leaks ground truth.** It runs the game's own strike pipeline, which
  sweeps up other vessels near the target from the global object list. That is the game's
  behaviour and the one place the detection-limited picture does not hold.
- **Failure is non-fatal by design.** If the sidecar is not running, or a cycle fails, the
  plugin logs a warning and the game's own tactical AI keeps fighting.
