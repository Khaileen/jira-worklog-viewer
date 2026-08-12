using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using Amazon.BedrockRuntime.Model;
using JiraWorklogViewer.Models;
using JiraWorklogViewer.Services;

namespace JiraWorklogViewer
{
    public partial class ChatWindow : Window
    {
        private readonly JiraFetchService _fetchService;
        private BedrockService _bedrockService;
        private JiraAgentService _agentService;
        private readonly List<Message> _history = new List<Message>();
        private CancellationTokenSource _cts;

        public ChatWindow(JiraFetchService fetchService)
        {
            InitializeComponent();
            _fetchService = fetchService;

            cboModel.Items.Add(ModelNames.BedrockSonnet);
            cboModel.Items.Add(ModelNames.BedrockHaiku);
            cboModel.Items.Add(ModelNames.BedrockOpus);
            cboModel.SelectedItem = ModelNames.BedrockSonnet;

            Loaded += (s, e) =>
            {
                txtBedrockProfile.Text = App.Settings.BedrockProfile;
                RebuildAgentService();
            };
        }

        private void RebuildAgentService()
        {
            _bedrockService = new BedrockService(txtBedrockProfile.Text.Trim());
            _agentService   = new JiraAgentService(_fetchService, _bedrockService);
        }

        private void TxtBedrockProfile_LostFocus(object sender, RoutedEventArgs e)
        {
            App.Settings.BedrockProfile = txtBedrockProfile.Text.Trim();
            App.Settings.Save();
            RebuildAgentService();
        }

        private void TxtInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                BtnSend_Click(sender, e);
            }
        }

        private async void BtnSend_Click(object sender, RoutedEventArgs e)
        {
            string message = txtInput.Text.Trim();
            if (string.IsNullOrEmpty(message)) return;

            if (!_bedrockService.IsConfigured)
            {
                MessageBox.Show("Enter an AWS Profile for Bedrock first.", "Not Configured",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string model = cboModel.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(model))
            {
                MessageBox.Show("Please select a model.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            txtInput.Clear();
            Append($"You: {message}\n");

            _cts = new CancellationTokenSource();
            SetUIRunning(true);
            SetStatus("Thinking...");

            try
            {
                string answer = await _agentService.RunAsync(
                    _history, message, model,
                    line => Append(line + "\n"),
                    _cts.Token);

                Append($"\nAssistant: {answer}\n\n{new string('-', 60)}\n");
                SetStatus("Ready.");
            }
            catch (OperationCanceledException)
            {
                Append("(cancelled)\n\n");
                SetStatus("Cancelled.");
            }
            catch (Exception ex)
            {
                Append($"⚠ Error: {ex.Message}\n\n");
                SetStatus($"Error: {ex.Message}");
                MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetUIRunning(false);
            }
        }

        private void BtnNewChat_Click(object sender, RoutedEventArgs e)
        {
            _history.Clear();
            txtTranscript.Clear();
            SetStatus("New chat started.");
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            Close();
        }

        private void Append(string text)
        {
            txtTranscript.AppendText(text);
            txtTranscript.ScrollToEnd();
        }

        private void SetStatus(string msg) => txtStatus.Text = msg;

        private void SetUIRunning(bool running)
        {
            btnSend.IsEnabled   = !running;
            btnCancel.IsEnabled = running;
            cboModel.IsEnabled  = !running;
            txtInput.IsEnabled  = !running;
        }
    }
}
