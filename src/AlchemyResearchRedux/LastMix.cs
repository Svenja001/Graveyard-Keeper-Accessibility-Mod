namespace AlchemyResearchRedux;

// Remembers the ingredients last used at each alchemy bench (keyed by station
// type) so the player can drop the same set back in instead of re-picking.
public static class LastMix
{
    private static Dictionary<string, List<string>> Mixes = new();

    private static string SavePath => Path.Combine(Application.persistentDataPath, "AlchemyLastMix.json");

    public static void Set(string objId, List<string> ingredientIds)
    {
        if (string.IsNullOrEmpty(objId)) return;
        Mixes[objId] = ingredientIds;
        SaveToFile();
    }

    public static bool TryGet(string objId, out List<string> ingredientIds)
    {
        return Mixes.TryGetValue(objId, out ingredientIds);
    }

    public static void SaveToFile()
    {
        var json = JsonConvert.SerializeObject(Mixes, Formatting.Indented);
        File.WriteAllText(SavePath, json);
    }

    public static void LoadFromFile()
    {
        if (!File.Exists(SavePath))
        {
            Mixes = new Dictionary<string, List<string>>();
            return;
        }

        var json = File.ReadAllText(SavePath);
        Mixes = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(json) ?? new Dictionary<string, List<string>>();
    }
}
