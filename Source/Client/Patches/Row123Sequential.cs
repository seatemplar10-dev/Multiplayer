using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using RimWorld.Planet;
using Gilzoide.ManagedJobs;
using Unity.Jobs;
using Verse;

namespace Multiplayer.Client.Patches
{
    // FIXES row 123 (class C16): worker-to-worker races inside the simulation's own parallel loops. In a session these run
    // sequentially, in list/index order, on the calling (simulation) thread. Off with -mplaboff=123 (the loops stay parallel).
    //
    // Why transpilers and not prefixes with a copy of the vanilla body: the three bodies use private fields, closures and
    // profiler scopes (GatherData also drives cellDeltas/lastGatherTick); a copy would drift from the game. A transpiler
    // swaps only the Parallel / Schedule call for a helper that checks the gate at run time, so other mods' patches on these
    // methods (Vehicles, RimFridge, QuestEditor, ...) still run and still see the vanilla body, just on one thread.
    //
    // (a) PathFinderMapData.GatherData: decomp17_full/Assembly-CSharp/Verse/PathFinderMapData.cs:218 and the recompute loop
    //     right after it: Parallel.ForEach(sources, ...) over the 13 sources (list built at :78-82). Multiplayer's own
    //     AreaSource_Patch (AreaSource.cs:22-24) swaps the shared field map.areaManager inside a source's prefix, so another
    //     pool thread reading it sees the swapped manager.
    // (b) PathFinder.ScheduleBatchedPathJobs: Verse/PathFinder.cs:391 schedules GetDoorBlockedJob(..).Schedule(handle) on
    //     workers (door jobs call mods: Rand.Chance via CustomDoor.PawnCanOpen, RimFridge region rebuild). FindPathNow
    //     (:584) already runs it inline `.Run()` on the main thread; we do the same: Run inline (the dependency is not
    //     completed, see DoorScheduleGate), return the same handle. PathFinderJob (:392-393) stays scheduled and parallel.
    // (c) FastTileFinder.RegenerateCache: RimWorld.Planet/FastTileFinder.cs:580 Parallel.For(0, Layer.TilesCount, ...).
    static class Row123
    {
        public static bool Seq => Multiplayer.Client != null && !LabOff.Row(123);

        public static ParallelLoopResult ForEachGate<T>(IEnumerable<T> source, Action<T> body)
        {
            if (!Seq) return Parallel.ForEach(source, body);
            foreach (var item in source) body(item);
            return default;
        }

        public static ParallelLoopResult ForGate(int from, int to, Action<int> body)
        {
            if (!Seq) return Parallel.For(from, to, body);
            for (int i = from; i < to; i++) body(i);
            return default;
        }

        public static JobHandle DoorScheduleGate(ref ManagedJob job, JobHandle dependsOn)
        {
            if (!Seq) return job.Schedule(dependsOn);
            // Do not Complete dependsOn: on the 2nd+ request of a batch it is the previous PathFinderJob (PathFinder.cs:394),
            // and the door job writes only its own state and never reads it. Completing would run A* serially.
            // Log like WaylithJobRuntime.MarkFaulted so one faulted door job does not drop the remaining requests.
            try
            {
                job.Run();
            }
            catch (Exception e)
            {
                Log.Error($"Multiplayer: Row123 inline PathGridDoorsBlockedJob faulted: {e}");
            }
            return dependsOn;
        }

        public static IEnumerable<CodeInstruction> ReplaceCalls(IEnumerable<CodeInstruction> insts, Func<MethodInfo, MethodInfo> map, int expected, string label)
        {
            var result = new List<CodeInstruction>();
            int replaced = 0;
            foreach (var inst in insts)
            {
                if ((inst.opcode == OpCodes.Call || inst.opcode == OpCodes.Callvirt) && inst.operand is MethodInfo mi && map(mi) is { } repl)
                {
                    var n = new CodeInstruction(OpCodes.Call, repl);
                    n.labels.AddRange(inst.labels);
                    n.blocks.AddRange(inst.blocks);
                    result.Add(n);
                    replaced++;
                }
                else result.Add(inst);
            }
            if (replaced != expected)
            {
                Log.Error($"Multiplayer: Row123 transpiler for {label} replaced {replaced} call(s), expected {expected} (FIXES row 123 is not applied)");
                Multiplayer.loadingErrors = true;
            }
            return result;
        }
    }

    [HarmonyPatch(typeof(PathFinderMapData), nameof(PathFinderMapData.GatherData))]
    static class Row123_GatherData
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts) =>
            Row123.ReplaceCalls(insts, mi =>
                mi.DeclaringType == typeof(Parallel) && mi.Name == nameof(Parallel.ForEach) && mi.IsGenericMethod
                && mi.GetParameters() is { Length: 2 } p
                && p[0].ParameterType.IsGenericType && p[0].ParameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                && p[1].ParameterType.IsGenericType && p[1].ParameterType.GetGenericTypeDefinition() == typeof(Action<>)
                    ? AccessTools.Method(typeof(Row123), nameof(Row123.ForEachGate)).MakeGenericMethod(mi.GetGenericArguments())
                    : null,
                2, "PathFinderMapData.GatherData");
    }

    [HarmonyPatch(typeof(PathFinder), "ScheduleBatchedPathJobs")]
    static class Row123_DoorJobs
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts) =>
            Row123.ReplaceCalls(insts, mi =>
                mi.DeclaringType == typeof(ManagedJob) && mi.Name == nameof(ManagedJob.Schedule)
                && mi.GetParameters() is { Length: 1 } p && p[0].ParameterType == typeof(JobHandle)
                    ? AccessTools.Method(typeof(Row123), nameof(Row123.DoorScheduleGate))
                    : null,
                1, "PathFinder.ScheduleBatchedPathJobs");
    }

    [HarmonyPatch(typeof(FastTileFinder), nameof(FastTileFinder.RegenerateCache))]
    static class Row123_FastTileFinder
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts) =>
            Row123.ReplaceCalls(insts, mi =>
                mi.DeclaringType == typeof(Parallel) && mi.Name == nameof(Parallel.For)
                && mi.GetParameters() is { Length: 3 } p
                && p[0].ParameterType == typeof(int) && p[1].ParameterType == typeof(int) && p[2].ParameterType == typeof(Action<int>)
                    ? AccessTools.Method(typeof(Row123), nameof(Row123.ForGate))
                    : null,
                1, "FastTileFinder.RegenerateCache");
    }
}
