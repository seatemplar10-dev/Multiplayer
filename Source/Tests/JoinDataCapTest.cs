using FluentAssertions;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;

namespace Tests;

public class JoinDataCapTest
{
    private const int OldCap = 1 << 19; // the cap on 39e1fa96

    private static byte[] RandomBytes(int len, int seed)
    {
        var b = new byte[len];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static ServerJoinDataPacket JoinPacket(byte[] raw) => new()
    {
        gameName = "g", playerId = 1, rwVersion = "1.7", mpVersion = "0.0", defStatus = [],
        configsIncluded = true, rawServerInitData = raw
    };

    private static ServerJoinDataPacket RoundTrip(ServerJoinDataPacket p)
    {
        var writer = new ByteWriter();
        p.Bind(new PacketWriter(writer));
        var read = default(ServerJoinDataPacket);
        read.Bind(new PacketReader(new ByteReader(writer.ToArray())));
        return read;
    }

    // Test-only copy of the old binding (BindRemaining capped at 1<<19)
    private static void BindOld(PacketBuffer buf, ref byte[] raw) => buf.BindRemaining(ref raw, maxLength: OldCap);

    [Test]
    public void OneMiBIncompressibleRoundTrips()
    {
        var raw = RandomBytes(1_048_576, 1);
        var back = RoundTrip(JoinPacket(raw));
        back.rawServerInitData.Should().Equal(raw);
        back.gameName.Should().Be("g");
    }

    [Test]
    public void OneMiBIncompressibleThrowsUnderOldCap()
    {
        var raw = RandomBytes(1_048_576, 1);
        var act = () => BindOld(new PacketWriter(new ByteWriter()), ref raw);
        act.Should().Throw<WriterException>();
    }

    [Test]
    public void OverNewCapStillThrows()
    {
        var raw = RandomBytes((1 << 23) + 1, 2);
        var act = () => JoinPacket(raw).Bind(new PacketWriter(new ByteWriter()));
        act.Should().Throw<WriterException>();
    }

    [Test]
    public void FieldSizedModListRoundTrips()
    {
        var rng = new Random(3);
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789/._-";
        string Str(int n) => new(Enumerable.Range(0, n).Select(_ => chars[rng.Next(chars.Length)]).ToArray());

        var mods = new List<ClientInitDataPacket.ModData>();
        for (var i = 0; i < 1781; i++)
        {
            var files = Enumerable.Range(0, 15)
                .Select(_ => new ClientInitDataPacket.ModFile { path = Str(60), hash = rng.Next() }).ToList();
            mods.Add(new ClientInitDataPacket.ModData
            {
                packageIdNonUnique = "pkg." + i, name = "Mod " + i, publishedFileId = (ulong)i,
                source = ClientInitDataPacket.ModSource.SteamWorkshop, files = files,
                configs = i < 20
                    ? [new ClientInitDataPacket.ModConfig { fileName = "s" + i, contents = Str(30_000) }]
                    : []
            });
        }

        var packet = JoinPacket([]);
        packet.ServerInitData = mods;
        packet.rawServerInitData.Length.Should().BeGreaterThan(OldCap, "the point is a blob the old cap rejects");

        var back = RoundTrip(packet);
        back.rawServerInitData.Should().Equal(packet.rawServerInitData);
        var list = back.ServerInitData;
        list.Should().HaveCount(1781);
        list[1780].files.Should().Equal(mods[1780].files);
        list[5].configs[0].contents.Should().Be(mods[5].configs[0].contents);

        // same blob through the client side packet
        var cp = new ClientInitDataPacket { rwVersion = "1.7", debugOnlySyncCmds = [], hostOnlySyncCmds = [], defInfos = [], Mods = mods };
        var w = new ByteWriter();
        cp.Bind(new PacketWriter(w));
        var cb = default(ClientInitDataPacket);
        cb.Bind(new PacketReader(new ByteReader(w.ToArray())));
        cb.Mods.Should().HaveCount(1781);
    }
}
