using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;

namespace Tests;

// Lane ARBTRACE (run mp17): who is asked for the other side's desync traces, and who may answer.
// Server side only (PlayerManager.OnDesync, ServerPlayingState.HandleTraces); no game needed.
[TestFixture]
public class DesyncTraceRoutingTest
{
    private MultiplayerServer server = null!;
    private int nextPlayerId;

    [SetUp]
    public void SetUp()
    {
        server = MultiplayerServer.instance = new MultiplayerServer(new ServerSettings
        {
            gameName = "Test",
            direct = false,
            lan = false
        });
        nextPlayerId = 0;
    }

    [TearDown]
    public void TearDown()
    {
        MultiplayerServer.instance = null;
    }

    private (ServerPlayer player, RecordingConnection conn) AddPlayer(string username, bool isHost = false,
        bool isArbiter = false)
    {
        var conn = new RecordingConnection(username);
        var player = new ServerPlayer(nextPlayerId++, conn);
        conn.serverPlayer = player;
        conn.ChangeState(ConnectionStateEnum.ServerPlaying);
        player.status = PlayerStatus.Playing;
        if (isArbiter) player.type = PlayerType.Arbiter;
        if (isHost) server.hostUsername = username;
        server.playerManager.Players.Add(player);
        return (player, conn);
    }

    private static int TracesSent(RecordingConnection c) => c.SentPackets.Count(p => p == Packets.Server_Traces);

    [Test]
    public void HostDesyncsAgainstPlayingArbiter_ArbiterIsAsked()
    {
        // run mp17: host + arbiter, the host flags the desync
        var (host, hostConn) = AddPlayer("SeaHost", isHost: true);
        var (_, arbConn) = AddPlayer("The Arbiter", isArbiter: true);

        server.playerManager.OnDesync(host, 23851, 0);

        Assert.That(TracesSent(arbConn), Is.EqualTo(1), "arbiter must get the trace request");
        Assert.That(TracesSent(hostConn), Is.EqualTo(0), "the desynced host must not be asked for its own traces");
    }

    [Test]
    public void ClientDesyncsWithPlayingArbiter_ArbiterIsAsked()
    {
        // with the arbiter Playing, clients are compared with the arbiter's opinions too (HandleDesyncCheck)
        var (_, hostConn) = AddPlayer("SeaHost", isHost: true);
        var (_, arbConn) = AddPlayer("The Arbiter", isArbiter: true);
        var (client, _) = AddPlayer("SeaClient");

        server.playerManager.OnDesync(client, 100, 5);

        Assert.That(TracesSent(arbConn), Is.EqualTo(1));
        Assert.That(TracesSent(hostConn), Is.EqualTo(0));
    }

    [Test]
    public void ClientDesyncsWithoutArbiter_HostIsAsked()
    {
        // runs mp14-mp16 (host + observer client): unchanged, the host answers
        var (_, hostConn) = AddPlayer("SeaHost", isHost: true);
        var (client, _) = AddPlayer("SeaClient");

        server.playerManager.OnDesync(client, 100, 5);

        Assert.That(TracesSent(hostConn), Is.EqualTo(1));
    }

    [Test]
    public void ArbiterNotPlayingYet_HostIsAsked()
    {
        var (_, hostConn) = AddPlayer("SeaHost", isHost: true);
        var (arb, arbConn) = AddPlayer("The Arbiter", isArbiter: true);
        arb.status = PlayerStatus.Simulating;
        var (client, _) = AddPlayer("SeaClient");

        server.playerManager.OnDesync(client, 100, 5);

        Assert.That(TracesSent(hostConn), Is.EqualTo(1));
        Assert.That(TracesSent(arbConn), Is.EqualTo(0));
    }

    [Test]
    public void NoHostNoArbiter_NoThrowNoRequest()
    {
        // standalone-like: nobody else to ask; HostPlayer.First would throw here
        var (client, clientConn) = AddPlayer("SeaClient");

        Assert.DoesNotThrow(() => server.playerManager.OnDesync(client, 100, 5));
        Assert.That(TracesSent(clientConn), Is.EqualTo(0));
    }

    [Test]
    public void ArbiterAnswer_IsForwardedToTheRequester()
    {
        var (host, hostConn) = AddPlayer("SeaHost", isHost: true);
        var (arb, arbConn) = AddPlayer("The Arbiter", isArbiter: true);

        new ServerPlayingState(arbConn).HandleTraces(new ClientTracesPacket
            { playerId = host.id, rawTraces = [1, 2, 3], rawJittedMethods = [4] });

        Assert.That(TracesSent(hostConn), Is.EqualTo(1), "the arbiter's answer must reach the desynced host");
    }

    [Test]
    public void NormalClientAnswer_IsDropped()
    {
        var (host, hostConn) = AddPlayer("SeaHost", isHost: true);
        var (_, clientConn) = AddPlayer("SeaClient");

        new ServerPlayingState(clientConn).HandleTraces(new ClientTracesPacket
            { playerId = host.id, rawTraces = [1], rawJittedMethods = [2] });

        Assert.That(TracesSent(hostConn), Is.EqualTo(0));
    }
}
