using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Multiplayer.Client.Desyncs;

namespace Tests;

// The Fast desync trace on CoreCLR (FastManagedTrace + StackUnwinder): every capture must give the frames the Slow mode
// (System.Diagnostics.StackTrace + Harmony.GetMethodFromStackframe) gives, first sight and cache hit alike, through
// plain, shared-generic, dynamic-method, delegate, interface, finally-funclet, reflection and Harmony-patched frames.
[TestFixture]
[NonParallelizable]
public class FastManagedTraceTest
{
    const MethodImplOptions NI = MethodImplOptions.NoInlining;

    static Func<string, int, int> dyn = null!;
    static string[]? slow, fast;
    static int compares, mismatches;
    static readonly List<string> mismatchReport = [];

    [OneTimeSetUp]
    public void Setup()
    {
        if (!FastManagedTrace.Supported) Assert.Ignore("StackUnwinder runs on Windows x64 only");

        var dm = new DynamicMethod("DynFrame", typeof(int), [typeof(string), typeof(int)], typeof(FastManagedTraceTest).Module, true);
        var il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Call, typeof(FastManagedTraceTest).GetMethod(nameof(Next))!);
        il.Emit(OpCodes.Ldc_I4, 11);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ret);
        dyn = dm.CreateDelegate<Func<string, int, int>>();

        var h = new Harmony("tests.fastmanagedtrace");
        h.Patch(AccessTools.Method(typeof(FastManagedTraceTest), nameof(Patched)),
            prefix: new HarmonyMethod(typeof(FastManagedTraceTest), nameof(PatchedPrefix)));
        h.Patch(AccessTools.Method(typeof(FastManagedTraceTest), nameof(Target)),
            postfix: new HarmonyMethod(typeof(FastManagedTraceTest), nameof(TargetPostfix)));
    }

    static string Name(MethodBase? m) => m == null ? "Null" : m.FullDescription();

    // The leaf, called by Harmony's replacement of Target like DeferredStackTracing.Postfix
    static void TargetPostfix()
    {
        if (probeAction != null) probeAction();
        else Probe();
    }

    [MethodImpl(NI)]
    static void Probe()
    {
        // Both skip Probe: new StackTrace(1) here, Capture(1) = Capture's own frame + Probe
        slow = new StackTrace(1, false).GetFrames().Take(FastManagedTrace.MaxFrames)
            .Select(f => Name(Harmony.GetMethodFromStackframe(f))).ToArray();
        var frames = FastManagedTrace.Capture(1);
        fast = frames?.Select(Name).ToArray();
    }

    [MethodImpl(NI)]
    public static int Next(string p, int i)
    {
        if (i >= p.Length) return Target();
        return p[i] switch
        {
            'R' => R(p, i),
            'G' => Gen<string>.M<object>(p, i),
            'S' => Gen<int>.M<long>(p, i),
            'D' => dyn(p, i) + 10,
            'L' => Lambda(p, i),
            'I' => Iface.Call(p, i),
            'F' => Fin(p, i),
            'P' => Patched(p, i),
            'Q' => (int)typeof(FastManagedTraceTest).GetMethod(nameof(R))!.Invoke(null, [p, i])!,
            _ => throw new ArgumentException(p)
        };
    }

    [MethodImpl(NI)] public static int R(string p, int i) => Next(p, i + 1) + 1;

    public static class Gen<T>
    {
        [MethodImpl(NI)] public static int M<U>(string p, int i) => Next(p, i + 1) + 3;
    }

    public interface IThing { int Call(string p, int i); }
    sealed class Thing : IThing { [MethodImpl(NI)] public int Call(string p, int i) => Next(p, i + 1) + 4; }
    static readonly IThing Iface = new Thing();

    static int captured = 6;

    [MethodImpl(NI)]
    static int Lambda(string p, int i)
    {
        Func<string, int, int> f = (a, b) => Next(a, b + 1) + captured;
        return f(p, i) + 1;
    }

    [MethodImpl(NI)]
    static int Fin(string p, int i)
    {
        int r = 0;
        try { r = 1; }
        finally { r += Next(p, i + 1); }
        return r + 8;
    }

    [MethodImpl(NI)] public static int Patched(string p, int i) => Next(p, i + 1) + 9;
    static void PatchedPrefix() { }

    [MethodImpl(NI)] public static int Target() => Environment.TickCount & 1;

    [MethodImpl(NI)]
    static void RunAndCompare(string path)
    {
        slow = fast = null;
        Next(path, 0);
        if (fast == null) return; // the Slow path was taken for this call: allowed, never wrong frames
        compares++;
        if (!slow!.SequenceEqual(fast))
        {
            mismatches++;
            if (mismatchReport.Count < 40)
            {
                mismatchReport.Add($"path '{path}':");
                for (int i = 0; i < Math.Max(slow!.Length, fast.Length); i++)
                    mismatchReport.Add($"  {(i < slow.Length ? slow[i] : "-")} | {(i < fast.Length ? fast[i] : "-")}");
            }
        }
    }

    [Test]
    public void FastFramesEqualSlowFrames()
    {
        var paths = new List<string> { "", "R", "RRRR", "G", "S", "D", "L", "I", "F", "P", "Q", "RGSDLIFPQ", "DDDD", "PGPG",
            new string('R', 40) + "GDP", "GDP" + new string('R', 40) };
        var rnd = new Random(1234);
        const string alphabet = "RGSDLIFPQ";
        for (int k = 0; k < 150; k++)
            paths.Add(new string(Enumerable.Range(0, rnd.Next(1, 25)).Select(_ => alphabet[rnd.Next(alphabet.Length)]).ToArray()));

        FastManagedTrace.Reset();
        compares = mismatches = 0;
        long hits0 = FastManagedTrace.Hits;

        // Several rounds from one call site (a loop the JIT can't unroll: the count is not a constant), so the same
        // stacks repeat and hit the cache; the pauses let tiered compilation replace tier-0 code with tier-1 code
        int rounds = paths.Count > 0 ? 6 : 0;
        for (int round = 0; round < rounds; round++)
        {
            foreach (var p in paths)
                RunAndCompare(p);
            if (round == 2) Thread.Sleep(500);
        }

        TestContext.Out.WriteLine($"compares {compares}, mismatches {mismatches}, {FastManagedTrace.Stats}");
        foreach (var line in mismatchReport) TestContext.Out.WriteLine(line);

        Assert.That(mismatches, Is.EqualTo(0));
        Assert.That(compares, Is.GreaterThan(paths.Count * rounds * 9 / 10), "most captures should not need the Slow path");
        Assert.That(FastManagedTrace.Hits - hits0, Is.GreaterThan(paths.Count), "repeated stacks should hit the cache");
    }

    // GC suspension hijacks return addresses on the stack while a collection is pending; a background thread collects
    // in a loop while the main thread captures. Every capture must still equal Slow (or take the Slow path).
    [Test]
    public void FastFramesEqualSlowFramesUnderGcSuspension()
    {
        var paths = new List<string> { "RRRR", "GDP", "RGSDLIFP", new string('R', 30), "PGPGPG" };
        compares = mismatches = 0;
        mismatchReport.Clear();
        long incomplete0 = FastManagedTrace.Incomplete;
        int gcs = 0;
        using var stop = new ManualResetEventSlim();
        var gcThread = new Thread(() =>
        {
            while (!stop.IsSet)
            {
                GC.Collect(0, GCCollectionMode.Forced, true);
                Interlocked.Increment(ref gcs);
                var spin = Stopwatch.StartNew();
                while (spin.Elapsed.TotalMilliseconds < 0.2) Thread.SpinWait(20);
            }
        }) { IsBackground = true };
        gcThread.Start();

        var sw = Stopwatch.StartNew();
        int calls = 0;
        while (sw.ElapsedMilliseconds < 3000)
            foreach (var p in paths)
            {
                RunAndCompare(p);
                calls++;
            }
        stop.Set();
        gcThread.Join();

        TestContext.Out.WriteLine($"calls {calls}, compares {compares}, mismatches {mismatches}, GCs {gcs}, " +
                                  $"incomplete walks this test {FastManagedTrace.Incomplete - incomplete0}, {FastManagedTrace.Stats}");
        foreach (var line in mismatchReport) TestContext.Out.WriteLine(line);
        Assert.That(mismatches, Is.EqualTo(0));
        Assert.That(gcs, Is.GreaterThan(100));
        Assert.That(calls, Is.GreaterThan(1000));
    }

    // Timing, informational: Fast capture of a repeated stack vs one System.Diagnostics.StackTrace
    [Test]
    public void FastIsCheaperThanSlow()
    {
        double slowNs = 0, fastNs = 0;
        int n = 2000;
        Action probe = () =>
        {
            for (int i = 0; i < 50; i++) FastManagedTrace.Capture(0);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++) _ = new StackTrace(0, false).GetFrames().Length;
            slowNs = sw.Elapsed.TotalMilliseconds * 1e6 / n;
            sw.Restart();
            for (int i = 0; i < n * 10; i++) _ = FastManagedTrace.Capture(0)?.Length;
            fastNs = sw.Elapsed.TotalMilliseconds * 1e6 / (n * 10);
        };
        probeAction = probe;
        Next("RGRGRGRGRGRGRGRG", 0);
        probeAction = null;
        TestContext.Out.WriteLine($"Slow {slowNs:F0} ns, Fast (hit) {fastNs:F0} ns, {slowNs / fastNs:F1}x, {FastManagedTrace.Stats}");
        Assert.That(fastNs * 5, Is.LessThan(slowNs));
    }

    static Action? probeAction;
}
