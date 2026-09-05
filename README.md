# Sea Power Force AI

A force-level brain for Sea Power, built on the empty hook the developers left behind.

## Why this hook

Sea Power's AI has three layers. The bottom one is good; the top two are empty.

| Layer | Class | State |
|---|---|---|
| Force | `SeaPowerAI.*` | Wired, empty |
| Group | `SeaPower.TaskForceAI` | Wired, empty |
| Unit | `SeaPower.AI` | ~4,250 lines, real |

`TaskForceAI` is constructed per task force (`Taskforce.cs:222`) and its `OnUpdate` is
called every frame (`Taskforce.cs:332`) — with an empty body. This mod postfixes that
method, so it **adds** a decision layer rather than overriding one. There is no incumbent
behaviour to conflict with.

Cadence follows the game's own precedent: `Taskforce` throttles its internal `CheckAI()`
to 10 seconds (`Taskforce.cs:405`), so this ticks at 10s by default rather than per-frame.

## Design

```
TaskForceAI.OnUpdate  (Harmony postfix, throttled)
    │
    ├── PictureBuilder ── Taskforce.PlottingTable ──► TacticalPicture
    │                     (own contacts only, never ground truth)
    │
    ├── IForceBrain.Submit(picture)        non-blocking
    ├── IForceBrain.TryTakeOrders(out set) polled each tick
    │
    └── OrderExecutor ──► validate ──► ObjectBase commands
```

Two properties are deliberate:

**The picture is detection-limited by construction.** It is built from
`Taskforce.PlottingTable`, which holds the task force's own contacts with their real
uncertainty — `Identified`, `IsClassified`, `DetectingSensors`, and a null position for
bearing-only holds. A brain fed this data *cannot* cheat the way the shipped air-strike
pipeline does (`AirStrikeStates/AssigningAircraft.cs:93` reads
`ObjectsManager._listOfAllObjects` directly).

**The brain interface is async.** `Submit` / `TryTakeOrders` rather than a blocking
`Decide()`, so a brain backed by a network call can take several ticks without ever
stalling a frame.

## Build

```bash
dotnet build
```

Output lands in `bin/Debug/` and is copied into `BepInEx/plugins/` automatically.
Skip the copy with `-p:DeployToGame=false`. If your Steam library is elsewhere:

```bash
dotnet build -p:SeaPowerDir="D:\Steam\steamapps\common\Sea Power"
```

Every game reference is `Private=false` — those assemblies are already in the process,
and shipping copies would break type identity.

**Sea Power cannot hot-reload code mods.** Toggling the mod only reloads the scene; you
must fully close and reopen the game to load or unload it.

## Current state

`ObservingBrain` is the default: it logs a one-line picture summary each tick and issues
no orders. That verifies the tick fires and the picture reads correctly before anything
touches a unit. Set `DumpPictureJson = true` in the config to see the full serialized
picture a brain would receive.

Config lives at `BepInEx/config/com.codykilpatrick.forceai.cfg`.

## Extending

**Add an order type:** add to `ForceOrderKind`, implement it in `OrderExecutor.ApplyOne`,
keep validation in the executor. That enum is the whole action space.

**Write a real brain:** implement `IForceBrain`, return it from `Plugin.CreateBrain()`.

**Attach an LLM:** run it as a sidecar process, not in-process. The `Anthropic` NuGet
package does resolve for net472, but it pulls System.Text.Json 10.x, System.Memory and
Microsoft.Extensions.AI.Abstractions into Unity's Mono runtime — restoring is not the same
as running. A sidecar also keeps API keys out of a distributed mod and lets you iterate on
prompts without the full game restart a code-mod change requires. Have the brain POST the
picture to `127.0.0.1`, hold the pending request, and return orders from `TryTakeOrders`
when they arrive.

Generate the model's JSON schema from `ForceOrderKind` so it cannot emit an order the
executor has no way to carry out. `OrderExecutor` validates every order against the live
task force regardless — model output is never trusted.

**Multiplayer:** if both clients run brains independently they will desync. The host must
own the loop and replicate resulting orders; `TaskForceAI.OnFixedUpdate` (also empty) is
the deterministic-cadence counterpart to patch.
