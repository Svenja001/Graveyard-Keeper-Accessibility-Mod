using System.Diagnostics;
using System.IO;

namespace GraveyardKeeperAccessibility;

internal static class ScreenReader
{
    private static bool _prismAvailable;
    private static bool _sapiAvailable;
    private static Process _sapiProcess;
    private static StreamWriter _sapiStdin;
    private static string _lastMenuText = "";
    private static ManualLogSource _log;

    internal static void Init(ManualLogSource log)
    {
        _log = log;
        _prismAvailable = PrismWrapper.Init(log);
        if (!_prismAvailable)
            _sapiAvailable = InitSapi();

        if (!_prismAvailable && !_sapiAvailable)
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

        KillSapi();
        _prismAvailable = false;
        _sapiAvailable = false;
    }

    internal static bool Say(string text, bool interrupt = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        if (_prismAvailable)
            return PrismWrapper.Speak(text, interrupt);

        return SapiSpeak(text);
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
        // The game embeds skull/cross counts as inline sprite tokens, e.g.
        // "(wskull)20", "(rskull)5", "(cross)10" (the number always immediately
        // follows the token). Spoken literally these are unintelligible, so turn
        // them into words like "20 white skulls" / "5 red skulls" / "10 crosses".
        if (text.Contains('('))
            text = Regex.Replace(text, @"\((wskull|rskull|skull|cross)\)(-?\d+(?:\.\d+)?)?", SkullTokenToWords);
        // Strip NGUI color codes: [XXXXXX], [-], [c], [/c], etc.
        if (text.Contains('['))
            text = Regex.Replace(text, @"\[[\da-fA-F]{6}\]|\[-\]|\[/?c\]", "");
        return text;
    }

    private static string SkullTokenToWords(Match m)
    {
        string token = m.Groups[1].Value;
        string num = m.Groups[2].Success ? m.Groups[2].Value : null;
        // "(skull)" and "(wskull)" are both the white skull; "(rskull)" is red.
        bool plural = num == null || num != "1";
        string label;
        switch (token)
        {
            case "rskull": label = plural ? "red skulls" : "red skull"; break;
            case "cross":  label = plural ? "crosses" : "cross"; break;
            default:       label = plural ? "white skulls" : "white skull"; break;
        }
        return num != null ? num + " " + label : label;
    }
}
