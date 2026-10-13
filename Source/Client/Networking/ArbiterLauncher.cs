using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Multiplayer.Client.Desyncs;
using Multiplayer.Client.Util;
using UnityEngine;
using Verse;
using Debug = UnityEngine.Debug;

namespace Multiplayer.Client.Networking
{
    /// Starts the arbiter: a second, headless instance of the game that joins the local server and is the reference
    /// for desync checks. RimWorld 1.7 (Waylith) ignores Unity's -batchmode, -nographics and -logfile; it has its own
    /// -headless mode and mirrors the whole game log to stdout, which is captured here into arbiter_log.txt.
    public static class ArbiterLauncher
    {
        public const string LogFileName = "arbiter_log.txt";
        public const string PrevLogFileName = "arbiter_log.prev.txt";
        public const string SaveDataFolderName = "MpArbiter";

        // The arbiter has its own save data folder because the game writes there on its own: mod settings,
        // Knowledge.xml and KeyPrefs.xml at startup, and a core-only ModsConfig.xml after any exception while loading
        // play data. In a folder shared with the host, that would rewrite the host's files.
        // Before every start, every top-level folder of the host's save data is mirrored into it, except the ones
        // below: Config (mod list, mod settings) is not the only place mods keep state that changes the game. Some
        // keep their settings in a folder of their own (Missile Girl: MissileGirl\Mod_*.xml), others data that map
        // generation reads (RealRuins: blueprints), and Multiplayer's def check compares def names, not values, so
        // a difference there would only show up as a desync.
        // Not mirrored: saves, Multiplayer's own output, the engine's caches (rebuilt per machine and GPU) and this
        // folder itself.
        private static readonly HashSet<string> SkippedFolders = new(PathComparer)
        {
            "Saves", "MpReplays", "MpDesyncs", SaveDataFolderName,
            "BundleCache", "ShaderCache", "TextureCache", "HardwareProfile", "Logs"
        };

        // Folders with these names are skipped at any depth inside a mirrored folder, in the host's copy and in the
        // arbiter's: caches a mod rebuilds from the mod files, and its own logs and reports. Missile Girl's Cache
        // holds ~300 MB of unified def XML that the host rewrites at its own start; the arbiter has the same mod
        // files and builds its own.
        private static readonly HashSet<string> SkippedSubfolders = new(PathComparer) { "Cache", "Logs", "Reports" };

        private static Process lastStarted;
        private static bool exitHookAdded;

        public static Process Start(int port)
        {
            string exe = ExecutablePath();
            string hostSaveData = Path.GetFullPath(GenFilePaths.SaveDataFolderPath);
            string logPath = Path.Combine(hostSaveData, LogFileName);

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe)
            };

            string saveData = null;
            if (Native.IsMono)
            {
                // Unity (RimWorld 1.6 and older): the original launch; Unity writes the log file itself
                psi.Arguments = $"-batchmode -nographics -arbiter -logfile {LogFileName} -connect=127.0.0.1:{port}";
                if (GenCommandLine.TryGetCommandLineArg("savedatafolder", out saveData))
                    psi.Arguments += $" \"-savedatafolder={saveData}\"";
            }
            else
            {
                saveData = PrepareSaveData(hostSaveData);
                AddWaylithArguments(psi, port, saveData);
            }

            // The child inherits the host's environment: no auto-join target from a restart, and the same server
            // configs the host is running with (if it restarted with them)
            psi.Environment.Remove(AutoJoinHandler.RestartConnectVariable);
            if (SyncConfigs.Applicable)
                psi.Environment[SyncConfigs.RestartConfigsVariable] = "true";
            else
                psi.Environment.Remove(SyncConfigs.RestartConfigsVariable);

            string commandLine = psi.ArgumentList.Count > 0
                ? Quote(exe) + " " + string.Join(" ", psi.ArgumentList.Select(Quote))
                : Quote(exe) + " " + psi.Arguments;
            Log.Message($"Multiplayer: starting the arbiter: {commandLine}");

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            ArbiterLogSink sink = null;
            if (psi.RedirectStandardOutput)
            {
                sink = new ArbiterLogSink(logPath, commandLine) { process = process };
                process.OutputDataReceived += (_, e) => sink.Line(e.Data, false);
                process.ErrorDataReceived += (_, e) => sink.Line(e.Data, true);
            }
            process.Exited += (_, _) => OnExited(process, logPath);

            try
            {
                process.Start();
            }
            catch
            {
                sink?.Close();
                throw;
            }

            if (sink != null)
            {
                // Asynchronous reads: the pipes are drained on thread pool threads, never on the main thread
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }

            lastStarted = process;
            AddExitHook();

            Log.Message($"Multiplayer: arbiter started, pid {process.Id}, " +
                        $"log {(sink != null ? logPath : LogFileName + " (Unity)")}, save data {saveData ?? "(game default)"}");
            return process;
        }

        private static void AddWaylithArguments(ProcessStartInfo psi, int port, string saveData)
        {
            // -headless: no window, no graphics device, null audio (RimWorldWaylith.Program.Main, Bootstrap.Initialize)
            psi.ArgumentList.Add("-headless");
            // No <persistent data>\Logs\Player.log: Waylith writes it outside -savedatafolder, so every instance on
            // the machine shares (and truncates) the same file. The log arrives on stdout instead.
            psi.ArgumentList.Add("-NoFileLog");
            psi.ArgumentList.Add("-arbiter");
            psi.ArgumentList.Add($"-{AutoJoinHandler.AutoJoinCommandLineArgName}=127.0.0.1:{port}");
            psi.ArgumentList.Add($"-savedatafolder={saveData}");
            // The arbiter traces like the host: the tracing mode is not in the settings file (it is set per run with
            // -mptracemode or by a lab harness), and the trace hashes only compare when both sides hash the same way
            if (Multiplayer.settings != null)
                psi.ArgumentList.Add($"-{DeferredStackTracing.TraceModeArg}={Multiplayer.settings.desyncTracingMode}");
            if (DeferredStackTracing.HashManagedFrames)
                psi.ArgumentList.Add("-" + DeferredStackTracing.TraceFrameHashArg);

            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
        }

        private static string ExecutablePath()
        {
            if (Native.IsMono && Application.platform == RuntimePlatform.OSXPlayer)
                return Application.dataPath + "/MacOS/" + Process.GetCurrentProcess().MainModule!.ModuleName;

            return Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;
        }

        private static string Quote(string arg) => arg.Contains(' ') ? $"\"{arg}\"" : arg;

        // <host save data>\MpArbiter, with the mirrored folders made equal to the host's. The engine caches the arbiter
        // builds in its own folder (BundleCache, ShaderCache, TextureCache) stay between runs. A first start copies
        // everything (RealRuins alone can be ~350 MB); later starts copy only what changed.
        private static string PrepareSaveData(string hostSaveData)
        {
            string dir = Path.Combine(hostSaveData, SaveDataFolderName);
            Directory.CreateDirectory(dir);

            var watch = Stopwatch.StartNew();
            int copied = 0, removed = 0, failed = 0;
            long bytes = 0;
            var mirrored = new List<string>();
            // The arbiter's folders too: one the host no longer has is emptied (stale settings or server configs)
            var names = Directory.GetDirectories(hostSaveData).Concat(Directory.GetDirectories(dir))
                .Select(d => Path.GetFileName(d)).Distinct(PathComparer).OrderBy(n => n, PathComparer).ToList();
            foreach (var name in names)
            {
                if (SkippedFolders.Contains(name)) continue;
                var folder = Path.Combine(hostSaveData, name);
                mirrored.Add(name);
                try
                {
                    MirrorFolder(folder, Path.Combine(dir, name), ref copied, ref removed, ref failed, ref bytes);
                }
                catch (Exception e)
                {
                    // A folder that cannot be listed: the arbiter still starts, with what was copied
                    failed++;
                    Log.Warning($"Multiplayer: arbiter save data: could not mirror {folder}: {e.Message}");
                }
            }

            Log.Message($"Multiplayer: arbiter save data {dir}: {copied} file(s) copied ({bytes / (1024 * 1024)} MB), " +
                        $"{removed} removed, {failed} failed in {watch.ElapsedMilliseconds} ms; mirrored from " +
                        $"{hostSaveData}: {string.Join(", ", mirrored)} (folders named " +
                        $"{string.Join("/", SkippedSubfolders)} inside them skipped)");
            return dir;
        }

        private static StringComparer PathComparer =>
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        // All files under root, relative to it, leaving out folders named in SkippedSubfolders (and what is in them)
        private static IEnumerable<string> MirroredFiles(string root)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var dir = pending.Pop();
                foreach (var file in Directory.GetFiles(dir))
                    yield return Path.GetRelativePath(root, file);
                foreach (var sub in Directory.GetDirectories(dir))
                    if (!SkippedSubfolders.Contains(Path.GetFileName(sub)))
                        pending.Push(sub);
            }
        }

        // Makes dst a copy of src: files that differ in size or write time are copied, files src does not have are
        // removed (a stale settings file would give the arbiter settings the host does not have). Skipped subfolders
        // are left alone on both sides. Only ever called with dst under the arbiter's own folder.
        private static void MirrorFolder(string src, string dst, ref int copied, ref int removed, ref int failed,
            ref long bytes)
        {
            var keep = new HashSet<string>(PathComparer);

            if (Directory.Exists(src))
            {
                foreach (var rel in MirroredFiles(src))
                {
                    var file = Path.Combine(src, rel);
                    keep.Add(rel);
                    var target = Path.Combine(dst, rel);
                    try
                    {
                        var from = new FileInfo(file);
                        var to = new FileInfo(target);
                        if (to.Exists && to.Length == from.Length && to.LastWriteTimeUtc == from.LastWriteTimeUtc)
                            continue;

                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(file, target, true);
                        File.SetLastWriteTimeUtc(target, from.LastWriteTimeUtc);
                        copied++;
                        bytes += from.Length;
                    }
                    catch (Exception e)
                    {
                        failed++;
                        Log.Warning($"Multiplayer: arbiter save data: could not copy {file}: {e.Message}");
                    }
                }
            }

            if (!Directory.Exists(dst)) return;

            foreach (var rel in MirroredFiles(dst).ToList())
            {
                if (keep.Contains(rel)) continue;
                var file = Path.Combine(dst, rel);
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (Exception e)
                {
                    failed++;
                    Log.Warning($"Multiplayer: arbiter save data: could not remove stale {file}: {e.Message}");
                }
            }
        }

        // Thread pool thread: UnityEngine.Debug.Log, like the server's log, not Verse.Log
        private static void OnExited(Process process, string logPath)
        {
            try
            {
                Debug.Log($"Multiplayer: arbiter process exited with code {ExitCode(process)} (log {logPath})");
            }
            catch (Exception)
            {
                // Never throw on a thread pool thread
            }
        }

        private static string ExitCode(Process process)
        {
            try
            {
                // Both pipes can reach their end a moment before the process counts as exited
                return process.WaitForExit(2000) ? process.ExitCode.ToString() : "none yet (still running)";
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        // Session.Stop kills the arbiter; this also covers the host process ending without stopping the session
        private static void AddExitHook()
        {
            if (exitHookAdded) return;
            exitHookAdded = true;
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try
                {
                    if (lastStarted is { HasExited: false } p) p.Kill();
                }
                catch (Exception) { }
            };
        }

        // Writes the arbiter's stdout and stderr lines to one file. The data handlers run on thread pool threads;
        // the file is flushed once a second and closed when both streams have ended.
        private sealed class ArbiterLogSink
        {
            private readonly object gate = new();
            private readonly Timer flushTimer;
            private StreamWriter writer;
            private int openStreams = 2;
            public Process process;

            public ArbiterLogSink(string path, string commandLine)
            {
                try
                {
                    if (File.Exists(path))
                        File.Copy(path, Path.Combine(Path.GetDirectoryName(path)!, PrevLogFileName), true);
                }
                catch (Exception) { }

                // Shared read, so the log can be followed while the arbiter runs
                writer = new StreamWriter(
                    new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete),
                    new UTF8Encoding(false));
                writer.WriteLine($"=== Multiplayer arbiter log, started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                writer.WriteLine($"=== {commandLine} ===");
                writer.Flush();

                flushTimer = new Timer(_ => Flush(), null, 1000, 1000);
            }

            // data == null: that stream has ended
            public void Line(string data, bool stderr)
            {
                if (data == null)
                {
                    if (Interlocked.Decrement(ref openStreams) == 0)
                    {
                        Write($"=== arbiter output ended at {DateTime.Now:yyyy-MM-dd HH:mm:ss}, exit code " +
                              $"{(process != null ? ExitCode(process) : "unknown")} ===", true);
                        Close();
                    }
                    return;
                }

                Write(stderr ? "[stderr] " + data : data, false);
            }

            private void Write(string line, bool flush)
            {
                lock (gate)
                {
                    try
                    {
                        if (writer == null) return;
                        writer.WriteLine(line);
                        if (flush) writer.Flush();
                    }
                    catch (Exception) { }
                }
            }

            private void Flush()
            {
                lock (gate)
                {
                    try { writer?.Flush(); }
                    catch (Exception) { }
                }
            }

            public void Close()
            {
                lock (gate)
                {
                    try
                    {
                        flushTimer.Dispose();
                        writer?.Flush();
                        writer?.Dispose();
                    }
                    catch (Exception) { }
                    writer = null;
                }
            }
        }
    }
}
