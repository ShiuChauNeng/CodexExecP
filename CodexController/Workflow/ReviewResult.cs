using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace CodexController.Workflow;

public sealed record ReviewResult(bool Approved, string Summary, string[] Recommendations, string[] Concerns);