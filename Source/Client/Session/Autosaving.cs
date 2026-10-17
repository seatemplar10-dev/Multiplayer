using System;
using System.IO;
using System.Linq;
using Multiplayer.API;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;
using RimWorld;
using UnityEngine;
using Verse;

namespace Multiplayer.Client;

public static class Autosaving
{
    // 1.7 lab (FIXES row 41): saving runs every mod's ExposeData(Saving), and some of that changes the live game (PsiTech
    // deletes each non-activated pawn's tracker; the next stat read rebuilds it and dirties the pawn's capacity cache). On a
    // hosted server the autosave used to save on the host only, so the host's simulation drifted from the arbiter's and the
    // joiners' (lab mp51: host-only capacity refreshes from the autosave tick on -> RJW Need_Sex drift, row 36).
    // A synced save long event on every instance froze the catching-up arbiter (lab mp53), so the autosave now rides
    // Multiplayer's own join point: the host only asks for a forced join point, every instance saves and reloads at that
    // command's tick (CreateJoinPointAndSendIfHost), and the host writes the autosave file from the join point snapshot.
    internal static string pendingAutosaveFile;

    public static void DoAutosave()
    {
        if (Multiplayer.LocalServer != null && Multiplayer.session?.ConnectedToStandaloneServer != true && !Multiplayer.IsReplay)
        {
            pendingAutosaveFile ??= GetNextAutosaveFileName();
            SendAutosavingRequest(JoinPointRequestReason.Save);
            return;
        }

        QueueHostSave();
    }

    // Called on the host right after a join point snapshot was made.
    internal static void WritePendingAutosave(GameDataSnapshot snapshot)
    {
        var name = pendingAutosaveFile;
        if (name == null || snapshot == null)
            return;

        pendingAutosaveFile = null;
        SaveGameToFile_Overwrite(name, snapshot);
    }

    private static void QueueHostSave()
    {
        LongEventHandler.QueueLongEvent(() =>
        {
            var snapshot = SaveLoad.CreateGameDataSnapshot(SaveLoad.SaveGameData(), false);

            if (!SaveGameToFile_Overwrite(GetNextAutosaveFileName(), snapshot))
                return;

            SendAutosavingRequest(JoinPointRequestReason.Save);

            // When connected to a standalone server, also upload fresh snapshots
            if (Multiplayer.session?.ConnectedToStandaloneServer == true)
            {
                SaveLoad.SendStandaloneMapSnapshots(snapshot);
                SaveLoad.SendStandaloneWorldSnapshot(snapshot);
            }
        }, "MpSaving", false, null);
    }

    public static void SendAutosavingRequest(JoinPointRequestReason reason)
    {
        if (TickPatch.Simulating)
            return;

        Multiplayer.Client.Send(new ClientAutosavingPacket(reason));
    }

    private static string GetNextAutosaveFileName()
    {
        var autosavePrefix = "Autosave-";

        if (Multiplayer.settings.appendNameToAutosave)
            autosavePrefix += $"{GenFile.SanitizedFileName(Multiplayer.session.gameName)}-";

        return Enumerable
            .Range(1, Multiplayer.settings.autosaveSlots)
            .Select(i => $"{autosavePrefix}{i}")
            .OrderBy(s => new FileInfo(Path.Combine(Multiplayer.ReplaysDir, $"{s}.zip")).LastWriteTime)
            .First();
    }

    public static bool SaveGameToFile_Overwrite(string fileNameNoExtension, bool currentReplay)
        => SaveGameToFile_Overwrite(fileNameNoExtension,
            currentReplay ? Multiplayer.session.dataSnapshot : null);

    public static bool SaveGameToFile_Overwrite(string fileNameNoExtension, GameDataSnapshot snapshot)
    {
        Log.Message($"Multiplayer: saving to file {fileNameNoExtension}");

        try
        {
            var tmpPath = Path.Combine(Multiplayer.ReplaysDir, $"{fileNameNoExtension}.tmp.zip");
            if (File.Exists(tmpPath))
                File.Delete(tmpPath);

            Replay.ForSaving(new FileInfo(tmpPath)).WriteData(
                snapshot ?? SaveLoad.CreateGameDataSnapshot(SaveLoad.SaveGameData(), false)
            );

            var dstPath = Path.Combine(Multiplayer.ReplaysDir, $"{fileNameNoExtension}.zip");
            if (File.Exists(dstPath))
                File.Replace(tmpPath, dstPath, destinationBackupFileName: null);
            else
                File.Move(tmpPath, dstPath);

            Messages.Message("MpGameSaved".Translate(fileNameNoExtension), MessageTypeDefOf.SilentInput, false);
            Multiplayer.session.lastSaveAt = Time.realtimeSinceStartup;
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"Exception saving multiplayer game as {fileNameNoExtension}: {e}");
            Messages.Message("MpGameSaveFailed".Translate(), MessageTypeDefOf.SilentInput, false);
            return false;
        }
    }
}
