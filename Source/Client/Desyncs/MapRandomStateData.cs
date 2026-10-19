using System.Collections.Generic;
using Multiplayer.Common;

namespace Multiplayer.Client
{
    /// <summary>
    /// Holds the random states for a given map, and its map id
    /// </summary>
    public class MapRandomStateData(int mapId)
    {
        public int mapId = mapId;
        public List<uint> randomStates = new();

        // FIXES row 113 item 3: list order/content checkpoints (ListOrderHash). Null until the window's first checkpoint,
        // so a window without one allocates nothing.
        public List<ListHash> listHashes;
    }
}
