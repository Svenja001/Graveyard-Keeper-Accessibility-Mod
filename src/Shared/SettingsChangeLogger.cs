using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace Shared;

// Logs config changes as "Setting changed: [Section/Key] 'old' -> 'new'".
// We snapshot prior values because BepInEx's SettingChanged event doesn't carry them.
internal static class SettingsChangeLogger
{
    public static void Register(ConfigFile config, TimestampedLogger log)
    {
        if (config == null || log == null) return;

        try
        {
            var snapshot = new Dictionary<ConfigDefinition, object>();
            foreach (var kv in config)
            {
                snapshot[kv.Key] = kv.Value.BoxedValue;
            }

            config.SettingChanged += (_, args) =>
            {
                try
                {
                    var entry = args?.ChangedSetting;
                    if (entry == null) return;

                    var def = entry.Definition;
                    var newVal = entry.BoxedValue;
                    snapshot.TryGetValue(def, out var oldVal);

                    if (Equals(oldVal, newVal)) return;

                    log.LogInfo($"Setting changed: [{def.Section}/{def.Key}] '{Format(oldVal)}' -> '{Format(newVal)}'");
                    snapshot[def] = newVal;
                }
                catch
                {
                    // A misbehaving ToString() must never break the mod.
                }
            };

            config.ConfigReloaded += (_, _) =>
            {
                try
                {
                    foreach (var kv in config)
                    {
                        if (!snapshot.ContainsKey(kv.Key))
                        {
                            snapshot[kv.Key] = kv.Value.BoxedValue;
                        }
                    }
                }
                catch
                {
                    // ignore
                }
            };
        }
        catch (Exception ex)
        {
            try { log.LogWarning($"SettingsChangeLogger.Register failed: {ex.GetType().Name}: {ex.Message}"); }
            catch { /* nothing more we can do */ }
        }
    }

    private static string Format(object value) => value == null ? "null" : value.ToString();
}
