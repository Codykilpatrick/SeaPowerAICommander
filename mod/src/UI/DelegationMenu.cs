using HarmonyLib;
using SeaPower;
using SeapowerUI.ViewModels;

namespace SeaPowerAICommander.UI
{
    /// <summary>
    /// Adds "Hand fleet to AI Commander" / "Take fleet back" to the game's OWN right-click
    /// menu, on the player's own units.
    ///
    /// The menu is a Noesis ContextMenu built imperatively in C# from
    /// <see cref="ContextMenuItem"/>; the game's XAML is baked into its Noesis assets and
    /// cannot be edited, so appending to the collection the game returns is the only way
    /// in - and is all that is needed.
    ///
    /// Deliberately the whole task force, not a set of units. Delegation is a
    /// <em>task-force</em> concept here: the commander builds one picture and takes one
    /// decision per force, so there is no cheaper unit of hand-over, and a per-unit
    /// version would cost the same per cycle while being far harder to reason about.
    /// This is the "let it drive my fleet the way it drives the enemy's" feature, and it
    /// is one boolean.
    /// </summary>
    internal static class DelegationMenu
    {
        /// <summary>
        /// Build the entry, or null when it does not belong on this menu.
        ///
        /// Returning null rather than a disabled row is forced on us:
        /// ContextMenuItem's enabled state is a ReadOnlyReactiveProperty fixed at
        /// construction, so there is no greying one out afterwards - and a row that
        /// silently does nothing is worse than no row.
        /// </summary>
        internal static ContextMenuItem BuildToggle(ObjectBase anchor)
        {
            if (anchor == null) return null;

            // Only on our own fleet. On an enemy unit the entry would be meaningless -
            // the commander already drives that side, and nothing here would change it.
            var tf = anchor._taskforce;
            if (tf == null || tf.Side != Taskforce.TfType.Player) return null;

            // The master switch keeps the Harmony patch applied but stops the tick, so a
            // hand-over would be accepted and then ignored.
            if (!Plugin.Enabled) return null;

            bool delegated = Plugin.DrivePlayerTaskforce;

            // An Observing brain logs the picture and orders nothing, so handing it the
            // fleet changes nothing visible. Say so in the label rather than letting the
            // player conclude the commander is asleep. Not suppressed: watching what it
            // WOULD have seen of your own force is a legitimate reason to turn this on.
            bool inert = Plugin.Brain == Plugin.BrainType.Observing;

            string label = delegated
                ? "Take fleet back from AI Commander"
                : inert
                    ? "Hand fleet to AI Commander (observing only - issues no orders)"
                    : "Hand fleet to AI Commander";

            return new ContextMenuItem(label, null, new DelegateCommand(delegate
            {
                Plugin.SetDrivePlayerTaskforce(!delegated);
            }));
        }
    }

    /// <summary>The formation marker's own menu (right-click a formation on the map).</summary>
    [HarmonyPatch(typeof(UnitFormation), nameof(UnitFormation.GetContextMenuItems))]
    public static class Patch_UnitFormation_ContextMenu
    {
        static void Postfix(UnitFormation __instance,
                            TrulyObservableCollection<ContextMenuItem> __result)
        {
            if (__result == null) return;

            var anchor = __instance?.LeaderStation?.UnitObject;
            if (anchor == null) return;

            var item = DelegationMenu.BuildToggle(anchor);

            // APPENDED, never inserted at an index. The game's own entries shift with
            // every update, so the bottom is the one position that cannot go stale - and
            // it is where a mod-added action belongs anyway.
            if (item != null) __result.Add(item);
        }
    }

    /// <summary>The per-unit menu (right-click a unit, or a unit inside a formation).</summary>
    [HarmonyPatch(typeof(ObjectBaseViewModel), nameof(ObjectBaseViewModel.GetContextMenuItems))]
    public static class Patch_ObjectBaseViewModel_ContextMenu
    {
        static void Postfix(ObjectBaseViewModel __instance,
                            TrulyObservableCollection<ContextMenuItem> __result)
        {
            if (__result == null) return;

            var anchor = __instance?._objectBase;
            if (anchor == null) return;

            var item = DelegationMenu.BuildToggle(anchor);
            if (item != null) __result.Add(item);
        }
    }
}
