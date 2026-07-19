namespace GraveyardKeeperAccessibility;

internal static class InteractionDetector
{
    private static string _lastAnnouncedObject = null;
    private static int _lastHighlightedDropId = 0;
    private static bool _wasCarrying = false;
    // Completion tracking — remembers the specific station last seen mid-craft so we can voice
    // the outcome even after a repair replaces the object out from under us (change_wgo).
    private static bool _craftPending = false;
    private static WorldGameObject _craftStation = null;
    private static bool _craftIsFixing = false;
    private static bool _craftIsRemoving = false;
    private static string _craftOutputName = null;
    private static WorldGameObject _lastWorkHighlight = null;
    private static bool _wasWorking = false;
    private static float _workAnnounceAccum = 0f;
    private static ManualLogSource _log;
    private static bool _initialized = false;
    private const float InteractionRange = 300f;

    internal static void Init(ManualLogSource log)
    {
        _log = log;
        _log?.LogInfo("[INTERACTION] InteractionDetector initialized (monitoring E key input)");
        _initialized = true;
    }

    internal static void Update()
    {
        if (!_initialized) return;

        try
        {
            // Detect when player presses E
            if (Input.GetKeyDown(KeyCode.E))
            {
                // The game blocks interaction with most objects during the tutorial/intro
                // (WorldGameObject.CheckIfDisabledInTutorial). To a blind player that just
                // feels like "nothing happens", so when the object the game would interact
                // with is tutorial-locked, say why.
                var gameNearest = GetGameInteractionNearest();
                if (gameNearest != null && IsTutorialDisabled(gameNearest))
                {
                    var label = GetObjectLabel(gameNearest);
                    ScreenReader.Say($"{label}. Not available during the intro.", interrupt: true);
                    _lastAnnouncedObject = gameNearest.name;
                }
                else
                {
                    var target = FindClosestInteractable();
                    if (target != null)
                    {
                        var label = WithZombieInfo(WithPalletInfo(WithBuffetInfo(WithNpcQuestInfo(WithCraftStatus(WithUpgradeInfo(WithRepairInfo(GetObjectLabel(target), target), target), target), target), target), target), target);
                        ScreenReader.Say(label, interrupt: true);
                        _lastAnnouncedObject = target.name;
                    }
                }
            }

            // Monitor proximity continuously
            var nearby = FindClosestInteractable();
            if (nearby != null)
            {
                if (nearby.name != _lastAnnouncedObject)
                {
                    var label = WithZombieInfo(WithPalletInfo(WithBuffetInfo(WithNpcQuestInfo(WithCraftStatus(WithUpgradeInfo(WithRepairInfo(GetObjectLabel(nearby), nearby), nearby), nearby), nearby), nearby), nearby), nearby);
                    ScreenReader.Say(label, interrupt: false);
                    _lastAnnouncedObject = nearby.name;
                }
            }
            else if (_lastAnnouncedObject != null)
            {
                _lastAnnouncedObject = null;
            }

            // Ground drops (bodies/loot) only highlight visually and have no interaction
            // bubble, so a blind player gets no cue they can pick something up.
            AnnounceHighlightedDrop();

            // Knowing whether a body is in hand matters: doors like the mortuary gate on
            // HasOverheadBody(), so announce the carry state on change.
            AnnounceCarryState();

            // Work actions (hold F to craft/dig/chop/...) are invisible to a blind player:
            // they get no "Press F" prompt and no progress cue. Announce both.
            AnnounceWorkState(nearby);
        }
        catch (Exception ex)
        {
            _log?.LogError($"[INTERACTION] Error: {ex.Message}");
        }
    }

    // Announce when a carryable ground drop becomes highlighted (i.e. close + faced),
    // which is exactly when vanilla E will pick it up.
    private static void AnnounceHighlightedDrop()
    {
        try
        {
            var drop = DropResGameObject.currently_higlighted_obj;
            if (drop == null || drop.is_collected ||
                drop.res == null || drop.res.IsEmpty() || drop.res.definition == null)
            {
                _lastHighlightedDropId = 0;
                return;
            }

            int id = drop.GetInstanceID();
            if (id == _lastHighlightedDropId) return;
            _lastHighlightedDropId = id;

            var name = ScreenReader.StripNguiCodes(drop.res.definition.GetItemName() ?? "").Trim();
            if (string.IsNullOrEmpty(name)) name = drop.res.id;
            ScreenReader.Say($"{name}, press E to pick up", interrupt: false);
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[INTERACTION] highlighted-drop announce failed: {ex.Message}");
        }
    }

    // Announce transitions of the player's overhead carry slot (e.g. picking up / putting
    // down a corpse), so the player knows whether they're carrying a body.
    private static void AnnounceCarryState()
    {
        try
        {
            var character = MainGame.me?.player?.components?.character;
            if (character == null) return;

            bool carrying = character.has_overhead;
            if (carrying == _wasCarrying) return;
            _wasCarrying = carrying;

            if (carrying)
            {
                var item = character.GetOverheadItem();
                string name = null;
                bool isBody = false;
                try
                {
                    // GetItemName() resolves the body's personalised name (GJL.L on its
                    // definition id) — e.g. "John's dead body" — the same source the grave UI
                    // uses, so a specific corpse is named, not just "a body".
                    name = ScreenReader.StripNguiCodes(item?.definition?.GetItemName() ?? "").Trim();
                    isBody = item?.definition?.type == ItemDefinition.ItemType.Body;
                }
                catch { }

                var spoken = !string.IsNullOrEmpty(name) ? $"Carrying {name}"
                           : isBody ? "Carrying a body"
                           : "Carrying item";
                ScreenReader.Say(spoken, interrupt: false);
            }
            else
            {
                // The body just left the overhead slot — say HOW it left, since each is
                // otherwise silent. Three cases, checked in order:
                //   1. thrown into the river at the throw_body_river spot (Yorick's quest);
                //   2. set onto a nearby table/station that now holds it (autopsy etc.);
                //   3. just dropped — bare "Hands free".
                if (FindNearbyRiverThrowSpot() != null)
                {
                    ScreenReader.Say("Body thrown in the river", interrupt: false);
                }
                else
                {
                    var table = FindNearbyObjectHoldingBody();
                    if (table != null)
                    {
                        var label = GetObjectLabel(table);
                        ScreenReader.Say($"Body placed on {label}, press E to open", interrupt: false);
                    }
                    else
                    {
                        ScreenReader.Say("Hands free", interrupt: false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[INTERACTION] carry-state announce failed: {ex.Message}");
        }
    }

    // Narrate the "hold F to work" loop a sighted player relies on but a blind player can't
    // see: the on-screen "Press F" prompt, the work-in-progress animation, and craft completion.
    //
    // Work in GK (cut flesh on the autopsy table, dig a grave, chop a tree...) is performed by
    // HOLDING GameKey.Work (default F) while standing on the object's dock point. The game shows
    // a "Press F" bubble and sets character.wgo_hilighted_for_work when you're in position; while
    // you hold F the character plays the Tool animation (anim_state == Tool) and progress fills.
    private static void AnnounceWorkState(WorldGameObject nearby)
    {
        try
        {
            var character = MainGame.me?.player?.components?.character;
            if (character == null) return;

            // 1) "Press F to {verb}" — fires when a new object becomes highlighted for work,
            //    i.e. exactly when the game would show the on-screen F prompt.
            WorldGameObject highlight = null;
            try { highlight = character.wgo_hilighted_for_work; } catch { }
            if (highlight != _lastWorkHighlight)
            {
                _lastWorkHighlight = highlight;
                if (highlight != null)
                    ScreenReader.Say($"Press F to {GetWorkVerb(highlight)}", interrupt: false);
            }

            // 2) "In progress" — while the character is actually working (F held, Tool anim).
            bool working = false;
            try { working = character.anim_state == CharAnimState.Tool; } catch { }
            if (working)
            {
                if (!_wasWorking) _workAnnounceAccum = 0f;
                _workAnnounceAccum += Time.deltaTime;
                if (_workAnnounceAccum >= 2f)
                {
                    _workAnnounceAccum = 0f;
                    ScreenReader.Say("In progress", interrupt: false);
                }
            }
            _wasWorking = working;

            // 3) Completion cue. A station craft (workbench, autopsy) and a broken-object
            //    repair both run through CraftComponent.is_crafting, but a repair finishes by
            //    REPLACING the object (change_wgo) — so by the time the craft clears, `nearby`
            //    is already the new, repaired WGO and the old one is destroyed. We therefore
            //    remember the exact station we saw working and report when THAT craft ends,
            //    rather than watching `nearby`. This also means walking away from a half-done
            //    craft (which leaves is_crafting set) doesn't trigger a false completion.
            var station = (nearby != null && nearby.obj_def != null && nearby.obj_def.has_craft)
                ? nearby.components?.craft : null;
            bool stationCrafting = station != null && station.is_crafting && station.current_craft != null;
            // Demolition also runs through a craft, but the object may not carry obj_def.has_craft,
            // so key it off is_removing rather than the station lookup above.
            bool removing = nearby != null && nearby.is_removing;

            if (stationCrafting || removing)
            {
                // Refresh what's happening each frame so we can name it once it's done. A repair
                // craft is the one the game tags Fixing (see GetFixingCraft); a demolition is
                // whatever we're removing.
                _craftPending = true;
                _craftStation = nearby;
                _craftIsRemoving = removing;
                _craftIsFixing = !removing && station.current_craft.craft_type == CraftDefinition.CraftType.Fixing;
                _craftOutputName = (removing || _craftIsFixing) ? null : CraftOutputName(station.current_craft);
            }
            else if (_craftPending && !IsStationStillCrafting(_craftStation) && !IsBeingRemoved(_craftStation))
            {
                // The remembered craft is no longer running: it finished, or its object was
                // swapped for the repaired version, or the demolition completed and destroyed it
                // (the old WGO now reads as destroyed/null).
                if (_craftIsRemoving)
                    ScreenReader.Say("Removed", interrupt: false);
                else if (_craftIsFixing)
                    ScreenReader.Say("Repaired", interrupt: false);
                else if (string.IsNullOrEmpty(_craftOutputName))
                    ScreenReader.Say("Finished", interrupt: false);
                else
                    // A station craft drops its output on the ground beside the station — a
                    // sighted player sees it land, a blind one needs telling where it went.
                    ScreenReader.Say($"{_craftOutputName} crafted, on the ground nearby", interrupt: false);
                _craftPending = false;
                _craftStation = null;
                _craftIsFixing = false;
                _craftIsRemoving = false;
                _craftOutputName = null;
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[INTERACTION] work-state announce failed: {ex.Message}");
        }
    }

    // The action verb a sighted player sees on the F prompt. Crafting stations say
    // "craft {output}"; tool actions map the required tool to its verb (shovel→dig, etc.).
    private static string GetWorkVerb(WorldGameObject wgo)
    {
        try
        {
            // An object marked for demolition in build remove-mode runs a "removal craft" — the
            // work that tears it down. That craft is a live is_crafting craft, so without this the
            // generic branch below would call it "craft"; say "remove" so the F prompt matches.
            if (wgo.is_removing) return "remove";

            // A broken station/fence carries a repair craft — the action that rebuilds it. Name
            // it "repair" rather than the generic Hammer "build" so the prompt matches the task.
            if (GetRepairCraft(wgo) != null) return "repair";

            var craft = (wgo.obj_def != null && wgo.obj_def.has_craft) ? wgo.components?.craft : null;
            if (craft != null && craft.is_crafting && craft.current_craft != null)
            {
                string outName = null;
                try { outName = ScreenReader.StripNguiCodes(craft.current_craft.GetFirstRealOutput()?.definition?.GetItemName() ?? "").Trim(); }
                catch { }
                return string.IsNullOrEmpty(outName) ? "craft" : $"craft {outName}";
            }

            var actions = wgo.obj_def?.tool_actions;
            if (actions != null && !actions.no_actions && actions.action_tools != null && actions.action_tools.Count > 0)
            {
                switch (actions.action_tools[0])
                {
                    case ItemDefinition.ItemType.Shovel: return "dig";
                    case ItemDefinition.ItemType.Axe: return "chop";
                    case ItemDefinition.ItemType.Pickaxe: return "mine";
                    case ItemDefinition.ItemType.Hammer: return "build";
                    case ItemDefinition.ItemType.Hand: return "gather";
                }
            }

            if (craft != null) return "craft";
        }
        catch { }
        return "work";
    }

    // True while the given station still has a live, running craft. A destroyed/replaced WGO
    // (e.g. a repaired object swapped via change_wgo) reads as Unity-null and counts as "done".
    private static bool IsStationStillCrafting(WorldGameObject wgo)
    {
        try
        {
            if (wgo == null) return false;
            var craft = wgo.components?.craft;
            return craft != null && craft.is_crafting && craft.current_craft != null;
        }
        catch
        {
            return false;
        }
    }

    // True while the remembered object is still marked for demolition (removal not yet finished).
    // A destroyed object reads as Unity-null → false, which is exactly when we announce "Removed".
    private static bool IsBeingRemoved(WorldGameObject wgo)
    {
        try { return wgo != null && wgo.is_removing; }
        catch { return false; }
    }

    // Readable name of a craft's main output, for the "X crafted" completion cue. Null when the
    // craft has no nameable output (then the caller falls back to a bare "Finished").
    private static string CraftOutputName(CraftDefinition craft)
    {
        try
        {
            var name = ScreenReader.StripNguiCodes(craft?.GetFirstRealOutput()?.definition?.GetItemName() ?? "").Trim();
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    // Broken tables/machines are repaired by a craft the game tags CraftType.Fixing. While the
    // object is still broken that craft is present in its craft list; once rebuilt it changes
    // into a different object that no longer has it. So the presence of a Fixing craft is our
    // "this is broken and repairable" signal, and its needs are the repair materials. The
    // floating material icons a sighted player reads are invisible to a blind one, so we voice them.
    internal static CraftDefinition GetFixingCraft(WorldGameObject wgo)
    {
        try
        {
            if (wgo == null || wgo.obj_def == null || !wgo.obj_def.has_craft) return null;
            var crafts = wgo.components?.craft?.crafts;
            if (crafts == null || crafts.Count == 0) return null;

            CraftDefinition fallback = null;
            foreach (var c in crafts)
            {
                if (c == null || c.craft_type != CraftDefinition.CraftType.Fixing) continue;
                if (fallback == null) fallback = c;
                // Prefer a craft whose condition is currently satisfiable; fall back to the
                // first Fixing craft if none evaluate cleanly.
                try { if (c.condition.EvaluateBoolean(wgo, MainGame.me.player)) return c; }
                catch { }
            }
            return fallback;
        }
        catch
        {
            return null;
        }
    }

    // The craft that repairs a broken object. Most broken stations use a Fixing craft
    // (GetFixingCraft). Worn fences instead carry a craft that rebuilds the fence in place
    // (change_wgo set, no real item output). We only fall back to that rebuild craft for objects
    // whose id marks them as a fence or already-broken variant, so ordinary stations with an
    // upgrade/grow change_wgo craft aren't mislabelled "repairable".
    internal static CraftDefinition GetRepairCraft(WorldGameObject wgo)
    {
        try
        {
            var fix = GetFixingCraft(wgo);
            if (fix != null) return fix;

            if (wgo?.obj_def == null || !wgo.obj_def.has_craft) return null;
            var id = wgo.obj_id ?? "";
            bool fenceLike = id.IndexOf("fence", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             id.IndexOf("broken", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!fenceLike) return null;

            var crafts = wgo.components?.craft?.crafts;
            if (crafts == null) return null;

            CraftDefinition fallback = null;
            foreach (var c in crafts)
            {
                if (c == null || string.IsNullOrEmpty(c.change_wgo) || c.GetFirstRealOutput() != null) continue;
                if (fallback == null) fallback = c;
                try { if (c.condition.EvaluateBoolean(wgo, MainGame.me.player)) return c; }
                catch { }
            }
            return fallback;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The craft that upgrades a placed station to its next, already-researched tier. An upgrade in
    /// GK has no dedicated code — it is just a <c>change_wgo</c> craft (like a repair) that swaps the
    /// station for a better object via <c>CraftComponent.OnCraftFinished → ReplaceWithObject</c>. A
    /// sighted player sees a floating upgrade icon; a blind player gets nothing. We surface the craft
    /// that <see cref="GetRepairCraft"/> deliberately ignores ("ordinary stations with an
    /// upgrade/grow change_wgo craft"), filtered so we don't mistake repairs, fences, or growing
    /// plants/trees for upgrades. Returns null when the station can't be upgraded.
    /// </summary>
    internal static CraftDefinition GetUpgradeCraft(WorldGameObject wgo)
    {
        try
        {
            if (wgo?.obj_def == null || !wgo.obj_def.has_craft) return null;

            // Repairs (Fixing crafts and worn fences/broken variants) are a different feature with
            // their own cues — never relabel those as upgrades.
            if (GetFixingCraft(wgo) != null) return null;
            var id = wgo.obj_id ?? "";
            if (id.IndexOf("fence", StringComparison.OrdinalIgnoreCase) >= 0 ||
                id.IndexOf("broken", StringComparison.OrdinalIgnoreCase) >= 0)
                return null;

            // Garden beds are ordinary Craft stations (interaction_type == Craft): each "plant
            // seed" recipe is a change_wgo transform (empty bed → growing crop) that outputs no
            // item, so it looks exactly like an upgrade craft. It isn't — planting is done through
            // the bed's craft menu (press E), not an upgrade. All beds/crops start with "garden"
            // by the game's own convention (see WorldGameObject.ProcessMultiQualityOutput_OLD and
            // WheresMaVeggies). Never relabel them "can be upgraded".
            if (id.StartsWith("garden", StringComparison.OrdinalIgnoreCase))
                return null;

            var crafts = wgo.components?.craft?.crafts;
            if (crafts == null) return null;

            CraftDefinition fallback = null;
            foreach (var c in crafts)
            {
                if (c == null || string.IsNullOrEmpty(c.change_wgo)) continue;
                // A transform craft, not one that outputs an item.
                if (c.GetFirstRealOutput() != null) continue;
                // Only upgrades the player has actually researched (same gate the build desk uses).
                try { if (!MainGame.me.save.IsCraftVisible(c)) continue; } catch { continue; }
                // Must turn this station into a *different* station — guards against grow crafts
                // (sapling→tree) and self-referential transforms.
                if (string.Equals(c.change_wgo, id, StringComparison.Ordinal)) continue;
                if (!TargetIsStationLike(wgo, c.change_wgo)) continue;

                if (fallback == null) fallback = c;
                try { if (c.condition.EvaluateBoolean(wgo, MainGame.me.player)) return c; }
                catch { }
            }
            return fallback;
        }
        catch
        {
            return null;
        }
    }

    // An upgrade must produce another buildable station, not a grown plant/tree. The target counts
    // as station-like when its own definition has a craft component, or when the current object is a
    // player-built object (has a removal craft). Conservative on purpose: the in-game regression
    // check (broken station / growing plant) confirms this stays tight.
    private static bool TargetIsStationLike(WorldGameObject wgo, string targetId)
    {
        try
        {
            var def = GameBalance.me?.GetDataOrNull<ObjectDefinition>(targetId)
                      ?? GameBalance.me?.GetDataOrNull<ObjectDefinition>(targetId + "_place");
            if (def != null && def.has_craft) return true;
            return wgo.has_removal_craft;
        }
        catch
        {
            return false;
        }
    }

    // The Witch Hill "Buffet" quest (Build the buffet → Serve the beer / Serve the burgers) has two
    // scripted serve stands. Unlike the quest text ("Make 10 Beers and 5 Burgers") the serve action
    // is a Flow that checks the player's inventory for the EXACT gold-quality item id — cup_beer:3
    // and meal:burger:3 — so bronze/silver beer or burgers are silently refused with no on-screen
    // hint. A sighted player learns this by trial; a blind player just hears "nothing happened". Map
    // each stand's obj_id to what it actually demands so we can say the quality out loud.
    private sealed class BuffetNeed
    {
        public string ItemId;  // exact gold-quality id the serve script checks for
        public int Count;      // how many the quest wants total
        public string Noun;    // spoken noun, e.g. "beer" / "burgers"
    }

    // The buffet is a single crafting station (obj_id "vendor_stall", whose object def is literally
    // named "Buffet"; beer_barrels_place / burgers_place are only its custom tags, NOT the obj_id we
    // see at runtime). Pressing E opens its craft window whose two "serve" recipes each demand an
    // exact gold-quality ingredient. Read out BOTH requirements in a fixed order (an array, not a
    // dict, to keep that order deterministic) whenever the player reaches the stand.
    private static readonly BuffetNeed[] _buffetNeeds =
    {
        new BuffetNeed { ItemId = "cup_beer:3", Count = 10, Noun = "beer" },
        new BuffetNeed { ItemId = "meal:burger:3", Count = 5, Noun = "burgers" },
    };

    private static readonly HashSet<string> _buffetStandIds = new HashSet<string>
    {
        "vendor_stall",
    };

    /// <summary>
    /// Append the whole Witch Hill buffet's serve requirements to a buffet stand's label — the exact
    /// quality tier each serve script demands and how many the player still owes — so a blind player
    /// learns both up front on approach/E instead of by trial-and-error feeding items in. Returns the
    /// bare label unchanged for anything that isn't a buffet stand.
    /// </summary>
    private static string WithBuffetInfo(string label, WorldGameObject wgo)
    {
        try
        {
            var id = wgo?.obj_id;
            if (string.IsNullOrEmpty(id) || !_buffetStandIds.Contains(id))
                return label;

            var parts = new List<string>();
            foreach (var need in _buffetNeeds)
            {
                int have = 0;
                try { have = MainGame.me.player.data.GetItemsCount(need.ItemId, count_secondary_inventory: true); }
                catch { }

                var tail = have >= need.Count ? "you have enough" : $"you have {have}";
                parts.Add($"requires {need.Count} {need.Noun}, golden quality, {tail}");
            }
            var result = $"{label}. {string.Join(". ", parts)}";
            _log?.LogInfo($"[INTERACTION] Buffet requirements announced: {result}");
            return result;
        }
        catch
        {
            return label;
        }
    }

    /// <summary>
    /// Append upgrade guidance to a station's label: the tier it can become, the materials the
    /// upgrade consumes, and what the player is still short of. Returns the bare label unchanged for
    /// non-upgradeable objects. See <see cref="GetUpgradeCraft"/>.
    /// </summary>
    private static string WithUpgradeInfo(string label, WorldGameObject wgo)
    {
        try
        {
            var up = GetUpgradeCraft(wgo);
            if (up == null) return label;

            var newName = LocalizedObjectName(up.change_wgo);
            var into = string.IsNullOrWhiteSpace(newName) ? "" : $" to {newName}";

            var needs = up.needs;
            if (needs == null || needs.Count == 0)
                return $"{label}. Can be upgraded{into}, press F to upgrade";

            var all = new List<string>();
            var missing = new List<string>();
            foreach (var need in needs)
            {
                if (need == null || string.IsNullOrEmpty(need.id)) continue;

                var iname = ScreenReader.StripNguiCodes(need.definition?.GetItemName() ?? need.id)?.Trim();
                if (string.IsNullOrWhiteSpace(iname)) iname = need.id;
                all.Add(need.value > 1 ? $"{need.value} {iname}" : iname);

                int have = 0;
                try { have = MainGame.me.player.data.GetItemsCount(need.id, count_secondary_inventory: true); }
                catch { }
                int shortfall = need.value - have;
                if (shortfall > 0)
                    missing.Add(shortfall > 1 ? $"{shortfall} {iname}" : iname);
            }

            if (all.Count == 0)
                return $"{label}. Can be upgraded{into}, press F to upgrade";

            var tail = missing.Count > 0
                ? $"You still need {string.Join(", ", missing)}"
                : "You have the materials, press F to upgrade";
            return $"{label}. Can be upgraded{into}, needs {string.Join(", ", all)}. {tail}";
        }
        catch
        {
            return label;
        }
    }

    /// <summary>
    /// Append repair guidance to a broken object's label: the materials its repair consumes and
    /// what the player is still short of. Returns the bare label unchanged for non-repairable
    /// objects. See <see cref="GetRepairCraft"/>.
    /// </summary>
    private static string WithRepairInfo(string label, WorldGameObject wgo)
    {
        try
        {
            // Graves are repaired through their own menu (press E to open it, then pick the worn
            // part), never a hold-F world action. Point at E and flag a worn fence instead of
            // appending the F-repair text below — which is what made graves wrongly say
            // "press F to repair" when F does nothing for them.
            if (wgo?.obj_def != null &&
                wgo.obj_def.interaction_type == ObjectDefinition.InteractionType.Grave)
                return WithGraveFenceInfo(label, wgo);

            var fix = GetRepairCraft(wgo);
            if (fix == null) return label;

            var needs = fix.needs;
            if (needs == null || needs.Count == 0)
                return $"{label}. Repairable, press F to repair";

            var all = new List<string>();
            var missing = new List<string>();
            foreach (var need in needs)
            {
                if (need == null || string.IsNullOrEmpty(need.id)) continue;

                var iname = ScreenReader.StripNguiCodes(need.definition?.GetItemName() ?? need.id)?.Trim();
                if (string.IsNullOrWhiteSpace(iname)) iname = need.id;
                all.Add(need.value > 1 ? $"{need.value} {iname}" : iname);

                int have = 0;
                try { have = MainGame.me.player.data.GetItemsCount(need.id, count_secondary_inventory: true); }
                catch { }
                int shortfall = need.value - have;
                if (shortfall > 0)
                    missing.Add(shortfall > 1 ? $"{shortfall} {iname}" : iname);
            }

            if (all.Count == 0)
                return $"{label}. Repairable, press F to repair";

            var tail = missing.Count > 0
                ? $"You still need {string.Join(", ", missing)}"
                : "You have the materials, press F to repair";
            return $"{label}. Repairable, needs {string.Join(", ", all)}. {tail}";
        }
        catch
        {
            return label;
        }
    }

    /// <summary>
    /// Append a crafting station's live state to its label: what it's making and how far along
    /// ("Making iron, 60 percent done"), plus what it currently holds ("Contains 8 fuel"). A blind
    /// player can't see the furnace's progress bar, its fuel gauge, or items sitting inside it, so
    /// without this they only hear the bare station name and can't tell whether a smelt is done,
    /// whether the oven is fuelled, or where the fuel/food they made went. Re-read on every
    /// approach/E-press so the numbers stay current. Repair crafts are skipped (the repair-info /
    /// "Repaired" cues already cover those).
    /// </summary>
    private static string WithCraftStatus(string label, WorldGameObject wgo)
    {
        try
        {
            // An object marked for demolition runs a "removal craft" whose output is the refunded
            // material (e.g. stone). Reporting that as "Making stone" is misleading — it's being
            // torn down, not built. Say so instead, and never fall through to the crafting branch.
            if (wgo != null && wgo.is_removing)
            {
                int rpct = Mathf.RoundToInt(Mathf.Clamp01(wgo.progress) * 100f);
                return $"{label}. Marked for removal, {rpct} percent torn down. Press F to remove";
            }

            var craft = (wgo?.obj_def != null && wgo.obj_def.has_craft) ? wgo.components?.craft : null;
            if (craft == null) return label;

            if (craft.is_crafting && craft.current_craft != null &&
                craft.current_craft.craft_type != CraftDefinition.CraftType.Fixing)
            {
                var outName = CraftOutputName(craft.current_craft);
                int pct = Mathf.RoundToInt(Mathf.Clamp01(wgo.progress) * 100f);
                var what = string.IsNullOrEmpty(outName) ? "something" : outName;
                label = $"{label}. Making {what}, {pct} percent done";
            }

            var contents = StationContents(wgo, craft);
            if (!string.IsNullOrEmpty(contents))
                label = $"{label}. {contents}";

            return label;
        }
        catch
        {
            return label;
        }
    }

    /// <summary>
    /// Describe what a station is holding: resources its own crafts deposit into it (e.g. an oven's
    /// fuel — stored as an object param, NOT a ground drop, which is why a crafted "Brennstoff"
    /// seems to vanish) plus any items physically inside it. The fuel param can be named anything,
    /// so we learn the resource names from the station's crafts' <c>output_res_wgo</c> and read the
    /// object's current value of each. Returns null when the station holds nothing.
    /// </summary>
    private static string StationContents(WorldGameObject wgo, CraftComponent craft)
    {
        try
        {
            var parts = new List<string>();

            // Resources the station's crafts add to the object itself (fuel, charge, …).
            var resTypes = new HashSet<string>();
            if (craft.crafts != null)
            {
                foreach (var c in craft.crafts)
                {
                    var types = c?.output_res_wgo?.Types;
                    if (types == null) continue;
                    foreach (var t in types)
                        if (!string.IsNullOrEmpty(t)) resTypes.Add(t);
                }
            }
            foreach (var t in resTypes)
            {
                float v = wgo.GetParam(t);
                if (v > 0.01f)
                    parts.Add($"{Mathf.RoundToInt(v)} {ResourceDisplayName(t)}");
            }

            // Items physically inside the station (loaded ingredients, a body on the table, …).
            var inv = wgo.data?.inventory;
            if (inv != null)
            {
                foreach (var it in inv)
                {
                    if (it == null || it.IsEmpty()) continue;
                    var n = ScreenReader.StripNguiCodes(it.definition?.GetItemName() ?? it.id)?.Trim();
                    if (string.IsNullOrEmpty(n)) continue;
                    parts.Add(it.value > 1 ? $"{it.value} {n}" : n);
                }
            }

            return parts.Count == 0 ? null : "Contains " + string.Join(", ", parts);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Readable name for a station's stored resource param. These internal param names aren't real
    /// localization keys, so they'd otherwise read as the raw token — and the furnace's fuel charge
    /// is unhelpfully called "fire". Map the ones we know to plain words; fall back to the localized
    /// / cleaned token for anything else.
    /// </summary>
    private static string ResourceDisplayName(string resType)
    {
        switch (resType)
        {
            case "fire": return "fuel";
            default: return LocalizedObjectName(resType);
        }
    }

    /// <summary>
    /// Repair guidance for a grave: its fence (and cross) wear down over time and are restored
    /// with a repair kit from the grave's own menu. If the fence is worn, append its condition and
    /// point at E (the menu key) — not F. Returns the bare label for a pristine or fence-less grave.
    /// </summary>
    private static string WithGraveFenceInfo(string label, WorldGameObject grave)
    {
        try
        {
            var fence = grave.data?.GetItemOfType(ItemDefinition.ItemType.GraveFence);
            if (fence == null || fence.durability >= 0.999f) return label;
            int pct = Mathf.RoundToInt(Mathf.Clamp01(fence.durability) * 100f);
            return $"{label}. Worn fence {pct} percent, press E to open the grave and repair it";
        }
        catch
        {
            return label;
        }
    }

    /// <summary>
    /// Find the closest interactable object within reach whose inventory now contains a body
    /// (e.g. the autopsy table after laying a corpse down). Used to confirm placement audibly.
    /// </summary>
    private static WorldGameObject FindNearbyObjectHoldingBody()
    {
        try
        {
            var player = MainGame.me?.player;
            if (player == null) return null;
            var playerPos = player.pos;

            WorldGameObject best = null;
            float bestDist = float.MaxValue;
            foreach (var obj in UnityEngine.Object.FindObjectsOfType<WorldGameObject>(true))
            {
                if (obj == null || IsPlayer(obj) || IsPrefab(obj)) continue;
                if (!obj.gameObject.activeInHierarchy) continue;

                // 1 tile = 96 world units; only consider objects within a couple of tiles.
                var dist = Vector2.Distance(obj.pos, playerPos);
                if (dist > 240f || dist >= bestDist) continue;

                Item body = null;
                try { body = obj.GetBodyFromInventory(); } catch { }
                if (body == null) continue;

                best = obj;
                bestDist = dist;
            }
            return best;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The river-disposal spot (obj_id "throw_body_river") the player stands on to chuck a
    /// carried corpse into the water — Yorick's "throw the neighbour in the river" step. Used
    /// to announce a throw distinctly from a plain set-down. See [[exhumation-grave-disposal]].
    /// </summary>
    private static WorldGameObject FindNearbyRiverThrowSpot()
    {
        try
        {
            var player = MainGame.me?.player;
            if (player == null) return null;
            var playerPos = player.pos;

            foreach (var obj in UnityEngine.Object.FindObjectsOfType<WorldGameObject>(true))
            {
                if (obj == null || IsPlayer(obj) || IsPrefab(obj)) continue;
                if (!obj.gameObject.activeInHierarchy) continue;
                if (string.IsNullOrEmpty(obj.obj_id)) continue;
                if (obj.obj_id.IndexOf("throw_body_river", StringComparison.OrdinalIgnoreCase) < 0) continue;

                // 1 tile = 96 world units; the player stands right on the throw spot.
                if (Vector2.Distance(obj.pos, playerPos) <= 240f) return obj;
            }
        }
        catch { }
        return null;
    }

    // The object the game considers "in reach" for an E press (its highlighted interaction
    // target), or null. This is what the game would actually act on, so it's the right thing
    // to test for the tutorial lock rather than our looser nearest-by-distance scan.
    private static WorldGameObject GetGameInteractionNearest()
    {
        try { return MainGame.me?.player?.components?.interaction?.nearest; }
        catch { return null; }
    }

    // True when the game would refuse to interact with this object because the player is still
    // in the tutorial and the object isn't flagged interactive_in_tutorial
    // (see WorldGameObject.CheckIfDisabledInTutorial / GameSave.IsInTutorial).
    private static bool IsTutorialDisabled(WorldGameObject wgo)
    {
        try
        {
            return wgo?.obj_def != null
                && !wgo.obj_def.interactive_in_tutorial
                && MainGame.me?.save != null
                && MainGame.me.save.IsInTutorial();
        }
        catch
        {
            return false;
        }
    }

    private static WorldGameObject FindClosestInteractable()
    {
        try
        {
            if (MainGame.me?.player == null)
                return null;

            var playerPos = MainGame.me.player.transform.position;
            var allObjects = UnityEngine.Object.FindObjectsOfType<WorldGameObject>(true);

            if (allObjects == null || allObjects.Length == 0)
                return null;

            // Find closest object, filtering out inactive ones
            var nearby = allObjects
                .Where(obj => obj != null && !obj.is_removed && !IsPlayer(obj) && !IsPrefab(obj))
                .Where(obj => obj.gameObject.activeInHierarchy)
                // Don't announce DLC "ruins" (souls zone, Euric's room, ...) the player doesn't own;
                // they spawn into every save regardless of ownership but are inert without the DLC.
                .Where(obj => ObjectNavigator.IsObjectDlcAvailable(obj))
                .OrderBy(obj => Vector3.Distance(obj.transform.position, playerPos))
                .FirstOrDefault();

            if (nearby == null)
                return null;

            var distance = Vector3.Distance(nearby.transform.position, playerPos);
            if (distance > InteractionRange)
                return null;

            return nearby;
        }
        catch (Exception ex)
        {
            _log?.LogError($"[INTERACTION] Error: {ex.Message}");
            return null;
        }
    }

    internal static bool IsPlayer(WorldGameObject obj)
    {
        return obj.name.Contains("Player");
    }

    internal static bool IsPrefab(WorldGameObject obj)
    {
        return obj.name.Contains("prefab") || obj.name.Contains("Prefab") || obj.name.Contains("template");
    }

    /// <summary>
    /// Append a resurrected zombie worker's work values to its label. A zombie carries the quality of
    /// the corpse it was raised from, exactly the way a body carries its skulls (see <see cref="SkullInfo"/>):
    /// the game turns the corpse's usable white skulls into a work-efficiency figure
    /// (<c>working_k = white_skulls / 40</c>, see <c>Worker.UpdateWorkerLevel</c>) that decides how fast the
    /// zombie works its station. A sighted player reads this off the worker panel's skull bar / an overhead
    /// number; a blind player got nothing at all. Voice the efficiency, the white-skull count behind it, and
    /// which station the zombie is assigned to (or that it's idle). Non-workers pass through unchanged; the
    /// invisible refugee-camp worker (no body, no station) is skipped.
    /// </summary>
    /// <summary>
    /// Append a worker zombie's efficiency + assignment to a label, for the object tracker. Pressing
    /// E on a zombie PICKS IT UP, so the proximity/E readout can't be used to inspect one — the
    /// player needs this while browsing the navigator instead. Returns the label unchanged for
    /// anything that isn't a (visible) worker zombie, so it's safe to call on every tracked object.
    /// </summary>
    internal static string AppendWorkerInfo(string label, WorldGameObject wgo) => WithZombieInfo(label, wgo);

    private static string WithZombieInfo(string label, WorldGameObject wgo)
    {
        try
        {
            if (wgo == null || !wgo.IsWorker() || wgo.IsInvisibleWorker())
                return label;

            // Compute the efficiency the game's own way: usable white skulls of the corpse behind the
            // zombie, divided by 40, floored at one white skull (Worker.UpdateWorkerLevel). dont_count_self
            // matches the game — the body item itself isn't counted, only its skull-bearing parts/organs.
            int red = 0, white = 0;
            try { wgo.data.GetBodySkulls(out red, out white, out int _, dont_count_self: true); } catch { }
            int effWhite = white <= 0 ? 1 : white;
            int pct = Mathf.RoundToInt(effWhite / 40f * 100f);

            var value = $"{pct} percent work efficiency, {effWhite} white {(effWhite == 1 ? "skull" : "skulls")}";
            // Red skulls don't slow a worker (only white feed working_k), but they're part of the same
            // corpse value a player might be comparing against, so mention them when present.
            if (red > 0)
                value += $", {red} red {(red == 1 ? "skull" : "skulls")}";

            // What the zombie is doing: its assigned station, or idle.
            string job = null;
            try
            {
                var bench = wgo.linked_workbench;
                if (bench != null && !string.IsNullOrEmpty(bench.obj_id))
                    job = LocalizedObjectName(bench.obj_id);
            }
            catch { }
            var tail = string.IsNullOrEmpty(job) ? "not assigned to a station" : $"working at {job}";

            return $"{label}. {value}. {tail}";
        }
        catch
        {
            return label;
        }
    }

    /// <summary>
    /// For an NPC, append the quest marker the game floats over their head. A sighted player sees
    /// an exclamation icon (icon_quest_mark_small) above any NPC who has a <see cref="KnownNPC.TaskState.State.Visible"/>
    /// task; a blind player gets no such cue about whom it's worth talking to. We read the same
    /// per-NPC task list the quest log uses (<c>MainGame.me.save.known_npcs</c>, see
    /// <see cref="QuestAnnouncer"/>) and voice the objective, so "Horadric's wife" becomes
    /// "Horadric's wife. Has a task: bring the necklace." Non-NPCs and NPCs with no visible task
    /// pass through unchanged.
    /// </summary>
    private static string WithNpcQuestInfo(string label, WorldGameObject wgo)
    {
        try
        {
            if (string.IsNullOrEmpty(label) || wgo?.obj_def == null || !wgo.obj_def.IsNPC())
                return label;

            // known_npcs keys NPCs by their alias-resolved id (see KnownNPCList.GetOrCreateNPC),
            // so match on the alias first, then the raw obj id as a fallback.
            var def = wgo.obj_def;
            var key = string.IsNullOrEmpty(def.npc_alias) ? def.id : def.npc_alias;

            var npcs = MainGame.me?.save?.known_npcs?.npcs;
            if (npcs == null) return label;

            var tasks = new List<string>();
            foreach (var npc in npcs)
            {
                if (npc?.tasks == null) continue;
                if (npc.npc_id != key && npc.npc_id != wgo.obj_id) continue;

                foreach (var task in npc.tasks)
                {
                    if (task == null || task.state != KnownNPC.TaskState.State.Visible) continue;

                    string text = null;
                    try { text = ScreenReader.StripNguiCodes(task.GetTaskText() ?? "").Trim(); }
                    catch { }

                    // GJL.L echoes back "!task_x!" when there's no translation — skip those.
                    if (!string.IsNullOrEmpty(text) && text.IndexOf('!') < 0)
                        tasks.Add(text);
                }
                break;
            }

            if (tasks.Count == 0) return label;
            var header = tasks.Count == 1 ? "Has a task" : "Has tasks";
            return $"{label}. {header}: {string.Join(". ", tasks)}";
        }
        catch
        {
            return label;
        }
    }

    // A pallet (box_pallet, plus the merchant/sale pallets — any obj whose id contains "pallet")
    // holds the shipping crates for the merchant-selling / zombie-logistics questline. A sighted
    // player can see at a glance whether a pallet is empty, holds crates to grab, or is full; a
    // blind player pressing E just gets a crate silently put on their head (or nothing). Append the
    // pallet's state so the player knows BEFORE pressing E what will happen:
    //   - carrying a crate  -> whether there's room to leave it here
    //   - hands free + crates -> what's on it and that E takes one
    //   - empty              -> "empty pallet"
    private static string WithPalletInfo(string label, WorldGameObject wgo)
    {
        try
        {
            if (string.IsNullOrEmpty(label) || !IsCratePallet(wgo) || wgo.data == null)
                return label;

            var inv = wgo.data.inventory;
            int crateCount = 0;
            string firstCrateId = null;
            var parts = new List<string>();
            if (inv != null)
            {
                foreach (var it in inv)
                {
                    if (it == null || it.IsEmpty() || it.definition == null) continue;
                    crateCount += it.value;
                    if (firstCrateId == null) firstCrateId = it.id;
                    string nm = null;
                    try { nm = ScreenReader.StripNguiCodes(it.definition.GetItemName() ?? "").Trim(); } catch { }
                    if (string.IsNullOrEmpty(nm)) nm = it.id;
                    parts.Add(it.value > 1 ? $"{it.value} {nm}" : nm);
                }
            }

            // If the player is already carrying a crate, the useful question is "can I drop it here".
            var ch = MainGame.me?.player?.components?.character;
            var carried = (ch != null && ch.has_overhead) ? ch.GetOverheadItem() : null;
            bool carryingCrate = carried?.definition != null && carried.definition.is_crate;

            if (carryingCrate)
            {
                bool room = false;
                try { room = wgo.data.CanAddCount(carried.id, true) > 0; } catch { }
                var here = crateCount == 0
                    ? "empty pallet"
                    : $"pallet holds {crateCount} {(crateCount == 1 ? "crate" : "crates")}: {string.Join(", ", parts)}";
                var verdict = room ? "you can leave your crate here" : "no room for your crate here";
                return $"{label}. {Capitalize(here)}. {verdict}";
            }

            if (crateCount == 0)
                return $"{label}. Empty pallet";

            bool full = false;
            if (firstCrateId != null)
            {
                try { full = wgo.data.CanAddCount(firstCrateId, true) <= 0; } catch { }
            }
            var fullNote = full ? ", full" : "";
            return $"{label}. Pallet, {crateCount} {(crateCount == 1 ? "crate" : "crates")}: {string.Join(", ", parts)}{fullNote}. Press E to take a crate";
        }
        catch
        {
            return label;
        }
    }

    // A pallet is any world object whose id contains "pallet" (box_pallet and the merchant sale
    // pallets). Kept to the id substring on purpose: matching "accepts a crate" would also catch
    // chests that happen to hold one, and E on a chest opens it rather than taking a crate.
    private static bool IsCratePallet(WorldGameObject wgo)
    {
        if (wgo?.obj_def == null) return false;
        return (wgo.obj_id ?? "").IndexOf("pallet", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string Capitalize(string s)
        => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);

    // Stations whose own localized name is too generic to navigate by. mf_alchemy_survey is the
    // study/research table — you study items for tech points, and decompose notes/paper/books for
    // science (Wissenschaft) here — but it localizes to the bare "Arbeitstisch" (work table). Name
    // it for what it does so it matches the on-open purpose announcement. Add more ids here only
    // when a station's real name is genuinely confusing.
    private static readonly Dictionary<string, string> StationNameOverrides =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mf_alchemy_survey"] = "Forschungstisch",
        };

    internal static string GetObjectLabel(WorldGameObject wgo)
    {
        if (wgo == null)
            return null;

        // Check if this is an exit/door (teleport object). Label it by destination so a
        // blind player can tell which door leads where (e.g. into the mortuary vs back out)
        // instead of hearing a row of identical "Door" entries.
        if (IsExitObject(wgo))
        {
            return GetDoorLabel(wgo);
        }

        // Try to get a label from the object definition
        try
        {
            if (wgo.obj_def != null)
            {
                // A few stations have generic/ambiguous game names that leave a blind player
                // unable to tell them apart — e.g. the alchemy survey table localizes to the
                // bare "Arbeitstisch" (work table), easily confused with the research/study
                // table. Override those with an explicit name. Keep this list to genuinely
                // ambiguous ids; everything else keeps its real localized name.
                if (!string.IsNullOrEmpty(wgo.obj_def.id) &&
                    StationNameOverrides.TryGetValue(wgo.obj_def.id, out var clearName))
                    return clearName;

                // Try to use the object id, localized to a readable name where possible
                if (!string.IsNullOrEmpty(wgo.obj_def.id))
                    return LocalizedObjectName(wgo.obj_def.id);

                // Fall back to interaction type
                var typeString = wgo.obj_def.interaction_type.ToString();
                if (!string.IsNullOrEmpty(typeString))
                    return CleanObjectName(typeString);
            }
        }
        catch
        {
            // Fall back to object name if obj_def access fails
        }

        return CleanObjectName(wgo.name);
    }

    /// <summary>
    /// Name a teleport door so doors that share an obj_id are still distinguishable. The door
    /// "kind" comes from obj_id (teleport_inside / teleport_outside / hatch / stairs / dungeon);
    /// the destination comes from custom_tag, which the game formats as
    /// "tp_&lt;place&gt;_&lt;a|b&gt;[...]" (e.g. "tp_tavern_from_cellar_b_", "tp_mortuary_hatch_2_b").
    /// So a row of identical "Door inside" entries becomes "Door inside: Tavern cellar",
    /// "Door inside: Mortuary", etc. Falls back to the bare kind when no place can be recovered.
    /// </summary>
    internal static string GetDoorLabel(WorldGameObject wgo)
    {
        var id = (wgo?.obj_id ?? "").ToLowerInvariant();
        string kind =
            id.Contains("dungeon_exit") ? "Dungeon exit" :
            id.Contains("inside") ? "Door inside" :
            id.Contains("outside") ? "Door outside" :
            id.Contains("hatch") ? "Hatch" :
            id.Contains("stairs") ? "Stairs" :
            id.Contains("dungeon") ? "Dungeon entrance" :
            "Door";

        var place = DoorPlaceFromTag(wgo?.custom_tag);
        return string.IsNullOrEmpty(place) ? kind : $"{kind}: {place}";
    }

    /// <summary>
    /// Recover a human-readable destination from a teleport door's custom_tag. Tags follow the
    /// game's "tp_&lt;place&gt;_&lt;a|b&gt;[_extra][_]" convention (see the teleport spawns in
    /// GameSave and Flow_TeleportToWGO, which itself splits the tag on '_' and treats index 1 as
    /// the place key). Pair-end markers (a/b), direction connectors, numeric suffixes and the kind
    /// words already conveyed by obj_id are stripped, leaving the descriptive place words.
    /// Returns null when nothing meaningful remains.
    /// </summary>
    internal static string DoorPlaceFromTag(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return null;
        tag = tag.ToLowerInvariant().Trim();
        if (!tag.StartsWith("tp_")) return null;

        // Drop the "tp_" prefix and the trailing "_" that marks the door object (vs its anchor).
        var body = tag.Substring(3).Trim('_');

        var words = new List<string>();
        foreach (var part in body.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part)
            {
                case "a":
                case "b":
                case "to":
                case "from":
                case "hatch":
                case "stairs":
                case "inside":
                case "outside":
                    continue;
            }
            if (int.TryParse(part, out _)) continue;
            words.Add(part);
        }

        if (words.Count == 0) return null;

        var place = string.Join(" ", words).Replace("-", " ").Trim();
        // Guard against unhelpful single-letter tokens (e.g. "tp_h_a_").
        if (place.Length < 2) return null;
        return char.ToUpper(place[0]) + place.Substring(1);
    }

    private static bool IsExitObject(WorldGameObject wgo)
    {
        if (wgo == null || wgo.obj_def == null)
            return false;

        // Check by object name pattern (teleport objects are exits)
        if (wgo.name.IndexOf("teleport", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        // Check by interaction type if available
        try
        {
            // If obj_def has interaction_type property, check it
            var interactionType = wgo.obj_def?.interaction_type;
            if (interactionType != null)
            {
                var typeString = interactionType.ToString();
                if (typeString.IndexOf("Teleport", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
        }
        catch
        {
            // If we can't access interaction_type, fall back to name-based detection
        }

        return false;
    }

    /// <summary>
    /// Localized, human-readable name for an object-definition id. The game stores object
    /// names under their id key (see WorldGameObject: <c>GJL.L(this.obj_def.id)</c>), so
    /// "mf_preparation_1" resolves to "Autopsy table" / "Obduktionstisch" instead of the
    /// raw "Mf preparation 1". Falls back to the prettified id when there is no translation.
    /// </summary>
    internal static string LocalizedObjectName(string objId)
    {
        try
        {
            var loc = ScreenReader.StripNguiCodes(GJL.L(objId) ?? "").Trim();
            // GJL.L echoes the key back (or returns a "!key!" marker) when a translation
            // is missing — only use the result when it is a real, different string.
            if (!string.IsNullOrEmpty(loc) &&
                !loc.Equals(objId, StringComparison.OrdinalIgnoreCase) &&
                loc.IndexOf('!') < 0)
                return loc;
        }
        catch { }
        return CleanObjectName(objId);
    }

    private static string CleanObjectName(string objectName)
    {
        if (string.IsNullOrEmpty(objectName))
            return objectName;

        // Remove [wgo] prefix
        var cleaned = objectName.StartsWith("[wgo]")
            ? objectName.Substring(5).Trim()
            : objectName;

        // Replace underscores and hyphens with spaces
        cleaned = cleaned.Replace("_", " ").Replace("-", " ");

        // Remove (Clone) suffix
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s*\(Clone\)\s*$", "");

        // Capitalize first letter
        if (cleaned.Length > 0)
            cleaned = char.ToUpper(cleaned[0]) + cleaned.Substring(1);

        return cleaned.Trim();
    }
}
