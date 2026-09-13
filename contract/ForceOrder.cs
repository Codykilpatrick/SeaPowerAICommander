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

        /// <summary>
        /// Send a unit to establish what a contact actually is.
        ///
        /// The commander could see that a contact was unclassified and could not do
        /// anything about it. Every other order in this list assumes the picture is
        /// already good enough to act on; this is the one that makes it so, and its
        /// absence produced a deadlock that lost a GIUK interception - nothing
        /// classified, so nothing worth striking, so nothing launched, so nothing ever
        /// got classified.
        ///
        /// It is also the only way to redirect an aircraft that is already airborne.
        /// <see cref="MoveTo"/> is refused for air units because their AI rewrites the
        /// route every tick - but that same AI diverts to a contact the moment one is
        /// named, because naming a target is how the game's own tasking works.
        /// </summary>
        IdentifyContact,

        /// <summary>
        /// Send an aircraft or helicopter home.
        ///
        /// Without it a deck is a one-shot asset: everything launched flies until it runs
        /// out of fuel or ordnance and decides for itself. A fighter reporting
        /// airDefenceReachNM 0 is an empty airframe holding a station it can no longer
        /// defend, and recovering it is the only thing that turns it back into a sortie.
        /// </summary>
        ReturnToBase,

        /// <summary>
        /// Put a submarine in a depth band.
        ///
        /// Above or below the layer is the submarine decision in this game - the layer is
        /// reported in every picture as conditions.layerDepth and the commander had no way
        /// to act on it. Coarse bands rather than feet, because which side of the layer a
        /// boat sits on is a force-level choice and the exact depth is not.
        /// </summary>
        SetDepth,

        /// <summary>
        /// Ping, stream the tail, or put it under the layer.
        ///
        /// <see cref="SetEmcon"/> deliberately leaves active sonar alone, on the grounds
        /// that pinging is a louder decision than switching on a search radar. That was
        /// right, and it left the whole of ASW sensor management outside the action space:
        /// nothing could ping, nothing could stream a towed array, and nothing could put
        /// one on the far side of the layer from the boat it was hunting.
        /// </summary>
        SetSonar,

        /// <summary>
        /// Reshape a formation - screen, search line, column.
        ///
        /// Formation geometry is force-level by definition and was the one thing at this
        /// altitude the commander could not touch. A circular screen protects a carrier, a
        /// line abreast sweeps for a submarine, a column transits. Spacing is deliberately
        /// left alone: the formation keeps whatever it has.
        /// </summary>
        SetFormation,
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
                case ForceOrderKind.IdentifyContact:
                case ForceOrderKind.ReturnToBase:
                case ForceOrderKind.SetDepth:
                case ForceOrderKind.SetSonar:
                case ForceOrderKind.SetFormation:
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

        // AttackTarget / CoordinatedAttack / LaunchAirstrike / IdentifyContact
        /// <summary>
        /// Contact this order is aimed at. Must be a contact, never one of your own units.
        /// </summary>
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
        /// LaunchAirstrike only. The weapons fit to send the strike out with, named from the
        /// ordering unit's airstrikeLoadouts. Empty leaves the choice to the game.
        ///
        /// Worth naming because the game does not choose well: it takes whichever loadout in
        /// the pool has the most airframes available rather than the one suited to the
        /// target, so a base stocked mainly for land attack sends a land-attack fit at a
        /// destroyer. A "Missile" strike type only puts the anti-ship loadouts at the front
        /// of that pool; it does not insist on them.
        ///
        /// Refused if the deck cannot fly it. That is not pedantry - forcing an unavailable
        /// loadout empties the candidate pool, and the strike then sits in its assigning
        /// state for the rest of the mission without ever launching.
        /// </summary>
        public string Loadout;

        /// <summary>
        /// LaunchAircraft only: "CAP" | "AEW" | "Recon" | "MPA" | "ASW" | "Intercept".
        /// </summary>
        public string AirMission;

        /// <summary>
        /// SetDepth only: "Surface" | "Periscope" | "Shallow" | "AboveLayer" |
        /// "BelowLayer" | "Deep" | "VeryDeep".
        ///
        /// These are the game's own seven preset bands, in its own order, so the index
        /// this maps to is the index its state machine and its UI both use.
        /// </summary>
        public string Depth;

        /// <summary>
        /// SetSonar only: "ActiveOn" | "ActiveOff" | "DeployTowedArray" |
        /// "RetractTowedArray" | "TowedArrayAboveLayer" | "TowedArrayBelowLayer".
        ///
        /// Hull sonar and the towed array are separate decisions with opposite risk
        /// profiles - pinging announces you, streaming a tail only slows you - so one
        /// field spanning both is coarse by intent, not by accident.
        /// </summary>
        public string Sonar;

        /// <summary>
        /// SetFormation only: "Circle" | "Vic" | "Echelon" | "LineAbreast" |
        /// "LineAstern" | "Box".
        ///
        /// "Loose" is NOT among them, and its absence is load-bearing: UnitFormation.Reform
        /// has no case for it, so every station falls through with a zero offset and the
        /// whole formation is ordered onto the leader.
        /// </summary>
        public string FormationPattern;

        /// <summary>
        /// AttackTarget and CoordinatedAttack: "Auto" | "Missile" | "Torpedo" | "Gun" |
        /// "ASROC" | "RBU".
        ///
        /// Auto lets the unit's own weapon allocation choose, which is what every attack
        /// did before this field existed. Naming a type only narrows the choice - the
        /// executor still refuses to fire something the unit is not carrying, and falls
        /// back to Auto rather than issuing an attack that quietly fires nothing.
        ///
        /// Ignored for aircraft: the game's own AutoAttackByClick discards the ammunition
        /// type for air units and hands the target to the airframe's AI, which picks from
        /// what is on the pylons.
        /// </summary>
        public string Weapon;

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
