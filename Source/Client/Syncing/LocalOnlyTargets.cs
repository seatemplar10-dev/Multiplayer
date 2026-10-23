using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Client.Factions;
using RimWorld;
using Verse;

namespace Multiplayer.Client
{
    // 1.7 lab (FIXES row 200, live session 22 Oct 19:35 / 19:43, James during his multifaction Settle): interface code on ONE PC called synced
    // methods on pawns that exist only on that PC - the starting-pawn page's pawns, a Character Editor pawn being built or randomised on that
    // page. The Thing writer cannot place such a pawn (not spawned, no holder: SyncDictRimWorld Thing writer, "is inaccessible"), so every PC
    // read the target as null and dropped the call (RJW HediffComp_SexPart.Init -> "SyncDictionary.Hediff: pawn is null"; VFE Empire
    // RemoveAllHonors(null) threw): the action never ran, not even on the issuer. Such a pawn's state reaches the game only later, inside
    // SendPawn's bytes, so while the multifaction pawn page is open the original runs on this PC instead, unsynced.
    // Narrow on purpose (fresh review, 22 Oct): only with that page open; only plain handlers (SyncMethod.PlainForLocalRun: no by-value
    // argument - SendPawn itself ships a page pawn -, no transformer, no pre/post hook, no cancel rule); only when the target's pawn and every
    // Pawn argument are outside the game (not spawned, no holder, not a world pawn) and every other argument is plain data.
    // Anything else keeps the old behaviour.
    public static class LocalOnlyTargets
    {
        static readonly HashSet<int> logged = new();
        static readonly Dictionary<Type, FieldInfo> pawnFields = new();

        public static bool ShouldRunLocally(int internalId, object instance, object[] args)
        {
            if (Find.WindowStack?.IsOpen(typeof(Page_ConfigureStartingPawns_Multifaction)) != true) return false;
            // SyncDelegate (a lambda's closure) is excluded: its captured fields and its own receive-side rules are not looked at here
            if (Sync.internalIdToSyncMethod[internalId] is not SyncMethod { PlainForLocalRun: true } handler || handler is SyncDelegate) return false;

            bool anyLocal = false;

            if (instance != null)
            {
                if (!IsLocalOnly(OwnerPawn(instance))) return false;
                anyLocal = true;
            }

            foreach (var arg in args)
            {
                if (arg == null || IsPlain(arg)) continue;
                if (arg is Pawn p && IsLocalOnly(p)) { anyLocal = true; continue; }
                return false;
            }

            if (anyLocal && logged.Add(internalId))
                Log.Message($"MP: {handler} called on a pawn that is not in the game (multifaction starting-pawn page): run on this PC only, not synced (row 200)");

            return anyLocal;
        }

        // The pawn a sync target belongs to: the pawn itself, its comp, or a part of the pawn with an instance Pawn "pawn" field - only types
        // where that field IS the owner (Pawn_* trackers, Hediff, Need, Gene, Ability, Trait, SkillRecord); in a job driver or gizmo "pawn" is
        // the actor, not the target, so those stay synced.
        static Pawn OwnerPawn(object o)
        {
            switch (o)
            {
                case Pawn p: return p;
                case HediffComp c: return c.parent?.pawn;
                case ThingComp tc: return tc.parent as Pawn;
            }

            var type = o.GetType();
            if (!pawnFields.TryGetValue(type, out var field))
            {
                field = IsPawnPart(type) ? AccessTools.Field(type, "pawn") : null;
                if (field != null && (field.IsStatic || field.FieldType != typeof(Pawn))) field = null;
                pawnFields[type] = field;
            }

            return field?.GetValue(o) as Pawn;
        }

        static bool IsPawnPart(Type type) =>
            type.Name.StartsWith("Pawn_") || typeof(Hediff).IsAssignableFrom(type) || typeof(Need).IsAssignableFrom(type)
            || typeof(Gene).IsAssignableFrom(type) || typeof(Ability).IsAssignableFrom(type) || typeof(Trait).IsAssignableFrom(type)
            || typeof(SkillRecord).IsAssignableFrom(type);

        static bool IsLocalOnly(Pawn p) =>
            p != null && !p.Spawned && p.holdingOwner == null && !Find.WorldPawns.Contains(p);

        // Data that is written by value, never a reference to shared game state (not every struct: LocalTargetInfo holds a Thing).
        static bool IsPlain(object o) => o is string || o is Def || o is Name || o is Enum || o is IntVec3 || o.GetType().IsPrimitive;
    }
}
