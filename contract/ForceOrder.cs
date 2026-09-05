using System.Collections.Generic;

namespace SeaPowerForceAI.Orders
{
    /// <summary>
    /// The action space of the force brain.
    ///
    /// This enum is deliberately the whole vocabulary: if you wire an LLM behind
    /// <see cref="IForceBrain"/>, generate its JSON schema from this type so the model
    /// physically cannot emit an order the executor has no way to carry out.
    /// Add a kind here first, implement it in OrderExecutor, then widen the schema.
    /// </summary>
    public enum ForceOrderKind
    {
        /// <summary>Clear existing waypoints and steer for a lat/lon.</summary>
        MoveTo,

        /// <summary>Set a constant speed in knots. 0 means all stop.</summary>
        SetSpeed,

        /// <summary>Weapons Tight / Free / Hold.</summary>
        SetWeaponStatus,
    }

    public class ForceOrder
    {
        public ForceOrderKind Kind;

        /// <summary>ObjectBase.UniqueID of the unit being ordered. Must be in our own task force.</summary>
        public int UnitId;

        // MoveTo
        public double Latitude;
        public double Longitude;

        // SetSpeed
        public float SpeedKnots;

        // SetWeaponStatus - "Tight" | "Free" | "Hold"
        public string WeaponStatus;

        /// <summary>Free text for the log. Useful when a brain should explain itself.</summary>
        public string Reason;
    }

    public class ForceOrderSet
    {
        /// <summary>Task force these orders were produced for, matched on apply.</summary>
        public string TaskforceName;

        /// <summary>GameTime.time of the picture these orders were derived from.</summary>
        public float DerivedFromTime;

        public List<ForceOrder> Orders = new List<ForceOrder>();
    }
}
