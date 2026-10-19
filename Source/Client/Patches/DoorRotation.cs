using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Client.Patches
{
    // 1.7 lab, FIXES row 128: Building_Door.DoorPreDraw (called when a door is drawn: Building_Door / JammedDoor / MultiTileDoor /
    // SupportedDoor DrawAt) sets the 1x1 door's Rotation to fit the walls around it. Rotation is simulation state and is saved, but only
    // a PC that draws the door runs it: the windowed host turned its doors, the headless arbiter never did, and every PC reloads its own
    // save at a join point (lab mp92, join point 21,687: three colony doors with rot 1 on the host, 0 on the arbiter). In a session the
    // draw no longer writes it; the door's own tick sets it instead, from the same map on every PC at the same tick.
    [HarmonyPatch(typeof(Building_Door), "DoorPreDraw")]
    static class DoorPreDrawRotation
    {
        static bool Prefix() => Multiplayer.Client == null;
    }

    [HarmonyPatch(typeof(Building_Door), "Tick")]
    static class DoorTickRotation
    {
        static void Postfix(Building_Door __instance)
        {
            if (Multiplayer.Client == null || !__instance.Spawned || __instance.def.size != IntVec2.One) return;
            var rot = DoorUtility.DoorRotationAt(__instance.Position, __instance.Map, __instance.def.building.preferConnectingToFences);
            if (__instance.Rotation != rot)
                __instance.Rotation = rot;
        }
    }
}
