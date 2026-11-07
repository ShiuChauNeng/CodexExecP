namespace CodexController.Workflow;
/// <summary>
/// 表示對指定目標的程式碼分析結果。
/// </summary>
/// <param name="Target">分析的目標名稱。</param>
/// <param name="Found">是否找到分析目標。</param>
/// <param name="Summary">分析摘要。</param>
/// <param name="Evidence">支持分析結論的程式碼證據。</param>
/// <param name="Dependencies">分析出的相依元件。</param>
/// <param name="Risks">分析出的潛在風險。</param>
public sealed record AnalysisResult(string Target, bool Found, string Summary, string[] Evidence, string[] Dependencies, string[] Risks);
