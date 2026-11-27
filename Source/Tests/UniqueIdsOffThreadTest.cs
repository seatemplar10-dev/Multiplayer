using FluentAssertions;
using Multiplayer.Client.Util;

namespace Tests;

// FIXES row 514: a unique id asked for off the main thread is local, never a sim id.
[TestFixture]
public class UniqueIdsOffThreadTest
{
    [Test]
    public void OffMainThreadIsLocalEvenWhileTicking()
    {
        // not in interface (ticking), not entry, no override, but not the main thread
        LocalIdRule.UseLocal(false, false, false, false, true).Should().BeTrue();
    }

    [Test]
    public void MainThreadTickingKeepsRealIds()
    {
        LocalIdRule.UseLocal(false, false, false, true, true).Should().BeFalse();
    }

    [Test]
    public void RowOffRestoresOldBehaviour()
    {
        LocalIdRule.UseLocal(false, false, false, false, false).Should().BeFalse();
    }

    [Test]
    public void ExistingLocalCasesStayLocal()
    {
        LocalIdRule.UseLocal(true, false, false, true, true).Should().BeTrue();
        LocalIdRule.UseLocal(false, true, false, true, true).Should().BeTrue();
        LocalIdRule.UseLocal(false, false, true, true, true).Should().BeTrue();
    }
}
