namespace Multiplayer.Client.Util;

// FIXES row 514: no game types here so the unit tests can link this file.
public static class LocalIdRule
{
    // The simulation only runs on the main thread, so an id asked for off it (a mod's worker, Map Preview) is never a sim id.
    // The ticking/command flags are plain statics: without this a worker asking while the main thread ticks took a real id.
    public static bool UseLocal(bool overrideOn, bool inInterface, bool entry, bool mainThread, bool rowOn) =>
        overrideOn || inInterface || entry || (rowOn && !mainThread);
}
