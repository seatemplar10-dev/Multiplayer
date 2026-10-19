using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace Multiplayer.Client.Patches
{
    // C13 runtime (Ares FIXES row 114). Harmony runs every postfix of a method even when a prefix skipped the original, so another
    // mod's postfix on a method Multiplayer blocks in interface code (or intercepts as a sync method, whose original then runs later
    // inside the synced command on every PC) still ran in UI on the PC that called it: VAspirE completed aspirations on the host only
    // (row 110). This gates those postfixes FOR THAT CALL ONLY:
    //   Enter (first prefix) / Exit (last finalizer) keep a per-thread stack of calls of every method that has a gated postfix,
    //   MpPrefixResult (postfix on each Multiplayer bool prefix) marks the current call when that prefix returned false outside the
    //   simulation, OnSyncIntercepted (SyncTemplates.General) marks it when the call was turned into a sync command,
    //   Arm (first postfix of a guarded target) arms the mark once the original is known to be skipped,
    //   Gate (prefix on each gated postfix METHOD) skips that postfix only when the innermost call is armed and the postfix belongs
    //   to that target. A postfix method shared by several targets (VAspirE CheckGeneral: 14 targets) runs normally for the
    //   others, nested guarded calls each have their own frame.
    // Never consumes Rand; never gates in a tick, a command or outside Multiplayer (the context check is NonSimMpContext and the
    // sync intercept only happens in interface code). Kept free of game types so the test project runs it against the lab Harmony.
    public static class C13Core
    {
        public const int FlagMpSkipped = 1, FlagSyncIntercepted = 2, FlagArmed = 4;

        // Multiplayer.Client != null && !Ticking && !ExecutingCmds (the condition of Multiplayer's interface-skip prefixes).
        public static Func<bool> NonSimMpContext = () => false;
        public static volatile bool Active = true;   // kill switch read by Gate (dev A/B; per PC, like SimGuards' switches)
        public static Action<string> LogWarning = _ => { };
        public static Action<MethodBase, MethodBase> OnFirstGate;   // (target, postfix), once per pair

        static Harmony harmony;
        public static Harmony Harmony => harmony ??= new Harmony(C13Plan.GuardOwner);

        sealed class Tables
        {
            public Dictionary<MethodBase, HashSet<MethodBase>> gatedByTarget = new();     // guarded target -> its gated postfix methods
            public Dictionary<MethodBase, HashSet<MethodBase>> targetsByMpPrefix = new(); // hooked MP bool prefix -> guarded targets
            public Dictionary<MethodBase, int> syncIdByTarget = new();                     // guarded sync target -> Sync internal id
        }
        // Rebuilt from scratch by every pass: a pair that a later pass no longer gates (e.g. the mod added a paired prefix) is
        // un-gated at once. The patches below stay, but without a table entry they never skip anything.
        static volatile Tables tables = new();

        // What is patched (only ever grows; the main thread is the only writer). Snapshots for other threads are published at
        // the end of Apply.
        static readonly HashSet<MethodBase> gatedPostfixMethods = new();
        static readonly HashSet<MethodBase> hookedMpPrefixes = new();
        static readonly HashSet<MethodBase> bracketed = new();
        static readonly HashSet<MethodBase> armed = new();
        static readonly HashSet<(MethodBase, MethodBase)> firstGateSeen = new();
        static volatile HashSet<MethodBase> gatedSnapshot = new(), bracketedSnapshot = new();

        // Gated postfix methods that currently have a target WITHOUT a frame (added by a mod after the last pass, or a bracket that
        // failed): such a call would see the enclosing frame, so these postfixes are not gated until a pass frames every target.
        // A Gate on a foreign postfix cannot check its caller by stack walk: the target may call it as a tail call (fact F15b).
        static volatile HashSet<MethodBase> suspended = new();
        static readonly object suspendLock = new();

        public static int GateSkips;          // postfix calls skipped so far (all threads; diagnostics only)
        public static int SkipCallerUnknown;  // MP prefix skips whose caller the stack walk could not name (frame used instead)
        internal static bool verifySkipCaller = true;   // test switch: off = attribute a skip to the innermost frame unchecked

        public struct Frame
        {
            public MethodBase original;
            public int flags;
        }

        [ThreadStatic] static Frame[] frames;
        [ThreadStatic] static int depth;

        public static int Depth => depth;
        public static int TopFlags => depth > 0 ? frames[depth - 1].flags : 0;

        static readonly MethodInfo m_Enter = AccessTools.Method(typeof(C13Core), nameof(Enter));
        static readonly MethodInfo m_Exit = AccessTools.Method(typeof(C13Core), nameof(Exit));
        static readonly MethodInfo m_Arm = AccessTools.Method(typeof(C13Core), nameof(Arm));
        static readonly MethodInfo m_Gate = AccessTools.Method(typeof(C13Core), nameof(Gate));
        static readonly MethodInfo m_MpPrefixResult = AccessTools.Method(typeof(C13Core), nameof(MpPrefixResult));
        static readonly MethodInfo m_Noop = AccessTools.Method(typeof(C13Core), nameof(Noop));

        // ---- patch methods ----

        // First prefix (int.MaxValue) of every bracketed method. __state = depth + 1 so that 0 means "Enter did not run".
        public static void Enter(MethodBase __originalMethod, out int __state)
        {
            var f = frames;
            if (f == null) frames = f = new Frame[16];
            else if (depth == f.Length) { Array.Resize(ref frames, depth * 2); f = frames; }
            f[depth] = new Frame { original = __originalMethod };
            depth++;
            __state = depth;
        }

        // Last finalizer (int.MinValue): also runs when the original or a patch threw.
        public static void Exit(int __state)
        {
            if (__state > 0 && depth >= __state)
            {
                for (int i = __state - 1; i < depth; i++) frames[i].original = null;
                depth = __state - 1;
            }
        }

        // First postfix (int.MaxValue) of a guarded target: from here to Exit only postfixes and finalizers of this call run.
        public static void Arm(MethodBase __originalMethod, bool __runOriginal)
        {
            int d = depth;
            if (d == 0) return;
            ref var f = ref frames[d - 1];
            if (!ReferenceEquals(f.original, __originalMethod)) return;
            if ((f.flags & FlagMpSkipped) != 0 && !__runOriginal || (f.flags & FlagSyncIntercepted) != 0)
                f.flags |= FlagArmed;
        }

        // Postfix on a Multiplayer bool prefix: __result is the prefix's return value (false = Harmony skips the original).
        public static void MpPrefixResult(bool __result, MethodBase __originalMethod)
        {
            if (__result) return;
            int d = depth;
            if (d == 0) return;
            if (!NonSimMpContext()) return;   // a skip in a tick or command is the simulation's own business (same on every PC)
            var top = frames[d - 1].original;
            if (top == null || !tables.targetsByMpPrefix.TryGetValue(__originalMethod, out var targets) || !targets.Contains(top)) return;
            // The same Multiplayer prefix can sit on methods without a frame (DesignatorPatches.DesignateSingleCell: 83 targets); if it
            // skipped one of those nested in this call's prefix phase, the innermost frame is not its caller. A prefix is never a
            // tail call, so the frame above its replacement is the method it runs for (fact F15a). Slow path only (a skip in UI).
            if (verifySkipCaller)
            {
                var caller = CallerOfPatch(__originalMethod);
                if (caller == null) System.Threading.Interlocked.Increment(ref SkipCallerUnknown);
                else if (!caller.Equals(top)) return;
            }
            frames[d - 1].flags |= FlagMpSkipped;
        }

        // The original whose replacement called the replacement of `patchMethod` (null if not found within a few frames).
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static MethodBase CallerOfPatch(MethodBase patchMethod)
        {
            bool seen = false;
            for (int n = 1; n <= 12; n++)
            {
                MethodBase o;
                try { o = Harmony.GetOriginalMethodFromStackframe(new System.Diagnostics.StackFrame(n, false)); }
                catch { return null; }
                if (o == null) continue;
                if (!seen) { if (o.Equals(patchMethod)) seen = true; continue; }
                if (o.Equals(patchMethod)) continue;
                return o;
            }
            return null;
        }

        // Called by SyncTemplates.General after it turned the call into a sync command (interface code only).
        public static void OnSyncIntercepted(int internalId)
        {
            int d = depth;
            if (d == 0) return;
            ref var f = ref frames[d - 1];
            if (f.original != null && tables.syncIdByTarget.TryGetValue(f.original, out int id) && id == internalId)
                f.flags |= FlagSyncIntercepted;
        }

        // Prefix on a gated postfix method (__originalMethod = that postfix method). false = skip its body for this call.
        public static bool Gate(MethodBase __originalMethod)
        {
            int d = depth;
            if (d == 0 || !Active) return true;
            ref var f = ref frames[d - 1];
            if ((f.flags & FlagArmed) == 0) return true;
            var t = tables;
            if (f.original == null || !t.gatedByTarget.TryGetValue(f.original, out var set) || !set.Contains(__originalMethod)) return true;
            if (suspended.Contains(__originalMethod)) return true;
            System.Threading.Interlocked.Increment(ref GateSkips);
            if (OnFirstGate != null)
            {
                bool first;
                lock (firstGateSeen) first = firstGateSeen.Add((f.original, __originalMethod));
                if (first) OnFirstGate(f.original, __originalMethod);
            }
            return false;
        }

        public static void Noop() { }

        // Harmony changed the patches of `original` (any thread; from the UpdateWrapper hook). Until the next pass, a gated postfix
        // is suspended when it now sits on a method without a frame, or when its mod just gave this guarded target a prefix (a pair
        // that C13Plan would not gate). Patch lists are (owner, method).
        public static void NotePatchChange(MethodBase original, IEnumerable<(string owner, MethodBase method)> prefixes,
            IEnumerable<(string owner, MethodBase method)> postfixes)
        {
            var gated = gatedSnapshot;
            if (gated.Count == 0 || original == null || postfixes == null) return;
            List<MethodBase> add = null;
            if (!bracketedSnapshot.Contains(original))
            {
                foreach (var (_, p) in postfixes)
                    if (p != null && gated.Contains(p) && !suspended.Contains(p)) (add ??= new List<MethodBase>()).Add(p);
            }
            else if (tables.gatedByTarget.TryGetValue(original, out var set) && prefixes != null)
            {
                var prefixOwners = new HashSet<string>(prefixes.Select(x => x.owner));
                foreach (var (owner, p) in postfixes)
                    if (p != null && set.Contains(p) && prefixOwners.Contains(owner) && !C13Plan.ReviewedPairs.Contains(p.DeclaringType?.FullName ?? "")
                        && !suspended.Contains(p))
                        (add ??= new List<MethodBase>()).Add(p);
            }
            if (add == null) return;
            lock (suspendLock)
            {
                var s = new HashSet<MethodBase>(suspended);
                s.UnionWith(add);
                suspended = s;
            }
        }

        public static int SuspendedCount => suspended.Count;

        // Safety net, called at a top-level point of the calling thread (OnMainThread.Update): no patched call is open there, so a
        // non-empty stack is a leak. Resetting only drops frames, which can only turn gating off (fail-open), never on.
        // Harmony runs our int.MinValue finalizer even when an earlier finalizer throws (facts F6/F6b), so this should never fire.
        public static bool ResetIfLeaked()
        {
            if (depth == 0) return false;
            for (int i = 0; i < depth; i++) frames[i].original = null;
            depth = 0;
            return true;
        }

        // ---- applying a plan (main thread only) ----

        public sealed class GuardedTarget
        {
            public MethodBase target;
            public List<MethodInfo> gatedPostfixes = new();
            public List<MethodInfo> mpBoolPrefixes = new();   // only for the prefix channel
            public int syncId = -1;                           // only for the sync channel
        }

        public sealed class ApplyResult
        {
            public int newGates, newMpHooks, newBrackets, newArms, rebuilt, bracketedTotal, hookedTotal, gatedTotal, suspended;
            public int guardedTargets, gatedPairs;   // what the runtime tables hold after this pass
            public List<string> errors = new();
            public List<string> ties = new();
            public HashSet<MethodInfo> failedPostfixes = new();
        }

        // targetsOfPostfix: every method that has this postfix method as a postfix (all of them get a frame, so a shared postfix
        // called by an unguarded target nested inside a guarded call is not gated).
        public static ApplyResult Apply(List<GuardedTarget> guarded, Func<MethodInfo, IEnumerable<MethodBase>> targetsOfPostfix)
        {
            var r = new ApplyResult();
            var h = Harmony;

            // 1. gate the postfix methods (before any target is rebuilt: a replacement built earlier may have the postfix inlined)
            var touched = new HashSet<MethodBase>();   // methods whose replacement must be rebuilt after this step
            foreach (var p in guarded.SelectMany(g => g.gatedPostfixes).Distinct())
            {
                if (gatedPostfixMethods.Contains(p)) continue;
                try
                {
                    h.Patch(p, prefix: new HarmonyMethod(m_Gate) { priority = int.MaxValue });
                    gatedPostfixMethods.Add(p);
                    r.newGates++;
                    foreach (var o in targetsOfPostfix(p)) touched.Add(o);
                }
                catch (Exception e) { r.failedPostfixes.Add(p); r.errors.Add("gate " + Name(p) + ": " + e.Message); }
            }

            // 2. hook Multiplayer's bool prefixes (a newly hooked prefix is only seen by targets rebuilt after this step)
            foreach (var g in guarded)
                foreach (var x in g.mpBoolPrefixes)
                {
                    if (hookedMpPrefixes.Contains(x)) continue;   // hooked by an earlier pass, and its targets were rebuilt then
                    try
                    {
                        h.Patch(x, postfix: new HarmonyMethod(m_MpPrefixResult) { priority = int.MaxValue });
                        hookedMpPrefixes.Add(x);
                        r.newMpHooks++;
                    }
                    catch (Exception e) { r.errors.Add("hook " + Name(x) + ": " + e.Message); }
                    touched.Add(g.target);
                }

            // 3. tables for the runtime, from THIS pass only (published before the brackets that read them exist)
            var nt = new Tables();
            foreach (var g in guarded)
            {
                var ok = g.gatedPostfixes.Where(p => gatedPostfixMethods.Contains(p)).Cast<MethodBase>().ToList();
                if (ok.Count == 0) continue;
                nt.gatedByTarget[g.target] = new HashSet<MethodBase>(ok);
                foreach (var x in g.mpBoolPrefixes.Where(hookedMpPrefixes.Contains))
                {
                    if (!nt.targetsByMpPrefix.TryGetValue(x, out var s)) nt.targetsByMpPrefix[x] = s = new HashSet<MethodBase>();
                    s.Add(g.target);
                }
                if (g.syncId >= 0) nt.syncIdByTarget[g.target] = g.syncId;
            }
            tables = nt;

            // 4. frames on every guarded target and on every other target of a gated postfix; Arm on the guarded ones
            var needFrame = new HashSet<MethodBase>(nt.gatedByTarget.Keys);
            foreach (var p in gatedPostfixMethods.OfType<MethodInfo>())
                foreach (var o in targetsOfPostfix(p)) needFrame.Add(o);
            foreach (var o in needFrame)
            {
                bool wantArm = nt.gatedByTarget.ContainsKey(o);
                try
                {
                    if (!bracketed.Contains(o))
                    {
                        h.Patch(o,
                            prefix: new HarmonyMethod(m_Enter) { priority = int.MaxValue },
                            postfix: wantArm ? new HarmonyMethod(m_Arm) { priority = int.MaxValue } : null,
                            finalizer: new HarmonyMethod(m_Exit) { priority = int.MinValue });
                        bracketed.Add(o);
                        if (wantArm) { armed.Add(o); r.newArms++; }
                        r.newBrackets++;
                        touched.Remove(o);   // just rebuilt
                    }
                    else if (wantArm && !armed.Contains(o))
                    {
                        h.Patch(o, postfix: new HarmonyMethod(m_Arm) { priority = int.MaxValue });
                        armed.Add(o);
                        r.newArms++;
                        touched.Remove(o);
                    }
                }
                catch (Exception e) { r.errors.Add("bracket " + Name(o) + ": " + e.Message); }
            }

            // 5. rebuild what still holds a replacement built before step 1/2 (a gate or hook is only seen by a rebuilt caller)
            foreach (var o in touched)
            {
                if (!bracketed.Contains(o)) continue;   // not ours to rebuild: its postfix is only gated where we have frames
                try
                {
                    h.Patch(o, prefix: new HarmonyMethod(m_Noop));
                    h.Unpatch(o, m_Noop);
                    r.rebuilt++;
                }
                catch (Exception e) { r.errors.Add("rebuild " + Name(o) + ": " + e.Message); }
            }

            // 6. a target whose frame or Arm could not be added is not guarded; a gated postfix with a target without a frame is
            //    suspended (fail-open) until a later pass frames it
            var final = new Tables();
            foreach (var kv in nt.gatedByTarget)
                if (bracketed.Contains(kv.Key) && armed.Contains(kv.Key)) final.gatedByTarget[kv.Key] = kv.Value;
            foreach (var kv in nt.targetsByMpPrefix)
            {
                var s = new HashSet<MethodBase>(kv.Value.Where(final.gatedByTarget.ContainsKey));
                if (s.Count > 0) final.targetsByMpPrefix[kv.Key] = s;
            }
            foreach (var kv in nt.syncIdByTarget)
                if (final.gatedByTarget.ContainsKey(kv.Key)) final.syncIdByTarget[kv.Key] = kv.Value;
            var susp = new HashSet<MethodBase>();
            foreach (var p in gatedPostfixMethods.OfType<MethodInfo>())
                if (targetsOfPostfix(p).Any(o => !bracketed.Contains(o))) susp.Add(p);
            lock (suspendLock) suspended = susp;
            tables = final;
            gatedSnapshot = new HashSet<MethodBase>(gatedPostfixMethods);
            bracketedSnapshot = new HashSet<MethodBase>(bracketed);
            r.suspended = susp.Count;
            r.guardedTargets = final.gatedByTarget.Count;
            r.gatedPairs = final.gatedByTarget.Values.Sum(s => s.Count);

            // 7. priority ties: a foreign int.MaxValue prefix/postfix registered before ours runs before Enter/Arm (its postfix is
            //    then not gated), a foreign int.MinValue finalizer runs after Exit; reported, not fixed
            foreach (var o in final.gatedByTarget.Keys)
            {
                var info = Harmony.GetPatchInfo(o);
                if (info == null) continue;
                if (info.Prefixes.Any(p => p.owner != C13Plan.GuardOwner && p.priority == int.MaxValue) ||
                    info.Postfixes.Any(p => p.owner != C13Plan.GuardOwner && p.priority == int.MaxValue) ||
                    info.Finalizers.Any(p => p.owner != C13Plan.GuardOwner && p.priority == int.MinValue))
                    r.ties.Add(Name(o));
            }

            r.bracketedTotal = bracketed.Count;
            r.hookedTotal = hookedMpPrefixes.Count;
            r.gatedTotal = gatedPostfixMethods.Count;
            return r;
        }

        // ---- discovery: scan every patched method, decide (C13Plan), apply ----

        public sealed class DiscoverResult
        {
            public List<C13Plan.Decision> decisions = new();
            public ApplyResult apply;
            public string summary;
        }

        // isSyncTemplate: is this transpiler Multiplayer's SyncTemplates.Transpiler; syncIdOf: Sync internal id of a method or -1
        public static DiscoverResult Discover(string reason, Func<MethodInfo, bool> isSyncTemplate, Func<MethodBase, int> syncIdOf)
        {
            var res = new DiscoverResult();
            var targetsOfPostfix = new Dictionary<MethodInfo, List<MethodBase>>();
            foreach (var original in Harmony.GetAllPatchedMethods().ToList())
            {
                var info = Harmony.GetPatchInfo(original);
                if (info == null) continue;
                foreach (var p in info.Postfixes)
                    if (!C13Plan.IsMpFamily(p.owner) && p.PatchMethod != null)
                    {
                        if (!targetsOfPostfix.TryGetValue(p.PatchMethod, out var l)) targetsOfPostfix[p.PatchMethod] = l = new List<MethodBase>();
                        l.Add(original);
                    }

                var t = new C13Plan.TargetEntry
                {
                    original = original,
                    name = Name(original),
                    returnsVoid = original is not MethodInfo mi || mi.ReturnType == typeof(void),
                };
                AddPatches(t, "prefix", info.Prefixes, isSyncTemplate);
                AddPatches(t, "postfix", info.Postfixes, isSyncTemplate);
                AddPatches(t, "transpiler", info.Transpilers, isSyncTemplate);
                AddPatches(t, "finalizer", info.Finalizers, isSyncTemplate);
                int syncId = t.patches.Any(p => p.isSyncTemplate) ? syncIdOf(original) : -1;
                t.syncIdKnown = syncId >= 0;
                var d = C13Plan.Decide(t);
                if (d == null || d.gated.Count == 0 && d.notGated.Count == 0) continue;
                d.syncId = syncId;
                res.decisions.Add(d);
            }

            var guarded = new List<GuardedTarget>();
            foreach (var d in res.decisions.Where(d => d.Guarded))
            {
                var g = new GuardedTarget { target = (MethodBase)d.target.original };
                g.gatedPostfixes.AddRange(d.gated.Select(p => (MethodInfo)p.method));
                if (d.prefixChannel) g.mpBoolPrefixes.AddRange(d.mpBoolPrefixes.Select(p => (MethodInfo)p.method));
                if (d.syncChannel) g.syncId = d.syncId;
                guarded.Add(g);
            }
            res.apply = Apply(guarded, p => targetsOfPostfix.TryGetValue(p, out var l) ? l : Enumerable.Empty<MethodBase>());
            res.summary = Summary(reason, res.decisions, res.apply);
            return res;
        }

        static void AddPatches(C13Plan.TargetEntry t, string kind, IEnumerable<HarmonyLib.Patch> patches, Func<MethodInfo, bool> isSyncTemplate)
        {
            foreach (var p in patches)
            {
                var m = p.PatchMethod;
                if (m == null) continue;
                t.patches.Add(new C13Plan.PatchEntry
                {
                    kind = kind,
                    owner = p.owner,
                    method = m,
                    name = Name(m),
                    declaringType = m.DeclaringType?.FullName,
                    returnsVoid = m.ReturnType == typeof(void),
                    returnsBool = m.ReturnType == typeof(bool),
                    isGeneric = m.IsGenericMethodDefinition || (m.DeclaringType?.ContainsGenericParameters ?? false),
                    isSyncTemplate = kind == "transpiler" && isSyncTemplate(m),
                });
            }
        }

        public static string Summary(string reason, List<C13Plan.Decision> decisions, ApplyResult r)
        {
            var guarded = decisions.Where(d => d.Guarded).ToList();
            var excluded = decisions.SelectMany(d => d.notGated).ToList();
            var sb = new System.Text.StringBuilder();
            sb.Append($"MP C13 postfix guard ({reason}): {r.gatedPairs} postfixes gated on {r.guardedTargets} targets ");
            sb.Append($"(planned {guarded.Sum(d => d.gated.Count)} on {guarded.Count}: prefix-skip {guarded.Count(d => d.prefixChannel)}, sync-intercept {guarded.Count(d => d.syncChannel)}; ");
            sb.Append($"{r.hookedTotal} Multiplayer prefixes hooked, {r.bracketedTotal} methods with frames, {r.rebuilt} rebuilt, ");
            sb.Append($"{r.suspended} suspended, {r.ties.Count} priority ties{(r.ties.Count > 0 ? " [" + string.Join(", ", r.ties) + "]" : "")}, {r.errors.Count} errors); ");
            sb.Append($"not gated {excluded.Count} (");
            sb.Append(string.Join(", ", excluded.GroupBy(e => e.reason).Select(g => g.Key + " " + g.Count())));
            sb.Append("). GATED: ");
            sb.Append(string.Join("; ", guarded.Select(d =>
                d.target.name + (d.prefixChannel ? " [skip]" : "") + (d.syncChannel ? " [sync]" : "") + " <- " +
                string.Join(", ", d.gated.Select(p => p.owner + ":" + p.name)))));
            sb.Append(". NOT GATED: ");
            sb.Append(string.Join("; ", decisions.Where(d => d.notGated.Count > 0).Select(d =>
                d.target.name + " <- " + string.Join(", ", d.notGated.Select(n => n.patch.owner + ":" + n.patch.name + " (" + n.reason + ")")))));
            return sb.ToString();
        }

        public static bool IsOurPatchMethod(MethodBase m) =>
            m == m_Enter || m == m_Exit || m == m_Arm || m == m_Gate || m == m_MpPrefixResult || m == m_Noop;

        public static string Name(MethodBase m) => m == null ? "null" : (m.DeclaringType?.FullName ?? "?") + "::" + m.Name;
    }
}
