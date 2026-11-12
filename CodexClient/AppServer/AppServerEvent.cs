using System.Text.Json;

namespace CodexClient.AppServer;

public sealed record AppServerEvent(string Method, JsonElement Params);