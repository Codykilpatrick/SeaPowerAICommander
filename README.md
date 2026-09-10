# Sea Power AI Commander

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

It ticks every 60s by default, not per-frame. The game throttles its own `CheckAI()` to
10s (`Taskforce.cs:405`), but that handles finer-grained work — force-level intent changes
on a scale of minutes, so a one-minute command cycle is realistic rather than a compromise.
It also keeps a network-backed brain affordable.

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

Output lands in `bin/Debug/` and is deployed into the game as its own mod folder at
`Sea Power_Data/StreamingAssets/SeaPowerAICommander/` — **not** `BepInEx/plugins/`. Anchor
Chain finds mods by scanning the game's own mod search paths, so a dev build installs
exactly like a Workshop item and appears in the in-game mod menu. Skip the copy with
`-p:DeployToGame=false`. If your Steam library is elsewhere:

```bash
dotnet build -p:SeaPowerDir="D:\Steam\steamapps\common\Sea Power"
```

Every game reference is `Private=false` — those assemblies are already in the process,
and shipping copies would break type identity.

**Sea Power cannot hot-reload code mods.** Toggling the mod only reloads the scene; you
must fully close and reopen the game to load or unload it.

## Layout

```
contract/   netstandard2.0 - TacticalPicture, ForceOrder. Referenced by BOTH sides,
            so the wire format and the LLM schema cannot drift from the executor.
mod/        net472  - the BepInEx plugin that runs inside the game.
sidecar/    net8.0  - the out-of-process brain that calls OpenRouter.
```

Each has its own README: [`contract/`](contract/README.md) for the shared vocabulary and
the action space, [`mod/`](mod/README.md) for the in-game plugin and its config,
[`sidecar/`](sidecar/README.md) for the brain and prompt tuning.

## The LLM sidecar

Set `Brain.Type = Sidecar` in the config, then run the sidecar alongside the game.

```bash
setx OPENROUTER_API_KEY "sk-or-..."     # from https://openrouter.ai/keys, then reopen your terminal
dotnet run --project sidecar
```

| Env var | Default | Meaning |
|---|---|---|
| `OPENROUTER_API_KEY` | *(required)* | Your key. Never read by the mod or committed. |
| `AICOMMANDER_MODEL` | `anthropic/claude-opus-4.5` | Any OpenRouter model id that supports structured outputs. |
| `AICOMMANDER_PREFIX` | `http://127.0.0.1:8787/` | Listener address. Loopback only by design. |
| `AICOMMANDER_TIMEOUT_SECONDS` | `150` | Per-decision ceiling. Match it to the mod's `SidecarTimeoutMs`. |

Why out-of-process, and not the Anthropic SDK in the plugin: Unity's Mono runtime is
hostile to modern BCL dependency trees, the API key stays out of a distributed mod, and
prompts can be iterated without the full game restart a code-mod change requires.

**Cost is real.** One call per AI task force per tick. At the default 60s tick, a
30-minute battle with two AI task forces is about 60 calls. Dropping the tick to 10s
multiplies that by six for responsiveness you mostly won't notice at this altitude.

**Failure is non-fatal by design.** If the sidecar is not running, or a cycle fails, the
mod logs a warning and the game's own tactical AI keeps fighting. `HttpBrain` also drops
a submission while one is already in flight — a stale naval picture is worse than none.

**Multiplayer will desync** if both clients run brains independently. The host must own
the loop and replicate resulting orders; `TaskForceAI.OnFixedUpdate` (also empty) is the
deterministic-cadence hook to patch for that.

## Current state

`ObservingBrain` is the default: it logs a one-line picture summary each tick and issues
no orders. That verifies the tick fires and the picture reads correctly before anything
touches a unit. Set `DumpPictureJson = true` in the config to see the full serialized
picture a brain would receive.

Config lives at `BepInEx/config/com.codykilpatrick.aicommander.cfg`.

## Extending

**Add an order type:** add to `ForceOrderKind`, implement it in `OrderExecutor.ApplyOne`,
keep validation in the executor. That enum is the whole action space.

**Write a real brain:** implement `IForceBrain`, return it from `Plugin.CreateBrain()`.

**Tune the commander:** `sidecar/CommanderPrompt.cs`. Iterating on it needs only a
sidecar restart, not a game restart.

`OrderSchema.Build()` generates the model's JSON schema from `ForceOrderKind`, so the
model cannot emit an order the executor has no way to carry out. `OrderExecutor` validates
every order against the live task force regardless — model output is never trusted, and a
bad unit id is dropped with a log line rather than thrown.
