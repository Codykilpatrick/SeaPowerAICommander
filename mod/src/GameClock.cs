using SeaPower;

namespace SeaPowerAICommander
{
    /// <summary>
    /// Mission time in seconds, as the picture and every grace window measure it.
    ///
    /// 0.8.3 renamed GameTime.time to GameTime.missionElapsedTime and widened it to a
    /// double, so that it can be written into a save (GameTime.SaveStateToFile). Nothing
    /// here needs that width - these are tick cadences, order ages and attack staggers,
    /// intervals of seconds to minutes - so the cast back to float keeps BrainState,
    /// ForceOrderSet and TacticalPicture.TimeSeconds exactly as they were. Holding the
    /// cast in one place also means the next rename of the game's clock is one line
    /// rather than the eight call sites this replaced.
    /// </summary>
    internal static class GameClock
    {
        internal static float Now => (float)GameTime.missionElapsedTime;
    }
}
