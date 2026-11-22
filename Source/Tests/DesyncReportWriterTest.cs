using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Multiplayer.Client.Desyncs;

namespace Tests;

// FIXES row 446: the desync report must be written even when the window closes before the report tasks end.
// Behaviour tests run the real DesyncReportWriter (linked file, no game); the IL check reads Multiplayer.dll
// (env MP446_DLL) without loading it.
[TestFixture]
public class DesyncReportWriterTest
{
    private class Host { public string Name = "h"; }

    private static (DesyncReportWriter<Host> w, List<string> saves, List<string> logs, Queue<Action> main, TaskCompletionSource tcs)
        Make()
    {
        var saves = new List<string>();
        var logs = new List<string>();
        var main = new Queue<Action>();
        var tcs = new TaskCompletionSource();
        var w = new DesyncReportWriter<Host>(tcs.Task, h => { saves.Add(h?.Name ?? "none"); return "Desync-1.zip"; },
            a => { lock (main) main.Enqueue(a); }, logs.Add);
        return (w, saves, logs, main, tcs);
    }

    private static void Drain(Queue<Action> main, int expected)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until)
        {
            lock (main) if (main.Count >= expected) break;
            Thread.Sleep(5);
        }
        while (true)
        {
            Action a;
            lock (main) { if (main.Count == 0) break; a = main.Dequeue(); }
            a();
        }
    }

    [Test]
    public void CloseBeforeReady_WritesOnceAfterTasksEnd()
    {
        var (w, saves, logs, main, tcs) = Make();
        Host? host = null;
        w.WriteWhenReady(() => host);           // window closed, tasks still running
        w.WriteWhenReady(() => host);           // PostClose after Try resync: idempotent
        Assert.That(saves.Count, Is.EqualTo(0));
        host = new Host();                      // traces arrive late
        tcs.SetResult();
        Drain(main, 1);
        TestContext.Out.WriteLine($"saves={saves.Count} [{string.Join(",", saves)}] logs=[{string.Join(" | ", logs)}]");
        Assert.That(saves, Is.EqualTo(new[] { "h" }));
        Assert.That(logs, Is.EqualTo(new[] { "Multiplayer: desync report written after the window closed: Desync-1.zip" }));
    }

    [Test]
    public void WindowPathThenClose_WritesOnce()
    {
        var (w, saves, logs, main, tcs) = Make();
        tcs.SetResult();
        Assert.That(w.TryWrite(null, false), Is.True);
        w.WriteWhenReady(() => null);
        Drain(main, 0);
        Assert.That(w.TryWrite(null, false), Is.False);
        Assert.That(saves.Count, Is.EqualTo(1));
        Assert.That(logs, Is.EqualTo(new[] { "Multiplayer: desync report written: Desync-1.zip" }));
    }

    [Test]
    public void CloseThenWindowPathRace_WritesOnce()
    {
        var (w, saves, _, main, tcs) = Make();
        w.WriteWhenReady(() => null);
        tcs.SetResult();
        Assert.That(w.TryWrite(null, false), Is.True);   // main thread got there first
        Drain(main, 1);
        Assert.That(saves.Count, Is.EqualTo(1));
    }

    [Test]
    public void TryWriteNeverBlocksWhenNotReady()
    {
        var (w, saves, _, _, _) = Make();
        Assert.That(w.TryWrite(null, false), Is.False);
        Assert.That(saves.Count, Is.EqualTo(0));
    }

    // Planted wrong value: the check must reject a claim of 2 writes.
    [Test]
    public void PlantedWrongCount_IsCaught()
    {
        var (w, saves, _, main, tcs) = Make();
        w.WriteWhenReady(() => null);
        tcs.SetResult();
        Drain(main, 1);
        Assert.That(saves.Count == 2, Is.False, "planted: claimed 2 writes");
        TestContext.Out.WriteLine($"planted wrong value 2 rejected, real count {saves.Count}");
    }

    // ---- static IL check (does not load the dll) ----
    private static List<string> SaveCallers(string dll)
    {
        using var fs = File.OpenRead(dll);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();
        var ops = new Dictionary<short, OpCode>();
        foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (f.GetValue(null) is OpCode oc) ops[oc.Value] = oc;

        string TypeName(EntityHandle h) => h.Kind switch
        {
            HandleKind.TypeDefinition => md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)h).Name),
            HandleKind.TypeReference => md.GetString(md.GetTypeReference((TypeReferenceHandle)h).Name),
            _ => "?"
        };
        bool IsSave(EntityHandle h)
        {
            if (h.Kind == HandleKind.MethodDefinition)
            {
                var m = md.GetMethodDefinition((MethodDefinitionHandle)h);
                return md.GetString(m.Name) == "Save" && TypeName(m.GetDeclaringType()) == "SaveableDesyncInfo";
            }
            if (h.Kind == HandleKind.MemberReference)
            {
                var m = md.GetMemberReference((MemberReferenceHandle)h);
                return md.GetString(m.Name) == "Save" && TypeName(m.Parent) == "SaveableDesyncInfo";
            }
            return false;
        }

        var result = new List<string>();
        foreach (var th in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(th);
            foreach (var mh in t.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                if (m.RelativeVirtualAddress == 0) continue;
                var r = pe.GetMethodBody(m.RelativeVirtualAddress).GetILReader();
                while (r.RemainingBytes > 0)
                {
                    short code = r.ReadByte();
                    if (code == 0xFE) code = unchecked((short)(0xFE00 | r.ReadByte()));
                    if (!ops.TryGetValue(code, out var oc)) break;
                    switch (oc.OperandType)
                    {
                        case OperandType.InlineMethod:
                            var tok = r.ReadInt32();
                            if (IsSave(MetadataTokens.EntityHandle(tok)))
                                result.Add(md.GetString(t.Name) + "." + md.GetString(m.Name));
                            break;
                        case OperandType.InlineNone: break;
                        case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar:
                            r.ReadByte(); break;
                        case OperandType.InlineVar: r.ReadInt16(); break;
                        case OperandType.InlineI8: case OperandType.InlineR: r.ReadInt64(); break;
                        case OperandType.InlineSwitch:
                            var n = r.ReadInt32();
                            for (var i = 0; i < n; i++) r.ReadInt32();
                            break;
                        default: r.ReadInt32(); break;
                    }
                }
            }
        }
        return result.Distinct().OrderBy(x => x).ToList();
    }

    [Test]
    public void IlCheck_SaveReachableWithoutWindowUpdate()
    {
        var dll = Environment.GetEnvironmentVariable("MP446_DLL");
        if (dll == null) Assert.Ignore("MP446_DLL not set");
        var callers = SaveCallers(dll!);
        TestContext.Out.WriteLine($"Save references in {dll}: [{string.Join(", ", callers)}]");
        Assert.That(callers, Is.Not.Empty, "no Save reference found at all (check itself broken)");
        var outsideWindow = callers.Where(c => !c.StartsWith("DesyncedWindow.")).ToList();
        Assert.That(outsideWindow, Is.Not.Empty, "Save is only called from the DesyncedWindow: closing it first leaves no report");
    }
}
