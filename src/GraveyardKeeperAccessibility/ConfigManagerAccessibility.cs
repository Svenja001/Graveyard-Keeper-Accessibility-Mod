namespace GraveyardKeeperAccessibility;

internal class ConfigItem
{
    internal string PluginName;
    internal string Section;
    internal string Name;
    internal string Description;
    internal ConfigEntryBase Entry;

    internal string ReadLabel()
    {
        var val = Entry?.BoxedValue?.ToString() ?? "?";
        return $"{Name}: {val}";
    }

    internal string ReadFull()
    {
        var val = Entry?.BoxedValue?.ToString() ?? "?";
        var desc = !string.IsNullOrWhiteSpace(Description) ? ". " + Description : "";
        return $"{PluginName}, {Section}, {Name}: {val}{desc}";
    }
}

internal static class ConfigManagerAccessibility
{
    private static readonly List<ConfigItem> Items = new();
    private static int _selectedIndex = -1;
    private static bool _isActive;
    private static bool _wasDisplaying;

    internal static bool IsActive => _isActive;

    private static int _updateCount;

    internal static void Update()
    {
        if (++_updateCount % 300 == 0)
            Plugin.Log.LogInfo($"[ConfigMgr] Update called, checking status");

        var displaying = IsConfigManagerDisplaying();

        if (displaying != _wasDisplaying)
            Plugin.Log.LogInfo($"[ConfigMgr] displaying={displaying} (was={_wasDisplaying})");

        if (displaying && !_wasDisplaying)
        {
            Open();
        }
        else if (!displaying && _wasDisplaying)
        {
            Close();
        }

        _wasDisplaying = displaying;

        if (!_isActive) return;

        var count = Items.Count;
        if (count == 0) return;

        if (Input.GetKeyDown(KeyCode.DownArrow))
        {
            _selectedIndex = (_selectedIndex + 1) % count;
            ScreenReader.Say(Items[_selectedIndex].ReadLabel());
        }
        else if (Input.GetKeyDown(KeyCode.UpArrow))
        {
            _selectedIndex = (_selectedIndex - 1 + count) % count;
            ScreenReader.Say(Items[_selectedIndex].ReadLabel());
        }
        else if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            if (_selectedIndex >= 0 && _selectedIndex < count)
                ScreenReader.Say(Items[_selectedIndex].ReadFull());
        }
        else if (Input.GetKeyDown(KeyCode.LeftArrow))
        {
            AdjustValue(-1);
        }
        else if (Input.GetKeyDown(KeyCode.RightArrow))
        {
            AdjustValue(1);
        }
    }

    private static void Open()
    {
        _isActive = true;
        _selectedIndex = -1;
        Items.Clear();
        BuildSettingsList();
        Plugin.Log.LogInfo($"ConfigManager opened, {Items.Count} settings");
        ScreenReader.Say($"Configuration Manager, {Items.Count} settings");
    }

    private static void Close()
    {
        _isActive = false;
        _selectedIndex = -1;
        Items.Clear();
    }

    private static void BuildSettingsList()
    {
        foreach (var kvp in Chainloader.PluginInfos)
        {
            var pluginInfo = kvp.Value;
            var pluginName = pluginInfo.Metadata?.Name ?? kvp.Key;
            var config = pluginInfo.Instance?.Config;
            if (config == null) continue;

            foreach (var entry in config)
            {
                Items.Add(new ConfigItem
                {
                    PluginName = pluginName,
                    Section = entry.Key.Section,
                    Name = entry.Key.Key,
                    Description = entry.Value.Description?.Description,
                    Entry = entry.Value
                });
            }
        }

        Items.Sort((a, b) =>
        {
            var cmp = string.Compare(a.PluginName, b.PluginName, StringComparison.OrdinalIgnoreCase);
            if (cmp != 0) return cmp;
            cmp = string.Compare(a.Section, b.Section, StringComparison.OrdinalIgnoreCase);
            if (cmp != 0) return cmp;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static void AdjustValue(int direction)
    {
        if (_selectedIndex < 0 || _selectedIndex >= Items.Count) return;

        var item = Items[_selectedIndex];
        var entry = item.Entry;
        if (entry == null) return;

        var type = entry.SettingType;
        var current = entry.BoxedValue;

        if (type == typeof(bool))
        {
            entry.BoxedValue = !(bool)current;
        }
        else if (type == typeof(int))
        {
            var range = entry.Description?.AcceptableValues;
            if (range is AcceptableValueRange<int> intRange)
                entry.BoxedValue = Mathf.Clamp((int)current + direction, intRange.MinValue, intRange.MaxValue);
            else
                entry.BoxedValue = (int)current + direction;
        }
        else if (type == typeof(float))
        {
            var range = entry.Description?.AcceptableValues;
            if (range is AcceptableValueRange<float> floatRange)
                entry.BoxedValue = Mathf.Clamp((float)current + direction * 0.1f, floatRange.MinValue, floatRange.MaxValue);
            else
                entry.BoxedValue = (float)current + direction * 0.1f;
        }
        else if (type.IsEnum)
        {
            var values = Enum.GetValues(type);
            var idx = Array.IndexOf(values, current);
            idx = (idx + direction + values.Length) % values.Length;
            entry.BoxedValue = values.GetValue(idx);
        }
        else if (type == typeof(string))
        {
            var acceptable = entry.Description?.AcceptableValues;
            if (acceptable is AcceptableValueList<string> list)
            {
                var vals = list.AcceptableValues;
                var idx = Array.IndexOf(vals, (string)current);
                idx = (idx + direction + vals.Length) % vals.Length;
                entry.BoxedValue = vals[idx];
            }
        }
        else if (type == typeof(KeyboardShortcut))
        {
            ScreenReader.Say("Keybind, press Enter to hear details");
            return;
        }

        ScreenReader.Say(item.ReadLabel());
    }

    private static PropertyInfo _isDisplayingProp;
    private static Type _cmType;
    private static bool _cmLookupDone;

    private static bool IsConfigManagerDisplaying()
    {
        try
        {
            if (!_cmLookupDone)
            {
                _cmLookupDone = true;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!asm.GetName().Name.Contains("ConfigurationManager"))
                        continue;

                    _cmType = asm.GetType("ConfigurationManager.ConfigurationManager");
                    if (_cmType == null) continue;

                    _isDisplayingProp = _cmType.GetProperty("DisplayingWindow", BindingFlags.Public | BindingFlags.Instance);
                    if (_isDisplayingProp != null)
                    {
                        Plugin.Log.LogInfo("[ConfigMgr] Ready to detect configuration manager");
                    }
                    break;
                }
            }

            if (_cmType == null || _isDisplayingProp == null) return false;

            var instance = UnityEngine.Object.FindObjectOfType(_cmType);
            if (instance == null) return false;

            return (bool)_isDisplayingProp.GetValue(instance);
        }
        catch
        {
            return false;
        }
    }
}
