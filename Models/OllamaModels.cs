using System.Collections.Generic;

namespace JiraWorklogViewer.Models
{
    public class OllamaModel
    {
        public string Name { get; set; }
        public string DisplayName => Name;
    }

    public static class ModelNames
    {
        public const string Claude        = "Claude (Prepare for Claude)";
        public const string BedrockSonnet = "Bedrock: Claude Sonnet 4.6";
        public const string BedrockHaiku  = "Bedrock: Claude Haiku 4.5";
        public const string BedrockOpus   = "Bedrock: Claude Opus 4.7";

        public static bool IsClaude(string model) =>
            string.Equals(model, Claude, System.StringComparison.OrdinalIgnoreCase);

        public static bool IsBedrock(string model) =>
            model != null && model.StartsWith("Bedrock:", System.StringComparison.OrdinalIgnoreCase);
    }

    public class OllamaTagsResponse
    {
        public List<OllamaModelInfo> models { get; set; }
    }

    public class OllamaModelInfo
    {
        public string name { get; set; }
        public string modified_at { get; set; }
        public long size { get; set; }
    }

    public class OllamaChatRequest
    {
        public string model { get; set; }
        public List<OllamaChatMessage> messages { get; set; }
        public bool stream { get; set; } = false;
        public Dictionary<string, object> options { get; set; }
    }

    public class OllamaChatMessage
    {
        public string role { get; set; }
        public string content { get; set; }
    }

    public class OllamaChatResponse
    {
        public OllamaChatMessage message { get; set; }
        public string model { get; set; }
        public long eval_count { get; set; }
        public long prompt_eval_count { get; set; }
        public long eval_duration { get; set; }
    }

    public class OllamaAnalysisResult
    {
        public string Model { get; set; }
        public string Content { get; set; }
        public double ResponseTimeSec { get; set; }
        public double TokensPerSec { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
    }
}
