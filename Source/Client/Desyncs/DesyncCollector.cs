using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ionic.Zlib;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;
using Verse;

namespace Multiplayer.Client.Desyncs;

// FIXES row 447: desync collection that does not depend on the client player's window. At detection the client writes its
// local traces at once and sends them to the host; the host writes Desync-host-<tick>.zip from what it sent and what it got.
public static class DesyncCollector
{
    // (opinion tick, player id) -> what this PC sent as its traces for that player's desync request
    private static readonly Dictionary<(int tick, int playerId), string> sentToPlayer = new();

    public static void RememberSent(int tick, int playerId, string hostResponse) => sentToPlayer[(tick, playerId)] = hostResponse;

    // Client side, called from SyncCoordinator.HandleDesync on the main thread, synchronously
    public static string OnDesyncDetected(ClientSyncOpinion local, int diffAt, int traceCount)
    {
        string text;
        try
        {
            text = $"Traces from: client {Multiplayer.username} (opinion start tick {local.startTick}, trace {diffAt})\n" +
                   local.GetFormattedStackTracesForRange(diffAt);
        }
        catch (Exception e)
        {
            text = $"Traces not available: {e}";
        }

        string path = null;
        try
        {
            Directory.CreateDirectory(Multiplayer.DesyncsDir);
            path = Path.Combine(Multiplayer.DesyncsDir, $"Desync-{local.startTick}-local_traces.txt");
            File.WriteAllText(path, text);
            Log.Message($"Multiplayer: desync local traces written: {path} ({text.Length} chars)");
        }
        catch (Exception e)
        {
            Log.Error($"Multiplayer: could not write the desync local traces: {e}");
        }

        try
        {
            if (Multiplayer.LocalServer == null && Multiplayer.Client != null)
                Multiplayer.Client.SendFragmented(new ClientDesyncTracesPacket
                {
                    tick = local.startTick,
                    diffAt = diffAt,
                    traceCount = traceCount,
                    version = MpVersion.Version,
                    protocol = MpVersion.Protocol,
                    rawTraces = GZipStream.CompressString(text)
                }.Serialize());
        }
        catch (Exception e)
        {
            Log.Error($"Multiplayer: could not send the desync traces to the host: {e}");
        }

        return path;
    }

    // Host side: the forwarded client traces arrived
    public static string OnClientTraces(ServerDesyncTracesPacket packet)
    {
        try
        {
            var clientText = GZipStream.UncompressString(packet.rawTraces);
            string thingsPath = Path.Combine(Multiplayer.DesyncsDir, $"Things-host-{packet.tick}.zip");
            if (!sentToPlayer.TryGetValue((packet.tick, packet.playerId), out var hostText))
            {
                // the request did not reach this PC (the arbiter answered): build this PC's own window now
                var info = Multiplayer.game?.sync.knownClientOpinions.FirstOrDefault(o => o.startTick == packet.tick);
                hostText = $"Traces from: host {Multiplayer.username} (built when the client's traces arrived, opinion start tick {packet.tick}, trace {packet.diffAt})\n" +
                           (info?.GetFormattedStackTracesForRange(packet.diffAt) ?? "Traces not available: opinion no longer kept");
                DesyncEvidence.WriteAllMapsFile("host", packet.tick, out _, out _);
            }

            var input = new DesyncReport.HostReportInput(packet.tick, packet.diffAt, hostText, clientText,
                Multiplayer.username, packet.username, MpVersion.Version, MpVersion.Protocol, packet.version,
                packet.protocol, thingsPath);
            return WriteHostReport(input);
        }
        catch (Exception e)
        {
            Log.Error($"Multiplayer: could not write the host desync report: {e}");
            return null;
        }
    }

    public static string WriteHostReport(DesyncReport.HostReportInput input)
    {
        var diff = DesyncReport.FindFirstDiff(input.HostTraces, input.ClientTraces, input.OpinionTick);
        Log.Message(diff.LogLine);
        var path = DesyncReport.WriteHostZip(Multiplayer.DesyncsDir, input, diff);
        Log.Message($"Multiplayer: host desync report written: {path}");
        return path;
    }
}
