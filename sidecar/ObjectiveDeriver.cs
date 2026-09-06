using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using SeaPowerForceAI.Picture;

namespace SeaPowerForceAI.Sidecar;

/// <summary>
/// Works out what the AI force is actually trying to achieve.
///
/// Without an objective a commander optimises for survival, and withdrawal is always the
/// right answer to that - which is exactly what one does, every cycle, however good its
/// tactical reasoning. An objective is what makes risk worth taking.
///
/// The mission's own objectives are authored for the player, so they cannot simply be
/// handed over. Instead they are read once per mission to infer what THIS side's orders
/// would plausibly have been, since the scenario author designed both sides and the
/// opposing briefing is the best evidence of the situation. The result is cached: a
/// commander whose mission changed between cycles would be incoherent.
/// </summary>
public static class ObjectiveDeriver
{
    private static readonly ConcurrentDictionary<string, string> Cache = new();

    private const string DerivationSystem = """
        You are reading a naval wargame scenario briefing written for one side, and
        inferring what the OPPOSING commander's orders would have been.

        Write those orders: two or three sentences, second person, addressed to that
        commander. State what they are to achieve and what risk is worth taking for it.

        Rules:
        - Infer a POSTURE, not a mirror. "Prevent hostile warships transiting the strait"
          is right; "protect the frigate at 26.1N 56.3E" is not.
        - Never include specific enemy unit names, positions, timings or strengths taken
          from the briefing. That side has not detected any of it yet, and orders written
          from the other side's plan would be intelligence they do not have.
        - Say plainly what the force is FOR. A commander with only "survive" will withdraw
          and achieve nothing.
        - If the briefing is too thin to infer anything, give a general area-denial
          mission suited to the forces described.
        """;

    /// <summary>
    /// Returns the objective for this picture's force, deriving and caching it on first
    /// sight of a mission. A configured objective always wins.
    /// </summary>
    public static async Task<string> ResolveAsync(
        TacticalPicture picture,
        Func<string, string, CancellationToken, Task<string>> ask,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(picture.Objective))
            return picture.Objective;

        var key = $"{picture.MissionName}|{picture.Side}";

        if (Cache.TryGetValue(key, out var cached))
            return cached;

        if (picture.OpposingObjectives.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine($"Scenario: {picture.MissionName}");
        sb.AppendLine($"You are writing orders for the {picture.Side} side.");
        sb.AppendLine();
        sb.AppendLine("The other side's briefing states:");
        foreach (var o in picture.OpposingObjectives)
            sb.AppendLine($"  - {o}");

        try
        {
            var derived = await ask(DerivationSystem, sb.ToString(), ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(derived)) return string.Empty;

            derived = derived.Trim();
            Cache[key] = derived;

            Console.WriteLine();
            Console.WriteLine($"=== Derived objective for {picture.MissionName} ({picture.Side}) ===");
            Console.WriteLine(derived);
            Console.WriteLine();

            return derived;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  ! could not derive objective: {ex.Message}");
            return string.Empty;
        }
    }
}
