using Newtonsoft.Json;
using SeaPowerAICommander.Orders;
using SeaPowerAICommander.Picture;

namespace SeaPowerAICommander
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
                $"plot={picture.PlotEntries} " +
                $"contacts={picture.Contacts.Count} (identified {identified}, bearing-only {bearingOnly})");

            // Info, not Debug: BepInEx's disk logger here is configured for
            // "Fatal, Error, Warning, Message, Info", so LogDebug writes nothing to
            // LogOutput.log and the setting would look broken.
            if (_dumpJson)
                Plugin.Log.LogInfo(JsonConvert.SerializeObject(picture, Formatting.Indented));
        }

        /// <summary>Never busy - observing is synchronous and free.</summary>
        public bool IsBusy
        {
            get { return false; }
        }

        public bool TryTakeOrders(out ForceOrderSet orders)
        {
            orders = null;
            return false;
        }
    }
}
