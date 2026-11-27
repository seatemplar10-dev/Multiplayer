using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Client.Util;
using Verse;

namespace Multiplayer.Client.Patches;

// FIXES row 512: a mod settings window closed (vanilla Dialog_ModSettings; MSF draws inside it, 0ModSettingsFramework has
// only a Dialog_ModSettings_Patch). Postfix of PreClose, after the game has written the mod's settings.
[HarmonyPatch]
static class Dialog_ModSettings_PreClose_Row512
{
    static MethodBase TargetMethod() =>
        AccessTools.AllTypes().FirstOrDefault(t => t.Name == "Dialog_ModSettings") is { } t ? AccessTools.DeclaredMethod(t, "PreClose") : null;

    static bool Prepare() => TargetMethod() != null;

    static void Postfix()
    {
        try { SettingsCheck.OnSettingsWindowClosed(); }
        catch (Exception e) { Log.Error($"[Multiplayer] row 512: window hook failed: {e}"); }
    }
}
