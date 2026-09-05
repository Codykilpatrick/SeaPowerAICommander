using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SeaPowerForceAI
{
    /// <summary>
    /// Plugin root. Booted once by <see cref="AnchorChainEntry"/>.
    ///
    /// Sea Power cannot hot-reload code mods - toggling this in the mod menu only
    /// reloads the scene. A full game restart is required to load or unload it.
    /// </summary>
    public class Plugin : MonoBehaviour
    {
        public const string Guid = "com.codykilpatrick.forceai";
        public const string Name = "Sea Power Force AI";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        private static bool _booted;
        private static ConfigFile _config;

        private static ConfigEntry<bool> _cfgEnabled;
        private static ConfigEntry<float> _cfgTickInterval;
        private static ConfigEntry<bool> _cfgDrivePlayerTaskforce;
        private static ConfigEntry<bool> _cfgDumpPictureJson;

        internal static bool Enabled => _cfgEnabled != null && _cfgEnabled.Value;
        internal static float TickIntervalSeconds => _cfgTickInterval != null ? _cfgTickInterval.Value : 10f;
        internal static bool DrivePlayerTaskforce => _cfgDrivePlayerTaskforce != null && _cfgDrivePlayerTaskforce.Value;

        public static void Boot()
        {
            if (_booted) return;
            _booted = true;

            // Fully qualified: UnityEngine also defines a Logger type.
            Log = BepInEx.Logging.Logger.CreateLogSource(Name);

            try
            {
                _config = new ConfigFile(Path.Combine(Paths.ConfigPath, Guid + ".cfg"), true);
                BindConfig();

                var go = new GameObject("SeaPowerForceAI");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                Instance = go.AddComponent<Plugin>();

                new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);

                Log.LogInfo($"{Name} {Version} booted. Tick every {TickIntervalSeconds:F0}s, enabled={Enabled}.");
            }
            catch (Exception ex)
            {
                Log.LogError($"Boot failed: {ex}");
            }
        }

        private static void BindConfig()
        {
            _cfgEnabled = _config.Bind("General", "Enabled", true,
                "Master switch. When false the force-AI tick does nothing, but the patch stays applied.");

            _cfgTickInterval = _config.Bind("General", "TickIntervalSeconds", 10f,
                new ConfigDescription(
                    "Game-seconds between force-level decisions. The game's own Taskforce.CheckAI() " +
                    "uses 10s. Lower is more responsive and more expensive - matters a lot once a " +
                    "network-backed brain is attached.",
                    new AcceptableValueRange<float>(1f, 300f)));

            _cfgDrivePlayerTaskforce = _config.Bind("General", "DrivePlayerTaskforce", false,
                "Also run the brain on the player's own task force. Off by default - useful only " +
                "for testing what the brain would do with your fleet.");

            _cfgDumpPictureJson = _config.Bind("Debug", "DumpPictureJson", false,
                "Write the full serialized tactical picture to the log at debug level each tick. " +
                "Verbose - use it to inspect exactly what a brain would receive.");
        }

        /// <summary>
        /// Constructs the brain for one task force. Swap the implementation here when
        /// moving from observation to a heuristic or a sidecar-backed brain.
        /// </summary>
        internal static IForceBrain CreateBrain()
        {
            return new ObservingBrain(_cfgDumpPictureJson != null && _cfgDumpPictureJson.Value);
        }
    }
}
