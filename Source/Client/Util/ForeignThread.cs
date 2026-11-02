using System.Threading;
using Verse;

namespace Multiplayer.Client.Util
{
    // FIXES row 187: a thread that is neither the main thread nor the long event thread while it runs a long event.
    // The long event thread is excluded because a joiner loads the game on it (Root_Play, doAsynchronously) while the
    // host and the other players reload on the main thread (SaveLoad.LoadInMainThread): both loads must take the same paths.
    // What is left is background work such as Map Preview generating a world tile's map, which runs on one PC only.
    public static class ForeignThread
    {
        public static bool Now =>
            !UnityData.IsInMainThread
            && !(LongEventHandler.currentEvent != null && Thread.CurrentThread == LongEventHandler.eventThread);
    }
}
