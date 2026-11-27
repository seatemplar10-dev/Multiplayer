using System;
using System.Collections.Generic;

namespace Multiplayer.Client.Util;

// FIXES rows 548/549: no game types here so the unit tests can link this file.
public static class PinRule
{
    // Row 548: record "pushed" the moment push() returned, so a throw in afterPush (WorldComp.SetFaction) still gets popped.
    public static void PushThenApply(ref bool pushed, Action push, Action afterPush, bool rowOn = true)
    {
        push();
        if (rowOn) pushed = true;
        afterPush();
    }

    // Row 548: the Finalizer may only pop when the session still exists and the stack still holds the frame. ErrorWhileGeneratingMap
    // -> GoToMainMenu -> StopMultiplayer clears the stack (and the game) mid-event.
    public static bool CanPop(bool gameAlive, int stackCount, bool mapsAlive, bool rowOn = true) =>
        !rowOn || (gameAlive && stackCount > 0 && mapsAlive);

    // Row 548: the faction ofPlayer, WorldComp and every map must hold after the Finalizer. The faction before the Prefix, unless
    // the body changed the real player faction (FactionCreator): then what ChangeRealPlayerFaction set.
    public static T RestoreTarget<T>(T before, T realBefore, T realNow, bool rowOn = true) where T : class =>
        rowOn && !EqualityComparer<T>.Default.Equals(realBefore, realNow) && realNow != null ? realNow : before;

    // Row 549: vanilla 1.7 UpdateCurrentSynchronousEvent runs callback itself, so the old append ran it twice.
    public static bool AppendCallback(bool hasCallback, bool rowOn = true) => !rowOn && hasCallback;
}
