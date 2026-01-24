using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JiraWorklogViewer.Models;
using Newtonsoft.Json;

namespace JiraWorklogViewer.Services
{
    public class ActiveWorklogService
    {
        private readonly string _filePath;
        private ActiveWorklogData _data;

        public ActiveWorklogService()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var folder = Path.Combine(appData, "JiraWorklogViewer");
            Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, "active-worklogs.json");
            
            Load();
        }

        private void Load()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    _data = JsonConvert.DeserializeObject<ActiveWorklogData>(json) ?? new ActiveWorklogData();
                }
                else
                {
                    _data = new ActiveWorklogData();
                }
            }
            catch
            {
                _data = new ActiveWorklogData();
            }
        }

        public void Reload()
        {
            Load();
        }

        private void Save()
        {
            try
            {
                var json = JsonConvert.SerializeObject(_data, Formatting.Indented);
                File.WriteAllText(_filePath, json);
            }
            catch
            {
                // Silently fail - could log this
            }
        }

        public List<ActiveWorklog> GetAll()
        {
            return _data.Worklogs.ToList();
        }

        public ActiveWorklog GetRunning()
        {
            return _data.Worklogs.FirstOrDefault(w => w.Status == ActiveWorklogStatus.Running);
        }

        public ActiveWorklog Get(string ticketKey)
        {
            return _data.Worklogs.FirstOrDefault(w => w.TicketKey.Equals(ticketKey, StringComparison.OrdinalIgnoreCase));
        }

        public void Start(string ticketKey, string ticketSummary)
        {
            // Pause any currently running worklog
            var running = GetRunning();
            if (running != null && !running.TicketKey.Equals(ticketKey, StringComparison.OrdinalIgnoreCase))
            {
                Pause(running.TicketKey);
            }

            var worklog = Get(ticketKey);
            
            if (worklog == null)
            {
                // Create new
                worklog = new ActiveWorklog
                {
                    TicketKey = ticketKey.ToUpperInvariant(),
                    TicketSummary = ticketSummary,
                    Status = ActiveWorklogStatus.Running,
                    CurrentSessionStart = DateTime.Now
                };
                _data.Worklogs.Add(worklog);
            }
            else
            {
                // Resume existing
                worklog.Status = ActiveWorklogStatus.Running;
                worklog.CurrentSessionStart = DateTime.Now;
            }

            Save();
        }

        public void Pause(string ticketKey)
        {
            var worklog = Get(ticketKey);
            if (worklog == null || worklog.Status != ActiveWorklogStatus.Running)
                return;

            // Save current session
            if (worklog.CurrentSessionStart.HasValue)
            {
                worklog.Sessions.Add(new WorklogSession
                {
                    Start = worklog.CurrentSessionStart.Value,
                    End = DateTime.Now
                });
                worklog.CurrentSessionStart = null;
            }

            worklog.Status = ActiveWorklogStatus.Stopped;
            Save();
        }

        public void UpdateNotes(string ticketKey, string notes)
        {
            var worklog = Get(ticketKey);
            if (worklog == null) return;

            worklog.Notes = notes;
            Save();
        }

        public void AddTime(string ticketKey, int seconds)
        {
            var worklog = Get(ticketKey);
            if (worklog == null || worklog.Status != ActiveWorklogStatus.Running) return;

            // Move the session start back in time to add more elapsed time
            if (worklog.CurrentSessionStart.HasValue)
            {
                worklog.CurrentSessionStart = worklog.CurrentSessionStart.Value.AddSeconds(-seconds);
                Save();
            }
        }

        public void MarkCompleted(string ticketKey)
        {
            var worklog = Get(ticketKey);
            if (worklog == null) return;

            // Make sure it's paused first
            if (worklog.Status == ActiveWorklogStatus.Running)
            {
                Pause(ticketKey);
                worklog = Get(ticketKey);
            }

            worklog.Status = ActiveWorklogStatus.Completed;
            Save();
        }

        public void Remove(string ticketKey)
        {
            var worklog = Get(ticketKey);
            if (worklog == null) return;

            // Make sure to close any running session first
            if (worklog.Status == ActiveWorklogStatus.Running)
            {
                Pause(ticketKey);
            }

            _data.Worklogs.Remove(worklog);
            Save();
        }

        public void PauseAll()
        {
            foreach (var worklog in _data.Worklogs.Where(w => w.Status == ActiveWorklogStatus.Running))
            {
                if (worklog.CurrentSessionStart.HasValue)
                {
                    worklog.Sessions.Add(new WorklogSession
                    {
                        Start = worklog.CurrentSessionStart.Value,
                        End = DateTime.Now
                    });
                    worklog.CurrentSessionStart = null;
                }
                worklog.Status = ActiveWorklogStatus.Stopped;
            }
            Save();
        }
    }
}
