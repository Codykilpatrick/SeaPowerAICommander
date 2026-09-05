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
    /// Cadence follows the game's own precedent: Taskforce throttles its CheckAI() to
    /// 10 seconds (Taskforce.cs:405). Running per-frame at this altitude would be waste.
    /// </summary>
    [HarmonyPatch(typeof(TaskForceAI), nameof(TaskForceAI.OnUpdate))]
    internal static class ForceBrainTick
    {
        /// <summary>
        /// TaskForceAI._taskforce is private. Cached FieldRef beats reflection per frame.
        /// </summary>
        private static readonly AccessTools.FieldRef<TaskForceAI, Taskforce> TaskforceOf =
            AccessTools.FieldRefAccess<TaskForceAI, Taskforce>("_taskforce");

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
            state.Brain.Submit(picture);
        }
    }
}
