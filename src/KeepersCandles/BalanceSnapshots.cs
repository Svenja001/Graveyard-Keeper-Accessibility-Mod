using System.Runtime.CompilerServices;

namespace KeepersCandles;

// Remembers the game's own burn-down values for each candle and incense definition, so they
// can be put back when a player switches burn-down on.
internal static class BalanceSnapshots
{
    private sealed class CraftValues
    {
        internal float DurNeedsItem;
        internal float DurParameter;
        internal bool CanCraftAlways;
    }

    private sealed class ObjectValues
    {
        internal float DurabilityModificator;
        internal bool AlwaysActive;
    }

    // Keyed by the definition itself, not its id. A reloaded balance gets its own baseline, and a
    // definition is only ever captured before we first change it.
    private static readonly ConditionalWeakTable<CraftDefinition, CraftValues> Crafts = new();
    private static readonly ConditionalWeakTable<ObjectDefinition, ObjectValues> Objects = new();

    internal static void Apply(CraftDefinition craft, bool permanent)
    {
        var original = Crafts.GetValue(craft, c => new CraftValues
        {
            DurNeedsItem = c.dur_needs_item,
            DurParameter = c.dur_parameter,
            CanCraftAlways = c.can_craft_always
        });

        if (permanent)
        {
            craft.dur_needs_item = 0f;
            craft.dur_parameter = 0f;
            craft.can_craft_always = true;
            return;
        }

        craft.dur_needs_item = original.DurNeedsItem;
        craft.dur_parameter = original.DurParameter;
        craft.can_craft_always = original.CanCraftAlways;
    }

    internal static void Apply(ObjectDefinition def, bool permanent)
    {
        var original = Objects.GetValue(def, d => new ObjectValues
        {
            DurabilityModificator = d.durability_modificator,
            AlwaysActive = d.always_active
        });

        if (permanent)
        {
            def.durability_modificator = 0f;
            def.always_active = true;
            return;
        }

        def.durability_modificator = original.DurabilityModificator;
        def.always_active = original.AlwaysActive;
    }
}
