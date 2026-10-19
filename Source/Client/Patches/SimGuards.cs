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

        // C9: a map tick sees the ticking map as Find.CurrentMap (dev switch for A/B in the lab)
        [TweakValue("Multiplayer")] public static bool pinMapInMapTicks = true;

        // viewed = the map this PC looked at when the context began, kept as the object: a map tick can remove a map (pocket map,
        // gravship, caravan leaving), which shifts every index, so an index saved here could name another map afterwards (review 19 Oct)
        struct Saved { public UnityEngine.Random.State unity; public Map viewed; public bool pinned; public int salt; }
        static readonly Stack<Saved> stack = new();
        static int systemRandomSalt;

        // End of AsyncTimeComp.PreContext (map tick or map command) and AsyncWorldTimeComp.PreContext, after the synced Rand state
        // is in place. Called on every PC at the same point of the simulation.
        public static void Enter(Map map)
        {
            var g = Current.Game;   // not the CurrentMap getter: it throws on a stale index
            Map viewedNow = g != null && g.currentMapIndex >= 0 && g.currentMapIndex < g.Maps.Count ? g.Maps[g.currentMapIndex] : null;
            var s = new Saved { unity = UnityEngine.Random.state, viewed = viewedNow, salt = systemRandomSalt };
            // C3: Unity's own generator runs from the synced state for this context, without advancing Verse.Rand; its previous
            // (per-PC) state comes back in Exit, so interface code keeps its own sequence.
            ulong st = Rand.StateCompressed;
            UnityEngine.Random.InitState(Gen.HashCombineInt((int)st, (int)(st >> 32)));
            systemRandomSalt = 0;
            // C9: in a map TICK (map commands are pinned by AsyncTimeComp.ExecuteCmd already) the ticking map is Find.CurrentMap
            if (map != null && pinMapInMapTicks && AsyncTimeComp.tickingMap == map && Current.Game != null && map.Index >= 0)
            {
                s.pinned = true;
                Current.Game.currentMapIndex = (sbyte)map.Index;   // the field, like ExecuteCmd: the setter notifies UI and VTRSync
            }
            stack.Push(s);
        }

        // Start of the matching PostContext (both run in a finally).
        public static void Exit()
        {
            if (stack.Count == 0) return;
            var s = stack.Pop();
            UnityEngine.Random.state = s.unity;
            systemRandomSalt = s.salt;   // a nested context (quest getters, world-tick incidents) must not reset the outer one's count
            if (s.pinned && Current.Game != null)
            {
                // back to the viewed map by identity; no viewed map (headless arbiter) = -1 as before; if the tick removed the
                // viewed map, keep what Game.DeinitAndRemoveMap chose (review 19 Oct: a map removing ITSELF in its tick still runs
                // vanilla's view switch under the pin on every PC - open, to be rebuilt on WorldCurrentMapPin's mechanism)
                if (s.viewed == null) Current.Game.currentMapIndex = -1;
                else
                {
                    int i = Find.Maps.IndexOf(s.viewed);
                    if (i >= 0) Current.Game.currentMapIndex = (sbyte)i;
                    else if (Current.Game.currentMapIndex >= Find.Maps.Count) Current.Game.currentMapIndex = (sbyte)(Find.Maps.Count - 1);
                }
            }
        }

        // True inside a pinned map tick of a map this PC is not looking at: presentation (sounds, camera shake) is skipped there,
        // as it was when Find.CurrentMap was the viewed map.
        public static bool TickingAwayFromView
        {
            get
            {
                if (stack.Count == 0 || !stack.Peek().pinned) return false;
                return Current.Game == null || stack.Peek().viewed != Current.Game.CurrentMap;
            }
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

    // C9 presentation: with the ticking map pinned, one-shot sounds and camera shake of a map this PC is not viewing are skipped,
    // as before the pin (they are presentation only; Multiplayer already isolates the Rand of sound methods).
    [HarmonyPatch]
    static class SimGuardPresentationAway
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(SoundStarter), nameof(SoundStarter.PlayOneShot));
            yield return AccessTools.Method(typeof(SoundStarter), nameof(SoundStarter.PlayOneShotOnCamera));   // compares onlyThisMap with Find.CurrentMap
            yield return AccessTools.Method(typeof(CameraShaker), nameof(CameraShaker.DoShake), new[] { typeof(float) });
            yield return AccessTools.Method(typeof(CameraShaker), nameof(CameraShaker.DoShake), new[] { typeof(float), typeof(int) });
        }

        static bool Prefix() => !SimGuards.TickingAwayFromView;
    }
}
