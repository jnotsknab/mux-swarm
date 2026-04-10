namespace MuxSwarm.Utils;

public static class AutoInject
{
    public enum Mode { Full, WorkingMemory, None, Custom }
    public static Mode Current { get; set; }
    public static string? CustomContent { get; set; }
}