using System.Text.Json;

namespace CodexClient.AppServer;

/// <summary>
/// 表示 App Server 傳送的 notification 事件。
/// </summary>
/// <param name="Method">事件方法名稱。</param>
/// <param name="Params">事件參數。</param>
public sealed record AppServerEvent(string Method, JsonElement Params);
