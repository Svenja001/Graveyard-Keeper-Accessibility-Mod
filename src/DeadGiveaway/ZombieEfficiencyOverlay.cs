namespace DeadGiveaway;

// Floats each zombie's work efficiency over its head using the game's own overhead
// text font. Keeps one pooled NGUI label per visible zombie and repositions it every
// frame from the worker's head anchor.
internal class ZombieEfficiencyOverlay : MonoBehaviour
{
    private const float RefreshInterval = 1f;
    private const float ScreenMargin = 64f;

    private readonly List<WorldGameObject> _workers = new();
    private readonly List<UILabel> _pool = new();
    private readonly System.Text.StringBuilder _sb = new();

    private Transform _container;
    private UILabel _template;
    private float _nextRefresh;
    private bool _ready;

    private void LateUpdate()
    {
        if (!Plugin.OverlayVisible || !MainGame.game_started || !BaseGUI.all_guis_closed || MainGame.paused)
        {
            HideAll();
            return;
        }

        // The overhead panel is torn down on scene changes; rebuild if our container went with it.
        if (_ready && !_container)
        {
            _ready = false;
            _pool.Clear();
            _workers.Clear();
        }

        if (!_ready && !TryInit()) return;

        if (Time.time >= _nextRefresh)
        {
            _nextRefresh = Time.time + RefreshInterval;
            RefreshWorkers();
        }

        Draw();
    }

    private bool TryInit()
    {
        if (!GUIElements.me) return false;

        var bubbles = GUIElements.me.effect_bubbles;
        var prefab = bubbles ? bubbles.effect_bubble_prefab : null;
        if (!prefab || !prefab.label) return false;

        // Parent under the prefab's parent (the live overhead panel), not the prefab itself -
        // the prefab is deactivated and our labels would inherit that and never draw.
        var parent = prefab.transform.parent;
        if (!parent && GUIElements.me.overhead_panel)
        {
            parent = GUIElements.me.overhead_panel.transform;
        }
        if (!parent) return false;

        _template = prefab.label;

        var go = new GameObject("DeadGiveawayLabels") { layer = _template.gameObject.layer };
        go.transform.SetParent(parent, false);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;
        _container = go.transform;
        _ready = true;

        if (Plugin.DebugEnabled)
        {
            Plugin.Log.LogInfo($"[Overlay] ready; labels parented under '{parent.name}'");
        }
        return true;
    }

    private void RefreshWorkers()
    {
        _workers.Clear();

        // Index loop, not foreach: the game mutates this list mid-frame and foreach throws.
        var all = WorldMap._objs;
        for (var i = 0; i < all.Count; i++)
        {
            var w = all[i];
            if (!w || !w.IsWorker()) continue;
            _workers.Add(w);

            try
            {
                w.worker?.UpdateWorkerLevel();
            }
            catch (Exception e)
            {
                if (Plugin.DebugEnabled)
                {
                    Plugin.Log.LogWarning($"[Overlay] UpdateWorkerLevel failed for '{w.obj_id}': {e.Message}");
                }
            }
        }

        if (Plugin.DebugEnabled)
        {
            Plugin.Log.LogInfo($"[Overlay] tracking {_workers.Count} zombie(s)");
        }
    }

    private void Draw()
    {
        var worldCam = MainGame.me.world_cam;
        var guiCam = MainGame.me.gui_cam;
        if (!worldCam || !guiCam) return;

        var showJob = Plugin.ShowJob.Value;
        var showPct = Plugin.ShowPercentage.Value;
        var showSkulls = Plugin.ShowSkulls.Value;
        if (!showJob && !showPct && !showSkulls)
        {
            HideAll();
            return;
        }

        var coloured = Plugin.ColourByEfficiency.Value;
        var separator = SpacedSeparator(Plugin.Separator.Value);
        var scale = Plugin.TextSize.Value;
        var lift = new Vector3(Plugin.HorizontalOffset.Value, Plugin.VerticalOffset.Value, 0f);

        var used = 0;
        for (var i = 0; i < _workers.Count; i++)
        {
            var w = _workers[i];
            if (!w) continue;

            // bubble_pos sits at head height; lift it so the text clears the head.
            var pos = w.bubble_pos + lift;
            var sp = worldCam.WorldToScreenPoint(pos);
            if (sp.z <= 0f || sp.x < -ScreenMargin || sp.x > Screen.width + ScreenMargin || sp.y < -ScreenMargin || sp.y > Screen.height + ScreenMargin)
            {
                continue;
            }

            var k = w.data.GetParam("working_k");
            var label = GetLabel(used++);
            label.text = Compose(w, k, showJob, showPct, showSkulls, coloured, separator);
            label.color = Color.white;
            label.MakePixelPerfect();
            label.transform.localScale = Vector3.one * scale;
            label.transform.SetGUIPosToWorldPos(pos, worldCam, guiCam);
            if (!label.gameObject.activeSelf) label.gameObject.SetActive(true);
        }

        for (var i = used; i < _pool.Count; i++)
        {
            if (_pool[i] && _pool[i].gameObject.activeSelf) _pool[i].gameObject.SetActive(false);
        }
    }

    private UILabel GetLabel(int index)
    {
        if (index < _pool.Count) return _pool[index];

        var go = new GameObject("ZombieEfficiency") { layer = _template.gameObject.layer };
        go.transform.SetParent(_container, false);
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;

        var label = go.AddComponent<UILabel>();
        if (_template.bitmapFont) label.bitmapFont = _template.bitmapFont;
        else if (_template.trueTypeFont) label.trueTypeFont = _template.trueTypeFont;
        label.fontSize = _template.fontSize;
        label.fontStyle = _template.fontStyle;
        label.pivot = UIWidget.Pivot.Center;
        label.alignment = NGUIText.Alignment.Center;
        label.overflowMethod = UILabel.Overflow.ResizeFreely;
        label.multiLine = true;
        label.supportEncoding = true;
        label.depth = _template.depth + 1;
        label.color = Color.white;

        _pool.Add(label);
        return label;
    }

    private void HideAll()
    {
        for (var i = 0; i < _pool.Count; i++)
        {
            if (_pool[i] && _pool[i].gameObject.activeSelf) _pool[i].gameObject.SetActive(false);
        }
    }

    // Builds the overhead text in up to two lines. Top line is the enabled numbers joined by the
    // separator; bottom line is always the job, when shown. The numbers carry the efficiency colour
    // via one inline NGUI tag; the job line stays plain white so a station name never reads as a
    // low-efficiency warning. "(skull)" is the game's inline white-skull sprite. White skulls =
    // round(k * 40), since working_k = white_skulls / 40.
    private string Compose(WorldGameObject w, float k, bool showJob, bool showPct, bool showSkulls, bool coloured, string separator)
    {
        _sb.Length = 0;

        var any = false;
        if (showPct)
        {
            AppendNumber(ref any, separator, Mathf.RoundToInt(k * 100f) + "%");
        }
        if (showSkulls)
        {
            AppendNumber(ref any, separator, "(skull)" + Mathf.RoundToInt(k * 40f));
        }

        // Tint the whole number line at once; every value shares the same efficiency.
        if (any && coloured)
        {
            _sb.Insert(0, "[" + NGUIText.EncodeColor(Grade(k)) + "]");
            _sb.Append("[-]");
        }

        if (showJob)
        {
            if (any) _sb.Append('\n');
            var bench = w.linked_workbench;
            _sb.Append(bench ? GJL.L(bench.obj_id) : Lang.Get("label.idle"));

            // Porters: tack on what's in the backpack (or its crate) in brackets.
            var carried = CarriedSummary(w);
            if (carried != null)
            {
                _sb.Append(" (").Append(carried).Append(')');
            }
        }

        return _sb.ToString();
    }

    // Names whatever a porter is hauling: the goods loose in its backpack, or the contents of a
    // crate it's carrying. Returns null for a non-porter or an empty-handed one. Reads the worker's
    // inventory directly so it never creates a backpack the way Worker.GetBackpack would.
    private static string CarriedSummary(WorldGameObject w)
    {
        var inv = w.data?.inventory;
        if (inv == null) return null;

        Item backpack = null;
        for (var i = 0; i < inv.Count; i++)
        {
            if (inv[i] != null && inv[i].id == Worker.BACKPACK_ID)
            {
                backpack = inv[i];
                break;
            }
        }
        if (backpack?.inventory == null || backpack.inventory.Count == 0) return null;

        // The backpack holds either loose goods or a single crate item. A crate carries no inventory
        // of its own; its id names the set it hauls (e.g. vegetables), so naming each backpack item
        // covers both cases. Use the bare name - GetItemName() bakes its own "(xN)" in, which would
        // clash with the "Nx" count prefix.
        var carried = backpack.inventory;
        var result = string.Empty;
        for (var i = 0; i < carried.Count; i++)
        {
            var it = carried[i];
            if (it?.definition == null) continue;
            if (result.Length > 0) result += ", ";
            if (it.value > 1) result += it.value + "x ";
            result += it.definition.GetItemName();
        }
        return result.Length == 0 ? null : result;
    }

    private void AppendNumber(ref bool any, string separator, string text)
    {
        if (any) _sb.Append(separator);
        _sb.Append(text);
        any = true;
    }

    // The player types just the symbol; the mod owns the spacing. Always a trailing space, plus a
    // leading one unless it's punctuation that hugs the value before it (comma, full stop, and so on).
    private static string SpacedSeparator(string raw)
    {
        raw = raw.Trim();
        if (raw.Length == 0) return " ";
        var leading = raw.Length == 1 && ",.;:!?".IndexOf(raw[0]) >= 0 ? "" : " ";
        return leading + raw + " ";
    }

    // Red at 0%, yellow at 50%, green at 100% and above.
    private static Color Grade(float k)
    {
        var t = Mathf.Clamp01(k);
        return t < 0.5f
            ? Color.Lerp(Color.red, Color.yellow, t * 2f)
            : Color.Lerp(Color.yellow, Color.green, (t - 0.5f) * 2f);
    }
}
