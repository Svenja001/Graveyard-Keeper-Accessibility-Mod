using BepInEx.Bootstrap;

namespace QueueEverything;

// Reads settings out of other mods' own config objects rather than their files, so a toggle
// flipped in the F1 menu counts straight away and nothing of ours is written to their config.
// When a value can't be read the answer is always the vanilla one.
internal static class ModCompat
{
    internal const string ExhaustlessGuid = "p1xel8ted.gyk.exhaustless";
    internal const string FasterCraftGuid = "p1xel8ted.gyk.fastercraftreloaded";

    private const string ExhaustlessGameplay = "── Gameplay ──";
    private const string ExhaustlessUnlimited = "── Unlimited Stats ──";
    private const string FasterCraftSpeed = "── Speed ──";

    // One line per setting we couldn't read, not one per redraw.
    private static readonly HashSet<string> Warned = new(StringComparer.Ordinal);

    internal static bool IsPresent(string guid) => Chainloader.PluginInfos.ContainsKey(guid);

    // What Exhaust-less will actually charge, as a multiplier on the vanilla cost.
    internal static float EnergyFactor() => SpendFactor("Unlimited Energy", "Spend Half Energy");

    internal static float GratitudeFactor() => SpendFactor("Unlimited Gratitude", "Spend Half Gratitude");

    private static float SpendFactor(string unlimitedKey, string halfKey)
    {
        var unlimitedRead = TryRead(ExhaustlessGuid, ExhaustlessUnlimited, unlimitedKey, out bool unlimited);
        var halfRead = TryRead(ExhaustlessGuid, ExhaustlessGameplay, halfKey, out bool half);

        return ResolveSpendFactor(unlimitedRead, unlimited, halfRead, half);
    }

    // FasterCraft Reloaded's speed setting. 1 means nothing is speeding crafts up.
    internal static float CraftSpeedMultiplier()
    {
        if (!TryRead(FasterCraftGuid, FasterCraftSpeed, "Craft Speed Multiplier", out float speed)) return 1f;

        var usable = ValidateSpeed(speed);
        if (usable != speed)
        {
            Warn($"{FasterCraftGuid}|speed", $"FasterCraft Reloaded's craft speed reads as {speed}; using 1 instead.");
        }

        return usable;
    }

    // Pattern-matching the entry means a setting of the wrong type reads as unavailable rather
    // than throwing. Nothing is remembered between calls, so an edit lands on the next read.
    private static bool TryRead<T>(string guid, string section, string key, out T value)
    {
        value = default;

        if (!Chainloader.PluginInfos.TryGetValue(guid, out var info) || info?.Instance == null) return false;

        var wanted = new ConfigDefinition(section, key);
        foreach (var entry in info.Instance.Config)
        {
            if (!entry.Key.Equals(wanted)) continue;

            if (entry.Value is ConfigEntry<T> typed)
            {
                value = typed.Value;
                return true;
            }

            Warn($"{guid}|{section}|{key}|type", $"{guid} has [{section}] {key} but not as a {typeof(T).Name}; using the vanilla figure.");
            return false;
        }

        Warn($"{guid}|{section}|{key}|missing", $"{guid} is loaded but has no [{section}] {key}; using the vanilla figure.");
        return false;
    }

    private static void Warn(string token, string message)
    {
        if (!Warned.Add(token)) return;

        Plugin.Log.LogWarning(message);
    }

    // Exhaust-less checks Unlimited first and stops there, so decide the same way round. The two
    // toggles switch each other off, but a hand-edited config could still have both set. Half on
    // its own can't tell us what the mod will do, so anything unreadable means full price.
    // Kept separate so the precedence can be checked without a game running.
    internal static float ResolveSpendFactor(bool unlimitedRead, bool unlimited, bool halfRead, bool half)
    {
        if (!unlimitedRead) return 1f;
        if (unlimited) return 0f;
        if (!halfRead) return 1f;

        return half ? 0.5f : 1f;
    }

    internal static float ValidateSpeed(float speed) =>
        float.IsNaN(speed) || float.IsInfinity(speed) || speed <= 0f ? 1f : speed;
}
