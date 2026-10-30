using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
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

        // NoInlining and no tail calls below: the traces skip a fixed number of frames (this one and the trace method),
        // which only holds while both frames exist
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Postfix()
        {
            if (Native.LmfPtr == 0)
            {
                // CoreCLR (RimWorld 1.7) has no Mono LMF to walk. The modes there, per traced call (each Rand.Value/Rand.Int,
                // thing spawn and despawn, unique id while ticking):
                // - Fast (default, FastTrace): the OS unwinder's return addresses (~0.3 us at 40 frames) and a cache of the
                //   frames System.Diagnostics.StackTrace gave the first time that exact stack was seen. Same frames as Slow.
                // - Slow (ManagedTrace): one System.Diagnostics.StackTrace each time (~5-20 us).
                // - Lean (FIXES row 267, LeanTrace): the entry with no stack (~15 ns).
                // The trace hash is the random state alone in every mode (tiered compilation can inline a frame away in one
                // process and not another), so Fast, Slow and Lean PCs compare and the desync index is found. With
                // -mptraceframehash the frames are hashed, and Lean then traces like Slow. The mode is not saved in the
                // settings file (MpSettings.ExposeData); -mptracemode sets it for one run. The arbiter is started with the
                // host's current mode (ArbiterLauncher).
                if (Native.IsMono) return;
                if (!ShouldAddStackTraceForDesyncLog()) return;
                switch (Multiplayer.settings?.desyncTracingMode)
                {
                    case DesyncTracingMode.Fast:
                        FastTrace();
                        break;
                    case DesyncTracingMode.Slow:
                        ManagedTrace();
                        break;
                    case DesyncTracingMode.Lean:
                        if (HashManagedFrames) ManagedTrace();
                        else LeanTrace();
                        break;
                }
                traceCalls++; // keeps the calls above out of tail position
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

        // -mptracemode=None|Fast|Slow|Lean sets the desync tracing mode for this run only; desyncTracingMode is not in the
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
                Log.Warning($"Multiplayer: -{TraceModeArg}={value} is not None, Fast, Slow or Lean; tracing mode stays " +
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

        // skipFrames: this frame and Postfix by default, like the Mono walk skips Postfix
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ManagedTrace(int skipFrames = 2)
        {
            var logItem = StackTraceLogItemManaged.GetFromPool();
            var frames = new StackTrace(skipFrames, false).GetFrames();
            int depth = 0;

            foreach (var frame in frames)
            {
                if (depth == StackTraceLogItemRaw.MaxDepth) break;

                // Harmony's replacement for a patched method shows as a dynamic method; on CoreCLR Harmony maps it
                // back to the original
                logItem.methods[depth++] = Harmony.GetMethodFromStackframe(frame);
            }

            AddManagedTrace(logItem, depth);
        }

        private static void AddManagedTrace(StackTraceLogItemManaged logItem, int depth)
        {
            int hash = 0;
            bool hashFrames = HashManagedFrames;

            if (hashFrames)
                for (int i = 0; i < depth; i++)
                {
                    var method = logItem.methods[i];
                    if (method == null) continue;

                    if (!managedNameHashes.TryGetValue(method, out int nameHash))
                        managedNameHashes[method] = nameHash = GenText.StableStringHash(method.FullDescription());

                    hash = Gen.HashCombineInt(hash, nameHash);
                }

            // The depth varies with inlining like the frames do
            Multiplayer.game.sync.TryAddStackTraceForDesyncLogRaw(logItem, depth, hash, hashDepth: hashFrames);
        }

        static bool fastTraceDisabled;
        static int traceCalls;
        const int MaxIncompleteInARow = 256;

        // Fast on CoreCLR: FastManagedTrace gives the frames ManagedTrace would give, from a cache keyed by the return
        // addresses. A stack it can't vouch for takes ManagedTrace. Where the unwinder can't run (not Windows x64) or
        // after an unexpected error, Fast records Lean entries: same entries and hashes as the other modes, no stack.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FastTrace()
        {
            if (fastTraceDisabled || !FastManagedTrace.Supported)
            {
                FastFallback();
                traceCalls++; // keeps FastFallback out of tail position: it skips this frame
                return;
            }

            MethodBase[] frames;
            try
            {
                // Skips this frame and Postfix, like ManagedTrace
                frames = FastManagedTrace.Capture(2);
            }
            catch (Exception e)
            {
                fastTraceDisabled = true;
                Log.Error($"Multiplayer: Fast desync tracing failed, Fast records Lean traces from now on: {e}");
                FastFallback();
                return;
            }

            if (frames == null)
            {
                // The walk did not reach the end of the stack. If that keeps happening (a frame without unwind info on
                // this machine), every call would pay the walk and a StackTrace: stop trying.
                if (FastManagedTrace.IncompleteInARow >= MaxIncompleteInARow)
                {
                    fastTraceDisabled = true;
                    Log.Warning($"Multiplayer: Fast desync tracing can't walk this stack ({FastManagedTrace.Stats}); " +
                                "Fast records Lean traces from now on");
                }
                ManagedTrace(3);
                traceCalls++; // keeps ManagedTrace(3) out of tail position: a tail call would drop this frame
                return;
            }

            var logItem = StackTraceLogItemManaged.GetFromPool();
            Array.Copy(frames, logItem.methods, frames.Length);
            AddManagedTrace(logItem, frames.Length);
        }

        // Fast where it can't capture: Lean entries, or Slow ones with -mptraceframehash (Lean hashes nothing)
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FastFallback()
        {
            // Called from FastTrace: skip this frame, FastTrace and Postfix
            if (HashManagedFrames) ManagedTrace(4);
            else LeanTrace();
            traceCalls++; // keeps ManagedTrace(4) out of tail position
        }

        public const string LeanInfo = "Lean trace: no stack";

        // FIXES row 267: a trace entry without a stack. Same hash as ManagedTrace without -mptraceframehash
        // (hashIn 0, hashDepth false: the random state alone), same context fields (tick, thing, faction, rng state).
        private static void LeanTrace()
        {
            Multiplayer.game.sync.TryAddStackTraceForDesyncLogRaw(StackTraceLogItemRaw.GetFromPool(), 0, 0, LeanInfo,
                hashDepth: false);
        }

        public static bool ShouldAddStackTraceForDesyncLog()
        {
            // 1.7 lab, FIXES row 152: only the simulation's own thread adds traces. A background job that draws Rand while
            // the map ticks (Map Preview's preview thread, lab F1) entered one PC's trace list and ended the session.
            if (!UnityData.IsInMainThread) return false;
            if (!SupportsDeferredStackTracing) return false;
            if (Multiplayer.Client == null) return false;
            if (Multiplayer.settings.desyncTracingMode == DesyncTracingMode.None) return false;
            if (Multiplayer.game == null) return false;

            // The opinion being built is full (SyncOpinion.MaxTraceHashes): the trace would be dropped, so don't
            // pay for capturing it
            if (Multiplayer.game.sync.DropTraceAtCap()) return false;

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

    // 1.7 lab row 78: on-screen messages and letters take an id inside the tick only on a PC that shows them (never on the
    // headless arbiter), so their GetNextID trace shifted every later trace and flagged a desync with identical game state
    [HarmonyPatch(typeof(UniqueIDsManager), nameof(UniqueIDsManager.GetNextMessageID))]
    static class MessageIdTraceIgnore
    {
        static void Prefix() => DeferredStackTracing.ignoreTraces++;
        static void Finalizer() => DeferredStackTracing.ignoreTraces--;
    }

    [HarmonyPatch(typeof(UniqueIDsManager), nameof(UniqueIDsManager.GetNextLetterID))]
    static class LetterIdTraceIgnore
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
