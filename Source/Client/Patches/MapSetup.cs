using System;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Client;

[HarmonyPatch(typeof(MapGenerator), nameof(MapGenerator.GenerateMap))]
public static class MapSetup
{
    public static bool SetupNextMapFromTickZero;

    static void Prefix(ref Action<Map> extraInitBeforeContentGen)
    {
        if (Multiplayer.Client == null) return;
        extraInitBeforeContentGen += map => SetupMap(map);
    }

    public static void SetupMap(Map map, bool usingMapTimeFromSingleplayer = false)
    {
        Log.Message("MP: Setting up map " + map.uniqueID);

        // Initialize and store Multiplayer

        var mapComp = new MultiplayerMapComp(map);
        Multiplayer.game.mapComps.Add(mapComp);

        var async = CreateAsyncTimeCompForMap(map, usingMapTimeFromSingleplayer);
        Multiplayer.game.asyncTimeComps.Add(async);

        // Store all current managers for Faction.OfPlayer
        InitFactionDataFromMap(map, Faction.OfPlayer);

        // Add all other (non Faction.OfPlayer) factions to the map
        foreach (var faction in Find.FactionManager.AllFactions.Where(f => f.IsPlayer))
            if (faction != Faction.OfPlayer)
                InitNewFactionData(map, faction);
    }

    private static AsyncTimeComp CreateAsyncTimeCompForMap(Map map, bool usingMapTimeFromSingleplayer)
    {
        int startingMapTicks;
        int gameStartAbsTick;
        TimeSpeed startingTimeSpeed;

        bool startingMapTimeFromBeginning =
            Multiplayer.GameComp.multifaction &&
            Multiplayer.GameComp.asyncTime &&
            SetupNextMapFromTickZero;

        if (usingMapTimeFromSingleplayer)
        {
            startingMapTicks = Find.TickManager.TicksGame;
            gameStartAbsTick = Find.TickManager.gameStartAbsTick;
            startingTimeSpeed = Find.TickManager.CurTimeSpeed;
        }
        else if (startingMapTimeFromBeginning)
        {
            startingMapTicks = 0;
            gameStartAbsTick = GenTicks.ConfiguredTicksAbsAtGameStart;
            startingTimeSpeed = TimeSpeed.Paused;
        }
        else
        {
            startingMapTicks = Find.Maps.Where(m => m != map).Select(m => m.AsyncTime()?.mapTicks).Max() ?? Find.TickManager.TicksGame;
            gameStartAbsTick = Find.TickManager.gameStartAbsTick;
            startingTimeSpeed = TimeSpeed.Paused;
        }

        if (!Multiplayer.GameComp.asyncTime)
            startingTimeSpeed = Find.TickManager.CurTimeSpeed;

        var asyncTimeCompForMap = new AsyncTimeComp(map, gameStartAbsTick)
        {
            mapTicks = startingMapTicks,
            DesiredTimeSpeed = startingTimeSpeed
        };

        SetupNextMapFromTickZero = false;

        return asyncTimeCompForMap;
    }

    private static void InitFactionDataFromMap(Map map, Faction f)
    {
        var mapComp = map.MpComp();
        mapComp.factionData[f.loadID] = FactionMapData.NewFromMap(map, f.loadID);

        var customData = mapComp.customFactionData[f.loadID] = CustomFactionMapData.New(f.loadID, map);

        foreach (var t in map.listerThings.AllThings)
            if (t is ThingWithComps tc &&
                tc.GetComp<CompForbiddable>() is { forbiddenInt: false })
                customData.unforbidden.Add(t);
    }

    public static void InitNewFactionData(Map map, Faction f)
    {
        var mapComp = map.MpComp();

        var data = FactionMapData.New(f.loadID, map);
        mapComp.factionData[f.loadID] = data;

        // 1.7 lab, FIXES row 169 (run FA3): AddStartingAreas runs mod postfixes that read map.areaManager instead of __instance (Vehicles'
        // Patch_Areas.AddVehicleAreas -> map.EnsureAreaInitialized<Area_Road/Area_RoadAvoidal>, TD Enhancement Pack's
        // TryMakeNewAllowed_Patch -> MapComponent_AreaOrder.Notify_Added -> map.areaManager.AllAreas). FactionCreator.CreateFaction
        // calls this from a long event that runs in each PC's OWN faction context, so map.areaManager was the host's own faction
        // on the host and the spectator on a joiner: Road/RoadAvoidal landed in a different faction's manager (2 area ids taken
        // on the joiners only, nextAreaID 46 vs 48) and the area order index was counted in a different manager. Swap in the
        // new faction's manager for the call so the result does not depend on the PC.
        var prevAreaManager = map.areaManager;
        map.areaManager = data.areaManager;
        try
        {
            data.areaManager.AddStartingAreas();
        }
        finally
        {
            map.areaManager = prevAreaManager;
        }

        mapComp.customFactionData[f.loadID] = CustomFactionMapData.New(f.loadID, map);
    }
}
