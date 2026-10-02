namespace QueueEverything;

// The organ workbench raises a skull pip one level at a time through its own window, so the
// amount arrows there only ever applied the first level. This works out the whole run the
// player asked for and folds it into one craft: the shards, energy and time of every level
// added up, with the final organ as the output.
internal static class OrganEnhancer
{
    private const string WhitePrefix = "organ_enhance_w_";
    private const string RedPrefix = "organ_enhance_r_";
    private const string WhiteParam = "total_white_skulls";
    private const string RedParam = "total_red_skulls";

    // Set while an organ row redraws so the ingredient and cost draws inside that redraw
    // show the whole run instead of just the first level. Put back when the redraw ends.
    private static OrganPreview _preview;

    internal static bool IsOrganCraft(CraftDefinition craft)
    {
        if (craft?.id == null) return false;
        return craft.id.StartsWith(WhitePrefix, StringComparison.Ordinal) ||
               craft.id.StartsWith(RedPrefix, StringComparison.Ordinal);
    }

    private static bool IsWhiteAxis(CraftDefinition craft) => craft.id.StartsWith(WhitePrefix, StringComparison.Ordinal);

    // Same clamp the game uses to turn a skull count into a recipe number.
    private static int RecipeIndex(int skulls)
    {
        var clamped = Mathf.Clamp(skulls, 0, OrganEnhancerGUI.MAX_ORGAN_ENHANCING_CUP);
        return clamped == OrganEnhancerGUI.MAX_ORGAN_ENHANCING_CUP ? clamped : clamped + 1;
    }

    private static int SkullsOf(Item item, bool isWhite) => isWhite ? item.GetWhiteSkullsValue() : item.GetRedSkullsValue();

    // Builds the run for the requested number of levels. The window has already worked out the
    // first one: the definition it hands over carries the organ a pip up from the one the player
    // put in, and the bench still holds that organ's values, so that level counts as step one and
    // is costed against the bench as it stands. Later levels move the bench on as they go.
    // Stops early only on things no amount of shards can change: the pip is already full, the
    // recipe is missing, its condition fails, or the upgraded organ has no item definition. Pass
    // an inventory to also stop once the running total no longer fits, which is what the plus
    // arrow needs.
    internal static OrganRun BuildRun(CraftDefinition craft, WorldGameObject bench, int amount, MultiInventory stopWhenShort)
    {
        if (!IsOrganCraft(craft) || bench == null || amount < 1) return null;
        if (craft.output == null || craft.output.Count == 0 || craft.output[0]?.definition == null) return null;

        var isWhite = IsWhiteAxis(craft);
        var player = MainGame.me.player;

        var savedWhite = bench.GetParamInt(WhiteParam);
        var savedRed = bench.GetParamInt(RedParam);

        try
        {
            if (craft.condition != null && !craft.condition.EvaluateBoolean(bench, player)) return null;

            var needs = new Dictionary<string, int>(StringComparer.Ordinal);
            Add(needs, craft.needs);
            if (stopWhenShort != null && !stopWhenShort.IsEnoughItems(ToItems(needs))) return null;

            var output = craft.output[0];
            var steps = 1;
            var energy = craft.energy?.EvaluateFloat(bench, player) ?? 0f;
            var time = craft.craft_time?.EvaluateFloat(bench, player) ?? 0f;

            for (var step = 1; step < amount; step++)
            {
                var skulls = SkullsOf(output, isWhite);
                if (skulls >= OrganEnhancerGUI.MAX_ORGAN_ENHANCING_CUP) break;

                var prefix = isWhite ? WhitePrefix : RedPrefix;
                var stepDef = GameBalance.me.GetDataOrNull<CraftDefinition>($"{prefix}{RecipeIndex(skulls)}");
                if (stepDef == null) break;

                // Point the bench at the organ as it stands going into this level, so the
                // recipe's own condition and cost see the state it would really be in.
                // The other pip keeps its value.
                bench.SetParam(WhiteParam, output.GetWhiteSkullsValue());
                bench.SetParam(RedParam, output.GetRedSkullsValue());

                if (stepDef.condition != null && !stepDef.condition.EvaluateBoolean(bench, player)) break;

                var candidate = new Dictionary<string, int>(needs, StringComparer.Ordinal);
                Add(candidate, stepDef.needs);
                if (stopWhenShort != null && !stopWhenShort.IsEnoughItems(ToItems(candidate))) break;

                var next = OrganEnhancerGUI.GetModifiedItemForCraftOutput(output, isWhite);
                if (next?.definition == null) break;

                needs = candidate;
                output = next;
                steps++;
                energy += stepDef.energy?.EvaluateFloat(bench, player) ?? 0f;
                time += stepDef.craft_time?.EvaluateFloat(bench, player) ?? 0f;
            }

            var total = ToItems(needs);
            return new OrganRun(steps, total, BuildDefinition(craft, output, total, energy, time));
        }
        finally
        {
            bench.SetParam(WhiteParam, savedWhite);
            bench.SetParam(RedParam, savedRed);
        }
    }

    // Mirrors the definition CraftGUI.OpenAsOrganEnhancer builds, but carrying the organ as it
    // ends up after the whole run and the totals for it. Energy and time have to go in as
    // expressions: the game reads default_value only when there is no expression, so a plain
    // default would be charged while every cost display read it as nothing.
    private static CraftDefinition BuildDefinition(CraftDefinition source, Item finalOrgan, List<Item> needs, float energy, float time)
    {
        // A one-level run hands back the window's own item, so copy before touching it.
        var output = new Item(finalOrgan);
        output.min_value = new SmartExpression();
        output.max_value = new SmartExpression();
        output.self_chance = new SmartExpression { default_value = 1f };

        var energyExp = new SmartExpression();
        energyExp.FromString(energy.ToString(CultureInfo.InvariantCulture));

        var timeExp = new SmartExpression();
        timeExp.FromString(time.ToString(CultureInfo.InvariantCulture));

        return new CraftDefinition
        {
            id = source.id,
            craft_in = new List<string> { "soul_workbench" },
            sanity = new SmartExpression(),
            disable_multi_craft = source.disable_multi_craft,
            icon = source.icon,
            condition = source.condition,
            needs = needs,
            energy = energyExp,
            craft_time = timeExp,
            enqueue_type = source.enqueue_type,
            output = [output]
        };
    }

    private static void Add(Dictionary<string, int> totals, List<Item> items)
    {
        if (items == null) return;
        foreach (var item in items)
        {
            totals.TryGetValue(item.id, out var running);
            totals[item.id] = running + item.value;
        }
    }

    private static List<Item> ToItems(Dictionary<string, int> totals) => totals.Select(kv => new Item(kv.Key, kv.Value)).ToList();

    // Swaps in the run being previewed and hands back what was there, so the redraw that set
    // it can put things back afterwards.
    internal static OrganPreview SwapPreview(OrganPreview next)
    {
        var previous = _preview;
        _preview = next;
        return previous;
    }

    // The run's shards and energy already cover every level, so anything reading them has to
    // stop applying the amount on top.
    internal static bool TryGetPreview(CraftDefinition craft, out CraftDefinition aggregate)
    {
        aggregate = _preview.Aggregate;
        return aggregate != null && ReferenceEquals(craft, _preview.Source);
    }

    internal static bool TryGetPreviewNeeds(List<Item> needs, out List<Item> aggregate)
    {
        aggregate = null;
        if (_preview.Aggregate == null || _preview.Source == null) return false;
        if (!ReferenceEquals(needs, _preview.Source.needs)) return false;

        aggregate = _preview.Aggregate.needs;
        return true;
    }
}

public readonly struct OrganPreview(CraftDefinition source, CraftDefinition aggregate)
{
    internal CraftDefinition Source { get; } = source;
    internal CraftDefinition Aggregate { get; } = aggregate;
}

internal sealed class OrganRun(int steps, List<Item> needs, CraftDefinition definition)
{
    internal int Steps { get; } = steps;
    internal List<Item> Needs { get; } = needs;
    internal CraftDefinition Definition { get; } = definition;
}
