using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace CodexController.Codex;

public static class CodexEventParser
{
    // Methods
    public static CodexExecutionEvent Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        string type = GetString(root, "type") ?? throw new JsonException("Missing event type.");
        string? threadId = GetString(root, "thread_id");
        if (!root.TryGetProperty("item", out JsonElement item) || item.ValueKind != JsonValueKind.Object)
        {
            return new CodexExecutionEvent(type, null, null, null, null, null, threadId);
        }

        return new CodexExecutionEvent(
            type,
            GetString(item, "id"),
            GetString(item, "type"),
            GetString(item, "command"),
            GetInt32(item, "exit_code"),
            GetString(item, "text"),
            threadId);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static int? GetInt32(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result))
        {
            return result;
        }

        return null;
    }
}
