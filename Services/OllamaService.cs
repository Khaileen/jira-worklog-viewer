using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JiraWorklogViewer.Models;
using Newtonsoft.Json;

namespace JiraWorklogViewer.Services
{
    public class OllamaService
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;

        public OllamaService(string baseUrl = "http://localhost:11434")
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(5)
            };
        }

        /// <summary>
        /// Checks if Ollama is running and reachable.
        /// </summary>
        public async Task<bool> IsRunningAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync(_baseUrl + "/api/tags");
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Returns all pulled models available in Ollama.
        /// </summary>
        public async Task<List<OllamaModel>> GetAvailableModelsAsync()
        {
            var models = new List<OllamaModel>();

            try
            {
                var response = await _httpClient.GetAsync(_baseUrl + "/api/tags");
                if (!response.IsSuccessStatusCode) return models;

                var json = await response.Content.ReadAsStringAsync();
                var tags = JsonConvert.DeserializeObject<OllamaTagsResponse>(json);

                if (tags?.models != null)
                {
                    foreach (var m in tags.models)
                    {
                        models.Add(new OllamaModel { Name = m.name });
                    }
                }
            }
            catch
            {
                // Ollama not running — return empty list
            }

            return models;
        }

        /// <summary>
        /// Sends a prompt to Ollama and returns the full analysis result with metrics.
        /// </summary>
        public async Task<OllamaAnalysisResult> AnalyzeAsync(
            string prompt,
            string model,
            CancellationToken cancellationToken = default,
            int numCtx = 0)
        {
            var result = new OllamaAnalysisResult { Model = model };
            var sw = Stopwatch.StartNew();

            try
            {
                var request = new OllamaChatRequest
                {
                    model = model,
                    messages = new List<OllamaChatMessage>
                    {
                        new OllamaChatMessage { role = "user", content = prompt }
                    },
                    stream = false
                };

                if (numCtx > 0)
                    request.options = new Dictionary<string, object> { { "num_ctx", numCtx } };

                var json = JsonConvert.SerializeObject(request, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(
                    _baseUrl + "/api/chat", content, cancellationToken);

                sw.Stop();

                if (!response.IsSuccessStatusCode)
                {
                    result.Success = false;
                    result.ErrorMessage = string.Format(
                        "Ollama error {0}: {1}", (int)response.StatusCode, response.ReasonPhrase);
                    return result;
                }

                var responseJson = await response.Content.ReadAsStringAsync();
                var chatResponse = JsonConvert.DeserializeObject<OllamaChatResponse>(responseJson);

                result.Success      = true;
                result.Content      = chatResponse.message?.content ?? string.Empty;
                result.ResponseTimeSec = sw.Elapsed.TotalSeconds;
                result.InputTokens  = chatResponse.prompt_eval_count;
                result.OutputTokens = chatResponse.eval_count;
                result.TokensPerSec = chatResponse.eval_duration > 0
                    ? Math.Round(chatResponse.eval_count / (chatResponse.eval_duration / 1e9), 1)
                    : 0;
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.ErrorMessage = "Analysis cancelled.";
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Formats metrics from an analysis result into a readable summary string.
        /// </summary>
        public static string FormatMetrics(OllamaAnalysisResult result)
        {
            if (!result.Success)
                return string.Format("⚠ Failed: {0}", result.ErrorMessage);

            return string.Format(
                "⏱ {0:F1}s  |  🔤 {1} tok/s  |  📥 {2} in  |  📤 {3} out  |  💰 ${4:F6} Claude equiv",
                result.ResponseTimeSec,
                result.TokensPerSec,
                result.InputTokens,
                result.OutputTokens,
                (result.InputTokens * 3.0 + result.OutputTokens * 15.0) / 1_000_000.0);
        }
    }
}
