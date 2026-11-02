using HarmonyLib;
using Multiplayer.Client.Util;
using RimWorld;
using RimWorld.Planet;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Verse;

namespace Multiplayer.Client
{
    [HarmonyPatch(typeof(TileTemperaturesComp.CachedTileTemperatureData))]
    [HarmonyPatch(nameof(TileTemperaturesComp.CachedTileTemperatureData.CheckCache))]
    static class CachedTileTemperatureData_CheckCache
    {
        static void Prefix(PlanetTile ___tile, ref TimeSnapshot? __state)
        {
            if (Multiplayer.Client == null) return;

            Map map = Current.Game.FindMap(___tile);
            if (map == null) return;

            __state = TimeSnapshot.GetAndSetFromMap(map);
        }

        static void Postfix(TimeSnapshot? __state) => __state?.Set();
    }

    [HarmonyPatch(typeof(TileTemperaturesComp), nameof(TileTemperaturesComp.RetrieveCachedData))]
    static class RetrieveCachedData_Patch
    {
        [System.ThreadStatic] static Dictionary<PlanetTile, TileTemperaturesComp.CachedTileTemperatureData> workerEntries;

        static bool Prefix(TileTemperaturesComp __instance, PlanetTile tile, ref TileTemperaturesComp.CachedTileTemperatureData __result)
        {
            // FIXES row 187: Multiplayer.InInterface is one flag for the whole process, so a worker thread (Map Preview generating
            // a world tile's map) that runs while the main thread ticks counts as simulation and added its tile to the simulation's
            // cache and usedSlots on that PC only. usedSlots is the eviction queue (WorldComponentTick drops its first entry every
            // 300 ticks, a dropped entry is rebuilt with a new 60-tick refresh phase), so the PCs' outdoor temperatures could part.
            // On such a thread (ForeignThread: not the joiner's load thread) the caller gets entries of its own thread, which no
            // tick refreshes; they only feed previews.
            if (Multiplayer.Client != null && ForeignThread.Now)
            {
                workerEntries ??= new Dictionary<PlanetTile, TileTemperaturesComp.CachedTileTemperatureData>();
                if (!workerEntries.TryGetValue(tile, out __result))
                    workerEntries[tile] = __result = new TileTemperaturesComp.CachedTileTemperatureData(tile);
                return false;
            }

            if (Multiplayer.InInterface && __instance != Multiplayer.WorldComp.uiTemperatures)
            {
                __result = Multiplayer.WorldComp.uiTemperatures.RetrieveCachedData(tile);
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(TileTemperaturesComp), nameof(TileTemperaturesComp.WorldComponentTick))]
    static class TileTemperaturesTick_Patch
    {
        static void Prefix(TileTemperaturesComp __instance)
        {
            if (Multiplayer.InInterface && __instance != Multiplayer.WorldComp.uiTemperatures)
                Multiplayer.WorldComp.uiTemperatures.WorldComponentTick();
        }
    }

    [HarmonyPatch(typeof(GenTemperature), nameof(GenTemperature.AverageTemperatureAtTileForTwelfth))]
    static class CacheAverageTileTemperature
    {
        static Dictionary<PlanetTile, float[]> averageTileTemps = new Dictionary<PlanetTile, float[]>();

        static bool Prefix(PlanetTile tile, Twelfth twelfth)
        {
            // FIXES row 187: a plain Dictionary, so background threads compute without it
            if (ForeignThread.Now) return true;
            return !averageTileTemps.TryGetValue(tile, out float[] arr) || float.IsNaN(arr[(int)twelfth]);
        }

        static void Postfix(PlanetTile tile, Twelfth twelfth, ref float __result)
        {
            if (ForeignThread.Now) return;
            if (averageTileTemps.TryGetValue(tile, out float[] arr) && !float.IsNaN(arr[(int)twelfth]))
            {
                __result = arr[(int)twelfth];
                return;
            }

            if (arr == null)
                averageTileTemps[tile] = Enumerable.Repeat(float.NaN, 12).ToArray();

            averageTileTemps[tile][(int)twelfth] = __result;
        }

        public static void Clear()
        {
            averageTileTemps.Clear();
        }
    }

    [HarmonyPatch]
    static class ClearTemperatureCache
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PlanetLayer), nameof(PlanetLayer.RawDataToTiles));
            yield return AccessTools.Method(typeof(WorldGenStep_Terrain), nameof(WorldGenStep_Terrain.GenerateFresh));
        }

        static void Postfix() => CacheAverageTileTemperature.Clear();
    }

}
