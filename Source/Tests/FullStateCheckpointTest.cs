using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;

namespace Tests;

// FIXES row 99 (live full-state checksum), lane d136: the Common half - comparison on common MP timers, the carry window, the
// packet / ClientSyncOpinion byte layout. ClientSyncOpinion / SyncCoordinator need the game: lane d136's client harness covers them.
[TestFixture]
public class FullStateCheckpointTest
{
    private static List<FullStateHash> Hashes(params (int timer, ulong hash)[] entries) =>
        entries.Select(e => new FullStateHash { timer = e.timer, hash = e.hash }).ToList();

    [Test]
    public void EqualLists_Pass()
    {
        var a = Hashes((2500, 0x1111), (5000, 0x2222));
        Assert.That(FullStateCheckpoints.FirstDifference(a, Hashes((2500, 0x1111), (5000, 0x2222)), "local", "remote"), Is.Null);
    }

    [Test]
    public void PlantedDifference_IsNamedAtItsTimer()
    {
        var a = Hashes((2500, 0x1111), (5000, 0x2222));
        var b = Hashes((2500, 0x1111), (5000, 0x2223));
        Assert.That(FullStateCheckpoints.FirstDifference(a, b, "local", "remote"),
            Is.EqualTo("at MP timer 5000: local 0000000000002222 / remote 0000000000002223"));
    }

    [Test]
    public void TwoDifferences_TheFirstTimerIsNamed()
    {
        var a = Hashes((2500, 1), (5000, 2));
        var b = Hashes((2500, 9), (5000, 9));
        Assert.That(FullStateCheckpoints.FirstDifference(a, b, "a", "b"), Does.StartWith("at MP timer 2500:"));
    }

    [Test]
    public void TimersOnlyOneSideCarries_AreNotADifference()
    {
        // a PC that joined later lacks the early checkpoints; another K gives other timers
        var a = Hashes((2500, 1), (5000, 2));
        var b = Hashes((5000, 2), (7500, 3));
        Assert.That(FullStateCheckpoints.FirstDifference(a, b, "a", "b"), Is.Null);
        Assert.That(FullStateCheckpoints.Common(a, b), Has.Count.EqualTo(1));
        Assert.That(FullStateCheckpoints.Common(a, b)[0].a.timer, Is.EqualTo(5000));
    }

    [Test]
    public void NullOrEmpty_IsNoDifference()
    {
        Assert.That(FullStateCheckpoints.FirstDifference(null, Hashes((2500, 1)), "a", "b"), Is.Null);
        Assert.That(FullStateCheckpoints.FirstDifference(Hashes((2500, 1)), null, "a", "b"), Is.Null);
        Assert.That(FullStateCheckpoints.FirstDifference([], [], "a", "b"), Is.Null);
        Assert.That(FullStateCheckpoints.Common(null, null), Is.Empty);
    }

    [Test]
    public void Prune_KeepsTheCarryWindowAndDropsTimersAfterARewind()
    {
        var recent = Hashes((2500, 1), (5000, 2), (7500, 3), (10000, 4));
        FullStateCheckpoints.Prune(recent, 10000);
        Assert.That(recent.Select(h => h.timer), Is.EqualTo(new[] { 5000, 7500, 10000 }), "(now - CarryTicks, now]");

        // a resync reloads an older save: the MP timer comes back to 8000, the 10000 walk is from the abandoned pass
        FullStateCheckpoints.Prune(recent, 8000);
        Assert.That(recent.Select(h => h.timer), Is.EqualTo(new[] { 5000, 7500 }));
    }

    [Test]
    public void Add_ReplacesAWalkOfTheSameTimer()
    {
        var recent = Hashes((5000, 2));
        FullStateCheckpoints.Add(recent, 5000, 7);
        FullStateCheckpoints.Add(recent, 7500, 8);
        Assert.That(recent, Is.EqualTo(Hashes((5000, 7), (7500, 8))));
    }

    [Test]
    public void CarryTicks_CoversThreeLabCheckpoints()
    {
        Assert.That(FullStateCheckpoints.CarryTicks, Is.EqualTo(7500));
        Assert.That(MpVersion.Protocol, Is.EqualTo(59));
    }

    // Two PCs, the same walks, opinions finished every 30 MP ticks as ConstantTicker does, and only every 7th window compared
    // (on a real host most windows are never compared: mp94's first 100 checks spanned 231 windows). The carry window still
    // brings every checkpoint into a compared window; a planted difference is named at its own timer, and nothing else.
    [TestCase(false)]
    [TestCase(true)]
    public void SparseComparisons_StillCompareEveryCheckpoint(bool plant)
    {
        var pcA = new List<FullStateHash>();
        var pcB = new List<FullStateHash>();
        var comparedTimers = new SortedSet<int>();
        string? firstMismatch = null;
        int window = 0;

        for (int timer = 1; timer <= 20000; timer++)
        {
            // ConstantTicker: the opinion is finished at timer % 30 == 0, before the walk of that tick
            if (timer % 30 == 0)
            {
                FullStateCheckpoints.Prune(pcA, timer);
                FullStateCheckpoints.Prune(pcB, timer);
                if (++window % 7 == 0 && firstMismatch == null)
                {
                    foreach (var (x, _) in FullStateCheckpoints.Common(pcA, pcB)) comparedTimers.Add(x.timer);
                    firstMismatch = FullStateCheckpoints.FirstDifference(pcA, pcB, "host", "arbiter");
                }
            }

            // the harness: a walk every 2,500 MP ticks in TickPatch.DoTick's postfix
            if (timer % 2500 == 0)
            {
                ulong h = (ulong)timer * 0x9E3779B97F4A7C15UL;
                FullStateCheckpoints.Add(pcA, timer, h);
                FullStateCheckpoints.Add(pcB, timer, plant && timer == 12500 ? h ^ 1 : h);
            }
        }

        if (plant)
        {
            Assert.That(firstMismatch, Does.StartWith("at MP timer 12500:"));
            Assert.That(comparedTimers, Is.EqualTo(new[] { 2500, 5000, 7500, 10000, 12500 }));
        }
        else
        {
            Assert.That(firstMismatch, Is.Null);
            Assert.That(comparedTimers, Is.EqualTo(Enumerable.Range(1, 7).Select(i => i * 2500)), "every checkpoint before the last");
        }
    }

    private static SyncOpinion OpinionWith(List<FullStateHash>? full) => new()
    {
        startTick = 12345,
        commandRandomStates = [1, 2, 3],
        worldRandomStates = [10, 20, 30],
        mapRandomStates = [new MapRandomState { mapId = 0, randomStates = [111, 222], listHashes = [] }],
        traceHashes = [999, 888],
        simulating = false,
        roundMode = RoundModeEnum.ToNearest,
        fullStateHashes = full!
    };

    [Test]
    public void Hashes_RoundTripThroughTheSyncInfoPacket()
    {
        var full = Hashes((2500, 0x0123456789ABCDEF), (5000, 0xFEDCBA9876543210));
        var back = new ClientSyncInfoPacket { SyncOpinion = OpinionWith(full) }.SyncOpinion;
        Assert.That(back.fullStateHashes, Is.EqualTo(full));
        Assert.That(new ServerSyncInfoPacket { SyncOpinion = OpinionWith(full) }.SyncOpinion.fullStateHashes, Is.EqualTo(full));
        Assert.That(new ClientSyncInfoPacket { SyncOpinion = OpinionWith(null) }.SyncOpinion.fullStateHashes, Is.Empty,
            "an opinion without a checkpoint carries an empty list");
    }

    // The server keeps the raw packet bytes and a joiner parses them with ClientSyncOpinion.Deserialize. This mirrors
    // Deserialize's reads exactly; the bytes must be used up with nothing left over.
    [Test]
    public void PacketBytes_ParseWithTheClientSyncOpinionLayout()
    {
        var full = Hashes((2500, 0x0123456789ABCDEF));
        var raw = new ClientSyncInfoPacket { SyncOpinion = OpinionWith(full) }.rawSyncOpinion;

        var data = new ByteReader(raw!);
        Assert.That(data.ReadInt32(), Is.EqualTo(12345));
        Assert.That(data.ReadPrefixedUInts(), Is.EqualTo(new uint[] { 1, 2, 3 }));
        Assert.That(data.ReadPrefixedUInts(), Is.EqualTo(new uint[] { 10, 20, 30 }));
        Assert.That(data.ReadInt32(), Is.EqualTo(1));
        Assert.That(data.ReadInt32(), Is.EqualTo(0));
        Assert.That(data.ReadPrefixedUInts(), Is.EqualTo(new uint[] { 111, 222 }));
        Assert.That(ListHash.ReadList(data), Is.Empty);
        Assert.That(data.ReadPrefixedInts(), Is.EqualTo(new[] { 999, 888 }));
        Assert.That(data.ReadBool(), Is.False);
        Assert.That(data.ReadShort(), Is.EqualTo((short)RoundModeEnum.ToNearest));
        Assert.That(FullStateHash.ReadList(data), Is.EqualTo(full));
        Assert.That(data.Left, Is.EqualTo(0));
    }

    [Test]
    public void WriteList_WritesTheSameBytesAsBind()
    {
        var full = Hashes((2500, 1), (5000, ulong.MaxValue));
        var viaBind = BinderOf.List(BinderOf.Identity<FullStateHash>()).Serialize(full);
        var writer = new ByteWriter();
        FullStateHash.WriteList(writer, full);
        Assert.That(writer.ToArray(), Is.EqualTo(viaBind));

        var empty = new ByteWriter();
        FullStateHash.WriteList(empty, null);
        Assert.That(empty.ToArray(), Is.EqualTo(new byte[] { 0, 0, 0, 0 }));
    }

    [Test]
    public void ReadList_RefusesACountOutOfRange()
    {
        var writer = new ByteWriter();
        writer.WriteInt32(FullStateHash.MaxPerOpinion + 1);
        Assert.Throws<ReaderException>(() => FullStateHash.ReadList(new ByteReader(writer.ToArray())));
    }
}
