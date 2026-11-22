using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Multiplayer.Common.Util;

namespace Multiplayer.Common;

// FIXES row 447: the host's own desync report (Desync-host-<tick>.zip). It is built from the two trace texts alone, so
// it does not depend on the client player's window, replay or metadata tasks. Pure logic, no game types.
public static class DesyncReport
{
    public const int TopFrames = 12;

    public record TraceEntry(int Index, int Tick, int Hash, string Header, List<string> Frames);

    public record FirstDiffResult(bool Found, int Index, TraceEntry? Host, TraceEntry? Client, string Text, string LogLine);

    private static readonly Regex EntryStart = new(@"^(\d+) Tick:(-?\d+) Hash:(-?\d+) '", RegexOptions.Compiled);

    // The trace text is "<index> Tick:<t> Hash:<h> '<info>'" followed by the stack frames, one per line, entries split by
    // a blank line (ClientSyncOpinion.GetFormattedStackTracesForRange). The first entry seen for an index wins (the text
    // repeats the diffAt entry before the context window).
    public static Dictionary<int, TraceEntry> ParseTraces(string text)
    {
        var entries = new Dictionary<int, TraceEntry>();
        TraceEntry? current = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var m = EntryStart.Match(line);
            if (m.Success)
            {
                current = new TraceEntry(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                    int.Parse(m.Groups[3].Value), line.Substring(m.Groups[1].Length + 1), []);
                entries.TryAdd(current.Index, current);
                // a duplicate index must not collect frames into the first entry
                if (entries[current.Index] != current) current = null;
            }
            else if (current != null)
            {
                if (line.Length == 0) current = null;
                else if (current.Frames.Count < TopFrames) current.Frames.Add(line.Trim());
            }
        }

        return entries;
    }

    public static FirstDiffResult FindFirstDiff(string hostTraces, string clientTraces, int opinionTick)
    {
        var host = ParseTraces(hostTraces);
        var client = ParseTraces(clientTraces);

        var indices = new List<int>(host.Keys);
        indices.Sort();
        foreach (var i in indices)
        {
            if (!client.TryGetValue(i, out var c)) continue;
            var h = host[i];
            if (h.Hash == c.Hash) continue;

            var hostFrame = h.Frames.Count > 0 ? h.Frames[0] : h.Header;
            var clientFrame = c.Frames.Count > 0 ? c.Frames[0] : c.Header;
            var sb = new StringBuilder();
            sb.AppendLine($"First difference at trace {i} (opinion tick {opinionTick}, trace tick host {h.Tick} / client {c.Tick})");
            sb.AppendLine();
            sb.AppendLine($"HOST header: {h.Header}");
            sb.AppendLine($"HOST top {TopFrames} frames:");
            foreach (var f in h.Frames) sb.AppendLine("  " + f);
            sb.AppendLine();
            sb.AppendLine($"CLIENT header: {c.Header}");
            sb.AppendLine($"CLIENT top {TopFrames} frames:");
            foreach (var f in c.Frames) sb.AppendLine("  " + f);
            var log = $"Multiplayer: desync first difference at trace {i} (tick {opinionTick}): host {hostFrame} / client {clientFrame}";
            return new FirstDiffResult(true, i, h, c, sb.ToString(), log);
        }

        var none = $"No trace with a different hash among {host.Count} host / {client.Count} client traces in the exchanged window";
        return new FirstDiffResult(false, -1, null, null, none, $"Multiplayer: desync first difference not found (tick {opinionTick}): {none}");
    }

    public record HostReportInput(
        int OpinionTick,
        int DiffAt,
        string HostTraces,
        string ClientTraces,
        string HostPlayer,
        string ClientPlayer,
        string HostVersion,
        int HostProtocol,
        string ClientVersion,
        int ClientProtocol,
        string? ThingsFilePath);

    public static string BuildInfo(HostReportInput input, FirstDiffResult diff) =>
        $"###Desync###\nOpinion tick|||{input.OpinionTick}\nTrace index (reported by client)|||{input.DiffAt}\n" +
        $"First differing trace (host computed)|||{(diff.Found ? diff.Index.ToString() : "not found")}\n" +
        $"\n###Players###\nHost|||{input.HostPlayer}\nClient|||{input.ClientPlayer}\n" +
        $"\n###Versions###\nHost dll / protocol|||{input.HostVersion} / {input.HostProtocol}\n" +
        $"Client dll / protocol|||{input.ClientVersion} / {input.ClientProtocol}\n";

    // Returns the zip path. Never overwrites: a second report for the same tick gets a -2, -3 ... suffix.
    public static string WriteHostZip(string dir, HostReportInput input, FirstDiffResult diff)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"Desync-host-{input.OpinionTick}.zip");
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(dir, $"Desync-host-{input.OpinionTick}-{n}.zip");

        var temp = path + ".tmp";
        File.Delete(temp);
        using (var zip = MpZipFile.Open(temp, ZipArchiveMode.Create))
        {
            zip.AddEntry("desync_info", BuildInfo(input, diff));
            zip.AddEntry("first_diff.txt", diff.Text);
            zip.AddEntry("host_traces.txt", input.HostTraces);
            zip.AddEntry("client_traces.txt", input.ClientTraces);
            if (input.ThingsFilePath != null && File.Exists(input.ThingsFilePath))
                zip.CreateEntryFromFile(input.ThingsFilePath, Path.GetFileName(input.ThingsFilePath), CompressionLevel.NoCompression);
        }

        File.Move(temp, path);
        return path;
    }
}
