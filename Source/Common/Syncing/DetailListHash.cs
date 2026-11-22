using System;
using System.Collections.Generic;

namespace Multiplayer.Common;

// FIXES row 444 (W444C): two more checkpoint lists that name their split.
// ColonistBuildings: listerBuildings.allBuildingsColonist in list order (WorkGiver_Grower scans it first), plus the
//   allBuildingsNonColonist count. Entry 0 is the header (both counts, hash of the non-colonist count), then one entry
//   per BuildingChunk buildings (position-salted ids). The remote side sends hashes only, so the first differing
//   index is a range: [chunk * BuildingChunk, +BuildingChunk); the local building at the start of that range is named.
// Reservations: one entry per claimant (sorted by claimant id): its reservations sorted by (target thing id or cell, job loadID).
// Pure code: no game types. Tests: Source/Tests/DetailListHashTest.cs.
public static class DetailListHash
{
    public const int BuildingChunk = 32;

    /// <summary>Header entry: colonist count and non-colonist count.</summary>
    public static ListHash BuildingHeader(int mapTick, int colonistCount, int nonColonistCount)
    {
        var h = new ListOrderHash.Hasher();
        h.BeginSegment(-3);
        h.Add(colonistCount);
        h.Add(nonColonistCount);
        return h.Finish(SyncedList.ColonistBuildings, mapTick);
    }

    /// <summary>Chunk entry for ids[start .. min(start+BuildingChunk, Count)) in list order.</summary>
    public static ListHash BuildingChunkHash<T>(int mapTick, List<T> items, int start, Func<T, int> id)
    {
        var h = new ListOrderHash.Hasher();
        h.BeginSegment(start / BuildingChunk);
        int end = Math.Min(items.Count, start + BuildingChunk);
        for (int i = start; i < end; i++) h.Add(id(items[i]));
        return h.Finish(SyncedList.ColonistBuildings, mapTick);
    }

    /// <summary>One reservation row as (target key, job loadID). Target key: thing id, or a cell id with the high bit set marker.</summary>
    public readonly record struct Res(int TargetKey, int JobId) : IComparable<Res>
    {
        public int CompareTo(Res o)
        {
            int c = TargetKey.CompareTo(o.TargetKey);
            return c != 0 ? c : JobId.CompareTo(o.JobId);
        }
    }

    /// <summary>One claimant's entry; rows are sorted here (the caller's order does not matter).</summary>
    public static ListHash OfClaimant(int mapTick, int claimantId, List<Res> rows)
    {
        rows.Sort();
        var h = new ListOrderHash.Hasher();
        h.BeginSegment(-4);
        h.Add(claimantId);
        foreach (var r in rows)
        {
            h.Add(r.TargetKey);
            h.Add(r.JobId);
        }
        return h.Finish(SyncedList.Reservations, mapTick);
    }

    /// <summary>
    /// Names the first entry of list kind <paramref name="kind"/> that differs. Ordinals are positions among that kind's
    /// entries on each side; keys[ordinal] is the local side's text. Null when none differs.
    /// </summary>
    public static string? NameFirstDifference(SyncedList kind, IReadOnlyList<ListHash>? a, IReadOnlyList<ListHash>? b,
        IReadOnlyList<string>? aKeys, IReadOnlyList<string>? bKeys, string aName, string bName)
    {
        if (a == null || b == null) return null;
        int ia = 0, ib = 0, ord = -1;
        while (true)
        {
            while (ia < a.Count && a[ia].list != kind) ia++;
            while (ib < b.Count && b[ib].list != kind) ib++;
            if (ia >= a.Count && ib >= b.Count) return null;
            ord++;
            string key = (aKeys != null && ord < aKeys.Count ? aKeys[ord] : null)
                         ?? (bKeys != null && ord < bKeys.Count ? bKeys[ord] : null) ?? $"entry #{ord}";
            string title = kind == SyncedList.ColonistBuildings ? "colonist building list" : "reservations";
            if (ia >= a.Count || ib >= b.Count)
                return $"{title}: {key}: one side has fewer entries ({aName} {(ia >= a.Count ? "ends" : "continues")})";
            var x = a[ia++];
            var y = b[ib++];
            if (x.mapTick == y.mapTick && x == y) continue;
            string what = x.mapTick != y.mapTick ? "entries don't line up"
                : x.count != y.count || x.contentHash != y.contentHash ? $"different content ({x.count} {aName} vs {y.count} {bName} ids)"
                : "same content in another order";
            return $"{title}: {key} at map tick {x.mapTick}: {what}";
        }
    }
}
