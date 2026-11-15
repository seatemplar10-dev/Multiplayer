using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Multiplayer.Common;

public class WorldData
{
    public int hostFactionId;
    public int spectatorFactionId;
    public byte[]? savedGame; // Compressed game save
    public byte[]? sessionData; // Compressed semi persistent data
    public Dictionary<int, byte[]> mapData = new(); // Map id to compressed map data

    public Dictionary<int, List<byte[]>> mapCmds = new(); // Map id to serialized cmds list
    public Dictionary<int, List<byte[]>>? tmpMapCmds;
    public int lastJoinPointAtTick = -1;

    public List<byte[]> syncInfos = new();

    public StandaloneWorldSnapshotState standaloneWorldSnapshot = new();
    public Dictionary<int, StandaloneMapSnapshotState> standaloneMapSnapshots = new();

    private TaskCompletionSource<WorldData>? dataSource;

    // 1.7 lab (FIXES row 37): while the arbiter is Playing only its world upload ends a join point
    // (ServerPlayingState.HandleWorldDataUpload), so an arbiter that never sends it (frozen, failed save, or it ran the join
    // point while still simulating) kept the join point open forever: joins and resyncs hung at "waiting for game data".
    // The host's upload of that join point is kept and used when the arbiter stops Playing, or has not uploaded
    // ArbiterUploadGraceSecs after the host. Then the arbiter is set Desynced: the host is the reference again (uploads and
    // sync opinions) and the arbiter no longer holds the game timer, until it rejoins.
    public const int ArbiterUploadGraceSecs = 60;
    public const int ArbiterUploadGraceNetTicks = ArbiterUploadGraceSecs * MultiplayerServer.NetTicksPerSecond;
    private byte[]? hostUploadForArbiterTimeout;
    private int hostUploadKeptAt;

    public bool HasHostUploadForArbiterTimeout => hostUploadForArbiterTimeout != null;

    public bool CreatingJoinPoint => tmpMapCmds != null;

    public MultiplayerServer Server { get; }

    public WorldData(MultiplayerServer server)
    {
        Server = server;
    }

    private int CurrentJoinPointTick => Server.IsStandaloneServer ? Server.gameTimer : Server.workTicks;

    public bool TryStartJoinPointCreation(bool force = false, ServerPlayer? sourcePlayer = null)
    {
        int currentTick = CurrentJoinPointTick;

        if (!force && lastJoinPointAtTick >= 0 && currentTick - lastJoinPointAtTick < 30)
        {
            ServerLog.Detail($"Join point skipped: cooldown active at tick={currentTick}, last={lastJoinPointAtTick}, standalone={Server.IsStandaloneServer}");
            return false;
        }

        if (CreatingJoinPoint)
        {
            ServerLog.Detail("Join point skipped: already creating one");
            return false;
        }

        var issuingPlayer = sourcePlayer;
        if (Server.IsStandaloneServer && issuingPlayer == null)
        {
            issuingPlayer = Server.PlayingPlayers.FirstOrDefault(player => player.IsHost)
                ?? Server.PlayingPlayers.FirstOrDefault();

            if (issuingPlayer == null)
            {
                ServerLog.Detail("Join point skipped: no playing player available for standalone creation");
                return false;
            }
        }

        ServerLog.Detail($"Join point started at tick={currentTick}, force={force}, standalone={Server.IsStandaloneServer}");
        Server.SendChat("Creating a join point...");

        Server.commands.Send(CommandType.CreateJoinPoint, ScheduledCommand.NoFaction, ScheduledCommand.Global, Array.Empty<byte>(),
            sourcePlayer: Server.IsStandaloneServer ? issuingPlayer : null);
        tmpMapCmds = new Dictionary<int, List<byte[]>>();
        dataSource = new TaskCompletionSource<WorldData>();

        return true;
    }

    public void EndJoinPointCreation()
    {
        int currentTick = CurrentJoinPointTick;
        ServerLog.Detail($"Join point completed at tick={currentTick}, standalone={Server.IsStandaloneServer}");
        mapCmds = tmpMapCmds!;
        tmpMapCmds = null;
        hostUploadForArbiterTimeout = null;
        lastJoinPointAtTick = currentTick;

        if (Server.IsStandaloneServer && Server.persistence != null)
        {
            try
            {
                Server.persistence.WriteJoinPoint(this, currentTick);
            }
            catch (Exception e)
            {
                ServerLog.Error($"Failed to persist standalone join point at tick={currentTick}: {e}");
            }
        }

        dataSource!.SetResult(this);
        dataSource = null;
    }

    public void AbortJoinPointCreation()
    {
        if (!CreatingJoinPoint)
            return;

        tmpMapCmds = null;
        hostUploadForArbiterTimeout = null;
        dataSource?.SetResult(this);
        dataSource = null;
    }

    // A Client_WorldDataUpload body (SaveLoad.SendGameData): the maps, the game and the session data. Ends the join point.
    public void ApplyWorldUpload(ByteReader data)
    {
        mapData = new Dictionary<int, byte[]>();

        int maps = data.ReadInt32();
        for (int i = 0; i < maps; i++)
        {
            int mapId = data.ReadInt32();
            mapData[mapId] = data.ReadPrefixedBytes();
        }

        savedGame = data.ReadPrefixedBytes();
        sessionData = data.ReadPrefixedBytes();

        if (CreatingJoinPoint)
            EndJoinPointCreation();
    }

    // FIXES row 37: the host's upload of the current join point, dropped because the arbiter is the uploader
    public void KeepHostUploadForArbiterTimeout(byte[] upload, int netTimer)
    {
        if (!CreatingJoinPoint)
            return;

        hostUploadForArbiterTimeout = upload;
        hostUploadKeptAt = netTimer;
        ServerLog.Detail($"Join point: kept the host's world upload ({upload.Length} bytes) while the arbiter's is due");
    }

    // FIXES row 37: runs every server loop (MultiplayerServer.Run)
    public void TickArbiterUploadFallback(int netTimer)
    {
        if (hostUploadForArbiterTimeout == null)
            return;

        if (!CreatingJoinPoint)
        {
            hostUploadForArbiterTimeout = null;
            return;
        }

        var arbiter = Server.PlayingPlayers.FirstOrDefault(p => p.IsArbiter && p.status == PlayerStatus.Playing);
        if (arbiter != null && netTimer - hostUploadKeptAt <= ArbiterUploadGraceNetTicks)
            return;

        var upload = hostUploadForArbiterTimeout;
        if (arbiter != null)
        {
            ServerLog.Log($"Join point: no world upload from the arbiter {ArbiterUploadGraceSecs} s after the host's; " +
                          "using the host's upload and setting the arbiter Desynced until it rejoins");
            Server.SendChat("The arbiter did not send the join point; the host's was used. The arbiter is out until it rejoins.");
            arbiter.UpdateStatus(PlayerStatus.Desynced);
        }
        else
        {
            ServerLog.Log("Join point: the arbiter stopped playing before it sent its world upload; using the host's upload");
        }

        ApplyWorldUpload(new ByteReader(upload));
    }

    public Task<WorldData> WaitJoinPoint()
    {
        return dataSource?.Task ?? Task.FromResult(this);
    }

    public bool TryAcceptStandaloneWorldSnapshot(ServerPlayer player, int tick, byte[] worldSnapshot,
        byte[] sessionSnapshot, byte[] expectedHash)
    {
        if (tick < standaloneWorldSnapshot.tick)
            return false;

        var actualHash = ComputeHash(worldSnapshot, sessionSnapshot);
        if (expectedHash.Length > 0 && !actualHash.AsSpan().SequenceEqual(expectedHash))
            return false;

        savedGame = worldSnapshot;
        sessionData = sessionSnapshot;
        standaloneWorldSnapshot = new StandaloneWorldSnapshotState
        {
            tick = tick,
            producerPlayerId = player.id,
            producerUsername = player.Username,
            sha256Hash = actualHash
        };

        // Persist to disk
        Server.persistence?.WriteWorldSnapshot(worldSnapshot, sessionSnapshot, tick);

        return true;
    }

    public bool TryAcceptStandaloneMapSnapshot(ServerPlayer player, int mapId, int tick,
        byte[] mapSnapshot, byte[] expectedHash)
    {
        if (mapId < 0)
            return false;

        var snapshotState = standaloneMapSnapshots.GetOrAddNew(mapId);
        if (tick < snapshotState.tick)
            return false;

        var actualHash = ComputeHash(mapSnapshot);
        if (expectedHash.Length > 0 && !actualHash.AsSpan().SequenceEqual(expectedHash))
            return false;

        mapData[mapId] = mapSnapshot;
        snapshotState.tick = tick;
        snapshotState.producerPlayerId = player.id;
        snapshotState.producerUsername = player.Username;
        snapshotState.sha256Hash = actualHash;
        standaloneMapSnapshots[mapId] = snapshotState;

        // Persist to disk
        Server.persistence?.WriteMapSnapshot(mapId, mapSnapshot);

        return true;
    }

    private static byte[] ComputeHash(params byte[][] payloads)
    {
        using var hasher = SHA256.Create();
        foreach (var payload in payloads)
        {
            hasher.TransformBlock(payload, 0, payload.Length, null, 0);
        }

        hasher.TransformFinalBlock([], 0, 0);
        return hasher.Hash;
    }
}

public struct StandaloneWorldSnapshotState
{
    public StandaloneWorldSnapshotState() { }
    public int tick;
    public int producerPlayerId;
    public string producerUsername = "";
    public byte[] sha256Hash = Array.Empty<byte>();
}

public struct StandaloneMapSnapshotState
{
    public StandaloneMapSnapshotState() { }
    public int tick;
    public int producerPlayerId;
    public string producerUsername = "";
    public byte[] sha256Hash = Array.Empty<byte>();
}
