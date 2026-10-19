using HarmonyLib;
using Verse;

namespace Multiplayer.Client.Patches
{
    // 1.7 lab, class C15 (FIXES rows 117, 119): PathFinder.PathFinderTick is the last call of Map.MapPreTick and schedules
    // PathGridDoorsBlockedJob + PathFinderJob on worker threads. The door job reads live main-thread state: CanBlockEver ->
    // Pawn_PathFollower.BestPathHadPawnsInTheWayRecently -> Find.TickManager.TicksGame. AsyncTimeComp.Tick sets ticksGameInt to
    // the next tick right after MapPreTick and completes the jobs only after that, so the job saw tick T or T+1 by thread timing:
    // other pawns blocked different cells and the same pawn got a different path on each PC, with no random draw to show it
    // (two headless replays of one section split 618 ticks after the load). Other mods' MapPreTick postfixes also ran meanwhile.
    // In a session the jobs complete before PathFinderTick returns: nothing on the main thread runs while they do, and every PC
    // reads the same tick. AsyncTimeComp's own ForceCompleteScheduledJobs after MapPreTick stays (a no-op unless this threw).
    [HarmonyPatch(typeof(PathFinder), nameof(PathFinder.PathFinderTick))]
    static class PathJobsComplete
    {
        // Lab mutation test (FIXES row 120): -mplabnoc15 leaves this out, so a replay A/B under chaos scheduling must split.
        // ArbiterLauncher passes it on to the arbiter.
        public const string OffArg = "mplabnoc15";
        public static readonly bool Off = GenCommandLine.CommandLineArgPassed(OffArg);

        static bool Prepare()
        {
            if (!Off) return true;
            Log.Warning($"Multiplayer: -{OffArg}: path jobs are NOT completed inside PathFinderTick (FIXES row 119, C15) - lab mutation test");
            return false;
        }

        [HarmonyPriority(Priority.First)]
        static void Postfix(PathFinder __instance)
        {
            if (Multiplayer.Client == null) return;
            __instance.ForceCompleteScheduledJobs();
        }
    }
}
