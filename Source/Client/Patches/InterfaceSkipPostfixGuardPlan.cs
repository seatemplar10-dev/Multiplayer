using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Client.Patches
{
    // C13 (Ares FIXES row 114): which postfixes of other mods the interface-skip postfix guard gates, decided per patched method.
    // Pure decision code, no game or Harmony types: the in-game guard (InterfaceSkipPostfixGuard) and the offline lab inventory
    // tool both run exactly this.
    public static class C13Plan
    {
        public const string MpOwner = "multiplayer";
        public const string CompatOwner = "rimworld.multiplayer.compat";
        public const string GuardOwner = "multiplayer.c13";

        // A value-returning target is only gated through the prefix channel when Multiplayer's skip means "the call is deferred to a
        // synced command" (the result is then a placeholder), not "Multiplayer supplies the result". Reviewed per method.
        public static readonly HashSet<string> NonVoidPrefixSkipAllow = new()
        {
            "RimWorld.TradeDeal::TryExecute",   // TradeDealExecutePatch: in the MP trade window the deal runs as MpTradeSession.TryExecute (synced)
        };

        // Targets where Multiplayer's skip SUBSTITUTES the work rather than cancelling or deferring it, so other mods' postfixes keep
        // running. Reviewed per method.
        public static readonly Dictionary<string, string> PrefixSkipDeny = new()
        {
            // LoadedObjectsClearPatch keeps SharedCrossRefs (swaps the loader's directory instead of clearing); VEF's
            // AddHireablesToLoadedObjectDirectory then registers its hireables there, which later cross-references may need
            ["Verse.LoadedObjectDirectory::Clear"] = "Multiplayer substitutes the work (reviewed)",
        };

        // A mod that has a prefix AND a postfix on the same target may pair them through static state (BigAndSmall
        // WealthWatcher_ForceRecount_Patch: prefix sets raidWealthActive = true, postfix resets it); gating only the postfix would
        // leave that state stuck, so pairs are not gated unless reviewed (decompiled, 19 Oct) as safe:
        public static readonly HashSet<string> ReviewedPairs = new()
        {
            "VanillaTraitsExpanded.TryTakeOrderedJob_Patch",   // prefix only reads (HeavySleeper veto), no state
            "VEF.Factions.TradeDeal_TryExecute_Patch",         // prefix writes only its own __state; postfix returns early unless __result
        };

        public sealed class PatchEntry
        {
            public string kind;            // prefix, postfix, transpiler, finalizer
            public string owner;           // Harmony id
            public string name;            // "Ns.Type::Method" (logs)
            public string declaringType;   // full name of the patch class
            public bool returnsVoid;
            public bool returnsBool;
            public bool isGeneric;         // generic method or declared in a generic type: not patchable as-is
            public bool isSyncTemplate;    // Multiplayer's SyncTemplates transpiler (the method is a sync method)
            public object method;          // MethodInfo in game; null offline
        }

        public sealed class TargetEntry
        {
            public string name;            // "Ns.Type::Method"
            public bool returnsVoid;
            public bool syncIdKnown = true; // in game: Sync.methodBaseToInternalId has the method
            public List<PatchEntry> patches = new();
            public object original;        // MethodBase in game; null offline
        }

        public sealed class Decision
        {
            public TargetEntry target;
            public bool prefixChannel;     // a Multiplayer bool prefix may skip the original in interface code
            public bool syncChannel;       // Multiplayer's sync template may intercept the call in interface code
            public string targetReason;    // why nothing on this target is gated (null when the target is guarded)
            public List<PatchEntry> mpBoolPrefixes = new();
            public List<PatchEntry> gated = new();
            public List<(PatchEntry patch, string reason)> notGated = new();
            public int syncId = -1;
            public bool Guarded => gated.Count > 0;
        }

        public static bool IsMpFamily(string owner) => owner == MpOwner || owner == CompatOwner || owner == GuardOwner;

        // null = the target has no Multiplayer skip at all (not a C13 candidate)
        public static Decision Decide(TargetEntry t)
        {
            var mpBool = t.patches.Where(p => p.kind == "prefix" && p.owner == MpOwner && p.returnsBool).ToList();
            bool sync = t.patches.Any(p => p.kind == "transpiler" && p.owner == MpOwner && p.isSyncTemplate);
            if (mpBool.Count == 0 && !sync) return null;

            var foreignPostfixes = t.patches.Where(p => p.kind == "postfix" && !IsMpFamily(p.owner)).ToList();
            var d = new Decision { target = t, mpBoolPrefixes = mpBool };
            if (foreignPostfixes.Count == 0) { d.targetReason = "no foreign postfix"; return d; }

            PrefixSkipDeny.TryGetValue(t.name, out var deny);
            d.prefixChannel = mpBool.Count > 0 && deny == null && (t.returnsVoid || NonVoidPrefixSkipAllow.Contains(t.name));
            d.syncChannel = sync && t.syncIdKnown;
            if (!d.prefixChannel && !d.syncChannel)
            {
                d.targetReason = sync ? "sync method without an internal id" : deny ?? "returns a value (Multiplayer's prefix may supply the result)";
                foreach (var p in foreignPostfixes) d.notGated.Add((p, d.targetReason));
                return d;
            }

            foreach (var p in foreignPostfixes)
            {
                string reason = null;
                if (!p.returnsVoid) reason = "pass-through postfix (returns a value)";
                else if (p.isGeneric) reason = "generic patch method";
                else if (t.patches.Any(q => q.kind == "prefix" && q.owner == p.owner) && !ReviewedPairs.Contains(p.declaringType))
                    reason = "paired with a prefix of the same mod";
                if (reason != null) d.notGated.Add((p, reason));
                else d.gated.Add(p);
            }
            if (d.gated.Count == 0) d.targetReason = "every foreign postfix excluded";
            return d;
        }
    }
}
