using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;

namespace Tests;

// FIXES row 113 item 3 (live order sync), lane d113_sync. Two "PCs" build their checkpoints from fake lists the way
// Client/Desyncs/ListCheckpoint.cs does (AllThings, then the three tick lists; motes skipped) and compare them with
// ListOrderHash.FirstDifference, as ClientSyncOpinion.CheckForDesync does. The client half (real Things, real TickLists,
// ClientSyncOpinion) needs the game and is checked by the lane harness (d113_sync/harness), not here.
[TestFixture]
public class ListOrderHashTest
{
    private sealed record Fake(int Id, bool Mote = false);

    private static readonly Func<Fake, bool> IsMote = t => t.Mote;
    private static readonly Func<Fake, int> IdOf = t => t.Id;

    // One map's shared lists on one PC
    private sealed class Pc
    {
        public List<Fake> allThings = [];
        public List<List<Fake>> normal = [[]];
        public List<List<Fake>> rare = Buckets(250);
        public List<List<Fake>> longList = Buckets(2000);
        public List<Fake> toRegister = [];
        public List<Fake> toDeregister = [];

        public List<ListHash> Checkpoint(int mapTick) =>
        [
            ListOrderHash.OfList(SyncedList.AllThings, mapTick, allThings, IsMote, IdOf),
            ListOrderHash.OfTickList(SyncedList.TickNormal, mapTick, normal, toRegister, toDeregister, IsMote, IdOf),
            ListOrderHash.OfTickList(SyncedList.TickRare, mapTick, rare, [], [], IsMote, IdOf),
            ListOrderHash.OfTickList(SyncedList.TickLong, mapTick, longList, [], [], IsMote, IdOf)
        ];

        public Pc Clone() => new()
        {
            allThings = [..allThings],
            normal = normal.Select(b => new List<Fake>(b)).ToList(),
            rare = rare.Select(b => new List<Fake>(b)).ToList(),
            longList = longList.Select(b => new List<Fake>(b)).ToList(),
            toRegister = [..toRegister],
            toDeregister = [..toDeregister]
        };
    }

    private static List<List<Fake>> Buckets(int n) => Enumerable.Range(0, n).Select(_ => new List<Fake>()).ToList();

    // A map like the lab's: 2,000 listed things, 300 in the normal tick list, some rare / long tickers, 3 text motes
    private static Pc SampleMap()
    {
        var pc = new Pc();
        for (int i = 0; i < 2000; i++)
            pc.allThings.Add(new Fake(1000 + i * 7));
        for (int i = 0; i < 300; i++)
            pc.normal[0].Add(pc.allThings[i * 3]);
        for (int i = 0; i < 500; i++)
            pc.rare[(1000 + i * 11) % 250].Add(new Fake(1000 + i * 11));
        for (int i = 0; i < 50; i++)
            pc.longList[(5000 + i) % 2000].Add(new Fake(5000 + i));

        // Mote_Text: listed (drawGUIOverlay) and ticking, thingIDNumber -1
        pc.allThings.Insert(10, new Fake(-1, Mote: true));
        pc.allThings.Insert(500, new Fake(-1, Mote: true));
        pc.allThings.Add(new Fake(-1, Mote: true));
        pc.normal[0].Insert(5, new Fake(-1, Mote: true));
        return pc;
    }

    private static string? Compare(Pc host, Pc arbiter, int mapTick = 12360) =>
        ListOrderHash.FirstDifference(host.Checkpoint(mapTick), arbiter.Checkpoint(mapTick), "local", "remote");

    private static void Swap<T>(List<T> list, int i, int j) => (list[i], list[j]) = (list[j], list[i]);

    [Test]
    public void EqualLists_Pass()
    {
        var host = SampleMap();
        Assert.That(Compare(host, host.Clone()), Is.Null);
        Assert.That(Compare(new Pc(), new Pc()), Is.Null);
    }

    [Test]
    public void SwappedPairInAllThings_FailsAndNamesAllThings()
    {
        var host = SampleMap();
        var arbiter = host.Clone();
        Swap(arbiter.allThings, 100, 1700);

        var msg = Compare(host, arbiter);
        Assert.That(msg, Is.Not.Null);
        Assert.That(msg, Does.Contain("listerThings.AllThings: the same 2000 things in another order"));
        Assert.That(msg, Does.Contain("at map tick 12360"));
        Assert.That(msg, Does.Not.Contain("tickList"));
    }

    [Test]
    public void SwappedPairInTickNormal_FailsAndNamesTickListNormal()
    {
        var host = SampleMap();
        var arbiter = host.Clone();
        Swap(arbiter.normal[0], 20, 21);

        var msg = Compare(host, arbiter);
        Assert.That(msg, Does.Contain("tickListNormal: the same 300 things in another order"));
        Assert.That(msg, Does.Not.Contain("listerThings.AllThings"));
    }

    [Test]
    public void SwappedPairInRareBucket_FailsAndNamesTickListRare()
    {
        var host = SampleMap();
        var arbiter = host.Clone();
        var bucket = arbiter.rare.First(b => b.Count >= 2);
        Swap(bucket, 0, 1);

        Assert.That(Compare(host, arbiter), Does.Contain("tickListRare: the same 500 things in another order"));
    }

    [Test]
    public void TwoListsSplitAtOneCheckpoint_BothNamed()
    {
        var host = SampleMap();
        var arbiter = host.Clone();
        Swap(arbiter.allThings, 3, 4);
        Swap(arbiter.normal[0], 7, 8);

        var msg = Compare(host, arbiter);
        Assert.That(msg, Does.Contain("listerThings.AllThings: the same"));
        Assert.That(msg, Does.Contain("tickListNormal: the same"));
    }

    [Test]
    public void MoteOnlyDifference_Passes()
    {
        var host = SampleMap();
        var arbiter = host.Clone();

        // The arbiter's text motes left at other moments (gone, or still there), and it has one more
        arbiter.allThings.RemoveAt(500);
        arbiter.allThings.RemoveAt(10);
        arbiter.allThings.Insert(1234, new Fake(-1, Mote: true));
        arbiter.normal[0].RemoveAt(5);
        arbiter.normal[0].Add(new Fake(-1, Mote: true));
        arbiter.toDeregister.Add(new Fake(-1, Mote: true));
        arbiter.toRegister.Insert(0, new Fake(-1, Mote: true));

        Assert.That(Compare(host, arbiter), Is.Null);
    }

    [Test]
    public void DifferentThing_FailsWithCounts()
    {
        var host = SampleMap();
        var arbiter = host.Clone();
        arbiter.allThings.Add(new Fake(999_999));

        Assert.That(Compare(host, arbiter),
            Does.Contain("listerThings.AllThings: different things (2000 local vs 2001 remote)"));

        // Same count, one id replaced
        var other = host.Clone();
        other.allThings[42] = new Fake(888_888);
        Assert.That(Compare(host, other),
            Does.Contain("listerThings.AllThings: different things (2000 local vs 2000 remote)"));
    }

    [Test]
    public void PendingRegistrationOrder_Fails()
    {
        var host = SampleMap();
        host.toRegister.AddRange([new Fake(70_001), new Fake(70_002)]);
        var arbiter = host.Clone();
        Swap(arbiter.toRegister, 0, 1);

        Assert.That(Compare(host, arbiter), Does.Contain("tickListNormal: the same 302 things in another order"));
    }

    [Test]
    public void ThingInAnotherBucket_IsOtherContent()
    {
        // Same concatenated sequence, other bucket boundaries
        var host = new Pc();
        host.rare[3].AddRange([new Fake(1), new Fake(2)]);
        var arbiter = new Pc();
        arbiter.rare[3].Add(new Fake(1));
        arbiter.rare[4].Add(new Fake(2));

        Assert.That(Compare(host, arbiter), Does.Contain("tickListRare: different things (2 local vs 2 remote)"));
    }

    // Row 112 in miniature: the same op sequence on both PCs except the moment a text mote leaves. With vanilla 4981
    // swap-removal the shared things end in a different order; with the C12 guard (order-keeping removal) they do not.
    [TestCase(false, ExpectedResult = true)]
    [TestCase(true, ExpectedResult = false)]
    public bool MoteLeavingAtAnotherMoment_SplitsOnlyWithSwapRemoval(bool keepOrder)
    {
        void Remove(List<Fake> list, Fake t)
        {
            int i = list.IndexOf(t);
            if (i < 0) return;
            if (keepOrder)
            {
                list.RemoveAt(i);
                return;
            }

            int last = list.Count - 1;
            list[i] = list[last];
            list.RemoveAt(last);
        }

        var mote = new Fake(-1, Mote: true);
        var host = new List<Fake> { new(1), mote, new(2), new(3) };
        var arbiter = new List<Fake>(host);

        Remove(host, mote); // host: the mote's 2.2 s are up before the next synced spawn
        host.Add(new Fake(4));
        arbiter.Add(new Fake(4)); // the synced spawn, on both
        Remove(arbiter, mote); // arbiter: a few frames later

        var a = ListOrderHash.OfList(SyncedList.AllThings, 60, host, IsMote, IdOf);
        var b = ListOrderHash.OfList(SyncedList.AllThings, 60, arbiter, IsMote, IdOf);
        Assert.That(a.contentHash, Is.EqualTo(b.contentHash), "same things either way");
        return ListOrderHash.FirstDifference([a], [b], "local", "remote") != null;
    }

    [Test]
    public void CheckpointCountDiffers_Fails()
    {
        var pc = SampleMap();
        var two = pc.Checkpoint(60).Concat(pc.Checkpoint(120)).ToList();
        var one = pc.Checkpoint(60);

        Assert.That(ListOrderHash.FirstDifference(two, one, "local", "remote"),
            Does.Contain("list checkpoint count differs: 8 (local) vs 4 (remote)"));
        Assert.That(ListOrderHash.FirstDifference(one, [], "local", "remote"), Does.Contain("4 (local) vs 0 (remote)"));
        Assert.That(ListOrderHash.FirstDifference(null, [], "local", "remote"), Is.Null);
        Assert.That(ListOrderHash.FirstDifference(null, null, "local", "remote"), Is.Null);
    }

    [Test]
    public void CheckpointsAtOtherTicks_Fail()
    {
        var pc = SampleMap();
        Assert.That(ListOrderHash.FirstDifference(pc.Checkpoint(60), pc.Checkpoint(120), "local", "remote"),
            Does.Contain("list checkpoints don't line up"));
    }

    [Test]
    public void CheckpointInterval_IsSixtyMapTicks()
    {
        Assert.That(ListOrderHash.CheckpointInterval, Is.EqualTo(60));
        Assert.That(ListOrderHash.IsCheckpoint(0), Is.True);
        Assert.That(ListOrderHash.IsCheckpoint(12360), Is.True);
        Assert.That(ListOrderHash.IsCheckpoint(12359), Is.False);
        Assert.That(ListOrderHash.IsCheckpoint(12361), Is.False);
    }

    // Known answer: the hash has no per-process seed (unlike System.HashCode or string.GetHashCode on .NET), so the same
    // ids give these exact values on every PC and in every run. If the function is ever changed on purpose, update the
    // values AND MpVersion.Protocol.
    [Test]
    public void KnownAnswer_SameOnEveryPc()
    {
        var items = new List<Fake> { new(5), new(-1, Mote: true), new(17), new(123456) };
        var h = ListOrderHash.OfList(SyncedList.AllThings, 60, items, IsMote, IdOf);

        Assert.That(h.count, Is.EqualTo(3));
        Assert.That(h.orderHash.ToString("x16"), Is.EqualTo(KnownOrder));
        Assert.That(h.contentHash.ToString("x16"), Is.EqualTo(KnownContent));
    }

    // From an independent re-implementation of the documented algorithm (lane d113_sync tools/known_answer.py)
    private const string KnownOrder = "68f54c53c08302c8";
    private const string KnownContent = "1d6e0c7e4fd2c9a6";

    // ---- wire ----

    private static List<ListHash> SomeHashes() => SampleMap().Checkpoint(12360);

    private static SyncOpinion OpinionWithLists(List<ListHash>? lists) => new()
    {
        startTick = 12345,
        commandRandomStates = [1, 2, 3],
        worldRandomStates = [10, 20, 30],
        mapRandomStates =
        [
            new MapRandomState { mapId = 0, randomStates = [111, 222], listHashes = lists! },
            new MapRandomState { mapId = 3, randomStates = [333] }
        ],
        traceHashes = [999, 888],
        simulating = false,
        roundMode = RoundModeEnum.ToNearest
    };

    [Test]
    public void ListHashes_RoundTripThroughTheSyncInfoPacket()
    {
        var lists = SomeHashes();
        var packet = new ClientSyncInfoPacket { SyncOpinion = OpinionWithLists(lists) };

        var back = packet.SyncOpinion;
        Assert.That(back.mapRandomStates[0].listHashes, Is.EqualTo(lists));
        Assert.That(back.mapRandomStates[1].listHashes, Is.Empty, "a map without a checkpoint carries an empty list");
        Assert.That(ListOrderHash.FirstDifference(back.mapRandomStates[0].listHashes, lists, "a", "b"), Is.Null);
    }

    // The server keeps the raw packet bytes (ServerPlayingState.HandleDesyncCheck) and a joiner parses them with
    // ClientSyncOpinion.Deserialize (ClientLoadingState). This mirrors Deserialize's reads exactly; the bytes must be
    // used up with nothing left over.
    [Test]
    public void PacketBytes_ParseWithTheClientSyncOpinionLayout()
    {
        var lists = SomeHashes();
        var raw = new ClientSyncInfoPacket { SyncOpinion = OpinionWithLists(lists) }.rawSyncOpinion;

        var data = new ByteReader(raw!);
        Assert.That(data.ReadInt32(), Is.EqualTo(12345));
        Assert.That(data.ReadPrefixedUInts(), Is.EqualTo(new uint[] { 1, 2, 3 }));
        Assert.That(data.ReadPrefixedUInts(), Is.EqualTo(new uint[] { 10, 20, 30 }));
        Assert.That(data.ReadInt32(), Is.EqualTo(2));

        Assert.That(data.ReadInt32(), Is.EqualTo(0));
        Assert.That(data.ReadPrefixedUInts(), Is.EqualTo(new uint[] { 111, 222 }));
        Assert.That(ListHash.ReadList(data), Is.EqualTo(lists));

        Assert.That(data.ReadInt32(), Is.EqualTo(3));
        Assert.That(data.ReadPrefixedUInts(), Is.EqualTo(new uint[] { 333 }));
        Assert.That(ListHash.ReadList(data), Is.Empty);

        Assert.That(data.ReadPrefixedInts(), Is.EqualTo(new[] { 999, 888 }));
        Assert.That(data.ReadBool(), Is.False);
        Assert.That(data.ReadShort(), Is.EqualTo((short)RoundModeEnum.ToNearest));
        Assert.That(data.Left, Is.EqualTo(0));
    }

    [Test]
    public void WriteList_WritesTheSameBytesAsBind()
    {
        var lists = SomeHashes();

        var viaBind = BinderOf.List(BinderOf.Identity<ListHash>()).Serialize(lists);
        var writer = new ByteWriter();
        ListHash.WriteList(writer, lists);

        Assert.That(writer.ToArray(), Is.EqualTo(viaBind));

        var empty = new ByteWriter();
        ListHash.WriteList(empty, null);
        Assert.That(empty.ToArray(), Is.EqualTo(new byte[] { 0, 0, 0, 0 }));
    }

    [Test]
    public void NullListHashes_SerializeAsEmpty()
    {
        var packet = new ClientSyncInfoPacket { SyncOpinion = OpinionWithLists(null) };
        Assert.That(packet.SyncOpinion.mapRandomStates[0].listHashes, Is.Empty);
    }
}
