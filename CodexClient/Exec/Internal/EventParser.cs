using System.Text.Json;

namespace CodexClient.Exec.Internal;

/// <summary>
/// 解析 Codex 輸出的 JSON 事件。
/// </summary>
internal static class EventParser
{
    // Methods
    /// <summary>
    /// 將單行 JSON 事件解析為執行事件。
    /// </summary>
    /// <param name="line">Codex 輸出的 JSON 行。</param>
    /// <returns>解析後的執行事件。</returns>
    public static ExecutionEvent Parse(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;

        JsonElement item = GetObject(root, "item");
        string? itemType = GetString(item, "type");

        string type = GetString(root, "type") ?? throw new JsonException("Missing event type.");
        string? errorMessage = type switch
        {
            "turn.failed" or "error" => GetString(GetObject(root, "error"), "message") ?? GetString(root, "message"),
            "item.completed" or "item.updated" or "item.started" when itemType == "error" => GetString(item, "message"),
            "item.completed" or "item.updated" or "item.started" => GetString(GetObject(item, "error"), "message"),
            _ => null
        };

        string? toolName = itemType switch
        {
            "mcp_tool_call" => GetToolName(item),
            "collab_tool_call" => GetString(item, "tool"),
            _ => null
        };

        return new ExecutionEvent(
            Type: type,
            ItemId: GetString(item, "id"),
            ItemType: itemType,
            Command: GetString(item, "command"),
            ExitCode: GetInt32(item, "exit_code"),
            Text: GetString(item, "text"),
            ThreadId: GetString(root, "thread_id"),
            ItemStatus: GetString(item, "status"),
            ErrorMessage: errorMessage,
            ToolName: toolName);
    }

    /// <summary>
    /// 從事件項目組合工具名稱。
    /// </summary>
    /// <param name="item">包含工具資訊的 JSON 項目。</param>
    /// <returns>組合後的工具名稱；沒有工具資訊時回傳 <see langword="null"/>。</returns>
    private static string? GetToolName(JsonElement item)
    {
        string? server = GetString(item, "server");
        string? tool = GetString(item, "tool");
        if (server is null)
        {
            return tool;
        }

        return tool is null ? server : $"{server}/{tool}";
    }

    /// <summary>
    /// 從 JSON 元素取得指定名稱的物件屬性。
    /// </summary>
    /// <param name="element">要讀取的 JSON 元素。</param>
    /// <param name="property">屬性名稱。</param>
    /// <returns>找到的 JSON 物件；找不到時回傳預設值。</returns>
    private static JsonElement GetObject(JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object)
        {
            return value;
        }

        return default;
    }

    /// <summary>
    /// 從 JSON 元素取得指定名稱的字串屬性。
    /// </summary>
    /// <param name="element">要讀取的 JSON 元素。</param>
    /// <param name="property">屬性名稱。</param>
    /// <returns>字串值；找不到或型別不符時回傳 <see langword="null"/>。</returns>
    private static string? GetString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>
    /// 從 JSON 元素取得指定名稱的 32 位元整數屬性。
    /// </summary>
    /// <param name="element">要讀取的 JSON 元素。</param>
    /// <param name="property">屬性名稱。</param>
    /// <returns>整數值；找不到或型別不符時回傳 <see langword="null"/>。</returns>
    private static int? GetInt32(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result) ? result : null;
    }
}