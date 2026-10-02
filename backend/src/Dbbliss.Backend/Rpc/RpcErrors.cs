using System.Text.Json.Nodes;

namespace Dbbliss.Backend.Rpc;

public static class RpcErrors
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int Internal = -32603;

    // Application errors.
    public const int UnknownConnection = 1001;
    public const int ConnectionBusy = 1002;
    public const int TransactionOpen = 1003;
    public const int CredentialNotFound = 1004;
    public const int Database = 1005;
    public const int ShuttingDown = 1006;
}

public sealed class RpcException(int code, string message, JsonNode? data = null) : Exception(message)
{
    public int Code { get; } = code;
    public JsonNode? ErrorData { get; } = data;
}
