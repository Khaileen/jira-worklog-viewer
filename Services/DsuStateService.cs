using System;
using System.Collections.Generic;
using System.IO;
using JiraWorklogViewer.Models;
using Newtonsoft.Json;

namespace JiraWorklogViewer.Services
{
    /// <summary>Everything the DSU Meeting window keeps: per user/day tickets (worklogs, AI + manual entries, edits, flags) and the model.</summary>
    public class DsuState
    {
        public string Model { get; set; }
        public Dictionary<string, List<UserDayTicket>> Days { get; set; } = new Dictionary<string, List<UserDayTicket>>();
    }

    /// <summary>Persists DsuState to %LocalAppData%\JiraWorklogViewer\dsu-state.json.</summary>
    public class DsuStateService
    {
        private readonly string _filePath;

        public DsuStateService()
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JiraWorklogViewer");
            Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, "dsu-state.json");
        }

        public DsuState Load()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var state = JsonConvert.DeserializeObject<DsuState>(File.ReadAllText(_filePath));
                    if (state != null)
                    {
                        if (state.Days == null) state.Days = new Dictionary<string, List<UserDayTicket>>();
                        return state;
                    }
                }
            }
            catch { }
            return new DsuState();
        }

        public void Save(DsuState state)
        {
            try
            {
                // Write to a temp file first so a crash mid-write cannot corrupt the saved state
                var tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(state, Formatting.Indented));
                if (File.Exists(_filePath)) File.Delete(_filePath);
                File.Move(tmp, _filePath);
            }
            catch { }
        }
    }
}
