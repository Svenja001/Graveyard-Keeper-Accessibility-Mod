using System.Diagnostics;
using System.Text.RegularExpressions;

namespace BeamMeUpGerry;

[Serializable]
[SuppressMessage("ReSharper", "InconsistentNaming")]
internal class Location
{
    private const string Locations = "Locations";
    [NonSerialized] public bool enabled;
    [NonSerialized] public bool defaultLocation;
    public string zone;
    public string preset;
    [NonSerialized] public string teleportPoint;
    public Vector2 coords;
    public EnvironmentEngine.State state;
    public bool customZone;

    public Location()
    {
    }

    public Location(string zone, string preset, string teleportPoint, Vector2 coords, bool defaultLocation = false, EnvironmentEngine.State state = EnvironmentEngine.State.RealTime)
    {
        this.zone = zone;
        this.preset = preset;
        this.teleportPoint = teleportPoint;
        this.coords = coords;
        this.state = state;
        this.defaultLocation = defaultLocation;
        customZone = false;
    }

    internal static string GetSavePath()
    {
        var assemblyName = Assembly.GetExecutingAssembly().GetName().Name;
        assemblyName = Regex.Replace(assemblyName, "[^a-zA-Z0-9_.]+", "", RegexOptions.Compiled);
        var assemblyFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        var returnPath = assemblyFolder != null ? Path.Combine(assemblyFolder, Locations) : Path.Combine(Paths.PluginPath, assemblyName, Locations);
        return returnPath;
    }

    public void SaveJson()
    {
        var json = JsonUtility.ToJson(this, true);
        var path = GetSavePath();

        if (!Directory.Exists(path)) Directory.CreateDirectory(path);

        var fileName = $"{zone}.json";

        var saveLocation = Path.Combine(path, fileName);
        try
        {
            File.WriteAllText(saveLocation, json);
            if (!Plugin.OpenNewLocationFileOnSave.Value) return;
            var startInfo = new ProcessStartInfo
            {
                FileName = saveLocation,
                UseShellExecute = true,
                CreateNoWindow = false
            };
            Process.Start(startInfo);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError(e);
        }
    }

    public static Location LoadFromJson(string path)
    {
        if (!File.Exists(path))
        {
            Plugin.Log.LogError($"Location.LoadFromJson - File {path} does not exist!");
            return new Location();
        }

        var json = File.ReadAllText(path);
        return JsonUtility.FromJson<Location>(json);
    }
}