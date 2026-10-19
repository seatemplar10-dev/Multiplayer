using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LudeonTK;
using Verse;

namespace Multiplayer.Client.Patches
{
    // C13 glue (Ares FIXES row 114): when to discover and what "outside the simulation" means; the guard is C13Core, the rules C13Plan.
    //   Discovery runs when a Multiplayer session is created (before a joiner/arbiter loads the game, so load-time skips such as
    //   LifeStageWorker_Patch are covered) and again whenever Harmony's patch set changed later: a postfix on HarmonyLib's internal
    //   PatchFunctions.UpdateWrapper (the one place every Patch/Unpatch/PatchAll goes through) sets a flag and suspends a gated
    //   postfix that landed on a method without a frame; the rescan runs on the main thread from OnMainThread.Update outside long
    //   events, at most every 2 s, and is retried after a failure. One summary line per pass whose result changed.
    //   Nothing is patched before the first Multiplayer session, so single player without Multiplayer ever connected is untouched.
    //   After a session the patches stay: they cannot gate (Multiplayer.Client == null), but the methods keep the replacements
    //   rebuilt at session start (every transpiler on them ran again then, and they carry our try/finalizer).
    public static class InterfaceSkipPostfixGuard
    {
        // Dev switches for A/B in the lab only: they are per PC, so switching the guard off on one PC of a session splits it by design
        [TweakValue("Multiplayer")] public static bool enabled = true;
        [TweakValue("Multiplayer")] public static bool logFirstGate = true;

        static volatile bool dirty, applying;
        static bool started, initDone, leakLogged, callerLogged;
        static int applyingThread = -1, failures;
        static string lastSummary;
        static long lastScanMs = -RescanIntervalMs;
        const long RescanIntervalMs = 2000;   // a mod that patches every frame (profilers, runtime toggles) must not cost a scan per frame

        public static void OnSessionStart()
        {
            started = true;
            Discover("session start");
        }

        // OnMainThread.Update, every frame while connected
        public static void Update()
        {
            C13Core.Active = enabled;
            if (C13Core.ResetIfLeaked() && !leakLogged)
            {
                leakLogged = true;
                Log.Error("MP C13 postfix guard: call frames still open at OnMainThread.Update - reset (logged once)");
            }
            if (C13Core.SkipCallerUnknown > 0 && !callerLogged)
            {
                callerLogged = true;
                Log.Warning("MP C13 postfix guard: a stack walk could not name the caller of a Multiplayer prefix skip; the innermost call frame was used (logged once)");
            }
            if (!dirty || !started || applying) return;
            if (LongEventHandler.currentEvent != null) return;   // patch only between long events (loading can run off the main thread)
            if (Multiplayer.clock.ElapsedMilliseconds - lastScanMs < RescanIntervalMs) return;
            Discover("patches changed");
        }

        static void Init()
        {
            initDone = true;
            C13Core.NonSimMpContext = () => Multiplayer.Client != null && !Multiplayer.Ticking && !Multiplayer.ExecutingCmds;
            C13Core.LogWarning = s => Log.Warning(s);
            C13Core.OnFirstGate = (t, p) =>
            {
                if (logFirstGate) Log.Message($"MP C13 postfix guard: first skip of {C13Core.Name(p)} on {C13Core.Name(t)} (interface call)");
            };

            var uw = AccessTools.Method("HarmonyLib.PatchFunctions:UpdateWrapper");
            if (uw == null) { Log.Warning("MP C13 postfix guard: HarmonyLib.PatchFunctions.UpdateWrapper not found, later patches are only seen at the next session start"); return; }
            try { C13Core.Harmony.Patch(uw, postfix: new HarmonyMethod(typeof(InterfaceSkipPostfixGuard), nameof(UpdateWrapperPostfix))); }
            catch (Exception e) { Log.Warning("MP C13 postfix guard: could not watch later patches: " + e.Message); }
        }

        // Any thread. Our own patching during a pass is ignored; anything else marks the guard dirty and, if a gated postfix just
        // landed on a method without a frame, suspends that postfix until the rescan frames it.
        static void UpdateWrapperPostfix(MethodBase original, PatchInfo patchInfo)
        {
            if (applying && Environment.CurrentManagedThreadId == applyingThread) return;
            dirty = true;
            try
            {
                C13Core.NotePatchChange(original,
                    patchInfo?.prefixes?.Select(p => (p.owner, (MethodBase)p.PatchMethod)),
                    patchInfo?.postfixes?.Select(p => (p.owner, (MethodBase)p.PatchMethod)));
            }
            catch { }
        }

        public static void Discover(string reason)
        {
            if (!enabled) return;
            if (!UnityData.IsInMainThread) { dirty = true; return; }
            if (applying) return;

            applying = true;
            applyingThread = Environment.CurrentManagedThreadId;
            dirty = false;
            lastScanMs = Multiplayer.clock.ElapsedMilliseconds;
            try
            {
                if (!initDone) Init();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var res = C13Core.Discover(reason,
                    m => m.DeclaringType == typeof(SyncTemplates),
                    m => Sync.methodBaseToInternalId.TryGetValue(m, out int id) ? id : -1);
                watch.Stop();
                foreach (var e in res.apply.errors) Log.Warning("MP C13 postfix guard: " + e);
                // compare without the pass name: log only when the guard changed
                var key = res.summary.Substring(res.summary.IndexOf("):", StringComparison.Ordinal));
                if (key != lastSummary) { lastSummary = key; Log.Message(res.summary + $" [{watch.ElapsedMilliseconds} ms]"); }
            }
            catch (Exception e)
            {
                dirty = true;   // retried by Update after RescanIntervalMs
                if (++failures <= 3) Log.Error("MP C13 postfix guard: discovery failed (" + reason + ", retried; logged 3 times at most): " + e);
            }
            finally
            {
                applying = false;
            }
        }
    }
}
