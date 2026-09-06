using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SeaPower;
using SeaPowerForceAI.Orders;
using SeaPowerForceAI.Picture;

namespace SeaPowerForceAI
{
    /// <summary>
    /// Occupies TaskForceAI.OnUpdate.
    ///
    /// The game constructs a TaskForceAI per task force (Taskforce.cs:222) and calls
    /// OnUpdate on it every frame (Taskforce.cs:332) - but the method body is empty in
    /// the shipped game. There is no incumbent behaviour here, so a postfix adds a
    /// force-level decision layer without overriding anything.
    ///
    /// Cadence is throttled hard - 60s by default. The game throttles its own CheckAI()
    /// to 10s (Taskforce.cs:405), but that handles finer-grained work; force-level intent
    /// changes on a scale of minutes. Running per-frame at this altitude would be waste,
    /// and with a network-backed brain it would also be expensive.
    /// </summary>
    [HarmonyPatch(typeof(TaskForceAI), nameof(TaskForceAI.OnUpdate))]
    internal static class ForceBrainTick
    {
        /// <summary>
        /// TaskForceAI._taskforce is private. Cached FieldRef beats reflection per frame.
        ///
        /// Resolved defensively: a static initializer that throws would surface as a
        /// TypeInitializationException inside Harmony's call path on every frame, which
        /// is far worse than degrading to a no-op. If a game update renames the field,
        /// this stays null and the tick disables itself with one log line.
        /// </summary>
        private static readonly AccessTools.FieldRef<TaskForceAI, Taskforce> TaskforceOf = ResolveTaskforceField();

        private static bool _fieldMissingLogged;

        /// <summary>One frame at 60fps. Main-thread work above this is visible as a hitch.</summary>
        private const double FrameBudgetMs = 16.0;

        private static AccessTools.FieldRef<TaskForceAI, Taskforce> ResolveTaskforceField()
        {
            try
            {
                return AccessTools.FieldRefAccess<TaskForceAI, Taskforce>("_taskforce");
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Per-task-force state, keyed weakly so a torn-down task force is collected
        /// rather than leaked for the life of the process.
        /// </summary>
        private static readonly ConditionalWeakTable<TaskForceAI, BrainState> States =
            new ConditionalWeakTable<TaskForceAI, BrainState>();

        private class BrainState
        {
            public float NextSubmitTime = float.MinValue;
            public IForceBrain Brain;

            /// <summary>Units present at the last decision, so losses can be diffed against now.</summary>
            public readonly Dictionary<int, LostUnit> KnownUnits = new Dictionary<int, LostUnit>();

            /// <summary>
            /// Orders currently in force, keyed by unit and kind.
            ///
            /// These ACCUMULATE. A cycle that issues no orders does not mean nothing is in
            /// force - it means everything previously ordered still is. A new order
            /// supersedes the previous one of the same kind for the same unit and leaves
            /// every other unit's orders untouched.
            /// </summary>
            public readonly Dictionary<string, ForceOrder> StandingOrders =
                new Dictionary<string, ForceOrder>();

            public int TotalLosses;

            /// <summary>
            /// Kills already counted. A wreck lingers in the plotting table for several
            /// cycles, so without this the same sinking would be reported over and over
            /// and the commander would think it was winning far harder than it is.
            /// </summary>
            public readonly HashSet<int> CountedKills = new HashSet<int>();

            public int TotalKills;
            public float LastDecisionTime = -1f;
            public bool Seeded;
        }

        private static void Postfix(TaskForceAI __instance)
        {
            if (!Plugin.Enabled) return;

            try
            {
                Tick(__instance);
            }
            catch (Exception ex)
            {
                // A throw here happens once per task force per frame. Never let it escape
                // into the game's update loop.
                Plugin.Log.LogError($"[tick] {ex}");
            }
        }

        private static void Tick(TaskForceAI instance)
        {
            if (TaskforceOf == null)
            {
                if (!_fieldMissingLogged)
                {
                    _fieldMissingLogged = true;
                    Plugin.Log.LogError(
                        "TaskForceAI._taskforce could not be resolved - the game version likely " +
                        "changed. Force AI is inactive; re-check the field name against this build.");
                }
                return;
            }

            var tf = TaskforceOf(instance);
            if (tf == null) return;

            // Only drive AI-controlled sides. The player's own force is off limits
            // unless explicitly opted in (useful for testing against yourself).
            if (tf.Side == Taskforce.TfType.Player && !Plugin.DrivePlayerTaskforce) return;
            if (tf.Side == Taskforce.TfType.None) return;

            var state = States.GetOrCreateValue(instance);
            if (state.Brain == null)
                state.Brain = Plugin.CreateBrain();

            // Release any coordinated shots whose moment has arrived. Must run every tick,
            // not just on decision ticks - the schedule is in game seconds, not decisions.
            AttackScheduler.Pump();

            // Apply anything the brain finished since the last tick. Do this before
            // submitting, so a slow brain still gets its orders in promptly.
            ForceOrderSet ready;
            if (state.Brain.TryTakeOrders(out ready))
                Consume(tf, state, ready);

            var now = GameTime.time;
            if (now < state.NextSubmitTime) return;

            // Ask before building. A picture walks every unit and every plotting-table
            // entry, and a decision spans many ticks under time compression - building one
            // the brain will only drop is pure waste. Do NOT advance NextSubmitTime here:
            // this tick did not consume a decision slot, so the next one should not wait.
            if (state.Brain.IsBusy) return;

            state.NextSubmitTime = now + Plugin.TickIntervalSeconds;

            // Everything from here runs on the Unity thread, so it is the only part of
            // this mod that can cause a frame hitch. Measured rather than assumed.
            var buildWatch = Stopwatch.StartNew();
            var picture = PictureBuilder.Build(tf);
            buildWatch.Stop();

            var buildMs = buildWatch.Elapsed.TotalMilliseconds;
            if (buildMs > FrameBudgetMs)
            {
                Plugin.Log.LogWarning(
                    $"[perf] {picture.TaskforceName}: picture took {buildMs:F1}ms on the game thread " +
                    $"({picture.OwnUnits.Count} units, {picture.PlotEntries} plot entries) - " +
                    "over one frame at 60fps, this is visible as a hitch.");
            }
            else
            {
                Plugin.Log.LogInfo(
                    $"[perf] {picture.TaskforceName}: picture built in {buildMs:F1}ms " +
                    $"({picture.OwnUnits.Count} units, {picture.PlotEntries} plot entries)");
            }

            // Nothing to command. Missions carry task forces that hold no units at all
            // (an empty Neutral side, for one), and asking a model what to do with an
            // empty fleet costs real money to be told "nothing".
            if (picture.OwnUnits.Count == 0) return;

            ApplyContinuity(state, picture, now);

            state.Brain.Submit(picture);
        }

        /// <summary>
        /// Applies a returned decision, unless it has been overtaken by events.
        ///
        /// The tick interval is GAME time but a network round trip is REAL time, and time
        /// compression divorces the two: at 10x, a 45-second decision is made against a
        /// picture seven game-minutes old by the time it lands. Acting on that is worse
        /// than doing nothing, because the tactical AI underneath is at least reacting to
        /// the present.
        /// </summary>
        private static void Consume(Taskforce tf, BrainState state, ForceOrderSet ready)
        {
            var age = GameTime.time - ready.DerivedFromTime;

            // Filter per order, not per decision. A decision typically mixes perishable
            // waypoints with durable posture; discarding the whole set to protect against
            // one stale waypoint throws away orders that are still perfectly good.
            var dropped = 0;
            for (int i = ready.Orders.Count - 1; i >= 0; i--)
            {
                var order = ready.Orders[i];
                if (order == null) continue;

                var limit = ForceOrderKinds.IsPositionDependent(order.Kind)
                    ? Plugin.MaxPositionalOrderAgeSeconds
                    : Plugin.MaxPostureOrderAgeSeconds;

                if (limit > 0f && age > limit)
                {
                    ready.Orders.RemoveAt(i);
                    dropped++;
                }
            }

            if (dropped > 0)
            {
                Plugin.Log.LogWarning(
                    $"[orders] {tf._nameInMissionFile}: dropped {dropped} perishable order(s) " +
                    $"from a picture {age:F0}s old; {ready.Orders.Count} durable order(s) kept.");
            }

            if (ready.Orders.Count == 0)
            {
                // Log it. "No change needed" is a real and often correct decision, but
                // silence here is indistinguishable from the brain never having run -
                // which made a live battle impossible to diagnose.
                Plugin.Log.LogInfo(
                    $"[orders] {tf._nameInMissionFile}: no change ordered " +
                    $"(picture {age:F0}s old, {state.StandingOrders.Count} standing)");
                return;
            }

            var accepted = OrderExecutor.Apply(tf, ready);

            // Only orders that took effect become standing. Replaying a rejected order
            // would tell the brain a sunk ship is still under way.
            foreach (var order in accepted)
                state.StandingOrders[order.UnitId + ":" + order.Kind] = order;
        }

        /// <summary>
        /// Gives the picture a memory: what was ordered previously, and what has been lost
        /// since. Without this the brain re-derives everything each tick and cannot tell
        /// that it is losing the battle.
        /// </summary>
        private static void ApplyContinuity(BrainState state, TacticalPicture picture, float now)
        {
            // Diff this cycle's roster against the last to find losses. Skipped on the
            // first decision - every unit would otherwise look newly arrived.
            if (state.Seeded)
            {
                var present = new HashSet<int>();
                foreach (var u in picture.OwnUnits) present.Add(u.Id);

                foreach (var pair in state.KnownUnits)
                {
                    if (!present.Contains(pair.Key))
                        picture.RecentLosses.Add(pair.Value);
                }

                state.TotalLosses += picture.RecentLosses.Count;
                picture.SecondsSinceLastDecision = now - state.LastDecisionTime;
            }

            picture.TotalLosses = state.TotalLosses;

            // Report each kill once, the cycle it is first observed.
            for (int i = picture.RecentKills.Count - 1; i >= 0; i--)
            {
                if (!state.CountedKills.Add(picture.RecentKills[i].Id))
                    picture.RecentKills.RemoveAt(i);
            }

            state.TotalKills += picture.RecentKills.Count;
            picture.TotalKills = state.TotalKills;

            if (picture.RecentKills.Count > 0)
            {
                foreach (var k in picture.RecentKills)
                    Plugin.Log.LogInfo($"[kill] {picture.TaskforceName}: {k.Name} ({k.Id}) destroyed");
            }

            // Drop standing orders for units that no longer exist, then replay the rest.
            var live = new HashSet<int>();
            foreach (var u in picture.OwnUnits) live.Add(u.Id);

            var stale = new List<string>();
            foreach (var pair in state.StandingOrders)
            {
                if (!live.Contains(pair.Value.UnitId))
                {
                    stale.Add(pair.Key);
                    continue;
                }

                // Replay what is in force, not the prose that justified it. The commander
                // wrote those reasons itself and does not need them read back; at a dozen
                // standing orders they were a meaningful slice of a payload whose growth
                // has been tracking decision latency.
                var o = pair.Value;
                picture.StandingOrders.Add(new ForceOrder
                {
                    Kind = o.Kind,
                    UnitId = o.UnitId,
                    Latitude = o.Latitude,
                    Longitude = o.Longitude,
                    SpeedKnots = o.SpeedKnots,
                    WeaponStatus = o.WeaponStatus,
                    TargetContactId = o.TargetContactId,
                    Salvo = o.Salvo,
                    CoordinationGroup = o.CoordinationGroup,
                    Reason = null,
                });
            }
            foreach (var key in stale) state.StandingOrders.Remove(key);

            // Re-seed the roster for the next diff.
            state.KnownUnits.Clear();
            foreach (var u in picture.OwnUnits)
            {
                state.KnownUnits[u.Id] = new LostUnit
                {
                    Id = u.Id,
                    Name = u.Name,
                    Category = u.Category,
                };
            }

            LogUnitState(picture);
            VerifyStandingOrders(picture);

            state.LastDecisionTime = now;
            state.Seeded = true;
        }

        /// <summary>
        /// One compact line per unit: what it is, whether the formation owns it, where it
        /// is going and how fast.
        ///
        /// Readable in a way the full JSON dump is not, and aimed at the question that
        /// keeps coming up - which units can actually be given orders. A formation
        /// follower accepts movement orders and ignores them, so its presence here
        /// explains an order that appeared to vanish.
        /// </summary>
        private static void LogUnitState(TacticalPicture picture)
        {
            if (!Plugin.LogUnitStateEnabled) return;

            foreach (var u in picture.OwnUnits)
            {
                // InFormation alone does not distinguish a leader from a follower - it is
                // true for both - so a leader was being reported as untaskable while
                // visibly holding its own route.
                var formation = !u.InFormation ? "independent"
                    : u.IsFormationLeader ? "FORMATION-LEADER"
                    : u.ActsIndependentlyInFormation ? "formation(independent)"
                    : "formation-follower";

                var route = u.WaypointsRemaining > 0
                    ? $"{u.WaypointsRemaining}wp"
                    : "no-route";

                Plugin.Log.LogInfo(
                    $"[units] {u.Name} ({u.Id}) {u.Category} {formation} {route} " +
                    $"{u.SpeedKnots:F0}/{u.CommandedSpeedKnots:F0}kt max{u.MaxSpeedKnots:F0} " +
                    $"weapons={u.WeaponStatus} reach asuw={u.AntiSurfaceReachNM:F1} aaw={u.AirDefenceReachNM:F1}");
            }

            foreach (var c in picture.Contacts)
            {
                // What the commander actually knows about this contact, in the terms it
                // reasons in - identification state is what gates the threat envelope, so
                // an unidentified contact showing "envelope unknown" explains why a
                // range check did not happen.
                var id = c.Identified ? "IDENTIFIED" : (c.Classified ? "classified" : "unknown");
                var pos = c.Latitude.HasValue ? $"{c.RangeFromForceNM:F0}nm" : "bearing-only";

                var envelope = c.AirDefenceRangeNM.HasValue
                    ? $"aaw={c.AirDefenceRangeNM:F0} asuw={c.AntiSurfaceRangeNM:F0}"
                    : "envelope unknown";

                var terrain = c.TerrainOnBearingM.HasValue
                    ? (c.TerrainOnBearingM.Value > 0f ? $"terrain {c.TerrainOnBearingM:F0}m" : "open water")
                    : "terrain unsampled";

                Plugin.Log.LogInfo(
                    $"[contact] {c.Id} {c.Class} {id}{(c.Dormant ? " DORMANT" : "")} {pos} " +
                    $"{c.Relationship} sensors={c.DetectingSensors} {envelope} {terrain}");
            }
        }

        /// <summary>
        /// Checks what was ordered against what the units are actually doing.
        ///
        /// "applied N, rejected 0" only means the call did not throw - it says nothing
        /// about whether the unit obeyed. A command that silently no-ops for some unit
        /// type would look identical to one that worked, and the commander would keep
        /// issuing it forever. This is the only thing that closes the loop.
        /// </summary>
        private static void VerifyStandingOrders(TacticalPicture picture)
        {
            if (picture.StandingOrders.Count == 0) return;

            var units = new Dictionary<int, OwnUnit>();
            foreach (var u in picture.OwnUnits) units[u.Id] = u;

            var ignored = 0;

            foreach (var order in picture.StandingOrders)
            {
                OwnUnit unit;
                if (!units.TryGetValue(order.UnitId, out unit)) continue;

                switch (order.Kind)
                {
                    case ForceOrderKind.MoveTo:
                        // A formation follower takes its movement from the leader and
                        // legitimately holds no waypoints of its own, so an empty route is
                        // not evidence the order failed - though it does mean the order
                        // probably achieved nothing, which the commander is now told.
                        if (unit.InFormation && !unit.IsFormationLeader && !unit.ActsIndependentlyInFormation)
                        {
                            ignored++;
                            Plugin.Log.LogWarning(
                                $"[verify] {unit.Name} ({unit.Id}): ordered MoveTo but is a formation follower - " +
                                "movement comes from its leader, so the order likely had no effect");
                        }
                        else if (unit.WaypointsRemaining == 0)
                        {
                            ignored++;
                            Plugin.Log.LogWarning(
                                $"[verify] {unit.Name} ({unit.Id}): ordered MoveTo but has no waypoints - order did not take");
                        }
                        break;

                    case ForceOrderKind.SetSpeed:
                        // Compare against the COMMANDED speed, not the actual one - a unit
                        // still accelerating is obeying, just not there yet.
                        if (Math.Abs(unit.CommandedSpeedKnots - order.SpeedKnots) <= 2f) break;
                        if (order.SpeedKnots > unit.MaxSpeedKnots) break;

                        // A formation caps its members at its slowest hull, so a higher
                        // order is clamped rather than refused. Report it as the ceiling
                        // it is, not as a failure.
                        if (unit.MaxFormationSpeedKnots > 0f && order.SpeedKnots > unit.MaxFormationSpeedKnots)
                        {
                            ignored++;
                            Plugin.Log.LogWarning(
                                $"[verify] {unit.Name} ({unit.Id}): ordered {order.SpeedKnots:F0}kt but its " +
                                $"formation is capped at {unit.MaxFormationSpeedKnots:F0}kt by its slowest " +
                                "member - clamped, not refused");
                            break;
                        }

                        ignored++;
                        Plugin.Log.LogWarning(
                            $"[verify] {unit.Name} ({unit.Id}): ordered {order.SpeedKnots:F0}kt " +
                            $"but commanded speed is {unit.CommandedSpeedKnots:F0}kt - order did not take");
                        break;

                    case ForceOrderKind.SetWeaponStatus:
                        if (!string.IsNullOrEmpty(unit.WeaponStatus)
                            && !string.Equals(unit.WeaponStatus, order.WeaponStatus, StringComparison.OrdinalIgnoreCase))
                        {
                            ignored++;
                            Plugin.Log.LogWarning(
                                $"[verify] {unit.Name} ({unit.Id}): ordered weapons {order.WeaponStatus} " +
                                $"but posture is {unit.WeaponStatus} - order did not take");
                        }
                        break;
                }
            }

            Plugin.Log.LogInfo(
                $"[verify] {picture.TaskforceName}: {picture.StandingOrders.Count} standing, " +
                $"{ignored} not reflected in unit state");
        }
    }
}
