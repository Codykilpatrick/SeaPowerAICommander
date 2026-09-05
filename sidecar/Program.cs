using System.Net;
using System.Text;
using System.Text.Json;
using SeaPowerForceAI.Orders;
using SeaPowerForceAI.Picture;
using SeaPowerForceAI.Sidecar;

// Loopback-only HTTP listener. The mod POSTs a tactical picture; we answer with orders.
//
// Bound to 127.0.0.1 deliberately - this endpoint takes an API key's worth of spend per
// request and must not be reachable off the machine.

const string DefaultPrefix = "http://127.0.0.1:8787/";
const string DefaultModel = "anthropic/claude-opus-4.5";

var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("OPENROUTER_API_KEY is not set.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  Create a key at https://openrouter.ai/keys, then:");
    Console.Error.WriteLine("    setx OPENROUTER_API_KEY \"sk-or-...\"");
    Console.Error.WriteLine("  and open a new terminal.");
    return 1;
}

var model = Environment.GetEnvironmentVariable("FORCEAI_MODEL") ?? DefaultModel;
var prefix = Environment.GetEnvironmentVariable("FORCEAI_PREFIX") ?? DefaultPrefix;
var timeout = TimeSpan.FromSeconds(
    int.TryParse(Environment.GetEnvironmentVariable("FORCEAI_TIMEOUT_SECONDS"), out var t) ? t : 90);

using var client = new OpenRouterClient(apiKey, model, timeout);

using var listener = new HttpListener();
listener.Prefixes.Add(prefix);

try
{
    listener.Start();
}
catch (HttpListenerException ex)
{
    Console.Error.WriteLine($"Could not listen on {prefix}: {ex.Message}");
    Console.Error.WriteLine("Another process may already hold that port.");
    return 1;
}

Console.WriteLine($"Force AI sidecar listening on {prefix}");
Console.WriteLine($"Model: {model}   (override with FORCEAI_MODEL)");
Console.WriteLine("Waiting for the game. Ctrl+C to stop.");
Console.WriteLine();

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); listener.Stop(); };

while (!shutdown.IsCancellationRequested)
{
    HttpListenerContext ctx;
    try
    {
        ctx = await listener.GetContextAsync();
    }
    catch (Exception) when (shutdown.IsCancellationRequested)
    {
        break;
    }

    // Handle sequentially: the mod submits one picture at a time per task force, and
    // serialising keeps the console log readable and the spend predictable.
    await HandleAsync(ctx, client, shutdown.Token);
}

Console.WriteLine("Stopped.");
return 0;

static async Task HandleAsync(HttpListenerContext ctx, OpenRouterClient client, CancellationToken ct)
{
    var started = DateTime.UtcNow;
    try
    {
        if (ctx.Request.HttpMethod != "POST")
        {
            await WriteAsync(ctx, 405, "{\"error\":\"POST only\"}");
            return;
        }

        string payload;
        using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
            payload = await reader.ReadToEndAsync(ct);

        var picture = JsonSerializer.Deserialize<TacticalPicture>(payload, PictureJson.Options);
        if (picture is null)
        {
            await WriteAsync(ctx, 400, "{\"error\":\"could not parse picture\"}");
            return;
        }

        Console.WriteLine(
            $"[{DateTime.Now:HH:mm:ss}] {picture.TaskforceName} ({picture.Side}) " +
            $"t={picture.TimeSeconds:F0}s own={picture.OwnUnits.Count} contacts={picture.Contacts.Count}");

        var orders = await client.DecideAsync(picture, ct);

        var elapsed = (DateTime.UtcNow - started).TotalSeconds;
        Console.WriteLine($"  -> {orders.Orders.Count} order(s) in {elapsed:F1}s");
        foreach (var o in orders.Orders)
            Console.WriteLine($"     {o.Kind} unit {o.UnitId}: {o.Reason}");

        await WriteAsync(ctx, 200, JsonSerializer.Serialize(orders, PictureJson.Options));
    }
    catch (Exception ex)
    {
        // Never take the sidecar down over one bad cycle - the mod treats a failed
        // request as "no orders" and simply tries again next tick.
        Console.Error.WriteLine($"  ! {ex.Message}");
        try { await WriteAsync(ctx, 500, "{\"error\":\"decision failed\"}"); } catch { /* client gone */ }
    }
}

static async Task WriteAsync(HttpListenerContext ctx, int status, string body)
{
    var bytes = Encoding.UTF8.GetBytes(body);
    ctx.Response.StatusCode = status;
    ctx.Response.ContentType = "application/json";
    ctx.Response.ContentLength64 = bytes.Length;
    await ctx.Response.OutputStream.WriteAsync(bytes);
    ctx.Response.Close();
}
