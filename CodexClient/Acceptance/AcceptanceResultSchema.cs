using System.Text.Json;

namespace CodexClient.Acceptance;

/// <summary>指定給驗收步驟的 DataSchema，不包含 TaskRunner 外層的 state/message/issues。</summary>
public static class AcceptanceResultSchema
{
    public static JsonElement Value { get; } = JsonSerializer.Deserialize<JsonElement>(
        """
        {
          "type": "object",
          "properties": {
            "checks": {
              "type": "array",
              "description": "逐一回報呼叫端提供的驗收項目，不得省略無法驗證的項目。",
              "items": {
                "type": "object",
                "properties": {
                  "criterionId": { "type": "string" },
                  "status": { "type": "string", "enum": ["Passed", "Failed", "NotEvaluated"], "description": "Passed=符合規格；Failed=不符合規格；NotEvaluated=未完成檢查或無法判斷，不可自行當作通過。" },
                  "evidence": { "type": "string", "description": "具體規格條文、程式位置或測試結果；無法驗證時說明原因。" }
                },
                "required": ["criterionId", "status", "evidence"],
                "additionalProperties": false
              }
            },
            "findings": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "id": { "type": "string" },
                  "severity": { "type": "string", "enum": ["Error", "Warning", "Info"], "description": "Error=必須修正，包含違反必要規格或必要測試失敗；Warning=未違反必要條件的風險；Info=改善建議。不得將必要條件違規降為 Warning。" },
                  "criterionId": { "type": ["string", "null"] },
                  "message": { "type": "string" },
                  "evidence": { "type": "string" },
                  "suggestedFix": { "type": ["string", "null"] }
                },
                "required": ["id", "severity", "criterionId", "message", "evidence", "suggestedFix"],
                "additionalProperties": false
              }
            },
            "reportPath": { "type": "string" }
          },
          "required": ["checks", "findings", "reportPath"],
          "additionalProperties": false
        }
        """);
}
