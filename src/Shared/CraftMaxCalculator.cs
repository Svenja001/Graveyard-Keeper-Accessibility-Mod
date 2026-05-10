using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Shared;

internal static class CraftMaxCalculator
{
    internal sealed class CraftInfo
    {
        public int Min;
        public bool IsMultiQualCraft;
        public List<int> NotCraftable = [];
    }

    // Computes the maximum craftable amount for a recipe, correctly handling
    // multi-quality (starred) ingredients stored per-tier as "id:1"/"id:2"/"id:3".
    // When autoSelectHighestQuality is true, mutates __instance._multiquality_ids
    // to lock in the preferred star tier and returns that tier's max — used by both
    // QueueEverything (auto-max on craft GUI open) and MaxButtonsRedux (Max button)
    // so pressing Max mid-window reproduces what the auto-max would set. Pass false
    // for a true read-only mixed-tier calculation if a caller ever needs that.
    internal static CraftInfo Calculate(CraftItemGUI __instance, MultiInventory multiInventory, bool autoSelectHighestQuality)
    {
        var info = new CraftInfo();
        if (__instance == null || multiInventory == null || __instance.current_craft == null)
        {
            return info;
        }

        List<int> craftable = [];
        List<int> notCraftable = [];
        var isMultiQualCraft = false;
        var bCraftable = 0;
        var sCraftable = 0;
        var gCraftable = 0;

        var firstNeedId = __instance.current_craft.needs.Count > 0 ? __instance.current_craft.needs[0].id : null;
        var allSameItem = __instance.current_craft.needs.Count > 1 &&
                          __instance.current_craft.needs.All(n => n.id == firstNeedId);
        var sameItemHandled = false;

        for (var i = 0; i < __instance._multiquality_ids.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(__instance._multiquality_ids[i]))
            {
                var itemCount = multiInventory.GetTotalCount(__instance.current_craft.needs[i].id);
                var itemNeed = __instance.current_craft.needs[i].value;
                var itemCraftable = itemCount / itemNeed;
                if (itemCraftable != 0)
                {
                    craftable.Add(itemCraftable);
                }
                else
                {
                    notCraftable.Add(itemCraftable);
                }
            }
            else
            {
                isMultiQualCraft = true;
                var itemID = __instance.current_craft.needs[i].id;
                var bStarItem = multiInventory.GetTotalCount(itemID + ":1");
                var sStarItem = multiInventory.GetTotalCount(itemID + ":2");
                var gStarItem = multiInventory.GetTotalCount(itemID + ":3");

                if (allSameItem)
                {
                    if (!sameItemHandled)
                    {
                        sameItemHandled = true;
                        var totalNeeded = __instance.current_craft.needs.Sum(n => n.value);

                        var gMaxCrafts = gStarItem / totalNeeded;
                        var sMaxCrafts = sStarItem / totalNeeded;
                        var bMaxCrafts = bStarItem / totalNeeded;
                        var totalAvailable = bStarItem + sStarItem + gStarItem;
                        var mixedMaxCrafts = totalAvailable / totalNeeded;

                        int maxCrafts;
                        string chosenTierSuffix = null;

                        if (autoSelectHighestQuality)
                        {
                            if (gMaxCrafts > 0) { maxCrafts = gMaxCrafts; chosenTierSuffix = ":3"; }
                            else if (sMaxCrafts > 0) { maxCrafts = sMaxCrafts; chosenTierSuffix = ":2"; }
                            else if (bMaxCrafts > 0) { maxCrafts = bMaxCrafts; chosenTierSuffix = ":1"; }
                            else { maxCrafts = mixedMaxCrafts; }
                        }
                        else
                        {
                            maxCrafts = mixedMaxCrafts;
                        }

                        // Without this, _multiquality_ids stays at whatever Draw() initialised
                        // (typically the bronze variant), so a player holding only silver/gold
                        // gets "not_enough_resources" at Y-press even though the calculator
                        // returned a positive max. Mirrors the per-slot branch below for the
                        // multi-slot-same-item recipes (e.g. Greek salad: 2x fish).
                        if (chosenTierSuffix != null)
                        {
                            for (var slot = 0; slot < __instance._multiquality_ids.Count; slot++)
                            {
                                if (!string.IsNullOrWhiteSpace(__instance._multiquality_ids[slot]))
                                {
                                    __instance._multiquality_ids[slot] = firstNeedId + chosenTierSuffix;
                                }
                            }
                        }

                        if (maxCrafts > 0)
                        {
                            craftable.Add(maxCrafts);
                        }
                        else
                        {
                            notCraftable.Add(0);
                        }
                    }
                }
                else
                {
                    var itemValueNeeded = __instance.current_craft.needs[i].value;
                    bCraftable = bStarItem / itemValueNeeded;
                    sCraftable = sStarItem / itemValueNeeded;
                    gCraftable = gStarItem / itemValueNeeded;

                    if (bCraftable != 0) { craftable.Add(bCraftable); }
                    if (sCraftable != 0) { craftable.Add(sCraftable); }
                    if (gCraftable != 0) { craftable.Add(gCraftable); }

                    if (bCraftable + sCraftable + gCraftable > 0)
                    {
                        if (autoSelectHighestQuality)
                        {
                            if (gCraftable > 0)
                            {
                                __instance._multiquality_ids[i] = itemID + ":3";
                            }
                            else if (sCraftable > 0)
                            {
                                __instance._multiquality_ids[i] = itemID + ":2";
                            }
                            else
                            {
                                __instance._multiquality_ids[i] = itemID + ":1";
                            }
                        }
                    }
                }
            }
        }

        var m1 = craftable.Count > 0 ? craftable.Min() : 0;
        var multiMin = sameItemHandled ? 0 : Mathf.Max(bCraftable, sCraftable, gCraftable);
        var min = multiMin <= 0 ? m1 : Math.Min(m1, multiMin);

        info.Min = min;
        info.IsMultiQualCraft = isMultiQualCraft;
        info.NotCraftable = notCraftable;
        return info;
    }
}
