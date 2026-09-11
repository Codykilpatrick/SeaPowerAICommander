using HarmonyLib;
using SeaPower;

namespace SeaPowerAICommander
{
    /// <summary>
    /// Stops the game disarming a delegated force behind the commander's back.
    ///
    /// <c>ObjectBase.CheckForPlayerAbort</c> reverts any unit where
    /// <c>IsPlayerObject</c> is true from weapons Free to Tight and calls CeaseFire,
    /// clearing its attack orders. It is called when a unit reaches a waypoint
    /// (GoToWaypointTask:480), reaches a station point (GoToRelativeToStationPoint:204),
    /// and through the formation leader (UnitFormation:1815).
    ///
    /// That is a sensible interlock in the game it was written for: the player's own ships
    /// should not keep shooting autonomously after completing a movement leg, because a
    /// human is sitting there to decide. It becomes wrong the moment the player's force is
    /// handed to a commander, and it is silent - the order is accepted, applied, and undone
    /// a moment later by something the commander cannot see.
    ///
    /// Observed cost: CAP fighters fly continuous station-keeping legs, so they were being
    /// disarmed every time they reached a station point, over and over, for the whole
    /// battle. The commander noticed the effect and kept re-ordering weapons Free, which
    /// looked like it was ignoring the log; it was not. The opposing force was unaffected
    /// throughout, because none of its units are IsPlayerObject.
    ///
    /// DELIBERATELY NARROW. This suppresses a safety behaviour on the player's own fleet,
    /// so it applies only when the commander is actually driving that fleet - if
    /// DrivePlayerTaskforce is off, or the plugin is disabled, the interlock stands
    /// untouched and the game behaves exactly as shipped. A unit outside the player's
    /// taskforce never reaches the interesting branch anyway.
    /// </summary>
    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.CheckForPlayerAbort))]
    internal static class PlayerAbortGuard
    {
        private static bool Prefix(ObjectBase __instance)
        {
            // true = run the original. Anything uncertain falls through to the game's
            // own behaviour rather than suppressing it.
            if (__instance == null) return true;
            if (!Plugin.Enabled) return true;
            if (!Plugin.DrivePlayerTaskforce) return true;

            var tf = __instance._taskforce;
            if (tf == null || tf.Side != Taskforce.TfType.Player) return true;

            // This force is under commander control, so the commander's weapons posture
            // is the intended one. Skip the abort.
            return false;
        }
    }
}
