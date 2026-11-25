using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
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

        static void Prefix(ref bool __state)
        {
            __state = LongEventHandler.currentEvent == null;
            currentEventKey = LongEventHandler.currentEvent?.eventTextKey;
            // row 453 (25 Nov, Fable review): computed here because vanilla nulls currentEvent before the end queue runs.
            keyIsMpOrSaving = currentEventKey != null
                && (currentEventKey.StartsWith("Mp", StringComparison.Ordinal) || currentEventKey == "Saving");
            currentEventWasMarked = MarkLongEvents.IsTickMarked(LongEventHandler.currentEvent?.eventAction);
        }

        static void Postfix(bool __state)
        {
            currentEventWasMarked = false;

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

        // row 453 (25 Nov, Fable review): save/restore instead of set/clear, so a nested call cannot clear the outer flag; main thread,
        // currentEvent == null (the queue really runs now) and not an Mp/Saving event; lab switch -mplaboff=453 restores the old behaviour.
        static void Prefix(out bool __state)
        {
            __state = runningMarkedQueue;
            if (Multiplayer.Client != null && NewLongEvent.currentEventWasMarked && UnityData.IsInMainThread
                && LongEventHandler.currentEvent == null && !NewLongEvent.keyIsMpOrSaving && !LabOff.Row(453))
                runningMarkedQueue = true;
        }

        // row 453 (25 Nov, Fable review): void finalizer restores the outer value even if a queued action throws, and does not swallow the exception.
        static void Finalizer(bool __state)
        {
            runningMarkedQueue = __state;
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
}
