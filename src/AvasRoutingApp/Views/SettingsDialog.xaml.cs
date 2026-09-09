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
        }

        private void LoadConfigToUi(AppConfig config)
        {
            if (string.Equals(config.Theme, "Light", StringComparison.OrdinalIgnoreCase))
            {
                CmbTheme.SelectedIndex = 1;
            }
            else
            {
                CmbTheme.SelectedIndex = 0;
            }

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

            var candidateConfig = new AppConfig
            {
                Theme = selectedTheme,
                SidebarWidth = _configService.Current.SidebarWidth,
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
    }
}
