using System;
using System.Collections.Generic;
using System.Windows;
using AvasRoutingApp.Configuration;

namespace AvasRoutingApp.Views
{
    /// <summary>
    /// Interaction logic for SettingsDialog.xaml
    /// </summary>
    public partial class SettingsDialog : Window
    {
        private readonly IConfigService _configService;

        public SettingsDialog(IConfigService configService)
        {
            InitializeComponent();
            _configService = configService ?? throw new ArgumentNullException(nameof(configService));
            ApplyAppLogo();
            LoadConfigToUi(_configService.Current);
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
                ImgSettingsLogo.Source = appIcon;
                ImgSettingsLogo.Visibility = Visibility.Visible;
            }
            else
            {
                ImgSettingsLogo.Visibility = Visibility.Collapsed;
            }

            TxtSettingsAppVersion.Text = AppInfo.VersionString;
        }

        private void LoadConfigToUi(AppConfig config)
        {
            if (string.Equals(config.Theme, "Dark", StringComparison.OrdinalIgnoreCase))
            {
                CmbTheme.SelectedIndex = 1;
            }
            else
            {
                CmbTheme.SelectedIndex = 0;
            }

            if (string.Equals(config.SidebarFontSize, "Small", StringComparison.OrdinalIgnoreCase))
            {
                CmbSidebarFontSize.SelectedIndex = 0;
            }
            else if (string.Equals(config.SidebarFontSize, "Large", StringComparison.OrdinalIgnoreCase))
            {
                CmbSidebarFontSize.SelectedIndex = 2;
            }
            else if (string.Equals(config.SidebarFontSize, "ExtraLarge", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(config.SidebarFontSize, "Extra Large", StringComparison.OrdinalIgnoreCase))
            {
                CmbSidebarFontSize.SelectedIndex = 3;
            }
            else
            {
                CmbSidebarFontSize.SelectedIndex = 1;
            }
            UpdateLivePreviewScale();

            TxtBlueRiverUrl.Text = config.BlueRiverUrl;
            TxtControlServerIp.Text = config.ControlServerIp;
            TxtRestPort.Text = config.RestPort.ToString();
            TxtTelnetPort.Text = config.TelnetPort.ToString();
            TxtMulticastStartIp.Text = config.MulticastStartIp;
            TxtMulticastEndIp.Text = config.MulticastEndIp;
            TxtBasePort.Text = config.BasePort.ToString();
            TxtLocalNetworkInterfaceIp.Text = config.LocalNetworkInterfaceIp;
            BorderErrors.Visibility = Visibility.Collapsed;
        }

        private void CmbSidebarFontSize_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateLivePreviewScale();
        }

        private void UpdateLivePreviewScale()
        {
            if (PreviewScaleTransform == null || TxtPreviewScaleBadge == null || CmbSidebarFontSize == null) return;

            string tag = (CmbSidebarFontSize.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "Normal";
            double scale = 1.0;
            string label = "100% (Normal)";

            switch (tag)
            {
                case "Small":
                    scale = 0.90;
                    label = "90% (Small)";
                    break;
                case "Large":
                    scale = 1.15;
                    label = "115% (Large)";
                    break;
                case "ExtraLarge":
                    scale = 1.30;
                    label = "130% (Extra Large)";
                    break;
                default:
                    scale = 1.00;
                    label = "100% (Normal)";
                    break;
            }

            PreviewScaleTransform.ScaleX = scale;
            PreviewScaleTransform.ScaleY = scale;
            TxtPreviewScaleBadge.Text = label;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var errors = new List<string>();

            if (!int.TryParse(TxtRestPort.Text.Trim(), out int restPort))
            {
                errors.Add("REST API Port must be a valid integer.");
            }

            if (!int.TryParse(TxtTelnetPort.Text.Trim(), out int telnetPort))
            {
                errors.Add("Telnet Port must be a valid integer.");
            }

            if (!int.TryParse(TxtBasePort.Text.Trim(), out int basePort))
            {
                errors.Add("Multicast Base Port must be a valid integer.");
            }

            string selectedTheme = (CmbTheme.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? _configService.Current.Theme;
            string selectedFontSize = (CmbSidebarFontSize.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "Normal";

            // If font size preset changed, automatically adapt default sidebar width
            double candidateWidth = _configService.Current.SidebarWidth;
            if (!string.Equals(selectedFontSize, _configService.Current.SidebarFontSize, StringComparison.OrdinalIgnoreCase))
            {
                var tempCfg = new AppConfig { SidebarFontSize = selectedFontSize };
                candidateWidth = tempCfg.GetDefaultSidebarWidth();
            }

            var candidateConfig = new AppConfig
            {
                Theme = selectedTheme,
                SidebarFontSize = selectedFontSize,
                SidebarWidth = candidateWidth,
                BlueRiverUrl = TxtBlueRiverUrl.Text.Trim(),
                ControlServerIp = TxtControlServerIp.Text.Trim(),
                RestPort = restPort,
                TelnetPort = telnetPort,
                MulticastStartIp = TxtMulticastStartIp.Text.Trim(),
                MulticastEndIp = TxtMulticastEndIp.Text.Trim(),
                BasePort = basePort,
                LocalNetworkInterfaceIp = TxtLocalNetworkInterfaceIp.Text.Trim()
            };

            var (isValid, validatorErrors) = ConfigValidator.Validate(candidateConfig);
            errors.AddRange(validatorErrors);

            if (errors.Count > 0)
            {
                TxtErrors.Text = string.Join(Environment.NewLine, errors);
                BorderErrors.Visibility = Visibility.Visible;
                return;
            }

            try
            {
                _configService.Save(candidateConfig);
                App.ApplyTheme(candidateConfig.Theme);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                TxtErrors.Text = $"Failed to save configuration: {ex.Message}";
                BorderErrors.Visibility = Visibility.Visible;
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void BtnResetDefaults_Click(object sender, RoutedEventArgs e)
        {
            LoadConfigToUi(new AppConfig());
        }

        private async void BtnTestConnection_Click(object sender, RoutedEventArgs e)
        {
            string host = TxtControlServerIp.Text.Trim();
            if (!int.TryParse(TxtTelnetPort.Text.Trim(), out int port))
            {
                port = 6970;
            }

            TxtTestResult.Text = "Testing connection...";
            TxtTestResult.Foreground = (System.Windows.Media.Brush)FindResource("ThemeAccentBrush");
            TxtTestResult.Visibility = Visibility.Visible;

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var client = new System.Net.Sockets.TcpClient();
                var connectTask = client.ConnectAsync(host, port);
                var delayTask = System.Threading.Tasks.Task.Delay(2000);
                if (await System.Threading.Tasks.Task.WhenAny(connectTask, delayTask) == connectTask && client.Connected)
                {
                    sw.Stop();
                    TxtTestResult.Text = $"✓ SDVoE Server reachable ({sw.ElapsedMilliseconds} ms)";
                    TxtTestResult.Foreground = (System.Windows.Media.Brush)FindResource("ThemeStatusOnlineBrush");
                }
                else
                {
                    TxtTestResult.Text = "✗ Connection timed out (check host & port)";
                    TxtTestResult.Foreground = (System.Windows.Media.Brush)FindResource("ThemeStatusOfflineBrush");
                }
            }
            catch (Exception ex)
            {
                TxtTestResult.Text = $"✗ Connection failed: {ex.Message}";
                TxtTestResult.Foreground = (System.Windows.Media.Brush)FindResource("ThemeStatusOfflineBrush");
            }
        }
    }
}
