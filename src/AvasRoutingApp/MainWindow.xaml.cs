using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using Microsoft.Web.WebView2.Core;
using AvasRoutingApp.Configuration;
using AvasRoutingApp.Logging;
using AvasRoutingApp.ViewModels;
using AvasRoutingApp.Views;

namespace AvasRoutingApp
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// Hosts the embedded BlueRiver AV Manager via Microsoft WebView2
    /// and provides an expandable sidebar for native preview cards.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly IConfigService _configService;
        private bool _isSidebarExpanded = false;
        private double _currentSidebarWidth = 400.0;
        private string _currentLoadedUrl = string.Empty;

        public MainViewModel ViewModel { get; }

        public MainWindow() : this(new ConfigService())
        {
        }

        public MainWindow(IConfigService configService)
        {
            InitializeComponent();
            _configService = configService ?? throw new ArgumentNullException(nameof(configService));
            _currentSidebarWidth = _configService.Current.SidebarWidth >= 320 && _configService.Current.SidebarWidth <= 650
                ? _configService.Current.SidebarWidth
                : 400.0;

            ViewModel = new MainViewModel(configService: _configService);
            SidebarView.DataContext = ViewModel;

            _configService.ConfigChanged += OnConfigChanged;
            UpdateStatusDisplays(_configService.Current);
            ApplyAppLogo();

            Loaded += MainWindow_Loaded;
        }

        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _configService = ViewModel.ConfigService;
            _currentSidebarWidth = _configService.Current.SidebarWidth >= 320 && _configService.Current.SidebarWidth <= 650
                ? _configService.Current.SidebarWidth
                : 400.0;

            SidebarView.DataContext = ViewModel;

            _configService.ConfigChanged += OnConfigChanged;
            UpdateStatusDisplays(_configService.Current);
            ApplyAppLogo();

            Loaded += MainWindow_Loaded;
        }

        private void ApplyAppLogo()
        {
            var windowIcon = AppIconHelper.GetWindowIcon();
            if (windowIcon != null)
            {
                Icon = windowIcon;
            }

            var appIcon = AppIconHelper.GetAppIcon(32);
            if (appIcon != null)
            {
                ImgAppLogo.Source = appIcon;
                ImgAppLogo.Visibility = Visibility.Visible;
            }
            else
            {
                ImgAppLogo.Visibility = Visibility.Collapsed;
            }

            TxtAppVersion.Text = AppInfo.VersionString;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await InitializeWebViewAsync();
        }

        private async Task InitializeWebViewAsync()
        {
            try
            {
                TxtStatus.Text = "Initializing portable WebView2 environment...";

                // 100% portable isolation: UserDataFolder in application base directory
                string userDataFolder = Path.Combine(AppContext.BaseDirectory, "WebView2_UserData");
                if (!Directory.Exists(userDataFolder))
                {
                    Directory.CreateDirectory(userDataFolder);
                }

                var options = new CoreWebView2EnvironmentOptions();
                var environment = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: userDataFolder,
                    options: options);

                await MainWebView.EnsureCoreWebView2Async(environment);

                if (MainWebView.CoreWebView2 != null)
                {
                    MainWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                    MainWebView.CoreWebView2.Settings.AreDevToolsEnabled = true;
                    MainWebView.CoreWebView2.Settings.IsZoomControlEnabled = true;

                    MainWebView.CoreWebView2.NavigationStarting += OnNavigationStarting;
                    MainWebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                }

                AppLogger.Info("WebView2", $"WebView2 initialized with UserData: {userDataFolder}");
                NavigateToConfiguredUrl();
            }
            catch (Exception ex)
            {
                AppLogger.Error("WebView2", "WebView2 runtime initialization error", ex);
                ShowOfflineBanner($"WebView2 runtime initialization error: {ex.Message}");
                TxtStatus.Text = "Error initializing WebView2 environment.";
            }
        }

        private void NavigateToConfiguredUrl()
        {
            string url = _configService.Current.BlueRiverUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                AppLogger.Warn("WebView2", "BlueRiver AV Manager URL is not configured.");
                ShowOfflineBanner("BlueRiver AV Manager URL is not configured. Open Settings to specify a valid URL.");
                return;
            }

            if (MainWebView.CoreWebView2 == null)
            {
                AppLogger.Warn("WebView2", "WebView2 browser core is not yet initialized.");
                ShowOfflineBanner("WebView2 browser core is not yet initialized.");
                return;
            }

            try
            {
                _currentLoadedUrl = url;
                TxtCurrentUrlDisplay.Text = $"URL: {url}";
                TxtStatus.Text = $"Connecting to {url}...";
                AppLogger.Info("WebView2", $"Navigating to {url}");
                MainWebView.CoreWebView2.Navigate(url);
            }
            catch (Exception ex)
            {
                AppLogger.Error("WebView2", $"Navigation failed to {url}", ex);
                ShowOfflineBanner($"Navigation failed: {ex.Message}");
            }
        }

        private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            TxtStatus.Text = $"Loading {e.Uri}...";
            AppLogger.Debug("WebView2", $"Navigation starting: {e.Uri}");
        }

        private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                AppLogger.Warn("WebView2", $"Navigation completed with error: {e.WebErrorStatus} for {_currentLoadedUrl}");
                ShowOfflineBanner($"Unable to reach BlueRiver AV Manager at '{_currentLoadedUrl}'. Web error: {e.WebErrorStatus}");
                TxtStatus.Text = $"Connection failed ({e.WebErrorStatus}). Offline fallback notice displayed.";
            }
            else
            {
                AppLogger.Info("WebView2", $"Successfully connected to BlueRiver AV Manager at {_currentLoadedUrl}");
                HideOfflineBanner();
                TxtStatus.Text = "Connected to BlueRiver AV Manager | Portable WebView2 (.\\WebView2_UserData)";
            }
        }

        private void ShowOfflineBanner(string message)
        {
            TxtOfflineMessage.Text = $"⚠ {message}";
            OfflineBanner.Visibility = Visibility.Visible;
        }

        private void HideOfflineBanner()
        {
            OfflineBanner.Visibility = Visibility.Collapsed;
        }

        private void BtnReload_Click(object sender, RoutedEventArgs e)
        {
            if (MainWebView.CoreWebView2 != null)
            {
                MainWebView.CoreWebView2.Reload();
            }
            else
            {
                NavigateToConfiguredUrl();
            }
        }

        private void BtnRetry_Click(object sender, RoutedEventArgs e)
        {
            NavigateToConfiguredUrl();
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SettingsDialog(_configService)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                var config = _configService.Current;
                UpdateStatusDisplays(config);

                if (config.BlueRiverUrl != _currentLoadedUrl)
                {
                    NavigateToConfiguredUrl();
                }
            }
        }

        private async void BtnToggleSidebar_Click(object sender, RoutedEventArgs e)
        {
            _isSidebarExpanded = !_isSidebarExpanded;

            if (_isSidebarExpanded)
            {
                SidebarContainer.Width = _currentSidebarWidth;
                SidebarContainer.Visibility = Visibility.Visible;
                SidebarSplitter.Visibility = Visibility.Visible;
                TxtToggleIcon.Text = "▶ CLOSE";
            }
            else
            {
                SidebarContainer.Width = 0;
                SidebarContainer.Visibility = Visibility.Collapsed;
                SidebarSplitter.Visibility = Visibility.Collapsed;
                TxtToggleIcon.Text = "◀ PREVIEW";
            }

            AppLogger.Info("MainWindow", $"Preview sidebar toggled | Expanded: {_isSidebarExpanded} | Width: {_currentSidebarWidth}");

            try
            {
                if (_isSidebarExpanded)
                {
                    await ViewModel.ExpandSidebarAsync();
                }
                else
                {
                    await ViewModel.CollapseSidebarAsync();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("MainWindow", "Exception during sidebar toggle", ex);
            }
        }

        private void SidebarSplitter_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (!_isSidebarExpanded) return;

            // Dragging left (e.HorizontalChange < 0) widens the right-docked sidebar
            double targetWidth = SidebarContainer.Width - e.HorizontalChange;
            targetWidth = Math.Clamp(targetWidth, 320.0, 650.0);
            SidebarContainer.Width = targetWidth;
            _currentSidebarWidth = targetWidth;
        }

        private void SidebarSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (_configService != null)
            {
                var cfg = _configService.Current.Clone();
                cfg.SidebarWidth = Math.Round(_currentSidebarWidth, 0);
                _configService.Save(cfg);
                AppLogger.Debug("MainWindow", $"Persisted custom sidebar width: {cfg.SidebarWidth}px");
            }
        }

        private void OnConfigChanged(AppConfig config)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateStatusDisplays(config);
                App.ApplyTheme(config.Theme);
            });
        }

        private void UpdateStatusDisplays(AppConfig config)
        {
            TxtServerStatus.Text = $"SDVoE Server: {config.ControlServerIp}:{config.RestPort}";
            TxtCurrentUrlDisplay.Text = $"URL: {config.BlueRiverUrl}";
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            _configService.ConfigChanged -= OnConfigChanged;
            ViewModel?.Dispose();
            MainWebView?.Dispose();
            base.OnClosing(e);
        }
    }
}
