using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;
using Verse;

namespace Multiplayer.Client
{

    public class ClientSyncOpinion(int startTick)
    {
        public bool isLocalClientsOpinion;

        public int startTick = startTick;
        public List<uint> commandRandomStates = new();
        public List<uint> worldRandomStates = new();
        public List<MapRandomStateData> mapStates = new();

        // todo Unused for now
        public List<int> pawnCapacityHashes = new();
        public List<int> pawnStatHashes = new();
        public List<int> pawnNeedHashes = new();

        // Serialized only after a desync to reduce bandwidth usage in regular gameplay (only the hashes are used) and
        // help with debugging in case something goes wrong.
        public List<StackTraceLogItem> desyncStackTraces = new();
        public List<int> desyncStackTraceHashes = new();
        public bool simulating;
        public RoundModeEnum roundMode;

        // No more traces are recorded past SyncOpinion.MaxTraceHashes, on every side alike (see SyncCoordinator)
        public bool TraceCapReached => desyncStackTraceHashes.Count >= SyncOpinion.MaxTraceHashes;

        // Set when a trace was dropped because of the cap; SyncCoordinator.FinishLocalOpinion logs it
        public bool tracesCapped;

        // FIXES row 99 (live full-state checksum): the lab harness's checkpoints of the last FullStateCheckpoints.CarryTicks MP
        // ticks, attached by SyncCoordinator.FinishLocalOpinion. Null when there are none.
        public List<FullStateHash> fullStateHashes;

        public string CheckForDesync(ClientSyncOpinion other)
        {
            var message = CheckForDesyncWithoutFullState(other);

            // FIXES row 99: a full-state checksum difference on an MP timer both sides carry is named by itself; with another
            // difference it is added to that
            var fullState = FullStateCheckpoints.FirstDifference(fullStateHashes, other.fullStateHashes, SideName, other.SideName);
            if (fullState == null) return message;
            return message == null
                ? $"Full-state checksum mismatch {fullState}"
                : $"{message}; full-state checksum mismatch too, {fullState}";
        }

        private string CheckForDesyncWithoutFullState(ClientSyncOpinion other)
        {
            if (roundMode != other.roundMode)
                return $"FP round mode doesn't match: {roundMode} != {other.roundMode}";

            if (!mapStates.Select(m => m.mapId).SequenceEqual(other.mapStates.Select(m => m.mapId)))
                return "Map instances don't match";

            foreach (var g in
                     from map1 in mapStates
                     join map2 in other.mapStates on map1.mapId equals map2.mapId
                     select (map1, map2))
            {
                // FIXES row 113 item 3: a shared list split (same things in another order, or different things) while
                // the random states still match is named by itself; with a random state difference it is added to that
                var listSplit = ListOrderHash.FirstDifference(g.map1.listHashes, g.map2.listHashes, SideName,
                    other.SideName);

                if (!g.map1.randomStates.SequenceEqual(g.map2.randomStates))
                    return $"Wrong random state on map {g.map1.mapId}" +
                           (listSplit != null ? $"; shared list split too, {listSplit}" : "");

                if (listSplit != null)
                    return $"Shared list split on map {g.map1.mapId} {listSplit}";
            }

            if (!worldRandomStates.SequenceEqual(other.worldRandomStates))
                return "Wrong random state for the world";

            if (!commandRandomStates.SequenceEqual(other.commandRandomStates))
                return "Random state from commands doesn't match";

            if (!simulating && !other.simulating && desyncStackTraceHashes.Any() && other.desyncStackTraceHashes.Any() && !SyncOpinion.TraceHashesMatch(desyncStackTraceHashes, other.desyncStackTraceHashes))
                return "Trace hashes don't match";

            return null;
        }

        private string SideName => isLocalClientsOpinion ? "local" : "remote";

        public List<uint> GetRandomStatesForMap(int mapId) => GetMapData(mapId).randomStates;

        public MapRandomStateData GetMapData(int mapId)
        {
            var result = mapStates.Find(m => m.mapId == mapId);
            if (result != null) return result;
            mapStates.Add(result = new MapRandomStateData(mapId));
            return result;
        }

        public byte[] Serialize()
        {
            var writer = new ByteWriter();

            writer.WriteInt32(startTick);
            writer.WritePrefixedUInts(commandRandomStates);
            writer.WritePrefixedUInts(worldRandomStates);

            writer.WriteInt32(mapStates.Count);
            foreach (var map in mapStates)
            {
                writer.WriteInt32(map.mapId);
                writer.WritePrefixedUInts(map.randomStates);
                ListHash.WriteList(writer, map.listHashes); // same bytes as MapRandomState.Bind (protocol 58)
            }

            writer.WritePrefixedInts(desyncStackTraceHashes);
            writer.WriteBool(simulating);
            writer.WriteShort((short)roundMode);
            FullStateHash.WriteList(writer, fullStateHashes); // same bytes as SyncOpinion.Bind (protocol 59)

            return writer.ToArray();
        }

        public static ClientSyncOpinion FromNet(SyncOpinion sync) => new(sync.startTick)
        {
            commandRandomStates = sync.commandRandomStates,
            worldRandomStates = sync.worldRandomStates,
            mapStates = sync.mapRandomStates.Select(state => new MapRandomStateData(state.mapId)
                { randomStates = state.randomStates, listHashes = state.listHashes }).ToList(),
            desyncStackTraceHashes = sync.traceHashes,
            simulating = sync.simulating,
            roundMode = sync.roundMode,
            fullStateHashes = sync.fullStateHashes
        };

        public SyncOpinion ToNet() => new()
        {
            startTick = startTick,
            commandRandomStates = commandRandomStates,
            worldRandomStates = worldRandomStates,
            mapRandomStates = mapStates.Select(state => new MapRandomState
                { mapId = state.mapId, randomStates = state.randomStates, listHashes = state.listHashes }).ToList(),
            traceHashes = desyncStackTraceHashes,
            simulating = simulating,
            roundMode = roundMode,
            fullStateHashes = fullStateHashes
        };

        public static ClientSyncOpinion Deserialize(ByteReader data)
        {
            var startTick = data.ReadInt32();

            var cmds = new List<uint>(data.ReadPrefixedUInts());
            var world = new List<uint>(data.ReadPrefixedUInts());

            var maps = new List<MapRandomStateData>();
            int mapCount = data.ReadInt32();
            for (int i = 0; i < mapCount; i++)
            {
                int mapId = data.ReadInt32();
                var mapData = new List<uint>(data.ReadPrefixedUInts());
                var listHashes = ListHash.ReadList(data);
                maps.Add(new MapRandomStateData(mapId) { randomStates = mapData, listHashes = listHashes });
            }

            var traceHashes = new List<int>(data.ReadPrefixedInts());
            var simulating = data.ReadBool();
            var roundMode = data.ReadShort();
            var fullStateHashes = FullStateHash.ReadList(data);

            return new ClientSyncOpinion(startTick)
            {
                commandRandomStates = cmds,
                worldRandomStates = world,
                mapStates = maps,
                desyncStackTraceHashes = traceHashes,
                simulating = simulating,
                roundMode = (RoundModeEnum)roundMode,
                fullStateHashes = fullStateHashes
            };
        }

        public void TryMarkSimulating()
        {
            if (TickPatch.Simulating)
                simulating = true;
        }

        public string GetFormattedStackTracesForRange(int diffAt)
        {
            var start = Math.Max(0, diffAt - Multiplayer.settings.desyncTracesRadius);
            var end = diffAt + Multiplayer.settings.desyncTracesRadius;
            var traceId = start;

            return
                $"Trace count: {desyncStackTraces.Count}\nTrace of first desynced map random state:\n{diffAt} {desyncStackTraces.ElementAtOrDefault(diffAt)}" +
                "\n\nContext traces:\n" +
                desyncStackTraces
                .Skip(start)
                .Take(end - start)
                .Join(a => traceId++ + " " + a, "\n\n");
        }

        public void Clear()
        {
            for (int i = 0; i < desyncStackTraces.Count; i++)
                desyncStackTraces[i].Dispose();
        }
    }
}
