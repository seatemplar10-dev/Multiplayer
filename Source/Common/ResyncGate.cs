namespace Multiplayer.Common;

// FIXES row 447: the DesyncedWindow's Try resync button is off, labelled "Saving desync report (Ns)", until the report
// is written or MaxWaitSeconds have passed
public static class ResyncGate
{
    public const float MaxWaitSeconds = 30f;

    public readonly record struct GateState(bool Enabled, string Label);

    public static GateState State(bool reportWritten, float secondsSinceDesync)
    {
        if (reportWritten || secondsSinceDesync >= MaxWaitSeconds) return new GateState(true, "");
        return new GateState(false, $"Saving desync report ({(int)secondsSinceDesync}s)");
    }
}
