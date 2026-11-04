namespace CodexController.Workflow;


using System.Text.Json.Serialization;

public sealed record AnalysisResult(string Target, bool Found, string Summary, string[] Evidence, string[] Dependencies, string[] Risks);