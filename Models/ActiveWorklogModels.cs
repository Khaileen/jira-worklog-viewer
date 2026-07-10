using System;
using System.Collections.Generic;
using System.Linq;

namespace JiraWorklogViewer.Models
{
    public class ActiveWorklog
    {
        public string TicketKey { get; set; }
        public string TicketSummary { get; set; }
        public ActiveWorklogStatus Status { get; set; }
        public DateTime? CurrentSessionStart { get; set; }
        public List<WorklogSession> Sessions { get; set; } = new List<WorklogSession>();
        public string Notes { get; set; } = "";

        public int TotalSeconds
        {
            get
            {
                int total = Sessions.Sum(s => s.DurationSeconds);
                
                // Add current running session if any
                if (Status == ActiveWorklogStatus.Running && CurrentSessionStart.HasValue)
                {
                    total += (int)(DateTime.Now - CurrentSessionStart.Value).TotalSeconds;
                }
                
                return total;
            }
        }

        public string TotalTimeFormatted
        {
            get
            {
                int total = TotalSeconds;
                int hours = total / 3600;
                int minutes = (total % 3600) / 60;
                int seconds = total % 60;
                return string.Format("{0:D2}:{1:D2}:{2:D2}", hours, minutes, seconds);
            }
        }

        public string TotalTimeJiraFormat
        {
            get
            {
                int total = TotalSeconds;
                int hours = total / 3600;
                int minutes = (total % 3600) / 60;
                
                if (hours > 0 && minutes > 0)
                    return string.Format("{0}h {1}m", hours, minutes);
                else if (hours > 0)
                    return string.Format("{0}h", hours);
                else if (minutes > 0)
                    return string.Format("{0}m", minutes);
                else
                    return "1m"; // Minimum 1 minute for Jira
            }
        }

        public string DisplayText
        {
            get
            {
                return string.Format("{0} - {1}", TicketKey, TicketSummary);
            }
        }
    }

    public class WorklogSession
    {
        public DateTime Start { get; set; }
        public DateTime End { get; set; }

        public int DurationSeconds
        {
            get { return (int)(End - Start).TotalSeconds; }
        }
    }

    public enum ActiveWorklogStatus
    {
        Stopped,
        Running,
        Completed
    }

    public class ActiveWorklogData
    {
        public List<ActiveWorklog> Worklogs { get; set; } = new List<ActiveWorklog>();
    }

    public class JiraAssignedIssue
    {
        public string Key { get; set; }
        public string Summary { get; set; }
        public string Status { get; set; }

        public string DisplayText
        {
            get { return string.Format("{0} - {1}", Key, Summary); }
        }

        public override string ToString()
        {
            return DisplayText;
        }
    }
}
