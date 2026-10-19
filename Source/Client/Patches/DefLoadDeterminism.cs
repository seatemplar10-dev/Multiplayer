using System;
using HarmonyLib;
using Verse;

namespace Multiplayer.Client.Patches
{
    // 1.7 lab (FIXES row 96, class C11 "defs randomized at game start"): some mods draw Verse.Rand while defs RESOLVE at game start,
    // unseeded, and bake the result into the def - the Warhammer 40k core rolls each backstory's forcedTraitsChance into
    // forcedTraits (AdeptusMechanicus.BackstoryDef.ResolveReferences), HAR picks a random bodyTypeGlobal
    // (AlienRace.AlienBackstoryDef.ResolveReferences). Every game process - host, arbiter, each joiner - then has different defs.
    // Multiplayer only compares def names at join, so nothing notices; pawns get different traits / body types per PC.
    //
    // Guard: each def type's reference resolution (PlayDataLoader's sequential loop over Def subclasses,
    // GenGeneric.InvokeStaticMethodOnGenericType(DefDatabase<>, type, "ResolveAllReferences")) runs with Rand seeded from the type's
    // full name, so a draw in one type never shifts the next. Same mod list = same defs on every PC. Applies whenever this mod is
    // loaded (defs exist before any session); in single player it only makes those rolls repeat. NOT covered: ThingDef (resolved by
    // a direct call), ThingCategoryDef / RecipeDef (resolved in parallel), XML-time PostLoad. A wrap of PlayDataLoader.DoPlayLoad
    // would be too late: mod constructors (where this patch is applied) run inside it.
    [EarlyPatch]
    [HarmonyPatch(typeof(GenGeneric), nameof(GenGeneric.InvokeStaticMethodOnGenericType),
        typeof(Type), typeof(Type), typeof(string), typeof(object[]))]
    static class SeedDefTypeResolution
    {
        public const int Seed = 0x4D50_3137;   // "MP17"

        // __state = the Rand stack depth before our push, -1 = not pushed. A mod whose ResolveReferences pops one state too many
        // would otherwise pop ours, and our pop would then throw on an empty stack and abort the whole def load (review 19 Oct).
        static void Prefix(Type genericBase, Type genericParam, string methodName, ref int __state)
        {
            __state = -1;
            if (genericBase != typeof(DefDatabase<>) || methodName != "ResolveAllReferences" || genericParam == null)
                return;
            __state = Rand.stateStack.Count;
            Rand.PushState(Gen.HashCombineInt(Seed, GenText.StableStringHash(genericParam.FullName)));
        }

        static void Finalizer(int __state)
        {
            if (__state >= 0 && Rand.stateStack.Count > __state)
                Rand.PopState();
        }
    }
}
