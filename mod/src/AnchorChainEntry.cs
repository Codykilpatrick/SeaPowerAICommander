using AnchorChain;

namespace SeaPowerForceAI
{
    /// <summary>
    /// Anchor Chain entry point. The chainloader finds this attribute, instantiates the
    /// class, and calls TriggerEntryPoint once - the same shape the Seapower Multiplayer
    /// mod uses.
    /// </summary>
    [ACPlugin(Plugin.Guid, Plugin.Name, Plugin.Version)]
    public class AnchorChainEntry : IAnchorChainMod
    {
        public void TriggerEntryPoint()
        {
            Plugin.Boot();
        }
    }
}
