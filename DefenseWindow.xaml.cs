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
        private string _lastTimeAnalysis;

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

            // Start with CRM-3484 as default for testing
            _tickets.Add(new TicketEntry { Key = "CRM-3484" });
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
            txtSummary.Text      = string.Empty;
            txtTechnical.Text    = string.Empty;
            txtTimeAnalysis.Text = string.Empty;
            txtMetrics.Text      = string.Empty;
            _lastSummary         = null;
            _lastTechnical       = null;
            _lastTimeAnalysis    = null;

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

                        // Auto-populate estimate from Jira — prefer custom Estimated Effort (Hrs) field
                        if (string.IsNullOrWhiteSpace(txtEstimate.Text))
                        {
                            if (details.EstimatedEffortHours.HasValue && details.EstimatedEffortHours.Value > 0)
                                txtEstimate.Text = string.Format("{0}h", details.EstimatedEffortHours.Value);
                            else if (details.OriginalEstimateFormatted != null)
                                txtEstimate.Text = details.OriginalEstimateFormatted;
                        }
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

                // Step 2: Build chronological event list — worklogs + comments + status changes
                // Merge all events by date, no sorting tricks needed
                var allEvents = new List<(DateTime Date, string Entry)>();

                // Worklogs — summarized by Qwen
                var sortedWorklogs = allWorklogs.OrderBy(w => w.Started).ToList();
                int totalWorklogs2 = sortedWorklogs.Count;
                var summaryOutput  = new StringBuilder();

                SetStatus(string.Format("Summarizing {0} worklog(s)...", totalWorklogs2));
                int wlIndex = 0;

                foreach (var wl in sortedWorklogs)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    wlIndex++;
                    SetStatus(string.Format("Summarizing {0}/{1}  [{2} | {3}]...",
                        wlIndex, totalWorklogs2, wl.Started.ToString("yyyy-MM-dd"), wl.TimeSpent));
                    SetProgress(wlIndex, totalWorklogs2, "Summarizing");

                    var nearbyComments = allComments
                        .Where(c => Math.Abs((c.Created - wl.Started).TotalDays) <= 1)
                        .ToList();

                    var wlPrompt = BuildWorklogSummaryPrompt(wl, nearbyComments);
                    var wlResult = await _ollamaService.AnalyzeAsync(wlPrompt, model, _cts.Token);

                    string entry = string.Format("**{0} | {1} | {2} | {3}:** {4}",
                        wl.Started.ToString("yyyy-MM-dd"), wl.IssueKey, wl.TimeSpent,
                        wl.AuthorDisplayName ?? "Unknown",
                        wlResult.Success ? wlResult.Content.Trim() : "(summary failed)");

                    if (wlResult.Success)
                    {
                        totalInputTokens  += (int)wlResult.InputTokens;
                        totalOutputTokens += (int)wlResult.OutputTokens;
                        totalResponseSec  += wlResult.ResponseTimeSec;
                    }

                    allEvents.Add((wl.Started, entry));
                }

                // Comments — cleaned, no Qwen call
                foreach (var c in allComments.OrderBy(c => c.Created))
                {
                    string body = CleanComment(c.Body ?? string.Empty, c.Author ?? string.Empty);
                    if (string.IsNullOrEmpty(body)) continue;
                    string entry = string.Format("**{0} | COMMENT | {1}:** {2}",
                        c.Created.ToString("yyyy-MM-dd"), c.Author, body);
                    allEvents.Add((c.Created, entry));
                }

                // Status changes — simple list, no filtering
                var meaningfulStatuses = FilterMeaningfulStatusChanges(allStatusChanges);
                foreach (var sc in allStatusChanges.OrderBy(s => s.Created))
                {
                    string entry = string.Format("**{0} | STATUS | {1}:** {2} → {3}",
                        sc.Created.ToString("yyyy-MM-dd"), sc.Author, sc.FromStatus, sc.ToStatus);
                    allEvents.Add((sc.Created, entry));
                }

                // Sort everything chronologically and build WLS
                foreach (var ev in allEvents.OrderBy(e => e.Date))
                {
                    summaryOutput.AppendLine(ev.Entry);
                    summaryOutput.AppendLine();
                }

                txtSummary.Text   = summaryOutput.ToString();
                _lastSummary      = summaryOutput.ToString();
                tabOutput.SelectedItem = tabSummary;
                btnCopySummary.IsEnabled = true;

                var worklogSummaries = allEvents.OrderBy(e => e.Date).Select(e => e.Entry).ToList();

                // Build consolidated summary for synthesis
                string allSummaries = string.Join("\n", worklogSummaries);
                var ticketSummaries = ticketKeys
                    .Where(k => ticketDetails.ContainsKey(k))
                    .Select(k => string.Format("{0} — {1}", k, ticketDetails[k].Summary))
                    .ToList();

                // Step 4: Pre-group by meaningful status phases
                var groupedText = BuildStatusGroupedText(worklogSummaries, meaningfulStatuses);
                int phaseCount  = meaningfulStatuses.Count;
                int chapterCount = Math.Max(3, Math.Min(phaseCount, 7));

                // Step 5: Recursive compression — reduce to ≤ 20 entries then synthesize
                SetStatus("Compressing worklog summaries...");

                var sortedSummaries = worklogSummaries
                    .Where(s => !s.Contains("| STATUS |"))
                    .OrderBy(s => s.Length > 12 ? s.Substring(2, 10) : "")
                    .ToList();

                var compressed = await RecursiveCompressAsync(
                    sortedSummaries, model, 30, _cts.Token,
                    (msg, cur, tot) => { SetStatus(msg); SetProgress(cur, tot, "Compressing"); },
                    (inp, out_, sec) => { totalInputTokens += inp; totalOutputTokens += out_; totalResponseSec += sec; });

                // Step 6: Analysis tab — root cause + one-liner from WLS
                SetStatus(string.Format("Generating analysis with {0}...", model));
                SetProgress(1, 2, "Analysis");

                int totalSecs2    = allWorklogs.Sum(w => w.TimeSpentSeconds);
                string totalTime2 = FormatHours(totalSecs2);
                var wlsText       = _lastSummary ?? string.Join("\n\n", worklogSummaries);

                var rcPrompt = BuildRootCausePrompt(ticketSummaries, wlsText, allWorklogs);
                var rcResult = await _ollamaService.AnalyzeAsync(rcPrompt, model, _cts.Token, prefill: "| ");

                string analysisText = "## Root Cause Summary\n| Root Cause | Est. Hours | Category |\n|---|---|---|\n";
                if (rcResult.Success)
                {
                    analysisText     += rcResult.Content.Trim();
                    totalInputTokens  += (int)rcResult.InputTokens;
                    totalOutputTokens += (int)rcResult.OutputTokens;
                    totalResponseSec  += rcResult.ResponseTimeSec;
                }

                var olPrompt = string.Format(
                    "Complete this sentence in ONE line. English only. Output ONLY the sentence.\n\n" +
                    "Sentence: \"The {0} of developer time spent on {1} were necessary because...\"\n\n" +
                    "Use only facts from this work log summary:\n{2}",
                    totalTime2, string.Join(", ", ticketSummaries),
                    wlsText.Length > 2000 ? wlsText.Substring(0, 2000) : wlsText);

                var olResult = await _ollamaService.AnalyzeAsync(olPrompt, model, _cts.Token);
                if (olResult.Success)
                {
                    analysisText     += string.Format("\n\n## One-Line Summary\n{0}", olResult.Content.Trim());
                    totalInputTokens  += (int)olResult.InputTokens;
                    totalOutputTokens += (int)olResult.OutputTokens;
                    totalResponseSec  += olResult.ResponseTimeSec;
                }

                _lastTechnical    = analysisText;
                txtTechnical.Text = analysisText;

                // Step 7: Time Analysis tab — deterministic, no model call
                SetStatus("Building time analysis...");
                SetProgress(2, 2, "Time Analysis");

                string estimateInput = txtEstimate.Text.Trim();
                string timeAnalysis  = BuildDeterministicTimeAnalysis(
                    ticketSummaries, allWorklogs, meaningfulStatuses, estimateInput, wlsText);

                _lastTimeAnalysis    = timeAnalysis;
                txtTimeAnalysis.Text = timeAnalysis;
                tabOutput.SelectedItem = tabTimeAnalysis;
                btnCopySummary.IsEnabled = true;

                txtMetrics.Text = string.Format(
                    "⏱ {0:F1}s total  |  📥 {1} in  |  📤 {2} out  |  💰 ${3:F6} Claude equiv",
                    totalResponseSec, totalInputTokens, totalOutputTokens,
                    (totalInputTokens * 3.0 + totalOutputTokens * 15.0) / 1_000_000.0);

                SetStatus(string.Format("Analysis complete — {0} ticket(s), {1} worklog(s).",
                    ticketKeys.Count, allWorklogs.Count));

                btnSaveMd.IsEnabled = _lastSummary != null || _lastTechnical != null || _lastTimeAnalysis != null;
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
            sb.AppendLine("Summarize this developer worklog entry. Output ONLY the summary. English only.");
            sb.AppendLine();
            sb.AppendLine("RULES:");
            sb.AppendLine("- NEVER remove key points or choke points — these are the most important parts for billing defense");
            sb.AppendLine("- Key points: root causes, bug discoveries, fixes applied, decisions made, escalations sent");
            sb.AppendLine("- Choke points: blockers, carrier delays, deployment issues, waiting periods, regressions, spec gaps");
            sb.AppendLine("- Keep ALL specific technical details: error messages (verbatim), method names, class names, codes, carrier names");
            sb.AppendLine("- Simple entry (one action): 1-2 sentences");
            sb.AppendLine("- Complex entry (multiple steps, blockers, findings): as many sentences as needed — do NOT truncate");
            sb.AppendLine("- Do NOT invent context, causes, or outcomes not stated in the comment");
            sb.AppendLine("- Do NOT speculate about time gaps — leave them unaddressed");
            sb.AppendLine("- English only throughout — never switch language");
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

        // ---------------------------------------------------------------------------
        // Recursive compression — reduce entries to ≤ maxEntries via batched summarization
        // ---------------------------------------------------------------------------
        private async Task<List<string>> RecursiveCompressAsync(
            List<string> entries,
            string model,
            int maxEntries,
            CancellationToken ct,
            Action<string, int, int> setProgress,
            Action<int, int, double> addTokens)
        {
            if (entries.Count <= maxEntries)
                return entries;

            int level = 0;
            var current = entries;

            while (current.Count > maxEntries)
            {
                level++;
                int batchSize    = maxEntries;
                int totalBatches = (int)Math.Ceiling(current.Count / (double)batchSize);
                var compressed   = new List<string>();

                for (int b = 0; b < totalBatches; b++)
                {
                    ct.ThrowIfCancellationRequested();
                    var batch = current.Skip(b * batchSize).Take(batchSize).ToList();

                    setProgress(
                        string.Format("Compressing (pass {0}) batch {1}/{2}...", level, b + 1, totalBatches),
                        b + 1, totalBatches);

                    var prompt = new StringBuilder();
                    prompt.AppendLine("Compress these developer worklog entries into a single dense paragraph.");
                    prompt.AppendLine("Preserve ALL: dates, error messages, method names, class names, root causes, decisions, blocker sources.");
                    prompt.AppendLine("Compress only repetitive or obvious steps. Output ONLY the paragraph. English only.");
                    prompt.AppendLine();
                    foreach (var e in batch)
                        prompt.AppendLine(e);

                    var result = await _ollamaService.AnalyzeAsync(prompt.ToString(), model, ct);

                    if (result.Success)
                    {
                        // Prefix with date range of this batch for ordering
                        string firstDate = batch.First().Length > 12 ? batch.First().Substring(2, 10) : "unknown";
                        string lastDate  = batch.Last().Length  > 12 ? batch.Last().Substring(2, 10)  : "unknown";
                        compressed.Add(string.Format("[{0} to {1}] {2}", firstDate, lastDate, result.Content.Trim()));
                        addTokens((int)result.InputTokens, (int)result.OutputTokens, result.ResponseTimeSec);
                    }
                    else
                    {
                        // Fallback — keep batch as-is joined
                        compressed.Add(string.Join(" ", batch));
                    }
                }

                current = compressed;
            }

            return current;
        }

        // ---------------------------------------------------------------------------
        // Status filtering — remove same-day back-and-forth noise
        // ---------------------------------------------------------------------------
        private List<TicketStatusChange> FilterMeaningfulStatusChanges(List<TicketStatusChange> all)
        {
            var sorted = all.OrderBy(s => s.Created).ToList();
            var meaningful = new List<TicketStatusChange>();

            for (int i = 0; i < sorted.Count; i++)
            {
                var sc = sorted[i];

                // Skip if same-day reversal — e.g. Corrections→QA-In-Progress same day as QA-In-Progress→Corrections
                bool isSameDayReversal = i + 1 < sorted.Count &&
                    sorted[i + 1].Created.Date == sc.Created.Date &&
                    sorted[i + 1].FromStatus == sc.ToStatus &&
                    sorted[i + 1].ToStatus == sc.FromStatus;

                if (isSameDayReversal)
                {
                    i++; // skip both this and next
                    continue;
                }

                // Skip trivial same-day duplicate transitions
                bool isDuplicate = meaningful.Count > 0 &&
                    meaningful.Last().Created.Date == sc.Created.Date &&
                    meaningful.Last().ToStatus == sc.FromStatus &&
                    meaningful.Last().ToStatus == sc.ToStatus;

                if (!isDuplicate)
                    meaningful.Add(sc);
            }

            return meaningful;
        }

        // ---------------------------------------------------------------------------
        // Group worklog summaries by meaningful status phase with exact dates
        // ---------------------------------------------------------------------------
        private string BuildStatusGroupedText(List<string> summaries, List<TicketStatusChange> phases)
        {
            if (phases.Count == 0)
                return string.Join("\n", summaries);

            var sb = new StringBuilder();

            for (int i = 0; i < phases.Count; i++)
            {
                var sc  = phases[i];
                var end = i + 1 < phases.Count ? phases[i + 1].Created : DateTime.MaxValue;
                string endStr = end == DateTime.MaxValue ? "present" : end.ToString("yyyy-MM-dd");

                string label = string.Format("=== Phase {0}: {1} → {2} ({3} to {4}) ===",
                    i + 1, sc.FromStatus, sc.ToStatus,
                    sc.Created.ToString("yyyy-MM-dd"), endStr);

                var phaseEntries = summaries.Where(s =>
                {
                    if (s.Length < 12) return false;
                    if (!DateTime.TryParse(s.Substring(2, 10), out var d)) return false;
                    return d >= sc.Created.Date && d < end.Date;
                }).ToList();

                if (phaseEntries.Count == 0) continue;

                sb.AppendLine(label);
                foreach (var e in phaseEntries)
                    sb.AppendLine(e);
                sb.AppendLine();
            }

            return sb.ToString();
        }

        // ---------------------------------------------------------------------------
        // Narrative prompt — simple chronological story, no chapter invention
        // ---------------------------------------------------------------------------
        private string BuildNarrativePrompt(
            List<string> ticketSummaries, string summaries,
            List<WorklogEntry> allWorklogs)
        {
            var hoursSummary = BuildPerTicketHoursSummary(
                allWorklogs.Select(w => w.IssueKey).Distinct().ToList(), allWorklogs);

            var sb = new StringBuilder();
            sb.AppendLine("IMPORTANT: English only. Output ONLY the narrative. No preamble, no self-introduction.");
            sb.AppendLine("Write a chronological developer story from the worklog entries below.");
            sb.AppendLine("Use ONLY facts from the entries. Do NOT invent dates, fixes, errors, or class names.");
            sb.AppendLine();
            sb.AppendLine("DOMAIN CONTEXT:");
            sb.AppendLine("- Insurance carrier RTR (real-time rating) integration project");
            sb.AppendLine("- State abbreviations (UT=Utah, SC=South Carolina, AZ=Arizona) = US states, NOT unit tests");
            sb.AppendLine("- TR = TurboRater (rating platform). QA = quality assurance analyst (Sernel Asunto).");
            sb.AppendLine("- PUF = product upload file. DLL = compiled .NET library. Master = shared base branch.");
            sb.AppendLine();
            sb.AppendLine("HOURS — use EXACTLY these numbers:");
            sb.AppendLine(hoursSummary);
            sb.AppendLine();
            sb.AppendLine("STRUCTURE:");
            sb.AppendLine("## The Starting Premise");
            sb.AppendLine("One paragraph: what was this ticket supposed to accomplish?");
            sb.AppendLine();
            sb.AppendLine("## The Story");
            sb.AppendLine("Chronological narrative — one paragraph per significant date or period.");
            sb.AppendLine("Format each paragraph: **YYYY-MM-DD (~Xh):** narrative...");
            sb.AppendLine("Connect events naturally — show cause and effect between entries.");
            sb.AppendLine("Preserve all technical specifics from the entries.");
            sb.AppendLine("If there is a gap between dates, do NOT speculate — just move to the next date.");
            sb.AppendLine();
            sb.AppendLine(string.Format("TICKET(S): {0}", string.Join(", ", ticketSummaries)));
            sb.AppendLine();
            sb.AppendLine("WORKLOG ENTRIES (chronological — each starts with its EXACT date, use ONLY these dates):");
            sb.AppendLine(summaries);
            sb.AppendLine();
            sb.AppendLine("REMINDER: Use ONLY the dates that appear in the entries above. Do NOT invent dates like Mar 14, Mar 17, Mar 28 etc. if they are not in the entries.");
            return sb.ToString();
        }

        // ---------------------------------------------------------------------------
        // Root cause table — separate focused call
        // ---------------------------------------------------------------------------
        private string BuildRootCausePrompt(
            List<string> ticketSummaries, string narrative, List<WorklogEntry> allWorklogs)
        {
            var hoursSummary = BuildPerTicketHoursSummary(
                allWorklogs.Select(w => w.IssueKey).Distinct().ToList(), allWorklogs);

            var sb = new StringBuilder();
            sb.AppendLine("Fill in a root cause table from the developer narrative below.");
            sb.AppendLine("Output ONLY table rows — no header, no preamble. Format: | Root Cause | Est. Hours | Category |");
            sb.AppendLine("Category: Our Side / Carrier / Scope Change / Platform Bug");
            sb.AppendLine("One row per distinct root cause. Hours must add up to TOTAL. English only.");
            sb.AppendLine();
            sb.AppendLine("TOTAL:");
            sb.AppendLine(hoursSummary);
            sb.AppendLine();
            sb.AppendLine("NARRATIVE:");
            sb.AppendLine(narrative.Length > 2000 ? narrative.Substring(0, 2000) : narrative);
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

        // ---------------------------------------------------------------------------
        // Comment cleaner — strips email thread history and noise
        // ---------------------------------------------------------------------------
        private string CleanComment(string rawBody, string author)
        {
            if (string.IsNullOrWhiteSpace(rawBody)) return string.Empty;

            // Normalize line endings
            var body = rawBody.Replace("\r\n", "\n").Replace("\r", "\n");

            // Email thread posted by "Zywave Jira" — extract only the newest (top) message
            if (author.IndexOf("Zywave Jira", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Split into lines and find the first email reply separator
                var lines = body.Split('\n');
                var topLines = new List<string>();

                foreach (var line in lines)
                {
                    string trimmed = line.Trim();

                    // Stop at email thread separators
                    if (trimmed.StartsWith("-----Original Message-----") ||
                        trimmed.StartsWith("________________________________") ||
                        trimmed.StartsWith("From:") && topLines.Count > 0 ||
                        trimmed.StartsWith("Sent:") && topLines.Count > 0 ||
                        (trimmed.StartsWith("On 20") && trimmed.Contains("wrote:")))
                        break;

                    topLines.Add(line);
                }

                body = string.Join("\n", topLines).Trim();
            }

            // Strip noise from all comments — greetings, sign-offs, CC lines
            var cleanLines = new List<string>();
            foreach (var line in body.Split('\n'))
            {
                string t = line.Trim();

                // Skip greeting lines
                if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^(Hi|Hello|Dear|Hey)\s*[,\.]?\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    continue;
                if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^(Hi|Hello|Dear|Hey)\s+\w+\s*[,\.]?\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    continue;

                // Skip sign-off lines
                if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^(Thanks|Thank you|Regards|Best regards|Kind regards|Sincerely|Cheers)[,\.]?\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    continue;

                // Skip CC lines
                if (t.StartsWith("CC:", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith("cc:", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip lines that are just whitespace or a single punctuation
                if (t.Length <= 1) continue;

                cleanLines.Add(line);
            }

            return string.Join(" ", cleanLines.Select(l => l.Trim()))
                         .Trim();
        }

        // ---------------------------------------------------------------------------
        // Deterministic WLS compression for Time Analysis input
        // Keeps: technical signals, blockers, outcomes. Drops: narrative filler.
        // ---------------------------------------------------------------------------
        private string CompressWlsForAnalysis(string wls)
        {
            // Keywords that indicate important content
            var keepKeywords = new[]
            {
                "error", "fix", "fixed", "root cause", "blocked", "blocker", "escalat",
                "carrier", "regression", "latency", "credential", "deployed", "deploy",
                "override", "override", "rebase", "commit", "push", "merge",
                "failed", "failure", "exception", "invalid", "incorrect", "missing",
                "workaround", "resolved", "confirmed", "approved", "rejected",
                "waited", "waiting", "pending", "delay", "deferred", "cannot",
                "QA", "retest", "retested", "issue #", "UT-", "CRM-", "ITC-"
            };

            // Patterns that indicate technical specifics worth keeping
            bool HasTechnicalSignal(string sentence)
            {
                // Method calls: SomeName()
                if (System.Text.RegularExpressions.Regex.IsMatch(sentence, @"\w+\(\)"))
                    return true;
                // Class names: PascalCase words
                if (System.Text.RegularExpressions.Regex.IsMatch(sentence, @"\b[A-Z][a-z]+[A-Z][a-zA-Z]+\b"))
                    return true;
                // File names: word.ext
                if (System.Text.RegularExpressions.Regex.IsMatch(sentence, @"\w+\.\w{2,4}\b"))
                    return true;
                // Quoted error messages or codes
                if (sentence.Contains("\"") || sentence.Contains("`"))
                    return true;
                // Numbers with units (hours, minutes, counts)
                if (System.Text.RegularExpressions.Regex.IsMatch(sentence, @"\d+h|\d+m|\d+ test|\d+ issue"))
                    return true;
                // Keep keywords
                if (keepKeywords.Any(k => sentence.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                    return true;

                return false;
            }

            var lines = wls.Split('\n');
            var result = new StringBuilder();

            foreach (var line in lines)
            {
                // Always keep headers, status lines, empty lines
                if (line.StartsWith("**") || line.StartsWith("===") || line.StartsWith("#") ||
                    string.IsNullOrWhiteSpace(line))
                {
                    // For worklog entry lines — compress the paragraph
                    if (line.StartsWith("**") && line.Contains("| CRM-") || line.Contains("| ITC-") || line.Contains("| TT-"))
                    {
                        // Split prefix from content
                        int colonIdx = line.IndexOf(":**");
                        if (colonIdx > 0)
                        {
                            string prefix  = line.Substring(0, colonIdx + 3);
                            string content = line.Substring(colonIdx + 3).Trim();

                            // Split into sentences and filter
                            var sentences = content
                                .Split(new[] { ". ", "! ", "? " }, StringSplitOptions.RemoveEmptyEntries)
                                .Where(s => HasTechnicalSignal(s))
                                .ToList();

                            if (sentences.Count > 0)
                                result.AppendLine(prefix + " " + string.Join(". ", sentences).Trim());
                            else
                                result.AppendLine(prefix + " " + content); // fallback — keep as-is
                        }
                        else
                        {
                            result.AppendLine(line);
                        }
                    }
                    else
                    {
                        result.AppendLine(line);
                    }
                }
                else
                {
                    result.AppendLine(line);
                }
            }

            return result.ToString();
        }

        private string BuildDeterministicTimeAnalysis(
            List<string> ticketSummaries,
            List<WorklogEntry> allWorklogs,
            List<TicketStatusChange> statuses,
            string estimateInput,
            string wlsText)
        {
            var sb = new StringBuilder();

            // --- Hours by author ---
            int totalSecs  = allWorklogs.Sum(w => w.TimeSpentSeconds);
            string totalTime = FormatHours(totalSecs);
            double actualHours = totalSecs / 3600.0;

            sb.AppendLine("## Time Summary");
            sb.AppendLine();
            sb.AppendLine(string.Format("**Actual Total:** {0} ({1:F1}h)", totalTime, actualHours));

            // Per-author breakdown
            var byAuthor = allWorklogs
                .GroupBy(w => w.AuthorDisplayName ?? "Unknown")
                .OrderByDescending(g => g.Sum(w => w.TimeSpentSeconds));
            foreach (var a in byAuthor)
                sb.AppendLine(string.Format("- {0}: {1}", a.Key, FormatHours(a.Sum(w => w.TimeSpentSeconds))));

            // Estimate comparison
            double estimateHours = 0;
            if (!string.IsNullOrEmpty(estimateInput))
            {
                var m = System.Text.RegularExpressions.Regex.Match(estimateInput, @"(\d+\.?\d*)");
                if (m.Success) estimateHours = double.Parse(m.Groups[1].Value);
            }

            if (estimateHours > 0)
            {
                double ratio = Math.Round(actualHours / estimateHours, 2);
                bool overrun = actualHours > estimateHours * 1.5;
                sb.AppendLine();
                sb.AppendLine(string.Format("**Original Estimate:** {0}", estimateInput));
                sb.AppendLine(string.Format("**Overrun Ratio:** {0}× — {1}",
                    ratio, overrun ? "⚠️ OVER THRESHOLD (>1.5×) — defense required" : "✅ Within acceptable range"));
            }

            // --- Status timeline ---
            sb.AppendLine();
            sb.AppendLine("## Status Timeline");
            sb.AppendLine();
            foreach (var sc in statuses)
                sb.AppendLine(string.Format("- **{0}** ({1}): {2} → {3}",
                    sc.Created.ToString("yyyy-MM-dd"), sc.Author, sc.FromStatus, sc.ToStatus));

            // Idle periods
            sb.AppendLine();
            sb.AppendLine("**Notable gaps (no worklogs):**");
            var worklogDates = allWorklogs.Select(w => w.Started.Date).OrderBy(d => d).ToList();
            if (worklogDates.Count > 1)
            {
                for (int i = 1; i < worklogDates.Count; i++)
                {
                    int gap = (worklogDates[i] - worklogDates[i - 1]).Days;
                    if (gap > 7)
                        sb.AppendLine(string.Format("- {0} day gap: {1:yyyy-MM-dd} → {2:yyyy-MM-dd}",
                            gap, worklogDates[i - 1], worklogDates[i]));
                }
            }

            // --- Ready for Claude ---
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("## Ready for Claude Analysis");
            sb.AppendLine();
            sb.AppendLine("Paste the **Work Log Summary** tab into Claude Code and ask:");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(string.Format("Using the work log summary below for {0}:", string.Join(", ", ticketSummaries)));
            sb.AppendLine();
            if (estimateHours > 0 && actualHours > estimateHours * 1.5)
            {
                sb.AppendLine(string.Format("Actual time: {0} | Original estimate: {1} | Overrun: {2:F2}×",
                    totalTime, estimateInput, actualHours / estimateHours));
                sb.AppendLine();
                sb.AppendLine("Answer these 3 questions:");
                sb.AppendLine("1. What was the estimate and what did it assume?");
                sb.AppendLine("2. What were the key choke points that caused the overrun? (carrier delays, bugs, QA cycles, regressions — with hours per choke point)");
                sb.AppendLine("3. What should the estimate have been? (break down: base dev + carrier buffer + QA rounds + risk items = revised total)");
            }
            else
            {
                sb.AppendLine(string.Format("Actual time: {0}{1}",
                    totalTime, estimateHours > 0 ? string.Format(" | Estimate: {0} | Ratio: {1:F2}×", estimateInput, actualHours / estimateHours) : ""));
                sb.AppendLine();
                sb.AppendLine("Answer these 3 questions:");
                sb.AppendLine("1. Was the estimate reasonable, or did we get lucky?");
                sb.AppendLine("2. Identify choke points that COULD have bloated the time but didn't — near-misses, fast carrier responses, bugs caught early.");
                sb.AppendLine("3. What would a better-informed estimate look like for similar tickets? (base dev + carrier buffer + QA + risk buffer = recommended)");
            }
            sb.AppendLine("```");

            return sb.ToString();
        }

        private string BuildTimeAnalysisPrompt(
            List<string> ticketSummaries, string wls,
            List<WorklogEntry> allWorklogs, string estimateInput)
        {
            int totalSecs     = allWorklogs.Sum(w => w.TimeSpentSeconds);
            double actualHours = totalSecs / 3600.0;
            string actualTime = FormatHours(totalSecs);

            // Parse estimate input
            double estimateHours = 0;
            if (!string.IsNullOrEmpty(estimateInput))
            {
                var match = System.Text.RegularExpressions.Regex.Match(estimateInput, @"(\d+\.?\d*)h");
                if (match.Success) estimateHours = double.Parse(match.Groups[1].Value);
            }

            bool hasEstimate = estimateHours > 0;
            bool isOverrun   = hasEstimate && actualHours > estimateHours * 1.5;
            double overrunRatio = hasEstimate ? Math.Round(actualHours / estimateHours, 2) : 0;

            var sb = new StringBuilder();
            sb.AppendLine("IMPORTANT: English only. Output ONLY the answers to the 3 questions below.");
            sb.AppendLine("Do NOT produce a ticket summary, timeline, or status history.");
            sb.AppendLine("Do NOT invent events, delays, or causes not present in the work log.");
            sb.AppendLine("Start your output directly with '## Q1' — nothing before it.");
            sb.AppendLine();
            sb.AppendLine("DOMAIN CONTEXT:");
            sb.AppendLine("- Insurance carrier RTR (real-time rating) integration project");
            sb.AppendLine("- State abbreviations (UT=Utah, SC=South Carolina, etc.) = US states");
            sb.AppendLine("- TR = TurboRater. QA = quality assurance analyst. Carrier = external insurance company.");
            sb.AppendLine("- 'Choke point' = a specific event that caused unplanned hours");
            sb.AppendLine();
            sb.AppendLine(string.Format("TICKET(S): {0}", string.Join(", ", ticketSummaries)));
            sb.AppendLine(string.Format("ACTUAL TOTAL TIME: {0} ({1:F1}h)", actualTime, actualHours));

            if (hasEstimate)
            {
                sb.AppendLine(string.Format("ORIGINAL ESTIMATE: {0} ({1}h)", estimateInput, estimateHours));
                sb.AppendLine(string.Format("OVERRUN RATIO: {0}× ({1})",
                    overrunRatio,
                    isOverrun ? "OVER THRESHOLD — defense required" : "within acceptable range"));
            }
            else
            {
                sb.AppendLine("ORIGINAL ESTIMATE: Not available — infer from scope if possible.");
            }

            sb.AppendLine();

            if (isOverrun)
            {
                sb.AppendLine("SCENARIO: Overrun detected. Complete each section below using ONLY facts from the work log.");
                sb.AppendLine("Do NOT produce a ticket summary. Fill in each section directly.");
                sb.AppendLine();
                sb.AppendLine(string.Format("## Q1: What was the estimate and what did it assume?\nThe original estimate was {0}. It assumed [complete this based on the ticket type and scope from the work log].", estimateInput));
                sb.AppendLine();
                sb.AppendLine("## Q2: What caused the overrun?\nThe key choke points that caused unplanned hours were:");
                sb.AppendLine("[List each choke point: what happened, who caused it, estimated hours, avoidable or not]");
                sb.AppendLine("Categories to check from the work log: carrier spec gaps, base bridge bugs, QA cycles, deployment latency, carrier response delays, regressions.");
                sb.AppendLine();
                sb.AppendLine("## Q3: What should the estimate have been?\nA better-informed estimate would be:");
                sb.AppendLine("[Base dev] + [carrier buffer] + [QA rounds] + [risk items] = [revised total]");
                sb.AppendLine("Explain why each component was unavoidable.");
            }
            else
            {
                sb.AppendLine("SCENARIO: Within estimate. Complete each section below using ONLY facts from the work log.");
                sb.AppendLine("Do NOT produce a ticket summary. Fill in each section directly.");
                sb.AppendLine();
                sb.AppendLine(string.Format("## Q1: Was the estimate reasonable?\nActual time was {0} against an estimate of {1}. The estimate was [reasonable/optimistic/conservative] because [explain based on scope from work log].",
                    actualTime, hasEstimate ? estimateInput : "unknown"));
                sb.AppendLine();
                sb.AppendLine("## Q2: Was the estimate good, or did we get lucky?\nLooking at the work log for near-misses and choke points:");
                sb.AppendLine("[Identify specific events that could have been worse: quick carrier responses, bugs caught early, QA rounds that resolved fast]");
                sb.AppendLine("[Be honest — distinguish good planning from fortunate timing]");
                sb.AppendLine();
                sb.AppendLine("## Q3: What would a better estimate look like for similar tickets?\nBased on the actual work breakdown:");
                sb.AppendLine("[Base dev] + [carrier dependency buffer] + [QA rounds] + [risk buffer] = [recommended estimate]");
                sb.AppendLine("Lessons learned from this ticket for future estimation:");
            }

            sb.AppendLine();
            sb.AppendLine("WORK LOG SUMMARY:");
            sb.AppendLine(wls);
            return sb.ToString();
        }

        private void BtnCopyTimeAnalysis_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_lastTimeAnalysis))
            {
                Clipboard.SetText(_lastTimeAnalysis);
                SetStatus("Time analysis copied to clipboard.");
            }
        }

        private void BtnCopySummary_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_lastSummary)) return;

            // When copying, include the Claude prompt so pasting into terminal gives Claude everything it needs
            var sb = new StringBuilder();
            sb.AppendLine(_lastSummary);

            // Append the analysis prompt inline
            if (_lastTimeAnalysis != null)
            {
                var lines     = _lastTimeAnalysis.Split('\n');
                bool inPrompt = false;
                var prompt    = new List<string>();
                foreach (var line in lines)
                {
                    if (line.TrimStart().StartsWith("```") && !inPrompt) { inPrompt = true; continue; }
                    if (line.TrimStart().StartsWith("```") && inPrompt)  { inPrompt = false; continue; }
                    if (inPrompt) prompt.Add(line);
                }
                if (prompt.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("---");
                    sb.AppendLine();
                    sb.AppendLine(string.Join("\n", prompt).Trim());
                }
            }

            Clipboard.SetText(sb.ToString());
            SetStatus("Work log summary + Claude prompt copied to clipboard.");
        }

        private void BtnCopyTech_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_lastTechnical))
            {
                Clipboard.SetText(_lastTechnical);
                SetStatus("Technical analysis copied to clipboard.");
            }
        }


        private string BuildClaudeInstructions()
        {
            if (_lastTimeAnalysis == null) return string.Empty;

            // Extract the Ready for Claude prompt from Time Analysis
            var lines      = _lastTimeAnalysis.Split('\n');
            var promptLines = new List<string>();
            bool inPrompt  = false;

            foreach (var line in lines)
            {
                if (line.TrimStart().StartsWith("```") && !inPrompt) { inPrompt = true; continue; }
                if (line.TrimStart().StartsWith("```") && inPrompt)  { inPrompt = false; continue; }
                if (inPrompt) promptLines.Add(line);
            }

            string extractedPrompt = promptLines.Count > 0
                ? string.Join("\n", promptLines).Trim()
                : string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("<!--");
            sb.AppendLine("CLAUDE INSTRUCTIONS — Read this file and perform the analysis below.");
            sb.AppendLine("When done, append your findings at the bottom of this file under '# Claude Analysis'.");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(extractedPrompt))
                sb.AppendLine(extractedPrompt);
            else
            {
                sb.AppendLine("Using the Work Log Summary in this file, answer:");
                sb.AppendLine("1. Was the estimate reasonable, or did we get lucky?");
                sb.AppendLine("2. What were the key choke points — near-misses, carrier delays, bugs caught early?");
                sb.AppendLine("3. What would a better-informed estimate look like for similar tickets?");
            }
            sb.AppendLine();
            sb.AppendLine("Append your response at the bottom of this file under: # Claude Analysis");
            sb.AppendLine("-->");
            return sb.ToString();
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

                // Embedded Claude instructions at the top
                string instructions = BuildClaudeInstructions();
                if (!string.IsNullOrEmpty(instructions))
                {
                    content.AppendLine(instructions);
                    content.AppendLine();
                }

                if (_lastSummary != null)
                {
                    content.AppendLine("# Work Log Summary");
                    content.AppendLine();
                    content.AppendLine(_lastSummary);
                }
                if (_lastTechnical != null)
                {
                    if (_lastSummary != null) content.AppendLine("\n---\n");
                    content.AppendLine("# Analysis");
                    content.AppendLine();
                    content.AppendLine(_lastTechnical);
                }
                if (_lastTimeAnalysis != null)
                {
                    content.AppendLine("\n---\n");
                    content.AppendLine("# Time Analysis");
                    content.AppendLine();
                    content.AppendLine(_lastTimeAnalysis);
                }

                // Placeholder for Claude to append
                content.AppendLine("\n---\n");
                content.AppendLine("# Claude Analysis");
                content.AppendLine();
                content.AppendLine("_(Pending — ask Claude to read this file and complete the analysis above)_");

                File.WriteAllText(dlg.FileName, content.ToString(), Encoding.UTF8);

                // Open in VS Code so Claude Code can immediately pick it up
                try { System.Diagnostics.Process.Start("code", string.Format("\"{0}\"", dlg.FileName)); } catch { }

                SetStatus(string.Format("Saved and opened: {0}", dlg.FileName));
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            Close();
        }
    }
}
