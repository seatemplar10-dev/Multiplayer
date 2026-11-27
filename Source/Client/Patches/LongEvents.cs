using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Multiplayer.Client.Factions;
using RimWorld;
using HarmonyLib;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;
using Verse;

namespace Multiplayer.Client.Patches
{
    [HarmonyPatch(typeof(LongEventHandler), nameof(LongEventHandler.QueueLongEvent),
        typeof(Action), typeof(string), typeof(bool), typeof(Action<Exception>), typeof(bool), typeof(bool), typeof(Action))]
    static class MarkLongEvents
    {
        private static MethodInfo MarkerMethod = AccessTools.Method(typeof(MarkLongEvents), nameof(Marker));

        static void Prefix(ref Action action, string textKey)
        {
            if (Multiplayer.Client is { State: ConnectionStateEnum.ClientPlaying } && (Multiplayer.Ticking || Multiplayer.ExecutingCmds || textKey == "MpSaving"))
            {
                action += Marker;
            }
        }

        private static void Marker()
        {
        }

        public static bool IsTickMarked(Action action)
        {
            return action?.GetInvocationList()?.Any(d => d.Method == MarkerMethod) ?? false;
        }
    }

    [HarmonyPatch(typeof(LongEventHandler), nameof(LongEventHandler.LongEventsUpdate))]
    static class NewLongEvent
    {
        public static bool currentEventWasMarked;
        // row 453 (25 Nov): the text key of the running event, still known after vanilla nulls currentEvent (LongEventEnd needs it).
        public static string currentEventKey;
        // row 453 (25 Nov, Fable review): true for Multiplayer's own events (Mp*, Saving), so LongEventEnd can skip them without string work.
        public static bool keyIsMpOrSaving;
        // row 456 (27 Nov): the running event itself (vanilla nulls currentEvent before the end queue runs); key into LongEventQueueInfo
        public static LongEventHandler.QueuedLongEvent currentQueued;

        static void Prefix(ref bool __state)
        {
            __state = LongEventHandler.currentEvent == null;
            currentEventKey = LongEventHandler.currentEvent?.eventTextKey;
            currentQueued = LongEventHandler.currentEvent;
            // row 453 (25 Nov, Fable review): computed here because vanilla nulls currentEvent before the end queue runs.
            keyIsMpOrSaving = currentEventKey != null
                && (currentEventKey.StartsWith("Mp", StringComparison.Ordinal) || currentEventKey == "Saving");
            currentEventWasMarked = MarkLongEvents.IsTickMarked(LongEventHandler.currentEvent?.eventAction);
        }

        static void Postfix(bool __state)
        {
            currentEventWasMarked = false;
            currentQueued = null;

            if (Multiplayer.Client == null) return;

            if (__state && MarkLongEvents.IsTickMarked(LongEventHandler.currentEvent?.eventAction))
                Multiplayer.Client.Send(ClientFreezePacket.Freeze());
        }
    }

    // 1.7 lab (FIXES row 202): with no long event running, vanilla runs the action at once (LongEventHandler.cs:301-303). Inside
    // Multiplayer's ReadExposable that is mid-load; queue it for ScribeUtil.ReadExposable to run after FinalizeLoading.
    [HarmonyPatch(typeof(LongEventHandler), nameof(LongEventHandler.ExecuteWhenFinished))]
    static class DeferWhenFinishedDuringReadExposable
    {
        static bool Prefix(Action action)
        {
            if (ScribeUtil.readingExposable == 0 || LongEventHandler.currentEvent != null || !UnityData.IsInMainThread) return true;
            // lab switch row 202 (package G, 17 Nov): off = old behaviour (action runs at once, mid-load)
            if (LabOff.Row(202)) return true;
            ScribeUtil.deferredWhenFinished.Add(action);
            return false;
        }
    }

    [HarmonyPatch(typeof(LongEventHandler), nameof(LongEventHandler.ExecuteToExecuteWhenFinished))]
    public static class LongEventEnd
    {
        // row 453 (25 Nov): vanilla sets currentEvent = null before running ExecuteWhenFinished actions, so mods' actions queued
        // inside a tick-marked event (e.g. VEF map spawns after a grav ship landing) saw InInterface == true and got per-PC ids.
        public static bool runningMarkedQueue;

        // row 456 (27 Nov): what Prefix changed, so Finalizer undoes exactly that
        public struct EndState
        {
            public bool prevRunning;
            public bool pushedRand;
            public bool pushedFaction;
        }

        // row 453 (25 Nov, Fable review): save/restore instead of set/clear, so a nested call cannot clear the outer flag; main thread,
        // currentEvent == null (the queue really runs now) and not an Mp/Saving event; lab switch -mplaboff=453 restores the old behaviour.
        static void Prefix(out EndState __state)
        {
            __state = default;
            __state.prevRunning = runningMarkedQueue;
            if (Multiplayer.Client != null && NewLongEvent.currentEventWasMarked && UnityData.IsInMainThread
                && LongEventHandler.currentEvent == null && !NewLongEvent.keyIsMpOrSaving && !LabOff.Row(453))
            {
                runningMarkedQueue = true;

                // row 456 (27 Nov): outermost drain only; seed and faction recorded when the event was queued (LongEventQueueInfo)
                if (!__state.prevRunning && !LabOff.Row(456)
                    && NewLongEvent.currentQueued != null && LongEventQueueInfo.table.TryGetValue(NewLongEvent.currentQueued, out var info))
                {
                    // row 456 1b-4 (review finding 4, stage 1b review): the seed is consumed by the first outermost drain. A later drain
                    // of the same event (a callback or a second ExecuteToExecuteWhenFinished) is not re-seeded with this seed and continues
                    // the ambient stream (the callback has its own callbackSeed from SeedLongEvents), but it keeps the queued faction.
                    var queuedSeed = info.seed;
                    info.seed = null;
                    if (queuedSeed is { } seed)
                    {
                        Rand.PushState(seed);
                        __state.pushedRand = true;
                    }

                    if (info.faction != null && Multiplayer.game != null && Multiplayer.GameComp.multifaction)
                    {
                        FactionExtensions.PushFaction(null, info.faction, force: true);
                        __state.pushedFaction = true;
                        foreach (var map in Find.Maps)
                            map.MpComp().SetFaction(info.faction);
                    }
                }
            }
        }

        // row 453 (25 Nov, Fable review): void finalizer restores the outer value even if a queued action throws, and does not swallow the exception.
        // row 456: also pops the faction and seed pushed by Prefix (faction first, like AsyncWorldTimeComp.PostContext, then Rand).
        static void Finalizer(EndState __state)
        {
            runningMarkedQueue = __state.prevRunning;

            // row 456 1b-3 (review finding 3): the Rand pop runs even if the faction pop throws
            try
            {
                if (__state.pushedFaction)
                {
                    var f = FactionExtensions.PopFaction();
                    if (f != null)
                        foreach (var map in Find.Maps)
                            map.MpComp().SetFaction(f);
                }
            }
            finally
            {
                if (__state.pushedRand)
                    Rand.PopState();
            }
        }

        static void Postfix()
        {
            if (Multiplayer.Client != null && NewLongEvent.currentEventWasMarked)
                Multiplayer.Client.Send(ClientFreezePacket.Unfreeze());
        }
    }

    [HarmonyPatch(typeof(LongEventHandler), nameof(LongEventHandler.QueueLongEvent), [typeof(Action), typeof(string), typeof(bool), typeof(Action<Exception>), typeof(bool), typeof(bool), typeof(Action)])]
    static class LongEventAlwaysSync
    {
        static void Prefix(ref bool doAsynchronously, ref Action action, Action callback)
        {
            if (Multiplayer.ExecutingCmds)
            {
                doAsynchronously = false;

                // Callback is only called in asynchronous long events and will be skipped in
                // a synchronous ones. Make sure they are called after the actual action.
                if (callback != null)
                    action += callback;
            }
        }
    }

    [HarmonyPatch(typeof(LongEventHandler), nameof(LongEventHandler.UpdateCurrentAsynchronousEvent))]
    public static class PatchUpdateCurrentAsynchronousEventCleanUpOrder
    {
        readonly static MethodInfo OrginalMethod = AccessTools.Method(typeof(LongEventHandler), nameof(LongEventHandler.ExecuteToExecuteWhenFinished));
        readonly static MethodInfo ReplacementMethod = AccessTools.Method(typeof(PatchUpdateCurrentAsynchronousEventCleanUpOrder), nameof(ReorderCleanupCode));

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(OrginalMethod))
                {
                    yield return new CodeInstruction(OpCodes.Call, ReplacementMethod);
                    yield return new CodeInstruction(OpCodes.Ret);
                }
                else
                    yield return instruction;
            }
        }

        public static void ReorderCleanupCode()
        {
            Action callback = LongEventHandler.currentEvent.callback;

            LongEventHandler.currentEvent = null;
            LongEventHandler.eventThread = null;
            LongEventHandler.levelLoadOp = null;

            LongEventHandler.ExecuteToExecuteWhenFinished();

            if (callback != null)
                callback();
        }
    }

    // row 456 (27 Nov): per queued long event, the seed (derived from the draw SeedLongEvents already makes, so the tick's Rand sequence
    // does not shift) and the faction in force when the event was queued. Read by LongEventEnd for the event's ExecuteWhenFinished queue.
    [HarmonyPatch(typeof(LongEventHandler), nameof(LongEventHandler.QueueLongEvent),
        typeof(Action), typeof(string), typeof(bool), typeof(Action<Exception>), typeof(bool), typeof(bool), typeof(Action))]
    static class LongEventQueueInfo
    {
        public class Info
        {
            public int? seed;
            public Faction faction;
        }

        public static readonly ConditionalWeakTable<LongEventHandler.QueuedLongEvent, Info> table = new();

        static readonly AccessTools.FieldRef<Queue<LongEventHandler.QueuedLongEvent>> EventQueue =
            AccessTools.StaticFieldRefAccess<Queue<LongEventHandler.QueuedLongEvent>>(AccessTools.Field(typeof(LongEventHandler), "eventQueue"));

        // row 456 1b-2 (review finding 2): the faction in force right now. FactionContext.Push/Set (used by the map tick, the command
        // executor and WorldObjectMethodPatches, which sets the gravship's faction around Gravship.TickInterval where the landing event is
        // queued) writes FactionManager.ofPlayer, so that field is the top of the context stack. CaravanFactionScope acts only inside the
        // event action (the ArriveNewMap prefix), not at queue time. Read the field, not a fallback to the spectator.
        static Faction ActiveFaction()
        {
            if (Multiplayer.game == null) return null;
            var fm = Find.FactionManager;
            return fm.ofPlayer ?? fm.OfPlayer;
        }

        static void Postfix()
        {
            var drawn = SeedLongEvents.lastDrawnSeed;
            SeedLongEvents.lastDrawnSeed = null;
            if (drawn == null || Multiplayer.Client == null) return;

            var queue = EventQueue();
            if (queue.Count == 0) return;

            table.AddOrUpdate(queue.Last(), new Info
            {
                seed = Gen.HashCombineInt(drawn.Value, 456),
                faction = ActiveFaction()
            });
        }
    }
}
