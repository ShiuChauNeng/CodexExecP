using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CodexController.Workflow;

public sealed record WorkflowResult(AnalysisResult Analysis, ReviewResult Review);