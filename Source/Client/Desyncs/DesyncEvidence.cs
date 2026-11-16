using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Multiplayer.Common;
using Multiplayer.Common.Util;
using Verse;

namespace Multiplayer.Client.Desyncs
{
    // FIXES row 315 (P1, P2): more evidence in a desync report. On 29 Oct the host sent 0 trace hashes and a thing count split
    // (35132 vs 35129 things on map 1) named no thing, so neither desync could be explained.
    // Runs only at a desync (SaveableDesyncInfo is built in SyncCoordinator.HandleDesync) or at a trace request
    // (ClientPlayingState.HandleTraces), on the main thread. Reads fields only (thingIDNumber, def.defName, positionInt,
    // Spawned, the lists, settings): no Rand, no thing ids, no game state written. Nothing runs while the checks pass.
    public static class DesyncEvidence
    {
        // P1: maps where a list checkpoint of the compared pair holds different things (count or content), motes left out
        // as in ListCheckpoint. The same checkpoint on both sides is the same map tick and list.
        public static List<int> MapsWithDifferentThings(ClientSyncOpinion local, ClientSyncOpinion remote)
        {
            var maps = new List<int>();
            foreach (var l in local.mapStates)
            {
                var r = remote.mapStates.FirstOrDefault(m => m.mapId == l.mapId);
                if (l.listHashes == null || r?.listHashes == null) continue;

                int n = Math.Min(l.listHashes.Count, r.listHashes.Count);
                for (int i = 0; i < n; i++)
                {
                    var p = l.listHashes[i];
                    var q = r.listHashes[i];
                    if (p.mapTick == q.mapTick && p.list == q.list && (p.count != q.count || p.contentHash != q.contentHash))
                    {
                        maps.Add(l.mapId);
                        break;
                    }
                }
            }

            return maps;
        }

        // P1, the side that found the desync: this PC's thing lists of the maps with different things, for the desync zip
        public static List<(int mapId, string text)> ThingListsForSplit(ClientSyncOpinion local, ClientSyncOpinion remote)
        {
            var result = new List<(int, string)>();
            try
            {
                foreach (var mapId in MapsWithDifferentThings(local, remote))
                {
                    var map = Find.Maps?.FirstOrDefault(m => m.uniqueID == mapId);
                    var text = map != null ? ThingList(map, "local") : $"Map {mapId} is not loaded here: no thing list";
                    result.Add((mapId, text));
                    Log.Message($"Multiplayer: desync with different things on map {mapId}: this PC's thing lists go into " +
                                $"the desync zip as local_things_map{mapId}.txt ({text.Length} chars)");
                }
            }
            catch (Exception e)
            {
                Log.Error($"Multiplayer: writing the thing lists for the desync report failed: {e}");
            }

            return result;
        }

        // P1, the side that answers a trace request (host or arbiter): it does not know which check failed on the other PC,
        // so it writes every map, to its own MpDesyncs folder. Returns the path, or why there is none.
        public static string WriteAllMapsFile(string side, int opinionTick)
        {
            try
            {
                var path = Path.Combine(Multiplayer.DesyncsDir, $"Things-{side}-{opinionTick}.zip");
                if (File.Exists(path)) return $"{path} (written by an earlier request for this tick)";

                using var zip = MpZipFile.Open(path, ZipArchiveMode.Create);
                foreach (var map in Find.Maps ?? [])
                    zip.AddEntry($"{side}_things_map{map.uniqueID}.txt", ThingList(map, side));
                return path;
            }
            catch (Exception e)
            {
                Log.Error($"Multiplayer: writing the thing lists for the desync at opinion tick {opinionTick} failed: {e}");
                return $"not written ({e.GetType().Name}: {e.Message})";
            }
        }

        // listerThings.AllThings and the map's three tick lists, in list order, motes left out (the counts match the list
        // checkpoint counts in the desync message). Columns: thingIDNumber def position spawned
        public static string ThingList(Map map, string side)
        {
            var comp = map.AsyncTime();
            var text = new StringBuilder(1 << 20);
            text.AppendLine($"Thing lists of map {map.uniqueID} on {side} {Multiplayer.username}, map tick " +
                            $"{comp?.mapTicks.ToString() ?? "?"}, MP timer {TickPatch.Timer}, motes left out as in the list checkpoints");
            text.AppendLine("Columns: thingIDNumber def position spawned (tick lists: bucket b<n>, reg = to register, dereg = to deregister)");

            AppendList(text, "listerThings.AllThings", null, map.listerThings.AllThings);
            if (comp != null)
            {
                AppendTickList(text, "tickListNormal", comp.tickListNormal);
                AppendTickList(text, "tickListRare", comp.tickListRare);
                AppendTickList(text, "tickListLong", comp.tickListLong);
            }

            return text.ToString();
        }

        private static void AppendTickList(StringBuilder text, string name, TickList tickList)
        {
            for (int b = 0; b < tickList.thingLists.Count; b++)
                AppendList(text, name, $"b{b}", tickList.thingLists[b]);
            AppendList(text, name, "reg", tickList.thingsToRegister);
            AppendList(text, name, "dereg", tickList.thingsToDeregister);
        }

        private static void AppendList(StringBuilder text, string name, string segment, List<Thing> things)
        {
            int count = 0;
            foreach (var t in things)
                if (t == null || !ListCheckpoint.IsPerPc(t))
                    count++;

            text.AppendLine($"## {name}{(segment != null ? " " + segment : "")}: {count}");
            foreach (var t in things)
            {
                if (t == null)
                {
                    text.AppendLine("null");
                    continue;
                }

                if (ListCheckpoint.IsPerPc(t)) continue;
                var pos = t.positionInt;
                text.Append(t.thingIDNumber).Append(' ').Append(t.def?.defName ?? "null").Append(' ')
                    .Append(pos.x).Append(',').Append(pos.y).Append(',').Append(pos.z).Append(' ')
                    .Append(t.Spawned ? "spawned" : "unspawned").AppendLine();
            }
        }

        // P2: this PC's tracing state, for the warning and the reports. traceHashes: the opinion's trace hash count,
        // null when the opinion is not kept here
        public static string TracingState(int? traceHashes) =>
            $"tracing mode {Multiplayer.settings?.desyncTracingMode.ToString() ?? "?"}" +
            $"{(DeferredStackTracing.HashManagedFrames ? " +frame hash" : "")}, " +
            $"stack tracing supported {DeferredStackTracing.SupportsDeferredStackTracing}, " +
            $"session desync traces {Multiplayer.GameComp?.logDesyncTraces.ToString() ?? "?"}, " +
            $"trace hashes in the opinion {traceHashes?.ToString() ?? "unknown (opinion not kept)"}";

        // P2, the side that found the desync: a warning when either side has no trace hashes, logged and returned for the zip
        public static string WarnIfNoTraces(ClientSyncOpinion local, ClientSyncOpinion remote)
        {
            int l = local.desyncStackTraceHashes.Count, r = remote.desyncStackTraceHashes.Count;
            if (l > 0 && r > 0) return null;

            var who = l == 0 && r == 0 ? "neither side has" : r == 0 ? "the remote side (host or arbiter) sent no" : "this PC has no";
            var warning = $"Multiplayer: this desync has no trace comparison: {who} trace hashes for opinion start tick " +
                          $"{local.startTick} (local {l}, remote {r}). This PC: {TracingState(l)}. The remote side's tracing " +
                          "mode is in host_traces.txt of the desync zip. Tracing mode None, a CPU without stack tracing or a " +
                          "session hosted with desync traces off gives 0 trace hashes.";
            Log.Warning(warning);
            return warning;
        }
    }
}
