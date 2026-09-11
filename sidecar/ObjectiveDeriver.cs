using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using SeaPowerAICommander.Picture;

namespace SeaPowerAICommander.Sidecar;

/// <summary>
/// Works out what the AI force is actually trying to achieve.
///
/// Without an objective a commander optimises for survival, and withdrawal is always the
/// right answer to that - which is exactly what one does, every cycle, however good its
/// tactical reasoning. An objective is what makes risk worth taking.
///
/// There are two cases, and conflating them is worse than having no objective at all.
///
/// Driving the ENEMY, the mission's objectives are the player's and cannot be handed
/// over. They are read once to infer what this side's orders would plausibly have been -
/// the scenario author designed both sides, so the opposing briefing is the best evidence
/// of the situation - and never mirrored down to specifics.
///
/// Driving the PLAYER's own delegated force, those same objectives ARE this force's
/// orders. They are adopted, not inferred against. Running the enemy path here produced
/// an objective opposed to the player's actual mission, which reads as bad tactical
/// judgement rather than the plumbing fault it is.
///
/// Either result is cached: a commander whose mission changed between cycles would be
/// incoherent.
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
        - Units and platforms keep whose they are. Inverting the PERSPECTIVE does not
          invert ownership: a submarine the briefing calls theirs is theirs, and a ship it
          calls ours is ours. A commander was once told to hunt an Oscar SSGN that was its
          own boat, because the briefing mentioned one and the side got flipped with the
          rest of the reasoning.
        """;

    /// <summary>
    /// Own-side framing: the briefings ARE this force's orders, so they are restated
    /// rather than planned against.
    ///
    /// A separate prompt rather than a flag inside the other one. Every rule in
    /// <see cref="DerivationSystem"/> exists to stop the model adopting what it reads,
    /// and here adopting it is the entire job - so there is nothing shared to factor out.
    /// </summary>
    private const string RestatementSystem = """
        You are reading a naval wargame briefing written for the force you are about to
        command. These are its own orders.

        Restate them as a standing objective: two or three sentences, second person,
        addressed to that commander. State what the force is to achieve and what risk is
        worth taking for it.

        Rules:
        - ADOPT the briefing. Do not invert it, and do not plan against it - it is yours.
        - Keep it a standing objective, not a running commentary. Drop anything that was
          only true at the start, such as opening positions or a first waypoint.
        - Say plainly what the force is FOR. A commander with only "survive" will withdraw
          and achieve nothing.
        - Where the briefing states a constraint - rules of engagement, a place to hold, a
          thing not to lose - keep it. Those are the orders too.
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

        var hasDescription = !string.IsNullOrWhiteSpace(picture.MissionDescription);
        if (!hasDescription && picture.MissionBriefings.Count == 0)
            return string.Empty;

        var ownSide = picture.BriefingIsOwnSide;

        var sb = new StringBuilder();
        sb.AppendLine($"Scenario: {picture.MissionName}");
        sb.AppendLine($"You are writing orders for the {picture.Side} side.");
        sb.AppendLine();

        if (hasDescription)
        {
            // Neutral setup naming both sides - the best evidence of the situation.
            sb.AppendLine("The scenario is described as:");
            sb.AppendLine(picture.MissionDescription);
            sb.AppendLine();
        }

        if (picture.MissionBriefings.Count > 0)
        {
            sb.AppendLine(ownSide
                ? "Your orders read:"
                : "The other side's orders read:");
            foreach (var o in picture.MissionBriefings)
                sb.AppendLine($"  - {o}");
        }

        try
        {
            var system = ownSide ? RestatementSystem : DerivationSystem;
            var derived = await ask(system, sb.ToString(), ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(derived)) return string.Empty;

            derived = derived.Trim();
            Cache[key] = derived;

            Console.WriteLine();
            Console.WriteLine(ownSide
                ? $"=== Restated objective for {picture.MissionName} ({picture.Side}, own briefing) ==="
                : $"=== Derived objective for {picture.MissionName} ({picture.Side}) ===");
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
