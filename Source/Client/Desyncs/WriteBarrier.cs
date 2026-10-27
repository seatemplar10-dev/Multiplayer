using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Verse;

namespace Multiplayer.Client
{
    // 1.7 lab, desync-by-class task (PROMPT-2026-10-27-desync-by-class.md, lane dbc): one-PC write barrier.
    // -mplabwbar=<sites.tsv> (DetScan --writes; a relative path is from the save data folder) puts a call before every write
    // site the static scan found on a path from non-simulation code and logs each one that runs outside a simulation context,
    // with one stack per distinct caller. Counted, never logged: sim (Ticking, ExecutingCmds), load (Scribe loading), jpsave
    // (Scribe saving inside a join point, which every PC runs), nomp (no MP session). Logged: save (one PC's own save), reload
    // (per-PC code in a join point reload outside Scribe, row 253), longevent, interface (MP.InInterface), thread (not the main
    // thread), other. Kind "fin": a Finalizer on that method logs each exception that leaves it, with the patch classes on it
    // that have a Prefix and a Postfix but no Finalizer (row 252: Harmony skips every Postfix when the method throws).
    // Each frame (TickManagerUpdate prefix and postfix): Rand state stack depth and global ticksGame - world ticks, logged when
    // they change. Every 5,000 MP ticks: hit counts per site and context. Log: <save data>/mplab_wbar.log.
    // -mplabwplant=ui,tick: PlantOnGUI allocates a thing id every 600 frames from Map.MapUpdate (must log as interface; MP's
    // UniqueIdsPatch makes that id local) and every 600 ticks of map 0 from Map.MapPostTick (must log nothing). Give every PC
    // the same -mplabwplant: the tick plant moves the real thing id counter. No flag = nothing patched.
    public static class WriteBarrier
    {
        public const string Arg = "mplabwbar", PlantArg = "mplabwplant";
        public static readonly bool On = GenCommandLine.TryGetCommandLineArg(Arg, out _);
        public static readonly bool PlantUi = GenCommandLine.TryGetCommandLineArg(PlantArg, out var p) && p?.Contains("ui") == true;
        public static readonly bool PlantTick = GenCommandLine.TryGetCommandLineArg(PlantArg, out var p2) && p2?.Contains("tick") == true;
        static readonly string[] ctxNames = { "sim", "load", "jpsave", "nomp", "save", "reload", "longevent", "interface", "thread", "other" };
        const int FirstLogged = 4, NCtx = 10;
        static readonly object sync = new();
        static readonly Harmony harmony = new("multiplayer.lab.wbar");
        static StreamWriter log;
        static readonly List<string> siteInfo = new();
        static int[] hits = new int[0], matched = new int[0];
        static readonly HashSet<(int, int, int)> stacksSeen = new();
        static readonly Dictionary<MethodBase, List<(int id, int op, string member)>> byMethod = new();
        static readonly Dictionary<(MethodBase, string), int> excSeen = new();
        static int frame, nextSummary = 5000, lastRandDepth, lastTickDelta;

        static int Context()
        {
            if (!UnityData.IsInMainThread) return 8;
            if (Multiplayer.Client == null) return 3;
            if (Multiplayer.Ticking || Multiplayer.ExecutingCmds) return 0;
            var mode = Scribe.mode;
            if (mode is LoadSaveMode.LoadingVars or LoadSaveMode.ResolvingCrossRefs or LoadSaveMode.PostLoadInit) return 1;
            if (mode == LoadSaveMode.Saving) return Multiplayer.reloading ? 2 : 4;
            if (Multiplayer.reloading) return 5;
            if (LongEventHandler.currentEvent != null) return 6;
            return Multiplayer.InInterface ? 7 : 9;
        }

        // Called by the instrumented IL right before the write. A stack is taken for the first 16 logged hits of a site in a
        // context and then at every power of two, so a write in a draw loop costs a counter, not a stack per frame.
        [MethodImpl(MethodImplOptions.NoInlining)]   // Report skips exactly 2 frames (Report, Hit)
        public static void Hit(int site)
        {
            int ctx = Context();
            int n = Interlocked.Increment(ref hits[site * NCtx + ctx]);
            if (ctx >= FirstLogged && (n <= 16 || (n & (n - 1)) == 0)) Report(site, ctx, n);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Report(int site, int ctx, int n)
        {
            var st = new StackTrace(2, false);
            var frames = Enumerable.Range(0, Math.Min(st.FrameCount, 30)).Select(i => FrameName(st.GetFrame(i))).ToList();
            int h = frames.Take(12).Aggregate(17, (a, f) => a * 31 + f.GetHashCode());
            lock (sync)
            {
                if (stacksSeen.Count > 50000 || !stacksSeen.Add((site, ctx, h))) return;
                Write($"HIT {ctxNames[ctx]} site {site} n {n} mp {TickPatch.Timer} frame {frame} | {siteInfo[site]}");
                foreach (var f in frames) Write("    at " + f);
            }
        }

        static string FrameName(StackFrame f)
        {
            var m = f?.GetMethod();
            return m == null ? "?" : m.DeclaringType == null ? m.Name : m.DeclaringType.FullName + "::" + m.Name;
        }

        static void Write(string s) { try { log?.WriteLine(s); } catch { } }

        // EarlyInit.LatePatches: after every mod's static constructors and Multiplayer's sync setup
        public static void Init()
        {
            if (!On || !GenCommandLine.TryGetCommandLineArg(Arg, out var path) || path.NullOrEmpty()) return;
            var watch = Stopwatch.StartNew();
            string file = Path.IsPathRooted(path) ? path : Path.Combine(GenFilePaths.SaveDataFolderPath, path);
            log = new StreamWriter(Path.Combine(GenFilePaths.SaveDataFolderPath, "mplab_wbar.log"), false) { AutoFlush = true };
            var rows = File.ReadAllLines(file).Skip(1).Select(l => l.Split('\t')).ToList();
            hits = new int[rows.Count * NCtx];
            matched = new int[rows.Count];
            var fin = new Dictionary<MethodBase, int>();
            int missing = 0, generic = 0;
            foreach (var r in rows)   // id kind score writer op member target dll mod
            {
                int id = siteInfo.Count;
                siteInfo.Add($"{r[1]} {r[6]} in {r[3]} ({r[8]}, score {r[2]})");
                var ms = Resolve(r[3]);
                if (ms.Count == 0) { missing++; Write($"NOT FOUND site {id}: {r[3]}"); continue; }
                foreach (var m in ms)
                {
                    if (m.ContainsGenericParameters) { generic++; continue; }   // shared generic bodies (repo CLAUDE.md)
                    if (r[1] == "fin") fin[m] = id;
                    else
                    {
                        if (!byMethod.TryGetValue(m, out var l)) byMethod[m] = l = new();
                        l.Add((id, Convert.ToInt32(r[4], 16), r[5]));
                    }
                }
            }
            Write($"INIT {rows.Count} sites from {file}: {byMethod.Count} methods to instrument, {fin.Count} finalizers, {missing} writers not found, {generic} generic skipped");
            Patch(fin);
            Log.Warning($"Multiplayer: -{Arg}: write barrier on {byMethod.Count} methods + {fin.Count} finalizers in {watch.ElapsedMilliseconds} ms, log mplab_wbar.log (lab probe, desync-by-class)");
        }

        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        static Dictionary<string, List<Type>> typesByName;

        // DetScan key "Ns.Type+Nested::Name/paramCount"; a type in two dlls (bundled copies) gives both
        static List<MethodBase> Resolve(string key)
        {
            if (typesByName == null)
            {
                typesByName = new();
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] ts;
                    try { ts = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { ts = e.Types.Where(t => t != null).ToArray(); } catch { continue; }
                    foreach (var t in ts)
                        if (t.FullName != null) { if (!typesByName.TryGetValue(t.FullName, out var l)) typesByName[t.FullName] = l = new(); l.Add(t); }
                }
            }
            // sites.tsv gives the bare name (no "/paramCount"): then every overload with a body
            int s = key.IndexOf("::", StringComparison.Ordinal), sl = key.LastIndexOf('/');
            if (s < 0) return new();
            string name = sl < s ? key.Substring(s + 2) : key.Substring(s + 2, sl - s - 2);
            int n = sl < s ? -1 : int.Parse(key.Substring(sl + 1));
            return typesByName.GetValueOrDefault(key.Substring(0, s))?.SelectMany(t => t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All)))
                .Where(m => m.Name == name && (n < 0 || m.GetParameters().Length == n) && m.GetMethodBody() != null).ToList() ?? new();
        }

        static void Patch(Dictionary<MethodBase, int> fin)
        {
            var tr = new HarmonyMethod(AccessTools.Method(typeof(WriteBarrier), nameof(Transpiler)));
            var fz = new HarmonyMethod(AccessTools.Method(typeof(WriteBarrier), nameof(Finalizer)));
            int failed = 0;
            foreach (var m in byMethod.Keys.Concat(fin.Keys).Distinct().ToList())
            {
                try { harmony.Patch(m, transpiler: byMethod.ContainsKey(m) ? tr : null, finalizer: fin.ContainsKey(m) ? fz : null); }
                catch (Exception e) { failed++; Write($"PATCH FAILED {Key(m)}: {e.GetType().Name}: {e.Message.Split('\n')[0]}"); }
            }
            int unmatched = 0;
            foreach (var l in byMethod.Values) foreach (var s in l) if (matched[s.id] == 0) { unmatched++; Write($"NO IL MATCH site {s.id}: op {s.op:X} {s.member} | {siteInfo[s.id]}"); }
            Write($"PATCHED {byMethod.Count + fin.Count - failed} methods, {failed} failed; {unmatched} sites without an IL match");
        }

        static string Key(MethodBase m) => (m.DeclaringType?.FullName ?? "?") + "::" + m.Name + "/" + m.GetParameters().Length;
        static string TypeKey(Type t) => t == null ? "" : (t.IsGenericType ? t.GetGenericTypeDefinition() : t).FullName;
        static string MemberKey(object o) => o switch
        {
            FieldInfo f => TypeKey(f.DeclaringType) + "::" + f.Name,
            MethodBase m => TypeKey(m.DeclaringType) + "::" + m.Name,   // sites.tsv names have no "/paramCount" (DetScan Bare)
            _ => null
        };
        static bool IsPrefix(OpCode o) => o == OpCodes.Constrained || o == OpCodes.Volatile || o == OpCodes.Tailcall || o == OpCodes.Unaligned || o == OpCodes.Readonly;

        // ldc.i4 <site>; call Hit before each matching instruction (and before its IL prefixes). The pair leaves the stack as it
        // was, takes over the instruction's labels and exception block marks, so branches and try blocks still start on it.
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var list = instructions.ToList();
            if (original == null || !byMethod.TryGetValue(original, out var sites)) return list;
            var hit = AccessTools.Method(typeof(WriteBarrier), nameof(Hit));
            for (int i = list.Count - 1; i >= 0; i--)
            {
                string key = MemberKey(list[i].operand);
                if (key == null) continue;
                int op = (ushort)list[i].opcode.Value;
                foreach (var s in sites)
                {
                    if (s.op != op || s.member != key) continue;
                    int at = i;
                    while (at > 0 && IsPrefix(list[at - 1].opcode)) at--;
                    var ld = new CodeInstruction(OpCodes.Ldc_I4, s.id);
                    ld.MoveLabelsFrom(list[at]);
                    ld.MoveBlocksFrom(list[at]);
                    list.InsertRange(at, new[] { ld, new CodeInstruction(OpCodes.Call, hit) });
                    i += 2;   // list[i] moved; the loop goes on from the same instruction's left side
                    Interlocked.Increment(ref matched[s.id]);
                }
            }
            return list;
        }

        // Returns void: Harmony rethrows the exception, so behaviour does not change
        static void Finalizer(Exception __exception, MethodBase __originalMethod)
        {
            if (__exception == null || __originalMethod == null) return;
            lock (sync)
            {
                var k = (__originalMethod, __exception.GetType().FullName);
                int n = excSeen.TryGetValue(k, out var c) ? c + 1 : 1;
                excSeen[k] = n;
                if (n > 3 && (n & (n - 1)) != 0) return;
                var info = Harmony.GetPatchInfo(__originalMethod);
                var pre = info?.Prefixes.Select(x => x.PatchMethod.DeclaringType).ToHashSet() ?? new();
                var open = info?.Postfixes.Select(x => x.PatchMethod.DeclaringType).Where(pre.Contains)
                    .Except(info.Finalizers.Select(x => x.PatchMethod.DeclaringType)).Select(t => t.FullName).Distinct().ToList() ?? new();
                Write($"EXC {ctxNames[Context()]} n {n} mp {TickPatch.Timer} frame {frame} leaves {Key(__originalMethod)}: {__exception.GetType().Name}: {__exception.Message.Split('\n')[0]}");
                Write($"    Prefix+Postfix classes on it without a Finalizer: {(open.Count == 0 ? "none" : string.Join(", ", open))}");
                foreach (var l in (__exception.StackTrace ?? "").Split('\n').Take(12)) Write("    " + l.Trim());
            }
        }

        // Between frames no time context is open: the Rand state stack is empty and the global time is the world's
        public static void Frame(bool start)
        {
            if (log == null) return;
            if (start) frame++;
            if (Multiplayer.Client == null || Multiplayer.AsyncWorldTime == null || Multiplayer.Ticking || Multiplayer.ExecutingCmds) return;
            int rd = Rand.stateStack.Count, td = Find.TickManager.ticksGameInt - Multiplayer.AsyncWorldTime.worldTicks;
            if (rd != lastRandDepth || td != lastTickDelta)
            {
                Write($"FRAME {(start ? "start" : "after ticks")} mp {TickPatch.Timer} frame {frame}: Rand state stack {lastRandDepth} -> {rd}, global ticksGame - world ticks {lastTickDelta} -> {td}");
                lastRandDepth = rd;
                lastTickDelta = td;
            }
            if (!start && TickPatch.Timer >= nextSummary) { Summary(); nextSummary = TickPatch.Timer - TickPatch.Timer % 5000 + 5000; }
        }

        static void Summary()
        {
            lock (sync)
            {
                Write($"SUMMARY mp {TickPatch.Timer} frame {frame}");
                var tot = new long[NCtx];
                for (int s = 0; s < siteInfo.Count; s++)
                {
                    var cs = Enumerable.Range(0, NCtx).Where(c => hits[s * NCtx + c] > 0).ToList();
                    foreach (var c in cs) tot[c] += hits[s * NCtx + c];
                    if (cs.Any(c => c >= FirstLogged)) Write($"  site {s}: {string.Join(" ", cs.Select(c => ctxNames[c] + "=" + hits[s * NCtx + c]))} | {siteInfo[s]}");
                }
                Write("  totals: " + string.Join(" ", Enumerable.Range(0, NCtx).Select(c => ctxNames[c] + "=" + tot[c])));
            }
        }
    }

    [HarmonyPatch(typeof(TickManager), nameof(TickManager.TickManagerUpdate))]
    static class WriteBarrierFrame
    {
        static bool Prepare() => WriteBarrier.On;
        [HarmonyPriority(Priority.First)] static void Prefix() => WriteBarrier.Frame(true);
        [HarmonyPriority(Priority.Last)] static void Postfix() => WriteBarrier.Frame(false);
    }

    // The planted write (-mplabwplant): its name makes it an OnGUI root for DetScan, and it allocates a vanilla id
    public static class WriteBarrierPlant
    {
        public static void PlantOnGUI() => Find.UniqueIDsManager.GetNextThingID();
    }

    [HarmonyPatch(typeof(Map), nameof(Map.MapUpdate))]
    static class WriteBarrierPlantUi
    {
        static int frames;
        static bool Prepare() => WriteBarrier.PlantUi;
        static void Postfix() { if (++frames % 600 == 0) WriteBarrierPlant.PlantOnGUI(); }
    }

    [HarmonyPatch(typeof(Map), nameof(Map.MapPostTick))]
    static class WriteBarrierPlantTick
    {
        static bool Prepare() => WriteBarrier.PlantTick;
        static void Postfix(Map __instance) { if (__instance.Index == 0 && Find.TickManager.TicksGame % 600 == 0) WriteBarrierPlant.PlantOnGUI(); }
    }
}
