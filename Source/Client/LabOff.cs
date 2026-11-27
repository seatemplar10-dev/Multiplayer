using System;
using HarmonyLib;
using Verse;

namespace Multiplayer.Client
{
    // Lab mutation switch: -mplaboff=<rows> puts the listed fixes back to their old behaviour (FIXES rows 17, 114, 117, 152, 165, 236, 407, 109, 129, 167, 215, 273, 280, 313, 316, 379, 421, 124, 438, 444, 450, 446).
    // No flag = every Row() call is false and nothing changes. The flag counts only when the lab harness type SeaMPLab.MPLab is loaded:
    // the shipped game has no harness dll, so a stray -mplaboff there is ignored.
    // The command line and the gate are read lazily at the first Row() / Raw use (not in a static constructor), so SeaMPLab is looked up
    // as late as the first caller allows. One log line per row: [LabOff] OFF row N dll=Multiplayer.
    internal static class LabOff
    {
        public const string Arg = "mplaboff";
        const string GateType = "SeaMPLab.MPLab";
        // The rows THIS dll can switch. 114 also keeps its old name -mplabnoc13 (InterfaceSkipPostfixGuard.OffArg).
        static readonly int[] Owned = { 5, 17, 114, 117, 152, 165, 187, 236, 237, 407, 417, 199, 191, 92, 35, 78, 115, 122, 123, 256, 418, 419, 109, 129, 167, 215, 273, 280, 313, 316, 379, 97, 202, 211, 421, 124, 359, 322, 427, 438, 444, 450, 446, 453, 456, 512 };
        static volatile bool ready;
        static string raw;
        static bool[] on;   // null when no row is off

        // "117,152" as given on the command line, or null (no flag, or the harness is not loaded)
        public static string Raw
        {
            // with a flip tick the value ends with ",flip<tick>": ArbiterLauncher passes Raw on as -mplaboff=<Raw>
            get { if (!ready) Init(); return raw != null && flipTick >= 0 ? raw + ",flip" + flipTick : raw; }
        }

        // Trigger line: ONE log line the first time a row's switch is read in this process (same text in every fork).
        static readonly bool[] hitDone = new bool[1000];
        static void Hit(int row, bool off)
        {
            try
            {
                if (row < 0 || row >= hitDone.Length || hitDone[row]) return;
                hitDone[row] = true;
                var tick = -1;
                try { tick = Find.TickManager?.TicksGame ?? -1; } catch (Exception) { tick = -1; }
                Log.Message("[LabOff] HIT row " + row + " state=" + (off ? "off" : "on") + " tick=" + tick);
            }
            catch (Exception) { }
        }

        // -mplabflip=<tick> (or "-mplabflip <tick>", or a part "flip<tick>" in the -mplaboff value): one boot = control ticks, then fix ticks. The rows in -mplaboff stay OFF (old behaviour)
        // while Find.TickManager.TicksGame < tick and are ON (fix) from that tick on. No game loaded = tick -1 = OFF. The switch is not latched:
        // it is the TicksGame value at each Row() call. One log line per row at its first read at or after the tick: [LabOff] FLIP row N tick=T.
        // No -mplabflip = flipTick -1 = Row() returns what it returned before. Only a row whose gate is read per call can flip.
        public const string FlipArg = "mplabflip";
        static int flipTick = -1;
        static readonly bool[] flipDone = new bool[1000];

        static int ReadFlip()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                var key = "-" + FlipArg;
                for (var i = 0; i < args.Length; i++)
                {
                    var t = args[i];
                    if (t == null) continue;
                    string v = null;
                    if (t.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) v = t.Substring(key.Length + 1);
                    else if (string.Equals(t, key, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) v = args[i + 1];
                    if (v != null && int.TryParse(v.Trim(), out var n) && n >= 0) return n;
                    // the same tick as a part of the -mplaboff value: -mplaboff=117,flip600 (this is how the arbiter process gets it)
                    if (t.StartsWith("-" + Arg + "=", StringComparison.OrdinalIgnoreCase))
                        foreach (var part in t.Substring(Arg.Length + 2).Split(','))
                            if (part.Trim().StartsWith("flip", StringComparison.OrdinalIgnoreCase) && int.TryParse(part.Trim().Substring(4), out var m) && m >= 0) return m;
                }
            }
            catch (Exception) { }
            return -1;
        }

        // true = the flip tick is reached, the row is ON now
        static bool Flipped(int row)
        {
            var tick = -1;
            try { tick = Find.TickManager?.TicksGame ?? -1; } catch (Exception) { tick = -1; }
            if (tick < flipTick) return false;
            try
            {
                if (row >= 0 && row < flipDone.Length && !flipDone[row])
                {
                    flipDone[row] = true;
                    Log.Message("[LabOff] FLIP row " + row + " tick=" + tick);
                }
            }
            catch (Exception) { }
            return true;
        }

        public static bool Row(int row)
        {
            if (!ready) Init();
            var a = on;
            var r = a != null && row >= 0 && row < a.Length && a[row];
            if (r && flipTick >= 0 && Flipped(row)) r = false;
            Hit(row, r);
            return r;
        }

        // Reads -mplaboff from Environment.GetCommandLineArgs() in both forms: one token "-mplaboff=117,152" (any case), or
        // "-mplaboff 117 152" / "-mplaboff 117,152" (the tokens after it up to the next one starting with "-"; cmd.exe splits at = and ,).
        // Returns the ints joined with ",", or null when there is no flag or no int.
        static string ReadArg()
        {
            var args = Environment.GetCommandLineArgs();
            var parts = new System.Collections.Generic.List<string>();
            var key = "-" + Arg;
            for (var i = 0; i < args.Length; i++)
            {
                var t = args[i];
                if (t == null) continue;
                if (t.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                    parts.AddRange(t.Substring(key.Length + 1).Split(','));
                else if (string.Equals(t, key, StringComparison.OrdinalIgnoreCase))
                {
                    for (var j = i + 1; j < args.Length && args[j] != null && !args[j].StartsWith("-"); j++)
                        parts.AddRange(args[j].Split(','));
                }
            }
            var ints = new System.Collections.Generic.List<string>();
            foreach (var p in parts)
                if (int.TryParse(p.Trim(), out var n)) ints.Add(n.ToString());
            return ints.Count == 0 ? null : string.Join(",", ints.ToArray());
        }

        static void Init()
        {
            lock (typeof(LabOff))
            {
                if (ready) return;
                try
                {
                    var value = ReadArg();
                    if (!string.IsNullOrEmpty(value))
                    {
                        if (AccessTools.TypeByName(GateType) == null)
                            Log.Warning("[LabOff] -" + Arg + "=" + value + " ignored: " + GateType + " is not loaded");
                        else
                        {
                            var flags = new bool[512];
                            var any = false;
                            foreach (var p in value.Split(','))
                            {
                                if (!int.TryParse(p.Trim(), out var r) || Array.IndexOf(Owned, r) < 0 || flags[r]) continue;
                                flags[r] = true;
                                any = true;
                                Log.Warning("[LabOff] OFF row " + r + " dll=Multiplayer");
                            }
                            flipTick = ReadFlip();
                            if (flipTick >= 0) Log.Warning("[LabOff] FLIP armed tick=" + flipTick + " dll=Multiplayer");
                            raw = value;
                            if (any) on = flags;
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.Error("[LabOff] init failed: " + e);
                }
                ready = true;
            }
        }
    }
}
