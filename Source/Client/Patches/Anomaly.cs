using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Client.Patches
{
    [HarmonyPatch(typeof(AnomalyUtility), nameof(AnomalyUtility.TryGetNearbyUnseenCell))]
    static class AnomalyUtility_TryGetNearbyUnseenCell
    {
        // Discards the result from CurrentViewRect and calls EmptyCellRect
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
        {
            var target = AccessTools.PropertyGetter(typeof(CameraDriver), nameof(CameraDriver.CurrentViewRect));
            var replace = AccessTools.Method(typeof(AnomalyUtility_TryGetNearbyUnseenCell), nameof(EmptyCellRect));

            foreach (var inst in insts)
            {
                yield return inst;

                if (inst.operand as MethodInfo == target)
                {
                    yield return new CodeInstruction(OpCodes.Pop);
                    yield return new CodeInstruction(OpCodes.Call, replace);
                }
            }
        }

        static CellRect EmptyCellRect()
        {
            return Multiplayer.Client != null ? CellRect.Empty : Find.CameraDriver.CurrentViewRect;
        }

        static void Prefix() => Rand.PushState(Find.TickManager.TicksAbs);
        static void Postfix() => Rand.PopState();
    }

    // FIXES row 105: UnnaturalCorpse.IsOutsideView (teleport and disappear gate, called from TickInterval and DoDisappear) and
    // UnnaturalCorpseTracker.UpdateCorpseState (container breakout gate, called from the tracker tick) read
    // Find.CameraDriver.CurrentViewRect inside the simulation, so each PC decides from its own camera whether the corpse moves.
    // In MP the view rect is CellRect.Empty, like TryGetNearbyUnseenCell above, and the camera is not read at all: every PC
    // treats the corpse as not on screen (the reservation and Find.CurrentMap checks are unchanged) and takes the same path.
    [HarmonyPatch]
    static class UnnaturalCorpse_SimViewRect
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.DeclaredMethod(typeof(UnnaturalCorpse), nameof(UnnaturalCorpse.IsOutsideView));
            yield return AccessTools.DeclaredMethod(typeof(UnnaturalCorpseTracker), nameof(UnnaturalCorpseTracker.UpdateCorpseState));
        }

        // Replaces the pair "call Find.get_CameraDriver; callvirt CameraDriver.get_CurrentViewRect" with "call SimViewRect"
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts, MethodBase original)
        {
            var driver = AccessTools.PropertyGetter(typeof(Find), nameof(Find.CameraDriver));
            var viewRect = AccessTools.PropertyGetter(typeof(CameraDriver), nameof(CameraDriver.CurrentViewRect));
            var replace = AccessTools.Method(typeof(UnnaturalCorpse_SimViewRect), nameof(SimViewRect));
            var list = new List<CodeInstruction>(insts);
            int found = 0;

            for (int i = 0; i < list.Count; i++)
            {
                if (i + 1 < list.Count && list[i].Calls(driver) && list[i + 1].Calls(viewRect) && list[i + 1].labels.Count == 0)
                {
                    yield return new CodeInstruction(OpCodes.Call, replace).MoveLabelsFrom(list[i]).MoveBlocksFrom(list[i]);
                    i++;
                    found++;
                    continue;
                }

                yield return list[i];
            }

            if (found != 1)
                Log.Error($"MP row 105: expected 1 camera view rect read in {original.DeclaringType?.Name}.{original.Name}, replaced {found}");
        }

        static CellRect SimViewRect()
        {
            return Multiplayer.Client != null ? CellRect.Empty : Find.CameraDriver.CurrentViewRect;
        }
    }
}