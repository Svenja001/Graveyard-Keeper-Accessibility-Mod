using System.IO;

namespace GraveyardKeeperAccessibility;

/// <summary>
/// Loads the mod's spoken strings from <c>lang/GraveyardKeeperAccessibility.&lt;code&gt;.json</c>
/// next to the DLL, following the same convention as the other mods in this repo.
///
/// The accessibility mod excludes <c>..\Shared\**</c> from compilation (see the csproj), so this is a
/// standalone twin of <c>Shared/Localisation.cs</c> that logs through BepInEx's <see cref="ManualLogSource"/>
/// instead of the shared TimestampedLogger.
///
/// Everything the mod speaks goes through <see cref="Get"/> or <see cref="Fmt"/>. English is the
/// fallback, so a missing key in another language still says something sensible rather than a raw key.
/// </summary>
internal static class Loc
{
    private static Dictionary<string, string> _translations = new();
    private static Dictionary<string, string> _fallback = new();
    // Normalized lang code -> file path, so "pt-br", "pt_BR" etc. all resolve to one file.
    private static readonly Dictionary<string, string> LangFiles = new();
    private static string _langDir;
    private static string _prefix;
    private static string _currentLang;
    private static ManualLogSource _log;

    internal static void Init(ManualLogSource log)
    {
        _log = log;
        var assembly = Assembly.GetExecutingAssembly();
        _langDir = Path.Combine(Path.GetDirectoryName(assembly.Location) ?? ".", "lang");
        _prefix = assembly.GetName().Name;

        IndexLangFiles();

        _fallback = LoadLang("en");
        if (_fallback.Count == 0)
            _log?.LogWarning("[Loc] No English fallback loaded - translations will return raw keys");

        Reload();
    }

    /// <summary>Current game language, or "en" before the game has settled on one.</summary>
    private static string CurrentGameLang()
    {
        try
        {
            var lang = GameSettings._cur_lng;
            return string.IsNullOrEmpty(lang) ? "en" : lang;
        }
        catch
        {
            // GameSettings isn't up yet during very early plugin Awake.
            return "en";
        }
    }

    /// <summary>
    /// True once the game has actually applied a language. Empty until GameSettings.ApplyLanguageChange
    /// runs, which is well after plugin Awake — anything spoken before then would be English.
    /// </summary>
    internal static bool LanguageKnown
    {
        get
        {
            try { return !string.IsNullOrEmpty(GameSettings._cur_lng); }
            catch { return false; }
        }
    }

    internal static void Reload()
    {
        var lang = CurrentGameLang();
        if (_currentLang == lang) return;
        _currentLang = lang;
        _translations = Normalize(lang) == "en" ? _fallback : LoadLang(lang);
    }

    /// <summary>Looks up a spoken string. Falls back to English, then to the key itself.</summary>
    internal static string Get(string key)
    {
        var found = Find(key);
        if (found != null) return found;
        _log?.LogWarning($"[Loc] Missing key: {key}");
        return key;
    }

    /// <summary>
    /// Like <see cref="Get"/>, but returns null instead of the key when there is no translation and
    /// stays quiet about it. For lookups that are *expected* to miss and have their own fallback —
    /// e.g. naming a window after its class when we haven't given that window a name of its own.
    /// </summary>
    internal static string Find(string key)
    {
        if (_currentLang != CurrentGameLang()) Reload();

        if (_translations.TryGetValue(key, out var value)) return value;
        if (_fallback.TryGetValue(key, out var fallback)) return fallback;
        return null;
    }

    /// <summary>
    /// Looks up a string containing <c>{0}</c>-style placeholders and fills them in. Translators can
    /// reorder the placeholders freely, which German word order regularly needs.
    /// </summary>
    internal static string Fmt(string key, params object[] args)
    {
        var format = Get(key);
        try
        {
            return string.Format(format, args);
        }
        catch (FormatException)
        {
            // A translation with a malformed placeholder shouldn't silence the mod.
            _log?.LogWarning($"[Loc] Bad format string for key: {key}");
            return format;
        }
    }

    /// <summary>Picks the singular or plural form of a key ("<c>key.one</c>" / "<c>key.other</c>").</summary>
    internal static string Plural(string key, int count, params object[] args)
    {
        return Fmt(count == 1 ? key + ".one" : key + ".other", args);
    }

    // Duplicate normalized keys (e.g. pt-br.json and pt_BR.json) keep the first one found.
    private static void IndexLangFiles()
    {
        LangFiles.Clear();
        if (!Directory.Exists(_langDir))
        {
            _log?.LogWarning($"[Loc] Lang directory not found: {_langDir}");
            return;
        }

        foreach (var path in Directory.GetFiles(_langDir, $"{_prefix}.*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var prefix = _prefix + ".";
            if (name == null || !name.StartsWith(prefix)) continue;
            var key = Normalize(name.Substring(prefix.Length));
            if (LangFiles.ContainsKey(key))
            {
                _log?.LogWarning($"[Loc] Duplicate lang file for '{key}': keeping {Path.GetFileName(LangFiles[key])}, ignoring {Path.GetFileName(path)}");
                continue;
            }
            LangFiles[key] = path;
        }
    }

    private static Dictionary<string, string> LoadLang(string lang)
    {
        var key = Normalize(lang);
        if (!LangFiles.TryGetValue(key, out var path))
        {
            if (key != "en")
                _log?.LogInfo($"[Loc] No translation file for '{lang}' (normalized '{key}'), falling back to English");
            return new Dictionary<string, string>();
        }

        try
        {
            var dict = new Dictionary<string, string>();
            var json = File.ReadAllText(path, System.Text.Encoding.UTF8);

            // Minimal parser for a flat string->string object, matching Shared/Localisation.cs so the
            // mod has no hard dependency on a JSON library at runtime.
            var i = json.IndexOf('{') + 1;
            while (i < json.Length)
            {
                var keyStart = json.IndexOf('"', i);
                if (keyStart < 0) break;
                var keyEnd = FindUnescapedQuote(json, keyStart + 1);
                var jsonKey = Unescape(json.Substring(keyStart + 1, keyEnd - keyStart - 1));

                var valStart = json.IndexOf('"', keyEnd + 1);
                if (valStart < 0) break;
                var valEnd = FindUnescapedQuote(json, valStart + 1);
                dict[jsonKey] = Unescape(json.Substring(valStart + 1, valEnd - valStart - 1));

                i = valEnd + 1;
            }

            _log?.LogInfo($"[Loc] Loaded {dict.Count} keys from {Path.GetFileName(path)}");
            return dict;
        }
        catch (Exception ex)
        {
            _log?.LogError($"[Loc] Failed to read {Path.GetFileName(path)}: {ex.Message}");
            return new Dictionary<string, string>();
        }
    }

    private static string Unescape(string s)
    {
        return s.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\\\", "\\");
    }

    private static string Normalize(string code)
    {
        return string.IsNullOrEmpty(code) ? "en" : code.ToLowerInvariant().Replace('-', '_');
    }

    private static int FindUnescapedQuote(string s, int start)
    {
        for (var i = start; i < s.Length; i++)
        {
            if (s[i] == '\\') { i++; continue; }
            if (s[i] == '"') return i;
        }
        return s.Length;
    }
}
