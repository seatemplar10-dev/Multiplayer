using Multiplayer.Common;

namespace Tests;

// FIXES row 444 (W444B). A fake Zone with vanilla's lazy Cells getter (shuffle once, flag not saved) run under the
// ShouldShuffle rule of the Multiplayer fork: the pre-fix rule (shuffle in MP while ticking) and the fixed rule (never in MP).
// The game half (Zone.Cells transpiler, ListCheckpoint.TakeZones) needs the game and is not run here.
[TestFixture]
public class ZoneOrderHashTest
{
    private sealed class FakeZone(List<(int x, int z)> saved, bool oldRule, int rand, bool inMp = true, bool ticking = true)
    {
        private readonly List<(int x, int z)> cells = new(saved);
        private bool cellsShuffled; // not saved: false on a freshly loaded PC
        public List<(int x, int z)> Raw => cells;

        // vanilla: if (!cellsShuffled) { cells.Shuffle(); cellsShuffled = true; } return cells;
        // fork transpiler: if (!cellsShuffled || !ShouldShuffle()) { ... } -- as the field is read the shuffle runs
        // only when ShouldShuffle() is true; otherwise cellsShuffled is treated as true (see CellsShufflePatchShared)
        public List<(int x, int z)> Cells
        {
            get
            {
                if (!cellsShuffled && ZoneOrderHash.ShouldShuffle(inMp, ticking, oldRule))
                {
                    var r = new Random(rand);
                    for (int i = cells.Count - 1; i > 0; i--)
                    {
                        int j = r.Next(i + 1);
                        (cells[i], cells[j]) = (cells[j], cells[i]);
                    }
                    cellsShuffled = true;
                }
                return cells;
            }
        }
    }

    private static List<(int x, int z)> Saved() =>
        Enumerable.Range(0, 40).Select(i => (i % 8, i / 8)).ToList();

    private static ListHash Hash(FakeZone z, int faction = 3, int zoneId = 7) =>
        ZoneOrderHash.OfZone(120, faction, zoneId, z.Raw, c => ZoneOrderHash.CellId(c.x, 0, c.z));

    [Test]
    public void OldRule_TwoPcsTouchingCellsInTick_SplitOrder_RED_on_base()
    {
        var a = new FakeZone(Saved(), oldRule: true, rand: 1);
        var b = new FakeZone(Saved(), oldRule: true, rand: 2); // other Rand state at the first tick access
        _ = a.Cells; _ = b.Cells;
        Assert.That(Hash(a), Is.Not.EqualTo(Hash(b)), "base rule: the two PCs hold different cell orders");
    }

    [Test]
    public void FixedRule_NoShuffleInMp_SameOrder_GREEN_on_fix()
    {
        var a = new FakeZone(Saved(), oldRule: false, rand: 1);
        var b = new FakeZone(Saved(), oldRule: false, rand: 2);
        _ = a.Cells; _ = b.Cells;
        Assert.That(a.Raw, Is.EqualTo(Saved()));
        Assert.That(Hash(a), Is.EqualTo(Hash(b)));
    }

    [Test]
    public void Policy_Table()
    {
        Assert.That(ZoneOrderHash.ShouldShuffle(false, false, false), Is.True, "single player shuffles");
        Assert.That(ZoneOrderHash.ShouldShuffle(true, true, true), Is.True, "old rule: MP ticking shuffles");
        Assert.That(ZoneOrderHash.ShouldShuffle(true, false, true), Is.False, "old rule: MP not ticking does not");
        Assert.That(ZoneOrderHash.ShouldShuffle(true, true, false), Is.False, "fix: MP ticking does not");
        Assert.That(ZoneOrderHash.ShouldShuffle(true, false, false), Is.False, "fix: MP not ticking does not");
    }

    [Test]
    public void PlantedWrongOrder_IsNamedWithFactionAndZone()
    {
        var good = new FakeZone(Saved(), oldRule: false, rand: 1);
        var bad = new FakeZone(Saved(), oldRule: false, rand: 1);
        (bad.Raw[4], bad.Raw[5]) = (bad.Raw[5], bad.Raw[4]); // planted: two cells swapped

        var other = new FakeZone(Saved(), oldRule: false, rand: 1);
        var a = new List<ListHash> { Hash(other, 3, 6), Hash(good, 3, 7) };
        var b = new List<ListHash> { Hash(other, 3, 6), Hash(bad, 3, 7) };
        var keys = new List<string> { "faction 3 zone 6 (Sow A)", "faction 3 zone 7 (Sow B)" };

        var text = ZoneOrderHash.NameFirstDifference(a, b, keys, null, "local", "remote");
        Console.WriteLine("PLANTED: " + text);
        Assert.That(text, Does.Contain("faction 3 zone 7"));
        Assert.That(text, Does.Contain("another order"));
        Assert.That(ZoneOrderHash.NameFirstDifference(a, a, keys, null, "local", "remote"), Is.Null);

        // and the generic list-split text names the list
        var generic = ListOrderHash.FirstDifference(a, b, "local", "remote");
        Console.WriteLine("GENERIC: " + generic);
        Assert.That(generic, Does.Contain("zone cell order"));
    }
}
