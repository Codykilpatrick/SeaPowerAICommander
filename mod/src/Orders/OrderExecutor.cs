using System;
using System.Collections.Generic;
using SeaPower;

namespace SeaPowerForceAI.Orders
{
    /// <summary>
    /// Applies a <see cref="ForceOrderSet"/> to the game.
    ///
    /// This is the validation boundary. Orders may come from a heuristic, a sidecar
    /// process, or a language model - none of them are trusted. Every order is checked
    /// against the live task force before it touches a unit, and a bad order is dropped
    /// with a log line rather than thrown.
    /// </summary>
    public static class OrderExecutor
    {
        /// <summary>
        /// Applies a set of orders and returns those that actually took effect, so they
        /// can be replayed to the brain next cycle as standing orders. Replaying a
        /// rejected order would tell the brain a sunk ship is still under way.
        /// </summary>
        public static List<ForceOrder> Apply(Taskforce tf, ForceOrderSet set)
        {
            var accepted = new List<ForceOrder>();
            if (tf == null || set == null || set.Orders == null) return accepted;

            var byId = IndexOwnUnits(tf);
            int rejected = 0;

            // Coordinated attacks are planned as a group, not executed one by one - the
            // whole point is that their release times depend on each other.
            var groups = new Dictionary<string, List<ForceOrder>>();
            foreach (var order in set.Orders)
            {
                if (order == null || order.Kind != ForceOrderKind.CoordinatedAttack) continue;

                var key = string.IsNullOrEmpty(order.CoordinationGroup) ? "default" : order.CoordinationGroup;
                List<ForceOrder> members;
                if (!groups.TryGetValue(key, out members))
                {
                    members = new List<ForceOrder>();
                    groups[key] = members;
                }
                members.Add(order);
            }

            foreach (var group in groups)
            {
                var scheduled = AttackScheduler.Schedule(
                    group.Key,
                    group.Value,
                    id => { ObjectBase u; return byId.TryGetValue(id, out u) ? u : null; },
                    id => ResolveContact(tf, id));

                accepted.AddRange(scheduled);
                rejected += group.Value.Count - scheduled.Count;
            }

            foreach (var order in set.Orders)
            {
                if (order == null) { rejected++; continue; }

                // Already handled above as part of a coordination group.
                if (order.Kind == ForceOrderKind.CoordinatedAttack) continue;

                ObjectBase unit;
                if (!byId.TryGetValue(order.UnitId, out unit))
                {
                    // Not ours, sunk between the picture being sent and the orders coming
                    // back, or hallucinated. Never trust the id.
                    Plugin.Log.LogWarning($"[orders] rejected {order.Kind} - unit {order.UnitId} not in {tf._nameInMissionFile}");
                    rejected++;
                    continue;
                }

                try
                {
                    if (ApplyOne(unit, order)) accepted.Add(order);
                    else rejected++;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"[orders] {order.Kind} on {unit.getName()} threw: {ex.Message}");
                    rejected++;
                }
            }

            if (accepted.Count > 0 || rejected > 0)
                Plugin.Log.LogInfo($"[orders] {tf._nameInMissionFile}: applied {accepted.Count}, rejected {rejected}");

            return accepted;
        }

        private static bool ApplyOne(ObjectBase unit, ForceOrder order)
        {
            switch (order.Kind)
            {
                case ForceOrderKind.MoveTo:
                    return MoveTo(unit, order);

                case ForceOrderKind.SetSpeed:
                    return SetSpeed(unit, order);

                case ForceOrderKind.SetWeaponStatus:
                    return SetWeaponStatus(unit, order);

                case ForceOrderKind.AttackTarget:
                case ForceOrderKind.CoordinatedAttack:
                    return AttackTarget(unit, order);

                case ForceOrderKind.Disengage:
                    return Disengage(unit);

                default:
                    Plugin.Log.LogWarning($"[orders] unknown kind {order.Kind}");
                    return false;
            }
        }

        private static bool MoveTo(ObjectBase unit, ForceOrder order)
        {
            if (order.Latitude < -90.0 || order.Latitude > 90.0 ||
                order.Longitude < -180.0 || order.Longitude > 180.0)
            {
                Plugin.Log.LogWarning($"[orders] MoveTo out of range: {order.Latitude},{order.Longitude}");
                return false;
            }

            unit.RemoveWaypoints();
            unit.setWaypointTask(
                new GeoPosition(order.Latitude, order.Longitude),
                "force-ai",
                WaypointData.WaypointHeightState.NoChange);
            return true;
        }

        private static bool SetSpeed(ObjectBase unit, ForceOrder order)
        {
            if (order.SpeedKnots < 0f)
            {
                Plugin.Log.LogWarning($"[orders] negative speed {order.SpeedKnots}");
                return false;
            }

            if (order.SpeedKnots <= 0.01f)
            {
                unit.SetSpeedCommand(new ZeroSpeed());
                return true;
            }

            // Clamp rather than reject - a brain asking for flank is not an error.
            var max = unit.MaxForwardSpeedInKnots;
            var knots = (max > 0f && order.SpeedKnots > max) ? max : order.SpeedKnots;

            unit.SetSpeedCommand(new ConstantSpeed(knots, unit));
            return true;
        }

        /// <summary>
        /// Engage a named contact.
        ///
        /// The target is resolved through the task force's OWN plotting table, never the
        /// global object list. A commander cannot order an attack on something it has not
        /// detected, which is the same rule the rest of the picture obeys - and the rule
        /// the shipped air-strike code breaks.
        /// </summary>
        /// <summary>
        /// Finds a contact in the task force's OWN plotting table.
        ///
        /// Never the global object list. A commander cannot order an attack on something
        /// it has not detected - the same rule the rest of the picture obeys, and the one
        /// the shipped air-strike code breaks.
        /// </summary>
        internal static ObjectBase ResolveContact(Taskforce tf, int contactId)
        {
            var plot = tf != null ? tf.PlottingTable : null;
            if (plot == null) return null;

            foreach (var veh in plot.Vehicles)
            {
                if (veh == null) continue;
                var obj = veh.Object;
                if (obj == null || obj.IsDestroyed) continue;
                if (obj.UniqueID != contactId) continue;

                // Refuse to turn our own guns on ourselves.
                if (obj._taskforce == tf) return null;

                return obj;
            }

            return null;
        }

        private static bool AttackTarget(ObjectBase unit, ForceOrder order)
        {
            var tf = unit._taskforce;

            var target = ResolveContact(tf, order.TargetContactId);
            if (target == null)
            {
                Plugin.Log.LogWarning(
                    $"[orders] {order.Kind}: contact {order.TargetContactId} not held by this task force");
                return false;
            }

            if (unit._ai == null)
            {
                Plugin.Log.LogWarning($"[orders] {order.Kind}: {unit.getName()} has no AI to engage with");
                return false;
            }

            var salvo = order.Salvo > 0 ? order.Salvo : 1;
            unit._ai.AutoAttackByClick(target, Ammunition.Type.None, ignoreExecuting: false, salvo: salvo);
            return true;
        }

        /// <summary>
        /// Drops the unit's attack and engagement tasks without touching its weapons
        /// posture, so it stops prosecuting but can still defend itself.
        /// </summary>
        private static bool Disengage(ObjectBase unit)
        {
            unit.ClearAttackTasks();
            unit.ClearEngageTasks();
            return true;
        }

        private static bool SetWeaponStatus(ObjectBase unit, ForceOrder order)
        {
            ObjectBase.WeaponStatus status;
            if (!TryParseWeaponStatus(order.WeaponStatus, out status))
            {
                Plugin.Log.LogWarning($"[orders] bad weapon status '{order.WeaponStatus}'");
                return false;
            }

            unit.SetWeaponStatus(status);
            return true;
        }

        private static bool TryParseWeaponStatus(string raw, out ObjectBase.WeaponStatus status)
        {
            status = ObjectBase.WeaponStatus.Tight;
            if (string.IsNullOrEmpty(raw)) return false;

            switch (raw.Trim().ToLowerInvariant())
            {
                case "tight": status = ObjectBase.WeaponStatus.Tight; return true;
                case "free":  status = ObjectBase.WeaponStatus.Free;  return true;
                case "hold":  status = ObjectBase.WeaponStatus.Hold;  return true;
                default: return false;
            }
        }

        private static Dictionary<int, ObjectBase> IndexOwnUnits(Taskforce tf)
        {
            var map = new Dictionary<int, ObjectBase>();
            AddAll(map, tf._taskforceVessels);
            AddAll(map, tf._taskforceSubmarines);
            AddAll(map, tf._taskforceAircraft);
            AddAll(map, tf._taskforceHelicopters);
            AddAll(map, tf._taskforceLandUnits);
            return map;
        }

        private static void AddAll(Dictionary<int, ObjectBase> map, List<ObjectBase> units)
        {
            if (units == null) return;
            foreach (var u in units)
            {
                if (u == null || u.IsDestroyed) continue;
                map[u.UniqueID] = u;
            }
        }
    }
}
