using Multiplayer.Common;

namespace Tests;

// FIXES row 37 (lab mp49): with the arbiter Playing only its world upload ended a join point, so an arbiter that never sent
// it left joins and resyncs at "waiting for game data" for good. Server side only (ServerPlayingState.HandleWorldDataUpload,
// WorldData.TickArbiterUploadFallback); no game needed.
[TestFixture]
public class ArbiterJoinPointFallbackTest
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

    private static byte[] Upload(byte tag)
    {
        var writer = new ByteWriter();
        writer.WriteInt32(1);
        writer.WriteInt32(0);
        writer.WritePrefixedBytes([tag, 1]);
        writer.WritePrefixedBytes([tag, 2]);
        writer.WritePrefixedBytes([tag, 3]);
        return writer.ToArray();
    }

    private static void SendUpload(RecordingConnection conn, byte tag) =>
        new ServerPlayingState(conn).HandleWorldDataUpload(new ByteReader(Upload(tag)));

    [Test]
    public void NoArbiter_HostUploadEndsJoinPointAtOnce()
    {
        var (_, hostConn) = AddPlayer("SeaHost", isHost: true);
        Assert.That(server.worldData.TryStartJoinPointCreation(true), Is.True);

        SendUpload(hostConn, 7);

        Assert.That(server.worldData.CreatingJoinPoint, Is.False);
        Assert.That(server.worldData.savedGame, Is.EqualTo(new byte[] { 7, 2 }));
        Assert.That(server.worldData.HasHostUploadForArbiterTimeout, Is.False);
    }

    [Test]
    public void ArbiterPlaying_HostUploadIsKeptUntilTheGraceEnds()
    {
        var (_, hostConn) = AddPlayer("SeaHost", isHost: true);
        var (arbiter, _) = AddPlayer("The Arbiter", isArbiter: true);
        Assert.That(server.worldData.TryStartJoinPointCreation(true), Is.True);

        SendUpload(hostConn, 7);
        server.worldData.TickArbiterUploadFallback(WorldData.ArbiterUploadGraceNetTicks);

        Assert.That(server.worldData.CreatingJoinPoint, Is.True, "the arbiter still has time");
        Assert.That(server.worldData.savedGame, Is.Null);
        Assert.That(server.worldData.HasHostUploadForArbiterTimeout, Is.True);
        Assert.That(arbiter.status, Is.EqualTo(PlayerStatus.Playing));
    }

    [Test]
    public void ArbiterSilentPastTheGrace_HostUploadEndsJoinPointAndArbiterIsDesynced()
    {
        var (_, hostConn) = AddPlayer("SeaHost", isHost: true);
        var (arbiter, _) = AddPlayer("The Arbiter", isArbiter: true);
        Assert.That(server.worldData.TryStartJoinPointCreation(true), Is.True);
        var waiter = server.worldData.WaitJoinPoint();

        SendUpload(hostConn, 7);
        server.worldData.TickArbiterUploadFallback(WorldData.ArbiterUploadGraceNetTicks + 1);

        Assert.That(server.worldData.CreatingJoinPoint, Is.False);
        Assert.That(waiter.IsCompleted, Is.True, "a joiner waiting in ServerLoadingState gets the world");
        Assert.That(server.worldData.savedGame, Is.EqualTo(new byte[] { 7, 2 }));
        Assert.That(server.worldData.mapData[0], Is.EqualTo(new byte[] { 7, 1 }));
        Assert.That(server.worldData.sessionData, Is.EqualTo(new byte[] { 7, 3 }));
        Assert.That(arbiter.status, Is.EqualTo(PlayerStatus.Desynced));
        Assert.That(server.ArbiterPlaying, Is.False, "the host is the uploader and sync reference again");
        Assert.That(server.worldData.HasHostUploadForArbiterTimeout, Is.False);
    }

    [Test]
    public void ArbiterUploadsInTime_ArbiterUploadWins()
    {
        var (_, hostConn) = AddPlayer("SeaHost", isHost: true);
        var (arbiter, arbConn) = AddPlayer("The Arbiter", isArbiter: true);
        Assert.That(server.worldData.TryStartJoinPointCreation(true), Is.True);

        SendUpload(hostConn, 7);
        SendUpload(arbConn, 9);
        server.worldData.TickArbiterUploadFallback(WorldData.ArbiterUploadGraceNetTicks + 1);

        Assert.That(server.worldData.CreatingJoinPoint, Is.False);
        Assert.That(server.worldData.savedGame, Is.EqualTo(new byte[] { 9, 2 }));
        Assert.That(server.worldData.HasHostUploadForArbiterTimeout, Is.False);
        Assert.That(arbiter.status, Is.EqualTo(PlayerStatus.Playing));
    }

    [Test]
    public void ArbiterStopsPlaying_HostUploadEndsJoinPointAtOnce()
    {
        var (_, hostConn) = AddPlayer("SeaHost", isHost: true);
        var (arbiter, _) = AddPlayer("The Arbiter", isArbiter: true);
        Assert.That(server.worldData.TryStartJoinPointCreation(true), Is.True);

        SendUpload(hostConn, 7);
        server.playerManager.Players.Remove(arbiter); // disconnected before its upload
        server.worldData.TickArbiterUploadFallback(1);

        Assert.That(server.worldData.CreatingJoinPoint, Is.False);
        Assert.That(server.worldData.savedGame, Is.EqualTo(new byte[] { 7, 2 }));
    }

    [Test]
    public void HostUploadOutsideAJoinPoint_IsNotKept()
    {
        var (_, hostConn) = AddPlayer("SeaHost", isHost: true);
        AddPlayer("The Arbiter", isArbiter: true);

        SendUpload(hostConn, 7);
        server.worldData.TickArbiterUploadFallback(WorldData.ArbiterUploadGraceNetTicks + 1);

        Assert.That(server.worldData.HasHostUploadForArbiterTimeout, Is.False);
        Assert.That(server.worldData.savedGame, Is.Null);
    }
}
