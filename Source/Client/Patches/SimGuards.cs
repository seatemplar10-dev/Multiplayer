using System;
using HarmonyLib;
using UnityEngine;
using Verse;
using Random = UnityEngine.Random;

namespace Multiplayer.Client.Patches
{
    // 1.7 lab, determinism by class (FIXES row 87): per-PC inputs read INSIDE the simulation are answered from synced state, one
    // guard per class instead of one compat patch per mod. "In the simulation" = a map tick, the world tick, a command or the
    // constant ticker (Multiplayer.Ticking || Multiplayer.ExecutingCmds); interface code keeps the real per-PC answer.
    // Evidence: the lab sanitizer (-mplabsanitize) logs who reads these in sim; the static scan (tools/detscan) lists the rest.
    public static class SimGuards
    {
        public static bool InSim => Multiplayer.Client != null && (Multiplayer.Ticking || Multiplayer.ExecutingCmds);
    }

    // Class "view gate": code in a tick that spawns motes / flecks behind GenView.ShouldSpawnMotesAt and draws Rand for them
    // (VHGE gas sprayer, Blood Animations flecks, Fortifications burst flecks, Bezier projectiles ...) draws a different number
    // of times on each PC, because the gate reads the viewed map and the camera. In the simulation the gate answers from the
    // map alone (cell in bounds), like FixNullMotes does for MoteMaker.MakeStaticMote. Flecks for an unviewed map are made but
    // only drawn where that map is shown.
    [HarmonyPatch(typeof(GenView), nameof(GenView.ShouldSpawnMotesAt), typeof(IntVec3), typeof(Map), typeof(bool))]
    static class SimGuardMoteGateCell
    {
        static bool Prefix(IntVec3 loc, Map map, ref bool __result)
        {
            if (!SimGuards.InSim) return true;
            __result = map != null && loc.InBounds(map);
            return false;
        }
    }

    [HarmonyPatch(typeof(GenView), nameof(GenView.ShouldSpawnMotesAt), typeof(Vector3), typeof(Map), typeof(bool))]
    static class SimGuardMoteGateVector
    {
        static bool Prefix(Vector3 loc, Map map, ref bool __result)
        {
            if (!SimGuards.InSim) return true;
            __result = map != null && loc.ToIntVec3().InBounds(map);
            return false;
        }
    }

    // Class "frame time in a tick": a tick that advances by Time.deltaTime (Lightsaber thrown blades, row 47) moves by each
    // PC's frame time. In the simulation a frame is one tick at normal speed: 1/60 s.
    [HarmonyPatch]
    static class SimGuardFrameTime
    {
        static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime));
            yield return AccessTools.PropertyGetter(typeof(Time), nameof(Time.unscaledDeltaTime));
            yield return AccessTools.PropertyGetter(typeof(Time), nameof(Time.smoothDeltaTime));
        }

        static bool Prefix(ref float __result)
        {
            if (!SimGuards.InSim) return true;
            __result = 1f / 60f;
            return false;
        }
    }

    // Class "unseeded randomness in a tick": UnityEngine.Random in the simulation draws from Verse.Rand (the synced state of
    // the map / world being ticked) instead of each PC's own Unity generator. MP-Compat redirects some mods one by one; this
    // covers every caller. Interface code keeps Unity's generator.
    [HarmonyPatch(typeof(Random), nameof(Random.Range), typeof(float), typeof(float))]
    static class SimGuardUnityRangeFloat
    {
        static bool Prefix(float minInclusive, float maxInclusive, ref float __result)
        {
            if (!SimGuards.InSim) return true;
            __result = Rand.Range(minInclusive, maxInclusive);
            return false;
        }
    }

    [HarmonyPatch(typeof(Random), nameof(Random.Range), typeof(int), typeof(int))]
    static class SimGuardUnityRangeInt
    {
        static bool Prefix(int minInclusive, int maxExclusive, ref int __result)
        {
            if (!SimGuards.InSim) return true;
            __result = Rand.Range(minInclusive, maxExclusive);
            return false;
        }
    }

    [HarmonyPatch(typeof(Random), nameof(Random.value), MethodType.Getter)]
    static class SimGuardUnityValue
    {
        static bool Prefix(ref float __result)
        {
            if (!SimGuards.InSim) return true;
            __result = Rand.Value;
            return false;
        }
    }

    [HarmonyPatch(typeof(Random), nameof(Random.insideUnitCircle), MethodType.Getter)]
    static class SimGuardUnityInsideUnitCircle
    {
        static bool Prefix(ref Vector2 __result)
        {
            if (!SimGuards.InSim) return true;
            __result = Rand.InsideUnitCircle;
            return false;
        }
    }

    // "new System.Random()" in the simulation (unseeded: a different sequence on every PC) is re-seeded from Verse.Rand right
    // after construction: one synced draw per construction, then the mod's own sequence is the same everywhere.
    [HarmonyPatch(typeof(System.Random), MethodType.Constructor, new Type[0])]
    static class SimGuardSystemRandom
    {
        static readonly System.Reflection.ConstructorInfo seeded = AccessTools.Constructor(typeof(System.Random), new[] { typeof(int) });

        static void Postfix(System.Random __instance)
        {
            if (!SimGuards.InSim) return;
            seeded.Invoke(__instance, new object[] { Rand.Int });
        }
    }

    // Class "per-PC settings in a tick": Prefs.DevMode is each player's own option; in the simulation it answers with the
    // session's synced dev mode (MultiplayerGameComp.debugMode), so dev-only branches run on every PC or on none.
    [HarmonyPatch(typeof(Prefs), nameof(Prefs.DevMode), MethodType.Getter)]
    static class SimGuardDevMode
    {
        static bool Prefix(ref bool __result)
        {
            if (!SimGuards.InSim || Multiplayer.GameComp == null) return true;
            __result = Multiplayer.GameComp.debugMode;
            return false;
        }
    }
}
