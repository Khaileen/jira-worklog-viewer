using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Interop;
using JiraWorklogViewer.Models;
using JiraWorklogViewer.Services;

namespace JiraWorklogViewer
{
    public partial class ActiveWorklogWindow : Window
    {
        private readonly JiraService _jiraService;
        private readonly ActiveWorklogService _activeWorklogService;
        private readonly DispatcherTimer _timer;
        private ActiveWorklog _currentWorklog;
        private bool _isUpdatingNotes;
        private List<JiraAssignedIssue> _allIssues = new List<JiraAssignedIssue>();
        private Action _onWorklogCompleted;

        // Focus/blur sizing
        private const double CompactWidth = 280;
        private const double CompactHeight = 200;

        public ActiveWorklogWindow(JiraService jiraService, Action onWorklogCompleted = null)
        {
            InitializeComponent();

            _jiraService = jiraService;
            _activeWorklogService = new ActiveWorklogService();
            _onWorklogCompleted = onWorklogCompleted;

            // Setup timer for UI updates
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += Timer_Tick;

            Loaded += ActiveWorklogWindow_Loaded;
            Closing += ActiveWorklogWindow_Closing;
            KeyDown += ActiveWorklogWindow_KeyDown;

            // Start in upper-left corner of primary screen
            var screen = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
            Left = screen.Left + 10;
            Top = screen.Top + 10;

            // Start expanded since it will have focus on first show
            Width = screen.Width * 0.25 * App.Settings.UiScale;
            Height = screen.Height * 0.50 * App.Settings.UiScale;
            MinWidth = 250 * App.Settings.UiScale;
            MinHeight = 150 * App.Settings.UiScale;
        }

        private void Window_Activated(object sender, EventArgs e)
        {
            // Get the screen this window is on
            var screen = GetCurrentScreen();

            // Expand to 25% width, 50% height of monitor, scaled up so scaled content isn't clipped
            Width = screen.WorkingArea.Width * 0.25 * App.Settings.UiScale;
            Height = screen.WorkingArea.Height * 0.50 * App.Settings.UiScale;

            // Show all controls
            SetExpandedMode(true);
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            // Shrink to compact size, scaled up so scaled content isn't clipped.
            // Left/Top are intentionally left untouched — the window stays wherever the user put it.
            Width = CompactWidth * App.Settings.UiScale;
            Height = CompactHeight * App.Settings.UiScale;

            // Hide most controls, show only timer and buttons
            SetExpandedMode(false);
        }

        private void SetExpandedMode(bool expanded)
        {
            var visibility = expanded ? Visibility.Visible : Visibility.Collapsed;

            chkAlwaysOnTop.Visibility = visibility;
            grpStartNew.Visibility = visibility;
            grpNotes.Visibility = visibility;
            grpPausedWorklogs.Visibility = visibility;
            txtStatus.Visibility = visibility;

            // In compact mode, hide ticket name and complete button too
            txtCurrentTicket.Visibility = visibility;
            btnComplete.Visibility = visibility;

            // Remove the GroupBox header border in compact mode
            if (expanded)
            {
                grpCurrentlyRunning.Header = "Currently Running";
            }
            else
            {
                grpCurrentlyRunning.Header = null;
            }
        }

        private System.Windows.Forms.Screen GetCurrentScreen()
        {
            var windowInteropHelper = new WindowInteropHelper(this);
            var screen = System.Windows.Forms.Screen.FromHandle(windowInteropHelper.Handle);
            return screen;
        }

        private void ActiveWorklogWindow_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // Ctrl++ or Ctrl+= to add 1 minute (for testing)
            if (System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control &&
                (e.Key == System.Windows.Input.Key.OemPlus || e.Key == System.Windows.Input.Key.Add))
            {
                if (_currentWorklog != null && _currentWorklog.Status == ActiveWorklogStatus.Running)
                {
                    _activeWorklogService.AddTime(_currentWorklog.TicketKey, 60); // Add 1 minute
                    _currentWorklog = _activeWorklogService.Get(_currentWorklog.TicketKey); // Reload
                    txtTimer.Text = _currentWorklog.TotalTimeFormatted;
                    txtStatus.Text = "+1 minute added (testing)";
                    e.Handled = true;
                }
            }
        }

        private async void ActiveWorklogWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Load assigned tickets
            txtStatus.Text = "Loading assigned tickets...";
            try
            {
                _allIssues = await _jiraService.GetAssignedIssuesAsync();
                cboTickets.ItemsSource = _allIssues;
                if (_allIssues.Count > 0)
                {
                    cboTickets.SelectedIndex = 0;
                }
                txtStatus.Text = string.Format("Loaded {0} assigned tickets", _allIssues.Count);
            }
            catch (Exception ex)
            {
                txtStatus.Text = "Failed to load tickets: " + ex.Message;
            }

            // Restore state
            RefreshUI();

            // Start timer if there's a running worklog
            if (_currentWorklog != null && _currentWorklog.Status == ActiveWorklogStatus.Running)
            {
                _timer.Start();
            }
        }

        private void ActiveWorklogWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Pause any running worklog on close
            _activeWorklogService.PauseAll();
            _timer.Stop();
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (_currentWorklog != null)
            {
                txtTimer.Text = _currentWorklog.TotalTimeFormatted;
            }
        }

        private void RefreshUI()
        {
            var all = _activeWorklogService.GetAll();
            var running = _activeWorklogService.GetRunning();

            // _currentWorklog is ONLY the running one (or null if none running)
            _currentWorklog = running;

            // Update current worklog display
            if (_currentWorklog != null)
            {
                txtCurrentTicket.Text = _currentWorklog.DisplayText;
                txtTimer.Text = _currentWorklog.TotalTimeFormatted;

                _isUpdatingNotes = true;
                txtNotes.Text = _currentWorklog.Notes;
                _isUpdatingNotes = false;

                btnPause.Visibility = Visibility.Visible;
                btnPause.IsEnabled = true;
                btnResume.Visibility = Visibility.Collapsed;
                btnComplete.IsEnabled = true;

                txtTimer.Foreground = System.Windows.Media.Brushes.Green;

                if (!_timer.IsEnabled)
                {
                    _timer.Start();
                }
            }
            else
            {
                txtCurrentTicket.Text = "(none)";
                txtTimer.Text = "00:00:00";
                txtTimer.Foreground = System.Windows.Media.Brushes.Gray;
                txtNotes.Text = "";
                btnPause.IsEnabled = false;
                btnPause.Visibility = Visibility.Visible;
                btnResume.Visibility = Visibility.Collapsed;
                btnComplete.IsEnabled = false;
                _timer.Stop();
            }

            // Update paused list - show all Stopped items
            var paused = all
                .Where(w => w.Status == ActiveWorklogStatus.Stopped)
                .ToList();
            lstPaused.ItemsSource = paused;
        }

        private void ChkAlwaysOnTop_Changed(object sender, RoutedEventArgs e)
        {
            Topmost = chkAlwaysOnTop.IsChecked == true;
        }

        private void TxtTicketFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_allIssues == null || _allIssues.Count == 0) return;

            var searchText = txtTicketFilter.Text;
            
            if (string.IsNullOrWhiteSpace(searchText))
            {
                cboTickets.ItemsSource = _allIssues;
            }
            else
            {
                var filtered = _allIssues
                    .Where(i => i.Key.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                               i.Summary.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
                
                cboTickets.ItemsSource = filtered;
            }

            if (cboTickets.Items.Count > 0)
            {
                cboTickets.SelectedIndex = 0;
            }
        }

        private void BtnStartNew_Click(object sender, RoutedEventArgs e)
        {
            var selected = cboTickets.SelectedItem as JiraAssignedIssue;

            if (selected == null)
            {
                MessageBox.Show("Please select a ticket.", "No Ticket Selected",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _activeWorklogService.Start(selected.Key, selected.Summary);
            RefreshUI();
            txtStatus.Text = "Started: " + selected.Key;
        }

        private void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWorklog == null) return;

            var ticketKey = _currentWorklog.TicketKey;
            _activeWorklogService.Pause(ticketKey);
            RefreshUI();
            txtStatus.Text = "Paused: " + ticketKey;
        }

        private void BtnResume_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWorklog == null) return;

            var ticketKey = _currentWorklog.TicketKey;
            _activeWorklogService.Start(_currentWorklog.TicketKey, _currentWorklog.TicketSummary);
            RefreshUI();
            txtStatus.Text = "Resumed: " + ticketKey;
        }

        private void BtnComplete_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWorklog == null) return;

            // Pause first to capture current session
            if (_currentWorklog.Status == ActiveWorklogStatus.Running)
            {
                _activeWorklogService.Pause(_currentWorklog.TicketKey);
            }

            // Refresh to get updated total
            _currentWorklog = _activeWorklogService.Get(_currentWorklog.TicketKey);

            if (_currentWorklog.TotalSeconds < 60)
            {
                MessageBox.Show("Minimum worklog time is 1 minute.", "Time Too Short",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Mark as completed
            _activeWorklogService.MarkCompleted(_currentWorklog.TicketKey);

            txtStatus.Text = "Completed: " + _currentWorklog.TicketKey + " - ready for review";
            _currentWorklog = null;
            RefreshUI();
            
            // Notify main window
            _onWorklogCompleted?.Invoke();
        }

        private void TxtNotes_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingNotes || _currentWorklog == null) return;

            _activeWorklogService.UpdateNotes(_currentWorklog.TicketKey, txtNotes.Text);
        }

        private void NotesTextBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (sender is not TextBox textBox) return;

            const string tabSpaces = "  ";

            if (e.Key == System.Windows.Input.Key.Tab)
            {
                int caretPos = textBox.CaretIndex;
                textBox.SelectedText = tabSpaces;
                textBox.CaretIndex = caretPos + tabSpaces.Length;
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Back)
            {
                if (textBox.CaretIndex >= 2 && textBox.SelectionLength == 0)
                {
                    int pos = textBox.CaretIndex;
                    string before = textBox.Text.Substring(pos - 2, 2);
                    if (before == tabSpaces)
                    {
                        textBox.Select(pos - 2, 2);
                        textBox.SelectedText = "";
                        e.Handled = true;
                    }
                }
            }
            else if (e.Key == System.Windows.Input.Key.Delete)
            {
                if (textBox.CaretIndex + 2 <= textBox.Text.Length && textBox.SelectionLength == 0)
                {
                    int pos = textBox.CaretIndex;
                    string after = textBox.Text.Substring(pos, 2);
                    if (after == tabSpaces)
                    {
                        textBox.Select(pos, 2);
                        textBox.SelectedText = "";
                        e.Handled = true;
                    }
                }
            }
        }

        private void LstPaused_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var selected = lstPaused.SelectedItem as ActiveWorklog;
            if (selected == null) return;

            _activeWorklogService.Start(selected.TicketKey, selected.TicketSummary);
            RefreshUI();
            txtStatus.Text = "Resumed: " + selected.TicketKey;
        }

        private void ContextMenu_Resume_Click(object sender, RoutedEventArgs e)
        {
            var selected = lstPaused.SelectedItem as ActiveWorklog;
            if (selected == null) return;

            _activeWorklogService.Start(selected.TicketKey, selected.TicketSummary);
            RefreshUI();
            txtStatus.Text = "Resumed: " + selected.TicketKey;
        }

        private void ContextMenu_Complete_Click(object sender, RoutedEventArgs e)
        {
            var selected = lstPaused.SelectedItem as ActiveWorklog;
            if (selected == null) return;

            if (selected.TotalSeconds < 60)
            {
                MessageBox.Show("Minimum worklog time is 1 minute.", "Time Too Short",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _activeWorklogService.MarkCompleted(selected.TicketKey);
            RefreshUI();
            txtStatus.Text = "Completed: " + selected.TicketKey + " - ready for review";
            
            // Notify main window
            _onWorklogCompleted?.Invoke();
        }

        private void ContextMenu_Discard_Click(object sender, RoutedEventArgs e)
        {
            var selected = lstPaused.SelectedItem as ActiveWorklog;
            if (selected == null) return;

            var result = MessageBox.Show(
                string.Format("Discard worklog for {0}?\n\nTime tracked: {1}\n\nThis cannot be undone.",
                    selected.TicketKey, selected.TotalTimeFormatted),
                "Confirm Discard",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            _activeWorklogService.Remove(selected.TicketKey);
            RefreshUI();
            txtStatus.Text = "Discarded: " + selected.TicketKey;
        }
    }
}
