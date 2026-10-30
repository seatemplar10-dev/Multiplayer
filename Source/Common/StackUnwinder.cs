using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Multiplayer.Client.Desyncs;

// Return-address walk of the current thread's stack on Windows x64 (CoreCLR, RimWorld 1.7). JIT code has OS unwind
// info through the runtime's function table callback, so RtlVirtualUnwind can unwind managed frames. The OS unwinder
// costs ~50 ns a frame, so it runs once per return address: the effect of unwinding that frame is kept as a recipe (the
// caller's rsp and the restored nonvolatile registers at fixed offsets from the frame base) and later walks apply the
// recipe with a few stack reads (~6 ns a frame). Every read the recipes do is bounds-checked against the thread's
// stack, so a stale recipe (code memory reused after a dynamic method is collected) ends the walk early instead of
// reading outside the stack. Frames whose unwind codes are not plain offsets (a machine frame, an unknown code) always
// go through RtlVirtualUnwind. Not thread-safe: the desync traces only run on the main thread.
// Spike and measurements: waylith\_lanes\live30\fasttrace\spike (30 Oct 2026).
public static unsafe class StackUnwinder
{
    const int ContextSize = 1232;
    const int CtxFlags = 0x30, CtxRsp = 0x98, CtxRip = 0xF8, CtxRax = 0x78;
    const uint ContextAmd64Full = 0x10000B; // CONTEXT_AMD64 | CONTROL | INTEGER | FLOATING_POINT

    // Nonvolatile integer registers, as CONTEXT / KNONVOLATILE_CONTEXT_POINTERS.IntegerContext indices (rax = 0 ... r15 = 15)
    static readonly int[] NonVol = [3, 5, 6, 7, 12, 13, 14, 15];
    const int NotSaved = int.MinValue;

    const int KindSimple = 1, KindExact = 2;

    struct Recipe
    {
        public ulong ip;
        public int kind;
        public int baseReg;     // 0 = rsp, else the frame register's index
        public int baseAdj;     // frame base = value(baseReg) - baseAdj (the establisher frame)
        public int rspOff;      // caller's rsp = base + rspOff
        public int o3, o5, o6, o7, o12, o13, o14, o15; // restored register = *(base + o), NotSaved if not restored
    }

    static readonly delegate* unmanaged[SuppressGCTransition]<void*, void> pCaptureContext;
    static readonly delegate* unmanaged<ulong, ulong*, void*, void*> pLookup;
    static readonly delegate* unmanaged<uint, ulong, ulong, void*, void*, void**, ulong*, void*, void*> pUnwind;
    static readonly delegate* unmanaged[SuppressGCTransition]<ulong*, ulong*, void> pStackLimits;

    public static readonly bool Supported;

    static StackUnwinder()
    {
        try
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
            var nt = NativeLibrary.Load("ntdll.dll");
            var k32 = NativeLibrary.Load("kernel32.dll");
            pCaptureContext = (delegate* unmanaged[SuppressGCTransition]<void*, void>)NativeLibrary.GetExport(nt, "RtlCaptureContext");
            pLookup = (delegate* unmanaged<ulong, ulong*, void*, void*>)NativeLibrary.GetExport(nt, "RtlLookupFunctionEntry");
            pUnwind = (delegate* unmanaged<uint, ulong, ulong, void*, void*, void**, ulong*, void*, void*>)NativeLibrary.GetExport(nt, "RtlVirtualUnwind");
            pStackLimits = (delegate* unmanaged[SuppressGCTransition]<ulong*, ulong*, void>)NativeLibrary.GetExport(k32, "GetCurrentThreadStackLimits");
            Supported = true;
        }
        catch (Exception)
        {
            Supported = false;
        }
    }

    // Open-addressing table of recipes keyed by return address
    static Recipe[] table = new Recipe[1 << 12];
    static int count;
    public static int RecipeCount => count;
    public static long ExactFrames;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Slot(ulong ip, int mask) => (int)(((ip >> 4) * 0x9E3779B97F4A7C15UL) >> 40) & mask;

    static ref Recipe Find(ulong ip, out bool found)
    {
        int mask = table.Length - 1;
        int i = Slot(ip, mask);
        while (true)
        {
            ref var r = ref table[i];
            if (r.ip == ip) { found = true; return ref r; }
            if (r.ip == 0) { found = false; return ref r; }
            i = (i + 1) & mask;
        }
    }

    static void Grow()
    {
        var old = table;
        table = new Recipe[old.Length * 2];
        int mask = table.Length - 1;
        foreach (var r in old)
        {
            if (r.ip == 0) continue;
            int i = Slot(r.ip, mask);
            while (table[i].ip != 0) i = (i + 1) & mask;
            table[i] = r;
        }
    }

    public static void ResetRecipes()
    {
        table = new Recipe[1 << 12];
        count = 0;
    }

    // True when every unwind code is one RtlVirtualUnwind turns into fixed offsets (no machine frame, no unknown op)
    static bool CodesSimple(byte* info, ulong imageBase)
    {
        for (int chain = 0; chain < 32; chain++)
        {
            int ver = info[0] & 7, flags = info[0] >> 3, cnt = info[2];
            if (ver != 1 && ver != 2) return false;
            ushort* codes = (ushort*)(info + 4);
            for (int i = 0; i < cnt;)
            {
                int op = (codes[i] >> 8) & 0xF, opInfo = codes[i] >> 12;
                switch (op)
                {
                    case 0: case 2: case 3: i += 1; break;      // PUSH_NONVOL, ALLOC_SMALL, SET_FPREG
                    case 1: i += opInfo == 0 ? 2 : 3; break;    // ALLOC_LARGE
                    case 4: case 8: i += 2; break;              // SAVE_NONVOL, SAVE_XMM128
                    case 5: case 9: i += 3; break;              // SAVE_NONVOL_FAR, SAVE_XMM128_FAR
                    case 6 when ver == 2: i += 2; break;        // EPILOG (version 2)
                    default: return false;                      // PUSH_MACHFRAME (10) or unknown
                }
            }
            if ((flags & 4) == 0) return true;                  // no UNW_FLAG_CHAININFO
            uint* chained = (uint*)(codes + ((cnt + 1) & ~1));  // RUNTIME_FUNCTION { begin, end, unwindData }
            if ((chained[2] & 1) != 0) return false;
            info = (byte*)(imageBase + chained[2]);
        }
        return false;
    }

    // One frame with the OS unwinder. Fills the recipe for this return address. False = stop the walk.
    static bool ExactStep(ref ulong rsp, ref ulong rip, ulong* regs, ulong lo, ulong hi, ref Recipe rec)
    {
        ulong imageBase;
        void* fe = pLookup(rip, &imageBase, null);
        if (fe == null)
            return false; // no unwind info past the first frame: not a frame this walk can trust

        // A real return address follows a call, so it is never the first byte of a function (the JIT puts an int3 after
        // a call that does not return). The runtime's GC suspension can replace a return address on the stack with its
        // hijack stub (an entry point) until that frame returns: the walk can't see the real address there, so it stops
        // and the caller takes the Slow path, which knows about hijacks. No recipe is kept for such an address.
        if (imageBase + ((uint*)fe)[0] == rip)
            return false;

        // The frame register's value comes from earlier recipes: check it before RtlVirtualUnwind reads through it
        uint unwindRva0 = ((uint*)fe)[2];
        if ((unwindRva0 & 1) == 0)
        {
            int frameReg0 = ((byte*)(imageBase + unwindRva0))[3] & 0xF;
            if (frameReg0 != 0 && (regs[frameReg0] < lo || regs[frameReg0] >= hi)) return false;
        }

        byte* raw = stackalloc byte[ContextSize + 16];
        byte* ctx = (byte*)(((ulong)raw + 15) & ~15UL);
        new Span<byte>(ctx, ContextSize).Clear();
        *(uint*)(ctx + CtxFlags) = ContextAmd64Full;
        for (int r = 0; r < 16; r++) *(ulong*)(ctx + CtxRax + r * 8) = regs[r];
        *(ulong*)(ctx + CtxRsp) = rsp;
        *(ulong*)(ctx + CtxRip) = rip;

        ulong* ptrs = stackalloc ulong[32];
        new Span<ulong>(ptrs, 32).Clear();
        void* handlerData;
        ulong establisher;
        pUnwind(0, imageBase, rip, fe, ctx, &handlerData, &establisher, ptrs);

        ulong newRsp = *(ulong*)(ctx + CtxRsp);
        ulong newRip = *(ulong*)(ctx + CtxRip);
        if (newRsp <= rsp || newRsp > hi) return false;

        // The recipe: offsets from the frame base (the establisher frame)
        uint unwindRva = ((uint*)fe)[2];
        bool simple = (unwindRva & 1) == 0 && CodesSimple((byte*)(imageBase + unwindRva), imageBase);
        if (simple)
        {
            byte fr = ((byte*)(imageBase + unwindRva))[3];
            int frameReg = fr & 0xF, frameAdj = (fr >> 4) * 16;
            ulong b = (frameReg == 0 ? rsp : regs[frameReg]) - (ulong)frameAdj;
            simple = b == establisher && newRsp - b < int.MaxValue && newRsp >= 8 && *(ulong*)(newRsp - 8) == newRip;
            if (simple)
            {
                rec.baseReg = frameReg;
                rec.baseAdj = frameAdj;
                rec.rspOff = (int)(newRsp - b);
                fixed (int* offs = &rec.o3)
                    for (int k = 0; k < 8; k++)
                    {
                        ulong p = ptrs[16 + NonVol[k]];
                        long off = p == 0 ? NotSaved : (long)(p - b);
                        if (p != 0 && (off < int.MinValue + 1 || off > int.MaxValue)) simple = false;
                        offs[k] = (int)off;
                    }
            }
        }
        rec.kind = simple ? KindSimple : KindExact;

        for (int r = 0; r < 16; r++) regs[r] = *(ulong*)(ctx + CtxRax + r * 8);
        rsp = newRsp;
        rip = newRip;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool Load(ulong addr, ulong lo, ulong hi, out ulong value)
    {
        if (addr < lo || addr > hi - 8 || (addr & 7) != 0) { value = 0; return false; }
        value = *(ulong*)addr;
        return true;
    }

    // Writes up to cap return addresses, innermost first, starting with the return address into the caller of Walk.
    // complete = the walk reached the end of the stack (a zero return address) within cap frames; false = it stopped at
    // cap or at a frame it could not unwind.
    [MethodImpl(MethodImplOptions.NoInlining)]
    [SkipLocalsInit]
    public static int Walk(ulong* buf, int cap, out bool complete)
    {
        complete = false;
        if (!Supported || cap <= 0) return 0;

        ulong lo, hi;
        pStackLimits(&lo, &hi);

        byte* raw = stackalloc byte[ContextSize + 16];
        byte* ctx = (byte*)(((ulong)raw + 15) & ~15UL);
        pCaptureContext(ctx);
        ulong* regs = stackalloc ulong[16];
        for (int r = 0; r < 16; r++) regs[r] = *(ulong*)(ctx + CtxRax + r * 8);
        ulong rsp = *(ulong*)(ctx + CtxRsp);
        ulong rip = *(ulong*)(ctx + CtxRip);

        // Every frame this walk reads lies above Walk's own rsp. GetCurrentThreadStackLimits gives the reserved range,
        // whose low end is not committed: reading there would fault, so the low bound is Walk's rsp.
        if (rsp < lo || rsp >= hi) return 0;
        lo = rsp;

        int n = 0;
        while (true)
        {
            if (n == cap) break;

            ref var rec = ref Find(rip, out bool found);
            if (!found || rec.kind == KindExact)
            {
                var fresh = new Recipe();
                ulong ip0 = rip;
                if (!ExactStep(ref rsp, ref rip, regs, lo, hi, ref fresh)) break;
                if (!found)
                {
                    fresh.ip = ip0;
                    rec = fresh;
                    if (++count * 2 > table.Length) Grow();
                }
                else ExactFrames++;
            }
            else
            {
                ulong b = (rec.baseReg == 0 ? rsp : regs[rec.baseReg]) - (ulong)rec.baseAdj;
                if (b < lo || b >= hi) break;
                bool ok = true;
                ulong v;
                if (rec.o3 != NotSaved) { ok &= Load(b + (ulong)(long)rec.o3, lo, hi, out v); regs[3] = v; }
                if (rec.o5 != NotSaved) { ok &= Load(b + (ulong)(long)rec.o5, lo, hi, out v); regs[5] = v; }
                if (rec.o6 != NotSaved) { ok &= Load(b + (ulong)(long)rec.o6, lo, hi, out v); regs[6] = v; }
                if (rec.o7 != NotSaved) { ok &= Load(b + (ulong)(long)rec.o7, lo, hi, out v); regs[7] = v; }
                if (rec.o12 != NotSaved) { ok &= Load(b + (ulong)(long)rec.o12, lo, hi, out v); regs[12] = v; }
                if (rec.o13 != NotSaved) { ok &= Load(b + (ulong)(long)rec.o13, lo, hi, out v); regs[13] = v; }
                if (rec.o14 != NotSaved) { ok &= Load(b + (ulong)(long)rec.o14, lo, hi, out v); regs[14] = v; }
                if (rec.o15 != NotSaved) { ok &= Load(b + (ulong)(long)rec.o15, lo, hi, out v); regs[15] = v; }
                ulong newRsp = b + (ulong)rec.rspOff;
                if (!ok || newRsp <= rsp || !Load(newRsp - 8, lo, hi, out ulong newRip)) break;
                rsp = newRsp;
                rip = newRip;
            }

            if (rip == 0) { complete = true; break; }
            buf[n++] = rip;
        }
        return n;
    }
}
