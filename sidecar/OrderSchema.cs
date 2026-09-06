using System.Text.Json.Nodes;
using SeaPowerForceAI.Orders;

namespace SeaPowerForceAI.Sidecar;

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
                    ["description"] = "The order type. MoveTo steers to a position, SetSpeed sets speed in knots, SetWeaponStatus sets Tight/Free/Hold.",
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
                ["coordinationGroup"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "CoordinatedAttack only. A label shared by every unit in one simultaneous attack, e.g. \"strike-alpha\". Their releases are timed so the weapons arrive together. Use \"\" for other order kinds.",
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
                "targetContactId", "salvo", "coordinationGroup", "reason"),
            ["additionalProperties"] = false,
        };

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["orders"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Orders to issue this cycle. Return an empty array when the current posture is already correct - doing nothing is a valid and often correct decision.",
                    ["items"] = order,
                },
            },
            ["required"] = new JsonArray("orders"),
            ["additionalProperties"] = false,
        };
    }
}
