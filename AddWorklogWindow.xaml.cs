using System;
using System.Windows;
using System.Windows.Controls;

namespace JiraWorklogViewer
{
    public partial class AddWorklogWindow : Window
    {
        public string TicketKey { get; private set; }
        public DateTime StartedDateTime { get; private set; }
        public string TimeSpent { get; private set; }
        public string Comment { get; private set; }

        public bool IsEditMode { get; private set; }
        public string WorklogId { get; private set; }

        public AddWorklogWindow()
        {
            InitializeComponent();

            // Set size to 25% of screen
            var screenWidth = SystemParameters.PrimaryScreenWidth;
            var screenHeight = SystemParameters.PrimaryScreenHeight;
            Width = screenWidth * 0.25;
            Height = screenHeight * 0.25;
            MinWidth = 400;
            MinHeight = 340;

            InitializeControls();
        }

        public AddWorklogWindow(string defaultTicketKey) : this()
        {
            if (!string.IsNullOrWhiteSpace(defaultTicketKey))
            {
                txtTicketKey.Text = defaultTicketKey;
            }
        }

        /// <summary>
        /// Constructor for Edit mode
        /// </summary>
        public AddWorklogWindow(string worklogId, string ticketKey, DateTime started, string timeSpent, string comment) : this()
        {
            IsEditMode = true;
            WorklogId = worklogId;
            Title = "Edit Worklog";

            txtTicketKey.Text = ticketKey;
            txtTicketKey.IsEnabled = false; // Can't change ticket when editing

            dpDate.SelectedDate = started.Date;
            cboHour.SelectedItem = started.Hour.ToString("D2");
            
            // Find nearest minute option
            int minute = started.Minute;
            if (minute < 8) cboMinute.SelectedItem = "00";
            else if (minute < 23) cboMinute.SelectedItem = "15";
            else if (minute < 38) cboMinute.SelectedItem = "30";
            else if (minute < 53) cboMinute.SelectedItem = "45";
            else cboMinute.SelectedItem = "00";

            txtTimeSpent.Text = timeSpent;
            txtComment.Text = comment ?? "";
        }

        private void InitializeControls()
        {
            // Set default date to today
            dpDate.SelectedDate = DateTime.Today;

            // Populate hour combo (0-23)
            for (int i = 0; i < 24; i++)
            {
                cboHour.Items.Add(i.ToString("D2"));
            }

            // Populate minute combo (00, 15, 30, 45)
            cboMinute.Items.Add("00");
            cboMinute.Items.Add("15");
            cboMinute.Items.Add("30");
            cboMinute.Items.Add("45");

            // Set default time to current hour
            var now = DateTime.Now;
            cboHour.SelectedItem = now.Hour.ToString("D2");
            cboMinute.SelectedItem = "00";

            // Focus on ticket key field
            txtTicketKey.Focus();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            // Validate ticket key
            if (string.IsNullOrWhiteSpace(txtTicketKey.Text))
            {
                MessageBox.Show("Please enter a ticket key.", "Validation Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                txtTicketKey.Focus();
                return;
            }

            // Validate date
            if (!dpDate.SelectedDate.HasValue)
            {
                MessageBox.Show("Please select a date.", "Validation Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Validate time spent
            if (string.IsNullOrWhiteSpace(txtTimeSpent.Text))
            {
                MessageBox.Show("Please enter time spent.", "Validation Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                txtTimeSpent.Focus();
                return;
            }

            // Parse and validate time spent format
            if (!IsValidTimeSpent(txtTimeSpent.Text.Trim()))
            {
                MessageBox.Show("Invalid time format. Use formats like: 1h, 30m, 1h 30m, 2h 15m",
                    "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtTimeSpent.Focus();
                return;
            }

            // Build the started datetime
            var date = dpDate.SelectedDate.Value;
            int hour = int.Parse(cboHour.SelectedItem.ToString());
            int minute = int.Parse(cboMinute.SelectedItem.ToString());
            StartedDateTime = new DateTime(date.Year, date.Month, date.Day, hour, minute, 0);

            TicketKey = txtTicketKey.Text.Trim().ToUpperInvariant();
            TimeSpent = NormalizeTimeSpent(txtTimeSpent.Text.Trim());
            Comment = txtComment.Text.Trim();

            DialogResult = true;
            Close();
        }

        private bool IsValidTimeSpent(string input)
        {
            // Valid formats: 1h, 30m, 1h 30m, 2h30m, 1h30m
            input = input.ToLowerInvariant().Replace(" ", "");

            bool hasHours = false;
            bool hasMinutes = false;

            int hIndex = input.IndexOf('h');
            int mIndex = input.IndexOf('m');

            if (hIndex > 0)
            {
                string hourPart = input.Substring(0, hIndex);
                if (!int.TryParse(hourPart, out int hours) || hours < 0)
                    return false;
                hasHours = true;
            }

            if (mIndex > 0)
            {
                int startIndex = hIndex >= 0 ? hIndex + 1 : 0;
                if (mIndex > startIndex)
                {
                    string minPart = input.Substring(startIndex, mIndex - startIndex);
                    if (!int.TryParse(minPart, out int mins) || mins < 0 || mins >= 60)
                        return false;
                    hasMinutes = true;
                }
            }

            return hasHours || hasMinutes;
        }

        private string NormalizeTimeSpent(string input)
        {
            // Convert to Jira format: "1h 30m"
            input = input.ToLowerInvariant().Replace(" ", "");

            int hours = 0;
            int minutes = 0;

            int hIndex = input.IndexOf('h');
            int mIndex = input.IndexOf('m');

            if (hIndex > 0)
            {
                string hourPart = input.Substring(0, hIndex);
                int.TryParse(hourPart, out hours);
            }

            if (mIndex > 0)
            {
                int startIndex = hIndex >= 0 ? hIndex + 1 : 0;
                if (mIndex > startIndex)
                {
                    string minPart = input.Substring(startIndex, mIndex - startIndex);
                    int.TryParse(minPart, out minutes);
                }
            }

            if (hours > 0 && minutes > 0)
                return string.Format("{0}h {1}m", hours, minutes);
            else if (hours > 0)
                return string.Format("{0}h", hours);
            else
                return string.Format("{0}m", minutes);
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

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
