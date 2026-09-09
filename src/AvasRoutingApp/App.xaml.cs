using System;
using System.Threading.Tasks;
using System.Windows;
using AvasRoutingApp.Logging;

namespace AvasRoutingApp
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// Provides global exception handling and file logging.
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            AppLogger.Initialize();
            AppLogger.Info("App", $"{AppInfo.AppName} {AppInfo.VersionString} starting up | OS: {Environment.OSVersion} | .NET: {Environment.Version}");

            var configService = new Configuration.ConfigService();
            ApplyTheme(configService.Current.Theme);

            // 1. WPF Dispatcher unhandled exceptions
            DispatcherUnhandledException += (s, args) =>
            {
                AppLogger.Fatal("Dispatcher", "Unhandled exception in WPF Dispatcher thread", args.Exception);
                MessageBox.Show(
                    $"An unexpected application error occurred:\n\n{args.Exception.Message}\n\nTechnical details have been written to:\n{AppLogger.LogFilePath}",
                    "AVAS Routing Software — Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                // Prevent the process from abruptly crashing
                args.Handled = true;
            };

            // 2. AppDomain-wide unhandled exceptions (worker threads, native callbacks)
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                {
                    AppLogger.Fatal("AppDomain", "Fatal unhandled exception in AppDomain", ex);
                }
                else
                {
                    AppLogger.Fatal("AppDomain", $"Fatal unhandled exception object: {args.ExceptionObject}");
                }
            };

            // 3. Unobserved background Task exceptions
            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                AppLogger.Error("TaskScheduler", "Unobserved background task exception", args.Exception);
                args.SetObserved();
            };
        }

        protected override void OnExit(ExitEventArgs e)
        {
            AppLogger.Info("App", $"AVAS Routing Software exiting with code {e.ApplicationExitCode}");
            base.OnExit(e);
        }

        public static void ApplyTheme(string themeName)
        {
            var app = Current;
            if (app == null) return;

            string themeFile = string.Equals(themeName, "Light", StringComparison.OrdinalIgnoreCase)
                ? "Themes/LightTheme.xaml"
                : "Themes/DarkTheme.xaml";

            try
            {
                var uri = new Uri(themeFile, UriKind.Relative);
                var newDict = new ResourceDictionary { Source = uri };

                app.Resources.MergedDictionaries.Clear();
                app.Resources.MergedDictionaries.Add(newDict);
                AppLogger.Info("Theme", $"Theme applied: {themeName} ({themeFile})");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Theme", $"Failed to apply theme {themeName}", ex);
            }
        }
    }
}
