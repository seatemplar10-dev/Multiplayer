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
        static readonly int[] Owned = { 17, 114, 117, 152, 165, 236, 407 };

        static bool ready;
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

        static void Init()
        {
            lock (typeof(LabOff))
            {
                if (ready) return;
                try
                {
                    if (GenCommandLine.TryGetCommandLineArg(Arg, out var value) && !string.IsNullOrEmpty(value))
                    {
                        if (AccessTools.TypeByName(GateType) == null)
                            Log.Warning("[LabOff] -" + Arg + "=" + value + " ignored: " + GateType + " is not loaded");
                        else
                        {
                            var flags = new bool[512];
                            var any = false;
                            foreach (var p in value.Split(','))
                            {
                                if (!int.TryParse(p.Trim(), out var r) || Array.IndexOf(Owned, r) < 0) continue;
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
