using System.Reflection.Emit;
using Multiplayer.Common;

namespace Tests;

// FIXES row 444 (W444C): the shuffle IL (run through a DynamicMethod) and the building / reservation checkpoint entries.
[TestFixture]
public class DetailListHashTest
{
    // stack as the transpiler leaves it: (cellsShuffled, ShouldShuffle()), then the negate ops, then `or`; non-zero = skip shuffle
    private static Func<int, int, int> Build(bool oldIl)
    {
        var dm = new DynamicMethod("t", typeof(int), new[] { typeof(int), typeof(int) });
        var il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        foreach (var op in ShuffleIl.NegateOps(oldIl)) il.Emit(op);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Ret);
        return (Func<int, int, int>)dm.CreateDelegate(typeof(Func<int, int, int>));
    }

    [Test]
    public void OldIl_AlwaysSkipsTheShuffle_RED()
    {
        var f = Build(true);
        foreach (var cs in new[] { 0, 1 })
            foreach (var should in new[] { 0, 1 })
            {
                Console.WriteLine($"OLD cellsShuffled={cs} ShouldShuffle={should} -> skip={f(cs, should) != 0}");
                Assert.That(f(cs, should), Is.Not.Zero, "old IL: skips in every combination (the bug)");
            }
    }

    [Test]
    public void FixedIl_SkipsOnlyWhenShuffledOrNotAllowed_GREEN()
    {
        var f = Build(false);
        foreach (var cs in new[] { 0, 1 })
            foreach (var should in new[] { 0, 1 })
            {
                bool skip = f(cs, should) != 0;
                Console.WriteLine($"NEW cellsShuffled={cs} ShouldShuffle={should} -> skip={skip}");
                Assert.That(skip, Is.EqualTo(cs == 1 || should == 0));
            }
        // single player: ShouldShuffle true -> shuffles when not yet shuffled; MP: false -> never
        Assert.That(f(0, ZoneOrderHash.ShouldShuffle(false, false, false) ? 1 : 0), Is.Zero, "single player shuffles");
        Assert.That(f(0, ZoneOrderHash.ShouldShuffle(true, true, false) ? 1 : 0), Is.Not.Zero, "MP never shuffles");
    }

    private record B(int Id);

    private static List<ListHash> Buildings(List<B> l, int nonCol)
    {
        var r = new List<ListHash> { DetailListHash.BuildingHeader(60, l.Count, nonCol) };
        for (int i = 0; i < l.Count; i += DetailListHash.BuildingChunk)
            r.Add(DetailListHash.BuildingChunkHash(60, l, i, b => b.Id));
        return r;
    }

    [Test]
    public void BuildingList_PlantedOrderSplit_NamesChunk()
    {
        var good = Enumerable.Range(1, 100).Select(i => new B(i)).ToList();
        var bad = good.ToList();
        (bad[40], bad[41]) = (bad[41], bad[40]); // chunk 1 = index 32..63
        var keys = new List<string> { "counts", "index 0..31", "index 32..63, first building id 33 Wall faction 1", "index 64..95", "index 96..99" };
        var text = DetailListHash.NameFirstDifference(SyncedList.ColonistBuildings, Buildings(good, 5), Buildings(bad, 5), keys, null, "local", "remote");
        Console.WriteLine("PLANTED: " + text);
        Assert.That(text, Does.Contain("colonist building list").And.Contain("index 32..63").And.Contain("another order"));
        Assert.That(DetailListHash.NameFirstDifference(SyncedList.ColonistBuildings, Buildings(good, 5), Buildings(good, 5), keys, null, "l", "r"), Is.Null);
        var nc = DetailListHash.NameFirstDifference(SyncedList.ColonistBuildings, Buildings(good, 5), Buildings(good, 6), keys, null, "l", "r");
        Assert.That(nc, Does.Contain("counts"));
    }

    private static List<ListHash> Res(params (int claimant, (int t, int j)[] rows)[] c) =>
        c.Select(x => DetailListHash.OfClaimant(60, x.claimant, x.rows.Select(r => new DetailListHash.Res(r.t, r.j)).ToList())).ToList();

    [Test]
    public void Reservations_RowOrderIrrelevant_ButContentCaught_NamesClaimant()
    {
        var a = Res((10, new[] { (5, 1), (6, 2) }), (11, new[] { (7, 3) }));
        var sameShuffled = Res((10, new[] { (6, 2), (5, 1) }), (11, new[] { (7, 3) }));
        Assert.That(a, Is.EqualTo(sameShuffled));
        var b = Res((10, new[] { (5, 1), (6, 2) }), (11, new[] { (7, 4) })); // other job id
        var keys = new List<string> { "claimant 10 (Bob)", "claimant 11 (Ann)" };
        var text = DetailListHash.NameFirstDifference(SyncedList.Reservations, a, b, keys, null, "local", "remote");
        Console.WriteLine("PLANTED: " + text);
        Assert.That(text, Does.Contain("claimant 11 (Ann)"));
        Assert.That(DetailListHash.NameFirstDifference(SyncedList.Reservations, a, a, keys, null, "l", "r"), Is.Null);
    }

    [Test]
    public void GenericName_AndWireBytes()
    {
        Assert.That(ListOrderHash.Name(SyncedList.ColonistBuildings), Is.EqualTo("colonist building list"));
        Assert.That(ListOrderHash.Name(SyncedList.Reservations), Is.EqualTo("reservations"));
        var l = Buildings(Enumerable.Range(1, 40).Select(i => new B(i)).ToList(), 2);
        var w = new ByteWriter();
        ListHash.WriteList(w, l);
        Assert.That(ListHash.ReadList(new ByteReader(w.ToArray())), Is.EqualTo(l));
    }
}
