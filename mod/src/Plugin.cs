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
        private static ConfigEntry<BrainType> _cfgBrain;
        private static ConfigEntry<string> _cfgSidecarEndpoint;
        private static ConfigEntry<int> _cfgSidecarTimeoutMs;
        private static ConfigEntry<int> _cfgMinRequestGapMs;
        private static ConfigEntry<float> _cfgMaxPositionalOrderAgeSeconds;
        private static ConfigEntry<float> _cfgMaxPostureOrderAgeSeconds;

        internal static float MaxPositionalOrderAgeSeconds =>
            _cfgMaxPositionalOrderAgeSeconds != null ? _cfgMaxPositionalOrderAgeSeconds.Value : 240f;

        internal static float MaxPostureOrderAgeSeconds =>
            _cfgMaxPostureOrderAgeSeconds != null ? _cfgMaxPostureOrderAgeSeconds.Value : 0f;

        public enum BrainType
        {
            /// <summary>Log the picture, command nothing. Safe default.</summary>
            Observing,

            /// <summary>Send the picture to the out-of-process brain and execute its orders.</summary>
            Sidecar,
        }

        internal static bool Enabled => _cfgEnabled != null && _cfgEnabled.Value;
        internal static float TickIntervalSeconds => _cfgTickInterval != null ? _cfgTickInterval.Value : 60f;
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

            _cfgTickInterval = _config.Bind("General", "TickIntervalSeconds", 60f,
                new ConfigDescription(
                    "Game-seconds between force-level decisions. Force-level intent - where groups " +
                    "go, what posture to hold - changes on a scale of minutes, so a minute is a " +
                    "realistic command cycle rather than a compromise. The game's own " +
                    "Taskforce.CheckAI() runs at 10s, but that handles finer-grained work. " +
                    "Lower is more responsive and proportionally more expensive once a " +
                    "network-backed brain is attached.",
                    new AcceptableValueRange<float>(1f, 600f)));

            _cfgDrivePlayerTaskforce = _config.Bind("General", "DrivePlayerTaskforce", false,
                "Also run the brain on the player's own task force. Off by default - useful only " +
                "for testing what the brain would do with your fleet.");

            _cfgDumpPictureJson = _config.Bind("Debug", "DumpPictureJson", false,
                "Write the full serialized tactical picture to the log at debug level each tick. " +
                "Verbose - use it to inspect exactly what a brain would receive.");

            _cfgBrain = _config.Bind("Brain", "Type", BrainType.Observing,
                "Observing: log the picture, issue no orders. Sidecar: send the picture to the " +
                "out-of-process brain and execute what it returns. Sidecar costs real money per " +
                "decision and requires SeaPowerForceAI.Sidecar to be running.");

            _cfgSidecarEndpoint = _config.Bind("Brain", "SidecarEndpoint", "http://127.0.0.1:8787/",
                "Where the sidecar listens. Loopback only - do not point this off-machine.");

            _cfgSidecarTimeoutMs = _config.Bind("Brain", "SidecarTimeoutMs", 90000,
                new ConfigDescription(
                    "How long to wait for a decision before abandoning that cycle. Runs on a " +
                    "background thread, so this never stalls the game.",
                    new AcceptableValueRange<int>(1000, 600000)));

            _cfgMinRequestGapMs = _config.Bind("Brain", "MinRequestGapMs", 4000,
                new ConfigDescription(
                    "Minimum WALL-CLOCK milliseconds between sidecar requests, across all " +
                    "task forces. TickIntervalSeconds is game time, so time compression " +
                    "multiplies the request rate and every task force has its own brain - " +
                    "together they can burst past a provider rate limit. 4000ms holds the " +
                    "total at or under 15/min, inside OpenRouter's 20/min new-account cap.",
                    new AcceptableValueRange<int>(0, 120000)));

            _cfgMaxPositionalOrderAgeSeconds = _config.Bind("Brain", "MaxPositionalOrderAgeSeconds", 240f,
                new ConfigDescription(
                    "Drop POSITION-DEPENDENT orders (MoveTo) if the picture they came from " +
                    "is older than this many GAME seconds on arrival. A waypoint derived " +
                    "from where a contact used to be is simply wrong once it has moved, and " +
                    "time compression makes decisions arrive stale - at 10x a 45s decision " +
                    "is 450 game-seconds out of date. 0 disables the check.",
                    new AcceptableValueRange<float>(0f, 3600f)));

            _cfgMaxPostureOrderAgeSeconds = _config.Bind("Brain", "MaxPostureOrderAgeSeconds", 0f,
                new ConfigDescription(
                    "Same, for orders that do NOT depend on position (SetSpeed, " +
                    "SetWeaponStatus). Force-level posture changes slowly, so these stay " +
                    "valid far longer than a waypoint - 0 (no limit) is the sensible " +
                    "default. Raise it only if you want stale posture discarded too.",
                    new AcceptableValueRange<float>(0f, 3600f)));
        }

        /// <summary>
        /// Constructs the brain for one task force. Swap the implementation here when
        /// moving from observation to a heuristic or a sidecar-backed brain.
        /// </summary>
        internal static IForceBrain CreateBrain()
        {
            var type = _cfgBrain != null ? _cfgBrain.Value : BrainType.Observing;

            switch (type)
            {
                case BrainType.Sidecar:
                    return new HttpBrain(
                        _cfgSidecarEndpoint.Value,
                        _cfgSidecarTimeoutMs.Value,
                        _cfgMinRequestGapMs.Value);

                default:
                    return new ObservingBrain(_cfgDumpPictureJson != null && _cfgDumpPictureJson.Value);
            }
        }
    }
}
