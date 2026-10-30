using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace Multiplayer.Client.Desyncs;

// The Fast desync trace on CoreCLR (RimWorld 1.7). StackUnwinder captures every return address of the stack, down to
// the thread's start (~6 ns a frame); the first time a stack is seen, System.Diagnostics.StackTrace resolves it exactly
// like the Slow mode (Harmony.GetMethodFromStackframe per frame) and the frames are kept under a 128-bit hash of the
// return addresses. A stack seen again costs the walk and one table probe instead of a StackTrace (~5-20 us), with the
// same frames as Slow: the whole stack of return addresses is the same code locations, so the same methods. A walk that
// does not reach the end of the stack (deeper than MaxWalkFrames, or a frame it can't unwind) takes the Slow path.
// Not thread-safe: the desync traces only run on the main thread (DeferredStackTracing.ShouldAddStackTraceForDesyncLog).
public static unsafe class FastManagedTrace
{
    public const int MaxFrames = 32;    // StackTraceLogItemRaw.MaxDepth
    public const int MaxWalkFrames = 512;  // deeper stacks take the Slow path
    public const int MaxEntries = 1 << 18;   // keys: 24-byte slots, at most half full (~12 MB)
    public const int MaxFrameSets = 1 << 16; // interned frame lists, ~300 B each (~20 MB); past either limit all starts over

    struct Entry
    {
        public ulong h1, h2;    // h1 == 0 marks an empty slot
        public int frames;      // index into frameSets
    }

    static Entry[] table = new Entry[1 << 12];
    static int count;

    // Interned frame lists: many keys (different call sites of the same methods) share one list
    static readonly List<MethodBase?[]> frameSets = [];
    static readonly Dictionary<FrameSetKey, int> frameSetIndex = new();

    public static long Hits, Misses, Incomplete, Resets;
    public static int IncompleteInARow;
    public static int Entries => count;
    public static int FrameSets => frameSets.Count;

    public static bool Supported => StackUnwinder.Supported;

    public static string Stats =>
        $"entries {count}, frame lists {frameSets.Count}, recipes {StackUnwinder.RecipeCount}, hits {Hits}, misses {Misses}, " +
        $"incomplete walks {Incomplete}, exact-unwind frames {StackUnwinder.ExactFrames}, resets {Resets}";

    // Also forgets the unwind recipes: the code of a collected dynamic method can be reused by another method, and a
    // later Harmony patch changes what GetMethodFromStackframe returns. Called when a multiplayer game starts.
    public static void Reset()
    {
        table = new Entry[1 << 12];
        count = 0;
        frameSets.Clear();
        frameSetIndex.Clear();
        IncompleteInARow = 0;
        StackUnwinder.ResetRecipes();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Hash(ulong* p, int n, out ulong h1, out ulong h2)
    {
        ulong a = 0x9E3779B97F4A7C15UL ^ (ulong)n, b = 0xC2B2AE3D27D4EB4FUL + (ulong)n;
        for (int i = 0; i < n; i++)
        {
            a = (a ^ p[i]) * 0xFF51AFD7ED558CCDUL;
            a ^= a >> 32;
            b = (b + p[i]) * 0xC4CEB9FE1A85EC53UL;
            b ^= b >> 29;
        }
        h1 = a | 1; // never 0: 0 is the empty slot
        h2 = b;
    }

    static ref Entry Find(ulong h1, ulong h2, out bool found)
    {
        int mask = table.Length - 1;
        int i = (int)(h1 >> 40) & mask;
        while (true)
        {
            ref var e = ref table[i];
            if (e.h1 == h1 && e.h2 == h2) { found = true; return ref e; }
            if (e.h1 == 0) { found = false; return ref e; }
            i = (i + 1) & mask;
        }
    }

    static void Grow()
    {
        var old = table;
        table = new Entry[old.Length * 2];
        int mask = table.Length - 1;
        foreach (var e in old)
        {
            if (e.h1 == 0) continue;
            int i = (int)(e.h1 >> 40) & mask;
            while (table[i].h1 != 0) i = (i + 1) & mask;
            table[i] = e;
        }
    }

    // The frames that new StackTrace(callerSkip) in the caller would give, resolved like DeferredStackTracing.ManagedTrace
    // (at most MaxFrames). Null = take the Slow path for this call (the walk did not reach the end of the stack).
    // The returned array is shared between traces: read it, never write it.
    [MethodImpl(MethodImplOptions.NoInlining)]
    [SkipLocalsInit]
    public static MethodBase?[]? Capture(int callerSkip)
    {
        ulong* buf = stackalloc ulong[MaxWalkFrames];
        int n = StackUnwinder.Walk(buf, MaxWalkFrames, out bool complete);
        if (!complete)
        {
            Incomplete++;
            IncompleteInARow++;
            return null;
        }
        IncompleteInARow = 0;

        Hash(buf, n, out var h1, out var h2);
        int hit = Find(h1, h2, out bool found).frames;
        if (found)
        {
            Hits++;
            return frameSets[hit];
        }

        Misses++;
        // Skip this frame and the caller's callerSkip frames, like the caller's new StackTrace(callerSkip)
        var st = new StackTrace(1 + callerSkip, false).GetFrames();
        int depth = Math.Min(st.Length, MaxFrames);
        var frames = new MethodBase?[depth];
        for (int i = 0; i < depth; i++)
            frames[i] = Harmony.GetMethodFromStackframe(st[i]);

        if (count >= MaxEntries || frameSets.Count >= MaxFrameSets)
        {
            Resets++;
            Reset();
        }

        int index = Intern(frames);
        var result = frameSets[index];
        // Found again: resolving can run other code (type loads), so a slot found before it may be stale
        ref var slot = ref Find(h1, h2, out found);
        if (!found)
        {
            slot.h1 = h1;
            slot.h2 = h2;
            slot.frames = index;
            if (++count * 2 > table.Length) Grow();
        }

        return result;
    }

    static int Intern(MethodBase?[] frames)
    {
        var key = new FrameSetKey(frames);
        if (frameSetIndex.TryGetValue(key, out int index)) return index;
        index = frameSets.Count;
        frameSets.Add(frames);
        frameSetIndex[key] = index;
        return index;
    }

    readonly struct FrameSetKey(MethodBase?[] frames) : IEquatable<FrameSetKey>
    {
        readonly MethodBase?[] frames = frames;

        public bool Equals(FrameSetKey other)
        {
            if (other.frames.Length != frames.Length) return false;
            for (int i = 0; i < frames.Length; i++)
                if (!object.Equals(frames[i], other.frames[i])) return false;
            return true;
        }

        public override bool Equals(object? obj) => obj is FrameSetKey k && Equals(k);

        public override int GetHashCode()
        {
            var h = new HashCode();
            foreach (var f in frames) h.Add(f);
            return h.ToHashCode();
        }
    }
}
