namespace MaxButtonsRedux;

public static class MaxButtonCrafting
{
    public static void AddMinAndMaxButtons(CraftItemGUI craftItemGUI, string parentButtonName, string minMaxButtonName, bool isMaximum, WorldGameObject crafteryWgo)
    {
        if (!craftItemGUI.current_craft.CanCraftMultiple())
        {
            return;
        }

        // Primary install: the vanilla amount-button strip lives at "selection frame/amount buttons/".
        // Clone the cloned Min/Max as a sibling of the vanilla L/R there. Visible in normal
        // (collapsed) view; hidden when the row expands because CraftItemGUI.Redraw deactivates
        // selection_frame at line 261 for non-gamepad players.
        var legacyHost = craftItemGUI.transform.Find("selection frame/amount buttons");
        var sourceButton = legacyHost?.Find(parentButtonName);
        if (legacyHost == null || sourceButton == null)
        {
            return;
        }

        if (legacyHost.Find(minMaxButtonName) == null)
        {
            // First-time install: shrink the vanilla source button so the cloned Min/Max sits
            // cleanly below it. Idempotent — guarded by the legacy-host duplicate check above.
            sourceButton.localPosition = new Vector3(sourceButton.localPosition.x, -10f, sourceButton.localPosition.z);
            sourceButton.GetComponent<UI2DSprite>().SetDimensions(26, 26);
            sourceButton.GetComponent<BoxCollider2D>().size = new Vector2(29.4f, 26f);

            InstallClone(craftItemGUI, sourceButton.gameObject, legacyHost, minMaxButtonName, isMaximum, crafteryWgo,
                new Vector3(sourceButton.localPosition.x, -31f, sourceButton.localPosition.z));
        }

        // Secondary install: Min/Max on the bottom row of full_detailed_go (the vanilla expand
        // view, which has no amount controls of its own). The singular +1/-1 on the top row are
        // owned by QueueEverything — installed under the same parent with different child names
        // ("amount btn R/L"), so the two coexist. Gated on QE being loaded: without QE there are
        // no +1/-1 between Min and Max, which would be a confusing partial UI.
        if (craftItemGUI.full_detailed_go != null && Harmony.HasAnyPatches("p1xel8ted.gyk.queueeverything"))
        {
            var detailHost = craftItemGUI.full_detailed_go.transform;
            var sideX = isMaximum ? 109f : -281.7757f;
            const float bottomY = -31.1383f;

            if (detailHost.Find(minMaxButtonName) == null)
            {
                InstallClone(craftItemGUI, sourceButton.gameObject, detailHost, minMaxButtonName, isMaximum, crafteryWgo,
                    new Vector3(sideX, bottomY, 0f));
            }
        }
    }

    private static void InstallClone(CraftItemGUI craftItemGUI, GameObject sourceGo, Transform host, string minMaxButtonName, bool isMaximum, WorldGameObject crafteryWgo, Vector3 localPos)
    {
        var sourceUi = sourceGo.GetComponent<UIButton>();

        var minMaxButton = Object.Instantiate(sourceGo, host);
        minMaxButton.name = minMaxButtonName;
        minMaxButton.transform.localPosition = localPos;

        var minMaxButtonUI = minMaxButton.GetComponent<UIButton>();
        minMaxButtonUI.normalSprite2D = sourceUi.normalSprite2D;
        minMaxButtonUI.hoverSprite2D = sourceUi.hoverSprite2D;
        minMaxButtonUI.pressedSprite2D = sourceUi.pressedSprite2D;
        minMaxButtonUI.onClick = [];

        if (isMaximum)
        {
            EventDelegate.Add(minMaxButtonUI.onClick, delegate { SetMaximumAmount(craftItemGUI, crafteryWgo); });
        }
        else
        {
            EventDelegate.Add(minMaxButtonUI.onClick, delegate { SetMinimumAmount(craftItemGUI); });
        }

        var arrowSpriteTransform = minMaxButton.transform.Find("arrow spr");
        if (arrowSpriteTransform == null)
        {
            return;
        }
        arrowSpriteTransform.name = "arrow spr 1";
        arrowSpriteTransform.localPosition += new Vector3(4f, 0f, 0f);

        CloneAndPositionSprite(arrowSpriteTransform, "arrow spr 2", -4f);
        CloneAndPositionSprite(arrowSpriteTransform, "arrow spr 3", -8f);
    }
    
    private static void CloneAndPositionSprite(Transform spriteTransform, string spriteName, float xOffset)
    {
        var clonedSprite = Object.Instantiate(spriteTransform.gameObject, spriteTransform.parent);
        clonedSprite.name = spriteName;
        clonedSprite.transform.localPosition += new Vector3(xOffset, 0f, 0f);
    }

    internal static void SetMinimumAmount(CraftItemGUI craftItemGUI)
    {
        SetAmount(craftItemGUI, 1);
    }


    internal static void SetMaximumAmount(CraftItemGUI craftItemGUI, WorldGameObject crafteryWgo)
    {
        var maxCraftableFromWgo = 9999;
        var multiInventory = GlobalCraftControlGUI.is_global_control_active
            ? GUIElements.me.craft.multi_inventory
            : MainGame.me.player.GetMultiInventoryForInteraction(null);

        foreach (var neededItemFromWgo in craftItemGUI.craft_definition.needs_from_wgo)
        {
            if (neededItemFromWgo != null && crafteryWgo != null && crafteryWgo.data != null && neededItemFromWgo.id == "fire" && neededItemFromWgo.value > 0)
            {
                maxCraftableFromWgo = crafteryWgo.data.GetTotalCount(neededItemFromWgo.id, true) / neededItemFromWgo.value;
            }

            if (maxCraftableFromWgo > 1) continue;
            SetAmount(craftItemGUI, 1);
            return;
        }

        // autoSelectHighestQuality: true so the Max button reproduces QueueEverything's auto-max
        // semantic (lock _multiquality_ids to the highest available tier and compute that tier's
        // max). Without this, pressing Max after the user changes selection mid-window can return
        // a mixed-tier total that disagrees with what auto-max would have set on open.
        var info = CraftMaxCalculator.Calculate(craftItemGUI, multiInventory, autoSelectHighestQuality: true);
        if (info.NotCraftable.Count > 0 || info.Min <= 0)
        {
            SetAmount(craftItemGUI, 1);
            return;
        }

        var finalMaxCraftable = Math.Min(info.Min, maxCraftableFromWgo);
        finalMaxCraftable = Math.Max(finalMaxCraftable, 1);
        SetAmount(craftItemGUI, finalMaxCraftable);
    }


    private static void SetAmount(CraftItemGUI craftItemGUI, int amount)
    {
        craftItemGUI._amount = amount;
        craftItemGUI.Redraw();
        craftItemGUI.OnOver();
    }
}