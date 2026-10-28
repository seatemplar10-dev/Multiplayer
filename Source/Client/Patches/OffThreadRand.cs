using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace Multiplayer.Client.Patches
{
    /// <summary>
    /// Verse.Rand keeps one seed, call count and state stack for the whole process. On 1.7 some drawing runs on
    /// worker threads while the main thread ticks, and a worker's <c>PushState</c> ... <c>PopState</c> puts back the
    /// state it saw, wiping the main thread's calls in between (2026-10-22 Desync-03: the host's world Rand went from
    /// 2494299 calls back to 2494292 in one world tick, with 32 Rand calls off the main thread on the host and none
    /// on the client). Off the main thread in a session, the primitives below work on that thread's own state instead, so a worker
    /// can never move the simulation's. Last priority: a mod that already gives a thread its own state (Rimkit's map
    /// preview worker) has skipped the original by then, and is left alone.
    /// </summary>
    [EarlyPatch]
    [HarmonyPatch]
    public static class OffThreadRand
    {
        [System.ThreadStatic] private static uint seed;
        [System.ThreadStatic] private static uint iterations;
        [System.ThreadStatic] private static Stack<ulong> stack;
        [System.ThreadStatic] private static bool seeded;

        private static bool Mine(bool runOriginal)
        {
            if (!runOriginal || Multiplayer.Client == null || UnityData.IsInMainThread) return false;
            if (!seeded)
            {
                seeded = true;
                seed = (uint)(System.Environment.CurrentManagedThreadId * 7919 + System.Environment.TickCount);
            }
            return true;
        }

        [HarmonyPatch(typeof(Rand), nameof(Rand.Value), MethodType.Getter)]
        [HarmonyPrefix, HarmonyPriority(Priority.Last)]
        static bool Value(ref float __result, bool __runOriginal)
        {
            if (!Mine(__runOriginal)) return true;
            __result = (float)(((double)MurmurHash.GetInt(seed, iterations++) - -2147483648.0) / 4294967295.0);
            return false;
        }

        [HarmonyPatch(typeof(Rand), nameof(Rand.Int), MethodType.Getter)]
        [HarmonyPrefix, HarmonyPriority(Priority.Last)]
        static bool Int(ref int __result, bool __runOriginal)
        {
            if (!Mine(__runOriginal)) return true;
            __result = MurmurHash.GetInt(seed, iterations++);
            return false;
        }

        [HarmonyPatch(typeof(Rand), nameof(Rand.Seed), MethodType.Setter)]
        [HarmonyPrefix, HarmonyPriority(Priority.Last)]
        static bool Seed(int value, bool __runOriginal)
        {
            if (!Mine(__runOriginal)) return true;
            seed = (uint)value;
            iterations = 0u;
            return false;
        }

        [HarmonyPatch(typeof(Rand), nameof(Rand.StateCompressed), MethodType.Getter)]
        [HarmonyPrefix, HarmonyPriority(Priority.Last)]
        static bool GetState(ref ulong __result, bool __runOriginal)
        {
            if (!Mine(__runOriginal)) return true;
            __result = seed | ((ulong)iterations << 32);
            return false;
        }

        [HarmonyPatch(typeof(Rand), nameof(Rand.StateCompressed), MethodType.Setter)]
        [HarmonyPrefix, HarmonyPriority(Priority.Last)]
        static bool SetState(ulong value, bool __runOriginal)
        {
            if (!Mine(__runOriginal)) return true;
            seed = (uint)(value & 0xFFFFFFFFu);
            iterations = (uint)((value >> 32) & 0xFFFFFFFFu);
            return false;
        }

        [HarmonyPatch(typeof(Rand), nameof(Rand.PushState), new System.Type[0])]
        [HarmonyPrefix, HarmonyPriority(Priority.Last)]
        static bool PushState(bool __runOriginal)
        {
            if (!Mine(__runOriginal)) return true;
            (stack ??= new Stack<ulong>()).Push(seed | ((ulong)iterations << 32));
            return false;
        }

        [HarmonyPatch(typeof(Rand), nameof(Rand.PopState))]
        [HarmonyPrefix, HarmonyPriority(Priority.Last)]
        static bool PopState(bool __runOriginal)
        {
            if (!Mine(__runOriginal)) return true;
            // An unbalanced pop on a worker leaves its state as it is rather than throwing into drawing code
            if (stack is { Count: > 0 })
            {
                var state = stack.Pop();
                seed = (uint)(state & 0xFFFFFFFFu);
                iterations = (uint)((state >> 32) & 0xFFFFFFFFu);
            }
            return false;
        }
    }
}
