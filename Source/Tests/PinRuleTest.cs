using FluentAssertions;
using Multiplayer.Client.Util;

namespace Tests;

// FIXES rows 548/549
[TestFixture]
public class PinRuleTest
{
    class F { }

    [Test]
    public void PushedIsRecordedEvenWhenAfterPushThrows()
    {
        bool pushed = false, pushedCalled = false;
        Assert.Throws<InvalidOperationException>(() =>
            PinRule.PushThenApply(ref pushed, () => pushedCalled = true, () => throw new InvalidOperationException()));
        pushedCalled.Should().BeTrue();
        pushed.Should().BeTrue();
    }

    [Test]
    public void PushedStaysFalseWhenPushItselfThrows()
    {
        bool pushed = false;
        Assert.Throws<InvalidOperationException>(() =>
            PinRule.PushThenApply(ref pushed, () => throw new InvalidOperationException(), () => { }));
        pushed.Should().BeFalse();
    }

    [Test]
    public void FinalizerNeverPopsAnEmptyStackOrDeadGame()
    {
        PinRule.CanPop(true, 0, true).Should().BeFalse();   // StopMultiplayer cleared the stack
        PinRule.CanPop(false, 2, true).Should().BeFalse();  // game == null
        PinRule.CanPop(true, 2, false).Should().BeFalse();  // Find.Maps == null
        PinRule.CanPop(true, 1, true).Should().BeTrue();
    }

    [Test]
    public void RestoresOldFactionWhenRealFactionUnchanged()
    {
        var spectator = new F(); var neon = new F();
        PinRule.RestoreTarget(neon, spectator, spectator).Should().BeSameAs(neon);
    }

    [Test]
    public void KeepsNewRealFactionWhenBodyChangedIt()
    {
        var spectator = new F(); var created = new F();
        PinRule.RestoreTarget(spectator, spectator, created).Should().BeSameAs(created);
    }

    [Test]
    public void RowOffRestoresOldBehaviour()
    {
        var spectator = new F(); var created = new F();
        PinRule.RestoreTarget(spectator, spectator, created, rowOn: false).Should().BeSameAs(spectator);
        PinRule.CanPop(true, 0, true, rowOn: false).Should().BeTrue();
    }

    [Test]
    public void CallbackIsNotAppendedBecauseVanillaRunsIt()
    {
        PinRule.AppendCallback(true).Should().BeFalse();
        PinRule.AppendCallback(false).Should().BeFalse();
        PinRule.AppendCallback(true, rowOn: false).Should().BeTrue();
    }
}
