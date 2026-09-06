using System;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using SeaPowerForceAI.Orders;
using SeaPowerForceAI.Picture;

namespace SeaPowerForceAI
{
    /// <summary>
    /// Brain backed by the out-of-process sidecar.
    ///
    /// The whole point of the submit/poll interface lives here: a decision can take many
    /// seconds, and the game thread must never wait for it. Submit fires a background
    /// request and returns immediately; TryTakeOrders picks up the result whenever it
    /// lands, possibly several ticks later.
    /// </summary>
    public class HttpBrain : IForceBrain
    {
        /// <summary>
        /// camelCase on the wire. Newtonsoft defaults to the member name as-written
        /// (PascalCase); the sidecar's System.Text.Json is configured for camelCase and is
        /// case-SENSITIVE by default. Without this every property silently fails to bind
        /// and the sidecar receives a picture where every field is at its default - an
        /// empty task force with no name, no units and no contacts, which looks like a
        /// quiet battle rather than a bug.
        /// </summary>
        private static readonly JsonSerializerSettings WireSettings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
        };

        /// <summary>
        /// Wall-clock floor shared across every task force's brain.
        ///
        /// The tick interval is GAME seconds, so time compression multiplies the request
        /// rate, and each task force carries its own brain instance - together they can
        /// burst well past a provider's per-minute limit. This gate is the backstop.
        /// </summary>
        private static readonly object RateGate = new object();
        private static DateTime _lastRequestUtc = DateTime.MinValue;

        private readonly string _endpoint;
        private readonly int _timeoutMs;
        private readonly int _minRequestGapMs;

        // Written by the worker thread, read by the Unity thread.
        private volatile ForceOrderSet _ready;
        private volatile bool _inFlight;

        /// <summary>Ticks skipped while the current decision is outstanding, for logging.</summary>
        private int _skippedSinceSubmit;

        public HttpBrain(string endpoint, int timeoutMs, int minRequestGapMs)
        {
            _endpoint = endpoint;
            _timeoutMs = timeoutMs;
            _minRequestGapMs = minRequestGapMs;
        }

        public void Submit(TacticalPicture picture)
        {
            // Drop rather than queue. A stale naval picture is worse than no picture -
            // acting on where the enemy was two minutes ago is its own kind of wrong.
            if (_inFlight)
            {
                // Info, not Debug. BepInEx's disk logger is configured for
                // "Fatal, Error, Warning, Message, Info", so LogDebug never reaches the
                // file - and at high time compression this is THE reason most ticks
                // produce no decision. Hiding it makes the mod look inert.
                //
                // Throttled so it reports the backlog once per skipped decision rather
                // than once per tick.
                _skippedSinceSubmit++;
                if (_skippedSinceSubmit == 1 || _skippedSinceSubmit % 10 == 0)
                {
                    Plugin.Log.LogInfo(
                        $"[brain] {picture.TaskforceName}: tick skipped, decision still in flight " +
                        $"({_skippedSinceSubmit} skipped since it started)");
                }
                return;
            }

            _skippedSinceSubmit = 0;

            // Wall-clock backstop against time compression and many task forces at once.
            lock (RateGate)
            {
                var since = (DateTime.UtcNow - _lastRequestUtc).TotalMilliseconds;
                if (since < _minRequestGapMs)
                {
                    Plugin.Log.LogInfo(
                        $"[brain] rate gate: skipping {picture.TaskforceName}, " +
                        $"{since:F0}ms since last request (floor {_minRequestGapMs}ms)");
                    return;
                }
                _lastRequestUtc = DateTime.UtcNow;
            }

            _inFlight = true;

            string body;
            try
            {
                body = JsonConvert.SerializeObject(picture, WireSettings);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[brain] could not serialize picture: {ex.Message}");
                _inFlight = false;
                return;
            }

            var thread = new Thread(() => Work(body, picture.TaskforceName))
            {
                IsBackground = true,
                Name = "ForceAI-Brain",
            };
            thread.Start();
        }

        public bool TryTakeOrders(out ForceOrderSet orders)
        {
            orders = _ready;
            if (orders == null) return false;

            _ready = null;
            return true;
        }

        private void Work(string body, string taskforceName)
        {
            var startedUtc = DateTime.UtcNow;
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(_endpoint);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Timeout = _timeoutMs;
                request.ReadWriteTimeout = _timeoutMs;

                var payload = Encoding.UTF8.GetBytes(body);
                request.ContentLength = payload.Length;
                using (var stream = request.GetRequestStream())
                    stream.Write(payload, 0, payload.Length);

                string text;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new System.IO.StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    text = reader.ReadToEnd();

                var set = JsonConvert.DeserializeObject<ForceOrderSet>(text);
                if (set == null)
                {
                    Plugin.Log.LogWarning($"[brain] empty response for {taskforceName}");
                    return;
                }

                // Real-time latency is what makes decisions go stale under time
                // compression, so record it rather than inferring it.
                var seconds = (DateTime.UtcNow - startedUtc).TotalSeconds;
                Plugin.Log.LogInfo(
                    $"[brain] {taskforceName}: decision returned in {seconds:F1}s real " +
                    $"({set.Orders.Count} order(s))");

                _ready = set;
            }
            catch (WebException ex)
            {
                // Sidecar not running, or it failed this cycle. Not fatal - the next tick
                // tries again, and the tactical AI keeps fighting in the meantime.
                Plugin.Log.LogWarning($"[brain] request failed for {taskforceName}: {ex.Message}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[brain] {taskforceName}: {ex}");
            }
            finally
            {
                _inFlight = false;
            }
        }
    }
}
