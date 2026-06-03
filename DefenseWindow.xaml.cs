using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using JiraWorklogViewer.Models;
using JiraWorklogViewer.Services;
using Microsoft.Win32;

namespace JiraWorklogViewer
{
    public partial class DefenseWindow : Window
    {
        private readonly JiraService _jiraService;
        private readonly OllamaService _ollamaService;
        private CancellationTokenSource _cts;
        private string _lastSummary;
        private string _lastTechnical;

        // Observable list of ticket entries for the ItemsControl
        private ObservableCollection<TicketEntry> _tickets = new ObservableCollection<TicketEntry>();

        public class TicketEntry
        {
            public string Key { get; set; }
        }

        public DefenseWindow(JiraService jiraService, OllamaService ollamaService)
        {
            InitializeComponent();

            _jiraService = jiraService;
            _ollamaService = ollamaService;

            // Start with one empty ticket entry
            _tickets.Add(new TicketEntry { Key = string.Empty });
            lstTickets.ItemsSource = _tickets;

            Loaded += async (s, e) => await LoadModelsAsync();
        }

        private async Task LoadModelsAsync()
        {
            var models = await _ollamaService.GetAvailableModelsAsync();

            if (models.Count == 0)
            {
                cboModel.Items.Add("(Ollama not running)");
                cboModel.SelectedIndex = 0;
                cboModel.IsEnabled = false;
                btnAnalyze.IsEnabled = false;
                txtStatus.Text = "⚠ Ollama is not running. Start Ollama and reopen this window.";
                return;
            }

            foreach (var m in models)
                cboModel.Items.Add(m.Name);

            var preferred = new[] { "mistral", "qwen2.5:7b", "qwen2.5:14b", "qwen2.5" };
            string selected = preferred.FirstOrDefault(p => models.Any(m => m.Name == p)) ?? models[0].Name;
            cboModel.SelectedItem = selected;
        }

        private void BtnAddTicket_Click(object sender, RoutedEventArgs e)
        {
            _tickets.Add(new TicketEntry { Key = string.Empty });
        }

        private void BtnRemoveTicket_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is TicketEntry entry)
            {
                if (_tickets.Count > 1)
                    _tickets.Remove(entry);
            }
        }

        private async void BtnAnalyze_Click(object sender, RoutedEventArgs e)
        {
            var ticketKeys = _tickets
                .Select(t => t.Key.Trim().ToUpperInvariant())
                .Where(k => !string.IsNullOrEmpty(k))
                .Distinct()
                .ToList();

            if (ticketKeys.Count == 0)
            {
                MessageBox.Show("Please enter at least one ticket key.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string model = cboModel.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(model) || model.StartsWith("("))
            {
                MessageBox.Show("Please select a valid Ollama model.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }


            _cts = new CancellationTokenSource();
            SetUIAnalyzing(true);
            txtSummary.Text   = string.Empty;
            txtTechnical.Text = string.Empty;
            txtMetrics.Text   = string.Empty;
            _lastSummary      = null;
            _lastTechnical    = null;

            int totalInputTokens = 0, totalOutputTokens = 0;
            double totalResponseSec = 0;

            try
            {
                // Step 1: Fetch all data for each ticket
                var allWorklogs     = new List<WorklogEntry>();
                var allComments     = new List<TicketComment>();
                var allStatusChanges = new List<TicketStatusChange>();
                var ticketDetails   = new Dictionary<string, TicketDetails>();

                foreach (var key in ticketKeys)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    SetStatus(string.Format("Fetching {0}...", key));
                    SetProgress(ticketKeys.IndexOf(key) + 1, ticketKeys.Count, "Fetching");

                    try
                    {
                        var details   = await _jiraService.GetTicketDetailsAsync(key);
                        var worklogs  = await _jiraService.GetAllWorklogsForTicketAsync(key);
                        var comments  = await _jiraService.GetTicketCommentsAsync(key);
                        var changelog = await _jiraService.GetTicketChangelogAsync(key);

                        ticketDetails[key] = details;
                        allWorklogs.AddRange(worklogs);
                        allComments.AddRange(comments);
                        allStatusChanges.AddRange(changelog);
                    }
                    catch (Exception ex)
                    {
                        SetStatus(string.Format("Failed to fetch {0}: {1}", key, ex.Message));
                    }
                }

                if (allWorklogs.Count == 0)
                {
                    SetStatus("No worklogs found for the specified ticket(s).");
                    SetUIAnalyzing(false);
                    return;
                }

                // Step 2: Sort worklogs chronologically
                var sortedWorklogs = allWorklogs.OrderBy(w => w.Started).ToList();
                int totalWorklogs2 = sortedWorklogs.Count;
                var worklogSummaries = new List<string>();
                var summaryOutput = new StringBuilder();

                // Step 3: Per-worklog summary — one call per entry
                SetStatus(string.Format("Summarizing {0} worklog(s)...", totalWorklogs2));
                int wlIndex = 0;

                foreach (var wl in sortedWorklogs)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    wlIndex++;
                    SetStatus(string.Format("Summarizing worklog {0}/{1}  [{2} | {3}]...",
                        wlIndex, totalWorklogs2, wl.Started.ToString("yyyy-MM-dd"), wl.TimeSpent));
                    SetProgress(wlIndex, totalWorklogs2, "Summarizing");

                    // Get comments close to this worklog date
                    var nearbyComments = allComments
                        .Where(c => Math.Abs((c.Created - wl.Started).TotalDays) <= 1)
                        .ToList();

                    var wlPrompt = BuildWorklogSummaryPrompt(wl, nearbyComments);
                    var wlResult = await _ollamaService.AnalyzeAsync(wlPrompt, model, _cts.Token);

                    string entry;
                    if (wlResult.Success)
                    {
                        entry = string.Format("**{0} | {1} | {2} | {3}:** {4}",
                            wl.Started.ToString("yyyy-MM-dd"),
                            wl.IssueKey,
                            wl.TimeSpent,
                            wl.AuthorDisplayName ?? "Unknown",
                            wlResult.Content.Trim());
                        totalInputTokens  += (int)wlResult.InputTokens;
                        totalOutputTokens += (int)wlResult.OutputTokens;
                        totalResponseSec  += wlResult.ResponseTimeSec;
                    }
                    else
                    {
                        entry = string.Format("**{0} | {1} | {2} | {3}:** (summary failed)",
                            wl.Started.ToString("yyyy-MM-dd"), wl.IssueKey, wl.TimeSpent,
                            wl.AuthorDisplayName ?? "Unknown");
                    }

                    worklogSummaries.Add(entry);
                    summaryOutput.AppendLine(entry);
                    summaryOutput.AppendLine();

                    // Update summary tab live as entries come in
                    txtSummary.Text = summaryOutput.ToString();
                    txtSummary.ScrollToEnd();
                }

                // Step 3b: Append orphaned comments and status changes
                var coveredDates = sortedWorklogs.Select(w => w.Started.Date).ToHashSet();

                // Orphaned comments
                var orphanComments = allComments
                    .Where(c => !coveredDates.Any(d => Math.Abs((c.Created.Date - d).TotalDays) <= 1))
                    .OrderBy(c => c.Created)
                    .ToList();

                foreach (var c in orphanComments)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    string body = (c.Body ?? string.Empty).Replace("\n", " ").Replace("\r", "").Trim();
                    if (string.IsNullOrEmpty(body)) continue;

                    string entry = string.Format("**{0} | COMMENT | {1}:** {2}",
                        c.Created.ToString("yyyy-MM-dd"), c.Author,
                        body.Length > 300 ? body.Substring(0, 300) + "..." : body);

                    worklogSummaries.Add(entry);
                    summaryOutput.AppendLine(entry);
                    summaryOutput.AppendLine();
                }

                // Status changes
                foreach (var sc in allStatusChanges.OrderBy(s => s.Created))
                {
                    string entry = string.Format("**{0} | STATUS | {1}:** {2} → {3}",
                        sc.Created.ToString("yyyy-MM-dd"), sc.Author, sc.FromStatus, sc.ToStatus);

                    worklogSummaries.Add(entry);
                    summaryOutput.AppendLine(entry);
                    summaryOutput.AppendLine();
                }

                // Re-sort everything chronologically
                worklogSummaries = worklogSummaries
                    .OrderBy(e => e.Length > 12 ? e.Substring(2, 10) : "")
                    .ToList();

                txtSummary.Text = summaryOutput.ToString();

                _lastSummary = summaryOutput.ToString();
                tabOutput.SelectedItem = tabSummary;
                btnCopySummary.IsEnabled = true;

                // Build consolidated summary for synthesis
                string allSummaries = string.Join("\n", worklogSummaries);
                var ticketSummaries = ticketKeys
                    .Where(k => ticketDetails.ContainsKey(k))
                    .Select(k => string.Format("{0} — {1}", k, ticketDetails[k].Summary))
                    .ToList();

                // Step 4: Hybrid analysis synthesis
                SetStatus(string.Format("Generating analysis with {0}...", model));
                SetProgress(1, 1, "Analysis");

                var techPrompt = BuildTechnicalSynthesisPrompt(ticketSummaries, allSummaries, allWorklogs);
                var techResult = await _ollamaService.AnalyzeAsync(techPrompt, model, _cts.Token, prefill: "## ");

                if (techResult.Success)
                {
                    _lastTechnical    = techResult.Content;
                    txtTechnical.Text = techResult.Content;
                    totalInputTokens  += (int)techResult.InputTokens;
                    totalOutputTokens += (int)techResult.OutputTokens;
                    totalResponseSec  += techResult.ResponseTimeSec;
                    tabOutput.SelectedItem = tabTechnical;
                }
                else
                {
                    txtTechnical.Text = "Analysis failed: " + techResult.ErrorMessage;
                }

                txtMetrics.Text = string.Format(
                    "⏱ {0:F1}s total  |  📥 {1} in  |  📤 {2} out  |  💰 ${3:F6} Claude equiv",
                    totalResponseSec, totalInputTokens, totalOutputTokens,
                    (totalInputTokens * 3.0 + totalOutputTokens * 15.0) / 1_000_000.0);

                SetStatus(string.Format("Analysis complete — {0} ticket(s), {1} worklog(s).",
                    ticketKeys.Count, allWorklogs.Count));

                btnCopyTech.IsEnabled = _lastTechnical != null;
                btnSaveMd.IsEnabled   = _lastSummary != null || _lastTechnical != null;
            }
            catch (OperationCanceledException)
            {
                SetStatus("Cancelled.");
            }
            catch (Exception ex)
            {
                SetStatus("Error: " + ex.Message);
                MessageBox.Show("Error: " + ex.Message, "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetUIAnalyzing(false);
            }
        }

        // ---------------------------------------------------------------------------
        // Per-worklog summary prompt
        // ---------------------------------------------------------------------------
        private string BuildWorklogSummaryPrompt(WorklogEntry wl, List<TicketComment> nearbyComments)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Summarize this developer worklog entry in 1-3 concise sentences.");
            sb.AppendLine("Focus on: what was done, specific errors or findings, what blocked progress, who caused any delay.");
            sb.AppendLine("For simple entries (no comment or short comment): 1 sentence.");
            sb.AppendLine("For complex entries (long comment with multiple steps or blockers): up to 3 sentences.");
            sb.AppendLine("Output ONLY the sentences. English only — never switch to any other language mid-sentence or at any point.");
            sb.AppendLine("Preserve specific technical details: error messages, method names, URLs, carrier names.");
            sb.AppendLine();
            sb.AppendLine(string.Format("DATE    : {0}", wl.Started.ToString("yyyy-MM-dd")));
            sb.AppendLine(string.Format("TICKET  : {0}", wl.IssueKey));
            sb.AppendLine(string.Format("DURATION: {0}", wl.TimeSpent));
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(wl.Comment))
            {
                sb.AppendLine("WORKLOG COMMENT:");
                sb.AppendLine(wl.Comment.Replace("\r", ""));
            }
            else
            {
                sb.AppendLine("WORKLOG COMMENT: (none)");
            }

            if (nearbyComments.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("NEARBY TICKET COMMENTS:");
                foreach (var c in nearbyComments)
                    sb.AppendLine(string.Format("[{0}] {1}: {2}", c.DateDisplay, c.Author,
                        (c.Body ?? string.Empty).Replace("\n", " ").Replace("\r", "")));
            }

            sb.AppendLine();
            sb.AppendLine("One sentence summary:");
            return sb.ToString();
        }

        // ---------------------------------------------------------------------------
        // Monthly chunk model
        // ---------------------------------------------------------------------------
        private class MonthChunk
        {
            public string Label { get; set; }        // e.g. "Jul 2025"
            public DateTime Month { get; set; }
            public List<WorklogEntry> Worklogs { get; set; } = new List<WorklogEntry>();
            public List<TicketComment> Comments { get; set; } = new List<TicketComment>();
        }

        private List<MonthChunk> BuildMonthlyChunks(List<WorklogEntry> worklogs, List<TicketComment> comments)
        {
            var chunks = new Dictionary<string, MonthChunk>();

            foreach (var wl in worklogs.OrderBy(w => w.Started))
            {
                string key = wl.Started.ToString("yyyy-MM");
                if (!chunks.ContainsKey(key))
                    chunks[key] = new MonthChunk
                    {
                        Label = wl.Started.ToString("MMM yyyy"),
                        Month = new DateTime(wl.Started.Year, wl.Started.Month, 1)
                    };
                chunks[key].Worklogs.Add(wl);
            }

            foreach (var c in comments.OrderBy(c => c.Created))
            {
                string key = c.Created.ToString("yyyy-MM");
                if (chunks.ContainsKey(key))
                    chunks[key].Comments.Add(c);
                else
                {
                    // Add comment to nearest existing month
                    var nearest = chunks.Keys.OrderBy(k => Math.Abs((DateTime.ParseExact(k, "yyyy-MM", null) - c.Created).TotalDays)).FirstOrDefault();
                    if (nearest != null) chunks[nearest].Comments.Add(c);
                }
            }

            return chunks.Values.OrderBy(c => c.Month).ToList();
        }

        // ---------------------------------------------------------------------------
        // Phase extraction prompt (one per month chunk)
        // ---------------------------------------------------------------------------
        private string BuildPhaseExtractPrompt(MonthChunk chunk)
        {
            var sb = new StringBuilder();
            int totalSeconds = chunk.Worklogs.Sum(w => w.TimeSpentSeconds);
            int hrs  = totalSeconds / 3600;
            int mins = (totalSeconds % 3600) / 60;
            string totalTime = hrs > 0 ? string.Format("{0}h {1}m", hrs, mins) : string.Format("{0}m", mins);

            sb.AppendLine("Summarize the following developer worklog entries into a concise phase summary.");
            sb.AppendLine("Output ONLY bullet points. No preamble, no headers, English only.");
            sb.AppendLine("RULES:");
            sb.AppendLine("- Each bullet = one distinct event, finding, blocker, or decision");
            sb.AppendLine("- Include specific error messages, carrier responses, class names where mentioned");
            sb.AppendLine("- Note who caused delays: carrier / vendor / developer / scope change");
            sb.AppendLine("- Do NOT invent or infer — use only what is in the data below");
            sb.AppendLine();
            sb.AppendLine(string.Format("PERIOD: {0} | Total time logged: {1}", chunk.Label, totalTime));
            sb.AppendLine();

            foreach (var wl in chunk.Worklogs.OrderBy(w => w.Started))
            {
                string comment = string.IsNullOrWhiteSpace(wl.Comment) ? "(no comment)" : wl.Comment.Replace("\r", "").Replace("\n", " ");
                sb.AppendLine(string.Format("[{0}] [{1}] {2}: {3}",
                    wl.Started.ToString("yyyy-MM-dd"), wl.TimeSpent, wl.IssueKey, comment));
            }

            if (chunk.Comments.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("TICKET COMMENTS THIS MONTH:");
                foreach (var c in chunk.Comments.OrderBy(c => c.Created))
                {
                    string body = (c.Body ?? string.Empty).Replace("\n", " ").Replace("\r", "");
                    sb.AppendLine(string.Format("[{0}] {1}: {2}", c.DateDisplay, c.Author, body));
                }
            }

            sb.AppendLine();
            sb.AppendLine("Now output the bullet points for this period:");
            return sb.ToString();
        }

        // ---------------------------------------------------------------------------
        // Synthesis prompts (feed all phase summaries, produce final narrative)
        // ---------------------------------------------------------------------------
        private string FormatHours(int totalSeconds)
        {
            int hrs  = totalSeconds / 3600;
            int mins = (totalSeconds % 3600) / 60;
            return hrs > 0 ? string.Format("{0}h {1}m", hrs, mins) : string.Format("{0}m", mins);
        }

        private string BuildPerTicketHoursSummary(List<string> ticketKeys, List<WorklogEntry> allWorklogs)
        {
            var sb = new StringBuilder();
            foreach (var key in ticketKeys)
            {
                int secs = allWorklogs.Where(w => w.IssueKey == key).Sum(w => w.TimeSpentSeconds);
                if (secs > 0)
                    sb.AppendLine(string.Format("- {0}: {1}", key, FormatHours(secs)));
            }
            int totalSecs = allWorklogs.Sum(w => w.TimeSpentSeconds);
            sb.AppendLine(string.Format("- TOTAL: {0}", FormatHours(totalSecs)));
            return sb.ToString();
        }

        private string BuildTechnicalSynthesisPrompt(List<string> ticketSummaries, string phaseSummaries, List<WorklogEntry> allWorklogs)
        {
            var hoursSummary = BuildPerTicketHoursSummary(
                allWorklogs.Select(w => w.IssueKey).Distinct().ToList(), allWorklogs);

            var sb = new StringBuilder();
            sb.AppendLine("IMPORTANT: Respond in English only. Output ONLY the account. No preamble, no self-introduction.");
            sb.AppendLine("Write a developer story of this ticket — a hybrid narrative readable by both developers and managers.");
            sb.AppendLine("Technical enough that a developer understands what happened. Clear enough that a manager can follow the story.");
            sb.AppendLine("Use ONLY facts from the worklog summaries below.");
            sb.AppendLine();
            sb.AppendLine("ANTI-HALLUCINATION RULES — strictly enforced:");
            sb.AppendLine("- Do NOT invent fixes, errors, or root causes not explicitly mentioned in the summaries");
            sb.AppendLine("- Do NOT add technical details (method names, class names, error codes) unless they appear in the summaries");
            sb.AppendLine("- If a fix is not described in the summary, say 'fix applied — see worklog' not an invented description");
            sb.AppendLine("- Only state what actually happened based on the summary text");
            sb.AppendLine();
            sb.AppendLine("HOURS — use EXACTLY these numbers, do NOT recalculate:");
            sb.AppendLine(hoursSummary);
            sb.AppendLine();
            sb.AppendLine("GROUPING RULES:");
            sb.AppendLine("- Aim for 4-7 chapters total — group related worklog entries by theme/blocker");
            sb.AppendLine("- Sernel's QA testing entries = one 'QA Testing' chapter");
            sb.AppendLine("- Carrier escalation entries = one 'Carrier Escalations' chapter");
            sb.AppendLine("- Each distinct code fix = its own chapter only if significant (1h+)");
            sb.AppendLine("- Small entries (< 30m) that don't fit a larger theme: merge into nearest chapter");
            sb.AppendLine();
            sb.AppendLine("STRUCTURE:");
            sb.AppendLine();
            sb.AppendLine("## The Starting Premise");
            sb.AppendLine("What was this ticket supposed to accomplish? Brief context.");
            sb.AppendLine();
            sb.AppendLine("## Chapter 1 — [Descriptive name of the key event/blocker] (date range, ~Xh)");
            sb.AppendLine("What happened. Specific errors verbatim from summaries. Root cause from summaries. Fix from summaries.");
            sb.AppendLine();
            sb.AppendLine("(3-6 more chapters following same pattern)");
            sb.AppendLine();
            sb.AppendLine("## Root Cause Summary");
            sb.AppendLine("| Root Cause | Est. Hours | Category (Our Side / Carrier / Scope Change / Platform Bug) |");
            sb.AppendLine("|---|---|---|");
            sb.AppendLine("(specific rows — hours must add up to TOTAL)");
            sb.AppendLine();
            sb.AppendLine("## One-Line Summary");
            sb.AppendLine("One sentence: why did [TOTAL] dev hours get spent on this ticket?");
            sb.AppendLine();
            sb.AppendLine(string.Format("TICKET(S): {0}", string.Join(", ", ticketSummaries)));
            sb.AppendLine("DOMAIN CONTEXT: This is an insurance carrier real-time rating (RTR) integration project.");
            sb.AppendLine("State abbreviations (UT, SC, AZ, GA, etc.) refer to US states, NOT unit tests.");
            sb.AppendLine("TR = TurboRater (the rating platform). QA = quality assurance testing by a separate QA analyst.");
            sb.AppendLine("PUF = product upload file. DLL = compiled .NET library deployed to a rating server.");
            sb.AppendLine();
            sb.AppendLine("WORKLOG SUMMARIES:");
            sb.AppendLine(phaseSummaries);
            return sb.ToString();
        }


        // ---------------------------------------------------------------------------
        // UI helpers
        // ---------------------------------------------------------------------------
        private void SetStatus(string message) => txtStatus.Text = message;

        private void SetProgress(int current, int total, string phase)
        {
            progressBar.Visibility      = Visibility.Visible;
            txtProgressLabel.Visibility = Visibility.Visible;
            progressBar.Maximum         = total;
            progressBar.Value           = current;
            txtProgressLabel.Text       = string.Format("{0}  {1}/{2}", phase, current, total);
        }

        private void ClearProgress()
        {
            progressBar.Visibility      = Visibility.Collapsed;
            txtProgressLabel.Visibility = Visibility.Collapsed;
            progressBar.Value           = 0;
        }

        private void SetUIAnalyzing(bool analyzing)
        {
            btnAnalyze.IsEnabled   = !analyzing;
            btnCancel.IsEnabled    = analyzing;
            btnAddTicket.IsEnabled = !analyzing;
            cboModel.IsEnabled     = !analyzing;
            if (!analyzing) ClearProgress();
        }

        private void BtnCancelAnalyze_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
        }

        private void BtnCopySummary_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_lastSummary))
            {
                Clipboard.SetText(_lastSummary);
                SetStatus("Work log summary copied to clipboard.");
            }
        }

        private void BtnCopyTech_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_lastTechnical))
            {
                Clipboard.SetText(_lastTechnical);
                SetStatus("Technical analysis copied to clipboard.");
            }
        }


        private void BtnSaveMd_Click(object sender, RoutedEventArgs e)
        {
            var tickets = string.Join("-", _tickets.Select(t => t.Key.Trim()).Where(k => !string.IsNullOrEmpty(k)));
            var dlg = new SaveFileDialog
            {
                Title            = "Save Defense Analysis as Markdown",
                Filter           = "Markdown files (*.md)|*.md|All files (*.*)|*.*",
                FileName         = string.Format("defense-{0}-{1}.md", tickets, DateTime.Today.ToString("yyyy-MM-dd")),
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads"
            };

            if (dlg.ShowDialog() == true)
            {
                var content = new StringBuilder();
                if (_lastSummary != null)
                {
                    content.AppendLine("# Work Log Summary");
                    content.AppendLine();
                    content.AppendLine(_lastSummary);
                }
                if (_lastTechnical != null)
                {
                    if (_lastSummary != null) content.AppendLine("\n---\n");
                    content.AppendLine("# Technical Analysis");
                    content.AppendLine();
                    content.AppendLine(_lastTechnical);
                }
                File.WriteAllText(dlg.FileName, content.ToString(), Encoding.UTF8);
                SetStatus(string.Format("Saved: {0}", dlg.FileName));
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            Close();
        }
    }
}
