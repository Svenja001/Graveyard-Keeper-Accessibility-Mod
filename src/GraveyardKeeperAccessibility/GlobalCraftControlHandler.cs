namespace GraveyardKeeperAccessibility;

// Remote crafting — the "Better Save Soul" DLC's soul-receiver mechanic. Two halves, neither of
// which had any keyboard route before this handler:
//
//   1. The REMOTE ("Ferngesteuerte Handwerkskontrolle", unlocked by the Euric/Smiler questline).
//      It sets MainGame.me.save.has_global_craft_control, whose only visible effect is that the
//      WORLD MAP grows a clickable icon per zone group (ZoneControlItem). Those icons are bare
//      MonoBehaviours with a collider — no UIButton, no label — so DiscoverElements found exactly
//      zero elements in MapGUI and the map read as an empty window. The quest hands you the item,
//      the dialogue says "my map has changed", and then there is nothing to press.
//
//   2. The RECEIVER ("Seelenempfänger", ObjType.SoulTotem). One must stand in a zone before that
//      zone's stations can be driven remotely; crafts started this way are paid for with
//      gratitude points (earned by returning healed souls) instead of energy. Without a receiver
//      the game just draws every station's name in red and swallows the click — silently.
//
// Pressing a map icon opens GlobalCraftControlGUI: one tab per zone in the group, one
// CraftControlItem row per craft station in the current tab. Those rows are MonoBehaviours too,
// so that window was equally unreadable. Enter on a station hands off to the regular CraftGUI,
// which is already accessible, with global control still active — so recipes there cost
// gratitude points and pull from the remote zone's chests.
internal static class GlobalCraftControlHandler
{
    private static FieldInfo _tabIdsField;
    private static FieldInfo _curTabField;
    private static FieldInfo _tabsField;

    /// <summary>True once the questline has handed the player the remote craft control.</summary>
    internal static bool RemoteControlUnlocked
    {
        get
        {
            try { return MainGame.me != null && MainGame.me.save != null && MainGame.me.save.has_global_craft_control; }
            catch { return false; }
        }
    }

    // ------------------------------------------------------------------ map side

    /// <summary>
    /// One row per zone group the remote can reach. Mirrors ZoneControlItem.IsEnabled: the remote
    /// must be unlocked and the group must hold at least one known, enabled zone that owns a
    /// builder desk (the game needs a builder to anchor the control window's header). Enter opens
    /// that group's remote-crafting window.
    /// </summary>
    internal static void DiscoverMapZones(MapGUI gui, List<GUIElement> elements)
    {
        if (!RemoteControlUnlocked) return;

        var seen = new HashSet<string>();
        foreach (var item in gui.GetComponentsInChildren<ZoneControlItem>(true))
        {
            if (item == null) continue;
            var group = item.zone_group;
            if (string.IsNullOrEmpty(group) || !seen.Add(group)) continue;

            var zones = ReachableZonesInGroup(group);
            if (zones.Count == 0) continue;

            var captured = item;
            elements.Add(new GUIElement
            {
                Go = gui.gameObject,
                Label = MapZoneRowLabel(zones),
                Type = ElementType.Button,
                OnActivate = () => { try { captured.OnPress(); } catch (Exception ex) { Plugin.Log.LogWarning($"[REMOTE CRAFT] map press failed: {ex.Message}"); } }
            });
        }

        RemoteRowCount = elements.Count;
        Plugin.Log.LogInfo($"[REMOTE CRAFT] Map: {RemoteRowCount} remote-crafting area(s)");
    }

    // ------------------------------------------------------------- readable map

    /// <summary>How many rows of the map list are pressable remote-crafting entries.</summary>
    internal static int RemoteRowCount { get; private set; }

    /// <summary>How many discovered areas the last map discovery listed.</summary>
    internal static int KnownAreaCount { get; private set; }

    /// <summary>How many map markers are still undiscovered.</summary>
    internal static int UnknownAreaCount { get; private set; }

    /// <summary>
    /// The map's actual contents: one read-only row per area the map draws a marker for, nearest
    /// first, each saying where it lies from where the player is standing. The game shows these as
    /// labels pinned to a picture, so without this the map is a window with nothing in it — a
    /// sighted player uses it to get their bearings and to see what they have and haven't found.
    /// Undiscovered markers carry no name in-game (they are drawn as a blank), so they are summed
    /// into a single trailing row rather than repeated as N nameless entries.
    /// </summary>
    internal static void DiscoverMapAreas(MapGUI gui, List<GUIElement> elements)
    {
        KnownAreaCount = 0;
        UnknownAreaCount = 0;

        Vector2 playerPos;
        try { playerPos = MainGame.me.player.pos; }
        catch { playerPos = Vector2.zero; }

        string playerZone = null;
        try { playerZone = MainGame.me.player.GetMyWorldZone()?.id; } catch { }

        // (row text, sort distance). Unplaceable zones sort last but keep their name.
        var rows = new List<KeyValuePair<string, float>>();

        foreach (var marker in gui.GetComponentsInChildren<MapZoneGUI>(true))
        {
            if (marker == null) continue;
            var zoneId = marker.name;
            if (string.IsNullOrEmpty(zoneId)) continue;

            bool known;
            try { known = MainGame.me.save.known_world_zones.Contains(zoneId); }
            catch { known = false; }

            if (!known) { UnknownAreaCount++; continue; }
            KnownAreaCount++;

            // MapGUI.Open has already localized the marker's label (honouring override_name), so
            // prefer it over rebuilding the token ourselves.
            var name = ScreenReader.StripNguiCodes(marker.label?.text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name)) name = ZoneName(zoneId);

            var parts = new List<string> { name };
            var distance = float.MaxValue;

            if (zoneId == playerZone)
            {
                parts.Add(Loc.Get("map.you_are_here"));
                distance = 0f;
            }
            else if (TryZoneCenter(zoneId, out var center))
            {
                distance = Vector2.Distance(playerPos, center);
                parts.Add(CompassDirection(playerPos, center));
                parts.Add(Loc.Fmt("map.meters_away", (distance / TileSize).ToString("F0")));
            }

            if (HasReceiver(WorldZone.GetZoneByID(zoneId, null_is_error: false)))
                parts.Add(Loc.Get("map.soul_receiver"));

            rows.Add(new KeyValuePair<string, float>(string.Join(", ", parts), distance));
        }

        rows.Sort((a, b) => a.Value.CompareTo(b.Value));

        foreach (var row in rows)
            elements.Add(InfoRow(gui, row.Key));

        if (UnknownAreaCount > 0)
            elements.Add(InfoRow(gui, Loc.Plural("map.undiscovered", UnknownAreaCount, UnknownAreaCount)));

        Plugin.Log.LogInfo($"[MAP] {KnownAreaCount} known area(s), {UnknownAreaCount} undiscovered");
    }

    /// <summary>Spoken header for the world map.</summary>
    internal static string MapIntro()
    {
        var parts = new List<string> { Loc.Get("map.title") };

        parts.Add(Loc.Plural("map.discovered", KnownAreaCount, KnownAreaCount));

        if (RemoteRowCount > 0)
            parts.Add(Loc.Plural("map.remote_craftable", RemoteRowCount, RemoteRowCount));
        else if (RemoteControlUnlocked)
            parts.Add(Loc.Get("map.no_remote_craftable"));

        return string.Join(". ", parts) + ".";
    }

    /// <summary>
    /// A row that exists only to be read. It carries its own no-op action so Enter re-reads it
    /// instead of falling through to the generic UIButton hunt, which would search the whole map
    /// window and could press something unrelated.
    /// </summary>
    private static GUIElement InfoRow(MapGUI gui, string label)
    {
        return new GUIElement
        {
            Go = gui.gameObject,
            Label = label,
            Type = ElementType.Button,
            OnActivate = () => ScreenReader.Say(label)
        };
    }

    /// <summary>World-space centre of a zone, when the zone is loaded and placed.</summary>
    private static bool TryZoneCenter(string zoneId, out Vector2 center)
    {
        center = Vector2.zero;
        try
        {
            var zone = WorldZone.GetZoneByID(zoneId, null_is_error: false);
            var tf = zone?.center_tf;
            if (tf == null) return false;
            center = tf.position;
            return true;
        }
        catch { return false; }
    }

    // One world tile is 96 units; the navigator speaks tiles as "meters", so match it.
    private const float TileSize = 96f;

    /// <summary>Eight-point compass bearing, +x east and +y north (same convention as the navigator).</summary>
    private static string CompassDirection(Vector2 from, Vector2 to)
    {
        var d = to - from;
        if (d.sqrMagnitude < 1f) return Loc.Get("compass.here");

        var angle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
        if (angle < 0f) angle += 360f;
        return (Mathf.RoundToInt(angle / 45f) % 8) switch
        {
            0 => Loc.Get("compass.east"),
            1 => Loc.Get("compass.north_east"),
            2 => Loc.Get("compass.north"),
            3 => Loc.Get("compass.north_west"),
            4 => Loc.Get("compass.west"),
            5 => Loc.Get("compass.south_west"),
            6 => Loc.Get("compass.south"),
            _ => Loc.Get("compass.south_east"),
        };
    }

    /// <summary>Zones of a group the remote can actually open (known, enabled, has a builder).</summary>
    private static List<WorldZone> ReachableZonesInGroup(string group)
    {
        var result = new List<WorldZone>();
        try
        {
            foreach (var def in GameBalance.me.world_zones_data)
            {
                if (def == null || def.zone_group != group) continue;
                var zone = WorldZone.GetZoneByID(def.id, null_is_error: false);
                if (zone == null || zone.IsDisabled()) continue;
                if (!MainGame.me.save.IsWorldZoneKnown(zone.id)) continue;
                if (!zone.HasBuilder()) continue;
                result.Add(zone);
            }
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[REMOTE CRAFT] group scan failed: {ex.Message}"); }
        return result;
    }

    // A zone group has no name of its own in the game data, so name it after the zones it holds:
    // the first one, plus a count of the rest. Also say up front whether a soul receiver is
    // standing in any of them — that is the whole reason a group's stations refuse to work.
    private static string MapZoneRowLabel(List<WorldZone> zones)
    {
        var first = ZoneName(zones[0].id);
        var name = zones.Count > 1 ? Loc.Fmt("remote.group_name", first, zones.Count - 1) : first;

        var withReceiver = zones.Count(HasReceiver);
        string receiver;
        if (withReceiver == 0)
            receiver = Loc.Get("remote.receiver.none");
        else if (withReceiver == zones.Count)
            receiver = Loc.Get("remote.receiver.built");
        else
            receiver = Loc.Fmt("remote.receiver.partial", withReceiver, zones.Count);

        return Loc.Fmt("remote.map_row", name, receiver);
    }

    // ------------------------------------------------------ remote-crafting window

    /// <summary>
    /// Tab rows (one per zone in the group, only when there is more than one to choose from),
    /// then one row per craft station in the current tab. Everything is a plain button: Enter on
    /// a tab switches zone, Enter on a station opens its craft window.
    /// </summary>
    internal static void Discover(GlobalCraftControlGUI gui, List<GUIElement> elements)
    {
        var tabIds = TabIds(gui);
        var current = CurrentTab(gui);

        TabRowCount = tabIds.Count > 1 ? tabIds.Count : 0;

        if (tabIds.Count > 1)
        {
            foreach (var id in tabIds)
            {
                var captured = id;
                var label = Loc.Fmt(id == current ? "remote.tab.current" : "remote.tab", ZoneName(id));
                elements.Add(new GUIElement
                {
                    Go = gui.gameObject,
                    Label = label,
                    Type = ElementType.Button,
                    OnActivate = () => SwitchTab(gui, captured)
                });
            }
        }

        var stations = 0;
        foreach (var item in gui.list_items)
        {
            if (item == null || item.linked_wgo == null) continue;
            var captured = item;

            // Go is the window, not the row: switching tabs destroys every CraftControlItem, and
            // a destroyed GameObject throws on activeInHierarchy. The row's text is rebuilt from
            // the linked object each read anyway, so it never needs the row object itself.
            elements.Add(new GUIElement
            {
                Go = gui.gameObject,
                Label = StationLabel(captured),
                Type = ElementType.Button,
                ReadDynamic = () => StationLabel(captured),
                OnActivate = () => OpenStation(captured)
            });
            stations++;
        }

        Plugin.Log.LogInfo($"[REMOTE CRAFT] Window: tab '{current}', {stations} station(s), receiver={HasReceiverForTab(gui, current)}");
    }

    /// <summary>
    /// Spoken header for the remote-crafting window: which zone, how many gratitude points are
    /// left to spend, and — the part that silently blocks everything — whether that zone actually
    /// has a soul receiver standing in it.
    /// </summary>
    internal static string IntroFor(GlobalCraftControlGUI gui)
    {
        var current = CurrentTab(gui);
        var parts = new List<string> { Loc.Get("remote.title") };

        if (!string.IsNullOrEmpty(current)) parts.Add(ZoneName(current));

        parts.Add(Loc.Fmt("remote.gratitude_points", GratitudePoints()));

        if (!HasReceiverForTab(gui, current))
            parts.Add(Loc.Get("remote.no_receiver_here"));

        if (TabIds(gui).Count > 1)
            parts.Add(Loc.Get("remote.switch_hint"));

        return string.Join(". ", parts) + ".";
    }

    /// <summary>How many leading rows of the last discovery were zone tabs rather than stations.</summary>
    private static int TabRowCount;

    /// <summary>Index of the first station row (i.e. past the zone tab rows), or 0.</summary>
    internal static int FirstStationIndex(List<GUIElement> rows)
    {
        if (rows.Count == 0) return -1;
        return TabRowCount < rows.Count ? TabRowCount : 0;
    }

    /// <summary>
    /// Switch the window to another zone. The game routes a tab click through the tab widget's
    /// own callback, so drive that rather than the private SwitchTab — it keeps the selected-tab
    /// highlight and sounds in step. The caller re-discovers afterwards.
    /// </summary>
    private static void SwitchTab(GlobalCraftControlGUI gui, string tabId)
    {
        try
        {
            _tabsField ??= AccessTools.Field(typeof(GlobalCraftControlGUI), "_tabs");
            var tabs = _tabsField?.GetValue(gui) as List<CraftTabGUI>;
            var tab = tabs?.FirstOrDefault(t => t != null && t.tab_id == tabId);
            if (tab != null) tab.OnTabClicked();
            else Plugin.Log.LogWarning($"[REMOTE CRAFT] no tab widget for '{tabId}'");
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[REMOTE CRAFT] tab switch failed: {ex.Message}"); }

        GUIAccessibility.RefreshAfterRemoteTabSwitch(ZoneName(tabId));
    }

    /// <summary>
    /// Enter on a station. The game ignores the click outright when the station isn't usable
    /// (no receiver in the zone, or it's mid-craft and can't be interrupted) — say why instead of
    /// letting the press vanish. Otherwise hand off to CraftControlItem's own action, which hides
    /// this window and opens the station's craft GUI with global control still active.
    /// </summary>
    private static void OpenStation(CraftControlItem item)
    {
        if (item == null || item.linked_wgo == null)
        {
            ScreenReader.Say(Loc.Get("remote.station_gone"));
            return;
        }

        var blocker = StationBlocker(item.linked_wgo);
        if (blocker != null)
        {
            ScreenReader.Say(blocker);
            return;
        }

        try { item.OnItemAction(); }
        catch (Exception ex) { Plugin.Log.LogWarning($"[REMOTE CRAFT] open station failed: {ex.Message}"); }
    }

    // ------------------------------------------------------------------- labels

    /// <summary>
    /// One station row: its name, then what it is doing right now (crafting what, how far along),
    /// its zombie worker if it has one, and why it can't be opened when it can't.
    /// </summary>
    private static string StationLabel(CraftControlItem item)
    {
        if (item == null || item.linked_wgo == null) return Loc.Get("remote.empty_slot");

        var wgo = item.linked_wgo;
        var parts = new List<string> { StationName(wgo) };

        try
        {
            var craft = wgo.components?.craft;
            if (craft != null)
            {
                if (craft.is_crafting)
                {
                    var making = CurrentOutputName(craft);
                    var pct = Mathf.Clamp(Mathf.RoundToInt(wgo.progress * 100f), 0, 100);
                    parts.Add(string.IsNullOrEmpty(making) ? Loc.Fmt("remote.working", pct) : Loc.Fmt("remote.making", making, pct));
                }
                else if (craft.craft_queue != null && craft.craft_queue.Count > 0)
                {
                    parts.Add(Loc.Plural("remote.queued", craft.craft_queue.Count, craft.craft_queue.Count));
                }
                else
                {
                    parts.Add(Loc.Get("remote.idle"));
                }
            }

            if (wgo.has_linked_worker && wgo.linked_worker != null && !wgo.linked_worker.IsInvisibleWorker())
            {
                var eff = wgo.linked_worker.worker?.GetWorkerEfficiencyTextOnlyPercent();
                parts.Add(string.IsNullOrEmpty(eff) ? Loc.Get("remote.zombie_worker") : Loc.Fmt("remote.zombie_worker_at", eff));
            }
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[REMOTE CRAFT] station label failed: {ex.Message}"); }

        var blocker = StationBlocker(wgo);
        if (blocker != null) parts.Add(blocker);

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Why a station can't be opened remotely, or null when it can. Mirrors the interactable test
    /// in CraftControlItem.Redraw — the game only shows this as red text.
    /// </summary>
    private static string StationBlocker(WorldGameObject wgo)
    {
        try
        {
            if (!wgo.HasSoulsTotemInZone())
                return Loc.Get("remote.blocked.no_receiver");

            var def = wgo.obj_def;
            if (def != null && def.interaction_type != ObjectDefinition.InteractionType.Craft
                && def.GetValidInteraction(wgo) == null)
                return Loc.Get("remote.blocked.unavailable");

            var canWhileBusy = def != null && (def.can_insert_zombie || def.tool_actions.no_actions);
            if (!canWhileBusy && wgo.components?.craft != null && wgo.components.craft.is_crafting)
                return Loc.Get("remote.blocked.busy");
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[REMOTE CRAFT] blocker check failed: {ex.Message}"); }

        return null;
    }

    private static string StationName(WorldGameObject wgo)
    {
        try
        {
            var name = InteractionDetector.LocalizedObjectName(wgo.obj_id);
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        catch { }
        return Loc.Get("remote.station");
    }

    private static string CurrentOutputName(CraftComponent craft)
    {
        try
        {
            var item = craft.current_item;
            if (item == null || item.IsEmpty())
            {
                var output = craft.current_craft?.output;
                if (output != null && output.Count > 0) item = output[0];
            }
            if (item == null || item.IsEmpty()) return null;

            var def = GameBalance.me.GetData<ItemDefinition>(item.id);
            var name = ScreenReader.StripNguiCodes(def?.GetItemName() ?? item.id)?.Trim();
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ helpers

    private static int GratitudePoints()
    {
        try { return Mathf.RoundToInt(MainGame.me.player.gratitude_points); }
        catch { return 0; }
    }

    private static bool HasReceiver(WorldZone zone)
    {
        try { return zone != null && zone.HasSoulsTotemInZone(); }
        catch { return false; }
    }

    private static bool HasReceiverForTab(GlobalCraftControlGUI gui, string tabId)
    {
        try
        {
            if (!string.IsNullOrEmpty(tabId))
                return HasReceiver(WorldZone.GetZoneByID(tabId, null_is_error: false));

            // No tab yet (single-zone group before the first switch): fall back to any station.
            var first = gui.list_items.FirstOrDefault(i => i != null && i.linked_wgo != null);
            return first != null && first.linked_wgo.HasSoulsTotemInZone();
        }
        catch { return false; }
    }

    private static List<string> TabIds(GlobalCraftControlGUI gui)
    {
        try
        {
            _tabIdsField ??= AccessTools.Field(typeof(GlobalCraftControlGUI), "_tabs_ids");
            return _tabIdsField?.GetValue(gui) as List<string> ?? new List<string>();
        }
        catch { return new List<string>(); }
    }

    private static string CurrentTab(GlobalCraftControlGUI gui)
    {
        try
        {
            _curTabField ??= AccessTools.Field(typeof(GlobalCraftControlGUI), "_cur_tab_id");
            return _curTabField?.GetValue(gui) as string ?? "";
        }
        catch { return ""; }
    }

    /// <summary>Localized zone name ("zone_&lt;id&gt;", the same token the map labels use).</summary>
    private static string ZoneName(string zoneId)
    {
        if (string.IsNullOrEmpty(zoneId)) return Loc.Get("common.unknown_area");
        try
        {
            var loc = ScreenReader.StripNguiCodes(GJL.L("zone_" + zoneId) ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(loc) && !loc.Contains("!")) return loc;
        }
        catch { }
        return zoneId.Replace('_', ' ');
    }
}
