using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using Verse;

namespace Multiplayer.Client.Factions;

// FIXES row 418: the caravan / map gen / gravship setters below swap the faction's world data (storyWatcher, history, research...)
// together with ofPlayer, as FactionExtensions.PushFaction does. The bare FactionContext.Push changed only ofPlayer, so on a PC of
// another faction Find.StoryWatcher stayed that PC's own and Pawn.SpawnSetup raised the wrong faction's greatestPopulation.
// -mplaboff=418 restores the bare push (lab control arm).
static class CaravanFactionScope
{
    public static void Push(Faction faction)
    {
        if (!LabOff.Row(418)) FactionExtensions.PushFaction(null, faction);
        else FactionContext.Push(faction);
    }

    public static void Pop()
    {
        if (!LabOff.Row(418)) FactionExtensions.PopFaction();
        else FactionContext.Pop();
    }
}

[HarmonyPatch(typeof(SettlementUtility), nameof(SettlementUtility.AttackNow))]
static class AttackNowPatch
{
    static void Prefix(Caravan caravan)
    {
        CaravanFactionScope.Push(caravan.Faction);
    }

    static void Finalizer()
    {
        CaravanFactionScope.Pop();
    }
}

[HarmonyPatch(typeof(GetOrGenerateMapUtility), nameof(GetOrGenerateMapUtility.GetOrGenerateMap), [typeof(PlanetTile), typeof(IntVec3), typeof(WorldObjectDef), typeof(IEnumerable<GenStepWithParams>), typeof(bool)])]
static class MapGenFactionPatch
{
    static void Prefix(PlanetTile tile)
    {
        Faction factionToSet = GetFactionAt(tile);

        if (Multiplayer.Client != null && factionToSet == null)
            Log.Warning($"Couldn't set the faction context for map gen at {tile.tileId}: no world object and no stored faction.");

        CaravanFactionScope.Push(factionToSet);
    }

    private static Faction GetFactionAt(PlanetTile tile)
    {
        var worldObjectsHolder = Find.WorldObjects;

        var mapParent = worldObjectsHolder.MapParentAt(tile);
        if (mapParent != null && mapParent.Faction is { IsPlayer: true })
            return mapParent.Faction;

        // FIXES row 417: first caravan of ANY player faction on the tile, in Caravans order (same on every PC); the old lookup only saw this PC's own faction.
        var caravan = !LabOff.Row(417) ? worldObjectsHolder.Caravans.Find(c => c.Tile == tile && c.Faction is { IsPlayer: true }) : worldObjectsHolder.PlayerControlledCaravanAt(tile);
        if (caravan != null)
            return caravan.Faction;

        var transporters = worldObjectsHolder.TravellingTransporters.Find(t => t.destinationTile == tile && t.Faction is { IsPlayer: true });
        if (transporters != null)
            return transporters.Faction;

        var gravship = worldObjectsHolder.AllWorldObjects.Find(t => t is Gravship g && g.destinationTile == tile && t.Faction is { IsPlayer: true });
        if (gravship != null)
            return gravship.Faction;

        return TileFactionContext.GetFactionForTile(tile);
    }

    static void Finalizer()
    {
        CaravanFactionScope.Pop();
    }
}

[HarmonyPatch(typeof(CaravanEnterMapUtility), nameof(CaravanEnterMapUtility.Enter), new[] { typeof(Caravan), typeof(Map), typeof(Func<Pawn, IntVec3>), typeof(CaravanDropInventoryMode), typeof(bool) })]
static class CaravanEnterFactionPatch
{
    static void Prefix(Caravan caravan)
    {
        CaravanFactionScope.Push(caravan.Faction);
    }

    static void Finalizer()
    {
        CaravanFactionScope.Pop();
    }
}

[HarmonyPatch(typeof(WealthWatcher), nameof(WealthWatcher.ForceRecount))]
static class WealthRecountFactionPatch
{
    static void Prefix(WealthWatcher __instance)
    {
        FactionContext.Push(__instance.map.ParentFaction);
    }

    static void Finalizer()
    {
        FactionContext.Pop();
    }
}

[HarmonyPatch(typeof(FactionIdeosTracker), nameof(FactionIdeosTracker.RecalculateIdeosBasedOnPlayerPawns))]
static class RecalculateFactionIdeosContext
{
    static void Prefix(FactionIdeosTracker __instance)
    {
        FactionContext.Push(__instance.faction);
    }

    static void Finalizer()
    {
        FactionContext.Pop();
    }
}

[HarmonyPatch(typeof(Bill), nameof(Bill.ValidateSettings))]
static class BillValidateSettingsPatch
{
    static void Prefix(Bill __instance)
    {
        if (Multiplayer.Client == null) return;
        FactionContext.Push(__instance.pawnRestriction?.Faction); // todo HostFaction, SlaveFaction?
    }

    static void Finalizer()
    {
        if (Multiplayer.Client == null) return;
        FactionContext.Pop();
    }
}

[HarmonyPatch(typeof(Bill_Production), nameof(Bill_Production.ValidateSettings))]
static class BillProductionValidateSettingsPatch
{
    static void Prefix(Bill_Production __instance, ref Map __state)
    {
        if (Multiplayer.Client == null) return;

        if (__instance.Map != null && __instance.billStack?.billGiver is Thing { Faction: { } faction })
        {
            __instance.Map.PushFaction(faction);
            __state = __instance.Map;
        }
    }

    static void Finalizer(Map __state)
    {
        __state?.PopFaction();
    }
}

[HarmonyPatch(typeof(GravshipUtility), nameof(GravshipUtility.ArriveNewMap))]
static class GravshipArriveNewMapFactionPatch
{
    static void Prefix(Gravship gravship)
    {
        CaravanFactionScope.Push(gravship.Faction);
    }

    static void Finalizer()
    {
        CaravanFactionScope.Pop();
    }
}

// Clean up after map generation is complete
[HarmonyPatch(typeof(MapGenerator), nameof(MapGenerator.GenerateMap))]
static class CleanupTileFactionContext
{
    static void Finalizer(MapParent parent)
    {
        if (parent != null)
            TileFactionContext.ClearTile(parent.Tile);
    }
}
