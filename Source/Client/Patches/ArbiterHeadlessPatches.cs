using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Client.Patches;
using RimWorld;
using UnityEngine;
using Verse;

namespace Multiplayer.Client
{
    // RimWorld 1.7 (Waylith) guards for the arbiter, which runs with -headless: no window, UI.screenWidth 0, and
    // nobody reads anything but its stdout log.

    /// Verse.Log stops logging for good after 10,000 messages and turns Debug.unityLogger off with it, which also
    /// ends the stdout mirror the arbiter's log file is made from (Log.Notify_MessageReceivedThreadedInternal). A
    /// joiner on a big mod list writes more than that while it loads, so the arbiter's log would stop before it even
    /// joined. The arbiter never counts messages (debug builds do the same for everyone: DisableLogMessageLimit).
    /// An early patch: the count starts with the first message, long before the main patches are applied.
    [EarlyPatch]
    [HarmonyPatch(typeof(Log), nameof(Log.Notify_MessageReceivedThreadedInternal))]
    static class ArbiterNoLogMessageLimit
    {
        static bool Prepare(MethodBase original)
        {
            if (!Multiplayer.arbiterInstance) return false;

            // Prepare runs once without and once with the target; reset only once, in case the limit was reached
            // before Multiplayer loaded
            if (original != null && Log.reachedMaxMessagesLimit)
                Log.ResetMessageCount();

            return true;
        }

        static bool Prefix() => false;
    }

    /// Waylith's headless loop (EngineApplication.RunHeadlessLoop) never dispatches OnGUI: only ImguiDispatch does, and
    /// Bootstrap.ConfigureFrameRenderer creates it for a window only. A synchronous long event with a text and the standard
    /// window waits until LongEventHandler.DrawLongEventWindowContents sets alreadyDisplayed on a Repaint
    /// (QueuedLongEvent.ShouldWaitUntilDisplayed), so on a headless instance it never runs. Multiplayer's join point
    /// ("MpCreatingJoinPoint", AsyncWorldTimeComp) is one: the arbiter stopped at every join point after its own join, sent
    /// Freeze and never reloaded or uploaded the world, so the join point never ended and a rejoin ("Try resync") waited on
    /// it forever (1.7 lab mp55/mp56). Nothing is ever displayed here, so the current event counts as displayed and runs on
    /// the next update, as it does one frame after the window is drawn on a windowed instance. A field write before the
    /// update, not a patch on the small getter, which the JIT may have inlined into its callers.
    [HarmonyPatch(typeof(LongEventHandler), nameof(LongEventHandler.LongEventsUpdate))]
    static class HeadlessLongEventDisplayed
    {
        static bool Prepare() => Multiplayer.arbiterInstance || GenCommandLine.CommandLineArgPassed("headless");

        static void Prefix()
        {
            var current = LongEventHandler.currentEvent;
            if (current is { alreadyDisplayed: false })
                current.alreadyDisplayed = true;
        }
    }

    /// With -headless, UI.screenWidth is 0, so the colonist bar's maximum width (screen width - 520) is negative and
    /// ColonistBarDrawLocsFinder.FindBestScale loops forever looking for a scale at which a row fits. Anything that
    /// reads ColonistBar.Entries reaches it (the lab's headless observer hung there inside one frame through Moody's
    /// window update). A hung arbiter that stays connected is worse than none: while it plays, the server takes world
    /// data and sync opinions only from the arbiter. The arbiter draws no colonist bar, so the layout is skipped: one
    /// location per entry (callers index the locations by entry), scale 1.
    /// Applied only to the arbiter and to -headless instances; a game with a window never gets this patch.
    [HarmonyPatch]
    static class ArbiterHeadlessColonistBar
    {
        // UI.screenWidth below this leaves the bar too little width to ever fit a row
        public const int MinScreenWidth = 600;

        static bool Prepare() => Multiplayer.arbiterInstance || GenCommandLine.CommandLineArgPassed("headless");

        static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(ColonistBarDrawLocsFinder), nameof(ColonistBarDrawLocsFinder.CalculateDrawLocs),
                [typeof(List<Vector2>), typeof(float).MakeByRefType(), typeof(int)]);

        static bool Prefix(List<Vector2> outDrawLocs, out float scale)
        {
            scale = 1f;
            if (!Multiplayer.arbiterInstance && UI.screenWidth >= MinScreenWidth) return true;

            outDrawLocs.Clear();
            int entries = (Find.UIRoot as UIRoot_Play)?.mapUI?.colonistBar?.cachedEntries?.Count ?? 0;
            for (int i = 0; i < entries; i++)
                outDrawLocs.Add(Vector2.zero);
            return false;
        }
    }
}
