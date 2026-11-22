using System;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplayer.Client.Desyncs;

// FIXES row 446: the desync report zip used to be written only by DesyncedWindow.WindowUpdate, so closing the window
// (Try resync, Quit, a newer desync clearing the window stack) before the two report tasks ended lost the report.
// This class writes the report exactly once: from the window's update, or, once the window is gone, when the tasks end.
// No game types here on purpose: tests/ links this file and runs it without the game.
public sealed class DesyncReportWriter<THost> where THost : class
{
    private readonly Task ready;
    private readonly Func<THost?, string?> save;
    private readonly Action<Action> runOnMainThread;
    private readonly Action<string> log;
    private int written;
    private int armed;

    public DesyncReportWriter(Task ready, Func<THost?, string?> save, Action<Action> runOnMainThread, Action<string> log)
    {
        this.ready = ready;
        this.save = save;
        this.runOnMainThread = runOnMainThread;
        this.log = log;
    }

    public bool Written => Volatile.Read(ref written) != 0;

    // Main thread only. Never blocks: returns false when the tasks are still running or the report was already written.
    public bool TryWrite(THost? host, bool afterWindowClosed)
    {
        if (!ready.IsCompleted) return false;
        if (Interlocked.Exchange(ref written, 1) != 0) return false;

        var file = save(host);
        if (file != null)
            log("Multiplayer: desync report written" + (afterWindowClosed ? " after the window closed" : "") + ": " + file);
        return true;
    }

    // Call when the window is closing / the rejoin starts. Safe to call repeatedly. hostNow is read on the main thread
    // at write time, so it picks up whatever host traces arrived by then.
    public void WriteWhenReady(Func<THost?> hostNow)
    {
        if (Written) return;
        if (Interlocked.Exchange(ref armed, 1) != 0) return;

        ready.ContinueWith(
            _ => runOnMainThread(() => TryWrite(hostNow(), true)),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }
}
