using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime.CredentialManagement;
using JiraWorklogViewer.Models;

namespace JiraWorklogViewer.Services
{
    public class BedrockService
    {
        private readonly string _profileName;
        private static readonly RegionEndpoint Region = RegionEndpoint.USEast1;

        // Display name → Bedrock cross-region inference model ID
        private static readonly Dictionary<string, string> ModelIds = new Dictionary<string, string>
        {
            [ModelNames.BedrockSonnet] = "us.anthropic.claude-sonnet-4-6",
            [ModelNames.BedrockHaiku]  = "us.anthropic.claude-haiku-4-5-20251001-v1:0",
            [ModelNames.BedrockOpus]   = "us.anthropic.claude-opus-4-7",
        };

        public BedrockService(string profileName)
        {
            _profileName = profileName;
        }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(_profileName);

        public async Task<OllamaAnalysisResult> AnalyzeAsync(
            string prompt,
            string displayModelName,
            CancellationToken cancellationToken = default)
        {
            var result = new OllamaAnalysisResult { Model = displayModelName };
            var sw = Stopwatch.StartNew();

            try
            {
                if (!ModelIds.TryGetValue(displayModelName, out string modelId))
                {
                    result.Success = false;
                    result.ErrorMessage = $"Unknown Bedrock model: {displayModelName}";
                    return result;
                }

                var chain = new CredentialProfileStoreChain();
                if (!chain.TryGetAWSCredentials(_profileName, out var credentials))
                {
                    result.Success = false;
                    result.ErrorMessage = $"AWS profile '{_profileName}' not found. Run: aws configure sso";
                    return result;
                }

                using var client = new AmazonBedrockRuntimeClient(credentials, Region);

                var request = new ConverseRequest
                {
                    ModelId  = modelId,
                    Messages = new List<Message>
                    {
                        new Message
                        {
                            Role    = ConversationRole.User,
                            Content = new List<ContentBlock>
                            {
                                new ContentBlock { Text = prompt }
                            }
                        }
                    }
                };

                var response = await client.ConverseAsync(request, cancellationToken);
                sw.Stop();

                string text = response.Output?.Message?.Content?[0]?.Text ?? string.Empty;

                result.Success         = true;
                result.Content         = text;
                result.ResponseTimeSec = sw.Elapsed.TotalSeconds;
                result.InputTokens     = response.Usage?.InputTokens  ?? 0;
                result.OutputTokens    = response.Usage?.OutputTokens ?? 0;
                result.TokensPerSec    = result.ResponseTimeSec > 0
                    ? Math.Round(result.OutputTokens / result.ResponseTimeSec, 1)
                    : 0;
            }
            catch (OperationCanceledException)
            {
                result.Success      = false;
                result.ErrorMessage = "Cancelled.";
            }
            catch (Exception ex)
            {
                result.Success      = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }
    }
}
