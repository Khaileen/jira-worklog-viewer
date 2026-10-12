using System;
using System.Text.RegularExpressions;
using System.Windows;
using JiraWorklogViewer.Models;
using JiraWorklogViewer.Services;

namespace JiraWorklogViewer
{
    /// <summary>Ticket number + work log text (no time fields). Validates the ticket against Jira.</summary>
    public partial class AddEntryDialog : Window
    {
        private readonly JiraService _jiraService;

        public TicketDetails Ticket { get; private set; }
        public string Text { get; private set; }

        /// <summary>True = add to the Today grid, false = Yesterday.</summary>
        public bool ToToday { get; private set; }

        public AddEntryDialog(JiraService jiraService, bool defaultToday)
        {
            InitializeComponent();
            _jiraService = jiraService;
            rbToday.IsChecked = defaultToday;
            rbYesterday.IsChecked = !defaultToday;
            Loaded += (s, e) => txtTicketKey.Focus();
        }

        private async void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            string key = txtTicketKey.Text.Trim().ToUpperInvariant();
            string text = txtText.Text.Trim();

            if (!Regex.IsMatch(key, @"^[A-Z][A-Z0-9]+-\d+$"))
            {
                txtStatus.Text = "Enter a valid ticket number, e.g. CRM-1234.";
                return;
            }
            if (text.Length == 0)
            {
                txtStatus.Text = "Enter the work log text.";
                return;
            }

            btnAdd.IsEnabled = false;
            txtStatus.Text = string.Empty;
            try
            {
                Ticket = await _jiraService.GetTicketDetailsAsync(key);
                Text = text;
                ToToday = rbToday.IsChecked == true;
                DialogResult = true;
            }
            catch (Exception ex)
            {
                txtStatus.Text = "Could not load " + key + ": " + ex.Message;
                btnAdd.IsEnabled = true;
            }
        }
    }
}
