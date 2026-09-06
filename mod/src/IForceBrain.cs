using SeaPowerForceAI.Orders;
using SeaPowerForceAI.Picture;

namespace SeaPowerForceAI
{
    /// <summary>
    /// A force-level decision maker for one task force.
    ///
    /// The interface is deliberately split into submit/poll rather than a single
    /// blocking Decide(). A brain backed by a network call must never stall the
    /// game thread - it takes a picture now and produces orders whenever it can,
    /// which may be several ticks later or never.
    ///
    /// Implementations must be safe to call from the Unity main thread and must not
    /// block in either method.
    /// </summary>
    public interface IForceBrain
    {
        /// <summary>
        /// Hand over the current picture. Returns immediately.
        /// Implementations should drop the submission if one is already in flight
        /// rather than queueing - a stale naval picture is worse than no picture.
        /// </summary>
        void Submit(TacticalPicture picture);

        /// <summary>
        /// Collect orders if any are ready. Returns false when nothing is pending.
        /// Called every tick, so it must be cheap.
        /// </summary>
        bool TryTakeOrders(out ForceOrderSet orders);

        /// <summary>
        /// True when a submission right now would be dropped.
        ///
        /// Lets the caller skip building a picture it cannot use. Assembling one walks
        /// every unit and every plotting-table entry, and under time compression a single
        /// decision spans many ticks - the first live run built 200 pictures to produce
        /// 17 decisions and discarded the rest.
        /// </summary>
        bool IsBusy { get; }
    }
}
