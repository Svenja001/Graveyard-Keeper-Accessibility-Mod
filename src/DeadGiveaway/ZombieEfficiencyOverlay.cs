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
        var showSpeed = Plugin.ShowSpeed.Value;
        if (!showJob && !showPct && !showSkulls && !showSpeed)
        {
            HideAll();
            return;
        }

        var coloured = Plugin.ColourByEfficiency.Value;
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
            label.text = Compose(w, k, showJob, showPct, showSkulls, showSpeed, coloured);
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

    // Builds the stacked overhead text: job name on top, then each enabled number on its own line.
    // The numbers carry the efficiency colour via inline NGUI tags; the job line stays plain white
    // so a station name never reads as a low-efficiency warning. "(skull)" is the game's inline
    // white-skull sprite. White skulls = round(k * 40), since working_k = white_skulls / 40.
    private string Compose(WorldGameObject w, float k, bool showJob, bool showPct, bool showSkulls, bool showSpeed, bool coloured)
    {
        _sb.Length = 0;
        var tag = coloured ? "[" + NGUIText.EncodeColor(Grade(k)) + "]" : null;

        if (showJob)
        {
            var bench = w.linked_workbench;
            AddLine(bench ? GJL.L(bench.obj_id) : Lang.Get("label.idle"), null);
        }
        if (showPct)
        {
            AddLine(Mathf.RoundToInt(k * 100f) + "%", tag);
        }
        if (showSkulls)
        {
            AddLine("(skull)" + Mathf.RoundToInt(k * 40f), tag);
        }
        if (showSpeed)
        {
            AddLine(k.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "x", tag);
        }

        return _sb.ToString();
    }

    private void AddLine(string text, string colourTag)
    {
        if (_sb.Length > 0) _sb.Append('\n');
        if (colourTag != null)
        {
            _sb.Append(colourTag).Append(text).Append("[-]");
        }
        else
        {
            _sb.Append(text);
        }
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
