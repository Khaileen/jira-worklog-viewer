using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using JiraWorklogViewer.Models;
using Newtonsoft.Json;

namespace JiraWorklogViewer.Services
{
    public class JiraService
    {
        private readonly HttpClient _httpClient;
        private string _baseUrl;
        private string _currentUserAccountId;

        public JiraService()
        {
            _httpClient = new HttpClient();
        }

        public void SetCredentials(JiraCredentials credentials)
        {
            _baseUrl = credentials.ServerUrl.TrimEnd('/');

            var authString = string.Format("{0}:{1}", credentials.Email, credentials.ApiToken);
            var authBytes = Encoding.UTF8.GetBytes(authString);
            var authBase64 = Convert.ToBase64String(authBytes);

            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", authBase64);
            _httpClient.DefaultRequestHeaders.Accept.Clear();
            _httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public async Task<bool> TestConnectionAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync(_baseUrl + "/rest/api/3/myself");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var myself = JsonConvert.DeserializeObject<JiraMyselfResponse>(json);
                    _currentUserAccountId = myself.accountId;
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        public async Task<List<JiraAssignedIssue>> GetAssignedIssuesAsync()
        {
            var issues = new List<JiraAssignedIssue>();

            string jql = "assignee = currentUser() AND status != Done ORDER BY updated DESC";
            var searchUrl = string.Format(
                "{0}/rest/api/3/search/jql?jql={1}&fields=summary&maxResults=50",
                _baseUrl,
                Uri.EscapeDataString(jql));

            var response = await _httpClient.GetAsync(searchUrl);

            if (!response.IsSuccessStatusCode)
            {
                return issues;
            }

            var json = await response.Content.ReadAsStringAsync();
            var searchResult = JsonConvert.DeserializeObject<JiraSearchResponse>(json);

            if (searchResult.issues != null)
            {
                foreach (var issue in searchResult.issues)
                {
                    issues.Add(new JiraAssignedIssue
                    {
                        Key = issue.key,
                        Summary = issue.fields != null ? issue.fields.summary : ""
                    });
                }
            }

            return issues;
        }

        public async Task<List<WorklogGroup>> GetMyWorklogsAsync(DateTime? fromDate, DateTime? toDate, string ticketKey = null)
        {
            var worklogs = new List<WorklogEntry>();

            // Build JQL based on filters
            var jqlParts = new List<string>();
            jqlParts.Add("worklogAuthor = currentUser()");

            if (!string.IsNullOrWhiteSpace(ticketKey))
            {
                // Exact match on ticket key
                jqlParts.Add(string.Format("key = \"{0}\"", ticketKey.Trim()));
            }

            if (fromDate.HasValue && toDate.HasValue)
            {
                jqlParts.Add(string.Format("worklogDate >= \"{0}\"", fromDate.Value.ToString("yyyy-MM-dd")));
                jqlParts.Add(string.Format("worklogDate <= \"{0}\"", toDate.Value.ToString("yyyy-MM-dd")));
            }

            string jql = string.Join(" AND ", jqlParts) + " ORDER BY updated DESC";

            var searchUrl = string.Format(
                "{0}/rest/api/3/search/jql?jql={1}&fields=summary,worklog&maxResults=100",
                _baseUrl,
                Uri.EscapeDataString(jql));

            var response = await _httpClient.GetAsync(searchUrl);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new Exception("Failed to search issues: " + errorContent);
            }

            var json = await response.Content.ReadAsStringAsync();
            var searchResult = JsonConvert.DeserializeObject<JiraSearchResponse>(json);

            if (searchResult.issues == null)
            {
                return GroupWorklogsByDate(worklogs);
            }

            foreach (var issue in searchResult.issues)
            {
                // If worklog total is more than returned, fetch all worklogs separately
                if (issue.fields.worklog != null && issue.fields.worklog.total > 20)
                {
                    var issueWorklogs = await GetWorklogsForIssueAsync(issue.key);
                    foreach (var wl in issueWorklogs)
                    {
                        wl.IssueKey = issue.key;
                        wl.IssueSummary = issue.fields.summary;
                    }

                    // Apply date filter if specified
                    if (fromDate.HasValue && toDate.HasValue)
                    {
                        worklogs.AddRange(issueWorklogs.Where(w =>
                            w.Started.Date >= fromDate.Value.Date && w.Started.Date <= toDate.Value.Date));
                    }
                    else
                    {
                        worklogs.AddRange(issueWorklogs);
                    }
                }
                else if (issue.fields.worklog != null && issue.fields.worklog.worklogs != null)
                {
                    foreach (var wl in issue.fields.worklog.worklogs)
                    {
                        // Filter by current user
                        if (wl.author.accountId == _currentUserAccountId)
                        {
                            var entry = ConvertToWorklogEntry(wl, issue.key, issue.fields.summary);

                            // Apply date filter if specified
                            if (fromDate.HasValue && toDate.HasValue)
                            {
                                if (entry.Started.Date >= fromDate.Value.Date && entry.Started.Date <= toDate.Value.Date)
                                {
                                    worklogs.Add(entry);
                                }
                            }
                            else
                            {
                                worklogs.Add(entry);
                            }
                        }
                    }
                }
            }

            return GroupWorklogsByDate(worklogs);
        }

        private async Task<List<WorklogEntry>> GetWorklogsForIssueAsync(string issueKey)
        {
            var entries = new List<WorklogEntry>();

            var url = string.Format("{0}/rest/api/3/issue/{1}/worklog", _baseUrl, issueKey);
            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                return entries;
            }

            var json = await response.Content.ReadAsStringAsync();
            var worklogContainer = JsonConvert.DeserializeObject<JiraWorklogContainer>(json);

            if (worklogContainer.worklogs != null)
            {
                foreach (var wl in worklogContainer.worklogs)
                {
                    if (wl.author.accountId == _currentUserAccountId)
                    {
                        entries.Add(ConvertToWorklogEntry(wl, null, null));
                    }
                }
            }

            return entries;
        }

        private WorklogEntry ConvertToWorklogEntry(JiraWorklog jiraWorklog, string issueKey, string issueSummary)
        {
            return new WorklogEntry
            {
                Id = jiraWorklog.id,
                IssueKey = issueKey,
                IssueSummary = issueSummary,
                Started = DateTime.Parse(jiraWorklog.started).ToLocalTime(),
                TimeSpentSeconds = jiraWorklog.timeSpentSeconds,
                TimeSpent = jiraWorklog.timeSpent,
                Comment = ExtractCommentText(jiraWorklog.comment),
                AuthorDisplayName = jiraWorklog.author.displayName
            };
        }

        private string ExtractCommentText(JiraComment comment)
        {
            if (comment == null || comment.content == null)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            ExtractTextFromContent(comment.content, sb, 0);
            return sb.ToString().Trim();
        }

        private void ExtractTextFromContent(List<JiraContentBlock> content, StringBuilder sb, int indentLevel)
        {
            if (content == null) return;

            foreach (var block in content)
            {
                switch (block.type)
                {
                    case "paragraph":
                        if (block.content != null)
                        {
                            ExtractTextFromContent(block.content, sb, indentLevel);
                        }
                        sb.AppendLine();
                        break;

                    case "bulletList":
                        if (block.content != null)
                        {
                            ExtractTextFromContent(block.content, sb, indentLevel);
                        }
                        break;

                    case "orderedList":
                        if (block.content != null)
                        {
                            int itemNumber = 1;
                            foreach (var item in block.content)
                            {
                                string indent = new string(' ', indentLevel * 2);
                                sb.Append(indent + itemNumber + ". ");
                                if (item.content != null)
                                {
                                    ExtractListItemContent(item.content, sb, indentLevel + 1);
                                }
                                itemNumber++;
                            }
                        }
                        break;

                    case "listItem":
                        string bulletIndent = new string(' ', indentLevel * 2);
                        sb.Append(bulletIndent + "- ");
                        if (block.content != null)
                        {
                            ExtractListItemContent(block.content, sb, indentLevel + 1);
                        }
                        break;

                    case "text":
                        if (!string.IsNullOrEmpty(block.text))
                        {
                            sb.Append(block.text);
                        }
                        break;

                    case "hardBreak":
                        sb.AppendLine();
                        break;

                    default:
                        // Handle nested content for unknown types
                        if (block.content != null)
                        {
                            ExtractTextFromContent(block.content, sb, indentLevel);
                        }
                        if (!string.IsNullOrEmpty(block.text))
                        {
                            sb.Append(block.text);
                        }
                        break;
                }
            }
        }

        private void ExtractListItemContent(List<JiraContentBlock> content, StringBuilder sb, int indentLevel)
        {
            if (content == null) return;

            bool firstParagraph = true;
            foreach (var block in content)
            {
                if (block.type == "paragraph")
                {
                    if (block.content != null)
                    {
                        foreach (var innerBlock in block.content)
                        {
                            if (!string.IsNullOrEmpty(innerBlock.text))
                            {
                                sb.Append(innerBlock.text);
                            }
                        }
                    }
                    if (firstParagraph)
                    {
                        sb.AppendLine();
                        firstParagraph = false;
                    }
                }
                else if (block.type == "bulletList" || block.type == "orderedList")
                {
                    // Nested list
                    ExtractTextFromContent(new List<JiraContentBlock> { block }, sb, indentLevel);
                }
            }
        }

        private List<WorklogGroup> GroupWorklogsByDate(List<WorklogEntry> worklogs)
        {
            return worklogs
                .GroupBy(w => w.Started.Date)
                .OrderByDescending(g => g.Key)
                .Select(g => new WorklogGroup
                {
                    Date = g.Key,
                    Worklogs = g.OrderBy(w => w.Started).ToList()
                })
                .ToList();
        }

        public async Task<WorklogEntry> CreateWorklogAsync(string issueKey, DateTime started, string timeSpent, string comment)
        {
            var url = string.Format("{0}/rest/api/3/issue/{1}/worklog", _baseUrl, issueKey);

            // Build the request body
            object commentAdf = null;
            if (!string.IsNullOrEmpty(comment))
            {
                commentAdf = ConvertToAdfSafe(comment);
            }

            var worklogData = new Dictionary<string, object>
            {
                { "started", started.ToString("yyyy-MM-ddTHH:mm:ss.fffzz00") },
                { "timeSpent", timeSpent }
            };

            if (commentAdf != null)
            {
                worklogData["comment"] = commentAdf;
            }

            var jsonContent = JsonConvert.SerializeObject(worklogData, 
                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(url, content);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                var friendlyMessage = ParseJiraError(errorContent, response.StatusCode);
                throw new Exception(friendlyMessage);
            }

            var responseJson = await response.Content.ReadAsStringAsync();
            var createdWorklog = JsonConvert.DeserializeObject<JiraWorklog>(responseJson);

            // Get the issue summary for display
            string issueSummary = await GetIssueSummaryAsync(issueKey);

            return ConvertToWorklogEntry(createdWorklog, issueKey, issueSummary);
        }

        public async Task<WorklogEntry> UpdateWorklogAsync(string issueKey, string worklogId, DateTime started, string timeSpent, string comment)
        {
            var url = string.Format("{0}/rest/api/3/issue/{1}/worklog/{2}", _baseUrl, issueKey, worklogId);

            // Build the request body
            object commentAdf = null;
            if (!string.IsNullOrEmpty(comment))
            {
                commentAdf = ConvertToAdfSafe(comment);
            }

            var worklogData = new Dictionary<string, object>
            {
                { "started", started.ToString("yyyy-MM-ddTHH:mm:ss.fffzz00") },
                { "timeSpent", timeSpent }
            };

            if (commentAdf != null)
            {
                worklogData["comment"] = commentAdf;
            }

            var jsonContent = JsonConvert.SerializeObject(worklogData,
                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Put, url);
            request.Content = content;

            var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                var friendlyMessage = ParseJiraError(errorContent, response.StatusCode);
                throw new Exception(friendlyMessage);
            }

            var responseJson = await response.Content.ReadAsStringAsync();
            var updatedWorklog = JsonConvert.DeserializeObject<JiraWorklog>(responseJson);

            // Get the issue summary for display
            string issueSummary = await GetIssueSummaryAsync(issueKey);

            return ConvertToWorklogEntry(updatedWorklog, issueKey, issueSummary);
        }

        public async Task DeleteWorklogAsync(string issueKey, string worklogId)
        {
            var url = string.Format("{0}/rest/api/3/issue/{1}/worklog/{2}", _baseUrl, issueKey, worklogId);

            var request = new HttpRequestMessage(HttpMethod.Delete, url);
            var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                var friendlyMessage = ParseJiraError(errorContent, response.StatusCode);
                throw new Exception(friendlyMessage);
            }
        }

        private const int MaxCommentLength = 5000;
        private const int MaxCommentLines = 100;

        private object ConvertToAdfSafe(string text)
        {
            // For very long comments or too many lines, use simple plain text to avoid ADF complexity issues
            var lines = text.Replace("\r\n", "\n").Split('\n');
            
            if (text.Length > MaxCommentLength || lines.Length > MaxCommentLines)
            {
                return ConvertToPlainTextAdf(text);
            }

            try
            {
                return ConvertToAdf(text);
            }
            catch
            {
                // Fallback to plain text if ADF conversion fails
                return ConvertToPlainTextAdf(text);
            }
        }

        private object ConvertToPlainTextAdf(string text)
        {
            // Split into paragraphs and create simple ADF structure
            var paragraphs = text.Replace("\r\n", "\n").Split(new[] { "\n\n" }, StringSplitOptions.None);
            var content = new List<object>();

            foreach (var para in paragraphs)
            {
                if (string.IsNullOrWhiteSpace(para)) continue;

                // Replace single newlines with hardBreak for line preservation
                var lines = para.Split('\n');
                var paraContent = new List<object>();

                for (int i = 0; i < lines.Length; i++)
                {
                    if (!string.IsNullOrEmpty(lines[i]))
                    {
                        paraContent.Add(new Dictionary<string, object>
                        {
                            { "type", "text" },
                            { "text", lines[i] }
                        });
                    }

                    if (i < lines.Length - 1)
                    {
                        paraContent.Add(new Dictionary<string, object>
                        {
                            { "type", "hardBreak" }
                        });
                    }
                }

                if (paraContent.Count > 0)
                {
                    content.Add(new Dictionary<string, object>
                    {
                        { "type", "paragraph" },
                        { "content", paraContent }
                    });
                }
            }

            if (content.Count == 0)
            {
                content.Add(new Dictionary<string, object>
                {
                    { "type", "paragraph" },
                    { "content", new List<object>() }
                });
            }

            return new Dictionary<string, object>
            {
                { "type", "doc" },
                { "version", 1 },
                { "content", content }
            };
        }

        private object ConvertToAdf(string text)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var content = new List<object>();
            var listStack = new List<ListContext>();

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    // Empty line - close all lists and add empty paragraph
                    CloseAllLists(listStack, content);
                    continue;
                }

                // Count leading spaces/tabs for indentation
                int indent = 0;
                int charIndex = 0;
                while (charIndex < line.Length && (line[charIndex] == ' ' || line[charIndex] == '\t'))
                {
                    indent += line[charIndex] == '\t' ? 2 : 1;
                    charIndex++;
                }
                int indentLevel = indent / 2; // Every 2 spaces = 1 level

                string trimmedLine = line.Substring(charIndex);

                // Check for bullet list (- or *)
                bool isBullet = false;
                bool isOrdered = false;
                string itemText = trimmedLine;

                if (trimmedLine.StartsWith("- ") || trimmedLine.StartsWith("* "))
                {
                    isBullet = true;
                    itemText = trimmedLine.Substring(2);
                }
                else
                {
                    // Check for numbered list (1. 2. etc)
                    int dotIndex = trimmedLine.IndexOf(". ");
                    if (dotIndex > 0 && dotIndex <= 3)
                    {
                        string numPart = trimmedLine.Substring(0, dotIndex);
                        int num;
                        if (int.TryParse(numPart, out num))
                        {
                            isOrdered = true;
                            itemText = trimmedLine.Substring(dotIndex + 2);
                        }
                    }
                }

                if (isBullet || isOrdered)
                {
                    string listType = isBullet ? "bulletList" : "orderedList";

                    // Close deeper lists
                    while (listStack.Count > indentLevel + 1)
                    {
                        listStack.RemoveAt(listStack.Count - 1);
                    }

                    // If at same level but different list type, close and start new
                    if (listStack.Count == indentLevel + 1 && listStack[indentLevel].Type != listType)
                    {
                        listStack.RemoveAt(listStack.Count - 1);
                    }

                    // Create new list at this level if needed
                    if (listStack.Count <= indentLevel)
                    {
                        // Need to create lists up to this level
                        while (listStack.Count <= indentLevel)
                        {
                            var newList = new Dictionary<string, object>
                            {
                                { "type", listType },
                                { "content", new List<object>() }
                            };

                            if (listStack.Count == 0)
                            {
                                content.Add(newList);
                            }
                            else
                            {
                                // Add to parent list item
                                var parentItems = (List<object>)listStack[listStack.Count - 1].List["content"];
                                if (parentItems.Count > 0)
                                {
                                    var lastItem = (Dictionary<string, object>)parentItems[parentItems.Count - 1];
                                    var lastItemContent = (List<object>)lastItem["content"];
                                    lastItemContent.Add(newList);
                                }
                            }

                            listStack.Add(new ListContext { Type = listType, List = newList });
                        }
                    }

                    // Add list item
                    var listItem = new Dictionary<string, object>
                    {
                        { "type", "listItem" },
                        { "content", new List<object>
                            {
                                new Dictionary<string, object>
                                {
                                    { "type", "paragraph" },
                                    { "content", new List<object>
                                        {
                                            new Dictionary<string, object>
                                            {
                                                { "type", "text" },
                                                { "text", itemText }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    };

                    var currentList = (List<object>)listStack[indentLevel].List["content"];
                    currentList.Add(listItem);
                }
                else
                {
                    // Regular paragraph - close all lists first
                    CloseAllLists(listStack, content);

                    content.Add(new Dictionary<string, object>
                    {
                        { "type", "paragraph" },
                        { "content", new List<object>
                            {
                                new Dictionary<string, object>
                                {
                                    { "type", "text" },
                                    { "text", trimmedLine }
                                }
                            }
                        }
                    });
                }
            }

            CloseAllLists(listStack, content);

            // If no content, add empty paragraph
            if (content.Count == 0)
            {
                content.Add(new Dictionary<string, object>
                {
                    { "type", "paragraph" },
                    { "content", new List<object>() }
                });
            }

            return new Dictionary<string, object>
            {
                { "type", "doc" },
                { "version", 1 },
                { "content", content }
            };
        }

        private void CloseAllLists(List<ListContext> listStack, List<object> content)
        {
            listStack.Clear();
        }

        private class ListContext
        {
            public string Type { get; set; }
            public Dictionary<string, object> List { get; set; }
        }

        private string ParseJiraError(string errorJson, System.Net.HttpStatusCode statusCode)
        {
            try
            {
                var errorResponse = JsonConvert.DeserializeObject<JiraErrorResponse>(errorJson);
                
                var messages = new List<string>();
                
                if (errorResponse.errorMessages != null && errorResponse.errorMessages.Count > 0)
                {
                    messages.AddRange(errorResponse.errorMessages);
                }
                
                if (errorResponse.errors != null && errorResponse.errors.Count > 0)
                {
                    foreach (var kvp in errorResponse.errors)
                    {
                        messages.Add(string.Format("{0}: {1}", kvp.Key, kvp.Value));
                    }
                }

                if (messages.Count > 0)
                {
                    return string.Join("\n", messages);
                }
            }
            catch
            {
                // JSON parsing failed, fall through to default
            }

            // Fallback for non-JSON or unexpected format
            if (statusCode == System.Net.HttpStatusCode.NotFound)
            {
                return "Issue not found or you don't have permission to access it.";
            }
            else if (statusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return "Authentication failed. Please check your credentials.";
            }
            else if (statusCode == System.Net.HttpStatusCode.Forbidden)
            {
                return "You don't have permission to log work on this issue.";
            }

            return string.Format("Request failed ({0})", (int)statusCode);
        }

        private async Task<string> GetIssueSummaryAsync(string issueKey)
        {
            try
            {
                var url = string.Format("{0}/rest/api/3/issue/{1}?fields=summary", _baseUrl, issueKey);
                var response = await _httpClient.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var issue = JsonConvert.DeserializeObject<JiraIssue>(json);
                    return issue.fields != null ? issue.fields.summary : string.Empty;
                }
            }
            catch
            {
                // Ignore errors, just return empty
            }
            return string.Empty;
        }

        /// <summary>
        /// Fetches full ticket details — summary, status, assignee, priority, reporter.
        /// </summary>
        public async Task<TicketDetails> GetTicketDetailsAsync(string issueKey)
        {
            var url = string.Format(
                "{0}/rest/api/3/issue/{1}?fields=summary,status,assignee,priority,reporter,created,updated",
                _baseUrl, issueKey);

            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                throw new Exception(string.Format("Failed to fetch ticket {0}: {1}", issueKey, error));
            }

            var json = await response.Content.ReadAsStringAsync();
            var issue = JsonConvert.DeserializeObject<JiraIssueExtended>(json);
            var f = issue.fields;

            return new TicketDetails
            {
                Key      = issue.key,
                Summary  = f?.summary ?? string.Empty,
                Status   = f?.status?.name ?? "Unknown",
                Assignee = f?.assignee?.displayName ?? "Unassigned",
                Priority = f?.priority?.name ?? "None",
                Reporter = f?.reporter?.displayName ?? "Unknown",
                Created  = TryParseDate(f?.created),
                Updated  = TryParseDate(f?.updated),
            };
        }

        /// <summary>
        /// Fetches all comments for a ticket, paginated.
        /// </summary>
        public async Task<List<TicketComment>> GetTicketCommentsAsync(string issueKey)
        {
            var comments = new List<TicketComment>();
            int startAt = 0;
            const int maxResults = 100;

            while (true)
            {
                var url = string.Format(
                    "{0}/rest/api/3/issue/{1}/comment?startAt={2}&maxResults={3}&orderBy=created",
                    _baseUrl, issueKey, startAt, maxResults);

                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode) break;

                var json = await response.Content.ReadAsStringAsync();
                var container = JsonConvert.DeserializeObject<JiraCommentContainer>(json);

                if (container?.comments == null || container.comments.Count == 0) break;

                foreach (var c in container.comments)
                {
                    comments.Add(new TicketComment
                    {
                        Author  = c.author?.displayName ?? "Unknown",
                        Created = TryParseDate(c.created) ?? DateTime.MinValue,
                        Body    = ExtractCommentText(c.body),
                    });
                }

                startAt += container.comments.Count;
                if (startAt >= container.total) break;
            }

            return comments;
        }

        /// <summary>
        /// Fetches all worklogs for a specific ticket regardless of author.
        /// Used for the time defense feature.
        /// </summary>
        public async Task<List<WorklogEntry>> GetAllWorklogsForTicketAsync(string issueKey)
        {
            var entries = new List<WorklogEntry>();
            var issueSummary = await GetIssueSummaryAsync(issueKey);

            var url = string.Format("{0}/rest/api/3/issue/{1}/worklog", _baseUrl, issueKey);
            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode) return entries;

            var json = await response.Content.ReadAsStringAsync();
            var container = JsonConvert.DeserializeObject<JiraWorklogContainer>(json);

            if (container?.worklogs != null)
            {
                foreach (var wl in container.worklogs)
                {
                    // Include all authors — author name is preserved on WorklogEntry
                    var entry = ConvertToWorklogEntry(wl, issueKey, issueSummary);
                    entries.Add(entry);
                }
            }

            return entries.OrderBy(e => e.Started).ToList();
        }

        /// <summary>
        /// Fetches all status change events from the Jira changelog for a ticket.
        /// </summary>
        public async Task<List<TicketStatusChange>> GetTicketChangelogAsync(string issueKey)
        {
            var changes = new List<TicketStatusChange>();
            int startAt = 0;

            while (true)
            {
                var url = string.Format(
                    "{0}/rest/api/3/issue/{1}/changelog?startAt={2}&maxResults=100",
                    _baseUrl, issueKey, startAt);

                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode) break;

                var json = await response.Content.ReadAsStringAsync();
                var changelog = JsonConvert.DeserializeObject<JiraChangelogResponse>(json);

                if (changelog?.values == null || changelog.values.Count == 0) break;

                foreach (var entry in changelog.values)
                {
                    if (entry.items == null) continue;
                    foreach (var item in entry.items)
                    {
                        if (item.field?.ToLower() != "status") continue;
                        changes.Add(new TicketStatusChange
                        {
                            Created    = TryParseDate(entry.created) ?? DateTime.MinValue,
                            Author     = entry.author?.displayName ?? "Unknown",
                            FromStatus = item.fromString ?? string.Empty,
                            ToStatus   = item.toString   ?? string.Empty,
                        });
                    }
                }

                startAt += changelog.values.Count;
                if (changelog.isLast || startAt >= changelog.values.Count) break;
            }

            return changes.OrderBy(c => c.Created).ToList();
        }

        private DateTime? TryParseDate(string dateStr)
        {
            if (string.IsNullOrEmpty(dateStr)) return null;
            if (DateTime.TryParse(dateStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                return dt.ToLocalTime();
            return null;
        }
    }
}
