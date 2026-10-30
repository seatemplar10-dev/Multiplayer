using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Multiplayer.Client.Util;
using RimWorld.QuestGen;
using Verse;
using Verse.Grammar;

namespace Multiplayer.Client
{
    [HarmonyPatch(typeof(Game))]
    [HarmonyPatch(nameof(Game.LoadGame))]
    public static class SeedGameLoad
    {
        static void Prefix()
        {
            Rand.PushState();

            if (Multiplayer.Client == null) return;
            Rand.Seed = 1;
        }

        static void Postfix()
        {
            Rand.PopState();
        }
    }

    [HarmonyPatch(typeof(Map))]
    [HarmonyPatch(nameof(Map.ExposeData))]
    public static class SeedMapLoad
    {
        static void Prefix(Map __instance, ref bool __state)
        {
            if (Multiplayer.Client == null) return;
            if (Scribe.mode != LoadSaveMode.LoadingVars && Scribe.mode != LoadSaveMode.ResolvingCrossRefs && Scribe.mode != LoadSaveMode.PostLoadInit) return;

            int seed = Gen.HashCombineInt(__instance.uniqueID, Find.World.ConstantRandSeed);
            Rand.PushState(seed);

            __state = true;
        }

        static void Postfix(Map __instance, bool __state)
        {
            if (__state)
                Rand.PopState();
        }
    }

    [HarmonyPatch(typeof(Map))]
    [HarmonyPatch(nameof(Map.FinalizeLoading))]
    public static class SeedMapFinalizeLoading
    {
        static void Prefix(Map __instance, ref bool __state)
        {
            if (Multiplayer.Client == null) return;

            int seed = Gen.HashCombineInt(__instance.uniqueID, Find.World.ConstantRandSeed);
            Rand.PushState(seed);

            __state = true;
        }

        static void Postfix(Map __instance, bool __state)
        {
            if (__state)
                Rand.PopState();
        }
    }

    // FIXES row 241: RoomTempTracker.RegenerateEqualizeCells ends with equalizeCells.Shuffle() (Verse.Rand), and every interval
    // samples 20% of that list in cycle order, so a different shuffle order is a different room temperature (1/16 C apart on
    // the two PCs ~300 ticks after a join). Regenerates run in any context (load, join, room or roof change). Seed the shuffle
    // from the room's shape only (map id and smallest cell index; never room.ID), and sort the cells first because the
    // pre-shuffle order follows Room.Cells (district and region order), which is history, not shape. The outer Rand stream is
    // left alone. cycleIndex is deliberately not reset: a regenerate that runs on one PC only (outside a tick) would then restart
    // that PC's cycle while the other keeps its position. Unsaved, it is 0 on both PCs after the join point's reload.
    // Lab probe: -mplabequalize logs one line per regenerate (default off).
    [HarmonyPatch(typeof(RoomTempTracker), "RegenerateEqualizeCells")]
    static class SeedRoomTempEqualizeCells
    {
        public const string ProbeArg = "mplabequalize";
        static readonly bool Probe = GenCommandLine.CommandLineArgPassed(ProbeArg);

        static void Prefix(Room ___room, Map map, ref bool __state)
        {
            if (Multiplayer.Client == null) return;

            int minCell = int.MaxValue;
            foreach (var cell in ___room.Cells)
                minCell = Math.Min(minCell, map.cellIndices.CellToIndex(cell));

            // Not room.CellCount: it is a lazy cache that Room.AddDistrict does not reset, so it can be stale on one PC only
            Rand.PushState(Gen.HashCombineInt(map.uniqueID, minCell));
            __state = true;
        }

        // Finalizer, not Postfix: the PopState must run even if the original threw
        static void Finalizer(Room ___room, Map map, List<IntVec3> ___equalizeCells, bool __state)
        {
            if (!__state) return;

            try
            {
                // The vanilla shuffle already ran inside the pushed state and drew the same number of values on every PC;
                // sort to a canonical order and shuffle again, so the result no longer depends on the incoming order
                ___equalizeCells.Sort((a, b) => map.cellIndices.CellToIndex(a).CompareTo(map.cellIndices.CellToIndex(b)));
                ___equalizeCells.Shuffle();

                if (Probe)
                    LogProbe(___room, map, ___equalizeCells);
            }
            finally
            {
                Rand.PopState();
            }
        }

        static void LogProbe(Room room, Map map, List<IntVec3> cells)
        {
            int minCell = int.MaxValue;
            foreach (var cell in room.Cells)
                minCell = Math.Min(minCell, map.cellIndices.CellToIndex(cell));

            int hash = cells.Count;
            foreach (var cell in cells)
                hash = Gen.HashCombineInt(hash, map.cellIndices.CellToIndex(cell));

            Log.Message($"Multiplayer {ProbeArg}: mpTick={TickPatch.Timer} mapTick={map.AsyncTime()?.mapTicks} map={map.uniqueID} roomMinCell={minCell} equalizeCells={cells.Count} orderHash={hash} ticking={Multiplayer.Ticking} inInterface={Multiplayer.InInterface}");
        }
    }

    [HarmonyPatch(typeof(CaravanEnterMapUtility), nameof(CaravanEnterMapUtility.Enter), typeof(Caravan), typeof(Map), typeof(Func<Pawn, IntVec3>), typeof(CaravanDropInventoryMode), typeof(bool))]
    static class SeedCaravanEnter
    {
        static void Prefix(Map map, ref bool __state)
        {
            if (Multiplayer.Client == null) return;

            int seed = Gen.HashCombineInt(map.uniqueID, Find.World.ConstantRandSeed);
            Rand.PushState(seed);

            __state = true;
        }

        static void Postfix(Map map, bool __state)
        {
            if (__state)
                Rand.PopState();
        }
    }

    [HarmonyPatch(typeof(LongEventHandler), nameof(LongEventHandler.QueueLongEvent), typeof(Action), typeof(string), typeof(bool), typeof(Action<Exception>), typeof(bool), typeof(bool), typeof(Action))]
    static class SeedLongEvents
    {
        static void Prefix(ref Action action, ref Action callback)
        {
            if (Multiplayer.Client != null && (Multiplayer.Ticking || Multiplayer.ExecutingCmds))
            {
                var seed = Rand.Int;
                action = (() => Rand.PushState(seed)) + action + Rand.PopState;

                if (callback != null)
                {
                    var callbackSeed = Rand.Int;
                    callback = (() => Rand.PushState(callbackSeed)) + callback + Rand.PopState;
                }
            }
        }
    }

    // Seed the rotation random
    [HarmonyPatch(typeof(GenSpawn), nameof(GenSpawn.Spawn), typeof(Thing), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(WipeMode), typeof(bool), typeof(bool))]
    static class GenSpawnRotatePatch
    {
        static MethodInfo Rot4GetRandom = AccessTools.Property(typeof(Rot4), nameof(Rot4.Random)).GetGetMethod();

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
        {
            foreach (CodeInstruction inst in insts)
            {
                if (inst.operand as MethodInfo == Rot4GetRandom)
                {
                    // Load newThing.thingIdNumber to the stack
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Thing), nameof(Thing.thingIDNumber)));
                    // Load Find.World.ConstantRandSeed to the stack
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.PropertyGetter(typeof(Find), nameof(Find.World)));
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.PropertyGetter(typeof(World), nameof(World.ConstantRandSeed)));
                    // Pop our 2 values, call Gen.HashCombineInt with them, and push the outcome to the stack
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Gen), nameof(Gen.HashCombineInt), [typeof(int), typeof(int)]));
                    // Pop the value off the stack and call Rand.PushState with it as the argument
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Rand), nameof(Rand.PushState), [typeof(int)]));
                }

                yield return inst;

                if (inst.operand as MethodInfo == Rot4GetRandom)
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Rand), nameof(Rand.PopState)));
            }
        }
    }

    public static class RandPatches
    {
        [HarmonyPriority(MpPriority.MpFirst)]
        public static void Prefix(ref bool __state)
        {
            Rand.PushState();
            __state = true;
        }

        [HarmonyPriority(MpPriority.MpLast)]
        public static void Finalizer(bool __state)
        {
            if (__state)
                Rand.PopState();
        }
    }

    [HarmonyPatch]
    static class SeedGrammar
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(GrammarResolver), nameof(GrammarResolver.Resolve));
            yield return AccessTools.Method(typeof(PawnBioAndNameGenerator), nameof(PawnBioAndNameGenerator.GeneratePawnName));
            yield return AccessTools.Method(typeof(NameGenerator), nameof(NameGenerator.GenerateName), [typeof(RulePackDef), typeof(Predicate<string>), typeof(bool), typeof(string), typeof(string), typeof(List<Rule>)]);
            // The GrammarRequest overload holds the validator retry loop and is called directly
            // (bypassing the RulePackDef overload above) by quest naming among others; a validator
            // rejection count depends on the generated text and therefore on the client's language.
            yield return AccessTools.Method(typeof(NameGenerator), nameof(NameGenerator.GenerateName), [typeof(GrammarRequest), typeof(Predicate<string>), typeof(bool), typeof(string), typeof(string)]);
            // Quest naming additionally retries whole NameGenerator calls while the name collides
            // with an existing quest's name — wrap the outer loop too so quest naming costs exactly
            // one synced draw regardless of language or collisions.
            yield return AccessTools.Method(typeof(QuestNode_ResolveQuestName), nameof(QuestNode_ResolveQuestName.Resolve));
        }

        [HarmonyPriority(MpPriority.MpFirst)]
        static void Prefix(ref bool __state)
        {
            Rand.Element(0, 0); // advance the rng
            Rand.PushState();
            __state = true;
        }

        [HarmonyPriority(MpPriority.MpLast)]
        static void Postfix(bool __state)
        {
            if (__state)
                Rand.PopState();
        }
    }

    [HarmonyPatch]
    static class SeedPawnGraphics
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return MpMethodUtil.GetLambda(typeof(PawnRenderer), nameof(PawnRenderer.SetAllGraphicsDirty), 0);
        }

        [HarmonyPriority(MpPriority.MpFirst)]
        static void Prefix(PawnRenderer __instance, ref bool __state)
        {
            Rand.PushState(Gen.HashCombineInt(__instance.pawn.thingIDNumber, Find.World.ConstantRandSeed));
            __state = true;
        }

        [HarmonyPriority(MpPriority.MpLast)]
        static void Postfix(bool __state)
        {
            if (__state)
                Rand.PopState();
        }
    }

    [HarmonyPatch(typeof(PreceptComp_UnwillingToDo_Chance), nameof(PreceptComp_UnwillingToDo_Chance.MemberWillingToDo))]
    static class SeedPreceptComp_UnwillingToDo_Chance
    {
        public static void Prefix(ref bool __state, HistoryEvent ev)
        {
            if (Multiplayer.Client == null) return;

            // PreceptComp_UnwillingToDo_Chance.MemberWillingToDo uses RNG, and can be called in interface (or other places that cause issues).
            // Seed the RNG using the pawn's ID (or the world's constant rand seed as fallback) and the current tick. This will ensure we
            // get a unique result for each pawn on a given tick, but if called multiple times on the same time it'll have a consistent result.
            // The fallback is mostly unnecessary, as this precept comp expects a doer. However, we may as well include it as a precaution.
            // We could add some more parameters to the seed, like using the map's ID or the world's seed, but it's probably an overkill.
            // This could probably be handled in a smarter way, so if anyone has an idea on how and is willing to do it - go ahead and replace this.
            var pawnId = ev.args.TryGetArg<Pawn>(HistoryEventArgsNames.Doer, out var pawn) ? pawn.thingIDNumber : Find.World.ConstantRandSeed;

            Rand.PushState(Gen.HashCombineInt(pawnId, Find.TickManager.TicksGame));
            __state = true;
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
                Rand.PopState();
        }
    }

    [HarmonyPatch(typeof(RitualBehaviorWorker_GravshipLaunch), nameof(RitualBehaviorWorker_GravshipLaunch.CanReachGravship))]
    static class SeedGravshipCanReachGravship
    {
        static void Prefix(ref bool __state)
        {
            if (Multiplayer.Client == null) return;

            // The gravship launch confirmation dialog works out which pawns can board while it is being
            // built: Dialog_BeginRitual's constructor calls CreateRitualRoleAssignments ->
            // RitualRoleAssignments.PawnNotAssignableReason -> RitualBehaviorWorker_GravshipLaunch
            // .PawnCanFillRole -> CanReachGravship -> GravshipUtility.TryFindSpotOnGravship ->
            // Region.RandomCell, which consumes RNG. Building that dialog is UI work and, because the
            // dialog is opened only by the issuing peer (see CancelDialogBeginRitual's
            // currentExecutingCmdIssuedBySelf gate), it runs on just one peer - so this RNG must not
            // advance the shared stream. Spamming the launch button repeats it and the divergence
            // accumulates into a desync ("Random state from commands doesn't match").
            // CanReachGravship returns a bool that does not depend on the RNG (it only searches for any
            // allowed cell), and the real boarding calls TryFindSpotOnGravship outside this check, so
            // isolating the RNG here leaves both the check result and determinism intact.
            Rand.PushState();
            __state = true;
        }

        static void Finalizer(bool __state)
        {
            if (__state)
                Rand.PopState();
        }
    }

}
