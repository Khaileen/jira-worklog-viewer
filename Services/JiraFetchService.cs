using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using JiraWorklogViewer.Models;
using Newtonsoft.Json;

namespace JiraWorklogViewer.Services
{
    /// <summary>
    /// Handles the jira-fetch pipeline: ticket fetch, attachment download,
    /// pattern search, similar ticket search, and 7-point analysis via Ollama.
    /// </summary>
    public class JiraFetchService
    {
        // ---------------------------------------------------------------------------
        // Config
        // ---------------------------------------------------------------------------
        public static readonly string OutputBase       = @"C:\ResearchTicketsAnalysis_MCP";
        public static readonly string DataDir          = Path.Combine(OutputBase, "data");
        public static readonly string CarrierLookupFile = Path.Combine(DataDir, "carrier_lookup.txt");
        public static readonly string NetworkShareDir  = @"\\h-v-fps-p01\itc_dfs_root\Production\ITCSys\MichaelB\zw-claude-jira-data";
        public static readonly string PatternsFile     = Path.Combine(NetworkShareDir, "analysis_patterns.md");
        public static readonly string LogsDir          = Path.Combine(OutputBase, "logs");

        private static readonly string GitLabBase    = "https://gitlab.com/api/v4";
        private static readonly string GitLabGroup   = "zywave%2Fpl-cpq%2Fcarrier-operations";
        private static readonly string GitLabProject = "zywave%2Fpl-cpq%2Fcarrier-operations%2Fcompanies";

        private static readonly string[] TextExtensions  = { ".xml", ".json", ".txt", ".md", ".csv", ".log", ".tt2", ".xslt", ".xsl" };
        private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };
        private const int MaxAttachmentChars = 8000;

        private readonly HttpClient _jiraClient;
        private readonly HttpClient _gitlabClient;
        private readonly string _jiraBaseUrl;

        public JiraFetchService(string jiraBaseUrl, string jiraEmail, string jiraToken, string gitlabToken)
        {
            _jiraBaseUrl = jiraBaseUrl.TrimEnd('/');

            _jiraClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{jiraEmail}:{jiraToken}"));
            _jiraClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
            _jiraClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            _gitlabClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            if (!string.IsNullOrEmpty(gitlabToken))
                _gitlabClient.DefaultRequestHeaders.Add("PRIVATE-TOKEN", gitlabToken);
        }

        // ---------------------------------------------------------------------------
        // Ticket fetch
        // ---------------------------------------------------------------------------
        public async Task<JiraFetchTicket> FetchTicketAsync(string ticketNumber)
        {
            var url  = $"{_jiraBaseUrl}/rest/api/3/issue/{ticketNumber}";
            var resp = await _jiraClient.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"Jira {resp.StatusCode}: {ticketNumber}");

            var json   = await resp.Content.ReadAsStringAsync();
            var data   = JsonConvert.DeserializeObject<JiraIssueRaw>(json);
            var fields = data.fields;

            string summary     = fields?.summary     ?? "(no summary)";
            string status      = fields?.status?.name ?? "Unknown";
            string assignee    = fields?.assignee?.displayName ?? "Unassigned";
            string reporter    = fields?.reporter?.displayName ?? "Unknown";
            string priority    = fields?.priority?.name ?? "None";
            string created     = (fields?.created ?? "").Substring(0, Math.Min(10, (fields?.created ?? "").Length));
            string updated     = (fields?.updated ?? "").Substring(0, Math.Min(10, (fields?.updated ?? "").Length));
            string description = AdfToText(fields?.description);

            var attachments = fields?.attachment ?? new List<JiraAttachmentRaw>();
            string attachmentNames = string.Join(" ", attachments.Select(a => a.filename));
            string carrier = DetectCarrier(summary, description, attachmentNames);

            var linkedBugs = new List<JiraFetchLinkedBug>();
            foreach (var link in fields?.issuelinks ?? new List<JiraIssueLinkRaw>())
            {
                var linked = link.outwardIssue ?? link.inwardIssue;
                if (linked?.fields == null) continue;
                if (!string.Equals(linked.fields.issuetype?.name, "Bug", StringComparison.OrdinalIgnoreCase)) continue;
                if (linkedBugs.Any(b => b.Key == linked.key)) continue;

                linkedBugs.Add(new JiraFetchLinkedBug
                {
                    Key      = linked.key,
                    Summary  = linked.fields.summary ?? "(no summary)",
                    Status   = linked.fields.status?.name ?? "Unknown",
                    IsClosed = linked.fields.status?.statusCategory?.key == "done"
                });
            }

            string outputDir     = Path.Combine(OutputBase, carrier, ticketNumber);
            string attachmentDir = Path.Combine(outputDir, "attachments");
            Directory.CreateDirectory(attachmentDir);

            // Download attachments
            var downloadResults = new List<JiraFetchAttachment>();
            foreach (var att in attachments)
            {
                string filename    = SanitizeFilename(att.filename);
                string destination = Path.Combine(attachmentDir, filename);
                string attStatus;
                if (File.Exists(destination))
                {
                    attStatus = "skipped";
                }
                else
                {
                    try
                    {
                        var bytes = await _jiraClient.GetByteArrayAsync(att.content);
                        File.WriteAllBytes(destination, bytes);
                        attStatus = "downloaded";
                    }
                    catch (Exception ex)
                    {
                        attStatus = $"failed: {ex.Message}";
                        destination = null;
                    }
                }
                downloadResults.Add(new JiraFetchAttachment
                {
                    Filename = att.filename, Status = attStatus, Path = destination
                });
            }

            // Fetch comments
            var comments = new List<JiraFetchComment>();
            int startAt = 0;
            try
            {
                do
                {
                    var cUrl  = $"{_jiraBaseUrl}/rest/api/3/issue/{ticketNumber}/comment?startAt={startAt}&maxResults=100";
                    var cResp = await _jiraClient.GetAsync(cUrl);
                    if (!cResp.IsSuccessStatusCode) break;
                    var cJson = await cResp.Content.ReadAsStringAsync();
                    var cPage = JsonConvert.DeserializeObject<JiraCommentPageRaw>(cJson);
                    foreach (var c in cPage?.comments ?? new List<JiraCommentRaw>())
                        comments.Add(new JiraFetchComment
                        {
                            Author  = c.author?.displayName ?? "Unknown",
                            Created = (c.created ?? "").Substring(0, Math.Min(10, (c.created ?? "").Length)),
                            Body    = AdfToText(c.body)
                        });
                    startAt += cPage?.comments?.Count ?? 0;
                    if (startAt >= (cPage?.total ?? 0)) break;
                } while (true);
            }
            catch { }

            var worklogs = await FetchWorklogsAsync(ticketNumber);
            bool isDone  = fields?.status?.statusCategory?.key == "done";
            var schedule = BuildScheduleInfo(fields, worklogs, comments, updated, isDone);

            return new JiraFetchTicket
            {
                TicketNumber = ticketNumber,
                Summary      = summary,
                Status       = status,
                Assignee     = assignee,
                Reporter     = reporter,
                Priority     = priority,
                Created      = created,
                Updated      = updated,
                Description  = description,
                Carrier      = carrier,
                Attachments  = downloadResults,
                Comments     = comments,
                Worklogs     = worklogs,
                Schedule     = schedule,
                LinkedBugs   = linkedBugs,
                OutputDir    = outputDir,
                AttachmentDir = attachmentDir
            };
        }

        // ---------------------------------------------------------------------------
        // Effort / schedule analysis
        // ---------------------------------------------------------------------------
        private JiraFetchScheduleInfo BuildScheduleInfo(
            JiraIssueFieldsRaw fields, List<JiraFetchWorklog> worklogs,
            List<JiraFetchComment> comments, string updated, bool isDone)
        {
            string programmer = fields?.customfield_11192?.FirstOrDefault()?.displayName;
            string tester      = fields?.customfield_11193?.FirstOrDefault()?.displayName;

            var info = new JiraFetchScheduleInfo
            {
                DevEstimateHours   = ParseHours(fields?.customfield_15528?.value),
                QaEstimateHours    = ParseHours(fields?.customfield_15527?.value),
                TotalEstimateHours = ParseHours(fields?.customfield_12725?.value) ?? fields?.customfield_11639
                                     ?? (fields?.timetracking?.originalEstimateSeconds / 3600.0),
                TotalTimeSpentSeconds = fields?.timetracking?.timeSpentSeconds ?? worklogs.Sum(w => w.TimeSpentSeconds),
                Programmer      = programmer,
                Tester          = tester,
                SpecsReceived   = fields?.customfield_11702,
                DateScheduled   = fields?.customfield_11637,
                QaStartDate     = fields?.customfield_11690,
                QaDeliveryDate  = fields?.customfield_11196,
                DevDueDate      = fields?.customfield_12100,
                QaDueDate       = fields?.customfield_12101,
                DueDate         = fields?.duedate,
                IsDone          = isDone
            };

            foreach (var w in worklogs)
            {
                if (!string.IsNullOrEmpty(programmer) && string.Equals(w.Author, programmer, StringComparison.OrdinalIgnoreCase))
                    info.DevTimeSpentSeconds += w.TimeSpentSeconds;
                else if (!string.IsNullOrEmpty(tester) && string.Equals(w.Author, tester, StringComparison.OrdinalIgnoreCase))
                    info.QaTimeSpentSeconds += w.TimeSpentSeconds;
                else
                    info.OtherTimeSpentSeconds += w.TimeSpentSeconds;
            }

            info.DevOverEstimate   = info.DevEstimateHours.HasValue && info.DevTimeSpentSeconds / 3600.0 > info.DevEstimateHours.Value;
            info.QaOverEstimate    = info.QaEstimateHours.HasValue && info.QaTimeSpentSeconds / 3600.0 > info.QaEstimateHours.Value;
            info.TotalOverEstimate = info.TotalEstimateHours.HasValue && info.TotalTimeSpentSeconds / 3600.0 > info.TotalEstimateHours.Value;

            if (!isDone)
            {
                var today = DateTime.Now.Date;
                foreach (var (label, dateStr) in new[]
                {
                    ("Dev Due Date", info.DevDueDate), ("QA Due Date", info.QaDueDate),
                    ("QA Delivery Date", info.QaDeliveryDate), ("Due Date", info.DueDate)
                })
                {
                    if (DateTime.TryParse(dateStr, out var d) && d.Date < today)
                        info.MissedTargetDates.Add($"{label} ({dateStr})");
                }
            }

            var lastActivity = comments.Select(c => c.Created)
                .Concat(worklogs.Select(w => w.Started))
                .Where(d => DateTime.TryParse(d, out _))
                .Select(d => DateTime.Parse(d))
                .DefaultIfEmpty(DateTime.TryParse(updated, out var u) ? u : DateTime.Now.Date)
                .Max();
            info.LastActivityDate = lastActivity.ToString("yyyy-MM-dd");
            info.BusinessDaysSinceActivity = BusinessDaysBetween(lastActivity.Date, DateTime.Now.Date);
            info.IsStalled = !isDone && info.BusinessDaysSinceActivity >= 5;

            return info;
        }

        private static double? ParseHours(string value) =>
            double.TryParse(value, out var d) ? d : (double?)null;

        private static int BusinessDaysBetween(DateTime from, DateTime to)
        {
            if (to <= from) return 0;
            int days = 0;
            for (var d = from.AddDays(1); d <= to; d = d.AddDays(1))
                if (d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday) days++;
            return days;
        }

        /// <summary>
        /// Renders the effort/schedule picture: estimates vs logged time (dev/qa/total), target
        /// dates, and derived flags — over-estimate, missed target date, stalled (no recent activity).
        /// </summary>
        public string BuildScheduleSection(JiraFetchScheduleInfo s)
        {
            if (s == null) return "No effort/schedule data.";

            string Hrs(int seconds) => $"{seconds / 3600.0:0.#}h";
            string Est(double? h) => h.HasValue ? $"{h.Value:0.#}h" : "not set";

            var sb = new StringBuilder();
            sb.AppendLine($"- **Dev:** logged {Hrs(s.DevTimeSpentSeconds)} ({s.Programmer ?? "unassigned"}) vs estimate {Est(s.DevEstimateHours)}{(s.DevOverEstimate ? " ⚠ OVER ESTIMATE" : "")}");
            sb.AppendLine($"- **QA:** logged {Hrs(s.QaTimeSpentSeconds)} ({s.Tester ?? "unassigned"}) vs estimate {Est(s.QaEstimateHours)}{(s.QaOverEstimate ? " ⚠ OVER ESTIMATE" : "")}");
            sb.AppendLine($"- **Total:** logged {Hrs(s.TotalTimeSpentSeconds)} vs estimate {Est(s.TotalEstimateHours)}{(s.TotalOverEstimate ? " ⚠ OVER ESTIMATE" : "")}");
            if (s.OtherTimeSpentSeconds > 0)
                sb.AppendLine($"- Other logged time (not attributable to Programmer/Tester): {Hrs(s.OtherTimeSpentSeconds)}");
            sb.AppendLine($"- **Target dates:** Specs Received {s.SpecsReceived ?? "n/a"} | Scheduled {s.DateScheduled ?? "n/a"} | QA Start {s.QaStartDate ?? "n/a"} | QA Delivery {s.QaDeliveryDate ?? "n/a"} | Due {s.DueDate ?? "n/a"}");
            if (s.MissedTargetDates.Count > 0)
                sb.AppendLine($"- ⚠ MISSED TARGET DATE(S): {string.Join(", ", s.MissedTargetDates)}");
            sb.AppendLine($"- Last activity (comment/worklog): {s.LastActivityDate} ({s.BusinessDaysSinceActivity} business day(s) ago)");
            if (s.IsStalled)
                sb.AppendLine("- ⚠ STALLED: no comment or worklog activity in 5+ business days");
            return sb.ToString();
        }

        // ---------------------------------------------------------------------------
        // Worklogs
        // ---------------------------------------------------------------------------
        private async Task<List<JiraFetchWorklog>> FetchWorklogsAsync(string ticketNumber)
        {
            var worklogs = new List<JiraFetchWorklog>();
            int startAt = 0;
            try
            {
                do
                {
                    var wUrl  = $"{_jiraBaseUrl}/rest/api/3/issue/{ticketNumber}/worklog?startAt={startAt}&maxResults=100";
                    var wResp = await _jiraClient.GetAsync(wUrl);
                    if (!wResp.IsSuccessStatusCode) break;
                    var wJson = await wResp.Content.ReadAsStringAsync();
                    var wPage = JsonConvert.DeserializeObject<JiraWorklogPageRaw>(wJson);
                    foreach (var w in wPage?.worklogs ?? new List<JiraWorklogRaw>())
                        worklogs.Add(new JiraFetchWorklog
                        {
                            Author           = w.author?.displayName ?? "Unknown",
                            Started          = (w.started ?? "").Substring(0, Math.Min(10, (w.started ?? "").Length)),
                            TimeSpent        = w.timeSpent ?? "0m",
                            TimeSpentSeconds = w.timeSpentSeconds,
                            Comment          = AdfToText(w.comment)
                        });
                    startAt += wPage?.worklogs?.Count ?? 0;
                    if (startAt >= (wPage?.total ?? 0)) break;
                } while (true);
            }
            catch { }
            return worklogs.OrderBy(w => w.Started).ToList();
        }

        /// <summary>
        /// Renders worklogs as a per-author total rollup followed by the full chronological entry list.
        /// </summary>
        public string BuildWorklogsSection(List<JiraFetchWorklog> worklogs)
        {
            if (worklogs == null || worklogs.Count == 0) return "No worklogs.";

            var totals = worklogs
                .GroupBy(w => w.Author)
                .Select(g => $"- {g.Key}: {FormatSeconds(g.Sum(w => w.TimeSpentSeconds))}")
                .ToList();

            var entries = worklogs.Select(w =>
                $"- {w.Author} — {w.TimeSpent} ({w.Started}){(string.IsNullOrWhiteSpace(w.Comment) || w.Comment == "(no description)" ? "" : $": {w.Comment}")}");

            var sb = new StringBuilder();
            sb.AppendLine("**Totals by author:**");
            sb.AppendLine(string.Join("\n", totals));
            sb.AppendLine();
            sb.AppendLine("**Entries:**");
            sb.AppendLine(string.Join("\n", entries));
            return sb.ToString();
        }

        private static string FormatSeconds(int seconds)
        {
            int hours   = seconds / 3600;
            int minutes = (seconds % 3600) / 60;
            if (hours > 0 && minutes > 0) return $"{hours}h {minutes}m";
            if (hours > 0) return $"{hours}h";
            return $"{minutes}m";
        }

        // ---------------------------------------------------------------------------
        // Linked bug analysis — lighter than the full jira-fetch pipeline: status +
        // last few comments only, no attachments/excel/patterns/similar-ticket search.
        // ---------------------------------------------------------------------------
        public async Task<JiraFetchLinkedBugDetail> FetchLinkedBugDetailAsync(string bugKey)
        {
            var url  = $"{_jiraBaseUrl}/rest/api/3/issue/{bugKey}?fields=summary,status,assignee";
            var resp = await _jiraClient.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"Jira {resp.StatusCode}: {bugKey}");

            var json   = await resp.Content.ReadAsStringAsync();
            var data   = JsonConvert.DeserializeObject<JiraIssueRaw>(json);
            var fields = data.fields;

            var comments = new List<JiraFetchComment>();
            try
            {
                var cUrl  = $"{_jiraBaseUrl}/rest/api/3/issue/{bugKey}/comment?startAt=0&maxResults=100";
                var cResp = await _jiraClient.GetAsync(cUrl);
                if (cResp.IsSuccessStatusCode)
                {
                    var cJson = await cResp.Content.ReadAsStringAsync();
                    var cPage = JsonConvert.DeserializeObject<JiraCommentPageRaw>(cJson);
                    foreach (var c in cPage?.comments ?? new List<JiraCommentRaw>())
                        comments.Add(new JiraFetchComment
                        {
                            Author  = c.author?.displayName ?? "Unknown",
                            Created = (c.created ?? "").Substring(0, Math.Min(10, (c.created ?? "").Length)),
                            Body    = AdfToText(c.body)
                        });
                }
            }
            catch { }

            return new JiraFetchLinkedBugDetail
            {
                Key            = bugKey,
                Summary        = fields?.summary ?? "(no summary)",
                Status         = fields?.status?.name ?? "Unknown",
                Assignee       = fields?.assignee?.displayName ?? "Unassigned",
                RecentComments = comments.Count > 3 ? comments.Skip(comments.Count - 3).ToList() : comments
            };
        }

        /// <summary>
        /// One-sentence status/hold-up summary for a single non-closed linked bug.
        /// </summary>
        public string BuildLinkedBugOneSentencePrompt(JiraFetchLinkedBugDetail bug)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Summarize this linked Bug ticket's CURRENT state in EXACTLY ONE sentence.");
            sb.AppendLine("Include the current status, and any hold-up/blocker/waiting-on if one is mentioned — otherwise say what's actively happening.");
            sb.AppendLine("Output ONLY the one sentence. No preamble, no bullet point, no restating the ticket key.");
            sb.AppendLine();
            sb.AppendLine($"Ticket: {bug.Key} — {bug.Summary}");
            sb.AppendLine($"Status: {bug.Status}");
            sb.AppendLine($"Assignee: {bug.Assignee}");
            if (bug.RecentComments.Count > 0)
            {
                sb.AppendLine("Recent comments (oldest to newest):");
                foreach (var c in bug.RecentComments)
                    sb.AppendLine($"- ({c.Created}, {c.Author}): {CleanComment(c.Body, c.Author)}");
            }
            else
            {
                sb.AppendLine("No comments.");
            }
            sb.AppendLine();
            sb.AppendLine("One-sentence summary:");
            return sb.ToString();
        }

        // ---------------------------------------------------------------------------
        // Read text attachments
        // ---------------------------------------------------------------------------
        public List<JiraFetchAttachmentContent> ReadTextAttachments(JiraFetchTicket ticket)
        {
            var contents = new List<JiraFetchAttachmentContent>();
            foreach (var att in ticket.Attachments)
            {
                if (att.Path == null) continue;
                string ext = Path.GetExtension(att.Filename).ToLowerInvariant();
                if (!TextExtensions.Contains(ext) || ImageExtensions.Contains(ext)) continue;
                try
                {
                    string content = File.ReadAllText(att.Path);
                    contents.Add(new JiraFetchAttachmentContent { Name = att.Filename, Content = content });
                }
                catch { }
            }
            return contents;
        }

        // ---------------------------------------------------------------------------
        // Excel → JSON conversion
        // ---------------------------------------------------------------------------
        public List<(string Filename, string JsonPath)> ConvertExcelAttachments(JiraFetchTicket ticket)
        {
            var results = new List<(string, string)>();
            foreach (var att in ticket.Attachments)
            {
                if (att.Path == null) continue;
                string ext = Path.GetExtension(att.Filename).ToLowerInvariant();
                if (ext != ".xlsx" && ext != ".xls") continue;
                try
                {
                    using var wb = ClosedXML.Excel.XLWorkbook.OpenFromTemplate(att.Path);
                    var dict = new Dictionary<string, object>();
                    foreach (var ws in wb.Worksheets)
                    {
                        var rows = new List<Dictionary<string, string>>();
                        var headers = ws.Row(1).Cells().Select(c => c.GetString()).ToList();
                        foreach (var row in ws.RowsUsed().Skip(1))
                        {
                            var d = new Dictionary<string, string>();
                            for (int i = 0; i < headers.Count; i++)
                                d[headers[i]] = row.Cell(i + 1).GetString();
                            rows.Add(d);
                        }
                        dict[ws.Name] = rows;
                    }
                    string jsonPath = att.Path.Replace(".xlsx", ".json", StringComparison.OrdinalIgnoreCase)
                                              .Replace(".xls",  ".json", StringComparison.OrdinalIgnoreCase);
                    File.WriteAllText(jsonPath, JsonConvert.SerializeObject(dict, Formatting.Indented));
                    results.Add((att.Filename, jsonPath));
                }
                catch { }
            }
            return results;
        }

        // ---------------------------------------------------------------------------
        // Pattern search
        // ---------------------------------------------------------------------------
        public List<string> SearchPatterns(string query)
        {
            string file = null;
            try { if (Directory.Exists(NetworkShareDir)) file = PatternsFile; } catch { }
            if (file == null)
            {
                string local = Path.Combine(DataDir, "analysis_patterns.md");
                if (File.Exists(local)) file = local;
            }
            if (file == null || !File.Exists(file)) return new List<string>();

            string content = File.ReadAllText(file);
            string ql = query.ToLowerInvariant();
            return Regex.Split(content, @"(?=\n### )")
                        .Where(b => b.ToLowerInvariant().Contains(ql))
                        .Select(b => b.Trim())
                        .ToList();
        }

        // ---------------------------------------------------------------------------
        // Keyword extraction — shared by similar-ticket search and commonality check
        // ---------------------------------------------------------------------------
        private static readonly HashSet<string> StopWords = new HashSet<string> { "a","an","the","and","or","for","to","of","in","is","it","its","be","by","do","no","so","at","on","up","if","as","fix","fixing","update","change","add","misc","issue","issues","sending","sent","when","with","from","that","this","not","auto","base" };

        private List<string> ExtractKeywords(string summary, string description)
        {
            var summaryKw = Regex.Split(summary ?? "", @"[\s\-\/]+")
                .Select(w => Regex.Replace(w, @"[^a-zA-Z0-9]", ""))
                .Where(w => w.Length >= 2 && !StopWords.Contains(w.ToLowerInvariant()))
                .Distinct().ToList();

            var descKw = Regex.Matches(description ?? "", @"\b[A-Z][a-zA-Z]{3,}|[a-z]+[A-Z][a-zA-Z]+|[A-Z]{2,3}\b")
                .Cast<Match>().Select(m => Regex.Replace(m.Value, @"[^a-zA-Z0-9]", ""))
                .Where(w => w.Length >= 3 && !StopWords.Contains(w.ToLowerInvariant()))
                .Distinct().ToList();

            return descKw.Concat(summaryKw).Distinct().Take(7).ToList();
        }

        // ---------------------------------------------------------------------------
        // Similar tickets
        // ---------------------------------------------------------------------------
        public async Task<JiraFetchSimilarTickets> FindSimilarTicketsAsync(string ticketNumber, string summary, string description)
        {
            var keywords = ExtractKeywords(summary, description);
            var result   = new JiraFetchSimilarTickets();
            if (keywords.Count == 0) return result;

            // Jira search
            try
            {
                string kw  = string.Join(" AND ", keywords.Take(3).Select(k => $"summary ~ \"{k}\""));
                string jql = $"project in (CRM, ITC) AND ({kw}) AND status in (\"LIVE/ COMPLETE\",\"DONE\",\"Live/Complete\",\"CLOSED\") AND issue != {ticketNumber}";
                var body   = JsonConvert.SerializeObject(new { jql, fields = new[] { "summary", "status", "updated" }, maxResults = 10 });
                var resp   = await _jiraClient.PostAsync($"{_jiraBaseUrl}/rest/api/3/search/jql",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    var data = JsonConvert.DeserializeObject<JiraSearchRaw>(json);
                    result.JiraMatches = (data?.issues ?? new List<JiraIssueRaw>())
                        .Select(i => new JiraFetchMatch
                        {
                            Key     = i.key,
                            Summary = i.fields?.summary ?? "",
                            Status  = i.fields?.status?.name ?? "",
                            Updated = (i.fields?.updated ?? "").Substring(0, Math.Min(10, (i.fields?.updated ?? "").Length))
                        })
                        .OrderByDescending(m => m.Updated).ToList();
                }
            }
            catch { }

            // GitLab MR search
            try
            {
                string search = Uri.EscapeDataString(string.Join(" ", keywords.Take(2)));
                var resp = await _gitlabClient.GetAsync($"{GitLabBase}/groups/{GitLabGroup}/merge_requests?state=merged&search={search}&per_page=10");
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    var mrs  = JsonConvert.DeserializeObject<List<GitLabMrRaw>>(json) ?? new List<GitLabMrRaw>();
                    result.GitLabMrMatches = mrs.Select(mr => new JiraFetchMrMatch
                    {
                        Iid    = mr.iid,
                        Title  = mr.title,
                        Branch = mr.source_branch,
                        Merged = (mr.merged_at ?? "").Substring(0, Math.Min(10, (mr.merged_at ?? "").Length)),
                        Url    = mr.web_url
                    }).ToList();
                }
            }
            catch { }

            return result;
        }

        // ---------------------------------------------------------------------------
        // Chat/agent search — find tickets assigned to the current user, optionally
        // filtered by exact status name and/or carrier (client-side, via DetectCarrier).
        // ---------------------------------------------------------------------------
        public async Task<List<JiraFetchMatch>> SearchAssignedTicketsAsync(string status, string carrier, int maxResults = 25)
        {
            var result = new List<JiraFetchMatch>();

            var clauses = new List<string> { "project in (CRM, ITC)", "assignee = currentUser()" };
            if (!string.IsNullOrWhiteSpace(status))
                clauses.Add($"status = \"{status.Replace("\"", "'")}\"");
            string jql = string.Join(" AND ", clauses) + " ORDER BY updated DESC";

            try
            {
                var body = JsonConvert.SerializeObject(new
                {
                    jql,
                    fields = new[] { "summary", "status", "updated" },
                    maxResults = Math.Max(1, maxResults)
                });
                var resp = await _jiraClient.PostAsync($"{_jiraBaseUrl}/rest/api/3/search/jql",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                if (!resp.IsSuccessStatusCode) return result;

                var json = await resp.Content.ReadAsStringAsync();
                var data = JsonConvert.DeserializeObject<JiraSearchRaw>(json);

                foreach (var issue in data?.issues ?? new List<JiraIssueRaw>())
                {
                    string summary = issue.fields?.summary ?? "";
                    if (!string.IsNullOrWhiteSpace(carrier) &&
                        !string.Equals(DetectCarrier(summary, "", ""), carrier, StringComparison.OrdinalIgnoreCase))
                        continue;

                    result.Add(new JiraFetchMatch
                    {
                        Key     = issue.key,
                        Summary = summary,
                        Status  = issue.fields?.status?.name ?? "",
                        Updated = (issue.fields?.updated ?? "").Substring(0, Math.Min(10, (issue.fields?.updated ?? "").Length))
                    });
                }
            }
            catch { }

            return result;
        }

        /// <summary>
        /// Runs the same pipeline as the single-ticket jira-fetch Bedrock path (linked bugs,
        /// attachments, Excel conversion, patterns, similar tickets, 7-point analysis, save MD,
        /// log metrics) so the chat agent's analyze_ticket tool matches the manual-fetch behavior.
        /// </summary>
        public async Task<string> AnalyzeTicketFullAsync(string ticketNumber, BedrockService bedrock, string model, CancellationToken ct)
        {
            var ticket = await FetchTicketAsync(ticketNumber);

            foreach (var bug in ticket.LinkedBugs)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var commonality = await CheckBugCommonalityAsync(bug.Key, bug.Summary);
                    bug.IsCommon = commonality.IsCommon;
                    bug.CommonalitySummary = commonality.Summary;
                }
                catch { /* commonality is a bonus signal — don't fail the fetch over it */ }

                if (bug.IsClosed)
                {
                    bug.OneLineSummary = "Closed.";
                    continue;
                }

                try
                {
                    var detail = await FetchLinkedBugDetailAsync(bug.Key);
                    var bugPrompt = BuildLinkedBugOneSentencePrompt(detail);
                    var bugResult = await bedrock.AnalyzeAsync(bugPrompt, model, ct);
                    bug.OneLineSummary = bugResult.Success
                        ? bugResult.Content.Trim()
                        : $"{detail.Status} (analysis failed: {bugResult.ErrorMessage})";
                }
                catch (Exception ex)
                {
                    bug.OneLineSummary = $"(could not fetch: {ex.Message})";
                }
            }

            var textAttachments  = ReadTextAttachments(ticket);
            var excelConversions = ConvertExcelAttachments(ticket);
            string keywords = string.Join(" ", ticket.Summary.Split(' ').Where(w => w.Length > 4).Take(3));
            var patterns = SearchPatterns(keywords);
            var similar  = await FindSimilarTicketsAsync(ticketNumber, ticket.Summary, ticket.Description);

            var analysisPrompt = Build7PointPrompt(ticket, textAttachments, patterns, similar, null);
            var result = await bedrock.AnalyzeAsync(analysisPrompt, model, ct);
            string analysisText = result.Success ? result.Content : $"(analysis failed: {result.ErrorMessage})";

            var outputFile = BuildAndSaveMd(ticket, textAttachments, patterns, similar, analysisText, model, excelConversions);
            if (result.Success) LogMetrics(ticketNumber, ticket.Carrier, outputFile, result);

            return $"[{ticket.TicketNumber}] {ticket.Summary}\n" +
                   $"Carrier: {ticket.Carrier} | Status: {ticket.Status} | Assignee: {ticket.Assignee}\n\n" +
                   $"{analysisText}\n\n(Saved: {outputFile})";
        }

        // ---------------------------------------------------------------------------
        // Bug commonality check — is this bug pattern occurring in other carriers/states?
        // ---------------------------------------------------------------------------
        private static readonly HashSet<string> UsStateCodes = new HashSet<string> {
            "AL","AK","AZ","AR","CA","CO","CT","DE","FL","GA","HI","ID","IL","IN","IA","KS","KY","LA","ME","MD",
            "MA","MI","MN","MS","MO","MT","NE","NV","NH","NJ","NM","NY","NC","ND","OH","OK","OR","PA","RI","SC",
            "SD","TN","TX","UT","VT","VA","WA","WV","WI","WY","DC"
        };

        private string ExtractStateCode(string summary)
        {
            foreach (Match m in Regex.Matches(summary ?? "", @"\b([A-Z]{2})\b"))
                if (UsStateCodes.Contains(m.Value)) return m.Value;
            return null;
        }

        /// <summary>
        /// Flags a bug as "common" if similar tickets (by summary keywords) turn up for 2+ distinct
        /// carriers, or the same carrier shows up in 2+ distinct states. Searches all statuses —
        /// a pattern recurring across several currently-open tickets is just as meaningful as closed ones.
        /// </summary>
        public async Task<BugCommonalityResult> CheckBugCommonalityAsync(string ticketKey, string summary)
        {
            var result = new BugCommonalityResult();
            var keywords = ExtractKeywords(summary, null);
            if (keywords.Count == 0) return result;

            try
            {
                string kw  = string.Join(" AND ", keywords.Take(3).Select(k => $"summary ~ \"{k}\""));
                string jql = $"project in (CRM, ITC) AND ({kw}) AND issue != {ticketKey}";
                var body   = JsonConvert.SerializeObject(new { jql, fields = new[] { "summary", "status", "updated" }, maxResults = 25 });
                var resp   = await _jiraClient.PostAsync($"{_jiraBaseUrl}/rest/api/3/search/jql",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                if (!resp.IsSuccessStatusCode) return result;

                var json = await resp.Content.ReadAsStringAsync();
                var data = JsonConvert.DeserializeObject<JiraSearchRaw>(json);

                foreach (var issue in data?.issues ?? new List<JiraIssueRaw>())
                {
                    string mSummary = issue.fields?.summary ?? "";
                    result.Matches.Add(new JiraFetchMatch
                    {
                        Key     = issue.key,
                        Summary = mSummary,
                        Status  = issue.fields?.status?.name ?? "",
                        Updated = (issue.fields?.updated ?? "").Substring(0, Math.Min(10, (issue.fields?.updated ?? "").Length))
                    });
                }

                var carrierStates = result.Matches
                    .Select(m => new { Carrier = DetectCarrier(m.Summary, "", ""), State = ExtractStateCode(m.Summary) })
                    .Where(x => x.Carrier != "Unknown")
                    .ToList();

                var distinctCarriers   = carrierStates.Select(x => x.Carrier).Distinct().ToList();
                bool multiStateCarrier = carrierStates
                    .Where(x => x.State != null)
                    .GroupBy(x => x.Carrier)
                    .Any(g => g.Select(x => x.State).Distinct().Count() >= 2);

                result.Carriers  = distinctCarriers;
                result.IsCommon  = distinctCarriers.Count >= 2 || multiStateCarrier;
                result.Summary   = string.Join(", ", carrierStates
                    .GroupBy(x => x.Carrier)
                    .Select(g =>
                    {
                        var states = g.Select(x => x.State).Where(s => s != null).Distinct().ToList();
                        return states.Count > 0 ? $"{g.Key} ({string.Join("/", states)})" : g.Key;
                    }));
            }
            catch { }

            return result;
        }

        // ---------------------------------------------------------------------------
        // 7-point analysis prompt
        // ---------------------------------------------------------------------------
        /// <summary>
        /// Summarizes a single comment into two bullets:
        /// - Summary: what happened in this comment
        /// - Issue: any problem/finding/blocker relevant to the 7-point analysis (or "None")
        /// </summary>
        public string BuildCommentSummaryPrompt(JiraFetchComment comment)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Analyze this Jira ticket comment. Output EXACTLY 2 bullet lines. English only.");
            sb.AppendLine();
            sb.AppendLine("Line 1: '- Summary: [1-2 sentence summary of what happened in this comment]'");
            sb.AppendLine("Line 2: '- Issue: [specific problem, bug, blocker, error, or finding mentioned — or \"None\" if no issue]'");
            sb.AppendLine();
            sb.AppendLine("RULES:");
            sb.AppendLine("- Summary: preserve root causes, fixes, decisions, carrier responses, escalations. Drop greetings/disclaimers.");
            sb.AppendLine("- Issue: extract only concrete problems (error messages, bugs, blockers, spec gaps, carrier rejections). Not 'None' if there is a real issue.");
            sb.AppendLine("- Output ONLY the 2 bullet lines. Nothing else.");
            sb.AppendLine();
            sb.AppendLine($"Author: {comment.Author} ({comment.Created})");
            sb.AppendLine("Comment:");
            sb.AppendLine(CleanComment(comment.Body, comment.Author));
            return sb.ToString();
        }

        public string Build7PointPrompt(
            JiraFetchTicket ticket,
            List<JiraFetchAttachmentContent> attachments,
            List<string> patterns,
            JiraFetchSimilarTickets similar,
            List<string> commentSummaries = null)
        {
            string attachmentSection = attachments.Count > 0
                ? string.Join("\n\n", attachments.Select(a =>
                    $"### Attachment: {a.Name}\n```\n{(a.Content.Length > MaxAttachmentChars ? a.Content.Substring(0, MaxAttachmentChars) + "\n... [truncated]" : a.Content)}\n```"))
                : "No readable attachments.";

            string patternSection = patterns.Count > 0
                ? string.Join("\n\n", patterns.Take(3))
                : "No matching patterns found.";

            string similarSection = string.Join("\n",
                similar.JiraMatches.Select(m => $"- [{m.Key}] {m.Summary} ({m.Status}, updated {m.Updated})")
                .Concat(similar.GitLabMrMatches.Select(mr => $"- MR !{mr.Iid}: {mr.Title} (merged {mr.Merged}) — {mr.Url}")));
            if (string.IsNullOrEmpty(similarSection)) similarSection = "None found.";

            // Extract issues from comment summaries for Point 1
            var issueLines = new List<string>();
            string commentsSection;

            if (commentSummaries != null && commentSummaries.Count > 0)
            {
                var summaryLines = new List<string>();
                for (int i = 0; i < Math.Min(commentSummaries.Count, ticket.Comments.Count); i++)
                {
                    string author  = ticket.Comments[i].Author;
                    string created = ticket.Comments[i].Created;
                    string content = commentSummaries[i];

                    summaryLines.Add($"**{author} ({created}):**\n{content}");

                    // Extract Issue bullet if present and not "None"
                    var issueLine = content.Split('\n')
                        .FirstOrDefault(l => l.TrimStart().StartsWith("- Issue:") &&
                                             !l.Contains("None", StringComparison.OrdinalIgnoreCase));
                    if (issueLine != null)
                        issueLines.Add($"({created} — {author}) {issueLine.Replace("- Issue:", "").Trim()}");
                }
                commentsSection = string.Join("\n\n", summaryLines);
            }
            else if (ticket.Comments.Count > 0)
            {
                commentsSection = string.Join("\n\n", ticket.Comments.Select(c =>
                    $"**{c.Author} ({c.Created}):** {CleanComment(c.Body, c.Author)}"));
            }
            else
            {
                commentsSection = "No comments.";
            }

            string issueSection = issueLines.Count > 0
                ? "EXTRACTED ISSUES (use these for Point 1):\n" + string.Join("\n", issueLines.Select(l => $"- {l}"))
                : string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("You are an insurance carrier RTR integration developer analyzing a Jira ticket. Produce a concise 7-point analysis in Markdown.");
            sb.AppendLine();
            sb.AppendLine("IMPORTANT — READ IN THIS ORDER:");
            sb.AppendLine("1. Ticket status and summary (tells you the current state)");
            sb.AppendLine("2. Comments (contain the REAL story — root causes, fixes, decisions)");
            sb.AppendLine("3. Worklogs (worklog comments often contain investigation notes, root causes, or decisions not repeated in the ticket's comments — treat as a real source, not just time tracking)");
            sb.AppendLine("4. Linked Bugs (may reveal blockers or root causes not in this ticket's own comments)");
            sb.AppendLine("5. Attachments (supporting evidence only — do NOT base your analysis primarily on attachment content)");
            sb.AppendLine();
            sb.AppendLine($"## Ticket: {ticket.TicketNumber}");
            sb.AppendLine($"- **Summary:** {ticket.Summary}");
            sb.AppendLine($"- **Status:** {ticket.Status}");
            sb.AppendLine($"- **Carrier:** {ticket.Carrier}");
            sb.AppendLine($"- **Priority:** {ticket.Priority}");
            sb.AppendLine($"- **Assignee:** {ticket.Assignee}");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(issueSection))
            {
                sb.AppendLine("## Extracted Issues (pre-identified from comments — use for Point 1)");
                sb.AppendLine(issueSection);
                sb.AppendLine();
            }
            sb.AppendLine("## Comments (PRIMARY SOURCE — read these first)");
            sb.AppendLine(commentsSection);
            sb.AppendLine();
            sb.AppendLine("## Linked Bugs");
            sb.AppendLine(BuildLinkedBugsSection(ticket.LinkedBugs));
            sb.AppendLine();
            sb.AppendLine("## Worklogs (comments may contain investigation notes / root causes — read for content, not just time spent)");
            sb.AppendLine(BuildWorklogsSection(ticket.Worklogs));
            sb.AppendLine();
            sb.AppendLine("## Effort & Schedule (⚠ flags are pre-computed — use them to judge if the ticket is running behind and where)");
            sb.AppendLine(BuildScheduleSection(ticket.Schedule));
            sb.AppendLine();
            sb.AppendLine("## Description");
            sb.AppendLine(ticket.Description);
            sb.AppendLine();
            sb.AppendLine("## Attachments (supporting evidence only)");
            sb.AppendLine(attachmentSection);
            sb.AppendLine();
            sb.AppendLine("## Known Patterns");
            sb.AppendLine(patternSection);
            sb.AppendLine();
            sb.AppendLine("## Similar Prior Work");
            sb.AppendLine(similarSection);
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("STRICT RULES:");
            sb.AppendLine("- Responsibility must be exactly one of: Our Side / Carrier Side / Platform-Core / Pending");
            sb.AppendLine("- Our Side = code/mapping/config/DB issue in our module. Base bridge bugs = Our Side. New Development = Our Side.");
            sb.AppendLine("- Carrier Side = carrier API, business rules, spec gaps requiring carrier input.");
            sb.AppendLine("- Platform-Core = TR platform issue unrelated to our module.");
            sb.AppendLine("- Current Status rules:");
            sb.AppendLine("  * LIVE/COMPLETE or DONE → 'Fixed and deployed to Live'");
            sb.AppendLine("  * QA-Ready or QA-In-Progress → state what is still pending QA and what was most recently fixed");
            sb.AppendLine("  * Waiting-Carrier → state what is waiting and from whom");
            sb.AppendLine("  * In-Progress → state what is actively being worked on");
            sb.AppendLine("  * Never say 'no immediate action required' unless the ticket is fully closed");
            sb.AppendLine("  * If the Effort & Schedule section shows an OVER ESTIMATE, MISSED TARGET DATE, or STALLED flag, mention it and use the Dev/QA logged-time split plus the ticket status to hint at WHERE the hold-up likely is (e.g. heavy Dev time with no QA time logged and a QA-Ready-adjacent status → Dev hasn't actually handed off yet; heavy QA time logged in a Corrections/QA status → bugs are recurring in QA; Waiting-Carrier status plus stalled → the hold-up is with the carrier, not us). Don't over-claim — say \"likely\" and name the specific signal that suggests it.");
            sb.AppendLine("- Open Items rules:");
            sb.AppendLine("  * List ONLY concrete pending actions derived from the comments — not generic advice");
            sb.AppendLine("  * Reference specific issue numbers, fields, or actions from the comments");
            sb.AppendLine("  * If all issues are resolved, say so explicitly and list only carrier/UAT pending items");
            sb.AppendLine("- Affected Scope format: \"Carrier (Program) — State Line\" e.g. \"Patriot (Auto MGA) — TX Auto\" or \"GEICO — UT Auto\". Never just the carrier name.");
            sb.AppendLine("- Do NOT write team names as Responsibility value.");
            sb.AppendLine("- Base your analysis on the COMMENTS, not the attachment content.");
            sb.AppendLine();
            sb.AppendLine("Produce the 7-point analysis. Output only the 7 sections. No preamble.");
            sb.AppendLine("For Point 1: synthesize ALL extracted issues and comment summaries into one coherent paragraph — not just the most recent.");
            sb.AppendLine();
            sb.AppendLine("### 1. Summarized Issue");
            sb.AppendLine("### 2. Responsibility");
            sb.AppendLine("### 3. Affected Scope");
            sb.AppendLine("### 4. Current Status");
            sb.AppendLine("### 5. Open Items / Next Steps");
            sb.AppendLine("### 6. Similar Prior Work");
            sb.AppendLine("### 7. Known Pattern?");
            return sb.ToString();

        }

        // ---------------------------------------------------------------------------
        // Save MD file
        // ---------------------------------------------------------------------------
        public string BuildAndSaveMd(
            JiraFetchTicket ticket,
            List<JiraFetchAttachmentContent> attachments,
            List<string> patterns,
            JiraFetchSimilarTickets similar,
            string analysisText,
            string model,
            List<(string Filename, string JsonPath)> excelConversions)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Analysis Brief: {ticket.TicketNumber}");
            sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine($"Carrier: {ticket.Carrier} | Status: {ticket.Status} | Priority: {ticket.Priority}");
            sb.AppendLine($"Assignee: {ticket.Assignee}");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("## Ticket Info");
            sb.AppendLine($"- **Summary:** {ticket.Summary}");
            sb.AppendLine($"- **Created:** {ticket.Created} | **Updated:** {ticket.Updated}");
            sb.AppendLine();
            sb.AppendLine("## Effort & Schedule");
            sb.AppendLine(BuildScheduleSection(ticket.Schedule));
            sb.AppendLine();
            sb.AppendLine("## Description");
            sb.AppendLine(ticket.Description);
            sb.AppendLine();
            sb.AppendLine("## Comments");
            if (ticket.Comments.Count > 0)
                foreach (var c in ticket.Comments)
                    sb.AppendLine($"**{c.Author} ({c.Created}):** {CleanComment(c.Body, c.Author)}\n");
            else
                sb.AppendLine("No comments.");
            sb.AppendLine();
            sb.AppendLine("## Linked Bugs");
            sb.AppendLine(BuildLinkedBugsSection(ticket.LinkedBugs));
            sb.AppendLine();
            sb.AppendLine("## Worklogs");
            sb.AppendLine(BuildWorklogsSection(ticket.Worklogs));
            sb.AppendLine();
            sb.AppendLine("## Attachments");
            foreach (var att in ticket.Attachments)
            {
                string ext = Path.GetExtension(att.Filename).ToLowerInvariant();
                if (ext == ".xlsx" || ext == ".xls")
                {
                    var conv = excelConversions.FirstOrDefault(e => e.Filename == att.Filename);
                    sb.AppendLine(conv.JsonPath != null
                        ? $"### {att.Filename}\n_Excel — JSON: `{conv.JsonPath}`_"
                        : $"### {att.Filename}\n_Excel — conversion failed_");
                }
                else if (TextExtensions.Contains(ext))
                {
                    var content = attachments.FirstOrDefault(a => a.Name == att.Filename);
                    if (content != null)
                        sb.AppendLine(content.Content.Length <= MaxAttachmentChars
                            ? $"### {att.Filename}\n```\n{content.Content}\n```"
                            : $"### {att.Filename}\n_Too large ({content.Content.Length:N0} chars). File: `{att.Path}`_");
                }
                else
                {
                    sb.AppendLine($"### {att.Filename}\n_Binary — available at: `{att.Path}`_");
                }
            }
            sb.AppendLine();
            sb.AppendLine("## Known Patterns");
            sb.AppendLine(patterns.Count > 0 ? string.Join("\n\n", patterns.Take(3)) : "No matching patterns found.");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("## 7-Point Analysis");
            sb.AppendLine(string.IsNullOrEmpty(analysisText)
                ? "> Paste this file into Claude Code and ask: \"Complete the 7-point analysis based on the above.\"\n\n### 1. Summarized Issue\n_Pending_\n\n### 2. Responsibility\n_Pending_\n\n### 3. Affected Scope\n_Pending_\n\n### 4. Current Status\n_Pending_\n\n### 5. Open Items / Next Steps\n_Pending_\n\n### 6. Similar Prior Work\n_Pending_\n\n### 7. Known Pattern?\n_Pending_"
                : $"> Generated by Ollama ({model})\n\n{analysisText}");

            string outputFile = Path.Combine(ticket.OutputDir, $"{ticket.TicketNumber}_7point.md");
            File.WriteAllText(outputFile, sb.ToString(), Encoding.UTF8);
            return outputFile;
        }

        /// <summary>
        /// Renders linked bugs as "- [KEY] Summary (Status): one-line summary" — closed bugs
        /// just say "Closed.", open ones show whatever OneLineSummary was filled in with.
        /// </summary>
        private string BuildLinkedBugsSection(List<JiraFetchLinkedBug> linkedBugs)
        {
            if (linkedBugs == null || linkedBugs.Count == 0) return "No linked bugs.";

            return string.Join("\n", linkedBugs.Select(b =>
            {
                string flag = b.IsCommon ? $" ⚠ COMMON — also seen in {b.CommonalitySummary}." : "";
                string line = b.OneLineSummary ?? (b.IsClosed ? "Closed." : "(not analyzed)");
                return $"- [{b.Key}] {b.Summary} ({b.Status}):{flag} {line}";
            }));
        }

        // ---------------------------------------------------------------------------
        // Metrics logging
        // ---------------------------------------------------------------------------
        public void LogMetrics(string ticketNumber, string carrier, string outputFile, OllamaAnalysisResult result)
        {
            try
            {
                Directory.CreateDirectory(LogsDir);
                string today    = DateTime.Now.ToString("yyyy-MM-dd");
                string logFile  = Path.Combine(LogsDir, $"daily-{today}.jsonl");
                var entry = new
                {
                    timestamp    = DateTime.Now.ToString("o"),
                    ticket       = ticketNumber,
                    carrier,
                    analysisFile = outputFile,
                    metrics = new
                    {
                        model          = result.Model,
                        responseTimeSec = result.ResponseTimeSec,
                        tokensPerSec   = result.TokensPerSec,
                        inputTokens    = result.InputTokens,
                        outputTokens   = result.OutputTokens,
                        claudeEquivUsd = (result.InputTokens * 3.0 + result.OutputTokens * 15.0) / 1_000_000.0
                    }
                };
                File.AppendAllText(logFile, JsonConvert.SerializeObject(entry) + "\n");
            }
            catch { }
        }

        // ---------------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------------
        /// <summary>
        /// Cleans a Jira comment — strips email thread history for Zywave Jira posts,
        /// removes greetings, sign-offs, CC lines, and link protection wrappers.
        /// </summary>
        public static string CleanComment(string body, string author)
        {
            if (string.IsNullOrWhiteSpace(body)) return string.Empty;

            body = body.Replace("\r\n", "\n").Replace("\r", "\n");

            // Email thread posted by "Zywave Jira" — keep only newest (top) message
            if (author.IndexOf("Zywave Jira", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var lines    = body.Split('\n');
                var topLines = new List<string>();
                foreach (var line in lines)
                {
                    string t = line.Trim();
                    // Stop at any email thread separator
                    if (t.StartsWith("-----Original Message-----") ||
                        t.StartsWith("________________________________") ||
                        t.StartsWith("Warning: Unusual sender") ||
                        t.StartsWith("You don't often get email from") ||
                        (t.StartsWith("From:") && topLines.Count > 2) ||
                        (t.StartsWith("Sent:") && topLines.Count > 2) ||
                        (t.StartsWith("On 20") && t.Contains("wrote:")) ||
                        (t.StartsWith("From: Turbo Rater Support") && topLines.Count > 2) ||
                        t.StartsWith("https://linkprotect.cudasvc.com"))
                        break;
                    topLines.Add(line);
                }
                body = string.Join("\n", topLines).Trim();
            }

            // Strip noise from all comments
            var cleanLines = new List<string>();
            foreach (var line in body.Split('\n'))
            {
                string t = line.Trim();
                if (Regex.IsMatch(t, @"^(Hi|Hello|Dear|Hey)\s*[,\.]?\s*$", RegexOptions.IgnoreCase)) continue;
                if (Regex.IsMatch(t, @"^(Hi|Hello|Dear|Hey)\s+\w[\w\s]*[,\.]?\s*$", RegexOptions.IgnoreCase)) continue;
                if (Regex.IsMatch(t, @"^(Thanks|Thank you|Regards|Best regards|Kind regards|Sincerely|Cheers)[,\.]?\s*$", RegexOptions.IgnoreCase)) continue;
                if (t.StartsWith("CC:", StringComparison.OrdinalIgnoreCase)) continue;
                if (t.StartsWith("cc:", StringComparison.OrdinalIgnoreCase)) continue;
                if (t.Length <= 1) continue;
                cleanLines.Add(line);
            }

            return string.Join(" ", cleanLines.Select(l => l.Trim())).Trim();
        }

        private string AdfToText(object adfNode)
        {
            if (adfNode == null) return "(no description)";
            try
            {
                var json = adfNode is string s ? s : JsonConvert.SerializeObject(adfNode);
                var node = JsonConvert.DeserializeObject<AdfNode>(json);
                return AdfNodeToText(node)?.Trim() ?? "(no description)";
            }
            catch { return "(no description)"; }
        }

        private string AdfNodeToText(AdfNode node)
        {
            if (node == null) return "";
            if (node.type == "text") return node.text ?? "";
            if (node.content != null)
                return string.Join(" ", node.content.Select(AdfNodeToText)).Replace("  ", " ").Trim();
            return "";
        }

        private string DetectCarrier(string summary, string description, string attachmentNames)
        {
            if (!File.Exists(CarrierLookupFile)) return "Unknown";
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadAllLines(CarrierLookupFile))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
                var parts = line.Split('|');
                if (parts.Length == 2) map[parts[0].Trim()] = parts[1].Trim();
            }
            string sl = summary.ToLowerInvariant();
            foreach (var kv in map) if (sl.Contains(kv.Key.ToLowerInvariant())) return kv.Value;
            string fl = $"{description} {attachmentNames}".ToLowerInvariant();
            foreach (var kv in map) if (fl.Contains(kv.Key.ToLowerInvariant())) return kv.Value;
            return "Unknown";
        }

        private string SanitizeFilename(string name) =>
            Regex.Replace(name, @"[<>:""/\\|?*\x00-\x1f]", "_");
    }

    // ---------------------------------------------------------------------------
    // Models
    // ---------------------------------------------------------------------------
    public class JiraFetchTicket
    {
        public string TicketNumber { get; set; }
        public string Summary { get; set; }
        public string Status { get; set; }
        public string Assignee { get; set; }
        public string Reporter { get; set; }
        public string Priority { get; set; }
        public string Created { get; set; }
        public string Updated { get; set; }
        public string Description { get; set; }
        public string Carrier { get; set; }
        public List<JiraFetchAttachment> Attachments { get; set; } = new List<JiraFetchAttachment>();
        public List<JiraFetchComment> Comments { get; set; } = new List<JiraFetchComment>();
        public List<JiraFetchWorklog> Worklogs { get; set; } = new List<JiraFetchWorklog>();
        public JiraFetchScheduleInfo Schedule { get; set; }
        public List<JiraFetchLinkedBug> LinkedBugs { get; set; } = new List<JiraFetchLinkedBug>();
        public string OutputDir { get; set; }
        public string AttachmentDir { get; set; }
    }

    /// <summary>
    /// A Bug linked to the fetched ticket. Closed bugs are never analyzed further —
    /// OneLineSummary is just "Closed." Open bugs get a lighter fetch + one-sentence AI summary.
    /// </summary>
    public class JiraFetchLinkedBug
    {
        public string Key { get; set; }
        public string Summary { get; set; }
        public string Status { get; set; }
        public bool IsClosed { get; set; }
        public string OneLineSummary { get; set; }
        public bool IsCommon { get; set; }
        public string CommonalitySummary { get; set; }
    }

    /// <summary>
    /// Lighter-than-jira-fetch detail for a single linked bug — status + last few comments only,
    /// no attachments/excel/patterns/similar-ticket search.
    /// </summary>
    public class JiraFetchLinkedBugDetail
    {
        public string Key { get; set; }
        public string Summary { get; set; }
        public string Status { get; set; }
        public string Assignee { get; set; }
        public List<JiraFetchComment> RecentComments { get; set; } = new List<JiraFetchComment>();
    }

    /// <summary>
    /// Result of checking whether a bug pattern recurs across carriers/states.
    /// </summary>
    public class BugCommonalityResult
    {
        public bool IsCommon { get; set; }
        public List<string> Carriers { get; set; } = new List<string>();
        public string Summary { get; set; } = "";
        public List<JiraFetchMatch> Matches { get; set; } = new List<JiraFetchMatch>();
    }

    public class JiraFetchAttachment
    {
        public string Filename { get; set; }
        public string Status { get; set; }
        public string Path { get; set; }
    }

    public class JiraFetchAttachmentContent
    {
        public string Name { get; set; }
        public string Content { get; set; }
    }

    public class JiraFetchComment
    {
        public string Author { get; set; }
        public string Created { get; set; }
        public string Body { get; set; }
    }

    public class JiraFetchWorklog
    {
        public string Author { get; set; }
        public string Started { get; set; }
        public string TimeSpent { get; set; }
        public int TimeSpentSeconds { get; set; }
        public string Comment { get; set; }
    }

    /// <summary>
    /// Effort estimates (Dev/QA/Total), logged-time split (by matching worklog author against the
    /// Programmer/Tester fields — a heuristic, since Jira doesn't tag worklog entries by type),
    /// target dates, and derived over-estimate/missed-date/stalled flags.
    /// </summary>
    public class JiraFetchScheduleInfo
    {
        public double? DevEstimateHours { get; set; }
        public double? QaEstimateHours { get; set; }
        public double? TotalEstimateHours { get; set; }

        public int TotalTimeSpentSeconds { get; set; }
        public int DevTimeSpentSeconds { get; set; }
        public int QaTimeSpentSeconds { get; set; }
        public int OtherTimeSpentSeconds { get; set; }

        public string Programmer { get; set; }
        public string Tester { get; set; }

        public string SpecsReceived { get; set; }
        public string DateScheduled { get; set; }
        public string QaStartDate { get; set; }
        public string QaDeliveryDate { get; set; }
        public string DevDueDate { get; set; }
        public string QaDueDate { get; set; }
        public string DueDate { get; set; }

        public bool IsDone { get; set; }
        public string LastActivityDate { get; set; }
        public int BusinessDaysSinceActivity { get; set; }

        public bool DevOverEstimate { get; set; }
        public bool QaOverEstimate { get; set; }
        public bool TotalOverEstimate { get; set; }
        public List<string> MissedTargetDates { get; set; } = new List<string>();
        public bool IsStalled { get; set; }
    }

    public class JiraFetchSimilarTickets
    {
        public List<JiraFetchMatch> JiraMatches { get; set; } = new List<JiraFetchMatch>();
        public List<JiraFetchMrMatch> GitLabMrMatches { get; set; } = new List<JiraFetchMrMatch>();
    }

    public class JiraFetchMatch
    {
        public string Key { get; set; }
        public string Summary { get; set; }
        public string Status { get; set; }
        public string Updated { get; set; }
    }

    public class JiraFetchMrMatch
    {
        public int Iid { get; set; }
        public string Title { get; set; }
        public string Branch { get; set; }
        public string Merged { get; set; }
        public string Url { get; set; }
    }

    // Raw Jira/GitLab API response models
    public class JiraIssueRaw
    {
        public string key { get; set; }
        public JiraIssueFieldsRaw fields { get; set; }
    }

    public class JiraIssueFieldsRaw
    {
        public string summary { get; set; }
        public JiraStatusRaw status { get; set; }
        public JiraPersonRaw assignee { get; set; }
        public JiraPersonRaw reporter { get; set; }
        public JiraPriorityRaw priority { get; set; }
        public string created { get; set; }
        public string updated { get; set; }
        public object description { get; set; }
        public List<JiraAttachmentRaw> attachment { get; set; }
        public List<JiraIssueLinkRaw> issuelinks { get; set; }
        public JiraTimeTrackingRaw timetracking { get; set; }
        public string duedate { get; set; }
        public List<JiraPersonRaw> customfield_11192 { get; set; }  // Programmer
        public List<JiraPersonRaw> customfield_11193 { get; set; }  // Tester
        public JiraCustomFieldOptionRaw customfield_15528 { get; set; }  // Estimated DEV Effort (hrs)
        public JiraCustomFieldOptionRaw customfield_15527 { get; set; }  // Estimated QA Effort (hrs)
        public JiraCustomFieldOptionRaw customfield_12725 { get; set; }  // Estimated Effort (Hrs) — total
        public double? customfield_11639 { get; set; }                  // Estimated Effort (hrs) — legacy total
        public string customfield_11702 { get; set; }  // Specs Received
        public string customfield_11637 { get; set; }  // Date Scheduled
        public string customfield_11690 { get; set; }  // QA start date
        public string customfield_11196 { get; set; }  // QA Delivery Date
        public string customfield_12100 { get; set; }  // DEV due date
        public string customfield_12101 { get; set; }  // QA due date
    }

    public class JiraTimeTrackingRaw
    {
        public string originalEstimate { get; set; }
        public int? originalEstimateSeconds { get; set; }
        public string remainingEstimate { get; set; }
        public int? remainingEstimateSeconds { get; set; }
        public string timeSpent { get; set; }
        public int? timeSpentSeconds { get; set; }
    }

    public class JiraCustomFieldOptionRaw
    {
        public string value { get; set; }
    }

    public class JiraStatusRaw
    {
        public string name { get; set; }
        public JiraStatusCategoryRaw statusCategory { get; set; }
    }
    public class JiraStatusCategoryRaw { public string key { get; set; } }  // "new" | "indeterminate" | "done"
    public class JiraPersonRaw   { public string displayName { get; set; } }
    public class JiraPriorityRaw { public string name { get; set; } }
    public class JiraIssueTypeRaw { public string name { get; set; } }

    public class JiraIssueLinkRaw
    {
        public JiraLinkedIssueRaw inwardIssue { get; set; }
        public JiraLinkedIssueRaw outwardIssue { get; set; }
    }

    public class JiraLinkedIssueRaw
    {
        public string key { get; set; }
        public JiraLinkedIssueFieldsRaw fields { get; set; }
    }

    public class JiraLinkedIssueFieldsRaw
    {
        public string summary { get; set; }
        public JiraStatusRaw status { get; set; }
        public JiraIssueTypeRaw issuetype { get; set; }
    }

    public class JiraAttachmentRaw
    {
        public string filename { get; set; }
        public string content { get; set; }
    }

    public class JiraCommentPageRaw
    {
        public List<JiraCommentRaw> comments { get; set; }
        public int total { get; set; }
    }

    public class JiraCommentRaw
    {
        public JiraPersonRaw author { get; set; }
        public string created { get; set; }
        public object body { get; set; }
    }

    public class JiraWorklogPageRaw
    {
        public List<JiraWorklogRaw> worklogs { get; set; }
        public int total { get; set; }
    }

    public class JiraWorklogRaw
    {
        public JiraPersonRaw author { get; set; }
        public string started { get; set; }
        public string timeSpent { get; set; }
        public int timeSpentSeconds { get; set; }
        public object comment { get; set; }
    }

    public class JiraSearchRaw
    {
        public List<JiraIssueRaw> issues { get; set; }
    }

    public class GitLabMrRaw
    {
        public int iid { get; set; }
        public string title { get; set; }
        public string source_branch { get; set; }
        public string merged_at { get; set; }
        public string web_url { get; set; }
    }

    public class AdfNode
    {
        public string type { get; set; }
        public string text { get; set; }
        public List<AdfNode> content { get; set; }
    }
}
