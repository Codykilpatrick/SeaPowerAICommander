# sidecar

`net8.0` — the out-of-process brain. A loopback HTTP listener that takes a tactical picture
and answers with orders, calling an LLM through OpenRouter in between.

## Why out of process

Three reasons, in order of how much they cost to work around:

1. **Unity's Mono runtime is hostile to modern BCL dependency trees.** Getting a current
   HTTP/JSON stack loading cleanly inside the game is a fight you do not need to have.
2. **The API key stays out of a distributed mod.** It lives in your environment, on your
   machine, and is never read by the plugin.
3. **Prompts can be iterated without restarting the game.** Sea Power cannot hot-reload
   code mods — a plugin change means a full close-and-reopen. A prompt change means
   `Ctrl+C` and `dotnet run`.

## Run it

```bash
setx OPENROUTER_API_KEY "sk-or-..."     # from https://openrouter.ai/keys, then reopen your terminal
dotnet run --project sidecar
```

Then set `Brain.Type = Sidecar` in `BepInEx/config/com.codykilpatrick.forceai.cfg` and
start the game. Order does not matter — the plugin treats a missing sidecar as a
non-fatal warning.

| Env var | Default | Meaning |
|---|---|---|
| `OPENROUTER_API_KEY` | *(required)* | Your key. Never committed, never read by the mod. |
| `FORCEAI_MODEL` | `anthropic/claude-opus-4.5` | Any OpenRouter model id supporting structured outputs. |
| `FORCEAI_PREFIX` | `http://127.0.0.1:8787/` | Listener address. Loopback by design. |
| `FORCEAI_TIMEOUT_SECONDS` | `150` | Per-decision ceiling on the OpenRouter call. |

The listener binds `127.0.0.1` deliberately: this endpoint spends an API key's worth of
money per request and must not be reachable off the machine.

There are **two** timeouts on this path — the mod waiting on the sidecar
(`SidecarTimeoutMs`, 150s) and the sidecar waiting on OpenRouter (`FORCEAI_TIMEOUT_SECONDS`,
150s). Raising only one leaves decisions still being abandoned at the other. Change both.

## Files

| File | Job |
|---|---|
| `Program.cs` | listener, env config, request loop, tee'd logging |
| `CommanderPrompt.cs` | the system prompt — where behaviour actually gets tuned |
| `OpenRouterClient.cs` | the API call, structured-output request, response parsing |
| `OrderSchema.cs` | builds the JSON schema from `ForceOrderKind` |
| `ObjectiveDeriver.cs` | infers what this force is trying to achieve, once per mission |

## The schema is generated, not written

`OrderSchema.Build()` reflects over `ForceOrderKind` — the same enum `OrderExecutor`
switches on. Add a kind in [`contract/`](../contract/README.md) and the model's vocabulary
widens automatically, so the schema and the executor's capability cannot drift apart.

The executor still validates every order against the live task force. Model output is never
trusted: a bad unit id is dropped with a log line, not thrown.

## Objective derivation

A commander with no objective optimises for survival, and withdrawal is always the right
answer to that — which is exactly what one does, every cycle, however good its tactical
reasoning. An objective is what makes risk worth taking.

The mission's own objectives are authored for the player, so they cannot simply be handed
over. `ObjectiveDeriver` reads the neutral mission description and the *opposing* side's
briefing once, infers what this side's orders would plausibly have been, and caches the
result per mission — a commander whose mission changed between cycles would be incoherent.

The derived objective is a posture, never mirrored down to specifics like unit names or
positions this side would have no way to know. Set `Brain.ForceObjective` in the mod config
to skip derivation and state it yourself.

## Logging

Everything the console prints is also written to `logs/sidecar-<timestamp>.log` next to the
binary. The assessments are the only place the commander's reasoning is visible, and reading
them back should not depend on having had the terminal open at the time.

## Cost

One call per AI task force per tick. At the default 60-second tick, a 30-minute battle with
two AI task forces is roughly 60 calls. Dropping the tick to 10 seconds multiplies that by
six for responsiveness you mostly will not notice at this altitude.

`MinRequestGapMs` in the mod config is the wall-clock floor between requests and is what
stops time compression multiplying the bill.

## Tuning

`CommanderPrompt.cs` is where behaviour lives. Iterating on it needs only a sidecar restart.
Read a run's log first — most bad behaviour traces to the commander lacking a fact or an
action, not to it reasoning badly about what it had.
