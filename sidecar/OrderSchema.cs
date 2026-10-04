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
                    ["description"] = "The order type. MoveTo steers a SURFACE OR SUBSURFACE unit to a position (air units are refused - they fly their tasking), SetSpeed sets speed in knots, SetWeaponStatus sets Tight/Free/Hold, SetEmcon goes Silent or Radiate. IdentifyContact sends a unit to find out what a contact is and is the ONLY way to redirect an aircraft already in the air, ReturnToBase recovers one, SetDepth puts a submarine in a depth band, SetSonar pings or streams a towed array, SetFormation reshapes a formation.",
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
                    ["description"] = "AttackTarget, CoordinatedAttack, LaunchAirstrike and IdentifyContact. Id of a CONTACT from the contacts list - never one of your own units. Use 0 for other order kinds.",
                },
                ["salvo"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["description"] = "How many to commit. AttackTarget and CoordinatedAttack: rounds or missiles. LaunchAircraft: how many airframes to put up - one call launches ONE aircraft, so use 2 for a pair on CAP. Max 4. Use 1 for other order kinds.",
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
                ["airMission"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("CAP", "AEW", "Recon", "MPA", "ASW", "Intercept"),
                    ["description"] = "LaunchAircraft only. The standing mission to launch on: CAP and Intercept are air defence, AEW and Recon and MPA extend your sensor picture, ASW hunts submarines. Use \"CAP\" for other order kinds.",
                },
                ["loadout"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "LaunchAirstrike only. The weapons fit to send, named EXACTLY as it appears in the ordering unit's airstrikeLoadouts - e.g. \"AntiShip\". Use \"\" to let the game choose, which it does by airframe count rather than by suitability. Naming one the base cannot fly is refused, so read airstrikeLoadouts first. Use \"\" for other order kinds.",
                },
                ["depth"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(
                        "Surface", "Periscope", "Shallow", "AboveLayer", "BelowLayer", "Deep", "VeryDeep"),
                    ["description"] = "SetDepth only, and submarines only. Bands, not feet - what each one means in feet depends on the boat and the local layer. AboveLayer and BelowLayer are relative to conditions.layerDepth. Use \"Shallow\" for other order kinds.",
                },
                ["sonar"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(
                        "ActiveOn", "ActiveOff", "DeployTowedArray", "RetractTowedArray",
                        "TowedArrayAboveLayer", "TowedArrayBelowLayer"),
                    ["description"] = "SetSonar only. ActiveOn/ActiveOff ping with the hull sonar - loud, and it gives your position away. The towed array is passive and separate: deploying it costs speed, and putting it above or below the layer decides which side of the thermocline you can hear. Use \"ActiveOff\" for other order kinds.",
                },
                ["formationPattern"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(
                        "Circle", "Vic", "Echelon", "LineAbreast", "LineAstern", "Box"),
                    ["description"] = "SetFormation only. Circle screens a high-value unit on every bearing, LineAbreast sweeps a front, LineAstern is a column for transiting or following a swept channel, Vic and Echelon and Box are directional screens. Use \"Circle\" for other order kinds.",
                },
                ["weapon"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("Auto", "Missile", "Torpedo", "Gun", "ASROC", "RBU"),
                    ["description"] = "AttackTarget and CoordinatedAttack only. Auto lets the unit's own allocation choose and is right most of the time. Name a type only when the choice matters, and only one the unit's weaponTypes lists - naming one it does not carry falls back to Auto. Ignored for aircraft. Use \"Auto\" for other order kinds.",
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
                "targetContactId", "salvo", "coordinationGroup", "strikeType", "emcon",
                "airMission", "loadout", "depth", "sonar", "formationPattern", "weapon", "reason"),
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
                // Last, so it is written after the orders and describes the plan they
                // serve rather than one the orders then drift away from.
                ["plan"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "Your note to yourself for the next decision - the only thing you will remember of this one. Rewrite it in full every cycle; anything you leave out is forgotten.",
                    ["properties"] = new JsonObject
                    {
                        ["intent"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "The course of action over the next several cycles and why, in two or three sentences. Not a list of this cycle's orders. Keep it unless the picture gives you a reason to change it, and when you change it, say what changed.",
                        },
                        ["watchFor"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "What would make you change the plan, as concretely as the picture allows - a contact id closing inside a range, a unit reaching a position, a classification firming up.",
                        },
                        ["lessons"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["description"] = "Durable facts the GAME told you that the picture will not repeat, one line each naming the unit: above all refusals that say they are permanent, and tactical-AI behaviour you decided to accept rather than fight. Never a guess about what a contact is - that belongs in watchFor. Carry each forward until it stops being true. At most 8.",
                            ["items"] = new JsonObject { ["type"] = "string" },
                        },
                    },
                    ["required"] = new JsonArray("intent", "watchFor", "lessons"),
                    ["additionalProperties"] = false,
                },
            },
            ["required"] = new JsonArray("assessment", "orders", "plan"),
            ["additionalProperties"] = false,
        };
    }
}
