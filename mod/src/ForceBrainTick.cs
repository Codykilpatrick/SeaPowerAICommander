using System;
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
                OrderExecutor.Apply(tf, ready);

            var now = GameTime.time;
            if (now < state.NextSubmitTime) return;
            state.NextSubmitTime = now + Plugin.TickIntervalSeconds;

            var picture = PictureBuilder.Build(tf);

            // Nothing to command. Missions carry task forces that hold no units at all
            // (an empty Neutral side, for one), and asking a model what to do with an
            // empty fleet costs real money to be told "nothing".
            if (picture.OwnUnits.Count == 0) return;

            state.Brain.Submit(picture);
        }
    }
}
