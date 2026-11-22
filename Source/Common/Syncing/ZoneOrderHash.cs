using System;
using System.Collections.Generic;

namespace Multiplayer.Common;

// FIXES row 444 (W444B): zone / plan cell ORDER must be the same on every PC.
// Vanilla Zone.Cells shuffles the cell list once (cellsShuffled is not saved, AddCell resets it), which uses Rand and
// leaves a PC that reloaded in another order than a PC that did not. WorkGiver_Grower yields growZone.cells in list order
// and JobGiver_Work only calls HasJobOnCell while dist < best, so the order decides how many jobs a scan makes.
// Pure code: no game types. Tests: Source/Tests/ZoneOrderHashTest.cs.
public static class ZoneOrderHash
{
    /// <summary>
    /// Whether the Zone.Cells / Plan.Cells getter may run vanilla's shuffle. Before the fix: always outside MP, and in MP
    /// while ticking. Fix (row 444 on): outside MP only; in MP the saved / insertion order is kept on every PC.
    /// <paramref name="oldBehaviour"/> is the LabOff row 444 switch (true = the pre-fix rule).
    /// </summary>
    public static bool ShouldShuffle(bool inMultiplayer, bool ticking, bool oldBehaviour)
    {
        if (!inMultiplayer) return true;
        if (oldBehaviour) return ticking;
        return false;
    }

    private const int ZoneSegment = 0;

    /// <summary>
    /// One zone's entry: the faction id, the zone id, then the cells in list order (id of a cell from CellId).
    /// The caller passes the raw cells list (zone.cells), never the Cells getter.
    /// </summary>
    public static ListHash OfZone<T>(int mapTick, int factionId, int zoneId, List<T> cells, Func<T, int> cellId)
    {
        var h = new ListOrderHash.Hasher();
        h.BeginSegment(ZoneSegment);
        h.Add(factionId);
        h.Add(zoneId);
        for (int i = 0; i < cells.Count; i++)
            h.Add(cellId(cells[i]));
        return h.Finish(SyncedList.ZoneOrder, mapTick);
    }

    public static int CellId(int x, int y, int z) => unchecked(x * 73856093 ^ y * 83492791 ^ z * 19349663);

    /// <summary>
    /// Names the first zone entry that differs: ordinals are positions among the ZoneOrder entries of each list (both
    /// sides sort the same way: faction id, then zone id). keys[ordinal] is the local side's "faction F zone Z" text.
    /// Null when no ZoneOrder entry differs.
    /// </summary>
    public static string? NameFirstDifference(IReadOnlyList<ListHash>? a, IReadOnlyList<ListHash>? b, IReadOnlyList<string>? aKeys,
        IReadOnlyList<string>? bKeys, string aName, string bName)
    {
        if (a == null || b == null) return null;
        int ia = 0, ib = 0;
        int ord = -1;
        while (true)
        {
            while (ia < a.Count && a[ia].list != SyncedList.ZoneOrder) ia++;
            while (ib < b.Count && b[ib].list != SyncedList.ZoneOrder) ib++;
            if (ia >= a.Count || ib >= b.Count) return null;
            ord++;
            var x = a[ia++];
            var y = b[ib++];
            if (x.mapTick == y.mapTick && x == y) continue;

            string key = (aKeys != null && ord < aKeys.Count ? aKeys[ord] : null)
                         ?? (bKeys != null && ord < bKeys.Count ? bKeys[ord] : null) ?? $"zone entry #{ord}";
            string what = x.mapTick != y.mapTick ? "entries don't line up"
                : x.count != y.count || x.contentHash != y.contentHash ? $"different cells ({x.count} {aName} vs {y.count} {bName} ids)"
                : $"the same {x.count - 2} cells in another order";
            return $"zone order: {key} at map tick {x.mapTick}: {what}";
        }
    }
}

/// <summary>
/// FIXES row 444 (W444C): the IL the Zone.Cells / Plan.Cells transpiler puts after the cellsShuffled load. The stack is
/// (cellsShuffled, ShouldShuffle()) and `or` follows; a non-zero result skips the shuffle. The shipped IL used `not`,
/// which is bitwise: cellsShuffled | ~x is never zero, so the shuffle never ran, in single player either.
/// The fix is a logical not (ldc.i4.0; ceq): skip = cellsShuffled | (ShouldShuffle == 0).
/// </summary>
public static class ShuffleIl
{
    public static System.Reflection.Emit.OpCode[] NegateOps(bool oldShippedIl) => oldShippedIl
        ? new[] { System.Reflection.Emit.OpCodes.Not }
        : new[] { System.Reflection.Emit.OpCodes.Ldc_I4_0, System.Reflection.Emit.OpCodes.Ceq };
}
