using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
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
                OutputDir    = outputDir,
                AttachmentDir = attachmentDir
            };
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
        // Similar tickets
        // ---------------------------------------------------------------------------
        public async Task<JiraFetchSimilarTickets> FindSimilarTicketsAsync(string ticketNumber, string summary, string description)
        {
            var stopWords = new HashSet<string> { "a","an","the","and","or","for","to","of","in","is","it","its","be","by","do","no","so","at","on","up","if","as","fix","fixing","update","change","add","misc","issue","issues","sending","sent","when","with","from","that","this","not","auto","base" };

            var summaryKw = Regex.Split(summary, @"[\s\-\/]+")
                .Select(w => Regex.Replace(w, @"[^a-zA-Z0-9]", ""))
                .Where(w => w.Length >= 2 && !stopWords.Contains(w.ToLowerInvariant()))
                .Distinct().ToList();

            var descKw = Regex.Matches(description ?? "", @"\b[A-Z][a-zA-Z]{3,}|[a-z]+[A-Z][a-zA-Z]+|[A-Z]{2,3}\b")
                .Cast<Match>().Select(m => Regex.Replace(m.Value, @"[^a-zA-Z0-9]", ""))
                .Where(w => w.Length >= 3 && !stopWords.Contains(w.ToLowerInvariant()))
                .Distinct().ToList();

            var keywords = descKw.Concat(summaryKw).Distinct().Take(7).ToList();
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
            sb.AppendLine("3. Attachments (supporting evidence only — do NOT base your analysis primarily on attachment content)");
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
        public string OutputDir { get; set; }
        public string AttachmentDir { get; set; }
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
        public List<object> issuelinks { get; set; }
    }

    public class JiraStatusRaw   { public string name { get; set; } }
    public class JiraPersonRaw   { public string displayName { get; set; } }
    public class JiraPriorityRaw { public string name { get; set; } }

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
