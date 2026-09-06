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

        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(Endpoint, content, ct).ConfigureAwait(false);

        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenRouter returned {(int)response.StatusCode}: {Truncate(raw, 400)}");

        return Parse(raw, picture);
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
