# Jira Worklog Viewer

A lightweight Windows desktop application to view and manage your Jira worklogs. Built with WPF and .NET Framework 4.8.

## Features

- **Secure Credential Storage** — Credentials are encrypted using Windows DPAPI and stored locally
- **Auto-Connect** — Automatically connects on startup if credentials are saved
- **Flexible Filtering**
  - Filter by date range (or disable to fetch all worklogs)
  - Filter by specific ticket key
  - Combine both filters
- **Grouped View** — Worklogs displayed in a tree view, grouped by date with daily totals
- **Detailed Notes** — Select any worklog to view full details including comments/notes
- **Time Tracking** — Shows total logged time for each day and overall total in status bar

## Requirements

- Windows 10 or later
- .NET Framework 4.8
- Jira Cloud account with API access

## Installation

1. Download the latest release
2. Extract to a folder of your choice
3. Run `JiraWorklogViewer.exe`

## Setup

### Getting Your Jira API Token

1. Log in to [Atlassian Account Settings](https://id.atlassian.com/manage-profile/security/api-tokens)
2. Click **Create API token**
3. Give it a label (e.g., "Worklog Viewer")
4. Copy the generated token

### Connecting to Jira

1. **Server URL** — Enter your Jira Cloud URL (e.g., `https://yourcompany.atlassian.net`)
2. **Email** — Your Atlassian account email
3. **API Token** — Paste the token you generated
4. Click **Save & Connect**

Your credentials are encrypted and stored in `%LocalAppData%\JiraWorklogViewer\credentials.dat`

## Usage

### Fetching Worklogs

| Filter | Description |
|--------|-------------|
| **Date range checked** | Fetches worklogs within the specified date range |
| **Date range unchecked** | Fetches all your worklogs (may take longer) |
| **Ticket field empty** | Fetches worklogs across all tickets |
| **Ticket field filled** | Fetches worklogs only for that specific ticket (e.g., `CRM-1234`) |

### Examples

- **Last 7 days, all tickets** — Check date range, set dates, leave ticket empty
- **All time for one ticket** — Uncheck date range, enter ticket key
- **Specific ticket in date range** — Check date range, set dates, enter ticket key

### Viewing Details

Click on any worklog entry in the tree view to see:
- Issue key and summary
- Date and time
- Time spent
- Notes/comments

## Building from Source

### Prerequisites

- Visual Studio 2019 or later
- .NET Framework 4.8 SDK

### Steps

```bash
git clone https://github.com/Khaileen/jira-worklog-viewer.git
cd jira-worklog-viewer
```

1. Open `JiraWorklogViewer.csproj` in Visual Studio
2. Restore NuGet packages (Newtonsoft.Json)
3. Build solution (Ctrl+Shift+B)
4. Run (F5)

## Project Structure

```
JiraWorklogViewer/
├── Models/
│   └── JiraModels.cs         # Data models for Jira API responses
├── Services/
│   ├── JiraService.cs        # Jira REST API client
│   └── CredentialService.cs  # Encrypted credential storage
├── MainWindow.xaml           # Main UI layout
├── MainWindow.xaml.cs        # UI logic and event handlers
├── App.xaml                  # Application definition
└── app.ico                   # Application icon
```

## Security

- Credentials are encrypted using Windows Data Protection API (DPAPI)
- Encryption is scoped to the current Windows user
- API tokens are never stored in plain text
- No data is sent to third parties — only direct communication with your Jira instance

## Limitations

- Jira Cloud only (uses REST API v3)
- Read-only — viewing worklogs only (no create/edit/delete yet)
- Maximum 100 issues per query (Jira API limit)

## Roadmap

- [ ] Add/edit/delete worklogs
- [ ] Export to CSV/Excel
- [ ] Weekly/monthly summary reports
- [ ] Dark mode support

## License

MIT License — feel free to use, modify, and distribute.

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.
