using System;
using HarmonyLib;
using Verse;

namespace Multiplayer.Client
{
    // Lab mutation switch: -mplaboff=<rows> puts the listed fixes back to their old behaviour (FIXES rows 17, 114, 117, 152, 165, 236, 407).
    // No flag = every Row() call is false and nothing changes. The flag counts only when the lab harness type SeaMPLab.MPLab is loaded:
    // the shipped game has no harness dll, so a stray -mplaboff there is ignored.
    // The command line and the gate are read lazily at the first Row() / Raw use (not in a static constructor), so SeaMPLab is looked up
    // as late as the first caller allows. One log line per row: [LabOff] OFF row N dll=Multiplayer.
    internal static class LabOff
    {
        public const string Arg = "mplaboff";
        const string GateType = "SeaMPLab.MPLab";
        // The rows THIS dll can switch. 114 also keeps its old name -mplabnoc13 (InterfaceSkipPostfixGuard.OffArg).
        static readonly int[] Owned = { 17, 114, 117, 152, 165, 236, 407, 417, 199, 191, 92, 35, 78, 115, 122, 256 };

        static volatile bool ready;
        static string raw;
        static bool[] on;   // null when no row is off

        // "117,152" as given on the command line, or null (no flag, or the harness is not loaded)
        public static string Raw
        {
            get { if (!ready) Init(); return raw; }
        }

        public static bool Row(int row)
        {
            if (!ready) Init();
            var a = on;
            return a != null && row >= 0 && row < a.Length && a[row];
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
