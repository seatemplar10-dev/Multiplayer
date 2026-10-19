using System;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using Verse;
using Verse.Profile;

namespace Multiplayer.Client;

public static class Rejoiner
{
    public static void ReturnToEntry(Action onFinished)
    {
        LongEventHandler.ClearQueuedEvents();
        LongEventHandler.QueueLongEvent(() =>
        {
            MemoryUtility.ClearAllMapsAndWorld();
            Current.Game = null;

            LongEventHandler.ExecuteWhenFinished(() =>
            {
                MpUI.ClearWindowStack();
                onFinished?.Invoke();
            });
        }, "Entry", "LoadingLongEvent", true, null, false);
    }

    public static void DoRejoin()
    {
        // 1.7 lab, FIXES row 118: the rejoin load (a multifaction snapshot) has no current map; Loader.PostLoad gives this view back
        AsyncTime.WorldCurrentMapPin.RememberViewForReload();

        Multiplayer.Client.Send(Packets.Client_RequestRejoin);

        Multiplayer.Client.ChangeState(ConnectionStateEnum.ClientLoading);
        Multiplayer.Client.Lenient = true;

        Multiplayer.session.desynced = false;

        Log.Message("Multiplayer: rejoining");

        ReturnToEntry(() => Find.WindowStack.Add(new RejoiningWindow()));
    }
}
