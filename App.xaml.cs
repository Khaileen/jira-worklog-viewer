using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace JiraWorklogViewer
{
    public partial class App : Application
    {
        public static AppSettings Settings { get; private set; }

        // Each window's true 100%-scale Width/Height/MinWidth/MinHeight, captured the first
        // time ApplyScale runs on it — always the base for recomputing size on later scale
        // changes (e.g. MainWindow's scale dropdown), so repeated calls never compound.
        private static readonly ConditionalWeakTable<Window, BaseWindowSize> _baseSizes = new ConditionalWeakTable<Window, BaseWindowSize>();

        private class BaseWindowSize
        {
            public double Width, Height, MinWidth, MinHeight;
            public BaseWindowSize(double width, double height, double minWidth, double minHeight)
            {
                Width = width; Height = height; MinWidth = minWidth; MinHeight = minHeight;
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            Settings = AppSettings.Load();
            base.OnStartup(e);
        }

        /// <summary>
        /// Applies the current UiScale to a window's root element via LayoutTransform, and grows
        /// the window itself (Width/Height/MinWidth/MinHeight) proportionally so scaled content
        /// isn't clipped. Call this from each window's Loaded event or constructor.
        /// </summary>
        public static void ApplyScale(Window window)
        {
            if (window == null) return;

            if (window.Content is FrameworkElement root)
                root.LayoutTransform = new ScaleTransform(Settings.UiScale, Settings.UiScale);

            // ActiveWorklogWindow recomputes its own Width/Height on every Activated/Deactivated
            // (far more often than ApplyScale runs) — it scales those directly itself instead.
            if (window is ActiveWorklogWindow) return;

            if (!_baseSizes.TryGetValue(window, out var baseSize))
            {
                baseSize = new BaseWindowSize(window.Width, window.Height, window.MinWidth, window.MinHeight);
                _baseSizes.Add(window, baseSize);
            }

            if (!double.IsNaN(baseSize.Width))  window.Width  = baseSize.Width  * Settings.UiScale;
            if (!double.IsNaN(baseSize.Height)) window.Height = baseSize.Height * Settings.UiScale;
            if (baseSize.MinWidth  > 0) window.MinWidth  = baseSize.MinWidth  * Settings.UiScale;
            if (baseSize.MinHeight > 0) window.MinHeight = baseSize.MinHeight * Settings.UiScale;
        }
    }
}
