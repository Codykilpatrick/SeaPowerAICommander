using System;
using System.Collections.Generic;
using SeaPower;

namespace SeaPowerAICommander.Orders
{
    /// <summary>
    /// Time-on-target coordination.
    ///
    /// Firing simultaneously is not the same as arriving simultaneously. Units sit at
    /// different ranges, so a volley released together lands strung out over tens of
    /// seconds - and a defended target engages the arrivals one at a time, which is
    /// exactly how a fast attack craft force dies piecemeal.
    ///
    /// This holds the closer shooters back so every weapon arrives together. The farthest
    /// unit fires first; each other unit fires late by the difference in its time of
    /// flight. Saturation is the only thing that changes the arithmetic against a layered
    /// air defence.
    /// </summary>
    internal static class AttackScheduler
    {
        private class Pending
        {
            public float FireAtGameTime;
            public ObjectBase Unit;
            public ObjectBase Target;
            public int Salvo;
            public string Group;
        }

        private static readonly List<Pending> Queue = new List<Pending>();

        /// <summary>
        /// Works out a release schedule for one coordination group and queues it.
        /// Returns the orders that were accepted, for standing-order bookkeeping.
        /// </summary>
        public static List<ForceOrder> Schedule(
            string group,
            List<ForceOrder> orders,
            Func<int, ObjectBase> resolveUnit,
            Func<int, ObjectBase> resolveContact)
        {
            var accepted = new List<ForceOrder>();
            var plan = new List<Pending>();
            var longestFlight = 0f;

            foreach (var order in orders)
            {
                var unit = resolveUnit(order.UnitId);
                if (unit == null) continue;

                var target = resolveContact(order.TargetContactId);
                if (target == null) continue;

                var flight = EstimateTimeOfFlightSeconds(unit, target);
                if (flight > longestFlight) longestFlight = flight;

                plan.Add(new Pending
                {
                    FireAtGameTime = flight,   // provisional: replaced with a real time below
                    Unit = unit,
                    Target = target,
                    Salvo = order.Salvo > 0 ? order.Salvo : 1,
                    Group = group,
                });

                accepted.Add(order);
            }

            if (plan.Count == 0) return accepted;

            var now = GameTime.time;
            foreach (var p in plan)
            {
                // Hold each shooter by however much sooner its weapon would otherwise land.
                var delay = longestFlight - p.FireAtGameTime;
                p.FireAtGameTime = now + (delay > 0f ? delay : 0f);
                Queue.Add(p);
            }

            // A zero here is not a computed schedule - it is the fallback for weapons whose
            // flight time we could not estimate, which is every coastal missile battery so
            // far. They all fire at once, which is usually what saturation wants anyway, but
            // the timing is a coincidence rather than a plan and the log should not pretend
            // otherwise.
            if (longestFlight <= 0f)
            {
                Plugin.Log.LogInfo(
                    $"[attack] group '{group}': {plan.Count} shooter(s) firing together - " +
                    "flight time could not be estimated for these weapons, so arrival is not coordinated");
            }
            else
            {
                Plugin.Log.LogInfo(
                    $"[attack] group '{group}': {plan.Count} shooter(s) scheduled, " +
                    $"longest flight {longestFlight:F0}s, release spread {longestFlight:F0}s");
            }

            return accepted;
        }

        /// <summary>
        /// Releases any shot whose moment has come. Cheap; called every tick.
        /// </summary>
        public static void Pump()
        {
            if (Queue.Count == 0) return;

            var now = GameTime.time;

            for (int i = Queue.Count - 1; i >= 0; i--)
            {
                var p = Queue[i];
                if (now < p.FireAtGameTime) continue;

                Queue.RemoveAt(i);

                // The world moved while we held this. Both ends must still exist.
                if (p.Unit == null || p.Unit.IsDestroyed) continue;
                if (p.Target == null || p.Target.IsDestroyed) continue;
                if (p.Unit._ai == null) continue;

                try
                {
                    p.Unit._ai.AutoAttackByClick(p.Target, Ammunition.Type.None, ignoreExecuting: false, salvo: p.Salvo);
                    Plugin.Log.LogInfo($"[attack] group '{p.Group}': {p.Unit.getName()} releasing on schedule");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"[attack] release failed for {p.Unit.getName()}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Rough time of flight for the longest-reaching weapon this unit carries against
        /// that target. Straight-line range over nominal weapon speed - good enough to
        /// order releases, which is all it is used for.
        /// </summary>
        private static float EstimateTimeOfFlightSeconds(ObjectBase unit, ObjectBase target)
        {
            try
            {
                var metres = (float)unit._geoPosition.GetDistance(target._geoPosition) * 1000f;

                var fastest = 0f;
                var ammo = unit._ai != null ? unit._ai.AmmunitionForTarget(target) : null;
                if (ammo != null)
                {
                    foreach (var a in ammo)
                    {
                        if (a == null || a._ap == null) continue;
                        var speed = a._ap._muzzleVelocityInMeterPerSecond;
                        if (speed > fastest) fastest = speed;
                    }
                }

                // No usable figure - treat as instantaneous so it simply fires first.
                if (fastest <= 1f) return 0f;

                var seconds = metres / fastest;
                return (float.IsNaN(seconds) || float.IsInfinity(seconds)) ? 0f : seconds;
            }
            catch (Exception)
            {
                return 0f;
            }
        }
    }
}
