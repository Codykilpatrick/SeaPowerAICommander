using Newtonsoft.Json;
using SeaPowerForceAI.Orders;
using SeaPowerForceAI.Picture;

namespace SeaPowerForceAI
{
    /// <summary>
    /// The default brain: watches and reports, commands nothing.
    ///
    /// This is the safe starting point. It proves the tick fires, the picture builds,
    /// and the detection-limited view looks sane in the log - without touching a single
    /// unit. Swap it for a real brain once the picture reads correctly.
    /// </summary>
    public class ObservingBrain : IForceBrain
    {
        private readonly bool _dumpJson;

        public ObservingBrain(bool dumpJson)
        {
            _dumpJson = dumpJson;
        }

        public void Submit(TacticalPicture picture)
        {
            int identified = 0, bearingOnly = 0;
            foreach (var c in picture.Contacts)
            {
                if (c.Identified) identified++;
                if (!c.Latitude.HasValue) bearingOnly++;
            }

            Plugin.Log.LogInfo(
                $"[picture] {picture.TaskforceName} ({picture.Side}) t={picture.TimeSeconds:F0}s " +
                $"alert={picture.IsOnAlert} own={picture.OwnUnits.Count} " +
                $"contacts={picture.Contacts.Count} (identified {identified}, bearing-only {bearingOnly})");

            if (_dumpJson)
                Plugin.Log.LogDebug(JsonConvert.SerializeObject(picture, Formatting.Indented));
        }

        public bool TryTakeOrders(out ForceOrderSet orders)
        {
            orders = null;
            return false;
        }
    }
}
