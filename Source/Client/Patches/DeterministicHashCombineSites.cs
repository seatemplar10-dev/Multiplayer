using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Client.Util;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Client.Patches
{
    // RimWorld 1.7 (CoreCLR) replacement for the old global prefixes on System.HashCode.Combine<int,...> (Combine2Patch ..
    // Combine8Patch). Those patched a CoreLib method the JIT inlines (only Assembly-CSharp is JIT-patchable on Waylith,
    // RimWorld.runtimeconfig.json "WaylithClr.JitPatchableAssemblies"), so once a session existed some callers hashed with
    // the patch and others with the inlined original. Every ValueTuple key hashes through HashCode.Combine, so tuple-keyed
    // dictionaries broke: LoadIDsWantedBank lost every list reference on join-point reloads, Replace Stuff threw "same key
    // already added" every tick (lab runs mp10-mp13; mp14 with the prefixes removed: 5/5 reloads clean, 0 errors).
    //
    // HashCode's per-process random seed only matters where a Combine result decides game outcomes. A scan of all
    // all-int HashCode.Combine call sites in 1.7 Assembly-CSharp (and all lab mods) found exactly these four; every other
    // site is a lookup key, a log-dedupe key or UI. Transpile just these call sites to deterministic combiners.
    [HarmonyPatch]
    static class DeterministicHashCombineSites
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.PropertyGetter(typeof(Map), nameof(Map.NextGenSeed));          // map generation seeds
            yield return AccessTools.Method(typeof(LayoutWorker), "FillAllRooms");                    // room contents (RandBlock seeds)
            yield return AccessTools.Method(typeof(Building_Trap), nameof(Building_Trap.KnowsOfTrap)); // raider trap knowledge, ticks
            yield return AccessTools.PropertyGetter(typeof(Gravship), nameof(Gravship.ConstantRandSeed)); // incident cycle seed (latent)
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts, MethodBase original)
        {
            int replaced = 0;
            foreach (var inst in insts)
            {
                if (inst.operand is MethodInfo mi && IsIntCombine(mi, out int arity))
                {
                    var wrapper = arity switch
                    {
                        2 => AccessTools.Method(typeof(DeterministicHashCombineSites), nameof(Combine2)),
                        3 => AccessTools.Method(typeof(DeterministicHashCombineSites), nameof(Combine3)),
                        _ => null
                    };
                    if (wrapper != null)
                    {
                        inst.operand = wrapper;
                        replaced++;
                    }
                    else
                    {
                        Log.Error($"Multiplayer: HashCode.Combine with {arity} ints in {original.FullDescription()} has no deterministic wrapper");
                    }
                }
                yield return inst;
            }
            if (replaced == 0)
                Log.Error($"Multiplayer: no HashCode.Combine<int..> call found in {original.FullDescription()} (game update?) - its seed is not deterministic");
        }

        static bool IsIntCombine(MethodInfo mi, out int arity)
        {
            arity = 0;
            if (!mi.IsGenericMethod || mi.DeclaringType != typeof(HashCode) || mi.Name != nameof(HashCode.Combine)) return false;
            Type[] args = mi.GetGenericArguments();
            if (!args.All(t => t == typeof(int))) return false;
            arity = args.Length;
            return true;
        }

        // Same rule the old prefixes had: vanilla result outside a session, deterministic inside one.
        static int Combine2(int v1, int v2) =>
            Multiplayer.Client == null ? HashCode.Combine(v1, v2) : Gen.HashCombineInt(v1, v2);

        static int Combine3(int v1, int v2, int v3) =>
            Multiplayer.Client == null ? HashCode.Combine(v1, v2, v3) : DeterministicHash.HashCombineInt(v1, v2, v3);
    }
}
