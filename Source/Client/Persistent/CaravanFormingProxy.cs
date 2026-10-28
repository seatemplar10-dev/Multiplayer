using RimWorld;
using System;
using UnityEngine;
using Verse;

namespace Multiplayer.Client
{
    public class CaravanFormingProxy : Dialog_FormCaravan, ISwitchToMap
    {
        public static CaravanFormingProxy drawing;

        // FIXES row 273: the window shows the session it was opened for (one session per faction per map), not the
        // first session on the map. Session ids are scribed, so the id stays valid after a load.
        public CaravanFormingSession Session => map.MpComp().sessionManager.GetFirstWithId<CaravanFormingSession>(originalSessionId);

        public int originalSessionId;

        public CaravanFormingProxy(int originalSessionId, Map map, bool reform = false, Action onClosed = null, bool mapAboutToBeRemoved = false, IntVec3? meetingSpot = null) : base(map, reform, onClosed, mapAboutToBeRemoved, meetingSpot)
        {
            this.originalSessionId = originalSessionId;
        }

        public override void DoWindowContents(Rect inRect)
        {
            var session = Session;
            SyncSessionWithTransferablesMarker.DrawnSessionWithTransferables = session;
            drawing = this;

            try
            {
                if (session == null)
                {
                    Close();
                }
                else if (session.uiDirty)
                {
                    Notify_TransferablesChanged();
                    startingTile = session.startingTile;
                    destinationTile = session.destinationTile;
                    autoSelectTravelSupplies = session.autoSelectTravelSupplies;
                    if (autoSelectTravelSupplies)
                        SelectApproximateBestTravelSupplies();

                    session.uiDirty = false;
                }

                base.DoWindowContents(inRect);
            }
            finally
            {
                drawing = null;
                SyncSessionWithTransferablesMarker.DrawnSessionWithTransferables = null;
            }
        }
    }

}
