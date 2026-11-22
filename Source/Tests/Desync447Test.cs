using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;

namespace Tests;

// FIXES row 447: desync collection that does not depend on the client player
[TestFixture]
public class Desync447Test
{
    private static Binder<IPacket> BinderOf(Type type) => (PacketBuffer buf, ref IPacket? packet) =>
    {
        packet ??= (IPacket)Activator.CreateInstance(type)!;
        packet.Bind(buf);
    };

    private static IPacket RoundTrip(IPacket p)
    {
        var binder = BinderOf(p.GetType());
        return binder.Deserialize(binder.Serialize(p));
    }

    private static byte[] Payload(int n)
    {
        var b = new byte[n];
        new Random(447).NextBytes(b);
        return b;
    }

    [TestCase(15)]
    [TestCase(2 * 1024 * 1024)]
    public void ClientDesyncTracesPacket_RoundTrips(int size)
    {
        var p = new ClientDesyncTracesPacket
            { tick = 324841, diffAt = 9733, traceCount = 10176, version = "0.11.5+abc", protocol = 62, rawTraces = Payload(size) };
        var back = (ClientDesyncTracesPacket)RoundTrip(p);
        back.rawTraces.Should().Equal(p.rawTraces);
        back.Should().BeEquivalentTo(p);
    }

    [TestCase(15)]
    [TestCase(2 * 1024 * 1024)]
    public void ServerDesyncTracesPacket_RoundTrips(int size)
    {
        var p = ServerDesyncTracesPacket.From(3, "JamesCron",
            new ClientDesyncTracesPacket { tick = 1, diffAt = 2, traceCount = 3, version = "v", protocol = 62, rawTraces = Payload(size) });
        var back = (ServerDesyncTracesPacket)RoundTrip(p);
        back.rawTraces.Should().Equal(p.rawTraces);
        back.username.Should().Be("JamesCron");
        back.playerId.Should().Be(3);
    }

    [Test]
    public void TwoMegabytePacket_IsFragmentedNotRejected()
    {
        var conn = new RecordingConnection("c");
        conn.SendFragmented(new ClientDesyncTracesPacket
            { tick = 1, diffAt = 2, traceCount = 3, version = "v", protocol = 62, rawTraces = Payload(2 * 1024 * 1024) }.Serialize());
        conn.SentPackets.Count.Should().BeGreaterThan(1000, "2 MB goes out in 1 KB fragments");
    }

    [Test]
    public void ProtocolIs62()
    {
        MpVersion.Protocol.Should().Be(62);
    }

    [Test]
    public void MixedProtocolJoin_IsRefused()
    {
        // the existing check (ServerJoiningState.HandleProtocol) compares ClientProtocolPacket.protocolVersion with
        // MpVersion.Protocol and disconnects with MpDisconnectReason.Protocol; the 61 client therefore cannot join a 62 host
        new ClientProtocolPacket(61).protocolVersion.Should().NotBe(MpVersion.Protocol);
    }

    // ---- first difference -------------------------------------------------------------------------------------

    private static string Traces(int from, int to, Func<int, int> hash, string frame)
    {
        var sb = new StringBuilder("Traces from: test\nTrace count: 999\nTrace of first desynced map random state:\n");
        sb.Append(Entry(from, hash(from), frame)).Append("\n\nContext traces:\n");
        var parts = new List<string>();
        for (int i = from; i < to; i++) parts.Add(Entry(i, hash(i), frame));
        sb.Append(string.Join("\n\n", parts));
        return sb.ToString();
    }

    private static string Entry(int i, int hash, string frame)
    {
        var sb = new StringBuilder($"{i} Tick:{1000 + i} Hash:{hash} 'info{i}'\n");
        for (int f = 0; f < 20; f++) sb.AppendLine($"{frame}{f}");
        return sb.ToString();
    }

    [Test]
    public void FirstDiff_IdenticalTraces_NotFound()
    {
        var a = Traces(10, 60, i => i * 7, "Host.Frame");
        var r = DesyncReport.FindFirstDiff(a, a, 500);
        r.Found.Should().BeFalse();
    }

    [Test]
    public void FirstDiff_PlantedChange_NamesTheIndexAndFrames()
    {
        var host = Traces(10, 60, i => i * 7, "Host.Frame");
        var client = Traces(10, 60, i => i == 33 || i == 40 ? -1 : i * 7, "Client.Frame");
        var r = DesyncReport.FindFirstDiff(host, client, 500);
        r.Found.Should().BeTrue();
        r.Index.Should().Be(33);
        r.LogLine.Should().Be("Multiplayer: desync first difference at trace 33 (tick 500): host Host.Frame0 / client Client.Frame0");
        r.Text.Should().Contain("Tick:1033 Hash:231").And.Contain("Hash:-1");
        r.Host!.Frames.Should().HaveCount(DesyncReport.TopFrames);
        r.Text.Should().Contain("Host.Frame11").And.NotContain("Host.Frame12");
    }

    [Test]
    public void FirstDiff_ChangedTraceMovesTheIndex_RedWhenNotNamed()
    {
        var host = Traces(10, 60, i => i * 7, "F");
        for (int planted = 12; planted < 58; planted += 9)
        {
            var client = Traces(10, 60, i => i == planted ? 12345 : i * 7, "F");
            DesyncReport.FindFirstDiff(host, client, 1).Index.Should().Be(planted);
        }
    }

    // ---- button gating ----------------------------------------------------------------------------------------

    [Test]
    public void ResyncGate_DisabledWithLabelUntilWrittenOr30s()
    {
        var s = ResyncGate.State(false, 3.4f);
        s.Enabled.Should().BeFalse();
        s.Label.Should().Be("Saving desync report (3s)");
        ResyncGate.State(true, 3.4f).Enabled.Should().BeTrue();
        ResyncGate.State(false, 29.9f).Enabled.Should().BeFalse();
        ResyncGate.State(false, 30f).Enabled.Should().BeTrue();
    }

    // ---- host zip ---------------------------------------------------------------------------------------------

    [Test]
    public void HostZip_HasAllEntries()
    {
        var dir = Path.Combine(Path.GetTempPath(), "w447_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var things = Path.Combine(dir, "Things-host-500.zip");
            using (var z = MpZipFile.Open(things, ZipArchiveMode.Create)) Multiplayer.Common.Util.ZipExtensions.AddEntry(z, "host_things_map0.txt", "thing 1");

            var host = Traces(10, 60, i => i * 7, "Host.Frame");
            var client = Traces(10, 60, i => i == 33 ? -1 : i * 7, "Client.Frame");
            var input = new DesyncReport.HostReportInput(500, 33, host, client, "Seatemplar", "JamesCron", "0.11.5+h", 62, "0.11.5+c", 62, things);
            var diff = DesyncReport.FindFirstDiff(host, client, 500);
            var path = DesyncReport.WriteHostZip(dir, input, diff);

            Path.GetFileName(path).Should().Be("Desync-host-500.zip");
            using var zip = ZipFile.OpenRead(path);
            string Read(string n) => new StreamReader(zip.GetEntry(n)!.Open()).ReadToEnd();
            zip.Entries.Select(e => e.FullName).Should().BeEquivalentTo(
                "desync_info", "first_diff.txt", "host_traces.txt", "client_traces.txt", "Things-host-500.zip");
            Read("host_traces.txt").Should().Be(host);
            Read("client_traces.txt").Should().Be(client);
            Read("first_diff.txt").Should().Contain("First difference at trace 33");
            var info = Read("desync_info");
            info.Should().Contain("Seatemplar").And.Contain("JamesCron").And.Contain("0.11.5+h / 62").And.Contain("0.11.5+c / 62").And.Contain("500");

            // a second report for the same tick does not overwrite
            Path.GetFileName(DesyncReport.WriteHostZip(dir, input, diff)).Should().Be("Desync-host-500-2.zip");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---- server forwarding ------------------------------------------------------------------------------------

    private static (ServerPlayer p, RecordingConnection c) Add(MultiplayerServer server, int id, string name, bool host)
    {
        var conn = new RecordingConnection(name);
        var player = new ServerPlayer(id, conn);
        conn.serverPlayer = player;
        conn.ChangeState(ConnectionStateEnum.ServerPlaying);
        player.status = PlayerStatus.Playing;
        if (host) server.hostUsername = name;
        server.playerManager.Players.Add(player);
        return (player, conn);
    }

    [Test]
    public void Server_ForwardsClientTracesToHostOnly()
    {
        var server = MultiplayerServer.instance = new MultiplayerServer(new ServerSettings { gameName = "T", direct = false, lan = false });
        try
        {
            var (_, hostConn) = Add(server, 0, "Seatemplar", true);
            var (_, clientConn) = Add(server, 1, "JamesCron", false);
            var pkt = new ClientDesyncTracesPacket { tick = 5, diffAt = 6, traceCount = 7, version = "v", protocol = 62, rawTraces = [1, 2, 3] };

            new ServerPlayingState(clientConn).HandleDesyncTraces(pkt);
            hostConn.SentPackets.Count(p => p == Packets.Server_DesyncTraces).Should().Be(1);
            clientConn.SentPackets.Count(p => p == Packets.Server_DesyncTraces).Should().Be(0);

            // the host's own packet is not echoed back
            new ServerPlayingState(hostConn).HandleDesyncTraces(pkt);
            hostConn.SentPackets.Count(p => p == Packets.Server_DesyncTraces).Should().Be(1);
        }
        finally
        {
            MultiplayerServer.instance = null;
        }
    }
}
