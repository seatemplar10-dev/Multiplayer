using System;
using System.Collections.Generic;
using HarmonyLib;
using LudeonTK;
using Multiplayer.Client.AsyncTime;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace Multiplayer.Client.Patches
{
    // 1.7 lab, determinism by class (FIXES row 87): per-PC inputs read INSIDE the simulation are answered from synced state, one
    // guard per class instead of one compat patch per mod. Interface code keeps every real per-PC answer.
    // v2 after an adversarial review (lab swarm 18 Oct): no guard consumes synced Rand (a draw that only some PCs make would CREATE a
    // desync), commands keep Multiplayer's own per-issuer state, ConstantTicker's per-PC work is not "the simulation".
    // Evidence: the lab sanitizer (-mplabsanitize) logs who reads these in sim; the static scan (tools/detscan) lists the rest.
    public static class SimGuards
    {
        // map tick, world tick or command; NOT ConstantTicker (its TickNonSimulation is per PC by design)
        public static bool InSim => Multiplayer.Client != null && (AsyncWorldTimeComp.tickingWorld || AsyncTimeComp.tickingMap != null || Multiplayer.ExecutingCmds);
        public static bool InTick => InSim && !Multiplayer.ExecutingCmds;

        // C9: a map tick sees the ticking map as Find.CurrentMap (dev switch for A/B in the lab only: it is per PC, so switching it
        // on one PC of a session splits that session by design)
        [TweakValue("Multiplayer")] public static bool pinMapInMapTicks = true;
        static bool leakLogged;

        struct Saved { public UnityEngine.Random.State unity; public bool pinned; public int salt; }
        static readonly Stack<Saved> stack = new();
        static int systemRandomSalt;

        // End of AsyncTimeComp.PreContext (map tick or map command) and AsyncWorldTimeComp.PreContext, after the synced Rand state
        // is in place. Called on every PC at the same point of the simulation.
        public static void Enter(Map map)
        {
            var s = new Saved { unity = UnityEngine.Random.state, salt = systemRandomSalt };
            // C3: Unity's own generator runs from the synced state for this context, without advancing Verse.Rand; its previous
            // (per-PC) state comes back in Exit, so interface code keeps its own sequence.
            ulong st = Rand.StateCompressed;
            UnityEngine.Random.InitState(Gen.HashCombineInt((int)st, (int)(st >> 32)));
            systemRandomSalt = 0;
            // C9: in a map TICK (map commands are pinned by AsyncTimeComp.ExecuteCmd already) the ticking map is Find.CurrentMap,
            // through the world tick's pin (FIXES row 97): it keeps this PC's view, moves off a map that is being removed (also
            // the ticking map itself) and nests inside a world tick's pin (incidents on a map). Unpinned by the caller after
            // Rand.PopState, because restoring the view can run UI code.
            if (map != null && pinMapInMapTicks && AsyncTimeComp.tickingMap == map && Current.Game != null && map.Index >= 0)
            {
                s.pinned = true;
                WorldCurrentMapPin.PinMap(map);
            }
            stack.Push(s);
        }

        // Start of the matching PostContext. True = this context pinned its map: the caller unpins once its synced state is popped.
        public static bool Exit()
        {
            if (stack.Count == 0) return false;
            var s = stack.Pop();
            UnityEngine.Random.state = s.unity;
            systemRandomSalt = s.salt;   // a nested context (quest getters, world-tick incidents) must not reset the outer one's count
            return s.pinned;
        }

        // A context that opened and never closed (an exception where a context is closed in a Postfix, or between PreContext and
        // its try) would leave the guards and the C9 pin on for good: every later frame on the pinned map, later pins no-ops.
        // Called by TickPatch right before every command and every tick, a point every PC reaches with the same simulation
        // state, so the repair cannot split PCs (FIXES row 97, review 19 Oct). Nothing is open there in a healthy session.
        public static void ResetIfLeaked(string where)
        {
            if (stack.Count == 0 && !WorldCurrentMapPin.Active) return;
            if (!leakLogged)
            {
                leakLogged = true;
                Log.Error($"Multiplayer sim guards: {stack.Count} guard frame(s) and pin depth {WorldCurrentMapPin.Depth} still open before {where} - reset (logged once)");
            }
            Saved bottom = default;
            bool any = stack.Count > 0;
            while (stack.Count > 0) bottom = stack.Pop();
            if (any)
            {
                UnityEngine.Random.state = bottom.unity;
                systemRandomSalt = bottom.salt;
            }
            WorldCurrentMapPin.ForceReset();
        }

        // Multiplayer.StopMultiplayer: no guard frame or pin carries over to the next session
        public static void Clear()
        {
            stack.Clear();
            systemRandomSalt = 0;
            leakLogged = false;
            WorldCurrentMapPin.Clear();
        }

        public static int NextSystemRandomSeed()
        {
            ulong st = Rand.StateCompressed;   // read, never advanced
            return Gen.HashCombineInt(Gen.HashCombineInt((int)st, (int)(st >> 32)), ++systemRandomSalt);
        }
    }

    // C1 "view gate": code in a tick that spawns motes / flecks behind GenView.ShouldSpawnMotesAt and draws Rand for them draws a
    // different number of times on each PC, because the gate reads the viewed map and the camera. In the simulation the gate
    // answers from the map alone (cell in bounds), like FixNullMotes does for MoteMaker.MakeStaticMote.
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

    // C1 continued: the per-map mote counter differs per PC too (motes made from interface code count); in the simulation it is
    // never "saturated" (FixNullMotes does the same with a transpiler inside MakeStaticMote).
    [EarlyPatch]
    [HarmonyPatch]
    static class SimGuardMoteCounter
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.PropertyGetter(typeof(MoteCounter), nameof(MoteCounter.Saturated));
            yield return AccessTools.PropertyGetter(typeof(MoteCounter), nameof(MoteCounter.SaturatedLowPriority));
        }

        static bool Prefix(ref bool __result)
        {
            if (!SimGuards.InSim) return true;
            __result = false;
            return false;
        }
    }

    // C2 "frame time in a tick": a tick that advances by Time.deltaTime (Lightsaber thrown blades, row 47) moves by each PC's frame
    // time. In the simulation a frame is one tick at normal speed: 1/60 s. Only the DELTA getters: absolute clocks
    // (realtimeSinceStartup, time, frameCount) are read inside ticks by sounds, motes, letters and music only (lab sanitizer),
    // and faking them would break those; mod code that schedules by them stays a per-mod fix (detscan "realtime" leads).
    [EarlyPatch]
    [HarmonyPatch]
    static class SimGuardFrameTime
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
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

    // C3 "unseeded randomness in a tick": UnityEngine.Random is re-seeded per simulation context in SimGuards.Enter. An exact
    // "new System.Random()" in the simulation is re-seeded from the synced state WITHOUT advancing it (+ a per-context counter), so
    // its sequence is the same on every PC that makes it and nothing changes on a PC that does not. MP-Compat's RandRedirector
    // subclasses already draw from Verse.Rand and are left alone.
    [HarmonyPatch(typeof(System.Random), MethodType.Constructor, new Type[0])]
    static class SimGuardSystemRandom
    {
        static readonly System.Reflection.ConstructorInfo seeded = AccessTools.Constructor(typeof(System.Random), new[] { typeof(int) });

        static void Postfix(System.Random __instance)
        {
            if (!SimGuards.InSim || __instance.GetType() != typeof(System.Random)) return;
            seeded.Invoke(__instance, new object[] { SimGuards.NextSystemRandomSeed() });
        }
    }

    // C4 "per-PC settings in a tick": Prefs.DevMode is each player's own option; in a TICK it answers the session's synced debug
    // mode. Commands keep what Multiplayer sets for the issuing player (Prefs.data.devMode from canUseDevMode).
    [EarlyPatch]
    [HarmonyPatch(typeof(Prefs), nameof(Prefs.DevMode), MethodType.Getter)]
    static class SimGuardDevMode
    {
        static bool Prefix(ref bool __result)
        {
            if (!SimGuards.InTick || Multiplayer.game?.gameComp == null) return true;
            __result = Multiplayer.game.gameComp.debugMode;
            return false;
        }
    }

    // C9 presentation: camera shake has no map of its own (callers gate it on Find.CurrentMap), so inside a pin on a map this PC
    // is not viewing it is skipped, as before the pin. Sounds get this PC's view in their map checks (WorldCurrentMapPinViewPatch).
    [HarmonyPatch]
    static class SimGuardPresentationAway
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CameraShaker), nameof(CameraShaker.DoShake), new[] { typeof(float) });
            yield return AccessTools.Method(typeof(CameraShaker), nameof(CameraShaker.DoShake), new[] { typeof(float), typeof(int) });
        }

        static bool Prefix() => !WorldCurrentMapPin.PinnedAwayFromView;
    }
}
