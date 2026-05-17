namespace BeamMeUpGerry;

public static class Teleport
{
    internal static void TryTeleport(Location chosenLocation)
    {
        if (Plugin.DebugEnabled) Helpers.Log($"[TryTeleport] {chosenLocation.zone}");
        var targetPosition = GetTeleportPosition(chosenLocation);
        if (targetPosition == Vector2.zero) return;

        // Fire the "tp_<area>" quest key before the fade so quest/build gates tied to entering an area still unlock.
        var tag = chosenLocation.teleportPoint;
        if (!tag.IsNullOrWhiteSpace())
        {
            var parts = tag.Split('_');
            if (parts.Length >= 2 && parts[0] == "tp")
            {
                var key = "tp_" + parts[1];
                MainGame.me.save.quests.CheckKeyQuests(key);
                if (Plugin.DebugEnabled) Helpers.Log($"[TryTeleport] CheckKeyQuests {key}");
            }
        }

        MainGame.me.player.components.character.TeleportWithFade(targetPosition,
            middle_delegate: () => Helpers.UpdateEnvironmentPreset(chosenLocation),
            finished_delegate: () => PostPortingWork(chosenLocation));

        LogTeleportationDetails(chosenLocation, targetPosition);
    }

    private static void LogTeleportationDetails(Location chosenLocation, Vector2 targetPosition)
    {
        var logMessage = $"Teleporting to {chosenLocation.zone} at {targetPosition}";
        if (Plugin.DebugEnabled) Helpers.Log(logMessage);
    }

    private static Vector2 GetTeleportPosition(Location chosenLocation)
    {
        if (Plugin.DebugEnabled) Helpers.Log($"[GetTeleportPosition] {chosenLocation.zone} {chosenLocation.teleportPoint} {chosenLocation.coords}");

        if (!chosenLocation.teleportPoint.IsNullOrWhiteSpace())
        {
            var worldGameObject = WorldMap.GetWorldGameObjectByCustomTag(chosenLocation.teleportPoint);
            if (worldGameObject != null)
            {
                if (Plugin.DebugEnabled) Helpers.Log($"[GetTeleportPosition] {worldGameObject.name} - {worldGameObject.grid_pos}");
                return worldGameObject.grid_pos;
            }
        }

        if (chosenLocation.coords != Vector2.zero)
        {
            if (Plugin.DebugEnabled) Helpers.Log($"[GetTeleportPosition] {chosenLocation.coords}");
            return chosenLocation.coords;
        }

        if (Plugin.DebugEnabled) Helpers.Log("[GetTeleportPosition] No valid grid position found. Using player's current position.");
        return MainGame.me.player.grid_pos;
    }



    private static void PostPortingWork(Location chosenLocation)
    {
        // Run the new-zone quest key now so it fires before the player can interact with anything in the destination.
        MainGame.me.player_component?.UpdateZone();

        var gerryAppears = Plugin.GerryAppears.Value;
        var gerryCharges = Plugin.GerryCharges.Value;

        if (gerryAppears && gerryCharges && !chosenLocation.defaultLocation)
        {
            Helpers.SpawnGerry("", Vector3.zero, true);
            return;
        }

        if (gerryAppears && chosenLocation.defaultLocation)
        {
            Helpers.SpawnGerry("", Vector3.zero);
            return;
        }

        if (!gerryAppears && gerryCharges && !chosenLocation.defaultLocation)
        {
            Helpers.TakeMoney(Helpers.MessagePositioning());
        }
    }
}