using HarmonyLib;
using Verse;

namespace Multiplayer.Client
{
    [HarmonyPatch(typeof(TickManager), nameof(TickManager.TickRateMultiplier), MethodType.Getter)]
    [HarmonyPriority(Priority.First)] // a false prefix stops the later ones, so MP's value wins over other mods (Smart Speed)
    public static class TickRatePatch
    {
        static bool Prefix(TickManager __instance, ref float __result)
        {
            if (Multiplayer.Client == null) return true;

            switch (__instance.CurTimeSpeed, __instance.slower.ForcedNormalSpeed)
            {
                case (TimeSpeed.Paused, _):
                    __result = 0;
                    break;
                case (_, true):
                    __result = 1;
                    break;
                case (TimeSpeed.Fast, _):
                    __result = 3;
                    break;
                case (TimeSpeed.Superfast, _):
                    __result = 6;
                    break;
                case (TimeSpeed.Ultrafast, _):
                    __result = 15;
                    break;
                default:
                    __result = 1;
                    break;
            }

            return false;
        }
    }

}
