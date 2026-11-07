using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CodexController.Workflow;

/// <summary>
/// 表示完整工作流程的分析與審查結果。
/// </summary>
/// <param name="Analysis">程式碼分析結果。</param>
/// <param name="Review">分析結果的審查結果。</param>
public sealed record WorkflowResult(AnalysisResult Analysis, ReviewResult Review);
