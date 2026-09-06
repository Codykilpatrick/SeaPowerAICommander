using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SeaPowerForceAI.Orders;
using SeaPowerForceAI.Picture;

namespace SeaPowerForceAI.Sidecar;

/// <summary>
/// Talks to OpenRouter's OpenAI-compatible chat completions endpoint.
///
/// Raw HTTP rather than an SDK: the surface is one call, and hand-writing it avoids
/// SDK assumptions that don't hold against a third-party gateway whose responses carry
/// provider-specific extras.
/// </summary>
public sealed class OpenRouterClient : IDisposable
{
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";

    private readonly HttpClient _http;
    private readonly string _model;

    /// <summary>
    /// Ceiling on reasoning tokens per decision. Left unbounded, reasoning grows with how
    /// hard the tactical situation is - measured at ~700 tokens with nothing in contact and
    /// an extrapolated ~7,500 in a six-contact engagement, which is where decisions began
    /// arriving after the game had stopped listening. A commander that answers late has not
    /// answered. Tune with FORCEAI_REASONING_TOKENS; 0 removes the ceiling.
    /// </summary>
    private readonly int _reasoningMaxTokens =
        int.TryParse(Environment.GetEnvironmentVariable("FORCEAI_REASONING_TOKENS"), out var r) ? r : 2048;

    public OpenRouterClient(string apiKey, string model, TimeSpan timeout)
    {
        _model = model;
        _http = new HttpClient { Timeout = timeout };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        // Optional attribution headers OpenRouter uses to identify the calling app.
        _http.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/Codykilpatrick/SeaPowerForceAI");
        _http.DefaultRequestHeaders.Add("X-Title", "Sea Power Force AI");
    }

    /// <summary>
    /// A plain question with no schema, used for the one-off objective derivation.
    /// </summary>
    public async Task<string> AskAsync(string system, string user, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = _model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }),
        };

        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(Endpoint, content, ct).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenRouter returned {(int)response.StatusCode}: {Truncate(raw, 400)}");

        var root = JsonNode.Parse(raw) as JsonObject
                   ?? throw new InvalidOperationException("Response was not a JSON object.");

        return root["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? string.Empty;
    }

    public async Task<ForceOrderSet> DecideAsync(TacticalPicture picture, CancellationToken ct)
    {
        // Resolve the objective before deciding. Derived once per mission and cached, so
        // this costs an extra call on the first cycle only.
        picture.Objective = await ObjectiveDeriver.ResolveAsync(picture, AskAsync, ct).ConfigureAwait(false);

        var body = new JsonObject
        {
            ["model"] = _model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = CommanderPrompt.System },
                new JsonObject { ["role"] = "user", ["content"] = CommanderPrompt.BuildUserMessage(picture) }),
            // Usage accounting is opt-in on OpenRouter. Without it we cannot tell a slow
            // decision caused by a large picture from one caused by long reasoning.
            ["usage"] = new JsonObject { ["include"] = true },
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = OrderSchema.SchemaName,
                    ["strict"] = true,
                    ["schema"] = OrderSchema.Build(),
                },
            },
        };

        if (_reasoningMaxTokens > 0)
            body["reasoning"] = new JsonObject { ["max_tokens"] = _reasoningMaxTokens };

        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(Endpoint, content, ct).ConfigureAwait(false);

        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenRouter returned {(int)response.StatusCode}: {Truncate(raw, 400)}");

        return Parse(raw, picture);
    }

    /// <summary>
    /// Prints what the decision cost in tokens. Reasoning tokens are broken out because
    /// they are the part that grows with how hard the tactical problem is rather than with
    /// how big the picture is, and they are invisible in the returned text.
    /// </summary>
    private static void ReportUsage(JsonObject root)
    {
        if (root["usage"] is not JsonObject usage) return;

        var prompt = usage["prompt_tokens"]?.GetValue<int>() ?? 0;
        var completion = usage["completion_tokens"]?.GetValue<int>() ?? 0;
        var reasoning = usage["completion_tokens_details"]?["reasoning_tokens"]?.GetValue<int>() ?? 0;
        var cached = usage["prompt_tokens_details"]?["cached_tokens"]?.GetValue<int>() ?? 0;

        var visible = completion - reasoning;
        Console.WriteLine(
            $"     tokens: in {prompt} ({cached} cached), out {completion} " +
            $"({reasoning} reasoning + {visible} orders)");
    }

    private static ForceOrderSet Parse(string raw, TacticalPicture picture)
    {
        var root = JsonNode.Parse(raw) as JsonObject
                   ?? throw new InvalidOperationException("Response was not a JSON object.");

        // OpenRouter surfaces upstream provider failures as a 200 with an error body.
        if (root["error"] is JsonNode err)
            throw new InvalidOperationException($"OpenRouter error: {Truncate(err.ToJsonString(), 400)}");

        var message = root["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(message))
            throw new InvalidOperationException($"No message content in response: {Truncate(raw, 400)}");

        var parsed = JsonNode.Parse(message) as JsonObject
                     ?? throw new InvalidOperationException("Model content was not a JSON object.");

        var set = new ForceOrderSet
        {
            TaskforceName = picture.TaskforceName,
            DerivedFromTime = picture.TimeSeconds,
        };

        // Printed here rather than carried in the contract, so this needed only a sidecar
        // restart to land mid-session.
        ReportUsage(root);

        var assessment = parsed["assessment"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(assessment))
            Console.WriteLine($"     assessment: {assessment}");

        if (parsed["orders"] is not JsonArray array) return set;

        foreach (var node in array)
        {
            if (node is not JsonObject o) continue;

            var kindText = o["kind"]?.GetValue<string>();
            if (!Enum.TryParse<ForceOrderKind>(kindText, ignoreCase: true, out var kind))
            {
                Console.Error.WriteLine($"  ! dropping order with unknown kind '{kindText}'");
                continue;
            }

            set.Orders.Add(new ForceOrder
            {
                Kind = kind,
                UnitId = (int)(o["unitId"]?.GetValue<double>() ?? -1),
                Latitude = o["latitude"]?.GetValue<double>() ?? 0.0,
                Longitude = o["longitude"]?.GetValue<double>() ?? 0.0,
                SpeedKnots = (float)(o["speedKnots"]?.GetValue<double>() ?? 0.0),
                WeaponStatus = o["weaponStatus"]?.GetValue<string>() ?? "Tight",
                TargetContactId = (int)(o["targetContactId"]?.GetValue<double>() ?? 0),
                Salvo = (int)(o["salvo"]?.GetValue<double>() ?? 1),
                CoordinationGroup = o["coordinationGroup"]?.GetValue<string>() ?? string.Empty,
                StrikeType = o["strikeType"]?.GetValue<string>() ?? "Bomb",
                Reason = o["reason"]?.GetValue<string>() ?? string.Empty,
            });
        }

        return set;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";

    public void Dispose() => _http.Dispose();
}
