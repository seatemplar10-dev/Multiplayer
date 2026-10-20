using System.Collections.Generic;
using Multiplayer.Common.Networking.Packet;

namespace Multiplayer.Common;

// 1.7 lab, FIXES row 99 (live full-state checksum). Sea's MP Lab harness (MPLabFullState, -mplabfull=K -mplabfulllive) walks the
// whole simulation state every K MP timer ticks at the same point of the tick on every PC (TickPatch.DoTick's postfix) and hands
// the walk's root hash to SyncCoordinator.TryAddFullStateHash. Every sync opinion then carries the checkpoints of the last
// CarryTicks MP ticks, so ANY compared window after a checkpoint compares it: only part of the 30-tick windows are compared on a
// process (lab run mp94: the host's first 100 checks spanned 6,937 MP ticks = 231 windows). Two opinions are compared on the MP
// timers both carry; a timer only one side carries (a PC that joined later, a PC with another K) is not a difference.
// Pure code: no game types. Tests: Source/Tests/FullStateCheckpointTest.cs.

/// <summary>One full-state checkpoint: the MP timer it was taken at and the root hash of the walk.</summary>
public record struct FullStateHash : IPacketBufferable
{
    public int timer; // TickPatch.Timer after the tick (the harness's DoTick postfix)
    public ulong hash;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref timer);
        buf.Bind(ref hash);
    }

    /// <summary>Most checkpoints one opinion carries (writer and reader alike).</summary>
    public const int MaxPerOpinion = 1 << 10;

    /// <summary>
    /// The bytes <see cref="Bind"/> writes for a list of these (count, then each entry), for ClientSyncOpinion's own Serialize /
    /// Deserialize: the server keeps the raw packet bytes and a joiner parses them with Deserialize, so both layouts must stay
    /// the same (FullStateCheckpointTest checks it). Null is written as an empty list.
    /// </summary>
    public static void WriteList(ByteWriter writer, IReadOnlyList<FullStateHash>? hashes)
    {
        int n = hashes?.Count ?? 0;
        writer.WriteInt32(n);
        for (int i = 0; i < n; i++)
        {
            writer.WriteInt32(hashes![i].timer);
            writer.WriteULong(hashes[i].hash);
        }
    }

    public static List<FullStateHash> ReadList(ByteReader reader)
    {
        int n = reader.ReadInt32();
        if (n < 0 || n > MaxPerOpinion) throw new ReaderException($"Full-state checkpoint count out of range ({n})");

        var result = new List<FullStateHash>(n);
        for (int i = 0; i < n; i++)
            result.Add(new FullStateHash { timer = reader.ReadInt32(), hash = reader.ReadULong() });
        return result;
    }
}

public static class FullStateCheckpoints
{
    /// <summary>
    /// MP ticks a checkpoint stays in every opinion after it was taken. A compile-time constant: every PC carries the same
    /// timers (MpVersion.Protocol changes with this file). 7,500 = three checkpoints at the lab's K = 2,500, and no more than the
    /// harness keeps leaf files for (it keeps at least CarryTicks / K + 2 per epoch in live mode).
    /// </summary>
    public const int CarryTicks = 7500;

    /// <summary>Adds a checkpoint; a timer taken again (a rewind after a reload) replaces the earlier walk of that timer.</summary>
    public static void Add(List<FullStateHash> recent, int timer, ulong hash)
    {
        recent.RemoveAll(h => h.timer == timer);
        recent.Add(new FullStateHash { timer = timer, hash = hash });
    }

    /// <summary>
    /// Keeps the checkpoints an opinion finished at MP timer <paramref name="now"/> carries: timer in (now - CarryTicks, now].
    /// A timer above now is from before a rewind (a resync reloads an older save) and goes.
    /// </summary>
    public static void Prune(List<FullStateHash> recent, int now) =>
        recent.RemoveAll(h => h.timer > now || h.timer <= now - CarryTicks);

    /// <summary>The checkpoints both lists carry, paired by MP timer, in <paramref name="a"/>'s order.</summary>
    public static List<(FullStateHash a, FullStateHash b)> Common(IReadOnlyList<FullStateHash>? a, IReadOnlyList<FullStateHash>? b)
    {
        var pairs = new List<(FullStateHash, FullStateHash)>();
        if (a == null || b == null) return pairs;
        foreach (var x in a)
            foreach (var y in b)
                if (y.timer == x.timer)
                {
                    pairs.Add((x, y));
                    break;
                }
        return pairs;
    }

    /// <summary>
    /// Null when every common MP timer has the same hash on both sides; else "at MP timer T: aName h / bName h" for the first
    /// common timer (in a's order) whose hashes differ.
    /// </summary>
    public static string? FirstDifference(IReadOnlyList<FullStateHash>? a, IReadOnlyList<FullStateHash>? b, string aName,
        string bName)
    {
        foreach (var (x, y) in Common(a, b))
            if (x.hash != y.hash)
                return $"at MP timer {x.timer}: {aName} {x.hash:x16} / {bName} {y.hash:x16}";
        return null;
    }
}
