namespace QueueEverything;

// Installs singular +1 / -1 buttons under CraftItemGUI.full_detailed_go (the vanilla
// expanded multi-quality view). Vanilla never put amount controls there; QE's
// multi-quality features (ForceMultiCraft, AutoMaxMultiQualCrafts, the auto-max in
// Redraw) make those crafts adjustable, so the view needs +1/-1 to be useful.
//
// Min/Max in this view are owned by MaxButtonsRedux — different child names
// ("amount btn min" / "amount btn max"), so the two mods coexist without conflict.
public static class ExpandViewAmountButtons
{
    public static void Install(CraftItemGUI craftItemGUI)
    {
        if (craftItemGUI == null) return;
        if (craftItemGUI.current_craft == null) return;
        if (!craftItemGUI.current_craft.CanCraftMultiple()) return;
        if (craftItemGUI.full_detailed_go == null) return;

        var legacyHost = craftItemGUI.transform.Find("selection frame/amount buttons");
        if (legacyHost == null) return;

        var sourceR = legacyHost.Find("amount btn R");
        var sourceL = legacyHost.Find("amount btn L");
        if (sourceR == null || sourceL == null) return;

        var detailHost = craftItemGUI.full_detailed_go.transform;

        if (detailHost.Find("amount btn R") == null)
        {
            InstallSingular(craftItemGUI, sourceR.gameObject, detailHost, "amount btn R", isPlus: true,
                new Vector3(109f, -10f, 0f));
        }

        if (detailHost.Find("amount btn L") == null)
        {
            InstallSingular(craftItemGUI, sourceL.gameObject, detailHost, "amount btn L", isPlus: false,
                new Vector3(-281.7757f, -10f, 0f));
        }
    }

    private static void InstallSingular(CraftItemGUI craftItemGUI, GameObject sourceGo, Transform host, string buttonName, bool isPlus, Vector3 localPos)
    {
        var sourceUi = sourceGo.GetComponent<UIButton>();

        var btn = Object.Instantiate(sourceGo, host);
        btn.name = buttonName;
        btn.transform.localPosition = localPos;

        var btnUi = btn.GetComponent<UIButton>();
        btnUi.normalSprite2D = sourceUi.normalSprite2D;
        btnUi.hoverSprite2D = sourceUi.hoverSprite2D;
        btnUi.pressedSprite2D = sourceUi.pressedSprite2D;
        btnUi.onClick = [];

        if (isPlus)
        {
            EventDelegate.Add(btnUi.onClick, craftItemGUI.OnAmountPlus);
        }
        else
        {
            EventDelegate.Add(btnUi.onClick, craftItemGUI.OnAmountMinus);
        }
    }
}
