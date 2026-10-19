using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Client.Patches;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.Sound;

namespace Multiplayer.Client.AsyncTime;

// Find.CurrentMap during the simulation. Unpinned it is the map this client happens to view: the host's colony, a
// joiner's other colony, or null on a headless client, so simulation code that reads it (storyteller, quests, game components,
// mods; Milira's GameComponentTick threw on the observer only) desyncs or fails on some clients only. World ticks, world
// commands and the long events the simulation queues therefore pin it to a map every client has. Map commands pin their own
// map (AsyncTimeComp.ExecuteCmd). 1.7 lab (FIXES rows 87, 97): a map tick pins its own map through PinMap (SimGuards C9).
// Presentation that asks "is this the map I am looking at" (sounds, camera shake) still gets this client's view.
// FIXES row 118: the outermost restore puts back the view it found, also "no map". A load from a multifaction snapshot has
// no current map (SaveLoad.CreateGameDataSnapshot drops currentMapIndex, AllowCurrentMapNullWhenLoading keeps it null), so a
// rejoiner would sit on the world view for good; Loader.PostLoad gives it its view back (RestoreViewAfterLoad).
public static class WorldCurrentMapPin
{
    private static int depth;
    private static Game game;
    private static Map viewedMap;
    private static Map pinnedMap;
    // Pinned maps of the enclosing scopes, innermost last. Every client enters the same scopes in the same order, so these
    // agree between clients; only viewedMap is this client's own.
    private static readonly List<Map> outerPins = new();
    // Whether the innermost level is a map context's own pin (PinMap), and the same for the enclosing levels
    private static bool mapLevel;
    private static readonly List<bool> outerMapLevels = new();
    private static int viewScope;
    // The simulation removed the map this client was viewing; the view switches once the context is over (FlushViewSwitch)
    private static Game viewSwitchPending;
    // FIXES row 118: uniqueID of the map this client viewed before a load that replaces the Game (-1 = none recorded)
    private static int viewToRestoreId = -1;
    // FIXES row 118 diagnostic: restores that left this client without a view while maps exist (per PC, logging only)
    private static readonly System.Diagnostics.Stopwatch noViewClock = System.Diagnostics.Stopwatch.StartNew();
    private static bool noViewStretch;
    private static long noViewLoggedAt;
    private static int noViewCount;

    public static bool Active => depth > 0;
    public static int Depth => depth;
    public static Map PinnedMap => pinnedMap;
    public static Map ViewedMap => viewedMap;
    // A map context pinned a map this client is not looking at (world and long-event pins keep their old presentation)
    public static bool PinnedAwayFromView => depth > 0 && mapLevel && pinnedMap != viewedMap;

    public static void Pin() => Enter(null);

    // A map context of the simulation (a map tick) sees its own map. Nested in another pin it pins that map until the
    // matching Unpin, then the enclosing pin's map is back.
    public static void PinMap(Map map) => Enter(map);

    // Writes Game.currentMapIndex directly, like AsyncTimeComp.ExecuteCmd: the Game.CurrentMap setter notifies the map UI
    // and ambient sound, and VTRSync's MapSwitchPatch would send a viewed-map command.
    private static void Enter(Map map)
    {
        var g = Current.Game;

        if (depth > 0)
        {
            outerPins.Add(pinnedMap);
            outerMapLevels.Add(mapLevel);
            depth++;
            if (map != null && g != null && g == game && g.Maps.Contains(map))
            {
                pinnedMap = map;
                mapLevel = true;
                g.currentMapIndex = IndexIn(g, map);
            }
            return;
        }

        Map viewed = null, pinned = null;
        if (g != null)
        {
            viewed = MapAt(g, g.currentMapIndex);
            pinned = map != null && g.Maps.Contains(map) ? map : ChooseMap(g.Maps, null);
        }

        game = g;
        viewedMap = viewed;
        pinnedMap = pinned;
        mapLevel = map != null && pinned == map;
        depth = 1;

        if (g != null)
            g.currentMapIndex = IndexIn(g, pinned);
    }

    public static void Unpin()
    {
        var g = game;
        if (UnpinCore())
            NoteViewAfterRestore(g, "Unpin");
    }

    // True when this call was the outermost level and restored this client's view
    private static bool UnpinCore()
    {
        if (depth == 0) return false;

        if (--depth > 0)
        {
            pinnedMap = outerPins[outerPins.Count - 1];
            outerPins.RemoveAt(outerPins.Count - 1);
            mapLevel = outerMapLevels[outerMapLevels.Count - 1];
            outerMapLevels.RemoveAt(outerMapLevels.Count - 1);
            if (game != null && game == Current.Game)
                game.currentMapIndex = IndexIn(game, pinnedMap);
            return false;
        }

        var g = game;
        var restore = viewedMap;
        game = null;
        viewedMap = null;
        pinnedMap = null;
        mapLevel = false;
        outerPins.Clear();
        outerMapLevels.Clear();
        viewScope = 0;

        // A reload replaced the Game: the new one keeps its own current map
        if (g == null || g != Current.Game) return false;

        if (restore == null || g.Maps.Contains(restore))
        {
            g.currentMapIndex = IndexIn(g, restore);
            return true;
        }

        // The simulation removed the map this client was viewing. Game.DeinitAndRemoveMap would switch the view; here the
        // switch waits for the end of the tick or command: it runs UI code and VTRSync sends a command from this client.
        g.currentMapIndex = -1;
        viewSwitchPending = g;
        return true;
    }

    // What Game.DeinitAndRemoveMap does for the viewer of a removed map, from "no map", so VTRSync does not count a leave
    // from whichever map moved into the old index. Called where a tick or command has ended (not ticking, not executing).
    public static void FlushViewSwitch()
    {
        if (viewSwitchPending == null || depth > 0) return;

        var g = viewSwitchPending;
        viewSwitchPending = null;

        // A camera jump in the simulation set a new view in the meantime
        if (g != Current.Game || g.currentMapIndex >= 0) return;

        if (g.Maps.Count > 0)
            g.CurrentMap = g.Maps[0];
        else
        {
            // The last map is gone: the setter would see -1 -> -1 and skip what vanilla's removal does for its viewer
            Find.MapUI?.Notify_SwitchedMap();
            AmbientSoundManager.Notify_SwitchedMap();
        }
        Find.World.renderer.wantedMode = WorldRenderMode.Planet;
    }

    // Leak repair (SimGuards.ResetIfLeaked): unwinds every level, restoring this client's view as the outermost Unpin does
    internal static void ForceReset()
    {
        var g = game;
        bool restored = false;
        while (depth > 0)
            restored |= UnpinCore();
        viewScope = 0;
        FlushViewSwitch();
        if (restored)
            NoteViewAfterRestore(g, "ForceReset");
    }

    // FIXES row 118 diagnostic: the outermost restore left this client viewing no map while maps exist (expected only on a
    // headless arbiter or a multifaction spectator who has not picked a faction). Said when such a stretch starts, then at
    // most every 5 min while it lasts; reads and logs only, so it cannot change the simulation.
    private static void NoteViewAfterRestore(Game g, string where)
    {
        if (g == null || g != Current.Game || g.Maps.Count == 0 || viewSwitchPending == g) return;
        if (g.currentMapIndex >= 0)
        {
            noViewStretch = false;
            return;
        }

        noViewCount++;
        long now = noViewClock.ElapsedMilliseconds;
        if (noViewStretch && now - noViewLoggedAt < 300_000) return;
        noViewStretch = true;
        noViewLoggedAt = now;
        Log.Warning($"Multiplayer: the map pin's outermost {where} left Game.currentMapIndex at -1 while {g.Maps.Count} map(s) exist, this PC views no map ({noViewCount} such restore(s) in this session; FIXES row 118)");
    }

    // FIXES row 118: before a load that replaces the Game (Rejoiner.DoRejoin, Loader.ReloadGame) remember this client's own
    // view; inside a pin that is the view the pin keeps, not the pinned map. Without a view the last record stays.
    public static void RememberViewForReload()
    {
        var g = Current.Game;
        if (g == null) return;
        var view = depth > 0 && game == g ? viewedMap : MapAt(g, g.currentMapIndex);
        if (view != null)
            viewToRestoreId = view.uniqueID;
    }

    public static void ForgetViewForReload() => viewToRestoreId = -1;

    // FIXES row 118, Loader.PostLoad (outside any context): a load that chose a map keeps it. A load that chose none (every
    // multifaction snapshot) gives this client the map it viewed before the reload if that map still exists, else the first
    // home map of its own faction; a spectator (a multifaction joiner who has not picked a faction, the arbiter) keeps the
    // world view, as before. Through the setter, as a player's own map switch: map UI, ambient sound, VTRSync's player count.
    public static void RestoreViewAfterLoad(Faction myFaction, Faction spectatorFaction)
    {
        int id = viewToRestoreId;
        viewToRestoreId = -1;

        var g = Current.Game;
        if (g == null || g.Maps.Count == 0) return;
        var viewNow = depth > 0 && game == g ? viewedMap : MapAt(g, g.currentMapIndex);
        if (viewNow != null) return;

        Map map = null;
        if (id >= 0)
            map = g.Maps.Find(m => m.uniqueID == id);
        if (map == null && myFaction != null && myFaction != spectatorFaction && myFaction.IsPlayer)
            map = g.Maps.Find(m => m.ParentFaction == myFaction && !m.IsPocketMap) ?? g.Maps.Find(m => m.ParentFaction == myFaction);
        if (map == null) return;

        g.CurrentMap = map;
    }

    // A new session: nothing from the last one may survive (Multiplayer.StopMultiplayer)
    internal static void Clear()
    {
        depth = 0;
        game = null;
        viewedMap = null;
        pinnedMap = null;
        mapLevel = false;
        outerPins.Clear();
        outerMapLevels.Clear();
        viewScope = 0;
        viewSwitchPending = null;
        viewToRestoreId = -1;
        noViewStretch = false;
        noViewCount = 0;
    }

    // The first player colony in Find.Maps, else the first map, else null. Find.Maps comes from the save every client
    // loaded and changes only inside synced simulation, so every client picks the same map. Never null while maps exist,
    // as vanilla expects: Game.LoadGame and Game.DeinitAndRemoveMap fall back to maps[0].
    public static Map ChooseMap(List<Map> maps, Map exclude)
    {
        for (int i = 0; i < maps.Count; i++)
            if (maps[i] != exclude && IsPlayerColony(maps[i]))
                return maps[i];

        for (int i = 0; i < maps.Count; i++)
            if (maps[i] != exclude)
                return maps[i];

        return null;
    }

    // Not Map.IsPlayerHome: that reads Faction.OfPlayer (the spectator in multifaction world ticks, each player's own
    // faction outside them) and searches the map for grav engines.
    private static bool IsPlayerColony(Map map)
    {
        var parent = map?.info?.parent;
        return parent?.def != null && parent.def.canBePlayerHome && parent.Faction is { IsPlayer: true };
    }

    // The simulation set Game.CurrentMap inside a pinned scope (a world object entering a map, a camera jump). The setter
    // sees this client's own view, so the switch, its UI notifications and VTRSync's command come out as without the
    // pin, and the new map is the view restored at the end. The pin does not follow the setter: issuer-only code calls
    // it too (FactionCreator, camera jumps), which would split the pin between clients.
    internal static void BeforeCurrentMapSet(Game g)
    {
        if (g == game)
            g.currentMapIndex = IndexIn(g, viewedMap);
    }

    internal static void AfterCurrentMapSet(Game g)
    {
        if (g != game) return;
        viewedMap = MapAt(g, g.currentMapIndex);
        // Inside a view scope (a camera jump) the rest of the scope keeps the new view; HideView restores the pin
        g.currentMapIndex = IndexIn(g, viewScope > 0 ? viewedMap : pinnedMap);
    }

    // Game.DeinitAndRemoveMap treats the current map as the viewed one: removing it switches to maps[0] and the world
    // view. Move every pin off a map being removed first (also a map removing itself in its own tick), so vanilla only
    // re-indexes. A removed viewed map is Unpin's.
    internal static void BeforeMapRemoved(Game g, Map map)
    {
        if (g != game || map == null) return;

        var replacement = ChooseMap(g.Maps, map);
        for (int i = 0; i < outerPins.Count; i++)
            if (outerPins[i] == map)
                outerPins[i] = replacement;

        if (map != pinnedMap) return;
        pinnedMap = replacement;
        g.currentMapIndex = IndexIn(g, pinnedMap);
    }

    internal static void AfterMapRemoved(Game g)
    {
        if (g == game)
            g.currentMapIndex = IndexIn(g, pinnedMap);
    }

    // Presentation inside a pinned scope runs with this client's view as the current map (WorldCurrentMapPinViewPatch)
    internal static bool ShowView()
    {
        if (depth == 0 || game == null || game != Current.Game) return false;
        if (viewScope++ == 0)
            game.currentMapIndex = IndexIn(game, viewedMap);
        return true;
    }

    internal static void HideView()
    {
        if (viewScope == 0 || --viewScope > 0) return;
        if (game != null && game == Current.Game)
            game.currentMapIndex = IndexIn(game, pinnedMap);
    }

    private static Map MapAt(Game g, int index) => index >= 0 && index < g.Maps.Count ? g.Maps[index] : null;

    private static sbyte IndexIn(Game g, Map map)
    {
        int index = map == null ? -1 : g.Maps.IndexOf(map);
        return (sbyte)(index >= 0 ? index : -1);
    }
}

[HarmonyPatch(typeof(Game), nameof(Game.CurrentMap), MethodType.Setter)]
static class WorldCurrentMapPinSetterPatch
{
    // Before VTRSync's MapSwitchPatch, which takes the previous map from Game.currentMapIndex
    [HarmonyPriority(MpPriority.MpFirst)]
    static void Prefix(Game __instance)
    {
        if (WorldCurrentMapPin.Active)
            WorldCurrentMapPin.BeforeCurrentMapSet(__instance);
    }

    [HarmonyPriority(MpPriority.MpLast)]
    static void Finalizer(Game __instance)
    {
        if (WorldCurrentMapPin.Active)
            WorldCurrentMapPin.AfterCurrentMapSet(__instance);
    }
}

[HarmonyPatch(typeof(Game), nameof(Game.DeinitAndRemoveMap))]
static class WorldCurrentMapPinRemoveMapPatch
{
    [HarmonyPriority(MpPriority.MpFirst)]
    static void Prefix(Game __instance, Map map)
    {
        if (WorldCurrentMapPin.Active)
            WorldCurrentMapPin.BeforeMapRemoved(__instance, map);
    }

    [HarmonyPriority(MpPriority.MpLast)]
    static void Finalizer(Game __instance)
    {
        if (WorldCurrentMapPin.Active)
            WorldCurrentMapPin.AfterMapRemoved(__instance);
    }
}

// Presentation that compares with Find.CurrentMap runs, inside a pinned scope, with this client's view, as without the pin:
// the sounds' map checks (PlayOneShotOnCamera(map) gates on the map; PlayOneShot reaches SoundDefHelper.CorrectContextNow;
// SubSoundDef.TryPlay reads the map's sound parameters; Multiplayer isolates their Rand: SubSoundDef.TryPlay, Sample) and
// camera jumps / selection by simulation code (a quest's camera jump moves each viewer's own camera, review 19 Oct).
[HarmonyPatch]
static class WorldCurrentMapPinViewPatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(SoundDefHelper), nameof(SoundDefHelper.CorrectContextNow));
        yield return AccessTools.Method(typeof(SoundStarter), nameof(SoundStarter.PlayOneShotOnCamera));
        yield return AccessTools.Method(typeof(SubSoundDef), nameof(SubSoundDef.TryPlay));
        yield return AccessTools.Method(typeof(CameraJumper), "TryJumpInternal", new[] { typeof(IntVec3), typeof(Map), typeof(CameraJumper.MovementMode) });
        yield return AccessTools.Method(typeof(CameraJumper), "TrySelectInternal", new[] { typeof(Thing) });
    }

    static void Prefix(ref bool __state)
    {
        __state = WorldCurrentMapPin.ShowView();
    }

    static void Finalizer(bool __state)
    {
        if (__state)
            WorldCurrentMapPin.HideView();
    }
}

// Long events the simulation queued (MarkLongEvents) run after the tick, outside any context: caravans entering a map,
// map generation for sites. Synchronous ones run here on the main thread. Multiplayer's own events (join points, saves,
// reloads) keep the real view: a save records Game.currentMapIndex.
[HarmonyPatch(typeof(LongEventHandler), nameof(LongEventHandler.UpdateCurrentSynchronousEvent))]
static class WorldCurrentMapPinLongEventPatch
{
    static void Prefix(ref bool __state)
    {
        var ev = LongEventHandler.currentEvent;
        if (Multiplayer.Client == null || ev?.eventAction == null || ev.ShouldWaitUntilDisplayed) return;
        if (!MarkLongEvents.IsTickMarked(ev.eventAction)) return;

        var key = ev.eventTextKey;
        if (key != null && (key.StartsWith("Mp", StringComparison.Ordinal) || key == "Saving")) return;

        WorldCurrentMapPin.Pin();
        __state = true;
    }

    static void Finalizer(bool __state)
    {
        if (!__state) return;
        WorldCurrentMapPin.Unpin();
        WorldCurrentMapPin.FlushViewSwitch();
    }
}
