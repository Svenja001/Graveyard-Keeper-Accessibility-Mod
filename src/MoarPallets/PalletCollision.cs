namespace MoarPallets;

internal static class PalletCollision
{
    private const string PalletObjId = "box_pallet";

    // Update walk-through on every pallet in the world. The option value doubles as the
    // ignore flag, so switching it off puts collision back in the same pass.
    internal static void RefreshAll()
    {
        if (!MainGame.game_started || MainGame.me == null || !MainGame.me.player) return;

        var playerCollider = MainGame.me.player.GetComponentInChildren<CircleCollider2D>();
        if (!playerCollider) return;

        var pallets = WorldMap.GetWorldGameObjectsByObjId(PalletObjId);
        if (pallets == null || pallets.Count == 0) return;

        foreach (var pallet in pallets)
        {
            Apply(pallet, playerCollider);
        }

        if (Plugin.Debug.Value)
        {
            Plugin.Log.LogInfo($"Pallet walk-through {(Plugin.WalkThroughPallets.Value ? "on" : "off")} for {pallets.Count} pallet(s).");
        }
    }

    // One freshly placed pallet - look the player collider up ourselves.
    internal static void Apply(WorldGameObject pallet)
    {
        if (MainGame.me == null || !MainGame.me.player) return;
        var playerCollider = MainGame.me.player.GetComponentInChildren<CircleCollider2D>();
        if (!playerCollider) return;
        Apply(pallet, playerCollider);
    }

    internal static void Apply(WorldGameObject pallet, Collider2D playerCollider)
    {
        if (!pallet || !playerCollider) return;

        var ignore = Plugin.WalkThroughPallets.Value;
        foreach (var col in pallet.GetComponentsInChildren<Collider2D>(true))
        {
            if (!col) continue;
            Physics2D.IgnoreCollision(col, playerCollider, ignore);
        }
    }
}
