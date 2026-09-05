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
        public static void Apply(Taskforce tf, ForceOrderSet set)
        {
            if (tf == null || set == null || set.Orders == null) return;

            var byId = IndexOwnUnits(tf);
            int applied = 0, rejected = 0;

            foreach (var order in set.Orders)
            {
                if (order == null) { rejected++; continue; }

                ObjectBase unit;
                if (!byId.TryGetValue(order.UnitId, out unit))
                {
                    // Not ours, destroyed, or hallucinated. Never trust the id.
                    Plugin.Log.LogWarning($"[orders] rejected {order.Kind} - unit {order.UnitId} not in {tf._nameInMissionFile}");
                    rejected++;
                    continue;
                }

                try
                {
                    if (ApplyOne(unit, order)) applied++;
                    else rejected++;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"[orders] {order.Kind} on {unit.getName()} threw: {ex.Message}");
                    rejected++;
                }
            }

            if (applied > 0 || rejected > 0)
                Plugin.Log.LogInfo($"[orders] {tf._nameInMissionFile}: applied {applied}, rejected {rejected}");
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
