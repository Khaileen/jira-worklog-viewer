using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using JiraWorklogViewer.Models;
using JiraWorklogViewer.Services;

namespace JiraWorklogViewer
{
    public partial class JiraFetchWindow : Window
    {
        private readonly JiraFetchService _fetchService;
        private readonly OllamaService    _ollamaService;
        private BedrockService            _bedrockService;
        private CancellationTokenSource   _cts;
        private string _lastOutputFile;

        public JiraFetchWindow(JiraFetchService fetchService, OllamaService ollamaService)
        {
            InitializeComponent();
            _fetchService  = fetchService;
            _ollamaService = ollamaService;

            Loaded += async (s, e) =>
            {
                txtBedrockProfile.Text = App.Settings.BedrockProfile;
                RebuildBedrockService();
                await LoadModelsAsync();
            };
        }

        private void RebuildBedrockService()
        {
            _bedrockService = new BedrockService(txtBedrockProfile.Text.Trim());
        }

        private async void TxtBedrockProfile_LostFocus(object sender, System.Windows.RoutedEventArgs e)
        {
            App.Settings.BedrockProfile = txtBedrockProfile.Text.Trim();
            App.Settings.Save();
            RebuildBedrockService();
            await LoadModelsAsync();
        }

        private async Task LoadModelsAsync()
        {
            cboModel.Items.Clear();

            // 1. Claude (Prepare for Claude) — always first, always available
            cboModel.Items.Add(ModelNames.Claude);

            // 2. Bedrock models — shown if profile is configured
            if (_bedrockService.IsConfigured)
            {
                cboModel.Items.Add(ModelNames.BedrockSonnet);
                cboModel.Items.Add(ModelNames.BedrockHaiku);
                cboModel.Items.Add(ModelNames.BedrockOpus);
            }

            // 3. Ollama models — shown if Ollama is running
            var ollamaModels = await _ollamaService.GetAvailableModelsAsync();
            foreach (var m in ollamaModels) cboModel.Items.Add(m.Name);

            cboModel.SelectedItem = ModelNames.Claude;

            if (!_bedrockService.IsConfigured && ollamaModels.Count == 0)
                txtStatus.Text = "No models available — enter an AWS Profile or start Ollama.";
            else if (!_bedrockService.IsConfigured)
                txtStatus.Text = "Ollama ready. Enter an AWS Profile to enable Bedrock models.";
            else if (ollamaModels.Count == 0)
                txtStatus.Text = "Bedrock ready. Ollama not running.";
        }

        private async void BtnFetch_Click(object sender, RoutedEventArgs e)
        {
            string ticketNumber = txtTicket.Text.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(ticketNumber))
            {
                MessageBox.Show("Please enter a ticket number.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string model = cboModel.SelectedItem?.ToString();
            bool claudeMode  = ModelNames.IsClaude(model);
            bool bedrockMode = ModelNames.IsBedrock(model);

            if (!claudeMode && !bedrockMode && (string.IsNullOrEmpty(model) || model.StartsWith("(")))
            {
                MessageBox.Show("Please select a valid model.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _cts = new CancellationTokenSource();
            SetUIRunning(true);
            txtAnalysis.Text = string.Empty;
            txtInfo.Text     = string.Empty;
            txtLog.Text      = string.Empty;
            txtMetrics.Text  = string.Empty;
            _lastOutputFile  = null;

            try
            {
                // Step 1: Fetch ticket
                Log($"Fetching {ticketNumber}...");
                SetStatus("Fetching ticket...");
                SetProgress(1, 6, "Fetching");
                var ticket = await Task.Run(() => _fetchService.FetchTicketAsync(ticketNumber));
                Log($"✔ {ticket.Carrier} | {ticket.Status}");
                foreach (var att in ticket.Attachments)
                    Log($"  → {att.Filename} ({att.Status})");

                // Step 2: Read text attachments
                SetStatus("Reading attachments...");
                SetProgress(2, 6, "Attachments");
                var textAttachments = await Task.Run(() => _fetchService.ReadTextAttachments(ticket));
                if (textAttachments.Count > 0)
                    Log($"✔ Read {textAttachments.Count} text attachment(s)");

                // Step 3: Convert Excel
                SetProgress(3, 6, "Excel");
                var excelConversions = await Task.Run(() => _fetchService.ConvertExcelAttachments(ticket));
                foreach (var (fn, jp) in excelConversions)
                    Log($"✔ Converted: {fn}");

                // Step 4: Search patterns
                SetStatus("Searching patterns...");
                SetProgress(4, 6, "Patterns");
                string keywords = string.Join(" ", ticket.Summary.Split(' ')
                    .Where(w => w.Length > 4).Take(3));
                var patterns = await Task.Run(() => _fetchService.SearchPatterns(keywords));
                Log($"✔ Patterns: {patterns.Count} match(es)");

                // Step 5: Find similar tickets
                SetStatus("Finding similar tickets...");
                SetProgress(5, 6, "Similar tickets");
                var similar = await _fetchService.FindSimilarTicketsAsync(
                    ticketNumber, ticket.Summary, ticket.Description);
                Log($"✔ Similar: {similar.JiraMatches.Count} Jira, {similar.GitLabMrMatches.Count} MRs");

                // Step 6: Claude mode — skip summarization, save raw MD + Claude instruction
                if (claudeMode)
                {
                    string analysisInstruction = "Using the ticket data above, complete the 7-point analysis:\n" +
                        "### 1. Summarized Issue\n### 2. Responsibility\n### 3. Affected Scope\n" +
                        "### 4. Current Status\n### 5. Open Items / Next Steps\n" +
                        "### 6. Similar Prior Work\n### 7. Known Pattern?";

                    _lastOutputFile = await Task.Run(() =>
                        _fetchService.BuildAndSaveMd(ticket, textAttachments, patterns, similar,
                            string.Empty, ModelNames.Claude, excelConversions));

                    // Rewrite the 7-point section with Claude instruction
                    string mdContent = System.IO.File.ReadAllText(_lastOutputFile);
                    mdContent = mdContent.Replace(
                        "> Paste this file into Claude Code and ask: \"Complete the 7-point analysis based on the above.\"",
                        "> **Claude Mode** — paste this file into Claude Code or Claude.ai and ask:\n>\n> _\"" + analysisInstruction + "\"_");
                    System.IO.File.WriteAllText(_lastOutputFile, mdContent, System.Text.Encoding.UTF8);

                    txtAnalysis.Text = "Claude mode — raw ticket data saved.\n\nOpen the MD file and paste into Claude to complete the 7-point analysis.";
                    txtInfo.Text = BuildTicketInfo(ticket, patterns, similar);
                    tabOutput.SelectedItem = tabAnalysis;
                    txtMetrics.Text = "Claude mode — no model call made.";
                    Log($"✔ Saved: {_lastOutputFile}");
                    SetStatus($"Ready for Claude — {_lastOutputFile}");
                    btnOpenFolder.IsEnabled = true;
                    btnOpenVsCode.IsEnabled = true;
                    btnCopyAll.IsEnabled    = true;
                    SetUIRunning(false);
                    return;
                }

                // Step 6: Summarize comments per-comment (better context coverage)
                var commentSummaries = new List<string>();
                if (ticket.Comments.Count > 0 && !bedrockMode)
                {
                    SetStatus($"Summarizing {ticket.Comments.Count} comment(s)...");
                    SetProgress(6, 7, "Comments");
                    int ci = 0;
                    foreach (var comment in ticket.Comments)
                    {
                        _cts.Token.ThrowIfCancellationRequested();
                        ci++;
                        SetStatus($"Summarizing comment {ci}/{ticket.Comments.Count} ({comment.Author})...");
                        var cPrompt = _fetchService.BuildCommentSummaryPrompt(comment);
                        var cResult = await _ollamaService.AnalyzeAsync(cPrompt, model, _cts.Token);
                        commentSummaries.Add(cResult.Success ? cResult.Content.Trim() : comment.Body?.Substring(0, Math.Min(200, comment.Body?.Length ?? 0)) ?? "(no content)");
                        if (cResult.Success)
                            Log($"  → Comment {ci} summarized ({cResult.OutputTokens} tokens)");
                    }
                    Log($"✔ {ticket.Comments.Count} comment(s) summarized");
                }

                // Step 7: Run analysis (Bedrock or Ollama)
                SetStatus($"Running analysis with {model}...");
                SetProgress(7, 7, "Analysis");
                var analysisPrompt = _fetchService.Build7PointPrompt(ticket, textAttachments, patterns, similar, commentSummaries);

                OllamaAnalysisResult result;
                if (bedrockMode)
                    result = await _bedrockService.AnalyzeAsync(analysisPrompt, model, _cts.Token);
                else
                    result = await _ollamaService.AnalyzeAsync(analysisPrompt, model, _cts.Token);

                string analysisText = result.Success ? result.Content : string.Empty;
                if (!result.Success)
                    Log($"⚠ Analysis failed: {result.ErrorMessage}");
                else
                    Log($"✔ Analysis complete ({result.ResponseTimeSec:F1}s, {result.TokensPerSec} tok/s)");

                // Save MD
                _lastOutputFile = await Task.Run(() =>
                    _fetchService.BuildAndSaveMd(ticket, textAttachments, patterns, similar,
                        analysisText, model, excelConversions));
                Log($"✔ Saved: {_lastOutputFile}");

                // Log metrics
                if (result.Success)
                    _fetchService.LogMetrics(ticketNumber, ticket.Carrier, _lastOutputFile, result);

                // Display
                txtAnalysis.Text = analysisText;
                tabOutput.SelectedItem = tabAnalysis;

                txtInfo.Text = BuildTicketInfo(ticket, patterns, similar);

                if (result.Success)
                    txtMetrics.Text = OllamaService.FormatMetrics(result);

                SetStatus($"Done — {ticketNumber} | {ticket.Carrier} | {ticket.Status}");
                btnOpenFolder.IsEnabled = true;
                btnOpenVsCode.IsEnabled = _lastOutputFile != null;
                btnCopyAll.IsEnabled    = _lastOutputFile != null;
            }
            catch (OperationCanceledException)
            {
                SetStatus("Cancelled.");
                Log("Cancelled.");
            }
            catch (Exception ex)
            {
                SetStatus($"Error: {ex.Message}");
                Log($"✖ Error: {ex.Message}");
                MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetUIRunning(false);
            }
        }

        private string BuildTicketInfo(JiraFetchTicket ticket,
            System.Collections.Generic.List<string> patterns,
            JiraFetchSimilarTickets similar)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Ticket  : {ticket.TicketNumber}");
            sb.AppendLine($"Summary : {ticket.Summary}");
            sb.AppendLine($"Carrier : {ticket.Carrier}");
            sb.AppendLine($"Status  : {ticket.Status}");
            sb.AppendLine($"Assignee: {ticket.Assignee}");
            sb.AppendLine($"Priority: {ticket.Priority}");
            sb.AppendLine($"Created : {ticket.Created} | Updated: {ticket.Updated}");
            sb.AppendLine();
            sb.AppendLine($"Attachments: {ticket.Attachments.Count}");
            foreach (var a in ticket.Attachments)
                sb.AppendLine($"  - {a.Filename} ({a.Status})");
            sb.AppendLine();
            sb.AppendLine($"Comments: {ticket.Comments.Count}");
            sb.AppendLine($"Patterns matched: {patterns.Count}");
            sb.AppendLine();
            sb.AppendLine($"Similar Jira tickets: {similar.JiraMatches.Count}");
            foreach (var m in similar.JiraMatches)
                sb.AppendLine($"  [{m.Key}] {m.Summary} ({m.Status})");
            sb.AppendLine();
            sb.AppendLine($"Similar GitLab MRs: {similar.GitLabMrMatches.Count}");
            foreach (var mr in similar.GitLabMrMatches)
                sb.AppendLine($"  !{mr.Iid}: {mr.Title} — {mr.Url}");
            sb.AppendLine();
            sb.AppendLine($"Output: {ticket.OutputDir}");
            return sb.ToString();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        private void BtnCopyAll_Click(object sender, RoutedEventArgs e)
        {
            if (_lastOutputFile == null || !File.Exists(_lastOutputFile)) return;
            try
            {
                string content = File.ReadAllText(_lastOutputFile);
                Clipboard.SetText(content);
                SetStatus("Copied entire MD to clipboard.");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to copy: " + ex.Message, "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_lastOutputFile != null)
                Process.Start("explorer.exe", Path.GetDirectoryName(_lastOutputFile));
        }

        private void BtnOpenVsCode_Click(object sender, RoutedEventArgs e)
        {
            if (_lastOutputFile == null) return;
            try
            {
                // Try VS Code first
                Process.Start(new ProcessStartInfo("code", $"\"{_lastOutputFile}\"")
                {
                    UseShellExecute = true
                });
            }
            catch
            {
                try
                {
                    // Fallback: open with default app via shell
                    Process.Start(new ProcessStartInfo(_lastOutputFile)
                    {
                        UseShellExecute = true
                    });
                }
                catch { }
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            Close();
        }

        private void Log(string msg)
        {
            txtLog.AppendText(msg + "\n");
            txtLog.ScrollToEnd();
        }

        private void SetStatus(string msg) => txtStatus.Text = msg;

        private void SetProgress(int current, int total, string phase)
        {
            progressBar.Visibility      = Visibility.Visible;
            txtProgressLabel.Visibility = Visibility.Visible;
            progressBar.Maximum         = total;
            progressBar.Value           = current;
            txtProgressLabel.Text       = $"{phase}  {current}/{total}";
        }

        private void SetUIRunning(bool running)
        {
            btnFetch.IsEnabled  = !running;
            btnCancel.IsEnabled = running;
            cboModel.IsEnabled  = !running;
            txtTicket.IsEnabled = !running;
            if (!running)
            {
                progressBar.Visibility      = Visibility.Collapsed;
                txtProgressLabel.Visibility = Visibility.Collapsed;
            }
        }
    }
}
