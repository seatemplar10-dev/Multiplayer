namespace Multiplayer.Common.Networking.Packet;

[PacketDefinition(Packets.Client_Traces, allowFragmented: true)]
public record struct ClientTracesPacket : IPacket
{
    public int playerId;
    public byte[] rawTraces;
    public byte[] rawJittedMethods;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref playerId);
        buf.BindBytes(ref rawTraces);
        buf.BindBytes(ref rawJittedMethods);
    }
}

[PacketDefinition(Packets.Server_Traces, allowFragmented: true)]
public record struct ServerTracesPacket : IPacket
{
    public enum Mode : byte
    {
        Request, Transfer
    }

    public Mode mode;

    public int tick;
    public int diffAt;
    public int playerId;

    // Used in transfer only
    public byte[] rawTraces;
    public byte[] rawJittedMethods;

    public static ServerTracesPacket Request(int tick, int diffAt, int playerId) => new()
    {
        mode = Mode.Request,
        tick = tick,
        diffAt = diffAt,
        playerId = playerId
    };

    public static ServerTracesPacket Transfer(byte[] rawTraces, byte[] rawJittedMethods) =>
        new() { mode = Mode.Transfer, rawTraces = rawTraces, rawJittedMethods = rawJittedMethods };

    public void Bind(PacketBuffer buf)
    {
        buf.BindEnum(ref mode);
        if (mode == Mode.Request)
        {
            buf.Bind(ref tick);
            buf.Bind(ref diffAt);
            buf.Bind(ref playerId);
        }
        else
        {
            buf.BindBytes(ref rawTraces);
            buf.BindBytes(ref rawJittedMethods);
        }
    }
}

// FIXES row 447: the desynced client pushes its own trace window to the host, so the host's report does not depend on
// the client player's window or tasks. rawTraces is GZip text. The server forwards it to the host as ServerDesyncTracesPacket.
[PacketDefinition(Packets.Client_DesyncTraces, allowFragmented: true)]
public record struct ClientDesyncTracesPacket : IPacket
{
    public int tick;
    public int diffAt;
    public int traceCount;
    public string version;
    public int protocol;
    public byte[] rawTraces;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref tick);
        buf.Bind(ref diffAt);
        buf.Bind(ref traceCount);
        buf.Bind(ref version);
        buf.Bind(ref protocol);
        buf.BindBytes(ref rawTraces);
    }
}

[PacketDefinition(Packets.Server_DesyncTraces, allowFragmented: true)]
public record struct ServerDesyncTracesPacket : IPacket
{
    public int playerId;
    public string username;
    public int tick;
    public int diffAt;
    public int traceCount;
    public string version;
    public int protocol;
    public byte[] rawTraces;

    public static ServerDesyncTracesPacket From(int playerId, string username, ClientDesyncTracesPacket p) => new()
    {
        playerId = playerId, username = username, tick = p.tick, diffAt = p.diffAt, traceCount = p.traceCount,
        version = p.version, protocol = p.protocol, rawTraces = p.rawTraces
    };

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref playerId);
        buf.Bind(ref username);
        buf.Bind(ref tick);
        buf.Bind(ref diffAt);
        buf.Bind(ref traceCount);
        buf.Bind(ref version);
        buf.Bind(ref protocol);
        buf.BindBytes(ref rawTraces);
    }
}
