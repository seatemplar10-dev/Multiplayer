using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Multiplayer.Client.Patches
{
    // FIXES row 197: double-click select did not stick on slow frames.
    // Waylith's ImguiDispatch.DispatchInput runs one press pass, then one release pass, per frame. When the release of
    // click 1 and the press of click 2 fall in the same frame, the game sees press 2 (clickCount 2, adds every matching
    // thing on screen) before release 1. Release 1 then finds dragBox.active from press 1 and runs SelectUnderMouse,
    // which clears the selection back to one thing. Here a double-click press finishes the pending release first, the
    // way it would have run in order: clear unless shift is held (SelectUnderMouse does that), and drop the drag box.
    // Local UI only: no sim state, no sync, nothing saved.
    [HarmonyPatch(typeof(Selector), "HandleMapClicks")]
    static class SelectorDoubleClickPendingRelease
    {
        static int logged;

        static void Prefix(Selector __instance)
        {
            var e = Event.current;
            if (e == null || e.type != EventType.MouseDown || e.button != 0 || e.clickCount < 2 || !__instance.dragBox.active)
                return;

            __instance.dragBox.active = false;
            if (!Selector.ShiftIsHeld)
                __instance.ClearSelection();

            if (logged < 3)
            {
                logged++;
                Log.Message($"Multiplayer: double-click arrived before the first click's release (same frame); kept the double-click selection (row 197, {logged}/3 logged)");
            }
        }
    }
}
