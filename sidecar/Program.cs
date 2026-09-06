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
// Matches the mod's SidecarTimeoutMs default. There are two timeouts on this path -
// the mod waiting on the sidecar, and the sidecar waiting on OpenRouter - and raising
// only the first left decisions still being abandoned at 90 seconds.
var timeout = TimeSpan.FromSeconds(
    int.TryParse(Environment.GetEnvironmentVariable("FORCEAI_TIMEOUT_SECONDS"), out var t) ? t : 150);

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

// Everything the console shows is also written to a file. The assessments are the only
// place the commander's reasoning is visible, and reading them back should not depend on
// someone having the terminal open at the time.
var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
Directory.CreateDirectory(logDir);
var logPath = Path.Combine(logDir, $"sidecar-{DateTime.Now:yyyyMMdd-HHmmss}.log");
var logFile = new StreamWriter(logPath, append: true) { AutoFlush = true };
Console.SetOut(new TeeWriter(Console.Out, logFile));
Console.SetError(new TeeWriter(Console.Error, logFile));
Console.WriteLine($"Logging to {logPath}");

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

/// <summary>
/// Keeps the exact bytes the mod sent. Latency work needs to replay a real picture
/// against the model repeatedly, and a live game session is a slow and lossy way to
/// obtain one - a decision that times out leaves nothing behind to study.
/// </summary>
static void SavePicture(string payload)
{
    try
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "pictures");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"picture-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, payload);
    }
    catch (Exception ex)
    {
        // Never fail a decision over diagnostics.
        Console.Error.WriteLine($"  ! could not save picture: {ex.Message}");
    }
}

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

        SavePicture(payload);

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

/// <summary>Writes to the console and to a file at once, so a session leaves a record.</summary>
sealed class TeeWriter : TextWriter
{
    private readonly TextWriter _a;
    private readonly TextWriter _b;

    public TeeWriter(TextWriter a, TextWriter b) { _a = a; _b = b; }

    public override Encoding Encoding => _a.Encoding;

    public override void Write(char value) { _a.Write(value); _b.Write(value); }
    public override void Write(string? value) { _a.Write(value); _b.Write(value); }
    public override void WriteLine(string? value) { _a.WriteLine(value); _b.WriteLine(value); }
    public override void Flush() { _a.Flush(); _b.Flush(); }
}
