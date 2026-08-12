using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime.CredentialManagement;
using Amazon.Runtime.Documents;
using JiraWorklogViewer.Models;

namespace JiraWorklogViewer.Services
{
    /// <summary>
    /// Drives a Bedrock Converse API tool-use loop so the user can issue a natural-language
    /// instruction (e.g. "analyze all [Scheduled - no work] tickets for Carrier [First Chicago]")
    /// instead of typing ticket numbers one at a time. Two tools are exposed: search_tickets
    /// (wraps JiraFetchService.SearchAssignedTicketsAsync) and analyze_ticket (wraps
    /// JiraFetchService.AnalyzeTicketFullAsync, the same pipeline the single-ticket fetch uses).
    /// </summary>
    public class JiraAgentService
    {
        private const int MaxToolTurns = 8;
        private static readonly RegionEndpoint Region = RegionEndpoint.USEast1;

        // Mirrors BedrockService's display name -> cross-region inference model ID map.
        private static readonly Dictionary<string, string> ModelIds = new Dictionary<string, string>
        {
            [ModelNames.BedrockSonnet] = "us.anthropic.claude-sonnet-4-6",
            [ModelNames.BedrockHaiku]  = "us.anthropic.claude-haiku-4-5-20251001-v1:0",
            [ModelNames.BedrockOpus]   = "us.anthropic.claude-opus-4-7",
        };

        private static readonly SystemContentBlock SystemPrompt = new SystemContentBlock
        {
            Text =
                "You are a jira-fetch assistant for an insurance-carrier RTR (Rate/Rules/Forms) integration team. " +
                "You help the user find and analyze Jira tickets from a natural-language instruction instead of " +
                "typing ticket numbers one at a time.\n\n" +
                "You have two tools:\n" +
                "- search_tickets: finds tickets assigned to the current user, optionally filtered by an exact " +
                "Jira status name and/or a carrier name.\n" +
                "- analyze_ticket: runs the full jira-fetch 7-point analysis pipeline on one ticket key (attachments, " +
                "comments, worklogs, linked bugs, similar prior work) and returns the result. This is slow — only " +
                "call it for tickets you actually need to reason about.\n\n" +
                "When the user asks a broad question (e.g. 'analyze all tickets in status X for carrier Y and tell " +
                "me if they can share one branch, and how much can be done in the base'), first call search_tickets " +
                "to find the matching tickets, then call analyze_ticket for each match, then synthesize one final " +
                "answer that directly addresses what the user asked. Base any feasibility judgment (shared branch, " +
                "base vs carrier/state-specific overlap) on the actual ticket content returned by analyze_ticket — " +
                "do not guess or invent tickets. If search_tickets finds nothing, say so plainly."
        };

        private readonly JiraFetchService _fetchService;
        private readonly BedrockService _bedrockService;

        public JiraAgentService(JiraFetchService fetchService, BedrockService bedrockService)
        {
            _fetchService   = fetchService;
            _bedrockService = bedrockService;
        }

        private static List<Tool> BuildTools() => new List<Tool>
        {
            new Tool
            {
                ToolSpec = new ToolSpecification
                {
                    Name = "search_tickets",
                    Description = "Search Jira tickets assigned to the current user, optionally filtered by exact " +
                        "status name and/or carrier name. Returns key, summary, status, and last-updated date for each match.",
                    InputSchema = new ToolInputSchema
                    {
                        Json = Document.FromObject(new
                        {
                            type = "object",
                            properties = new
                            {
                                status = new
                                {
                                    type = "string",
                                    description = "Exact Jira status name to filter by, e.g. 'Scheduled - no work'. Omit to search all statuses."
                                },
                                carrier = new
                                {
                                    type = "string",
                                    description = "Carrier name to filter by, e.g. 'First Chicago'. Omit to search all carriers."
                                },
                                max_results = new
                                {
                                    type = "integer",
                                    description = "Maximum number of tickets to return. Default 25."
                                }
                            }
                        })
                    }
                }
            },
            new Tool
            {
                ToolSpec = new ToolSpecification
                {
                    Name = "analyze_ticket",
                    Description = "Runs the full jira-fetch pipeline on a single ticket (attachments, comments, " +
                        "worklogs, linked bugs, similar prior work) and returns a 7-point analysis.",
                    InputSchema = new ToolInputSchema
                    {
                        Json = Document.FromObject(new
                        {
                            type = "object",
                            properties = new
                            {
                                ticket_key = new { type = "string", description = "Jira ticket key, e.g. CRM-1234." }
                            },
                            required = new[] { "ticket_key" }
                        })
                    }
                }
            }
        };

        /// <summary>
        /// Sends userMessage as the next turn in the given conversation history, running the
        /// tool-use loop until the model produces a final text answer (or the turn cap is hit).
        /// history is mutated in place so subsequent calls continue the same conversation.
        /// </summary>
        public async Task<string> RunAsync(
            List<Message> history,
            string userMessage,
            string model,
            Action<string> onLog,
            CancellationToken ct)
        {
            if (!ModelIds.TryGetValue(model, out string modelId))
                throw new Exception($"Unknown Bedrock model: {model}");

            var chain = new CredentialProfileStoreChain();
            if (!chain.TryGetAWSCredentials(_bedrockService.ProfileName, out var credentials))
                throw new Exception($"AWS profile '{_bedrockService.ProfileName}' not found. Run: aws configure sso");

            history.Add(new Message
            {
                Role = ConversationRole.User,
                Content = new List<ContentBlock> { new ContentBlock { Text = userMessage } }
            });

            using var client = new AmazonBedrockRuntimeClient(credentials, Region);

            for (int turn = 0; turn < MaxToolTurns; turn++)
            {
                ct.ThrowIfCancellationRequested();

                var request = new ConverseRequest
                {
                    ModelId    = modelId,
                    Messages   = history,
                    System     = new List<SystemContentBlock> { SystemPrompt },
                    ToolConfig = new ToolConfiguration { Tools = BuildTools() }
                };

                var response = await client.ConverseAsync(request, ct);
                var assistantMessage = response.Output?.Message;
                if (assistantMessage == null) return "(no response)";

                history.Add(assistantMessage);

                if (response.StopReason != StopReason.Tool_use)
                {
                    return string.Join("\n", (assistantMessage.Content ?? new List<ContentBlock>())
                        .Where(c => c.Text != null)
                        .Select(c => c.Text));
                }

                var toolUses = (assistantMessage.Content ?? new List<ContentBlock>())
                    .Where(c => c.ToolUse != null)
                    .Select(c => c.ToolUse)
                    .ToList();

                var resultContent = new List<ContentBlock>();
                foreach (var toolUse in toolUses)
                {
                    ct.ThrowIfCancellationRequested();
                    string resultText;
                    try
                    {
                        resultText = await ExecuteToolAsync(toolUse, model, onLog, ct);
                    }
                    catch (Exception ex)
                    {
                        resultText = $"Error: {ex.Message}";
                    }

                    resultContent.Add(new ContentBlock
                    {
                        ToolResult = new ToolResultBlock
                        {
                            ToolUseId = toolUse.ToolUseId,
                            Content   = new List<ToolResultContentBlock> { new ToolResultContentBlock { Text = resultText } }
                        }
                    });
                }

                history.Add(new Message { Role = ConversationRole.User, Content = resultContent });
            }

            return "(stopped after reaching the tool-call limit — the request may be too broad; try narrowing it.)";
        }

        private async Task<string> ExecuteToolAsync(ToolUseBlock toolUse, string model, Action<string> onLog, CancellationToken ct)
        {
            var input = toolUse.Input.IsDictionary()
                ? toolUse.Input.AsDictionary()
                : new Dictionary<string, Document>();

            string GetString(string key) =>
                input.TryGetValue(key, out var v) && v.IsString() ? v.AsString() : null;

            switch (toolUse.Name)
            {
                case "search_tickets":
                {
                    string status  = GetString("status");
                    string carrier = GetString("carrier");
                    int maxResults = 25;
                    if (input.TryGetValue("max_results", out var mr))
                    {
                        if (mr.IsInt()) maxResults = mr.AsInt();
                        else if (mr.IsDouble()) maxResults = (int)mr.AsDouble();
                    }

                    onLog?.Invoke($"🔧 search_tickets(status=\"{status}\", carrier=\"{carrier}\")...");
                    var matches = await _fetchService.SearchAssignedTicketsAsync(status, carrier, maxResults);
                    onLog?.Invoke($"   → {matches.Count} match(es)");

                    return matches.Count == 0
                        ? "No matching tickets found."
                        : string.Join("\n", matches.Select(m => $"- [{m.Key}] {m.Summary} (status: {m.Status}, updated: {m.Updated})"));
                }

                case "analyze_ticket":
                {
                    string key = GetString("ticket_key");
                    if (string.IsNullOrWhiteSpace(key)) return "Error: ticket_key is required.";

                    onLog?.Invoke($"🔧 analyze_ticket({key})...");
                    string analysis = await _fetchService.AnalyzeTicketFullAsync(key, _bedrockService, model, ct);
                    onLog?.Invoke($"   ✔ {key} analyzed");
                    return analysis;
                }

                default:
                    return $"Error: unknown tool '{toolUse.Name}'.";
            }
        }
    }
}
