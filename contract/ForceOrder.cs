using System.Collections.Generic;

namespace SeaPowerAICommander.Orders
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

        /// <summary>
        /// Engage a specific contact now.
        ///
        /// Until this existed the commander could only set weapons free and hope the
        /// tactical AI picked the target it had in mind. It could position a force
        /// perfectly and still not be able to say what to shoot.
        /// </summary>
        AttackTarget,

        /// <summary>
        /// Engage a contact as part of a simultaneous attack.
        ///
        /// Orders sharing a CoordinationGroup are held until every unit in the group is
        /// ready, then released together. Against a defended target this is the only
        /// thing that changes the arithmetic - weapons arriving one at a time are
        /// defeated one at a time, which is precisely how a fast attack craft force dies
        /// piecemeal.
        /// </summary>
        CoordinatedAttack,

        /// <summary>
        /// Call off a unit's current attack, leaving it free to defend itself.
        ///
        /// Added because the commander needed it and had no way to say it: having ordered
        /// an attack that identification later revealed to be suicidal, the only tool it
        /// had for cancelling was weapons-hold - which also stops the unit defending
        /// itself. Being able to start something you cannot stop is a bad action space.
        /// </summary>
        Disengage,

        /// <summary>
        /// Order an airbase or carrier to mount a strike on a contact.
        ///
        /// Aircraft sit on the ground until something launches them, and nothing in the
        /// action space could. The commander repeatedly identified idle aircraft as wasted
        /// assets and then tasked them with movement orders they could not obey. This runs
        /// the game's own strike pipeline instead - assigning aircraft, launching,
        /// assembling and ingressing - which also sidesteps the formation problem, since
        /// the strike manages its own package.
        ///
        /// Note: the strike will sweep up other vessels near the target from ground truth,
        /// which is the game's behaviour and the one place this mod's detection-limited
        /// picture does not hold.
        /// </summary>
        LaunchAirstrike,

        /// <summary>
        /// Go silent, or start radiating.
        ///
        /// Emission control was the largest hole in the action space: the commander could
        /// neither see whether its ships were radiating nor decide it. In a GIUK
        /// interception that is most of the problem - the side that finds the other first
        /// shoots first, and a radiating search radar is both the way you find them and
        /// the way they find you. Both commanders spent that battle complaining they could
        /// not classify anything while the levers that decide detection sat outside their
        /// reach entirely.
        ///
        /// Coarse on purpose. "Silent" runs the game's own EMCON, shutting down search
        /// radars, active sonar and offensive jamming together; "Radiate" turns the search
        /// radars back on. Choosing individual emitters is a tactical-AI job, not a
        /// force-level one.
        /// </summary>
        SetEmcon,

        /// <summary>
        /// Put aircraft in the air on a standing mission - CAP, AEW, Recon, MPA, ASW or
        /// Intercept.
        ///
        /// <see cref="LaunchAirstrike"/> needs a contact to strike, which makes it useless
        /// in exactly the situation where aircraft matter most: nothing is classified yet,
        /// so nothing can be struck, so nothing launches, so nothing gets classified. A
        /// GIUK interception was lost inside that loop with both sides' air groups parked
        /// on deck, each commander noting it could not classify anything.
        ///
        /// This is the other half of the air picture: launching to SEE rather than to
        /// hit. An AEW bird or a recon sweep is usually a better first move than any
        /// strike, and it was the one move unavailable.
        /// </summary>
        LaunchAircraft,
    }

    public static class ForceOrderKinds
    {
        /// <summary>
        /// True when the order's meaning depends on where things were at the moment it was
        /// decided.
        ///
        /// This is what makes an order perishable. A waypoint derived from a contact's
        /// position is wrong once that contact has moved; "weapons free" or "slow to 12
        /// knots" is a posture that stays valid as long as the situation holds - and
        /// force-level posture changes slowly by nature. Treating both alike means either
        /// discarding good posture orders or acting on bad waypoints.
        ///
        /// This lives in the contract, not the mod, so whoever adds an order kind has to
        /// answer the question right next to the kind itself.
        /// </summary>
        public static bool IsPositionDependent(ForceOrderKind kind)
        {
            switch (kind)
            {
                case ForceOrderKind.MoveTo:
                    return true;

                // Attacks name a contact, not a position - the tactical AI re-resolves
                // where that contact actually is when it engages, so a stale decision
                // does not put a weapon at old coordinates.
                case ForceOrderKind.SetSpeed:
                case ForceOrderKind.SetWeaponStatus:
                case ForceOrderKind.AttackTarget:
                case ForceOrderKind.CoordinatedAttack:
                case ForceOrderKind.Disengage:
                case ForceOrderKind.LaunchAirstrike:
                case ForceOrderKind.SetEmcon:
                case ForceOrderKind.LaunchAircraft:
                    return false;

                default:
                    // An unclassified new kind is assumed perishable - the safe default.
                    return true;
            }
        }
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

        // AttackTarget / CoordinatedAttack
        /// <summary>Contact id to engage. Must be a contact, never one of your own units.</summary>
        public int TargetContactId;

        /// <summary>How many rounds or missiles to commit. 1 if unspecified.</summary>
        public int Salvo;

        /// <summary>
        /// CoordinatedAttack only. Free-form label; every order sharing it is released
        /// together once all its units are ready.
        /// </summary>
        public string CoordinationGroup;

        /// <summary>
        /// LaunchAirstrike only: "Bomb" | "Missile" | "SEAD" | "Jam".
        /// SEAD suppresses air defences; Jam is electronic attack.
        /// </summary>
        public string StrikeType;

        /// <summary>SetEmcon only: "Silent" | "Radiate".</summary>
        public string Emcon;

        /// <summary>
        /// LaunchAircraft only: "CAP" | "AEW" | "Recon" | "MPA" | "ASW" | "Intercept".
        /// </summary>
        public string AirMission;

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
