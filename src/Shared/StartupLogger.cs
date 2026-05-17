using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using MonoMod.Utils;
using UnityEngine;

namespace Shared;

[Harmony]
internal static class StartupLogger
{
    private const string SentinelName = "GYK_ModSummaryLogged";

    // Do not move back to PlatformSpecific.FixScreenModeAfterStart - chaining postfixes there
    // crashed Mono 32-bit during Preloader on some load orders.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(MainGame), nameof(MainGame.Awake))]
    public static void MainGame_Awake()
    {
        try
        {
            if (GameObject.Find(SentinelName)) return;

            var sentinel = new GameObject(SentinelName);
            UnityEngine.Object.DontDestroyOnLoad(sentinel);

            var version = $"{LazyConsts.VERSION:0.000#}".Replace(",", ".");
            var bepinexVersion = typeof(Chainloader).Assembly.GetName().Version;
            var managerObj = Chainloader.ManagerObject;
            var bepinexManagerHidden = managerObj != null && managerObj.hideFlags.HasFlag(HideFlags.HideAndDontSave);
            var buildGuid = Application.buildGUID;
            var platform = PlatformHelper.Current;
            var store = DetectStorefront();

            var log = BepInEx.Logging.Logger.CreateLogSource("GYK Mods");
            log.LogInfo("==========================================");
            log.LogInfo("  Graveyard Keeper Mod Summary");
            log.LogInfo("==========================================");
            log.LogInfo($"  Game      : ver. {version} (BuildGUID: {buildGuid})");
            log.LogInfo($"  BepInEx   : v{bepinexVersion} (Manager Hidden: {bepinexManagerHidden})");
            log.LogInfo($"  Platform  : {platform}");
            log.LogInfo($"  Storefront: {store}");
            if (!bepinexManagerHidden)
            {
                log.LogWarning("  BepInEx Manager GameObject is NOT hidden - Unity event methods (Awake, Start, Update) will not fire on plugins!");
                log.LogWarning("  To fix: open BepInEx/config/BepInEx.cfg, find [Chainloader] section, set HideManagerGameObject = true");
            }
            log.LogInfo("------------------------------------------");
            log.LogInfo("  Loaded plugins:");

            foreach (var plugin in Chainloader.PluginInfos.Values.OrderBy(p => p.Metadata.Name))
            {
                log.LogInfo($"    {plugin.Metadata.Name} v{plugin.Metadata.Version} | {plugin.Metadata.GUID}");
            }

            log.LogInfo($"------------------------------------------");
            log.LogInfo($"  Total: {Chainloader.PluginInfos.Count} plugins");
            log.LogInfo("------------------------------------------");
            log.LogInfo("  Plugin configurations:");
            LogPluginConfigs(log);
            log.LogInfo("==========================================");

            if (!Chainloader.ConfigHideBepInExGOs.Value)
            {
                log.LogWarning("  BepInEx HideManagerGameObject was disabled - enabling it to prevent Unity event methods from failing");
                Chainloader.ConfigHideBepInExGOs.Value = true;
                if (managerObj)
                {
                    managerObj.hideFlags = HideFlags.HideAndDontSave;
                    UnityEngine.Object.DontDestroyOnLoad(managerObj);
                }
            }

            BepInEx.Logging.Logger.Sources.Remove(log);
        }
        catch (Exception ex)
        {
            BepInEx.Logging.Logger.CreateLogSource("GYK Mods").LogError($"StartupLogger failed: {ex}");
        }
    }

    private static void LogPluginConfigs(ManualLogSource log)
    {
        foreach (var plugin in Chainloader.PluginInfos.Values.OrderBy(p => p.Metadata.Name))
        {
            try
            {
                if (plugin.Instance is not { } instance)
                {
                    log.LogInfo($"  [{plugin.Metadata.Name}] (instance not available)");
                    continue;
                }

                var config = instance.Config;
                if (config == null || config.Keys.Count == 0)
                {
                    log.LogInfo($"  [{plugin.Metadata.Name}] (no config entries)");
                    continue;
                }

                log.LogInfo($"  [{plugin.Metadata.Name}]");
                var grouped = config.Keys
                    .GroupBy(k => k.Section)
                    .OrderBy(g => g.Key);
                foreach (var section in grouped)
                {
                    log.LogInfo($"    {section.Key}");
                    foreach (var key in section.OrderBy(k => k.Key))
                    {
                        object value;
                        try
                        {
                            value = config[key].BoxedValue;
                        }
                        catch (Exception ex)
                        {
                            value = $"<error reading value: {ex.Message}>";
                        }
                        log.LogInfo($"      {key.Key} = {value}");
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogInfo($"  [{plugin.Metadata.Name}] (config dump failed: {ex.Message})");
            }
        }
    }

    private static readonly string[] PiracyFiles =
    [
        // Steam emulators
        "SmartSteamEmu.ini", "steam_emu.ini", "goldberg_emulator.dll",
        "steamclient_loader.dll", "steam_api64_o.dll", "steam_api.cdx", "steam_api64.cdx.dll",
        "steam_interfaces.txt", "local_save.txt", "valve.ini",
        "coldclient.dll", "ColdClientLoader.ini", "steamless.dll", "GreenLuma",
        "SteamFix.dll", "SteamFix64.dll", "LumaEmu.ini", "Lumaplay",

        // Goldberg emulator config
        "account_name.txt", "user_steam_id.txt", "force_listen_port.txt",
        "goldberg_steam_appid.txt",

        // DLC unlockers
        "CreamAPI.dll", "creamapi.dll", "cream_api.ini", "ScreamAPI.dll", "UnlockAll.dll",

        // Proxy loaders
        "Koaloader.dll", "Koaloader64.dll",

        // Online fixes
        "OnlineFix.dll", "OnlineFix.url", "online-fix.me",

        // Scene group markers
        "codex.ini", "codex64.dll", "CODEX",
        "SKIDROW", "SKIDROW.ini",
        "CPY", "PLAZA", "HOODLUM", "EMPRESS", "TENOKE",
        "PROPHET", "REVOLT", "DARKSiDERS", "RAZOR1911",
        "FLT", "FLT.dll", "RUNE", "RUNE.ini",
        "TiNYiSO", "RELOADED", "RLD!", "DOGE", "CHRONOS", "DINOByTES", "I_KnoW",
        "ElAmigos", "FitGirl", "DODI", "xatab", "KaOs", "IGG", "Masquerade",

        // Common crack files
        "3dmgame.dll", "ALI213.dll", "crack.exe", "Crack.nfo",
        "crackfix", "CrackOnly", "gamefix.dll",
        "nosTEAM", "NoSteam", "FCKDRM", "NoDRM", "VALVEEMPRESS",
    ];

    private static string[] GetSearchDirs()
    {
        var root = Directory.GetCurrentDirectory();
        var dirs = new List<string> { root };

        // Unity games store platform DLLs in *_Data/Plugins/ subdirectories
        try
        {
            foreach (var dataDir in Directory.GetDirectories(root, "*_Data"))
            {
                var pluginsDir = Path.Combine(dataDir, "Plugins");
                if (!Directory.Exists(pluginsDir)) continue;
                dirs.Add(pluginsDir);
                dirs.AddRange(Directory.GetDirectories(pluginsDir));
            }
        }
        catch
        {
            // Ignore permission errors
        }

        return dirs.ToArray();
    }

    private static bool FileExistsInAny(string[] dirs, string filename)
    {
        return dirs.Any(d => File.Exists(Path.Combine(d, filename)));
    }

    private static bool DirExistsInAny(string[] dirs, string dirname)
    {
        return dirs.Any(d => Directory.Exists(Path.Combine(d, dirname)));
    }

    private static string DetectStorefront()
    {
        var root = Directory.GetCurrentDirectory();
        var dirs = GetSearchDirs();
        var store = "Unknown";

        if (FileExistsInAny(dirs, "steam_api.dll") ||
            FileExistsInAny(dirs, "steam_api64.dll") ||
            File.Exists(Path.Combine(root, "steam_appid.txt")))
            store = "Steam";
        else if (Directory.GetFiles(root, "goggame-*.info").Any() ||
                 FileExistsInAny(dirs, "galaxy.dll") ||
                 FileExistsInAny(dirs, "Galaxy64.dll") ||
                 FileExistsInAny(dirs, "GalaxyPeer.dll"))
            store = "GOG";
        else if (FileExistsInAny(dirs, "EOSSDK-Win64-Shipping.dll") ||
                 FileExistsInAny(dirs, "EpicOnlineServices.dll") ||
                 Directory.Exists(Path.Combine(root, ".egstore")))
            store = "Epic";
        else if (root.Contains("WindowsApps") ||
                 File.Exists(Path.Combine(root, "appxmanifest.xml")) ||
                 File.Exists(Path.Combine(root, "microsoft.gameconfig")))
            store = "Xbox/Microsoft Store";
        else if (IsProcessRunning("steam")) store = "Steam (process only)";
        else if (IsProcessRunning("GalaxyClient")) store = "GOG (process only)";
        else if (IsProcessRunning("EpicGamesLauncher")) store = "Epic (process only)";
        else if (IsProcessRunning("XboxApp") || IsProcessRunning("GamingServices")) store = "Xbox (process only)";

        var isPirated = PiracyFiles.Any(pirate =>
            FileExistsInAny(dirs, pirate) || DirExistsInAny(dirs, pirate)
        );

        // Goldberg emulator leaves config in steam_settings/
        if (!isPirated && Directory.Exists(Path.Combine(root, "steam_settings")))
        {
            var settingsDir = Path.Combine(root, "steam_settings");
            isPirated = File.Exists(Path.Combine(settingsDir, "force_account_name.txt")) ||
                        File.Exists(Path.Combine(settingsDir, "force_steamid.txt")) ||
                        File.Exists(Path.Combine(settingsDir, "force_language.txt"));
        }

        if (isPirated)
            store += " + Possible Pirated/Cracked Files Found!";

        return store;
    }

    private static bool IsProcessRunning(string name) => Process.GetProcessesByName(name).Length > 0;
}