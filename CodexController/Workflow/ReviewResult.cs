using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace CodexController.Workflow;

/// <summary>
/// 表示對分析結果的審查結果。
/// </summary>
/// <param name="Approved">審查是否通過。</param>
/// <param name="Summary">審查摘要。</param>
/// <param name="Recommendations">審查建議。</param>
/// <param name="Concerns">尚未解決的疑慮。</param>
public sealed record ReviewResult(bool Approved, string Summary, string[] Recommendations, string[] Concerns);
