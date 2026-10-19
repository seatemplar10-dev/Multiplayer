using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Client.Patches
{
    // 1.7 lab, class C14 (FIXES row 115, sibling of C12 / row 112): ListerThings.ThingsOfDef / ThingsInGroup return the lister's
    // OWN list, and these vanilla 4981 methods sort it in place (GenCollection.SortBy -> List.Sort). TryCreateRecolorJob also
    // runs with dryRun from the styling station's float menu, i.e. on one PC; that PC's shared list is then in another order,
    // and the simulation's own later sort breaks distance ties differently (List.Sort is unstable). In a session these methods
    // get a copy, so no caller reorders the shared list.
    [HarmonyPatch]
    static class ListerSortGuard
    {
        static readonly MethodInfo ThingsOfDef = AccessTools.Method(typeof(ListerThings), nameof(ListerThings.ThingsOfDef), new[] { typeof(ThingDef) });
        static readonly MethodInfo ThingsInGroup = AccessTools.Method(typeof(ListerThings), nameof(ListerThings.ThingsInGroup), new[] { typeof(ThingRequestGroup) });
        static readonly MethodInfo CopyOfDef = AccessTools.Method(typeof(ListerSortGuard), nameof(ThingsOfDefCopy));
        static readonly MethodInfo CopyInGroup = AccessTools.Method(typeof(ListerSortGuard), nameof(ThingsInGroupCopy));

        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(JobGiver_OptimizeApparel), nameof(JobGiver_OptimizeApparel.TryCreateRecolorJob));
            yield return AccessTools.Method(typeof(JobGiver_AIResurrectTarget), "UpdateResurrectTarget");
            yield return AccessTools.Method(typeof(ThoughtWorker_HateAura), "CurrentStateInternal");
            var bossgroup = AccessTools.TypeByName("CallBossgroupUtility");
            if (bossgroup != null)
                yield return AccessTools.Method(bossgroup, "TryStartSummonBossgroupJob");
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts, MethodBase __originalMethod)
        {
            int replaced = 0;
            foreach (var inst in insts)
            {
                if (inst.operand as MethodInfo == ThingsOfDef)
                {
                    inst.opcode = System.Reflection.Emit.OpCodes.Call;
                    inst.operand = CopyOfDef;
                    replaced++;
                }
                else if (inst.operand as MethodInfo == ThingsInGroup)
                {
                    inst.opcode = System.Reflection.Emit.OpCodes.Call;
                    inst.operand = CopyInGroup;
                    replaced++;
                }
                yield return inst;
            }

            if (replaced == 0)
            {
                Log.Error($"Multiplayer: {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name} no longer reads a lister list - sort guard (FIXES row 115) not applied");
                Multiplayer.loadingErrors = true;
            }
        }

        public static List<Thing> ThingsOfDefCopy(ListerThings lister, ThingDef def)
        {
            var list = lister.ThingsOfDef(def);
            return Multiplayer.Client != null ? new List<Thing>(list) : list;
        }

        public static List<Thing> ThingsInGroupCopy(ListerThings lister, ThingRequestGroup group)
        {
            var list = lister.ThingsInGroup(group);
            return Multiplayer.Client != null ? new List<Thing>(list) : list;
        }
    }
}
