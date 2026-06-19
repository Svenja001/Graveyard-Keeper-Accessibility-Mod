namespace GraveyardKeeperAccessibility;

/// <summary>
/// Speaks what the player just received — "Got 4 wood" — whenever an item lands in the
/// player's inventory. Picking something off the ground, collecting a finished craft, reeling
/// in a fish, buying from a vendor: all of these route through WorldGameObject.AddToInventory
/// on the player object, but none of them voice the item, so a blind player never learns what
/// they picked up. We hook that one method (its string/list overloads delegate to it) and read
/// the item name + count back.
///
/// Gains are coalesced over a short window before speaking, so a burst — a stack pickup or a
/// multi-output craft that fires several AddToInventory calls in the same frame — comes out as
/// one line ("Got 4 wood, 2 stone") instead of stepping on itself.
/// </summary>
internal static class ItemPickupAnnouncer
{
    private static ManualLogSource _log;

    // Insertion-ordered tally of pending gains (name -> total count). We keep the order list
    // and the lookup index in sync so repeated gains of the same item merge into one entry
    // while the first-seen order is preserved for the spoken sentence.
    private static readonly List<KeyValuePair<string, int>> _pending = new();
    private static readonly Dictionary<string, int> _index = new();
    private static float _lastAddTime;

    // Quiet period (seconds) after the last gain before we speak. Long enough to gather a
    // single frame's worth of simultaneous drops, short enough to feel immediate.
    private const float FlushDelay = 0.2f;

    internal static void Init(ManualLogSource log)
    {
        _log = log;
        _log?.LogInfo("[PICKUP] ItemPickupAnnouncer initialized");
    }

    /// <summary>Record an item the player just gained, to be spoken on the next flush.</summary>
    internal static void OnItemGained(Item item)
    {
        try
        {
            if (item == null || item.IsEmpty() || item.is_tech_point) return;

            int count = item.value;
            if (count <= 0) return;

            var name = DescribeItem(item);
            if (string.IsNullOrEmpty(name)) return;

            Accumulate(name, count);
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[PICKUP] OnItemGained error: {ex.Message}");
        }
    }

    /// <summary>
    /// Record tech points just awarded (the red/green/blue study/craft drops). The game bursts
    /// them out a single point at a time over ~1s as they fly to the HUD counter, but this fires
    /// once per award with the full r/g/b totals, so we announce "Got 3 green tech points" rather
    /// than three staggered "1 green" lines. Routed through the same buffer so points merge with
    /// any items dropped alongside them ("Got 2 wood, 3 green tech points").
    /// </summary>
    internal static void OnTechPointsGained(int r, int g, int b)
    {
        try
        {
            Accumulate("red tech points", r);
            Accumulate("green tech points", g);
            Accumulate("blue tech points", b);
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[PICKUP] OnTechPointsGained error: {ex.Message}");
        }
    }

    /// <summary>Add <paramref name="count"/> of <paramref name="name"/> to the pending tally.</summary>
    private static void Accumulate(string name, int count)
    {
        if (count <= 0 || string.IsNullOrEmpty(name)) return;

        if (_index.TryGetValue(name, out var i))
            _pending[i] = new KeyValuePair<string, int>(name, _pending[i].Value + count);
        else
        {
            _index[name] = _pending.Count;
            _pending.Add(new KeyValuePair<string, int>(name, count));
        }

        _lastAddTime = Time.unscaledTime;
    }

    /// <summary>Flush the coalesced gains once the burst has settled. Called every frame.</summary>
    internal static void Update()
    {
        if (_pending.Count == 0) return;
        if (Time.unscaledTime - _lastAddTime < FlushDelay) return;

        try
        {
            var parts = new List<string>(_pending.Count);
            foreach (var kv in _pending)
                parts.Add($"{kv.Value} {kv.Key}");

            var spoken = "Got " + string.Join(", ", parts);
            _log?.LogInfo($"[PICKUP] {spoken}");
            ScreenReader.Say(spoken, interrupt: false);
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[PICKUP] flush error: {ex.Message}");
        }
        finally
        {
            _pending.Clear();
            _index.Clear();
        }
    }

    /// <summary>
    /// Spoken name for a gained item: localized name plus star tier when it has one. Mirrors
    /// InventoryItemHandler.DescribeItemCell (minus the trailing count, which we tally ourselves).
    /// </summary>
    private static string DescribeItem(Item item)
    {
        var name = ScreenReader.StripNguiCodes(item.definition?.GetItemName() ?? "").Trim();
        if (string.IsNullOrEmpty(name)) name = item.id;
        if (string.IsNullOrEmpty(name)) return null;

        var quality = QualityTierName(item.definition);
        if (!string.IsNullOrEmpty(quality))
            name = $"{name}, {quality}";

        return name;
    }

    /// <summary>Spoken star tier (bronze/silver/gold) for an item, or null if it has none.</summary>
    private static string QualityTierName(ItemDefinition def)
    {
        if (def == null || def.quality_type != ItemDefinition.QualityType.Stars) return null;

        int stars = Mathf.FloorToInt(def.quality);
        switch (stars)
        {
            case 1: return "bronze quality";
            case 2: return "silver quality";
            case 3: return "gold quality";
            case <= 0: return null;
            default: return $"{stars} stars";
        }
    }
}
