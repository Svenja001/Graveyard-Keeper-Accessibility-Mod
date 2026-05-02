namespace RestInPatches.Patches;

[Harmony]
public static class CraftArrowFixPatches
{
    private const string MaxButtonGuid = "p1xel8ted.gyk.maxbuttonsredux";
    private const string QueueEverythingGuid = "p1xel8ted.gyk.queueeverything";
    private const string ArrowSpr = "arrow spr";
    private const string Arr = "arr";

    // Both MBR and QE clone the vanilla L/R amount buttons into the craft GUI; if their
    // postfixes run before this one, they capture the broken arrow sprites and propagate
    // them to every clone. Declaring the order from both sides (HarmonyAfter on theirs,
    // HarmonyBefore here) is belt-and-suspenders — Harmony only needs one side, but the
    // explicit pair survives any future patch-order regressions caused by load order or
    // assembly trimming.
    [HarmonyPostfix]
    [HarmonyBefore(MaxButtonGuid, QueueEverythingGuid)]
    [HarmonyPatch(typeof(CraftGUI), nameof(CraftGUI.Open), typeof(WorldGameObject), typeof(CraftsInventory), typeof(string))]
    public static void CraftGUI_Open_RestoreArrows(CraftGUI __instance)
    {
        RestoreArrowsForAll(__instance);
    }

    [HarmonyPostfix]
    [HarmonyBefore(MaxButtonGuid, QueueEverythingGuid)]
    [HarmonyPatch(typeof(CraftGUI), nameof(CraftGUI.SwitchTab))]
    public static void CraftGUI_SwitchTab_RestoreArrows(CraftGUI __instance)
    {
        RestoreArrowsForAll(__instance);
    }

    private static void RestoreArrowsForAll(CraftGUI craftGui)
    {
        if (!craftGui)
        {
            return;
        }

        foreach (var item in craftGui.GetComponentsInChildren<CraftItemGUI>(true))
        {
            RestoreFromButton(item.btn_amount_plus);
            RestoreFromButton(item.btn_amount_minus);
        }
    }

    private static void RestoreFromButton(UIButton btn)
    {
        if (!btn)
        {
            return;
        }

        var arrow = btn.transform.Find(ArrowSpr);
        if (!arrow)
        {
            return;
        }

        var spr = arrow.GetComponent<UI2DSprite>();
        if (spr && !spr.sprite2D)
        {
            Assign(spr, Plugin.ArrowLeftSprite);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(UIWidget), nameof(UIWidget.CreatePanel))]
    public static void UIWidget_CreatePanel_RestoreArrows(UIWidget __instance)
    {
        if (__instance is not UI2DSprite sprite || sprite.sprite2D)
        {
            return;
        }

        var name = sprite.name;

        if (string.Equals(name, ArrowSpr, StringComparison.Ordinal))
        {
            Assign(sprite, Plugin.ArrowLeftSprite);
            return;
        }

        if (!string.Equals(name, Arr, StringComparison.Ordinal))
        {
            return;
        }

        var craftItem = sprite.GetComponentInParent<CraftItemGUI>();
        if (!craftItem)
        {
            return;
        }

        if (craftItem.full_detailed_go && sprite.transform.IsChildOf(craftItem.full_detailed_go.transform))
        {
            Assign(sprite, Plugin.ArrowUpSprite);
            return;
        }

        if (craftItem.multi_quality_go && sprite.transform.IsChildOf(craftItem.multi_quality_go.transform))
        {
            Assign(sprite, Plugin.ArrowDownSprite);
        }
    }

    private static void Assign(UI2DSprite sprite, Sprite replacement)
    {
        if (!replacement)
        {
            return;
        }

        sprite.sprite2D = replacement;
        sprite.MarkAsChanged();
    }
}