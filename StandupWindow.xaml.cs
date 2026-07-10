using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using JiraWorklogViewer.Models;
using JiraWorklogViewer.Services;
using Microsoft.Win32;

namespace JiraWorklogViewer
{
    public partial class StandupWindow : Window
    {
        private readonly JiraService _jiraService;
        private readonly OllamaService _ollamaService;
        private BedrockService _bedrockService;
        private CancellationTokenSource _cts;
        private string _lastGeneratedContent;

        public StandupWindow(JiraService jiraService, OllamaService ollamaService)
        {
            InitializeComponent();

            _jiraService = jiraService;
            _ollamaService = ollamaService;

            // Default dates
            dpToday.SelectedDate = DateTime.Today;
            dpYesterday.SelectedDate = DateTime.Today.DayOfWeek == DayOfWeek.Monday
                ? DateTime.Today.AddDays(-3)   // Monday → Friday
                : DateTime.Today.AddDays(-1);

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

        private async void TxtBedrockProfile_LostFocus(object sender, RoutedEventArgs e)
        {
            App.Settings.BedrockProfile = txtBedrockProfile.Text.Trim();
            App.Settings.Save();
            RebuildBedrockService();
            await LoadModelsAsync();
        }

        private async Task LoadModelsAsync()
        {
            cboModel.Items.Clear();

            cboModel.Items.Add(ModelNames.Claude);

            if (_bedrockService.IsConfigured)
            {
                cboModel.Items.Add(ModelNames.BedrockSonnet);
                cboModel.Items.Add(ModelNames.BedrockHaiku);
                cboModel.Items.Add(ModelNames.BedrockOpus);
            }

            var models = await _ollamaService.GetAvailableModelsAsync();
            foreach (var m in models) cboModel.Items.Add(m.Name);

            cboModel.SelectedItem = ModelNames.Claude;

            if (!_bedrockService.IsConfigured && models.Count == 0)
                txtStatus.Text = "No models available — enter an AWS Profile or start Ollama.";
            else if (!_bedrockService.IsConfigured)
                txtStatus.Text = "Ollama ready. Enter an AWS Profile to enable Bedrock models.";
            else if (models.Count == 0)
                txtStatus.Text = "Bedrock ready. Ollama not running.";
        }

        private async void BtnGenerate_Click(object sender, RoutedEventArgs e)
        {
            if (!dpYesterday.SelectedDate.HasValue || !dpToday.SelectedDate.HasValue)
            {
                MessageBox.Show("Please select both dates.", "Validation",
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
            SetUIGenerating(true);
            txtOutput.Text = string.Empty;
            txtMetrics.Text = string.Empty;
            _lastGeneratedContent = null;

            try
            {
                var yesterday = dpYesterday.SelectedDate.Value;
                var today = dpToday.SelectedDate.Value;

                // Step 1: Fetch worklogs for both days
                SetStatus("Fetching worklogs...");
                var yesterdayGroups = await _jiraService.GetMyWorklogsAsync(yesterday, yesterday);
                var todayGroups = await _jiraService.GetMyWorklogsAsync(today, today);

                var loggedTickets = yesterdayGroups
                    .SelectMany(g => g.Worklogs)
                    .Concat(todayGroups.SelectMany(g => g.Worklogs))
                    .Select(w => w.IssueKey)
                    .Distinct()
                    .ToList();

                var todayLoggedKeys = new HashSet<string>(
                    todayGroups.SelectMany(g => g.Worklogs).Select(w => w.IssueKey));

                // Tickets currently in DEV-In Progress with no worklog logged TODAY — surfaced under
                // ## Today so active work doesn't fall out of the stand-up.
                SetStatus("Checking active tickets...");
                var assignedIssues = await _jiraService.GetAssignedIssuesAsync();
                var wipOnlyIssues = assignedIssues
                    .Where(i => string.Equals(i.Status, "DEV-In Progress", StringComparison.OrdinalIgnoreCase))
                    .Where(i => !todayLoggedKeys.Contains(i.Key))
                    .ToList();

                // Tickets "Approved; Ready to stage" — staging today go under ## Today, the rest
                // under a ## For Staging section (omitted entirely if empty).
                SetStatus("Checking staging tickets...");
                var stagingCandidates = await _jiraService.GetStagingReadyTicketsAsync();
                var stagingTodayTickets = stagingCandidates
                    .Where(t => t.StagingDate.HasValue && t.StagingDate.Value.Date == today.Date)
                    .Where(t => !todayLoggedKeys.Contains(t.Key) && !wipOnlyIssues.Any(w => w.Key == t.Key))
                    .ToList();
                var stagingTodayKeys = new HashSet<string>(stagingTodayTickets.Select(t => t.Key));
                var forStagingTickets = stagingCandidates
                    .Where(t => !stagingTodayKeys.Contains(t.Key))
                    .Where(t => !loggedTickets.Contains(t.Key) && !wipOnlyIssues.Any(w => w.Key == t.Key))
                    .ToList();

                var allTickets = loggedTickets
                    .Concat(wipOnlyIssues.Select(i => i.Key))
                    .Concat(stagingTodayTickets.Select(t => t.Key))
                    .Concat(forStagingTickets.Select(t => t.Key))
                    .Distinct()
                    .ToList();

                if (allTickets.Count == 0)
                {
                    SetStatus("No worklogs found for selected dates.");
                    txtOutput.Text = "No worklogs found for the selected date range.";
                    SetUIGenerating(false);
                    return;
                }

                // Step 2: Fetch ticket details + comments + status history for each unique ticket
                SetStatus(string.Format("Fetching details for {0} ticket(s)...", allTickets.Count));
                var ticketDetails = new Dictionary<string, TicketDetails>();
                var ticketComments = new Dictionary<string, List<TicketComment>>();
                var ticketChangelogs = new Dictionary<string, List<TicketStatusChange>>();

                foreach (var key in allTickets)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    try
                    {
                        ticketDetails[key] = await _jiraService.GetTicketDetailsAsync(key);
                        var comments = await _jiraService.GetTicketCommentsAsync(key);

                        // Filter comments to yesterday and today only
                        ticketComments[key] = comments
                            .Where(c => c.Created.Date == yesterday.Date || c.Created.Date == today.Date)
                            .ToList();
                    }
                    catch
                    {
                        // Skip tickets we can't access
                    }

                    try
                    {
                        ticketChangelogs[key] = await _jiraService.GetTicketChangelogAsync(key);
                    }
                    catch
                    {
                        ticketChangelogs[key] = new List<TicketStatusChange>();
                    }
                }

                // Step 3: Claude mode — skip analysis, output raw entries + prompt
                if (claudeMode)
                {
                    _lastGeneratedContent = BuildRawWorklogText(yesterday, today, yesterdayGroups, todayGroups, ticketChangelogs, wipOnlyIssues, stagingTodayTickets, forStagingTickets);
                    txtOutput.Text = _lastGeneratedContent;
                    txtMetrics.Text = "Claude mode — raw data prepared, no model call made.";
                    btnCopy.IsEnabled = true;
                    btnSaveMd.IsEnabled = true;
                    SetStatus("Ready for Claude — copy and paste into Claude.");
                    SetUIGenerating(false);
                    return;
                }

                // Step 3b: Bedrock mode — single call with all raw data
                if (bedrockMode)
                {
                    var raw = BuildRawWorklogText(yesterday, today, yesterdayGroups, todayGroups, ticketChangelogs, wipOnlyIssues, stagingTodayTickets, forStagingTickets);

                    SetStatus(string.Format("Running stand-up analysis with {0}...", model));
                    var bedrockResult = await _bedrockService.AnalyzeAsync(raw, model, _cts.Token);

                    _lastGeneratedContent = bedrockResult.Success ? bedrockResult.Content : "(Bedrock analysis failed: " + bedrockResult.ErrorMessage + ")";
                    txtOutput.Text = _lastGeneratedContent;
                    txtMetrics.Text = bedrockResult.Success
                        ? string.Format("⏱ {0:F1}s  |  📥 {1} in  |  📤 {2} out", bedrockResult.ResponseTimeSec, bedrockResult.InputTokens, bedrockResult.OutputTokens)
                        : "⚠ Bedrock failed: " + bedrockResult.ErrorMessage;
                    btnCopy.IsEnabled = true;
                    btnSaveMd.IsEnabled = true;
                    SetStatus(bedrockResult.Success ? string.Format("Done — {0}.", model) : "Bedrock error.");
                    SetUIGenerating(false);
                    return;
                }

                // Step 3: Per-worklog Ollama analysis
                bool summarize = true;
                var output = new StringBuilder();
                int totalInputTokens = 0, totalOutputTokens = 0;
                double totalResponseSec = 0;
                int totalWorklogs = allTickets.Sum(key =>
                    yesterdayGroups.SelectMany(g => g.Worklogs).Count(w => w.IssueKey == key) +
                    todayGroups.SelectMany(g => g.Worklogs).Count(w => w.IssueKey == key));
                int worklogIndex = 0;

                // --- YESTERDAY ---
                var yesterdayTickets = yesterdayGroups
                    .SelectMany(g => g.Worklogs)
                    .GroupBy(w => w.IssueKey)
                    .OrderBy(g => g.First().Started)
                    .ToList();

                if (yesterdayTickets.Any())
                {
                    output.AppendLine(string.Format("## Yesterday ({0})", yesterday.ToString("yyyy-MM-dd dddd")));
                    output.AppendLine();

                    foreach (var ticketGroup in yesterdayTickets)
                    {
                        string key = ticketGroup.Key;
                        ticketDetails.TryGetValue(key, out var details);
                        ticketComments.TryGetValue(key, out var comments);

                        output.AppendLine(BuildTicketHeader(key, ticketGroup.ToList(), details));
                        AppendStatusChangeBullets(output, key, yesterday, ticketChangelogs);

                        // Per worklog entry
                        var ticketBullets = new StringBuilder();
                        foreach (var wl in ticketGroup.OrderBy(w => w.Started))
                        {
                            _cts.Token.ThrowIfCancellationRequested();
                            worklogIndex++;
                            SetStatus(string.Format("Processing {0}  {1}...", key, wl.Started.ToString("HH:mm")));
                            SetProgress(worklogIndex, totalWorklogs, IsMdWorklog(wl.Comment) ? "MD sections" : "Extracting");

                            var dayComments = comments?.Where(c => c.Created.Date == yesterday.Date).ToList();
                            var bullets = await ProcessWorklogAsync(key, wl, dayComments, model, summarize, _cts.Token,
                                msg => SetStatus(msg),
                                tokens => {
                                    var parts = tokens.Split(',');
                                    if (parts.Length == 3) {
                                        totalInputTokens  += (int)double.Parse(parts[0]);
                                        totalOutputTokens += (int)double.Parse(parts[1]);
                                        totalResponseSec  += double.Parse(parts[2]);
                                    }
                                });

                            ticketBullets.AppendLine(bullets);
                        }

                        output.AppendLine(ticketBullets.ToString().Trim());
                        output.AppendLine();
                    }
                }

                // --- TODAY ---
                var todayTickets = todayGroups
                    .SelectMany(g => g.Worklogs)
                    .GroupBy(w => w.IssueKey)
                    .OrderBy(g => g.First().Started)
                    .ToList();

                output.AppendLine(string.Format("## Today ({0})", today.ToString("yyyy-MM-dd dddd")));
                output.AppendLine();

                if (todayTickets.Any())
                {
                    foreach (var ticketGroup in todayTickets)
                    {
                        string key = ticketGroup.Key;
                        ticketDetails.TryGetValue(key, out var details);
                        ticketComments.TryGetValue(key, out var comments);

                        output.AppendLine(BuildTicketHeader(key, ticketGroup.ToList(), details));
                        AppendStatusChangeBullets(output, key, today, ticketChangelogs);

                        var ticketBullets = new StringBuilder();
                        foreach (var wl in ticketGroup.OrderBy(w => w.Started))
                        {
                            _cts.Token.ThrowIfCancellationRequested();
                            worklogIndex++;
                            SetStatus(string.Format("Processing {0}  {1}...", key, wl.Started.ToString("HH:mm")));
                            SetProgress(worklogIndex, totalWorklogs, IsMdWorklog(wl.Comment) ? "MD sections" : "Extracting");

                            var dayComments = comments?.Where(c => c.Created.Date == today.Date).ToList();
                            var bullets = await ProcessWorklogAsync(key, wl, dayComments, model, summarize, _cts.Token,
                                msg => SetStatus(msg),
                                tokens => {
                                    var parts = tokens.Split(',');
                                    if (parts.Length == 3) {
                                        totalInputTokens  += (int)double.Parse(parts[0]);
                                        totalOutputTokens += (int)double.Parse(parts[1]);
                                        totalResponseSec  += double.Parse(parts[2]);
                                    }
                                });

                            ticketBullets.AppendLine(bullets);
                        }

                        output.AppendLine(ticketBullets.ToString().Trim());
                        output.AppendLine();
                    }
                }

                foreach (var issue in wipOnlyIssues)
                {
                    ticketDetails.TryGetValue(issue.Key, out var details);
                    output.AppendLine(BuildTicketHeader(issue.Key, new List<WorklogEntry>(), details));
                    output.AppendLine(string.Format("- No time logged today — currently {0}.", issue.Status));
                    AppendStatusChangeBullets(output, issue.Key, today, ticketChangelogs);
                    output.AppendLine();
                }

                foreach (var ticket in stagingTodayTickets)
                {
                    ticketDetails.TryGetValue(ticket.Key, out var details);
                    output.AppendLine(BuildTicketHeader(ticket.Key, new List<WorklogEntry>(), details));
                    output.AppendLine(string.Format("- No time logged today — staged for release today ({0}).", ticket.Status));
                    AppendStatusChangeBullets(output, ticket.Key, today, ticketChangelogs);
                    output.AppendLine();
                }

                if (!todayTickets.Any() && !wipOnlyIssues.Any() && !stagingTodayTickets.Any())
                {
                    output.AppendLine("- No work logged yet today.");
                    output.AppendLine();
                }

                // --- FOR STAGING (omitted entirely if empty) ---
                if (forStagingTickets.Any())
                {
                    output.AppendLine("## For Staging");
                    output.AppendLine();

                    foreach (var ticket in forStagingTickets)
                    {
                        ticketDetails.TryGetValue(ticket.Key, out var details);
                        output.AppendLine(BuildTicketHeader(ticket.Key, new List<WorklogEntry>(), details));
                        output.AppendLine(string.Format("- Staging Date: {0}",
                            ticket.StagingDate.HasValue ? ticket.StagingDate.Value.ToString("yyyy-MM-dd") : "(not set)"));
                        output.AppendLine();
                    }
                }

                // --- BLOCKERS ---
                output.AppendLine("## Blockers / Waiting On");
                output.AppendLine("- _(Review above and add any blockers manually)_");

                _lastGeneratedContent = output.ToString();
                txtOutput.Text = _lastGeneratedContent;
                txtMetrics.Text = string.Format(
                    "⏱ {0:F1}s total  |  📥 {1} in  |  📤 {2} out  |  💰 ${3:F6} Claude equiv",
                    totalResponseSec, totalInputTokens, totalOutputTokens,
                    (totalInputTokens * 3.0 + totalOutputTokens * 15.0) / 1_000_000.0);

                SetStatus(string.Format("Stand-up generated — {0} ticket(s) analyzed.",
                    yesterdayTickets.Count + todayTickets.Count + wipOnlyIssues.Count + stagingTodayTickets.Count + forStagingTickets.Count));
                btnCopy.IsEnabled = true;
                btnSaveMd.IsEnabled = true;
            }
            catch (OperationCanceledException)
            {
                SetStatus("Cancelled.");
                txtOutput.Text = "(Generation cancelled)";
            }
            catch (Exception ex)
            {
                SetStatus("Error: " + ex.Message);
                MessageBox.Show("Error generating stand-up: " + ex.Message, "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetUIGenerating(false);
            }
        }

        /// <summary>
        /// Builds the raw worklog + status-change text used by Claude/Bedrock modes, including any
        /// DEV-In Progress tickets with no worklog logged today and any staging-ready tickets.
        /// </summary>
        private string BuildRawWorklogText(
            DateTime yesterday, DateTime today,
            List<WorklogGroup> yesterdayGroups, List<WorklogGroup> todayGroups,
            Dictionary<string, List<TicketStatusChange>> ticketChangelogs,
            List<JiraAssignedIssue> wipOnlyIssues,
            List<StagingTicket> stagingTodayTickets,
            List<StagingTicket> forStagingTickets)
        {
            var raw = new StringBuilder();
            raw.AppendLine(string.Format("## Yesterday ({0})", yesterday.ToString("yyyy-MM-dd dddd")));
            raw.AppendLine();
            AppendRawDay(raw, yesterday, yesterdayGroups, ticketChangelogs);

            raw.AppendLine();
            raw.AppendLine(string.Format("## Today ({0})", today.ToString("yyyy-MM-dd dddd")));
            raw.AppendLine();
            AppendRawDay(raw, today, todayGroups, ticketChangelogs);

            foreach (var issue in wipOnlyIssues)
            {
                raw.AppendLine(string.Format("**{0} | {1} | (no time logged) | —:** Currently {2} — no worklog entry today.",
                    today.ToString("yyyy-MM-dd"), issue.Key, issue.Status));
                AppendStatusChangeLines(raw, issue.Key, today, ticketChangelogs);
            }

            foreach (var ticket in stagingTodayTickets)
            {
                raw.AppendLine(string.Format("**{0} | {1} | (no time logged) | —:** Staged for release today ({2}).",
                    today.ToString("yyyy-MM-dd"), ticket.Key, ticket.Status));
                AppendStatusChangeLines(raw, ticket.Key, today, ticketChangelogs);
            }

            if (forStagingTickets.Any())
            {
                raw.AppendLine();
                raw.AppendLine("## For Staging");
                raw.AppendLine();
                foreach (var ticket in forStagingTickets)
                    raw.AppendLine(string.Format("**{0} | (no time logged) | —:** {1} — Staging Date: {2}",
                        ticket.Key, ticket.Status,
                        ticket.StagingDate.HasValue ? ticket.StagingDate.Value.ToString("yyyy-MM-dd") : "(not set)"));
            }

            raw.AppendLine();
            raw.AppendLine("---");
            raw.AppendLine();
            raw.AppendLine("Using the worklog entries above, generate a daily stand-up summary:");
            raw.AppendLine("- Organize by: ## Yesterday, then ## Today");
            raw.AppendLine("- Under each section, one ### per ticket (KEY — Summary | Status | Assignee)");
            raw.AppendLine("- Bullets from worklog comments only — no invented content");
            raw.AppendLine("- Note any status changes and tickets with no time logged but currently in progress");
            raw.AppendLine("- If a ## For Staging block is present above, reproduce it verbatim as its own ## For Staging section at the end — do not merge it into ## Today, and omit it entirely if not present");
            raw.AppendLine("- End with: ## Blockers / Waiting On");

            return raw.ToString();
        }

        /// <summary>
        /// Appends raw worklog lines and same-day status changes for every ticket touched on the given day.
        /// </summary>
        private void AppendRawDay(StringBuilder raw, DateTime day, List<WorklogGroup> groups,
            Dictionary<string, List<TicketStatusChange>> ticketChangelogs)
        {
            var seenKeys = new HashSet<string>();
            foreach (var g in groups)
            {
                foreach (var wl in g.Worklogs.OrderBy(w => w.Started))
                {
                    raw.AppendLine(string.Format("**{0} | {1} | {2} | {3}:** {4}",
                        wl.Started.ToString("yyyy-MM-dd"), wl.IssueKey, wl.TimeSpent,
                        wl.AuthorDisplayName ?? "Unknown",
                        string.IsNullOrWhiteSpace(wl.Comment) ? "(no comment)" : wl.Comment.Replace("\n", " ")));
                    seenKeys.Add(wl.IssueKey);
                }
            }

            foreach (var key in seenKeys)
                AppendStatusChangeLines(raw, key, day, ticketChangelogs);
        }

        /// <summary>
        /// Appends one raw line per status transition that happened on the given day.
        /// </summary>
        private void AppendStatusChangeLines(StringBuilder raw, string key, DateTime day,
            Dictionary<string, List<TicketStatusChange>> ticketChangelogs)
        {
            if (!ticketChangelogs.TryGetValue(key, out var changes)) return;

            foreach (var c in changes.Where(c => c.Created.Date == day.Date))
                raw.AppendLine(string.Format("**{0} | {1} | status change | {2}:** {3} → {4}",
                    c.Created.ToString("yyyy-MM-dd HH:mm"), key, c.Author, c.FromStatus, c.ToStatus));
        }

        /// <summary>
        /// Appends '↳' bullet lines for status transitions that happened on the given day — used in the
        /// per-ticket Ollama analysis output, under the ### header.
        /// </summary>
        private void AppendStatusChangeBullets(StringBuilder output, string key, DateTime day,
            Dictionary<string, List<TicketStatusChange>> ticketChangelogs)
        {
            if (!ticketChangelogs.TryGetValue(key, out var changes)) return;

            foreach (var c in changes.Where(c => c.Created.Date == day.Date))
                output.AppendLine(string.Format("↳ {0} Status: {1} → {2}",
                    c.Created.ToString("HH:mm"), c.FromStatus, c.ToStatus));
        }

        /// <summary>
        /// Builds the ### header line for a ticket — no model needed, just formatted data.
        /// </summary>
        private string BuildTicketHeader(string ticketKey, List<WorklogEntry> worklogs, TicketDetails details)
        {
            string status   = details?.Status   ?? "Unknown";
            string assignee = details?.Assignee ?? "Unknown";
            string summary  = details?.Summary  ?? worklogs.FirstOrDefault()?.IssueSummary ?? string.Empty;

            int totalSeconds = worklogs.Sum(w => w.TimeSpentSeconds);
            int hrs  = totalSeconds / 3600;
            int mins = (totalSeconds % 3600) / 60;
            string totalTime = hrs > 0 ? string.Format("{0}h {1}m", hrs, mins) : string.Format("{0}m", mins);

            return string.Format("### {0} — {1} ({2}) | Status: {3} | Assignee: {4}",
                ticketKey, summary, totalTime, status, assignee);
        }

        /// <summary>
        /// Returns true if the comment looks like a structured Markdown work log
        /// (contains ### section headers).
        /// </summary>
        private bool IsMdWorklog(string comment)
        {
            if (string.IsNullOrWhiteSpace(comment)) return false;
            return comment.Contains("###");
        }

        /// <summary>
        /// Splits a Markdown work log by ### sections.
        /// Returns list of (heading, body) pairs.
        /// </summary>
        private List<(string Heading, string Body)> SplitMdSections(string comment)
        {
            var sections = new List<(string, string)>();
            var lines = comment.Replace("\r", "").Split('\n');
            string currentHeading = null;
            var currentBody = new StringBuilder();

            foreach (var line in lines)
            {
                if (line.TrimStart().StartsWith("###"))
                {
                    if (currentHeading != null)
                        sections.Add((currentHeading, currentBody.ToString().Trim()));
                    currentHeading = line.TrimStart('#').Trim();
                    currentBody.Clear();
                }
                else if (currentHeading != null)
                {
                    currentBody.AppendLine(line);
                }
            }

            if (currentHeading != null && currentBody.Length > 0)
                sections.Add((currentHeading, currentBody.ToString().Trim()));

            return sections;
        }

        /// <summary>
        /// Prompt to summarize one ### section into a single bullet line.
        /// </summary>
        private string BuildSectionOneLinerPrompt(string heading, string body)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Summarize the following work log section into exactly ONE concise bullet point.");
            sb.AppendLine("Output ONLY a single line starting with '- '. No preamble, no explanation.");
            sb.AppendLine();
            sb.AppendLine("RULES:");
            sb.AppendLine("- Output exactly one line starting with '- '");
            sb.AppendLine("- Use ONLY facts from the section content below — do not invent or extrapolate");
            sb.AppendLine("- Include the key outcome, finding, or decision — preserve technical specifics");
            sb.AppendLine("- Keep it concise enough for a stand-up but specific enough to be meaningful");
            sb.AppendLine();
            sb.AppendLine(string.Format("SECTION: {0}", heading));
            sb.AppendLine();
            sb.AppendLine("CONTENT:");
            sb.AppendLine(body);
            sb.AppendLine();
            sb.AppendLine("Output the single bullet now:");
            return sb.ToString();
        }

        /// <summary>
        /// Builds a prompt for a single worklog entry. Model outputs bullet points only.
        /// Used for plain-text (non-MD) worklogs.
        /// </summary>
        private string BuildWorklogPrompt(string ticketKey, WorklogEntry wl, List<TicketComment> dayComments)
        {
            var sb = new StringBuilder();

            sb.AppendLine("Extract bullet points from this Jira worklog entry. Output ONLY bullet points. No headers, no preamble, no explanation.");
            sb.AppendLine();
            sb.AppendLine("RULES:");
            sb.AppendLine("- Output ONLY lines starting with '- '");
            sb.AppendLine("- Use ONLY words and facts from the worklog comment below — never invent, infer, or extrapolate");
            sb.AppendLine("- Each distinct action, finding, or step mentioned = one bullet");
            sb.AppendLine("- If the comment is short or vague (e.g. 'AI T&U', 'N2 Leadership'), output it as a single bullet exactly as written");
            sb.AppendLine("- If comment is empty: output exactly '- (no comment — " + wl.TimeSpent + " logged)'");
            sb.AppendLine("- Do NOT add context, guess what happened next, or fill in gaps");
            sb.AppendLine("- No narrative. No summary paragraph. Bullets only.");
            sb.AppendLine();
            sb.AppendLine(string.Format("TICKET  : {0}", ticketKey));
            sb.AppendLine(string.Format("STARTED : {0}", wl.Started.ToString("yyyy-MM-dd HH:mm")));
            sb.AppendLine(string.Format("DURATION: {0}", wl.TimeSpent));
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(wl.Comment))
            {
                sb.AppendLine("WORKLOG COMMENT:");
                sb.AppendLine(wl.Comment.Replace("\r", ""));
            }
            else
            {
                sb.AppendLine("WORKLOG COMMENT: (empty)");
            }

            if (dayComments != null && dayComments.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("TICKET COMMENTS ON THIS DAY:");
                foreach (var c in dayComments)
                    sb.AppendLine(string.Format("[{0}] {1}: {2}", c.DateDisplay, c.Author, c.Body?.Replace("\n", " ")));
            }

            sb.AppendLine();
            sb.AppendLine("Now output the bullet points:");

            return sb.ToString();
        }

        /// <summary>
        /// Processes a single worklog entry — MD or plain text — and returns bullet lines.
        /// </summary>
        private async Task<string> ProcessWorklogAsync(
            string ticketKey,
            WorklogEntry wl,
            List<TicketComment> dayComments,
            string model,
            bool summarize,
            CancellationToken ct,
            Action<string> setStatus,
            Action<string> addTokens)
        {
            var bullets = new StringBuilder();

            if (IsMdWorklog(wl.Comment))
            {
                // MD path: one model call per ### section → one bullet each
                var sections = SplitMdSections(wl.Comment);
                foreach (var (heading, body) in sections)
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(body)) continue;
                    setStatus(string.Format("Summarizing section: {0}...", heading.Length > 40 ? heading.Substring(0, 40) + "…" : heading));
                    var prompt = BuildSectionOneLinerPrompt(heading, body);
                    var result = await _ollamaService.AnalyzeAsync(prompt, model, ct);
                    if (result.Success)
                    {
                        bullets.AppendLine(result.Content.Trim());
                        addTokens(string.Format("{0},{1},{2}", result.InputTokens, result.OutputTokens, result.ResponseTimeSec));
                    }
                    else
                    {
                        bullets.AppendLine(string.Format("- {0} (section analysis failed)", heading));
                    }
                }
            }
            else
            {
                // Plain text path: single extraction call with larger context window
                var prompt = BuildWorklogPrompt(ticketKey, wl, dayComments);
                var result = await _ollamaService.AnalyzeAsync(prompt, model, ct, numCtx: 8192);
                if (result.Success)
                {
                    string extracted = result.Content.Trim();
                    addTokens(string.Format("{0},{1},{2}", result.InputTokens, result.OutputTokens, result.ResponseTimeSec));

                    if (summarize)
                    {
                        var sumPrompt = BuildSummarizePrompt(ticketKey, extracted);
                        var sumResult = await _ollamaService.AnalyzeAsync(sumPrompt, model, ct);
                        if (sumResult.Success)
                        {
                            bullets.AppendLine(sumResult.Content.Trim());
                            addTokens(string.Format("{0},{1},{2}", sumResult.InputTokens, sumResult.OutputTokens, sumResult.ResponseTimeSec));
                        }
                        else
                        {
                            bullets.AppendLine(extracted);
                        }
                    }
                    else
                    {
                        bullets.AppendLine(extracted);
                    }
                }
                else
                {
                    bullets.AppendLine(string.Format("- ({0} | {1}) — analysis failed: {2}", wl.Started.ToString("HH:mm"), wl.TimeSpent, result.ErrorMessage));
                }
            }

            return bullets.ToString().Trim();
        }

        private string BuildSummarizePrompt(string ticketKey, string bullets)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are a bullet point editor. Your ONLY job is to merge and trim the bullets below.");
            sb.AppendLine("Output ONLY bullet points starting with '- '. No preamble, no headers, no explanation.");
            sb.AppendLine();
            sb.AppendLine("STRICT RULES:");
            sb.AppendLine("- Use ONLY words and facts already present in the input bullets");
            sb.AppendLine("- Do NOT add, infer, assume, or invent any new information");
            sb.AppendLine("- Do NOT extrapolate what happened next or what should have happened");
            sb.AppendLine("- Merge bullets that describe the same action into one");
            sb.AppendLine("- Drop bullets that are obvious steps with no specific finding or outcome");
            sb.AppendLine("- Keep all technical specifics: class names, method names, values, root causes, decisions");
            sb.AppendLine("- Short or ambiguous bullets: keep as-is, do not expand them");
            sb.AppendLine("- Output as few bullets as needed — no artificial minimum or maximum");
            sb.AppendLine("- Output ONLY '- ' prefixed lines. Nothing else.");
            sb.AppendLine();
            sb.AppendLine(string.Format("TICKET: {0}", ticketKey));
            sb.AppendLine();
            sb.AppendLine("INPUT BULLETS (edit these — do not add to them):");
            sb.AppendLine(bullets);
            sb.AppendLine();
            sb.AppendLine("Output the condensed bullets now:");
            return sb.ToString();
        }

        private void BtnCancelGenerate_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_lastGeneratedContent))
            {
                Clipboard.SetText(_lastGeneratedContent);
                SetStatus("Copied to clipboard.");
            }
        }

        private void BtnSaveMd_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_lastGeneratedContent)) return;

            var dlg = new SaveFileDialog
            {
                Title       = "Save Stand-Up as Markdown",
                Filter      = "Markdown files (*.md)|*.md|All files (*.*)|*.*",
                FileName    = string.Format("standup-{0}.md", DateTime.Today.ToString("yyyy-MM-dd")),
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                                   + @"\Downloads"
            };

            if (dlg.ShowDialog() == true)
            {
                File.WriteAllText(dlg.FileName, _lastGeneratedContent, Encoding.UTF8);
                SetStatus(string.Format("Saved: {0}", dlg.FileName));
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            Close();
        }

        private void SetStatus(string message)
        {
            txtStatus.Text = message;
        }

        private void SetProgress(int current, int total, string phase)
        {
            progressBar.Visibility    = Visibility.Visible;
            txtProgressLabel.Visibility = Visibility.Visible;
            progressBar.Maximum       = total;
            progressBar.Value         = current;
            txtProgressLabel.Text     = string.Format("{0}  {1}/{2}", phase, current, total);
        }

        private void ClearProgress()
        {
            progressBar.Visibility      = Visibility.Collapsed;
            txtProgressLabel.Visibility = Visibility.Collapsed;
            progressBar.Value           = 0;
        }

        private void SetUIGenerating(bool generating)
        {
            btnGenerate.IsEnabled   = !generating;
            btnCancel.IsEnabled     = generating;
            dpYesterday.IsEnabled   = !generating;
            dpToday.IsEnabled       = !generating;
            cboModel.IsEnabled      = !generating;
            if (!generating) ClearProgress();
        }
    }
}
