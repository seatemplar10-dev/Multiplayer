using System;
using System.Collections.Generic;
using HarmonyLib;
using Multiplayer.Client.Patches;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Client.AsyncTime;

// Find.CurrentMap during world simulation. Unpinned it is the map this client happens to view: the host's colony, a
// joiner's other colony, or null on a headless client, so world code that reads it (storyteller, quests, game components,
// mods; Milira's GameComponentTick threw on the observer only) desyncs or fails on some clients only. World ticks, world
// commands and the long events the simulation queues therefore pin it to a map every client has.
// Map commands pin their own map (AsyncTimeComp.ExecuteCmd). Map ticks keep the viewed map on purpose: there,
// "map == Find.CurrentMap" picks per-player visuals (sky glow, weather, camera shake, sounds).
public static class WorldCurrentMapPin
{
    private static int depth;
    private static Game game;
    private static Map viewedMap;
    private static Map pinnedMap;

    public static bool Active => depth > 0;
    public static Map PinnedMap => pinnedMap;
    public static Map ViewedMap => viewedMap;

    // Writes Game.currentMapIndex directly, like AsyncTimeComp.ExecuteCmd: the Game.CurrentMap setter notifies the map UI
    // and ambient sound, and VTRSync's MapSwitchPatch would send a viewed-map command.
    public static void Pin()
    {
        if (depth > 0)
        {
            depth++;
            return;
        }

        var g = Current.Game;
        Map viewed = null, pinned = null;
        if (g != null)
        {
            viewed = MapAt(g, g.currentMapIndex);
            pinned = ChooseMap(g.Maps, null);
        }

        game = g;
        viewedMap = viewed;
        pinnedMap = pinned;
        depth = 1;

        if (g != null)
            g.currentMapIndex = IndexIn(g, pinned);
    }

    public static void Unpin()
    {
        if (depth == 0) return;
        if (--depth > 0) return;

        var g = game;
        var restore = viewedMap;
        game = null;
        viewedMap = null;
        pinnedMap = null;

        // A reload replaced the Game: the new one keeps its own current map
        if (g == null || g != Current.Game) return;

        if (restore == null || g.Maps.Contains(restore))
        {
            g.currentMapIndex = IndexIn(g, restore);
            return;
        }

        // The simulation removed the map this client was viewing: what Game.DeinitAndRemoveMap does for the viewer.
        // From "no map", so VTRSync does not count a leave from whichever map moved into the old index.
        g.currentMapIndex = -1;
        g.CurrentMap = g.Maps.Count > 0 ? g.Maps[0] : null;
        Find.World.renderer.wantedMode = WorldRenderMode.Planet;
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
        g.currentMapIndex = IndexIn(g, pinnedMap);
    }

    // Game.DeinitAndRemoveMap treats the current map as the viewed one: removing it switches to maps[0] and the world
    // view. Move the pin off a map being removed first, so vanilla only re-indexes it. A removed viewed map is Unpin's.
    internal static void BeforeMapRemoved(Game g, Map map)
    {
        if (g != game || map == null || map != pinnedMap) return;
        pinnedMap = ChooseMap(g.Maps, map);
        g.currentMapIndex = IndexIn(g, pinnedMap);
    }

    internal static void AfterMapRemoved(Game g)
    {
        if (g == game)
            g.currentMapIndex = IndexIn(g, pinnedMap);
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
        if (__state)
            WorldCurrentMapPin.Unpin();
    }
}
