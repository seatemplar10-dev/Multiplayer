using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace Multiplayer.Client.Patches
{
    // 1.7 lab, class C12 (FIXES rows 91, 112): RimWorld 1.7.4981 removes from ListerThings' lists with RemoveSwap
    // (list[i] = list[last]; RemoveAt(last)). Some listed things live per PC: Mote_Text (drawGUIOverlay, so listed) is spawned
    // in the synced tick but leaves at a real-time moment on each PC. With swap-removal that moment decides which thing moves
    // into which hole, so the same things end up in a different order on each PC: Map.ExposeData saves them in that order, every
    // PC reloads its own save at a join point, and tick lists and list iteration split. In a session the removal keeps order
    // (as 4976 did), so a per-PC entry leaving never moves the others.
    [HarmonyPatch(typeof(ListerThings), nameof(ListerThings.Remove))]
    static class ListerThingsKeepOrder
    {
        static readonly MethodInfo RemoveSwap = AccessTools.Method(typeof(ListerThings), "RemoveSwap", new[] { typeof(List<Thing>), typeof(Thing) });
        static readonly MethodInfo Replacement = AccessTools.Method(typeof(ListerThingsKeepOrder), nameof(RemoveKeepingOrder));

        // 4981 calls RemoveSwap twice in Remove (listsByDef, each group list). Builds before 4981 have no RemoveSwap.
        const int ExpectedCalls = 2;

        static bool Prepare()
        {
            if (RemoveSwap == null)
                Log.Message("Multiplayer: ListerThings has no RemoveSwap (a build before 1.7.4981) - list order guard not needed");
            return RemoveSwap != null;
        }

        // First, so another mod's transpiler sees the call already replaced (review 19 Oct)
        [HarmonyPriority(Priority.First)]
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
        {
            int replaced = 0;
            foreach (var inst in insts)
            {
                if (inst.operand as MethodInfo == RemoveSwap)
                {
                    inst.operand = Replacement;
                    replaced++;
                }
                yield return inst;
            }

            // A half-applied guard splits sessions silently: counted as a loading error like any failed Multiplayer patch
            if (replaced != ExpectedCalls)
            {
                Log.Error($"Multiplayer: ListerThings.Remove calls RemoveSwap {replaced} time(s), expected {ExpectedCalls} - list order guard (FIXES row 112) not fully applied");
                Multiplayer.loadingErrors = true;
            }
            else
                Log.Message("Multiplayer: list order guard active (ListerThings.Remove keeps order in a session, FIXES row 112)");
        }

        public static void RemoveKeepingOrder(List<Thing> list, Thing t)
        {
            int i = list.IndexOf(t);
            if (i < 0) return;

            if (Multiplayer.Client != null)
            {
                list.RemoveAt(i);
                return;
            }

            // Outside a session: vanilla's swap-removal
            int last = list.Count - 1;
            list[i] = list[last];
            list.RemoveAt(last);
        }
    }
}
