using System.Text.Json.Nodes;
using SeaPowerAICommander.Orders;

namespace SeaPowerAICommander.Sidecar;

/// <summary>
/// Builds the JSON schema the model must answer in.
///
/// The order-kind list is generated from <see cref="ForceOrderKind"/> - the same enum
/// OrderExecutor switches on. Add a kind there and the schema widens automatically, so
/// the model's vocabulary and the executor's capability cannot drift apart.
/// </summary>
public static class OrderSchema
{
    public const string SchemaName = "force_orders";

    public static JsonObject Build()
    {
        var kinds = new JsonArray();
        foreach (var name in Enum.GetNames<ForceOrderKind>())
            kinds.Add(name);

        var order = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["kind"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = kinds,
                    ["description"] = "The order type. MoveTo steers a SURFACE OR SUBSURFACE unit to a position (air units are refused - they fly their tasking), SetSpeed sets speed in knots, SetWeaponStatus sets Tight/Free/Hold, SetEmcon goes Silent or Radiate.",
                },
                ["unitId"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["description"] = "Id of one of YOUR OWN units, taken from ownUnits. Never a contact id.",
                },
                ["latitude"] = new JsonObject
                {
                    ["type"] = "number",
                    ["description"] = "MoveTo only. Decimal degrees, -90 to 90. Use 0 for other order kinds.",
                },
                ["longitude"] = new JsonObject
                {
                    ["type"] = "number",
                    ["description"] = "MoveTo only. Decimal degrees, -180 to 180. Use 0 for other order kinds.",
                },
                ["speedKnots"] = new JsonObject
                {
                    ["type"] = "number",
                    ["description"] = "SetSpeed only. 0 means all stop. Clamped to the unit's maximum. Use 0 for other order kinds.",
                },
                ["weaponStatus"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("Tight", "Free", "Hold"),
                    ["description"] = "SetWeaponStatus only. Use \"Tight\" for other order kinds.",
                },
                ["targetContactId"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["description"] = "AttackTarget and CoordinatedAttack only. Id of a CONTACT from the contacts list - never one of your own units. Use 0 for other order kinds.",
                },
                ["salvo"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["description"] = "AttackTarget and CoordinatedAttack only. How many rounds or missiles to commit. Use 1 for other order kinds.",
                },
                ["strikeType"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("Bomb", "Missile", "SEAD", "Jam"),
                    ["description"] = "LaunchAirstrike only. SEAD suppresses air defences, Jam is electronic attack. Use \"Bomb\" for other order kinds.",
                },
                ["coordinationGroup"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "CoordinatedAttack only. A label shared by every unit in one simultaneous attack, e.g. \"strike-alpha\". Their releases are timed so the weapons arrive together. Use \"\" for other order kinds.",
                },
                ["emcon"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("Silent", "Radiate"),
                    ["description"] = "SetEmcon only. \"Silent\" shuts down search radars, active sonar and jamming together; \"Radiate\" switches the search radars back on. Use \"Radiate\" for other order kinds.",
                },
                ["reason"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "One short sentence of tactical justification. Logged, not parsed.",
                },
            },
            // Strict mode requires every property listed as required, hence the
            // "use 0 / use Tight" notes above for fields a given kind ignores.
            ["required"] = new JsonArray(
                "kind", "unitId", "latitude", "longitude", "speedKnots", "weaponStatus",
                "targetContactId", "salvo", "coordinationGroup", "strikeType", "emcon", "reason"),
            ["additionalProperties"] = false,
        };

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                // Required even when no orders follow. A cycle that issues nothing
                // otherwise prints nothing, so a commander deliberately holding looks
                // identical to one that has stopped working - and the only place that
                // difference is visible is here.
                ["assessment"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "One or two sentences on the situation and what you are doing about it. Required every cycle, ESPECIALLY when you issue no orders - say why nothing needs changing.",
                },
                ["orders"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Orders to issue this cycle. Return an empty array when the current posture is already correct - doing nothing is a valid and often correct decision.",
                    ["items"] = order,
                },
            },
            ["required"] = new JsonArray("assessment", "orders"),
            ["additionalProperties"] = false,
        };
    }
}
