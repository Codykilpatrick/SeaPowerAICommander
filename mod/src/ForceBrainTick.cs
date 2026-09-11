using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SeaPower;
using SeaPowerAICommander.Orders;
using SeaPowerAICommander.Picture;

namespace SeaPowerAICommander
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

            /// <summary>
            /// Air strikes ordered this mission, and the ids of those seen to get aircraft.
            /// The game drops a strike from its list once finished with it, so a stalled one
            /// leaves no trace - and the commander, having correctly concluded the airbase was
            /// unusable, sees an empty list next cycle and tries again.
            /// </summary>
            public int AirstrikesOrdered;

            /// <summary>Strike ids seen past AssigningAircraft, so each is counted once.</summary>
            public readonly HashSet<int> AirstrikesThatFlew = new HashSet<int>();

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
                        "changed. AI Commander is inactive; re-check the field name against this build.");
                }
                return;
            }

            var tf = TaskforceOf(instance);
            if (tf == null) return;

            // Only drive AI-controlled sides. The player's own force is off limits
            // unless explicitly opted in (useful for testing against yourself).
            if (tf.Side == Taskforce.TfType.Player && !Plugin.DrivePlayerTaskforce) return;
            if (tf.Side == Taskforce.TfType.None) return;

            // Neutrals are not belligerents, so there is nothing for a commander to decide
            // for them. HasArmedUnit below was supposed to cover this and does not: one
            // armed escort in a merchant group passes it, and the whole convoy then gets
            // commanded. Observed driving a twelve-ship Neutral force holding ZERO contacts
            // and trying to set emission control on MV Pacific Highway, a car carrier.
            //
            // It cost a third of the spend AND a third of the sidecar's queue depth, which
            // is what pushed the real belligerents' decisions past the mod's timeout.
            if (tf.Side == Taskforce.TfType.Neutral) return;

            // A side with nothing that can shoot is not a force to command. Baltim puts five
            // civilian shrimp boats in the water as their own task force, and we were building
            // them a full tactical picture every cycle, paying for a decision, and ordering the
            // trawlers to weapons Hold - roughly doubling the spend on any mission with
            // civilian traffic. Hormuz hid this because its Neutral side was empty.
            if (!HasArmedUnit(tf)) return;

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

            // Counted here rather than where strikes are read, because a strike that never
            // finds aircraft is pruned by the game and would otherwise go unrecorded.
            foreach (var order in accepted)
                if (order.Kind == ForceOrderKind.LaunchAirstrike) state.AirstrikesOrdered++;

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

            // An attack order against a contact that is gone is finished, whether the target
            // sank or the track was simply lost. Left standing, the commander sees a live
            // order against a ship it watched die and issues Disengage to clear it - which
            // cannot be verified either, so it reissues the same cancellation every cycle
            // forever. Retiring the order removes the thing being cancelled.
            var held = new HashSet<int>();
            foreach (var c in picture.Contacts) held.Add(c.Id);

            var stale = new List<string>();
            foreach (var pair in state.StandingOrders)
            {
                if (!live.Contains(pair.Value.UnitId))
                {
                    stale.Add(pair.Key);
                    continue;
                }

                if (IsAgainstAContact(pair.Value.Kind) && !held.Contains(pair.Value.TargetContactId))
                {
                    stale.Add(pair.Key);
                    Plugin.Log.LogInfo(
                        $"[orders] retiring {pair.Value.Kind} by unit {pair.Value.UnitId}: " +
                        $"contact {pair.Value.TargetContactId} is no longer held");
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

            TallyAirstrikes(picture, state);
            LogUnitState(picture);
            VerifyStandingOrders(picture);

            state.LastDecisionTime = now;
            state.Seeded = true;
        }

        /// <summary>
        /// Whether this order kind is aimed at a specific contact, and so becomes meaningless
        /// once that contact is no longer held.
        /// </summary>
        private static bool IsAgainstAContact(ForceOrderKind kind)
        {
            switch (kind)
            {
                case ForceOrderKind.AttackTarget:
                case ForceOrderKind.CoordinatedAttack:
                case ForceOrderKind.LaunchAirstrike:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Whether this task force holds anything that can engage. Checked before a picture is
        /// built, so a purely civilian side costs nothing at all rather than costing a model
        /// call to be told it has nothing to fight with.
        /// </summary>
        private static bool HasArmedUnit(Taskforce tf)
        {
            try
            {
                if (Armed(tf._taskforceVessels)) return true;
                if (Armed(tf._taskforceLandUnits)) return true;
                if (Armed(tf._taskforceAircraft)) return true;
                if (Armed(tf._taskforceSubmarines)) return true;
                return false;
            }
            catch (Exception ex)
            {
                // Never silently stop driving a force over a null field somewhere.
                Plugin.Log.LogWarning($"[brain] could not check {tf._nameInMissionFile} for weapons: {ex.Message}");
                return true;
            }
        }

        private static bool Armed(List<ObjectBase> units)
        {
            if (units == null) return false;

            foreach (var obj in units)
            {
                if (obj == null || obj.IsDestroyed || obj._obp == null) continue;

                // Chaff alone does not make a combatant; anything that can shoot does.
                var p = obj._obp;
                if (p._gunWeapons != null && p._gunWeapons.Count > 0) return true;
                if (p._launcherWeapons != null && p._launcherWeapons.Count > 0) return true;
                if (p._hardpointWeapons != null && p._hardpointWeapons.Count > 0) return true;
                if (p._ciwsWeapons != null && p._ciwsWeapons.Count > 0) return true;
            }

            return false;
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
        /// <summary>
        /// Keeps a mission-long count of air strikes ordered against air strikes that ever got
        /// aircraft, and puts both in the picture.
        ///
        /// A strike that cannot find aircraft never leaves AssigningAircraft, and the game
        /// eventually drops it from the task force's list. The commander then sees no strikes
        /// at all - not a failed one - and orders another, having reasoned correctly the cycle
        /// before that the airbase had nothing to give. Six were ordered in one mission that
        /// way. A running count survives the pruning and makes the pattern visible.
        /// </summary>
        private static void TallyAirstrikes(TacticalPicture picture, BrainState state)
        {
            foreach (var strike in picture.Airstrikes)
            {
                if (strike.AircraftAssigned > 0
                    || !string.Equals(strike.State, "AssigningAircraft", StringComparison.Ordinal))
                {
                    state.AirstrikesThatFlew.Add(strike.Id);
                }
            }

            picture.AirstrikesOrdered = state.AirstrikesOrdered;
            picture.AirstrikesThatFlew = state.AirstrikesThatFlew.Count;
        }

        /// <summary>
        /// Records an order that is not being carried out - to the log for us, and into the
        /// picture for the commander. It had no way to learn that an order failed, so it kept
        /// planning around orders that were doing nothing: speeds the tactical AI had
        /// rewritten, movement given to formation followers that cannot steer.
        /// </summary>
        private static void Problem(TacticalPicture picture, string message)
        {
            Plugin.Log.LogWarning(message);
            if (picture.OrderProblems.Count < 20) picture.OrderProblems.Add(Strip(message));
        }

        /// <summary>The log tag is ours; the commander only needs the sentence.</summary>
        private static string Strip(string message)
        {
            const string tag = "[verify] ";
            return message.StartsWith(tag) ? message.Substring(tag.Length) : message;
        }

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
                            Problem(picture,
                                $"[verify] {unit.Name} ({unit.Id}): ordered MoveTo but is a formation follower - " +
                                "movement comes from its leader, so the order likely had no effect");
                        }
                        else if (unit.WaypointsRemaining == 0)
                        {
                            ignored++;
                            Problem(picture,
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
                            Problem(picture,
                                $"[verify] {unit.Name} ({unit.Id}): ordered {order.SpeedKnots:F0}kt but its " +
                                $"formation is capped at {unit.MaxFormationSpeedKnots:F0}kt by its slowest " +
                                "member - clamped, not refused");
                            break;
                        }

                        ignored++;
                        Problem(picture,
                            $"[verify] {unit.Name} ({unit.Id}): ordered {order.SpeedKnots:F0}kt " +
                            $"but commanded speed is {unit.CommandedSpeedKnots:F0}kt - order did not take");
                        break;

                    case ForceOrderKind.SetWeaponStatus:
                        if (!string.IsNullOrEmpty(unit.WeaponStatus)
                            && !string.Equals(unit.WeaponStatus, order.WeaponStatus, StringComparison.OrdinalIgnoreCase))
                        {
                            ignored++;
                            Problem(picture,
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
