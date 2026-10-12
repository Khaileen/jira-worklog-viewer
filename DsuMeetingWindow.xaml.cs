using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using JiraWorklogViewer.Models;
using JiraWorklogViewer.Services;

namespace JiraWorklogViewer
{
    public partial class DsuMeetingWindow : Window
    {
        private readonly JiraService _jiraService;
        private readonly OllamaService _ollamaService;
        private BedrockService _bedrockService;
        private CancellationTokenSource _cts;
        private readonly RosterService _rosterService = new RosterService();
        private readonly DsuStateService _stateService = new DsuStateService();
        private readonly DispatcherTimer _saveTimer;
        private string _savedModel;
        private readonly List<RosterUser> _roster;

        // Loaded tickets (with any edits) per "accountId|yyyy-MM-dd", so edits survive switching users.
        private readonly Dictionary<string, List<UserDayTicket>> _cache = new Dictionary<string, List<UserDayTicket>>();

        private bool _busy;
        private bool _suppressModelChange;
        private string _currentModel;

        /// <summary>One "Yesterday" / "Today" block: ticket grid + editable DSU entry + Reset.</summary>
        private class DaySection
        {
            public string Label;
            public DatePicker Picker;
            public DataGrid Grid;
            public TextBox Entry;
            public Button Reset;
            public Button Add;
            public Button Improve;
            public Button ImproveAll;
            public TextBlock Edited;
            public bool SuppressEntryChange;
            public DateTime? Day { get { return Picker.SelectedDate; } }
            public UserDayTicket SelectedTicket { get { return Grid.SelectedItem as UserDayTicket; } }
        }

        private List<DaySection> _sections;

        public DsuMeetingWindow(JiraService jiraService, OllamaService ollamaService)
        {
            InitializeComponent();
            _jiraService = jiraService;
            _ollamaService = ollamaService;
            _roster = _rosterService.Load();

            // Restore everything saved last time: worklogs, AI + manual entries, edits, flags, model
            var state = _stateService.Load();
            _savedModel = state.Model;
            foreach (var kv in state.Days) _cache[kv.Key] = kv.Value;
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _saveTimer.Tick += (s2, e2) => { _saveTimer.Stop(); SaveStateNow(); };
            _sections = new List<DaySection>
            {
                new DaySection { Label = "yesterday", Picker = dpYesterday, Grid = gridTickets, Entry = txtEntry, Reset = btnReset, Add = btnAddEntry, Improve = btnImprove, ImproveAll = btnImproveAll, Edited = txtEdited },
                new DaySection { Label = "today", Picker = dpToday, Grid = gridToday, Entry = txtEntryToday, Reset = btnResetToday, Add = btnAddEntryToday, Improve = btnImproveToday, ImproveAll = btnImproveAllToday, Edited = txtEditedToday }
            };
            dpToday.SelectedDate = DateTime.Today;
            dpYesterday.SelectedDate = PreviousWorkingDay(DateTime.Today);
            RefreshRosterList();
            txtStatus.Text = _roster.Count > 0
                ? "Select a user from the roster, or click “AI Summarize ALL”."
                : "Roster is empty. Click “Add User…” to build your DSU list.";

            Loaded += async (s, e) =>
            {
                txtBedrockProfile.Text = App.Settings.BedrockProfile;
                _bedrockService = new BedrockService(txtBedrockProfile.Text.Trim());
                await LoadModelsAsync();
            };
            Closed += (s, e) =>
            {
                if (_cts != null) _cts.Cancel();
                _saveTimer.Stop();
                SaveStateNow();
            };
            UpdateDoneFlags();
        }

        #region Export

        /// <summary>Writes the DSU Markdown (same layout as the /dsu command, minus Blockers) and opens it in VS Code.</summary>
        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            var yes = _sections.First(s => s.Label == "yesterday");
            var tod = _sections.First(s => s.Label == "today");
            if (!yes.Day.HasValue || !tod.Day.HasValue) return;

            // Only completed users (both days loaded and every ticket AI-improved/summarized — the bold ✓ in the roster)
            UpdateDoneFlags();
            var included = _roster.Where(u => u.IsDone).ToList();
            var skipped = _roster.Where(u => !u.IsDone).Select(u => u.DisplayName).ToList();

            if (included.Count == 0)
            {
                MessageBox.Show("No completed users yet. A user is completed (✓, bold) once both days are loaded and every ticket is AI-improved or summarized.",
                    "Export DSU", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var md = new StringBuilder();
            md.AppendLine("# DSU — " + tod.Day.Value.ToString("yyyy-MM-dd"));
            md.AppendLine();
            foreach (var u in included)
            {
                md.AppendLine("## " + u.DisplayName);
                md.AppendLine();
                AppendDay(md, "Yesterday", yes.Day.Value, u);
                AppendDay(md, "Today", tod.Day.Value, u);
                md.AppendLine("---");
                md.AppendLine();
            }

            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            string path = Path.Combine(dir, "DSU " + tod.Day.Value.ToString("yyyy-MM-dd") + ".md");

            if (File.Exists(path))
            {
                var answer = MessageBox.Show(
                    "\"" + Path.GetFileName(path) + "\" already exists (it may contain Blockers you added by hand).\n\nYes = overwrite it\nNo = save as a new numbered file\nCancel = stop",
                    "Export DSU", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (answer == MessageBoxResult.Cancel) return;
                if (answer == MessageBoxResult.No)
                {
                    int n = 2;
                    string baseName = Path.GetFileNameWithoutExtension(path);
                    do { path = Path.Combine(dir, baseName + " (" + n++ + ").md"); } while (File.Exists(path));
                }
            }

            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(path, md.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not write the file: " + ex.Message, "Export DSU", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            txtStatus.Text = "Exported " + included.Count + " completed user(s) to " + path
                + (skipped.Count > 0 ? " — left out (not completed): " + string.Join(", ", skipped) + "." : ".");
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", "/c code \"" + path + "\"") { CreateNoWindow = true, UseShellExecute = false });
            }
            catch
            {
                try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
            }
        }

        private void AppendDay(StringBuilder md, string title, DateTime day, RosterUser u)
        {
            md.AppendLine("**" + title + " (" + day.ToString("yyyy-MM-dd") + ")**");
            List<UserDayTicket> list;
            if (!_cache.TryGetValue(CacheKey(u, day), out list))
            {
                md.AppendLine("- Not loaded.");
            }
            else if (list.Count == 0)
            {
                md.AppendLine("- No Jira activity found for " + day.ToString("yyyy-MM-dd") + ".");
            }
            else
            {
                foreach (var t in list) md.AppendLine(FormatBullet(t));
            }
            md.AppendLine();
        }

        /// <summary>"KEY – sentence [Status…]" becomes "- KEY: sentence [Status…]"; extra lines are indented under the bullet.</summary>
        private static string FormatBullet(UserDayTicket t)
        {
            string text = (t.CurrentEntry ?? string.Empty).Replace("\r", "").Trim();
            string prefix = t.Key + " – ";
            if (text.StartsWith(prefix)) text = t.Key + ": " + text.Substring(prefix.Length);

            var lines = text.Split('\n');
            var sb = new StringBuilder("- " + lines[0].TrimEnd());
            for (int i = 1; i < lines.Length; i++)
                sb.Append(Environment.NewLine).Append(lines[i].Length == 0 ? string.Empty : "  " + lines[i].TrimEnd());
            return sb.ToString();
        }

        #endregion

        #region Persistence

        /// <summary>Schedules a save shortly after the last change (debounced so typing does not rewrite the file per keystroke).</summary>
        private void MarkDirty()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void SaveStateNow()
        {
            _stateService.Save(new DsuState
            {
                Model = _currentModel ?? _savedModel,
                Days = _cache
            });
        }

        #endregion

        #region AI summarize

        private async void TxtBedrockProfile_LostFocus(object sender, RoutedEventArgs e)
        {
            App.Settings.BedrockProfile = txtBedrockProfile.Text.Trim();
            App.Settings.Save();
            _bedrockService = new BedrockService(txtBedrockProfile.Text.Trim());
            await LoadModelsAsync();
        }

        private async Task LoadModelsAsync()
        {
            _suppressModelChange = true;
            cboModel.Items.Clear();
            if (_bedrockService.IsConfigured)
            {
                cboModel.Items.Add(ModelNames.BedrockHaiku);
                cboModel.Items.Add(ModelNames.BedrockSonnet);
                cboModel.Items.Add(ModelNames.BedrockOpus);
            }
            var models = await _ollamaService.GetAvailableModelsAsync();
            foreach (var m in models) cboModel.Items.Add(m.Name);
            if (_savedModel != null && cboModel.Items.Contains(_savedModel)) cboModel.SelectedItem = _savedModel;
            else if (cboModel.Items.Count > 0) cboModel.SelectedIndex = 0;
            _currentModel = cboModel.SelectedItem as string;
            _suppressModelChange = false;
            btnSummarizeAll.IsEnabled = !_busy && cboModel.Items.Count > 0 && _roster.Count > 0;
            UpdateImproveButtons();
            if (cboModel.Items.Count == 0)
                txtStatus.Text = "No AI model available — enter an AWS Profile or start Ollama. Rule-based one-liners still work.";
        }

        private void BtnCancelAi_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null) _cts.Cancel();
        }

        private void CboModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressModelChange || _busy) return;
            string newModel = cboModel.SelectedItem as string;
            if (newModel == null || newModel == _currentModel) return;

            bool anySummarized = _cache.Values.Any(list => list.Any(t => t.AiSummarized));
            if (!anySummarized)
            {
                _currentModel = newModel;
                MarkDirty();
                return;
            }

            var answer = MessageBox.Show(
                string.Format("Switch to \"{0}\"?\n\nAll users will be un-flagged as done. Existing summaries stay until re-summarized, which happens when you open a user or click AI Summarize ALL (your manual edits are kept).", newModel),
                "Switch Model", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
            {
                // Revert the dropdown to the previous model without re-triggering this handler
                _suppressModelChange = true;
                cboModel.SelectedItem = _currentModel;
                _suppressModelChange = false;
                return;
            }

            _currentModel = newModel;
            foreach (var list in _cache.Values)
                foreach (var t in list)
                    t.AiSummarized = false;

            UpdateDoneFlags();
            MarkDirty();
            txtStatus.Text = "Model switched to " + newModel + ". Users un-flagged — re-summarize by opening a user or clicking AI Summarize ALL.";
        }

        private async void BtnSummarizeAll_Click(object sender, RoutedEventArgs e)
        {
            await RunSummarizeAllAsync();
        }

        private async Task RunSummarizeAllAsync()
        {
            if (_busy || _roster.Count == 0 || _sections.Any(s => !s.Day.HasValue)) return;

            SetBusy(true);
            int index = 0;
            try
            {
                foreach (var user in _roster.ToList())
                {
                    index++;
                    string prefix = string.Format("User {0}/{1}: ", index, _roster.Count);

                    var all = new List<UserDayTicket>();
                    foreach (var sec in _sections)
                    {
                        string key = CacheKey(user, sec.Day.Value);
                        List<UserDayTicket> tickets;
                        if (!_cache.TryGetValue(key, out tickets))
                        {
                            txtStatus.Text = prefix + "loading " + user.DisplayName + " (" + sec.Label + ")…";
                            progressBar.IsIndeterminate = true;
                            tickets = await FetchTicketsAsync(user, sec.Day.Value, key, sec.Label);
                        }
                        foreach (var tk in tickets) tk.DayLabel = sec.Label;
                        all.AddRange(tickets);
                    }

                    if (all.Count > 0)
                    {
                        // Cancel button / model errors stop the whole run
                        if (!await SummarizeTicketsAsync(user, all, prefix)) break;
                    }
                }

                // Show the (now summarized) tickets of the selected user
                var sel = SelectedUser;
                if (sel != null)
                {
                    foreach (var sec in _sections)
                    {
                        List<UserDayTicket> shown;
                        if (_cache.TryGetValue(CacheKey(sel, sec.Day.Value), out shown))
                        {
                            sec.Grid.ItemsSource = shown;
                            SelectFirstOrNone(sec, shown);
                        }
                    }
                }
                if (!txtStatus.Text.StartsWith("AI summarize cancelled"))
                    txtStatus.Text = "AI Summarize ALL finished for " + _roster.Count + " user(s).";
            }
            catch (Exception ex)
            {
                txtStatus.Text = "Summarize ALL failed: " + ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task<bool> SummarizeTicketsAsync(RosterUser user, List<UserDayTicket> tickets, string prefix)
        {
            string model = cboModel.SelectedItem != null ? cboModel.SelectedItem.ToString() : null;
            if (string.IsNullOrEmpty(model))
            {
                txtStatus.Text = "No AI model available. Enter an AWS Profile or start Ollama.";
                return false;
            }

            var pending = tickets.Where(t => !t.AiSummarized).ToList();
            if (pending.Count == 0)
            {
                txtStatus.Text = (prefix ?? "") + user.DisplayName + ": " + tickets.Count + " ticket(s) — already AI-summarized.";
                return true;
            }

            bool bedrock = ModelNames.IsBedrock(model);
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            btnCancelAi.IsEnabled = true;
            progressBar.IsIndeterminate = false;
            progressBar.Maximum = pending.Count;
            progressBar.Value = 0;

            int done = 0, failed = 0;
            try
            {
                foreach (var t in pending)
                {
                    ct.ThrowIfCancellationRequested();
                    txtStatus.Text = string.Format("{5}AI summarizing {0} with {1} — {2} ({3}/{4})… switching users is locked until done.",
                        user.DisplayName, model, t.Key, done + 1, pending.Count, prefix ?? "");

                    var prompt = BuildOneLinerPrompt(t);
                    var result = bedrock
                        ? await _bedrockService.AnalyzeAsync(prompt, model, ct)
                        : await _ollamaService.AnalyzeAsync(prompt, model, ct, numCtx: 8192);

                    string line = result.Success ? CleanAiLine(result.Content) : null;
                    if (string.IsNullOrEmpty(line))
                    {
                        failed++;
                    }
                    else
                    {
                        // Never overwrite text the user typed themselves; AI-made text can be replaced
                        if (t.EditedEntry == null || t.AiEntry) ApplyAiEntry(t, BuildEntryFromAi(t, line));
                        t.AiSummarized = true;
                        RefreshBoxesShowing(t);
                    }
                    done++;
                    progressBar.Value = done;
                }
                txtStatus.Text = failed == 0
                    ? string.Format("AI summaries ready for {0} ({1} ticket(s)).", user.DisplayName, pending.Count)
                    : string.Format("AI summaries done for {0}; {1} ticket(s) kept the raw worklog entry (model call failed).", user.DisplayName, failed);
                return true;
            }
            catch (OperationCanceledException)
            {
                txtStatus.Text = string.Format("AI summarize cancelled ({0}/{1} done).", done, pending.Count);
                return false;
            }
            finally
            {
                btnCancelAi.IsEnabled = false;
                _cts = null;
                UpdateDoneFlags();
            }
        }

        private string BuildOneLinerPrompt(UserDayTicket t)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Write ONE concise stand-up sentence describing what this person did on this Jira ticket " + (t.DayLabel ?? "yesterday") + ".");
            sb.AppendLine("Output ONLY that single sentence. No bullet, no preamble, no ticket key.");
            sb.AppendLine();
            sb.AppendLine("RULES:");
            sb.AppendLine("- Use ONLY facts from the worklog comments below — do not invent or extrapolate");
            sb.AppendLine("- Keep key outcomes, findings and technical specifics; start with a past-tense verb");
            sb.AppendLine("- Maximum 30 words");
            sb.AppendLine("- If there is no comment, write that time was logged without detail");
            if (t.HasManualText)
            {
                sb.AppendLine("- Some comments were typed by hand and may contain shorthand, acronyms, misspellings, poor grammar or non-English words/dialects.");
                sb.AppendLine("  Interpret them, fix spelling and grammar, translate to clear English, and expand shorthand where the meaning is obvious.");
                sb.AppendLine("  Keep acronyms and technical terms you cannot confidently expand exactly as written. Never guess at meaning that is not there.");
            }
            sb.AppendLine();
            sb.AppendLine("TICKET: " + t.Key + " — " + t.Summary);
            sb.AppendLine("TIME LOGGED: " + t.TimeSpent);
            sb.AppendLine();
            sb.AppendLine("WORKLOG COMMENTS:");
            if (t.WorklogComments.Count == 0) sb.AppendLine("(none)");
            foreach (var c in t.WorklogComments)
            {
                var txt = c.Replace("\r", "");
                sb.AppendLine(txt.Length > 4000 ? txt.Substring(0, 4000) : txt);
                sb.AppendLine("---");
            }
            if (t.StatusChanges.Count > 0)
                sb.AppendLine("STATUS CHANGES: " + string.Join(", ", t.StatusChanges));
            sb.AppendLine();
            sb.AppendLine("Output the single sentence now:");
            return sb.ToString();
        }

        private static string CleanAiLine(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return null;
            return content.Replace("\r", "").Split('\n')
                .Select(l => l.Trim().TrimStart('-', '*', ' ').Trim())
                .FirstOrDefault(l => l.Length > 0);
        }

        private static string BuildEntryFromAi(UserDayTicket t, string line)
        {
            var sb = new StringBuilder();
            sb.Append(t.Key).Append(" – ").Append(line);
            if (t.StatusChanges.Count > 0)
                sb.Append(" [Status: ").Append(string.Join(", ", t.StatusChanges)).Append("]");
            return sb.ToString();
        }

        #endregion

        private static DateTime PreviousWorkingDay(DateTime today)
        {
            var d = today.AddDays(-1);
            while (d.DayOfWeek == DayOfWeek.Saturday || d.DayOfWeek == DayOfWeek.Sunday)
                d = d.AddDays(-1);
            return d;
        }

        private RosterUser SelectedUser { get { return lstRoster.SelectedItem as RosterUser; } }

        /// <summary>Finds the section a grid / entry box / reset button belongs to.</summary>
        private DaySection SectionFor(object control)
        {
            return _sections.First(s => ReferenceEquals(s.Grid, control)
                || ReferenceEquals(s.Entry, control) || ReferenceEquals(s.Reset, control)
                || ReferenceEquals(s.Add, control) || ReferenceEquals(s.Improve, control)
                || ReferenceEquals(s.ImproveAll, control));
        }

        private string CacheKey(RosterUser u, DateTime day)
        {
            return u.AccountId + "|" + day.ToString("yyyy-MM-dd");
        }

        #region Roster

        private void RefreshRosterList(RosterUser select = null)
        {
            lstRoster.ItemsSource = null;
            lstRoster.ItemsSource = _roster;
            if (select != null) lstRoster.SelectedItem = select;
        }

        private void BtnAddUser_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new UserSearchDialog(_jiraService, _roster.Select(u => u.AccountId));
            dlg.Owner = this;
            dlg.Loaded += (s, ev) => App.ApplyScale(dlg);
            if (dlg.ShowDialog() != true) return;

            RosterUser first = null;
            foreach (var u in dlg.SelectedUsers)
            {
                if (_roster.Any(r => r.AccountId == u.AccountId)) continue;
                var ru = new RosterUser { AccountId = u.AccountId, DisplayName = u.DisplayName, Email = u.Email };
                _roster.Add(ru);
                if (first == null) first = ru;
            }
            _rosterService.Save(_roster);
            RefreshRosterList(SelectedUser);
        }

        private void BtnRemoveUser_Click(object sender, RoutedEventArgs e)
        {
            var user = SelectedUser;
            if (user == null) return;
            _roster.Remove(user);
            _rosterService.Save(_roster);
            RefreshRosterList();
            ClearTickets();
        }

        private async void LstRoster_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            btnRemoveUser.IsEnabled = SelectedUser != null;
            await LoadSelectedUserAsync(false);
        }

        #endregion

        #region Tickets

        private async void DpYesterday_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
        {
            await LoadSelectedUserAsync(false);
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            await LoadSelectedUserAsync(true);
        }

        private void ClearTickets()
        {
            txtUserHeader.Text = string.Empty;
            foreach (var sec in _sections)
            {
                sec.Grid.ItemsSource = null;
                ShowEntry(sec, null);
                sec.Add.IsEnabled = false;
            }
            UpdateDoneFlags();
        }

        private void BtnAddEntry_Click(object sender, RoutedEventArgs e)
        {
            var clicked = SectionFor(sender);
            var user = SelectedUser;
            if (_busy || user == null) return;

            var dlg = new AddEntryDialog(_jiraService, clicked.Label == "today");
            dlg.Owner = this;
            dlg.Loaded += (s, ev) => App.ApplyScale(dlg);
            if (dlg.ShowDialog() != true) return;

            // The radio button in the dialog decides the target grid
            var sec = _sections.First(s => s.Label == (dlg.ToToday ? "today" : "yesterday"));
            if (!sec.Day.HasValue) return;

            string key = CacheKey(user, sec.Day.Value);
            List<UserDayTicket> list;
            if (!_cache.TryGetValue(key, out list))
            {
                list = new List<UserDayTicket>();
                _cache[key] = list;
            }

            // Same ticket already listed → add the text to it, otherwise create a new row
            var ticket = list.FirstOrDefault(t => t.Key == dlg.Ticket.Key);
            if (ticket == null)
            {
                ticket = new UserDayTicket
                {
                    Key = dlg.Ticket.Key,
                    Summary = dlg.Ticket.Summary,
                    Status = dlg.Ticket.Status,
                    DayLabel = sec.Label,
                    IsManual = true
                };
                list.Add(ticket);
                list.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            }
            ticket.WorklogComments.Add(dlg.Text);
            ticket.ManualTexts.Add(dlg.Text);
            ticket.ManualAddedOn.Add(DateTime.Today);
            ticket.HasManualText = true;
            ticket.AiSummarized = false;
            ticket.EditedEntry = null;
            ticket.AiEntry = false;
            ticket.DefaultEntry = BuildOneLiner(ticket);

            sec.Grid.ItemsSource = null;
            sec.Grid.ItemsSource = list;
            sec.Grid.SelectedItem = ticket;
            UpdateDoneFlags();
            UpdateImproveButtons();
            MarkDirty();
            txtStatus.Text = "Added " + ticket.Key + ". Click ✨ AI Improve to turn it into a one-liner.";
        }

        private async Task LoadSelectedUserAsync(bool forceReload)
        {
            var user = SelectedUser;
            if (user == null || _sections.Any(s => !s.Day.HasValue)) { ClearTickets(); return; }

            txtUserHeader.Text = user.DisplayName;

            SetBusy(true);
            try
            {
                var all = new List<UserDayTicket>();
                var counts = new List<string>();
                foreach (var sec in _sections)
                {
                    DateTime day = sec.Day.Value;
                    string key = CacheKey(user, day);
                    List<UserDayTicket> tickets;
                    if (!forceReload && _cache.TryGetValue(key, out tickets))
                    {
                        // cached — nothing to fetch
                    }
                    else
                    {
                        sec.Grid.ItemsSource = null;
                        ShowEntry(sec, null);
                        txtStatus.Text = "Loading " + user.DisplayName + " (" + sec.Label + ")…";
                        tickets = await FetchTicketsAsync(user, day, key, sec.Label);
                    }

                    // Saved data from an earlier day may carry the other label (today → yesterday the next morning)
                    foreach (var tk in tickets) tk.DayLabel = sec.Label;
                    sec.Grid.ItemsSource = tickets;
                    SelectFirstOrNone(sec, tickets);
                    counts.Add(char.ToUpper(sec.Label[0]) + sec.Label.Substring(1) + ": " + tickets.Count);
                    all.AddRange(tickets);
                }
                txtStatus.Text = string.Join("   ", counts) + " ticket(s). Click ✨ AI Improve / Improve ALL to turn worklogs into one-liners. ↻ Refresh re-pulls from Jira.";
            }
            catch (Exception ex)
            {
                txtStatus.Text = "Load failed: " + ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task<List<UserDayTicket>> FetchTicketsAsync(RosterUser user, DateTime day, string key, string label)
        {
            var tickets = await _jiraService.GetUserDayActivityAsync(user.AccountId, day);
            foreach (var t in tickets)
            {
                t.DayLabel = label;
                t.DefaultEntry = BuildOneLiner(t);
            }

            // Keep edits and manually added entries when refreshing
            if (_cache.TryGetValue(key, out var old))
            {
                foreach (var t in tickets)
                {
                    var prev = old.FirstOrDefault(o => o.Key == t.Key);
                    if (prev == null) continue;

                    // Text added by hand to a Jira ticket is not in Jira — carry it over
                    if (prev.ManualTexts.Count > 0)
                    {
                        t.ManualTexts.AddRange(prev.ManualTexts);
                        t.ManualAddedOn.AddRange(prev.ManualAddedOn);
                        t.WorklogComments.AddRange(prev.ManualTexts);
                        t.HasManualText = true;
                    }
                    t.DefaultEntry = BuildOneLiner(t);

                    // Keep the user's / AI's entry (and whether it was AI-made)
                    // Always keep the entry (user-typed or AI-made); Reset is the user's call
                    if (prev.EditedEntry != null)
                    {
                        t.EditedEntry = prev.EditedEntry;
                        t.AiEntry = prev.AiEntry;
                    }
                    // The AI flag only survives if the worklog text it was based on is unchanged
                    if (prev.AiSummarized && prev.WorklogComments.SequenceEqual(t.WorklogComments))
                        t.AiSummarized = true;
                }
                foreach (var m in old.Where(o => o.IsManual && tickets.All(t => t.Key != o.Key)))
                    tickets.Add(m);
                tickets.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            }
            _cache[key] = tickets;
            return tickets;
        }

        /// <summary>
        /// Locks everything that could switch the user/date (or start another run) while a load
        /// or AI summarize is in flight, and shows the progress bar.
        /// </summary>
        private void SetBusy(bool busy)
        {
            _busy = busy;
            if (!busy) { UpdateDoneFlags(); MarkDirty(); }
            lstRoster.IsEnabled = !busy;
            btnAddUser.IsEnabled = !busy;
            btnRemoveUser.IsEnabled = !busy && SelectedUser != null;
            dpYesterday.IsEnabled = !busy;
            dpToday.IsEnabled = !busy;
            btnRefresh.IsEnabled = !busy;
            btnExport.IsEnabled = !busy;
            foreach (var sec in _sections) sec.Add.IsEnabled = !busy && SelectedUser != null;
            btnSummarizeAll.IsEnabled = !busy && cboModel.Items.Count > 0 && _roster.Count > 0;
            cboModel.IsEnabled = !busy;
            txtBedrockProfile.IsEnabled = !busy;
            progressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            progressBar.IsIndeterminate = busy;
            UpdateImproveButtons();
        }

        /// <summary>A user is "done" when both days' tickets are loaded and all AI-summarized.</summary>
        private void UpdateDoneFlags()
        {
            foreach (var u in _roster)
            {
                bool done = _sections.All(sec =>
                {
                    List<UserDayTicket> list;
                    return sec.Day.HasValue
                        && _cache.TryGetValue(CacheKey(u, sec.Day.Value), out list)
                        && list.All(t => t.AiSummarized);
                });
                u.IsDone = done;
            }
        }

        private void SelectFirstOrNone(DaySection sec, List<UserDayTicket> tickets)
        {
            if (tickets.Count > 0) sec.Grid.SelectedIndex = 0;
            else ShowEntry(sec, null);
        }

        #endregion

        #region DSU entry

        private void GridTickets_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var sec = SectionFor(sender);
            ShowEntry(sec, sec.SelectedTicket);
        }

        private void ShowEntry(DaySection sec, UserDayTicket ticket)
        {
            sec.SuppressEntryChange = true;
            sec.Entry.Text = ticket != null ? ticket.CurrentEntry : string.Empty;
            sec.SuppressEntryChange = false;
            sec.Entry.IsEnabled = ticket != null;
            UpdateEditedState(sec, ticket);
            UpdateImproveButtons();
        }

        private void UpdateImproveButtons()
        {
            foreach (var sec in _sections)
            {
                bool ai = !_busy && cboModel.SelectedItem != null;
                var list = sec.Grid.ItemsSource as List<UserDayTicket>;
                sec.Improve.IsEnabled = ai && sec.SelectedTicket != null;
                sec.ImproveAll.IsEnabled = ai && list != null && list.Count > 0;
            }
        }

        private void ApplyAiEntry(UserDayTicket t, string text)
        {
            t.EditedEntry = text == t.DefaultEntry ? null : text;
            t.AiEntry = t.EditedEntry != null;
        }

        /// <summary>Re-shows the entry in any section whose selected ticket is t.</summary>
        private void RefreshBoxesShowing(UserDayTicket t)
        {
            foreach (var sec in _sections)
                if (ReferenceEquals(t, sec.SelectedTicket)) ShowEntry(sec, t);
        }

        private async void BtnImprove_Click(object sender, RoutedEventArgs e)
        {
            var sec = SectionFor(sender);
            var ticket = sec.SelectedTicket;
            if (ticket == null) return;
            await ImproveAsync(new List<UserDayTicket> { ticket }, "AI Improve");
        }

        private async void BtnImproveAll_Click(object sender, RoutedEventArgs e)
        {
            var sec = SectionFor(sender);
            var list = sec.Grid.ItemsSource as List<UserDayTicket>;
            if (list == null || list.Count == 0) return;
            await ImproveAsync(list.ToList(), "Improve ALL");
        }

        /// <summary>
        /// Rewrites the CURRENT text of each ticket (worklogs, or the user's own edits) as a clean one-liner.
        /// Reset restores the raw worklog text.
        /// </summary>
        private async Task ImproveAsync(List<UserDayTicket> tickets, string what)
        {
            string model = cboModel.SelectedItem as string;
            if (_busy || model == null || tickets.Count == 0) return;
            bool bedrock = ModelNames.IsBedrock(model);

            SetBusy(true);
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            btnCancelAi.IsEnabled = true;
            progressBar.IsIndeterminate = false;
            progressBar.Maximum = tickets.Count;
            progressBar.Value = 0;

            int done = 0, failed = 0;
            try
            {
                foreach (var t in tickets)
                {
                    ct.ThrowIfCancellationRequested();
                    txtStatus.Text = string.Format("{0}: {1} with {2} ({3}/{4})…", what, t.Key, model, done + 1, tickets.Count);

                    var prompt = BuildImprovePrompt(t, t.CurrentEntry ?? string.Empty);
                    var result = bedrock
                        ? await _bedrockService.AnalyzeAsync(prompt, model, ct)
                        : await _ollamaService.AnalyzeAsync(prompt, model, ct, numCtx: 8192);

                    string line = result.Success ? CleanAiLine(result.Content) : null;
                    if (string.IsNullOrEmpty(line))
                    {
                        failed++;
                    }
                    else
                    {
                        ApplyAiEntry(t, BuildEntryFromAi(t, line));
                        t.AiSummarized = true;
                        RefreshBoxesShowing(t);
                    }
                    done++;
                    progressBar.Value = done;
                }
                txtStatus.Text = failed == 0
                    ? string.Format("{0} finished ({1} ticket(s)). Reset restores the original worklog text.", what, tickets.Count)
                    : string.Format("{0} finished; {1} ticket(s) failed and were left unchanged.", what, failed);
            }
            catch (OperationCanceledException)
            {
                txtStatus.Text = string.Format("{0} cancelled ({1}/{2} done).", what, done, tickets.Count);
            }
            finally
            {
                btnCancelAi.IsEnabled = false;
                _cts = null;
                SetBusy(false);
            }
        }

        private string BuildImprovePrompt(UserDayTicket t, string text)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Turn the stand-up text below into ONE clear, concise sentence describing what was done on this Jira ticket.");
            sb.AppendLine("The text may be full worklogs, or hand-typed with shorthand, acronyms, misspellings, poor grammar, or non-English words/dialects.");
            sb.AppendLine();
            sb.AppendLine("RULES:");
            sb.AppendLine("- Output ONLY that single sentence on one line. No bullet, no preamble, no quotes, no ticket key");
            sb.AppendLine("- Use ONLY facts in the text — do not invent or extrapolate");
            sb.AppendLine("- Fix spelling and grammar, translate to clear English, expand shorthand where the meaning is obvious");
            sb.AppendLine("- Keep acronyms and technical terms you cannot confidently expand exactly as written");
            sb.AppendLine("- Keep key outcomes, findings and technical specifics; start with a past-tense verb");
            sb.AppendLine("- Ignore a leading \"KEY – Summary\" header line and any trailing [Status: ...] part — they are added back automatically");
            sb.AppendLine("- A line like \"Manual Entry From Yesterday:\" is only a label for the text below it, not content");
            sb.AppendLine("- Maximum 35 words");
            sb.AppendLine();
            sb.AppendLine("TICKET: " + t.Key + " — " + t.Summary);
            sb.AppendLine();
            sb.AppendLine("TEXT:");
            sb.AppendLine(text.Length > 6000 ? text.Substring(0, 6000) : text);
            sb.AppendLine();
            sb.AppendLine("Output the single sentence now:");
            return sb.ToString();
        }

        private void TxtEntry_TextChanged(object sender, TextChangedEventArgs e)
        {
            var sec = SectionFor(sender);
            if (sec.SuppressEntryChange) return;
            var ticket = sec.SelectedTicket;
            if (ticket == null) return;
            ticket.EditedEntry = sec.Entry.Text == ticket.DefaultEntry ? null : sec.Entry.Text;
            ticket.AiEntry = false;
            UpdateEditedState(sec, ticket);
            MarkDirty();
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            var sec = SectionFor(sender);
            var ticket = sec.SelectedTicket;
            if (ticket == null) return;
            ticket.EditedEntry = null;
            ticket.AiEntry = false;
            ShowEntry(sec, ticket);
            MarkDirty();
        }

        private void UpdateEditedState(DaySection sec, UserDayTicket ticket)
        {
            bool edited = ticket != null && ticket.EditedEntry != null;
            sec.Reset.IsEnabled = edited;
            sec.Edited.Text = ticket != null && ticket.AiEntry ? "AI" : "edited";
            sec.Edited.Visibility = edited ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// The raw entry shown before any AI step: header line, then the entirety of the worklog text.
        /// "KEY – Summary [Status: A → B]" + each worklog comment in full.
        /// </summary>
        private string BuildOneLiner(UserDayTicket t)
        {
            var sb = new StringBuilder();
            sb.Append(t.Key).Append(" – ").Append(t.Summary);
            if (t.StatusChanges.Count > 0)
                sb.Append(" [Status: ").Append(string.Join(", ", t.StatusChanges)).Append("]");

            // Jira worklog comments = all comments minus the hand-typed ones (which are shown separately, clearly labelled)
            var jira = t.WorklogComments.ToList();
            foreach (var m in t.ManualTexts)
            {
                int idx = jira.LastIndexOf(m);
                if (idx >= 0) jira.RemoveAt(idx);
            }
            jira = jira.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();

            if (jira.Count == 0 && t.ManualTexts.Count == 0)
            {
                if (t.TimeSpentSeconds > 0) sb.Append(Environment.NewLine).Append(t.TimeSpent).Append(" logged (no comment)");
            }

            foreach (var c in jira)
            {
                sb.Append(Environment.NewLine).Append(Environment.NewLine);
                sb.Append(c.Replace("\r", "").Trim().Replace("\n", Environment.NewLine));
            }

            for (int i = 0; i < t.ManualTexts.Count; i++)
            {
                DateTime? added = i < t.ManualAddedOn.Count ? t.ManualAddedOn[i] : (DateTime?)null;
                sb.Append(Environment.NewLine).Append(Environment.NewLine);
                sb.Append("Manual Entry").Append(ManualFrom(added)).Append(":").Append(Environment.NewLine);
                sb.Append(t.ManualTexts[i].Replace("\r", "").Trim().Replace("\n", Environment.NewLine));
            }
            return sb.ToString();
        }

        /// <summary>" From Today" / " From Yesterday" / " From Fri 2026-10-09" relative to now ("" when the date is unknown).</summary>
        private static string ManualFrom(DateTime? added)
        {
            if (!added.HasValue) return string.Empty;
            var d = added.Value.Date;
            if (d == DateTime.Today) return " From Today";
            if (d == DateTime.Today.AddDays(-1)) return " From Yesterday";
            return " From " + d.ToString("ddd yyyy-MM-dd");
        }

        #endregion
    }
}
