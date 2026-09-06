using System;
using System.Collections.Generic;
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

            // Apply anything the brain finished since the last tick. Do this before
            // submitting, so a slow brain still gets its orders in promptly.
            ForceOrderSet ready;
            if (state.Brain.TryTakeOrders(out ready))
                Consume(tf, state, ready);

            var now = GameTime.time;
            if (now < state.NextSubmitTime) return;
            state.NextSubmitTime = now + Plugin.TickIntervalSeconds;

            var picture = PictureBuilder.Build(tf);

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

            // Drop standing orders for units that no longer exist, then replay the rest.
            var live = new HashSet<int>();
            foreach (var u in picture.OwnUnits) live.Add(u.Id);

            var stale = new List<string>();
            foreach (var pair in state.StandingOrders)
            {
                if (!live.Contains(pair.Value.UnitId)) stale.Add(pair.Key);
                else picture.StandingOrders.Add(pair.Value);
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

            state.LastDecisionTime = now;
            state.Seeded = true;
        }
    }
}
