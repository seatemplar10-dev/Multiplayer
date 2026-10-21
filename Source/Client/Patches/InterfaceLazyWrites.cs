// 1.7 lab (FIXES row 169, lane d171_multifaction, run FA3): vanilla GETTERS that store their answer, called from UI code on the windowed PCs
// Class: a vanilla GETTER that stores its answer, called from UI code on the windowed PCs only (nothing on the headless arbiter).
//   Pawn_FoodRestrictionTracker.CurrentFoodPolicy  (Pawn_FoodRestrictionTracker.cs:21-24)  curPolicy = Current.Game.foodRestrictionDatabase.DefaultFoodRestriction()
//   Pawn_DrugPolicyTracker.CurrentPolicy           (Pawn_DrugPolicyTracker.cs:26-29)       same shape
//   Pawn_OutfitTracker.CurrentApparelPolicy        (Pawn_OutfitTracker.cs:21-24)           same shape
//   Pawn_ReadingTracker.CurrentPolicy              (Pawn_ReadingTracker.cs:19-22)          same shape
//   ResearchManager.GetProgress                    (ResearchManager.cs:190-211)            progress.Add(proj, 0f) / anomalyKnowledge.Add(proj, 0f)
// Only the food policy and the knowledge entry were seen in FA3; the other three are the same code shape.
// Rule: inside the interface (Multiplayer.InInterface: no tick, no command, no long event) answer exactly what vanilla would answer
// but store nothing. Simulation and commands keep the vanilla behaviour (they run the same on every PC).
using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Client.Patches;

[HarmonyPatch(typeof(Pawn_FoodRestrictionTracker), nameof(Pawn_FoodRestrictionTracker.CurrentFoodPolicy), MethodType.Getter)]
static class FoodPolicyGetterNoStoreInInterface
{
    static bool Prefix(Pawn_FoodRestrictionTracker __instance, FoodPolicy ___curPolicy, ref FoodPolicy __result)
    {
        if (!Multiplayer.InInterface || ___curPolicy != null || __instance.pawn.IsMutant) return true;
        var all = Current.Game.foodRestrictionDatabase.AllFoodRestrictions;
        if (all.Count == 0) return true; // vanilla would MakeNewFoodRestriction() (takes a thing id): not our call to skip
        __result = all[0];
        return false;
    }
}

[HarmonyPatch(typeof(Pawn_DrugPolicyTracker), nameof(Pawn_DrugPolicyTracker.CurrentPolicy), MethodType.Getter)]
static class DrugPolicyGetterNoStoreInInterface
{
    static bool Prefix(Pawn_DrugPolicyTracker __instance, DrugPolicy ___curPolicy, ref DrugPolicy __result)
    {
        if (!Multiplayer.InInterface || ___curPolicy != null || __instance.pawn.IsMutant) return true;
        var all = Current.Game.drugPolicyDatabase.AllPolicies;
        if (all.Count == 0) return true;
        __result = all[0];
        return false;
    }
}

[HarmonyPatch(typeof(Pawn_OutfitTracker), nameof(Pawn_OutfitTracker.CurrentApparelPolicy), MethodType.Getter)]
static class ApparelPolicyGetterNoStoreInInterface
{
    static bool Prefix(Pawn_OutfitTracker __instance, ApparelPolicy ___curApparelPolicy, ref ApparelPolicy __result)
    {
        if (!Multiplayer.InInterface || ___curApparelPolicy != null || __instance.pawn.IsMutant) return true;
        var all = Current.Game.outfitDatabase.AllOutfits;
        if (all.Count == 0) return true;
        __result = all[0];
        return false;
    }
}

[HarmonyPatch(typeof(Pawn_ReadingTracker), nameof(Pawn_ReadingTracker.CurrentPolicy), MethodType.Getter)]
static class ReadingPolicyGetterNoStoreInInterface
{
    static bool Prefix(Pawn_ReadingTracker __instance, ReadingPolicy ___curPolicy, ref ReadingPolicy __result)
    {
        if (!Multiplayer.InInterface || ___curPolicy != null || __instance.pawn.IsMutant) return true;
        var all = Current.Game.readingPolicyDatabase.AllReadingPolicies;
        if (all.Count == 0) return true;
        __result = all[0];
        return false;
    }
}

// Vanilla ResearchManager.GetProgress (ResearchManager.cs:190-211) without the two Add(proj, 0f) calls. The stored 0 is read as absent
// everywhere (GetProgress/GetKnowledge return 0f; AddProgress, ResearchManager.cs:213-221, and ApplyKnowledge(project,..), :371-379,
// do TryGetValue-then-Add), so only the saved dictionary differs.
[HarmonyPatch(typeof(ResearchManager), nameof(ResearchManager.GetProgress))]
static class ResearchGetProgressNoStoreInInterface
{
    static bool Prefix(ResearchManager __instance, ResearchProjectDef proj, ref float __result)
    {
        if (!Multiplayer.InInterface) return true;
        if (proj.baseCost > 0f)
            __result = __instance.progress.TryGetValue(proj, out var p) ? p : 0f;
        else if (ModsConfig.AnomalyActive && proj.knowledgeCost > 0f)
            __result = __instance.anomalyKnowledge.TryGetValue(proj, out var k) ? k : 0f;
        else
            __result = 0f;
        return false;
    }
}
