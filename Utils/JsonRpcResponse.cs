namespace MuxSwarm.Utils;

public class JsonRpcResponse
{
    public string? Jsonrpc { get; set; }
    public int? Id { get; set; }
    public object? Result { get; set; }
    public object? Error { get; set; }
}