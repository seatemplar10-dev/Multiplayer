using System;
using System.Collections.Generic;

namespace Multiplayer.Common.Networking.Packet;

[PacketDefinition(Packets.Client_SyncInfo, allowFragmented: true)]
public record struct ClientSyncInfoPacket : IPacket
{
    // 8MiB. For some big saves, the sync opinion can reach ~2.5MiB, so this includes some leeway.
    public const int MaxLength = 1 << 23;
    public byte[] rawSyncOpinion;

    public SyncOpinion SyncOpinion
    {
        get => SyncOpinionBinder.Deserialize(rawSyncOpinion);
        set => rawSyncOpinion = SyncOpinionBinder.Serialize(value);
    }

    public void Bind(PacketBuffer buf)
    {
        buf.BindRemaining(ref rawSyncOpinion, maxLength: MaxLength);
    }

    private static readonly Binder<SyncOpinion> SyncOpinionBinder = BinderOf.Identity<SyncOpinion>();
}

[PacketDefinition(Packets.Server_SyncInfo, allowFragmented: true)]
public record struct ServerSyncInfoPacket : IPacket
{
    public byte[] rawSyncOpinion;
    public SyncOpinion SyncOpinion
    {
        get => SyncOpinionBinder.Deserialize(rawSyncOpinion);
        set => rawSyncOpinion = SyncOpinionBinder.Serialize(value);
    }

    public void Bind(PacketBuffer buf)
    {
        buf.BindRemaining(ref rawSyncOpinion, maxLength: ClientSyncInfoPacket.MaxLength);
    }

    private static readonly Binder<SyncOpinion> SyncOpinionBinder = BinderOf.Identity<SyncOpinion>();
}

public record struct SyncOpinion : IPacketBufferable
{
    /// <summary>
    /// The most trace hashes (and, on the client, trace items) one opinion holds. Every side stops recording at this
    /// count, so the host, the arbiter and a joiner all keep and compare the same prefix of the window and the
    /// opinion always fits its packet. Without it, a window with millions of traces (<c>-mptracemode=Slow</c> records
    /// one per Rand call) overflowed the list limit below and the whole opinion was never sent. Random states are not
    /// capped, so the window stays checked after the cap. 1 &lt;&lt; 16 hashes are 256 KiB: a joining client is sent up
    /// to 10 stored opinions (ServerPlayingState) inside the world data packet, which is limited to
    /// ConnectionBase.MaxFragmentPacketTotalSize (32 MiB) together with the save, and the lab's test save is 170 MB of
    /// XML. A normal 30-tick window held ~2,800 traces in run mp21; only the first window after hosting held millions.
    /// </summary>
    public const int MaxTraceHashes = 1 << 16;

    /// <summary>
    /// Whether two trace hash lists agree. Only the first <see cref="MaxTraceHashes"/> entries of each count, so a
    /// longer list (an older sender, or one that was never capped) cannot turn the cap into a false mismatch. Below
    /// the cap the lists must be equal, including their length, as before.
    /// </summary>
    public static bool TraceHashesMatch(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        var count = Math.Min(a.Count, MaxTraceHashes);
        if (count != Math.Min(b.Count, MaxTraceHashes))
            return false;

        for (var i = 0; i < count; i++)
            if (a[i] != b[i])
                return false;

        return true;
    }

    public int startTick;
    public List<uint> commandRandomStates;
    public List<uint> worldRandomStates;
    public List<MapRandomState> mapRandomStates;
    public List<int> traceHashes;
    public bool simulating;
    public RoundModeEnum roundMode;

    // FIXES row 99 (live full-state checksum): the harness's recent full-state checkpoints (FullStateCheckpoints), protocol 59+
    public List<FullStateHash> fullStateHashes;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref startTick);

        buf.Bind(ref commandRandomStates, BinderOf.UInt());
        buf.Bind(ref worldRandomStates, BinderOf.UInt());
        buf.Bind(ref mapRandomStates, BinderOf.Identity<MapRandomState>());
        // Max 2M trace hashes. For some big saves, the trace hashes can reach ~600k entries, so this includes
        // some leeway. Senders stay under MaxTraceHashes, which is far lower: the packet as a whole is limited too.
        buf.Bind(ref traceHashes, BinderOf.Int(), maxLength: 1<<21);

        buf.Bind(ref simulating);
        buf.BindEnum(ref roundMode);

        // Written as an empty list when the opinion carries no checkpoint. Same bytes as FullStateHash.WriteList.
        if (buf.isWriting) fullStateHashes ??= [];
        buf.Bind(ref fullStateHashes, BinderOf.Identity<FullStateHash>(), maxLength: FullStateHash.MaxPerOpinion);
    }
}

public record struct MapRandomState : IPacketBufferable
{
    public int mapId;
    public List<uint> randomStates;

    // FIXES row 113 item 3: order and content hashes of the map's shared lists (ListOrderHash), protocol 58+
    public List<ListHash> listHashes;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref mapId);
        buf.Bind(ref randomStates, BinderOf.UInt());

        // Written as an empty list when no checkpoint fell into the window. Same bytes as ListHash.WriteList.
        if (buf.isWriting) listHashes ??= [];
        buf.Bind(ref listHashes, BinderOf.Identity<ListHash>(), maxLength: ListHash.MaxPerMap);
    }
}
