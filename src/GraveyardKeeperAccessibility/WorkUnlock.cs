namespace GraveyardKeeperAccessibility;

/// <summary>
/// Whether a resource node can be worked AT ALL YET, and which technology opens it.
///
/// Every tree, rock, ore vein and mushroom in the game sits in one of thirteen "object groups"
/// (t_wood_small, t_wood_big, t_stone, t_iron_ore_1, t_mushroom, …) and each group is unlocked by
/// one technology. Until then the game simply refuses the work — <c>HPActionComponent</c> puts a
/// "(tech_locked)" token on the work prompt, which is a picture a blind player never sees. The
/// result was a player walking to tree after tree and being told, once there, that they still need
/// a technology.
///
/// The check is the game's own (<c>GameSave.IsWorkAvailible</c>: the object's first group must be
/// in <c>unlocked_works</c>), so this can never disagree with what the game will actually allow.
/// The technology's name comes from the tech that lists that group in its <c>works</c> — the same
/// mapping the tech tree screen is built from, so "Woodcutter" for big trees, "Gathering" for
/// mushrooms and berries, and so on, without a hand-written table.
/// </summary>
internal static class WorkUnlock
{
    private static ManualLogSource _log;

    // Object group id -> the localized name of the tech that unlocks it. Built once from the
    // balance data, which never changes during a session.
    private static Dictionary<string, string> _techByGroup;

    internal static void Init(ManualLogSource log)
    {
        _log = log;
    }

    /// <summary>Drop the cached tech names — they are localized. Called on a language change.</summary>
    internal static void Forget()
    {
        _techByGroup = null;
    }

    /// <summary>
    /// True when the player has not unlocked the work this object needs, so pressing F on it will
    /// be refused. False for everything that can be worked, and for everything that is not a
    /// tool-worked node at all.
    /// </summary>
    internal static bool IsLocked(ObjectDefinition def)
    {
        try
        {
            if (def == null || !def.need_unlock_work) return false;
            var save = MainGame.me?.save;
            if (save == null) return false;
            return !save.IsWorkAvailible(def);
        }
        catch
        {
            // Never let a readout claim "locked" because a lookup threw.
            return false;
        }
    }

    /// <summary>
    /// "Technology Woodcutter needed" for a node the player cannot work yet, or null when they can.
    /// Falls back to a plain "not unlocked yet" when no tech claims the group — better to say the
    /// work is closed than to say nothing, since the player would otherwise walk there for nothing.
    /// </summary>
    internal static string LockedNote(ObjectDefinition def)
    {
        if (!IsLocked(def)) return null;

        var tech = TechFor(def);
        return string.IsNullOrEmpty(tech)
            ? Loc.Get("work.locked")
            : Loc.Fmt("work.tech_locked", tech);
    }

    /// <summary>
    /// <paramref name="label"/> with "technology X needed" appended when this node cannot be worked
    /// yet, unchanged otherwise.
    /// </summary>
    internal static string With(string label, ObjectDefinition def)
    {
        var note = LockedNote(def);
        return string.IsNullOrEmpty(note) ? label : Loc.Fmt("work.with_note", label, note);
    }

    /// <summary>Localized name of the technology that unlocks this object's work, or null.</summary>
    private static string TechFor(ObjectDefinition def)
    {
        try
        {
            var groups = def?.object_groups;
            if (groups == null || groups.Count == 0) return null;
            var groupId = groups[0]?.id;
            if (string.IsNullOrEmpty(groupId)) return null;

            BuildTechMap();
            return _techByGroup.TryGetValue(groupId, out var name) ? name : null;
        }
        catch
        {
            return null;
        }
    }

    private static void BuildTechMap()
    {
        if (_techByGroup != null) return;

        _techByGroup = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var tech in GameBalance.me.techs_data)
            {
                if (tech?.works == null) continue;
                // The tech id IS its localization key, the same way the tech tree reader names one
                // (see MainMenuPatches.TechLabel).
                var name = ScreenReader.StripNguiCodes(GJL.L(tech.id) ?? "").Trim();
                if (string.IsNullOrEmpty(name) || name.IndexOf('!') >= 0) name = tech.id;

                foreach (var work in tech.works)
                {
                    if (string.IsNullOrEmpty(work) || _techByGroup.ContainsKey(work)) continue;
                    _techByGroup[work] = name;
                }
            }
            _log?.LogInfo($"[WORK] {_techByGroup.Count} object group(s) mapped to their technology");
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[WORK] Could not map works to techs: {ex.Message}");
        }
    }
}
