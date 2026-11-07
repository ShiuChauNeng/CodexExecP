using System.Text.Json;

namespace CodexClient.Exec.Internal;

internal static class EventParser
{
    // Methods
    public static ExecutionEvent Parse(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;

        string type = GetString(root, "type") ?? throw new JsonException("Missing event type.");

        JsonElement item = GetObject(root, "item");

        string? itemType = GetString(item, "type");

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

    private static JsonElement GetObject(JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object)
        {
            return value;
        }

        return default;
    }

    private static string? GetString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static int? GetInt32(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result) ? result : null;
    }
}