namespace Tests;

// Row 124: the autosave counter must be zeroed on every PC at the CreateJoinPoint command (before the save and before
// the standalone early return), else a host that rejoins from the arbiter's join point autosaves on its first tick.
// Client code needs the game DLLs, so this is a source check (fails on the parent commit, passes on the fix).
[TestFixture]
public class Row124AutosaveCounterTest
{
    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Client", "AsyncTime", "AsyncWorldTimeComp.cs")))
            dir = dir.Parent;
        Assert.That(dir, Is.Not.Null, "Source root not found");
        return dir!.FullName;
    }

    [Test]
    public void CreateJoinPoint_ZeroesAutosaveCounter_BeforeEarlyReturn_AndIsGatedByRow124()
    {
        var text = File.ReadAllText(Path.Combine(SourceRoot(), "Client", "AsyncTime", "AsyncWorldTimeComp.cs"));
        var start = text.IndexOf("cmdType == CommandType.CreateJoinPoint", StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0));
        var end = text.IndexOf("QueueLongEvent(CreateJoinPointAndSendIfHost", start, StringComparison.Ordinal);
        Assert.That(end, Is.GreaterThan(start));
        var block = text.Substring(start, end - start);

        var zero = block.IndexOf("autosaveCounter = 0", StringComparison.Ordinal);
        var ret = block.IndexOf("return;", StringComparison.Ordinal);
        Assert.That(zero, Is.GreaterThanOrEqualTo(0), "no autosaveCounter = 0 in the CreateJoinPoint branch");
        Assert.That(ret, Is.GreaterThan(zero), "the zeroing must come before the early return");
        Assert.That(block, Does.Contain("LabOff.Row(124)"));
    }

    [Test]
    public void LabOff_Owns_Row124()
    {
        var text = File.ReadAllText(Path.Combine(SourceRoot(), "Client", "LabOff.cs"));
        Assert.That(text, Does.Match(@"Owned = \{[^}]*\b124\b"));
    }
}
