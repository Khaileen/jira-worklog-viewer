using System;
using System.Collections.Generic;
using System.ComponentModel;
using Newtonsoft.Json;

namespace JiraWorklogViewer.Models
{
    public class RosterUser : INotifyPropertyChanged
    {
        public string AccountId { get; set; }
        public string DisplayName { get; set; }
        public string Email { get; set; }

        private bool _isDone;

        /// <summary>True when all of this user's tickets for the selected day are AI-summarized (not persisted).</summary>
        [JsonIgnore]
        public bool IsDone
        {
            get { return _isDone; }
            set
            {
                if (_isDone == value) return;
                _isDone = value;
                if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("IsDone"));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public override string ToString() { return DisplayName; }
    }

    public class JiraUserInfo
    {
        public string AccountId { get; set; }
        public string DisplayName { get; set; }
        public string Email { get; set; }

        public string Display
        {
            get
            {
                return string.IsNullOrEmpty(Email) ? DisplayName : DisplayName + "  (" + Email + ")";
            }
        }
    }

    /// <summary>One ticket a user interacted with on a given day (worklog and/or status change).</summary>
    public class UserDayTicket
    {
        public string Key { get; set; }
        public string Summary { get; set; }
        public string Status { get; set; }
        public int TimeSpentSeconds { get; set; }
        public List<string> WorklogComments { get; set; } = new List<string>();
        public List<string> StatusChanges { get; set; } = new List<string>();

        [JsonIgnore]
        public string TimeSpent
        {
            get
            {
                if (TimeSpentSeconds <= 0) return "—";
                return string.Format("{0}h {1}m", TimeSpentSeconds / 3600, (TimeSpentSeconds % 3600) / 60);
            }
        }

        [JsonIgnore]
        public string Interaction
        {
            get
            {
                var parts = new List<string>();
                if (TimeSpentSeconds > 0) parts.Add("Worklog");
                if (StatusChanges.Count > 0) parts.Add("Status change");
                if (IsManual) parts.Add("Manual entry");
                else if (ManualTexts.Count > 0) parts.Add("Manual note");
                return string.Join(" + ", parts);
            }
        }

        // Machine-generated one-liner and the user's edited version (null = unedited).
        public string DefaultEntry { get; set; }
        public string EditedEntry { get; set; }
        public bool AiSummarized { get; set; }

        /// <summary>True when added by hand via "Add New Entry" (lives only in this window, not in Jira).</summary>
        public bool IsManual { get; set; }

        /// <summary>True when EditedEntry was produced by the AI (not typed by the user).</summary>
        public bool AiEntry { get; set; }

        /// <summary>True when any hand-typed text is among the comments (may be shorthand, misspelt, or non-English).</summary>
        public bool HasManualText { get; set; }

        /// <summary>The hand-typed texts (also present in WorklogComments); kept separately so a Jira refresh can re-attach them.</summary>
        public List<string> ManualTexts { get; set; } = new List<string>();

        /// <summary>The date each ManualTexts item was typed (parallel list; used for the "Manual Entry From ..." label).</summary>
        public List<DateTime> ManualAddedOn { get; set; } = new List<DateTime>();


        /// <summary>"yesterday" or "today" — used in the AI prompt.</summary>
        public string DayLabel { get; set; }
        [JsonIgnore]
        public string CurrentEntry { get { return EditedEntry ?? DefaultEntry; } }
    }
}
