namespace Multiplayer.Common
{
    public class DefInfo
    {
        public int count;
        public int hash;

        // 1.7 lab: def types the join check compares and reports but does not refuse on (MultiplayerData.CollectDefInfos)
        public const string SoftPrefix = "~";
    }
}
