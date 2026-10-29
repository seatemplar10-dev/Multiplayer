using System;
using System.Collections.Generic;
using Multiplayer.API;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Client
{
    public class CaravanFormingSession : ExposableSession, ISessionWithTransferables, ISessionWithCreationRestrictions
    {
        public Map map;

        public Faction faction;

        public bool reform;
        public Action onClosed;
        public bool mapAboutToBeRemoved;
        public PlanetTile startingTile = -1;
        public PlanetTile destinationTile = -1;
        public List<TransferableOneWay> transferables;
        public bool autoSelectTravelSupplies;
        public IntVec3? meetingSpot;

        public bool uiDirty;

        public override Map Map => map;

        public override bool IsSessionValid => map != null && faction != null;

        // Used when saving and loading
        private CaravanFormingSession(Map map) : base(map)
        {
            this.map = map;
        }

        public CaravanFormingSession(Faction faction, Map map) : base(map)
        {
            this.map = map;
            this.faction = faction;
        }

        public CaravanFormingSession(Faction faction, Map map, bool reform, Action onClosed, bool mapAboutToBeRemoved, IntVec3? meetingSpot = null) : this(faction, map)
        {
            this.reform = reform;
            this.onClosed = onClosed;
            this.mapAboutToBeRemoved = mapAboutToBeRemoved;
            autoSelectTravelSupplies = !reform;
            this.meetingSpot = meetingSpot;

            // Should this be called from PostAddSession? It would also be called from the other constructor
            // (map only parameter) - do we want that to happen? Is it going to come up?
            AddItems();
        }

        private void AddItems()
        {
            transferables = BuildTransferables();
        }

        // FIXES row 273 (v2): list the map without CalculateAndRecacheTransferables. That also builds the window widgets,
        // and Vehicle Framework's postfix on CreateCaravanTransferableWidgets then overwrites its static Vehicles-tab list on
        // every PC (synced code), so one faction's click put its list into another player's open caravan window.
        private List<TransferableOneWay> BuildTransferables()
        {
            var dialog = new CaravanFormingProxy(sessionId, map, reform, null, mapAboutToBeRemoved, meetingSpot)
            {
                autoSelectTravelSupplies = autoSelectTravelSupplies,
                transferables = new List<TransferableOneWay>()
            };
            dialog.AddPawnsToTransferables();
            dialog.AddItemsToTransferables();
            return dialog.transferables;
        }

        // FIXES row 273 (v2): a form/reform click that finds this faction's session already on the map re-lists the map, as
        // vanilla does on every open. Before, the old list stayed: a pawn that became sendable later (or a session made
        // before the pawn arrived) never showed up until someone pressed Cancel. Counts already chosen carry over (matched by
        // thing id); things that left the map drop out. Called from synced code only - same list on every PC.
        public void RefreshTransferables()
        {
            var fresh = BuildTransferables();

            // Every old count carries over, 0 too (a reform pawn the player unticked stays unticked)
            var oldCounts = new Dictionary<int, int>();
            if (transferables != null)
                foreach (var tr in transferables)
                    if (tr?.things != null)
                        foreach (var t in tr.things)
                            if (t != null)
                                oldCounts[t.thingIDNumber] = tr.CountToTransfer;

            foreach (var tr in fresh)
            {
                var old = tr.things.Find(t => oldCounts.ContainsKey(t.thingIDNumber));
                if (old != null)
                    tr.CountToTransfer = Math.Min(oldCounts[old.thingIDNumber], tr.MaxCount);
            }

            // Same list object: an open window of this session keeps reading the session's list
            transferables ??= new List<TransferableOneWay>();
            transferables.Clear();
            transferables.AddRange(fresh);
            uiDirty = true;
        }

        public CaravanFormingProxy OpenWindow(bool sound = true)
        {
            var dialog = PrepareDummyDialog();
            if (!sound)
                dialog.soundAppear = null;

            CaravanUIUtility.CreateCaravanTransferableWidgets(
                transferables,
                out dialog.pawnsTransfer,
                out dialog.itemsTransfer,
                out dialog.travelSuppliesTransfer,
                "FormCaravanColonyThingCountTip".Translate(),
                dialog.IgnoreInventoryMode,
                () => dialog.MassCapacity - dialog.MassUsage,
                dialog.AutoStripSpawnedCorpses,
                dialog.CurrentTile,
                mapAboutToBeRemoved
            );

            dialog.Notify_TransferablesChanged();
            Find.WindowStack.Add(dialog);

            return dialog;
        }

        private CaravanFormingProxy PrepareDummyDialog()
        {
            var dialog = new CaravanFormingProxy(sessionId, map, reform, null, mapAboutToBeRemoved, meetingSpot)
            {
                transferables = transferables,
                startingTile = startingTile,
                destinationTile = destinationTile,
                thisWindowInstanceEverOpened = true,
                autoSelectTravelSupplies = autoSelectTravelSupplies,
            };

            if (autoSelectTravelSupplies)
                dialog.SelectApproximateBestTravelSupplies();

            return dialog;
        }

        [SyncMethod]
        public void ChooseRoute(PlanetTile destination)
        {
            var dialog = PrepareDummyDialog();
            dialog.Notify_ChoseRoute(destination);

            startingTile = dialog.startingTile;
            destinationTile = dialog.destinationTile;

            uiDirty = true;
        }

        [SyncMethod]
        public void TryReformCaravan()
        {
            if (PrepareDummyDialog().TryReformCaravan())
                Remove();
        }

        [SyncMethod]
        public void TryFormAndSendCaravan()
        {
            if (PrepareDummyDialog().TryFormAndSendCaravan())
                Remove();
        }

        [SyncMethod(debugOnly = true)]
        public void DebugTryFormCaravanInstantly()
        {
            if (PrepareDummyDialog().DebugTryFormCaravanInstantly())
                Remove();
        }

        [SyncMethod]
        public void Reset()
        {
            transferables.ForEach(t => t.CountToTransfer = 0);
            uiDirty = true;
        }

        [SyncMethod]
        public void Cancel()
        {
            Remove();
        }

        private void Remove()
        {
            map.MpComp().sessionManager.RemoveSession(this);

            if (Find.WorldRoutePlanner.currentFormCaravanDialog is CaravanFormingProxy proxy && proxy.originalSessionId == sessionId)
                Find.WorldRoutePlanner.Stop();
        }

        [SyncMethod]
        public void SetAutoSelectTravelSupplies(bool value)
        {
            if (autoSelectTravelSupplies != value)
            {
                autoSelectTravelSupplies = value;
                uiDirty = true;
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();

            Scribe_References.Look(ref faction, "faction");
            Scribe_Values.Look(ref reform, "reform");
            Scribe_Values.Look(ref onClosed, "onClosed");
            Scribe_Values.Look(ref mapAboutToBeRemoved, "mapAboutToBeRemoved");
            Scribe_Values.Look(ref startingTile, "startingTile");
            Scribe_Values.Look(ref destinationTile, "destinationTile");
            Scribe_Values.Look(ref autoSelectTravelSupplies, "autoSelectTravelSupplies");
            Scribe_Values.Look(ref meetingSpot, "meetingSpot");

            Scribe_Collections.Look(ref transferables, "transferables", LookMode.Deep);
        }

        public Transferable GetTransferableByThingId(int thingId)
        {
            // FIXES row 273 (v2): a thing that left the game loads back as null in an old session's list
            return transferables.Find(tr => tr?.things != null && tr.things.Any(t => t != null && t.thingIDNumber == thingId));
        }

        public void Notify_CountChanged(Transferable tr)
        {
            uiDirty = true;
        }

        public override bool IsCurrentlyPausing(Map map) => map == this.map;

        public override FloatMenuOption GetBlockingWindowOptions(ColonistBar.Entry entry)
        {
            // FIXES row 273: a map can hold one session per faction - name the faction when it is not the local one
            string label = "MpCaravanFormingSession".Translate();
            if (faction != null && faction != Multiplayer.RealPlayerFaction)
                label += $" ({faction.Name})";

            return new FloatMenuOption(label, () =>
            {
                OpenWindow();
            });
        }

        // FIXES row 273: sessions of different factions can exist together on one map; one session per faction.
        public bool CanExistWith(Session other) => other is not CaravanFormingSession otherForming || otherForming.faction != faction;
    }

}
