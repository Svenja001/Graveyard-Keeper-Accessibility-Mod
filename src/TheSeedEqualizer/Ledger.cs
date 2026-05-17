using Newtonsoft.Json;

namespace TheSeedEqualizer;

// Per-crop seed ledger written to ledger.json next to the plugin DLL. Tracks
// seeds-in vs seeds-out per crop for observability; writes are best-effort.
public static class Ledger
{
    public sealed class CropTotals
    {
        public int SpendEvents;
        public int HarvestEvents;
        public int SeedsIn;
        public int SeedsOut;
        public int Net;
    }

    public sealed class EventEntry
    {
        public string AtUtc;
        public string Type;       // "spend" | "harvest"
        public string Kind;       // player_garden | zombie_garden | zombie_vineyard | refugee_garden
        public string CropType;
        public string CraftOrObjId;
        public string SeedId;
        public int    Qty;
        public string PositionKey;
    }

    public sealed class LedgerFile
    {
        public int Schema = 2;
        public string GeneratedAtUtc;
        public Dictionary<string, CropTotals> TotalsByCrop = new();
        public List<EventEntry> RecentEvents = new();
    }

    private const int RecentEventsCap = 250;

    private static readonly object Sync = new();
    private static LedgerFile _file;
    private static bool _loaded;
    private static bool _dirty;
    private static DebouncedSaver _saver;

    private static string SavePath
    {
        get
        {
            var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            return Path.Combine(dir ?? string.Empty, "ledger.json");
        }
    }

    private static LedgerFile File
    {
        get
        {
            if (!_loaded) Load();
            return _file;
        }
    }

    private static void Load()
    {
        try
        {
            if (System.IO.File.Exists(SavePath))
            {
                var json = System.IO.File.ReadAllText(SavePath);
                var loaded = JsonConvert.DeserializeObject<LedgerFile>(json);
                if (loaded != null && loaded.Schema == 2)
                {
                    _file = loaded;
                    _file.TotalsByCrop ??= new Dictionary<string, CropTotals>();
                    _file.RecentEvents ??= new List<EventEntry>();
                }
                else
                {
                    LogHelper.Info($"[Ledger] Existing ledger.json is schema {loaded?.Schema ?? 0}, expected 2. Starting fresh - old file will be overwritten on next save.");
                    _file = new LedgerFile();
                }
            }
            else
            {
                _file = new LedgerFile();
            }
        }
        catch (Exception ex)
        {
            LogHelper.Error($"[Ledger] Failed to load {SavePath}: {ex.Message}. Starting with an empty ledger.");
            _file = new LedgerFile();
        }
        _loaded = true;
    }

    private static void Save()
    {
        try
        {
            _file.GeneratedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var json = JsonConvert.SerializeObject(_file, Formatting.Indented);
            System.IO.File.WriteAllText(SavePath, json);
            _dirty = false;
        }
        catch (Exception ex)
        {
            LogHelper.Error($"[Ledger] Failed to write {SavePath}: {ex.Message}. Will retry on next event.");
        }
    }

    public static string PositionKeyFor(WorldGameObject wgo)
    {
        if (wgo == null || wgo.transform == null) return string.Empty;
        var p = wgo.transform.position;
        return string.Format(CultureInfo.InvariantCulture, "{0:F1},{1:F1}", p.x, p.y);
    }

    // Pull the crop name out of any planting/growing/ready id by skipping the
    // known structural tokens.
    public static string CropTypeFromId(string id)
    {
        if (string.IsNullOrEmpty(id)) return id;
        var parts = id.Split('_');
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (string.IsNullOrEmpty(p)) continue;
            if (p.Length == 1 && char.IsDigit(p[0])) continue;
            switch (p)
            {
                case "garden":
                case "refugee":
                case "grow":
                case "desk":
                case "vineyard":
                case "planting":
                case "growing":
                case "ready":
                    continue;
            }
            return p;
        }
        return id;
    }

    public static string KindFromId(string id)
    {
        if (string.IsNullOrEmpty(id)) return "unknown";
        if (id.Contains("vineyard")) return "zombie_vineyard";
        if (id.StartsWith("refugee_garden")) return "refugee_garden";
        if (id.Contains("grow_desk")) return "zombie_garden";
        if (id.StartsWith("garden")) return "player_garden";
        return "unknown";
    }

    public static void RecordSpend(WorldGameObject wgo, string sourceId, string seedId, int qty)
    {
        if (qty <= 0) return;
        EnsureSaver();
        var crop = CropTypeFromId(sourceId);
        var kind = KindFromId(sourceId);
        var posKey = PositionKeyFor(wgo);

        lock (Sync)
        {
            var totals = GetOrCreate(crop);
            totals.SpendEvents++;
            totals.SeedsIn += qty;
            totals.Net = totals.SeedsOut - totals.SeedsIn;
            AppendRecent("spend", kind, crop, sourceId, seedId, qty, posKey);
            _dirty = true;
        }

        if (Plugin.DebugTracking != null && Plugin.DebugTracking.Value)
        {
            LogHelper.Info($"[Ledger] spend pos={posKey} kind={kind} crop={crop} src={sourceId} seed={seedId} qty={qty}");
        }
        _saver?.RequestFlush();
    }

    public static void RecordHarvest(WorldGameObject wgo, string sourceId, string seedId, int qty)
    {
        if (qty <= 0) return;
        EnsureSaver();
        var crop = CropTypeFromId(sourceId);
        var kind = KindFromId(sourceId);
        var posKey = PositionKeyFor(wgo);

        lock (Sync)
        {
            var totals = GetOrCreate(crop);
            totals.HarvestEvents++;
            totals.SeedsOut += qty;
            totals.Net = totals.SeedsOut - totals.SeedsIn;
            AppendRecent("harvest", kind, crop, sourceId, seedId, qty, posKey);
            _dirty = true;
        }

        if (Plugin.DebugTracking != null && Plugin.DebugTracking.Value)
        {
            LogHelper.Info($"[Ledger] harvest pos={posKey} kind={kind} crop={crop} src={sourceId} seed={seedId} qty={qty}");
        }
        _saver?.RequestFlush();
    }

    private static CropTotals GetOrCreate(string crop)
    {
        if (!File.TotalsByCrop.TryGetValue(crop, out var t))
        {
            t = new CropTotals();
            File.TotalsByCrop[crop] = t;
        }
        return t;
    }

    private static void AppendRecent(string type, string kind, string crop, string source, string seedId, int qty, string posKey)
    {
        File.RecentEvents.Add(new EventEntry
        {
            AtUtc        = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            Type         = type,
            Kind         = kind,
            CropType     = crop,
            CraftOrObjId = source,
            SeedId       = seedId,
            Qty          = qty,
            PositionKey  = posKey,
        });
        // Cap recent log to RecentEventsCap entries (FIFO).
        var overflow = File.RecentEvents.Count - RecentEventsCap;
        if (overflow > 0)
        {
            File.RecentEvents.RemoveRange(0, overflow);
        }
    }

    private static void EnsureSaver()
    {
        if (_saver != null) return;
        var go = new GameObject("~SeedEqualizerLedgerSaver");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.HideAndDontSave;
        _saver = go.AddComponent<DebouncedSaver>();
    }

    internal static bool ConsumeDirty()
    {
        lock (Sync)
        {
            if (!_dirty) return false;
            Save();
            return true;
        }
    }

    private sealed class DebouncedSaver : MonoBehaviour
    {
        private const float MinIntervalSeconds = 1.0f;
        private float _flushAt = -1f;

        public void RequestFlush()
        {
            if (_flushAt < 0f) _flushAt = Time.unscaledTime + MinIntervalSeconds;
        }

        private void Update()
        {
            if (_flushAt < 0f) return;
            if (Time.unscaledTime < _flushAt) return;
            _flushAt = -1f;
            ConsumeDirty();
        }

        private void OnApplicationQuit() => ConsumeDirty();
    }
}
