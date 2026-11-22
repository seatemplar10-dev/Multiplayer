using System;
using System.Collections.Generic;
using Multiplayer.Common;
using Verse;

namespace Multiplayer.Client.Desyncs
{
    // 1.7 lab, FIXES row 113 item 3 (live order sync): every ListOrderHash.CheckpointInterval map ticks, at the end of the
    // map's tick (AsyncTimeComp.Tick), the order and content of the map's shared lists go into the sync opinion, so a
    // split like row 112's (same things, other order, every random state still equal) is a desync at the checkpoint
    // after it starts instead of a few ticks after the next join point.
    // Reads fields only (thingIDNumber, def, def.category, the lists): no Rand, no getter that fills a cache, no
    // allocation but the four entries. Motes are left out: they are per PC (row 112's Mote_Text leaves at a real-time
    // moment on each PC; ThingDef.HasThingIDNumber is false for them).
    public static class ListCheckpoint
    {
        public static readonly Func<Thing, bool> IsPerPc = t => t.def.category == ThingCategory.Mote;
        public static readonly Func<Thing, int> IdOf = t => t.thingIDNumber;

        /// <summary>Appends one entry per SyncedList, in SyncedList order.</summary>
        public static void Take(List<ListHash> into, int mapTick, List<Thing> allThings, TickList normal, TickList rare,
            TickList longList)
        {
            into.Add(ListOrderHash.OfList(SyncedList.AllThings, mapTick, allThings, IsPerPc, IdOf));
            into.Add(OfTickList(SyncedList.TickNormal, mapTick, normal));
            into.Add(OfTickList(SyncedList.TickRare, mapTick, rare));
            into.Add(OfTickList(SyncedList.TickLong, mapTick, longList));
        }

        /// <summary>FIXES row 444: appends one ZoneOrder entry per zone (faction id, then zone ID), raw cells list in list order.</summary>
        public static void TakeZones(MapRandomStateData data, Map map, int mapTick)
        {
            var comp = map.MpComp();
            if (comp?.factionData == null) return;
            var hashes = data.listHashes ??= new List<ListHash>(4);
            var keys = data.zoneKeys ??= new List<string>();
            var zones = new List<Zone>();
            foreach (var kv in comp.factionData) // SortedDictionary: faction loadID order
            {
                var all = kv.Value.zoneManager?.AllZones;
                if (all == null) continue;
                zones.Clear();
                zones.AddRange(all);
                zones.Sort((p, q) => p.ID.CompareTo(q.ID));
                foreach (var z in zones)
                {
                    hashes.Add(ZoneOrderHash.OfZone(mapTick, kv.Key, z.ID, z.cells, c => ZoneOrderHash.CellId(c.x, c.y, c.z)));
                    keys.Add($"faction {kv.Key} zone {z.ID} ({z.label})");
                }
            }
        }

        /// <summary>FIXES row 444 (W444C): ColonistBuildings entries (header + chunks) and one Reservations entry per claimant.</summary>
        public static void TakeDetails(MapRandomStateData data, Map map, int mapTick)
        {
            var hashes = data.listHashes ??= new List<ListHash>(4);

            var lb = map.listerBuildings;
            var bKeys = data.buildingKeys ??= new List<string>();
            var col = lb.allBuildingsColonist;
            hashes.Add(DetailListHash.BuildingHeader(mapTick, col.Count, lb.allBuildingsNonColonist.Count));
            bKeys.Add($"counts: {col.Count} colonist, {lb.allBuildingsNonColonist.Count} non-colonist buildings");
            for (int i = 0; i < col.Count; i += DetailListHash.BuildingChunk)
            {
                hashes.Add(DetailListHash.BuildingChunkHash(mapTick, col, i, IdOf2));
                var b = col[i];
                bKeys.Add($"index {i}..{Math.Min(col.Count, i + DetailListHash.BuildingChunk) - 1}, first building id {b.thingIDNumber} " +
                          $"{b.def?.defName} faction {b.Faction?.loadID.ToString() ?? "none"}");
            }

            var rKeys = data.reservationKeys ??= new List<string>();
            var res = map.reservationManager?.ReservationsReadOnly;
            if (res == null || res.Count == 0) return;
            var byClaimant = new SortedDictionary<int, (Verse.Pawn pawn, List<DetailListHash.Res> rows)>();
            foreach (var r in res)
            {
                var c = r.Claimant;
                if (c == null) continue;
                if (!byClaimant.TryGetValue(c.thingIDNumber, out var e))
                    byClaimant[c.thingIDNumber] = e = (c, new List<DetailListHash.Res>());
                var t = r.Target;
                int tk = t.HasThing ? t.Thing.thingIDNumber : unchecked(ZoneOrderHash.CellId(t.Cell.x, t.Cell.y, t.Cell.z) | 0x40000000);
                e.rows.Add(new DetailListHash.Res(tk, r.Job?.loadID ?? -1));
            }
            foreach (var kv in byClaimant)
            {
                hashes.Add(DetailListHash.OfClaimant(mapTick, kv.Key, kv.Value.rows));
                rKeys.Add($"claimant {kv.Key} ({kv.Value.pawn.LabelShortCap}, faction {kv.Value.pawn.Faction?.loadID.ToString() ?? "none"}), {kv.Value.rows.Count} reservations");
            }
        }

        private static readonly Func<Building, int> IdOf2 = b => b.thingIDNumber;

        // TickList's lists are private in the game; Assembly-CSharp is publicized for this assembly
        private static ListHash OfTickList(SyncedList list, int mapTick, TickList tickList) =>
            ListOrderHash.OfTickList(list, mapTick, tickList.thingLists, tickList.thingsToRegister,
                tickList.thingsToDeregister, IsPerPc, IdOf);
    }
}
