using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;

namespace Tests;

// Row 17 (lane r17_tracecap): with -mptracemode=Slow one 30-tick sync opinion can hold millions of trace hashes (the lab
// logged "List too big (2469885>2097152)"), the opinion was then never sent and its window was not compared. The client
// stops recording at SyncOpinion.MaxTraceHashes and compares with SyncOpinion.TraceHashesMatch. These tests cover the
// Common half (packet fit, join budget, comparison); ClientSyncOpinion/SyncCoordinator need the game and are not here.
[TestFixture]
public class SyncOpinionTest
{
    private static List<int> Hashes(int count, int seed = 7)
    {
        var list = new List<int>(count);
        for (var i = 0; i < count; i++)
            list.Add(unchecked(i * 31 + seed));
        return list;
    }

    private static SyncOpinion OpinionWith(List<int> traceHashes) => new()
    {
        startTick = 12960,
        commandRandomStates = [1, 2, 3],
        worldRandomStates = Enumerable.Range(0, 30).Select(i => (uint)(i * 7919)).ToList(),
        mapRandomStates =
        [
            new MapRandomState { mapId = 0, randomStates = Enumerable.Range(0, 30).Select(i => (uint)(i * 104729)).ToList() }
        ],
        traceHashes = traceHashes,
        simulating = false,
        roundMode = RoundModeEnum.ToNearest
    };

    [Test]
    public void OverflowingTraceList_FailsToSerialize_WhichIsWhyTheClientCapsIt()
    {
        // The size the lab logged. Before the cap this was the whole opinion being dropped.
        var opinion = OpinionWith(Hashes(2_469_885));
        Assert.Throws<WriterException>(() => _ = new ClientSyncInfoPacket { SyncOpinion = opinion });
    }

    [Test]
    public void CappedOpinion_FitsItsPacketAndRoundTrips()
    {
        var traces = Hashes(SyncOpinion.MaxTraceHashes);
        var packet = new ClientSyncInfoPacket { SyncOpinion = OpinionWith(traces) };

        Assert.That(packet.rawSyncOpinion.Length, Is.LessThan(ClientSyncInfoPacket.MaxLength));

        var back = packet.SyncOpinion;
        Assert.That(back.traceHashes, Is.EqualTo(traces));
        Assert.That(back.startTick, Is.EqualTo(12960));
        Assert.That(back.worldRandomStates, Has.Count.EqualTo(30));
        Assert.That(back.mapRandomStates[0].randomStates, Has.Count.EqualTo(30));
    }

    [Test]
    public void TenCappedOpinions_FitInTheJoinWorldDataPacket()
    {
        // ServerPlayingState keeps the last 10 opinions and ServerLoadingState sends them with the save in one
        // fragmented packet. Leave at least half of it for the save.
        var raw = new ClientSyncInfoPacket { SyncOpinion = OpinionWith(Hashes(SyncOpinion.MaxTraceHashes)) }
            .rawSyncOpinion.Length;

        Assert.That(10L * (raw + 4), Is.LessThanOrEqualTo(ConnectionBase.MaxFragmentPacketTotalSize / 2));
    }

    [Test]
    public void TraceHashesMatch_BelowTheCap_NeedsEqualLists()
    {
        Assert.That(SyncOpinion.TraceHashesMatch(Hashes(100), Hashes(100)), Is.True);
        Assert.That(SyncOpinion.TraceHashesMatch([], []), Is.True);

        var changed = Hashes(100);
        changed[57]++;
        Assert.That(SyncOpinion.TraceHashesMatch(Hashes(100), changed), Is.False);

        // One side recorded more traces (a tick ended sooner on the other): still a mismatch, as before the cap
        Assert.That(SyncOpinion.TraceHashesMatch(Hashes(100), Hashes(101)), Is.False);
        Assert.That(SyncOpinion.TraceHashesMatch(Hashes(100), []), Is.False);
    }

    [Test]
    public void TraceHashesMatch_AtTheCap_ComparesOnlyTheCommonPrefix()
    {
        var cap = SyncOpinion.MaxTraceHashes;

        // Both sides capped identically
        Assert.That(SyncOpinion.TraceHashesMatch(Hashes(cap), Hashes(cap)), Is.True);

        // A list that was never capped against a capped one: same first MaxTraceHashes entries, so no false mismatch
        Assert.That(SyncOpinion.TraceHashesMatch(Hashes(cap + 5000), Hashes(cap)), Is.True);
        Assert.That(SyncOpinion.TraceHashesMatch(Hashes(cap), Hashes(cap + 5000)), Is.True);
        Assert.That(SyncOpinion.TraceHashesMatch(Hashes(cap + 5000), Hashes(2 * cap)), Is.True);

        // A real divergence inside the compared prefix is still found
        var changed = Hashes(cap + 5000);
        changed[cap - 1]++;
        Assert.That(SyncOpinion.TraceHashesMatch(Hashes(cap + 5000), changed), Is.False);
        Assert.That(SyncOpinion.TraceHashesMatch(Hashes(cap), changed), Is.False);

        // ...while one past the prefix is not looked at (the random states cover that part of the window)
        var changedPastCap = Hashes(cap + 5000);
        changedPastCap[cap]++;
        Assert.That(SyncOpinion.TraceHashesMatch(Hashes(cap + 5000), changedPastCap), Is.True);

        // Just under the cap against a full list is a length difference inside the prefix
        Assert.That(SyncOpinion.TraceHashesMatch(Hashes(cap - 1), Hashes(cap)), Is.False);
    }
}
