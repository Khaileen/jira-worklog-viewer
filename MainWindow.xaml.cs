using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using JiraWorklogViewer.Models;
using JiraWorklogViewer.Services;

namespace JiraWorklogViewer
{
    public partial class MainWindow : Window
    {
        private readonly JiraService _jiraService;
        private readonly CredentialService _credentialService;
        private bool _isConnected;

        public MainWindow()
        {
            InitializeComponent();

            _jiraService = new JiraService();
            _credentialService = new CredentialService();

            // Set default date range (last 7 days)
            dpToDate.SelectedDate = DateTime.Today;
            dpFromDate.SelectedDate = DateTime.Today.AddDays(-7);

            // Load stored credentials
            LoadStoredCredentials();
        }

        private async void LoadStoredCredentials()
        {
            var credentials = _credentialService.LoadCredentials();
            if (credentials != null)
            {
                txtServerUrl.Text = credentials.ServerUrl;
                txtEmail.Text = credentials.Email;
                txtApiToken.Password = credentials.ApiToken;

                // Auto-connect
                await ConnectAsync(credentials);
            }
        }

        private async void BtnSaveCredentials_Click(object sender, RoutedEventArgs e)
        {
            var credentials = new JiraCredentials
            {
                ServerUrl = txtServerUrl.Text.Trim(),
                Email = txtEmail.Text.Trim(),
                ApiToken = txtApiToken.Password
            };

            if (string.IsNullOrEmpty(credentials.ServerUrl) ||
                string.IsNullOrEmpty(credentials.Email) ||
                string.IsNullOrEmpty(credentials.ApiToken))
            {
                MessageBox.Show("Please fill in all credential fields.",
                    "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _credentialService.SaveCredentials(credentials);
            await ConnectAsync(credentials);
        }

        private async Task ConnectAsync(JiraCredentials credentials)
        {
            SetStatus("Connecting to Jira...");
            SetUIEnabled(false);

            try
            {
                _jiraService.SetCredentials(credentials);
                var success = await _jiraService.TestConnectionAsync();

                if (success)
                {
                    _isConnected = true;
                    SetStatus("Connected successfully!");
                    MessageBox.Show("Connected to Jira successfully!",
                        "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    _isConnected = false;
                    SetStatus("Connection failed.");
                    MessageBox.Show("Failed to connect to Jira. Please check your credentials.",
                        "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                _isConnected = false;
                SetStatus("Connection error.");
                MessageBox.Show("Error connecting to Jira: " + ex.Message,
                    "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetUIEnabled(true);
            }
        }

        private void BtnClearCredentials_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Are you sure you want to clear stored credentials?",
                "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _credentialService.ClearCredentials();
                txtServerUrl.Text = string.Empty;
                txtEmail.Text = string.Empty;
                txtApiToken.Password = string.Empty;
                _isConnected = false;
                ClearWorklogData();
                SetStatus("Credentials cleared.");
            }
        }

        private async void BtnFetch_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected)
            {
                MessageBox.Show("Please connect to Jira first.",
                    "Not Connected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool useDateFilter = chkUseDateFilter.IsChecked == true;
            DateTime? fromDate = null;
            DateTime? toDate = null;

            if (useDateFilter)
            {
                if (!dpFromDate.SelectedDate.HasValue || !dpToDate.SelectedDate.HasValue)
                {
                    MessageBox.Show("Please select both From and To dates, or uncheck the date filter.",
                        "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                fromDate = dpFromDate.SelectedDate.Value;
                toDate = dpToDate.SelectedDate.Value;

                if (fromDate > toDate)
                {
                    MessageBox.Show("From date must be before or equal to To date.",
                        "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            // Get ticket filter (can be empty)
            string ticketKey = txtTicketFilter.Text.Trim();
            if (string.IsNullOrEmpty(ticketKey))
            {
                ticketKey = null;
            }

            // Build status message
            string statusMsg = "Fetching worklogs";
            if (ticketKey != null)
            {
                statusMsg += string.Format(" for {0}", ticketKey);
            }
            if (!useDateFilter)
            {
                statusMsg += " (all time - this may take a while)";
            }
            statusMsg += "...";

            SetStatus(statusMsg);
            SetUIEnabled(false);

            try
            {
                var worklogGroups = await _jiraService.GetMyWorklogsAsync(fromDate, toDate, ticketKey);

                // Display worklogs
                DisplayWorklogs(worklogGroups);

                // Calculate totals
                int totalWorklogs = worklogGroups.Sum(g => g.Worklogs.Count);
                int totalSeconds = worklogGroups.Sum(g => g.Worklogs.Sum(w => w.TimeSpentSeconds));
                int uniqueTickets = worklogGroups
                    .SelectMany(g => g.Worklogs)
                    .Select(w => w.IssueKey)
                    .Distinct()
                    .Count();

                string totalTime = FormatTimeSpent(totalSeconds);

                string dateInfo = useDateFilter
                    ? string.Format("in {0} days", worklogGroups.Count)
                    : "(all time)";

                string ticketInfo = ticketKey != null
                    ? string.Format(" for {0}", ticketKey)
                    : string.Format(" across {0} tickets", uniqueTickets);

                SetStatus(string.Format("Found {0} worklogs {1}{2}. Total: {3}",
                    totalWorklogs, dateInfo, ticketInfo, totalTime));
            }
            catch (Exception ex)
            {
                SetStatus("Error fetching worklogs.");
                MessageBox.Show("Error fetching worklogs: " + ex.Message,
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetUIEnabled(true);
            }
        }

        private void ChkUseDateFilter_Changed(object sender, RoutedEventArgs e)
        {
            // Guard against event firing during initialization
            if (dpFromDate == null || dpToDate == null) return;

            bool isChecked = chkUseDateFilter.IsChecked == true;
            dpFromDate.IsEnabled = isChecked;
            dpToDate.IsEnabled = isChecked;
        }

        private void BtnClearFilter_Click(object sender, RoutedEventArgs e)
        {
            if (txtTicketFilter != null)
            {
                txtTicketFilter.Text = string.Empty;
            }
        }

        private void DisplayWorklogs(List<WorklogGroup> worklogGroups)
        {
            tvWorklogs.ItemsSource = worklogGroups;

            // Expand all groups
            tvWorklogs.UpdateLayout();
            foreach (var item in tvWorklogs.Items)
            {
                var tvi = tvWorklogs.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
                if (tvi != null)
                {
                    tvi.IsExpanded = true;
                }
            }
        }

        private void TvWorklogs_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            var worklog = e.NewValue as WorklogEntry;
            if (worklog != null)
            {
                DisplayWorklogDetails(worklog);
            }
            else
            {
                ClearDetails();
            }
        }

        private void DisplayWorklogDetails(WorklogEntry worklog)
        {
            txtDetailIssue.Text = worklog.IssueKey;
            txtDetailSummary.Text = worklog.IssueSummary;
            txtDetailDate.Text = worklog.Started.ToString("dddd, MMMM dd, yyyy 'at' hh:mm tt");
            txtDetailTimeSpent.Text = worklog.TimeSpent;
            txtDetailNotes.Text = string.IsNullOrEmpty(worklog.Comment)
                ? "(No notes)"
                : worklog.Comment;
        }

        private void ClearDetails()
        {
            txtDetailIssue.Text = string.Empty;
            txtDetailSummary.Text = string.Empty;
            txtDetailDate.Text = string.Empty;
            txtDetailTimeSpent.Text = string.Empty;
            txtDetailNotes.Text = string.Empty;
        }

        private void ClearWorklogData()
        {
            tvWorklogs.ItemsSource = null;
            txtTicketFilter.Text = string.Empty;
            ClearDetails();
        }

        private void SetStatus(string message)
        {
            txtStatus.Text = message;
        }

        private string FormatTimeSpent(int totalSeconds)
        {
            int hours = totalSeconds / 3600;
            int minutes = (totalSeconds % 3600) / 60;

            if (hours > 0 && minutes > 0)
            {
                return string.Format("{0}h {1}m", hours, minutes);
            }
            else if (hours > 0)
            {
                return string.Format("{0}h", hours);
            }
            else
            {
                return string.Format("{0}m", minutes);
            }
        }

        private void SetUIEnabled(bool enabled)
        {
            btnSaveCredentials.IsEnabled = enabled;
            btnClearCredentials.IsEnabled = enabled;
            btnFetch.IsEnabled = enabled;
            txtServerUrl.IsEnabled = enabled;
            txtEmail.IsEnabled = enabled;
            txtApiToken.IsEnabled = enabled;
            chkUseDateFilter.IsEnabled = enabled;
            dpFromDate.IsEnabled = enabled && chkUseDateFilter.IsChecked == true;
            dpToDate.IsEnabled = enabled && chkUseDateFilter.IsChecked == true;
            txtTicketFilter.IsEnabled = enabled;
            btnClearFilter.IsEnabled = enabled;
        }
    }
}
