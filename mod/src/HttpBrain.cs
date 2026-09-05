using System;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
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
        private readonly string _endpoint;
        private readonly int _timeoutMs;

        // Written by the worker thread, read by the Unity thread.
        private volatile ForceOrderSet _ready;
        private volatile bool _inFlight;

        public HttpBrain(string endpoint, int timeoutMs)
        {
            _endpoint = endpoint;
            _timeoutMs = timeoutMs;
        }

        public void Submit(TacticalPicture picture)
        {
            // Drop rather than queue. A stale naval picture is worse than no picture -
            // acting on where the enemy was two minutes ago is its own kind of wrong.
            if (_inFlight)
            {
                Plugin.Log.LogDebug($"[brain] skipping submit for {picture.TaskforceName}, request still in flight");
                return;
            }

            _inFlight = true;

            string body;
            try
            {
                body = JsonConvert.SerializeObject(picture);
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
