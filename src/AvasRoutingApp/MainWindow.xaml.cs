using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using AvasRoutingApp.Configuration;
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
        private string _currentLoadedUrl = string.Empty;

        public MainViewModel ViewModel { get; }

        public MainWindow() : this(new ConfigService())
        {
        }

        public MainWindow(IConfigService configService)
        {
            InitializeComponent();
            _configService = configService ?? throw new ArgumentNullException(nameof(configService));
            ViewModel = new MainViewModel(configService: _configService);
            SidebarView.DataContext = ViewModel;

            _configService.ConfigChanged += OnConfigChanged;
            UpdateStatusDisplays(_configService.Current);

            Loaded += MainWindow_Loaded;
        }

        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _configService = ViewModel.ConfigService;
            SidebarView.DataContext = ViewModel;

            _configService.ConfigChanged += OnConfigChanged;
            UpdateStatusDisplays(_configService.Current);

            Loaded += MainWindow_Loaded;
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

                NavigateToConfiguredUrl();
            }
            catch (Exception ex)
            {
                ShowOfflineBanner($"WebView2 runtime initialization error: {ex.Message}");
                TxtStatus.Text = "Error initializing WebView2 environment.";
            }
        }

        private void NavigateToConfiguredUrl()
        {
            string url = _configService.Current.BlueRiverUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                ShowOfflineBanner("BlueRiver AV Manager URL is not configured. Open Settings to specify a valid URL.");
                return;
            }

            if (MainWebView.CoreWebView2 == null)
            {
                ShowOfflineBanner("WebView2 browser core is not yet initialized.");
                return;
            }

            try
            {
                _currentLoadedUrl = url;
                TxtCurrentUrlDisplay.Text = $"URL: {url}";
                TxtStatus.Text = $"Connecting to {url}...";
                MainWebView.CoreWebView2.Navigate(url);
            }
            catch (Exception ex)
            {
                ShowOfflineBanner($"Navigation failed: {ex.Message}");
            }
        }

        private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            TxtStatus.Text = $"Loading {e.Uri}...";
        }

        private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                ShowOfflineBanner($"Unable to reach BlueRiver AV Manager at '{_currentLoadedUrl}'. Web error: {e.WebErrorStatus}");
                TxtStatus.Text = $"Connection failed ({e.WebErrorStatus}). Offline fallback notice displayed.";
            }
            else
            {
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
            SidebarContainer.Width = _isSidebarExpanded ? 340 : 0;
            TxtToggleIcon.Text = _isSidebarExpanded ? "▶ CLOSE" : "◀ PREVIEW";

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
            catch (Exception)
            {
                // Suppress async void exceptions if window is closed during expansion/collapse
            }
        }

        private void OnConfigChanged(AppConfig config)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateStatusDisplays(config);
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
