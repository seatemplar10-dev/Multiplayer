using RimWorld.Planet;
using Verse;

namespace Multiplayer.Client
{
    // 1.7-safe copy of Multiplayer.API.Session.SwitchToMapOrWorld. The API's version lives in 0MultiplayerAPI.dll,
    // and whichever copy of that dll loads first wins; old copies call WorldRendererUtility.WorldRenderedNow,
    // which 1.7 removed (MissingMethodException in the colonist bar session menu, FIXES row 313).
    public static class SessionMapSwitch
    {
        public static void SwitchToMapOrWorld(Map map)
        {
            if (map == null)
            {
                Find.World.renderer.wantedMode = WorldRenderMode.Planet;
            }
            else
            {
                if (WorldRendererUtility.WorldSelected) CameraJumper.TryHideWorld();
                Current.Game.CurrentMap = map;
            }
        }
    }
}
