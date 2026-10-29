using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Verse;

namespace Multiplayer.Client.AsyncTime
{
    // Measurement only (per-map authority study, 29 Oct 2026). Never part of a co-op package.
    // -mppermapprobe          : time each tickable's Tick() and log the totals every ReportEvery MP ticks.
    // -mppermapskip=<mapId>   : this PC does not tick that map (as if paused). Host-alone lab runs only:
    //                           it refuses to act while a second player (or the arbiter) is in the session.
    // Reads only: no Rand, no game state writes, so the probe alone cannot change the simulation.
    public static class PerMapProbe
    {
        const int ReportEvery = 2500;

        // The environment variables MP_PERMAP_PROBE=1 and MP_PERMAP_SKIP=<mapId> do the same: cmd.exe splits arguments at '='.
        public static readonly bool Enabled = GenCommandLine.CommandLineArgPassed("mppermapprobe") ||
            Environment.GetEnvironmentVariable("MP_PERMAP_PROBE") == "1";
        static readonly int skipMapId = ReadSkip();

        static int ReadSkip()
        {
            string s = Environment.GetEnvironmentVariable("MP_PERMAP_SKIP");
            if (string.IsNullOrEmpty(s)) GenCommandLine.TryGetCommandLineArg("mppermapskip", out s);
            return int.TryParse(s, out var id) ? id : -1;
        }

        static readonly Dictionary<int, long> ticksById = new();
        static readonly Dictionary<int, long> elapsedById = new();
        static long doTickElapsed;
        static int lastReportTimer = -1;
        static int skippedTicks;

        public static bool Skip(ITickable tickable)
        {
            if (skipMapId < 0 || tickable is not AsyncTimeComp comp || comp.map.uniqueID != skipMapId)
                return false;
            if (Multiplayer.session == null || Multiplayer.session.players.Count > 1)
                return false;
            skippedTicks++;
            return true;
        }

        static bool announced;

        public static long Start()
        {
            if (!Enabled) return 0;
            if (!announced)
            {
                announced = true;
                Log.Message($"[PerMapProbe] active, report every {ReportEvery} MP ticks, skip map {skipMapId}");
            }
            return Stopwatch.GetTimestamp();
        }

        public static void StopTickable(ITickable tickable, long start)
        {
            if (!Enabled) return;
            long dt = Stopwatch.GetTimestamp() - start;
            int key = tickable is AsyncTimeComp comp ? comp.map.uniqueID : -1; // -1 = world
            ticksById[key] = ticksById.GetValueOrDefault(key) + 1;
            elapsedById[key] = elapsedById.GetValueOrDefault(key) + dt;
        }

        public static void StopDoTick(long start, int timer)
        {
            if (!Enabled) return;
            doTickElapsed += Stopwatch.GetTimestamp() - start;
            if (lastReportTimer < 0) lastReportTimer = timer;
            if (timer - lastReportTimer < ReportEvery) return;
            Report(timer);
            lastReportTimer = timer;
        }

        static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        static void Report(int timer)
        {
            var sb = new StringBuilder();
            sb.Append($"[PerMapProbe] mpTimer={timer} window={timer - lastReportTimer} doTickMs={Ms(doTickElapsed):F1} skipped={skippedTicks}");
            foreach (var kv in elapsedById)
            {
                long n = ticksById[kv.Key];
                string who = "world";
                if (kv.Key >= 0)
                {
                    var map = Find.Maps.Find(m => m.uniqueID == kv.Key);
                    who = map == null
                        ? $"map{kv.Key}(gone)"
                        : $"map{kv.Key}({map.ParentFaction?.Name ?? "none"},pawns={map.mapPawns.AllPawnsSpawnedCount},things={map.listerThings.AllThings.Count})";
                }
                sb.Append($" | {who} ticks={n} ms={Ms(kv.Value):F1} msPerTick={(n > 0 ? Ms(kv.Value) / n : 0):F3}");
            }
            Log.Message(sb.ToString());
            ticksById.Clear();
            elapsedById.Clear();
            doTickElapsed = 0;
            skippedTicks = 0;
        }
    }
}
