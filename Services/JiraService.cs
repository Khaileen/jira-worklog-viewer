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

        public async Task<List<WorklogGroup>> GetMyWorklogsAsync(DateTime fromDate, DateTime toDate)
        {
            var worklogs = new List<WorklogEntry>();

            // Search for issues where current user has logged work
            // Using worklogAuthor to find issues with our worklogs
            var jql = string.Format(
                "worklogAuthor = currentUser() AND worklogDate >= \"{0}\" AND worklogDate <= \"{1}\" ORDER BY updated DESC",
                fromDate.ToString("yyyy-MM-dd"),
                toDate.ToString("yyyy-MM-dd"));

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
                    worklogs.AddRange(issueWorklogs.Where(w => 
                        w.Started.Date >= fromDate.Date && 
                        w.Started.Date <= toDate.Date));
                }
                else if (issue.fields.worklog != null && issue.fields.worklog.worklogs != null)
                {
                    foreach (var wl in issue.fields.worklog.worklogs)
                    {
                        // Filter by current user
                        if (wl.author.accountId == _currentUserAccountId)
                        {
                            var entry = ConvertToWorklogEntry(wl, issue.key, issue.fields.summary);
                            if (entry.Started.Date >= fromDate.Date && entry.Started.Date <= toDate.Date)
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
                Started = DateTime.Parse(jiraWorklog.started),
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
                                string indent = new string(' ', indentLevel * 3);
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
                        string bulletIndent = new string(' ', indentLevel * 3);
                        string bullet = indentLevel == 0 ? "• " : "◦ ";
                        sb.Append(bulletIndent + bullet);
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
    }
}
