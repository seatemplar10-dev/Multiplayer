using HarmonyLib;
using Multiplayer.Client.Factions;
using RimWorld;
using Verse;

namespace Multiplayer.Client
{
    // 1.7 lab (live co-op session 22 Oct 2026, err-vf-landing): in multifaction every player faction has its own DesignationManager
    // per map (FactionMapData.designationManager, swapped in by MultiplayerMapComp when a faction is pushed). Vanilla's "this
    // building / thing is gone" cleanups only reach the manager pushed at that moment:
    //   Building.DeSpawn                              map.designationManager.Notify_BuildingDespawned(this)
    //   Thing.RemoveAllReservationsAndDesignationsOnThis  maps[i].designationManager.RemoveAllDesignationsOn(this)  (Destroy, DeSpawn)
    // so the other factions keep designations on things that no longer exist: stale orders and icons, a "Need miner" alert, and at
    // the next save "Cell-needing designation (Mine target=null) had no cell target. Removing..." (3x at the 22 Oct 20:32 join point:
    // faction 69's thing-target Mine designations from Replace Stuff on rocks that were gone).
    // Both cleanups now reach every faction's manager. Same sim code path on every PC; factionData is a SortedDictionary.
    // RemoveAllDesignationsOn itself is NOT repeated: Designator_Cancel / Hunt / Tame / Plants call it for one player's own orders.
    [HarmonyPatch(typeof(DesignationManager), nameof(DesignationManager.Notify_BuildingDespawned))]
    static class DesignationBuildingDespawnedPatch
    {
        static bool ignore;

        static bool Prefix(DesignationManager __instance, Thing b) =>
            FactionRepeater.Template(
                __instance.map.MpComp()?.factionData, // The template doesn't run if not in MP
                d => d.designationManager.Notify_BuildingDespawned(b),
                __instance.map,
                ref ignore
            );
    }

    [HarmonyPatch(typeof(Thing), "RemoveAllReservationsAndDesignationsOnThis")]
    static class ThingGoneDesignationsAllFactionsPatch
    {
        static void Postfix(Thing __instance)
        {
            if (Multiplayer.Client == null || __instance.def.category == ThingCategory.Mote) return;

            var maps = Find.Maps;
            for (int i = 0; i < maps.Count; i++)
            {
                var map = maps[i];
                var comp = map.MpComp();
                if (comp == null) continue;

                foreach (var (id, data) in comp.factionData)
                {
                    var manager = data.designationManager;
                    // the pushed faction's manager was cleaned by the original; most things carry no designation at all
                    if (manager == null || ReferenceEquals(manager, map.designationManager) || manager.AllDesignationsOn(__instance).Count == 0)
                        continue;

                    map.PushFaction(id);
                    try
                    {
                        manager.RemoveAllDesignationsOn(__instance);
                    }
                    finally
                    {
                        map.PopFaction();
                    }
                }
            }
        }
    }
}
