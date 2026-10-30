using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace Multiplayer.Client.Patches
{
    // FIXES row 117 family (C15 neighbour): a PathFinder.FindPathNow call made from interface code on ONE PC changes PathFinder
    // state that the next simulated PathFinderTick on that PC reads. Callers seen in the 1.7 lab mod list: vanilla
    // FloatMenuOptionProvider_WorkGivers.cs:126 -> WorkGiver_Tend.HasJobOnThing -> SmartMedicine FieldTendingUtility.cs:69 /
    // Settings.cs:187,191; CE JobGiverUtils_Reload.cs:215; Achtung ForcedJob.cs:283. Evidence (vanilla 1.7.4982 decompile,
    // _lanes\live29\row117\decomp\Verse):
    //   1. PathFinder.cs:571 mapData.GatherData -> PathFinderMapData.cs:238-240 clears cellDeltas and stamps lastGatherTick.
    //      SetMapTime.cs (TimeSnapshot.GetAndSetFromMap) runs float menus at ticksGameInt = mapTicks, which is the TicksGame of
    //      the next MapPreTick (AsyncTimeComp.Tick: mapTicks++ after MapPreTick). So the next PathFinderTick sees
    //      lastGatherTick == TicksGame (PathFinderMapData.cs:198), skips the gather and RecycleGridJobData, and builds its grids
    //      from sources that miss the deltas of the commands executed since - on that PC only. The UI gather also adds the UI
    //      pawn's faction to FactionSource (FactionSource.cs:113-127) on that PC only.
    //   2. PathFinder.cs:578 ScheduleGridJob -> :445 gridJobLookup[query] keeps a grid built from the UI pawn's faction costs,
    //      drafted perceptual cost and lord walk grid (PathFinderMapData.cs:266, 272-279, 280-288). The key (PathFinder.cs:114)
    //      has none of these, so a sim request with the same key in the next tick reuses that grid on that PC only.
    //   3. PathFinder.cs:619-639 EnsureDoorsPawnsCached: cachedTick == TicksGame makes the next PathFinderTick reuse the door,
    //      pawn and cost provider lists (and door SnapshotWillCloseSoonForJobs, :636) taken at UI time.
    // Fix: while Multiplayer.InInterface (not ticking, not executing commands) on the main thread, FindPathNow runs isolated:
    //   - GatherData is skipped (returns false): the UI path uses the sources as of the last sim gather. That data is complete
    //     but may miss changes since that gather; only this PC's interface sees the result, so stale is safe. If no gather has
    //     run yet (lastGatherTick < 0: fresh load, or the map was fogged) the call returns PawnPath.NotFound instead, because
    //     the sources may hold no data. A faction with no FactionSource data gets the zero-cost array (FactionSource.cs:23-33).
    //   - An allowed area that AreaSource has not seen would throw KeyNotFoundException (AreaSource.cs:33-36,
    //     PathFinderMapData.cs:289): the call returns PawnPath.NotFound instead.
    //   - The finalizer completes and returns to gridJobOutputs every grid job the call added, removes their gridJobLookup keys,
    //     and restores cachedTick, the three cached lists and the doors' willCloseSoonForJobs values.
    //   - 4. map.avoidGrid (hostile pawns, PawnUtility.cs:462-465) regenerates when dirty on Grid access (AvoidGrid.cs:19-28,
    //     PathFinderMapData.cs:291). If it was dirty, its old bytes and the dirty flag are put back.
    //   - Cost: every interface call now builds its own grid and door/pawn cache (vanilla reused them until the next tick).
    //   - A nested FindPathNow on another map (mod code inside the call) runs unpatched; GatherData is skipped only for the
    //     map of the isolated call and only while Multiplayer.InInterface.
    //   - PawnPathPool (EmitPath) order and pathsCreated can differ per PC: PawnPath.Initialize overwrites the content.
    //   Pool queues (path states, path outputs, grid outputs) can change order or count: every user overwrites the whole buffer
    //   (PathGridJob.cs:79-90, PathGridDoorsBlockedJob.Execute Clear(), PathFinderJob path.Clear(), MakeDestination
    //   excluded.Clear()), so buffer identity has no effect on results.
    // Threads: in a session no path job is in flight between ticks (PathJobsComplete completes them in PathFinderTick,
    // AsyncTimeComp.Tick again after MapPreTick). The prefix still calls ForceCompleteScheduledJobs before it copies anything,
    // and FindPathNow completes or runs its own jobs synchronously (PathFinder.cs:570, 583, 584, 587).
    // Single player, sim calls, ticking and command execution: unchanged (the prefix returns true at once).
    // Not covered here: PathRequest.Validate -> pawn.CanReach (reachability cache), and side effects of mod code the call runs
    // (LordJob.GetWalkGrid, IPathGridCustomizer.GetOffsetGrid, IPathFindCostProvider.PathFindCostFor). A throw after the pools
    // are dequeued (PathFinder.cs:581-597) leaks those buffers, as in vanilla.
    // Status: [unverified] compiled only, not run in game; needs the red lab test first.
    [HarmonyPatch(typeof(PathFinder), nameof(PathFinder.FindPathNow),
        new[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(TraverseParms), typeof(PathFinderCostTuning?), typeof(PathEndMode), typeof(PathRequest.IPathGridCustomizer) })]
    static class PathFinderUiIsolation
    {
        static int depth;
        static PathFinderMapData activeMapData;

        // GatherData is skipped only for the map of the isolated call, and only while still in interface code
        public static bool SkipGather(PathFinderMapData mapData) =>
            depth > 0 && mapData == activeMapData && Multiplayer.InInterface;

        static AvoidGrid savedAvoidGrid;
        static byte[] savedAvoidGridBytes;

        static int savedCachedTick;
        static bool cacheMayRefresh;
        static int savedGridJobCount;
        static readonly List<IPathFindCostProvider> savedProviders = new();
        static readonly List<Thing> savedDoors = new();
        static readonly List<Pawn> savedPawns = new();
        static readonly List<Building_Door> savedSnapshotDoors = new();
        static readonly List<bool> savedSnapshotValues = new();
        static readonly HashSet<PathFinder.MapGridRequest> savedGridKeys = new();
        static readonly List<PathFinder.MapGridRequest> tmpAddedKeys = new();

        [HarmonyPriority(Priority.First)]
        static bool Prefix(PathFinder __instance, TraverseParms traverseParms, ref PawnPath __result, ref bool __state)
        {
            __state = false;
            if (depth > 0 || __instance.disposed || !Multiplayer.InInterface || !UnityData.IsInMainThread) return true;

            PathFinderMapData mapData = __instance.mapData;
            if (mapData.lastGatherTick < 0)
            {
                __result = PawnPath.NotFound;
                return false;
            }

            // Same call as the PathRequest constructor (PathRequest.cs:111) makes for the request's area
            Area area = PathUtility.GetAllowedArea(traverseParms.pawn);
            if (area != null && !mapData.areas.areaToBits.ContainsKey(area))
            {
                __result = PawnPath.NotFound;
                return false;
            }

            // A no-op between ticks in a session; makes sure no worker reads the lists copied and restored here
            __instance.ForceCompleteScheduledJobs();

            savedCachedTick = __instance.cachedTick;
            cacheMayRefresh = savedCachedTick != GenTicks.TicksGame;
            if (cacheMayRefresh)
            {
                savedProviders.Clear();
                savedProviders.AddRange(__instance.cachedProviders);
                savedDoors.Clear();
                savedDoors.AddRange(__instance.cachedDoors);
                savedPawns.Clear();
                savedPawns.AddRange(__instance.cachedPawns);
                savedSnapshotDoors.Clear();
                savedSnapshotValues.Clear();
                // The doors EnsureDoorsPawnsCached snapshots (PathFinder.cs:631-638)
                foreach (Thing thing in __instance.map.listerThings.ThingsInGroup(ThingRequestGroup.Door))
                {
                    if (thing is not Building_Door door) continue;
                    savedSnapshotDoors.Add(door);
                    savedSnapshotValues.Add(door.willCloseSoonForJobs);
                }
            }

            savedGridJobCount = __instance.scheduledGridJobs.Count;
            savedGridKeys.Clear();
            foreach (PathFinder.MapGridRequest key in __instance.gridJobLookup.Keys)
                savedGridKeys.Add(key);

            // 4. MapGridRequest.For -> TryGetAvoidGrid gives map.avoidGrid for hostile pawns (PawnUtility.cs:462-465), and
            // ParameterizeGridJob reads avoidGrid.Grid (PathFinderMapData.cs:291), which runs Regenerate when gridDirty
            // (AvoidGrid.cs:19-28). A dirty grid is saved here so the finalizer can put back its content and the dirty flag.
            AvoidGrid avoidGrid = __instance.map.avoidGrid;
            if (avoidGrid != null && avoidGrid.gridDirty && avoidGrid.grid.IsCreated)
            {
                savedAvoidGrid = avoidGrid;
                savedAvoidGridBytes = avoidGrid.grid.ToArray();
            }

            activeMapData = mapData;
            depth++;
            __state = true;
            return true;
        }

        static void Finalizer(PathFinder __instance, bool __state)
        {
            if (!__state) return;
            depth--;
            activeMapData = null;

            // 4. Avoid grid regenerated by this call: old content and dirty flag back, so the sim regenerates it as on other PCs
            if (savedAvoidGrid != null)
            {
                if (!savedAvoidGrid.gridDirty && savedAvoidGrid.grid.IsCreated && savedAvoidGrid.grid.Length == savedAvoidGridBytes.Length)
                {
                    savedAvoidGrid.grid.CopyFrom(savedAvoidGridBytes);
                    savedAvoidGrid.gridDirty = true;
                }
                savedAvoidGrid = null;
                savedAvoidGridBytes = null;
            }

            // 2. Grid jobs this call scheduled (PathFinder.cs:444-445): complete, return the buffer, drop the cache key
            var gridJobs = __instance.scheduledGridJobs;
            if (gridJobs.Count < savedGridJobCount)
                Log.ErrorOnce($"Multiplayer: PathFinderUiIsolation: grid jobs went from {savedGridJobCount} to {gridJobs.Count} in an interface FindPathNow", 117_4982);
            for (int i = savedGridJobCount; i < gridJobs.Count; i++)
            {
                gridJobs[i].handle.Complete();
                __instance.gridJobOutputs.Enqueue(gridJobs[i].output);
            }
            if (gridJobs.Count > savedGridJobCount)
                gridJobs.RemoveRange(savedGridJobCount, gridJobs.Count - savedGridJobCount);

            var lookup = __instance.gridJobLookup;
            if (lookup.Count != savedGridKeys.Count)
            {
                tmpAddedKeys.Clear();
                foreach (PathFinder.MapGridRequest key in lookup.Keys)
                    if (!savedGridKeys.Contains(key))
                        tmpAddedKeys.Add(key);
                foreach (PathFinder.MapGridRequest key in tmpAddedKeys)
                    lookup.Remove(key);
                tmpAddedKeys.Clear();
            }
            savedGridKeys.Clear();

            // 3. Door / pawn / provider cache (PathFinder.cs:619-639)
            if (cacheMayRefresh && __instance.cachedTick != savedCachedTick)
            {
                __instance.cachedTick = savedCachedTick;
                __instance.cachedProviders.Clear();
                __instance.cachedProviders.AddRange(savedProviders);
                __instance.cachedDoors.Clear();
                __instance.cachedDoors.AddRange(savedDoors);
                __instance.cachedPawns.Clear();
                __instance.cachedPawns.AddRange(savedPawns);
                for (int i = 0; i < savedSnapshotDoors.Count; i++)
                    savedSnapshotDoors[i].willCloseSoonForJobs = savedSnapshotValues[i];
            }

            // Drop the references so no pawn, door or map is held after the call
            savedProviders.Clear();
            savedDoors.Clear();
            savedPawns.Clear();
            savedSnapshotDoors.Clear();
            savedSnapshotValues.Clear();
        }
    }

    // 1. While an interface FindPathNow runs (PathFinder.cs:571), the gather is skipped: no deltas cleared (PathFinderMapData.cs:238),
    // lastGatherTick not stamped (:240), no UI faction added to FactionSource. Returning false also skips RecycleGridJobData (:573).
    [HarmonyPatch(typeof(PathFinderMapData), nameof(PathFinderMapData.GatherData))]
    static class PathFinderMapData_GatherData_UiSkip
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix(PathFinderMapData __instance, ref bool __result)
        {
            if (!PathFinderUiIsolation.SkipGather(__instance)) return true;
            __result = false;
            return false;
        }
    }
}
