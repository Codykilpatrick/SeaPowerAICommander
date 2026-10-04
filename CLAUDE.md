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

## Which game build this is written against

**Sea Power 0.8.4 Build 261002** (Steam buildid 25677385, 2 Oct 2026). Most of what follows
was learned against 0.8.2 Build #358 and re-checked against 0.8.4 on 4 Oct 2026; where a
claim has not been re-observed *in game* since, it says so.

Confirmed by live run on 0.8.4, 4 Oct 2026 (*Sub Duel JMSDF 1985*, both forces driven):
**Anchor Chain 1.1.0 still loads the plugin** despite not having been republished since
21 Aug — it was the likeliest thing to break and it did not. Briefing attribution is
right (`(Player, own briefing)` for the delegated fleet, `Derived` for the enemy), orders
land (`SetSonar`, `SetDepth`, `IdentifyContact`, `SetWeaponStatus`), the doctrine block is
populated, and prompt caching is working (17,310 of 19,565 input tokens cached).

A decompile lives at `C:\Users\codyk\Documents\seapower-decomp`, and it is **a git repo
whose history is one commit per game build**. That is the whole point of it: after an
update, re-decompile over the top and `git diff` says exactly what moved. Decompile with

```bash
ilspycmd -p -o . "<SeaPowerDir>/Sea Power_Data/Managed/Seapower-Scripts.dll"
```

after clearing everything but `.git`, so deletions show up as deletions. The 0.8.2→0.8.4
diff is 956 files, +104k/−26k, with 195 new types.

The game's own mod-compat check is `Seapower/ModCompatibility.cs`, reading the same
`[Compatibility]` keys as our `_info.ini`. `ApproximateVersion` resolves to
`SemanticVersion.ApproximatelyEquals`: MAJOR and MINOR equal, PATCH greater-or-equal. Ours
says `0.8.0`, so it passes anything in 0.8.x and will **refuse the day the game reaches
0.9**.

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
| `Submarine.ApplyAiTransitSpeed` | boat re-picks its own speed every tick | **claim the flag** — set `_hasExplicitSpeedOrder`, as the game's waypoint task does. This is only HALF the story: see the speed row below |
| AI states writing speed directly (`Drift`, formation states, evasion) | commanded speed is not what was ordered, and `_hasExplicitSpeedOrder` did not stop it | **accept and report** — the flag only gates `ApplyAiTransitSpeed`; `SubmarineStates/Drift.cs:67,121-144` calls `setTelegraph` and `SetSpeedCommand` directly on entry. The game sets `_isSpeedCommandOverridesInfo` when a state takes the throttle and clears it on exit, so read that and say the throttle was taken rather than that the order failed. **`speedOverriddenByAi` covers only those states** — `Sprint` and anything else going through `ApplyAiTransitSpeed` leaves it false, so it is not a general answer to "did my speed order fail" |
| **Our own `MoveTo` releasing the speed claim** | ordered 5kt, boat sprints at 15kt, `speedOverriddenByAi` false, verify says "order did not take" | **re-claim every tick** — `GoToWaypointTask.cs:309` assigns `_hasExplicitSpeedOrder = _setSpeed.value`, and the waypoint task `MoveTo` creates carries no speed, so it sets the flag to FALSE and frees `ApplyAiTransitSpeed` again. Ordering within a decision cannot fix it: a standing `SetSpeed` from an earlier cycle is wiped just as well by a fresh `MoveTo`, and the game starts waypoint tasks of its own. `OrderExecutor.ReassertSpeedClaims` runs from the tick for as long as the order stands |
| Aircraft AI states (`MPA`/`CAP`/`Intercept`/`AEW`) | waypoints wiped; routes rebuilt from `SetRelativeToStationWaypointTask` | **refuse** — `MoveTo` is rejected for air units |
| `Vessel` `PerformingAirOps` | launching carrier ignores speed AND course | **report it** — the game is right; say so and don't fight it |
| `Winchester` | non-player aircraft drops to Hold | **nothing** — it is out of ordnance, which the reach fields already show as 0. As of 0.8.4 this is doctrine, not a constant: `PlanesWinchester` / `HelicoptersWinchester`, defaulting to `!isPlayer` |
| `AI.CheckForRaiseAlert` | an AI-side unit reverts EMCON Silent **and** weapons Tight, together, the moment it holds a threat — and so does everything within 10nm and every ship in its formation | **report it** — `OnAlert` in the picture; the game is right that a warship holding a threat should look and shoot, and concealment is a pre-contact option only. 0.8.4 makes it conditional: see Force EMCON below |
| `AI._excludeFromAIRadarRoutine` (Force EMCON, 0.8.4) | unit stays silent even holding a threat — and when an *observed, identified, directly inbound* weapon finally does appear, it goes active AND slams `_weaponStatus = Free`, discarding the ordered posture | **report it** — read off `ObjectBase._ai`; it is a mission-authored property (`ExcludeFromAIRadarRoutine`, also a trigger action), so `SetEmcon Radiate` on such a unit cannot stick and the commander needs to know before it spends an order |
| Submarine AI states (`Drift`/`Sprint`/`ClassifyContact`/…) | boat re-picks its depth band | **accept and report** — each state calls `setPresetDepth` on *entry*, not per tick, so an ordered band holds until the next state change; the verify pass says when it went |

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

## 0.8.4 put a doctrine layer above every order we issue

**`Taskforce.TacticalDoctrine` is a force-level control surface the game now owns.** This
is the single most consequential thing in the 0.8.3/0.8.4 update for this project, and it
cuts both ways.

The old Doctrine Panel became Standing Orders, and the model behind it is `TacticalDoctrine`
— constructed for a `Taskforce` (the root, `isRoot: true`), for a `UnitFormation`, and
lazily per `ObjectBase`. Each setting resolves up the chain through `ParentDoctrine`, and
you read the effective answer off `.ResolvedValue`. Defaults are chosen by
`ReadRootPlayerDoctrineValue(isRoot, isPlayer, …)`, so **an AI force and the player's force
start from different doctrine**, which is exactly the seam a delegated player force falls
into.

Settings that gate orders we already issue:

| Doctrine setting | What it decides |
|---|---|
| `AutoAttackSurface` | whether a unit engages surface contacts at all. Note the gate at `AI.cs:4915` no longer tests `IsPlayerObject`; it is doctrine for everyone now |
| `ShipsAutoUseAntiShipMissilesAgainstSurfaceTargets` | whether Weapons Free is enough to release ASMs |
| `ShipsAutoUseSAMsAgainstSurfaceTargets` | SAMs against surface targets |
| `ShipsOnWeaponsTightEngageHostileAircraft` | flipped to **off** by default in 0.8.3 — Tight now means more Tight than it did |
| `RemoveEngageTaskAfter` | how long an engage task survives; read before trusting any "that attack is over" reasoning |
| `PlanesWinchester` / `HelicoptersWinchester` | the Winchester→RTB behaviour, now a knob |
| `FighterRtbCondition` | default `AllAamExpended` |
| `SubmarineTransitTelegraph` / `Vessel…` / `Aircraft…` | the AI's own speed choice — the thing `_hasExplicitSpeedOrder` exists to override |
| `AllowAlignment` | the auto-align that `ObjectBase.cs:4237` checks |

`PlayerAutoAttackSurface` is **gone** — `OptionsManager` migrates it once into
`[Tactics] AutoAttackSurface` and deletes the old key. Anything looking for the old option
finds nothing and reads `false`.

**Do not predict a doctrine value from the code defaults.** `ReadRootPlayerDoctrineValue`
returns the hardcoded default unless `isRoot && isPlayer`, in which case it reads
`usersettings.ini [Tactics]`. So an AI force always gets code defaults, while the player's
force gets whatever that machine's Standing Orders panel was last set to — including the
migrated old option. This was got wrong once already: `AutoAttackSurface` has a code
default of `!isPlayer`, which reads as "off for the player", and the first live 0.8.4 run
reported `true` because the player had the old option switched on. The asymmetry is the
argument for carrying doctrine in the picture at all: the one force whose behaviour the
commander most needs to predict is the one whose doctrine the code cannot tell you.

Two consequences, and they are different:

- **Risk.** The commander issues orders into a layer it cannot see. An order that doctrine
  forbids is accepted, never carried out, and then reported by the verify pass as "did not
  take" — which reaches the commander as an instruction to try again. That is the exact
  failure mode the grace windows were built to prevent, arriving by a new route.
- **Opportunity.** This is a *supported* control surface at the Force altitude the whole
  project is premised on, and it is writable. Several rows in the table above are
  workarounds for the absence of precisely this.

The risk side is handled: `TacticalPicture.Doctrine` carries the nine settings that gate
an order the commander can actually issue, and `OwnUnit.DoctrineOverrides` names the
settings where a unit or its formation departs from the force. Overrides are found by
comparing the unit's `ResolvedValue` against the force's rather than inspecting `IsSet` —
the question is "does this unit behave differently", not "where was the box ticked" — and
the field is left **null**, not empty, when it does not, because most units in most forces
have nothing to say and the wire format drops nulls.

Two things to keep straight when extending it:

- **`AutoAttackSurface` gates `AI.GetPossibleTargetsList`, not `AI.AutoAttackByClick`.**
  Auto-engagement stops; an explicit `Attack` order still works, because that is the path
  `OrderExecutor` and `AttackScheduler` both use. So `false` does not disarm a force — it
  means the commander must name every target. Reading it as "cannot shoot" would be the
  same class of mistake as reading `airDefenceReachNM == 0` as "incapable".
- **Telegraph settings are deliberately absent from the picture.** They decide the speed
  the tactical AI picks when it has *not* been told, so an explicit `SetSpeed` overrides
  them and they gate nothing. Carrying them would cost tokens every cycle to describe a
  default the commander already overrides.

The writable side has **not** been acted on. Nothing sets doctrine; the prompt tells the
commander to work inside it.

## Orders that name a target beat orders that name a place

The counterpart to the `MoveTo` refusal above, and the thing that took longest to see:
**an aircraft cannot be given a waypoint, but it can be given a target.**

The aircraft state machine transitions on tasking fields, not on routes — `Aircraft.cs:258`
enters `IdentifySurfaceContact` off `_ai._objectToIdentify` alone, and `Aircraft.cs:318`
enters `ReturnToBase` off `CurrentOrder.OrderType` from *any* lower-priority state. Neither
is a position, and neither gets overwritten, because this is how the game means aircraft to
be tasked. `IdentifyContact` is therefore both a way to improve the picture and the only way
to put an aircraft over a chosen piece of ocean.

The mechanism differs per hull type and choosing wrong is a silent no-op, not an error —
`OrderExecutor.IdentifyContact` has the table, taken from the game's own split at
`AI.cs:397`. Two gotchas in it:

- **Submarines have no order-driven identify path at all**, and `Submarine.cs:256` gates the
  field-driven one on `!IsPlayerObject`. A delegated player boat therefore cannot be told to
  identify anything; the executor refuses rather than pretending.
- **A dormant track cannot be identified at all.** Nothing is holding it on a sensor and the
  plot shows where it WAS, so a unit sent there finds empty ocean and the state machine
  condition never resolves. This was the cause behind every identify order that was accepted
  and then silently did nothing; `IdentifyContact` now refuses them.
- **Aircraft only divert from unhurried states** (`Default`, `MPA`, `MaritimePatrol` -
  NOT `Loitering`, whatever `Aircraft.cs:266` says; four live attempts from it never
  diverted while every attempt from `Default` worked). A fighter already prosecuting an air contact ignores the order. That is what
  `currentOrder` in the picture is for, and what the verify pass checks.
- **0.8.4 rewired these transitions and they have NOT been re-observed in game.** They now
  live around `Aircraft.cs:339-370`. Three changes worth knowing before trusting the list
  above: there is a second trigger field, `_ai._objectToClassify`, wired in parallel with
  `_objectToIdentify` on every identify state; `IdentifySubSurfaceContact` is reachable from
  `Default`/`MaritimePatrol`/`Loitering` but **not** from `MPA`; and a new `IdentifyAirContact`
  state exists, reachable from `Default` only and gated on `AllowAI()`. `Loitering` is still
  wired to `IdentifySurfaceContact` — it was wired in 0.8.2 as well and did not work, so
  leave the refusal in place until a live run says otherwise.

## The verify pass must not mistake slow for broken

`orderProblems` reaches the commander, so a false report is not a cosmetic bug — it is an
instruction. Everything the verify pass checks takes TIME, and checking one cycle after
ordering produced three wrong reports in the first four cycles of a live mission.

**Every grace below was timed on 0.8.2 and none has been re-timed on 0.8.4.** Treat them
as suspect until a live run says otherwise: the update removed `IdentificationRate` from
every ESM sensor, rebuilt gunnery and CIWS, enlarged all aircraft RCS while lowering
air-search radar gain, and added the OODA layer, in which a unit's combat system has a
reaction time and a cap on how many contacts it can work at once. Detection and
prosecution timings have all moved, and the numbers here are the ones that decide whether
the commander is told the truth.

| Order | Actually landed | Reported at N+1 |
|---|---|---|
| `SetSonar DeployTowedArray` | 3 cycles (it physically streams out) | "order did not take" |
| `SetDepth AboveLayer` | 2 cycles | (nearly missed) |
| `IdentifyContact` on an F-14 | 4 cycles, reaching `IdentifySurfaceContact` | "not prosecuting it" |

The commander did as it was told every time: reassigned units that were already doing the
job, and reissued orders that had landed. `BrainState.OrderIssuedAt` plus `GraceSeconds`
now hold each check back until the thing could plausibly have happened. **When you add a
verify case, give it a grace, and take the number from an observed run rather than taste.**

**A refusal is not a verify failure, and the commander needs both.** `orderProblems` is the
verify pass over orders that were ACCEPTED and became standing. An order the executor
refuses never becomes standing, so it can never appear there — which means that unless the
refusal is routed through `OrderExecutor.Refuse`, the commander learns nothing and reissues
forever. One mission issued fifteen `IdentifyContact` orders to a delegated player
submarine, which structurally cannot take one, and four `SetSonar` towed-array orders to a
boat with no array: nineteen refused against eighteen accepted, every refusal in the log,
`orderProblems` empty the whole time. `Refuse` now logs as well as recording, so there is
no reason for a refusal path to call `Plugin.Log` directly — **if you add a `return false`
to an order handler, route it through `Refuse`**, and write the message for the commander:
what it cannot do, what to do instead, and whether the refusal is permanent.

Two specific traps inside it:

- **`CurrentOrder` is the wrong signal for an aircraft.** Ships and helicopters are tasked
  via `setOrder`, so their order slot fills at once; an aircraft is tasked by writing
  `_ai._objectToIdentify` and the *game* writes the order only once its state machine picks
  the target up. Check `AiState` for those. The identify transitions are built from
  `Default`, `MPA`, `MaritimePatrol` and `Loitering` alone (`Aircraft.cs:259-266`), so a
  formation FOLLOWER — always in `MovingInFormation` — can never take one. Task the leader.
- **A held shot looks exactly like a finished one.** `AttackScheduler` deliberately keeps
  non-farthest shooters out of `_currentEngageTasks` for the length of the stagger. Ask
  `AttackScheduler.HasPending` before calling an attack over. Not doing so told the
  commander "that attack is over - order it again", and it fired eight salvos of four at one
  contact across four cycles believing it had fired two.

Word the message so it cannot be read as an instruction to retry. "This is what a completed
attack looks like, NOT a failed one" is the point of the sentence.

## A contact field is only worth what the prompt says it is for

Three separate bugs in one mission, all the same shape: the picture carried the answer and
nothing told the commander what it meant.

- **`firstDetectedAt` was an absolute epoch described as an age.** The prompt said it "tells
  you how old a track is"; the field was the detection timestamp. The commander read it as
  an age and called a track first detected at mission second 3.8 "freshly detected, under
  10s old" at mission second 6488. It is now `trackAgeSeconds`, an actual age, because the
  name was doing half the lying.
- **`domain` was gated on `IsClassified`, which means "we know WHOSE it is"**
  (`UnitTaskforce != null`), not "we know what it is". Almost never true in ASW, so every
  contact read `Unknown`. It now comes from the altitude estimate.
- **`altitude` sat in every contact, unexplained and unused** — a merchant at 0.1m below the
  surface looked exactly like a Victor III at 150m to a commander never told the field
  existed for that purpose.

Together they cost a whole mission: a delegated submarine stalked a merchant for two hours
while the Soviet boat that had it identified sat 2nm away, unheld and unreported.

**Altitude is safe to read and rule 1 is intact.** `DetectedPosition.Estimate` is built by
`EstimatePosition(bearing, range)` with an error ellipse, and `TrackAltitude` carries a
`PointEstimate { Estimate, Error }` synthesised from the force's own sensors. It is an
estimate, not ground truth — which is also why `ReadDomain` returns `Unknown` when the error
is too wide rather than guessing, and reports the error so the commander can see why.

**Detection epochs are on `missionSessionTime`, not `missionElapsedTime`.**
`PlottingTable.cs:265` stamps `InitialEpoch = Epoch.AtLive(GameTime.missionSessionTime)`,
which resets per mission, while `TacticalPicture.TimeSeconds` is the monotonic clock that
does not. Subtracting one from the other is right on the first mission of a process and
nonsense on every one after it. The two clocks look interchangeable and are not.

## Reach is not capability — `canMountAirstrike`

The three reach fields count ordnance **the unit fires itself**. An airfield fires none, so
Andersen AFB reported `antiSurfaceReachNM 0` while holding six F-4E, two B-52G and an
`AntiShipHeavy` fit. `unitsInReach` is built from those figures, so the base appeared on no
contact's list, and the prompt tells the commander in the strongest terms that a unit not on
the list cannot hit that contact. It never launched a sortie in a whole mission and left two
destroyers to fight a Soviet SAG alone.

`OwnUnit.CanMountAirstrike` says the thing reach cannot. It is deliberately a **flag, not a
distance**: nothing in the strike pipeline compares base to target — the aircraft launch,
fly, and go bingo if it was too far — so any radius would be invented. The range judgement
is the commander's, and the prompt now says so.

The general lesson is the one that keeps recurring here: **a field that is authoritative for
one question gets read as authoritative for a neighbouring one.** When you write "this is
THE field to use" into the prompt, say what it is the field for.

## `UnitFormation.Reform` has no case for `Loose`

Its switch covers Vic, LineAbreast, LineAstern, Echelon, Box and Circle. `Loose` falls
through with `vector = Vector3.zero`, which orders every station onto the leader's own
position. The game's own context menu hides it for air units; `SetFormation` refuses it
outright. `Convoy` is a different code path (`FormUpConvoy`) and `Battlegroup` is not a
runtime shape.

Note also that four of the six patterns read the formation's own `Spacing` field rather than
the `spacing` argument, so passing a spacing would work for Vic and Circle and quietly not
for the rest. `SetFormation` passes the existing spacing through for that reason.

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

## Start every session by reading both logs

Before anything else, look at what the last run did — and if a mission is running now, arm a
watch rather than checking back by hand. Neither log survives in anyone's memory between
sessions, and every real bug in this repo so far was found by reading one of them rather
than by reasoning about the code.

**The sidecar log** — `sidecar/bin/Debug/net8.0/logs/sidecar-*.log`, newest by mtime. This is
the commander's side: the derived or restated objective, each cycle's assessment, the orders
with their reasons, token counts and decision latency. Read the objective line first. It says
`(own briefing)` or `(Enemy)`, and getting that wrong means everything below it is arguing
for the wrong outcome.

**The game log** — `BepInEx/LogOutput.log` under the Sea Power install. This is the
executor's side: what was accepted, refused, verified, scheduled and released. It is **not**
truncated between runs and it contains NUL bytes, so find the last `booted` line and scope to
it, and pass `-a` / `tr -d '\000'` or grep reports it as a binary file:

```bash
L="/c/Program Files (x86)/Steam/steamapps/common/Sea Power/BepInEx/LogOutput.log"
awk "NR>=$(grep -an 'booted' "$L" | tail -1 | cut -d: -f1)" "$L" | tr -d '\000' | grep -aE '\[order\]|\[verify\]|\[diag\]|\[attack\]'
```

Reading one without the other is how a wrong conclusion survives. The commander's assessment
says what it believed; the game log says what actually happened. A whole evening went into
"why won't the F-14s take an identify order" when the game log had them reaching
`IdentifySurfaceContact` two cycles later — the sidecar log alone showed only the reissues.

While a mission is live, tail the game log filtered to the lines worth acting on, and let it
notify you. Polling it by hand between messages misses the window in which a decision can
still be changed.

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

- **Do not start the sidecar. Cody runs it himself.** Hand him the command and let him
  launch it in his own terminal:
  ```
  cd "<repo>\sidecar\bin\Debug\net8.0" ; .\SeaPowerAICommander.Sidecar.exe
  ```
  An agent-started sidecar is killed after thirty minutes, which is shorter than a
  mission, so it dies mid-run and takes the cached objective with it. It also holds
  `SeaPowerAICommander.Contract.dll` open, which fails any `dotnet build` while a mission
  is live. His terminal has neither problem, and he can watch the assessments scroll past
  and Ctrl-C it when he is done.
- Comments explain **why**, especially where the code looks wrong but isn't. Match that
  density; it is the house style and most of it is load-bearing.
- Mission time comes from `GameClock.Now`, never from `GameTime` directly. 0.8.3 renamed
  `GameTime.time` to `missionElapsedTime` and widened it to a double; the accessor holds
  the cast so the next rename is one line instead of eight call sites.
- Commit messages are prose explaining the problem and the reasoning, not a changelog.
- Git identity is **not set globally** on this machine — commits fail with "Author identity
  unknown". Both Sea Power repos set it locally as
  `Cody Kilpatrick <ckilpatrick@spear.ai>`. Set it per-repo, never globally.
- The API key lives only in `OPENROUTER_API_KEY` in the environment. It is never read by
  the mod and must never reach the repo. Sidecar env vars are `AICOMMANDER_*`.
