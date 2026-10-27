using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Client.Patches
{
    // 1.7 lab, FIXES row 255: PawnFlyer.RecomputePosition sets the flyer's Position (simulation state, saved) and runs only from DrawPos
    // and DynamicDrawPhaseAt, i.e. on a PC that draws the flyer's map. Lab c8soak2_1, MP 82,900: Melee Animation's grapple flyer on the
    // host's map stood on (83, 0, 123) on the host and on its spawn cell (90, 0, 129) on the client, which watched its own map. Same class
    // as row 128 (DoorRotation.cs). In a session every PC recomputes in the flyer's tick, spawn and gravship move; the draw then finds this
    // tick's position already computed (positionLastComputedTick) and writes nothing new.
    static class PawnFlyerPosition
    {
        static readonly Action<PawnFlyer> Recompute =
            AccessTools.MethodDelegate<Action<PawnFlyer>>(AccessTools.Method(typeof(PawnFlyer), "RecomputePosition"));

        internal static void Apply(PawnFlyer flyer)
        {
            if (Multiplayer.Client == null || flyer.Destroyed || !flyer.Spawned) return;
            if (flyer.GetDirectlyHeldThings().Count == 0) return;   // DestinationPos reads the flying thing
            Recompute(flyer);
        }
    }

    [HarmonyPatch(typeof(PawnFlyer), "TickInterval")]
    static class PawnFlyerTickPosition
    {
        static void Postfix(PawnFlyer __instance) => PawnFlyerPosition.Apply(__instance);
    }

    [HarmonyPatch(typeof(PawnFlyer), nameof(PawnFlyer.SpawnSetup))]
    static class PawnFlyerSpawnPosition
    {
        static void Postfix(PawnFlyer __instance) => PawnFlyerPosition.Apply(__instance);
    }

    [HarmonyPatch(typeof(PawnFlyer), nameof(PawnFlyer.Notify_TransportedOnGravship))]
    static class PawnFlyerGravshipPosition
    {
        static void Postfix(PawnFlyer __instance) => PawnFlyerPosition.Apply(__instance);
    }
}
