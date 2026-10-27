using System;
using HarmonyLib;
using Verse;

namespace Multiplayer.Client
{
    // 1.7 lab, FIXES row 252: on the host the global TickManager.ticksGameInt kept map 0's tick (25 behind the world) after a
    // frame-path exception skipped a SetMapTime Postfix restore (rgC6k_1, MP tick 98,185), so WorldPawns' hash-interval gates
    // ticked other pawns than on the client. SetMapTime restores in Finalizers now. This guard catches any other leak:
    // AsyncWorldTimeComp.PreContext calls it before every world tick and world command, a point every PC reaches with the
    // same simulation state, so the repair cannot split PCs. SaveLoad.SaveAndReloadCore calls it before the save of a join point,
    // whose reload would take worldTicks from a leaked time. Nothing has leaked at either point in a healthy session.
    static class WorldTickGuard
    {
        // Lab mutation test: -mplabnoc252 logs a leak but leaves the repair out. -mplab252mut (host only) throws once inside
        // Map.MapUpdate 2,000 MP ticks after a second player is in the session (the SetMapTime Finalizer must restore, so no
        // log) and moves the global time back 25 ticks outside any patch 4,000 MP ticks after it (the guard must log once and
        // repair; with -mplabnoc252 the PCs split).
        public const string OffArg = "mplabnoc252";
        public const string MutationArg = "mplab252mut";
        public static readonly bool Off = GenCommandLine.CommandLineArgPassed(OffArg);
        public static readonly bool Mutation = GenCommandLine.CommandLineArgPassed(MutationArg);
        static bool leakLogged;

        public static void ResetIfLeaked(int worldTicks)
        {
            var tickManager = Find.TickManager;
            if (tickManager.ticksGameInt == worldTicks) return;
            if (!leakLogged)
            {
                leakLogged = true;
                Log.Error($"Multiplayer: global ticksGame {tickManager.ticksGameInt} != world ticks {worldTicks} at MP tick {TickPatch.Timer} - {(Off ? "NOT reset (-" + OffArg + ")" : "reset")} (FIXES row 252, logged once per process)");
            }
            if (!Off) tickManager.ticksGameInt = worldTicks;
        }

        static int armedAt = -1;

        // -mplab252mut: counted from the first frame with 2 or more players in the session (host + the joining client)
        public static bool MutationDue(int after)
        {
            if (Multiplayer.Client == null) return false;
            if (armedAt < 0)
            {
                if (Multiplayer.session.players.Count < 2) return false;
                armedAt = TickPatch.Timer;
                Log.Warning($"-{MutationArg}: armed at MP tick {armedAt} ({Multiplayer.session.players.Count} players)");
            }
            return TickPatch.Timer >= armedAt + after;
        }
    }

    [HarmonyPatch(typeof(Map), nameof(Map.MapUpdate))]
    static class WorldTickGuardMutationThrow
    {
        static bool thrown;

        static bool Prepare()
        {
            if (!WorldTickGuard.Mutation) return false;
            Log.Warning($"Multiplayer: -{WorldTickGuard.MutationArg}: throws once in Map.MapUpdate 2,000 MP ticks after a second player joins and moves the time back 25 ticks 4,000 MP ticks after it (FIXES row 252) - lab mutation test");
            return true;
        }

        static void Postfix()
        {
            if (thrown || !WorldTickGuard.MutationDue(2000)) return;
            thrown = true;
            throw new Exception($"-{WorldTickGuard.MutationArg}: lab mutation throw in Map.MapUpdate at MP tick {TickPatch.Timer} (FIXES row 252)");
        }
    }

    // After TickPatch's prefix has run this frame's commands and ticks: no time context is open here
    [HarmonyPatch(typeof(TickManager), nameof(TickManager.TickManagerUpdate))]
    static class WorldTickGuardMutationLeak
    {
        static bool leaked;

        static bool Prepare() => WorldTickGuard.Mutation;

        static void Postfix()
        {
            if (leaked || !WorldTickGuard.MutationDue(4000)) return;
            leaked = true;
            Find.TickManager.ticksGameInt -= 25;
            Log.Warning($"-{WorldTickGuard.MutationArg}: lab mutation moved the global time back 25 ticks at MP tick {TickPatch.Timer} (FIXES row 252)");
        }
    }
}
