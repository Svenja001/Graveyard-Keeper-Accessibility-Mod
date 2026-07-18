namespace GraveyardKeeperAccessibility;

// The zombie transport station (PorterStationGUI, "Transportstation"). A zombie docked here
// carries the crates sitting in the source zone's storages (pallets) to a fixed destination
// zone. The window has two things a blind player needs but the generic path mangles:
//   1. a per-good check list — each row is a crate type the route can carry, with a check mark
//      that decides whether it's actually transported (the game stores the *unchecked* ones as
//      the station's blacklist on close). The generic item-cell reader would voice these as
//      "beer crate, 2" (mistaking the 1/2 check flag for a stack count), so we own the rows and
//      read/toggle the on/off state instead.
//   2. no keyboard way to hear whether a zombie is even docked, or what it's doing.
// The intro reads the worker/station state so the player can tell *why* nothing is moving
// (no zombie, or every good switched off), which is exactly the "he doesn't do anything" case.
internal static class PorterStationHandler
{
    private static FieldInfo _stationField;
    private static MethodInfo _onItemClickedMethod;

    private static PorterStation GetStation(PorterStationGUI gui)
    {
        try
        {
            _stationField ??= AccessTools.Field(typeof(PorterStationGUI), "_station");
            return _stationField?.GetValue(gui) as PorterStation;
        }
        catch { return null; }
    }

    // One navigable row per transportable good, plus a "take zombie back" row when a docked
    // zombie is idle. Rows are plain buttons: Enter toggles carrying (goods) or removes the
    // zombie. Cells the GUI padded in for big items are empty (id_empty) and skipped.
    internal static void Discover(PorterStationGUI gui, List<GUIElement> elements)
    {
        var count = 0;
        foreach (var cell in gui.GetComponentsInChildren<BaseItemCellGUI>(true))
        {
            if (cell == null || !cell.gameObject.activeInHierarchy) continue;
            if (cell.GetComponentInParent<BaseGUI>() != gui) continue;
            if (cell.id_empty || cell.item == null || cell.item.IsEmpty()) continue;
            if (elements.Any(e => e.Go == cell.gameObject)) continue;

            var capturedCell = cell;
            elements.Add(new GUIElement
            {
                Go = cell.gameObject,
                Label = CrateRowLabel(capturedCell),
                Type = ElementType.Button,
                ReadDynamic = () => CrateRowLabel(capturedCell),
                OnActivate = () => ToggleCrate(gui, capturedCell)
            });
            count++;
        }

        // Only offer "take back" when the game itself would: a worker is docked and idle
        // (mirrors the condition PorterStationGUI.Open uses to enable its remove-body button).
        var station = GetStation(gui);
        if (station != null && station.HasLinkedWorker() && station.state == PorterStation.PorterState.Waiting)
        {
            elements.Add(new GUIElement
            {
                Go = gui.gameObject,
                Label = "Take zombie back",
                Type = ElementType.Button,
                OnActivate = () => TakeZombie(gui)
            });
        }

        Plugin.Log.LogInfo($"[PORTER] Discovered {count} transportable good(s)");
    }

    // Spoken intro: whether a zombie is docked and what it's doing, then how many goods the
    // route carries and how to toggle them. GoingToDestination/Source mean it's mid-trip;
    // Waiting means it's ready but currently has nothing to carry (all goods off, source empty,
    // or the destination is full).
    internal static string IntroFor(PorterStationGUI gui)
    {
        var station = GetStation(gui);
        string status;
        if (station == null || !station.HasLinkedWorker())
        {
            status = "Transport station. No zombie assigned. Bring a zombie here to carry goods.";
        }
        else
        {
            string state;
            switch (station.state)
            {
                case PorterStation.PorterState.Waiting:
                    state = "Zombie assigned, waiting for goods to carry.";
                    break;
                case PorterStation.PorterState.GoingToDestination:
                    state = "Zombie assigned, carrying goods to the destination.";
                    break;
                case PorterStation.PorterState.GoingToSource:
                    state = "Zombie assigned, returning for more.";
                    break;
                default:
                    state = "Zombie assigned.";
                    break;
            }
            status = "Transport station. " + state;
        }

        var goods = CountGoods(gui);
        if (goods == 0)
            return status + " No goods set up for this route.";
        var noun = goods == 1 ? "good" : "goods";
        return $"{status} {goods} {noun}. Press Enter on each to turn carrying on or off.";
    }

    private static int CountGoods(PorterStationGUI gui)
    {
        var count = 0;
        foreach (var cell in gui.GetComponentsInChildren<BaseItemCellGUI>(true))
        {
            if (cell == null || !cell.gameObject.activeInHierarchy) continue;
            if (cell.GetComponentInParent<BaseGUI>() != gui) continue;
            if (cell.id_empty || cell.item == null || cell.item.IsEmpty()) continue;
            count++;
        }
        return count;
    }

    // "beer crate, transporting" / "beer crate, not transporting". The check flag lives in
    // item.value: 1 = carried, 2 = blacklisted (see PorterStationGUI.Open / OnClosePressed).
    private static string CrateRowLabel(BaseItemCellGUI cell)
    {
        try
        {
            var name = ScreenReader.StripNguiCodes(cell?.item?.definition?.GetItemName() ?? cell?.item?.id ?? "")?.Trim();
            if (string.IsNullOrWhiteSpace(name)) name = "goods";
            var on = cell?.item != null && cell.item.value == 1;
            return on ? $"{name}, transporting" : $"{name}, not transporting";
        }
        catch { return "goods"; }
    }

    // Flip a good on/off by driving the game's own OnItemClicked (value cycles 1->2->1 and
    // redraws the check mark), so the change is committed exactly like a mouse click when the
    // window closes. Then read the new state back.
    private static void ToggleCrate(PorterStationGUI gui, BaseItemCellGUI cell)
    {
        try
        {
            if (cell?.item == null) return;
            _onItemClickedMethod ??= AccessTools.Method(typeof(PorterStationGUI), "OnItemClicked");
            _onItemClickedMethod?.Invoke(gui, new object[] { cell });
            ScreenReader.Say(CrateRowLabel(cell), interrupt: true);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[PORTER] toggle failed: {ex.Message}");
        }
    }

    // Pull the zombie back off the station (the game's remove-body button). This closes the
    // window, so CheckForNewGUI announces whatever's underneath next.
    private static void TakeZombie(PorterStationGUI gui)
    {
        try
        {
            ScreenReader.Say("Taking zombie back", interrupt: true);
            gui.TakeWorker();
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[PORTER] take zombie failed: {ex.Message}");
        }
    }
}
