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
    // joiners' (lab mp51: host-only capacity refreshes from the autosave tick on -> RJW Need_Sex drift, row 36). The save now
    // runs on every instance at the same tick: the host sends a synced command, every instance queues the same save long
    // event; only the host writes the file and asks for the join point.
    private const int MaxDeferredFrames = 600;

    public static void DoAutosave()
    {
        if (Multiplayer.LocalServer != null && !Multiplayer.IsReplay && !Multiplayer.arbiterInstance)
        {
            // Called from ConstantTicker (inside the tick loop), where a sync method would run locally instead of being sent.
            OnMainThread.Enqueue(() => SendSyncedAutosave(0));
            return;
        }

        QueueHostSave();
    }

    private static void SendSyncedAutosave(int frames)
    {
        if (Multiplayer.Client == null)
            return;

        if (Multiplayer.ShouldSync)
        {
            SyncedAutosave();
            return;
        }

        if (frames < MaxDeferredFrames)
        {
            OnMainThread.Enqueue(() => SendSyncedAutosave(frames + 1));
            return;
        }

        Log.Warning("Multiplayer: synced autosave could not be sent (no interface frame); saving on this instance only");
        QueueHostSave();
    }

    [SyncMethod]
    public static void SyncedAutosave()
    {
        if (Multiplayer.LocalServer != null && !Multiplayer.arbiterInstance)
        {
            QueueHostSave();
            return;
        }

        // Same ExposeData(Saving) side effects as the host's save, at the same point of the simulation; the data is dropped.
        LongEventHandler.QueueLongEvent(() => SaveLoad.SaveGameData(), "MpSaving", false, null);
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
