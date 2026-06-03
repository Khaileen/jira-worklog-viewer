using System.Windows;
using System.Windows.Media;

namespace JiraWorklogViewer
{
    public partial class App : Application
    {
        public static AppSettings Settings { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            Settings = AppSettings.Load();
            base.OnStartup(e);
        }

        /// <summary>
        /// Applies the current UiScale to a window's root element via LayoutTransform.
        /// Call this from each window's Loaded event or constructor.
        /// </summary>
        public static void ApplyScale(Window window)
        {
            if (window?.Content is FrameworkElement root)
            {
                root.LayoutTransform = new ScaleTransform(Settings.UiScale, Settings.UiScale);
            }
        }
    }
}
