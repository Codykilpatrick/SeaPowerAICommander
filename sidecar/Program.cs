using System.Net;
using System.Text;
using System.Text.Json;
using SeaPowerAICommander.Orders;
using SeaPowerAICommander.Picture;
using SeaPowerAICommander.Sidecar;

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

var model = Environment.GetEnvironmentVariable("AICOMMANDER_MODEL") ?? DefaultModel;
var prefix = Environment.GetEnvironmentVariable("AICOMMANDER_PREFIX") ?? DefaultPrefix;
// Matches the mod's SidecarTimeoutMs default. There are two timeouts on this path -
// the mod waiting on the sidecar, and the sidecar waiting on OpenRouter - and raising
// only the first left decisions still being abandoned at 90 seconds.
var timeout = TimeSpan.FromSeconds(
    int.TryParse(Environment.GetEnvironmentVariable("AICOMMANDER_TIMEOUT_SECONDS"), out var t) ? t : 150);

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

Console.WriteLine($"AI Commander sidecar listening on {prefix}");
Console.WriteLine($"Model: {model}   (override with AICOMMANDER_MODEL)");
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

    // NOT awaited. Awaiting here served one task force perfectly and broke the moment
    // there were three: a decision takes ~75s, so the second request waited 75s to
    // start and the third 150s, which is exactly the mod's SidecarTimeoutMs. Requests
    // died of old age in the accept queue while this loop was still talking to
    // OpenRouter about someone else, and because the request had never been read the
    // sidecar logged no error at all - 15 timeouts on the mod side against a clean
    // sidecar log.
    //
    // Nothing here needs a lock. The mod already allows one decision in flight per task
    // force (HttpBrain._inFlight, per-instance, one brain per TaskForceAI), and spend is
    // bounded by its static rate gate rather than by this await. What serialising
    // actually protected was console readability, which HandleAsync now handles by
    // printing each decision as one block.
    _ = HandleAsync(ctx, client, shutdown.Token);
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

    // Hoisted so the catch can name which task force failed. With decisions running
    // concurrently, a bare "! The operation has timed out" no longer says whose.
    TacticalPicture? picture = null;

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

        picture = JsonSerializer.Deserialize<TacticalPicture>(payload, PictureJson.Options);
        if (picture is null)
        {
            await WriteAsync(ctx, 400, "{\"error\":\"could not parse picture\"}");
            return;
        }

        // Buffered, not printed as it happens. Decisions now run concurrently, and three
        // task forces interleaving their headers, assessments and order lines would make
        // the log useless for exactly the debugging it exists for. Each decision is
        // written once, as one block, when it completes.
        var log = new StringBuilder();
        log.AppendLine(
            $"[{DateTime.Now:HH:mm:ss}] {picture.TaskforceName} ({picture.Side}) " +
            $"t={picture.TimeSeconds:F0}s own={picture.OwnUnits.Count} contacts={picture.Contacts.Count}");

        var orders = await client.DecideAsync(picture, ct, log);

        var elapsed = (DateTime.UtcNow - started).TotalSeconds;
        log.AppendLine($"  -> {orders.Orders.Count} order(s) in {elapsed:F1}s");
        foreach (var o in orders.Orders)
            log.AppendLine($"     {o.Kind} unit {o.UnitId}: {o.Reason}");

        // One write per decision. TeeWriter fans out to console and file, so the lock is
        // what keeps a block from being split across those two by a concurrent decision.
        lock (Log.Gate) Console.Write(log.ToString());

        await WriteAsync(ctx, 200, JsonSerializer.Serialize(orders, PictureJson.Options));
    }
    catch (Exception ex)
    {
        // Never take the sidecar down over one bad cycle - the mod treats a failed
        // request as "no orders" and simply tries again next tick.
        lock (Log.Gate) Console.Error.WriteLine($"  ! [{picture?.TaskforceName ?? "?"}] {ex.Message}");
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

/// <summary>
/// Serialises the one-block-per-decision writes in HandleAsync. A plain local will not do:
/// top-level statements cannot declare a static field, and the static local function
/// HandleAsync cannot capture a non-static local.
///
/// This is the ONLY lock the sidecar needs. The mod already allows one decision in flight
/// per task force, and spend is bounded by its own rate gate.
/// </summary>
static class Log
{
    public static readonly object Gate = new object();
}
