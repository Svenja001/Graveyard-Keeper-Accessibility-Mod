using System.Diagnostics;
using System.IO;

namespace GraveyardKeeperAccessibility;

internal static class ScreenReader
{
    private static bool _prismAvailable;
    private static bool _tolkAvailable;
    private static bool _sapiAvailable;
    private static Process _sapiProcess;
    private static StreamWriter _sapiStdin;
    private static string _lastMenuText = "";
    private static ManualLogSource _log;

    internal static void Init(ManualLogSource log)
    {
        _log = log;

        // Three speech backends, tried in order, and at most one of the two native libraries ever
        // gets off the ground: Prism is 64-bit only, Tolk is bundled 32-bit only, and each refuses
        // to initialise in the other's process (see TolkWrapper). So they cannot both hold the
        // screen reader, and the choice needs no setting - the bitness of the game the player
        // bought decides it. Steam and the other 64-bit storefronts get Prism, GOG gets Tolk.
        _prismAvailable = PrismWrapper.Init(log);
        if (!_prismAvailable)
            _tolkAvailable = TolkWrapper.Init(log);
        if (!_prismAvailable && !_tolkAvailable)
            _sapiAvailable = InitSapi();

        if (!_prismAvailable && !_tolkAvailable && !_sapiAvailable)
            log.LogError("No TTS output available");
    }

    private static bool InitSapi()
    {
        try
        {
            var vbsPath = Path.Combine(Path.GetTempPath(), "gk_accessibility_tts.vbs");
            File.WriteAllText(vbsPath,
                "Set v=CreateObject(\"SAPI.SpVoice\")\r\n" +
                "Do While Not WScript.StdIn.AtEndOfStream\r\n" +
                "On Error Resume Next\r\n" +
                "s=WScript.StdIn.ReadLine\r\n" +
                "If Len(s)>0 Then v.Speak s,3\r\n" +
                "On Error Goto 0\r\n" +
                "Loop\r\n");

            _sapiProcess = new Process();
            _sapiProcess.StartInfo = new ProcessStartInfo
            {
                FileName = "cscript.exe",
                Arguments = "//nologo \"" + vbsPath + "\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                CreateNoWindow = true
            };
            _sapiProcess.Start();
            _sapiStdin = _sapiProcess.StandardInput;
            _sapiStdin.AutoFlush = true;

            _log.LogInfo("SAPI voice process started");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError($"SAPI init failed: {ex.Message}");
            return false;
        }
    }

    internal static void Shutdown()
    {
        if (_prismAvailable)
            PrismWrapper.Shutdown();
        if (_tolkAvailable)
            TolkWrapper.Shutdown();

        KillSapi();
        _prismAvailable = false;
        _tolkAvailable = false;
        _sapiAvailable = false;
    }

    /// <summary>
    /// When we last said anything at all (unscaled time). Used to detect stretches of silence —
    /// see <see cref="CutsceneAnnouncer"/>, which has to distinguish "a cutscene is playing a quiet
    /// camera pan" from "nothing is happening", something a sighted player reads off the screen.
    /// </summary>
    internal static float LastSpokenAt { get; private set; }

    /// <summary>
    /// True when output also reaches a braille display. Only a real screen reader can do this —
    /// Prism's backends on 64-bit, Tolk's on 32-bit; the SAPI fallback is speech-only.
    /// </summary>
    internal static bool SupportsBraille =>
        (_prismAvailable && PrismWrapper.SupportsBraille) || (_tolkAvailable && TolkWrapper.SupportsBraille);

    internal static bool Say(string text, bool interrupt = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        LastSpokenAt = Time.unscaledTime;

        // Prism routes this to speech *and* braille in one call when the screen reader supports
        // both, so everything the mod says is readable on a display without a second call site.
        if (_prismAvailable)
            return PrismWrapper.Speak(text, interrupt);

        // Tolk does the same job on 32-bit, and re-detects the screen reader on every call. A false
        // here means it has gone away mid-session, so this line goes out through SAPI instead and
        // the next one tries Tolk again - which is how a screen reader restart recovers by itself.
        if (_tolkAvailable && TolkWrapper.Speak(text, interrupt))
            return true;

        return SapiSpeak(text);
    }

    /// <summary>
    /// Writes to the braille display only, leaving speech alone. Use this for text that is worth
    /// reading but not worth interrupting speech for — a status line the player can pan over at
    /// their own pace. No-op when there is no braille-capable backend.
    /// </summary>
    internal static bool Braille(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (_prismAvailable) return PrismWrapper.Braille(text);
        if (_tolkAvailable) return TolkWrapper.Braille(text);
        return false;
    }

    private static bool SapiSpeak(string text)
    {
        _log?.LogInfo($"[SAPI] Attempting to speak: {text}");

        if (_sapiProcess == null || _sapiProcess.HasExited)
        {
            _log?.LogWarning("[SAPI] Process died, restarting");
            KillSapi();
            _sapiAvailable = InitSapi();
            if (!_sapiAvailable) return false;
        }

        try
        {
            var clean = text.Replace("\r", "").Replace("\n", " ").Replace("\0", "");
            if (clean.Length > 500) clean = clean.Substring(0, 500);
            _log?.LogInfo($"[SAPI] Writing to stdin: {clean}");
            _sapiStdin.WriteLine(clean);
            _sapiStdin.Flush();
            _log?.LogInfo("[SAPI] Write successful");
            return true;
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[SAPI] Write failed: {ex.Message}, restarting");
            KillSapi();
            _sapiAvailable = InitSapi();
            return false;
        }
    }

    private static void KillSapi()
    {
        try { _sapiStdin?.Close(); } catch { }
        try { if (_sapiProcess != null && !_sapiProcess.HasExited) _sapiProcess.Kill(); } catch { }
        _sapiProcess = null;
        _sapiStdin = null;
    }

    internal static bool SayMenu(string text, bool interrupt = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text == _lastMenuText) return false;
        _lastMenuText = text;
        return Say(text, interrupt);
    }

    internal static void ClearMenuContext()
    {
        _lastMenuText = "";
    }

    internal static string StripNguiCodes(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // The game embeds skull/cross counts and money amounts as inline sprite tokens,
        // e.g. "(wskull)20", "(rskull)5", "(cross)10", "(gld)10 (slv)20 (brz)5" (the number
        // always immediately follows the token). Spoken literally these are unintelligible,
        // so turn them into words like "20 white skulls" / "5 red skulls" / "10 crosses" /
        // "10 gold" / "20 silver" / "5 bronze" (see Trading.FormatMoney for the coin tokens).
        if (text.Contains('('))
        {
            text = Regex.Replace(text, @"\((wskull|rskull|skull|cross|gld|slv|brz)\)(?:\s?(-?\d+(?:\.\d+)?))?", TokenToWords);
            text = Regex.Replace(text, StatTokenPattern, StatTokenToWords);
            // A dropped decorative icon (see StatTokenToWords) leaves a double space or a space in
            // front of the punctuation it sat before; tidy both so the sentence still reads clean.
            text = Regex.Replace(text, @" {2,}", " ");
            text = Regex.Replace(text, @" ([,.;:!?])", "$1");
        }
        // Strip NGUI color codes: [XXXXXX], [-], [c], [/c], etc.
        if (text.Contains('['))
            text = Regex.Replace(text, @"\[[\da-fA-F]{6}\]|\[-\]|\[/?c\]", "");
        return text;
    }

    /// <summary>
    /// The bar and tech-point sprite tokens, in both the orders the game writes them.
    ///
    /// <see cref="GameRes.ToFormattedString"/> — which builds every "effect on use" and "energy
    /// consumption" line — emits SIGN, token, then the amount: "+(hp)3", "-(en)5". Hand-written
    /// description text does the opposite and puts the number first: "Gives 25(b) when used". Both
    /// are matched here, so the amount is whichever group actually captured.
    ///
    /// Note the game's short ids: energy is "(en)" and sanity is "(sn)", NOT the spelled-out names
    /// the balance data uses for the same values.
    ///
    /// The same sprite mechanism carries the counters that are only ever drawn as an icon —
    /// NPC relationship "(happy)"/"(rel)", a grave part's rating "(wr)", the merchant's fame
    /// "(fame)"/"(marketing)", the refugee camp's happiness/water and the tavern's quality. Task
    /// and door-lock text is written around those icons ("Die Tuer ist verschlossen, bis ich
    /// (happy)80 beim Ingenieur habe"), so without a word for them the sentence loses the very
    /// thing it is about. Longer ids come first in the alternation: the single letters r/g/b/v
    /// would otherwise swallow the start of "rel" and the "refugee_" ids and kill the match.
    ///
    /// Quest text writes the amount after a space ("Erreiche (rel) 100"), so one optional space is
    /// allowed — inside the amount group, never on its own, or a bare "(wskull) deines Friedhofs"
    /// would lose the space that separates the two words.
    /// </summary>
    private const string StatTokenPattern =
        @"([+-])?(?:(\d+(?:[.,]\d+)?)\s*)?\((hp|en|sn|energy|sanity|faith|gratitude_points|gratitude points|" +
        @"happy|rel|refugee_happiness_filler|refugee_happiness_slot|refugee_happiness|refugee_water|" +
        @"soul_zone_capacity|marketing|fame|tavern|wr|r|g|b|v)\)(?:\s?(\d+(?:[.,]\d+)?))?";

    /// <summary>
    /// "+(hp)3" -> "gives 3 health", "-(en)5" -> "drains 5 energy", "25(b)" -> "25 blue points",
    /// a bare "(faith)" -> "faith". Spoken literally these tokens come out as "hp", "en" or worse:
    /// the sprite is an icon on screen, and the text around it is written assuming you can see it.
    /// </summary>
    private static string StatTokenToWords(Match m)
    {
        string sign = m.Groups[1].Value;
        string amount = m.Groups[2].Success ? m.Groups[2].Value
                      : m.Groups[4].Success ? m.Groups[4].Value
                      : null;
        string token = m.Groups[3].Value;

        string value;
        switch (token)
        {
            case "hp":     value = Bar("perk.health", amount); break;
            case "en":
            case "energy": value = Bar("perk.energy", amount); break;
            case "sn":
            case "sanity": value = Bar("perk.sanity", amount); break;
            case "faith":  value = Bar("perk.faith", amount); break;

            // Icon-only counters. Nothing is spoken for them anywhere else, so the name is the
            // whole message: "(happy)80" has to come out as "80 relationship" / "80 Beziehung".
            case "happy":
            case "rel":    value = Bar("stat.relationship", amount); break;
            case "wr":     value = Bar("stat.grave_quality", amount); break;
            case "fame":
            case "marketing": value = Bar("stat.fame", amount); break;
            // These six only ever appear as pure decoration next to the word they illustrate
            // ("<Wasser> (refugee_water)") or with a number ("40 (tavern)"). Naming the uncounted
            // ones would stutter the sentence — "Wasser Wasser" — so an amount-less one is dropped.
            case "refugee_happiness": value = BarOrDrop("stat.refugee_happiness", amount); break;
            case "refugee_happiness_filler": value = BarOrDrop("stat.satisfaction", amount); break;
            case "refugee_happiness_slot": value = BarOrDrop("stat.camp_quality", amount); break;
            case "refugee_water": value = BarOrDrop("stat.water", amount); break;
            case "tavern": value = BarOrDrop("stat.tavern_quality", amount); break;
            case "soul_zone_capacity": value = BarOrDrop("stat.gratitude_capacity", amount); break;

            // Tech points have their own counted phrasing ("1 blue point" / "5 blue points"), so
            // they go through the shared point wording rather than a bare noun plus a number.
            default:
                var id = token == "gratitude points" ? "gratitude_points" : token;
                value = amount == null
                    ? Loc.Get(PointBareKey(id))
                    : InventoryItemHandler.PointPhrase(id, amount);
                break;
        }

        // The sign is only meaningful with a number behind it, and it is the whole difference
        // between food that feeds you and a swing that costs you: say it in words rather than
        // leaving a "+" or "-" for the speech engine to swallow or mispronounce.
        if (amount == null || sign.Length == 0) return value;
        return Loc.Fmt(sign == "-" ? "token.lose" : "token.gain", value);
    }

    /// <summary>"3 water" — the name with its amount, or nothing at all when uncounted.</summary>
    private static string BarOrDrop(string wordKey, string amount)
        => amount == null ? "" : Bar(wordKey, amount);

    /// <summary>"3 health" — the bar name with its amount, or the bare name when uncounted.</summary>
    private static string Bar(string wordKey, string amount)
    {
        var word = Loc.Get(wordKey);
        return amount == null ? word : $"{amount} {word}";
    }

    /// <summary>Uncounted name of a tech-point pool ("blue points").</summary>
    private static string PointBareKey(string id)
    {
        switch (id)
        {
            case "r": return "points.red.bare";
            case "g": return "points.green.bare";
            case "v": return "points.violet.bare";
            case "gratitude_points": return "points.gratitude.bare";
            default: return "points.blue.bare";
        }
    }

    private static string TokenToWords(Match m)
    {
        string token = m.Groups[1].Value;
        string num = m.Groups[2].Success ? m.Groups[2].Value : null;
        // Coins (gld/slv/brz) are mass nouns — no singular/plural distinction, "1 gold" reads fine.
        switch (token)
        {
            case "gld": return num != null ? Loc.Fmt("money.gold", num) : Loc.Get("token.gold");
            case "slv": return num != null ? Loc.Fmt("money.silver", num) : Loc.Get("token.silver");
            case "brz": return num != null ? Loc.Fmt("money.bronze", num) : Loc.Get("token.bronze");
        }

        // "(skull)" and "(wskull)" are both the white skull; "(rskull)" is red.
        string key = token switch
        {
            "rskull" => "token.red_skull",
            "cross"  => "token.cross",
            _        => "token.white_skull",
        };

        // A token with no number is the bare noun ("(wskull)" as a legend); with one it's counted,
        // and German needs the count inside the phrase so the plural form can agree.
        if (num == null) return Loc.Get(key + ".bare");
        return Loc.Plural(key, num == "1" ? 1 : 2, num);
    }
}
