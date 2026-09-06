# contract

`netstandard2.0` — the vocabulary shared by the plugin and the sidecar.

Two types live here, and they are the entire interface between a brain and the game:

| Type | Direction | Meaning |
|---|---|---|
| `TacticalPicture` | mod → brain | one task force's view of the battle at a moment |
| `ForceOrderSet` | brain → mod | what to do about it |

## Why this is its own project

The mod targets `net472` because it runs inside Unity's Mono; the sidecar targets `net8.0`
because it makes modern HTTPS calls. Neither can reference the other. `netstandard2.0` is
the only floor both stand on.

That constraint turns out to be the useful part. Because both sides reference *this*
assembly rather than duplicating DTOs, the wire format cannot drift from what the executor
understands. `OrderSchema.Build()` generates the model's JSON schema by reflecting over
`ForceOrderKind` — the same enum `OrderExecutor` switches on — so widening the action space
is a single edit here and the model physically cannot emit an order the executor has no way
to carry out.

Keep it dependency-free and keep it dumb. No game types, no `UnityEngine`, no logic beyond
classification. Anything that needs `ObjectBase` belongs in the mod.

## `ForceOrderKind` is the action space

The enum is the whole vocabulary. Everything the commander is capable of wanting has to be
expressible as one of these, and each was added because its absence was visible in play:

- **`AttackTarget`** — before it, the commander could only set weapons free and hope the
  tactical AI picked the target it had in mind. It could position a force perfectly and
  still not say what to shoot.
- **`CoordinatedAttack`** — weapons arriving one at a time are defeated one at a time,
  which is precisely how a fast attack craft force dies piecemeal.
- **`Disengage`** — having ordered an attack that identification later revealed to be
  suicidal, the only tool for cancelling was weapons-hold, which also stops the unit
  defending itself. Being able to start something you cannot stop is a bad action space.
- **`LaunchAirstrike`** — aircraft sit on the ground until something launches them. The
  commander kept identifying idle aircraft as wasted assets and then tasking them with
  movement orders they could not obey.

## `IsPositionDependent` and why it lives here

`ForceOrderKinds.IsPositionDependent` answers one question: does this order's meaning depend
on where things were when it was decided?

A `MoveTo` waypoint derived from a contact's position is wrong the moment that contact
moves. "Weapons free" or "slow to 12 knots" is a posture that stays valid as long as the
situation holds — and force-level posture changes slowly by nature. The mod ages the two
classes out on separate clocks (`MaxPositionalOrderAgeSeconds` vs
`MaxPostureOrderAgeSeconds`), because treating them alike means either discarding good
posture orders or acting on stale waypoints.

Attacks name a *contact*, not a position, so they are not position-dependent: the tactical
AI re-resolves where that contact actually is when it engages.

This classification sits in the contract rather than the mod on purpose — whoever adds an
order kind has to answer the question right next to the kind itself. An unclassified new
kind defaults to perishable, which is the safe direction to be wrong in.

## `TacticalPicture` is detection-limited by construction

Every field is built from `Taskforce.PlottingTable` — the task force's own contacts, with
their real uncertainty: `Identified`, `IsClassified`, `DetectingSensors`, and a null
position for a bearing-only hold.

This is the load-bearing property of the whole project. The game's own air-strike pipeline
reads `ObjectsManager._listOfAllObjects` directly (`AirStrikeStates/AssigningAircraft.cs:93`)
— it sees through the fog. A brain fed this type *cannot*, because the ground truth is
never in the object it receives. Keep it that way: the moment something here is populated
from a global object list, the guarantee is gone and no amount of prompt instruction gets it
back.

The two fields that need care:

- **`Objective`** — deliberately not the game's `MissionManager.Objectives`. Those are
  authored from the player's side; handing them to the opposing commander would be both
  wrong and a form of cheating. See `ObjectiveDeriver` in the sidecar for how one is
  inferred instead.
- **`OpposingObjectives`** — present *only* so an objective can be derived when none was
  configured. Used once, to infer a posture. Never handed to the commander as intelligence.

## Adding an order kind

1. Add the value to `ForceOrderKind` with a comment saying what gap it fills.
2. Classify it in `IsPositionDependent`.
3. Add any fields it needs to `ForceOrder` (they are a flat union — only some apply to each
   kind, which is deliberate for JSON-schema friendliness).
4. Implement it in `mod/src/Orders/OrderExecutor.cs`, with validation.

The schema widens automatically. The executor does not — and it validates every order
against the live task force regardless, because model output is never trusted.
