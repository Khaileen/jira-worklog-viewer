using System;
using System.Collections.Generic;

namespace JiraWorklogViewer.Models
{
    public class JiraCredentials
    {
        public string ServerUrl { get; set; }
        public string Email { get; set; }
        public string ApiToken { get; set; }
    }

    public class WorklogEntry
    {
        public string Id { get; set; }
        public string IssueKey { get; set; }
        public string IssueSummary { get; set; }
        public DateTime Started { get; set; }
        public int TimeSpentSeconds { get; set; }
        public string TimeSpent { get; set; }
        public string Comment { get; set; }
        public string AuthorDisplayName { get; set; }

        public string DisplayText
        {
            get
            {
                return string.Format("{0} - {1} ({2})", 
                    IssueKey ?? "(Unknown)", 
                    IssueSummary ?? "(No summary)", 
                    TimeSpent ?? "0m");
            }
        }

        public string StartedDateOnly
        {
            get { return Started.ToString("yyyy-MM-dd"); }
        }

        public string StartedTimeOnly
        {
            get { return Started.ToString("HH:mm"); }
        }
    }

    public class WorklogGroup
    {
        public DateTime Date { get; set; }

        public string DateDisplay
        {
            get { return Date.ToString("dddd, MMMM dd, yyyy"); }
        }

        public List<WorklogEntry> Worklogs { get; set; }

        public string TotalTime
        {
            get
            {
                int totalSeconds = 0;
                foreach (var w in Worklogs)
                {
                    totalSeconds += w.TimeSpentSeconds;
                }
                int hours = totalSeconds / 3600;
                int minutes = (totalSeconds % 3600) / 60;
                return string.Format("{0}h {1}m", hours, minutes);
            }
        }

        public WorklogGroup()
        {
            Worklogs = new List<WorklogEntry>();
        }
    }

    // Jira API Response Models
    public class JiraSearchResponse
    {
        public List<JiraIssue> issues { get; set; }
        public int total { get; set; }
    }

    public class JiraIssue
    {
        public string id { get; set; }
        public string key { get; set; }
        public JiraIssueFields fields { get; set; }
    }

    public class JiraIssueFields
    {
        public string summary { get; set; }
        public JiraWorklogContainer worklog { get; set; }
    }

    public class JiraWorklogContainer
    {
        public List<JiraWorklog> worklogs { get; set; }
        public int total { get; set; }
    }

    public class JiraWorklog
    {
        public string id { get; set; }
        public JiraAuthor author { get; set; }
        public string started { get; set; }
        public int timeSpentSeconds { get; set; }
        public string timeSpent { get; set; }
        public JiraComment comment { get; set; }
    }

    public class JiraAuthor
    {
        public string accountId { get; set; }
        public string emailAddress { get; set; }
        public string displayName { get; set; }
    }

    public class JiraComment
    {
        public string type { get; set; }
        public List<JiraContentBlock> content { get; set; }
    }

    public class JiraContentBlock
    {
        public string type { get; set; }
        public string text { get; set; }
        public List<JiraContentBlock> content { get; set; }
    }

    public class JiraMyselfResponse
    {
        public string accountId { get; set; }
        public string emailAddress { get; set; }
        public string displayName { get; set; }
    }

    public class JiraErrorResponse
    {
        public List<string> errorMessages { get; set; }
        public Dictionary<string, string> errors { get; set; }
    }
}
