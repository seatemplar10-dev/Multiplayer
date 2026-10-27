using System;
using System.Collections.Generic;
using System.Reflection;
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
    // FIXES row 256: a mod flyer class may declare its OWN RecomputePosition that sets Position from its own draw (TheForce's
    // TelekinesisPawnFlyer; Melee Animation's GrappleFlyer has one that sets no Position). The tick calls that one too, after vanilla's,
    // so the cell is the subclass's on every PC and the subclass's draw finds its own cache filled for this tick.
    static class PawnFlyerPosition
    {
        static readonly Action<PawnFlyer> Recompute =
            AccessTools.MethodDelegate<Action<PawnFlyer>>(AccessTools.Method(typeof(PawnFlyer), "RecomputePosition"));
        static readonly Dictionary<Type, MethodInfo> ownRecompute = new();

        internal static void Apply(PawnFlyer flyer)
        {
            if (Multiplayer.Client == null || flyer.Destroyed || !flyer.Spawned) return;
            if (flyer.GetDirectlyHeldThings().Count == 0) return;   // DestinationPos reads the flying thing
            Recompute(flyer);
            try { OwnRecompute(flyer.GetType())?.Invoke(flyer, null); }
            catch (Exception e) { Log.ErrorOnce($"Multiplayer: {flyer.GetType()}.RecomputePosition threw (FIXES row 256): {e.InnerException ?? e}", 2560256); }
        }

        static MethodInfo OwnRecompute(Type type)
        {
            if (ownRecompute.TryGetValue(type, out var m)) return m;
            for (var t = type; m == null && t != null && t != typeof(PawnFlyer); t = t.BaseType)
                m = AccessTools.DeclaredMethod(t, "RecomputePosition", Type.EmptyTypes);
            return ownRecompute[type] = m;
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
