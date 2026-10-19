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

        // TickList's lists are private in the game; Assembly-CSharp is publicized for this assembly
        private static ListHash OfTickList(SyncedList list, int mapTick, TickList tickList) =>
            ListOrderHash.OfTickList(list, mapTick, tickList.thingLists, tickList.thingsToRegister,
                tickList.thingsToDeregister, IsPerPc, IdOf);
    }
}
