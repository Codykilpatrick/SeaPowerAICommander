using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using SeaPowerAICommander.Orders;
using SeaPowerAICommander.Picture;

namespace SeaPowerAICommander.Sidecar;

/// <summary>
/// The commander's own note to itself, carried from one decision to the next.
///
/// Every decision is a fresh call, so without this the commander re-derives its intent
/// from scratch each cycle - and the picture's continuity fields say what HAPPENED (losses,
/// standing orders, orderProblems) but never what it was TRYING to do. One sub-duel run
/// showed the cost three ways: seven different speed orders across one transit, eleven
/// MoveTos mostly a few hundred metres apart, and five towed-array orders to a boat with no
/// array. The refusal said "do not order one for this unit again" every time, but
/// orderProblems carries a refusal for exactly one cycle, and two cycles later there was
/// nothing left to remember it by.
///
/// The model rewrites the plan in full every cycle rather than appending to it, which is
/// what keeps it bounded: whatever it leaves out is forgotten. The caps below are the
/// backstop for a model that ignores that.
///
/// Held here rather than in the mod so it needed only a sidecar restart. The cost is that
/// restarting the sidecar mid-mission forgets the plan; the next decision simply writes a
/// new one, which is the same thing that happens on the first cycle.
/// </summary>
public static class CommanderPlan
{
    public sealed record Entry(string Intent, string WatchFor, IReadOnlyList<string> Lessons, float WrittenAt);

    // Generous enough for a real plan, small enough that a model writing an essay into
    // intent cannot quietly double the uncached half of every request after it.
    private const int MaxFieldChars = 600;
    private const int MaxLessons = 8;
    private const int MaxLessonChars = 200;

    private static readonly ConcurrentDictionary<string, Entry> Plans = new();

    // The orders each force's last decision returned, kept so the next decision can be told
    // which of them never became standing. The mod drops positional orders from a picture
    // that went stale in flight - at time compression that was every MoveTo of a 134s
    // decision - and the commander, holding a plan that said "close on 227", then reported
    // its boats closing on waypoints that did not exist. standingOrders alone could not
    // catch it: an absence is invisible unless something says what should have been there.
    private static readonly ConcurrentDictionary<string, IReadOnlyList<ForceOrder>> LastOrders = new();

    // Per task force, not per side: two forces on one side are commanded separately and
    // would otherwise overwrite each other's plan every cycle. No lock needed - the mod
    // allows one decision in flight per task force, so each key has one writer.
    private static string Key(TacticalPicture p) => $"{p.MissionName}|{p.Side}|{p.TaskforceName}";

    /// <summary>
    /// The plan written at this force's previous decision, or null if there is none.
    /// </summary>
    public static Entry? Recall(TacticalPicture picture)
    {
        var key = Key(picture);

        // -1 means the mod has no prior decision for this task force, which is a new
        // mission or a restart of the same one. MissionName alone cannot tell those apart,
        // and a plan from the previous attempt would be orders for a battle that no longer
        // exists.
        if (picture.SecondsSinceLastDecision < 0f)
        {
            Plans.TryRemove(key, out _);
            LastOrders.TryRemove(key, out _);
            return null;
        }

        return Plans.TryGetValue(key, out var entry) ? entry : null;
    }

    /// <summary>
    /// Stores the plan from a decision response. A response with no usable plan leaves the
    /// previous one in place: one malformed answer should not wipe the commander's memory.
    /// </summary>
    public static Entry? Remember(TacticalPicture picture, JsonNode? node)
    {
        if (node is not JsonObject o) return null;

        var intent = Clip(Str(o["intent"]), MaxFieldChars);
        var watchFor = Clip(Str(o["watchFor"]), MaxFieldChars);

        var lessons = new List<string>();
        if (o["lessons"] is JsonArray array)
        {
            foreach (var item in array)
            {
                var lesson = Clip(Str(item), MaxLessonChars);
                if (lesson.Length == 0) continue;
                lessons.Add(lesson);
                if (lessons.Count == MaxLessons) break;
            }
        }

        if (intent.Length == 0 && watchFor.Length == 0 && lessons.Count == 0) return null;

        var entry = new Entry(intent, watchFor, lessons, picture.TimeSeconds);
        Plans[Key(picture)] = entry;
        return entry;
    }

    /// <summary>Records the orders a decision returned, for the next decision's check.</summary>
    public static void RecordOrders(TacticalPicture picture, IReadOnlyList<ForceOrder> orders) =>
        LastOrders[Key(picture)] = orders.ToList();

    /// <summary>
    /// The plan as it is put in front of the commander, ahead of the picture.
    /// </summary>
    public static void AppendTo(StringBuilder sb, Entry? plan, TacticalPicture picture)
    {
        AppendNotStanding(sb, picture);

        if (plan is null)
        {
            sb.AppendLine("YOUR PLAN: none yet. Write one this cycle.");
            sb.AppendLine();
            return;
        }

        // TimeSeconds is the monotonic clock, so this difference is valid across missions
        // in one process - unlike the detection epochs, which are not on the same clock.
        var age = picture.TimeSeconds - plan.WrittenAt;

        sb.AppendLine($"YOUR PLAN, as you wrote it {age:F0}s ago at mission time {plan.WrittenAt:F0}s:");
        if (plan.Intent.Length > 0) sb.AppendLine($"  Intent: {plan.Intent}");
        if (plan.WatchFor.Length > 0) sb.AppendLine($"  Watching for: {plan.WatchFor}");
        if (plan.Lessons.Count > 0)
        {
            sb.AppendLine("  Learned:");
            foreach (var lesson in plan.Lessons)
                sb.AppendLine($"    - {lesson}");
        }
        sb.AppendLine("This is your own note, written on less information than you have now. Where the picture below disagrees with it, the picture is right.");
        sb.AppendLine();
    }

    /// <summary>
    /// Lists last decision's orders that are not among the standing orders now.
    ///
    /// The mod keys standing orders by unit and kind, and a later order replaces an earlier
    /// one, so a match on unit and kind alone would pass a dropped SetSpeed 5 because an
    /// older SetSpeed 15 is still standing. Hence the comparison on parameters too - only
    /// those the mod replays; it does not carry emcon, loadout or air mission back.
    /// </summary>
    private static void AppendNotStanding(StringBuilder sb, TacticalPicture picture)
    {
        if (!LastOrders.TryGetValue(Key(picture), out var last) || last.Count == 0) return;

        var standing = new HashSet<string>(picture.StandingOrders.Select(Signature));
        var missing = last.Where(o => !standing.Contains(Signature(o))).ToList();
        if (missing.Count == 0) return;

        sb.AppendLine("NOT STANDING - ordered at your last decision, absent from standingOrders now:");
        foreach (var o in missing)
            sb.AppendLine($"  - {Describe(o)}");
        sb.AppendLine("Each was dropped because the picture went stale before it arrived (waypoints are " +
                      "discarded first), refused (see orderProblems), lost with its unit, or was an attack " +
                      "that has since released or lost its target. None of them is under way. Reissue " +
                      "only what you still want.");
        sb.AppendLine();
    }

    private static string Signature(ForceOrder o) => string.Join("|",
        o.Kind, o.UnitId, o.TargetContactId,
        Math.Round(o.Latitude, 3), Math.Round(o.Longitude, 3), Math.Round(o.SpeedKnots, 1),
        o.Kind == ForceOrderKind.SetWeaponStatus ? o.WeaponStatus : "",
        o.Kind == ForceOrderKind.SetDepth ? o.Depth : "",
        o.Kind == ForceOrderKind.SetSonar ? o.Sonar : "",
        o.Kind == ForceOrderKind.SetFormation ? o.FormationPattern : "");

    private static string Describe(ForceOrder o)
    {
        var s = $"{o.Kind} unit {o.UnitId}";
        if (o.TargetContactId > 0) s += $" on contact {o.TargetContactId}";
        return o.Kind switch
        {
            ForceOrderKind.MoveTo => s + $" to {o.Latitude:F4},{o.Longitude:F4}",
            ForceOrderKind.SetSpeed => s + $" {o.SpeedKnots:F0}kt",
            ForceOrderKind.SetWeaponStatus => s + $" {o.WeaponStatus}",
            ForceOrderKind.SetDepth => s + $" {o.Depth}",
            ForceOrderKind.SetSonar => s + $" {o.Sonar}",
            ForceOrderKind.SetFormation => s + $" {o.FormationPattern}",
            _ => s,
        };
    }

    /// <summary>The plan in the sidecar log, so a post-mortem can see what the commander carried.</summary>
    public static void AppendLog(StringBuilder log, Entry? plan)
    {
        if (plan is null)
        {
            log.AppendLine("     plan: (none returned - previous plan kept)");
            return;
        }

        log.AppendLine($"     plan: {plan.Intent}");
        if (plan.WatchFor.Length > 0) log.AppendLine($"     watch: {plan.WatchFor}");
        foreach (var lesson in plan.Lessons)
            log.AppendLine($"     learned: {lesson}");
    }

    // Tolerant of a wrong type. A plan is memory, not orders, so a number where a string
    // belongs should cost that one field rather than throw away the whole decision.
    private static string? Str(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string Clip(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        s = s.Trim();
        return s.Length <= max ? s : s[..max] + "...";
    }
}
