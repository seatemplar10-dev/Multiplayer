using System;
using System.Collections.Generic;
using System.Linq;
using Multiplayer.Client.Desyncs;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;
using RimWorld;
using Verse;

namespace Multiplayer.Client
{
    public class SyncCoordinator
    {
        public bool ShouldCollect => !Multiplayer.IsReplay;

        private ClientSyncOpinion OpinionInBuilding =>
            currentOpinion ??= new ClientSyncOpinion(TickPatch.Timer)
            {
                isLocalClientsOpinion = true
            };

        // Contains both local and remote opinions. The first opinion is the oldest one, and the last is the newest one.
        // The host player has only local opinions.
        public readonly List<ClientSyncOpinion> knownClientOpinions = [];

        private ClientSyncOpinion currentOpinion;

        public int lastValidTick = -1;
        public bool arbiterWasPlayingOnLastValidTick;
        private int checksPassed;
        private int cappedOpinions;
        private long listEntriesCompared; // FIXES row 113: shows in the "sync check passed" line that checkpoints are compared

        // FIXES row 99 (live full-state checksum): the lab harness's checkpoints of the last FullStateCheckpoints.CarryTicks MP
        // ticks; every finished opinion carries them, so any compared window after a checkpoint compares it
        private readonly List<FullStateHash> recentFullState = [];
        private long fullStateCompared; // common checkpoints compared, shown in the "sync check passed" line

        /// <summary>
        /// FIXES row 99, lab hook (null unless Sea's MP Lab harness sets it, -mplabfulllive): called once per full-state
        /// checkpoint both opinions of a compared pair carry, with (MP timer, local hash, remote hash, the pair's desync message
        /// or null). A desync stops the comparisons (Multiplayer.session.desynced), so the first unequal call is the first mismatch.
        /// </summary>
        public static Action<int, ulong, ulong, string> OnFullStateCompared;

        private const int MaxBacklog = 30;

        public ClientSyncOpinion FinishLocalOpinion()
        {
            if (!ShouldCollect || currentOpinion == null) return null;
            currentOpinion.roundMode = RoundMode.GetCurrentRoundMode();
            var opinion = currentOpinion;
            currentOpinion = null;

            // FIXES row 99: the recent full-state checkpoints ride in every opinion (the same timers on every PC: opinions are
            // finished at the same MP timers and the harness walks at the same ones)
            FullStateCheckpoints.Prune(recentFullState, TickPatch.Timer);
            if (recentFullState.Count > 0)
                opinion.fullStateHashes = new List<FullStateHash>(recentFullState);

            // Every side drops the same traces past the cap, so the window is still compared; say so now and then
            if (opinion.tracesCapped)
            {
                cappedOpinions++;
                if (cappedOpinions == 1 || cappedOpinions % 100 == 0)
                    Log.Warning($"Multiplayer: the sync opinion starting at tick {opinion.startTick} reached the cap of " +
                                $"{SyncOpinion.MaxTraceHashes} trace hashes; later traces of its window are dropped on " +
                                "every side and random states are still compared in full " +
                                $"({cappedOpinions} capped windows so far)");
            }

            return opinion;
        }

        /// <summary>
        /// True once the opinion being built holds <see cref="SyncOpinion.MaxTraceHashes"/> traces: the next trace is
        /// dropped, so callers can skip capturing it. A trace is dropped whole (hash and item together, so their
        /// indices stay aligned). The cap is a fixed count in the deterministic trace sequence, so the host, the
        /// arbiter and joiners all keep the same prefix of the window. Never starts an opinion.
        /// </summary>
        public bool DropTraceAtCap()
        {
            var opinion = currentOpinion;
            if (opinion == null || !opinion.TraceCapReached) return false;

            opinion.tracesCapped = true;
            return true;
        }

        /// <summary>
        /// Adds a client opinion to the <see cref="knownClientOpinions"/> list and checks that it matches the most recent currently in there. If not, a desync event is fired.
        /// </summary>
        /// <param name="newOpinion">The <see cref="ClientSyncOpinion"/> to add and check.</param>
        public void AddClientOpinionAndCheckDesync(ClientSyncOpinion newOpinion)
        {
            //If we've already desynced, don't even bother
            if (Multiplayer.session.desynced) return;

            //If this is the first client opinion we have nothing to compare it with, so just add it
            if (knownClientOpinions.Count == 0)
            {
                knownClientOpinions.Add(newOpinion);
                return;
            }

            if (knownClientOpinions[0].isLocalClientsOpinion == newOpinion.isLocalClientsOpinion)
            {
                knownClientOpinions.Add(newOpinion);
                if (knownClientOpinions.Count > MaxBacklog)
                    RemoveAndClearFirst();
                return;
            }

            // Remove all opinions that started before this one, as it's the most up-to-date one
            while (knownClientOpinions.Count > 0 && knownClientOpinions[0].startTick < newOpinion.startTick)
                RemoveAndClearFirst();

            // If there are none left, we don't need to compare this new one
            if (knownClientOpinions.Count == 0)
            {
                knownClientOpinions.Add(newOpinion);
                return;
            }

            if (knownClientOpinions.First().startTick != newOpinion.startTick)
            {
                // Ignore this opinion
                newOpinion.Clear();
                return;
            }

            // If these two contain the same tick range - i.e., they start at the same time, because they should
            // continue to the current tick, then do a comparison.
            var oldOpinion = knownClientOpinions.RemoveFirst();

            // Actually do the comparison to find any desync
            var desyncMessage = oldOpinion.CheckForDesync(newOpinion);
            ReportFullState(oldOpinion, newOpinion, desyncMessage);

            if (desyncMessage != null)
            {
                MpLog.Log($"Desynced after last valid tick {lastValidTick}: {desyncMessage}");
                Multiplayer.session.desynced = true;
                TickPatch.ClearSimulating();
                OnMainThread.Enqueue(() => HandleDesync(oldOpinion, newOpinion, desyncMessage));
            }
            else
            {
                // Update fields
                lastValidTick = oldOpinion.startTick;
                arbiterWasPlayingOnLastValidTick = Multiplayer.session.ArbiterPlaying;

                // A line now and then, so a soak's log shows the checks run and carry traces
                checksPassed++;
                foreach (var map in oldOpinion.mapStates)
                    listEntriesCompared += map.listHashes?.Count ?? 0;
                if (checksPassed == 1 || checksPassed % 100 == 0)
                {
                    var localOpinion = oldOpinion.isLocalClientsOpinion ? oldOpinion : newOpinion;
                    var remoteOpinion = oldOpinion.isLocalClientsOpinion ? newOpinion : oldOpinion;
                    Log.Message($"Multiplayer: sync check {checksPassed} passed (opinion start tick {oldOpinion.startTick}, " +
                                $"trace hashes local {localOpinion.desyncStackTraceHashes.Count} / remote " +
                                $"{remoteOpinion.desyncStackTraceHashes.Count}, arbiter playing {Multiplayer.session.ArbiterPlaying}, " +
                                $"list checkpoint entries compared so far {listEntriesCompared}, " +
                                $"full-state checkpoints compared so far {fullStateCompared})");
                }

                // Return inner data to the pool
                oldOpinion.Clear();
            }
        }

        // FIXES row 99: counts the full-state checkpoints the pair compared and hands each to the lab hook
        private void ReportFullState(ClientSyncOpinion a, ClientSyncOpinion b, string desyncMessage)
        {
            var local = a.isLocalClientsOpinion ? a : b;
            var remote = a.isLocalClientsOpinion ? b : a;
            var pairs = FullStateCheckpoints.Common(local.fullStateHashes, remote.fullStateHashes);
            fullStateCompared += pairs.Count;

            var hook = OnFullStateCompared;
            if (hook == null) return;
            foreach (var (l, r) in pairs)
            {
                try
                {
                    hook(l.timer, l.hash, r.hash, desyncMessage);
                }
                catch (Exception e)
                {
                    Log.Error($"Multiplayer: full-state checkpoint hook failed at MP timer {l.timer}: {e}");
                }
            }
        }

        private void RemoveAndClearFirst()
        {
            var opinion = knownClientOpinions.RemoveFirst();
            opinion.Clear();
        }

        /// <summary>
        /// Called by <see cref="AddClientOpinionAndCheckDesync"/> if the newly added opinion doesn't match with what other ones.
        /// </summary>
        /// <param name="oldOpinion">The first up-to-date client opinion present in <see cref="knownClientOpinions"/>, that disagreed with the new one</param>
        /// <param name="newOpinion">The opinion passed to <see cref="AddClientOpinionAndCheckDesync"/> that disagreed with the currently known opinions.</param>
        /// <param name="desyncMessage">The error message that explains exactly what desynced.</param>
        private void HandleDesync(ClientSyncOpinion oldOpinion, ClientSyncOpinion newOpinion, string desyncMessage)
        {
            // Identify which of the two sync infos is local and which is the remote.
            var local = oldOpinion.isLocalClientsOpinion ? oldOpinion : newOpinion;
            var remote = !oldOpinion.isLocalClientsOpinion ? oldOpinion : newOpinion;

            var diffAt = FindTraceHashesDiffTick(local, remote, out var found);
            Multiplayer.Client.Send(new ClientDesyncedPacket(local.startTick, diffAt));

            MpUI.ClearWindowStack();
            Find.WindowStack.Add(new DesyncedWindow(
                desyncMessage,
                new SaveableDesyncInfo(this, local, remote, diffAt, found)
            ));
        }

        private static int FindTraceHashesDiffTick(ClientSyncOpinion local, ClientSyncOpinion remote, out bool found)
        {
            found = true;
            //Find the length of whichever stack trace is shorter.
            var localCount = local.desyncStackTraceHashes.Count;
            var remoteCount = remote.desyncStackTraceHashes.Count;
            int count = Math.Min(localCount, remoteCount);

            //Find the point at which the hashes differ - this is where the desync occurred.
            for (int i = 0; i < count; i++)
                if (local.desyncStackTraceHashes[i] != remote.desyncStackTraceHashes[i])
                    return i;

            found = false;
            if (localCount != remoteCount)
                return count - 1;

            return -1;
        }

        /// <summary>
        /// Adds a random state to the commandRandomStates list
        /// </summary>
        /// <param name="state">The state to add</param>
        public void TryAddCommandRandomState(ulong state)
        {
            if (!ShouldCollect) return;
            OpinionInBuilding.TryMarkSimulating();
            OpinionInBuilding.commandRandomStates.Add((uint) (state >> 32));
        }

        /// <summary>
        /// Adds a random state to the worldRandomStates list
        /// </summary>
        /// <param name="state">The state to add</param>
        public void TryAddWorldRandomState(ulong state)
        {
            if (!ShouldCollect) return;
            OpinionInBuilding.TryMarkSimulating();
            OpinionInBuilding.worldRandomStates.Add((uint) (state >> 32));
        }

        /// <summary>
        /// Adds a random state to the list of the map random state handler for the map with the given id
        /// </summary>
        /// <param name="map">The map id to add the state to</param>
        /// <param name="state">The state to add</param>
        public void TryAddMapRandomState(int map, ulong state)
        {
            if (!ShouldCollect) return;
            OpinionInBuilding.TryMarkSimulating();
            OpinionInBuilding.GetRandomStatesForMap(map).Add((uint) (state >> 32));
        }

        /// <summary>
        /// FIXES row 113 item 3: order and content hashes of the map's listerThings.AllThings and its three tick lists,
        /// motes excluded (<see cref="ListCheckpoint"/>). AsyncTimeComp.Tick calls it right after the map's random state,
        /// at map ticks where <see cref="ListOrderHash.IsCheckpoint"/> is true.
        /// </summary>
        public void TryAddListCheckpoint(int map, int mapTick, List<Thing> allThings, TickList normal, TickList rare,
            TickList longList)
        {
            if (!ShouldCollect) return;
            OpinionInBuilding.TryMarkSimulating();
            var data = OpinionInBuilding.GetMapData(map);
            ListCheckpoint.Take(data.listHashes ??= new List<ListHash>(4), mapTick, allThings, normal, rare, longList);
        }

        /// <summary>
        /// FIXES row 99 (live full-state checksum): the lab harness (MPLabFullState, -mplabfulllive) calls this from TickPatch.DoTick's
        /// postfix with the root hash of its full-state walk at MP timer <paramref name="timer"/>. The checkpoint rides in every
        /// opinion finished in the next <see cref="FullStateCheckpoints.CarryTicks"/> MP ticks.
        /// </summary>
        public void TryAddFullStateHash(int timer, ulong hash)
        {
            if (!ShouldCollect) return;
            FullStateCheckpoints.Add(recentFullState, timer, hash);
        }

        /// <summary>
        /// Logs an item to aid in desync debugging.
        /// </summary>
        /// <param name="info1">Information to be logged</param>
        /// <param name="info2">Information to be logged</param>
        public void TryAddInfoForDesyncLog(string info1, string info2)
        {
            if (!ShouldCollect) return;

            OpinionInBuilding.TryMarkSimulating();

            if (DropTraceAtCap()) return;

            int hash = Gen.HashCombineInt(info1.GetHashCode(), info2.GetHashCode());

            OpinionInBuilding.desyncStackTraces.Add(new StackTraceLogItemObj {
                tick = TickPatch.Timer,
                hash = hash,
                info1 = info1,
                info2 = info2,
            });

            OpinionInBuilding.desyncStackTraceHashes.Add(hash);
        }

        public void TryAddStackTraceForDesyncLogRaw(StackTraceLogItemRaw item, int depth, int hashIn, string moreInfo = null, bool hashDepth = true)
        {
            if (!ShouldCollect) return;

            OpinionInBuilding.TryMarkSimulating();

            if (DropTraceAtCap())
            {
                item.Dispose();
                return;
            }

            item.depth = depth;
            item.ticksGame = Find.TickManager.ticksGameInt;
            item.rngState = Rand.StateCompressed;
            item.tick = TickPatch.Timer;
            item.factionName = Faction.OfPlayer?.Name ?? string.Empty;
            item.moreInfo = moreInfo;

            var thing = ThingContext.Current;
            if (thing != null)
            {
                item.thingDef = thing.def;
                item.thingId = thing.thingIDNumber;
            }

            var hash = hashDepth
                ? Gen.HashCombineInt(hashIn, depth, (int)(item.rngState >> 32), (int)item.rngState)
                : Gen.HashCombineInt(Gen.HashCombineInt(hashIn, (int)(item.rngState >> 32)), (int)item.rngState);
            item.hash = hash;

            OpinionInBuilding.desyncStackTraces.Add(item);

            // Track & network trace hash, for comparison with other opinions.
            OpinionInBuilding.desyncStackTraceHashes.Add(hash);
        }

        public static string MethodNameWithIL(string rawName)
        {
            // Note: The names currently don't include IL locations so the code is commented out

            // at Verse.AI.JobDriver.ReadyForNextToil () [0x00000] in <c847e073cda54790b59d58357cc8cf98>:0
            // =>
            // at Verse.AI.JobDriver.ReadyForNextToil () [0x00000]
            // rawName = rawName.Substring(0, rawName.LastIndexOf(']') + 1);

            return rawName;
        }

        public static string MethodNameWithoutIL(string rawName)
        {
            // Note: The names currently don't include IL locations so the code is commented out

            // at Verse.AI.JobDriver.ReadyForNextToil () [0x00000] in <c847e073cda54790b59d58357cc8cf98>:0
            // =>
            // at Verse.AI.JobDriver.ReadyForNextToil ()
            // rawName = rawName.Substring(0, rawName.LastIndexOf('['));

            return rawName;
        }
    }
}
