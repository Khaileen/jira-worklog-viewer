using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JiraWorklogViewer.Models;
using JiraWorklogViewer.Services;

namespace JiraWorklogViewer
{
    public partial class UserSearchDialog : Window
    {
        private readonly JiraService _jiraService;
        private readonly HashSet<string> _alreadyInRoster;

        public List<JiraUserInfo> SelectedUsers { get; private set; } = new List<JiraUserInfo>();

        public UserSearchDialog(JiraService jiraService, IEnumerable<string> existingAccountIds)
        {
            InitializeComponent();
            _jiraService = jiraService;
            _alreadyInRoster = new HashSet<string>(existingAccountIds);
            lstResults.SelectionChanged += (s, e) => btnAdd.IsEnabled = lstResults.SelectedItems.Count > 0;
            Loaded += (s, e) => txtQuery.Focus();
        }

        private void TxtQuery_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) BtnSearch_Click(sender, e);
        }

        private async void BtnSearch_Click(object sender, RoutedEventArgs e)
        {
            var query = txtQuery.Text.Trim();
            if (query.Length < 2)
            {
                txtStatus.Text = "Type at least 2 characters.";
                return;
            }

            btnSearch.IsEnabled = false;
            txtStatus.Text = "Searching…";
            try
            {
                var users = (await _jiraService.SearchUsersAsync(query))
                    .Where(u => !_alreadyInRoster.Contains(u.AccountId)).ToList();
                lstResults.ItemsSource = users;
                txtStatus.Text = users.Count == 0
                    ? "No new users found (users already on the roster are hidden)."
                    : users.Count + " user(s) found.";
            }
            catch (Exception ex)
            {
                txtStatus.Text = "Search failed: " + ex.Message;
            }
            finally
            {
                btnSearch.IsEnabled = true;
            }
        }

        private void LstResults_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (lstResults.SelectedItems.Count > 0) BtnAdd_Click(sender, e);
        }

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            SelectedUsers = lstResults.SelectedItems.Cast<JiraUserInfo>().ToList();
            DialogResult = true;
        }
    }
}
