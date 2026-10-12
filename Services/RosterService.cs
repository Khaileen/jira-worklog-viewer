using System;
using System.Collections.Generic;
using System.IO;
using JiraWorklogViewer.Models;
using Newtonsoft.Json;

namespace JiraWorklogViewer.Services
{
    /// <summary>Persists the DSU meeting roster to %LocalAppData%\JiraWorklogViewer\dsu-roster.json.</summary>
    public class RosterService
    {
        private readonly string _filePath;

        public RosterService()
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JiraWorklogViewer");
            Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, "dsu-roster.json");
        }

        public List<RosterUser> Load()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    return JsonConvert.DeserializeObject<List<RosterUser>>(json) ?? new List<RosterUser>();
                }
            }
            catch { }
            return new List<RosterUser>();
        }

        public void Save(IEnumerable<RosterUser> users)
        {
            try
            {
                File.WriteAllText(_filePath, JsonConvert.SerializeObject(users, Formatting.Indented));
            }
            catch { }
        }
    }
}
