using HarmonyLib;
using Multiplayer.API;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Verse;

namespace Multiplayer.Client
{
    public static class SyncUtil
    {
        public static List<object> prevSelected = null;
        public static List<WorldObject> prevWorldSelected = null;
        public static bool isDialogNodeTreeOpen = false;

        internal static void DialogNodeTreePostfix()
        {
            if (Multiplayer.Client != null && Find.WindowStack?.WindowOfType<Dialog_NodeTree>() != null)
                isDialogNodeTreeOpen = true;
        }

        internal static void PatchMethodForDialogNodeTreeSync(MethodBase method)
        {
            Multiplayer.harmony.PatchMeasure(method, postfix: new HarmonyMethod(typeof(SyncUtil), nameof(SyncUtil.DialogNodeTreePostfix)));
        }

        internal static void PatchMethodForSync(MethodBase method)
        {
            Multiplayer.harmony.PatchMeasure(method, transpiler: SyncTemplates.CreateTranspiler());
        }

        public static SyncHandler HandleCmd(ByteReader data)
        {
            int randDepth = Rand.stateStack.Count;
            int syncId = data.ReadInt32();
            SyncHandler handler;

            try
            {
                handler = Sync.handlers[syncId];
            }
            catch (ArgumentOutOfRangeException)
            {
                Log.Error($"Error: invalid syncId {syncId}/{Sync.handlers.Count}, this implies mismatched mods, ensure your versions match! Stacktrace follows.");
                throw;
            }

            prevSelected = Find.Selector.selected;
            prevWorldSelected = Find.WorldSelector.selected;

            bool shouldQueue = false;

            if (handler.context != SyncContext.None)
            {
                if (handler.context.HasFlag(SyncContext.MapMouseCell))
                {
                    IntVec3 mouseCell = SyncSerialization.ReadSync<IntVec3>(data);
                    MouseCellPatch.result = mouseCell;
                }

                if (handler.context.HasFlag(SyncContext.MapSelected))
                {
                    List<ISelectable> selected = SyncSerialization.ReadSync<List<ISelectable>>(data);
                    Find.Selector.selected = selected.Cast<object>().AllNotNull().ToList();
                }

                if (handler.context.HasFlag(SyncContext.WorldSelected))
                {
                    List<ISelectable> selected = SyncSerialization.ReadSync<List<ISelectable>>(data);
                    FieldRefs.worldSelected(Find.WorldSelector) = selected.Cast<WorldObject>().AllNotNull().ToList();
                }

                if (handler.context.HasFlag(SyncContext.QueueOrder_Down))
                    shouldQueue = data.ReadBool();
            }

            KeyIsDownPatch.shouldQueue = shouldQueue;

            try
            {
                handler.Handle(data);
            }
            finally
            {
                MouseCellPatch.result = null;
                KeyIsDownPatch.shouldQueue = null;
                Find.Selector.selected = prevSelected;
                FieldRefs.worldSelected(Find.WorldSelector) = prevWorldSelected;

                // 1.7 lab (FIXES row 191, run MF8): a command whose code throws between Rand.PushState and PopState (SendPawn: a starting
                // pawn with an addiction threw while its needs were read back) left a push open. Vanilla's Rand.EnsureStateStackEmpty
                // repairs it later, at a moment that differs per PC, so one PC ran a whole tick inside the stale push (no traces, other
                // draws). Every PC runs the same command and leaks the same pushes, so popping them here keeps the PCs equal.
                if (Rand.stateStack.Count > randDepth)
                {
                    Log.Warning($"MP: command {handler} left {Rand.stateStack.Count - randDepth} Rand state push(es) open; popped at the end of the command (row 191)");
                    while (Rand.stateStack.Count > randDepth)
                        Rand.PopState();
                }
            }

            return handler;
        }

        public static void WriteContext(SyncHandler handler, ByteWriter data)
        {
            if (handler.context == SyncContext.None) return;

            if (handler.context.HasFlag(SyncContext.CurrentMap))
                data.MpContext().map = Find.CurrentMap;

            if (handler.context.HasFlag(SyncContext.MapMouseCell))
            {
                data.MpContext().map = Find.CurrentMap;
                SyncSerialization.WriteSync(data, UI.MouseCell());
            }

            if (handler.context.HasFlag(SyncContext.MapSelected))
                SyncSerialization.WriteSync(data, Find.Selector.selected.Cast<ISelectable>().ToList());

            if (handler.context.HasFlag(SyncContext.WorldSelected))
                SyncSerialization.WriteSync(data, Find.WorldSelector.selected.Cast<ISelectable>().ToList());

            if (handler.context.HasFlag(SyncContext.QueueOrder_Down))
                data.WriteBool(KeyBindingDefOf.QueueOrder.IsDownEvent);
        }
    }
}
