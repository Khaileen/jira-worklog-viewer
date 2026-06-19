using System;
using System.IO;
using Newtonsoft.Json;

namespace JiraWorklogViewer
{
    public class AppSettings
    {
        public double UiScale { get; set; } = 1.25;
        public string BedrockProfile { get; set; } = string.Empty;

        private static readonly string SettingsDir =
            AppDomain.CurrentDomain.BaseDirectory;

        private static readonly string SettingsFile =
            Path.Combine(SettingsDir, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    var json = File.ReadAllText(SettingsFile);
                    return JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch { }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(SettingsFile, JsonConvert.SerializeObject(this, Formatting.Indented));
            }
            catch { }
        }
    }
}
