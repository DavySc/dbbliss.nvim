using System.Text.Json.Nodes;
using Dbbliss.Backend.Rpc;

namespace Dbbliss.Backend;

/// <summary>
/// Resolves a password reference; passwords never appear in the Lua config or on the wire from Lua.
/// Phase 0 supports <c>{ "env": "NAME" }</c> only. Windows Credential Manager, <c>pass</c> and
/// libsecret arrive in Phase 1 behind this same shape.
/// </summary>
public static class Credentials
{
    public static string? Resolve(JsonNode? reference)
    {
        if (reference is null) return null;
        if (reference is not JsonObject obj)
        {
            throw new RpcException(RpcErrors.InvalidParams, "password must be a reference such as { env = \"NAME\" }, never a literal.");
        }
        if (obj["env"]?.GetValue<string>() is { } name)
        {
            return Environment.GetEnvironmentVariable(name)
                ?? throw new RpcException(RpcErrors.CredentialNotFound, $"Environment variable {name} is not set.");
        }
        throw new RpcException(RpcErrors.InvalidParams, "Unsupported password reference; Phase 0 supports { env = \"NAME\" }.");
    }
}
