using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Multiplayer.API;
using Multiplayer.Common;
using RimWorld;
using Verse;

namespace Multiplayer.Client.Util;

// FIXES row 512. Mod settings that differ between PCs (a mod settings window can rewrite Defs on one PC only), checked
// outside the join compare: after a mod settings window closes, and once after join. A PC sends its store hash list
// (SettingsStoreHash) through one synced method; every PC compares it with its own list. A difference is logged and shown
// as one message. Never changes game state, never blocks the window. Gated by LabOff.Row(512) (true = old behaviour = off).
public static class SettingsCheck
{
    const int KindWindow = 0, KindJoin = 1, KindReply = 2;
    static bool startLogged;
    static bool pendingJoin;
    static bool pendingReply;

    static bool Enabled => !LabOff.Row(512) && Multiplayer.Client != null && !Multiplayer.arbiterInstance;

    static ICollection<string> IgnoredFolders()
    {
        var folders = new List<string>();
        foreach (var mod in LoadedModManager.RunningMods)
        {
            var id = mod.ModMetaData?.PackageIdNonUnique;
            if (id != null && SyncConfigs.ignoredConfigsModIds.Contains(id.ToLowerInvariant()))
                folders.Add(mod.FolderName);
        }
        return folders;
    }

    public static string[] LocalLines() =>
        SettingsStoreHash.ComputeLines(GenFilePaths.SaveDataFolderPath, IgnoredFolders());

    // Once per process, at startup.
    public static void LogStart()
    {
        if (startLogged || LabOff.Row(512)) return;
        startLogged = true;
        try
        {
            var lines = LocalLines();
            var kinds = string.Join(", ", lines.GroupBy(l => SettingsStoreHash.Kind(l.Split('\t')[0]))
                .Select(g => g.Key + " " + g.Count()));
            Log.Message($"[Multiplayer] row 512: settings check on ({lines.Length} stores: {kinds})");
        }
        catch (Exception e)
        {
            Log.Error($"[Multiplayer] row 512: start log failed: {e}");
        }
    }

    // Called from the joiner's loading state once it is playing; the send waits for Update (no long event, not simulating).
    public static void OnJoined()
    {
        if (!LabOff.Row(512)) pendingJoin = true;
    }

    public static void OnSettingsWindowClosed()
    {
        if (!Enabled || TickPatch.Simulating) return;
        Send(KindWindow);
    }

    // Per frame, from OnMainThread.Update (a synced call made while commands execute would only run locally).
    public static void Update()
    {
        if (!(pendingJoin || pendingReply)) return;
        if (!Enabled || TickPatch.Simulating || Multiplayer.session?.client?.State != ConnectionStateEnum.ClientPlaying
            || LongEventHandler.AnyEventNowOrWaiting || Find.CurrentMap == null || Multiplayer.ExecutingCmds)
            return;

        if (pendingJoin) { pendingJoin = false; Send(KindJoin); }
        if (pendingReply) { pendingReply = false; Send(KindReply); }
    }

    static void Send(int kind)
    {
        try
        {
            Receive(Multiplayer.username, kind, SettingsStoreHash.Pack(LocalLines()));
        }
        catch (Exception e)
        {
            Log.Error($"[Multiplayer] row 512: send failed: {e}");
        }
    }

    // Runs on every PC, the sender included (the sender skips itself).
    [SyncMethod]
    static void Receive(string player, int kind, string payload)
    {
        try
        {
            if (!Enabled || TickPatch.Simulating || player == Multiplayer.username) return;

            var theirs = SettingsStoreHash.Unpack(payload);
            var differ = SettingsStoreHash.Diff(LocalLines(), theirs);
            if (differ.Count > 0)
            {
                var files = string.Join(", ", differ);
                Log.Message($"[Multiplayer] row 512: mod settings differ from {player}: {files}");
                Messages.Message($"Mod settings differ from {player}'s ({differ.Count} file(s), see the log). Defs may now differ between the PCs.",
                    MessageTypeDefOf.RejectInput, false);
            }

            // the host answers a joiner's list with its own, so the compare runs in both directions
            if (kind == KindJoin && Multiplayer.LocalServer != null) pendingReply = true;
        }
        catch (Exception e)
        {
            Log.Error($"[Multiplayer] row 512: compare failed: {e}");
        }
    }
}
