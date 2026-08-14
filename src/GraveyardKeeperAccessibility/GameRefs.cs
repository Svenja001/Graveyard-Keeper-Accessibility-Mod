namespace GraveyardKeeperAccessibility;

// Guarded access to game state that is unsafe to read at the wrong moment.
//
// MainGame.dungeon_root is a *poisoning* property, the same trap as has_removal_craft:
//
//     public TextureDrawer dungeon_root {
//         get {
//             if (!_dungeon_root_set) {
//                 _dungeon_root = FindObjectOfType<TextureDrawer>();
//                 _dungeon_root_set = true;          // cached forever, even if null
//             }
//             return _dungeon_root;
//         }
//     }
//
// FindObjectOfType does not see components on inactive GameObjects. Vanilla only ever touches
// this property when a dungeon is being entered — by which time the root is live — so the null
// case never comes up in the base game. The mod, though, polls it every tick from the moment the
// world loads (dungeon transition detection, x-ray filtering, the L exit key, the Q clock). Read
// it one tick too early and `_dungeon_root_set` latches with a null, permanently, for the rest of
// the session.
//
// From then on EVERY vanilla `MainGame.me.dungeon_root.…` throws. That is what stranded the
// player on 2026-08-14: the Ruhestein's StoneTeleport flowscript disables the player, then hits
// Flow_TryGetCurrentDungeonLevelNum, which dereferences dungeon_root with no null check. The
// NullReferenceException killed the graph before it could re-enable the player, so the teleport
// never happened and input never came back.
//
// So: never touch the property before the game has resolved it, and heal the cache if it is
// already poisoned.
internal static class GameRefs
{
    private static FieldInfo _rootField;
    private static FieldInfo _rootSetField;
    private static TextureDrawer _ownCache;
    private static bool _repairLogged;
    private static bool _seedLogged;
    private static bool _diagLogged;

    /// <summary>
    /// The dungeon root, resolved without ever letting the game cache a null for it.
    /// Returns null when there is no dungeon root in the scene. Use this everywhere instead of
    /// MainGame.me.dungeon_root.
    /// </summary>
    internal static TextureDrawer DungeonRoot()
    {
        var game = MainGame.me;
        if (game == null) return null;

        try
        {
            _rootSetField ??= AccessTools.Field(typeof(MainGame), "_dungeon_root_set");
            _rootField ??= AccessTools.Field(typeof(MainGame), "_dungeon_root");

            // Can't inspect the cache — then we must not risk filling it either. Losing a dungeon
            // announcement is nothing next to wedging the player out of the game.
            if (_rootSetField == null || _rootField == null) return null;

            var alreadySet = (bool)_rootSetField.GetValue(game);
            LogFirstRead(alreadySet);

            // The game hasn't resolved it yet.
            //
            // Before the world scene loads there is no root to find at all — answer null and leave
            // the cache alone, so the game still gets to resolve it later itself.
            //
            // Once the world IS up, seed the cache with the real root instead of leaving vanilla to
            // its own FindObjectOfType. Vanilla's lookup only sees *active* objects, so if the root
            // is ever inactive at the moment the game first asks — which is precisely the teleport
            // stone's situation, asking about dungeons while standing in the overworld — vanilla
            // would latch a null of its own making and the mod could only repair it a tick too
            // late, after StoneTeleport had already thrown. Seeding a known-correct value closes
            // that window; it writes exactly what vanilla wants there, just from a lookup that also
            // sees inactive objects.
            if (!alreadySet)
            {
                var resolved = FindInScene();
                if (resolved == null) return null;

                _rootField.SetValue(game, resolved);
                _rootSetField.SetValue(game, true);
                if (!_seedLogged)
                {
                    _seedLogged = true;
                    Plugin.Log.LogInfo($"[DUNGEON] seeded MainGame.dungeon_root (active={resolved.gameObject.activeInHierarchy})");
                }
                return resolved;
            }

            var cached = _rootField.GetValue(game) as TextureDrawer;
            if (cached != null) return cached;

            // Cache is already poisoned (by an earlier build of this mod, another mod, or a bad
            // load order). Put the real root back so vanilla code that dereferences it blind —
            // the teleport stone above all — stops throwing.
            var found = FindInScene();
            if (found != null)
            {
                _rootField.SetValue(game, found);
                if (!_repairLogged)
                {
                    _repairLogged = true;
                    Plugin.Log.LogWarning("[DUNGEON] MainGame.dungeon_root was cached as null; restored it");
                }
            }
            return found;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[DUNGEON] dungeon root lookup failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// One line, once per session, recording the state at the moment the mod first wants the
    /// dungeon root. This is the evidence that says whether the poisoning theory is right:
    /// `game_would_find=False` at a point where the game had not resolved it yet means an
    /// unguarded read here WOULD have latched a null. FindObjectOfType is safe to call ourselves
    /// — it only reads the scene, it does not touch MainGame's cache.
    /// </summary>
    private static void LogFirstRead(bool alreadySet)
    {
        if (_diagLogged) return;
        _diagLogged = true;

        try
        {
            var wouldFind = UnityEngine.Object.FindObjectOfType<TextureDrawer>();
            var weFind = FindInScene();
            Plugin.Log.LogInfo(
                $"[DUNGEON] first read: game_cache_set={alreadySet}, " +
                $"game_would_find={wouldFind != null}, we_find={weFind != null}, " +
                $"root_active={(weFind != null && weFind.gameObject.activeInHierarchy)}");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[DUNGEON] first-read diagnostic failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Find the dungeon root ourselves. FindObjectsOfTypeAll sees inactive objects too — which is
    /// the whole point — but it also returns prefab and asset copies, so keep only a real
    /// scene instance.
    /// </summary>
    private static TextureDrawer FindInScene()
    {
        if (_ownCache != null) return _ownCache;

        try
        {
            foreach (var drawer in Resources.FindObjectsOfTypeAll<TextureDrawer>())
            {
                if (drawer == null) continue;
                if (drawer.hideFlags != HideFlags.None) continue;
                if (!drawer.gameObject.scene.IsValid()) continue;
                _ownCache = drawer;
                return drawer;
            }
        }
        catch { }

        return null;
    }
}
