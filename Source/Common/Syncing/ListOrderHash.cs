using System;
using System.Collections.Generic;
using System.Text;
using Multiplayer.Common.Networking.Packet;

namespace Multiplayer.Common;

// 1.7 lab, FIXES row 113 item 3 (live order sync). Row 112 (class C12) showed two PCs holding the SAME things in a
// different ORDER in listerThings and the tick lists, for thousands of ticks, while every random state still matched:
// the split only became a desync after a join point reload. These hashes put the order and the content of each map's
// shared lists into the sync opinion every CheckpointInterval map ticks, so a split is reported at the checkpoint after
// it starts, with the list named. Things that live per PC (motes) are left out by the caller's skip predicate.
// Pure code: no game types, no Rand, nothing but the ids it is given. Tests: Source/Tests/ListOrderHashTest.cs.

/// <summary>The shared lists a checkpoint hashes, in the order a checkpoint records them.</summary>
public enum SyncedList : byte
{
    AllThings = 0, // map.listerThings.AllThings
    TickNormal = 1, // AsyncTimeComp.tickListNormal
    TickRare = 2, // AsyncTimeComp.tickListRare
    TickLong = 3 // AsyncTimeComp.tickListLong
}

/// <summary>One list at one checkpoint: how many entries were hashed, their order and their content.</summary>
public record struct ListHash : IPacketBufferable
{
    public int mapTick;
    public SyncedList list;
    public int count; // entries hashed (skipped ones not counted)
    public ulong orderHash; // depends on the order of the entries
    public ulong contentHash; // same for the same entries in any order

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref mapTick);
        buf.BindEnum(ref list);
        buf.Bind(ref count);
        buf.Bind(ref orderHash);
        buf.Bind(ref contentHash);
    }

    /// <summary>Most checkpoint entries one map's part of one opinion holds (writer and reader alike).</summary>
    public const int MaxPerMap = 1 << 16;

    /// <summary>
    /// The bytes <see cref="Bind"/> writes for a list of these (count, then each entry), for ClientSyncOpinion's own
    /// Serialize / Deserialize: the server keeps the raw packet bytes and a joiner parses them with Deserialize, so both
    /// layouts must stay the same (ListOrderHashTest checks it). Null is written as an empty list.
    /// </summary>
    public static void WriteList(ByteWriter writer, IReadOnlyList<ListHash>? hashes)
    {
        int n = hashes?.Count ?? 0;
        writer.WriteInt32(n);
        for (int i = 0; i < n; i++)
        {
            var h = hashes![i];
            writer.WriteInt32(h.mapTick);
            writer.WriteByte((byte)h.list);
            writer.WriteInt32(h.count);
            writer.WriteULong(h.orderHash);
            writer.WriteULong(h.contentHash);
        }
    }

    public static List<ListHash> ReadList(ByteReader reader)
    {
        int n = reader.ReadInt32();
        if (n < 0 || n > MaxPerMap) throw new ReaderException($"List checkpoint count out of range ({n})");

        var result = new List<ListHash>(n);
        for (int i = 0; i < n; i++)
            result.Add(new ListHash
            {
                mapTick = reader.ReadInt32(),
                list = (SyncedList)reader.ReadByte(),
                count = reader.ReadInt32(),
                orderHash = reader.ReadULong(),
                contentHash = reader.ReadULong()
            });
        return result;
    }
}

public static class ListOrderHash
{
    /// <summary>
    /// Map ticks between checkpoints. A compile-time constant on purpose: every PC must take its checkpoints at the same
    /// map ticks (a per-PC setting could differ, and then the opinions differ in length = a false desync). Builds with a
    /// different value cannot join each other: MpVersion.Protocol changes with this file.
    /// </summary>
    public const int CheckpointInterval = 60;

    public static bool IsCheckpoint(int mapTick) => mapTick % CheckpointInterval == 0;

    // Segments of a tick list: its buckets are 0..n-1, then the two pending lists
    public const int RegisterSegment = -1;
    public const int DeregisterSegment = -2;

    private const ulong Golden = 0x9E3779B97F4A7C15UL;
    private const ulong SegmentSalt = 0xD6E8FEB86659FD93UL;

    // SplitMix64's finalizer: a bijection on 64 bits, the same on every PC (no per-process seed, unlike HashCode)
    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>
    /// Folds entries in. Order hash: h = Mix(h ^ m) per entry, so a swap of two entries changes it. Content hash: the sum
    /// of the entries' m, so it is the same for the same entries in any order. m mixes the id with its segment, so a thing
    /// in another bucket is other content. A segment's marker enters the order hash before its first counted entry only:
    /// empty buckets cost nothing.
    /// </summary>
    public struct Hasher
    {
        private ulong order;
        private ulong content;
        private int count;
        private int segment;
        private bool segmentPending;

        public void BeginSegment(int seg)
        {
            segment = seg;
            segmentPending = true;
        }

        public void Add(int id)
        {
            if (segmentPending)
            {
                order = Mix(order ^ (SegmentSalt + (uint)segment));
                segmentPending = false;
            }

            ulong m = Mix(((ulong)(uint)segment << 32 | (uint)id) + Golden);
            order = Mix(order ^ m);
            content += m;
            count++;
        }

        public ListHash Finish(SyncedList list, int mapTick) => new()
        {
            mapTick = mapTick,
            list = list,
            count = count,
            orderHash = order,
            contentHash = content
        };
    }

    /// <summary>One plain list (listerThings.AllThings), entries for which skip is true left out.</summary>
    public static ListHash OfList<T>(SyncedList list, int mapTick, List<T> items, Func<T, bool> skip, Func<T, int> id)
    {
        var h = new Hasher();
        h.BeginSegment(0);
        Fold(ref h, items, skip, id);
        return h.Finish(list, mapTick);
    }

    /// <summary>
    /// A Verse.TickList: its buckets in index order, then the things waiting to be registered and deregistered (their
    /// order decides the bucket order of the next tick).
    /// </summary>
    public static ListHash OfTickList<T>(SyncedList list, int mapTick, List<List<T>> buckets, List<T> toRegister,
        List<T> toDeregister, Func<T, bool> skip, Func<T, int> id)
    {
        var h = new Hasher();
        for (int b = 0; b < buckets.Count; b++)
        {
            h.BeginSegment(b);
            Fold(ref h, buckets[b], skip, id);
        }

        h.BeginSegment(RegisterSegment);
        Fold(ref h, toRegister, skip, id);
        h.BeginSegment(DeregisterSegment);
        Fold(ref h, toDeregister, skip, id);
        return h.Finish(list, mapTick);
    }

    private static void Fold<T>(ref Hasher h, List<T> items, Func<T, bool> skip, Func<T, int> id)
    {
        for (int i = 0; i < items.Count; i++)
        {
            var t = items[i];
            if (skip(t)) continue;
            h.Add(id(t));
        }
    }

    public static string Name(SyncedList list) => list switch
    {
        SyncedList.AllThings => "listerThings.AllThings",
        SyncedList.TickNormal => "tickListNormal",
        SyncedList.TickRare => "tickListRare",
        SyncedList.TickLong => "tickListLong",
        _ => $"list {(byte)list}"
    };

    /// <summary>
    /// Null when both sides took the same checkpoints with the same hashes. Otherwise names the first checkpoint that
    /// differs and every list that differs at it: "same N things in another order" (order hash only) or "different things"
    /// (count or content). aName / bName label the two sides in the message ("local", "remote").
    /// </summary>
    public static string? FirstDifference(IReadOnlyList<ListHash>? a, IReadOnlyList<ListHash>? b, string aName, string bName)
    {
        int aCount = a?.Count ?? 0, bCount = b?.Count ?? 0;
        int n = Math.Min(aCount, bCount);

        for (int i = 0; i < n; i++)
        {
            var x = a![i];
            var y = b![i];

            if (x.mapTick != y.mapTick || x.list != y.list)
                return $"list checkpoints don't line up: {Name(x.list)} at map tick {x.mapTick} ({aName}) vs " +
                       $"{Name(y.list)} at map tick {y.mapTick} ({bName})";

            if (x == y) continue;

            // Every list that differs at this checkpoint
            var text = new StringBuilder($"at map tick {x.mapTick}: ");
            bool first = true;
            for (int j = i; j < n && a[j].mapTick == x.mapTick && b[j].mapTick == x.mapTick; j++)
            {
                var p = a[j];
                var q = b[j];
                if (p.list != q.list || p == q) continue;

                if (!first) text.Append("; ");
                first = false;
                text.Append(Name(p.list)).Append(": ");
                if (p.count != q.count || p.contentHash != q.contentHash)
                    text.Append($"different things ({p.count} {aName} vs {q.count} {bName})");
                else
                    text.Append($"the same {p.count} things in another order");
            }

            text.Append($" (motes excluded, checkpoint every {CheckpointInterval} map ticks)");
            return text.ToString();
        }

        if (aCount != bCount)
        {
            var extra = aCount > bCount ? a![n] : b![n];
            return $"list checkpoint count differs: {aCount} ({aName}) vs {bCount} ({bName}), first unmatched: " +
                   $"{Name(extra.list)} at map tick {extra.mapTick}";
        }

        return null;
    }
}
