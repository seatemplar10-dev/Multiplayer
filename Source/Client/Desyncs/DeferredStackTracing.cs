using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Multiplayer.Client.Patches;
using Multiplayer.Common;
using RimWorld;
using Verse;
using Verse.AI;

namespace Multiplayer.Client.Desyncs
{
    [EarlyPatch]
    [HarmonyPatch]
    public static class DeferredStackTracing
    {
        public static int ignoreTraces;
        public static long maxTraceDepth;
        public static int randCalls;

        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.PropertyGetter(typeof(Rand), nameof(Rand.Value));
            yield return AccessTools.PropertyGetter(typeof(Rand), nameof(Rand.Int));
        }

        public static int acc;

        private static bool SupportsDeferredStackTracing =>
            RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.X86;

        public static void Postfix()
        {
            if (Native.LmfPtr == 0)
            {
                // CoreCLR (RimWorld 1.7) has no Mono LMF to walk, so Fast records nothing there. A managed StackTrace
                // per traced call (each Rand.Value/Rand.Int, thing spawn and despawn, unique id while ticking) costs
                // microseconds instead of the Mono walk's fraction of one, so it only runs in the Slow mode. Slow is
                // not saved in the settings file (MpSettings.ExposeData); set it for one run with -mptracemode=Slow.
                // The arbiter is started with the host's current mode (ArbiterLauncher).
                if (!Native.IsMono && Multiplayer.settings?.desyncTracingMode == DesyncTracingMode.Slow &&
                    ShouldAddStackTraceForDesyncLog())
                    ManagedTrace();
                return;
            }

            if (!ShouldAddStackTraceForDesyncLog()) return;

            var logItem = StackTraceLogItemRaw.GetFromPool();
            var trace = logItem.raw;
            int hash = 0;
            // Skip this (DeferredStackTracing.Postfix) frame. Keeping it in the trace doesn't provide any value.
            int depth = DeferredStackTracingImpl.TraceImpl(trace, ref hash, skipFrames: 1);

            Multiplayer.game.sync.TryAddStackTraceForDesyncLogRaw(logItem, depth, hash);

            acc++;
        }

        private static Dictionary<MethodBase, int> managedNameHashes = new();

        public const string TraceFrameHashArg = "mptraceframehash";

        // On CoreCLR the frames of one call path can differ between two games that are in sync: tiered compilation
        // and dynamic PGO decide inlining at run time, on timing, so a method can be a frame in one process and be
        // inlined away in another. Hashing the frames would then report "Trace hashes don't match" with no real
        // desync. By default the managed trace hash is the random state alone; the frames are still recorded for
        // the desync report. The -mptraceframehash switch hashes the frames too (the arbiter gets it from the host).
        public static readonly bool HashManagedFrames = GenCommandLine.CommandLineArgPassed(TraceFrameHashArg);

        public const string TraceModeArg = "mptracemode";

        // -mptracemode=None|Fast|Slow sets the desync tracing mode for this run only; desyncTracingMode is not in the
        // settings file and the settings UI shows it only in debug builds. Called once, right after the settings load.
        // "-mptracemode Slow" is accepted too: cmd launchers split "-name=value" at the "=".
        internal static void ApplyTraceModeArg()
        {
            if (!TryGetArg(TraceModeArg, out var value)) return;

            if (Enum.TryParse(value, true, out DesyncTracingMode mode) && Enum.IsDefined(typeof(DesyncTracingMode), mode))
            {
                Multiplayer.settings.desyncTracingMode = mode;
                Log.Message($"Multiplayer: desync tracing mode {mode} (-{TraceModeArg}, this run only)");
            }
            else
            {
                Log.Warning($"Multiplayer: -{TraceModeArg}={value} is not None, Fast or Slow; tracing mode stays " +
                            $"{Multiplayer.settings.desyncTracingMode}");
            }
        }

        private static bool TryGetArg(string name, out string value)
        {
            if (GenCommandLine.TryGetCommandLineArg(name, out value)) return true;

            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], "-" + name, StringComparison.OrdinalIgnoreCase) && !args[i + 1].StartsWith("-"))
                {
                    value = args[i + 1];
                    return true;
                }

            value = null;
            return false;
        }

        private static void ManagedTrace()
        {
            var logItem = StackTraceLogItemManaged.GetFromPool();
            // Skip this frame and Postfix, like the Mono walk skips Postfix
            var frames = new StackTrace(2, false).GetFrames();
            int hash = 0;
            int depth = 0;
            bool hashFrames = HashManagedFrames;

            foreach (var frame in frames)
            {
                if (depth == StackTraceLogItemRaw.MaxDepth) break;

                // Harmony's replacement for a patched method shows as a dynamic method; on CoreCLR Harmony maps it
                // back to the original
                var method = Harmony.GetMethodFromStackframe(frame);
                logItem.methods[depth++] = method;

                if (method == null || !hashFrames)
                    continue;

                if (!managedNameHashes.TryGetValue(method, out int nameHash))
                    managedNameHashes[method] = nameHash = GenText.StableStringHash(method.FullDescription());

                hash = Gen.HashCombineInt(hash, nameHash);
            }

            // The depth varies with inlining like the frames do
            Multiplayer.game.sync.TryAddStackTraceForDesyncLogRaw(logItem, depth, hash, hashDepth: hashFrames);
        }

        public static bool ShouldAddStackTraceForDesyncLog()
        {
            if (!SupportsDeferredStackTracing) return false;
            if (Multiplayer.Client == null) return false;
            if (Multiplayer.settings.desyncTracingMode == DesyncTracingMode.None) return false;
            if (Multiplayer.game == null) return false;

            if (!Multiplayer.game.gameComp.logDesyncTraces) return false;

            if (Rand.stateStack.Count > 1) return false;
            if (Multiplayer.IsReplay) return false;

            if (!Multiplayer.Ticking && !Multiplayer.ExecutingCmds) return false;

            return ignoreTraces == 0;
        }
    }

    [HarmonyPatch(typeof(UniqueIDsManager), nameof(UniqueIDsManager.GetNextID))]
    public static class UniqueIdsPatch
    {
        static void Postfix()
        {
            DeferredStackTracing.Postfix();
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.SpawnSetup))]
    public static class ThingSpawnPatch
    {
        static void Postfix(Thing __instance)
        {
            if (__instance.def.HasThingIDNumber)
                DeferredStackTracing.Postfix();
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.DeSpawn))]
    public static class ThingDeSpawnPatch
    {
        static void Postfix(Thing __instance)
        {
            if (__instance.def.HasThingIDNumber)
                DeferredStackTracing.Postfix();
        }
    }

    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob))]
    public static class EndCurrentJobPatch
    {
        static void Prefix(Pawn_JobTracker __instance)
        {
            if (MpVersion.IsDebug && __instance.curJob != null && DeferredStackTracing.ShouldAddStackTraceForDesyncLog())
                Multiplayer.game.sync.TryAddInfoForDesyncLog($"EndCurrentJob for {__instance.pawn}: {__instance.curJob}", "");
        }
    }

    [HarmonyPatch(typeof(WildAnimalSpawner), nameof(WildAnimalSpawner.WildAnimalSpawnerTick))]
    static class WildAnimalSpawnerTickTraceIgnore
    {
        static void Prefix() => DeferredStackTracing.ignoreTraces++;
        static void Finalizer() => DeferredStackTracing.ignoreTraces--;
    }

    [HarmonyPatch(typeof(WildPlantSpawner), nameof(WildPlantSpawner.WildPlantSpawnerTick))]
    static class WildPlantSpawnerTickTraceIgnore
    {
        static void Prefix() => DeferredStackTracing.ignoreTraces++;
        static void Finalizer() => DeferredStackTracing.ignoreTraces--;
    }

    [HarmonyPatch(typeof(SteadyEnvironmentEffects), nameof(SteadyEnvironmentEffects.SteadyEnvironmentEffectsTick))]
    static class SteadyEnvironmentEffectsTickTraceIgnore
    {
        static void Prefix() => DeferredStackTracing.ignoreTraces++;
        static void Finalizer() => DeferredStackTracing.ignoreTraces--;
    }

    [HarmonyPatch(typeof(TempTerrainManager), nameof(TempTerrainManager.Tick))]
    static class TempTerrainManagerTickTraceIgnore
    {
        static void Prefix() => DeferredStackTracing.ignoreTraces++;
        static void Finalizer() => DeferredStackTracing.ignoreTraces--;
    }

    /*[HarmonyPatch(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterStoreCellForWorker))]
    static class FindBestStorageCellTraceIgnore
    {
        static void Prefix() => DeferredStackTracing.ignoreTraces++;
        static void Finalizer() => DeferredStackTracing.ignoreTraces--;
    }*/

    [HarmonyPatch(typeof(IntermittentSteamSprayer), nameof(IntermittentSteamSprayer.SteamSprayerTick))]
    static class SteamSprayerTickTraceIgnore
    {
        static void Prefix() => DeferredStackTracing.ignoreTraces++;
        static void Finalizer() => DeferredStackTracing.ignoreTraces--;
    }
}
