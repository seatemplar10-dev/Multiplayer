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
    //     (:584) already runs it inline `.Run()` on the main thread; we do the same: Complete the dependency, Run inline,
    //     return the same handle. PathFinderJob (:392-393) stays scheduled and parallel.
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
            dependsOn.Complete();
            job.Run();
            return dependsOn;
        }

        public static IEnumerable<CodeInstruction> ReplaceCalls(IEnumerable<CodeInstruction> insts, Func<MethodInfo, MethodInfo> map)
        {
            foreach (var inst in insts)
            {
                if ((inst.opcode == OpCodes.Call || inst.opcode == OpCodes.Callvirt) && inst.operand is MethodInfo mi && map(mi) is { } repl)
                {
                    var n = new CodeInstruction(OpCodes.Call, repl);
                    n.labels.AddRange(inst.labels);
                    n.blocks.AddRange(inst.blocks);
                    yield return n;
                }
                else yield return inst;
            }
        }
    }

    [HarmonyPatch(typeof(PathFinderMapData), nameof(PathFinderMapData.GatherData))]
    static class Row123_GatherData
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts) =>
            Row123.ReplaceCalls(insts, mi =>
                mi.DeclaringType == typeof(Parallel) && mi.Name == nameof(Parallel.ForEach) && mi.IsGenericMethod
                && mi.GetParameters().Length == 2 && mi.GetParameters()[0].ParameterType.IsGenericType
                && mi.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                    ? AccessTools.Method(typeof(Row123), nameof(Row123.ForEachGate)).MakeGenericMethod(mi.GetGenericArguments())
                    : null);
    }

    [HarmonyPatch(typeof(PathFinder), "ScheduleBatchedPathJobs")]
    static class Row123_DoorJobs
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts) =>
            Row123.ReplaceCalls(insts, mi =>
                mi.DeclaringType == typeof(ManagedJob) && mi.Name == nameof(ManagedJob.Schedule)
                    ? AccessTools.Method(typeof(Row123), nameof(Row123.DoorScheduleGate))
                    : null);
    }

    [HarmonyPatch(typeof(FastTileFinder), nameof(FastTileFinder.RegenerateCache))]
    static class Row123_FastTileFinder
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts) =>
            Row123.ReplaceCalls(insts, mi =>
                mi.DeclaringType == typeof(Parallel) && mi.Name == nameof(Parallel.For)
                && mi.GetParameters().Length == 3 && mi.GetParameters()[0].ParameterType == typeof(int)
                && mi.GetParameters()[2].ParameterType == typeof(Action<int>)
                    ? AccessTools.Method(typeof(Row123), nameof(Row123.ForGate))
                    : null);
    }
}
