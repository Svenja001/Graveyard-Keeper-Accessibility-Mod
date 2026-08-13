namespace GraveyardKeeperAccessibility;

/// <summary>
/// Ctrl+G: a full audit of the scored zone the player is standing in — what is already built and
/// how many points it contributes, what the zone's build desk can still put down, and how much
/// room is left for each of those.
///
/// This exists because a rating quest ("get the sacrifice area to 20") can otherwise dead-end
/// invisibly: most decorations are restricted to a hand-placed <see cref="WorldSubZone"/> mount and
/// the world usually contains exactly ONE mount per decoration, so once it's used the catalog entry
/// stays visible but can never be placed again. Removability is the other half: the game defines
/// ~276 <c>BuildType.Remove</c> crafts, so most player-built furniture CAN be demolished — read it
/// through <see cref="BuildPlacementHandler.HasRemovalCraft"/>, never through the game's poisonable
/// <c>has_removal_craft</c>. The audit spells both out: the remaining headroom in points, and
/// whether the shortfall is space, materials or a hard vanilla limit.
/// </summary>
internal static class BuildZoneAudit
{
    private static ManualLogSource _log;

    internal static void Init(ManualLogSource log)
    {
        _log = log;
    }

    /// <summary>One buildable catalog entry, resolved against the world's mounts and the zone.</summary>
    private sealed class Option
    {
        internal ObjectCraftDefinition Craft;
        internal string ObjId;
        internal string Name;
        internal float Points;
        internal string SubZoneId;
        internal int Mounts;        // mount strips this decoration may sit on (-1 = free placement)
        internal int UsedMounts;    // mount strips that already carry one
        internal int BuiltInZone;   // instances of ObjId already standing in this zone
        internal bool Locked;
        internal string Missing;    // materials still missing, or null when affordable

        internal bool FreePlacement => Mounts < 0;
        internal int FreeSlots => FreePlacement ? int.MaxValue : Math.Max(0, Mounts - UsedMounts);
    }

    internal static void Announce()
    {
        try
        {
            if (!MainGame.game_started || MainGame.me == null)
            {
                ScreenReader.Say("No game in progress");
                return;
            }

            var zone = CurrentZone();
            if (zone == null)
            {
                ScreenReader.Say("You are not standing in a zone that gets a rating. Step into the area you want to check.");
                return;
            }

            float total = zone.GetTotalQuality();
            var zoneName = ZoneName(zone.id);
            var wgos = zone.GetZoneWGOs() ?? new List<WorldGameObject>();

            _log?.LogInfo($"[AUDIT] ===== zone '{zone.id}' total={total} wgos={wgos.Count} =====");

            LogZoneShape(zone);
            var built = SummarizeBuilt(wgos, out int removableCount, out float counted);
            var options = CollectOptions(zone, wgos);

            var lines = new List<string>
            {
                $"{zoneName} rating {Fmt(total)}."
            };

            // 1. What is standing there now.
            if (built.Count == 0)
            {
                lines.Add("Nothing in this area scores points.");
            }
            else
            {
                var sb = new List<string>();
                foreach (var b in built)
                    sb.Add(b.Count > 1
                        ? $"{b.Count} times {b.Name}, {Fmt(b.Each)} points each"
                        : $"{b.Name}, {Fmt(b.Each)} points");
                lines.Add($"{built.Count} kind{(built.Count == 1 ? "" : "s")} of scoring object, {Fmt(counted)} points in total: {string.Join("; ", sb)}.");
            }

            // 2. Demolition.
            lines.Add(removableCount == 0
                ? "Nothing here can be demolished: none of these objects has a removal craft, so remove mode will come up empty."
                : $"{removableCount} object{(removableCount == 1 ? " can" : "s can")} be demolished in remove mode.");

            // 3. What is still placeable, and the headroom that gives.
            float headroom = 0f;
            var buildable = new List<string>();
            var blocked = new List<string>();
            foreach (var o in options)
            {
                string state = OptionState(o, ref headroom);
                (o.Locked || o.FreeSlots == 0 ? blocked : buildable).Add(state);
            }

            if (options.Count == 0)
            {
                lines.Add("This area has no build desk, so nothing can be added here.");
            }
            else
            {
                lines.Add(buildable.Count > 0
                    ? $"Still buildable: {string.Join("; ", buildable)}."
                    : "Nothing on the build desk can be placed here any more.");

                if (blocked.Count > 0)
                    lines.Add($"Not placeable: {string.Join("; ", blocked)}.");
            }

            // 4. The verdict — the number the player actually cares about.
            if (options.Count > 0)
            {
                bool unlimited = options.Any(o => o.FreePlacement && !o.Locked);
                if (unlimited)
                    lines.Add($"At least {Fmt(headroom)} more points are reachable, and the free standing objects above can be repeated as often as you have materials, so the rating is not capped.");
                else if (headroom > 0f)
                    lines.Add($"Every remaining spot together is worth {Fmt(headroom)} more points, for a maximum of {Fmt(total + headroom)}.");
                else
                    lines.Add("There is no free spot left for anything this desk builds; the rating cannot go higher here.");
            }

            var text = string.Join(" ", lines);
            _log?.LogInfo($"[AUDIT] {text}");
            ScreenReader.Say(text);
        }
        catch (Exception ex)
        {
            _log?.LogError($"BuildZoneAudit error: {ex.Message}\n{ex.StackTrace}");
            ScreenReader.Say("Could not audit this area");
        }
    }

    // ---- zone contents ----------------------------------------------------

    /// <summary>
    /// Log the zone's actual buildable footprint: one line per collider with its bounds and whether
    /// it is enabled. A cell only counts as "inside the zone" when it overlaps an ENABLED zone
    /// collider (<see cref="FlowGridCell.IsInsideWorldZone"/> goes through Physics2D), so a disabled
    /// or simply short collider is what turns "there is obviously room over there" into a refusal.
    /// </summary>
    private static void LogZoneShape(WorldZone zone)
    {
        try
        {
            var cols = zone.GetComponentsInChildren<Collider2D>(true);
            _log?.LogInfo($"[AUDIT] zone '{zone.id}' colliders={cols?.Length ?? 0} bounds={zone.GetBounds()}");
            if (cols == null) return;
            for (int i = 0; i < cols.Length; i++)
            {
                var c = cols[i];
                if (c == null) continue;
                // WHICH collider counts is the whole question. IsInsideWorldZone asks the collider's
                // OWN GameObject for a WorldZone whose id matches — a child collider, or one owned
                // by a nested zone, is invisible to it however large it looks. So say what each
                // collider actually is: without this a huge "zone collider" covering the whole room
                // reads as buildable floor when nothing may be built on it.
                string owner;
                try
                {
                    var wz = c.GetComponent<WorldZone>();
                    var wsz = c.GetComponent<WorldSubZone>();
                    owner = wz != null
                        ? (wz.id == zone.id ? "BUILDABLE (this zone)" : $"other zone '{wz.id}'")
                        : wsz != null ? $"sub-zone '{wsz.sub_zone_id}'"
                        : "no zone component - not buildable";
                }
                catch { owner = "?"; }

                _log?.LogInfo($"[AUDIT]   zonecol#{i} enabled={c.enabled} active={c.gameObject.activeInHierarchy} " +
                              $"layer={c.gameObject.layer} center={c.bounds.center} size={c.bounds.size} " +
                              $"go='{c.gameObject.name}' {owner}");
            }
        }
        catch (Exception ex)
        {
            _log?.LogError($"[AUDIT] zone shape log failed: {ex.Message}");
        }
    }

    private struct BuiltEntry
    {
        internal string Name;
        internal int Count;
        internal float Each;
    }

    /// <summary>
    /// Group the zone's objects by id, keeping only those that actually count towards the rating
    /// (the same test <see cref="WorldZone.GetTotalQuality"/> makes), and count how many of ALL the
    /// zone's objects have a removal craft.
    /// </summary>
    private static List<BuiltEntry> SummarizeBuilt(List<WorldGameObject> wgos, out int removableCount, out float counted)
    {
        removableCount = 0;
        counted = 0f;
        var byId = new Dictionary<string, BuiltEntry>();

        foreach (var w in wgos)
        {
            if (w == null || w.obj_def == null) continue;

            // Never w.has_removal_craft: reading it outside build mode poisons the game's own
            // one-shot cache — see BuildPlacementHandler.HasRemovalCraft. The audit runs from a
            // hotkey anywhere in the world, so it is exactly such a caller.
            bool removable = BuildPlacementHandler.HasRemovalCraft(w);
            if (removable) removableCount++;

            float q = 0f;
            try { q = w.quality; } catch { }
            bool counts = !w.obj_def.ignore_counting_at_zone;

            _log?.LogInfo($"[AUDIT]   wgo '{w.obj_id}' quality={q} counts={counts} removable={removable} pos={w.pos}");

            if (!counts || Mathf.Abs(q) < 0.05f) continue;
            counted += q;

            if (byId.TryGetValue(w.obj_id, out var e))
            {
                e.Count++;
                byId[w.obj_id] = e;
            }
            else
            {
                byId[w.obj_id] = new BuiltEntry { Name = ObjName(w.obj_id), Count = 1, Each = q };
            }
        }

        return byId.Values.OrderByDescending(e => e.Each * e.Count).ToList();
    }

    // ---- catalog ----------------------------------------------------------

    /// <summary>
    /// Every "put" craft offered by the build desks standing in this zone, resolved against the
    /// world: how many mounts exist for it and how many are already used.
    /// </summary>
    private static List<Option> CollectOptions(WorldZone zone, List<WorldGameObject> wgos)
    {
        var options = new List<Option>();
        var deskIds = new HashSet<string>();

        foreach (var w in wgos)
        {
            if (w?.obj_def == null) continue;
            if (w.obj_def.interaction_type == ObjectDefinition.InteractionType.Builder)
                deskIds.Add(w.obj_id);
        }
        if (deskIds.Count == 0) return options;
        _log?.LogInfo($"[AUDIT] build desks: {string.Join(", ", deskIds)}");

        // How many of each object id already stand in the zone — a used mount shows up here.
        var counts = new Dictionary<string, int>();
        foreach (var w in wgos)
        {
            if (w == null || string.IsNullOrEmpty(w.obj_id)) continue;
            counts.TryGetValue(w.obj_id, out int c);
            counts[w.obj_id] = c + 1;
        }

        // The zone's own material stock — the same pool the build desk spends from.
        MultiInventory stock = null;
        try
        {
            var invs = zone.GetMultiInventory(null, MultiInventory.PlayerMultiInventory.IncludePlayer, true);
            if (invs != null && invs.Count > 0) stock = new MultiInventory(invs);
        }
        catch { }

        var mounts = AllMounts();

        foreach (var cd in GameBalance.me.craft_obj_data)
        {
            if (cd == null || cd.build_type != ObjectCraftDefinition.BuildType.Put) continue;
            if (cd.builder_ids == null || !cd.builder_ids.Any(deskIds.Contains)) continue;

            var o = new Option
            {
                Craft = cd,
                ObjId = cd.out_obj,
                Name = ObjName(cd.out_obj),
                SubZoneId = string.IsNullOrEmpty(cd.sub_zone_id) ? null : cd.sub_zone_id,
                Points = QualityOf(cd.out_obj),
            };
            try { o.Locked = cd.IsLocked() || cd.hidden; } catch { }
            counts.TryGetValue(o.ObjId ?? "", out int builtHere);
            o.BuiltInZone = builtHere;
            o.Missing = MissingMaterials(cd, stock);

            if (o.SubZoneId == null)
            {
                o.Mounts = -1;
            }
            else
            {
                mounts.TryGetValue(o.SubZoneId, out var spots);
                o.Mounts = spots?.Count ?? 0;
                o.UsedMounts = CountUsedMounts(spots, wgos, o.ObjId);
            }

            _log?.LogInfo($"[AUDIT]   craft '{cd.id}' out={o.ObjId} points={o.Points} subZone={o.SubZoneId ?? "-"} " +
                          $"mounts={o.Mounts} used={o.UsedMounts} builtInZone={o.BuiltInZone} " +
                          $"locked={o.Locked} missing={o.Missing ?? "-"}");

            options.Add(o);
        }

        return options.OrderByDescending(o => o.Points).ToList();
    }

    /// <summary>Spoken state of one catalog entry; adds its reachable points to <paramref name="headroom"/>.</summary>
    private static string OptionState(Option o, ref float headroom)
    {
        var pts = $"{Fmt(o.Points)} point{(Mathf.Abs(o.Points - 1f) < 0.05f ? "" : "s")}";

        if (o.Locked)
            return $"{o.Name}, {pts}, still locked";

        if (o.FreePlacement)
        {
            headroom += o.Points;
            var note = o.BuiltInZone > 0 ? $", {o.BuiltInZone} already here" : "";
            return o.Missing == null
                ? $"{o.Name}, {pts}, free placement, materials ready{note}"
                : $"{o.Name}, {pts}, free placement, missing {o.Missing}{note}";
        }

        if (o.Mounts == 0)
            return $"{o.Name}, {pts}, no mounting spot exists in this area at all";

        if (o.FreeSlots == 0)
            return $"{o.Name}, {pts}, all {o.Mounts} spot{(o.Mounts == 1 ? "" : "s")} already used";

        headroom += o.Points * o.FreeSlots;
        var slots = $"{o.FreeSlots} of {o.Mounts} spot{(o.Mounts == 1 ? "" : "s")} free";
        return o.Missing == null
            ? $"{o.Name}, {pts}, {slots}, materials ready"
            : $"{o.Name}, {pts}, {slots}, missing {o.Missing}";
    }

    /// <summary>
    /// sub_zone_id to the individual mounting spots it offers. A room's two symmetric wall spots are
    /// usually two COLLIDERS on a single <see cref="WorldSubZone"/> object, not two objects — count
    /// the objects and you conclude "one spot, all used" while the game happily accepts a second
    /// decoration on the far wall. So each collider is one spot.
    /// </summary>
    private static Dictionary<string, List<Bounds>> AllMounts()
    {
        var map = new Dictionary<string, List<Bounds>>();
        try
        {
            foreach (var z in UnityEngine.Object.FindObjectsOfType<WorldSubZone>(true))
            {
                if (z == null || string.IsNullOrEmpty(z.sub_zone_id)) continue;
                if (!map.TryGetValue(z.sub_zone_id, out var list))
                    map[z.sub_zone_id] = list = new List<Bounds>();

                bool any = false;
                foreach (var col in z.GetComponentsInChildren<Collider2D>(true))
                {
                    if (col == null) continue;
                    var b = col.bounds;
                    if (b.size.sqrMagnitude < 1f) continue;
                    list.Add(b);
                    any = true;
                }
                if (!any)
                    list.Add(new Bounds(z.transform.position, new Vector3(96f, 96f, 0f)));
            }
        }
        catch (Exception ex)
        {
            _log?.LogError($"[AUDIT] mount scan failed: {ex.Message}");
        }
        return map;
    }

    /// <summary>
    /// How many of a decoration's mounting spots already carry one. A placed decoration's anchor
    /// does NOT sit inside its strip — it hangs a tile or so above it — so a containment test with
    /// any fixed margin gets this wrong in both directions. Instead assign each placed object to its
    /// nearest mount; a mount with an object claiming it is used.
    /// </summary>
    private static int CountUsedMounts(List<Bounds> spots, List<WorldGameObject> wgos, string objId)
    {
        if (spots == null || spots.Count == 0 || string.IsNullOrEmpty(objId)) return 0;

        var taken = new bool[spots.Count];
        foreach (var w in wgos)
        {
            if (w == null || w.obj_id != objId) continue;

            int nearest = -1;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < spots.Count; i++)
            {
                float d = ((Vector2)spots[i].center - w.pos).sqrMagnitude;
                if (d < bestSqr) { bestSqr = d; nearest = i; }
            }
            if (nearest >= 0) taken[nearest] = true;
        }

        int used = 0;
        for (int i = 0; i < spots.Count; i++)
        {
            if (taken[i]) used++;
            _log?.LogInfo($"[AUDIT]     mount '{objId}' #{i} center={spots[i].center} size={spots[i].size} used={taken[i]}");
        }
        return used;
    }

    private static string MissingMaterials(CraftDefinition cd, MultiInventory stock)
    {
        try
        {
            if (cd?.needs == null || cd.needs.Count == 0) return null;
            var parts = new List<string>();
            foreach (var need in cd.needs)
            {
                if (need == null || string.IsNullOrEmpty(need.id)) continue;
                int have = stock?.GetTotalCount(need.id) ?? 0;
                int shortfall = need.value - have;
                if (shortfall <= 0) continue;

                var iname = ScreenReader.StripNguiCodes(need.definition?.GetItemName() ?? need.id)?.Trim();
                if (string.IsNullOrWhiteSpace(iname)) iname = need.id;
                parts.Add(shortfall > 1 ? $"{shortfall} {iname}" : iname);
            }
            return parts.Count > 0 ? string.Join(", ", parts) : null;
        }
        catch
        {
            return null;
        }
    }

    // ---- helpers ----------------------------------------------------------

    /// <summary>
    /// The zone to audit: the one the build menu is working on when it's open, otherwise the scored
    /// zone the player stands in. Unscored zones (calc_method None) are rejected by the caller.
    /// </summary>
    private static WorldZone CurrentZone()
    {
        try
        {
            var fromBuild = MainGame.me?.build_mode_logics?.cur_build_zone;
            if (fromBuild != null) return fromBuild;
        }
        catch { }

        try
        {
            var zone = MainGame.me?.player?.GetMyWorldZone();
            if (zone?.definition != null
                && zone.definition.calc_method != WorldZoneDefinition.QualityCalcMethod.None)
                return zone;
        }
        catch { }

        return null;
    }

    private static float QualityOf(string objId)
    {
        try
        {
            var def = GameBalance.me.GetData<ObjectDefinition>(objId);
            if (def == null) return 0f;
            if (Mathf.Abs(def.quality_multiplier) < 0.001f) return 0f;
            return def.quality.EvaluateFloat();
        }
        catch
        {
            return 0f;
        }
    }

    private static string ObjName(string objId)
    {
        if (string.IsNullOrEmpty(objId)) return "Object";
        var id = objId.EndsWith("_place") ? objId.Substring(0, objId.Length - "_place".Length) : objId;
        return InteractionDetector.LocalizedObjectName(id);
    }

    private static string ZoneName(string id)
    {
        try
        {
            var key = "zone_" + id;
            var loc = ScreenReader.StripNguiCodes(GJL.L(key) ?? "").Trim();
            if (!string.IsNullOrEmpty(loc) && loc != key) return loc;
        }
        catch { }
        return string.IsNullOrEmpty(id) ? "This area" : char.ToUpperInvariant(id[0]) + id.Substring(1);
    }

    private static string Fmt(float v) =>
        v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
