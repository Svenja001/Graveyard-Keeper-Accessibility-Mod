namespace GameBalanceDumper;

internal static class Dumper
{
    private static bool _dumped;

    internal static void DumpOnce()
    {
        if (_dumped) return;
        _dumped = true;

        // if (!Plugin.Enabled.Value)
        // {
        //     LogHelper.Info("[Dumper] Disabled by config; skipping dump.");
        //     return;
        // }

        try
        {
            DumpInternal();
        }
        catch (Exception ex)
        {
            LogHelper.Error($"[Dumper] Top-level failure: {ex}");
        }
    }

    private static void DumpInternal()
    {
        var gb = GameBalance.me;
        if (gb == null)
        {
            LogHelper.Warning("[Dumper] GameBalance.me is null — nothing to dump.");
            return;
        }

        var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (string.IsNullOrEmpty(asmDir))
        {
            LogHelper.Error("[Dumper] Could not resolve assembly directory; aborting.");
            return;
        }
        var dir = Path.Combine(asmDir, "dump");
        Directory.CreateDirectory(dir);

        var settings = new JsonSerializerSettings
        {
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            PreserveReferencesHandling = PreserveReferencesHandling.None,
            NullValueHandling = NullValueHandling.Include,
            Formatting = Plugin.PrettyPrint.Value ? Formatting.Indented : Formatting.None,
            ContractResolver = new UnityObjectIgnoringResolver(),
            Error = (_, e) => e.ErrorContext.Handled = true,
            MaxDepth = 32,
        };

        var index = new List<object>();
        var fields = typeof(GameBalance).GetFields(BindingFlags.Public | BindingFlags.Instance);
        var dumped = 0;

        // Reflectively serializing GameBalance walks every public property on every
        // definition. Some getters have side effects — most notably
        // ItemDefinition.linked_craft does GameBalance.GetData<CraftDefinition>("pray:" + id)
        // and Debug.LogErrors when the lookup misses, which fires for every item without
        // a sermon variant (thousands of "Craft for sermon not found" lines per dump).
        // Suppress Unity's logger for the duration; the errors are spurious here because
        // the dumper hits the getter on items the game itself never would.
        var prevLoggerEnabled = Debug.unityLogger.logEnabled;
        Debug.unityLogger.logEnabled = false;
        try
        {
            foreach (var f in fields)
            {
                var t = f.FieldType;
                var isList = t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>);
                var isDict = t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>);
                if (!isList && !isDict) continue;

                var value = f.GetValue(gb);
                var count = value is ICollection c ? c.Count : 0;
                var path = Path.Combine(dir, $"{f.Name}.json");

                try
                {
                    var json = JsonConvert.SerializeObject(value, settings);
                    File.WriteAllText(path, json);
                    index.Add(new
                    {
                        field = f.Name,
                        type = t.ToString(),
                        count,
                        file = $"{f.Name}.json",
                    });
                    dumped++;
                }
                catch (Exception ex)
                {
                    LogHelper.Error($"[Dumper] Field '{f.Name}' failed: {ex.Message}");
                    index.Add(new
                    {
                        field = f.Name,
                        type = t.ToString(),
                        count,
                        error = ex.Message,
                    });
                }
            }
        }
        finally
        {
            Debug.unityLogger.logEnabled = prevLoggerEnabled;
        }

        var indexPath = Path.Combine(dir, "_index.json");
        var indexDoc = new
        {
            pluginVersion = MyPluginInfo.PLUGIN_VERSION,
            gameVersion = LazyConsts.VERSION_INT,
            dumpedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            fields = index,
        };
        File.WriteAllText(indexPath, JsonConvert.SerializeObject(indexDoc, Formatting.Indented));

        LogHelper.Info($"[Dumper] Wrote {dumped} field file(s) to {dir}");
    }
}
