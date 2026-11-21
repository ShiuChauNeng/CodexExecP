namespace CodexClient.Models;

/// <summary>
/// 常用流程名稱，可供步驟、返回目標與決策群組共用；名稱本身不決定執行行為。
/// </summary>
public static class WorkflowNames
{
    /// <summary>
    /// 產生或整理規格。
    /// </summary>
    public const string Spec = "Spec";

    /// <summary>
    /// 規劃工作項目與執行順序。
    /// </summary>
    public const string Plan = "Plan";

    /// <summary>
    /// 設計架構、介面與資料結構。
    /// </summary>
    public const string Design = "Design";

    /// <summary>
    /// 依規格實作功能。
    /// </summary>
    public const string Implement = "Implement";

    /// <summary>
    /// 建置專案並檢查編譯結果。
    /// </summary>
    public const string Build = "Build";

    /// <summary>
    /// 撰寫或執行測試。
    /// </summary>
    public const string Test = "Test";

    /// <summary>
    /// 驗收實作是否符合規格與已確認需求。
    /// </summary>
    public const string Acceptance = "Acceptance";

    /// <summary>
    /// 調整程式碼風格與格式。
    /// </summary>
    public const string CodingStyle = "CodingStyle";

    /// <summary>
    /// 審查程式碼的正確性、風險與可維護性。
    /// </summary>
    public const string CodeReview = "CodeReview";

    /// <summary>
    /// 在維持既有行為下調整程式結構。
    /// </summary>
    public const string Refactor = "Refactor";

    /// <summary>
    /// 撰寫或更新使用與維護文件。
    /// </summary>
    public const string Documentation = "Documentation";

    /// <summary>
    /// 彙整執行結果與報表。
    /// </summary>
    public const string Report = "Report";

    /// <summary>
    /// 以範圍與名稱組成固定的「範圍.名稱」，例如 FeatureA.Acceptance。
    /// 相同輸入回傳相同名稱，不自動編號；呼叫端仍須確保步驟名稱唯一。
    /// </summary>
    /// <param name="scope">功能或階段的識別名稱。</param>
    /// <param name="name">常用名稱或自訂名稱。</param>
    /// <returns>包含範圍的名稱。</returns>
    public static string Create(string scope, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return $"{scope}.{name}";
    }
}
